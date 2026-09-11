using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// The engine picks a cell's broadphase from that cell's own density, and changes its mind when the density changes — including while the parallel fence is
/// migrating clusters between cells.
/// </summary>
/// <remarks>
/// <para><b>What was missing, and why it was not a small gap.</b> Both structures were implemented and separately tested, and the threshold that chooses
/// between them shipped defaulted to <see cref="int.MaxValue"/> with no writer anywhere in the engine — so no database ever promoted a cell, and the R-Tree
/// was code that could not be reached from a supported configuration. Raising the threshold was not enough either: a startup guard refused promotion
/// together with the parallel fence, which is the default runtime, and it was right to. The fix is upstream of both — the shared per-archetype arrays a
/// Migrate worker indexes through no longer reallocate under its siblings — so this fixture asserts the OUTCOME rather than the plumbing: a cell that fills
/// up ends on a tree, a cell that empties out ends on a scan, and neither transition changes the answer to a query.</para>
/// <para><b>Transitions are observed as they happen, not inferred at the end.</b> A final-state assertion passes just as well against an engine that promoted
/// once at spawn and never re-evaluated, which is close to the behaviour being fixed. <see cref="ShapePerTick"/> records the discriminator on every tick, so
/// the assertions can say WHERE the switch happened and that nothing switched before it should have.</para>
/// <para><b>Which test covers what, by ablation.</b> Reverting the pre-size to where it used to sit reddens
/// <see cref="PromotionSurvivesTheParallelFence_WhileClustersMigrateBetweenCells"/> by name — the growth refusal fires from a Migrate slice, which is how the
/// mis-placed pre-size was found. But reverting the REFUSAL as well leaves that test green, because with the growers serialised on the latch two of them can
/// no longer drop each other's copy, and what remains is a sibling holding a stale reference — an interleaving no test can force on demand. So that test
/// proves the configuration runs, not that the guard works.
/// <see cref="AShortPerArchetypeArrayAbortsTheTickInsteadOfGrowingUnderSiblingWorkers"/> is the one that proves the guard: it falsifies the bound directly
/// and is red with the guard removed, green with it present.</para>
/// <para><b>Cluster counts, not entity counts.</b> A cluster holds up to 64 entities of one archetype in one cell, so the thresholds here are two orders of
/// magnitude below the shipped default — a fixture that used the production number would spend its time spawning a quarter of a million entities to prove
/// something the small number proves exactly as well.</para>
/// </remarks>
[TestFixture]
// Every test here drives the fixture's ONE database file, and two of them run a whole runtime against it. Without this NUnit is free to overlap them and the
// loser fails inside TestBase.Setup, deleting a file the winner still has mapped — a failure that says nothing about promotion.
[NonParallelizable]
class CellTreeDensityTransitionTests : TestBase<CellTreeDensityTransitionTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    private const float CellSize = 1_000f;
    private const float WorldExtent = 4_000f;

    /// <summary>Promote at 24 clusters, so the fall-back is at 12 and both edges are reachable with a few thousand entities.</summary>
    private const int PromoteAt = 24;

    private const int DemoteAt = PromoteAt / 2;

    /// <summary>64 entities fill a cluster, so this is ~47 clusters in one cell — comfortably past <see cref="PromoteAt"/>.</summary>
    private const int DenseEntityCount = 3_000;

    private static ClCohPos PointAt(float x, float y) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Mass = 1.0f };

    private static ArchetypeClusterState ClusterStateOf(DatabaseEngine dbe) =>
        dbe._archetypeStates[Archetype<ClCohUnit>.Metadata.ArchetypeId].ClusterState;

    private static long SlotKey(int clusterChunkId, int slotIndex) => ((long)clusterChunkId << 8) | (uint)slotIndex;

    /// <summary>One tick's observation of a cell half: how many clusters it holds and which structure is holding them.</summary>
    private readonly record struct ShapePerTick(int Tick, int ClusterCount, bool IsTree);

    private DatabaseEngine SetupEngine(IServiceScope scope, int promoteThreshold)
    {
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClCohPos>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(WorldExtent, WorldExtent), CellSize));
        dbe.ClusterCellTreePromoteThreshold = promoteThreshold;
        // Step 16: these fixtures exercise the TREE, not the gate that decides when a cell gets one — their clusters are scattered over the cell
        // on purpose, which is exactly the shape the tightness gate refuses. Count-only promotion keeps them testing what they were written for.
        dbe.ClusterCellTreePromoteTightness = 1f;
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>The dynamic half of the cell containing (x, y): its cluster count and whether a tree is holding it.</summary>
    private static ShapePerTick ObserveCell(DatabaseEngine dbe, ArchetypeClusterState cs, int tick, float x, float y)
    {
        int cellKey = dbe.SpatialGrid.WorldToCellKey(x, y, 0f);
        if (cellKey < 0 || cs.PerCellIndex == null || cellKey >= cs.PerCellIndex.Length || cs.PerCellIndex[cellKey] == null)
        {
            return new ShapePerTick(tick, 0, false);
        }

        var slot = cs.PerCellIndex[cellKey];
        return slot.HasDynamicTree
            ? new ShapePerTick(tick, slot.DynamicTree.ClusterCount, true)
            : new ShapePerTick(tick, slot.DynamicIndex?.ClusterCount ?? 0, false);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // The switch, in both directions, on the serial fence
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A cell filling up crosses onto the tree; the same cell emptying out crosses back onto the scan. Both edges are located exactly, and a query is
    /// compared against storage on every tick so a switch that changes the ANSWER fails at the tick it happened rather than at the end of the run.
    /// </summary>
    /// <remarks>
    /// The population is added and removed in batches of 128 entities — two clusters' worth — so the cluster count moves in small steps through both
    /// thresholds instead of jumping over them. Everything lands in cell (0,0): one cell crossing a threshold is the unit under test, and spreading the
    /// population would only make each cell cross later.
    /// </remarks>
    [Test]
    [CancelAfter(120_000)]
    public void ACellPromotesAsItFillsAndDemotesAsItEmpties()
    {
        ServiceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        using var scope = ServiceProvider.CreateScope();
        using var dbe = SetupEngine(scope, PromoteAt);
        var cs = ClusterStateOf(dbe);

        const int batch = 128;
        var live = new List<EntityId>(DenseEntityCount);
        var shapes = new List<ShapePerTick>();
        var rng = new Random(20260906);
        int tick = 0;

        // ── Fill ──────────────────────────────────────────────────────────────────────────────────────────────────
        while (live.Count < DenseEntityCount)
        {
            using (var tx = dbe.CreateQuickTransaction())
            {
                for (int i = 0; i < batch; i++)
                {
                    float x = 1f + ((float)rng.NextDouble() * (CellSize - 2f));
                    float y = 1f + ((float)rng.NextDouble() * (CellSize - 2f));
                    live.Add(tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(x, y))));
                }
                tx.Commit();
            }

            dbe.WriteTickFence(++tick);
            shapes.Add(ObserveCell(dbe, cs, tick, 1f, 1f));
            AssertQueryMatchesStorage(dbe, cs, $"fill tick {tick}");
        }

        int fillTicks = shapes.Count;
        Assert.That(shapes[^1].IsTree, Is.True,
            $"the cell reached {shapes[^1].ClusterCount} clusters against a threshold of {PromoteAt} and is still on the linear scan — the engine never "
            + "promoted it, which is the state this fixture exists to rule out");

        // ── Drain ─────────────────────────────────────────────────────────────────────────────────────────────────
        while (live.Count > 0)
        {
            using (var tx = dbe.CreateQuickTransaction())
            {
                int take = Math.Min(batch, live.Count);
                for (int i = 0; i < take; i++)
                {
                    tx.Destroy(live[^1]);
                    live.RemoveAt(live.Count - 1);
                }
                tx.Commit();
            }

            dbe.WriteTickFence(++tick);
            shapes.Add(ObserveCell(dbe, cs, tick, 1f, 1f));
            AssertQueryMatchesStorage(dbe, cs, $"drain tick {tick}");
        }

        Assert.That(shapes[^1].IsTree, Is.False, "the cell emptied out and never fell back to the linear scan");

        // ── Where the edges actually were ─────────────────────────────────────────────────────────────────────────
        int promotedAt = shapes.FindIndex(s => s.IsTree);
        int demotedAt = shapes.FindLastIndex(s => s.IsTree);

        // The two invariants that matter, stated over the WHOLE trace rather than at the edges. An edge assertion compares a count sampled after the fence
        // against a threshold the fence evaluated mid-flight, and those legitimately differ: promotion is decided when a cluster is INSERTED, and the same
        // fence can then finalise clusters that emptied out, so a cell can be observed as a 16-cluster tree having promoted at 24. Measured, not assumed —
        // the edge form of this assertion failed exactly that way. Band is one tick's batch of spawns (128 entities ≈ 2 clusters) doubled.
        const int band = 4;

        Assert.Multiple(() =>
        {
            Assert.That(promotedAt, Is.GreaterThan(0), "the cell was already a tree on its first observation, so nothing was observed CROSSING the threshold");
            Assert.That(demotedAt, Is.GreaterThanOrEqualTo(fillTicks), "the cell fell back to the scan while it was still FILLING");
            Assert.That(demotedAt, Is.LessThan(shapes.Count - 1), "the run ended on a tree, so the fall-back edge was never observed");

            foreach (var s in shapes)
            {
                if (!s.IsTree && s.ClusterCount >= PromoteAt + band)
                {
                    Assert.Fail(
                        $"tick {s.Tick}: the cell held {s.ClusterCount} clusters — past the {PromoteAt} threshold — and was still on the linear scan");
                }

                if (s.IsTree && s.ClusterCount > 0 && s.ClusterCount <= DemoteAt - band)
                {
                    Assert.Fail($"tick {s.Tick}: the cell held {s.ClusterCount} clusters — under the {DemoteAt} fall-back — and was still on the tree");
                }
            }

            // The gap is the whole reason two thresholds exist. Without it a cell sitting on the boundary rebuilds itself in both directions on alternating
            // ticks, and the O(C) rebuild that is supposed to happen once per crossing happens every tick instead.
            int flips = 0;
            for (int i = 1; i < shapes.Count; i++)
            {
                if (shapes[i].IsTree != shapes[i - 1].IsTree)
                {
                    flips++;
                }
            }
            Assert.That(flips, Is.EqualTo(2), $"expected exactly one promotion and one fall-back over the run, saw {flips} shape changes");
        });

        TestContext.Out.WriteLine($"promoted at tick {shapes[promotedAt].Tick} ({shapes[promotedAt].ClusterCount} clusters), "
            + $"fell back at tick {shapes[demotedAt + 1].Tick} ({shapes[demotedAt + 1].ClusterCount} clusters)");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // The configuration the startup guard used to refuse: promotion + parallel fence + real migrations
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Promotion under the parallel fence, with entities crossing cell boundaries every tick so the Migrate phase runs with several workers while promoted
    /// trees are live.
    /// </summary>
    /// <remarks>
    /// <para><b>This is the combination that used to be refused at startup, and the refusal was correct.</b> A Migrate worker inserting a cluster into its
    /// destination cell's index reaches <c>AddClusterToPerCellIndex</c>, which grew four per-ARCHETYPE arrays with a bare <c>Array.Resize</c> — cell-disjoint
    /// slicing does not protect an array indexed by cluster chunk id. Every sibling worker holding the previous reference then wrote into an abandoned copy.
    /// For <c>ClusterSpatialIndexSlot</c> it was worse still: that array doubles as every cell tree's back-pointer store, so the resize dragged a rebind of
    /// every live tree behind it while other workers were mutating those trees.</para>
    /// <para><b>Migrations have to be REAL, and the test proves they were.</b> Entities drift along +X far enough to leave their cell, which is what makes
    /// the fence produce migration requests at all; a rotation inside one cell — which is what the sibling fixture does deliberately — exercises none of
    /// this. <c>TotalMigrationCount</c> and <c>PromotedCellCount</c> are both asserted non-zero, because a run that migrated nothing or promoted nothing
    /// would pass every correctness check below while testing neither.</para>
    /// </remarks>
    [Test]
    [VerifiesRule("MD-02")]
    [CancelAfter(180_000)]
    public void PromotionSurvivesTheParallelFence_WhileClustersMigrateBetweenCells()
    {
        ServiceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        using var scope = ServiceProvider.CreateScope();
        using var dbe = SetupEngine(scope, PromoteAt);
        var cs = ClusterStateOf(dbe);

        // Two columns of cells along X, densely packed so the leading column promotes, and drifting so entities cross into the next column.
        var rng = new Random(4242);
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < DenseEntityCount; i++)
            {
                float x = 1f + ((float)rng.NextDouble() * (CellSize - 2f));
                float y = 1f + ((float)rng.NextDouble() * ((2f * CellSize) - 2f));
                tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(x, y)));
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        Assert.That(cs.PromotedCellCount, Is.GreaterThan(0),
            "the seed population did not promote any cell, so the run below would exercise the parallel fence against linear indexes only");

        const int motionTicks = 40;
        int ticks = 0;
        int stopping = 0;
        Exception unhandled = null;
        Exception teardown = null;

        // Sampled from the public track, which runs AHEAD of this tick's fence, so each sample describes the PREVIOUS tick. Endpoint reads of the cumulative
        // counters cannot say this: TotalMigrationCount is bumped by the serial path too and PromotedCellCount is archetype-wide, so a pair of non-zero totals
        // is equally satisfied by a run whose migrations all landed on ticks with nothing promoted, or by one where EnableParallelFence quietly stopped taking
        // effect. What has to be true is that some ONE tick migrated clusters, across MORE THAN ONE Migrate work item packed into more than one chunk, while a
        // promoted tree was live.
        int concurrentTicks = 0;
        int maxMigrateItems = 0;
        int maxMigrateChunks = 0;
        long lastMigrations = cs.TotalMigrationCount;
        TyphonRuntime runtimeRef = null;

        using (var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Drift").CallbackSystem("Drift", ctx =>
            {
                var plan = runtimeRef?.FenceMigrateExec?.PlanForTest;
                if (plan != null)
                {
                    var items = 0;
                    for (var i = 0; i < plan.ItemCount; i++)
                    {
                        if (plan.Items[i].Kind == FenceWorkKind.MigrationApply && plan.Items[i].TargetId == Archetype<ClCohUnit>.Metadata.ArchetypeId)
                        {
                            items++;
                        }
                    }

                    var migrations = cs.TotalMigrationCount;
                    if (items > maxMigrateItems)
                    {
                        maxMigrateItems = items;
                    }
                    if (plan.ChunkCount > maxMigrateChunks)
                    {
                        maxMigrateChunks = plan.ChunkCount;
                    }
                    if (items >= 2 && plan.ChunkCount >= 2 && migrations > lastMigrations && cs.PromotedCellCount > 0)
                    {
                        concurrentTicks++;
                    }
                    lastMigrations = migrations;
                }

                DriftEveryEntity(ctx.Transaction);
                Interlocked.Increment(ref ticks);
            });
        }, new RuntimeOptions
        {
            WorkerCount = 4,
            // 100 Hz for the same measured reason as the sibling fixture: a tick that rewrites every spatial field and drains a promoted cell's deferrals
            // does not fit a 1 ms budget, and an overrun makes two ticks share one system transaction.
            BaseTickRate = 100,
            EnableParallelFence = true,
        }))
        {
            runtimeRef = runtime;
            runtime.Scheduler.UnhandledExceptionCallback = (_, _, ex) =>
            {
                // The teardown window forgives the ObjectDisposedException / affinity faults a tick abandoned mid-dispatch produces, and nothing else. The
                // growth refusal this change adds raises from a Migrate slice and could land in that window like any other fence fault; downgrading it to a
                // console line would make this fixture green on precisely the failure it exists to catch, so it is matched by message and always fatal.
                if (Volatile.Read(ref stopping) != 0 && ex.Message.IndexOf("Migrate slice", StringComparison.Ordinal) < 0)
                {
                    Interlocked.CompareExchange(ref teardown, ex, null);
                    return;
                }
                Interlocked.CompareExchange(ref unhandled, ex, null);
            };
            runtime.Start();
            SpinWait.SpinUntil(() => Volatile.Read(ref ticks) >= motionTicks, TimeSpan.FromSeconds(90));
            int reached = Volatile.Read(ref ticks);
            SpinWait.SpinUntil(() => Volatile.Read(ref ticks) >= reached + 3, TimeSpan.FromSeconds(10));
            Volatile.Write(ref stopping, 1);
            runtime.Shutdown();
        }

        if (teardown != null)
        {
            TestContext.Out.WriteLine($"ignored a teardown-window fault — {teardown.GetType().Name}: {teardown.Message}");
        }

        // A fence phase that throws now reaches the host rather than being swallowed, so this assertion is load-bearing: the growth refusal added with this
        // fix raises from inside a Migrate slice, and it would surface exactly here.
        Assert.That(unhandled, Is.Null, $"a fence phase threw while promotion and the parallel fence ran together — {unhandled}");
        Assert.That(Volatile.Read(ref ticks), Is.GreaterThanOrEqualTo(motionTicks), "the runtime did not complete the motion ticks");
        Assert.That(cs.TotalMigrationCount, Is.GreaterThan(0),
            "no cluster ever changed cell, so the Migrate phase never ran and this test proves nothing about it");
        Assert.That(cs.PromotedCellCount, Is.GreaterThan(0), "promotion was undone during the run, so the assertions below are about linear indexes");
        Assert.That(concurrentTicks, Is.GreaterThan(0),
            $"no single tick migrated clusters across >=2 Migrate work items in >=2 chunks while a cell was promoted (peak {maxMigrateItems} items, "
            + $"{maxMigrateChunks} chunks) — the Migrate phase never fanned out alongside a live tree, so nothing here exercised the hazard");

        AssertPromotedTreesMatchCellMap(dbe, cs);
        AssertQueryMatchesStorage(dbe, cs, "after parallel fence with migrations");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // The threshold is configuration, and configuration reaches the engine
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The same population promotes or does not, decided only by <see cref="SpatialOptions.CellTreePromoteThreshold"/>.
    /// </summary>
    /// <remarks>
    /// Both arms run the identical world; the only difference is the configured number. The high arm doubles as the guarantee that promotion remains
    /// switchable off — a database whose workload the default does not suit is not stuck with it.
    /// </remarks>
    [Test]
    [CancelAfter(120_000)]
    // A bool rather than the two ints it selects: NUnit builds the per-case database name out of the argument text, and "int.MaxValue" carries a dot that
    // ManagedPagedMMFOptions rejects as a file name — the case then fails in SetUp having tested nothing.
    public void TheConfiguredThresholdDecidesWhetherACellPromotes([Values(true, false)] bool promotionEnabled)
    {
        int threshold = promotionEnabled ? PromoteAt : int.MaxValue;

        ServiceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        using var scope = ServiceProvider.CreateScope();
        using var dbe = SetupEngine(scope, threshold);
        var cs = ClusterStateOf(dbe);

        var rng = new Random(999);
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < DenseEntityCount; i++)
            {
                float x = 1f + ((float)rng.NextDouble() * (CellSize - 2f));
                float y = 1f + ((float)rng.NextDouble() * (CellSize - 2f));
                tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(x, y)));
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        var shape = ObserveCell(dbe, cs, 1, 1f, 1f);
        Assert.That(shape.ClusterCount, Is.GreaterThan(PromoteAt),
            "the population did not fill the cell past the low threshold, so the two arms are not distinguishable");
        Assert.That(shape.IsTree, Is.EqualTo(promotionEnabled),
            promotionEnabled
                ? "a configured threshold the cell exceeded did not promote it"
                : "int.MaxValue must keep every cell on the linear scan whatever its density");

        // Whichever structure it chose, it must answer the same question.
        AssertQueryMatchesStorage(dbe, cs, $"threshold {threshold}");
    }

    /// <summary>
    /// Crossing the threshold repeatedly must not grow the archetype's shared cell-tree segment without bound.
    /// </summary>
    /// <remarks>
    /// <para>A promoted cell's R-Tree is built out of chunks of one <c>ChunkBasedSegment</c> shared by every cell of the archetype, and that segment is
    /// TRANSIENT — heap-backed, no GC watching it, nothing that reclaims a structure whose last reference was dropped. So falling back to the linear index by
    /// publishing the index and nulling the tree, which is the obvious way to write it, strands that cell's entire node set for the life of the database.
    /// A cell oscillating across the threshold does it once per crossing.</para>
    /// <para>The assertion is on the SEGMENT rather than on any count the demotion path maintains itself, because a bookkeeping counter would be wrong in
    /// exactly the same way the leak is. Three full promote/fall-back cycles must leave the segment no larger than one did.</para>
    /// </remarks>
    [Test]
    [CancelAfter(120_000)]
    public void CrossingTheThresholdRepeatedlyDoesNotGrowTheCellTreeSegment()
    {
        ServiceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        using var scope = ServiceProvider.CreateScope();
        using var dbe = SetupEngine(scope, PromoteAt);
        var cs = ClusterStateOf(dbe);

        var rng = new Random(31337);
        var tick = 0;
        var afterFirstCycle = -1;

        for (var cycle = 0; cycle < 3; cycle++)
        {
            var live = new List<EntityId>(DenseEntityCount);
            using (var tx = dbe.CreateQuickTransaction())
            {
                for (var i = 0; i < DenseEntityCount; i++)
                {
                    var x = 1f + ((float)rng.NextDouble() * (CellSize - 2f));
                    var y = 1f + ((float)rng.NextDouble() * (CellSize - 2f));
                    live.Add(tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(x, y))));
                }
                tx.Commit();
            }
            dbe.WriteTickFence(++tick);
            Assert.That(ObserveCell(dbe, cs, tick, 1f, 1f).IsTree, Is.True, $"cycle {cycle}: the cell did not promote, so no tree was built to leak");

            using (var tx = dbe.CreateQuickTransaction())
            {
                foreach (var id in live)
                {
                    tx.Destroy(id);
                }
                tx.Commit();
            }
            dbe.WriteTickFence(++tick);
            Assert.That(ObserveCell(dbe, cs, tick, 1f, 1f).IsTree, Is.False, $"cycle {cycle}: the cell did not fall back, so nothing was released");

            var allocated = cs.CellTreeSegment?.AllocatedChunkCount ?? 0;
            TestContext.Out.WriteLine($"cycle {cycle}: cell-tree segment holds {allocated} allocated chunks after fall-back");
            if (cycle == 0)
            {
                afterFirstCycle = allocated;
            }
            else
            {
                Assert.That(allocated, Is.LessThanOrEqualTo(afterFirstCycle),
                    $"cycle {cycle} left {allocated} chunks allocated against {afterFirstCycle} after the first — each promote/fall-back cycle is stranding "
                    + "the tree's nodes in the shared segment");
            }
        }
    }

    /// <summary>
    /// A per-archetype array that is too short when the Migrate phase starts must abort the tick loudly, not be grown under sibling workers.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this test has to falsify the pre-size rather than trust it.</b> The refusal only fires when the pre-size is wrong, so a fixture that runs
    /// an ordinary tick observes nothing — every other test here passes identically with the guard live and with it removed. The bound is deliberately
    /// falsified: <see cref="ArchetypeClusterState.PrepQueueProbe"/> fires at the very end of Prep, after the pre-size and before any Migrate worker starts,
    /// so shrinking the array there reproduces exactly the state a wrong bound would leave.</para>
    /// <para><b>What the failure must look like.</b> Not a crash and not a silently dropped index entry: the fence phase throws, the runtime publishes
    /// <c>TickOutcomeReason.FenceFailure</c>, and the message names the array. Replace the guard's <c>Enter</c> with <c>Exit</c> and the tick succeeds, which
    /// is what makes this the one assertion in the fixture that distinguishes a live guard from an absent one.</para>
    /// </remarks>
    [Test]
    [VerifiesRule("MD-02")]
    [CancelAfter(120_000)]
    public void AShortPerArchetypeArrayAbortsTheTickInsteadOfGrowingUnderSiblingWorkers()
    {
        ServiceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        using var scope = ServiceProvider.CreateScope();
        using var dbe = SetupEngine(scope, PromoteAt);
        var cs = ClusterStateOf(dbe);

        var rng = new Random(777);
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < DenseEntityCount; i++)
            {
                var x = 1f + ((float)rng.NextDouble() * (CellSize - 2f));
                var y = 1f + ((float)rng.NextDouble() * ((2f * CellSize) - 2f));
                tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(x, y)));
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        var ticks = 0;
        Exception seen = null;
        var armed = 0;

        try
        {
            // Shrinks the array on the first tick that actually has migrations to apply — an earlier tick would leave the Migrate phase with nothing to do
            // and the guard would never be consulted.
            ArchetypeClusterState.PrepQueueProbe = (state, _) =>
            {
                if (state != cs || state.PendingMigrationCount == 0 || Interlocked.Exchange(ref armed, 1) != 0)
                {
                    return;
                }

                state.ClusterSpatialIndexSlot = new int[8];
            };

            using var runtime = TyphonRuntime.Create(dbe, schedule =>
            {
                schedule.PublicTrack.DeclareDag("Drift").CallbackSystem("Drift", ctx =>
                {
                    DriftEveryEntity(ctx.Transaction);
                    Interlocked.Increment(ref ticks);
                });
            }, new RuntimeOptions { WorkerCount = 4, BaseTickRate = 100, EnableParallelFence = true });

            runtime.Scheduler.UnhandledExceptionCallback = (_, _, ex) => Interlocked.CompareExchange(ref seen, ex, null);
            runtime.Start();
            SpinWait.SpinUntil(() => Volatile.Read(ref seen) != null, TimeSpan.FromSeconds(30));
            runtime.Shutdown();
        }
        finally
        {
            ArchetypeClusterState.PrepQueueProbe = null;
        }

        Assert.That(armed, Is.EqualTo(1), "the probe never fired on a tick with pending migrations, so the bound was never falsified");
        Assert.That(seen, Is.Not.Null,
            "a Migrate slice found ClusterSpatialIndexSlot too short and did not raise — it grew the array under its sibling workers instead, which is the "
            + "silent index loss MD-02 forbids");
        Assert.That(seen.Message, Does.Contain("ClusterSpatialIndexSlot").And.Contains("Migrate slice"),
            $"the tick failed for some other reason: {seen}");
    }

    /// <summary>
    /// Rebuilding the per-cell index after cells have promoted must return their trees' chunks, not strand them.
    /// </summary>
    /// <remarks>
    /// <para>The rebuild paths clear <c>PerCellIndex</c> wholesale and reset the promoted-cell counter, which drops the last reference to every tree. On a
    /// transient segment that reclaims nothing. It is reachable because the rebuild itself PROMOTES — it adds every cluster to its cell, and each of those
    /// insertions evaluates the threshold — so a second rebuild discards what the first one built.</para>
    /// <para>Three rebuilds, because one proves nothing: the first runs against an index that was already populated by the spawn path, and only the second
    /// onwards discard trees the rebuild itself created.</para>
    /// </remarks>
    [Test]
    [CancelAfter(120_000)]
    public void RebuildingThePerCellIndexReleasesTheTreesItDiscards()
    {
        ServiceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        using var scope = ServiceProvider.CreateScope();
        using var dbe = SetupEngine(scope, PromoteAt);
        var cs = ClusterStateOf(dbe);

        var rng = new Random(24680);
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < DenseEntityCount; i++)
            {
                var x = 1f + ((float)rng.NextDouble() * (CellSize - 2f));
                var y = 1f + ((float)rng.NextDouble() * (CellSize - 2f));
                tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(x, y)));
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);
        Assert.That(ObserveCell(dbe, cs, 1, 1f, 1f).IsTree, Is.True, "the seed population did not promote, so no tree exists to be discarded");

        var afterFirst = -1;
        for (var pass = 0; pass < 3; pass++)
        {
            // The production callers (InitializeArchetypes, RebuildClusterFromChains) run inside an epoch scope; the method assumes one and asserts on it.
            using (EpochGuard.Enter(dbe.EpochManager))
            {
                cs.RebuildSpatialStateFromData(dbe.SpatialGrid, dbe.EpochManager);
            }

            Assert.That(ObserveCell(dbe, cs, pass, 1f, 1f).IsTree, Is.True, $"pass {pass}: the rebuild did not re-promote, so later passes discard nothing");
            AssertQueryMatchesStorage(dbe, cs, $"rebuild pass {pass}");

            var allocated = cs.CellTreeSegment?.AllocatedChunkCount ?? 0;
            TestContext.Out.WriteLine($"rebuild pass {pass}: cell-tree segment holds {allocated} allocated chunks");
            if (pass == 0)
            {
                afterFirst = allocated;
            }
            else
            {
                Assert.That(allocated, Is.LessThanOrEqualTo(afterFirst),
                    $"pass {pass} left {allocated} chunks allocated against {afterFirst} after the first — each rebuild is stranding the trees it discards");
            }
        }
    }

    /// <summary>The shipped default is what an engine built from unconfigured options actually carries.</summary>
    [Test]
    public void TheDefaultThresholdIsTheOneTheOptionsDeclare()
    {
        ServiceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();

        Assert.That(dbe.ClusterCellTreePromoteThreshold, Is.EqualTo(SpatialOptions.DefaultCellTreePromoteThreshold));
        // Reversed on 2026-09-10 (#917): on #906's workload a forced tree made the tick 1.43-1.75x slower at every density measured, so no cell promotes
        // unless the application sets a count. The rest of this fixture sets its own threshold and is untouched by that.
        Assert.That(SpatialOptions.DefaultCellTreePromoteThreshold, Is.EqualTo(int.MaxValue),
            "the default promotes nothing: the per-cell tree lost the tick on every workload measured");
    }

    /// <summary>
    /// <c>ForceCellHalfStructure</c> — the in-place switch <c>CellTreeCrossoverProfile</c> (#917) times the two structures with — changes a cell half's
    /// structure and never its answer, in both directions and repeatedly, on an archetype whose own gate is off.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public void ForcingTheStructureChangesTheShapeNotTheAnswer()
    {
        ServiceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        using var scope = ServiceProvider.CreateScope();
        using var dbe = SetupEngine(scope, int.MaxValue);
        var cs = ClusterStateOf(dbe);
        var cellKey = dbe.SpatialGrid.WorldToCellKey(1f, 1f, 0f);

        var rng = new Random(917);
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < DenseEntityCount; i++)
            {
                var x = 1f + ((float)rng.NextDouble() * (CellSize - 2f));
                var y = 1f + ((float)rng.NextDouble() * (CellSize - 2f));
                tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(x, y)));
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);
        Assert.That(ObserveCell(dbe, cs, 1, 1f, 1f).IsTree, Is.False, "the gate is off, so the cell must start on the linear scan");

        for (var round = 0; round < 3; round++)
        {
            foreach (var tree in new[] { true, false })
            {
                bool switched;
                using (EpochGuard.Enter(dbe.EpochManager))
                {
                    switched = cs.ForceCellHalfStructure(cellKey, tree);
                }

                var stage = $"round {round}, forced onto the {(tree ? "tree" : "linear scan")}";
                Assert.That(switched, Is.True, $"{stage}: the switch was refused");
                Assert.That(ObserveCell(dbe, cs, round, 1f, 1f).IsTree, Is.EqualTo(tree), $"{stage}: the half is on the other structure");
                Assert.That(cs.PromotedCellCount, Is.EqualTo(tree ? 1 : 0), $"{stage}: the promoted-cell count does not match the structure");
                AssertQueryMatchesStorage(dbe, cs, stage);
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // Motion and oracles
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Pushes every entity along +X, wrapping at the world edge, so clusters leave their cell and the fence has migrations to execute.</summary>
    private static void DriftEveryEntity(Transaction tx)
    {
        const float step = 60f;
        var accessor = tx.For<ClCohUnit>();
        try
        {
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                // TYPHON009 flags un-barriered spatial mutation through a span. This span is READ only — the write below goes through WriteSpatial, which is
                // what keeps ClusterProcessBitmap and ClusterAabbs correct, and therefore what makes the fence see a migration at all.
#pragma warning disable TYPHON009
                var positions = cluster.GetSpan(ClCohUnit.Pos);
#pragma warning restore TYPHON009
                ulong bits = cluster.OccupancyBits;
                while (bits != 0)
                {
                    int slotIndex = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;

                    ref readonly var b = ref positions[slotIndex].Bounds;
                    float x = b.MinX + step;
                    if (x >= WorldExtent - 1f)
                    {
                        x = 1f;
                    }
                    cluster.WriteSpatial(ClCohUnit.Pos, slotIndex, PointAt(x, b.MinY));
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }
    }

    /// <summary>Every promoted cell's tree holds exactly the clusters <c>ClusterCellMap</c> assigns to it — the membership half.</summary>
    private static void AssertPromotedTreesMatchCellMap(DatabaseEngine dbe, ArchetypeClusterState cs)
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

        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        for (int cellKey = 0; cellKey < cs.PerCellIndex.Length; cellKey++)
        {
            var tree = cs.PerCellIndex[cellKey]?.DynamicTree;
            if (tree == null)
            {
                continue;
            }

            var inTree = new HashSet<int>();
            foreach (int clusterChunkId in tree.EnumerateClusterIds())
            {
                Assert.That(inTree.Add(clusterChunkId), Is.True, $"cell {cellKey} holds cluster {clusterChunkId} twice");
            }

            var expected = byCell.TryGetValue(cellKey, out var e) ? e : new HashSet<int>();
            Assert.That(inTree, Is.EquivalentTo(expected), $"cell {cellKey}'s tree and ClusterCellMap disagree on which clusters live there");
            Assert.That(tree.ClusterCount, Is.EqualTo(expected.Count), $"cell {cellKey}'s ClusterCount does not match what the tree returns");
        }
    }

    /// <summary>
    /// A whole-world query against the entity positions read straight out of cluster storage. Shares no code with the index it checks, so it fails whichever
    /// structure the cell happens to be using.
    /// </summary>
    private static void AssertQueryMatchesStorage(DatabaseEngine dbe, ArchetypeClusterState cs, string stage)
    {
        var expected = new HashSet<long>();
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
                        int slotIndex = System.Numerics.BitOperations.TrailingZeroCount(bits);
                        bits &= bits - 1;
                        expected.Add(SlotKey(cluster.ChunkId, slotIndex));
                    }
                }
            }
            finally
            {
                accessor.Dispose();
            }
        }

        var actual = new HashSet<long>();
        using (var epoch = EpochGuard.Enter(dbe.EpochManager))
        {
            foreach (var r in cs.QueryAabb(dbe.SpatialGrid, 0f, 0f, float.NegativeInfinity, WorldExtent, WorldExtent, float.PositiveInfinity))
            {
                actual.Add(SlotKey(r.ClusterChunkId, r.SlotIndex));
            }
        }

        Assert.That(actual, Is.EquivalentTo(expected), $"{stage}: the spatial index and cluster storage disagree on which entities exist");
    }
}
