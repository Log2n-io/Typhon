using JetBrains.Annotations;
using Microsoft.Win32.SafeHandles;
using System;
using System.IO;

namespace Typhon.Engine.Internals;

/// <summary>
/// Production implementation of <see cref="IWalFileIO"/> using <see cref="File.OpenHandle"/> with O_DIRECT semantics (page-cache bypass) where the platform
/// supports it.
/// </summary>
/// <remarks>
/// <para>
/// On Windows, uses <c>(FileOptions)0x20000000</c> for <c>FILE_FLAG_NO_BUFFERING</c> (bypasses OS page cache). On other platforms, this flag is
/// omitted — durability is still guaranteed by <see cref="FileOptions.WriteThrough"/> (FUA) and <see cref="FlushBuffers"/> (<c>fsync</c>/<c>fdatasync</c>).
/// </para>
/// <para>
/// All writes via <see cref="WriteAligned"/> use <see cref="RandomAccess.Write(SafeFileHandle, ReadOnlySpan{byte}, long)"/> for synchronous, positioned writes.
/// </para>
/// </remarks>
[PublicAPI]
internal sealed class WalFileIO : IWalFileIO
{
    /// <summary>FILE_FLAG_NO_BUFFERING = 0x20000000. Bypasses OS page cache for direct I/O. Only effective on Windows.</summary>
    private const FileOptions NoBuffering = (FileOptions)0x20000000;

    /// <inheritdoc />
    public SafeFileHandle OpenSegment(string path, bool withFUA)
    {
        var options = OperatingSystem.IsWindows() ? NoBuffering : FileOptions.None;
        if (withFUA)
        {
            options |= FileOptions.WriteThrough;
        }

        return File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, options);
    }

    /// <inheritdoc />
    public SafeFileHandle OpenSegmentForRead(string path)
    {
        // Read-only access with ReadWrite sharing: allows the WAL writer to keep
        // the active segment open for writing while readers (FPI search, recovery) read it.
        var options = OperatingSystem.IsWindows() ? NoBuffering : FileOptions.None;

        return File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, options);
    }

    /// <inheritdoc />
    public void WriteAligned(SafeFileHandle handle, long offset, ReadOnlySpan<byte> data) => RandomAccess.Write(handle, data, offset);

    /// <inheritdoc />
    public void ReadAligned(SafeFileHandle handle, long offset, Span<byte> buffer) => RandomAccess.Read(handle, buffer, offset);

    /// <inheritdoc />
    public void FlushBuffers(SafeFileHandle handle) => RandomAccess.FlushToDisk(handle);

    /// <inheritdoc />
    public void PreAllocate(SafeFileHandle handle, long size) => RandomAccess.SetLength(handle, size);

    /// <inheritdoc />
    public bool Exists(string path) => File.Exists(path);

    /// <inheritdoc />
    public void Delete(string path) => File.Delete(path);

    /// <inheritdoc />
    public void Dispose()
    {
        // No per-instance resources to dispose; file handles are owned by callers.
    }
}
