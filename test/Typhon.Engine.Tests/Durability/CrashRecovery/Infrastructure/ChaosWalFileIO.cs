using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Tests;

/// <summary>
/// An <see cref="IWalFileIO"/> decorator over <see cref="InMemoryWalFileIO"/> that records every write and flush, can inject a deterministic crash at a chosen
/// write or flush boundary, and reconstructs the surviving on-disk state under a chosen <see cref="DamageModel"/>. Layer 1 of the crash-recovery testing framework
/// (<c>claude/design/Durability/crash-recovery-testing.md</c> §5.1).
/// </summary>
/// <remarks>
/// <para>
/// Flush-barrier model: each <see cref="WriteAligned"/> is tagged with the number of completed <see cref="FlushBuffers"/> calls at the time of the write. A write
/// is durable once a later flush completes (or it was written with FUA, which is its own barrier). At the crash instant, durable writes always survive; writes that
/// were still in-flight (after the last completed flush) are filtered by the damage model.
/// </para>
/// </remarks>
internal sealed class ChaosWalFileIO : IWalFileIO
{
    private readonly record struct WriteRecord(int Sequence, string SegmentPath, long Offset, byte[] Data, int FlushBarrierBefore, bool IsFua);

    private readonly InMemoryWalFileIO _inner = new();
    private readonly List<WriteRecord> _writeLog = [];
    private readonly Dictionary<nint, (string Path, bool Fua)> _handleInfo = new();
    private readonly Dictionary<string, bool> _segmentFua = new();
    private readonly Dictionary<string, long> _preAlloc = new();

    private int _writeSequence;
    private int _flushBarrierSequence;
    private bool _hasCrashed;
    private bool _flushCrash;
    private int _crashSequence;
    private int _flushBarrierAtCrash;
    private long _maxObservedLsn;

    private int _crashAtWrite = int.MaxValue;
    private int _crashAtFlush = int.MaxValue;

    // ── Test control ────────────────────────────────────────────────────────

    /// <summary>Configure a crash at the Nth <see cref="WriteAligned"/> call (1-based). The Nth write is recorded, then a <see cref="ChaosSimulatedCrashException"/> is thrown.</summary>
    public void SetCrashAtWrite(int n) => _crashAtWrite = n;

    /// <summary>Configure a crash at the Nth <see cref="FlushBuffers"/> call (1-based). The flush does not complete, so its barrier is not established.</summary>
    public void SetCrashAtFlush(int n) => _crashAtFlush = n;

    /// <summary>Total number of <see cref="WriteAligned"/> calls observed (including the crash write).</summary>
    public int TotalWriteCount => _writeLog.Count;

    /// <summary>Whether a simulated crash has fired on this instance.</summary>
    public bool HasCrashed => _hasCrashed;

    /// <summary>Highest WAL LSN seen in any <see cref="WriteAligned"/> payload (read from the frame headers). The honest durable watermark may never exceed this (LOG-05).</summary>
    public long MaxObservedLsn => _maxObservedLsn;

    // ── IWalFileIO (forward + intercept) ─────────────────────────────────────

    public SafeFileHandle OpenSegment(string path, bool withFUA)
    {
        ThrowIfCrashed();
        var handle = _inner.OpenSegment(path, withFUA);
        _handleInfo[handle.DangerousGetHandle()] = (Path.GetFullPath(path), withFUA);
        _segmentFua[Path.GetFullPath(path)] = withFUA;
        return handle;
    }

    public SafeFileHandle OpenSegmentForRead(string path)
    {
        ThrowIfCrashed();
        return _inner.OpenSegmentForRead(path);
    }

    public void WriteAligned(SafeFileHandle handle, long offset, ReadOnlySpan<byte> data)
    {
        ThrowIfCrashed();

        var seq = ++_writeSequence;
        var info = _handleInfo.GetValueOrDefault(handle.DangerousGetHandle(), ("<unknown>", false));
        _writeLog.Add(new WriteRecord(seq, info.Item1, offset, data.ToArray(), _flushBarrierSequence, info.Item2));

        // Offset 0 is always a WalSegmentHeader, never in-band frames — skip it explicitly rather than relying on the magic not aliasing a valid frame header.
        if (offset != 0)
        {
            TrackMaxLsn(data);
        }

        if (seq >= _crashAtWrite)
        {
            _crashSequence = seq;
            _flushBarrierAtCrash = _flushBarrierSequence;
            _hasCrashed = true;
            throw new ChaosSimulatedCrashException(seq, IoSubsystem.Wal);
        }

        _inner.WriteAligned(handle, offset, data);
    }

    public void FlushBuffers(SafeFileHandle handle)
    {
        ThrowIfCrashed();

        var flushNo = _flushBarrierSequence + 1;
        if (flushNo >= _crashAtFlush)
        {
            // The flush never completes: its barrier is NOT established, so every write since the last completed flush is in-flight and lost.
            _flushCrash = true;
            _flushBarrierAtCrash = _flushBarrierSequence;
            _crashSequence = _writeSequence + 1;
            _hasCrashed = true;
            throw new ChaosSimulatedCrashException(flushNo, IoSubsystem.Wal);
        }

        _flushBarrierSequence = flushNo;
        _inner.FlushBuffers(handle);
    }

    public void PreAllocate(SafeFileHandle handle, long size)
    {
        ThrowIfCrashed();
        if (_handleInfo.TryGetValue(handle.DangerousGetHandle(), out var info))
        {
            _preAlloc[info.Path] = Math.Max(_preAlloc.GetValueOrDefault(info.Path), size);
        }

        _inner.PreAllocate(handle, size);
    }

    public void ReadAligned(SafeFileHandle handle, long offset, Span<byte> buffer)
    {
        ThrowIfCrashed();
        _inner.ReadAligned(handle, offset, buffer);
    }

    public bool Exists(string path) => _inner.Exists(path);

    public void Delete(string path)
    {
        ThrowIfCrashed();
        _inner.Delete(path);
    }

    public void Dispose() => _inner.Dispose();

    // ── Post-crash reconstruction ────────────────────────────────────────────

    /// <summary>
    /// Builds a fresh <see cref="InMemoryWalFileIO"/> representing what survived on stable media after the crash, under the given <paramref name="model"/>. Durable
    /// writes (past a flush barrier or FUA) always survive; in-flight writes are filtered per the damage model (§5.1.4). The returned IO can be handed straight to
    /// <see cref="WalSegmentReader"/> / <c>WalRecovery</c>.
    /// </summary>
    public InMemoryWalFileIO GetPostCrashState(DamageModel model)
    {
        var result = new InMemoryWalFileIO();
        var rng = new Random(model.Seed);
        var handles = new Dictionary<string, SafeFileHandle>();

        SafeFileHandle HandleFor(string path)
        {
            if (!handles.TryGetValue(path, out var h))
            {
                h = result.OpenSegment(path, _segmentFua.GetValueOrDefault(path));
                handles[path] = h;
            }

            return h;
        }

        // Pre-open and pre-size every known segment so unwritten/empty regions read back as zero (the reader reads up to the header's SegmentSize).
        foreach (var path in _segmentFua.Keys)
        {
            var h = HandleFor(path);
            if (_preAlloc.TryGetValue(path, out var size))
            {
                result.PreAllocate(h, size);
            }
        }

        foreach (var w in _writeLog)
        {
            var isCrashWrite = !_flushCrash && w.Sequence == _crashSequence;
            var pastBarrier = w.FlushBarrierBefore < _flushBarrierAtCrash;
            var durable = (w.IsFua && !isCrashWrite) || pastBarrier;

            if (durable)
            {
                result.WriteAligned(HandleFor(w.SegmentPath), w.Offset, w.Data);
                continue;
            }

            if (_flushCrash)
            {
                // In-flight when the flush failed — lost.
                continue;
            }

            if (!isCrashWrite)
            {
                // Completed in-flight write before the crash. Reordered drops it on a coin flip; everything else keeps it.
                if (model.Type == DamageType.Reordered && rng.NextDouble() >= 0.5)
                {
                    continue;
                }

                result.WriteAligned(HandleFor(w.SegmentPath), w.Offset, w.Data);
            }
            else
            {
                // The crash write itself — never completed.
                switch (model.Type)
                {
                    case DamageType.TornWrite:
                        var survive = AlignDown((int)(w.Data.Length * model.TornWriteFraction), 4096);
                        if (survive > 0)
                        {
                            result.WriteAligned(HandleFor(w.SegmentPath), w.Offset, w.Data.AsSpan(0, survive));
                        }

                        break;
                    case DamageType.ZeroFill:
                        result.WriteAligned(HandleFor(w.SegmentPath), w.Offset, new byte[w.Data.Length]);
                        break;

                    // CleanCut and Reordered: the crash write is dropped entirely.
                }
            }
        }

        return result;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void TrackMaxLsn(ReadOnlySpan<byte> data)
    {
        var offset = 0;
        while (offset + WalFrameHeader.SizeInBytes <= data.Length)
        {
            ref readonly var fh = ref MemoryMarshal.AsRef<WalFrameHeader>(data.Slice(offset, WalFrameHeader.SizeInBytes));

            // Stop at padding/zero tail or anything that does not look like a valid in-band frame header (e.g. a segment-header write at offset 0).
            if (fh.FrameLength < WalFrameHeader.SizeInBytes || (fh.FrameLength & 7) != 0 || offset + fh.FrameLength > data.Length)
            {
                break;
            }

            if (fh.RecordCount > 0 && fh.LastLsn > _maxObservedLsn)
            {
                _maxObservedLsn = fh.LastLsn;
            }

            offset += fh.FrameLength;
        }
    }

    private void ThrowIfCrashed()
    {
        if (_hasCrashed)
        {
            throw new InvalidOperationException("ChaosWalFileIO instance has crashed — no further I/O is permitted.");
        }
    }

    private static int AlignDown(int value, int alignment) => value & ~(alignment - 1);
}
