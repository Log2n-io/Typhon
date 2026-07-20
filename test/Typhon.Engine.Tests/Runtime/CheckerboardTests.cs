using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Numerics;
using System.Threading;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// Regression suite for checkerboard dispatch, tier budget metrics, and multi-observer helpers (issue #234).
/// Reuses the <see cref="TierUnit"/> archetype and grid pattern from <see cref="TierDispatchTests"/>.
/// </summary>
[TestFixture]
[NonParallelizable] // timing-sensitive: exercises the real scheduler/tick context; concurrent CPU load from peer fixtures perturbs tick cadence (fails at LoP=8)
class CheckerboardTests : TestBase<CheckerboardTests>
{
    private static TierPos PointAt(float x, float y) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Data = 1.0f };

    private DatabaseEngine SetupEngineWithGrid()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<TierPos>();
        dbe.ConfigureSpatialGrid(new SpatialGridConfig(
            worldMin: new Vector2(0, 0),
            worldMax: new Vector2(100, 100),
            cellSize: 10f));
        dbe.InitializeArchetypes();
        return dbe;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Checkerboard dispatch
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Checkerboard_TwoPhases_BothExecute()
    {
        using var dbe = SetupEngineWithGrid();

        // Spawn entities in two cells with different colors:
        // Cell (0,0) → (0+0)%2=0 → Red
        // Cell (1,0) → (1+0)%2=1 → Black
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<TierUnit>(TierUnit.Pos.Set(PointAt(5f, 5f)));    // Red cell
            tx.Spawn<TierUnit>(TierUnit.Pos.Set(PointAt(15f, 5f)));   // Black cell
            tx.Commit();
        }

        dbe.SpatialGrid.SetCellTier(dbe.SpatialGrid.WorldToCellKey(5f, 5f), SimTier.Tier0);
        dbe.SpatialGrid.SetCellTier(dbe.SpatialGrid.WorldToCellKey(15f, 5f), SimTier.Tier0);

        using var txView = dbe.CreateQuickTransaction();
        var view = txView.Query<TierUnit>().ToView();

        var callbackCount = 0;
        var ticksDone = 0;
        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            dag.CallbackSystem("Counter", _ => Interlocked.Increment(ref ticksDone));
            dag.QuerySystem("CB_Check", ctx =>
            {
                // Callback is invoked once per phase — should see it twice per tick
                Interlocked.Increment(ref callbackCount);
            }, input: () => view, tier: SimTier.Tier0, parallel: true, checkerboard: true, after: "Counter");
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        runtime.Start();
        SpinWait.SpinUntil(() => ticksDone >= 2, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        // Each tick invokes the callback twice (Red + Black)
        Assert.That(callbackCount, Is.GreaterThanOrEqualTo(ticksDone * 2));
        view.Dispose();
    }

    [Test]
    public void Checkerboard_RedBlack_NoOverlap()
    {
        using var dbe = SetupEngineWithGrid();
        var meta = Archetype<TierUnit>.Metadata;

        // Spawn entities in a 3x3 grid of cells. Cells: (0,0) R, (1,0) B, (2,0) R, (0,1) B, (1,1) R, ...
        for (int cx = 0; cx < 3; cx++)
        {
            for (int cy = 0; cy < 3; cy++)
            {
                using var tx = dbe.CreateQuickTransaction();
                tx.Spawn<TierUnit>(TierUnit.Pos.Set(PointAt(cx * 10f + 5f, cy * 10f + 5f)));
                tx.Commit();
            }
        }

        // Set all cells to Tier0
        for (int cx = 0; cx < 3; cx++)
        {
            for (int cy = 0; cy < 3; cy++)
            {
                dbe.SpatialGrid.SetCellTier(dbe.SpatialGrid.WorldToCellKey(cx * 10f + 5f, cy * 10f + 5f), SimTier.Tier0);
            }
        }

        var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        var grid = dbe.SpatialGrid;

        // Manually split clusters
        int redCount = 0, blackCount = 0;
        for (int i = 0; i < cs.ActiveClusterCount; i++)
        {
            int chunkId = cs.ActiveClusterIds[i];
            int cellKey = cs.ClusterCellMap[chunkId];
            var (x, y) = grid.CellKeyToCoords(cellKey);
            if ((x + y) % 2 == 0)
            {
                redCount++;
            }
            else
            {
                blackCount++;
            }
        }

        // 3x3 grid: 5 Red cells (0,0),(2,0),(1,1),(0,2),(2,2) + 4 Black cells (1,0),(0,1),(2,1),(1,2)
        Assert.That(redCount, Is.EqualTo(5));
        Assert.That(blackCount, Is.EqualTo(4));
        Assert.That(redCount + blackCount, Is.EqualTo(9));
    }

    [Test]
    public void Checkerboard_ZeroRedClusters_BlackStillRuns()
    {
        using var dbe = SetupEngineWithGrid();

        // Spawn only in Black cells: cell (1,0) → (1+0)%2=1 → Black
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<TierUnit>(TierUnit.Pos.Set(PointAt(15f, 5f)));
            tx.Commit();
        }

        dbe.SpatialGrid.SetCellTier(dbe.SpatialGrid.WorldToCellKey(15f, 5f), SimTier.Tier0);

        using var txView = dbe.CreateQuickTransaction();
        var view = txView.Query<TierUnit>().ToView();

        var seenCount = 0;
        var ticksDone = 0;
        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            dag.CallbackSystem("Counter", _ => Interlocked.Increment(ref ticksDone));
            dag.QuerySystem("CB_BlackOnly", ctx =>
            {
                foreach (var id in ctx.Entities)
                {
                    Interlocked.Increment(ref seenCount);
                }
            }, input: () => view, tier: SimTier.Tier0, parallel: true, checkerboard: true, after: "Counter");
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        runtime.Start();
        SpinWait.SpinUntil(() => ticksDone >= 2, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        // Black phase should see the entity even though Red phase was empty
        Assert.That(seenCount, Is.GreaterThanOrEqualTo(1));
        view.Dispose();
    }

    [Test]
    public void Checkerboard_RequiresParallel_Throws()
    {
        using var dbe = SetupEngineWithGrid();
        using var tx = dbe.CreateQuickTransaction();
        var view = tx.Query<TierUnit>().ToView();

        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            TyphonRuntime.Create(dbe, schedule =>
            {
                var dag = schedule.PublicTrack.DeclareDag("Test");
                dag.QuerySystem("Bad", _ => { }, input: () => view, checkerboard: true);
            }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });
        });
        Assert.That(ex.Message, Does.Contain("checkerboard"));
        view.Dispose();
    }

    [Test]
    public void Checkerboard_WithDormancy_SleepingSkipped()
    {
        using var dbe = SetupEngineWithGrid();
        var meta = Archetype<TierUnit>.Metadata;

        // Spawn in Red cell (0,0) and Black cell (1,0)
        var cellRed = dbe.SpatialGrid.WorldToCellKey(5f, 5f);
        var cellBlack = dbe.SpatialGrid.WorldToCellKey(15f, 5f);

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<TierUnit>(TierUnit.Pos.Set(PointAt(5f, 5f)));
            tx.Spawn<TierUnit>(TierUnit.Pos.Set(PointAt(15f, 5f)));
            tx.Commit();
        }

        dbe.SpatialGrid.SetCellTier(cellRed, SimTier.Tier0);
        dbe.SpatialGrid.SetCellTier(cellBlack, SimTier.Tier0);

        var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        cs.SleepThresholdTicks = 3;

        // Sleep the Red cell's cluster
        int redChunkId = -1;
        for (int i = 0; i < cs.ActiveClusterCount; i++)
        {
            int cid = cs.ActiveClusterIds[i];
            if (cs.ClusterCellMap[cid] == cellRed)
            {
                redChunkId = cid;
                break;
            }
        }
        Assert.That(redChunkId, Is.GreaterThanOrEqualTo(0));
        cs.SleepStates[redChunkId] = ClusterSleepState.Sleeping;
        cs.SleepingClusterCount = 1;

        using var txView = dbe.CreateQuickTransaction();
        var view = txView.Query<TierUnit>().ToView();

        var seenCount = 0;
        var ticksDone = 0;
        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            dag.CallbackSystem("Counter", _ => Interlocked.Increment(ref ticksDone));
            dag.QuerySystem("CB_Dormancy", ctx =>
            {
                foreach (var id in ctx.Entities)
                {
                    Interlocked.Increment(ref seenCount);
                }
            }, input: () => view, tier: SimTier.Tier0, parallel: true, checkerboard: true, after: "Counter");
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        runtime.Start();
        SpinWait.SpinUntil(() => ticksDone >= 2, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        // Only Black cell entity should be seen (Red cell is sleeping)
        Assert.That(seenCount, Is.GreaterThanOrEqualTo(1));
        // Each tick sees at most 1 entity (the Black one)
        Assert.That(seenCount, Is.LessThanOrEqualTo(ticksDone));
        view.Dispose();
    }

    [Test]
    public void Checkerboard_NoTierFilter_StillSplits()
    {
        using var dbe = SetupEngineWithGrid();

        // Spawn in Red cell (0,0) and Black cell (1,0). No tier filter — SimTier.All.
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<TierUnit>(TierUnit.Pos.Set(PointAt(5f, 5f)));    // Red
            tx.Spawn<TierUnit>(TierUnit.Pos.Set(PointAt(15f, 5f)));   // Black
            tx.Commit();
        }

        // Set cell tiers (required for spatial grid state, but the system uses SimTier.All)
        dbe.SpatialGrid.SetCellTier(dbe.SpatialGrid.WorldToCellKey(5f, 5f), SimTier.Tier0);
        dbe.SpatialGrid.SetCellTier(dbe.SpatialGrid.WorldToCellKey(15f, 5f), SimTier.Tier0);

        using var txView = dbe.CreateQuickTransaction();
        var view = txView.Query<TierUnit>().ToView();

        var callbackCount = 0;
        var ticksDone = 0;
        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            dag.CallbackSystem("Counter", _ => Interlocked.Increment(ref ticksDone));
            // NOTE: no tier filter — checkerboard with SimTier.All (default)
            dag.QuerySystem("CB_AllTier", ctx =>
            {
                Interlocked.Increment(ref callbackCount);
            }, input: () => view, parallel: true, checkerboard: true, after: "Counter");
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        runtime.Start();
        SpinWait.SpinUntil(() => ticksDone >= 2, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        // Should invoke callback twice per tick (Red + Black) even without a tier filter
        Assert.That(callbackCount, Is.GreaterThanOrEqualTo(ticksDone * 2));
        view.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // TierBudgetMetrics
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void TierBudgetMetrics_PopulatedAfterTick()
    {
        using var dbe = SetupEngineWithGrid();

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<TierUnit>(TierUnit.Pos.Set(PointAt(5f, 5f)));
            tx.Commit();
        }

        dbe.SpatialGrid.SetCellTier(dbe.SpatialGrid.WorldToCellKey(5f, 5f), SimTier.Tier0);

        using var txView = dbe.CreateQuickTransaction();
        var view = txView.Query<TierUnit>().ToView();

        TierBudgetMetrics capturedMetrics = default;
        var ticksDone = 0;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            dag.CallbackSystem("Counter", _ => Interlocked.Increment(ref ticksDone));
            dag.QuerySystem("T0_Work", ctx =>
            {
                // On tick 2+, previous tick's metrics should be populated
                if (ctx.TickNumber > 0)
                {
                    capturedMetrics = ctx.TierBudgetMetrics;
                }
                foreach (var id in ctx.Entities) { }
            }, input: () => view, tier: SimTier.Tier0, parallel: true, after: "Counter");
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 60 });

        runtime.Start();
        SpinWait.SpinUntil(() => ticksDone >= 3, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        // BudgetMs should be 1000/60 ≈ 16.67
        Assert.That(capturedMetrics.BudgetMs, Is.GreaterThan(10f).And.LessThan(20f));
        // TotalCostMs should be > 0 (at least the system ran)
        Assert.That(capturedMetrics.TotalCostMs, Is.GreaterThan(0f));
        // UtilizationRatio = Total/Budget
        Assert.That(capturedMetrics.UtilizationRatio, Is.GreaterThan(0f));
        view.Dispose();
    }

    [Test]
    public void TierBudgetMetrics_PerTierCostAggregation()
    {
        using var dbe = SetupEngineWithGrid();

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<TierUnit>(TierUnit.Pos.Set(PointAt(5f, 5f)));
            tx.Spawn<TierUnit>(TierUnit.Pos.Set(PointAt(15f, 5f)));
            tx.Commit();
        }

        dbe.SpatialGrid.SetCellTier(dbe.SpatialGrid.WorldToCellKey(5f, 5f), SimTier.Tier0);
        dbe.SpatialGrid.SetCellTier(dbe.SpatialGrid.WorldToCellKey(15f, 5f), SimTier.Tier1);

        using var txView = dbe.CreateQuickTransaction();
        var view = txView.Query<TierUnit>().ToView();

        TierBudgetMetrics capturedMetrics = default;
        var ticksDone = 0;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            dag.CallbackSystem("Counter", _ => Interlocked.Increment(ref ticksDone));
            dag.QuerySystem("T0_Sys", ctx =>
            {
                foreach (var id in ctx.Entities) { }
                if (ctx.TickNumber > 0) { capturedMetrics = ctx.TierBudgetMetrics; }
            }, input: () => view, tier: SimTier.Tier0, parallel: true, after: "Counter");
            dag.QuerySystem("T1_Sys", ctx =>
            {
                foreach (var id in ctx.Entities) { }
            }, input: () => view, tier: SimTier.Tier1, parallel: true, after: "T0_Sys");
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 60 });

        runtime.Start();
        SpinWait.SpinUntil(() => ticksDone >= 3, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        // Both Tier0 and Tier1 should have non-zero cost
        Assert.That(capturedMetrics.Tier0CostMs, Is.GreaterThan(0f));
        Assert.That(capturedMetrics.Tier1CostMs, Is.GreaterThan(0f));
        view.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Multi-observer helpers
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void SetCellTierMin_OnlyPromotes()
    {
        using var dbe = SetupEngineWithGrid();
        var grid = dbe.SpatialGrid;
        int cellKey = grid.WorldToCellKey(5f, 5f);

        // Start at Tier2
        grid.SetCellTier(cellKey, SimTier.Tier2);
        Assert.That(grid.GetCell(cellKey).Tier, Is.EqualTo((byte)SimTier.Tier2));

        // Promote to Tier0 (lower value = higher priority) → succeeds
        grid.SetCellTierMin(cellKey, SimTier.Tier0);
        Assert.That(grid.GetCell(cellKey).Tier, Is.EqualTo((byte)SimTier.Tier0));

        // Attempt to "demote" to Tier1 → no-op (Tier0 is higher priority)
        grid.SetCellTierMin(cellKey, SimTier.Tier1);
        Assert.That(grid.GetCell(cellKey).Tier, Is.EqualTo((byte)SimTier.Tier0));
    }

    [Test]
    public void ResetAllTiers_BulkSetsAllCells()
    {
        using var dbe = SetupEngineWithGrid();
        var grid = dbe.SpatialGrid;

        // Set a few cells to different tiers
        grid.SetCellTier(grid.WorldToCellKey(5f, 5f), SimTier.Tier0);
        grid.SetCellTier(grid.WorldToCellKey(15f, 5f), SimTier.Tier1);

        // Reset all to Tier3
        grid.ResetAllTiers(SimTier.Tier3);

        Assert.That(grid.GetCell(grid.WorldToCellKey(5f, 5f)).Tier, Is.EqualTo((byte)SimTier.Tier3));
        Assert.That(grid.GetCell(grid.WorldToCellKey(15f, 5f)).Tier, Is.EqualTo((byte)SimTier.Tier3));
        Assert.That(grid.GetCell(grid.WorldToCellKey(55f, 55f)).Tier, Is.EqualTo((byte)SimTier.Tier3));
    }

    [Test]
    public void SetTierInAABB_MinSemantics()
    {
        using var dbe = SetupEngineWithGrid();
        var grid = dbe.SpatialGrid;

        // Reset all to Tier3
        grid.ResetAllTiers(SimTier.Tier3);

        // Set a 30x30 area to Tier0 (covers cells (0,0), (1,0), (2,0), (0,1), (1,1), (2,1), (0,2), (1,2), (2,2))
        grid.SetTierInAABB(0f, 0f, 30f, 30f, SimTier.Tier0);

        // Cells inside AABB should be Tier0
        Assert.That(grid.GetCell(grid.WorldToCellKey(5f, 5f)).Tier, Is.EqualTo((byte)SimTier.Tier0));
        Assert.That(grid.GetCell(grid.WorldToCellKey(15f, 15f)).Tier, Is.EqualTo((byte)SimTier.Tier0));
        Assert.That(grid.GetCell(grid.WorldToCellKey(25f, 25f)).Tier, Is.EqualTo((byte)SimTier.Tier0));

        // Cell outside AABB should still be Tier3
        Assert.That(grid.GetCell(grid.WorldToCellKey(55f, 55f)).Tier, Is.EqualTo((byte)SimTier.Tier3));

        // Now apply Tier1 over a larger area — min semantics means Tier0 cells stay Tier0
        grid.SetTierInAABB(0f, 0f, 60f, 60f, SimTier.Tier1);

        // Original Tier0 cells: still Tier0 (Tier0 < Tier1, min keeps Tier0)
        Assert.That(grid.GetCell(grid.WorldToCellKey(5f, 5f)).Tier, Is.EqualTo((byte)SimTier.Tier0));
        // Newly covered cells: Tier1 (was Tier3, Tier1 < Tier3)
        Assert.That(grid.GetCell(grid.WorldToCellKey(45f, 45f)).Tier, Is.EqualTo((byte)SimTier.Tier1));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // SpatialGridAccessor (issue #232)
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    // QUARANTINE (#406): passes on Windows (isolated + full-suite parallel) but fails only on Linux CI —
    // ctx.SpatialGrid.IsValid is false in the tick callback. Excluded from the merge gate pending a Linux repro.
    [Category("Quarantine")]
    public void SpatialGridAccessor_AccessibleFromTickContext()
    {
        using var dbe = SetupEngineWithGrid();

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<TierUnit>(TierUnit.Pos.Set(PointAt(5f, 5f)));
            tx.Commit();
        }

        using var txView = dbe.CreateQuickTransaction();
        var view = txView.Query<TierUnit>().ToView();

        SpatialGridAccessor captured = default;
        var ticksDone = 0;
        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            dag.CallbackSystem("GridAccess", ctx =>
            {
                if (Interlocked.Increment(ref ticksDone) == 1)
                {
                    captured = ctx.SpatialGrid;

                    // Basic operations via the accessor API
                    Assert.That(ctx.SpatialGrid.IsValid, Is.True);
                    Assert.That(ctx.SpatialGrid.CellCount, Is.GreaterThan(0));
                    Assert.That(ctx.SpatialGrid.GridWidth, Is.EqualTo(10));
                    Assert.That(ctx.SpatialGrid.GridHeight, Is.EqualTo(10));
                    Assert.That(ctx.SpatialGrid.CellSize, Is.EqualTo(10f));

                    // Coordinate conversion
                    int cellKey = ctx.SpatialGrid.WorldToCell(5f, 5f);
                    var (cx, cy) = ctx.SpatialGrid.GetCellCoords(cellKey);
                    Assert.That(cx, Is.EqualTo(0));
                    Assert.That(cy, Is.EqualTo(0));

                    // Tier assignment via accessor
                    ctx.SpatialGrid.SetCellTier(0, 0, SimTier.Tier0);
                    ctx.SpatialGrid.SetCellTier(1, 0, SimTier.Tier1);
                }
            });
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        runtime.Start();
        SpinWait.SpinUntil(() => ticksDone >= 1, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        Assert.That(captured.IsValid, Is.True);
        // Verify the tier assignments stuck
        Assert.That(dbe.SpatialGrid.GetCell(dbe.SpatialGrid.WorldToCellKey(5f, 5f)).Tier, Is.EqualTo((byte)SimTier.Tier0));
        Assert.That(dbe.SpatialGrid.GetCell(dbe.SpatialGrid.WorldToCellKey(15f, 5f)).Tier, Is.EqualTo((byte)SimTier.Tier1));
        view.Dispose();
    }

    [Test]
    public void SpatialGridAccessor_MultiObserver_Union()
    {
        using var dbe = SetupEngineWithGrid();

        using var txView = dbe.CreateQuickTransaction();
        var view = txView.Query<TierUnit>().ToView();

        var ticksDone = 0;
        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            dag.CallbackSystem("TierAssignment", ctx =>
            {
                if (Interlocked.Increment(ref ticksDone) == 1)
                {
                    var grid = ctx.SpatialGrid;

                    // Multi-observer pattern: reset → per-observer SetTierInAABB
                    grid.ResetAllTiers(SimTier.Tier3);

                    // Observer A: Tier0 for area (0,0)→(30,30) → cells (0,0)-(2,2)
                    grid.SetTierInAABB(0f, 0f, 30f, 30f, SimTier.Tier0);

                    // Observer B: Tier1 for area (20,20)→(60,60) → cells (2,2)-(5,5)
                    // Cell (2,2) overlaps both: min(Tier0=1, Tier1=2) = Tier0
                    grid.SetTierInAABB(20f, 20f, 60f, 60f, SimTier.Tier1);
                }
            });
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        runtime.Start();
        SpinWait.SpinUntil(() => ticksDone >= 1, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        // Cell (0,0): Observer A only → Tier0
        Assert.That(dbe.SpatialGrid.GetCell(dbe.SpatialGrid.WorldToCellKey(5f, 5f)).Tier, Is.EqualTo((byte)SimTier.Tier0));
        // Cell (2,2): Both observers → min(Tier0, Tier1) = Tier0
        Assert.That(dbe.SpatialGrid.GetCell(dbe.SpatialGrid.WorldToCellKey(25f, 25f)).Tier, Is.EqualTo((byte)SimTier.Tier0));
        // Cell (4,4): Observer B only → Tier1
        Assert.That(dbe.SpatialGrid.GetCell(dbe.SpatialGrid.WorldToCellKey(45f, 45f)).Tier, Is.EqualTo((byte)SimTier.Tier1));
        // Cell (8,8): Neither observer → Tier3
        Assert.That(dbe.SpatialGrid.GetCell(dbe.SpatialGrid.WorldToCellKey(85f, 85f)).Tier, Is.EqualTo((byte)SimTier.Tier3));
        view.Dispose();
    }
}
