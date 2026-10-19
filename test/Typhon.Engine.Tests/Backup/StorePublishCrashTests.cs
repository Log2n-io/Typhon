using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Typhon.Engine.Tests.Backup;

/// <summary>
/// AC-S1 / BKP-08: a power cut after ANY file-system operation of a backup leaves the store openable, its old points intact, and the new point
/// either complete or absent — never visible and partial. A point whose <c>Complete</c> returned survives. Run under both directory-durability
/// models on every OS (plan 1413 §6.4).
/// </summary>
[TestFixture]
internal sealed class StorePublishCrashTests
{
    private const string Root = "/s";

    [TestCase(CrashModelKind.Windows)]
    [TestCase(CrashModelKind.Linux)]
    [CancelAfter(60_000)]
    [VerifiesRule("BKP-08")]
    public void ACrashAfterAnyOperation_LeavesOldPointsIntact_AndTheNewOneCompleteOrAbsent(CrashModelKind kind)
    {
        var image = BackupTestImages.WithShortTail(BackupTestImages.Unclean);
        var crashes = 0;
        for (var k = 0; ; k++)
        {
            var fs = Seed(kind, image, out var chain, out var old);
            fs.CrashBefore(k);
            BackupPointInfo completed = default;
            var crashed = false;
            try
            {
                // The crash may land anywhere, including in the session's Dispose after Complete returned.
                using var store = BackupStore.OpenForWrite(Root, image.DatabaseId, true, fs);
                completed = StoreTestKit.WritePoint(store, image, chain, pages: [2, 3, 5], withTail: true, @override: StoreTestKit.Salted(image, 3));
            }
            catch (SimulatedCrashException)
            {
                crashed = true;
            }

            if (!crashed)
            {
                Assert.That(crashes, Is.GreaterThan(10), "the sweep must have crossed the whole backup");
                Assert.That(completed.BackupId, Is.EqualTo(3u));
                return;
            }

            crashes++;
            var log = string.Join(" | ", fs.Log.TakeLast(4));
            fs.Crash();
            var id3Survived = fs.AllFiles.Any(f => f.Contains("/00000003-"));

            // The store opens: a writer cleans up after the crash.
            using (var reopened = BackupStore.OpenForWrite(Root, image.DatabaseId, true, fs))
            {
                var visible = reopened.View.Catalog.Packs.Select(p => p.BackupId).ToHashSet();
                Assert.That(reopened.View.Catalog.Unreadable, Is.Empty, $"k={k}: a visible pack is unreadable after [{log}]");
                Assert.That(visible.IsSupersetOf(old.Keys), Is.True, $"k={k}: an old point vanished after [{log}]");
                Assert.That(visible.Except(old.Keys).All(id => id == 3), Is.True, $"k={k}: an unexpected point appeared");
                if (completed.BackupId != 0)
                {
                    Assert.That(visible, Does.Contain(completed.BackupId), $"k={k}: a point whose Complete returned was lost after [{log}]");
                }

                foreach (var p in reopened.View.Catalog.Packs)
                {
                    var path = Root + "/packs/" + p.File;
                    Assert.That(PackChecks.VerifyFull(fs, path), Is.Null, $"k={k}: visible pack {p.File} does not verify after [{log}]");
                    if (old.TryGetValue(p.BackupId, out var hash))
                    {
                        Assert.That(PackChecks.PagesHash(fs, path), Is.EqualTo(hash), $"k={k}: old point #{p.BackupId} changed");
                    }
                }

                // An id is never handed out twice: past every visible pack, and past #3 whenever anything of #3 reached the disk.
                var next = reopened.AllocateBackupId();
                Assert.That(next, Is.GreaterThan(visible.Max()), $"k={k}: next id {next}");
                if (id3Survived)
                {
                    Assert.That(next, Is.GreaterThanOrEqualTo(4u), $"k={k}: #3 left a file but its id is handed out again");
                }
            }
        }
    }

    [TestCase(CrashModelKind.Windows)]
    [TestCase(CrashModelKind.Linux)]
    [CancelAfter(15_000)]
    [VerifiesRule("BKP-08")]
    public void APublish_NeverReplacesAnExistingPack(CrashModelKind kind)
    {
        var image = BackupTestImages.Clean;
        var fs = Seed(kind, image, out var chain, out _);
        using var store = BackupStore.OpenForWrite(Root, image.DatabaseId, true, fs);
        byte[] stale = [0xDE, 0xAD];
        var ex = Assert.Catch(() => StoreTestKit.WritePoint(store, image, chain, pages: [2], beforeComplete: sink =>
        {
            // A file already sits at the name this point will publish under (it cannot happen with unique ids; the rename must still refuse).
            using var f = fs.OpenCreateNew(Root + "/packs/" + PackFormat.FileName(sink.BackupId, BackupKind.Incremental));
            f.Write(stale, 0);
        }));

        Assert.That(ex, Is.Not.InstanceOf<SimulatedCrashException>());
        Assert.That(fs.ReadAll(Root + "/packs/00000003-I.tpack"), Is.EqualTo(stale), "the existing file must be untouched");
    }

    /// <summary>A store with points #1 (full) and #2 (incremental), made fully durable; returns their content hashes.</summary>
    private static CrashModelFileSystem Seed(CrashModelKind kind, BackupTestImage image, out Guid chain, out Dictionary<uint, uint> hashes)
    {
        var fs = new CrashModelFileSystem(kind);
        fs.CreateDirectory("/");
        using (var store = BackupStore.OpenForWrite(Root, image.DatabaseId, true, fs))
        {
            var full = StoreTestKit.WritePoint(store, image, withTail: !image.Clean);
            chain = full.ChainId;
            StoreTestKit.WritePoint(store, image, chain, pages: [2, 4], withTail: !image.Clean, @override: StoreTestKit.Salted(image, 2));
        }

        fs.SyncAll();
        hashes = new Dictionary<uint, uint>
        {
            [1] = PackChecks.PagesHash(fs, Root + "/packs/00000001-F.tpack"),
            [2] = PackChecks.PagesHash(fs, Root + "/packs/00000002-I.tpack"),
        };
        return fs;
    }
}
