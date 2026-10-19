using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32.SafeHandles;

namespace Typhon.Engine.Internals;

/// <summary>
/// A read-only <see cref="IWalFileIO"/> over WAL segments as a backup point's tail holds them: each segment readable up to its copied length, zeros past
/// it, the way restore recreates it (C3). Lets <see cref="WalSegmentReader"/> scan a tail without a copy of it: over the live segments while the reclaim
/// floor keeps them (<see cref="OverSource"/>), or over a stored point's prefixes in memory (<see cref="OverPrefixes"/>).
/// </summary>
internal sealed class WalTailView : IWalFileIO
{
    private readonly IWalFileIO _source;   // null over prefixes
    private readonly Dictionary<string, long> _lengths;
    private readonly Dictionary<string, ReadOnlyMemory<byte>> _prefixes;
    private readonly Dictionary<SafeFileHandle, string> _open = new(ReferenceEqualityComparer.Instance);
    private int _nextHandle;

    private WalTailView(IWalFileIO source, Dictionary<string, long> lengths, Dictionary<string, ReadOnlyMemory<byte>> prefixes)
    {
        _source = source;
        _lengths = lengths;
        _prefixes = prefixes;
    }

    /// <summary>The segments of <paramref name="source"/> named in <paramref name="lengths"/>, each read up to its length (a 4 KiB multiple).</summary>
    internal static WalTailView OverSource(IWalFileIO source, Dictionary<string, long> lengths)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new WalTailView(source, lengths, null);
    }

    /// <summary>Segments held in memory, keyed by path.</summary>
    internal static WalTailView OverPrefixes(Dictionary<string, ReadOnlyMemory<byte>> prefixes)
    {
        var lengths = new Dictionary<string, long>(prefixes.Count);
        foreach (var (path, bytes) in prefixes)
        {
            lengths.Add(path, bytes.Length);
        }

        return new WalTailView(null, lengths, prefixes);
    }

    public bool Exists(string path) => _lengths.ContainsKey(path);

    public IReadOnlyList<string> EnumerateSegmentPaths(string directory)
    {
        var paths = new List<string>(_lengths.Keys);
        paths.Sort(StringComparer.Ordinal);
        return paths;
    }

    public SafeFileHandle OpenSegmentForRead(string path)
    {
        if (!_lengths.ContainsKey(path))
        {
            throw new FileNotFoundException("Not a segment of this tail.", path);
        }

        var handle = _source != null ? _source.OpenSegmentForRead(path) : new SafeFileHandle(new IntPtr(++_nextHandle), false);
        _open[handle] = path;
        return handle;
    }

    public void ReadAligned(SafeFileHandle handle, long offset, Span<byte> buffer)
    {
        var path = _open[handle];
        var n = (int)Math.Clamp(_lengths[path] - offset, 0, buffer.Length);
        if (n > 0)
        {
            if (_source != null)
            {
                _source.ReadAligned(handle, offset, buffer[..n]);
            }
            else
            {
                _prefixes[path].Span.Slice((int)offset, n).CopyTo(buffer);
            }
        }

        buffer[n..].Clear();
    }

    public SafeFileHandle OpenSegment(string path, bool withFUA) => throw new NotSupportedException("A tail view is read-only.");

    public void WriteAligned(SafeFileHandle handle, long offset, ReadOnlySpan<byte> data) => throw new NotSupportedException("A tail view is read-only.");

    public void FlushBuffers(SafeFileHandle handle) => throw new NotSupportedException("A tail view is read-only.");

    public void PreAllocate(SafeFileHandle handle, long size) => throw new NotSupportedException("A tail view is read-only.");

    public void Delete(string path) => throw new NotSupportedException("A tail view is read-only.");

    public void Dispose()
    {
    }
}
