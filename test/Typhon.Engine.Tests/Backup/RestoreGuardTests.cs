using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Typhon.Engine.Tests.Backup;

/// <summary>
/// T8 / AC-R2 / BKP-05: a restore refuses before writing a byte when the target is live, foreign-named, of another format or in the way; it
/// never deletes a database; and a crash anywhere in it leaves the old bundle, the complete new one, or the documented two-directory state.
/// </summary>
[TestFixture]
internal sealed class RestoreGuardTests
{
    private const string Store = "/s";
    private const string Parent = "/r";

    private static (InMemorySparseFileSystem Fs, BackupTestImage Image) StoreWithOnePoint(BackupTestImage image = null)
    {
        image ??= BackupTestImages.Clean;
        var fs = new InMemorySparseFileSystem();
        fs.CreateDirectory("/");
        fs.CreateDirectory(Parent);
        using var store = BackupStore.OpenForWrite(Store, image.DatabaseId, true, fs);
        StoreTestKit.WritePoint(store, image, withTail: !image.Clean);
        return (fs, image);
    }

    private static string Bundle(BackupTestImage image) => Parent + "/" + image.DatabaseName + ".typhon";

    /// <summary>Every file under <paramref name="dir"/> with its bytes, for before/after comparison.</summary>
    private static Dictionary<string, byte[]> Snapshot(InMemorySparseFileSystem fs, string dir) =>
        fs.AllFiles.Where(f => f.StartsWith(dir + "/", StringComparison.Ordinal)).ToDictionary(f => f[dir.Length..], fs.ReadAll);

    private static void PlantOldBundle(InMemorySparseFileSystem fs, string bundle)
    {
        fs.CreateDirectory(bundle + "/wal");
        foreach (var (name, b) in new[] { ("data", (byte)1), ("wal/0000000000000001.wal", (byte)2), ("chunk-summary", (byte)3) })
        {
            using var f = fs.OpenCreateNew(bundle + "/" + name);
            f.Write(Enumerable.Repeat(b, 100).ToArray(), 0);
        }
    }

    [Test]
    [CancelAfter(15_000)]
    public void AnExistingTarget_IsRefused_AndLeftByteIdentical()
    {
        var (fs, image) = StoreWithOnePoint();
        var bundle = Bundle(image);
        PlantOldBundle(fs, bundle);
        var before = Snapshot(fs, bundle);

        var ex = Assert.Throws<RestoreRefusedException>(() => RestoreStager.Run(Store, PointSelector.Latest, Parent, fs: fs));
        Assert.That(ex.Reason, Is.EqualTo(RestoreRefusalReason.TargetExists));
        Assert.That(Snapshot(fs, bundle), Is.EqualTo(before));
        Assert.That(fs.DirectoryExists(bundle + ".restoring"), Is.False, "nothing staged");
    }

    [Test]
    [CancelAfter(15_000)]
    public void Replace_MovesTheOldBundleAside_NeverDeletesIt()
    {
        var (fs, image) = StoreWithOnePoint();
        var bundle = Bundle(image);
        PlantOldBundle(fs, bundle);
        var before = Snapshot(fs, bundle);

        var outcome = RestoreStager.Run(Store, PointSelector.Latest, bundle, new RestoreOptions
        {
            Replace = true,
            UtcNow = () => new DateTime(2026, 10, 19, 13, 14, 15, DateTimeKind.Utc),
        }, fs);

        var displaced = InMemorySparseFileSystem.Normalize(outcome.DisplacedBundle);
        Assert.That(displaced, Is.EqualTo(bundle + ".pre-restore-20261019-131415"));
        Assert.That(Snapshot(fs, displaced), Is.EqualTo(before), "the displaced bundle is the old one, byte for byte");
        Assert.That(fs.ReadAll(bundle + "/data").Length, Is.EqualTo(image.PageCount * PackFormat.PageSize));
        Assert.That(fs.FileExists(bundle + "/chunk-summary"), Is.False, "nothing of the old bundle leaks into the new one");

        // A second replace in the same second must not collide with the first displaced bundle.
        var again = RestoreStager.Run(Store, PointSelector.Latest, bundle, new RestoreOptions
        {
            Replace = true,
            UtcNow = () => new DateTime(2026, 10, 19, 13, 14, 15, DateTimeKind.Utc),
        }, fs);
        Assert.That(InMemorySparseFileSystem.Normalize(again.DisplacedBundle), Is.EqualTo(bundle + ".pre-restore-20261019-131415-2"));
    }

    [TestCase(false)]
    [TestCase(true)]
    [CancelAfter(15_000)]
    public void ATargetHeldByALiveProcess_IsRefused_EvenWithReplace(bool replace)
    {
        var (fs, image) = StoreWithOnePoint();
        var bundle = Bundle(image);
        PlantOldBundle(fs, bundle);
        fs.MarkBundleHeld(bundle, true);
        var before = Snapshot(fs, bundle);

        var ex = Assert.Throws<RestoreRefusedException>(() =>
            RestoreStager.Run(Store, PointSelector.Latest, bundle, new RestoreOptions { Replace = replace }, fs));
        Assert.That(ex.Reason, Is.EqualTo(RestoreRefusalReason.TargetLocked));
        Assert.That(Snapshot(fs, bundle), Is.EqualTo(before));
    }

    [Test]
    [CancelAfter(15_000)]
    public void ADataFileHeldOpen_IsRefusedEvenWhenNoLockFileSaysSo()
    {
        // OfflineBundlePageSource.LockHeld trusts db.lock, which the engine never holds open (#1423): the OS probe is the guard that works.
        var (fs, image) = StoreWithOnePoint();
        var bundle = Bundle(image);
        PlantOldBundle(fs, bundle);
        using var engineHandle = fs.OpenRead(bundle + "/data");

        var ex = Assert.Throws<RestoreRefusedException>(() =>
            RestoreStager.Run(Store, PointSelector.Latest, bundle, new RestoreOptions { Replace = true }, fs));
        Assert.That(ex.Reason, Is.EqualTo(RestoreRefusalReason.TargetLocked));
    }

    [Test]
    [CancelAfter(15_000)]
    public void ABundleNamedOtherwise_IsRefused()
    {
        var (fs, _) = StoreWithOnePoint();

        var ex = Assert.Throws<RestoreRefusedException>(() => RestoreStager.Run(Store, PointSelector.Latest, Parent + "/world-restored.typhon", fs: fs));
        Assert.That(ex.Reason, Is.EqualTo(RestoreRefusalReason.NameMismatch));
        Assert.That(fs.AllDirectories.Where(d => d.StartsWith(Parent + "/", StringComparison.Ordinal)), Is.Empty);
    }

    [Test]
    [CancelAfter(15_000)]
    public void AnImageOfAnotherFormatRevision_IsRefusedUnlessAskedFor()
    {
        var forged = BackupTestImages.WithFormatRevision(BackupTestImages.Clean, 6);
        var (fs, image) = StoreWithOnePoint(forged);

        var ex = Assert.Throws<RestoreRefusedException>(() => RestoreStager.Run(Store, PointSelector.Latest, Parent, fs: fs));
        Assert.That(ex.Reason, Is.EqualTo(RestoreRefusalReason.FormatRevision));
        Assert.That(fs.DirectoryExists(Bundle(image)), Is.False);

        RestoreStager.Run(Store, PointSelector.Latest, Parent, new RestoreOptions { AllowFormatMismatch = true }, fs);
        Assert.That(fs.FileExists(Bundle(image) + "/data"), Is.True);
    }

    [Test]
    [CancelAfter(15_000)]
    public void ALeftoverStaging_IsReplacedOnlyWhenItIsARestoresOwn()
    {
        var (fs, image) = StoreWithOnePoint();
        var staging = Bundle(image) + ".restoring";

        fs.CreateDirectory(staging);
        using (var f = fs.OpenCreateNew(staging + "/precious"))
        {
            f.Write([42], 0);
        }

        var ex = Assert.Throws<RestoreRefusedException>(() => RestoreStager.Run(Store, PointSelector.Latest, Parent, fs: fs));
        Assert.That(ex.Reason, Is.EqualTo(RestoreRefusalReason.ForeignStaging));
        Assert.That(fs.ReadAll(staging + "/precious"), Is.EqualTo(new byte[] { 42 }), "a foreign directory is never touched");

        using (var f = fs.OpenCreateNew(staging + "/restore.staging"))
        {
            f.Write("{}"u8, 0);
        }

        RestoreStager.Run(Store, PointSelector.Latest, Parent, fs: fs);
        Assert.That(fs.DirectoryExists(staging), Is.False);
        Assert.That(fs.FileExists(Bundle(image) + "/precious"), Is.False);
    }

    [TestCase(CrashModelKind.Windows)]
    [TestCase(CrashModelKind.Linux)]
    [CancelAfter(60_000)]
    [VerifiesRule("BKP-05")]
    public void ACrashAnywhereInARestore_DeletesNothingOutsideItsStaging_AndLeavesARecognisableState(CrashModelKind kind)
    {
        var image = BackupTestImages.WithShortTail(BackupTestImages.Unclean);
        var crashes = 0;
        byte[] newData = null;
        for (var k = 0; ; k++)
        {
            var fs = new CrashModelFileSystem(kind);
            fs.CreateDirectory("/");
            fs.CreateDirectory(Parent);
            using (var store = BackupStore.OpenForWrite(Store, image.DatabaseId, true, fs))
            {
                StoreTestKit.WritePoint(store, image, withTail: true);
            }

            var bundle = Bundle(image);
            PlantOldBundle(fs, bundle);
            fs.SyncAll();
            var old = Snapshot(fs, bundle);

            fs.CrashBefore(k);
            var crashed = false;
            try
            {
                RestoreStager.Run(Store, PointSelector.Latest, bundle, new RestoreOptions { Replace = true }, fs);
            }
            catch (SimulatedCrashException)
            {
                crashed = true;
            }

            foreach (var op in fs.Log.Where(o => o.StartsWith("delete ", StringComparison.Ordinal) || o.StartsWith("rmdir ", StringComparison.Ordinal)))
            {
                Assert.That(op, Does.Contain(bundle + ".restoring/").Or.EndsWith(bundle + ".restoring"), $"k={k}: '{op}' outside the staging");
            }

            if (!crashed)
            {
                Assert.That(crashes, Is.GreaterThan(10));
                return;
            }

            crashes++;
            fs.Crash();
            var displaced = fs.AllDirectories.SingleOrDefault(d => d.StartsWith(bundle + ".pre-restore-", StringComparison.Ordinal) && !d.EndsWith("/wal"));
            var staging = bundle + ".restoring";
            if (fs.DirectoryExists(bundle))
            {
                var now = Snapshot(fs, bundle);
                if (displaced == null)
                {
                    Assert.That(now, Is.EqualTo(old), $"k={k}: the target is neither the old bundle nor the new one");
                }
                else
                {
                    Assert.That(Snapshot(fs, displaced), Is.EqualTo(old), $"k={k}: the displaced bundle changed");
                    AssertCompleteRestore(fs, bundle, image, ref newData, k);
                }
            }
            else
            {
                // R6: between the two renames. Both halves exist, complete, and are named; nothing was guessed.
                Assert.That(displaced, Is.Not.Null, $"k={k}: target gone with no displaced bundle");
                Assert.That(Snapshot(fs, displaced), Is.EqualTo(old));
                Assert.That(fs.FileExists(staging + "/restore.staging"), Is.False, $"k={k}: an unfinished staging was published");
                AssertCompleteRestore(fs, staging, image, ref newData, k);
            }
        }
    }

    private static void AssertCompleteRestore(InMemorySparseFileSystem fs, string bundle, BackupTestImage image, ref byte[] reference, int k)
    {
        var data = fs.ReadAll(bundle + "/data");
        Assert.That(data.Length, Is.EqualTo(image.PageCount * PackFormat.PageSize), $"k={k}: data length");
        reference ??= image.Pages.SelectMany(p => p).ToArray();
        Assert.That(data.AsSpan().SequenceEqual(reference), Is.True, $"k={k}: data");
        Assert.That(fs.FileExists(bundle + "/restore.json"), Is.True, $"k={k}: restore.json");
        foreach (var s in image.WalSegments)
        {
            Assert.That(fs.Fingerprint($"{bundle}/wal/{s.SegmentId:D16}.wal").Length, Is.EqualTo(s.SegmentSize), $"k={k}: segment {s.SegmentId}");
        }
    }

    /// <summary>The real thing: an engine holding the target in this process. Both the OS probe and the lock file must refuse it.</summary>
    [TestCase(false)]
    [TestCase(true)]
    [CancelAfter(30_000)]
    public void AnOpenEngine_KeepsItsDatabase(bool replace)
    {
        var root = Path.Combine(TestStorage.ForFixture(nameof(RestoreGuardTests)), Guid.NewGuid().ToString("N"));
        var image = BackupTestImages.Build("BkGuardLive", unclean: false, entities: 8, root: root);
        var storePath = Path.Combine(root, "store");
        using (var store = BackupStore.OpenForWrite(storePath, image.DatabaseId, true))
        {
            StoreTestKit.WritePoint(store, image);
        }

        var dbDir = Path.GetDirectoryName(image.BundlePath);
        using (var provider = BackupTestImages.BuildProvider(image.DatabaseName, dbDir, Path.Combine(image.BundlePath, "wal")))
        using (var scope = provider.CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompA>();
            dbe.InitializeArchetypes();

            var ex = Assert.Throws<RestoreRefusedException>(() =>
                RestoreStager.Run(storePath, PointSelector.Latest, dbDir, new RestoreOptions { Replace = replace }));
            Assert.That(ex.Reason, Is.EqualTo(RestoreRefusalReason.TargetLocked));

            // The engine did not notice.
            using var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate);
            using var tx = uow.CreateTransaction();
            tx.Spawn<CompAArch>(CompAArch.A.Set(new CompA(1, 2, 3)));
            Assert.That(tx.Commit(), Is.True);
        }

        Assert.That(Directory.GetDirectories(dbDir).Where(d => d.Contains(".pre-restore-") || d.EndsWith(".restoring")), Is.Empty);
        Directory.Delete(root, true);
    }
}
