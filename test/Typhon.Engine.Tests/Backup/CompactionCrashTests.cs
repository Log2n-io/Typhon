using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Typhon.Engine.Tests.Backup;

/// <summary>
/// BKP-13 / BKP-08 for compaction: a power cut after ANY file-system operation of a compaction leaves either no <c>-C</c> (at most a temp, which the
/// next lock holder deletes) or a complete, verified one — and every point, the compacted one included, still materialises to its image. Run under
/// both directory-durability models.
/// </summary>
[TestFixture]
internal sealed class CompactionCrashTests
{
    private const string Root = SyntheticChain.Root;

    [TestCase(CrashModelKind.Windows)]
    [TestCase(CrashModelKind.Linux)]
    [CancelAfter(60_000)]
    [VerifiesRule("BKP-13")]
    public void ACrashAfterAnyOperation_LeavesNoCompactedPackOrAVerifiedOne_AndEveryPointIntact(CrashModelKind kind)
    {
        var crashes = 0;
        for (var k = 0; ; k++)
        {
            var fs = Seed(kind, out var images, out var packs);
            fs.CrashBefore(k);
            try
            {
                var done = CompactionEquivalenceTests.Compact(fs, 4);
                Assert.That(done.AlreadyBase, Is.False);
                Assert.That(crashes, Is.GreaterThan(10), "the sweep must have crossed the whole compaction");
                CompactionEquivalenceTests.AssertNoUnverifiedCompactedPackIsVisible(fs);
                return;
            }
            catch (SimulatedCrashException)
            {
                crashes++;
            }

            var log = string.Join(" | ", fs.Log.TakeLast(3));
            fs.Crash();
            using (BackupStore.OpenForMaintenance(Root, "check", fs))
            {
                // The next lock holder deletes whatever temp the crash left.
            }

            var listed = fs.EnumerateFiles(Root + "/packs").Select(f => f.Name).ToList();
            Assert.That(listed.Where(n => n.Contains(".tmp")), Is.Empty, $"k={k}: a temp survived the next writer after [{log}]");
            Assert.That(listed.Except(packs.Keys), Is.SubsetOf(new[] { "00000004-C.tpack" }), $"k={k}: an unexpected file after [{log}]");
            CompactionEquivalenceTests.AssertNoUnverifiedCompactedPackIsVisible(fs);
            foreach (var (name, fingerprint) in packs)
            {
                Assert.That(fs.Fingerprint(Root + "/packs/" + name), Is.EqualTo(fingerprint), $"k={k}: {name} changed after [{log}]");
            }

            foreach (var (id, image) in images)
            {
                Assert.That(CompactionEquivalenceTests.Materialize(fs, id).AsSpan().SequenceEqual(image), Is.True,
                    $"k={k}: point #{id} no longer materialises after [{log}]");
            }
        }
    }

    /// <summary>A file already sitting at the compacted pack's name is never replaced: the publish refuses, the temp goes, nothing changes.</summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("BKP-13")]
    public void APublish_NeverReplacesAnExistingCompactedPack()
    {
        var chain = SyntheticChain.Build(3, 3);
        var inner = chain.Fs;
        byte[] stale = [0xDE, 0xAD];
        var fs = new HookedFileSystem(inner)
        {
            BeforeOpenRead = path =>
            {
                // While the temp is verified, before its rename: another file appears under the final name (it cannot happen with one lock holder;
                // the rename must still refuse).
                if (path.EndsWith("00000003-C.tpack.tmp", StringComparison.Ordinal) && !inner.FileExists(Root + "/packs/00000003-C.tpack"))
                {
                    using var f = inner.OpenCreateNew(Root + "/packs/00000003-C.tpack");
                    f.Write(stale, 0);
                }
            },
        };

        Assert.Throws<IOException>(() => CompactionEquivalenceTests.Compact(fs, 3));
        Assert.That(inner.ReadAll(Root + "/packs/00000003-C.tpack"), Is.EqualTo(stale), "the existing file must be untouched");
        Assert.That(inner.FileExists(Root + "/packs/00000003-C.tpack.tmp"), Is.False);
    }

    /// <summary>#1 full, #2–#4 incremental, every point carrying a tail; made durable. Returns each point's image and every pack's fingerprint.</summary>
    private static CrashModelFileSystem Seed(CrashModelKind kind, out Dictionary<uint, byte[]> images, out Dictionary<string, (long, uint)> packs)
    {
        var image = BackupTestImages.WithShortTail(BackupTestImages.Unclean);
        var fs = new CrashModelFileSystem(kind);
        fs.CreateDirectory("/");
        using (var store = BackupStore.OpenForWrite(Root, image.DatabaseId, true, fs))
        {
            var full = StoreTestKit.WritePoint(store, image, withTail: true);
            for (var i = 2; i <= 4; i++)
            {
                StoreTestKit.WritePoint(store, image, full.ChainId, pages: [2 + i, 3 + (2 * i)], withTail: true, @override: StoreTestKit.Salted(image, i));
            }
        }

        images = Enumerable.Range(1, 4).ToDictionary(i => (uint)i, i => CompactionEquivalenceTests.Materialize(fs, (uint)i));
        packs = fs.EnumerateFiles(Root + "/packs").ToDictionary(f => f.Name, f => fs.Fingerprint(Root + "/packs/" + f.Name));
        fs.SyncAll();
        return fs;
    }
}
