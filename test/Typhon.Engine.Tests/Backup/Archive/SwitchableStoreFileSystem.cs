using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine.Tests.Backup;

/// <summary>
/// The real file system, with the WAL archive's writes switchable: <see cref="Failing"/> makes every new file under <c>wal/</c> throw (a store that is
/// unreachable), <see cref="Blocking"/> holds every write to a <c>.twal</c> until released (a hung share).
/// </summary>
internal sealed class SwitchableStoreFileSystem : IStoreFileSystem
{
    private static readonly IStoreFileSystem Disk = StoreFileSystem.Instance;
    private readonly ManualResetEventSlim _released = new(true);

    /// <summary>Every new file under a <c>wal</c> directory fails with an <see cref="IOException"/>.</summary>
    public volatile bool Failing;

    /// <summary>Set by the first write that started blocking.</summary>
    public readonly ManualResetEventSlim WriteBlocked = new(false);

    /// <summary>Holds every write to an archived segment until <see cref="Release"/>.</summary>
    public void Block() => _released.Reset();

    public void Release() => _released.Set();

    private static bool InArchive(string path) => path.Replace('\\', '/').Contains("/wal/", StringComparison.Ordinal);

    public IStoreFile OpenCreateNew(string path, long preallocationSize = 0)
    {
        if (Failing && InArchive(path))
        {
            throw new IOException($"simulated: the store is unreachable ({path})");
        }

        var file = Disk.OpenCreateNew(path, preallocationSize);
        return path.EndsWith(".twal.tmp", StringComparison.Ordinal) ? new Gated(file, this) : file;
    }

    public IStoreFile OpenRead(string path) => Disk.OpenRead(path);

    public IDisposable TryLockExclusive(string path) => Disk.TryLockExclusive(path);

    public IDisposable TryLockTransient(string path) => Disk.TryLockTransient(path);

    public bool ProbeExclusive(string path) => Disk.ProbeExclusive(path);

    public void Rename(string source, string destination, bool replace) => Disk.Rename(source, destination, replace);

    public void FlushDirectory(string directory) => Disk.FlushDirectory(directory);

    public bool FileExists(string path) => Disk.FileExists(path);

    public bool DirectoryExists(string path) => Disk.DirectoryExists(path);

    public void CreateDirectory(string path) => Disk.CreateDirectory(path);

    public void DeleteFile(string path) => Disk.DeleteFile(path);

    public void DeleteDirectory(string path) => Disk.DeleteDirectory(path);

    public IReadOnlyList<StoreFileEntry> EnumerateFiles(string directory) => Disk.EnumerateFiles(directory);

    public IReadOnlyList<string> EnumerateDirectories(string directory) => Disk.EnumerateDirectories(directory);

    public bool IsBundleHeldByLiveProcess(string bundleDirectory) => Disk.IsBundleHeldByLiveProcess(bundleDirectory);

    private sealed class Gated(IStoreFile inner, SwitchableStoreFileSystem owner) : IStoreFile
    {
        public string Path => inner.Path;

        public long Length => inner.Length;

        public int Read(Span<byte> destination, long offset) => inner.Read(destination, offset);

        public void Write(ReadOnlySpan<byte> source, long offset)
        {
            if (!owner._released.IsSet)
            {
                owner.WriteBlocked.Set();
                owner._released.Wait();
            }

            inner.Write(source, offset);
        }

        public Task WriteAsync(ReadOnlyMemory<byte> source, long offset) => inner.WriteAsync(source, offset);

        public void SetLength(long length) => inner.SetLength(length);

        public void Flush() => inner.Flush();

        public void Dispose() => inner.Dispose();
    }
}
