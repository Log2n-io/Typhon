using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ═══════════════════════════════════════════════════════════════════════
// Trigger test types — reuse SpatialShip (Dynamic) and SpatialTerrain (Static) from ECS integration tests
// ═══════════════════════════════════════════════════════════════════════

[NonParallelizable]
class SpatialTriggerTests : TestBase<SpatialTriggerTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        Archetype<SpatialShipArchetype>.Touch();
        Archetype<SpatialTerrainArchetype>.Touch();
    }

    /// <summary>
    /// Default setup for Ship-based trigger tests. Registers <c>SpatialShip</c> + <c>SpatialName</c> and calls <c>ConfigureSpatialGrid</c> so the cluster archetype
    /// populates the per-cell spatial index (issue #230 Phase 3 migration target).
    /// </summary>
    /// <remarks>
    /// Does NOT register <c>SpatialTerrain</c>: issue #229 Phase 1+2 restricts the grid to a single spatial archetype per configured grid (stale gate —
    /// the per-cell index is per-archetype via <c>PerCellIndex</c>, but the grid's shared cell cluster list is not yet split). Terrain-based tests use
    /// <see cref="SetupEngineWithTerrain"/> instead.
    /// </remarks>
    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<SpatialShip>();
        dbe.RegisterComponentFromAccessor<SpatialName>();
        dbe.ConfigureSpatialGrid(new SpatialGridConfig(
            worldMin: new System.Numerics.Vector2(-1000f, -1000f),
            worldMax: new System.Numerics.Vector2(1000f, 1000f),
            cellSize: 100f));
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>
    /// Setup for the static-cache tests that use <c>SpatialTerrain</c>. Registers only Terrain (+ grid) because Option B requires a SpatialGrid for cluster
    /// spatial archetypes and #229 Q10 still allows only one spatial archetype per grid. These tests exercise the non-cluster per-table <c>StaticTree</c>
    /// cache invalidation logic — the cluster path isn't under test here.
    /// </summary>
    private DatabaseEngine SetupEngineWithTerrain()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<SpatialTerrain>();
        dbe.ConfigureSpatialGrid(new SpatialGridConfig(
            worldMin: new System.Numerics.Vector2(-1000f, -1000f),
            worldMax: new System.Numerics.Vector2(1000f, 1000f),
            cellSize: 100f));
        dbe.InitializeArchetypes();
        return dbe;
    }

    private SpatialTriggerSystem GetTriggerSystem(DatabaseEngine dbe)
    {
        var table = dbe.GetComponentTable<SpatialShip>();
        return table.SpatialIndex.GetOrCreateTriggerSystem(table);
    }

    private EntityId SpawnShipAt(DatabaseEngine dbe, float x, float y, float z, float size = 2f)
    {
        using var t = dbe.CreateQuickTransaction();
        var ship = new SpatialShip
        {
            Bounds = new AABB3F { MinX = x, MinY = y, MinZ = z, MaxX = x + size, MaxY = y + size, MaxZ = z + size },
            Speed = 1.0f
        };
        var id = t.Spawn<SpatialShipArchetype>(SpatialShipArchetype.Ship.Set(in ship), SpatialShipArchetype.Name.Set(new SpatialName { Id = 0 }));
        t.Commit();
        return id;
    }

    // ── Lifecycle ────────────────────────────────────────────────────────

    [Test]
    [CancelAfter(5000)]
    public void CreateRegion_DestroyRegion_Lifecycle()
    {
        using var dbe = SetupEngine();
        var ts = GetTriggerSystem(dbe);

        double[] bounds = { 0, 0, 0, 100, 100, 100 };
        var handle = ts.CreateRegion(bounds);
        Assert.That(ts.ActiveRegionCount, Is.EqualTo(1));

        ts.DestroyRegion(handle);
        Assert.That(ts.ActiveRegionCount, Is.EqualTo(0));

        // Double-destroy should throw
        Assert.Throws<ArgumentException>(() => ts.DestroyRegion(handle));
    }

    [Test]
    [CancelAfter(5000)]
    public void CreateRegion_HandleReuse_GenerationPreventsStaleAccess()
    {
        using var dbe = SetupEngine();
        var ts = GetTriggerSystem(dbe);

        double[] bounds = { 0, 0, 0, 100, 100, 100 };
        var handle1 = ts.CreateRegion(bounds);
        ts.DestroyRegion(handle1);

        var handle2 = ts.CreateRegion(bounds); // reuses slot
        Assert.That(handle2.Index, Is.EqualTo(handle1.Index)); // same slot
        Assert.That(handle2.Generation, Is.Not.EqualTo(handle1.Generation)); // different generation

        // Old handle should be invalid
        Assert.Throws<ArgumentException>(() => ts.EvaluateRegion(handle1, 1));

        // New handle works
        var result = ts.EvaluateRegion(handle2, 1);
        Assert.That(result.WasEvaluated, Is.True);

        ts.DestroyRegion(handle2);
    }

    // ── Enter / Leave ───────────────────────────────────────────────────

    [Test]
    [CancelAfter(5000)]
    public void Enter_SpawnInsideRegion()
    {
        using var dbe = SetupEngine();
        var ts = GetTriggerSystem(dbe);

        double[] regionBounds = { -10, -10, -10, 50, 50, 50 };
        var handle = ts.CreateRegion(regionBounds);

        // First eval — empty
        var r1 = ts.EvaluateRegion(handle, 1);
        Assert.That(r1.WasEvaluated, Is.True);
        Assert.That(r1.Entered.Length, Is.EqualTo(0));

        // Spawn entity inside region
        var shipId = SpawnShipAt(dbe, 10, 10, 10);

        // Second eval — entity should have entered
        var r2 = ts.EvaluateRegion(handle, 2);
        Assert.That(r2.Entered.Length, Is.EqualTo(1));
        Assert.That(r2.Entered[0], Is.EqualTo((long)shipId.RawValue));
        Assert.That(r2.Left.Length, Is.EqualTo(0));

        ts.DestroyRegion(handle);
    }

    [Test]
    [CancelAfter(5000)]
    public void Leave_DestroyEntity()
    {
        using var dbe = SetupEngine();
        var ts = GetTriggerSystem(dbe);

        double[] regionBounds = { -10, -10, -10, 50, 50, 50 };
        var handle = ts.CreateRegion(regionBounds);

        var shipId = SpawnShipAt(dbe, 10, 10, 10);

        // Eval 1: entity enters
        var r1 = ts.EvaluateRegion(handle, 1);
        Assert.That(r1.Entered.Length, Is.EqualTo(1));

        // Destroy entity
        using (var t = dbe.CreateQuickTransaction())
        {
            t.Destroy(shipId);
            t.Commit();
        }

        // Eval 2: entity should have left
        var r2 = ts.EvaluateRegion(handle, 2);
        Assert.That(r2.Left.Length, Is.EqualTo(1));
        Assert.That(r2.Left[0], Is.EqualTo((long)shipId.RawValue));
        Assert.That(r2.Entered.Length, Is.EqualTo(0));

        ts.DestroyRegion(handle);
    }

    [Test]
    [CancelAfter(5000)]
    public void StayInside_NoEnterLeaveEvents()
    {
        using var dbe = SetupEngine();
        var ts = GetTriggerSystem(dbe);

        double[] regionBounds = { -10, -10, -10, 50, 50, 50 };
        var handle = ts.CreateRegion(regionBounds);

        SpawnShipAt(dbe, 10, 10, 10);

        // Eval 1: entity enters
        ts.EvaluateRegion(handle, 1);

        // Eval 2: entity stays (no movement)
        var r2 = ts.EvaluateRegion(handle, 2);
        Assert.That(r2.Entered.Length, Is.EqualTo(0));
        Assert.That(r2.Left.Length, Is.EqualTo(0));
        Assert.That(r2.StayCount, Is.EqualTo(1));

        ts.DestroyRegion(handle);
    }

    // ── Frequency Gating ────────────────────────────────────────────────

    [Test]
    [CancelAfter(5000)]
    public void EvalFrequency_SkipsTicks()
    {
        using var dbe = SetupEngine();
        var ts = GetTriggerSystem(dbe);

        double[] regionBounds = { -10, -10, -10, 50, 50, 50 };
        var handle = ts.CreateRegion(regionBounds, evaluationFrequency: 5);

        SpawnShipAt(dbe, 10, 10, 10);

        // Tick 1: first eval always runs (lastEvaluatedTick starts at int.MinValue)
        var r1 = ts.EvaluateRegion(handle, 1);
        Assert.That(r1.WasEvaluated, Is.True);

        // Ticks 2-5: should be skipped
        for (int tick = 2; tick <= 5; tick++)
        {
            var r = ts.EvaluateRegion(handle, tick);
            Assert.That(r.WasEvaluated, Is.False, $"Tick {tick} should have been skipped");
        }

        // Tick 6: should evaluate (6 - 1 >= 5)
        var r6 = ts.EvaluateRegion(handle, 6);
        Assert.That(r6.WasEvaluated, Is.True);

        ts.DestroyRegion(handle);
    }

    // ── Category Mask ───────────────────────────────────────────────────

    [Test]
    [CancelAfter(5000)]
    public void CategoryMask_FiltersNonMatchingEntities()
    {
        using var dbe = SetupEngine();
        var ts = GetTriggerSystem(dbe);

        // Region only cares about category bit 0x1
        double[] regionBounds = { -10, -10, -10, 200, 200, 200 };
        var handle = ts.CreateRegion(regionBounds, categoryMask: 1);

        // Spawn entities — default categoryMask is uint.MaxValue (all bits set), so they match bit 0x1
        SpawnShipAt(dbe, 10, 10, 10);
        SpawnShipAt(dbe, 20, 20, 20);

        // First eval
        var r1 = ts.EvaluateRegion(handle, 1);
        Assert.That(r1.Entered.Length, Is.EqualTo(2));

        ts.DestroyRegion(handle);
    }

    // ── Update Bounds ───────────────────────────────────────────────────

    [Test]
    [CancelAfter(5000)]
    public void UpdateBounds_NewEntitiesEnterLeave()
    {
        using var dbe = SetupEngine();
        var ts = GetTriggerSystem(dbe);

        // Region at origin
        double[] bounds1 = { -10, -10, -10, 15, 15, 15 };
        var handle = ts.CreateRegion(bounds1);

        // Ship A at (10,10,10) — inside bounds1
        SpawnShipAt(dbe, 10, 10, 10);
        // Ship B at (100,100,100) — outside bounds1
        SpawnShipAt(dbe, 100, 100, 100);

        // Eval 1: only ship A enters
        var r1 = ts.EvaluateRegion(handle, 1);
        Assert.That(r1.Entered.Length, Is.EqualTo(1));

        // Move region to cover ship B only
        double[] bounds2 = { 90, 90, 90, 110, 110, 110 };
        ts.UpdateRegionBounds(handle, bounds2);

        // Eval 2: ship A leaves, ship B enters
        var r2 = ts.EvaluateRegion(handle, 2);
        Assert.That(r2.Left.Length, Is.EqualTo(1));
        Assert.That(r2.Entered.Length, Is.EqualTo(1));

        ts.DestroyRegion(handle);
    }

    // ── Multiple Regions ────────────────────────────────────────────────

    [Test]
    [CancelAfter(5000)]
    public void MultipleRegions_IndependentTracking()
    {
        using var dbe = SetupEngine();
        var ts = GetTriggerSystem(dbe);

        double[] boundsLeft = { -10, -10, -10, 50, 50, 50 };
        double[] boundsRight = { 80, 80, 80, 150, 150, 150 };
        var handleLeft = ts.CreateRegion(boundsLeft);
        var handleRight = ts.CreateRegion(boundsRight);

        // Ship in left region
        var shipLeft = SpawnShipAt(dbe, 10, 10, 10);
        // Ship in right region
        var shipRight = SpawnShipAt(dbe, 100, 100, 100);

        // Eval left — must read results before next EvaluateRegion (result spans share internal buffer)
        var rL = ts.EvaluateRegion(handleLeft, 1);
        Assert.That(rL.Entered.Length, Is.EqualTo(1));
        Assert.That(rL.Entered[0], Is.EqualTo((long)shipLeft.RawValue));

        // Eval right
        var rR = ts.EvaluateRegion(handleRight, 1);
        Assert.That(rR.Entered.Length, Is.EqualTo(1));
        Assert.That(rR.Entered[0], Is.EqualTo((long)shipRight.RawValue));

        ts.DestroyRegion(handleLeft);
        ts.DestroyRegion(handleRight);
    }

    // ── Empty Region ────────────────────────────────────────────────────

    [Test]
    [CancelAfter(5000)]
    public void EmptyRegion_NoEntities_EmptyResult()
    {
        using var dbe = SetupEngine();
        var ts = GetTriggerSystem(dbe);

        double[] bounds = { 1000, 1000, 1000, 2000, 2000, 2000 };
        var handle = ts.CreateRegion(bounds);

        var r = ts.EvaluateRegion(handle, 1);
        Assert.That(r.WasEvaluated, Is.True);
        Assert.That(r.Entered.Length, Is.EqualTo(0));
        Assert.That(r.Left.Length, Is.EqualTo(0));
        Assert.That(r.StayCount, Is.EqualTo(0));

        ts.DestroyRegion(handle);
    }

    // ── Large Scale ─────────────────────────────────────────────────────

    [Test]
    [CancelAfter(5000)]
    public void LargeScale_MultipleRegions_EventCorrectness()
    {
        using var dbe = SetupEngine();
        var ts = GetTriggerSystem(dbe);

        // Create 10 regions in a grid
        var handles = new SpatialRegionHandle[10];
        for (int i = 0; i < 10; i++)
        {
            double x = i * 50;
            double[] bounds = { x, 0, 0, x + 60, 60, 60 };
            handles[i] = ts.CreateRegion(bounds);
        }

        // Spawn 100 entities spread across the grid
        var entityIds = new List<EntityId>();
        for (int i = 0; i < 100; i++)
        {
            float x = (i % 10) * 50 + 5;
            float y = 5;
            float z = 5;
            entityIds.Add(SpawnShipAt(dbe, x, y, z));
        }

        // First eval: all entities should enter their respective regions
        int totalEntered = 0;
        for (int i = 0; i < 10; i++)
        {
            var r = ts.EvaluateRegion(handles[i], 1);
            totalEntered += r.Entered.Length;
        }
        // Each region covers ~60 units, entities spaced 50 apart, each entity enters 1-2 regions
        Assert.That(totalEntered, Is.GreaterThan(0));

        // Second eval: no changes, so all should be stays
        int totalEntered2 = 0;
        int totalLeft2 = 0;
        int totalStay2 = 0;
        for (int i = 0; i < 10; i++)
        {
            var r = ts.EvaluateRegion(handles[i], 2);
            totalEntered2 += r.Entered.Length;
            totalLeft2 += r.Left.Length;
            totalStay2 += r.StayCount;
        }
        Assert.That(totalEntered2, Is.EqualTo(0));
        Assert.That(totalLeft2, Is.EqualTo(0));
        Assert.That(totalStay2, Is.EqualTo(totalEntered)); // same entities, now staying

        // Cleanup
        for (int i = 0; i < 10; i++)
        {
            ts.DestroyRegion(handles[i]);
        }
    }

    // ── Static Cache ────────────────────────────────────────────────────

    [Test]
    [CancelAfter(5000)]
    public void StaticCache_CachedAfterFirstEval()
    {
        using var dbe = SetupEngineWithTerrain();
        var terrainTable = dbe.GetComponentTable<SpatialTerrain>();
        var ts = terrainTable.SpatialIndex.GetOrCreateTriggerSystem(terrainTable);

        // Spawn static terrain entity
        using (var t = dbe.CreateQuickTransaction())
        {
            var terrain = new SpatialTerrain
            {
                Footprint = new AABB3F { MinX = 10, MinY = 10, MinZ = 0, MaxX = 20, MaxY = 20, MaxZ = 5 }
            };
            t.Spawn<SpatialTerrainArchetype>(SpatialTerrainArchetype.Terrain.Set(in terrain));
            t.Commit();
        }

        double[] regionBounds = { 0, 0, -10, 30, 30, 30 };
        var handle = ts.CreateRegion(regionBounds, targetTree: TargetTreeMode.Both);

        // First eval — should query static tree and cache
        var r1 = ts.EvaluateRegion(handle, 1);
        Assert.That(r1.Entered.Length, Is.EqualTo(1));

        // Second eval — static cache should be used (entity still there)
        var r2 = ts.EvaluateRegion(handle, 2);
        Assert.That(r2.Entered.Length, Is.EqualTo(0));
        Assert.That(r2.StayCount, Is.EqualTo(1));

        ts.DestroyRegion(handle);
    }

    [Test]
    [CancelAfter(5000)]
    public void StaticCache_InvalidatedOnTreeMutation()
    {
        using var dbe = SetupEngineWithTerrain();
        var terrainTable = dbe.GetComponentTable<SpatialTerrain>();
        var ts = terrainTable.SpatialIndex.GetOrCreateTriggerSystem(terrainTable);

        EntityId terrainId;
        using (var t = dbe.CreateQuickTransaction())
        {
            var terrain = new SpatialTerrain
            {
                Footprint = new AABB3F { MinX = 10, MinY = 10, MinZ = 0, MaxX = 20, MaxY = 20, MaxZ = 5 }
            };
            terrainId = t.Spawn<SpatialTerrainArchetype>(SpatialTerrainArchetype.Terrain.Set(in terrain));
            t.Commit();
        }

        double[] regionBounds = { 0, 0, -10, 30, 30, 30 };
        var handle = ts.CreateRegion(regionBounds, targetTree: TargetTreeMode.Both);

        // First eval — entity enters
        var r1 = ts.EvaluateRegion(handle, 1);
        Assert.That(r1.Entered.Length, Is.EqualTo(1));

        // Destroy the static entity
        using (var t = dbe.CreateQuickTransaction())
        {
            t.Destroy(terrainId);
            t.Commit();
        }

        // Second eval — cache should be invalidated, entity should leave
        var r2 = ts.EvaluateRegion(handle, 2);
        Assert.That(r2.Left.Length, Is.EqualTo(1));

        ts.DestroyRegion(handle);
    }
}
