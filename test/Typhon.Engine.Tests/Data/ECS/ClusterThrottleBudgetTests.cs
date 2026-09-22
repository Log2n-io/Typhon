using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// <c>AC-11.1</c>, <c>AC-11.7</c> and <c>AC-11.8</c> — the re-clustering budget as an admission control (#872 step 11).
/// </summary>
/// <remarks>
/// <para><b>What this fixture is about, and what it deliberately is not.</b> Steps 10 and 12 asserted that the right entities move and that the cell gets
/// tighter. This asserts what happens when there is not enough budget to move all of them: that the spend is bounded, that a refused relocation is
/// accounted for exactly once, and that a zero budget degrades rather than breaking. None of those are visible to a fixture that gives the engine all the
/// budget it wants.</para>
/// <para><b>Motion, not a scattered spawn.</b> The repair fixtures build a degraded cell by spawning out of geometric order; the throttle needs a steady
/// stream of DRIFTERS, which only continuous motion produces. So every tick moves every entity, and the assertions are about the flow rather than about
/// the final layout.</para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class ClusterThrottleBudgetTests : TestBase<ClusterThrottleBudgetTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    private const float CellSize = 100f;
    private const float WorldMax = 1000f;

    /// <summary>Enough entities that a 1 ms budget cannot move all of their drifters in one tick, which is the condition every test here needs.</summary>
    /// <remarks>
    /// At the measured ~1 500 ns/entity a 1 ms budget admits ~660 moves. A population of 1 200 in one cell, all moving every tick, produces drifters well
    /// past that — so the throttle is exercised rather than merely present. A smaller population would make every assertion below vacuously true.
    /// </remarks>
    private const int Population = 1200;

    private static ClMigPos PointAt(float x, float y, int tag = 0) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Tag = tag };

    private static ushort ArchetypeId => Archetype<ClMigUnit>.Metadata.ArchetypeId;

    private static ArchetypeClusterState ClusterStateOf(DatabaseEngine dbe) =>
        dbe._archetypeStates[Archetype<ClMigUnit>.Metadata.ArchetypeId].ClusterState;

    /// <summary>
    /// An engine whose drift detection is armed and whose budget is the seam under test.
    /// </summary>
    /// <remarks>
    /// <paramref name="repairExtentRatio"/> defaults to 1.19 — just under the constructor's hard ceiling of 1.2, and unreachable in practice, because a
    /// cluster whose entities all belong to its own cell tops out near <c>1 + MigrationHysteresisRatio</c> = 1.05. So repair never fires and the budget is
    /// spent on RELOCATION alone. That isolation is what lets the arithmetic below be checked: with both mechanisms live the spend is a sum of a
    /// per-entity charge and a whole-unit charge, and a discrepancy could belong to either. (The step-10 fixtures disable the DRIFT gate the same way by
    /// setting <c>ClusterTargetExtentRatio</c> to 100 — that one has no ceiling, this one does, which is why the values differ.)
    /// </remarks>
    private DatabaseEngine SetupEngine(float budgetMs, float repairExtentRatio = 1.19f, float nsPerEntity = 1500f, float queryEfficiencyTolerance = 0.1f)
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClMigPos>();
        dbe.RegisterComponentFromAccessor<ClMigScratch>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(
            new Vector2(0, 0),
            new Vector2(WorldMax, WorldMax),
            CellSize,
            clusterRepairExtentRatio: repairExtentRatio,
            reclusterBudgetMs: budgetMs, batchSpawnSortThreshold: 0 /* step 15: this fixture builds its layout by spawn ORDER; the Morton sort would tighten it at birth */,
            repairNsPerEntity: nsPerEntity,
            // The valve is switched OFF, and saying so is required rather than tidy. With repair held off at 1.19 no cell can ever be nominated, so the
            // default critical ratio of 1.0 would sit BELOW the repair threshold — the configuration in which every nominated cell is critical and the
            // valve overshoots the budget on every tick. The constructor rejects that pairing, correctly; a fixture that means "no repair" means "no
            // valve" too.
            clusterRepairCriticalExtentRatio: 0f,
            // Constant-mode target (step 14): at 1 200 entities per cell the density-derived target is 0.35 and the throttle's boost would
            // raise it tick by tick, so the drifter flood these tests budget against would not be steady.
            clusterTargetPackingSlack: 0f,
            // TH-04's controller steers only by range queries, and only its own tests below run any: everywhere else the configured budget stands.
            queryEfficiencyTolerance: queryEfficiencyTolerance));
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>Spawn a cell full of entities scattered across it, so every cluster is already past its target extent and detection has work at once.</summary>
    private static void Spawn(DatabaseEngine dbe)
    {
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < Population; i++)
            {
                var x = 4f + ((i * 37) % 92);
                var y = 4f + ((i * 61) % 92);
                tx.Spawn<ClMigUnit>(ClMigUnit.Pos.Set(PointAt(x, y, i)));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);
    }

    /// <summary>Move every entity to a fresh position inside the cell, from a seeded generator so the run is reproducible.</summary>
    /// <remarks>
    /// Written through <c>WriteSpatial</c> over the cluster enumerator rather than by resolving ids, matching the step-10 fixtures: that is the path the
    /// spatial write barrier hooks, so it is what produces the process bits drift detection reads. Writing through <c>Open</c> would take a different
    /// route into the same storage and leave the detection side of the fence with nothing to find.
    /// </remarks>
    private static unsafe void MoveAll(DatabaseEngine dbe, Random rng)
    {
        using var tx = dbe.CreateQuickTransaction();
        var accessor = tx.For<ClMigUnit>();
        try
        {
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                var bits = cluster.OccupancyBits;
                while (bits != 0)
                {
                    var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    var x = 3f + (float)rng.NextDouble() * 94f;
                    var y = 3f + (float)rng.NextDouble() * 94f;
                    cluster.WriteSpatial(ClMigUnit.Pos, slot, PointAt(x, y));
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        tx.Commit();
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // AC-11.1 — the budget is respected
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Over a sustained run whose drift rate exceeds what the budget can buy, no tick admits more intra-cell relocations than the budget pays for.
    /// </summary>
    /// <remarks>
    /// <para><b>Asserted on the ADMITTED count against the budget's own arithmetic</b>, not on elapsed time. The budget is an admission control: it decides
    /// before the work happens, using a projected per-entity cost, and a test that measured wall-clock would be asserting about the machine rather than
    /// about the controller. <c>MeasuredNsPerEntity</c> is the number the controller actually used, so dividing the budget by it reproduces the decision
    /// exactly.</para>
    /// <para><b>Cell crossings are excluded from the bound because they are excluded from the refusal.</b> §5.7 makes a crossing a correctness move; the
    /// throttle charges it and never refuses it. The spawn here is confined to one cell and the motion never leaves it, so the crossing count is zero —
    /// asserted below on the cell's own population, so that a change which starts producing crossings makes this test fail loudly rather than silently
    /// widen its own bound. <c>RelocationsSurviveATickThatAlsoCarriesCellCrossings</c> is where the mixed case is covered.</para>
    /// </remarks>
    [Test]
    [VerifiesRule("TH-01")]
    public void NoTickAdmitsMoreRelocationsThanTheBudgetPaysFor()
    {
        using var dbe = SetupEngine(budgetMs: 1.0f);
        Spawn(dbe);
        var rng = new Random(4211);

        var sawThrottling = false;
        var totalAdmitted = 0;

        for (var tick = 2; tick <= 12; tick++)
        {
            MoveAll(dbe, rng);
            dbe.WriteTickFence(tick);

            var t = dbe.GetSpatialTelemetry(ArchetypeId);

            // Guarded, because the division below fails OPEN. A zero estimate makes the quotient +Infinity, and a float-to-int conversion saturates
            // rather than throwing — so `affordable` becomes int.MaxValue and the bound becomes "at most 2 147 483 647 migrations". One line zeroing
            // MeasuredNsPerEntity would leave every budget assertion in this file and in ClusterThrottleParallelTests green while AC-11.1 went unchecked.
            Assert.That(t.MeasuredNsPerEntity, Is.GreaterThan(0d),
                $"tick {tick} reported no per-entity estimate, so the budget bound below would divide by zero and saturate to int.MaxValue");

            var affordable = (int)((1.0d * 1_000_000d) / t.MeasuredNsPerEntity);

            Assert.That(t.MigrationCount, Is.LessThanOrEqualTo(affordable),
                $"tick {tick} executed {t.MigrationCount} migrations against a budget that pays for {affordable} at the "
                + $"{t.MeasuredNsPerEntity:F0} ns/entity it measured");

            // The added timers' whole reason for existing, and nothing else asserts it. MigrationExecuteMs brackets the migrant loop, which merely
            // STAGES the index update and the EntityMap patch; MigrationTotalMs adds the two apply phases that actually perform them. Substituting the
            // former for the latter in the cost model leaves every other assertion here green — both sides of those bounds divide by the same published
            // estimate — while the budget silently admits about twice what it can pay for.
            if (t.MigrationCount > 0)
            {
                Assert.That(t.MigrationTotalMs, Is.GreaterThan(t.MigrationExecuteMs),
                    $"tick {tick} moved {t.MigrationCount} entities and reported the same cost for the whole migration ({t.MigrationTotalMs:F4} ms) as for "
                    + $"the migrant loop alone ({t.MigrationExecuteMs:F4} ms) — the index and EntityMap apply phases are not being timed");
            }

            Assert.That(dbe.SpatialGrid.GetCell(0).EntityCount, Is.EqualTo(Population),
                $"tick {tick}: entities have left cell (0,0), so this tick carries CROSSINGS and the bound above is no longer a statement about "
                + "relocations alone");

            totalAdmitted += t.MigrationCount;
            if (t.RelocationsThrottled > 0)
            {
                sawThrottling = true;
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(sawThrottling, Is.True,
                "the budget was never binding, so the bound above held for want of work rather than because the throttle enforced it");
            Assert.That(totalAdmitted, Is.GreaterThan(0), "nothing was ever relocated, so the throttle was not the thing limiting the run");
        });
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // TH-04 — the budget follows the queries' efficiency
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The start of the verifier's message when the budget ignored the queries; the mutant looks for it.</summary>
    private const string BudgetIgnoredTheQueries = "the budget did not follow the queries' efficiency";

    /// <summary>
    /// Count the whole cell. Every entity lives in it, so every candidate matches: one candidate per hit, the best a query can be, whatever the motion has
    /// done to the clusters.
    /// </summary>
    private static int CountWholeCell(DatabaseEngine dbe)
    {
        var box = new AABB2F { MinX = 0f, MinY = 0f, MaxX = CellSize - 0.01f, MaxY = CellSize - 0.01f };
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var e = dbe.ClusterSpatialQuery<ClMigUnit>().AABB(in box);
        try
        {
            return e.Count();
        }
        finally
        {
            e.Dispose();
        }
    }

    /// <summary>
    /// Queries at the best efficiency they can have grant the maintenance next to nothing: however many drifters the motion produces, no relocation is
    /// admitted, and the telemetry says why.
    /// </summary>
    [Test]
    [VerifiesRule("TH-04")]
    public void AtTheBestEfficiencyTheQueriesHaveShown_TheBudgetAdmitsNoRelocation()
    {
        using var dbe = SetupEngine(budgetMs: 1.0f);
        Spawn(dbe);
        AssertTheBudgetFollowsTheQueries(dbe);
    }

    /// <summary>With the controller off, the same queries leave the whole budget standing — so the test above is not green for want of drifters.</summary>
    [Test]
    [RuleMutant("TH-04")]
    public void WithTheControllerOff_TheSameQueriesLeaveTheBudgetWhole()
    {
        using var dbe = SetupEngine(budgetMs: 1.0f, queryEfficiencyTolerance: 0f);
        Spawn(dbe);
        RuleMutants.AssertDetects("TH-04", BudgetIgnoredTheQueries, () => AssertTheBudgetFollowsTheQueries(dbe));
    }

    private static void AssertTheBudgetFollowsTheQueries(DatabaseEngine dbe)
    {
        var rng = new Random(4217);
        var admitted = 0;
        var throttled = 0;
        for (var tick = 2; tick <= 12; tick++)
        {
            MoveAll(dbe, rng);
            Assert.That(CountWholeCell(dbe), Is.EqualTo(Population), $"tick {tick}: precondition, the query matches the whole population");
            dbe.WriteTickFence(tick);

            var t = dbe.GetSpatialTelemetry(ArchetypeId);
            Assert.That(t.QueryCandidatesPerHitSmoothed, Is.EqualTo(1d).Within(1e-9), $"tick {tick}: precondition, every candidate matched");

            // Every tick from the first: the fence sets the scale before the throttle spends it, so tick 2's throttle — the first after a query — already
            // admits nothing. A scale computed a tick late would let that tick's relocations through.
            admitted += t.RelocationsAdmitted;
            throttled += t.RelocationsThrottled;
        }

        // Admitted plus throttled, not throttled alone: with the whole budget a fast machine affords every drifter, and a precondition on the throttled
        // count then failed the mutant on the wrong assertion (the gate's shards did, twice). The behavioural check comes first, so the mutant shows it can
        // fail; the granted budget after it.
        var last = dbe.GetSpatialTelemetry(ArchetypeId);
        Assert.That(admitted + throttled, Is.GreaterThan(0), "precondition: the motion produced relocations for the budget to decide on");
        Assert.That(admitted, Is.Zero, $"{BudgetIgnoredTheQueries}: {admitted} relocations admitted while every candidate matched");
        Assert.That(last.ReclusterBudgetGrantedMs, Is.LessThan(1e-3),
            $"{BudgetIgnoredTheQueries}: {last.ReclusterBudgetGrantedMs} ms granted while every candidate matched");
    }

    /// <summary>
    /// The repair planner is a consumer too: at the best efficiency the queries have shown it admits no unit, while the degraded cell it would repair waits
    /// in its queue.
    /// </summary>
    [Test]
    [VerifiesRule("TH-04")]
    public void AtTheBestEfficiency_TheRepairPlannerAdmitsNoUnit()
    {
        using var dbe = SetupEngine(budgetMs: 1.0f, repairExtentRatio: 0.75f);
        Spawn(dbe);
        AssertThePlannerFollowsTheQueries(dbe);
    }

    /// <summary>With the controller off, the same queries leave the planner its budget, and it repairs the cell.</summary>
    [Test]
    [RuleMutant("TH-04")]
    public void WithTheControllerOff_ThePlannerRepairsTheSameCell()
    {
        using var dbe = SetupEngine(budgetMs: 1.0f, repairExtentRatio: 0.75f, queryEfficiencyTolerance: 0f);
        Spawn(dbe);
        RuleMutants.AssertDetects("TH-04", BudgetIgnoredTheQueries, () => AssertThePlannerFollowsTheQueries(dbe));
    }

    private static void AssertThePlannerFollowsTheQueries(DatabaseEngine dbe)
    {
        var rng = new Random(4219);
        var units = 0;
        var queued = 0;
        for (var tick = 2; tick <= 12; tick++)
        {
            MoveAll(dbe, rng);
            Assert.That(CountWholeCell(dbe), Is.EqualTo(Population), $"tick {tick}: precondition, the query matches the whole population");
            dbe.WriteTickFence(tick);

            var t = dbe.GetSpatialTelemetry(ArchetypeId);
            units += t.RepairUnitCount;
            queued = Math.Max(queued, t.RepairQueueDepth);
        }

        // Queued, not refused: at its floor the planner stops before pricing a unit, so nothing is counted as refused.
        Assert.That(units + queued, Is.GreaterThan(0), "precondition: the scattered cell gave the planner a repair to decide on");
        Assert.That(units, Is.Zero, $"{BudgetIgnoredTheQueries}: the planner admitted {units} repair units while every candidate matched");
    }

    /// <summary>
    /// The controller's arithmetic, driven with made-up tallies: next to nothing at the best and never zero, the distance from the best over the tolerance
    /// in between, and the whole budget at the tolerance and past it.
    /// </summary>
    [Test]
    [VerifiesRule("TH-04")]
    public void TheScaleIsTheDistanceFromTheBest_OverTheTolerance()
    {
        using var dbe = SetupEngine(budgetMs: 1.0f);
        var cs = ClusterStateOf(dbe);
        var cfg = dbe.SpatialGrid.Config;
        void Tick(long candidates, long hits, int times) => FeedTally(cs, in cfg, candidates, hits, times);

        // A steady two candidates per hit: the best, so next to nothing is granted — but never zero, which would read as no enforcement.
        Tick(2_000, 1_000, 200);
        Assert.Multiple(() =>
        {
            Assert.That(cs.QueryCandidatesPerHitSmoothed, Is.EqualTo(2d).Within(1e-9));
            Assert.That(cs.QueryCandidatesPerHitBest, Is.EqualTo(2d).Within(1e-9));
            Assert.That(cs.MaintenanceBudgetScale, Is.EqualTo(ArchetypeClusterState.MinMaintenanceBudgetScale));
            Assert.That(cs.MaintenanceBudgetNs(in cfg), Is.GreaterThan(0d));
            Assert.That(cs.ComputeDriftNominationCap(in cfg), Is.EqualTo(256), "the nomination cap follows the budget down to its floor");
        });

        // A step up: the scale is the distance from the best over the tolerance, from the controller's own smoothed value and best.
        Tick(2_100, 1_000, 3);
        var expected = ((cs.QueryCandidatesPerHitSmoothed / cs.QueryCandidatesPerHitBest) - 1d) / cfg.QueryEfficiencyTolerance;
        Assert.Multiple(() =>
        {
            Assert.That(expected, Is.GreaterThan(0.01d).And.LessThan(1d), "precondition: a step inside the proportional band");
            Assert.That(cs.MaintenanceBudgetScale, Is.EqualTo(expected).Within(1e-12));
            Assert.That(cs.LastTickReclusterBudgetGrantedMs, Is.EqualTo(cfg.ReclusterBudgetMs * expected).Within(1e-9));
        });

        // Half as bad again: past the tolerance, the configured budget is the ceiling.
        Tick(3_000, 1_000, 100);
        Assert.That(cs.MaintenanceBudgetScale, Is.EqualTo(1d), "past the tolerance the configured budget is the ceiling");
    }

    /// <summary>
    /// A decline at half the rate of the creep this replaced is not followed by the best: it raises the budget. The best that rose 1e-4 a tick followed
    /// exactly this, and on the SWG demo held a starved archetype at the floor while its queries tested 5–8 % more entities per match (#906).
    /// </summary>
    [Test]
    [VerifiesRule("TH-04")]
    public void ASlowDecline_IsNotFollowedByTheBest_ItRaisesTheBudget()
    {
        using var dbe = SetupEngine(budgetMs: 1.0f);
        var cs = ClusterStateOf(dbe);
        var cfg = dbe.SpatialGrid.Config;
        FeedTally(cs, in cfg, 2_000, 1_000, 200);
        Assert.That(cs.MaintenanceBudgetScale, Is.EqualTo(ArchetypeClusterState.MinMaintenanceBudgetScale), "precondition: the queries at their best");

        // 5e-5 a tick for 1 500 ticks: about 7.8 % more candidates per match, never faster than half the old creep.
        var candidates = 2_000d;
        for (var i = 0; i < 1_500; i++)
        {
            candidates *= 1d + 5e-5;
            FeedTally(cs, in cfg, (long)candidates, 1_000, 1);
        }

        var expected = ((cs.QueryCandidatesPerHitSmoothed / 2d) - 1d) / cfg.QueryEfficiencyTolerance;
        Assert.Multiple(() =>
        {
            Assert.That(cs.QueryCandidatesPerHitBest, Is.EqualTo(2d).Within(1e-9), "the best does not follow a decline");
            Assert.That(expected, Is.GreaterThan(0.5d).And.LessThan(1d), "precondition: the decline sits inside the proportional band");
            Assert.That(cs.MaintenanceBudgetScale, Is.EqualTo(expected).Within(1e-9), "the budget rises with the decline");
        });
    }

    /// <summary>
    /// A level the whole budget cannot bring back within the tolerance is accepted after <see cref="ArchetypeClusterState.EfficiencyRebaseTicks"/> ticks at
    /// it: the best does not move before, and becomes the queries' present level on the tick after, which returns the grant to the floor.
    /// </summary>
    [Test]
    [VerifiesRule("TH-04")]
    public void AfterTheRebaseWindowAtTheWholeBudget_ThePresentLevelBecomesTheBest()
    {
        using var dbe = SetupEngine(budgetMs: 1.0f);
        var cs = ClusterStateOf(dbe);
        var cfg = dbe.SpatialGrid.Config;
        FeedTally(cs, in cfg, 2_000, 1_000, 200);
        ReachTheWholeBudget(cs, in cfg);

        // The rest of the window at the whole budget, and the best has not moved.
        FeedTally(cs, in cfg, 3_000, 1_000, ArchetypeClusterState.EfficiencyRebaseTicks - 1);
        Assert.Multiple(() =>
        {
            Assert.That(cs.QueryCandidatesPerHitBest, Is.EqualTo(2d).Within(1e-9), "the best holds for the whole window");
            Assert.That(cs.MaintenanceBudgetScale, Is.EqualTo(1d));
            Assert.That(cs.TicksAtWholeBudget, Is.EqualTo(ArchetypeClusterState.EfficiencyRebaseTicks), "the streak counted the window to its end");
            Assert.That(cs.TotalEfficiencyRebases, Is.Zero);
        });

        // One more: the whole budget has had its window, the present level is the best, and the grant is back at the floor.
        FeedTally(cs, in cfg, 3_000, 1_000, 1);
        Assert.Multiple(() =>
        {
            Assert.That(cs.QueryCandidatesPerHitBest, Is.EqualTo(cs.QueryCandidatesPerHitSmoothed).Within(1e-12), "re-based to the present level");
            Assert.That(cs.MaintenanceBudgetScale, Is.EqualTo(ArchetypeClusterState.MinMaintenanceBudgetScale), "and the grant back at the floor");
            Assert.That(cs.TotalEfficiencyRebases, Is.EqualTo(1), "the re-base is counted");
            Assert.That(cs.TicksAtWholeBudget, Is.Zero, "and the streak starts over");
            Assert.That(cs.ControllerFlags, Is.EqualTo(3), "the trace's flags: a signal, and a re-base this tick");
        });

        // The flag is this tick's alone; the count stays.
        FeedTally(cs, in cfg, 3_000, 1_000, 1);
        Assert.Multiple(() =>
        {
            Assert.That(cs.ControllerFlags, Is.EqualTo(1), "a signal, and no re-base this tick");
            Assert.That(cs.TotalEfficiencyRebases, Is.EqualTo(1));
        });
    }

    /// <summary>
    /// The window is consecutive: half the window at the whole budget, a spell back inside the tolerance — whose first thirty-odd ticks, still above it,
    /// count too — and half the window again add up to more than the window, and the best still does not move.
    /// </summary>
    [Test]
    [VerifiesRule("TH-04")]
    public void AWindowTheQueriesInterrupt_StartsAgain_AndDoesNotRebase()
    {
        using var dbe = SetupEngine(budgetMs: 1.0f);
        var cs = ClusterStateOf(dbe);
        var cfg = dbe.SpatialGrid.Config;
        FeedTally(cs, in cfg, 2_000, 1_000, 200);
        var span = ArchetypeClusterState.EfficiencyRebaseTicks / 2;

        ReachTheWholeBudget(cs, in cfg);
        FeedTally(cs, in cfg, 3_000, 1_000, span);

        // Back inside the tolerance, which restarts the window. Bounded: the smoothing returns within about thirty ticks.
        for (var i = 0; i < 200 && cs.MaintenanceBudgetScale >= 1d; i++)
        {
            FeedTally(cs, in cfg, 2_000, 1_000, 1);
        }

        Assert.That(cs.MaintenanceBudgetScale, Is.LessThan(1d), "precondition: back inside the tolerance");

        // As many whole-budget ticks again, more than the window in all, and nothing is re-based.
        ReachTheWholeBudget(cs, in cfg);
        FeedTally(cs, in cfg, 3_000, 1_000, span);
        Assert.Multiple(() =>
        {
            Assert.That(cs.QueryCandidatesPerHitBest, Is.EqualTo(2d).Within(1e-9), "an interrupted window re-bases nothing");
            Assert.That(cs.MaintenanceBudgetScale, Is.EqualTo(1d));
        });
    }

    /// <summary>
    /// Only a whole budget the distance set counts toward the window. Without a signal the budget is whole by fall-back, and however long that lasts, the
    /// signal's return does not re-base: the best is the one from before, and the returning queries, half as bad again, get the whole budget.
    /// </summary>
    [Test]
    [VerifiesRule("TH-04")]
    public void AWholeBudgetByFallBack_DoesNotCountTowardTheWindow()
    {
        using var dbe = SetupEngine(budgetMs: 1.0f);
        var cs = ClusterStateOf(dbe);
        var cfg = dbe.SpatialGrid.Config;
        FeedTally(cs, in cfg, 2_000, 1_000, 200);

        // The signal fades, lost after about 150 ticks, and the budget stays whole by fall-back for longer than the window.
        FeedTally(cs, in cfg, 0, 0, 150 + ArchetypeClusterState.EfficiencyRebaseTicks + 50);
        Assert.Multiple(() =>
        {
            Assert.That(cs.HasQuerySignal, Is.False, "precondition: no signal");
            Assert.That(cs.MaintenanceBudgetScale, Is.EqualTo(1d), "precondition: the whole budget, by fall-back");
        });

        FeedTally(cs, in cfg, 3_000, 1_000, 1);
        Assert.Multiple(() =>
        {
            Assert.That(cs.HasQuerySignal, Is.True, "precondition: the signal is back");
            Assert.That(cs.QueryCandidatesPerHitBest, Is.EqualTo(2d).Within(1e-9), "the fall-back ticks re-based nothing");
            Assert.That(cs.MaintenanceBudgetScale, Is.EqualTo(1d), "half as bad again as the best: the whole budget");
        });
    }

    /// <summary>From a best of 2, feed half as bad again until the scale reaches the whole budget: a handful of ticks, bounded.</summary>
    private static void ReachTheWholeBudget(ArchetypeClusterState cs, in SpatialGridConfig cfg)
    {
        for (var i = 0; i < 50 && cs.MaintenanceBudgetScale < 1d; i++)
        {
            FeedTally(cs, in cfg, 3_000, 1_000, 1);
        }

        Assert.That(cs.MaintenanceBudgetScale, Is.EqualTo(1d), "precondition: the whole budget");
    }

    /// <summary>Feed the controller the same made-up tally for <paramref name="times"/> ticks.</summary>
    private static void FeedTally(ArchetypeClusterState cs, in SpatialGridConfig cfg, long candidates, long hits, int times)
    {
        for (var i = 0; i < times; i++)
        {
            cs.LastTickQueryCandidates = candidates;
            cs.LastTickQueryHits = hits;
            cs.UpdateMaintenanceBudgetScale(in cfg);
        }
    }

    /// <summary>
    /// Without a signal the configured budget stands, exactly as it did before the controller: from the start, and again once a signal that held the budget
    /// at its floor fades — lost only below half the threshold it was gained at.
    /// </summary>
    [Test]
    [VerifiesRule("TH-04")]
    public void WithoutASignal_TheConfiguredBudgetStands_AndReturnsWhenTheSignalFades()
    {
        using var dbe = SetupEngine(budgetMs: 1.0f);
        var cs = ClusterStateOf(dbe);
        var cfg = dbe.SpatialGrid.Config;
        FeedTally(cs, in cfg, 0, 0, 100);
        Assert.Multiple(() =>
        {
            Assert.That(cs.HasQuerySignal, Is.False, "precondition: no query has hit");
            Assert.That(cs.MaintenanceBudgetScale, Is.EqualTo(1d), "no signal: the configured budget");
            Assert.That(cs.MaintenanceBudgetNs(in cfg), Is.EqualTo(1_000_000d));
        });

        // A signal at its best holds the budget at the floor...
        FeedTally(cs, in cfg, 2_000, 1_000, 200);
        Assert.That(cs.MaintenanceBudgetScale, Is.EqualTo(ArchetypeClusterState.MinMaintenanceBudgetScale), "precondition: the floor");

        // ...and keeps holding it while the smoothed hits fade below the threshold, down to half of it: 1 000 x 0.95^140 is about 0.76 a tick.
        FeedTally(cs, in cfg, 0, 0, 140);
        Assert.Multiple(() =>
        {
            Assert.That(cs.QueryHitsEwma, Is.LessThan(1d).And.GreaterThan(0.5d), "precondition: between half the threshold and the threshold");
            Assert.That(cs.HasQuerySignal, Is.True, "a signal is lost only below half the threshold it was gained at");
            Assert.That(cs.MaintenanceBudgetScale, Is.EqualTo(ArchetypeClusterState.MinMaintenanceBudgetScale));
        });

        // Below half: the signal is gone, and the configured budget is back.
        FeedTally(cs, in cfg, 0, 0, 20);
        Assert.Multiple(() =>
        {
            Assert.That(cs.HasQuerySignal, Is.False, "precondition: below half the threshold");
            Assert.That(cs.MaintenanceBudgetScale, Is.EqualTo(1d), "no signal: the configured budget again");
            Assert.That(cs.LastTickReclusterBudgetGrantedMs, Is.EqualTo(1d).Within(1e-12));
        });
    }

    /// <summary>With the tolerance at zero the controller still watches, and the configured budget stands however good the queries are.</summary>
    [Test]
    [VerifiesRule("TH-04")]
    public void WithTheControllerOff_TheConfiguredBudgetStands_HoweverGoodTheQueries()
    {
        using var dbe = SetupEngine(budgetMs: 1.0f, queryEfficiencyTolerance: 0f);
        var cs = ClusterStateOf(dbe);
        var cfg = dbe.SpatialGrid.Config;
        FeedTally(cs, in cfg, 2_000, 1_000, 200);
        Assert.Multiple(() =>
        {
            Assert.That(cs.QueryCandidatesPerHitBest, Is.EqualTo(2d).Within(1e-9), "the controller still watches with the tolerance at zero");
            Assert.That(cs.MaintenanceBudgetScale, Is.EqualTo(1d), "tolerance zero: the configured budget, however good the queries");
        });
    }

    /// <summary>A zero budget stays zero whatever the queries say, which the throttle reads as no enforcement (TH-01).</summary>
    [Test]
    [VerifiesRule("TH-04")]
    public void AZeroBudget_StaysUnenforced_WhateverTheQueriesSay()
    {
        using var dbe = SetupEngine(budgetMs: 0f, repairExtentRatio: 0.75f);
        var cs = ClusterStateOf(dbe);
        var cfg = dbe.SpatialGrid.Config;
        FeedTally(cs, in cfg, 2_000, 1_000, 200);
        Assert.Multiple(() =>
        {
            Assert.That(cs.MaintenanceBudgetScale, Is.EqualTo(ArchetypeClusterState.MinMaintenanceBudgetScale), "precondition: the queries sit at their best");
            Assert.That(cs.MaintenanceBudgetNs(in cfg), Is.Zero, "a zero budget stays zero, which the throttle reads as no enforcement");
            Assert.That(cs.ComputeDriftNominationCap(in cfg), Is.Zero, "and the nomination cap stays off");
        });
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // AC-11.7 — hysteresis and throttling do not double-discount
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Every detected drifter is accounted for exactly once — admitted, throttled, or unplaced — and an entity absorbed by the drift margin is none of
    /// those.
    /// </summary>
    /// <remarks>
    /// <para><b>The identity is the test.</b> §5.6 warns that hysteresis and throttling "must not both discount the same migration, or the budget model
    /// over-counts what it is actually deferring". In this implementation the two cannot overlap structurally — <c>DetectDriftersInCluster</c> increments
    /// the absorbed counter and <c>continue</c>s BEFORE the detected counter — but "cannot overlap structurally" is a claim about code that a refactor can
    /// silently break. <c>DriftersDetected == admitted + throttled + unplaced</c> is that claim expressed as arithmetic: if an absorbed entity ever leaked
    /// into the throttle's tally, the right-hand side would exceed the left.</para>
    /// <para><b>Why the admitted term is <c>MigrationCount</c> and not a counter of its own.</b> Adding one would make the identity true by construction
    /// rather than by agreement between two independently produced numbers, which is the property that makes it worth asserting.</para>
    /// </remarks>
    [Test]
    [VerifiesRule("TH-02")]
    public void EveryDetectedDrifterIsAccountedForExactlyOnce()
    {
        using var dbe = SetupEngine(budgetMs: 0.4f);
        Spawn(dbe);
        var rng = new Random(90210);

        var sawAbsorbed = false;
        var sawThrottled = false;
        var previousDetected = -1;
        var previousUnplaced = 0;

        for (var tick = 2; tick <= 10; tick++)
        {
            MoveAll(dbe, rng);
            dbe.WriteTickFence(tick);

            var t = dbe.GetSpatialTelemetry(ArchetypeId);

            // The identity spans TWO ticks, and that is a property of the pipeline rather than an allowance made for it. Drift detection runs in
            // AabbRefresh, which follows Migrate, so its requests land beyond this tick's drain prefix and are decided by the NEXT tick's Prep — where the
            // throttle admits or drops them. Comparing within one tick compares detections against the previous tick's admissions and fails by exactly the
            // lag. `unplaced` is the exception: it is decided AT detection, so it belongs to the earlier tick's side.
            if (previousDetected >= 0)
            {
                // `superseded` is CR-05's term and is zero throughout THIS fixture — one cell, so no crossings, so nothing can supersede. It is in the
                // identity anyway rather than assumed away: the fixture's isolation is a property of its configuration, and a later change that let a
                // crossing in would otherwise turn a correct engine into a red test with a misleading message.
                Assert.That(t.MigrationCount + t.RelocationsThrottled + t.RelocationsSuperseded + previousUnplaced, Is.EqualTo(previousDetected),
                    $"tick {tick}: {previousDetected} drifters detected on tick {tick - 1}, but {t.MigrationCount} moved + {t.RelocationsThrottled} "
                    + $"throttled + {t.RelocationsSuperseded} superseded + {previousUnplaced} unplaced — a drifter was counted twice or lost between "
                    + "detection and admission");
            }

            previousDetected = t.DriftersDetected;
            previousUnplaced = t.DriftersUnplaced;
            sawAbsorbed |= t.DriftAbsorbedCount > 0;
            sawThrottled |= t.RelocationsThrottled > 0;
        }

        Assert.Multiple(() =>
        {
            Assert.That(sawThrottled, Is.True, "nothing was ever throttled, so the identity never had a throttled term to get wrong");
            Assert.That(sawAbsorbed, Is.True, "the drift margin never absorbed anything, so this proves nothing about the two counters overlapping");
        });
    }

    /// <summary>
    /// A tick carrying BOTH cell crossings and intra-cell drifters keeps every relocation the budget can pay for.
    /// </summary>
    /// <remarks>
    /// <para><b>This is the arm that catches an in-place partition, and nothing else in the suite can.</b> The throttle separates mandatory crossings
    /// from throttleable relocations. The obvious implementation compacts the crossings to the front of the pending queue and then re-scans it for
    /// relocations — but the low indices have already been overwritten, so exactly <c>min(relocations, crossings)</c> relocations are destroyed, with
    /// <c>RelocationsThrottled</c> reading zero. It was written that way and shipped through a full green suite, because every other fixture here confines
    /// its entities to one cell and therefore produces <b>no crossings at all</b> — the one case where the bug cannot fire.</para>
    /// <para>The queue's layout makes it the normal case rather than a corner: AabbRefresh appends a tick's relocations and the next tick's Prep appends
    /// its crossings behind them, so relocations sit at the low indices and are the ones overwritten. On any tick with at least as many crossings as
    /// drifters, EVERY relocation is silently dropped and step 10's placement reverts to the first fit this issue exists to repair.</para>
    /// <para><b>The budget is deliberately generous</b>, so a missing relocation cannot be excused as a throttled one: at 5 ms nothing should be refused.
    /// </para>
    /// <para><b>What this arm proves, precisely.</b> The assertion is an INEQUALITY, not an identity, and it cannot be tightened here:
    /// <c>MigrationCount</c> sums all three migration kinds while <c>DriftersDetected</c> counts drifters alone, so the slack is this tick's crossing
    /// count — a quantity the fixture drives but does not pin. It therefore catches a partition that loses MORE relocations than there are crossings, and
    /// misses one that loses fewer. The exact net is
    /// <c>ThePartitionIsAPureFunctionOfItsInputs</c>, which calls the partition directly over a hand-built mixed queue and counts the survivors; this arm
    /// is the end-to-end complement that shows the mixed case actually reaches the throttle in a running engine.</para>
    /// </remarks>
    [Test]
    [VerifiesRule("TH-01")]
    public void RelocationsSurviveATickThatAlsoCarriesCellCrossings()
    {
        using var dbe = SetupEngine(budgetMs: 5.0f);
        Spawn(dbe);
        var rng = new Random(31337);

        var sawCrossings = false;
        var sawRelocations = false;
        var previousDetected = -1;
        var previousUnplaced = 0;

        for (var tick = 2; tick <= 10; tick++)
        {
            MoveAcrossCellBoundary(dbe, rng);
            dbe.WriteTickFence(tick);

            var t = dbe.GetSpatialTelemetry(ArchetypeId);

            Assert.That(t.RelocationsThrottled, Is.Zero,
                $"tick {tick} throttled {t.RelocationsThrottled} relocations against a 5 ms budget that can afford far more — either the budget arithmetic "
                + "is wrong or relocations are being lost and mis-counted");

            if (previousDetected > 0)
            {
                // Every drifter detected last tick must appear this tick as a migration or as unplaced. A partition that overwrote relocations with
                // crossings shows up here as a shortfall, because the lost requests are counted in NEITHER term.
                Assert.That(t.MigrationCount + previousUnplaced, Is.GreaterThanOrEqualTo(previousDetected),
                    $"tick {tick}: {previousDetected} drifters were detected on tick {tick - 1} but only {t.MigrationCount} migrations executed and "
                    + $"{previousUnplaced} were unplaced — {previousDetected - t.MigrationCount - previousUnplaced} relocation(s) vanished between "
                    + "detection and execution");
                sawRelocations = true;
            }

            sawCrossings |= t.MigrationCount > 0 && dbe.SpatialGrid.GetCell(0).EntityCount < Population;
            previousDetected = t.DriftersDetected;
            previousUnplaced = t.DriftersUnplaced;
        }

        Assert.Multiple(() =>
        {
            Assert.That(sawRelocations, Is.True, "no drifter was ever detected, so the shortfall assertion never had anything to constrain");
            Assert.That(sawCrossings, Is.True,
                "no entity ever left cell (0,0), so this tick carried no CROSSINGS and the fixture is the same single-cell case every other test here "
                + "already covers — which is precisely the case an in-place partition survives");
        });
    }

    /// <summary>
    /// Move most entities within their cell and a minority across a cell boundary, so one tick produces both kinds of migration.
    /// </summary>
    /// <remarks>
    /// The split is by slot rather than by position so it is stable across ticks: roughly a quarter of the population walks into the neighbouring cell and
    /// the rest jitters at home. Both counts stay non-zero for the whole run, which is what the assertions above need.
    /// </remarks>
    private static unsafe void MoveAcrossCellBoundary(DatabaseEngine dbe, Random rng)
    {
        using var tx = dbe.CreateQuickTransaction();
        var accessor = tx.For<ClMigUnit>();
        try
        {
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                var bits = cluster.OccupancyBits;
                while (bits != 0)
                {
                    var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;

                    var crosses = (slot & 3) == 0;
                    var originX = crosses ? CellSize : 0f;
                    var x = originX + 3f + (float)rng.NextDouble() * 94f;
                    var y = 3f + (float)rng.NextDouble() * 94f;
                    cluster.WriteSpatial(ClMigUnit.Pos, slot, PointAt(x, y));
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        tx.Commit();
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // AC-11.6 — the partition is a pure function of its inputs
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The same pending queue, budget and estimate produce the same admitted set — every time, on any thread.
    /// </summary>
    /// <remarks>
    /// <para><b>AC-11.6 says "deterministic under a fixed seed and fixed W", and the ADMITTED COUNT cannot satisfy that literally — which is a
    /// consequence of the adaptive cost model, not a defect in the throttle.</b> The budget buys
    /// <c>ReclusterBudgetMs / MeasuredNsPerEntity</c> entities, and that estimate is a wall-clock MEASUREMENT of the previous tick. Two runs of an
    /// identical workload therefore admit slightly different numbers, and a different set of entities moved means different bounds, which means a different
    /// detection on the next tick: the divergence compounds. Measured directly — two W=1 runs of the same motion schedule agreed for four ticks and then
    /// separated.</para>
    /// <para>That was not visible before step 11 because nothing was budgeted; the design's AC predates the decision (T4) to replace a hand-set constant
    /// with a measured one, and the two cannot both hold. What IS deterministic, and what the criterion is actually protecting, is the
    /// <b>partition itself</b>: given a queue, a budget and an estimate, the admitted set is fixed. That is asserted here, directly, with the timing taken
    /// out — and <c>ClusterThrottleParallelTests</c> covers the complementary half, that the RULE holds at W ∈ {serial, 1, 2, 8}.</para>
    /// <para><b>Mixed kinds, deliberately.</b> A queue of relocations alone would be partitioned correctly by an implementation that never separates the
    /// classes at all.</para>
    /// </remarks>
    [Test]
    [VerifiesRule("TH-01")]
    public void ThePartitionIsAPureFunctionOfItsInputs()
    {
        using var dbe = SetupEngine(budgetMs: 1.0f);
        Spawn(dbe);
        var state = ClusterStateOf(dbe);

        static List<string> Snapshot(ArchetypeClusterState s)
        {
            var lines = new List<string>();
            for (var i = 0; i < s.PendingMigrationCount; i++)
            {
                ref readonly var r = ref s.PendingMigrations[i];
                lines.Add($"{r.Kind}:{r.SourceClusterChunkId}.{r.SourceSlotIndex}->{r.DestCellKey}");
            }

            return lines;
        }

        List<string> reference = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            // Rebuilt identically each time: three relocations, two crossings, interleaved so a partition that overwrites in place loses some of them.
            state.PendingMigrationCount = 0;
            state.EnqueueMigration(new MigrationRequest(10, 1, 0, 11, MigrationRequest.AnySlot, MigrationKind.Relocation));
            state.EnqueueMigration(new MigrationRequest(10, 2, 5));
            state.EnqueueMigration(new MigrationRequest(12, 3, 0, 13, MigrationRequest.AnySlot, MigrationKind.Relocation));
            state.EnqueueMigration(new MigrationRequest(12, 4, 6));
            state.EnqueueMigration(new MigrationRequest(14, 5, 0, 15, MigrationRequest.AnySlot, MigrationKind.Relocation));

            state.ApplyMigrationThrottle(dbe.SpatialGrid);
            var admitted = Snapshot(state);

            if (reference == null)
            {
                Assert.That(admitted, Has.Count.EqualTo(5),
                    "a 1 ms budget could not admit five requests, so the partition below is being compared under refusal rather than under its normal path");
                Assert.That(admitted.FindAll(static l => l.StartsWith("Relocation", StringComparison.Ordinal)), Has.Count.EqualTo(3),
                    $"only {admitted.FindAll(static l => l.StartsWith("Relocation", StringComparison.Ordinal)).Count} of 3 relocations survived a partition "
                    + "that also had two crossings to compact — an in-place two-pass partition overwrites the low indices before the second pass reads them");
                reference = admitted;
                continue;
            }

            Assert.That(admitted, Is.EqualTo(reference), $"attempt {attempt} partitioned the identical queue differently");
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // AC-11.8 — a zero budget degrades, and does not deadlock or leak
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// With the budget at zero the engine keeps ticking, the pending queue stays bounded, and the repair queue stops growing at its cap.
    /// </summary>
    /// <remarks>
    /// <para><b>Zero means "no budget enforcement", not "do nothing", and this test pins that meaning.</b> <c>ReclusterBudgetMs = 0</c> already disabled
    /// REPAIR before step 11 — a whole-unit admission needs a positive budget to project against — and step-10 fixtures set it precisely to isolate
    /// relocation from repair. An early draft of the throttle also dropped every relocation at zero, which silently changed what an existing knob does:
    /// turning repair off would have reverted placement to the first fit this whole issue exists to repair. Three step-10 tests caught it. So the
    /// assertion here is that relocation still HAPPENS at zero.</para>
    /// <para><b>Thirty ticks, and the queue bound is the point.</b> <c>CR-01</c> records the failure mode a badly-behaved throttle produces — "the queue
    /// grows without bound", measured at 224 854 entries by the twentieth tick — so a run long enough to show that growth, asserting it does not happen,
    /// is what makes this more than a smoke test.</para>
    /// </remarks>
    [Test]
    [VerifiesRule("TH-01")]
    public void AZeroBudgetKeepsRelocatingAndKeepsEveryQueueBounded()
    {
        using var dbe = SetupEngine(budgetMs: 0f, repairExtentRatio: 0.75f);
        Spawn(dbe);
        var state = ClusterStateOf(dbe);
        var rng = new Random(1337);

        var maxQueued = 0;
        var totalMigrations = 0L;

        for (var tick = 2; tick <= 31; tick++)
        {
            MoveAll(dbe, rng);
            dbe.WriteTickFence(tick);

            var t = dbe.GetSpatialTelemetry(ArchetypeId);
            totalMigrations += t.MigrationCount;
            maxQueued = Math.Max(maxQueued, state.PendingMigrationCount);

            Assert.That(state.PendingMigrationCount, Is.LessThanOrEqualTo(t.DriftersDetected + t.MigrationCount),
                $"tick {tick} left {state.PendingMigrationCount} requests queued against {t.DriftersDetected} detected this tick — the queue is retaining "
                + "executed requests (CR-01's 'prefix too small')");

            // One, not the configured 4096: the whole spawn lives in cell (0,0), so at most one candidate can ever be queued. A bound of 4096 here
            // would be satisfied by deleting the cap entirely — TheQueueStopsAtItsCapAndReportsTheEvictions is what exercises the cap itself.
            Assert.That(t.RepairQueueDepth, Is.LessThanOrEqualTo(1),
                $"tick {tick}: the repair queue holds {t.RepairQueueDepth} candidates for a world with one occupied cell");
            Assert.That(t.ReclusterBudgetUsedMs, Is.Zero, $"tick {tick} spent budget it does not have");
            Assert.That(t.RepairUnitCount, Is.Zero, $"tick {tick} admitted a repair unit with no budget to admit it against");
        }

        Assert.Multiple(() =>
        {
            Assert.That(totalMigrations, Is.GreaterThan(0),
                "a zero budget stopped relocation entirely — that turns ReclusterBudgetMs into a switch for step 10 as well as step 12, which is not what "
                + "it means and would silently revert placement to first fit");
            Assert.That(maxQueued, Is.GreaterThan(0), "nothing was ever queued, so the bound above held trivially");
        });
    }
}
