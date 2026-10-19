using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>One WAL segment of a backup point's tail: what was copied of it, and what restore needs to recreate it.</summary>
/// <param name="SegmentId">The segment's id (its file is <c>{SegmentId:D16}.wal</c>).</param>
/// <param name="FirstLsn">Its header's first LSN.</param>
/// <param name="DeclaredSize">Its header's size. Restore recreates the file at this size, the copied prefix then zeros (C3).</param>
/// <param name="Prefix">The copied bytes, from offset 0; a whole number of 4 KiB pages, header included.</param>
internal readonly record struct WalTailSegmentCopy(long SegmentId, long FirstLsn, uint DeclaredSize, byte[] Prefix);

/// <summary>What a tail copy produced.</summary>
/// <param name="PointLsn"><c>P</c>: the highest LSN in <c>(C, end of the valid prefix]</c> recovery applies as a unit, <c>C</c> when there is none.</param>
/// <param name="Segments">The segments copied, ascending id.</param>
/// <param name="BytesCopied">Total bytes handed to the sink.</param>
internal readonly record struct WalTailResult(long PointLsn, IReadOnlyList<WalTailSegmentCopy> Segments, long BytesCopied);

/// <summary>
/// Copies a backup point's WAL tail (BKP-04) and computes its point LSN <c>P</c>, reusing the WAL's own reader and codec so the backup sees the log
/// exactly as crash recovery will. Record bytes stay inside <c>Durability/internals</c> (LOG-02).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the flush first (C1).</b> The checkpoint writes a page before its wave's flush2, so a page in the frozen image can reflect a record that was
/// published but not yet durable at the freeze. Every such record is at or below <c>PubF</c>, the last published LSN sampled under the freeze, so the copy
/// starts only once the WAL is durable through <c>PubF</c>.
/// </para>
/// <para>
/// <b>Which segments (C2).</b> Every sealed segment whose last LSN is at or above <c>C</c>, plus the active one: exactly the set a crash with that meta
/// would leave, because reclaim keeps <c>lastLSN ≥ C</c> (WP-09's strict <c>&lt;</c>). Recovery resumes the TSN counter above every commit stamp it scans,
/// at or below <c>C</c> included (RB-05), so dropping the segment that ends at <c>C</c> would let a restore reissue a stamp.
/// </para>
/// </remarks>
internal static class WalTailInspector
{
    /// <summary>Bytes read per I/O while copying a segment; a 4 KiB multiple (C20).</summary>
    internal const int CopyChunkBytes = 1024 * 1024;

    private const int PageAlignment = 4096;

    /// <summary>
    /// The segments a tail copies out of <paramref name="snapshot"/>: sealed ones whose last LSN is at or above <paramref name="checkpointLsn"/>, and the
    /// active one, in ascending id.
    /// </summary>
    internal static List<WalTailSegment> SelectTail(IReadOnlyList<WalTailSegment> snapshot, long checkpointLsn)
    {
        var result = new List<WalTailSegment>(snapshot.Count);
        foreach (var segment in snapshot)
        {
            if (segment.IsActive || segment.LastLsn >= checkpointLsn)
            {
                result.Add(segment);
            }
        }

        result.Sort(static (a, b) => a.SegmentId.CompareTo(b.SegmentId));
        return result;
    }

    /// <summary>
    /// The online tail copy, on the backup thread right after the freeze. Waits for the WAL to be durable through <paramref name="publishedAtFreeze"/>,
    /// snapshots the segments, copies the selected ones into <paramref name="sink"/> in 1 MiB aligned reads (calling <paramref name="serviceCbw"/>
    /// between reads, so writers waiting on copy-before-write slots keep moving), then computes <c>P</c>. The caller holds the reclaim floor throughout.
    /// </summary>
    /// <exception cref="BackupAbortedException">The WAL did not become durable in time, or a segment could not be read.</exception>
    internal static unsafe WalTailResult CaptureTail(WalManager wal, IMemoryAllocator allocator, IResource owner, long checkpointLsn, long publishedAtFreeze,
        IBackupPointSink sink, Action serviceCbw, TimeSpan flushTimeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(wal);
        ArgumentNullException.ThrowIfNull(sink);
        var io = wal.FileIO;

        // C1: everything the image can reflect is published at or below PubF; make it durable before reading a byte.
        try
        {
            wal.RequestFlush();
            var ctx = WaitContext.FromTimeout(flushTimeout, ct);
            wal.WaitForDurable(publishedAtFreeze, ref ctx);
        }
        catch (TyphonException e)
        {
            ct.ThrowIfCancellationRequested();   // a cancelled wait reads as a timeout; it is a cancellation (a closing engine, a caller)
            throw new BackupAbortedException(BackupAbortReason.WalFlushTimeout, $"the WAL did not become durable through LSN {publishedAtFreeze}.", e);
        }

        // Read only after the durable LSN reached PubF: the writer advances a segment's write offset before the durable LSN, so the active segment's
        // written length read now covers every record up to PubF. And it advances it only once the write has returned (WalWriter), so a write still in
        // flight lies wholly at or past the length read here: the copy never reads a byte of it (C24).
        var selected = SelectTail(wal.SegmentManager.SnapshotForTail(), checkpointLsn);

        using var block = allocator.AllocatePinned("Backup.TailCopy", owner, CopyChunkBytes, false, PageAlignment);
        var buffer = new Span<byte>(block.DataAsPointer, CopyChunkBytes);
        var copies = new List<WalTailSegmentCopy>(selected.Count);
        long total = 0;
        foreach (var segment in selected)
        {
            var copy = CopySegment(io, segment.Path, segment.SegmentId, segment.WrittenBytes, buffer, sink, serviceCbw, ct);
            copies.Add(copy);
            total += copy.Prefix.Length;
        }

        return new WalTailResult(ComputePointLsn(copies, checkpointLsn), copies, total);
    }

    /// <summary>
    /// The offline tail copy, for a database no engine holds (#1413): every segment of <paramref name="walDirectory"/> whose last LSN may be at or above
    /// <paramref name="checkpointLsn"/>, copied whole at its declared size. A segment's last LSN is the next one's first LSN minus one; the newest
    /// segment's is unknown, so it is always taken, which also gives an unclean image the segment its recovery needs to run.
    /// </summary>
    internal static unsafe WalTailResult CaptureTailOffline(IWalFileIO io, string walDirectory, long checkpointLsn, IMemoryAllocator allocator,
        IResource owner, IBackupPointSink sink, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(io);
        ArgumentNullException.ThrowIfNull(sink);

        using var block = allocator.AllocatePinned("Backup.TailCopy", owner, CopyChunkBytes, false, PageAlignment);
        var buffer = new Span<byte>(block.DataAsPointer, CopyChunkBytes);
        var valid = new List<(long Id, string Path, long FirstLsn, uint Size)>();
        foreach (var path in io.EnumerateSegmentPaths(walDirectory))
        {
            if (TryReadHeader(io, path, buffer[..WalSegmentHeader.SizeInBytes], out var header))
            {
                valid.Add((header.SegmentId, path, header.FirstLSN, header.SegmentSize));
            }
        }

        valid.Sort(static (a, b) => a.Id.CompareTo(b.Id));
        var copies = new List<WalTailSegmentCopy>();
        long total = 0;
        for (var i = 0; i < valid.Count; i++)
        {
            var lastLsn = i + 1 < valid.Count ? valid[i + 1].FirstLsn - 1 : long.MaxValue;
            if (lastLsn < checkpointLsn)
            {
                continue;
            }

            var copy = CopySegment(io, valid[i].Path, valid[i].Id, valid[i].Size, buffer, sink, null, ct);
            copies.Add(copy);
            total += copy.Prefix.Length;
        }

        return new WalTailResult(ComputePointLsn(copies, checkpointLsn), copies, total);
    }

    /// <summary>
    /// <c>P</c> over a tail: replays the scan <c>RecoveryDriver.Run</c> makes over the restored segments (each recreated at its declared size, zeros past
    /// the copied prefix): segments in path order, the same reader, the same record codec, and the same stop for good at the first corruption. <c>P</c> is
    /// the highest LSN above <paramref name="checkpointLsn"/> that recovery applies as a unit — a commit marker, or a fence record, which applies without
    /// one (C10) — and <paramref name="checkpointLsn"/> when there is none.
    /// </summary>
    internal static long ComputePointLsn(IReadOnlyList<WalTailSegmentCopy> segments, long checkpointLsn)
    {
        var pointLsn = checkpointLsn;
        if (segments.Count == 0)
        {
            return pointLsn;
        }

        // The reader takes its bytes from an IWalFileIO; a private in-memory one holds the prefixes and reads zeros past them (C3).
        using var io = new InMemoryWalFileIO();
        var directory = Path.Combine(Path.GetTempPath(), "typhon-tail-inspect");
        var paths = new List<string>(segments.Count);
        foreach (var segment in segments)
        {
            var path = Path.Combine(directory, $"{segment.SegmentId:D16}.wal");
            using var handle = io.OpenSegment(path, false);
            io.WriteAligned(handle, 0, segment.Prefix);
            paths.Add(path);
        }

        paths.Sort(StringComparer.Ordinal);
        using var reader = new WalSegmentReader(io);
        foreach (var path in paths)
        {
            if (!reader.OpenSegment(path))
            {
                continue;
            }

            while (reader.TryReadNext(out var chunk, out var body))
            {
                if (chunk.ChunkType != (ushort)WalChunkType.Transaction)
                {
                    continue;
                }

                var offset = 0;
                while (RecordCodec.TryReadRecord(body, offset, out var consumed, out var view))
                {
                    offset += consumed;
                    if (view.IsUnknownKind || view.Lsn <= checkpointLsn)
                    {
                        continue;
                    }

                    if ((view.IsTxCommit || view.IsFence) && view.Lsn > pointLsn)
                    {
                        pointLsn = view.Lsn;
                    }
                }
            }

            if (reader.WasTruncated)
            {
                break;
            }
        }

        return pointLsn;
    }

    private static WalTailSegmentCopy CopySegment(IWalFileIO io, string path, long segmentId, long length, Span<byte> buffer, IBackupPointSink sink,
        Action serviceCbw, CancellationToken ct)
    {
        if (length < WalSegmentHeader.SizeInBytes || length % PageAlignment != 0)
        {
            throw new BackupAbortedException(BackupAbortReason.TailUnreadable, $"WAL segment {segmentId} has an unaligned written length {length}.");
        }

        var prefix = new byte[length];
        SafeHandleScope handle;
        try
        {
            handle = new SafeHandleScope(io.OpenSegmentForRead(path));
        }
        catch (IOException e)
        {
            throw new BackupAbortedException(BackupAbortReason.TailUnreadable, $"WAL segment {segmentId} could not be opened.", e);
        }

        using (handle)
        {
            long offset = 0;
            while (offset < length)
            {
                ct.ThrowIfCancellationRequested();
                var n = (int)Math.Min(buffer.Length, length - offset);
                var chunk = buffer[..n];
                try
                {
                    io.ReadAligned(handle.Handle, offset, chunk);
                }
                catch (IOException e)
                {
                    throw new BackupAbortedException(BackupAbortReason.TailUnreadable, $"WAL segment {segmentId} could not be read at {offset}.", e);
                }

                if (offset == 0)
                {
                    var header = MemoryMarshal.Read<WalSegmentHeader>(chunk);
                    if (!header.Validate() || header.SegmentId != segmentId || header.SegmentSize < length)
                    {
                        throw new BackupAbortedException(BackupAbortReason.TailUnreadable, $"WAL segment {segmentId} has no valid header.");
                    }

                    sink.BeginTailSegment(segmentId, header.FirstLSN, header.SegmentSize);
                }

                sink.AppendTail(chunk);
                chunk.CopyTo(prefix.AsSpan((int)offset));
                offset += n;
                serviceCbw?.Invoke();
            }
        }

        sink.EndTailSegment();
        var head = MemoryMarshal.Read<WalSegmentHeader>(prefix);
        return new WalTailSegmentCopy(segmentId, head.FirstLSN, head.SegmentSize, prefix);
    }

    private static bool TryReadHeader(IWalFileIO io, string path, Span<byte> alignedBuffer, out WalSegmentHeader header)
    {
        header = default;
        try
        {
            using var handle = io.OpenSegmentForRead(path);
            io.ReadAligned(handle, 0, alignedBuffer);
            header = MemoryMarshal.Read<WalSegmentHeader>(alignedBuffer);
            return header.Validate();
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>Disposes a segment read handle on scope exit; a struct so the copy loop allocates nothing for it.</summary>
    private readonly struct SafeHandleScope : IDisposable
    {
        internal SafeHandleScope(Microsoft.Win32.SafeHandles.SafeFileHandle handle) => Handle = handle;

        internal Microsoft.Win32.SafeHandles.SafeFileHandle Handle { get; }

        public void Dispose() => Handle?.Dispose();
    }
}
