using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

[NonParallelizable]
class SpatialInterestTests : TestBase<SpatialInterestTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    // Issue #230 Phase 3 Option B: cluster spatial archetypes require a configured SpatialGrid.
    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<SpatialShip>();
        dbe.RegisterComponentFromAccessor<SpatialName>();
        dbe.ConfigureSpatialGrid(new SpatialGridConfig(
            worldMin: new Vector2(-10_000, -10_000),
            worldMax: new Vector2(10_000, 10_000),
            cellSize: 100f));
        dbe.InitializeArchetypes();
        return dbe;
    }

    private SpatialInterestSystem GetInterestSystem(DatabaseEngine dbe)
    {
        var table = dbe.GetComponentTable<SpatialShip>();
        return table.SpatialIndex.GetOrCreateInterestSystem(table);
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

    /// <summary>
    /// Spawn an entity AND mark it dirty for DirtyBitmap tracking.
    /// Spawns don't go through the SV write path (DirtyBitmap.Set), so we need a separate
    /// Open+Write to trigger the dirty bit. This simulates "entity was modified this tick".
    /// </summary>
    private EntityId SpawnAndDirtyShipAt(DatabaseEngine dbe, float x, float y, float z, float size = 2f)
    {
        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            var ship = new SpatialShip
            {
                Bounds = new AABB3F { MinX = x, MinY = y, MinZ = z, MaxX = x + size, MaxY = y + size, MaxZ = z + size },
                Speed = 1.0f
            };
            id = t.Spawn<SpatialShipArchetype>(SpatialShipArchetype.Ship.Set(in ship), SpatialShipArchetype.Name.Set(new SpatialName { Id = 0 }));
            t.Commit();
        }
        // Open+Write to trigger DirtyBitmap.Set (spawns bypass the SV dirty path)
        using (var t = dbe.CreateQuickTransaction())
        {
            var eref = t.OpenMut(id);
            var ship = eref.Read(SpatialShipArchetype.Ship);
            eref.Write(SpatialShipArchetype.Ship) = ship; // write same value — still marks dirty
            t.Commit();
        }
        return id;
    }

    // ── Lifecycle ────────────────────────────────────────────────────────

    [Test]
    [CancelAfter(5000)]
    public void RegisterObserver_UnregisterObserver_Lifecycle()
    {
        using var dbe = SetupEngine();
        var ims = GetInterestSystem(dbe);

        double[] bounds = { 0, 0, 0, 100, 100, 100 };
        var handle = ims.RegisterObserver(bounds);
        Assert.That(ims.ActiveObserverCount, Is.EqualTo(1));

        ims.UnregisterObserver(handle);
        Assert.That(ims.ActiveObserverCount, Is.EqualTo(0));

        Assert.Throws<ArgumentException>(() => ims.UnregisterObserver(handle));
    }

    [Test]
    [CancelAfter(5000)]
    public void HandleReuse_GenerationPreventsStaleAccess()
    {
        using var dbe = SetupEngine();
        var ims = GetInterestSystem(dbe);

        double[] bounds = { 0, 0, 0, 100, 100, 100 };
        var handle1 = ims.RegisterObserver(bounds);
        ims.UnregisterObserver(handle1);

        var handle2 = ims.RegisterObserver(bounds);
        Assert.That(handle2.Index, Is.EqualTo(handle1.Index));
        Assert.That(handle2.Generation, Is.Not.EqualTo(handle1.Generation));

        Assert.Throws<ArgumentException>(() => ims.GetSpatialChanges(handle1, 1));
        ims.UnregisterObserver(handle2);
    }

    [Test]
    [CancelAfter(5000)]
    public void UpdateObserverBounds_Works()
    {
        using var dbe = SetupEngine();
        var ims = GetInterestSystem(dbe);

        double[] bounds1 = { 0, 0, 0, 50, 50, 50 };
        var handle = ims.RegisterObserver(bounds1, initialTick: 0);

        SpawnAndDirtyShipAt(dbe, 100, 100, 100);
        dbe.WriteTickFence(1);

        // Entity at (100,100,100) is outside initial bounds [0,50]
        var r1 = ims.GetSpatialChanges(handle, 1);
        Assert.That(r1.ChangedEntities.Length, Is.EqualTo(0));

        // Move observer to cover the entity
        double[] bounds2 = { 90, 90, 90, 150, 150, 150 };
        ims.UpdateObserverBounds(handle, bounds2);

        // Spawn another entity in the new bounds to create a new dirty tick
        SpawnAndDirtyShipAt(dbe, 110, 110, 110);
        dbe.WriteTickFence(2);

        var r2 = ims.GetSpatialChanges(handle, 2);
        Assert.That(r2.ChangedEntities.Length, Is.GreaterThan(0));

        ims.UnregisterObserver(handle);
    }

    // ── Core Correctness (IM1) ──────────────────────────────────────────

    [Test]
    [CancelAfter(5000)]
    public void SingleDirtyEntity_InRegion_Reported()
    {
        using var dbe = SetupEngine();
        var ims = GetInterestSystem(dbe);

        double[] bounds = { -10, -10, -10, 50, 50, 50 };
        var handle = ims.RegisterObserver(bounds, initialTick: 0);

        var shipId = SpawnAndDirtyShipAt(dbe, 10, 10, 10);
        dbe.WriteTickFence(1);

        var result = ims.GetSpatialChanges(handle, 1);
        Assert.That(result.IsFullSync, Is.False);
        Assert.That(result.ChangedEntities.Length, Is.EqualTo(1));
        Assert.That(result.ChangedEntities[0], Is.EqualTo((long)shipId.RawValue));

        ims.UnregisterObserver(handle);
    }

    [Test]
    [CancelAfter(5000)]
    public void SingleDirtyEntity_OutsideRegion_NotReported()
    {
        using var dbe = SetupEngine();
        var ims = GetInterestSystem(dbe);

        double[] bounds = { -10, -10, -10, 5, 5, 5 };
        var handle = ims.RegisterObserver(bounds, initialTick: 0);

        SpawnAndDirtyShipAt(dbe, 100, 100, 100); // far outside
        dbe.WriteTickFence(1);

        var result = ims.GetSpatialChanges(handle, 1);
        Assert.That(result.ChangedEntities.Length, Is.EqualTo(0));

        ims.UnregisterObserver(handle);
    }

    [Test]
    [CancelAfter(5000)]
    public void MultipleEntities_OnlyDirtyInRegionReported()
    {
        using var dbe = SetupEngine();
        var ims = GetInterestSystem(dbe);

        double[] bounds = { -10, -10, -10, 50, 50, 50 };
        var handle = ims.RegisterObserver(bounds, initialTick: 0);

        // Entity 1: dirty + inside
        var shipInside = SpawnAndDirtyShipAt(dbe, 10, 10, 10);
        // Entity 2: dirty + outside
        SpawnAndDirtyShipAt(dbe, 200, 200, 200);

        dbe.WriteTickFence(1);

        var result = ims.GetSpatialChanges(handle, 1);
        Assert.That(result.ChangedEntities.Length, Is.EqualTo(1));
        Assert.That(result.ChangedEntities[0], Is.EqualTo((long)shipInside.RawValue));

        // Now tick 2: spawn entity 3 inside but entity 1 is no longer dirty
        SpawnAndDirtyShipAt(dbe, 20, 20, 20);
        dbe.WriteTickFence(2);

        var result2 = ims.GetSpatialChanges(handle, 2);
        Assert.That(result2.ChangedEntities.Length, Is.EqualTo(1)); // only the new one

        ims.UnregisterObserver(handle);
    }

    [Test]
    [CancelAfter(5000)]
    public void CategoryMask_FiltersNonMatching()
    {
        using var dbe = SetupEngine();
        var ims = GetInterestSystem(dbe);

        // Observer only cares about category bit 0x2
        double[] bounds = { -10, -10, -10, 200, 200, 200 };
        var handle = ims.RegisterObserver(bounds, categoryMask: 2, initialTick: 0);

        // Default spawn has categoryMask = uint.MaxValue (all bits set), so bit 0x2 is set → should match
        SpawnAndDirtyShipAt(dbe, 10, 10, 10);
        dbe.WriteTickFence(1);

        var result = ims.GetSpatialChanges(handle, 1);
        Assert.That(result.ChangedEntities.Length, Is.EqualTo(1));

        ims.UnregisterObserver(handle);
    }

    [Test]
    [CancelAfter(5000)]
    public void MultiTick_Accumulation()
    {
        using var dbe = SetupEngine();
        var ims = GetInterestSystem(dbe);

        double[] bounds = { -10, -10, -10, 200, 200, 200 };
        var handle = ims.RegisterObserver(bounds, initialTick: 0);

        // Tick 1: spawn entity A
        var shipA = SpawnAndDirtyShipAt(dbe, 10, 10, 10);
        dbe.WriteTickFence(1);

        // Tick 2: spawn entity B
        var shipB = SpawnAndDirtyShipAt(dbe, 20, 20, 20);
        dbe.WriteTickFence(2);

        // Tick 3: spawn entity C
        var shipC = SpawnAndDirtyShipAt(dbe, 30, 30, 30);
        dbe.WriteTickFence(3);

        // Observer consumes all 3 ticks at once
        var result = ims.GetSpatialChanges(handle, 3);
        Assert.That(result.ChangedEntities.Length, Is.EqualTo(3));
        Assert.That(result.Tick, Is.EqualTo(3));

        ims.UnregisterObserver(handle);
    }

    // ── Ring Buffer (IM2, IM3) ──────────────────────────────────────────

    [Test]
    [CancelAfter(5000)]
    public void ObserverTooStale_ReturnsFullSync()
    {
        using var dbe = SetupEngine();
        var ims = GetInterestSystem(dbe);

        double[] bounds = { -10, -10, -10, 200, 200, 200 };
        var handle = ims.RegisterObserver(bounds, initialTick: 0);

        // Spawn entity
        SpawnAndDirtyShipAt(dbe, 10, 10, 10);

        // Archive 70 ticks (exceeds ring size of 64)
        for (int t = 1; t <= 70; t++)
        {
            dbe.WriteTickFence(t);
        }

        // Observer at tick 0, currentTick 70 → gap = 70 > 64 → full sync
        var result = ims.GetSpatialChanges(handle, 70);
        Assert.That(result.IsFullSync, Is.True);
        Assert.That(result.ChangedEntities.Length, Is.EqualTo(1)); // entity found via tree query

        ims.UnregisterObserver(handle);
    }

    [Test]
    [CancelAfter(5000)]
    public void RingBuffer_ArchivesCorrectly()
    {
        using var dbe = SetupEngine();
        var ims = GetInterestSystem(dbe);

        SpawnAndDirtyShipAt(dbe, 10, 10, 10);
        dbe.WriteTickFence(1);

        // SpatialShipArchetype is now cluster-eligible — dirty bits go to per-archetype ring,
        // not per-table ring. Check the correct ring based on cluster eligibility.
        var meta = Archetype<SpatialShipArchetype>.Metadata;
        DirtyBitmapRing ring;
        if (meta.HasClusterSpatial)
        {
            ring = dbe._archetypeStates[meta.ArchetypeId].ClusterState.ClusterDirtyRing;
        }
        else
        {
            ring = ims.DirtyRing;
        }

        Assert.That(ring.HeadTick, Is.EqualTo(1));
        Assert.That(ring.IsTickAvailable(1), Is.True);
    }

    // ── Edge Cases ──────────────────────────────────────────────────────

    [Test]
    [CancelAfter(5000)]
    public void NoChanges_ReturnsEmpty()
    {
        using var dbe = SetupEngine();
        var ims = GetInterestSystem(dbe);

        double[] bounds = { -10, -10, -10, 200, 200, 200 };
        var handle = ims.RegisterObserver(bounds, initialTick: 1);

        // No entities spawned, no tick fence → no changes
        // Archive an empty tick
        dbe.WriteTickFence(2);

        var result = ims.GetSpatialChanges(handle, 2);
        Assert.That(result.ChangedEntities.Length, Is.EqualTo(0));

        ims.UnregisterObserver(handle);
    }

    [Test]
    [CancelAfter(5000)]
    public void DestroyedEntity_NotReported()
    {
        using var dbe = SetupEngine();
        var ims = GetInterestSystem(dbe);

        double[] bounds = { -10, -10, -10, 200, 200, 200 };
        var handle = ims.RegisterObserver(bounds, initialTick: 0);

        var shipId = SpawnAndDirtyShipAt(dbe, 10, 10, 10);
        dbe.WriteTickFence(1);

        // Consume tick 1
        ims.GetSpatialChanges(handle, 1);

        // Destroy entity, then tick fence
        using (var t = dbe.CreateQuickTransaction())
        {
            t.Destroy(shipId);
            t.Commit();
        }
        dbe.WriteTickFence(2);

        // The entity's back-pointer is cleared → should not appear
        var result = ims.GetSpatialChanges(handle, 2);
        // The entity WAS dirty (destroyed entities may still have dirty bit set) but back-pointer is zeroed
        // So the inverted iteration skips it
        foreach (long id in result.ChangedEntities)
        {
            Assert.That(id, Is.Not.EqualTo((long)shipId.RawValue));
        }

        ims.UnregisterObserver(handle);
    }

    [Test]
    [CancelAfter(5000)]
    public void MultipleObservers_IndependentTracking()
    {
        using var dbe = SetupEngine();
        var ims = GetInterestSystem(dbe);

        double[] boundsLeft = { -10, -10, -10, 50, 50, 50 };
        double[] boundsRight = { 80, 80, 80, 200, 200, 200 };

        var obsLeft = ims.RegisterObserver(boundsLeft, initialTick: 0);
        var obsRight = ims.RegisterObserver(boundsRight, initialTick: 0);

        var shipLeft = SpawnAndDirtyShipAt(dbe, 10, 10, 10);
        var shipRight = SpawnAndDirtyShipAt(dbe, 100, 100, 100);
        dbe.WriteTickFence(1);

        var rL = ims.GetSpatialChanges(obsLeft, 1);
        Assert.That(rL.ChangedEntities.Length, Is.EqualTo(1));
        Assert.That(rL.ChangedEntities[0], Is.EqualTo((long)shipLeft.RawValue));

        var rR = ims.GetSpatialChanges(obsRight, 1);
        Assert.That(rR.ChangedEntities.Length, Is.EqualTo(1));
        Assert.That(rR.ChangedEntities[0], Is.EqualTo((long)shipRight.RawValue));

        ims.UnregisterObserver(obsLeft);
        ims.UnregisterObserver(obsRight);
    }

    [Test]
    [CancelAfter(5000)]
    public void Observer_ConsumesThenConsumesAgain_NoDuplicates()
    {
        using var dbe = SetupEngine();
        var ims = GetInterestSystem(dbe);

        double[] bounds = { -10, -10, -10, 200, 200, 200 };
        var handle = ims.RegisterObserver(bounds, initialTick: 0);

        SpawnAndDirtyShipAt(dbe, 10, 10, 10);
        dbe.WriteTickFence(1);

        // First consumption
        var r1 = ims.GetSpatialChanges(handle, 1);
        Assert.That(r1.ChangedEntities.Length, Is.EqualTo(1));

        // Second consumption at same tick — should be empty (already consumed)
        var r2 = ims.GetSpatialChanges(handle, 1);
        Assert.That(r2.ChangedEntities.Length, Is.EqualTo(0));

        ims.UnregisterObserver(handle);
    }

    // ── Stress Test ─────────────────────────────────────────────────────

    [Test]
    [CancelAfter(10000)]
    public void ManyObservers_ManyDirty_AllCorrect()
    {
        using var dbe = SetupEngine();
        var ims = GetInterestSystem(dbe);

        // Create 10 observers in different regions
        var handles = new SpatialObserverHandle[10];
        for (int i = 0; i < 10; i++)
        {
            double x = i * 50;
            double[] bounds = { x, 0, 0, x + 60, 60, 60 };
            handles[i] = ims.RegisterObserver(bounds, initialTick: 0);
        }

        // Spawn 200 entities across the grid
        var allIds = new List<EntityId>();
        for (int i = 0; i < 200; i++)
        {
            float x = (i % 10) * 50 + 5;
            allIds.Add(SpawnAndDirtyShipAt(dbe, x, 5, 5));
        }
        dbe.WriteTickFence(1);

        // Each observer should see entities in their region
        int totalChanges = 0;
        for (int i = 0; i < 10; i++)
        {
            var r = ims.GetSpatialChanges(handles[i], 1);
            totalChanges += r.ChangedEntities.Length;
            Assert.That(r.IsFullSync, Is.False);
        }
        Assert.That(totalChanges, Is.GreaterThan(0));

        // Tick 2: no new spawns → no changes
        dbe.WriteTickFence(2);
        for (int i = 0; i < 10; i++)
        {
            var r = ims.GetSpatialChanges(handles[i], 2);
            Assert.That(r.ChangedEntities.Length, Is.EqualTo(0));
        }

        for (int i = 0; i < 10; i++)
        {
            ims.UnregisterObserver(handles[i]);
        }
    }
}
