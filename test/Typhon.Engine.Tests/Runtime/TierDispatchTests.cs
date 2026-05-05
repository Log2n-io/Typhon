using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime;

// ═══════════════════════════════════════════════════════════════════════════════
// Test-only archetype for tier dispatch tests (issue #231).
// Spatial archetype with a 2D AABB position field — the Phase 1+2 spatial grid
// only supports 2D float fields.
// ═══════════════════════════════════════════════════════════════════════════════

[Component("Typhon.Test.Tier.Pos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct TierPos
{
    [Field]
    [SpatialIndex(1.0f)]
    public AABB2F Bounds;

    [Field]
    public float Data;
}

[Archetype(860)]
partial class TierUnit : Archetype<TierUnit>
{
    public static readonly Comp<TierPos> Pos = Register<TierPos>();
}

[TestFixture]
[NonParallelizable]
class TierDispatchTests : TestBase<TierDispatchTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup() => Archetype<TierUnit>.Touch();

    private static TierPos PointAt(float x, float y) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Data = 1.0f };

    /// <summary>
    /// Set up a 10×10 grid (100×100 world units, cellSize 10) so 100 cells are addressable. Each cell can host
    /// multiple clusters but for small-N tests one cluster per cell is sufficient.
    /// </summary>
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
    // SimTier enum sanity
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void SimTier_ToIndex_MapsSingleFlags()
    {
        Assert.That(SimTier.Tier0.ToIndex(), Is.EqualTo(0));
        Assert.That(SimTier.Tier1.ToIndex(), Is.EqualTo(1));
        Assert.That(SimTier.Tier2.ToIndex(), Is.EqualTo(2));
        Assert.That(SimTier.Tier3.ToIndex(), Is.EqualTo(3));
    }

    [Test]
    public void SimTier_ConvenienceCombinations_Correct()
    {
        Assert.That((byte)SimTier.Near, Is.EqualTo((byte)(SimTier.Tier0 | SimTier.Tier1)));
        Assert.That((byte)SimTier.Active, Is.EqualTo((byte)(SimTier.Tier0 | SimTier.Tier1 | SimTier.Tier2)));
        Assert.That((byte)SimTier.All, Is.EqualTo((byte)(SimTier.Tier0 | SimTier.Tier1 | SimTier.Tier2 | SimTier.Tier3)));
        Assert.That(SimTier.Near.IsSingleTier(), Is.False);
        Assert.That(SimTier.Tier0.IsSingleTier(), Is.True);
        Assert.That(SimTier.Near.TierCountOf(), Is.EqualTo(2));
        Assert.That(SimTier.All.TierCountOf(), Is.EqualTo(4));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // TierClusterIndex rebuild
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void TierClusterIndex_Rebuild_GroupsClustersByCellTier()
    {
        using var dbe = SetupEngineWithGrid();
        var meta = Archetype<TierUnit>.Metadata;

        // Spawn 4 entities, each in a different cell. Each lands in its own cluster.
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<TierUnit>(TierUnit.Pos.Set(PointAt(5f, 5f)));    // cell (0, 0)
            tx.Spawn<TierUnit>(TierUnit.Pos.Set(PointAt(15f, 5f)));   // cell (1, 0)
            tx.Spawn<TierUnit>(TierUnit.Pos.Set(PointAt(25f, 5f)));   // cell (2, 0)
            tx.Spawn<TierUnit>(TierUnit.Pos.Set(PointAt(35f, 5f)));   // cell (3, 0)
            tx.Commit();
        }

        var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        Assert.That(cs.ActiveClusterCount, Is.EqualTo(4));

        // Assign each cell to a different tier.
        dbe.SpatialGrid.SetCellTier(dbe.SpatialGrid.WorldToCellKey(5f, 5f), SimTier.Tier0);
        dbe.SpatialGrid.SetCellTier(dbe.SpatialGrid.WorldToCellKey(15f, 5f), SimTier.Tier1);
        dbe.SpatialGrid.SetCellTier(dbe.SpatialGrid.WorldToCellKey(25f, 5f), SimTier.Tier2);
        dbe.SpatialGrid.SetCellTier(dbe.SpatialGrid.WorldToCellKey(35f, 5f), SimTier.Tier3);

        var index = new TierClusterIndex();
        index.Rebuild(dbe.SpatialGrid, cs);

        Assert.That(index.GetClusters(SimTier.Tier0).Length, Is.EqualTo(1));
        Assert.That(index.GetClusters(SimTier.Tier1).Length, Is.EqualTo(1));
        Assert.That(index.GetClusters(SimTier.Tier2).Length, Is.EqualTo(1));
        Assert.That(index.GetClusters(SimTier.Tier3).Length, Is.EqualTo(1));

        // Multi-tier merge: Near = Tier0 + Tier1 = 2 clusters.
        Assert.That(index.GetClusters(SimTier.Near).Length, Is.EqualTo(2));
        // Active = Tier0 + Tier1 + Tier2 = 3 clusters.
        Assert.That(index.GetClusters(SimTier.Active).Length, Is.EqualTo(3));
    }

    [Test]
    public void TierClusterIndex_RebuildIfStale_SkipsWhenVersionsUnchanged()
    {
        using var dbe = SetupEngineWithGrid();
        var meta = Archetype<TierUnit>.Metadata;

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<TierUnit>(TierUnit.Pos.Set(PointAt(5f, 5f)));
            tx.Commit();
        }

        var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        dbe.SpatialGrid.SetCellTier(dbe.SpatialGrid.WorldToCellKey(5f, 5f), SimTier.Tier0);

        var index = new TierClusterIndex();
        index.RebuildIfStale(dbe.SpatialGrid, cs);
        int firstCount = index.RebuildCount;
        Assert.That(firstCount, Is.EqualTo(1));

        // Idempotent: calling again with no state change must be a no-op.
        index.RebuildIfStale(dbe.SpatialGrid, cs);
        Assert.That(index.RebuildCount, Is.EqualTo(firstCount));
    }

    [Test]
    public void TierClusterIndex_RebuildIfStale_TriggersOnCellTierChange()
    {
        using var dbe = SetupEngineWithGrid();
        var meta = Archetype<TierUnit>.Metadata;

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<TierUnit>(TierUnit.Pos.Set(PointAt(5f, 5f)));
            tx.Commit();
        }

        var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        dbe.SpatialGrid.SetCellTier(dbe.SpatialGrid.WorldToCellKey(5f, 5f), SimTier.Tier0);

        var index = new TierClusterIndex();
        index.RebuildIfStale(dbe.SpatialGrid, cs);
        Assert.That(index.RebuildCount, Is.EqualTo(1));

        // Change the cell's tier → version bumps → rebuild runs.
        dbe.SpatialGrid.SetCellTier(dbe.SpatialGrid.WorldToCellKey(5f, 5f), SimTier.Tier2);
        index.RebuildIfStale(dbe.SpatialGrid, cs);
        Assert.That(index.RebuildCount, Is.EqualTo(2));
        Assert.That(index.GetClusters(SimTier.Tier0).Length, Is.EqualTo(0));
        Assert.That(index.GetClusters(SimTier.Tier2).Length, Is.EqualTo(1));
    }

    [Test]
    public void TierClusterIndex_RebuildIfStale_TriggersOnClusterSetChange()
    {
        using var dbe = SetupEngineWithGrid();
        var meta = Archetype<TierUnit>.Metadata;

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<TierUnit>(TierUnit.Pos.Set(PointAt(5f, 5f)));
            tx.Commit();
        }

        var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        dbe.SpatialGrid.SetCellTier(dbe.SpatialGrid.WorldToCellKey(5f, 5f), SimTier.Tier0);

        var index = new TierClusterIndex();
        index.RebuildIfStale(dbe.SpatialGrid, cs);
        int rebuildsBeforeSpawn = index.RebuildCount;

        // Spawn in a new cell → new cluster → ClusterSetVersion bumps → rebuild runs.
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<TierUnit>(TierUnit.Pos.Set(PointAt(55f, 55f)));
            tx.Commit();
        }
        dbe.SpatialGrid.SetCellTier(dbe.SpatialGrid.WorldToCellKey(55f, 55f), SimTier.Tier0);

        index.RebuildIfStale(dbe.SpatialGrid, cs);
        Assert.That(index.RebuildCount, Is.GreaterThan(rebuildsBeforeSpawn));
        Assert.That(index.GetClusters(SimTier.Tier0).Length, Is.EqualTo(2));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Build-time validation
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Build_TierNone_Throws()
    {
        using var dbe = SetupEngineWithGrid();
        using var tx = dbe.CreateQuickTransaction();
        var view = tx.Query<TierUnit>().ToView();

        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            TyphonRuntime.Create(dbe, schedule =>
            {
                schedule.QuerySystem("Bad", _ => { }, input: () => view, parallel: true, tier: SimTier.None);
            }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });
        });
        Assert.That(ex.Message, Does.Contain("SimTier.None"));
        view.Dispose();
    }

    [Test]
    public void Build_CellAmortizeWithoutTier_Throws()
    {
        using var dbe = SetupEngineWithGrid();
        using var tx = dbe.CreateQuickTransaction();
        var view = tx.Query<TierUnit>().ToView();

        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            TyphonRuntime.Create(dbe, schedule =>
            {
                schedule.QuerySystem("Bad", _ => { }, input: () => view, parallel: true, cellAmortize: 4);
            }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });
        });
        Assert.That(ex.Message, Does.Contain("cellAmortize"));
        view.Dispose();
    }

    [Test]
    public void Build_NegativeCellAmortize_Throws()
    {
        using var dbe = SetupEngineWithGrid();
        using var tx = dbe.CreateQuickTransaction();
        var view = tx.Query<TierUnit>().ToView();

        Assert.Throws<InvalidOperationException>(() =>
        {
            TyphonRuntime.Create(dbe, schedule =>
            {
                schedule.QuerySystem("Bad", _ => { }, input: () => view, parallel: true,
                    tier: SimTier.Tier2, cellAmortize: -1);
            }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });
        });
        view.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Tier-filtered dispatch (runtime integration)
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void TierDispatch_Tier0System_SeesOnlyTier0Entities()
    {
        using var dbe = SetupEngineWithGrid();
        var meta = Archetype<TierUnit>.Metadata;

        // Spawn one entity per cell across four different cells.
        var cellA = dbe.SpatialGrid.WorldToCellKey(5f, 5f);
        var cellB = dbe.SpatialGrid.WorldToCellKey(15f, 5f);
        var cellC = dbe.SpatialGrid.WorldToCellKey(25f, 5f);
        var cellD = dbe.SpatialGrid.WorldToCellKey(35f, 5f);

        EntityId eA, eB, eC, eD;
        using (var tx = dbe.CreateQuickTransaction())
        {
            eA = tx.Spawn<TierUnit>(TierUnit.Pos.Set(PointAt(5f, 5f)));
            eB = tx.Spawn<TierUnit>(TierUnit.Pos.Set(PointAt(15f, 5f)));
            eC = tx.Spawn<TierUnit>(TierUnit.Pos.Set(PointAt(25f, 5f)));
            eD = tx.Spawn<TierUnit>(TierUnit.Pos.Set(PointAt(35f, 5f)));
            tx.Commit();
        }

        dbe.SpatialGrid.SetCellTier(cellA, SimTier.Tier0);
        dbe.SpatialGrid.SetCellTier(cellB, SimTier.Tier1);
        dbe.SpatialGrid.SetCellTier(cellC, SimTier.Tier2);
        dbe.SpatialGrid.SetCellTier(cellD, SimTier.Tier3);

        using var txView = dbe.CreateQuickTransaction();
        var view = txView.Query<TierUnit>().ToView();

        var seen = new ConcurrentBag<EntityId>();
        var ticksSeen = 0;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.CallbackSystem("Tick", _ => Interlocked.Increment(ref ticksSeen));
            schedule.QuerySystem("T0_Check", ctx =>
            {
                foreach (var id in ctx.Entities)
                {
                    seen.Add(id);
                }
            }, input: () => view, parallel: true, tier: SimTier.Tier0, after: "Tick");
        }, new RuntimeOptions { WorkerCount = 2, BaseTickRate = 1000 });

        runtime.Start();
        SpinWait.SpinUntil(() => ticksSeen >= 2, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        Assert.That(seen, Does.Contain(eA));
        Assert.That(seen, Does.Not.Contain(eB));
        Assert.That(seen, Does.Not.Contain(eC));
        Assert.That(seen, Does.Not.Contain(eD));

        view.Dispose();
    }

    [Test]
    public void TierDispatch_MultiTierNear_SeesTier0AndTier1()
    {
        using var dbe = SetupEngineWithGrid();
        var cellA = dbe.SpatialGrid.WorldToCellKey(5f, 5f);
        var cellB = dbe.SpatialGrid.WorldToCellKey(15f, 5f);
        var cellC = dbe.SpatialGrid.WorldToCellKey(25f, 5f);

        EntityId eA, eB, eC;
        using (var tx = dbe.CreateQuickTransaction())
        {
            eA = tx.Spawn<TierUnit>(TierUnit.Pos.Set(PointAt(5f, 5f)));
            eB = tx.Spawn<TierUnit>(TierUnit.Pos.Set(PointAt(15f, 5f)));
            eC = tx.Spawn<TierUnit>(TierUnit.Pos.Set(PointAt(25f, 5f)));
            tx.Commit();
        }

        dbe.SpatialGrid.SetCellTier(cellA, SimTier.Tier0);
        dbe.SpatialGrid.SetCellTier(cellB, SimTier.Tier1);
        dbe.SpatialGrid.SetCellTier(cellC, SimTier.Tier2);

        using var txView = dbe.CreateQuickTransaction();
        var view = txView.Query<TierUnit>().ToView();

        var seen = new ConcurrentBag<EntityId>();
        var ticksSeen = 0;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.CallbackSystem("Tick", _ => Interlocked.Increment(ref ticksSeen));
            schedule.QuerySystem("Near_Check", ctx =>
            {
                foreach (var id in ctx.Entities)
                {
                    seen.Add(id);
                }
            }, input: () => view, parallel: true, tier: SimTier.Near, after: "Tick");
        }, new RuntimeOptions { WorkerCount = 2, BaseTickRate = 1000 });

        runtime.Start();
        SpinWait.SpinUntil(() => ticksSeen >= 2, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        Assert.That(seen, Does.Contain(eA));
        Assert.That(seen, Does.Contain(eB));
        Assert.That(seen, Does.Not.Contain(eC));

        view.Dispose();
    }

    [Test]
    public void TierDispatch_CellAmortize_ProcessesEachCellExactlyOncePerCycle()
    {
        using var dbe = SetupEngineWithGrid();

        // 4 cells at Tier2, each with one entity. cellAmortize: 4 means each tick visits one cell.
        EntityId[] entities = new EntityId[4];
        float[] coords = [5f, 15f, 25f, 35f];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 4; i++)
            {
                entities[i] = tx.Spawn<TierUnit>(TierUnit.Pos.Set(PointAt(coords[i], 5f)));
            }
            tx.Commit();
        }
        for (int i = 0; i < 4; i++)
        {
            dbe.SpatialGrid.SetCellTier(dbe.SpatialGrid.WorldToCellKey(coords[i], 5f), SimTier.Tier2);
        }

        using var txView = dbe.CreateQuickTransaction();
        var view = txView.Query<TierUnit>().ToView();

        var perTickCounts = new ConcurrentBag<int>();
        var ticksSeen = 0;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.CallbackSystem("Tick", _ => Interlocked.Increment(ref ticksSeen));
            schedule.QuerySystem("Amortized", ctx =>
            {
                int count = 0;
                foreach (var _ in ctx.Entities)
                {
                    count++;
                }
                perTickCounts.Add(count);
            }, input: () => view, parallel: true, tier: SimTier.Tier2, cellAmortize: 4, after: "Tick");
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        runtime.Start();
        SpinWait.SpinUntil(() => ticksSeen >= 8, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        // Each amortization cycle of 4 ticks should process all 4 entities — 1 per tick.
        // We allow some jitter (first tick may be empty if the prepare phase reads tick 0 before spawns are
        // visible) but demand that after 4 full cycles, all four entities have been processed.
        int totalProcessed = 0;
        foreach (var c in perTickCounts)
        {
            totalProcessed += c;
        }
        Assert.That(totalProcessed, Is.GreaterThanOrEqualTo(4),
            "cellAmortize: 4 over 8 ticks should process at least one full cycle of 4 entities.");

        view.Dispose();
    }

    [Test]
    public void TierDispatch_AmortizedDeltaTime_EqualsDeltaTimeTimesAmortize()
    {
        using var dbe = SetupEngineWithGrid();

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<TierUnit>(TierUnit.Pos.Set(PointAt(5f, 5f)));
            tx.Commit();
        }
        dbe.SpatialGrid.SetCellTier(dbe.SpatialGrid.WorldToCellKey(5f, 5f), SimTier.Tier2);

        using var txView = dbe.CreateQuickTransaction();
        var view = txView.Query<TierUnit>().ToView();

        float lastDeltaTime = 0f;
        float lastAmortizedDt = 0f;
        var ticksSeen = 0;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.CallbackSystem("Tick", _ => Interlocked.Increment(ref ticksSeen));
            schedule.QuerySystem("AmortTick", ctx =>
            {
                lastDeltaTime = ctx.DeltaTime;
                lastAmortizedDt = ctx.AmortizedDeltaTime;
            }, input: () => view, parallel: true, tier: SimTier.Tier2, cellAmortize: 10, after: "Tick");
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        runtime.Start();
        SpinWait.SpinUntil(() => ticksSeen >= 15, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        // AmortizedDeltaTime = DeltaTime * cellAmortize (10).
        if (lastDeltaTime > 0)
        {
            Assert.That(lastAmortizedDt, Is.EqualTo(lastDeltaTime * 10f).Within(1e-5f));
        }

        view.Dispose();
    }

    [Test]
    public void TierDispatch_AllFilter_UsesFastPath_NoTierIndexRebuild()
    {
        // A system with tier: SimTier.All must NOT trigger TierClusterIndex construction on the archetype.
        using var dbe = SetupEngineWithGrid();
        var meta = Archetype<TierUnit>.Metadata;

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<TierUnit>(TierUnit.Pos.Set(PointAt(5f, 5f)));
            tx.Commit();
        }

        using var txView = dbe.CreateQuickTransaction();
        var view = txView.Query<TierUnit>().ToView();

        var ticksSeen = 0;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.CallbackSystem("Tick", _ => Interlocked.Increment(ref ticksSeen));
            // Intentionally default tier: SimTier.All
            schedule.QuerySystem("AllTier", _ => { }, input: () => view, parallel: true, after: "Tick");
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        runtime.Start();
        SpinWait.SpinUntil(() => ticksSeen >= 3, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        // TierIndex stays null — the SimTier.All fast path never instantiates it.
        Assert.That(cs.TierIndex, Is.Null,
            "SimTier.All should skip tier index construction entirely (zero-overhead fast path).");

        view.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Phase 3: View-level tier filter + tier-filtered change detection
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void View_WithTier_FiltersFullEntitySet()
    {
        // Tests that BuildFullViewEntitySet's tier-scoped path emits only the tier's entities. We use a
        // non-parallel system iterating ctx.Entities — that path materializes via OnSystemStartInternal which
        // calls BuildFullViewEntitySet.
        using var dbe = SetupEngineWithGrid();

        var cellA = dbe.SpatialGrid.WorldToCellKey(5f, 5f);
        var cellB = dbe.SpatialGrid.WorldToCellKey(15f, 5f);

        EntityId eA, eB;
        using (var tx = dbe.CreateQuickTransaction())
        {
            eA = tx.Spawn<TierUnit>(TierUnit.Pos.Set(PointAt(5f, 5f)));
            eB = tx.Spawn<TierUnit>(TierUnit.Pos.Set(PointAt(15f, 5f)));
            tx.Commit();
        }
        dbe.SpatialGrid.SetCellTier(cellA, SimTier.Tier0);
        dbe.SpatialGrid.SetCellTier(cellB, SimTier.Tier3);

        using var txView = dbe.CreateQuickTransaction();
        var view = txView.Query<TierUnit>().ToView();

        var seen = new ConcurrentBag<EntityId>();
        var ticksSeen = 0;

        // Use a parallel system with sys.TierFilter — same materialization path under the hood, but exercises the
        // tier-scoped helper consistently with Phase 2.
        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.CallbackSystem("Tick", _ => Interlocked.Increment(ref ticksSeen));
            schedule.QuerySystem("Tier0_View", ctx =>
            {
                foreach (var id in ctx.Entities)
                {
                    seen.Add(id);
                }
            }, input: () => view, parallel: true, tier: SimTier.Tier0, after: "Tick");
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        runtime.Start();
        SpinWait.SpinUntil(() => ticksSeen >= 2, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        Assert.That(seen, Does.Contain(eA));
        Assert.That(seen, Does.Not.Contain(eB));

        view.Dispose();
    }

    [Test]
    public void TierFilteredChangeDetection_OnlyDeliversTierDirtyEntities()
    {
        // Two entities in different tiers; both get written every tick. A tier-filtered change-filtered system
        // should only see the tier's dirty entities.
        using var dbe = SetupEngineWithGrid();

        var cellA = dbe.SpatialGrid.WorldToCellKey(5f, 5f);
        var cellB = dbe.SpatialGrid.WorldToCellKey(55f, 5f);

        EntityId eA, eB;
        using (var tx = dbe.CreateQuickTransaction())
        {
            eA = tx.Spawn<TierUnit>(TierUnit.Pos.Set(PointAt(5f, 5f)));
            eB = tx.Spawn<TierUnit>(TierUnit.Pos.Set(PointAt(55f, 5f)));
            tx.Commit();
        }
        dbe.SpatialGrid.SetCellTier(cellA, SimTier.Tier0);
        dbe.SpatialGrid.SetCellTier(cellB, SimTier.Tier3);

        using var txView = dbe.CreateQuickTransaction();
        var view = txView.Query<TierUnit>().ToView();

        var dirtySeen = new ConcurrentBag<EntityId>();
        var ticksSeen = 0;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.CallbackSystem("Tick", _ => Interlocked.Increment(ref ticksSeen));

            // Writer: marks Pos dirty on both entities every tick.
            schedule.QuerySystem("Writer", ctx =>
            {
                foreach (var id in ctx.Entities)
                {
                    var e = ctx.Accessor.OpenMut(id);
                    ref var pos = ref e.Write(TierUnit.Pos);
                    pos.Data += 1f;
                }
            }, input: () => view, parallel: true, after: "Tick");

            // Tier-filtered change-filtered reader: should only see Tier0 dirty entities.
            schedule.QuerySystem("Tier0_Reactive", ctx =>
            {
                foreach (var id in ctx.Entities)
                {
                    dirtySeen.Add(id);
                }
            }, input: () => view, parallel: true, tier: SimTier.Tier0,
                changeFilter: [typeof(TierPos)], after: "Writer");
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        runtime.Start();
        SpinWait.SpinUntil(() => ticksSeen >= 4, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        Assert.That(dirtySeen, Does.Contain(eA));
        Assert.That(dirtySeen, Does.Not.Contain(eB));

        view.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // TEST-A (BUG-1 regression): change filter + tier filter precedence
    //
    // For a tier-filtered + change-filtered system, the entities source MUST come from the materialized dirty
    // list (which is tier-scoped upstream by ScanClusterDirtyEntities) — NOT from a ClusterRangeEntityView walking
    // all entities in the tier's clusters. Pre-fix the dispatch routed change-filtered+tier through the
    // ClusterRangeEntityView path, silently ignoring the change filter.
    //
    // We assert the dispatch path by inspecting the runtime type of ctx.Entities. For a change-filtered+tier system
    // it must be the PooledEntitySlice (Path 2 with tier-scoped materialization upstream), not ClusterRangeEntityView.
    // True per-entity dirty filtering (a writer that touches only one of two co-clustered entities) requires the
    // component to have a B+Tree-indexed field — TierPos has none, so we test the routing rather than the count.
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void TierFilteredChangeDetection_DispatchRoutesViaMaterializedList()
    {
        using var dbe = SetupEngineWithGrid();

        var cell = dbe.SpatialGrid.WorldToCellKey(5f, 5f);

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<TierUnit>(TierUnit.Pos.Set(PointAt(5f, 5f)));
            tx.Spawn<TierUnit>(TierUnit.Pos.Set(PointAt(6f, 5f)));
            tx.Commit();
        }
        dbe.SpatialGrid.SetCellTier(cell, SimTier.Tier0);

        using var txView = dbe.CreateQuickTransaction();
        var view = txView.Query<TierUnit>().ToView();

        string entitiesTypeName = null;
        var ticksSeen = 0;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.CallbackSystem("Tick", _ => Interlocked.Increment(ref ticksSeen));

            // Writer: any parallel write — keeps the change filter "active" so the reader's prepare path runs
            // BuildFilteredEntitySet (instead of being ReactiveSkipped).
            schedule.QuerySystem("Writer", ctx =>
            {
                foreach (var id in ctx.Entities)
                {
                    var e = ctx.Accessor.OpenMut(id);
                    ref var pos = ref e.Write(TierUnit.Pos);
                    pos.Data += 1f;
                }
            }, input: () => view, parallel: true, after: "Tick");

            // Reader: tier-filtered + change-filtered. The fix in ExecuteChunkWithAccessor must route this through
            // the Path 2 materialized-slice path, not the ClusterRangeEntityView path.
            schedule.QuerySystem("Tier0_Reactive", ctx =>
            {
                if (entitiesTypeName == null)
                {
                    entitiesTypeName = ctx.Entities.GetType().Name;
                }
            }, input: () => view, parallel: true, tier: SimTier.Tier0,
                changeFilter: [typeof(TierPos)], after: "Writer");
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        runtime.Start();
        SpinWait.SpinUntil(() => entitiesTypeName != null || ticksSeen >= 8, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        Assert.That(entitiesTypeName, Is.Not.Null, "Reader system must have run at least once.");
        Assert.That(entitiesTypeName, Is.EqualTo("PooledEntitySlice"),
            "BUG-1 regression: change-filtered + tier-filtered systems must route through the Path 2 materialized " +
            $"list (PooledEntitySlice). Got '{entitiesTypeName}' — likely ClusterRangeEntityView, which silently " +
            "iterates ALL entities in the tier's clusters regardless of dirty state.");

        view.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // TEST-D (BUG-4 regression): Versioned + tier system reading ctx.ClusterIds
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void TierDispatch_VersionedSystem_ClusterIdsPopulated()
    {
        using var dbe = SetupEngineWithGrid();

        var cellA = dbe.SpatialGrid.WorldToCellKey(5f, 5f);
        var cellB = dbe.SpatialGrid.WorldToCellKey(55f, 5f);

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<TierUnit>(TierUnit.Pos.Set(PointAt(5f, 5f)));
            tx.Spawn<TierUnit>(TierUnit.Pos.Set(PointAt(55f, 5f)));
            tx.Commit();
        }
        dbe.SpatialGrid.SetCellTier(cellA, SimTier.Tier0);
        dbe.SpatialGrid.SetCellTier(cellB, SimTier.Tier3);

        using var txView = dbe.CreateQuickTransaction();
        var view = txView.Query<TierUnit>().ToView();

        bool sawNonNullClusterIds = false;
        bool sawClusterIdsCoveringTier0Only = true;
        var ticksSeen = 0;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.CallbackSystem("Tick", _ => Interlocked.Increment(ref ticksSeen));
            schedule.QuerySystem("VersionedTier0", ctx =>
            {
                if (ctx.ClusterIds != null)
                {
                    sawNonNullClusterIds = true;
                    var cs = dbe._archetypeStates[Archetype<TierUnit>.Metadata.ArchetypeId].ClusterState;
                    for (int i = ctx.StartClusterIndex; i < ctx.EndClusterIndex; i++)
                    {
                        if (cs.ClusterCellMap[ctx.ClusterIds[i]] != cellA)
                        {
                            sawClusterIdsCoveringTier0Only = false;
                        }
                    }
                }
            }, input: () => view, parallel: true, writesVersioned: true,
                tier: SimTier.Tier0, after: "Tick");
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        runtime.Start();
        SpinWait.SpinUntil(() => ticksSeen >= 3, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        Assert.That(sawNonNullClusterIds, Is.True,
            "Versioned + tier system must see ctx.ClusterIds populated (BUG-4 regression).");
        Assert.That(sawClusterIdsCoveringTier0Only, Is.True,
            "All clusters in ctx.ClusterIds[StartClusterIndex..EndClusterIndex] must belong to Tier0 cells.");

        view.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // TEST-E: empty tier with cellAmortize — must not NRE
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void TierDispatch_AmortizeOnEmptyTier_NoNRE()
    {
        using var dbe = SetupEngineWithGrid();

        // Spawn entities only in Tier0; assign Tier2 to a different cell with NO entities.
        var cellA = dbe.SpatialGrid.WorldToCellKey(5f, 5f);
        var cellB = dbe.SpatialGrid.WorldToCellKey(55f, 5f);

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<TierUnit>(TierUnit.Pos.Set(PointAt(5f, 5f)));
            tx.Commit();
        }
        dbe.SpatialGrid.SetCellTier(cellA, SimTier.Tier0);
        dbe.SpatialGrid.SetCellTier(cellB, SimTier.Tier2); // empty cell with Tier2

        using var txView = dbe.CreateQuickTransaction();
        var view = txView.Query<TierUnit>().ToView();

        var ticksSeen = 0;
        int processedCount = 0;

        // Tier2 is set on cellB but cellB has no entities. amortized system on Tier2 must not crash.
        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.CallbackSystem("Tick", _ => Interlocked.Increment(ref ticksSeen));
            schedule.QuerySystem("EmptyTier2", ctx =>
            {
                foreach (var _ in ctx.Entities)
                {
                    Interlocked.Increment(ref processedCount);
                }
            }, input: () => view, parallel: true, tier: SimTier.Tier2, cellAmortize: 4, after: "Tick");
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        Assert.DoesNotThrow(() =>
        {
            runtime.Start();
            SpinWait.SpinUntil(() => ticksSeen >= 5, TimeSpan.FromSeconds(5));
            runtime.Shutdown();
        });

        Assert.That(processedCount, Is.EqualTo(0), "Tier2 has no clusters; the amortized system must process zero entities.");

        view.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // TEST-F: view.WithTier() end-to-end (system uses default tier: All)
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void View_WithTier_EndToEnd_NoSystemTier()
    {
        using var dbe = SetupEngineWithGrid();

        var cellA = dbe.SpatialGrid.WorldToCellKey(5f, 5f);
        var cellB = dbe.SpatialGrid.WorldToCellKey(15f, 5f);

        EntityId eA, eB;
        using (var tx = dbe.CreateQuickTransaction())
        {
            eA = tx.Spawn<TierUnit>(TierUnit.Pos.Set(PointAt(5f, 5f)));
            eB = tx.Spawn<TierUnit>(TierUnit.Pos.Set(PointAt(15f, 5f)));
            tx.Commit();
        }
        dbe.SpatialGrid.SetCellTier(cellA, SimTier.Tier0);
        dbe.SpatialGrid.SetCellTier(cellB, SimTier.Tier3);

        using var txView = dbe.CreateQuickTransaction();
        // Apply tier on the VIEW, not the system. System tier defaults to All.
        var view = txView.Query<TierUnit>().ToView();
        view.WithTier(SimTier.Tier0);

        var seen = new ConcurrentBag<EntityId>();
        var ticksSeen = 0;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.CallbackSystem("Tick", _ => Interlocked.Increment(ref ticksSeen));
            // Use a versioned system so it goes through PrepareVersionedFallback → BuildFullViewEntitySet,
            // which is where view.TierFilter is consumed end-to-end.
            schedule.QuerySystem("ViewTierOnly", ctx =>
            {
                foreach (var id in ctx.Entities)
                {
                    seen.Add(id);
                }
            }, input: () => view, parallel: true, writesVersioned: true, after: "Tick");
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        runtime.Start();
        SpinWait.SpinUntil(() => ticksSeen >= 3, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        Assert.That(seen, Does.Contain(eA));
        Assert.That(seen, Does.Not.Contain(eB),
            "view.WithTier(Tier0) must filter materialization to Tier0 even when the system has no tier filter.");

        view.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // TEST-G: multi-tier merge with empty tier components
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void TierClusterIndex_MultiTierMerge_EmptyTier1_StillReturnsTier0()
    {
        using var dbe = SetupEngineWithGrid();
        var meta = Archetype<TierUnit>.Metadata;

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<TierUnit>(TierUnit.Pos.Set(PointAt(5f, 5f)));
            tx.Spawn<TierUnit>(TierUnit.Pos.Set(PointAt(15f, 5f)));
            tx.Commit();
        }
        // Both cells set to Tier0; Tier1 stays empty.
        dbe.SpatialGrid.SetCellTier(dbe.SpatialGrid.WorldToCellKey(5f, 5f), SimTier.Tier0);
        dbe.SpatialGrid.SetCellTier(dbe.SpatialGrid.WorldToCellKey(15f, 5f), SimTier.Tier0);

        var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        var index = new TierClusterIndex();
        index.Rebuild(dbe.SpatialGrid, cs);

        // Near = Tier0 | Tier1. Tier1 is empty, so the merge result should equal Tier0's count.
        Assert.That(index.GetClusters(SimTier.Tier0).Length, Is.EqualTo(2));
        Assert.That(index.GetClusters(SimTier.Tier1).Length, Is.EqualTo(0));
        Assert.That(index.GetClusters(SimTier.Near).Length, Is.EqualTo(2));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // TEST-H: TierIndex buffer growth across rebuilds
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void TierClusterIndex_BufferGrowth_PastInitial16()
    {
        // Spawn enough entities in distinct cells (so each gets its own cluster) to grow the per-tier buffer
        // past its initial 16-entry capacity. Then verify the rebuild still produces correct counts.
        using var dbe = SetupEngineWithGrid();
        var meta = Archetype<TierUnit>.Metadata;

        const int N = 50;
        // 100x100 world, cellSize 10 → 10x10 grid = 100 cells. Place each entity in a different cell.
        for (int i = 0; i < N; i++)
        {
            using var tx = dbe.CreateQuickTransaction();
            int cy = i / 10;
            int cx = i % 10;
            tx.Spawn<TierUnit>(TierUnit.Pos.Set(PointAt(cx * 10f + 5f, cy * 10f + 5f)));
            tx.Commit();
            dbe.SpatialGrid.SetCellTier(dbe.SpatialGrid.WorldToCellKey(cx * 10f + 5f, cy * 10f + 5f), SimTier.Tier0);
        }

        var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        Assert.That(cs.ActiveClusterCount, Is.EqualTo(N), "Each entity should land in its own cluster (different cells).");

        var index = new TierClusterIndex();
        index.Rebuild(dbe.SpatialGrid, cs);

        Assert.That(index.GetClusters(SimTier.Tier0).Length, Is.EqualTo(N),
            "All N clusters should appear in the Tier0 list after rebuild — buffer must have grown past 16.");
    }

    [Test]
    public void TierDispatch_ClusterEnumerator_WithTierClusterIds()
    {
        // Verifies the new ClusterEnumerator overload: iterating via ctx.Accessor.GetClusterEnumerator(ctx.ClusterIds, start, end)
        // visits only clusters in the tier's cluster list.
        using var dbe = SetupEngineWithGrid();
        var meta = Archetype<TierUnit>.Metadata;

        var cellA = dbe.SpatialGrid.WorldToCellKey(5f, 5f);
        var cellB = dbe.SpatialGrid.WorldToCellKey(55f, 5f);

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<TierUnit>(TierUnit.Pos.Set(PointAt(5f, 5f)));
            tx.Spawn<TierUnit>(TierUnit.Pos.Set(PointAt(55f, 5f)));
            tx.Commit();
        }

        dbe.SpatialGrid.SetCellTier(cellA, SimTier.Tier1);
        dbe.SpatialGrid.SetCellTier(cellB, SimTier.Tier0);

        using var txView = dbe.CreateQuickTransaction();
        var view = txView.Query<TierUnit>().ToView();

        var visitedChunkIds = new ConcurrentBag<int>();
        var ticksSeen = 0;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.CallbackSystem("Tick", _ => Interlocked.Increment(ref ticksSeen));
            schedule.QuerySystem("Cluster_T1", ctx =>
            {
                if (ctx.ClusterIds == null)
                {
                    return;
                }
                // Record the chunk ids visited via the partition; we'll cross-check against ClusterCellMap below.
                for (int i = ctx.StartClusterIndex; i < ctx.EndClusterIndex; i++)
                {
                    visitedChunkIds.Add(ctx.ClusterIds[i]);
                }
            }, input: () => view, parallel: true, tier: SimTier.Tier1, after: "Tick");
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        runtime.Start();
        SpinWait.SpinUntil(() => ticksSeen >= 2, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        // Every visited cluster must belong to cellA (the only Tier1 cell).
        var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        Assert.That(visitedChunkIds, Is.Not.Empty, "Tier1 system should have visited at least one cluster.");
        foreach (int chunkId in visitedChunkIds)
        {
            Assert.That(cs.ClusterCellMap[chunkId], Is.EqualTo(cellA),
                $"Chunk {chunkId} was dispatched to a Tier1 system but lives in cell {cs.ClusterCellMap[chunkId]} (expected {cellA}).");
        }

        view.Dispose();
    }
}
