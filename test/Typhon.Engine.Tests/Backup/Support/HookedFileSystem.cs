using System;
using System.Collections.Generic;
using System.IO;

namespace Typhon.Engine.Tests.Backup;

/// <summary>
/// An <see cref="IStoreFileSystem"/> passing every call to another one, with hooks a test sets to stage what another process does between two
/// operations of the code under test (a retention deleting packs under a verify), or to fail one operation.
/// </summary>
internal sealed class HookedFileSystem(IStoreFileSystem inner) : IStoreFileSystem
{
    public IStoreFileSystem Inner { get; } = inner;

    /// <summary>Runs before every <see cref="OpenRead"/>, with its path.</summary>
    public Action<string> BeforeOpenRead { get; init; }

    /// <summary>When it returns true for a path, <see cref="OpenRead"/> throws <see cref="FileNotFoundException"/> without opening it.</summary>
    public Func<string, bool> OpenReadFails { get; init; }

    /// <summary>Runs before every <see cref="DeleteFile"/>, with its path.</summary>
    public Action<string> BeforeDelete { get; init; }

    public IStoreFile OpenCreateNew(string path, long preallocationSize = 0) => Inner.OpenCreateNew(path, preallocationSize);

    public IStoreFile OpenRead(string path)
    {
        BeforeOpenRead?.Invoke(path);
        if (OpenReadFails?.Invoke(path) == true)
        {
            throw new FileNotFoundException($"Could not find file '{path}' (staged).", path);
        }

        return Inner.OpenRead(path);
    }

    public IDisposable TryLockExclusive(string path) => Inner.TryLockExclusive(path);

    public bool ProbeExclusive(string path) => Inner.ProbeExclusive(path);

    public void Rename(string source, string destination, bool replace) => Inner.Rename(source, destination, replace);

    public void FlushDirectory(string directory) => Inner.FlushDirectory(directory);

    public bool FileExists(string path) => Inner.FileExists(path);

    public bool DirectoryExists(string path) => Inner.DirectoryExists(path);

    public void CreateDirectory(string path) => Inner.CreateDirectory(path);

    public void DeleteFile(string path)
    {
        BeforeDelete?.Invoke(path);
        Inner.DeleteFile(path);
    }

    public void DeleteDirectory(string path) => Inner.DeleteDirectory(path);

    public IReadOnlyList<StoreFileEntry> EnumerateFiles(string directory) => Inner.EnumerateFiles(directory);

    public bool IsBundleHeldByLiveProcess(string bundleDirectory) => Inner.IsBundleHeldByLiveProcess(bundleDirectory);

    public string GetFullPath(string path) => Inner.GetFullPath(path);

    public IDisposable TryLockTransient(string path) => Inner.TryLockTransient(path);

    public bool TryAcquireBundleLock(string bundleDirectory, out DatabaseLockFile.LockInfo acquired) =>
        Inner.TryAcquireBundleLock(bundleDirectory, out acquired);

    public void ReleaseBundleLock(string bundleDirectory, in DatabaseLockFile.LockInfo acquired) => Inner.ReleaseBundleLock(bundleDirectory, acquired);
}
