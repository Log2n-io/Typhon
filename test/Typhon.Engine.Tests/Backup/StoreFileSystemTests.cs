using NUnit.Framework;
using System;
using System.IO;

namespace Typhon.Engine.Tests.Backup;

/// <summary>
/// The durable file-system primitives a store publishes through (plan 1413 §2.17), on the real disk and in the two test models the crash tests
/// rely on — a model that is wrong would make every crash test prove nothing.
/// </summary>
[TestFixture]
internal sealed class StoreFileSystemTests
{
    private string _root;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(TestStorage.ForFixture(nameof(StoreFileSystemTests)), TestContext.CurrentContext.Test.Name);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }

        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    private static readonly IStoreFileSystem Disk = StoreFileSystem.Instance;

    [Test]
    [CancelAfter(5_000)]
    public void MoveDurable_RefusesAnExistingFile_UnlessReplacing()
    {
        var a = Path.Combine(_root, "a");
        var b = Path.Combine(_root, "b");
        File.WriteAllText(a, "new");
        File.WriteAllText(b, "old");

        Assert.Throws<IOException>(() => StoreFileSystem.MoveDurable(Disk, a, b, replace: false));
        Assert.That(File.ReadAllText(b), Is.EqualTo("old"), "a refused move must leave the destination untouched");
        Assert.That(File.Exists(a), Is.True);

        StoreFileSystem.MoveDurable(Disk, a, b, replace: true);
        Assert.That(File.ReadAllText(b), Is.EqualTo("new"));
        Assert.That(File.Exists(a), Is.False);
    }

    [Test]
    [CancelAfter(5_000)]
    public void MoveDurable_MovesADirectory_AndRefusesAnExistingOne()
    {
        var src = Path.Combine(_root, "staging");
        var dst = Path.Combine(_root, "target");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "data"), "x");
        Directory.CreateDirectory(dst);

        Assert.Throws<IOException>(() => StoreFileSystem.MoveDurable(Disk, src, dst, replace: false));
        Directory.Delete(dst);

        StoreFileSystem.MoveDurable(Disk, src, dst, replace: false);
        Assert.That(File.ReadAllText(Path.Combine(dst, "data")), Is.EqualTo("x"));
        Assert.That(Directory.Exists(src), Is.False);
    }

    [Test]
    [CancelAfter(5_000)]
    public void FlushDirectory_RunsOnTheRealDisk()
    {
        // A no-op on Windows; on Linux this is the open + fsync P/Invoke, which must not throw on a real directory.
        Assert.DoesNotThrow(() => Disk.FlushDirectory(_root));
    }

    [Test]
    [CancelAfter(5_000)]
    public void ProbeExclusive_SeesAnotherHandle()
    {
        var path = Path.Combine(_root, "data");
        File.WriteAllText(path, "x");
        Assert.That(Disk.ProbeExclusive(path), Is.True);
        Assert.That(Disk.ProbeExclusive(Path.Combine(_root, "absent")), Is.True, "a missing file is held by nobody");

        // The engine's own sharing: write access, others may read.
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            Assert.That(Disk.ProbeExclusive(path), Is.False);
        }

        Assert.That(Disk.ProbeExclusive(path), Is.True);
    }

    [Test]
    [CancelAfter(5_000)]
    public void TryLockExclusive_IsHeldUntilDisposed()
    {
        var path = Path.Combine(_root, "store.lock");
        using (var first = Disk.TryLockExclusive(path))
        {
            Assert.That(first, Is.Not.Null);
            Assert.That(Disk.TryLockExclusive(path), Is.Null, "a second exclusive open in the same process must fail too (share mode / flock per open)");
        }

        using var again = Disk.TryLockExclusive(path);
        Assert.That(again, Is.Not.Null);
    }

    [Test]
    [CancelAfter(5_000)]
    public void OpenRead_LetsADeleteThrough_AndKeepsReading()
    {
        var path = Path.Combine(_root, "00000001-F.tpack");
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        using var reader = Disk.OpenRead(path);

        File.Delete(path);

        Span<byte> buffer = stackalloc byte[4];
        Assert.That(reader.ReadExactly(buffer, 0), Is.True);
        Assert.That(buffer.ToArray(), Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
    }

    [Test]
    [CancelAfter(5_000)]
    public void InMemorySparse_AddressesPastFourGiB()
    {
        var fs = new InMemorySparseFileSystem();
        fs.CreateDirectory("/m");
        const long far = (5L << 30) + 12345;
        using (var file = fs.OpenCreateNew("/m/big"))
        {
            file.Write([9, 8, 7], far);
            Assert.That(file.Length, Is.EqualTo(far + 3));
        }

        using var read = fs.OpenRead("/m/big");
        Span<byte> buffer = stackalloc byte[5];
        Assert.That(read.ReadExactly(buffer, far - 2), Is.True);
        Assert.That(buffer.ToArray(), Is.EqualTo(new byte[] { 0, 0, 9, 8, 7 }));
        Assert.That(read.Read(buffer, far + 3), Is.Zero);
    }

    [TestCase(CrashModelKind.Windows)]
    [TestCase(CrashModelKind.Linux)]
    [CancelAfter(5_000)]
    public void CrashModel_KeepsOnlyFlushedContent(CrashModelKind kind)
    {
        var fs = NewCrashModel(kind);
        using (var file = fs.OpenCreateNew("/m/f.tmp"))
        {
            file.Write([1, 2], 0);
            file.Flush();
            file.Write([3, 4], 2);
        }

        StoreFileSystem.MoveDurable(fs, "/m/f.tmp", "/m/f", replace: false);
        fs.Crash();

        Assert.That(fs.ReadAll("/m/f"), Is.EqualTo(new byte[] { 1, 2 }), "bytes written after the last flush must not survive");
    }

    [Test]
    [CancelAfter(5_000)]
    public void CrashModel_Linux_LosesARenameWhoseDirectoryWasNotFlushed()
    {
        var fs = NewCrashModel(CrashModelKind.Linux);
        using (var file = fs.OpenCreateNew("/m/f.tmp"))
        {
            file.Write([1], 0);
            file.Flush();
        }

        fs.FlushDirectory("/m");
        fs.Rename("/m/f.tmp", "/m/f", replace: false);
        fs.Crash();

        Assert.That(fs.FileExists("/m/f"), Is.False);
        Assert.That(fs.FileExists("/m/f.tmp"), Is.True);
    }

    [Test]
    [CancelAfter(5_000)]
    public void CrashModel_Windows_AWriteThroughRenameIsDurable()
    {
        var fs = NewCrashModel(CrashModelKind.Windows);
        using (var file = fs.OpenCreateNew("/m/f.tmp"))
        {
            file.Write([1], 0);
            file.Flush();
        }

        fs.Rename("/m/f.tmp", "/m/f", replace: false);
        fs.Crash();

        Assert.That(fs.FileExists("/m/f"), Is.True);
        Assert.That(fs.FileExists("/m/f.tmp"), Is.False);
    }

    [Test]
    [CancelAfter(5_000)]
    public void CrashModel_CrashBefore_StopsAtTheArmedOperation()
    {
        var fs = NewCrashModel(CrashModelKind.Windows);
        fs.CrashBefore(1);
        using var file = fs.OpenCreateNew("/m/f");
        Assert.Throws<SimulatedCrashException>(() => file.Write([1], 0));
        Assert.That(fs.Operations, Is.EqualTo(1));
    }

    private static CrashModelFileSystem NewCrashModel(CrashModelKind kind)
    {
        var fs = new CrashModelFileSystem(kind);
        fs.CreateDirectory("/m");
        fs.SyncAll();
        return fs;
    }
}
