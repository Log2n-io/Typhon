using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// <c>AC-4</c> — the per-cell R-Tree under a REAL parallel fence: 50 ticks of motion, four workers, on both AabbRefresh slicing branches.
/// </summary>
/// <remarks>
/// <para><b>What is under test is a divert, not a lock.</b> <see cref="SpatialRTree{TStore}"/> is single-writer by specification (ADR-044, invariant
/// <c>O2</c>), and the AabbRefresh phase slices by CLUSTER id rather than by cell — so two workers routinely carry clusters of the SAME cell and would be two
/// writers in one tree. <c>ApplyOrDeferClusterUpdate</c> answers that by buffering every promoted cell's write worker-side and replaying it on one thread in
/// the fence tail (<c>DrainPromotedAabbApplies</c>). This fixture is the evidence that the answer holds under the interleaving it was written for.</para>
/// <para><b>Both branches, because the defect lived in exactly one of them.</b> The <c>ClusterProcessBitmap</c> branch was diverted first and the
/// <c>ActiveClusterIds</c> branch was not, and the test that was supposed to cover it happened to exercise the undiverted one. Which branch runs is decided by
/// <c>SetSpatialBarrierOnly</c>, so the identical scenario runs under both.</para>
/// <para><b>The shipped cost model, and the partition is checked rather than assumed.</b> An earlier version of this fixture seeded the planner at
/// 500 µs/cluster on the stated grounds that the default 2.4 µs "collapses this population to a single chunk". That was wrong twice over: the formula quoted
/// was <c>ComputeMaxChunks</c>, which the AabbRefresh emitter never calls, and the real one — <c>FenceWorkPlan.ComputeChunkCountAndPack</c>,
/// <c>ceil(totalCost / max(200µs, maxAtomicCost))</c>, whose own comment says worker count is irrelevant — gives <b>2 chunks</b> at the default for the 148
/// dirty clusters this population produces. Measured, not argued. The override is gone; <c>AdaptiveFenceCost</c> stays off only so the plan
/// <see cref="AssertPlanStraddlesAPromotedCell"/> rebuilds is the one the run actually used. That assertion is what makes the interleaving a fact: it proves
/// some promoted cell's clusters landed in two DIFFERENT chunks, which is "two workers, one tree" for an undiverted implementation.</para>
/// <para><b>Migrations are deliberately absent HERE.</b> Motion is a rotation about each cluster's own cell centre, at a radius inside the cell's inscribed
/// circle, so no entity ever leaves its cell. That keeps this fixture about the AabbRefresh divert and nothing else. The Migrate half — a worker inserting
/// into a cell's index while siblings hold the archetype-wide arrays it indexes through — is <c>CellTreeDensityTransitionTests</c>.</para>
/// <para><b>Non-vacuity, by ablation.</b> Reverting the divert alone — <c>ApplyOrDeferClusterUpdate</c> calling <c>UpdateClusterInPerCellIndex</c>
/// unconditionally, every other line untouched — turns both tests red in three runs of three, on <see cref="AssertPromotedTreesAgreeWithClusterState"/>:
/// <i>"cell 1 holds cluster 46 twice"</i>. A duplicated leaf entry is precisely what two concurrent remove-and-reinserts leave behind. The
/// <c>maxPromotedApplies</c> guard also reddens under that ablation and proves NOTHING — with the divert gone nothing is ever deferred, so a zero there is a
/// tautology. The ablation was judged with that guard suppressed, which is the only way it says anything about corruption.</para>
/// <para><b>Membership AND freshness.</b> Both halves are asserted, but they fail differently and both are needed. A second writer in one tree corrupts
/// MEMBERSHIP — a duplicated or lost leaf entry. A deferral that is buffered and never replayed corrupts FRESHNESS — the tree keeps a bound the entities have
/// rotated out of, while membership stays perfect. <see cref="AssertQueriesMatchStorageTruth"/> covers the second by comparing query answers against entity
/// positions read straight out of the cluster segment, which shares no code with the index it checks.</para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class CellTreeParallelFenceTests : TestBase<CellTreeParallelFenceTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    private const float WorldExtent = 4_000f;

    /// <summary>Four cells (2×2). Few cells and many clusters is what forces several slices onto one cell.</summary>
    private const float CellSize = 2_000f;

    private const int PromoteAt = 8;

    /// <summary>≥128 clusters, so the legacy branch emits ≥4 slices of 32 and the barrier branch ≥2 bitmap words.</summary>
    private const int EntityCount = 8_192;

    private const int MotionTicks = 50;
    private const int WorkerCount = 4;

    /// <summary>Ticks with motion switched off before shutdown — see the comment at the call site.</summary>
    private const int QuiesceTicks = 3;

    /// <summary>Rotation per tick, radians. Large enough that every cluster bound changes every tick, small enough to stay a rotation in f32.</summary>
    private const float ThetaPerTick = 0.031f;

    /// <summary>
    /// The SHIPPED AABB cost, mirrored here so <see cref="AssertPlanStraddlesAPromotedCell"/> rebuilds the same plan the run used. Not a lever — see the
    /// fixture remarks for the measurement that removed the one this fixture used to carry.
    /// </summary>
    private const float PlannerAabbCostUs = 2.4f;

    private static ClCohPos BoxAt(float x, float y) =>
        new() { Bounds = new AABB2F { MinX = x - 1f, MinY = y - 1f, MaxX = x + 1f, MaxY = y + 1f }, Mass = 1.0f };

    private static ArchetypeClusterState ClusterStateOf(DatabaseEngine dbe) =>
        dbe._archetypeStates[Archetype<ClCohUnit>.Metadata.ArchetypeId].ClusterState;

    /// <summary>
    /// Identifies an entity by its storage location, which both the cluster walk and the query result carry. Sidesteps the entity-key encoding.
    /// </summary>
    private static long SlotKey(int clusterChunkId, int slotIndex) => ((long)clusterChunkId << 8) | (uint)slotIndex;

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // AC-4
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    [Test]
    [VerifiesRule("PC-01")]
    [CancelAfter(120_000)]
    public void ParallelFence_PromotedTrees_StayConsistent_OnTheBitmapBranch() => RunFiftyTicks(barrierOnly: true);

    /// <summary>The branch whose divert was missing, and the one an earlier test failed to reach.</summary>
    [Test]
    [VerifiesRule("PC-01")]
    [CancelAfter(120_000)]
    public void ParallelFence_PromotedTrees_StayConsistent_OnTheActiveIdsBranch() => RunFiftyTicks(barrierOnly: false);

    private void RunFiftyTicks(bool barrierOnly)
    {
        // A fresh DATABASE per configuration: both branches run under the same fixture name, and a scope reopens the same file, so the second engine would
        // load the first one's 8 192 entities and spawn another 8 192 on top.
        ServiceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();

        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClCohPos>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(WorldExtent, WorldExtent), CellSize));

        // Must precede InitializeArchetypes — the threshold is copied onto each ArchetypeClusterState as it is built.
        dbe.ClusterCellTreePromoteThreshold = PromoteAt;
        // Step 16: these fixtures exercise the TREE, not the gate that decides when a cell gets one — their clusters are scattered over the cell
        // on purpose, which is exactly the shape the tightness gate refuses. Count-only promotion keeps them testing what they were written for.
        dbe.ClusterCellTreePromoteTightness = 1f;

        dbe.InitializeArchetypes();

        if (barrierOnly)
        {
            dbe.SetSpatialBarrierOnly<ClCohUnit>();
        }

        SpawnPopulation(dbe);
        dbe.WriteTickFence(1);

        var cs = ClusterStateOf(dbe);
        var grid = dbe.SpatialGrid;

        AssertPreconditions(cs, barrierOnly);

        var ticks = 0;
        var motion = 1;
        var maxPromotedApplies = 0;
        var stopping = 0;
        Exception unhandled = null;
        Exception teardown = null;
        int motionTicks;

        using (var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Motion").CallbackSystem("Rotate", ctx =>
            {
                // Read BEFORE moving: the system runs on the public track, ahead of this tick's fence, so this samples the PREVIOUS tick's drain. Taking the
                // maximum over the run is what proves the deferral path ran under load, rather than reading a post-shutdown value from a quiet tick.
                int applied = cs.PromotedApplyCount;
                if (applied > maxPromotedApplies)
                {
                    maxPromotedApplies = applied;
                }

                if (Volatile.Read(ref motion) != 0)
                {
                    RotateEveryEntity(ctx.Transaction, cs, grid);
                }
                Interlocked.Increment(ref ticks);
            });
        }, new RuntimeOptions
        {
            WorkerCount = WorkerCount,
            // 100 Hz, not the 1 000 the other runtime fixtures use, and the difference IS load-bearing, unlike the cost model above. A tick here rewrites 8 192
            // spatial fields and refreshes ~150 cluster bounds, so it does not fit a 1 ms budget and the next tick starts while this system is still inside
            // its callback. The scheduler's per-system claim is per-TICK, so the newer tick runs OnSystemStartInternal and overwrites
            // _systemTransactions[sysIdx]; the older tick's finally then disposes the NEWER transaction from the wrong thread, and Transaction.Dispose's
            // affinity assert raises out of the worker loop.
            //
            // Measured, because "probably overrun" is not a diagnosis: 2 failures in 16 runs at 1 000 Hz and again at 200 Hz; 0 in 16 at 100 Hz and 0 in 16 at
            // 50 Hz. It needs EnableParallelFence (0 in 12 with the serial fence) and it needs promotion (0 in 16 with the threshold off and the runtime
            // verifiably still running) — promotion is what pushes the tick past the budget, by adding the deferral drain and the loose-leaf refit. The
            // trigger is therefore tick OVERRUN, and a budget the tick fits inside removes the overlap instead of papering over it.
            BaseTickRate = 100,
            EnableParallelFence = true,
            AdaptiveFenceCost = false,
            FenceCostModel = new FenceCostModel(MigrationCost: 33.3f, AabbCost: PlannerAabbCostUs, ShadowCost: 1f, SpatialCost: 1f),
        }))
        {
            // Exceptions are split by WHEN they arrive, because Shutdown is documented as not being a quiescence point: "Both stop new ticks; neither waits
            // for the tick in flight." The tick still running when Dispose starts joining can therefore fault on a scheduler event or a transaction that is
            // already being torn down — observed here as ObjectDisposedException out of DagScheduler and as an EntityAccessor affinity violation out of
            // Transaction.Dispose, in roughly a third of runs. Neither says anything about the fence, and asserting on them makes this fixture a coin flip.
            runtime.Scheduler.UnhandledExceptionCallback = (_, _, ex) =>
            {
                if (Volatile.Read(ref stopping) != 0)
                {
                    Interlocked.CompareExchange(ref teardown, ex, null);
                    return;
                }
                Interlocked.CompareExchange(ref unhandled, ex, null);
            };
            runtime.Start();
            SpinWait.SpinUntil(() => Volatile.Read(ref ticks) >= MotionTicks, TimeSpan.FromSeconds(60));
            motionTicks = Volatile.Read(ref ticks);

            // Motion off, then quiet ticks before shutdown. Two things go wrong without them, and neither is what this fixture is about. The tick in flight
            // when Shutdown lands can be abandoned mid-dispatch, which surfaces as an ObjectDisposedException out of the scheduler's own teardown; and a
            // fence that never completed leaves the tree one apply behind the storage every assertion below reads.
            Volatile.Write(ref motion, 0);
            SpinWait.SpinUntil(() => Volatile.Read(ref ticks) >= motionTicks + QuiesceTicks, TimeSpan.FromSeconds(10));
            Volatile.Write(ref stopping, 1);
            runtime.Shutdown();
        }

        string stage = barrierOnly ? "ClusterProcessBitmap branch" : "ActiveClusterIds branch";

        // A fence that threw is the loudest form of the failure this fixture exists to catch: RemoveChecked raises on an identity mismatch, which is exactly
        // what a second writer in one tree produces. Reported before the set comparisons because it names the fault directly.
        if (teardown != null)
        {
            TestContext.Out.WriteLine($"AC-4 {stage}: ignored a teardown-window fault — {teardown.GetType().Name}: {teardown.Message}");
        }
        Assert.That(unhandled, Is.Null, $"{stage}: a fence phase threw while the runtime was running — {unhandled}");
        Assert.That(motionTicks, Is.GreaterThanOrEqualTo(MotionTicks), $"{stage}: the runtime did not complete {MotionTicks} ticks of motion");
        Assert.That(Volatile.Read(ref ticks), Is.GreaterThanOrEqualTo(motionTicks + QuiesceTicks),
            $"{stage}: the quiescing ticks did not run, so the last fence may still be mid-flight");
        Assert.That(maxPromotedApplies, Is.GreaterThan(0),
            $"{stage}: no cluster update was ever deferred, so the divert never ran and every assertion below is about the undiverted path");

        AssertPromotedTreesAgreeWithClusterState(dbe, cs, stage);
        AssertQueriesMatchStorageTruth(dbe, cs, stage);

        // Last: it seeds ClusterProcessBitmap and FenceBranchPath to rebuild the plan, so it must not run before the assertions that read live state.
        AssertPlanStraddlesAPromotedCell(dbe, cs, barrierOnly, stage);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // CA-02 — the defect this fixture turned up next door, and its regression guard
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>CA-02</c> — after a write-time AABB grow, a query must still find every entity. Promotion is off, the fence is serial, there is no runtime and no
    /// R-Tree, so this is about the ordinary cluster broadphase rather than anything #872 step 9 added.
    /// </summary>
    /// <remarks>
    /// <para><b>What it caught.</b> <c>ClusterAabbs</c> has two writers — <c>ClusterRef.MaybeGrowAndFlagShrink</c> grows it by CAS at WRITE time, and its own
    /// summary says a grow means "the cluster needs a fence-time <c>PerCellIndex.UpdateAt</c> with the fresh AABB" — while the per-cell index has one, the
    /// fence. The fence decided whether to write the index by comparing <c>stored</c> against <c>fresh</c>, both of which are the <c>ClusterAabbs</c> value:
    /// once the CAS had applied the grow they agreed, the fence took its <c>continue</c>, and the index kept the previous tick's smaller box. On the
    /// barrier-only branch it was not even a race — <c>fresh</c> is ASSIGNED from <c>stored</c> when no shrink is pending, so the comparison was a tautology
    /// and a grow-only tick could never update the index at all. Both demos run barrier-only.</para>
    /// <para><b>Pre-fix numbers, from this test.</b> 8 192 rotating entities, 50 serial fence ticks, no promotion. One cluster's authoritative bound was
    /// <c>x ∈ [2307.332, 3771.739]</c> while the linear index held <c>x ∈ [2309.087, 3770.604]</c> — strictly inside on every axis, one to two ticks stale. A
    /// query whose edge fell in the 1.8-unit gap pruned the cluster and lost the entity. Both slicing branches, identical result.</para>
    /// <para><b>Why the rotation, and why 50 ticks.</b> The gap only opens when a cluster's extreme moves OUTWARD without a shrink being flagged on the same
    /// axis in the same tick, and it has to open wide enough for a query edge to land inside it. A rotation produces both directions on every cluster every
    /// tick, and 50 of them lets the discrepancy accumulate past f32 noise. A single move would not reproduce it.</para>
    /// </remarks>
    [Test]
    [VerifiesRule("CA-02")]
    [CancelAfter(120_000)]
    public void CellIndexTracksClusterAabbs_AfterAWriteTimeGrow([Values(true, false)] bool barrierOnly)
    {
        ServiceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();

        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClCohPos>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(WorldExtent, WorldExtent), CellSize));
        dbe.InitializeArchetypes();

        if (barrierOnly)
        {
            dbe.SetSpatialBarrierOnly<ClCohUnit>();
        }

        SpawnPopulation(dbe);
        dbe.WriteTickFence(1);

        var cs = ClusterStateOf(dbe);
        Assert.That(cs.PromotedCellCount, Is.Zero, "this must run on the linear index — CA-02 is about the ordinary broadphase, not the promoted one");

        for (int t = 0; t < MotionTicks; t++)
        {
            using (var tx = dbe.CreateQuickTransaction())
            {
                RotateEveryEntity(tx, cs, dbe.SpatialGrid);
                tx.Commit();
            }
            dbe.WriteTickFence(2 + t);
        }

        AssertQueriesMatchStorageTruth(dbe, cs, barrierOnly ? "barrier-only" : "legacy");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // Population and motion
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    private static void SpawnPopulation(DatabaseEngine dbe)
    {
        var rng = new Random(872_904);
        using var tx = dbe.CreateQuickTransaction();
        for (int i = 0; i < EntityCount; i++)
        {
            // Round-robin over the four cells so cluster CREATION order interleaves them — which is what puts one cell's clusters into several slices of the
            // active list. Spawning cell by cell would make every slice single-cell and the divert would never be needed.
            int cell = i & 3;
            float ccx = (cell & 1) == 0 ? CellSize * 0.5f : CellSize * 1.5f;
            float ccy = (cell & 2) == 0 ? CellSize * 0.5f : CellSize * 1.5f;

            double angle = rng.NextDouble() * Math.PI * 2d;
            double radius = 200d + (rng.NextDouble() * 600d);   // 200..800, inside the cell's 1 000-unit inscribed circle
            tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(BoxAt(
                ccx + (float)(Math.Cos(angle) * radius),
                ccy + (float)(Math.Sin(angle) * radius))));
        }
        tx.Commit();
    }

    /// <summary>
    /// Rotate every entity about its own cluster's cell centre. Radius is preserved, so no entity crosses a cell boundary and the Migrate phase stays empty —
    /// see the fixture remarks on why that separation matters.
    /// </summary>
    private static unsafe void RotateEveryEntity(Transaction tx, ArchetypeClusterState cs, SpatialGrid grid)
    {
        float cos = MathF.Cos(ThetaPerTick);
        float sin = MathF.Sin(ThetaPerTick);

        var accessor = tx.For<ClCohUnit>();
        try
        {
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                int chunkId = cluster.ChunkId;
                if ((uint)chunkId >= (uint)cs.ClusterCellMap.Length)
                {
                    continue;
                }

                int cellKey = cs.ClusterCellMap[chunkId];
                if (cellKey < 0)
                {
                    continue;
                }

                grid.CellOrigin(cellKey, out double originX, out double originY, out _);
                float centreX = (float)(originX + (CellSize * 0.5f));
                float centreY = (float)(originY + (CellSize * 0.5f));

                // TYPHON009 flags un-barriered spatial mutation through a span. This span is READ only — every write below goes through WriteSpatial, which is
                // what keeps ClusterProcessBitmap and ClusterAabbs correct.
#pragma warning disable TYPHON009
                var positions = cluster.GetSpan(ClCohUnit.Pos);
#pragma warning restore TYPHON009

                ulong bits = cluster.OccupancyBits;
                while (bits != 0)
                {
                    int slotIndex = BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;

                    ref readonly var bounds = ref positions[slotIndex].Bounds;
                    float dx = (0.5f * (bounds.MinX + bounds.MaxX)) - centreX;
                    float dy = (0.5f * (bounds.MinY + bounds.MaxY)) - centreY;
                    cluster.WriteSpatial(ClCohUnit.Pos, slotIndex, BoxAt(
                        centreX + ((dx * cos) - (dy * sin)),
                        centreY + ((dx * sin) + (dy * cos))));
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // Assertions
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The shape the interleaving depends on, checked before the runtime starts rather than inferred afterwards from a green result.</summary>
    private static void AssertPreconditions(ArchetypeClusterState cs, bool barrierOnly)
    {
        int promoted = 0;
        for (int cellKey = 0; cellKey < cs.PerCellIndex.Length; cellKey++)
        {
            if (cs.PerCellIndex[cellKey]?.HasDynamicTree == true)
            {
                promoted++;
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(promoted, Is.GreaterThanOrEqualTo(2), "at least two cells must be promoted, or the run is about one tree and one worker");
            Assert.That(cs.ActiveClusterCount, Is.GreaterThan(64),
                barrierOnly
                    ? "the bitmap branch slices ONE WORD (64 clusters) per item; under 65 clusters it emits a single item and nothing can interleave"
                    : "the legacy branch slices 32 clusters per item; under 33 clusters it emits a single item and nothing can interleave");
        });
    }

    /// <summary>
    /// <c>AC-4</c>'s structural half: each promoted cell's tree holds exactly the clusters <c>ClusterCellMap</c> assigns to that cell, and the counts agree.
    /// </summary>
    /// <remarks>
    /// Membership is what an unsynchronised second writer destroys — a concurrent remove-and-reinsert loses an entry, duplicates one, or trips
    /// <c>RemoveChecked</c>'s identity check outright. Bound freshness would be the other half; see the fixture remarks for why it cannot be asserted yet.
    /// </remarks>
    private static void AssertPromotedTreesAgreeWithClusterState(DatabaseEngine dbe, ArchetypeClusterState cs, string stage)
    {
        var byCell = new Dictionary<int, HashSet<int>>();
        for (int i = 0; i < cs.ActiveClusterCount; i++)
        {
            int chunkId = cs.ActiveClusterIds[i];
            if ((uint)chunkId >= (uint)cs.ClusterCellMap.Length)
            {
                continue;
            }

            int cellKey = cs.ClusterCellMap[chunkId];
            if (cellKey < 0)
            {
                continue;
            }

            if (!byCell.TryGetValue(cellKey, out var set))
            {
                set = new HashSet<int>();
                byCell[cellKey] = set;
            }
            set.Add(chunkId);
        }

        int treesChecked = 0;
        long treeTotal = 0;
        long mappedTotal = 0;

        using (var epoch = EpochGuard.Enter(dbe.EpochManager))
        {
            for (int cellKey = 0; cellKey < cs.PerCellIndex.Length; cellKey++)
            {
                var tree = cs.PerCellIndex[cellKey]?.DynamicTree;
                if (tree == null)
                {
                    continue;
                }

                treesChecked++;
                var inTree = new HashSet<int>();
                foreach (int clusterChunkId in tree.EnumerateClusterIds())
                {
                    Assert.That(inTree.Add(clusterChunkId), Is.True, $"{stage}: cell {cellKey} holds cluster {clusterChunkId} twice");
                }

                var expected = byCell.TryGetValue(cellKey, out var e) ? e : new HashSet<int>();
                Assert.That(inTree, Is.EquivalentTo(expected), $"{stage}: cell {cellKey}'s tree and ClusterCellMap disagree on which clusters live there");
                Assert.That(tree.ClusterCount, Is.EqualTo(expected.Count), $"{stage}: cell {cellKey}'s ClusterCount does not match what the tree returns");

                treeTotal += tree.ClusterCount;
                mappedTotal += expected.Count;
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(treesChecked, Is.GreaterThanOrEqualTo(2), $"{stage}: promotion was undone during the run, so nothing above compared two trees");
            Assert.That(treeTotal, Is.EqualTo(mappedTotal), $"{stage}: summed tree cluster counts do not match the ClusterCellMap population");
        });
    }

    /// <summary>Query answers against the entity positions held in cluster storage — the freshness half of both tests above.</summary>
    /// <remarks>
    /// The truth set is read from the cluster segment directly — occupancy bits and the component span — so it shares no code with the spatial index it
    /// checks. Entities are identified by <c>(clusterChunkId, slotIndex)</c>, which both sides carry.
    /// </remarks>
    private static unsafe void AssertQueriesMatchStorageTruth(DatabaseEngine dbe, ArchetypeClusterState cs, string stage)
    {
        var truth = new List<(long key, float minX, float minY, float maxX, float maxY)>(EntityCount);
        using (var tx = dbe.CreateQuickTransaction())
        {
            var accessor = tx.For<ClCohUnit>();
            try
            {
                foreach (var cluster in accessor.GetClusterEnumerator())
                {
#pragma warning disable TYPHON009 // Read-only: this is the oracle, it must not mutate anything.
                    var positions = cluster.GetSpan(ClCohUnit.Pos);
#pragma warning restore TYPHON009
                    ulong bits = cluster.OccupancyBits;
                    while (bits != 0)
                    {
                        int slotIndex = BitOperations.TrailingZeroCount(bits);
                        bits &= bits - 1;
                        ref readonly var b = ref positions[slotIndex].Bounds;
                        truth.Add((SlotKey(cluster.ChunkId, slotIndex), b.MinX, b.MinY, b.MaxX, b.MaxY));
                    }
                }
            }
            finally
            {
                accessor.Dispose();
            }
        }

        Assert.That(truth, Has.Count.EqualTo(EntityCount), $"{stage}: cluster storage lost entities — every comparison below would share that blind spot");

        var rng = new Random(5150);
        for (int q = 0; q < 8; q++)
        {
            float minX = (float)(rng.NextDouble() * (WorldExtent - 900d));
            float minY = (float)(rng.NextDouble() * (WorldExtent - 900d));
            float maxX = minX + 900f;
            float maxY = minY + 900f;

            var expected = new HashSet<long>();
            foreach (var t in truth)
            {
                if (t.maxX >= minX && t.minX <= maxX && t.maxY >= minY && t.minY <= maxY)
                {
                    expected.Add(t.key);
                }
            }

            var actual = new HashSet<long>();
            using (var epoch = EpochGuard.Enter(dbe.EpochManager))
            {
                foreach (var r in cs.QueryAabb(dbe.SpatialGrid, minX, minY, float.NegativeInfinity, maxX, maxY, float.PositiveInfinity))
                {
                    actual.Add(SlotKey(r.ClusterChunkId, r.SlotIndex));
                }
            }

            Assert.That(expected, Is.Not.Empty, $"{stage}: query box {q} is empty in the truth set, so the comparison is between two empty sets");
            if (!actual.SetEquals(expected))
            {
                ReportStaleBound(dbe, cs, truth, expected, actual, q, minX, minY, maxX, maxY);
            }
            Assert.That(actual, Is.EquivalentTo(expected), $"{stage}: query box {q} disagrees with the positions held in cluster storage");
        }
    }

    /// <summary>Prints the authoritative bound beside the index's own, which is what makes the defect legible rather than a bare set difference.</summary>
    private static void ReportStaleBound(DatabaseEngine dbe, ArchetypeClusterState cs, List<(long key, float minX, float minY, float maxX, float maxY)> truth,
        HashSet<long> expected, HashSet<long> actual, int queryIndex, float minX, float minY, float maxX, float maxY)
    {
        var byKey = new Dictionary<long, (float minX, float minY, float maxX, float maxY)>();
        foreach (var t in truth)
        {
            byKey[t.key] = (t.minX, t.minY, t.maxX, t.maxY);
        }

        int shown = 0;
        foreach (long key in expected)
        {
            if (actual.Contains(key) || shown >= 4)
            {
                continue;
            }
            shown++;

            int chunkId = (int)(key >> 8);
            int cellKey = cs.ClusterCellMap[chunkId];
            var b = byKey[key];
            ref readonly var stored = ref cs.ClusterAabbs[chunkId];
            dbe.SpatialGrid.CellOrigin(cellKey, out double ox, out double oy, out _);

            TestContext.Out.WriteLine($"q{queryIndex} box=({minX:F3},{minY:F3})-({maxX:F3},{maxY:F3}) missing chunk={chunkId} slot={key & 255} "
                + $"entity=({b.minX:F3},{b.minY:F3})-({b.maxX:F3},{b.maxY:F3})");
            TestContext.Out.WriteLine($"    ClusterAabbs=({ox + stored.MinX:F3},{oy + stored.MinY:F3})-({ox + stored.MaxX:F3},{oy + stored.MaxY:F3})");

            var linear = cs.PerCellIndex[cellKey]?.DynamicIndex;
            if (linear != null)
            {
                int indexSlot = cs.ClusterSpatialIndexSlot[chunkId];
                TestContext.Out.WriteLine($"    cellIndex   =({ox + linear.MinX[indexSlot]:F3},{oy + linear.MinY[indexSlot]:F3})"
                    + $"-({ox + linear.MaxX[indexSlot]:F3},{oy + linear.MaxY[indexSlot]:F3})  <- what the broadphase prunes on");
            }
        }
    }

    /// <summary>
    /// The interleaving precondition, checked structurally rather than assumed: rebuild the AabbRefresh plan the run used and prove that some promoted cell's
    /// clusters land in two DIFFERENT chunks — which is exactly "two workers, one tree" for an undiverted implementation.
    /// </summary>
    /// <remarks>
    /// Rebuilt rather than captured, because the plan lives inside the phase's exec system and is rebuilt every tick. The inputs are reconstructed the way
    /// <c>FenceChunkSizingDiagnosticTests</c> does: seed the bitmap, set <c>FenceBranchPath</c>, and call <c>Build</c> with the same cost model, worker count
    /// and oversubscription the run configured. It mutates that bookkeeping, so it runs after every assertion that reads live state.
    /// </remarks>
    private static void AssertPlanStraddlesAPromotedCell(DatabaseEngine dbe, ArchetypeClusterState cs, bool barrierOnly, string stage)
    {
        var meta = Archetype<ClCohUnit>.Metadata;

        // A full-motion tick dirties every active cluster, which is what the run produced on all 50 ticks.
        cs.EnsureClusterWriteBookkeepingCapacity(cs.PrimarySegmentCapacity + 64);
        var bitmap = cs.ClusterProcessBitmap;
        Array.Clear(bitmap);
        int dirty = 0;
        for (int i = 0; i < cs.ActiveClusterCount; i++)
        {
            int chunkId = cs.ActiveClusterIds[i];
            if ((uint)(chunkId >> 6) >= (uint)bitmap.Length)
            {
                continue;
            }
            bitmap[chunkId >> 6] |= 1L << (chunkId & 63);
            dirty++;
        }

        cs.FenceProcessBitmapClusterCount = dirty;
        cs.FenceBranchPath = 1;

        var plan = new FenceWorkPlan();
        plan.Build(FencePhase.AabbRefresh, dbe,
            new LiveFenceCostModel(new FenceCostModel(MigrationCost: 33.3f, AabbCost: PlannerAabbCostUs, ShadowCost: 1f, SpatialCost: 1f)),
            WorkerCount, chunkOversubscription: 2);

        Assert.That(plan.ChunkCount, Is.GreaterThanOrEqualTo(2), $"{stage}: the phase planned a single chunk, so nothing in the run ran concurrently");

        // cellKey -> the set of chunks carrying at least one of its clusters.
        var chunksPerCell = new Dictionary<int, HashSet<int>>();
        for (int chunk = 0; chunk < plan.ChunkCount; chunk++)
        {
            int start = plan.ChunkStart[chunk];
            int end = start + plan.ChunkItemCnt[chunk];
            for (int i = start; i < end; i++)
            {
                ref readonly var item = ref plan.Items[i];
                if (item.Kind != FenceWorkKind.AabbRefreshSlice || item.TargetId != meta.ArchetypeId)
                {
                    continue;
                }

                foreach (int chunkId in ClustersInSlice(cs, barrierOnly, item.SliceStart, item.SliceCount))
                {
                    int cellKey = cs.ClusterCellMap[chunkId];
                    if (cellKey < 0 || cs.PerCellIndex[cellKey]?.HasDynamicTree != true)
                    {
                        continue;
                    }

                    if (!chunksPerCell.TryGetValue(cellKey, out var set))
                    {
                        set = new HashSet<int>();
                        chunksPerCell[cellKey] = set;
                    }
                    set.Add(chunk);
                }
            }
        }

        int straddled = 0;
        foreach (var pair in chunksPerCell)
        {
            if (pair.Value.Count >= 2)
            {
                straddled++;
            }
        }

        Assert.That(straddled, Is.GreaterThan(0),
            $"{stage}: no promoted cell had its clusters split across two chunks, so the run never put two workers in one tree and AC-4 proved nothing");
        TestContext.Out.WriteLine($"AC-4 {stage}: chunks={plan.ChunkCount} promotedCellsStraddlingChunks={straddled} dirtyClusters={dirty}");
    }

    /// <summary>The cluster ids one AabbRefresh slice carries, on whichever axis the archetype's mode slices.</summary>
    private static List<int> ClustersInSlice(ArchetypeClusterState cs, bool barrierOnly, int sliceStart, int sliceCount)
    {
        var ids = new List<int>();
        if (barrierOnly)
        {
            var bitmap = cs.ClusterProcessBitmap;
            int end = Math.Min(sliceStart + sliceCount, bitmap.Length);
            for (int word = sliceStart; word < end; word++)
            {
                ulong bits = (ulong)bitmap[word];
                while (bits != 0)
                {
                    int bit = BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    ids.Add((word << 6) + bit);
                }
            }
            return ids;
        }

        int activeEnd = Math.Min(sliceStart + sliceCount, cs.ActiveClusterCount);
        for (int i = sliceStart; i < activeEnd; i++)
        {
            ids.Add(cs.ActiveClusterIds[i]);
        }
        return ids;
    }
}
