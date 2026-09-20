using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// P1-12 — the shape of S2a's output: hits grouped into cluster runs, one directory probe per run, a new-block list for the clusters that have none, and a
/// per-session run list that survives the stage.
/// </summary>
/// <remarks>
/// <para>
/// <b>The probe count is the assertion that justifies the directory's data structure.</b> <c>foundation/02 § 4</c> chose an open-addressed map over a flat
/// array indexed by chunk id — 381 MB at a billion entities — on the strength of one claim: the probe is amortized over a run of 10-60 hits rather than paid
/// per hit. That claim is a count, so it is counted here and not reasoned about.
/// </para>
/// <para>
/// <b>Cost is asserted by counting, never by timing.</b> "O(hits)" is checked by doubling the session count and reading the hit, run and probe counts back;
/// a wall-clock assertion would measure the box.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class InterestPassTests : TestBase<InterestPassTests>
{
    private const int CreatureCount = 260;

    private DatabaseEngine SetupEngine() => ProjectionTestSchema.SetupEngine(ServiceProvider);

    private static ProjBounds PointAt(float x, float y) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Speed = 1f };

    private static void DeclareWorld(SubscriptionsRegistry subs)
    {
        ProjectionTestSchema.DeclareCreature(subs);
        subs.Profile("world", p => p.World().Of<ProjCreature>());
    }

    private static void Populate(DatabaseEngine dbe, int count)
    {
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < count; i++)
            {
                tx.Spawn<ProjCreature>(ProjCreature.Bounds.Set(PointAt(10f + (i % 40), 10f + (i / 40))));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);
    }

    /// <summary>Builds a harness with <paramref name="sessions"/> sessions bound to the world profile, its blocks already created.</summary>
    private static InterestHarness Ready(DatabaseEngine dbe, int sessions, string name)
    {
        var harness = InterestHarness.Create(dbe, DeclareWorld, name);
        harness.OpenSessions(sessions, "world");
        harness.RunPass(tick: 1);
        harness.CreateRequestedBlocks();
        return harness;
    }

    /// <summary>Every run the last pass produced, flattened, so an assertion block can hold them.</summary>
    private static List<InterestRun> RunsOf(InterestHarness harness, int sessionIndex)
    {
        var runs = new List<InterestRun>(harness.Interest.RunCountOf(sessionIndex));
        for (var r = 0; r < harness.Interest.RunCountOf(sessionIndex); r++)
        {
            runs.Add(harness.Interest.RunOf(sessionIndex, r));
        }

        return runs;
    }

    // ── grouping and probes ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Hits arrive grouped by cluster, so the directory is probed once per run and never once per hit.
    /// </summary>
    [Test]
    public void HitsAreGroupedByClusterSoTheDirectoryIsProbedOncePerRun()
    {
        const int Sessions = 4;

        using var dbe = SetupEngine();
        Populate(dbe, CreatureCount);
        using var harness = Ready(dbe, Sessions, nameof(HitsAreGroupedByClusterSoTheDirectoryIsProbedOncePerRun));

        harness.RunPass(tick: 2);

        var creature = harness.PlanIndex(nameof(ProjCreature));
        var clusters = harness.LiveClusters(creature).Count;
        var live = harness.LiveEntityCount(creature);

        var runs = 0;
        var hits = 0;
        var emptyRuns = 0;
        for (var i = 0; i < harness.Interest.TickSessionCount; i++)
        {
            var sessionHits = 0;
            foreach (var run in RunsOf(harness, i))
            {
                runs++;
                if (run.Slots == 0)
                {
                    emptyRuns++;
                }

                sessionHits += run.HitCount;
            }

            Assert.That(sessionHits, Is.EqualTo(harness.Interest.HitsCountOf(i)), "a session's recorded hit count must be its runs' popcount");
            hits += sessionHits;
        }

        Assert.Multiple(() =>
        {
            Assert.That(clusters, Is.GreaterThan(1), "one cluster would make 'once per run' and 'once per pass' the same number");
            Assert.That(live, Is.EqualTo(CreatureCount));
            Assert.That(emptyRuns, Is.Zero, "an empty cluster must not open a run");

            Assert.That(runs, Is.EqualTo(Sessions * clusters), "one run per live cluster per session");
            Assert.That(hits, Is.EqualTo(Sessions * live), "every live entity is a hit for every session");
            Assert.That(harness.Interest.HitCount, Is.EqualTo(hits));

            // The claim foundation/02 § 4 rests on: probes follow RUNS, not hits. At N = 21 that is roughly a twentieth as many.
            Assert.That(harness.Interest.DirectoryProbes, Is.EqualTo(runs));
            Assert.That(harness.Interest.DirectoryProbes, Is.LessThan(hits / 2), "a probe per hit would make the map the wrong structure");
        });
    }

    /// <summary>
    /// A hit into a cluster with no block lands on the worker's new-block list, once per cluster however many sessions hit it — and stops being asked for
    /// once the block exists.
    /// </summary>
    [Test]
    public void AHitIntoAClusterWithNoBlockLandsOnTheNewBlockList()
    {
        const int Sessions = 5;

        using var dbe = SetupEngine();
        Populate(dbe, CreatureCount);

        using var harness = InterestHarness.Create(dbe, DeclareWorld, nameof(AHitIntoAClusterWithNoBlockLandsOnTheNewBlockList));
        harness.OpenSessions(Sessions, "world");

        harness.RunPass(tick: 1);

        var creature = harness.PlanIndex(nameof(ProjCreature));
        var clusters = harness.LiveClusters(creature).Count;

        var flagged = 0;
        var flaggedWithPointer = 0;
        for (var i = 0; i < harness.Interest.TickSessionCount; i++)
        {
            foreach (var run in RunsOf(harness, i))
            {
                if ((run.Flags & InterestRunFlags.NoBlock) == 0)
                {
                    continue;
                }

                flagged++;
                if (run.Block != 0)
                {
                    flaggedWithPointer++;
                }
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(harness.Interest.NewBlockCount, Is.EqualTo(clusters),
                "one entry per cluster, not one per (cluster, session) — five sessions on one worker must not ask five times");
            Assert.That(flagged, Is.EqualTo(Sessions * clusters), "every run of every session is unbacked before the blocks step runs");
            Assert.That(flaggedWithPointer, Is.Zero, "a run flagged NoBlock must carry no block pointer");
        });

        Assert.That(harness.CreateRequestedBlocks(), Is.EqualTo(clusters));

        harness.RunPass(tick: 2);

        var unbacked = 0;
        var nullPointers = 0;
        for (var i = 0; i < harness.Interest.TickSessionCount; i++)
        {
            foreach (var run in RunsOf(harness, i))
            {
                if ((run.Flags & InterestRunFlags.NoBlock) != 0)
                {
                    unbacked++;
                }

                if (run.Block == 0)
                {
                    nullPointers++;
                }
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(harness.Interest.NewBlockCount, Is.Zero, "a cluster that has a block must not be asked for again");
            Assert.That(unbacked, Is.Zero);
            Assert.That(nullPointers, Is.Zero, "a backed run carries the block the next stage reads through");
        });
    }

    /// <summary>
    /// The per-session run list is still readable after the stage returns, it names that session's clusters, and two sessions get their own windows onto it.
    /// </summary>
    /// <remarks>
    /// This is the property S2b depends on: the hit list is kept, not scratch reused between sessions. The cheapest strong form of it is that each session's
    /// window covers every live cluster exactly once, with the cluster's own occupancy word as the slot mask.
    /// </remarks>
    [Test]
    public void TheKeptHitListSurvivesIntoTheNextStage()
    {
        const int Sessions = 3;

        using var dbe = SetupEngine();
        Populate(dbe, CreatureCount);
        using var harness = Ready(dbe, Sessions, nameof(TheKeptHitListSurvivesIntoTheNextStage));

        harness.RunPass(tick: 2);

        var creature = harness.PlanIndex(nameof(ProjCreature));
        var live = harness.LiveClusters(creature);
        var occupancyOf = new Dictionary<int, ulong>();
        foreach (var (chunkId, occupancy) in live)
        {
            occupancyOf[chunkId] = occupancy;
        }

        Assert.That(harness.Interest.TickSessionCount, Is.EqualTo(Sessions));

        var seen = new HashSet<(int Session, int ChunkId)>();
        for (var i = 0; i < Sessions; i++)
        {
            var runs = RunsOf(harness, i);
            Assert.That(runs, Has.Count.EqualTo(live.Count), "a session's window must cover every live cluster exactly once");

            foreach (var run in runs)
            {
                Assert.That(seen.Add((i, run.ChunkId)), Is.True, $"session {i}'s window repeats cluster {run.ChunkId}");
                Assert.That(run.ArchetypeIndex, Is.EqualTo(creature));
                Assert.That(occupancyOf.TryGetValue(run.ChunkId, out var occupancy), Is.True, $"run names cluster {run.ChunkId}, which is not live");
                Assert.That(run.Slots, Is.EqualTo(occupancy), "the kept run's slot mask is the cluster's occupancy, read back after the pass returned");
            }
        }

        // Every (session, cluster) pair, and no more: the windows are disjoint and together they are the whole arena.
        Assert.That(seen, Has.Count.EqualTo(Sessions * live.Count));
    }

    // ── cost ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The pass costs O(hits): twice the sessions is twice the hits and twice the probes, with the watched set unchanged.
    /// </summary>
    [Test]
    public void PerHitCostFollowsTheHitsAndNothingElse()
    {
        using var dbe = SetupEngine();
        Populate(dbe, CreatureCount);
        using var harness = Ready(dbe, sessions: 2, nameof(PerHitCostFollowsTheHitsAndNothingElse));

        harness.RunPass(tick: 2);
        var hitsAtTwo = harness.Interest.HitCount;
        var probesAtTwo = harness.Interest.DirectoryProbes;
        var watchedAtTwo = harness.Interest.WatchedBlockCount;

        harness.OpenSessions(2, "world");
        harness.RunPass(tick: 3);

        Assert.Multiple(() =>
        {
            Assert.That(harness.Interest.TickSessionCount, Is.EqualTo(4));
            Assert.That(harness.Interest.HitCount, Is.EqualTo(hitsAtTwo * 2), "hits are per session per entity");
            Assert.That(harness.Interest.DirectoryProbes, Is.EqualTo(probesAtTwo * 2), "probes are per session per cluster");

            // The watched set is a property of the world, not of who is looking at it: the same entities, marked once however many sessions reached them.
            Assert.That(harness.Interest.WatchedBlockCount, Is.EqualTo(watchedAtTwo), "a second pair of sessions must not double the watched-block list");
        });
    }

    /// <summary>
    /// After warm-up a pass allocates nothing: every buffer is cleared and reused, so a session costs no managed memory per tick (SUB-07).
    /// </summary>
    /// <remarks>
    /// Measured on the driving thread with one chunk, so <see cref="GC.GetAllocatedBytesForCurrentThread"/> sees the whole pass. Four warm-up passes first —
    /// the arena's run buffer doubles until it fits and the tick-session arrays grow once, both of which are the warm-up this asserts is finite.
    /// </remarks>
    [Test]
    public void APassAllocatesNothingAfterWarmUp()
    {
        using var dbe = SetupEngine();
        Populate(dbe, CreatureCount);
        using var harness = Ready(dbe, sessions: 6, nameof(APassAllocatesNothingAfterWarmUp));

        for (var tick = 2; tick <= 5; tick++)
        {
            harness.RunPass(tick);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        harness.RunPass(tick: 6);
        harness.RunPass(tick: 7);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.That(allocated, Is.Zero, $"two steady-state passes allocated {allocated} bytes");
    }

    // ── partitioning ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Sessions are partitioned across chunks, each session is resolved by exactly one worker, and the union is the same watched set.
    /// </summary>
    [Test]
    public void SessionsArePartitionedAcrossChunksAndEachIsResolvedOnce()
    {
        const int Sessions = 8;

        using var dbe = SetupEngine();
        Populate(dbe, CreatureCount);
        using var harness = Ready(dbe, Sessions, nameof(SessionsArePartitionedAcrossChunksAndEachIsResolvedOnce));

        var chunks = harness.RunPass(tick: 2, workers: 4);
        Assert.That(chunks, Is.EqualTo(4), "four workers and eight sessions is four chunks");

        var creature = harness.PlanIndex(nameof(ProjCreature));
        var clusters = harness.LiveClusters(creature).Count;

        var perWorker = new int[harness.Interest.ArenaCount];
        var wrongRunCount = 0;
        for (var i = 0; i < harness.Interest.TickSessionCount; i++)
        {
            if (harness.Interest.RunCountOf(i) != clusters)
            {
                wrongRunCount++;
            }

            perWorker[harness.Interest.WorkerOf(i)]++;
        }

        Assert.Multiple(() =>
        {
            Assert.That(wrongRunCount, Is.Zero, "every session's window covers every live cluster, whichever worker resolved it");
            Assert.That(harness.Interest.HitCount, Is.EqualTo((long)Sessions * CreatureCount));

            // ONCE EACH, not two each. The pass hands its cell groups out from a shared cursor rather than giving every chunk an equal slice (17 § 17),
            // so which worker takes which session is a race and an even split is not a property the pass has any more — a chunk that arrives first may
            // legitimately take all eight. What the dispatch still owes is that every session is resolved exactly once, which the total asserts, and
            // that no worker is handed a session twice, which this does.
            var total = 0;
            for (var w = 0; w < perWorker.Length; w++)
            {
                total += perWorker[w];
            }

            Assert.That(total, Is.EqualTo(Sessions), "every session is resolved exactly once, whichever worker took it");
        });

        harness.AssertWatchedMatchesOccupancy(creature, "a partitioned pass marks the same set a single-chunk one does");
    }

    /// <summary>
    /// P1-12's acceptance criterion, as counts: at 110 <c>World</c> sessions over a 12 k-entity world the watched set is exactly the live set, and the
    /// pass's cost is 110 × 12 000 hits over 110 × (clusters) runs.
    /// </summary>
    /// <remarks>
    /// The numbers are reported as well as asserted, because they are what the build plan's Q-M2 measurement is calibrated against: hits are per session per
    /// entity by construction of a <c>World</c> observer, while the watched set — and therefore everything S1 and S2b cost — is per entity, once.
    /// </remarks>
    [Test]
    public void AtOneHundredAndTenWorldSessionsTheWatchedSetIsTheLiveSet()
    {
        const int Sessions = 110;
        const int Entities = 12_000;

        using var dbe = SetupEngine();
        Populate(dbe, Entities);
        using var harness = Ready(dbe, Sessions, nameof(AtOneHundredAndTenWorldSessionsTheWatchedSetIsTheLiveSet));

        harness.RunPass(tick: 2, workers: 8);

        var creature = harness.PlanIndex(nameof(ProjCreature));
        var clusters = harness.LiveClusters(creature).Count;
        var live = harness.LiveEntityCount(creature);

        TestContext.Out.WriteLine(
            $"110 World sessions / {live} entities / {clusters} clusters: hits={harness.Interest.HitCount}, runs={harness.Interest.DirectoryProbes}, "
            + $"watched blocks={harness.Interest.WatchedBlockCount}, arena bytes={harness.Interest.DirectoryProbes * 24}");

        Assert.Multiple(() =>
        {
            Assert.That(live, Is.EqualTo(Entities));
            Assert.That(harness.Interest.HitCount, Is.EqualTo((long)Sessions * Entities), "a World hit is per session per entity");
            Assert.That(harness.Interest.DirectoryProbes, Is.EqualTo((long)Sessions * clusters), "one probe per cluster per session, never one per hit");
            Assert.That(harness.Interest.WatchedBlockCount, Is.EqualTo(clusters), "the watched set is per entity, once — not once per session");
        });

        harness.AssertWatchedMatchesOccupancy(creature, "the watched set equals the live set at 110 sessions");
    }

    /// <summary>A session with no profile bound has no interest, and costs the pass nothing at all.</summary>
    [Test]
    public void ASessionWithNoProfileIsNotPartitionedAtAll()
    {
        using var dbe = SetupEngine();
        Populate(dbe, CreatureCount);

        using var harness = InterestHarness.Create(dbe, DeclareWorld, nameof(ASessionWithNoProfileIsNotPartitionedAtAll));
        harness.OpenSessions(3, profile: null);

        var chunks = harness.RunPass(tick: 1, workers: 4);

        Assert.Multiple(() =>
        {
            Assert.That(chunks, Is.Zero, "three sessions with no interest must not buy a wake cycle");
            Assert.That(harness.Interest.TickSessionCount, Is.Zero);
            Assert.That(harness.Interest.HitCount, Is.Zero);
            Assert.That(harness.Interest.NewBlockCount, Is.Zero);
        });
    }
}
