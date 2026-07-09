using JetBrains.Annotations;
using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Purely in-memory implementation of <see cref="IWalFileIO"/> for unit tests. No files are created on disk — all data is tracked in memory buffers.
/// </summary>
/// <remarks>
/// <para>
/// Each call to <see cref="OpenSegment"/> returns a synthetic <see cref="SafeFileHandle"/> (not backed by a real file) and creates an in-memory
/// <see cref="MemorySegment"/> keyed by normalized path. The handle is used solely as a lookup token via <see cref="System.Runtime.InteropServices.SafeHandle.DangerousGetHandle"/>.
/// </para>
/// <para>
/// Tests use <see cref="GetSegment"/> to inspect written data for assertions.
/// </para>
/// </remarks>
[PublicAPI]
internal sealed class InMemoryWalFileIO : IWalFileIO
{
    private readonly ConcurrentDictionary<nint, string> _handleToPath = new();
    private int _nextHandleId;

    /// <summary>
    /// Gets the in-memory segment for a given path, or null if not opened.
    /// </summary>
    public MemorySegment GetSegment(string path) => Segments.GetValueOrDefault(NormalizePath(path));

    /// <summary>
    /// Gets all opened segment paths.
    /// </summary>
    public ConcurrentDictionary<string, MemorySegment> Segments { get; } = new();

    /// <inheritdoc />
    public SafeFileHandle OpenSegment(string path, bool withFUA)
    {
        var normalizedPath = NormalizePath(path);

        // Create a synthetic handle (not backed by a real file).
        // ownsHandle: false ensures Dispose() won't call CloseHandle on the fake pointer.
        var id = Interlocked.Increment(ref _nextHandleId);
        var handle = new SafeFileHandle(new IntPtr(id), false);

        // Track in-memory segment
        var segment = Segments.GetOrAdd(normalizedPath, _ => new MemorySegment(withFUA));
        segment.OpenCount++;

        // Map handle to path for WriteAligned/FlushBuffers/PreAllocate lookups
        _handleToPath[handle.DangerousGetHandle()] = normalizedPath;

        return handle;
    }

    /// <inheritdoc />
    public SafeFileHandle OpenSegmentForRead(string path) => OpenSegment(path, false);

    /// <inheritdoc />
    public void WriteAligned(SafeFileHandle handle, long offset, ReadOnlySpan<byte> data)
    {
        var seg = FindSegment(handle);
        if (seg == null)
        {
            return;
        }

        // Ensure the buffer is large enough
        var needed = (int)(offset + data.Length);
        if (needed > seg.Data.Length)
        {
            var newData = new byte[Math.Max(needed, seg.Data.Length * 2)];
            Buffer.BlockCopy(seg.Data, 0, newData, 0, seg.Data.Length);
            seg.Data = newData;
        }

        data.CopyTo(seg.Data.AsSpan((int)offset));
        seg.WriteCount++;
        seg.TotalBytesWritten += data.Length;

        if (offset + data.Length > seg.WrittenLength)
        {
            seg.WrittenLength = (int)(offset + data.Length);
        }
    }

    /// <inheritdoc />
    public void ReadAligned(SafeFileHandle handle, long offset, Span<byte> buffer)
    {
        var seg = FindSegment(handle);
        if (seg == null)
        {
            return;
        }

        var available = seg.Data.Length - (int)offset;
        var toCopy = Math.Min(available, buffer.Length);
        if (toCopy > 0)
        {
            seg.Data.AsSpan((int)offset, toCopy).CopyTo(buffer);
        }

        // Zero any remaining buffer space beyond available data
        if (toCopy < buffer.Length)
        {
            buffer[toCopy..].Clear();
        }
    }

    /// <inheritdoc />
    public void FlushBuffers(SafeFileHandle handle)
    {
        var seg = FindSegment(handle);
        if (seg != null)
        {
            seg.FlushCount++;
        }
    }

    /// <inheritdoc />
    public void PreAllocate(SafeFileHandle handle, long size)
    {
        var seg = FindSegment(handle);
        if (seg == null)
        {
            return;
        }

        if (size > seg.Data.Length)
        {
            var newData = new byte[(int)size];
            Buffer.BlockCopy(seg.Data, 0, newData, 0, seg.Data.Length);
            seg.Data = newData;
        }

        seg.PreAllocatedSize = (int)size;
    }

    /// <inheritdoc />
    public bool Exists(string path) => Segments.ContainsKey(NormalizePath(path));

    /// <inheritdoc />
    public void Delete(string path) => Segments.TryRemove(NormalizePath(path), out _);

    /// <inheritdoc />
    public void Dispose()
    {
        _handleToPath.Clear();
        Segments.Clear();
    }

    private MemorySegment FindSegment(SafeFileHandle handle)
    {
        if (_handleToPath.TryGetValue(handle.DangerousGetHandle(), out var path))
        {
            if (Segments.TryGetValue(path, out var seg))
            {
                return seg;
            }
        }

        return null;
    }

    private static string NormalizePath(string path) => System.IO.Path.GetFullPath(path);

    /// <summary>
    /// Represents an in-memory WAL segment file for testing.
    /// </summary>
    [PublicAPI]
    public sealed class MemorySegment
    {
        /// <summary>The raw data buffer (simulates file contents).</summary>
        public byte[] Data;

        /// <summary>Whether FUA was requested when opening.</summary>
        public bool WithFUA { get; }

        /// <summary>Number of times this segment has been opened.</summary>
        public int OpenCount;

        /// <summary>Number of write operations performed.</summary>
        public int WriteCount;

        /// <summary>Total bytes written across all writes.</summary>
        public long TotalBytesWritten;

        /// <summary>Number of explicit flush operations.</summary>
        public int FlushCount;

        /// <summary>File length as set by PreAllocate.</summary>
        public int PreAllocatedSize;

        /// <summary>High-water mark of written data.</summary>
        public int WrittenLength;

        internal MemorySegment(bool withFUA)
        {
            WithFUA = withFUA;
            Data = new byte[64 * 1024]; // Start with 64KB, grows as needed
        }
    }
}
