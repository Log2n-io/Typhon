using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// Step 14 of the VDB cell-grid design (§5.8, decisions D1 and D2): the intra-cell target is a function of the cell's population, repair is charged
/// before relocation, the budget is spent on the target rather than on a queue prefix, and the cost model charges span rather than summed CPU.
/// </summary>
/// <remarks>
/// <para><b>What was measured, and what these tests hold.</b> With a constant target of 0.25 × cell the drift gate fired on every written cluster on
/// every tick in every configuration the density guidance recommends, because a full cluster in a cell of E entities cannot be tighter than
/// <c>(64 / E)^(1/d)</c> — 1.0 in the 16–64 basin. Relocations then consumed the whole budget on every tick (<c>relocationSpendMs == budget</c> up to
/// 16 ms), the planner entered with a median 630 ns of 8 ms, and 91–99 % of admitted relocations rejected their pin because every drifter of a cell
/// named the same empty cluster. Each test below pins one of those against its fix.</para>
/// <para>The engine fixtures use the serial <c>WriteTickFence</c>, which is the path whose parallelism is 1 — the span-based estimator is therefore
/// unit-tested on the state directly rather than through a fence.</para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class ClusterDensityTargetTests : TestBase<ClusterDensityTargetTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    private const float CellSize = 100f;
    private const float WorldMax = 1000f;

    private static ClMigPos PointAt(float x, float y, int tag = 0) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Tag = tag };

    private static ushort ArchetypeId => Archetype<ClMigUnit>.Metadata.ArchetypeId;

    private static ArchetypeClusterState ClusterStateOf(DatabaseEngine dbe) =>
        dbe._archetypeStates[Archetype<ClMigUnit>.Metadata.ArchetypeId].ClusterState;

    private DatabaseEngine SetupEngine(float budgetMs, float criticalRatio = 0f, float repairRatio = 0.75f, float seedNsPerEntity = 1500f,
        int repairCooldownTicks = 50)
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClMigPos>();
        dbe.RegisterComponentFromAccessor<ClMigScratch>();
        // Defaults on purpose — the density-derived target and its slack are what these tests exercise. The valve is off so a repair that runs is the
        // planner's own admission and not a queue-jump, which is what AC-14.3 is about.
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(
            new Vector2(0, 0),
            new Vector2(WorldMax, WorldMax),
            CellSize,
            clusterRepairExtentRatio: repairRatio,
            reclusterBudgetMs: budgetMs, batchSpawnSortThreshold: 0 /* step 15: this fixture builds its layout by spawn ORDER; the Morton sort would tighten it at birth */,
            repairNsPerEntity: seedNsPerEntity,
            clusterRepairCriticalExtentRatio: criticalRatio,
            repairCooldownTicks: repairCooldownTicks));
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>Live entity count of the cell at grid coordinate (<paramref name="cellX"/>, 0), or 0 when the sparse grid never materialised it.</summary>
    private static int EntitiesInCell(DatabaseEngine dbe, int cellX) =>
        dbe.Realm0Grid.TryGetCellKey(cellX, 0, 0, out var key) ? dbe.Realm0Grid.GetCell(key).EntityCount : 0;

    /// <summary>Spawn <paramref name="perCell"/> entities into each of <paramref name="cells"/> cells along x, out of geometric order, so every cluster is born wide.</summary>
    private static void SpawnScattered(DatabaseEngine dbe, int cells, int perCell, int firstCell = 0, int spread = 92, bool fence = true)
    {
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var c = firstCell; c < firstCell + cells; c++)
            {
                var originX = c * CellSize;
                for (var i = 0; i < perCell; i++)
                {
                    var x = originX + 4f + ((i * 37) % spread);
                    var y = 4f + ((i * 61) % spread);
                    tx.Spawn<ClMigUnit>(ClMigUnit.Pos.Set(PointAt(x, y, (c * perCell) + i)));
                }
            }

            tx.Commit();
        }

        if (fence)
        {
            dbe.WriteTickFence(1);
        }
    }

    /// <summary>Jitter every entity inside its own cell through the spatial barrier, so the fence has written clusters to scan and no cell crossings.</summary>
    private static unsafe void JitterInPlace(DatabaseEngine dbe, Random rng, float amplitude)
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
                    var slot = BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    var current = cluster.GetReadOnly(ClMigUnit.Pos, slot);
                    var x = current.Bounds.MinX + ((float)rng.NextDouble() - 0.5f) * amplitude;
                    var y = current.Bounds.MinY + ((float)rng.NextDouble() - 0.5f) * amplitude;
                    // Clamped to the entity's own cell interior so the motion is intra-cell by construction.
                    var cellMinX = MathF.Floor(current.Bounds.MinX / CellSize) * CellSize;
                    x = Math.Clamp(x, cellMinX + 2f, cellMinX + CellSize - 2f);
                    y = Math.Clamp(y, 2f, CellSize - 2f);
                    cluster.WriteSpatial(ClMigUnit.Pos, slot, PointAt(x, y, current.Tag));
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
    // D1 — the target is a function of the cell's population
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The density target is <c>slack × (slots / E)^(1/d)</c>, 1 (off) at or below one cluster's worth, and 0 (constant mode) at zero slack.</summary>
    [Test]
    public void TheDensityTargetFollowsThePackingBound()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ArchetypeClusterState.DensityTargetRatio(64, 64, flat: true, slack: 1.5f), Is.EqualTo(1f), "one cluster's worth is off");
            Assert.That(ArchetypeClusterState.DensityTargetRatio(10, 64, flat: false, slack: 1.5f), Is.EqualTo(1f), "below one cluster's worth is off");
            Assert.That(ArchetypeClusterState.DensityTargetRatio(100, 64, flat: true, slack: 1.5f), Is.EqualTo(1f),
                "2D at 100/cell: 1.5 × sqrt(0.64) = 1.2 clamps to off");
            Assert.That(ArchetypeClusterState.DensityTargetRatio(256, 64, flat: true, slack: 1.5f), Is.EqualTo(0.75f).Within(1e-5f),
                "2D at 256/cell: bound 0.5, target 0.75");
            Assert.That(ArchetypeClusterState.DensityTargetRatio(512, 64, flat: false, slack: 1.5f), Is.EqualTo(0.75f).Within(1e-5f),
                "3D at 512/cell: bound 0.5, target 0.75");
            Assert.That(ArchetypeClusterState.DensityTargetRatio(4096, 64, flat: false, slack: 1.5f), Is.EqualTo(0.375f).Within(1e-5f),
                "3D at 4 096/cell: bound 0.25, target 0.375");
            Assert.That(ArchetypeClusterState.DensityTargetRatio(100_000, 64, flat: false, slack: 1.5f), Is.EqualTo(1.5f * MathF.Cbrt(64f / 100_000f)).Within(1e-5f),
                "the design's 100 K cell: bound 0.086");
            Assert.That(ArchetypeClusterState.DensityTargetRatio(4096, 32, flat: false, slack: 1.5f), Is.EqualTo(1.5f * MathF.Cbrt(32f / 4096f)).Within(1e-5f),
                "slots per cluster is the archetype's, not 64");
            Assert.That(ArchetypeClusterState.DensityTargetRatio(4096, 64, flat: false, slack: 0f), Is.EqualTo(0f), "zero slack is constant mode");
        });
    }

    /// <summary>
    /// AC-14.1 — in the 16–64 entities/cell basin under sustained motion the drift gate never opens: no cluster is gated, no drifter is detected, no
    /// relocation is filed, and every written cluster is reported as suppressed by density rather than merely absent.
    /// </summary>
    [Test]
    public void InTheBasinTheDriftGateNeverOpens()
    {
        using var dbe = SetupEngine(budgetMs: 1.0f);
        const int Cells = 6;
        const int PerCell = 40;
        SpawnScattered(dbe, Cells, PerCell);
        var rng = new Random(1406);

        var suppressed = 0;
        for (var tick = 2; tick <= 10; tick++)
        {
            JitterInPlace(dbe, rng, amplitude: 30f);
            dbe.WriteTickFence(tick);

            var t = dbe.GetSpatialTelemetry(ArchetypeId);
            Assert.Multiple(() =>
            {
                Assert.That(t.DriftGatedClusters, Is.Zero, $"tick {tick}: a cluster in a one-cluster cell was drift-gated");
                Assert.That(t.DriftersDetected, Is.Zero, $"tick {tick}: a drifter was detected where no relocation can tighten anything");
                Assert.That(t.MigrationCount, Is.Zero, $"tick {tick}: something migrated in a world whose motion never leaves a cell");
                Assert.That(t.RepairUnitCount, Is.Zero, $"tick {tick}: a one-cluster cell was re-sorted");
            });
            suppressed += t.DriftSuppressedByDensity;
        }

        // The clusters were born at ~100 % of their cell, so every one of them exceeds the 0.25 floor on every tick; that the run detected nothing is the
        // density target's doing, and the counter is what says so.
        Assert.That(suppressed, Is.GreaterThan(0), "no cluster was ever above the constant floor, so the gate stayed shut for want of a candidate rather than by design");
        for (var c = 0; c < Cells; c++)
        {
            Assert.That(EntitiesInCell(dbe, c), Is.EqualTo(PerCell), $"cell {c} lost or gained entities");
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // D2 — repair is charged first; the budget raises the target instead of truncating the queue
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// AC-14.3, AC-14.4, AC-14.5 — a dense degraded cell is re-sorted by the PLANNER (the valve is off) at a budget that covers one unit, while a
    /// second cell floods the queue with more relocations than the remainder can pay for and a third population crosses a cell boundary on every tick:
    /// the unit is admitted, no unit is ever starved by relocations, relocations are refused rather than the unit, and every crossing executes.
    /// </summary>
    /// <remarks>
    /// <para><b>The estimate is pinned, so the budget arithmetic is exact rather than a property of the Debug build.</b> Before every fence the previous
    /// tick's migration counters are overwritten with a sample of exactly 10 000 ns per entity, which the seed of the same value admits unclamped; the
    /// model therefore reads 10 000 ns plus the planner's own small term, whatever the machine measures.</para>
    /// <para>Cell 0: 600 entities spawned across the whole cell — 10 clusters at ~90 % against a 0.49 target, so a repair candidate (gate 0.75) and, by D2,
    /// not a relocation candidate. One unit is its 8 worst clusters, ~480 entities ≈ 4.8 ms. Cell 1: 1 200 entities spawned across 60 % of the cell —
    /// 19 clusters at ~0.6 against a 0.35 target, so drift-gated and not repair-gated, with ~40 % of them beyond the ±0.175 box: ~500 drifters per tick,
    /// ≈ 5 ms of relocations. Budget 8 ms: the planner takes its 4.8 ms first, the throttle admits ~300 relocations with what is left and refuses the rest. Under
    /// the shipped order the relocations took all 8 ms and the unit was never begun.</para>
    /// </remarks>
    [Test]
    [VerifiesRule("TH-01")]
    public void RepairIsChargedBeforeRelocationAndCrossingsAlwaysExecute()
    {
        // No repair cooldown (RP-07): the budget binds only while the planner takes cell 0's unit on the same ticks the relocations flood in, which is
        // the planner re-sorting cell 0 on every tick. With the default 50-tick cooldown the unit runs on tick 2 and not again, the relocations then fit
        // the whole budget, and nothing is refused — measured: a unit on 11 of 11 ticks and relocations refused on 4 at cooldown 0; units on 2 and
        // refusals on none at 50. The charge ORDER is what this pins, so the cooldown is switched off rather than the scenario rebuilt around it.
        using var dbe = SetupEngine(budgetMs: 8.0f, seedNsPerEntity: 10_000f, repairCooldownTicks: 0);
        var state = ClusterStateOf(dbe);
        SpawnScattered(dbe, cells: 1, perCell: 600, fence: false);
        SpawnScattered(dbe, cells: 1, perCell: 1200, firstCell: 1, spread: 60, fence: false);

        // The crossers: 40 entities alternating between cells 3 and 4 on every tick.
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 40; i++)
            {
                tx.Spawn<ClMigUnit>(ClMigUnit.Pos.Set(PointAt(3 * CellSize + 50f, 10f + i, 10_000 + i)));
            }

            tx.Commit();
        }

        PinCostSample(state);
        dbe.WriteTickFence(1);

        // Asserted from tick 2, the tick with the most bite: tick 1's AabbRefresh files the scattered spawn's ~1 000 drifters at boost 1, and tick 2's
        // Prep is where the old order would have handed all of them the budget ahead of the unit. Later ticks are gentler — the boost has raised the
        // gate past cell 1's clusters by tick 4 — so starting later would let a revert of the order pass on the warm-up alone.
        var rng = new Random(1407);
        var repairedByPlanner = 0;
        var throttledTicks = 0;
        var crossingsSeen = 0;
        for (var tick = 2; tick <= 12; tick++)
        {
            JitterInPlace(dbe, rng, amplitude: 8f);
            MoveTheCrossers(dbe, tick);
            PinCostSample(state);
            dbe.WriteTickFence(tick);

            var t = dbe.GetSpatialTelemetry(ArchetypeId);
            Assert.Multiple(() =>
            {
                Assert.That(t.RepairValveFires, Is.Zero, $"tick {tick}: the valve is configured off and fired anyway");
                Assert.That(t.RepairBudgetStarvedNs, Is.Zero, $"tick {tick}: a repair unit was refused on a tick where relocations had spent the budget");
                Assert.That(t.MeasuredNsPerEntity, Is.InRange(10_000d, 11_500d),
                    $"tick {tick}: the estimate is not pinned — the migration sample must read 10 000 ns");
                // The crossers are the only mandatory requests that are not repairs; on a repair tick the count must not absorb the unit's requests.
                Assert.That(t.CrossingsQueued, Is.LessThanOrEqualTo(40), $"tick {tick}: CrossingsQueued ({t.CrossingsQueued}) counts repair requests");
                if (t.RepairUnitCount > 0)
                {
                    Assert.That(t.ReclusterBudgetUsedMs, Is.LessThanOrEqualTo(8.0d + 1e-6d),
                        $"tick {tick}: the planner committed {t.ReclusterBudgetUsedMs:F3} ms against an 8 ms budget with the valve off");
                    // What the throttle may then spend is what the planner left minus the crossings it must admit, at the MIGRATION price — the pinned
                    // 10 000 ns, not MeasuredNsPerEntity, which adds the planner's per-entity term that only a repaired entity pays. The admitted count
                    // is the proof, and the bound is exact: the engine admits floor((budget − repair − crossings × price) / price).
                    var relocationsAffordable = (int)((8.0d * 1_000_000d - t.ReclusterBudgetUsedMs * 1_000_000d) / 10_000d) - t.CrossingsQueued;
                    Assert.That(t.RelocationsAdmitted, Is.LessThanOrEqualTo(relocationsAffordable + 1),
                        $"tick {tick}: {t.RelocationsAdmitted} relocations admitted after a {t.ReclusterBudgetUsedMs:F2} ms repair and "
                        + $"{t.CrossingsQueued} crossings, against a remainder that pays for {relocationsAffordable}");
                }
            });

            if (t.RepairUnitCount > 0)
            {
                repairedByPlanner++;
            }

            if (t.RelocationsThrottled > 0)
            {
                throttledTicks++;
            }

            // The crossers alternate cells every tick, so the two cells' populations must swap: 40 in one, 0 in the other, whichever tick it is.
            var inCell3 = EntitiesInCell(dbe, 3);
            var inCell4 = EntitiesInCell(dbe, 4);
            Assert.That(inCell3 + inCell4, Is.EqualTo(40), $"tick {tick}: crossers went missing — cell 3 holds {inCell3}, cell 4 holds {inCell4}, "
                + $"{t.MigrationCount} migrations executed, {t.CrossingsQueued} crossings queued");
            Assert.That(tick % 2 == 1 ? inCell4 : inCell3, Is.EqualTo(40),
                $"tick {tick}: a crossing was refused — the repair charge must never displace a correctness move (cell 3 {inCell3}, cell 4 {inCell4})");
            crossingsSeen += t.CrossingsQueued;
        }

        Assert.Multiple(() =>
        {
            Assert.That(repairedByPlanner, Is.GreaterThan(0), "the planner never admitted a unit at a budget that covers one — relocations are still charged first");
            Assert.That(throttledTicks, Is.GreaterThan(0), "relocations were never refused, so the budget was not binding and the ordering above was not tested");
            Assert.That(crossingsSeen, Is.GreaterThan(0), "no crossing was ever queued, so nothing above tested the mandatory class");
        });
    }

    /// <summary>Overwrite last tick's migration sample so the next Prep folds exactly 10 000 ns per entity into the cost model.</summary>
    private static void PinCostSample(ArchetypeClusterState state)
    {
        state.LastTickMigrationCount = 1000;
        state.LastTickMigrationExecuteMs = 10d;
        state.LastTickMigrationApplyTicks = 0L;
    }

    /// <summary>Move the 40 crossers between cell 3 and cell 4, by resolving their ids so the write is a plain component write and the crossing detector files it.</summary>
    private static unsafe void MoveTheCrossers(DatabaseEngine dbe, int tick)
    {
        using var tx = dbe.CreateQuickTransaction();
        var accessor = tx.For<ClMigUnit>();
        try
        {
            var targetX = (tick % 2 == 1 ? 4 : 3) * CellSize + 50f;
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                var bits = cluster.OccupancyBits;
                while (bits != 0)
                {
                    var slot = BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    var current = cluster.GetReadOnly(ClMigUnit.Pos, slot);
                    if (current.Tag < 10_000)
                    {
                        continue;
                    }

                    cluster.WriteSpatial(ClMigUnit.Pos, slot, PointAt(targetX, current.Bounds.MinY, current.Tag));
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        tx.Commit();
    }

    /// <summary>
    /// The boost multiplies by one step per throttled tick, holds for four clean ticks, then decays one step, never drops below 1, is capped where the
    /// configured floor would itself reach the cell, and does nothing at all in constant mode.
    /// </summary>
    [Test]
    public void TheTargetBoostRisesOnRefusalAndDecaysAfterFourCleanTicks()
    {
        using var dbe = SetupEngine(budgetMs: 1.0f);
        var state = ClusterStateOf(dbe);
        ref readonly var cfg = ref dbe.Realm0Grid.Config;
        const float Step = ArchetypeClusterState.DriftTargetBoostStep;
        Assert.That(state.DriftTargetBoost, Is.EqualTo(1f));

        state.LastTickRelocationsThrottled = 5;
        state.UpdateDriftTargetBoost(in cfg);
        state.UpdateDriftTargetBoost(in cfg);
        Assert.That(state.DriftTargetBoost, Is.EqualTo(Step * Step).Within(1e-6f));

        state.LastTickRelocationsThrottled = 0;
        for (var i = 1; i < ArchetypeClusterState.DriftTargetBoostDecayTicks; i++)
        {
            state.UpdateDriftTargetBoost(in cfg);
            Assert.That(state.DriftTargetBoost, Is.EqualTo(Step * Step).Within(1e-6f), $"decayed after only {i} clean ticks");
        }

        state.UpdateDriftTargetBoost(in cfg);
        Assert.That(state.DriftTargetBoost, Is.EqualTo(Step).Within(1e-6f), "the fourth clean tick decays one step");

        for (var i = 0; i < 20; i++)
        {
            state.UpdateDriftTargetBoost(in cfg);
        }

        Assert.That(state.DriftTargetBoost, Is.EqualTo(1f), "the boost floors at 1");

        // The cap: 1 / ClusterTargetExtentRatio (4 at the default 0.25) — past it every target is off already.
        state.LastTickRelocationsThrottled = 5;
        for (var i = 0; i < 40; i++)
        {
            state.UpdateDriftTargetBoost(in cfg);
        }

        Assert.That(state.DriftTargetBoost, Is.EqualTo(1f / cfg.ClusterTargetExtentRatio).Within(1e-5f), "the boost is capped where the floor reaches the cell");

        // Constant mode: the resolver never reads the boost, so the controller must not accumulate one either.
        state.DriftTargetBoost = 1f;
        var constant = SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(WorldMax, WorldMax), CellSize, clusterTargetPackingSlack: 0f);
        for (var i = 0; i < 40; i++)
        {
            state.UpdateDriftTargetBoost(in constant);
        }

        Assert.That(state.DriftTargetBoost, Is.EqualTo(1f), "constant mode must leave the boost alone");
    }

    /// <summary>
    /// The relocation chooser keeps a running ledger of each candidate's free slots, so two drifters of one pass are not both sent to the one empty
    /// cluster — the mechanism behind the measured 91–99 % pin rejection.
    /// </summary>
    [Test]
    public void TheChooserDoesNotSendTwoDriftersToOneFreeSlot()
    {
        using var dbe = SetupEngine(budgetMs: 1.0f);
        var state = ClusterStateOf(dbe);

        var empty = ClusterSpatialAabb.Empty;
        var wide = new ClusterSpatialAabb { MinX = 0f, MinY = 0f, MinZ = float.PositiveInfinity, MaxX = 90f, MaxY = 90f, MaxZ = float.NegativeInfinity };
        var candidates = new List<ArchetypeClusterState.RelocationCandidate>
        {
            new(chunkId: 7, in wide, freeSlots: 5),
            new(chunkId: 9, in empty, freeSlots: 1),
        };

        // Outside the wide box, so admitting the point costs it real growth and the empty cluster's 0 is a strict win rather than a tie on chunk id.
        var first = state.ChooseRelocationTarget(candidates, 95f, 95f, 0f, flat: true);
        var second = state.ChooseRelocationTarget(candidates, 95f, 95f, 0f, flat: true);

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.EqualTo(9), "the empty cluster is enlargement 0 and wins the first drifter");
            Assert.That(second, Is.EqualTo(7), "its one slot is spent, so the second drifter goes to the next-best candidate rather than to a full pin");
            Assert.That(candidates[0].FreeSlots, Is.EqualTo(4));
            Assert.That(candidates[1].FreeSlots, Is.Zero);
        });
    }

    /// <summary>
    /// The per-entity drifter test uses the cell's density-derived target, not the configured constant. 1 024 entities in one flat cell resolve to a
    /// target of 0.375 × cell (half-box 18.75); spawned across a 40-unit band they exceed the gate but sit almost entirely inside the box, so the scan
    /// must find few drifters. Against the 0.25 constant (half-box 12.5) the same band reads ~40 % drifters.
    /// </summary>
    [Test]
    public void ThePerEntityBoxFollowsTheDensityTarget()
    {
        using var dbe = SetupEngine(budgetMs: 0f);   // no throttle, no boost: the raw detection count is the measurement
        SpawnScattered(dbe, cells: 1, perCell: 1024, spread: 40);

        var rng = new Random(1408);
        var detected = 0;
        var gated = 0;
        for (var tick = 2; tick <= 4; tick++)
        {
            JitterInPlace(dbe, rng, amplitude: 2f);
            dbe.WriteTickFence(tick);
            var t = dbe.GetSpatialTelemetry(ArchetypeId);
            detected += t.DriftersDetected;
            gated += t.DriftGatedClusters;
        }

        Assert.Multiple(() =>
        {
            Assert.That(gated, Is.GreaterThan(0), "no cluster was gated, so nothing below is about the per-entity box");
            Assert.That(detected, Is.LessThan(3 * 1024 * 15 / 100),
                $"{detected} drifters over three ticks of 1 024 entities inside a band the density box should contain — the per-entity test is on the constant");
        });
    }

    /// <summary>
    /// A drifter whose cell has candidates but no free slot is filed for a fresh cluster and executes there — never back into the source through first
    /// fit — so a full cell gains clusters rather than churning entities between the boxes it already has.
    /// </summary>
    [Test]
    public void ASpilledDrifterLandsInAFreshCluster()
    {
        using var dbe = SetupEngine(budgetMs: 0f);   // no throttle: every filed relocation executes
        // 1 270 entities fill 20 clusters but for ten slots; target 0.335 × cell, a 40-unit band exceeds it, so the first ten drifters of a pass take the
        // ten slots and every later one finds the ledger spent. (A cell with NO free slot at all offers no candidate and its drifters stay unplaced —
        // CR-03's "drifters and no migrations" signal; the spill is for the pass that ran out of room, not the cell that never had any.)
        SpawnScattered(dbe, cells: 1, perCell: 1270, spread: 40);
        var before = dbe.GetSpatialTelemetry(ArchetypeId).ActiveClusterCount;

        var rng = new Random(1409);
        JitterInPlace(dbe, rng, amplitude: 30f);
        dbe.WriteTickFence(2);
        var spilled = dbe.GetSpatialTelemetry(ArchetypeId).DriftersSpilled;   // filed by tick 2's AabbRefresh
        JitterInPlace(dbe, rng, amplitude: 30f);
        dbe.WriteTickFence(3);   // executed by tick 3's Migrate

        var t = dbe.GetSpatialTelemetry(ArchetypeId);
        Assert.Multiple(() =>
        {
            Assert.That(spilled, Is.GreaterThan(0), "no drifter was spilled in a cell with no free slot");
            Assert.That(t.MigrationCount, Is.GreaterThan(0), "no spilled relocation executed");
            Assert.That(t.ActiveClusterCount, Is.GreaterThan(before),
                $"the cell held {before} full clusters and {t.MigrationCount} relocations executed without allocating one — they went back into their sources");
            Assert.That(EntitiesInCell(dbe, 0), Is.EqualTo(1270), "an entity left the cell or was lost");
        });
    }

    /// <summary>The migration cost sample is divided by the fence's measured parallelism, and a parallelism below 1 is treated as 1.</summary>
    /// <remarks>One engine, three observations: the first sample seeds the EWMA outright, each later one blends in at alpha 0.25, so the expected values
    /// are exact arithmetic on the constants rather than a tolerance band.</remarks>
    [Test]
    public void TheCostModelChargesSpanNotSummedCpu()
    {
        using var dbe = SetupEngine(budgetMs: 1.0f);
        var state = ClusterStateOf(dbe);
        ref readonly var cfg = ref dbe.Realm0Grid.Config;

        state.LastTickMigrationCount = 1000;
        state.LastTickMigrationExecuteMs = 4.0d;   // 4 000 ns of summed CPU per entity
        state.ObserveMigrationCost(in cfg, parallelism: 8d);
        Assert.That(state.LastTickMeasuredNsPerEntity, Is.EqualTo(500d).Within(1d), "eight-way: the frame pays an eighth of the summed CPU");

        state.LastTickMigrationCount = 1000;
        state.LastTickMigrationExecuteMs = 4.0d;
        state.ObserveMigrationCost(in cfg, parallelism: 1d);
        Assert.That(state.LastTickMeasuredNsPerEntity, Is.EqualTo(0.75d * 500d + 0.25d * 4000d).Within(1d), "serial: the sample is the summed CPU, blended in");

        state.LastTickMigrationCount = 1000;
        state.LastTickMigrationExecuteMs = 4.0d;
        state.ObserveMigrationCost(in cfg, parallelism: 0.5d);
        Assert.That(state.LastTickMeasuredNsPerEntity, Is.EqualTo(0.75d * 1375d + 0.25d * 4000d).Within(1d), "a parallelism below 1 is clamped to 1");
    }
}
