using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Typhon.Engine.Tests.Backup;

/// <summary>
/// BKP-14 on a real store (DL-4): two chains whose ids alternate in one store, as copied bundles or offline and online captures make them. 05 §3's
/// "delete every pack below the cutoff" would delete a kept point's base; retention instead compacts each chain's oldest kept point and deletes only
/// what no kept point resolves through.
/// </summary>
[TestFixture]
internal sealed class InterleavedChainsRetentionTests
{
    private const string Root = SyntheticChain.Root;
    private const string Marker = "BKP-14: kept point";

    private static readonly BackupRetentionPolicy KeepFour = new()
    {
        MinKeep = 1, MaxCount = 4, MaxAge = TimeSpan.Zero, MaxTotalBytes = 0, CompactThreshold = 0, KeepLastDrilled = false,
    };

    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("BKP-14")]
    public void EachChainsOldestKeptPoint_IsCompacted_AndOnlyWhatNoKeptPointNeedsIsDeleted()
    {
        var fs = TwoChains(out var images);

        var result = Apply(fs, KeepFour);

        Assert.That(result.Plan.Kept, Is.EqualTo(new uint[] { 5, 6, 7, 8 }));
        Assert.That(result.Compactions.Select(c => c.Point.BackupId), Is.EqualTo(new uint[] { 5, 6 }), "both chains reach below the cutoff");
        Assert.That(result.Deleted, Is.EqualTo(new[]
        {
            "00000006-I.tpack", "00000005-I.tpack", "00000004-I.tpack", "00000003-I.tpack", "00000002-F.tpack", "00000001-F.tpack",
        }));
        Assert.That(fs.EnumerateFiles(Root + "/packs").Select(f => f.Name), Is.EqualTo(new[]
        {
            "00000005-C.tpack", "00000006-C.tpack", "00000007-I.tpack", "00000008-I.tpack",
        }));
        AssertKeptPointsIntact(fs, images, result.Plan.Kept);
        Assert.That(Apply(fs, KeepFour).Deleted, Is.Empty, "a second run has nothing to do");
        Assert.That(BackupVerifier.VerifyStore(Root, VerifyLevel.Full, null, fs).Verdict, Is.EqualTo(VerifyVerdict.Ok));
    }

    /// <summary>The check can fail: 05 §3's literal rule — every pack below the cutoff, no compaction — leaves chain B's kept points without a base.</summary>
    [Test]
    [CancelAfter(15_000)]
    [RuleMutant("BKP-14")]
    public void Mutant_DeletingEveryPackBelowTheCutoff_IsReported()
    {
        var fs = TwoChains(out var images);
        var plan = BackupOperationsPlan(fs, KeepFour);
        foreach (var f in fs.EnumerateFiles(Root + "/packs").Where(f => uint.Parse(f.Name[..8]) < plan.CutoffId))
        {
            fs.DeleteFile(Root + "/packs/" + f.Name);
        }

        RuleMutants.AssertDetects("BKP-14", Marker, () => AssertKeptPointsIntact(fs, images, plan.Kept));
    }

    /// <summary>A chain with a point at or after the cutoff that does not resolve loses nothing; the other chain is pruned as usual.</summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("BKP-14")]
    public void AChainWithABrokenPointAfterTheCutoff_LosesNothing()
    {
        var fs = TwoChains(out var images);
        fs.DeleteFile(Root + "/packs/00000006-I.tpack");   // #8 of chain B no longer resolves
        var bPacks = new[] { "00000002-F.tpack", "00000004-I.tpack", "00000008-I.tpack" }.ToDictionary(n => n, n => fs.Fingerprint(Root + "/packs/" + n));

        var result = Apply(fs, new BackupRetentionPolicy
        {
            MinKeep = 1, MaxCount = 3, MaxAge = TimeSpan.Zero, MaxTotalBytes = 0, CompactThreshold = 0, KeepLastDrilled = false,
        });

        Assert.That(result.Plan.Kept, Is.EqualTo(new uint[] { 4, 5, 7 }));
        Assert.That(result.Plan.Blocked, Is.EqualTo(BackupRetentionBlock.DamagedChain));
        Assert.That(result.Deleted, Is.EqualTo(new[] { "00000005-I.tpack", "00000003-I.tpack", "00000001-F.tpack" }));
        foreach (var (name, fingerprint) in bPacks)
        {
            Assert.That(fs.Fingerprint(Root + "/packs/" + name), Is.EqualTo(fingerprint), $"chain B's {name} was touched");
        }

        AssertKeptPointsIntact(fs, images, result.Plan.Kept);
    }

    /// <summary>A compaction that fails (here its temp is damaged before its verify) costs its chain this run's deletes, nothing else.</summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("BKP-14")]
    public void AFailedCompaction_KeepsItsChainsPacks_AndTheOtherChainIsPruned()
    {
        var inner = TwoChains(out var images);
        var fs = new HookedFileSystem(inner)
        {
            BeforeOpenRead = path =>
            {
                if (path.EndsWith("00000006-C.tpack.tmp", StringComparison.Ordinal))
                {
                    var at = PackFormat.HeaderSize + 50;
                    inner.Poke(path, at, [(byte)(inner.ReadAll(path)[at] ^ 0x20)]);
                }
            },
        };

        var result = Apply(fs, KeepFour);

        Assert.That(result.Plan.Compactions, Is.EqualTo(new uint[] { 5, 6 }), "both were attempted");
        Assert.That(result.Compactions.Select(c => c.Point.BackupId), Is.EqualTo(new uint[] { 5 }), "#6's failed and was not published");
        Assert.That(result.Deleted, Is.EqualTo(new[] { "00000005-I.tpack", "00000003-I.tpack", "00000001-F.tpack" }), "chain B keeps its packs");
        Assert.That(result.FailedCompactions.Select(f => (f.BackupId, f.Kind)), Is.EqualTo(new[] { (6u, BackupFailureKind.Failed) }),
            "reported: its own pack failed its verify, the store is as it was");
        AssertKeptPointsIntact(inner, images, result.Plan.Kept);
    }

    /// <summary>
    /// A compaction is the one routine full read of a kept chain: a damaged page in one of its source packs is reported as store damage (a kept point that
    /// may not restore), not swallowed; the chain keeps its packs, the other chain is pruned.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("BKP-14")]
    public void ADamagedSourcePack_IsReportedAsStoreDamage_AndItsChainKeepsItsPacks()
    {
        var fs = TwoChains(out _);
        DamageAPageOnlyIn(fs, "00000001-F.tpack", "00000003-I.tpack", "00000005-I.tpack");

        var result = Apply(fs, KeepFour);

        Assert.That(result.Compactions.Select(c => c.Point.BackupId), Is.EqualTo(new uint[] { 6 }));
        Assert.That(result.FailedCompactions.Select(f => (f.BackupId, f.Kind)), Is.EqualTo(new[] { (5u, BackupFailureKind.StoreDamaged) }));
        Assert.That(result.Deleted, Is.EqualTo(new[] { "00000006-I.tpack", "00000004-I.tpack", "00000002-F.tpack" }), "chain A keeps its packs");
    }

    /// <summary>
    /// R1 (DL-7): a kept point that no longer resolves after the deletes is a defect, thrown as its own type (health Failing in the engine). Staged: the
    /// archive step, which runs between the deletes and the check, removes a kept pack.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    public void AKeptPointBrokenByTheDeletes_IsThrownAsTheRetentionsDefect()
    {
        var fs = TwoChains(out _);
        using var session = BackupStore.OpenForMaintenance(Root, "prune", fs);

        var e = Assert.Throws<RetentionBrokeAKeptPointException>(() => RetentionExecutor.Apply(session, KeepFour, null, [], DateTime.UtcNow, Guid.Empty,
            null, (s, _, _) =>
            {
                s.FileSystem.DeleteFile(Root + "/packs/00000008-I.tpack");
                s.RefreshView();
            }));
        Assert.That(e.PointId, Is.EqualTo(8u));
        Assert.That(BackupOperations.Classify(e), Is.EqualTo(BackupFailureKind.StoreDamaged), "a kept point is unrestorable: prune exits 3, as Failing does");
    }

    /// <summary>
    /// Deleting a gone point's drill records is best effort (DL-14): a records directory that cannot be listed or deleted from does not stop the retention
    /// before its archive prune and its kept-point check.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    public void DrillRecordsThatCannotBeDeleted_DoNotStopTheRetention()
    {
        var inner = TwoChains(out var images);
        var fs = new HookedFileSystem(inner)
        {
            Fault = (op, path) => op == nameof(IStoreFileSystem.EnumerateFiles) && path.EndsWith(DrillRecords.Directory, StringComparison.Ordinal)
                ? new IOException("the drills directory is unreadable")
                : null,
        };
        var pruned = false;
        BackupRetentionResult result;
        using (var session = BackupStore.OpenForMaintenance(Root, "prune", fs))
        {
            result = RetentionExecutor.Apply(session, KeepFour, null, [], DateTime.UtcNow, Guid.Empty, null, (_, _, _) => pruned = true);
        }

        Assert.That(result.Deleted, Is.Not.Empty, "premise: points were deleted, so their records were to go");
        Assert.That(pruned, Is.True, "the archive is still pruned");
        AssertKeptPointsIntact(inner, images, result.Plan.Kept);
    }

    /// <summary>Flips a byte of the frame of a page <paramref name="pack"/> holds and none of <paramref name="newer"/> does.</summary>
    private static void DamageAPageOnlyIn(InMemorySparseFileSystem fs, string pack, params string[] newer)
    {
        var shadowed = new HashSet<uint>();
        foreach (var n in newer)
        {
            using var reader = PackReader.Open(fs, Root + "/packs/" + n);
            foreach (var e in reader.LoadIndex())
            {
                shadowed.Add(e.PageId);
            }
        }

        var path = Root + "/packs/" + pack;
        PackPageEntry victim;
        using (var reader = PackReader.Open(fs, path))
        {
            victim = reader.LoadIndex().First(e => e.StoredLength is > 0 and <= 8192 && !shadowed.Contains(e.PageId));
        }

        var at = victim.DataOffset + 1;
        fs.Poke(path, at, [(byte)(fs.ReadAll(path)[at] ^ 0x5A)]);
    }

    /// <summary>DL-13: after the deletes, under the lock, the archive is pruned to the oldest kept point of each timeline, the live one named.</summary>
    [Test]
    [CancelAfter(15_000)]
    public void TheArchiveIsPrunedAfterTheDeletes_ToTheOldestKeptPointOfEachTimeline()
    {
        var fs = TwoChains(out _);
        var live = Guid.NewGuid();
        (IReadOnlyList<(Guid, long)> Floors, Guid Live, int PacksLeft) seen = default;
        using (var session = BackupStore.OpenForMaintenance(Root, "prune", fs))
        {
            RetentionExecutor.Apply(session, KeepFour, null, [], DateTime.UtcNow, live, null,
                (s, floors, liveTimeline) => seen = (floors, liveTimeline, s.View.Catalog.Packs.Count));
        }

        Assert.That(seen.Floors, Is.EqualTo(new[] { (StoreTestKit.Timeline, BackupTestImages.Clean.CheckpointLsn) }));
        Assert.That(seen.Live, Is.EqualTo(live));
        Assert.That(seen.PacksLeft, Is.EqualTo(4), "called once the packs are deleted");
    }

    internal static BackupRetentionResult Apply(IStoreFileSystem fs, BackupRetentionPolicy policy, BackupRetentionPlan approved = null)
    {
        using var session = BackupStore.OpenForMaintenance(Root, "prune", fs);
        return RetentionExecutor.Apply(session, policy, approved, [], DateTime.UtcNow, Guid.Empty, null);
    }

    private static BackupRetentionPlan BackupOperationsPlan(IStoreFileSystem fs, BackupRetentionPolicy policy) =>
        RetentionPlanner.Plan(StoreView.Open(Root, fs, trustCatalog: false), policy, [], DateTime.UtcNow, null).Plan;

    /// <summary>Every kept point resolves and materialises to the image it had before the retention.</summary>
    internal static void AssertKeptPointsIntact(InMemorySparseFileSystem fs, Dictionary<uint, byte[]> images, IEnumerable<uint> kept)
    {
        var view = StoreView.Open(Root, fs, trustCatalog: false);
        foreach (var id in kept)
        {
            Assert.That(ChainResolver.TryResolve(view, id, out _, out _, out var why), Is.True, $"{Marker} #{id} does not resolve: {why}");
            Assert.That(CompactionEquivalenceTests.Materialize(fs, id).AsSpan().SequenceEqual(images[id]), Is.True, $"{Marker} #{id} changed");
        }
    }

    /// <summary>Chain A: F1 I3 I5 I7. Chain B: F2 I4 I6 I8. Every point's image recorded.</summary>
    private static InMemorySparseFileSystem TwoChains(out Dictionary<uint, byte[]> images)
    {
        var image = BackupTestImages.Clean;
        var fs = new InMemorySparseFileSystem();
        fs.CreateDirectory("/");
        using (var store = BackupStore.OpenForWrite(Root, image.DatabaseId, true, fs))
        {
            var a = StoreTestKit.WritePoint(store, image, @override: StoreTestKit.Salted(image, 101));
            var b = StoreTestKit.WritePoint(store, image, @override: StoreTestKit.Salted(image, 102));
            for (var i = 3; i <= 8; i++)
            {
                var chain = i % 2 == 1 ? a.ChainId : b.ChainId;
                StoreTestKit.WritePoint(store, image, chain, pages: [2 + i, 3 + i], @override: StoreTestKit.Salted(image, i));
            }
        }

        images = Enumerable.Range(1, 8).ToDictionary(i => (uint)i, i => CompactionEquivalenceTests.Materialize(fs, (uint)i));
        return fs;
    }
}
