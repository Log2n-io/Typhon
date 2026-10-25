using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Typhon.Engine.Tests.Backup;

/// <summary>
/// BKP-14's plan (05 §3 as corrected by plan 1415 DL-4/5/12), over catalog entries: each limit alone and combined, MinKeep and KeepLastDrilled beating
/// them (and saying so), the compactions a cutoff needs, what is deleted and in which order, and what is never deleted.
/// </summary>
[TestFixture]
internal sealed class RetentionPlannerTests
{
    private static readonly Guid Db = Guid.Parse("d0d0d0d0-0000-0000-0000-000000000001");
    private static readonly Guid Store = Guid.Parse("5a5a5a5a-0000-0000-0000-000000000001");
    private static readonly Guid ChainA = Guid.Parse("c4a1c4a1-0000-0000-0000-00000000000a");
    private static readonly Guid ChainB = Guid.Parse("c4a1c4a1-0000-0000-0000-00000000000b");
    private static readonly Guid Timeline = Guid.Parse("71e71e00-0000-0000-0000-000000000001");
    private static readonly DateTime Now = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
    private const long FullSize = 1000;
    private const long IncrSize = 100;

    /// <summary>Point <paramref name="id"/>, frozen <paramref name="id"/> hours after a day ago; a full pack 1000 B, an incremental 100 B.</summary>
    private static CatalogPack Pack(uint id, BackupKind kind, uint parent, uint baseId, Guid chain, long size = 0, Guid timeline = default) => new()
    {
        File = PackFormat.FileName(id, kind),
        Size = size != 0 ? size : kind == BackupKind.Incremental ? IncrSize : FullSize,
        BackupId = id,
        Kind = kind,
        ParentId = parent,
        BaseId = baseId,
        ChainId = chain,
        DatabaseId = Db,
        StoreId = Store,
        TimelineId = timeline == default ? Timeline : timeline,
        ImagePages = 100,
        CheckpointLsn = id * 10,
        PointLsn = (id * 10) + 5,
        FreezeUtc = Now.AddDays(-1).AddHours(id),
    };

    /// <summary>A chain F1 ← I2 ← … ← I<paramref name="depth"/>.</summary>
    private static List<CatalogPack> Chain(int depth, Guid chain = default, uint first = 1)
    {
        chain = chain == default ? ChainA : chain;
        var packs = new List<CatalogPack> { Pack(first, BackupKind.Full, 0, first, chain) };
        for (var id = first + 1; id < first + depth; id++)
        {
            packs.Add(Pack(id, BackupKind.Incremental, id - 1, first, chain));
        }

        return packs;
    }

    private static StoreView View(IEnumerable<CatalogPack> packs, IEnumerable<CatalogUnreadable> unreadable = null) => new()
    {
        Paths = new StorePaths("/s"),
        Identity = new StoreIdentityFile { FormatVersion = 1, StoreId = Store, DatabaseId = Db },
        Catalog = new StoreCatalogFile { Packs = packs.ToList(), Unreadable = (unreadable ?? []).ToList() },
        Temps = [],
    };

    private static BackupRetentionPolicy Policy(int minKeep = 1, int maxCount = 0, TimeSpan maxAge = default, long maxBytes = 0, int compactAt = 0,
        bool keepDrilled = false)
    {
        var policy = new BackupRetentionPolicy
        {
            MinKeep = minKeep, MaxCount = maxCount, MaxAge = maxAge, MaxTotalBytes = maxBytes, CompactThreshold = compactAt, KeepLastDrilled = keepDrilled,
        };
        Assert.That(policy.Validate(out var error), Is.True, error);
        return policy;
    }

    private static RetentionDecision Plan(StoreView view, BackupRetentionPolicy policy, uint[] drilled = null, long? free = null) =>
        RetentionPlanner.Plan(view, policy, drilled ?? [], Now, free);

    [Test]
    [CancelAfter(5_000)]
    [VerifiesRule("BKP-14")]
    public void MaxCount_KeepsTheNewest_CompactsTheCutoff_AndDeletesEverythingBelowItNewestFirst()
    {
        var plan = Plan(View(Chain(10)), Policy(maxCount: 3)).Plan;

        Assert.That(plan.Kept, Is.EqualTo(new uint[] { 8, 9, 10 }));
        Assert.That(plan.CutoffId, Is.EqualTo(8u));
        Assert.That(plan.Compactions, Is.EqualTo(new uint[] { 8 }), "the cutoff becomes a base first");
        Assert.That(plan.PackDeletions, Is.EqualTo(new[] { "00000008-I.tpack" }.Concat(Enumerable.Range(1, 6).Reverse().Select(i => $"0000000{i + 1}-I.tpack"))
            .Append("00000001-F.tpack")), "the superseded -I, then every pack below the cutoff, descending");
        Assert.That(plan.Removed, Is.EqualTo(Enumerable.Range(1, 7).Select(i => (uint)i)));
        Assert.That(plan.Blocked, Is.EqualTo(BackupRetentionBlock.None));
        Assert.That(plan.BytesBefore, Is.EqualTo(FullSize + (9 * IncrSize)));
        Assert.That(plan.BytesAfterAtMost, Is.EqualTo(FullSize + (9 * IncrSize)), "an upper bound: today #10 resolves through every pack");
    }

    [Test]
    [CancelAfter(5_000)]
    [VerifiesRule("BKP-14")]
    public void MaxAge_KeepsThePointsFrozenWithinIt()
    {
        // #id is frozen at (now - 24 h + id h): within 5 h means id ≥ 19.
        var plan = Plan(View(Chain(22)), Policy(maxAge: TimeSpan.FromHours(5))).Plan;

        Assert.That(plan.Kept, Is.EqualTo(new uint[] { 19, 20, 21, 22 }));
    }

    [Test]
    [CancelAfter(5_000)]
    [VerifiesRule("BKP-14")]
    public void MaxTotalBytes_CountsThePacksTheKeptPointsResolveThrough()
    {
        // #10 needs F1 + I2..I10 = 1900 B; every older point adds nothing. A full #11 on another chain needs 1000 more.
        var packs = Chain(10);
        packs.Add(Pack(11, BackupKind.Full, 0, 11, ChainB));
        Assert.That(Plan(View(packs), Policy(maxBytes: 2_899)).Plan.Kept, Is.EqualTo(new uint[] { 11 }));
        Assert.That(Plan(View(packs), Policy(maxBytes: 2_900)).Plan.Kept, Is.EqualTo(Enumerable.Range(1, 11).Select(i => (uint)i)));
    }

    [Test]
    [CancelAfter(5_000)]
    [VerifiesRule("BKP-14")]
    public void AnyExceededLimit_Cuts()
    {
        var plan = Plan(View(Chain(22)), Policy(maxCount: 10, maxAge: TimeSpan.FromHours(5))).Plan;
        Assert.That(plan.Kept, Is.EqualTo(new uint[] { 19, 20, 21, 22 }), "the age limit cuts before the count does");
        plan = Plan(View(Chain(22)), Policy(maxCount: 2, maxAge: TimeSpan.FromHours(5))).Plan;
        Assert.That(plan.Kept, Is.EqualTo(new uint[] { 21, 22 }), "and the count before the age");
    }

    [Test]
    [CancelAfter(5_000)]
    [VerifiesRule("BKP-14")]
    public void MinKeep_BeatsEveryLimit_AndSaysSo()
    {
        var plan = Plan(View(Chain(10)), Policy(minKeep: 3, maxAge: TimeSpan.FromMinutes(1))).Plan;

        Assert.That(plan.Kept, Is.EqualTo(new uint[] { 8, 9, 10 }));
        Assert.That(plan.Blocked, Is.EqualTo(BackupRetentionBlock.MinKeep));
        Assert.That(plan.BlockedReason, Does.Contain("MaxAge"));
    }

    /// <summary>MinKeep counts RESTORABLE points: an unrestorable point is never one of the points MinKeep promises.</summary>
    [Test]
    [CancelAfter(5_000)]
    [VerifiesRule("BKP-14")]
    public void MinKeep_CountsRestorablePointsOnly()
    {
        var view = BrokenNewestView();
        var decision = Plan(view, Policy(minKeep: 2, maxAge: TimeSpan.FromMinutes(1)));

        AssertMinKeepHonoured(view, decision.Kept, 2);
    }

    /// <summary>The check can fail: a plan that counted the unrestorable newest point toward MinKeep keeps one restorable point too few.</summary>
    [Test]
    [CancelAfter(5_000)]
    [RuleMutant("BKP-14")]
    public void Mutant_MinKeepCountingUnrestorablePoints_IsReported()
    {
        var view = BrokenNewestView();
        var all = view.Catalog.Packs.Select(p => p.BackupId).Distinct().OrderByDescending(i => i).Take(2).ToList();

        RuleMutants.AssertDetects("BKP-14", "BKP-14: MinKeep", () => AssertMinKeepHonoured(view, all, 2));
    }

    [Test]
    [CancelAfter(5_000)]
    [VerifiesRule("BKP-14")]
    public void KeepLastDrilled_PinsTheNewestDrillPassedPoint_AndEveryPointAfterIt()
    {
        var policy = Policy(maxCount: 3, keepDrilled: true);
        var plan = Plan(View(Chain(10)), policy, drilled: [3, 5]).Plan;

        Assert.That(plan.Kept, Is.EqualTo(Enumerable.Range(5, 6).Select(i => (uint)i)));
        Assert.That(plan.Blocked, Is.EqualTo(BackupRetentionBlock.KeepLastDrilled));
        Assert.That(Plan(View(Chain(10)), policy, drilled: [9]).Plan.Kept, Is.EqualTo(new uint[] { 8, 9, 10 }), "a drill inside the kept set moves nothing");
        Assert.That(Plan(View(Chain(10)), Policy(maxCount: 3), drilled: [5]).Plan.Kept, Is.EqualTo(new uint[] { 8, 9, 10 }), "off: ignored");

        // Chain A: F1 I3 I5 I7 I9 I11; chain B: F2, I4 whose parent is gone. #4 was drilled, then broke: it pins nothing.
        var packs = new List<CatalogPack> { Pack(1, BackupKind.Full, 0, 1, ChainA), Pack(2, BackupKind.Full, 0, 2, ChainB) };
        for (uint id = 3; id <= 11; id += 2)
        {
            packs.Add(Pack(id, BackupKind.Incremental, id - 2, 1, ChainA));
        }

        packs.Add(Pack(4, BackupKind.Incremental, 99, 2, ChainB));
        Assert.That(Plan(View(packs), policy, drilled: [4]).Plan.Kept, Is.EqualTo(new uint[] { 7, 9, 11 }),
            "a drilled point that no longer resolves pins nothing");
    }

    [Test]
    [CancelAfter(5_000)]
    [VerifiesRule("BKP-14")]
    public void ACutoffThatIsABase_NeedsNoCompaction()
    {
        var packs = Chain(5);
        packs.Add(Pack(3, BackupKind.Compacted, 0, 3, ChainA));
        var plan = Plan(View(packs), Policy(maxCount: 3)).Plan;

        Assert.That(plan.Compactions, Is.Empty);
        Assert.That(plan.PackDeletions, Is.EqualTo(new[] { "00000003-I.tpack", "00000002-I.tpack", "00000001-F.tpack" }));
    }

    [Test]
    [CancelAfter(5_000)]
    [VerifiesRule("BKP-14")]
    public void CompactThreshold_CompactsAHeadThatWouldStayTooDeep_AndOnlyThen()
    {
        // Kept 3..10 (8 points); after compacting the cutoff #3, the head reads 3-C + 4..10 = 8 packs.
        Assert.That(Plan(View(Chain(10)), Policy(maxCount: 8, compactAt: 8)).Plan.Compactions, Is.EqualTo(new uint[] { 3 }));
        var plan = Plan(View(Chain(10)), Policy(maxCount: 8, compactAt: 7)).Plan;
        Assert.That(plan.Compactions, Is.EqualTo(new uint[] { 3, 10 }));
        Assert.That(plan.PackDeletions, Does.Contain("00000010-I.tpack"), "the head's superseded -I");
        Assert.That(plan.PackDeletions, Does.Not.Contain("00000009-I.tpack"), "#9 still needs its own pack");
        Assert.That(Plan(View(Chain(10)), Policy(maxCount: 20, compactAt: 9)).Plan.Compactions, Is.EqualTo(new uint[] { 10 }), "nothing cut, head too deep");
    }

    [Test]
    [CancelAfter(5_000)]
    [VerifiesRule("BKP-14")]
    public void ShortOfSpaceForACompaction_NothingIsCompacted_AndTheChainKeepsItsPacks()
    {
        // Chain A: F1..I6; chain B: F7, I8. Kept 6, 7, 8: #6 needs its compaction to let go of #1..#5.
        var packs = Chain(6, ChainA);
        packs.AddRange(Chain(2, ChainB, first: 7));
        var plan = Plan(View(packs), Policy(maxCount: 3), free: 100).Plan;

        Assert.That(plan.Blocked, Is.EqualTo(BackupRetentionBlock.InsufficientSpace));
        Assert.That(plan.Compactions, Is.Empty);
        Assert.That(plan.PackDeletions, Is.Empty, "#6 still resolves through #1..#5");
        Assert.That(Plan(View(packs), Policy(maxCount: 3), free: 1_000_000).Plan.Compactions, Is.EqualTo(new uint[] { 6 }));
    }

    [Test]
    [CancelAfter(5_000)]
    [VerifiesRule("BKP-14")]
    public void UnrestorablePointsBelowTheCutoff_AreDeleted_AndADamagedChainAtOrAfterItLosesNothing()
    {
        // Chain A: F1 I3 I5. Chain B: F2, I4 broken (its pack names parent 99), so I6 does not resolve either.
        var packs = new List<CatalogPack>
        {
            Pack(1, BackupKind.Full, 0, 1, ChainA), Pack(2, BackupKind.Full, 0, 2, ChainB), Pack(3, BackupKind.Incremental, 1, 1, ChainA),
            Pack(4, BackupKind.Incremental, 2, 2, ChainB), Pack(5, BackupKind.Incremental, 3, 1, ChainA), Pack(6, BackupKind.Incremental, 4, 2, ChainB),
        };
        packs[3].ParentId = 99;   // #4 and #6 do not resolve
        var plan = Plan(View(packs), Policy(maxCount: 1)).Plan;

        Assert.That(plan.Kept, Is.EqualTo(new uint[] { 5 }));
        Assert.That(plan.Blocked, Is.EqualTo(BackupRetentionBlock.DamagedChain));
        Assert.That(plan.PackDeletions, Is.EqualTo(new[] { "00000005-I.tpack", "00000003-I.tpack", "00000001-F.tpack" }),
            "chain A below the cutoff goes; chain B keeps everything: its #6 may be repaired");
        Assert.That(plan.Compactions, Is.EqualTo(new uint[] { 5 }));
    }

    /// <summary>
    /// The worst block is the one reported: a damaged chain (the CLI's exit 3) is not overwritten by a compaction's shortage of space (exit 1).
    /// </summary>
    [Test]
    [CancelAfter(5_000)]
    public void ADamagedChain_IsTheBlockReported_WhenSpaceIsShortToo()
    {
        var packs = DamagedChainB();
        var plan = Plan(View(packs), Policy(maxCount: 1), free: 1).Plan;

        Assert.That(plan.Compactions, Is.Empty, "premise: the space is short for the compaction of #5");
        Assert.That(plan.Blocked, Is.EqualTo(BackupRetentionBlock.DamagedChain));
    }

    /// <summary>
    /// DL-4: a damaged chain loses nothing because it may be repaired, and a repaired point replays the WAL archive from its own C: its timeline keeps the
    /// archive from its oldest pack, though no kept point is on it.
    /// </summary>
    [Test]
    [CancelAfter(5_000)]
    [VerifiesRule("BKP-14")]
    public void ADamagedChainsTimeline_KeepsItsArchive_FromItsOldestPack()
    {
        var other = Guid.Parse("71e71e00-0000-0000-0000-000000000002");
        var packs = DamagedChainB(other);
        var decision = Plan(View(packs), Policy(maxCount: 1));

        Assert.That(decision.Plan.Kept, Is.EqualTo(new uint[] { 5 }), "premise: chain B has no kept point");
        Assert.That(decision.ArchiveFloors, Is.EquivalentTo(new[] { (Timeline, 50L), (other, 20L) }));
        Assert.That(decision.KeepArchive, Is.False);
    }

    /// <summary>An unreadable pack at or after the cutoff (its timeline unknown), or a store with nothing restorable, keeps the whole archive.</summary>
    [Test]
    [CancelAfter(5_000)]
    public void AChainOfUnknownTimeline_OrNothingRestorable_KeepsTheWholeArchive()
    {
        var unreadable = Plan(View(Chain(6), [new CatalogUnreadable { File = "00000007-F.tpack", Size = 10, Reason = "torn" }]), Policy(maxCount: 2));
        Assert.That(unreadable.KeepArchive, Is.True);
        var nothing = Plan(View(Chain(4).Where(p => p.BackupId != 1)), Policy(maxCount: 1));
        Assert.That(nothing.KeepArchive, Is.True);
        Assert.That(Plan(View(Chain(6)), Policy(maxCount: 2)).KeepArchive, Is.False, "a sound store prunes it");
    }

    /// <summary>Chain A: F1 I3 I5. Chain B (on <paramref name="timeline"/>): F2, I4 broken (its pack names parent 99), so I6 does not resolve either.</summary>
    private static List<CatalogPack> DamagedChainB(Guid timeline = default)
    {
        var packs = new List<CatalogPack>
        {
            Pack(1, BackupKind.Full, 0, 1, ChainA), Pack(2, BackupKind.Full, 0, 2, ChainB, timeline: timeline), Pack(3, BackupKind.Incremental, 1, 1, ChainA),
            Pack(4, BackupKind.Incremental, 2, 2, ChainB, timeline: timeline), Pack(5, BackupKind.Incremental, 3, 1, ChainA),
            Pack(6, BackupKind.Incremental, 4, 2, ChainB, timeline: timeline),
        };
        packs[3].ParentId = 99;
        return packs;
    }

    [Test]
    [CancelAfter(5_000)]
    [VerifiesRule("BKP-14")]
    public void AnUnreadablePackAtOrAfterTheCutoff_ProtectsEverythingBelowIt()
    {
        var plan = Plan(View(Chain(6), [new CatalogUnreadable { File = "00000007-F.tpack", Size = 10, Reason = "FooterCRC mismatch" }]),
            Policy(maxCount: 2)).Plan;

        Assert.That(plan.Blocked, Is.EqualTo(BackupRetentionBlock.DamagedChain));
        Assert.That(plan.PackDeletions, Is.EqualTo(new[] { "00000005-I.tpack" }), "only the -I its own compaction supersedes");

        var below = Plan(View(Chain(6), [new CatalogUnreadable { File = "00000001-C.tpack", Size = 10, Reason = "torn" }]), Policy(maxCount: 2)).Plan;
        Assert.That(below.PackDeletions, Does.Contain("00000001-C.tpack"), "an unreadable pack below the cutoff goes with the rest");
    }

    [Test]
    [CancelAfter(5_000)]
    public void AStoreWithNoRestorablePoint_DeletesNothing()
    {
        var packs = Chain(4).Where(p => p.BackupId != 1).ToList();
        var plan = Plan(View(packs), Policy(maxCount: 1)).Plan;

        Assert.That((plan.Kept.Count, plan.PackDeletions.Count, plan.Blocked), Is.EqualTo((0, 0, BackupRetentionBlock.DamagedChain)));
        Assert.That(Plan(View([]), Policy(maxCount: 1)).Plan.Blocked, Is.EqualTo(BackupRetentionBlock.None), "an empty store is not damaged");
    }

    [Test]
    [CancelAfter(5_000)]
    public void TheFingerprint_FollowsTheDeletionsAndCompactions()
    {
        var a = Plan(View(Chain(10)), Policy(maxCount: 3)).Plan;
        var b = Plan(View(Chain(10)), Policy(maxCount: 3)).Plan;
        var c = Plan(View(Chain(10)), Policy(maxCount: 4)).Plan;
        var d = Plan(View(Chain(10)), Policy(maxCount: 3, minKeep: 2)).Plan;

        Assert.That(a.Fingerprint, Is.EqualTo(b.Fingerprint));
        Assert.That(a.Fingerprint, Is.Not.EqualTo(c.Fingerprint));
        Assert.That(a.Fingerprint, Is.EqualTo(d.Fingerprint), "same deletions, same plan");
    }

    [Test]
    [CancelAfter(5_000)]
    public void ThePolicy_StatesEveryLimit_AndRefusesContradictions()
    {
        static string Error(BackupRetentionPolicy p) => p.Validate(out var e) ? null : e;
        var ok = Policy(maxCount: 3);
        Assert.That(Error(ok), Is.Null);
        Assert.That(Error(new BackupRetentionPolicy
        {
            MinKeep = 1, MaxCount = 0, MaxAge = TimeSpan.Zero, MaxTotalBytes = 0, CompactThreshold = 0, KeepLastDrilled = false,
        }), Does.Contain("No limit"));
        Assert.That(Error(new BackupRetentionPolicy
        {
            MinKeep = 0, MaxCount = 3, MaxAge = TimeSpan.Zero, MaxTotalBytes = 0, CompactThreshold = 0, KeepLastDrilled = false,
        }), Does.Contain("MinKeep"));
        Assert.That(Error(new BackupRetentionPolicy
        {
            MinKeep = 4, MaxCount = 3, MaxAge = TimeSpan.Zero, MaxTotalBytes = 0, CompactThreshold = 0, KeepLastDrilled = false,
        }), Does.Contain("below MinKeep"));
        Assert.That(Error(new BackupRetentionPolicy
        {
            MinKeep = 1, MaxCount = 3, MaxAge = TimeSpan.Zero, MaxTotalBytes = 0, CompactThreshold = 1, KeepLastDrilled = false,
        }), Does.Contain("CompactThreshold"));
        Assert.That(Error(new BackupRetentionPolicy
        {
            MinKeep = 1, MaxCount = 3, MaxAge = TimeSpan.FromSeconds(-1), MaxTotalBytes = 0, CompactThreshold = 0, KeepLastDrilled = false,
        }), Does.Contain("negative"));
    }

    /// <summary>A plan over 2 000 points stays well inside the scheduler's budget (plan 1415 §9: ≤ 50 ms).</summary>
    [Test]
    [CancelAfter(15_000)]
    public void APlanOver2000Points_IsCheap()
    {
        var packs = new List<CatalogPack>();
        for (uint c = 0; c < 50; c++)
        {
            packs.AddRange(Chain(40, Guid.NewGuid(), first: (c * 40) + 1));
        }

        var view = View(packs);
        Plan(view, Policy(maxCount: 100));   // resolution is memoised per view, as in a real session
        var started = System.Diagnostics.Stopwatch.StartNew();
        var plan = Plan(View(packs), Policy(maxCount: 100)).Plan;
        Assert.That(plan.Kept, Has.Count.EqualTo(100));
        Assert.That(started.ElapsedMilliseconds, Is.LessThan(500), "an order of magnitude of slack over the 50 ms target, for a loaded CI box");
    }

    /// <summary>The newest MinKeep RESTORABLE points are all kept.</summary>
    internal static void AssertMinKeepHonoured(StoreView view, IReadOnlyCollection<uint> kept, int minKeep)
    {
        var newest = ChainResolver.RestorablePoints(view).OrderByDescending(i => i).Take(minKeep).ToList();
        Assert.That(kept, Is.SupersetOf(newest), $"BKP-14: MinKeep {minKeep} promises points {string.Join(", ", newest)}; kept {string.Join(", ", kept)}");
    }

    /// <summary>F1 ← I2 ← I3 ← I4, and a newest #5 whose parent is missing.</summary>
    private static StoreView BrokenNewestView()
    {
        var packs = Chain(4);
        packs.Add(Pack(5, BackupKind.Incremental, 99, 1, ChainA));
        return View(packs);
    }
}
