using NUnit.Framework;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Typhon.Engine.Tests.Backup;

/// <summary>
/// The store opened for writing (plan 1413 §2.5): its exclusive lock, its identity, abandoned temps, and a catalog that is only ever a cache.
/// </summary>
[TestFixture]
internal sealed class BackupStoreTests
{
    private const string Root = "/s";

    private static InMemorySparseFileSystem NewFs()
    {
        var fs = new InMemorySparseFileSystem();
        fs.CreateDirectory("/");
        return fs;
    }

    [Test]
    [CancelAfter(15_000)]
    public void AFreshStore_IsCreatedBoundToItsDatabase()
    {
        var image = BackupTestImages.Clean;
        var fs = NewFs();
        Guid storeId;
        using (var store = BackupStore.OpenForWrite(Root, image.DatabaseId, createIfMissing: true, fs))
        {
            storeId = store.StoreId;
            Assert.That(fs.FileExists(Root + "/store.json"), Is.True);
            Assert.That(fs.DirectoryExists(Root + "/packs"), Is.True);
            Assert.That(fs.FileExists(Root + "/store.lock.json"), Is.True, "the holder is named while the lock is held");
        }

        Assert.That(fs.FileExists(Root + "/store.lock.json"), Is.False);
        using var again = BackupStore.OpenForWrite(Root, image.DatabaseId, createIfMissing: false, fs);
        Assert.That(again.StoreId, Is.EqualTo(storeId), "store.json is written once");

        Assert.Throws<DirectoryNotFoundException>(() => BackupStore.OpenForWrite("/nowhere", image.DatabaseId, createIfMissing: false, fs));
    }

    [Test]
    [CancelAfter(15_000)]
    public void ASecondWriter_IsRefused_AndToldWhoHoldsTheStore()
    {
        var image = BackupTestImages.Clean;
        var fs = NewFs();
        using (BackupStore.OpenForWrite(Root, image.DatabaseId, true, fs))
        {
            var ex = Assert.Throws<BackupStoreLockedException>(() => BackupStore.OpenForWrite(Root, image.DatabaseId, true, fs));
            Assert.That(ex.HolderPid, Is.EqualTo(Environment.ProcessId));
            Assert.That(ex.IsTransient, Is.True);
        }

        Assert.DoesNotThrow(() => BackupStore.OpenForWrite(Root, image.DatabaseId, true, fs).Dispose());
    }

    [Test]
    [CancelAfter(15_000)]
    public void AStoreOfAnotherDatabase_IsRefused()
    {
        var image = BackupTestImages.Clean;
        var fs = NewFs();
        BackupStore.OpenForWrite(Root, image.DatabaseId, true, fs).Dispose();

        var other = Guid.NewGuid();
        var ex = Assert.Throws<BackupStoreMismatchException>(() => BackupStore.OpenForWrite(Root, other, true, fs));
        Assert.That((ex.StoreDatabaseId, ex.DatabaseId), Is.EqualTo((image.DatabaseId, other)));
    }

    [Test]
    [CancelAfter(15_000)]
    public void AbandonedTemps_AreDeletedByTheNextWriter_AndNeverJoinAChain()
    {
        var image = BackupTestImages.Clean;
        var fs = NewFs();
        using (var store = BackupStore.OpenForWrite(Root, image.DatabaseId, true, fs))
        {
            StoreTestKit.WritePoint(store, image);
        }

        foreach (var temp in new[] { "/packs/00000002-I.tpack.tmp", "/packs/00000002-I.tpack.tmp.idx1", "/catalog.json.tmp", "/store.hwm.tmp" })
        {
            using var f = fs.OpenCreateNew(Root + temp);
            f.Write([1, 2, 3], 0);
        }

        var reader = StoreView.Open(Root, fs);
        Assert.That(reader.Temps.Select(t => t.Name), Is.EquivalentTo(new[] { "00000002-I.tpack.tmp", "00000002-I.tpack.tmp.idx1" }));
        Assert.That(reader.Catalog.Packs.Select(p => p.BackupId), Is.EqualTo(new uint[] { 1 }), "a reader never counts a temp as a point");

        using (BackupStore.OpenForWrite(Root, image.DatabaseId, true, fs))
        {
            Assert.That(fs.AllFiles.Where(f => f.EndsWith(".tmp") || f.Contains(".tmp.")), Is.Empty);
        }
    }

    [Test]
    [CancelAfter(15_000)]
    public void TheCatalog_IsUsedOnlyWhileItsFingerprintMatchesThePacks()
    {
        var image = BackupTestImages.Clean;
        var fs = NewFs();
        string second;
        using (var store = BackupStore.OpenForWrite(Root, image.DatabaseId, true, fs))
        {
            var full = StoreTestKit.WritePoint(store, image);
            second = StoreTestKit.WritePoint(store, image, full.ChainId, pages: [3, 4]).PackPath;
        }

        var fresh = StoreView.Open(Root, fs);
        Assert.That(fresh.CatalogFromFile, Is.True);
        Assert.That(fresh.Catalog.Packs, Has.Count.EqualTo(2));

        // An rsync of a damaged copy changes a pack's size, not necessarily its name or its time: the catalog must not be trusted.
        fs.Resize(second, fs.ReadAll(second).Length - 1);
        var stale = StoreView.Open(Root, fs);
        Assert.That(stale.CatalogFromFile, Is.False);
        Assert.That(stale.Catalog.Packs.Select(p => p.BackupId), Is.EqualTo(new uint[] { 1 }));
        Assert.That(stale.Catalog.Unreadable.Single().File, Is.EqualTo("00000002-I.tpack"));
    }

    [Test]
    [CancelAfter(15_000)]
    public void TheCatalog_SurvivesATimestampOnlyChange_OnDisk()
    {
        // robocopy /MIR and rsync -a preserve or rewrite file times at will; only names and sizes say whether the packs changed.
        var image = BackupTestImages.Clean;
        var root = Path.Combine(TestStorage.ForFixture(nameof(BackupStoreTests)), Guid.NewGuid().ToString("N"));
        try
        {
            using (var store = BackupStore.OpenForWrite(root, image.DatabaseId, true))
            {
                StoreTestKit.WritePoint(store, image);
            }

            var pack = Directory.GetFiles(Path.Combine(root, "packs")).Single();
            File.SetLastWriteTimeUtc(pack, DateTime.UtcNow.AddDays(1));
            File.SetLastWriteTimeUtc(Path.Combine(root, "catalog.json"), DateTime.UtcNow.AddDays(-30));

            Assert.That(StoreView.Open(root).CatalogFromFile, Is.True);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    [CancelAfter(15_000)]
    public void ALostCatalog_IsRebuiltByteForByte()
    {
        var image = BackupTestImages.Clean;
        var fs = NewFs();
        using (var store = BackupStore.OpenForWrite(Root, image.DatabaseId, true, fs))
        {
            var full = StoreTestKit.WritePoint(store, image);
            StoreTestKit.WritePoint(store, image, full.ChainId, pages: [2]);
            StoreTestKit.WritePoint(store, image);
        }

        var before = fs.ReadAll(Root + "/catalog.json");
        fs.DeleteFile(Root + "/catalog.json");
        Assert.That(StoreView.Open(Root, fs).CatalogFromFile, Is.False, "a reader rebuilds in memory");
        Assert.That(fs.FileExists(Root + "/catalog.json"), Is.False, "and writes nothing");

        BackupStore.OpenForWrite(Root, image.DatabaseId, true, fs).Dispose();
        Assert.That(fs.ReadAll(Root + "/catalog.json"), Is.EqualTo(before));
    }

    [Test]
    [CancelAfter(15_000)]
    public void AnIncremental_IsRefused_WhenItsParentIsNoLongerTheHead()
    {
        var image = BackupTestImages.Clean;
        var fs = NewFs();
        using var store = BackupStore.OpenForWrite(Root, image.DatabaseId, true, fs);
        var full = StoreTestKit.WritePoint(store, image);
        var stalePlan = StoreTestKit.Plan(store, image, full.ChainId);
        StoreTestKit.WritePoint(store, image, full.ChainId, pages: [2]);

        var ex = Assert.Throws<InvalidOperationException>(() => store.BeginPoint(stalePlan));
        Assert.That(ex.Message, Does.Contain("admission saw another store state"));
        Assert.That(store.TryGetChainHead(full.ChainId, out var head) && head.BackupId == 2, Is.True);
    }

    [Test]
    [CancelAfter(15_000)]
    public void AFullPoint_StartsANewChain_AnIncrementalExtendsItsHead()
    {
        var image = BackupTestImages.Clean;
        var fs = NewFs();
        using var store = BackupStore.OpenForWrite(Root, image.DatabaseId, true, fs);
        var a = StoreTestKit.WritePoint(store, image);
        var b = StoreTestKit.WritePoint(store, image, a.ChainId, pages: [2]);
        var c = StoreTestKit.WritePoint(store, image);

        Assert.That((a.BackupId, b.BackupId, c.BackupId), Is.EqualTo((1u, 2u, 3u)));
        Assert.That(b.ChainId, Is.EqualTo(a.ChainId));
        Assert.That(c.ChainId, Is.Not.EqualTo(a.ChainId));
        Assert.That(store.TryGetChainHead(a.ChainId, out var head), Is.True);
        Assert.That((head.BackupId, head.Kind, head.BaseId), Is.EqualTo((2u, BackupKind.Incremental, 1u)));
        Assert.That(store.TryGetChainHead(Guid.NewGuid(), out _), Is.False);
    }

    /// <summary>AC-S3: another PROCESS holding the store lock keeps this one out, through the OS lock (share mode on Windows, flock on Linux).</summary>
    [Test]
    [CancelAfter(30_000)]
    public void AnotherProcessHoldingTheLock_KeepsAWriterOut()
    {
        var image = BackupTestImages.Clean;
        var root = Path.Combine(TestStorage.ForFixture(nameof(BackupStoreTests)), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var lockPath = Path.Combine(root, "store.lock");
        ProcessStartInfo psi;
        if (OperatingSystem.IsWindows())
        {
            psi = new ProcessStartInfo("powershell", "-NoProfile -NonInteractive -Command \"$f=[IO.File]::Open('" + lockPath
                + "','OpenOrCreate','ReadWrite','None'); [Console]::Out.WriteLine('locked'); [Console]::Out.Flush(); [void][Console]::In.ReadLine(); $f.Close()\"");
        }
        else
        {
            psi = new ProcessStartInfo("flock", $"-x -n \"{lockPath}\" -c \"echo locked; read x\"");
        }

        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.UseShellExecute = false;
        Process child;
        try
        {
            child = Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Assert.Inconclusive($"no '{psi.FileName}' to hold the lock from another process: {ex.Message}");
            return;
        }

        using (child)
        {
            try
            {
                Assert.That(child.StandardOutput.ReadLine(), Is.EqualTo("locked"), "the child must hold the lock before the probe");
                Assert.Throws<BackupStoreLockedException>(() => BackupStore.OpenForWrite(root, image.DatabaseId, true));
            }
            finally
            {
                child.StandardInput.WriteLine();
                child.WaitForExit(10_000);
            }
        }

        Assert.DoesNotThrow(() => BackupStore.OpenForWrite(root, image.DatabaseId, true).Dispose());
        Directory.Delete(root, true);
    }
}
