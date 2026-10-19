using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Typhon.Engine.Tests;

/// <summary>
/// A disk WAL backend whose record writes can be held: while held, the writer thread blocks in its next data write (segment headers pass), so a test
/// can stage "published but not durable" without timing.
/// </summary>
internal sealed class GatedWalFileIO : IWalFileIO
{
    private const int Block = 4096;
    private readonly WalFileIO _inner = new();
    private readonly ManualResetEventSlim _open = new(true);
    private volatile bool _torn;
    private int _passes;

    /// <summary>Set once a record write is waiting on the gate.</summary>
    public ManualResetEventSlim WriterHeld { get; } = new(false);

    /// <summary>Where the held write lands and how long it is; set with <see cref="WriterHeld"/>.</summary>
    public (long Offset, int Length) HeldWrite { get; private set; }

    /// <summary>The record writes <see cref="HoldAfter"/> let through, in order.</summary>
    public List<(long Offset, int Length)> PassedWrites { get; } = [];

    /// <summary>Holds every later record write until <see cref="Release"/>.</summary>
    public void Hold()
    {
        _torn = false;
        _passes = 0;
        PassedWrites.Clear();
        WriterHeld.Reset();
        _open.Reset();
    }

    /// <summary>
    /// Like <see cref="Hold"/>, but a held write of more than one 4 KiB block lands its first block before it waits: a write in flight, partly on disk.
    /// </summary>
    public void HoldTorn()
    {
        Hold();
        _torn = true;
    }

    /// <summary>
    /// Like <see cref="Hold"/>, but lets the next <paramref name="passes"/> record writes through first (recorded in <see cref="PassedWrites"/>).
    /// </summary>
    public void HoldAfter(int passes)
    {
        Hold();
        _passes = passes;
    }

    /// <summary>Lets held and later writes through.</summary>
    public void Release() => _open.Set();

    public SafeFileHandle OpenSegment(string path, bool withFUA) => _inner.OpenSegment(path, withFUA);

    public void WriteAligned(SafeFileHandle handle, long offset, ReadOnlySpan<byte> data)
    {
        if (offset >= WalSegmentHeader.SizeInBytes && !_open.IsSet && Interlocked.Decrement(ref _passes) >= 0)
        {
            PassedWrites.Add((offset, data.Length));
        }
        else if (offset >= WalSegmentHeader.SizeInBytes && !_open.IsSet)
        {
            if (_torn && data.Length > Block)
            {
                _inner.WriteAligned(handle, offset, data[..Block]);
            }

            HeldWrite = (offset, data.Length);
            WriterHeld.Set();
            _open.Wait();
        }

        _inner.WriteAligned(handle, offset, data);
    }

    public void FlushBuffers(SafeFileHandle handle) => _inner.FlushBuffers(handle);

    public void PreAllocate(SafeFileHandle handle, long size) => _inner.PreAllocate(handle, size);

    public void ReadAligned(SafeFileHandle handle, long offset, Span<byte> buffer) => _inner.ReadAligned(handle, offset, buffer);

    public SafeFileHandle OpenSegmentForRead(string path) => _inner.OpenSegmentForRead(path);

    public bool Exists(string path) => _inner.Exists(path);

    public void Delete(string path) => _inner.Delete(path);

    public IReadOnlyList<string> EnumerateSegmentPaths(string directory) => _inner.EnumerateSegmentPaths(directory);

    public void Dispose()
    {
        _open.Set();
        _inner.Dispose();
    }
}
