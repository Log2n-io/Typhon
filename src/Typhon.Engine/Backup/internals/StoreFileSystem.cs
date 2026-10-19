using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Typhon.Engine.Internals;

/// <summary>One file found by <see cref="IStoreFileSystem.EnumerateFiles"/>.</summary>
/// <param name="Name">File name only.</param>
/// <param name="Length">Size in bytes.</param>
internal readonly record struct StoreFileEntry(string Name, long Length);

/// <summary>
/// An open file of a backup store or a restore target, addressed by absolute offsets. Never a stream: every read and write names its offset.
/// </summary>
internal interface IStoreFile : IDisposable
{
    /// <summary>The path the file was opened at.</summary>
    string Path { get; }

    /// <summary>The file's current length in bytes.</summary>
    long Length { get; }

    /// <summary>Reads at most <c>destination.Length</c> bytes at <paramref name="offset"/>; returns the count read, <c>0</c> at end of file.</summary>
    int Read(Span<byte> destination, long offset);

    /// <summary>Writes every byte of <paramref name="source"/> at <paramref name="offset"/>.</summary>
    void Write(ReadOnlySpan<byte> source, long offset);

    /// <summary>
    /// Writes every byte of <paramref name="source"/> at <paramref name="offset"/> without waiting. The memory must stay valid and unchanged
    /// until the returned task completes.
    /// </summary>
    Task WriteAsync(ReadOnlyMemory<byte> source, long offset);

    /// <summary>Sets the file's length; new bytes read as zeros.</summary>
    void SetLength(long length);

    /// <summary>Makes every byte written so far durable (<c>FlushFileBuffers</c> / <c>fsync</c>).</summary>
    void Flush();
}

/// <summary>
/// The file-system operations the backup store, the pack writer and the restore use, behind one seam so a test can run them over an in-memory
/// sparse file system (64-bit offsets with no disk) or a crash model (durability per operation), and so every durable step is one named call.
/// </summary>
/// <remarks>
/// <para>
/// <b>Durability is explicit.</b> File content is durable after <see cref="IStoreFile.Flush"/>. A directory entry (create, rename, delete) is
/// durable after <see cref="Rename"/> on Windows, whose write-through flushes the volume's metadata log, and after <see cref="FlushDirectory"/> of
/// its parent on Linux. <see cref="StoreFileSystem.MoveDurable"/> composes the two so callers cannot forget the second half.
/// </para>
/// </remarks>
internal interface IStoreFileSystem
{
    /// <summary>
    /// Creates a file that must not exist yet (<c>CreateNew</c>), opened read/write with no sharing. A non-zero
    /// <paramref name="preallocationSize"/> reserves the space up front and fails early when the volume is short; the file's length stays the
    /// bytes written.
    /// </summary>
    IStoreFile OpenCreateNew(string path, long preallocationSize = 0);

    /// <summary>Opens an existing file read-only, sharing <c>Read | Delete</c>, so a retention delete never fails because a reader holds it.</summary>
    IStoreFile OpenRead(string path);

    /// <summary>
    /// Opens (creating when absent) <paramref name="path"/> with no sharing and keeps it open until the returned handle is disposed. Returns
    /// <c>null</c> when another handle holds it: Windows share mode, Linux <c>flock(LOCK_EX)</c>.
    /// </summary>
    IDisposable TryLockExclusive(string path);

    /// <summary>
    /// Whether <paramref name="path"/> can be opened with no sharing right now, i.e. no process holds it open. A missing file is not held.
    /// </summary>
    bool ProbeExclusive(string path);

    /// <summary>
    /// Renames a file or a directory. Fails when <paramref name="destination"/> exists unless <paramref name="replace"/> (files only). On Windows
    /// the rename is write-through: it is on disk when this returns.
    /// </summary>
    void Rename(string source, string destination, bool replace);

    /// <summary>Makes the entries of <paramref name="directory"/> durable. Unix: <c>open</c> + <c>fsync</c> of the directory. Windows: nothing.</summary>
    void FlushDirectory(string directory);

    /// <summary>Whether a file exists at <paramref name="path"/>.</summary>
    bool FileExists(string path);

    /// <summary>Whether a directory exists at <paramref name="path"/>.</summary>
    bool DirectoryExists(string path);

    /// <summary>Creates a directory and any missing parent.</summary>
    void CreateDirectory(string path);

    /// <summary>Deletes a file; a missing file is not an error.</summary>
    void DeleteFile(string path);

    /// <summary>Deletes a directory and everything under it; a missing directory is not an error.</summary>
    void DeleteDirectory(string path);

    /// <summary>The files directly inside <paramref name="directory"/>, ordered by name (ordinal). A missing directory has none.</summary>
    IReadOnlyList<StoreFileEntry> EnumerateFiles(string directory);

    /// <summary>Whether a database bundle's advisory lock names a live process (<see cref="DatabaseLockFile.IsHeldByLiveProcess"/>).</summary>
    bool IsBundleHeldByLiveProcess(string bundleDirectory);

    /// <summary>The absolute form of <paramref name="path"/>, so no caller ever derives an empty parent from a bare relative name.</summary>
    string GetFullPath(string path) => System.IO.Path.GetFullPath(path);

    /// <summary>
    /// Like <see cref="TryLockExclusive"/>, for a lock file that exists only while it is held: releasing it removes the file. The removal happens
    /// while the handle is still held, so no other claimant can open the file between them. A process that dies leaves the file, unheld: the next
    /// claimant takes it over.
    /// </summary>
    IDisposable TryLockTransient(string path) => TryLockExclusive(path);

    /// <summary>
    /// Takes a bundle's advisory lock as a non-yieldable holder (<see cref="DatabaseLockFile.TryAcquire"/>); <c>false</c> when a live process holds it.
    /// </summary>
    bool TryAcquireBundleLock(string bundleDirectory, out DatabaseLockFile.LockInfo acquired) =>
        DatabaseLockFile.TryAcquire(bundleDirectory, yieldable: false, out acquired);

    /// <summary>Releases a lock <see cref="TryAcquireBundleLock"/> took, now at <paramref name="bundleDirectory"/> (the bundle may have moved).</summary>
    void ReleaseBundleLock(string bundleDirectory, in DatabaseLockFile.LockInfo acquired) => DatabaseLockFile.Release(bundleDirectory, acquired);
}

/// <summary>Helpers over <see cref="IStoreFile"/>.</summary>
internal static class StoreFileExtensions
{
    /// <summary>Reads exactly <c>destination.Length</c> bytes at <paramref name="offset"/>; <c>false</c> when the file ends first.</summary>
    public static bool ReadExactly(this IStoreFile file, Span<byte> destination, long offset)
    {
        var total = 0;
        while (total < destination.Length)
        {
            var read = file.Read(destination[total..], offset + total);
            if (read <= 0)
            {
                return false;
            }

            total += read;
        }

        return true;
    }
}

/// <summary>The real file system, plus the durable compositions every caller goes through.</summary>
internal sealed partial class StoreFileSystem : IStoreFileSystem
{
    /// <summary>The process-wide instance.</summary>
    public static readonly StoreFileSystem Instance = new();

    private StoreFileSystem()
    {
    }

    /// <summary>
    /// Moves <paramref name="source"/> to <paramref name="destination"/> so that the new name survives a power cut once this returns: a
    /// write-through rename, then a flush of the directories involved (BKP-08). The content must have been flushed before.
    /// </summary>
    /// <param name="fs">The file system.</param>
    /// <param name="source">The current path (file or directory).</param>
    /// <param name="destination">The new path, in the same volume.</param>
    /// <param name="replace">Replace an existing destination file. Never set for a published pack or a restored bundle.</param>
    public static void MoveDurable(IStoreFileSystem fs, string source, string destination, bool replace)
    {
        fs.Rename(source, destination, replace);
        var destinationParent = System.IO.Path.GetDirectoryName(destination);
        fs.FlushDirectory(destinationParent);
        var sourceParent = System.IO.Path.GetDirectoryName(source);
        if (!string.Equals(sourceParent, destinationParent, StringComparison.Ordinal))
        {
            fs.FlushDirectory(sourceParent);
        }
    }

    /// <summary>
    /// Replaces the file at <paramref name="path"/> with <paramref name="content"/> durably: a temporary sibling written and flushed, then moved
    /// over the target with <see cref="MoveDurable"/>. A crash leaves the old content or the new one, never a mix.
    /// </summary>
    public static void WriteAllBytesDurable(IStoreFileSystem fs, string path, ReadOnlySpan<byte> content)
    {
        var temp = path + ".tmp";
        fs.DeleteFile(temp);
        using (var file = fs.OpenCreateNew(temp))
        {
            file.Write(content, 0);
            file.Flush();
        }

        MoveDurable(fs, temp, path, replace: true);
    }

    /// <summary>Reads a whole (small) file.</summary>
    public static byte[] ReadAllBytes(IStoreFileSystem fs, string path)
    {
        using var file = fs.OpenRead(path);
        var length = file.Length;
        if (length > int.MaxValue)
        {
            throw new IOException($"'{path}' is too large to read whole ({length} bytes).");
        }

        var bytes = new byte[length];
        if (!file.ReadExactly(bytes, 0))
        {
            throw new IOException($"'{path}' ended before its length ({length} bytes).");
        }

        return bytes;
    }

    /// <inheritdoc />
    public IStoreFile OpenCreateNew(string path, long preallocationSize = 0) =>
        new DiskFile(path, File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, FileOptions.None, preallocationSize));

    /// <inheritdoc />
    public IStoreFile OpenRead(string path) =>
        new DiskFile(path, File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete));

    /// <inheritdoc />
    public IDisposable TryLockExclusive(string path)
    {
        try
        {
            return File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex) when (IsHeldFailure(ex))
        {
            return null;
        }
    }

    /// <inheritdoc />
    public IDisposable TryLockTransient(string path)
    {
        try
        {
            // Delete sharing so the holder can remove the file while still holding it (Windows: delete-pending, refusing every new open until the
            // handle closes; Linux: unlinked under its flock).
            return new TransientLock(path, File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Delete));
        }
        catch (IOException ex) when (IsHeldFailure(ex))
        {
            return null;
        }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
        {
            // The previous holder's file is delete-pending: it is still being released.
            return null;
        }
    }

    private sealed class TransientLock(string path, SafeFileHandle handle) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left behind, unheld: the next claimant takes it over.
            }

            handle.Dispose();
        }
    }

    /// <inheritdoc />
    public bool ProbeExclusive(string path)
    {
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (IOException ex) when (IsHeldFailure(ex))
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // Cannot tell, so it must read as held: the alternative is acting on a database somebody may be using.
            return false;
        }
    }

    /// <inheritdoc />
    public void Rename(string source, string destination, bool replace)
    {
        if (OperatingSystem.IsWindows())
        {
            var flags = MoveFileWriteThrough | (replace ? MoveFileReplaceExisting : 0u);
            if (!MoveFileEx(ExtendedLength(source), ExtendedLength(destination), flags))
            {
                var error = Marshal.GetLastPInvokeError();
                throw new IOException($"Cannot move '{source}' to '{destination}': {new Win32Exception(error).Message}", error);
            }

            return;
        }

        if (Directory.Exists(source))
        {
            // Directory.Move refuses an existing destination. On Unix it is an lstat check then rename(2): safe only because every caller holds the
            // store lock or works in a directory it owns (plan 1413 §1.2).
            Directory.Move(source, destination);
        }
        else
        {
            File.Move(source, destination, replace);
        }
    }

    /// <summary>
    /// The extended-length (<c>\\?\</c>) form of a full path: the raw Win32 call does not get .NET's long-path handling, and a restore's staging path
    /// under a deep directory passes MAX_PATH easily.
    /// </summary>
    private static string ExtendedLength(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return full;
        }

        return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }

    /// <inheritdoc />
    public void FlushDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows: the write-through rename already put the entry on disk, and NTFS offers no handle on a directory to flush anyway.
            return;
        }

        // Every Unix: Linux, and macOS and the BSDs, which open and fsync a directory the same way (it used to be skipped there, silently).
        FlushDirectoryPosix(directory);
    }

    /// <inheritdoc />
    public bool FileExists(string path) => File.Exists(path);

    /// <inheritdoc />
    public bool DirectoryExists(string path) => Directory.Exists(path);

    /// <inheritdoc />
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    /// <inheritdoc />
    public void DeleteFile(string path) => File.Delete(path);

    /// <inheritdoc />
    public void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<StoreFileEntry> EnumerateFiles(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var list = new List<StoreFileEntry>();
        foreach (var info in new DirectoryInfo(directory).EnumerateFiles())
        {
            list.Add(new StoreFileEntry(info.Name, info.Length));
        }

        list.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return list;
    }

    /// <inheritdoc />
    public bool IsBundleHeldByLiveProcess(string bundleDirectory) => DatabaseLockFile.IsHeldByLiveProcess(bundleDirectory);

    private static bool IsHeldFailure(IOException ex) => IsSharingViolation(ex);

    /// <summary>
    /// Whether an open failed because another handle holds the file — and only that. Windows: <c>ERROR_SHARING_VIOLATION</c> or
    /// <c>ERROR_LOCK_VIOLATION</c>. Unix: <c>EWOULDBLOCK</c> from the <c>flock</c> behind <see cref="FileShare.None"/> (11 on Linux, 35 on macOS
    /// and the BSDs). Any other I/O failure is an I/O failure, never "locked".
    /// </summary>
    internal static bool IsSharingViolation(IOException ex) => ex.HResult == SharingViolationHResult
        || (OperatingSystem.IsWindows() && ex.HResult == unchecked((int)0x80070021));

    /// <summary>The <see cref="Exception.HResult"/> of a sharing violation on this platform.</summary>
    internal static int SharingViolationHResult =>
        OperatingSystem.IsWindows() ? unchecked((int)0x80070020) : OperatingSystem.IsLinux() ? 11 : 35;

    private static void FlushDirectoryPosix(string directory)
    {
        // .NET cannot open a directory (File.OpenHandle refuses S_IFDIR with EACCES), so the fsync goes through libc. O_RDONLY opens a directory;
        // O_DIRECTORY would only add the type check, and its value differs between platforms and architectures. O_CLOEXEC, so a process this one
        // starts in the window never inherits the descriptor. A signal (EINTR) retries the call rather than failing the flush.
        int fd;
        do
        {
            fd = PosixOpen(directory, OCloexec);
        }
        while (fd < 0 && Marshal.GetLastPInvokeError() == Eintr);

        if (fd < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new IOException($"Cannot open directory '{directory}' to flush it (errno {error}).", error);
        }

        try
        {
            int result;
            do
            {
                result = PosixFsync(fd);
            }
            while (result != 0 && Marshal.GetLastPInvokeError() == Eintr);

            if (result != 0)
            {
                var error = Marshal.GetLastPInvokeError();
                throw new IOException($"fsync of directory '{directory}' failed (errno {error}).", error);
            }
        }
        finally
        {
            PosixClose(fd);
        }
    }

    /// <summary><c>EINTR</c>: 4 on every Unix.</summary>
    private const int Eintr = 4;

    /// <summary><c>O_CLOEXEC</c>, which differs between Unixes: Linux 0x80000 (x64 and arm64), macOS 0x1000000, FreeBSD 0x100000.</summary>
    internal static int OCloexec =>
        OperatingSystem.IsLinux() ? 0x80000 : OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() ? 0x1000000 : OperatingSystem.IsFreeBSD() ? 0x100000 : 0;

    private const uint MoveFileReplaceExisting = 0x1;
    private const uint MoveFileWriteThrough = 0x8;

    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveFileEx(string existingFileName, string newFileName, uint flags);

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int PosixOpen(string path, int flags);

    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int PosixFsync(int fd);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int PosixClose(int fd);

    /// <summary>A file on disk, read and written through <see cref="RandomAccess"/> at explicit offsets.</summary>
    private sealed class DiskFile : IStoreFile
    {
        private readonly SafeFileHandle _handle;

        public DiskFile(string path, SafeFileHandle handle)
        {
            Path = path;
            _handle = handle;
        }

        public string Path { get; }

        public long Length => RandomAccess.GetLength(_handle);

        public int Read(Span<byte> destination, long offset) => RandomAccess.Read(_handle, destination, offset);

        public void Write(ReadOnlySpan<byte> source, long offset) => RandomAccess.Write(_handle, source, offset);

        public Task WriteAsync(ReadOnlyMemory<byte> source, long offset) => RandomAccess.WriteAsync(_handle, source, offset).AsTask();

        public void SetLength(long length) => RandomAccess.SetLength(_handle, length);

        public void Flush() => RandomAccess.FlushToDisk(_handle);

        public void Dispose() => _handle.Dispose();
    }
}
