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
    private readonly WalFileIO _inner = new();
    private readonly ManualResetEventSlim _open = new(true);

    /// <summary>Set once a record write is waiting on the gate.</summary>
    public ManualResetEventSlim WriterHeld { get; } = new(false);

    /// <summary>Holds every later record write until <see cref="Release"/>.</summary>
    public void Hold()
    {
        WriterHeld.Reset();
        _open.Reset();
    }

    /// <summary>Lets held and later writes through.</summary>
    public void Release() => _open.Set();

    public SafeFileHandle OpenSegment(string path, bool withFUA) => _inner.OpenSegment(path, withFUA);

    public void WriteAligned(SafeFileHandle handle, long offset, ReadOnlySpan<byte> data)
    {
        if (offset >= WalSegmentHeader.SizeInBytes && !_open.IsSet)
        {
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
