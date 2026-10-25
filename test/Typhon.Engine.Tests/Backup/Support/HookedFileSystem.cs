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

    /// <summary>When set and returning an exception for an operation and its path, that operation throws it instead of running. Settable mid-test.</summary>
    public Func<string, string, Exception> Fault { get; set; }

    /// <summary>Runs before every operation, with its name and path: a test parks a call here.</summary>
    public Action<string, string> BeforeAny { get; set; }

    private void Check(string op, string path)
    {
        BeforeAny?.Invoke(op, path);
        if (Fault?.Invoke(op, path) is { } e)
        {
            throw e;
        }
    }

    public IStoreFile OpenCreateNew(string path, long preallocationSize = 0)
    {
        Check(nameof(OpenCreateNew), path);
        return Inner.OpenCreateNew(path, preallocationSize);
    }

    public IStoreFile OpenRead(string path)
    {
        Check(nameof(OpenRead), path);
        BeforeOpenRead?.Invoke(path);
        if (OpenReadFails?.Invoke(path) == true)
        {
            throw new FileNotFoundException($"Could not find file '{path}' (staged).", path);
        }

        return Inner.OpenRead(path);
    }

    public IDisposable TryLockExclusive(string path)
    {
        Check(nameof(TryLockExclusive), path);
        return Inner.TryLockExclusive(path);
    }

    public bool ProbeExclusive(string path) => Inner.ProbeExclusive(path);

    public void Rename(string source, string destination, bool replace) => Inner.Rename(source, destination, replace);

    public void FlushDirectory(string directory) => Inner.FlushDirectory(directory);

    public bool FileExists(string path)
    {
        Check(nameof(FileExists), path);
        return Inner.FileExists(path);
    }

    public bool DirectoryExists(string path) => Inner.DirectoryExists(path);

    public void CreateDirectory(string path)
    {
        Check(nameof(CreateDirectory), path);
        Inner.CreateDirectory(path);
    }

    public void DeleteFile(string path)
    {
        Check(nameof(DeleteFile), path);
        BeforeDelete?.Invoke(path);
        Inner.DeleteFile(path);
    }

    public void DeleteDirectory(string path)
    {
        Check(nameof(DeleteDirectory), path);
        Inner.DeleteDirectory(path);
    }

    public IReadOnlyList<StoreFileEntry> EnumerateFiles(string directory)
    {
        Check(nameof(EnumerateFiles), directory);
        return Inner.EnumerateFiles(directory);
    }

    public bool IsBundleHeldByLiveProcess(string bundleDirectory) => Inner.IsBundleHeldByLiveProcess(bundleDirectory);

    public string GetFullPath(string path) => Inner.GetFullPath(path);

    public IDisposable TryLockTransient(string path) => Inner.TryLockTransient(path);

    public bool TryAcquireBundleLock(string bundleDirectory, out DatabaseLockFile.LockInfo acquired) =>
        Inner.TryAcquireBundleLock(bundleDirectory, out acquired);

    public void ReleaseBundleLock(string bundleDirectory, in DatabaseLockFile.LockInfo acquired) => Inner.ReleaseBundleLock(bundleDirectory, acquired);
}
