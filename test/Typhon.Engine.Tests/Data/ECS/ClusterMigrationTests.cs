using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ═══════════════════════════════════════════════════════════════════════════════
// Test-only archetype with a Transient component alongside the spatial field,
// for verifying that transient data is preserved across migration (Q8).
// ═══════════════════════════════════════════════════════════════════════════════

[Component("Typhon.Test.ClMig.Pos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct ClMigPos
{
    [Field]
    [SpatialIndex]
    public AABB2F Bounds;

    // Non-unique cluster B+Tree index — used by the Phase 3 non-unique index tests to verify that
    // destroy/migration removes only the specific (key, clusterLocation) entry via RemoveValue(elementId)
    // rather than wiping every sibling at the key. Existing Phase 3 tests use unique per-entity Tag
    // values, so the non-unique index degenerates to unique for them.
    [Field]
    [Index(AllowMultiple = true)]
    public int Tag;
}

[Component("Typhon.Test.ClMig.Scratch", 1, StorageMode = StorageMode.Transient)]
[StructLayout(LayoutKind.Sequential)]
struct ClMigScratch
{
    [Field]
    public int Counter;

    [Field]
    public float Energy;
}

[Archetype]
partial class ClMigUnit : Archetype<ClMigUnit>
{
    public static readonly Comp<ClMigPos> Pos = Register<ClMigPos>();
    public static readonly Comp<ClMigScratch> Scratch = Register<ClMigScratch>();
}

[TestFixture]
[NonParallelizable]
class ClusterMigrationTests : TestBase<ClusterMigrationTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    // 100×100 cells over a 1000×1000 world → 10×10 grid. Hysteresis margin = 5 world units.
    private const float CellSize = 100f;
    private const float WorldMax = 1000f;
    private const float HysteresisMarginUnits = CellSize * 0.05f; // 5 units

    private static ClMigPos PointAt(float x, float y, int tag = 0) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Tag = tag };

    private static ClMigScratch ScratchOf(int counter, float energy) =>
        new() { Counter = counter, Energy = energy };

    private DatabaseEngine SetupEngineWithGrid()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClMigPos>();
        dbe.RegisterComponentFromAccessor<ClMigScratch>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(
            worldMin: new Vector2(0, 0),
            worldMax: new Vector2(WorldMax, WorldMax),
            cellSize: CellSize));
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static (int chunkId, byte slotIndex) ReadLocation(DatabaseEngine dbe, EntityId id)
    {
        using var tx = dbe.CreateQuickTransaction();
        var eref = tx.OpenMut(id);
        // ClusterEntityRecord reads are implicit via the cluster accessor in EntityRef; we read location
        // through the cluster state instead for test verification.
        var meta = Archetype<ClMigUnit>.Metadata;
        var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        // Scan occupied slots for the entity id — one hit expected (single-entity test case).
        for (int i = 0; i < cs.ActiveClusterCount; i++)
        {
            int cid = cs.ActiveClusterIds[i];
            unsafe
            {
                using var epoch = EpochGuard.Enter(dbe.EpochManager);
                var accessor = cs.ClusterSegment.CreateChunkAccessor();
                try
                {
                    byte* clusterBase = accessor.GetChunkAddress(cid);
                    ulong occupancy = *(ulong*)clusterBase;
                    while (occupancy != 0)
                    {
                        int slot = BitOperations.TrailingZeroCount(occupancy);
                        occupancy &= occupancy - 1;
                        long entityAtSlot = *(long*)(clusterBase + cs.Layout.EntityIdsOffset + slot * 8);
                        if (entityAtSlot == (long)id.RawValue)
                        {
                            return (cid, (byte)slot);
                        }
                    }
                }
                finally
                {
                    accessor.Dispose();
                }
            }
        }
        return (-1, 0);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Basic migration — single entity crosses one cell boundary
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void SingleMigration_ToAdjacentCell_ExecutesAtTickFence()
    {
        using var dbe = SetupEngineWithGrid();

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<ClMigUnit>(
                ClMigUnit.Pos.Set(PointAt(50f, 50f)),
                ClMigUnit.Scratch.Set(ScratchOf(0, 0f)));
            tx.Commit();
        }

        int srcCell = dbe.SpatialGrid.WorldToCellKey(50f, 50f, 0f);
        int dstCell = dbe.SpatialGrid.WorldToCellKey(150f, 250f, 0f);
        Assert.That(srcCell, Is.Not.EqualTo(dstCell));

        var (preChunk, preSlot) = ReadLocation(dbe, id);
        Assert.That(preChunk, Is.GreaterThanOrEqualTo(0));

        // Move entity well past the hysteresis margin into a new cell
        using (var tx = dbe.CreateQuickTransaction())
        {
            var eref = tx.OpenMut(id);
            ref var pos = ref eref.Write(ClMigUnit.Pos);
            pos.Bounds = new AABB2F { MinX = 150f, MinY = 250f, MaxX = 150f, MaxY = 250f };
            tx.Commit();
        }

        dbe.WriteTickFence(1);

        // Post-migration assertions: source cell empty, dest cell holds the entity.
        ref var srcCellRef = ref dbe.SpatialGrid.GetCell(srcCell);
        ref var dstCellRef = ref dbe.SpatialGrid.GetCell(dstCell);
        Assert.That(srcCellRef.EntityCount, Is.EqualTo(0), "source cell entity count must drop to zero");
        Assert.That(dstCellRef.EntityCount, Is.EqualTo(1), "destination cell entity count must become 1");
        Assert.That(dstCellRef.ClusterCount, Is.EqualTo(1), "destination cell must own one cluster");

        var meta = Archetype<ClMigUnit>.Metadata;
        var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        Assert.That(cs.LastTickMigrationCount, Is.EqualTo(1), "telemetry counter reflects executed batch");

        // The entity's new cluster chunk id must be mapped to the destination cell
        var (postChunk, _) = ReadLocation(dbe, id);
        Assert.That(postChunk, Is.GreaterThanOrEqualTo(0));
        Assert.That(cs.ClusterCellMap[postChunk], Is.EqualTo(dstCell));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Scan-cursor behaviour under migration (issue #364 — cursor-thrash fix)
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Migration_OutOfCell_DoesNotResetSourceCursor()
    {
        // A parallel-fence migration release (deferFinalize:true) must NOT reset the source cell's scan cursor. That reset — firing on every release across
        // non-worker-exclusive source cells — is what thrashed the cursor and reintroduced the O(M²) re-scan during ExecuteMigrations.
        using var dbe = SetupEngineWithGrid();
        var meta = Archetype<ClMigUnit>.Metadata;
        int clusterSize = meta.ClusterLayout.ClusterSize;

        EntityId firstInCell = default;
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < clusterSize * 3; i++)
            {
                var id = tx.Spawn<ClMigUnit>(
                    ClMigUnit.Pos.Set(PointAt(50f, 50f)),
                    ClMigUnit.Scratch.Set(ScratchOf(0, 0f)));
                if (i == 0) { firstInCell = id; }
            }
            tx.Commit();
        }

        var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        int srcCell = dbe.SpatialGrid.WorldToCellKey(50f, 50f, 0f);
        int cursorBefore = cs.CellClusterPool.GetScanCursor(srcCell);
        Assert.That(cursorBefore, Is.GreaterThan(0), "cursor advanced during the spawn");

        // Migrate one entity out of srcCell — the release runs on the deferFinalize migration path.
        using (var tx = dbe.CreateQuickTransaction())
        {
            var eref = tx.OpenMut(firstInCell);
            ref var pos = ref eref.Write(ClMigUnit.Pos);
            pos.Bounds = new AABB2F { MinX = 850f, MinY = 150f, MaxX = 850f, MaxY = 150f };
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        Assert.That(cs.CellClusterPool.GetScanCursor(srcCell), Is.EqualTo(cursorBefore),
            "a migration release must leave the source cell's cursor untouched (no thrash)");
    }

    [Test]
    public void Migration_IntoCellWithFreedSlot_ReusesViaPhase2()
    {
        // After a migration frees a slot behind the destination cell's (un-reset) cursor, a later migration into that cell must reclaim the slot via
        // ClaimSlotInCell's phase-2 scan rather than allocate a new cluster.
        using var dbe = SetupEngineWithGrid();
        var meta = Archetype<ClMigUnit>.Metadata;
        int clusterSize = meta.ClusterLayout.ClusterSize;

        EntityId firstInCell = default;
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < clusterSize * 3; i++)
            {
                var id = tx.Spawn<ClMigUnit>(
                    ClMigUnit.Pos.Set(PointAt(50f, 50f)),
                    ClMigUnit.Scratch.Set(ScratchOf(0, 0f)));
                if (i == 0) { firstInCell = id; }
            }
            tx.Commit();
        }

        // The future migrant — spawned in a different cell, moved into the dst cell only on tick 2.
        EntityId migrant;
        using (var tx = dbe.CreateQuickTransaction())
        {
            migrant = tx.Spawn<ClMigUnit>(
                ClMigUnit.Pos.Set(PointAt(550f, 550f)),
                ClMigUnit.Scratch.Set(ScratchOf(0, 0f)));
            tx.Commit();
        }

        int dstCell = dbe.SpatialGrid.WorldToCellKey(50f, 50f, 0f);

        // Tick 1: migrate one entity OUT of the dst cell — frees a slot in its cluster 0; the deferFinalize release leaves the cursor advanced (stale-high).
        using (var tx = dbe.CreateQuickTransaction())
        {
            var eref = tx.OpenMut(firstInCell);
            ref var pos = ref eref.Write(ClMigUnit.Pos);
            pos.Bounds = new AABB2F { MinX = 850f, MinY = 150f, MaxX = 850f, MaxY = 150f };
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        Assert.That(dbe.SpatialGrid.GetCell(dstCell).ClusterCount, Is.EqualTo(3),
            "dst cell still owns its 3 clusters (one now has a freed slot)");

        // Tick 2: migrate the migrant INTO the dst cell — must reuse the freed slot via phase 2, not allocate a 4th cluster.
        using (var tx = dbe.CreateQuickTransaction())
        {
            var eref = tx.OpenMut(migrant);
            ref var pos = ref eref.Write(ClMigUnit.Pos);
            pos.Bounds = new AABB2F { MinX = 50f, MinY = 50f, MaxX = 50f, MaxY = 50f };
            tx.Commit();
        }
        dbe.WriteTickFence(2);

        ref var dstAfter = ref dbe.SpatialGrid.GetCell(dstCell);
        Assert.That(dstAfter.ClusterCount, Is.EqualTo(3),
            "phase-2 must reclaim the freed slot — no new cluster allocated despite the stale-high cursor");
        Assert.That(dstAfter.EntityCount, Is.EqualTo(clusterSize * 3),
            "dst cell holds the original entities minus the migrated-out one plus the migrant");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Hysteresis dead-zone — small crossings are absorbed
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void PositionChangeWithinHysteresis_NoMigration()
    {
        using var dbe = SetupEngineWithGrid();

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            // Spawn near the right edge of cell (0, 0). Cell bounds are [0, 100) × [0, 100).
            id = tx.Spawn<ClMigUnit>(
                ClMigUnit.Pos.Set(PointAt(95f, 50f)),
                ClMigUnit.Scratch.Set(ScratchOf(0, 0f)));
            tx.Commit();
        }

        int srcCell = dbe.SpatialGrid.WorldToCellKey(95f, 50f, 0f);

        // Move just 7 units across the boundary (to x=102). Raw cell is (1, 0) — a boundary crossing — but
        // the position is only 2 world units into the new cell, far less than the 5-unit hysteresis margin.
        // Migration must be absorbed.
        using (var tx = dbe.CreateQuickTransaction())
        {
            var eref = tx.OpenMut(id);
            ref var pos = ref eref.Write(ClMigUnit.Pos);
            pos.Bounds = new AABB2F { MinX = 102f, MinY = 50f, MaxX = 102f, MaxY = 50f };
            tx.Commit();
        }

        dbe.WriteTickFence(1);

        var meta = Archetype<ClMigUnit>.Metadata;
        var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        Assert.That(cs.LastTickMigrationCount, Is.EqualTo(0), "position within hysteresis margin must not migrate");
        Assert.That(cs.LastTickHysteresisAbsorbedCount, Is.EqualTo(1), "crossing should be counted as absorbed");

        // The entity is still in the source cell
        Assert.That(dbe.SpatialGrid.GetCell(srcCell).EntityCount, Is.EqualTo(1));
    }

    [Test]
    public void PositionChangeBeyondHysteresis_Migrates()
    {
        using var dbe = SetupEngineWithGrid();

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<ClMigUnit>(
                ClMigUnit.Pos.Set(PointAt(50f, 50f)),
                ClMigUnit.Scratch.Set(ScratchOf(0, 0f)));
            tx.Commit();
        }

        // Move 10 units past the cell boundary — well past the 5-unit margin.
        using (var tx = dbe.CreateQuickTransaction())
        {
            var eref = tx.OpenMut(id);
            ref var pos = ref eref.Write(ClMigUnit.Pos);
            pos.Bounds = new AABB2F { MinX = 110f, MinY = 50f, MaxX = 110f, MaxY = 50f };
            tx.Commit();
        }

        dbe.WriteTickFence(1);

        var meta = Archetype<ClMigUnit>.Metadata;
        var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        Assert.That(cs.LastTickMigrationCount, Is.EqualTo(1), "crossing beyond margin must migrate");
        Assert.That(cs.LastTickHysteresisAbsorbedCount, Is.EqualTo(0));

        int dstCell = dbe.SpatialGrid.WorldToCellKey(110f, 50f, 0f);
        Assert.That(dbe.SpatialGrid.GetCell(dstCell).EntityCount, Is.EqualTo(1));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Multi-entity migration in one batch
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void MultipleMigrations_SameTick_AllExecuted()
    {
        using var dbe = SetupEngineWithGrid();

        var ids = new EntityId[3];
        using (var tx = dbe.CreateQuickTransaction())
        {
            // All three spawn in cell (0, 0) - clusters share this cell
            for (int i = 0; i < 3; i++)
            {
                ids[i] = tx.Spawn<ClMigUnit>(
                    ClMigUnit.Pos.Set(PointAt(50f + i, 50f, tag: i)),
                    ClMigUnit.Scratch.Set(ScratchOf(i * 10, i * 0.5f)));
            }
            tx.Commit();
        }

        int srcCell = dbe.SpatialGrid.WorldToCellKey(50f, 50f, 0f);
        Assert.That(dbe.SpatialGrid.GetCell(srcCell).EntityCount, Is.EqualTo(3));

        // Move all three entities to three different cells
        (float x, float y)[] destPositions =
        {
            (150f, 50f),  // cell (1, 0)
            (50f, 250f),  // cell (0, 2)
            (350f, 450f), // cell (3, 4)
        };

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 3; i++)
            {
                var eref = tx.OpenMut(ids[i]);
                ref var pos = ref eref.Write(ClMigUnit.Pos);
                pos.Bounds = new AABB2F
                {
                    MinX = destPositions[i].x, MinY = destPositions[i].y,
                    MaxX = destPositions[i].x, MaxY = destPositions[i].y
                };
            }
            tx.Commit();
        }

        dbe.WriteTickFence(1);

        var meta = Archetype<ClMigUnit>.Metadata;
        var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        Assert.That(cs.LastTickMigrationCount, Is.EqualTo(3));

        // Source cell is empty; each destination cell has 1 entity
        Assert.That(dbe.SpatialGrid.GetCell(srcCell).EntityCount, Is.EqualTo(0));
        foreach (var (x, y) in destPositions)
        {
            int dst = dbe.SpatialGrid.WorldToCellKey(x, y, 0f);
            Assert.That(dbe.SpatialGrid.GetCell(dst).EntityCount, Is.EqualTo(1), $"cell at ({x}, {y}) should have 1 entity");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Source cluster cleanup when migration empties it
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Migration_LeavingClusterEmpty_DeallocatesCluster()
    {
        using var dbe = SetupEngineWithGrid();

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<ClMigUnit>(
                ClMigUnit.Pos.Set(PointAt(50f, 50f)),
                ClMigUnit.Scratch.Set(ScratchOf(0, 0f)));
            tx.Commit();
        }

        int srcCell = dbe.SpatialGrid.WorldToCellKey(50f, 50f, 0f);
        Assert.That(dbe.SpatialGrid.GetCell(srcCell).ClusterCount, Is.EqualTo(1));

        using (var tx = dbe.CreateQuickTransaction())
        {
            var eref = tx.OpenMut(id);
            ref var pos = ref eref.Write(ClMigUnit.Pos);
            pos.Bounds = new AABB2F { MinX = 450f, MinY = 550f, MaxX = 450f, MaxY = 550f };
            tx.Commit();
        }

        dbe.WriteTickFence(1);

        Assert.That(dbe.SpatialGrid.GetCell(srcCell).ClusterCount, Is.EqualTo(0),
            "empty source cluster must detach from cell");
        Assert.That(dbe.SpatialGrid.GetCell(srcCell).EntityCount, Is.EqualTo(0));
    }

    [Test]
    public void Migration_WithOtherEntitiesInSource_KeepsSourceCluster()
    {
        using var dbe = SetupEngineWithGrid();

        EntityId migrant;
        using (var tx = dbe.CreateQuickTransaction())
        {
            migrant = tx.Spawn<ClMigUnit>(
                ClMigUnit.Pos.Set(PointAt(50f, 50f, tag: 0)),
                ClMigUnit.Scratch.Set(ScratchOf(0, 0f)));
            // stayers — still in source cell after the tick
            for (int i = 1; i < 4; i++)
            {
                tx.Spawn<ClMigUnit>(
                    ClMigUnit.Pos.Set(PointAt(50f + i, 50f, tag: i)),
                    ClMigUnit.Scratch.Set(ScratchOf(i, i * 0.1f)));
            }
            tx.Commit();
        }

        int srcCell = dbe.SpatialGrid.WorldToCellKey(50f, 50f, 0f);
        Assert.That(dbe.SpatialGrid.GetCell(srcCell).EntityCount, Is.EqualTo(4));

        using (var tx = dbe.CreateQuickTransaction())
        {
            var eref = tx.OpenMut(migrant);
            ref var pos = ref eref.Write(ClMigUnit.Pos);
            pos.Bounds = new AABB2F { MinX = 450f, MinY = 50f, MaxX = 450f, MaxY = 50f };
            tx.Commit();
        }

        dbe.WriteTickFence(1);

        Assert.That(dbe.SpatialGrid.GetCell(srcCell).EntityCount, Is.EqualTo(3),
            "source cell still has the three stayers");
        Assert.That(dbe.SpatialGrid.GetCell(srcCell).ClusterCount, Is.EqualTo(1),
            "source cluster must remain because it still has occupied slots");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Destroy race — moved entity destroyed in the same tick
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void MoveThenDestroy_SameTick_NoMigration()
    {
        using var dbe = SetupEngineWithGrid();

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<ClMigUnit>(
                ClMigUnit.Pos.Set(PointAt(50f, 50f)),
                ClMigUnit.Scratch.Set(ScratchOf(0, 0f)));
            tx.Commit();
        }

        int srcCell = dbe.SpatialGrid.WorldToCellKey(50f, 50f, 0f);

        // Update position (dirty bit set) AND destroy in the same transaction.
        using (var tx = dbe.CreateQuickTransaction())
        {
            var eref = tx.OpenMut(id);
            ref var pos = ref eref.Write(ClMigUnit.Pos);
            pos.Bounds = new AABB2F { MinX = 450f, MinY = 450f, MaxX = 450f, MaxY = 450f };
            tx.Destroy(id);
            tx.Commit();
        }

        dbe.WriteTickFence(1);

        var meta = Archetype<ClMigUnit>.Metadata;
        var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        Assert.That(cs.LastTickMigrationCount, Is.EqualTo(0),
            "destroyed entity must not be migrated — occupancy mask filters it before detection");
        Assert.That(dbe.SpatialGrid.GetCell(srcCell).EntityCount, Is.EqualTo(0));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Non-finite position fails loudly
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void NonFinitePosition_ThrowsDescriptive()
    {
        using var dbe = SetupEngineWithGrid();

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<ClMigUnit>(
                ClMigUnit.Pos.Set(PointAt(50f, 50f)),
                ClMigUnit.Scratch.Set(ScratchOf(0, 0f)));
            tx.Commit();
        }

        // Write a NaN position
        using (var tx = dbe.CreateQuickTransaction())
        {
            var eref = tx.OpenMut(id);
            ref var pos = ref eref.Write(ClMigUnit.Pos);
            pos.Bounds = new AABB2F { MinX = float.NaN, MinY = 50f, MaxX = float.NaN, MaxY = 50f };
            tx.Commit();
        }

        var ex = Assert.Throws<InvalidOperationException>(() => dbe.WriteTickFence(1));
        Assert.That(ex.Message, Does.Contain("Non-finite"));
        Assert.That(ex.Message, Does.Contain("entityId"));
        Assert.That(ex.Message, Does.Contain("clusterChunkId"));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Transient component data preserved across migration (Q8)
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void TransientData_PreservedAcrossMigration()
    {
        using var dbe = SetupEngineWithGrid();

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<ClMigUnit>(
                ClMigUnit.Pos.Set(PointAt(50f, 50f, tag: 42)),
                ClMigUnit.Scratch.Set(ScratchOf(counter: 12345, energy: 67.89f)));
            tx.Commit();
        }

        // Move across a cell boundary well past the margin
        using (var tx = dbe.CreateQuickTransaction())
        {
            var eref = tx.OpenMut(id);
            ref var pos = ref eref.Write(ClMigUnit.Pos);
            pos.Bounds = new AABB2F { MinX = 350f, MinY = 450f, MaxX = 350f, MaxY = 450f };
            tx.Commit();
        }

        dbe.WriteTickFence(1);

        // Re-open and verify both persistent Tag and transient Counter/Energy survived
        using (var tx = dbe.CreateQuickTransaction())
        {
            var eref = tx.OpenMut(id);
            ref readonly var pos = ref eref.Read(ClMigUnit.Pos);
            ref readonly var scratch = ref eref.Read(ClMigUnit.Scratch);
            Assert.That(pos.Tag, Is.EqualTo(42), "persistent tag preserved");
            Assert.That(scratch.Counter, Is.EqualTo(12345), "transient counter preserved across migration (Q8)");
            Assert.That(scratch.Energy, Is.EqualTo(67.89f).Within(1e-4f), "transient energy preserved");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Reopen after migration — RebuildCellState reflects post-migration position
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void ReopenAfterMigration_RebuildsMappingFromNewPosition()
    {
        var dbName = NewUniqueDatabaseName("T_ClMigReopen");

        int dstCellKey;
        int srcCellKey;

        // Session 1: spawn + migrate + dispose
        {
            using var dbe = CreateNamedEngineWithGrid(dbName);

            EntityId id;
            using (var tx = dbe.CreateQuickTransaction())
            {
                id = tx.Spawn<ClMigUnit>(
                    ClMigUnit.Pos.Set(PointAt(50f, 50f)),
                    ClMigUnit.Scratch.Set(ScratchOf(0, 0f)));
                tx.Commit();
            }
            srcCellKey = dbe.SpatialGrid.WorldToCellKey(50f, 50f, 0f);

            using (var tx = dbe.CreateQuickTransaction())
            {
                var eref = tx.OpenMut(id);
                ref var pos = ref eref.Write(ClMigUnit.Pos);
                pos.Bounds = new AABB2F { MinX = 550f, MinY = 750f, MaxX = 550f, MaxY = 750f };
                tx.Commit();
            }
            dstCellKey = dbe.SpatialGrid.WorldToCellKey(550f, 750f, 0f);

            dbe.WriteTickFence(1);

            // Sanity: post-migration cell state in session 1
            Assert.That(dbe.SpatialGrid.GetCell(srcCellKey).EntityCount, Is.EqualTo(0));
            Assert.That(dbe.SpatialGrid.GetCell(dstCellKey).EntityCount, Is.EqualTo(1));
        }

        // Session 2: reopen — RebuildCellState reads the cluster's first-occupied entity position,
        // which is now the migrated destination. The reconstructed mapping must reflect that.
        {
            using var dbe = CreateNamedEngineWithGrid(dbName);
            var meta = Archetype<ClMigUnit>.Metadata;
            var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;

            Assert.That(cs.ClusterCellMap, Is.Not.Null);

            // Session 1's keys are NOT reusable here. A cell key is a pool slot handed out when a cell is first occupied (#872 step 8), so a rebuild
            // renumbers them from zero — session 1's srcCellKey happens to be slot 0, and after this reopen slot 0 is the DESTINATION. Reading it would
            // have asserted the destination's count against the source's expectation and passed or failed for reasons unrelated to migration.
            var grid = dbe.SpatialGrid;
            Assert.That(grid.TryGetCellKeyAt(50f, 50f, 0f, out _), Is.False,
                "the source cell must not even exist after the rebuild — nothing occupies it, and a sparse grid does not create empty cells");

            Assert.That(grid.TryGetCellKeyAt(550f, 750f, 0f, out int rebuiltDst), Is.True, "the destination cell must have been reconstructed");
            Assert.That(grid.GetCell(rebuiltDst).EntityCount, Is.EqualTo(1), "destination cell must be reconstructed with the migrated entity");
            Assert.That(grid.GetCell(rebuiltDst).ClusterCount, Is.EqualTo(1));
            Assert.That(grid.CellCount, Is.EqualTo(1), "one occupied cell, so exactly one cell exists");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Regression — closes the Phase 3 "new-cluster WAL edge case" loose end
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Migration_IntoBrandNewCluster_GrowsDirtyBitsSnapshot()
    {
        // Pre-fix behavior: when a migration allocated a brand-new destination cluster whose chunk id
        // exceeded the pre-migration dirtyBits snapshot length, the guard `if (dstChunkId < dirtyBits.Length)`
        // silently dropped the destination's dirty bit. The destination cluster was still persisted via
        // checkpoint, but a crash before the next checkpoint would lose the destination content because
        // its slot was never serialized into the tick's WAL record.
        //
        // Post-fix behavior: the snapshot is grown in place via Array.Resize, propagated back via `ref`,
        // and the destination bit is always set. Observed via LastMigrationDirtyBitsWordCount AND the
        // dst bit being present in PreviousTickDirtySnapshot after the tick fence.
        //
        // Test setup: spawn 1 entity in cell A (creates one cluster). The ClusterDirtyBitmap is initially
        // sized for the segment's ChunkCapacity (typically many words), so in the "natural" case the
        // snapshot is already large enough. To exercise the edge case deterministically, we artificially
        // shrink the bitmap to 1 word via DirtyBitmap.ShrinkForTesting, then trigger a migration whose
        // destination chunk id (1 or more) exceeds the truncated snapshot length.
        using var dbe = SetupEngineWithGrid();

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<ClMigUnit>(
                ClMigUnit.Pos.Set(PointAt(50f, 50f)),
                ClMigUnit.Scratch.Set(ScratchOf(0, 0f)));
            tx.Commit();
        }

        var (srcChunk, _) = ReadLocation(dbe, id);
        Assert.That(srcChunk, Is.GreaterThanOrEqualTo(0), "sanity: source cluster should be allocated");

        var meta = Archetype<ClMigUnit>.Metadata;
        var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;

        // Queue a position update that crosses into a distant cell (past hysteresis).
        using (var tx = dbe.CreateQuickTransaction())
        {
            var eref = tx.OpenMut(id);
            ref var pos = ref eref.Write(ClMigUnit.Pos);
            pos.Bounds = new AABB2F { MinX = 850f, MinY = 850f, MaxX = 850f, MaxY = 850f };
            tx.Commit();
        }

        // Shrink the dirty bitmap AFTER the update commits but BEFORE the tick fence takes its snapshot.
        // This simulates the natural worst case: segment grew past the bitmap's size and a subsequent
        // migration targets a chunk id beyond the snapshot. We shrink to srcChunk+1 words to preserve
        // the source cluster's dirty bit (required for DetectClusterMigrations to see the update
        // and detect the cell crossing) while truncating any word beyond the source. The migration
        // will then allocate a destination cluster whose id is > srcChunk, which triggers the edge case.
        cs.ClusterDirtyBitmap.ShrinkForTesting(wordCount: srcChunk + 1);

        dbe.WriteTickFence(1);

        // Post-migration: entity lives in a new cluster (different chunk id from source).
        var (dstChunk, dstSlot) = ReadLocation(dbe, id);
        Assert.That(dstChunk, Is.Not.EqualTo(srcChunk),
            "migration must have allocated a brand-new destination cluster (different chunkId)");

        // Assertion 1: the snapshot word count at the end of migration must cover the dst chunk id.
        // Pre-fix: LastMigrationDirtyBitsWordCount == 1 (the shrunk snapshot never grew; guard silently
        //          dropped the dst write because dstChunk >= 1).
        // Post-fix: LastMigrationDirtyBitsWordCount >= dstChunk+1 (Array.Resize grew the snapshot in place
        //           and the caller's reference was updated via the ref parameter).
        Assert.That(cs.LastMigrationDirtyBitsWordCount, Is.GreaterThan(dstChunk),
            $"dirtyBits snapshot must be grown to cover the new destination cluster " +
            $"(dstChunkId={dstChunk}, snapshot word count={cs.LastMigrationDirtyBitsWordCount})");

        // Assertion 2: the published tick-fence snapshot (stored as PreviousTickDirtySnapshot) must
        // contain the destination slot's bit. This proves the fix actually landed the bit, not just
        // that the array was grown.
        Assert.That(cs.PreviousTickDirtySnapshot, Is.Not.Null,
            "PreviousTickDirtySnapshot should be set after a tick fence with dirty content");
        Assert.That(cs.PreviousTickDirtySnapshot.Length, Is.GreaterThan(dstChunk),
            "PreviousTickDirtySnapshot must cover the dst chunk id");
        long dstBitMask = 1L << dstSlot;
        Assert.That(cs.PreviousTickDirtySnapshot[dstChunk] & dstBitMask, Is.EqualTo(dstBitMask),
            $"destination slot's dirty bit must be set in PreviousTickDirtySnapshot " +
            $"(dstChunk={dstChunk}, dstSlot={dstSlot}) — pre-fix dropped it when snapshot was too small");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Regression — Bug #1: EntityMap must use EntityKey, not RawValue
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Migration_ThenSubsequentSpawn_ReclaimingSourceSlot_DoesNotCorruptMigratedEntity()
    {
        // This is the critical regression test for the EntityMap key bug: if migration
        // fails to update the EntityMap record, the migrated entity's EntityMap still
        // points to its source slot. When a subsequent spawn reclaims that slot (because
        // ReleaseSlot cleared OccupancyBit but NOT the component data), the OLD entity's
        // EntityMap entry resolves to the NEW entity's bytes — silent cross-contamination.
        //
        // This test catches the bug by migrating A, spawning B (which will reclaim A's
        // old slot because source cell's cluster is now empty and will be reused), and
        // asserting that OpenMut(A) returns A's post-migration data, not B's.
        using var dbe = SetupEngineWithGrid();

        EntityId idA;
        using (var tx = dbe.CreateQuickTransaction())
        {
            idA = tx.Spawn<ClMigUnit>(
                ClMigUnit.Pos.Set(PointAt(50f, 50f, tag: 101)),
                ClMigUnit.Scratch.Set(ScratchOf(counter: 111, energy: 1.1f)));
            tx.Commit();
        }

        // Move A to a distant cell
        using (var tx = dbe.CreateQuickTransaction())
        {
            var eref = tx.OpenMut(idA);
            ref var pos = ref eref.Write(ClMigUnit.Pos);
            pos.Bounds = new AABB2F { MinX = 550f, MinY = 750f, MaxX = 550f, MaxY = 750f };
            tx.Commit();
        }

        dbe.WriteTickFence(1);

        // Spawn B in the source cell — ClaimSlotInCell finds the source cell has zero clusters
        // (migration deallocated the empty cluster), so B lands in a fresh cluster. That cluster
        // may reuse the same chunk id A originally occupied, since ChunkBasedSegment's free list
        // returns recently-freed chunks first.
        EntityId idB;
        using (var tx = dbe.CreateQuickTransaction())
        {
            idB = tx.Spawn<ClMigUnit>(
                ClMigUnit.Pos.Set(PointAt(50f, 50f, tag: 202)),
                ClMigUnit.Scratch.Set(ScratchOf(counter: 222, energy: 2.2f)));
            tx.Commit();
        }

        // A must still return its post-migration state.
        using (var tx = dbe.CreateQuickTransaction())
        {
            var erefA = tx.OpenMut(idA);
            ref readonly var posA = ref erefA.Read(ClMigUnit.Pos);
            ref readonly var scratchA = ref erefA.Read(ClMigUnit.Scratch);
            Assert.That(posA.Tag, Is.EqualTo(101), "A's tag must survive migration + subsequent B spawn");
            Assert.That(posA.Bounds.MinX, Is.EqualTo(550f), "A's position must reflect migration destination");
            Assert.That(posA.Bounds.MinY, Is.EqualTo(750f));
            Assert.That(scratchA.Counter, Is.EqualTo(111), "A's transient counter must not be corrupted by B's spawn");
            Assert.That(scratchA.Energy, Is.EqualTo(1.1f).Within(1e-4f));
        }

        // And B must have its own data in its own cell.
        using (var tx = dbe.CreateQuickTransaction())
        {
            var erefB = tx.OpenMut(idB);
            ref readonly var posB = ref erefB.Read(ClMigUnit.Pos);
            ref readonly var scratchB = ref erefB.Read(ClMigUnit.Scratch);
            Assert.That(posB.Tag, Is.EqualTo(202));
            Assert.That(posB.Bounds.MinX, Is.EqualTo(50f));
            Assert.That(scratchB.Counter, Is.EqualTo(222));
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Non-unique cluster B+Tree index — destroy + migrate must only affect the
    // targeted entity, not its siblings sharing the same key value.
    //
    // Pre-fix: the cluster destroy path and Phase 3 ExecuteMigrations called
    // `field.Index.Remove(&key, ...)` which on a MultipleBTree wipes EVERY
    // value at that key — corrupting the index for all sibling entities.
    //
    // Fix (issue #229 Phase 3): per-entity elementId is stored in the cluster
    // layout tail (see ArchetypeClusterInfo.IndexElementIdOffset). Destroy /
    // migration read that elementId and call RemoveValue(key, elementId, value)
    // to remove only the specific (key, clusterLocation) pair.
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Direct inspection of the cluster B+Tree buffer at a given key. Returns the total element count
    /// across the whole value-buffer chain. Bypasses the query engine so we assert on the raw index
    /// state rather than whatever the query planner decides to do (fallback scans, etc.).
    /// </summary>
    private static unsafe int ReadIndexBufferCount(DatabaseEngine dbe, ushort archetypeId, int tagKey)
    {
        var cs = dbe._archetypeStates[archetypeId].ClusterState;
        // ClMigPos.Tag is the only indexed field on the only indexed component slot.
        ref var ixSlot = ref cs.IndexSlots[0];
        ref var field = ref ixSlot.Fields[0];
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var idxAccessor = cs.IndexSegment.CreateChunkAccessor();
        try
        {
            using var buf = field.Index.TryGetMultiple(&tagKey, ref idxAccessor);
            return buf.IsValid ? buf.TotalCount : 0;
        }
        finally
        {
            idxAccessor.Dispose();
        }
    }

    /// <summary>
    /// The VALUES in the cluster B+Tree buffer at a given key — the ClusterLocations the index resolves that key to.
    /// </summary>
    /// <remarks>
    /// <see cref="ReadIndexBufferCount"/> is not enough and the difference is the point. A count is invariant under a value-only update, so it says nothing
    /// about whether a migrated entity's entry was repointed at the cluster it moved to. The whole suite passed with migration's index maintenance ablated
    /// away, because the only assertions on this buffer were counts.
    /// </remarks>
    private static unsafe List<int> ReadIndexBufferValues(DatabaseEngine dbe, ushort archetypeId, int tagKey)
    {
        var cs = dbe._archetypeStates[archetypeId].ClusterState;
        ref var ixSlot = ref cs.IndexSlots[0];
        ref var field = ref ixSlot.Fields[0];
        var values = new List<int>();
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var idxAccessor = cs.IndexSegment.CreateChunkAccessor();
        try
        {
            using var buf = field.Index.TryGetMultiple(&tagKey, ref idxAccessor);
            if (!buf.IsValid)
            {
                return values;
            }

            do
            {
                foreach (var v in buf.ReadOnlyElements)
                {
                    values.Add(v);
                }
            }
            while (buf.NextChunk());
        }
        finally
        {
            idxAccessor.Dispose();
        }

        return values;
    }

    [Test]
    public void ClusterIndex_MigrateOneEntity_RepointsItsIndexValueAtTheNewClusterLocation()
    {
        // The gap #872 step 6 found: every existing assertion on this buffer is a COUNT, and a count cannot tell a repointed entry from an untouched one.
        // With the tick fence's index maintenance ablated entirely, all 5 675 tests stayed green — a migrated entity kept an index entry pointing at the
        // cluster slot it had left, and nothing noticed. This asserts the thing that actually has to be true.
        using var dbe = SetupEngineWithGrid();
        var meta = Archetype<ClMigUnit>.Metadata;

        EntityId[] ids = new EntityId[3];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 3; i++)
            {
                ids[i] = tx.Spawn<ClMigUnit>(
                    ClMigUnit.Pos.Set(PointAt(50f + i, 50f, tag: 4242)),
                    ClMigUnit.Scratch.Set(ScratchOf(i, 0f)));
            }
            tx.Commit();
        }

        var before = ReadIndexBufferValues(dbe, meta.ArchetypeId, 4242);
        Assert.That(before, Has.Count.EqualTo(3), "sanity: three (Tag=4242, clusterLocation) entries before migration");

        using (var tx = dbe.CreateQuickTransaction())
        {
            var eref = tx.OpenMut(ids[0]);
            ref var pos = ref eref.Write(ClMigUnit.Pos);
            pos.Bounds = new AABB2F { MinX = 550f, MinY = 750f, MaxX = 550f, MaxY = 750f };
            tx.Commit();
        }

        dbe.WriteTickFence(1);

        var after = ReadIndexBufferValues(dbe, meta.ArchetypeId, 4242);
        Assert.That(after, Has.Count.EqualTo(3), "the buffer must still hold three entries — the migrant is repointed, not removed");

        // The migrant crossed into another cell and therefore another cluster, so its ClusterLocation must have changed; the two siblings did not move, so
        // theirs must not have. Exactly one value gone, exactly one value new.
        var beforeSet = new HashSet<int>(before);
        var afterSet = new HashSet<int>(after);

        var departed = new HashSet<int>(beforeSet);
        departed.ExceptWith(afterSet);
        var arrived = new HashSet<int>(afterSet);
        arrived.ExceptWith(beforeSet);

        var trace = $"before={string.Join(",", before)} after={string.Join(",", after)}";
        Assert.That(departed, Has.Count.EqualTo(1),
            $"exactly one cluster location must have left the index (the migrant's old slot); {trace}");
        Assert.That(arrived, Has.Count.EqualTo(1),
            $"exactly one cluster location must have entered the index (the migrant's new slot); {trace}");
    }

    [Test]
    public void ClusterIndex_NonUniqueField_DestroyOneEntity_PreservesSiblingsInIndex()
    {
        // Three entities sharing Tag = 777 in cell (0,0). Destroy one — the other two must still
        // have their (Tag=777, clusterLocation) entries in the cluster B+Tree value buffer.
        //
        // Direct B+Tree inspection via TryGetMultiple bypasses the query engine so we measure the
        // raw index state — pre-fix behavior wipes all 3 entries; post-fix leaves 2.
        using var dbe = SetupEngineWithGrid();
        var meta = Archetype<ClMigUnit>.Metadata;

        EntityId[] ids = new EntityId[3];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 3; i++)
            {
                ids[i] = tx.Spawn<ClMigUnit>(
                    ClMigUnit.Pos.Set(PointAt(50f + i, 50f, tag: 777)),
                    ClMigUnit.Scratch.Set(ScratchOf(i, 0f)));
            }
            tx.Commit();
        }

        // Sanity: all three (Tag=777, clusterLocation) entries are in the B+Tree buffer
        Assert.That(ReadIndexBufferCount(dbe, meta.ArchetypeId, 777), Is.EqualTo(3),
            "sanity: index buffer must have one entry per spawned entity before destroy");

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(ids[0]);
            tx.Commit();
        }

        // The two siblings' entries must still be in the buffer. Pre-fix bug: all three are wiped.
        Assert.That(ReadIndexBufferCount(dbe, meta.ArchetypeId, 777), Is.EqualTo(2),
            "two sibling entries must remain after one is destroyed — buffer must NOT be wiped");
    }

    [Test]
    public void ClusterIndex_NonUniqueField_MigrateOneEntity_PreservesSiblingsInIndex()
    {
        // Three entities sharing Tag = 888 in cell A. Move one to cell B past the hysteresis margin;
        // after the tick fence, the migration path must only remove the migrant's (Tag=888, oldLoc)
        // entry and insert a (Tag=888, newLoc) entry — NOT wipe the entire Tag=888 bucket. Direct
        // B+Tree inspection (TryGetMultiple) asserts the raw buffer state.
        using var dbe = SetupEngineWithGrid();
        var meta = Archetype<ClMigUnit>.Metadata;

        EntityId[] ids = new EntityId[3];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 3; i++)
            {
                ids[i] = tx.Spawn<ClMigUnit>(
                    ClMigUnit.Pos.Set(PointAt(50f + i, 50f, tag: 888)),
                    ClMigUnit.Scratch.Set(ScratchOf(i, 0f)));
            }
            tx.Commit();
        }

        Assert.That(ReadIndexBufferCount(dbe, meta.ArchetypeId, 888), Is.EqualTo(3),
            "sanity: all three (Tag=888, clusterLocation) entries before migration");

        using (var tx = dbe.CreateQuickTransaction())
        {
            var eref = tx.OpenMut(ids[0]);
            ref var pos = ref eref.Write(ClMigUnit.Pos);
            pos.Bounds = new AABB2F { MinX = 550f, MinY = 750f, MaxX = 550f, MaxY = 750f };
            tx.Commit();
        }

        dbe.WriteTickFence(1);

        // Post-migration: still three entries in the buffer. The migrant's entry has been rekeyed
        // from (Tag=888, oldLoc) to (Tag=888, newLoc); the two siblings' entries are untouched.
        // Pre-fix bug: Remove(key) wipes all three, then Add re-inserts one → count = 1.
        Assert.That(ReadIndexBufferCount(dbe, meta.ArchetypeId, 888), Is.EqualTo(3),
            "index buffer must still hold 3 entries after migrating 1 of the 3 sibling entities");
    }

    [Test]
    public void ClusterIndex_NonUniqueField_ManyCollisions_PreservesSiblings()
    {
        // Create enough entities sharing Tag = 999 to force the cluster B+Tree value buffer to
        // overflow into multiple chunks. Migrate a late-added one (most likely to live in the
        // overflow chunk) and verify the elementId-based RemoveValue correctly targets its entry
        // without corrupting any of the 199 siblings.
        using var dbe = SetupEngineWithGrid();
        var meta = Archetype<ClMigUnit>.Metadata;

        const int collisionCount = 200;
        EntityId[] ids = new EntityId[collisionCount];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < collisionCount; i++)
            {
                // Spread across one cell, all with Tag = 999 to force collisions in the BTree buffer
                ids[i] = tx.Spawn<ClMigUnit>(
                    ClMigUnit.Pos.Set(PointAt(50f + (i % 40) * 0.5f, 50f + (i / 40) * 0.5f, tag: 999)),
                    ClMigUnit.Scratch.Set(ScratchOf(i, 0f)));
            }
            tx.Commit();
        }

        Assert.That(ReadIndexBufferCount(dbe, meta.ArchetypeId, 999), Is.EqualTo(collisionCount),
            "sanity: all collision entries present in the buffer before migration");

        // Migrate the last-added entity — more likely to live in an overflow chunk
        int migrantIdx = collisionCount - 1;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var eref = tx.OpenMut(ids[migrantIdx]);
            ref var pos = ref eref.Write(ClMigUnit.Pos);
            pos.Bounds = new AABB2F { MinX = 550f, MinY = 750f, MaxX = 550f, MaxY = 750f };
            tx.Commit();
        }

        dbe.WriteTickFence(1);

        // All entries must remain in the buffer: the migrant's entry is rekeyed from oldLoc to
        // newLoc via RemoveValue(elementId) + Add; the sibling entries are untouched. The elementId
        // for the migrant was stored in the source cluster's elementId tail at spawn time and
        // retrieved at migration time to target the correct chain chunk — O(1), no scan.
        Assert.That(ReadIndexBufferCount(dbe, meta.ArchetypeId, 999), Is.EqualTo(collisionCount),
            $"all {collisionCount} entries must remain — elementId must have correctly targeted the migrant's chunk entry");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Migration into existing-non-empty destination cell
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Migration_IntoExistingNonEmptyDestCell_AbsorbsIntoExistingCluster()
    {
        // The dominant AntHill workload: destination cells already contain clusters with
        // free slots. ClaimSlotInCell's "scan existing clusters first" fast path must
        // produce a slot in the existing cluster, not allocate a new one.
        using var dbe = SetupEngineWithGrid();

        EntityId migrant;
        using (var tx = dbe.CreateQuickTransaction())
        {
            // Source cell (0, 0): one cluster containing the migrant
            migrant = tx.Spawn<ClMigUnit>(
                ClMigUnit.Pos.Set(PointAt(50f, 50f, tag: 1)),
                ClMigUnit.Scratch.Set(ScratchOf(1, 0.1f)));

            // Destination cell (5, 7): pre-populate with 2 resident entities so there's an existing cluster.
            tx.Spawn<ClMigUnit>(
                ClMigUnit.Pos.Set(PointAt(550f, 750f, tag: 2)),
                ClMigUnit.Scratch.Set(ScratchOf(2, 0.2f)));
            tx.Spawn<ClMigUnit>(
                ClMigUnit.Pos.Set(PointAt(555f, 755f, tag: 3)),
                ClMigUnit.Scratch.Set(ScratchOf(3, 0.3f)));
            tx.Commit();
        }

        int srcCell = dbe.SpatialGrid.WorldToCellKey(50f, 50f, 0f);
        int dstCell = dbe.SpatialGrid.WorldToCellKey(550f, 750f, 0f);

        var meta = Archetype<ClMigUnit>.Metadata;
        var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        Assert.That(cs.ActiveClusterCount, Is.EqualTo(2),
            "2 clusters: one for source cell, one for destination cell");
        Assert.That(dbe.SpatialGrid.GetCell(dstCell).ClusterCount, Is.EqualTo(1));
        Assert.That(dbe.SpatialGrid.GetCell(dstCell).EntityCount, Is.EqualTo(2));

        // Move migrant into the destination cell
        using (var tx = dbe.CreateQuickTransaction())
        {
            var eref = tx.OpenMut(migrant);
            ref var pos = ref eref.Write(ClMigUnit.Pos);
            pos.Bounds = new AABB2F { MinX = 560f, MinY = 760f, MaxX = 560f, MaxY = 760f };
            tx.Commit();
        }

        dbe.WriteTickFence(1);

        // Destination cell absorbs the migrant into its EXISTING cluster — still 1 cluster, now 3 entities.
        Assert.That(dbe.SpatialGrid.GetCell(dstCell).ClusterCount, Is.EqualTo(1),
            "existing cluster absorbs the migrant — no new cluster allocated");
        Assert.That(dbe.SpatialGrid.GetCell(dstCell).EntityCount, Is.EqualTo(3));
        // Source cell is now empty
        Assert.That(dbe.SpatialGrid.GetCell(srcCell).ClusterCount, Is.EqualTo(0));
        Assert.That(dbe.SpatialGrid.GetCell(srcCell).EntityCount, Is.EqualTo(0));

        // Overall archetype now has 1 cluster (the destination), not 2
        Assert.That(cs.ActiveClusterCount, Is.EqualTo(1));

        // All three entities (the migrant + the two residents) are readable and have their correct data
        using (var tx = dbe.CreateQuickTransaction())
        {
            var migrantRef = tx.OpenMut(migrant);
            ref readonly var migrantPos = ref migrantRef.Read(ClMigUnit.Pos);
            Assert.That(migrantPos.Tag, Is.EqualTo(1));
            Assert.That(migrantPos.Bounds.MinX, Is.EqualTo(560f));
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // EntityMap + cluster data consistent after migration
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Migration_EntityMapAndClusterData_ConsistentAfterMigration()
    {
        // Rather than round-trip through the R-Tree query API (which is 6-coord 3D and unwieldy for
        // 2D fields), this test verifies the primary correctness property directly: after migration,
        // looking up the entity by id returns cluster data that matches the post-migration position,
        // and ClusterCellMap on the entity's new cluster chunk id points to the destination cell.
        using var dbe = SetupEngineWithGrid();

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<ClMigUnit>(
                ClMigUnit.Pos.Set(PointAt(50f, 50f, tag: 7)),
                ClMigUnit.Scratch.Set(ScratchOf(0, 0f)));
            tx.Commit();
        }

        var (preChunk, _) = ReadLocation(dbe, id);

        using (var tx = dbe.CreateQuickTransaction())
        {
            var eref = tx.OpenMut(id);
            ref var pos = ref eref.Write(ClMigUnit.Pos);
            pos.Bounds = new AABB2F { MinX = 450f, MinY = 550f, MaxX = 450f, MaxY = 550f };
            tx.Commit();
        }

        dbe.WriteTickFence(1);

        // 1. Direct entity read: position should reflect the migrated values.
        using (var tx = dbe.CreateQuickTransaction())
        {
            var eref = tx.OpenMut(id);
            ref readonly var pos = ref eref.Read(ClMigUnit.Pos);
            Assert.That(pos.Bounds.MinX, Is.EqualTo(450f));
            Assert.That(pos.Bounds.MinY, Is.EqualTo(550f));
            Assert.That(pos.Tag, Is.EqualTo(7), "non-spatial component fields survive migration");
        }

        // 2. The entity's new cluster chunk id is mapped to the destination cell.
        var (postChunk, _) = ReadLocation(dbe, id);
        Assert.That(postChunk, Is.GreaterThanOrEqualTo(0), "entity still resolvable post-migration");
        Assert.That(postChunk, Is.Not.EqualTo(preChunk),
            "post-migration cluster chunk id must differ from pre-migration (new cell → new cluster)");

        var meta = Archetype<ClMigUnit>.Metadata;
        var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        int dstCell = dbe.SpatialGrid.WorldToCellKey(450f, 550f, 0f);
        Assert.That(cs.ClusterCellMap[postChunk], Is.EqualTo(dstCell));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Migration folds the migrant into the destination's MVCC visibility summary
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Migration is the born-site that is easiest to miss: it moves an entity between clusters of the same archetype WITHOUT changing its BornTSN, so a
    /// destination cluster that was entirely committed before a live reader's snapshot silently acquires an entity that is not. If the fold is dropped, the
    /// destination keeps claiming full visibility and the SoA scan emits a phantom for every reader in between.
    /// </summary>
    /// <remarks>
    /// The residents are spawned in an EARLIER transaction than the migrant, which is what makes the fold observable: the destination summary must RISE to
    /// the migrant's older-but-larger BornTSN. Spawning both in one transaction would leave the summary unchanged and the assertion vacuous.
    /// </remarks>
    [Test]
    public void Migration_FoldsMigrantBornTsn_IntoDestinationVisibilitySummary()
    {
        using var dbe = SetupEngineWithGrid();

        EntityId resident;
        using (var tx = dbe.CreateQuickTransaction())
        {
            resident = tx.Spawn<ClMigUnit>(
                ClMigUnit.Pos.Set(PointAt(550f, 750f, tag: 2)),
                ClMigUnit.Scratch.Set(ScratchOf(2, 0.2f)));
            tx.Commit();
        }

        EntityId migrant;
        using (var tx = dbe.CreateQuickTransaction())
        {
            migrant = tx.Spawn<ClMigUnit>(
                ClMigUnit.Pos.Set(PointAt(50f, 50f, tag: 1)),
                ClMigUnit.Scratch.Set(ScratchOf(1, 0.1f)));
            tx.Commit();
        }

        var meta = Archetype<ClMigUnit>.Metadata;
        var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        var (dstChunk, _) = ReadLocation(dbe, resident);
        var dstBornBefore = cs.ClusterMaxBornTsn[dstChunk];
        var (srcChunk, _) = ReadLocation(dbe, migrant);
        var migrantBorn = cs.ClusterMaxBornTsn[srcChunk];
        Assert.That(migrantBorn, Is.GreaterThan(dstBornBefore),
            "the migrant must be younger than the destination's residents, or the fold cannot be distinguished from doing nothing");

        using (var tx = dbe.CreateQuickTransaction())
        {
            var eref = tx.OpenMut(migrant);
            ref var pos = ref eref.Write(ClMigUnit.Pos);
            pos.Bounds = new AABB2F { MinX = 560f, MinY = 760f, MaxX = 560f, MaxY = 760f };
            tx.Commit();
        }

        dbe.WriteTickFence(1);

        var (postChunk, _) = ReadLocation(dbe, migrant);
        Assert.That(postChunk, Is.EqualTo(dstChunk), "the migrant is absorbed into the destination cell's existing cluster");

        var report = dbe.RunStorageIntegrityCheck();
        foreach (var issue in report.Issues)
        {
            TestContext.WriteLine($"ISSUE {issue.Kind}: {issue.Detail}");
        }

        // Both assertions, not the first that fails: the direct read says the fold happened, the audit says the summary is sound. Dropping the fold must be
        // visible through the audit alone — that is the property this fixture is here to pin, since a future site has no direct assertion watching it.
        Assert.Multiple(() =>
        {
            Assert.That(cs.ClusterMaxBornTsn[dstChunk], Is.GreaterThanOrEqualTo(migrantBorn),
                "the destination summary must now bound the migrant's BornTSN — migration carries the entity's TSNs across unchanged");
            Assert.That(report.Issues, Has.None.Matches<StorageIntegrityIssue>(i => i.Kind == StorageIntegrityIssueKind.ClusterVisibilitySummaryUnsound),
                "the recomputed summary must match after migration");
            Assert.That(report.VisibilitySummaryClustersChecked, Is.GreaterThan(0),
                "GENUINENESS: the audit must have recomputed at least the destination cluster");
        });
    }

    private static DatabaseEngine CreateNamedEngineWithGrid(string dbName)
    {
        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
          .AddResourceRegistry()
          .AddMemoryAllocator()
          .AddEpochManager()
          .AddHighResolutionSharedTimer()
          .AddDeadlineWatchdog()
          .AddScopedManagedPagedMemoryMappedFile(o =>
          {
              o.DatabaseName = dbName;
              o.DatabaseCacheSize = (ulong)(50 * 1024 * PagedMMF.PageSize);
              o.PagesDebugPattern = false;
          })
          .AddScopedDatabaseEngine(o => TestWalProfile.Apply(o, System.IO.Path.Combine(System.IO.Path.GetTempPath(), dbName)));
        sc.AddScoped<IWalFileIO>(_ => new InMemoryWalFileIO());

        var sp = sc.BuildServiceProvider();
        var dbe = sp.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClMigPos>();
        dbe.RegisterComponentFromAccessor<ClMigScratch>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(
            worldMin: new Vector2(0, 0),
            worldMax: new Vector2(WorldMax, WorldMax),
            cellSize: CellSize));
        dbe.InitializeArchetypes();
        return dbe;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // WriteSpatial barrier tests (V1, AABB2F path).
    // The barrier replaces the previous "raw GetSpan + MarkClusterSlotDirty" pattern with an
    // inline detector that updates the per-cluster bookkeeping arrays so the fence loop iterates
    // only the clusters that actually changed. See ClusterRef.WriteSpatial.
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void WriteSpatial_FlagsMigration_NoFullScan()
    {
        using var dbe = SetupEngineWithGrid();

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<ClMigUnit>(
                ClMigUnit.Pos.Set(PointAt(50f, 50f)),
                ClMigUnit.Scratch.Set(ScratchOf(0, 0f)));
            tx.Commit();
        }

        int srcCell = dbe.SpatialGrid.WorldToCellKey(50f, 50f, 0f);
        int dstCell = dbe.SpatialGrid.WorldToCellKey(250f, 250f, 0f);
        Assert.That(srcCell, Is.Not.EqualTo(dstCell));

        var meta = Archetype<ClMigUnit>.Metadata;
        var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        var (preChunkId, preSlot) = ReadLocation(dbe, id);

        // WriteSpatial via cluster API — same flow AntHill's ant integration uses.
        using (var tx = dbe.CreateQuickTransaction())
        {
            var accessor = tx.For<ClMigUnit>();
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                if (cluster.ChunkId != preChunkId) continue;
                cluster.WriteSpatial(ClMigUnit.Pos, preSlot,
                    new ClMigPos { Bounds = new AABB2F { MinX = 250f, MinY = 250f, MaxX = 250f, MaxY = 250f } });
            }
            accessor.Dispose();
            tx.Commit();
        }

        // The barrier must have flagged a migration on the source cluster.
        Assert.That(cs.ClusterMigrationPendingSlots, Is.Not.Null);
        Assert.That(cs.ClusterMigrationPendingSlots[preChunkId] & (1UL << preSlot), Is.Not.Zero,
            "WriteSpatial must set the per-slot migration bit on the source cluster");
        Assert.That(cs.ClusterMigrationDestCellKeys[preChunkId], Is.EqualTo(dstCell),
            "WriteSpatial must record the destination cell key");

        // Fence drains the flagged migration → entity ends up in dest cell.
        dbe.WriteTickFence(1);

        ref var srcCellRef = ref dbe.SpatialGrid.GetCell(srcCell);
        ref var dstCellRef = ref dbe.SpatialGrid.GetCell(dstCell);
        Assert.That(srcCellRef.EntityCount, Is.EqualTo(0), "source cell drained");
        Assert.That(dstCellRef.EntityCount, Is.EqualTo(1), "destination cell holds the migrated entity");
        Assert.That(cs.LastTickMigrationCount, Is.EqualTo(1), "telemetry counter records 1 migration");

        // Post-fence the bookkeeping bits MUST be cleared.
        Assert.That(cs.ClusterMigrationPendingSlots[preChunkId], Is.Zero, "migration pending bits cleared at fence");
    }

    /// <summary>
    /// An entity that crosses into another cell and comes back within the same tick is still home at the fence. The outbound <c>WriteSpatial</c> flags the
    /// slot with the far cell as its destination; the return write finds the entity home and clears nothing; the drain must therefore re-derive the
    /// destination from the position the slot holds NOW and drop the request, not execute the one recorded at write time (CC-02).
    /// </summary>
    /// <remarks>
    /// Found by <c>ClusterPlacementTests.ConcurrentSpawnsAndBoundGrowthKeepClustersInTheirCell</c> in its two-cell form: a concurrent writer reached a
    /// spawn's slot between its claim and its data (the documented in-flight window), flagged a crossing from the writer's position, the spawn's own
    /// position then landed, and the fence migrated the entity to a cell it had never been in. That took a race to reach; this takes two writes.
    /// </remarks>
    [Test]
    [VerifiesRule("CC-02")]
    public void WriteSpatial_CrossAndReturnInOneTick_StaysInItsCell()
    {
        using var dbe = SetupEngineWithGrid();
        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<ClMigUnit>(ClMigUnit.Pos.Set(PointAt(50f, 50f)), ClMigUnit.Scratch.Set(ScratchOf(0, 0f)));
            tx.Commit();
        }

        dbe.WriteTickFence(1);
        var homeCell = dbe.SpatialGrid.WorldToCellKey(50f, 50f, 0f);
        var cs = dbe._archetypeStates[Archetype<ClMigUnit>.Metadata.ArchetypeId].ClusterState;
        var (chunkId, slot) = ReadLocation(dbe, id);

        using (var tx = dbe.CreateQuickTransaction())
        {
            var accessor = tx.For<ClMigUnit>();
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                if (cluster.ChunkId != chunkId)
                {
                    continue;
                }

                cluster.WriteSpatial(ClMigUnit.Pos, slot, PointAt(250f, 250f));   // out: flagged, destination = the far cell
                cluster.WriteSpatial(ClMigUnit.Pos, slot, PointAt(60f, 60f));     // back home in the same tick — nothing clears the flag
            }

            accessor.Dispose();
            tx.Commit();
        }

        Assert.That(cs.ClusterMigrationPendingSlots[chunkId] & (1UL << slot), Is.Not.Zero, "precondition: the outbound write left its flag behind");
        dbe.WriteTickFence(2);

        var (chunkAfter, _) = ReadLocation(dbe, id);
        Assert.Multiple(() =>
        {
            Assert.That(cs.ClusterCellMap[chunkAfter], Is.EqualTo(homeCell), "migrated on a destination the entity no longer has");
            Assert.That(dbe.SpatialGrid.GetCell(homeCell).EntityCount, Is.EqualTo(1));
            Assert.That(cs.LastTickMigrationCount, Is.Zero, "a stale flag is dropped at the drain, not executed");
        });
    }

    /// <summary>Two crossings in one tick: the flag's per-cluster destination is the LAST write's, and the drain must land the entity where it is.</summary>
    [Test]
    [VerifiesRule("CC-02")]
    public void WriteSpatial_TwoCrossingsInOneTick_LandsWhereItIs()
    {
        using var dbe = SetupEngineWithGrid();
        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<ClMigUnit>(ClMigUnit.Pos.Set(PointAt(50f, 50f)), ClMigUnit.Scratch.Set(ScratchOf(0, 0f)));
            tx.Commit();
        }

        dbe.WriteTickFence(1);
        var (chunkId, slot) = ReadLocation(dbe, id);
        using (var tx = dbe.CreateQuickTransaction())
        {
            var accessor = tx.For<ClMigUnit>();
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                if (cluster.ChunkId != chunkId)
                {
                    continue;
                }

                cluster.WriteSpatial(ClMigUnit.Pos, slot, PointAt(250f, 250f));
                cluster.WriteSpatial(ClMigUnit.Pos, slot, PointAt(150f, 50f));
            }

            accessor.Dispose();
            tx.Commit();
        }

        dbe.WriteTickFence(2);
        var cs = dbe._archetypeStates[Archetype<ClMigUnit>.Metadata.ArchetypeId].ClusterState;
        var (chunkAfter, _) = ReadLocation(dbe, id);
        var whereItIs = dbe.SpatialGrid.WorldToCellKey(150f, 50f, 0f);
        Assert.Multiple(() =>
        {
            Assert.That(cs.ClusterCellMap[chunkAfter], Is.EqualTo(whereItIs));
            Assert.That(dbe.SpatialGrid.GetCell(whereItIs).EntityCount, Is.EqualTo(1));
            Assert.That(cs.LastTickMigrationCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void WriteSpatial_AABBGrow_InlineUpdate()
    {
        using var dbe = SetupEngineWithGrid();

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            // Spawn near cell-(0,0) center. AABB grow when we move further from center, staying inside the cell.
            id = tx.Spawn<ClMigUnit>(
                ClMigUnit.Pos.Set(PointAt(50f, 50f)),
                ClMigUnit.Scratch.Set(ScratchOf(0, 0f)));
            tx.Commit();
        }

        var meta = Archetype<ClMigUnit>.Metadata;
        var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        var (chunkId, slotIdx) = ReadLocation(dbe, id);

        // Move within the same cell but to a more extreme position — AABB should grow inline.
        using (var tx = dbe.CreateQuickTransaction())
        {
            var accessor = tx.For<ClMigUnit>();
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                if (cluster.ChunkId != chunkId) continue;
                cluster.WriteSpatial(ClMigUnit.Pos, slotIdx,
                    new ClMigPos { Bounds = new AABB2F { MinX = 95f, MinY = 95f, MaxX = 95f, MaxY = 95f } });
            }
            accessor.Dispose();
            tx.Commit();
        }

        // Inline grow must have updated ClusterAabbs[chunkId] (MaxX should now be 95).
        Assert.That(cs.ClusterAabbs[chunkId].MaxX, Is.EqualTo(95f).Within(0.001f),
            "AABB MaxX grew inline at WriteSpatial time");
        // No migration should be flagged (still in same cell).
        Assert.That(cs.ClusterMigrationPendingSlots[chunkId], Is.Zero, "no migration when staying in cell");
        // ClusterProcessBitmap should be set so the fence updates the per-cell index.
        Assert.That((cs.ClusterProcessBitmap[chunkId >> 6] >> (chunkId & 63)) & 1L, Is.EqualTo(1L),
            "process bit set for fence-time PerCellIndex update");
    }

    [Test]
    public void WriteSpatial_AABBShrink_DeferredToFence()
    {
        using var dbe = SetupEngineWithGrid();

        // Spawn TWO entities in the same cluster so one of them can shrink the AABB.
        EntityId idA, idB;
        using (var tx = dbe.CreateQuickTransaction())
        {
            idA = tx.Spawn<ClMigUnit>(
                ClMigUnit.Pos.Set(PointAt(10f, 50f)),
                ClMigUnit.Scratch.Set(ScratchOf(0, 0f)));
            idB = tx.Spawn<ClMigUnit>(
                ClMigUnit.Pos.Set(PointAt(90f, 50f)),
                ClMigUnit.Scratch.Set(ScratchOf(0, 0f)));
            tx.Commit();
        }

        var meta = Archetype<ClMigUnit>.Metadata;
        var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        var (chunkA, slotA) = ReadLocation(dbe, idA);
        var (chunkB, slotB) = ReadLocation(dbe, idB);
        // Sanity: tests assume both in same cluster.
        if (chunkA != chunkB)
        {
            Assert.Ignore("Two spawns landed in different clusters — test only meaningful for shared cluster");
        }

        // Pre-state: cluster's AABB MaxX should reflect entity B at x=90.
        Assert.That(cs.ClusterAabbs[chunkA].MaxX, Is.EqualTo(90f).Within(0.001f));

        // Move B inward (away from MaxX extreme). This should flag the MaxX shrink axis but NOT
        // update the stored AABB inline (shrink can't be done in O(1) — we don't know the new
        // second-most-extreme entity).
        using (var tx = dbe.CreateQuickTransaction())
        {
            var accessor = tx.For<ClMigUnit>();
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                if (cluster.ChunkId != chunkA) continue;
                cluster.WriteSpatial(ClMigUnit.Pos, slotB,
                    new ClMigPos { Bounds = new AABB2F { MinX = 50f, MinY = 50f, MaxX = 50f, MaxY = 50f } });
            }
            accessor.Dispose();
            tx.Commit();
        }

        // Shrink flag set, stored AABB unchanged until fence.
        Assert.That(cs.ClusterShrinkPendingAxes[chunkA] & 0x02, Is.Not.Zero, "MaxX shrink axis flagged");
        Assert.That(cs.ClusterAabbs[chunkA].MaxX, Is.EqualTo(90f).Within(0.001f),
            "stored AABB MaxX unchanged at write time (deferred to fence)");

        // Fence rescans → AABB tightens to reflect entity A at x=10 (now the MaxX-extreme).
        dbe.WriteTickFence(1);

        Assert.That(cs.ClusterAabbs[chunkA].MaxX, Is.LessThan(90f),
            "after fence, MaxX shrunk because the entity that defined the previous extreme moved inward");
        Assert.That(cs.ClusterShrinkPendingAxes[chunkA], Is.Zero, "shrink flags cleared at fence");
    }

    [Test]
    public void WriteSpatial_InteriorEntityMove_NoShrinkFlag()
    {
        using var dbe = SetupEngineWithGrid();

        // Three entities: A and B define the cluster's extremes on both axes; C is strictly interior.
        // Moving C around within the bounding box should NOT flag any shrink axis — C wasn't at any extreme.
        EntityId idA, idB, idC;
        using (var tx = dbe.CreateQuickTransaction())
        {
            idA = tx.Spawn<ClMigUnit>(
                ClMigUnit.Pos.Set(PointAt(10f, 10f)),
                ClMigUnit.Scratch.Set(ScratchOf(0, 0f)));
            idB = tx.Spawn<ClMigUnit>(
                ClMigUnit.Pos.Set(PointAt(90f, 90f)),
                ClMigUnit.Scratch.Set(ScratchOf(0, 0f)));
            idC = tx.Spawn<ClMigUnit>(
                ClMigUnit.Pos.Set(PointAt(50f, 50f)),
                ClMigUnit.Scratch.Set(ScratchOf(0, 0f)));
            tx.Commit();
        }

        var meta = Archetype<ClMigUnit>.Metadata;
        var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        var (chunkA, _) = ReadLocation(dbe, idA);
        var (chunkB, _) = ReadLocation(dbe, idB);
        var (chunkC, slotC) = ReadLocation(dbe, idC);
        if (chunkA != chunkB || chunkA != chunkC)
        {
            Assert.Ignore("Three spawns landed in different clusters — test only meaningful for shared cluster");
        }

        // Move C from (50, 50) → (60, 60). Still interior on both axes (10 < 60 < 90).
        // No extreme changes → no shrink, no grow → no fence work needed for the AABB.
        using (var tx = dbe.CreateQuickTransaction())
        {
            var accessor = tx.For<ClMigUnit>();
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                if (cluster.ChunkId != chunkC) continue;
                cluster.WriteSpatial(ClMigUnit.Pos, slotC,
                    new ClMigPos { Bounds = new AABB2F { MinX = 60f, MinY = 60f, MaxX = 60f, MaxY = 60f } });
            }
            accessor.Dispose();
            tx.Commit();
        }

        // Neither shrink nor migration flagged — only the slot's dirty bit was set.
        Assert.That(cs.ClusterShrinkPendingAxes[chunkC], Is.EqualTo(0), "interior move flags no shrink axis");
        Assert.That(cs.ClusterMigrationPendingSlots[chunkC], Is.Zero, "interior move triggers no migration");
        // Process bit should NOT be set (no grow either — C's new pos is strictly inside the cluster's existing AABB).
        Assert.That((cs.ClusterProcessBitmap[chunkC >> 6] >> (chunkC & 63)) & 1L, Is.EqualTo(0L),
            "interior move with no extreme change avoids the fence-time process loop entirely");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // CP-13 — ActiveChunkWriters conservation across cluster finalisation
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// CP-13: every ActiveChunkWriters registration is released. Emptying a cluster queues it for
    /// <c>DrainPendingClusterFinalizations</c>, which opens ChunkAccessors and dirties each drained chunk's page.
    /// Those accessors must reach Dispose/CommitChanges or the registration leaks permanently.
    /// </summary>
    /// <remarks>
    /// The regression this pins is #817, and it is worth spelling out why 5 000 tests walked past it. A leaked ACW
    /// corrupts nothing, throws nothing and costs no measurable time — its only consequence is that CP-11 skips
    /// that page in EVERY subsequent checkpoint cycle, so CK-03's coverage gate never opens, no WAL segment is ever
    /// recycled, and the log grows at the full write rate until the writer stalls. In the demo that surfaced ten
    /// minutes later as a WalBackPressureTimeout thrown from the tick fence — pointing at the WAL writer, which was
    /// innocent. Nothing observable connects the two ends, which is exactly why the invariant has to be asserted
    /// directly rather than inferred from behaviour.
    /// <para>
    /// The assertion must run QUIESCENT — no open transaction, no accessor alive. While anything is in flight a
    /// non-zero count is legitimate, and indistinguishable from a leak.
    /// </para>
    /// </remarks>
    [Test]
    [VerifiesRule("CP-13")]
    public void ClusterFinalization_ReleasesEveryActiveChunkWriter()
    {
        using var dbe = SetupEngineWithGrid();
        var mmf = ServiceProvider.GetRequiredService<ManagedPagedMMF>();

        Assert.That(mmf.CountPagesWithActiveChunkWriters(), Is.Zero, "precondition: no writer registered before the test spawns anything");

        // Fill one cell, then move every entity out of it. The source cluster empties, which is what queues a
        // finalisation — the path that leaked. Several entities so more than one page is involved.
        var ids = new EntityId[24];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < ids.Length; i++)
            {
                ids[i] = tx.Spawn<ClMigUnit>(
                    ClMigUnit.Pos.Set(PointAt(50f, 50f, i)),
                    ClMigUnit.Scratch.Set(ScratchOf(i, i)));
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < ids.Length; i++)
            {
                var eref = tx.OpenMut(ids[i]);
                ref var pos = ref eref.Write(ClMigUnit.Pos);
                pos.Bounds = new AABB2F { MinX = 650f, MinY = 650f, MaxX = 650f, MaxY = 650f };
            }
            tx.Commit();
        }

        // Migration executes on the next fence; the emptied cluster is drained on a later one, so run several.
        for (var tick = 2; tick <= 6; tick++)
        {
            dbe.WriteTickFence(tick);
        }

        Assert.That(mmf.CountPagesWithActiveChunkWriters(), Is.Zero,
            "every ActiveChunkWriters registration taken during migration and cluster finalisation must be released "
            + "(CP-13). A non-zero count here means the checkpoint coverage gate will skip those pages forever and "
            + "the WAL will never be reclaimed — see #817.");
    }

    /// <summary>
    /// Ground truth for "where does each entity carrying <paramref name="tagKey"/> actually live", read straight from cluster occupancy.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT derived from the index — comparing the index against itself is how the count-only assertions this fixture used to rely on managed to
    /// stay green with index maintenance ablated entirely.
    /// </remarks>
    private static unsafe List<int> ActualClusterLocationsForTag(DatabaseEngine dbe, ushort archetypeId, int tagKey)
    {
        var cs = dbe._archetypeStates[archetypeId].ClusterState;
        ref var ixSlot = ref cs.IndexSlots[0];
        ref var field = ref ixSlot.Fields[0];
        var compOffset = cs.Layout.ComponentOffset(ixSlot.Slot);
        var compSize = cs.Layout.ComponentSize(ixSlot.Slot);
        var fieldOffset = field.FieldOffset;

        var result = new List<int>();
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var accessor = cs.ClusterSegment.CreateChunkAccessor();
        try
        {
            for (var i = 0; i < cs.ActiveClusterCount; i++)
            {
                var cid = cs.ActiveClusterIds[i];
                var clusterBase = accessor.GetChunkAddress(cid);
                var occupancy = *(ulong*)clusterBase;
                while (occupancy != 0)
                {
                    var slot = BitOperations.TrailingZeroCount(occupancy);
                    occupancy &= occupancy - 1;
                    if (*(int*)(clusterBase + compOffset + slot * compSize + fieldOffset) == tagKey)
                    {
                        result.Add(cid * 64 + slot);
                    }
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        return result;
    }

    /// <summary>Where the EntityMap says an entity lives — the (clusterChunkId, slotIndex) pair its ClusterEntityRecord carries.</summary>
    private static unsafe (int ChunkId, int Slot) ReadEntityMapLocation(DatabaseEngine dbe, ushort archetypeId, EntityId id)
    {
        var state = dbe._archetypeStates[archetypeId];
        var buffer = stackalloc byte[512];
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var accessor = state.EntityMap.Segment.CreateChunkAccessor();
        try
        {
            return state.EntityMap.TryGet(id.EntityKey, buffer, ref accessor)
                ? (ClusterEntityRecordAccessor.GetClusterChunkId(buffer), ClusterEntityRecordAccessor.GetSlotIndex(buffer))
                : (-1, -1);
        }
        finally
        {
            accessor.Dispose();
        }
    }

    [Test]
    [CancelAfter(15_000)]
    public void EntityMap_MigrationUnderTheParallelFence_InlinePath_RepointsEveryMigrantsRecord()
    {
        // The OTHER arm. RuntimeOptions.EntityMapBulkMinEntriesPerBucket picks between staging the location patches for the bulk phase and applying them
        // inline, and a batch below the threshold takes the inline path — which is what the shipped default does for every batch this size. Two paths mean
        // two tests: covering only the one the default does not take is how a fallback rots.
        RunParallelFenceMigrationAndAssertEntityMap(bulkMinEntriesPerBucket: float.MaxValue, tag: 6271, expectBulk: false);
    }

    [Test]
    [CancelAfter(15_000)]
    public void EntityMap_MigrationUnderTheParallelFence_RepointsEveryMigrantsRecord()
        => RunParallelFenceMigrationAndAssertEntityMap(bulkMinEntriesPerBucket: 0f, tag: 6270, expectBulk: true);

    /// <summary>Drives a live runtime tick that migrates half a spawn set, then asserts the EntityMap against cluster occupancy.</summary>
    private void RunParallelFenceMigrationAndAssertEntityMap(float bulkMinEntriesPerBucket, int tag, bool expectBulk)
    {
        // The step-6 gap, reproduced exactly on the EntityMap side and caught before review this time. Ablating FenceEntityMapUpdateExecSystem.DispatchItem
        // left all 5 692 tests green, because every migration fixture drives WriteTickFence — the SERIAL drain — so nothing exercised the phase that a live
        // runtime actually runs. Ablating the serial drain, by contrast, reddens
        // Migration_ThenSubsequentSpawn_ReclaimingSourceSlot_DoesNotCorruptMigratedEntity immediately.
        //
        // The assertion is the EntityMap's own record against cluster occupancy: a stale record is what makes a migrated entity resolve to the slot it left,
        // and once a later spawn reclaims that slot it resolves to an unrelated entity's bytes.
        using var dbe = SetupEngineWithGrid();
        var meta = Archetype<ClMigUnit>.Metadata;

        var ids = new EntityId[24];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < ids.Length; i++)
            {
                var cell = i / 6;
                ids[i] = tx.Spawn<ClMigUnit>(
                    ClMigUnit.Pos.Set(PointAt(50f + cell * CellSize + i % 6, 50f, tag: tag)),
                    ClMigUnit.Scratch.Set(ScratchOf(i, 0f)));
            }

            tx.Commit();
        }

        var before = new (int ChunkId, int Slot)[ids.Length];
        for (var i = 0; i < ids.Length; i++)
        {
            before[i] = ReadEntityMapLocation(dbe, meta.ArchetypeId, ids[i]);
            Assert.That(before[i].ChunkId, Is.GreaterThanOrEqualTo(0), $"sanity: entity {i} must be in the map before anything migrates");
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < ids.Length; i += 2)
            {
                ref var pos = ref tx.OpenMut(ids[i]).Write(ClMigUnit.Pos);
                pos.Bounds = new AABB2F { MinX = 550f + i, MinY = 750f, MaxX = 550f + i, MaxY = 750f };
            }

            tx.Commit();
        }

        // Sampled EVERY tick from inside the tick, not once after Shutdown. Read afterwards it carries the LAST tick's decision — taken with zero pending
        // migrations — rather than the migrating tick's, and passes only because the two sentinel thresholds happen to be migration-count-independent. Any
        // mid-range threshold would have made it assert something other than what it claims.
        var ticks = 0;
        var bulkObservations = new System.Collections.Concurrent.ConcurrentBag<bool>();
        bool[] observedPaths = [];
        using (var runtime = TyphonRuntime.Create(dbe, schedule =>
               {
                   schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Tick", _ =>
                   {
                       // From tick 2 onward. This callback is on the PUBLIC track and the fence is Engine-Post, so tick 1 samples the flag before any fence
                       // has run and reads its initial `false` — a stale value that says nothing about which path migration took.
                       if (Interlocked.Increment(ref ticks) > 1)
                       {
                           bulkObservations.Add(dbe._archetypeStates[meta.ArchetypeId].ClusterState.UseBulkEntityMapUpdate);
                       }
                   });
               }, new RuntimeOptions
               {
                   WorkerCount = 4,
                   BaseTickRate = 1000,
                   EnableParallelFence = true,

                   // FORCED to one arm. The shipped default sends a batch this small down the inline path, so without this the bulk arm would go green while
                   // exercising none of the phase it exists to cover — the exact vacuity the step-6 review caught.
                   EntityMapBulkMinEntriesPerBucket = bulkMinEntriesPerBucket,
               }))
        {
            Exception unhandled = null;
            runtime.Scheduler.UnhandledExceptionCallback = (_, _, ex) => Interlocked.CompareExchange(ref unhandled, ex, null);

            runtime.Start();
            // Both observables: `ticks` is incremented by a system INSIDE the tick, CurrentTickNumber counts ticks that FINISHED, and the assertions below
            // read both. Waiting only on the counter races the clock whenever a fence runs long (ClusterDriftParallelTests hit exactly that, "But was: 4").
            SpinWait.SpinUntil(() => Volatile.Read(ref ticks) >= 6 && runtime.CurrentTickNumber >= 6, TimeSpan.FromSeconds(5));

            // Snapshotted BEFORE Shutdown, and that is not tidiness. Disposing the engine drives a final SERIAL WriteTickFence, which picks its path from
            // EntityMapUpdateStaging.DefaultMinEntriesPerBucket rather than from RuntimeOptions — a runtime-less fence has no options object — so the flag
            // flips to the default's answer on the way down. Asserting over samples taken after that would be asserting about shutdown, not about the ticks
            // that migrated.
            observedPaths = bulkObservations.ToArray();
            runtime.Shutdown();

            // `unhandled` alone was NOT enough before #890, and believing it was is the defect this replaces. The callback fired from the tick driver and
            // the system-execute path only (DagScheduler.cs:433, :1310); the scheduler's PREPARE catch calls RecordSystemFailure instead, so anything a fence
            // phase throws while merging, partitioning or leaf-snapping — the subtlest code in the phase — never reaches this callback. Proven by injecting a
            // throw into a phase's Prepare: `unhandled` stayed null. CurrentTickNumber is the observable that does move, because a failed system aborts its
            // tick and the clock stops advancing.
            Assert.That(unhandled, Is.Null, $"the parallel fence must not throw while applying the staged EntityMap batch. Got: {unhandled}");
            Assert.That(ticks, Is.GreaterThanOrEqualTo(6), "the runtime must actually have ticked, or nothing was measured");
            Assert.That(runtime.CurrentTickNumber, Is.GreaterThanOrEqualTo(6),
                "the runtime clock must have advanced — since #890 a phase that throws in Prepare also stops it, so a stalled clock is a failed fence");
        }

        var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;

        // Which path ran, asserted rather than assumed. Both paths are correct, so without this the two tests pass whichever one the threshold selects —
        // inverting the decision in DecideEntityMapPath reddened NOTHING before this line existed, which means a future change routing every batch inline
        // would leave the bulk phase dead and the suite green.
        Assert.That(observedPaths, Is.Not.Empty, "no tick sampled the path decision, so the assertion below would prove nothing");
        Assert.That(observedPaths, Is.All.EqualTo(expectBulk),
            expectBulk
                ? "every tick of this arm must take the BULK path, or it is not covering the phase it exists for"
                : "every tick of this arm must take the INLINE path, or the fallback is untested");

        // Readable at all only because ReportOrphanedMigrant is a counter rather than a Debug.Fail: Fail terminates the host uncatchably, so in Debug — the
        // configuration this suite runs in — this assertion could never have executed.
        Assert.That(cs.OrphanedMigrantCount, Is.Zero,
            $"no migrant may go missing from the EntityMap inside the fence — that requires a mutation EW-01 forbids. "
            + $"First was key {cs.FirstOrphanedMigrantKey} at dst {cs.FirstOrphanedMigrantDst >> 8}/{cs.FirstOrphanedMigrantDst & 0xFF}.");

        var occupancy = new HashSet<int>(ActualClusterLocationsForTag(dbe, meta.ArchetypeId, tag));
        var moved = 0;
        for (var i = 0; i < ids.Length; i++)
        {
            var now = ReadEntityMapLocation(dbe, meta.ArchetypeId, ids[i]);
            Assert.That(now.ChunkId, Is.GreaterThanOrEqualTo(0), $"entity {i} vanished from the EntityMap");
            Assert.That(occupancy, Does.Contain(now.ChunkId * 64 + now.Slot),
                $"entity {i}: the EntityMap points at cluster slot {now.ChunkId}/{now.Slot}, which no entity of this tag occupies");

            if (now != before[i])
            {
                moved++;
            }
        }

        // Without this the assertion above is satisfied by a tick in which nothing migrated at all: every record would still name a slot that is occupied.
        Assert.That(moved, Is.GreaterThan(0), "no record moved, so the run proved nothing about repointing");
    }

    [Test]
    [CancelAfter(15_000)]
    public void ClusterIndex_MigrationUnderTheParallelFence_RepointsEveryMigrantsIndexValue()
    {
        // The gap this closes, stated plainly: every other migration test in this fixture calls WriteTickFence, which is the SERIAL drain. The phase #872
        // step 6 actually adds — FenceIndexMassUpdateExecSystem, its plan emission and its chunked apply — is reached only from RunParallelFence, i.e. only
        // from a live TyphonRuntime tick. Ablating FenceWorkPlan.EmitIndexUpdateSliceItems to an early return left all 54 tests of the reviewed set green,
        // which is exactly what "the deliverable has no test" looks like.
        //
        // The assertion is against cluster occupancy, not against a count: the index's value set must equal the set of slots the entities are really in.
        // A stale entry (migrant repointed nowhere) and a lost entry (migrant dropped) both fail it, and neither would move a count.
        using var dbe = SetupEngineWithGrid();
        var meta = Archetype<ClMigUnit>.Metadata;
        const int Tag = 5150;

        var ids = new EntityId[24];
        using (var tx = dbe.CreateQuickTransaction())
        {
            // Four source cells, six entities each, so the batch spans several clusters rather than one.
            for (var i = 0; i < ids.Length; i++)
            {
                var cell = i / 6;
                ids[i] = tx.Spawn<ClMigUnit>(
                    ClMigUnit.Pos.Set(PointAt(50f + cell * CellSize + i % 6, 50f, tag: Tag)),
                    ClMigUnit.Scratch.Set(ScratchOf(i, 0f)));
            }

            tx.Commit();
        }

        Assert.That(ReadIndexBufferValues(dbe, meta.ArchetypeId, Tag), Has.Count.EqualTo(ids.Length),
            "sanity: one (Tag, clusterLocation) entry per spawned entity before anything migrates");

        // Half of them cross a cell boundary — enough that a phase which silently applied nothing cannot pass by luck.
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < ids.Length; i += 2)
            {
                ref var pos = ref tx.OpenMut(ids[i]).Write(ClMigUnit.Pos);
                pos.Bounds = new AABB2F { MinX = 550f + i, MinY = 750f, MaxX = 550f + i, MaxY = 750f };
            }

            tx.Commit();
        }

        // No WriteTickFence anywhere in this test, and that is the point.
        var ticks = 0;
        using (var runtime = TyphonRuntime.Create(dbe, schedule =>
               {
                   schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Tick", _ => Interlocked.Increment(ref ticks));
               }, new RuntimeOptions { WorkerCount = 4, BaseTickRate = 1000, EnableParallelFence = true }))
        {
            Exception unhandled = null;
            runtime.Scheduler.UnhandledExceptionCallback = (_, _, ex) => Interlocked.CompareExchange(ref unhandled, ex, null);

            runtime.Start();
            // Migration executes on the fence of one tick and the emptied source clusters are drained on later ones, so give it several.
            // Both observables: `ticks` is incremented by a system INSIDE the tick, CurrentTickNumber counts ticks that FINISHED, and the assertions below
            // read both. Waiting only on the counter races the clock whenever a fence runs long (ClusterDriftParallelTests hit exactly that, "But was: 4").
            SpinWait.SpinUntil(() => Volatile.Read(ref ticks) >= 6 && runtime.CurrentTickNumber >= 6, TimeSpan.FromSeconds(5));
            runtime.Shutdown();

            // `unhandled` alone was NOT enough before #890, and believing it was is the defect this replaces. The callback fired from the tick driver and
            // the system-execute path only (DagScheduler.cs:433, :1310); the scheduler's PREPARE catch calls RecordSystemFailure instead, so anything a fence
            // phase throws while merging, partitioning or leaf-snapping — the subtlest code in the phase — never reaches this callback. Proven by injecting a
            // throw into a phase's Prepare: `unhandled` stayed null. CurrentTickNumber is the observable that does move, because a failed system aborts its
            // tick and the clock stops advancing.
            Assert.That(unhandled, Is.Null, $"the parallel fence must not throw while applying the staged index batch. Got: {unhandled}");
            Assert.That(ticks, Is.GreaterThanOrEqualTo(6), "the runtime must actually have ticked, or nothing was measured");
            Assert.That(runtime.CurrentTickNumber, Is.GreaterThanOrEqualTo(6),
                "the runtime clock must have advanced — since #890 a phase that throws in Prepare also stops it, so a stalled clock is a failed fence");
        }

        var expected = ActualClusterLocationsForTag(dbe, meta.ArchetypeId, Tag);
        var actual = ReadIndexBufferValues(dbe, meta.ArchetypeId, Tag);
        expected.Sort();
        actual.Sort();

        Assert.That(expected, Has.Count.EqualTo(ids.Length), "sanity: every entity must still be somewhere after migrating");
        Assert.That(actual, Is.EqualTo(expected),
            $"after the parallel fence the index must name exactly the cluster slots the entities occupy. "
            + $"index=[{string.Join(",", actual)}] occupancy=[{string.Join(",", expected)}]");
    }

    [Test]
    [CancelAfter(15_000)]
    public unsafe void IndexUpdateStaging_MergeSortedRuns_ProducesOneSortedRunFromMany()
    {
        // MergeSortedRuns is the phase's remaining serial step and has no other unit seam: reaching it through a tick exercises it with ONE run, because the
        // planner sizes Migrate chunks by cost and a unit-test-sized batch fits in one. Its interesting behaviour — the pairwise passes, the odd-run carry,
        // the ping-pong buffer swap — starts at run three. Driven directly here, with five runs, over the same tree the migration path uses.
        //
        // Lives in this fixture rather than IndexMassUpdatePhaseTests because ClMigPos.Tag is the AllowMultiple int index the staging path actually writes;
        // constructing a second clustered indexed archetype elsewhere would register a duplicate for no gain.
        using var dbe = SetupEngineWithGrid();
        var meta = Archetype<ClMigUnit>.Metadata;
        var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        var tree = cs.IndexSlots[0].Fields[0].Index;

        const int Runs = 5;
        const int PerRun = 7;
        var staging = new IndexUpdateStaging([new IndexUpdateStaging.FieldRef(0, 0)]);
        staging.BeginTick(Runs);

        var stride = tree.BulkEntryStride(true);
        var radixCounts = new int[RadixSort.Buckets];
        var expected = new List<(int Key, int NewValue)>();
        for (var run = 0; run < Runs; run++)
        {
            for (var i = 0; i < PerRun; i++)
            {
                // Interleaved keys so no run is a prefix of the merged result and a merge that simply concatenated would be caught.
                var key = i * Runs + run;
                var newValue = run * 1000 + i;
                tree.WriteBulkMultiEntry(staging.Reserve(run, 0, stride), &key, elementId: i, oldValue: -1, newValue: newValue);
                expected.Add((key, newValue));
            }

            // What the Migrate worker does before it leaves its chunk. MergeSortedRuns' whole contract is that its inputs arrive sorted.
            var runBytes = staging.ChunkSpan(run, 0);
            tree.SortBulkEntries(runBytes, staging.SortScratch(run, runBytes.Length), radixCounts, true);
        }

        var merged = staging.MergeSortedRuns(0, stride, tree, true, out var byteCount);
        var entries = MemoryMarshal.Cast<byte, BTreeMultiValueUpdate<int>>(merged.AsSpan(0, byteCount));

        Assert.That(entries.Length, Is.EqualTo(Runs * PerRun), "the merge must neither drop nor duplicate an entry");

        var seen = new List<(int Key, int NewValue)>();
        for (var i = 0; i < entries.Length; i++)
        {
            if (i > 0)
            {
                Assert.That(entries[i].Key, Is.GreaterThanOrEqualTo(entries[i - 1].Key),
                    $"the merged batch must be non-decreasing by key — the partitioning descent asserts sortedness and applies to the wrong leaf without "
                    + $"it. Broke at index {i}.");
            }

            seen.Add((entries[i].Key, entries[i].NewValue));
        }

        expected.Sort((a, b) => a.Key != b.Key ? a.Key.CompareTo(b.Key) : a.NewValue.CompareTo(b.NewValue));
        var seenSorted = new List<(int Key, int NewValue)>(seen);
        seenSorted.Sort((a, b) => a.Key != b.Key ? a.Key.CompareTo(b.Key) : a.NewValue.CompareTo(b.NewValue));
        Assert.That(seenSorted, Is.EqualTo(expected), "every staged entry must survive the merge unchanged");

        // Stability, which AC-6.4 leans on: entries sharing a key must stay in run order, so the merged bytes are a pure function of the runs and not of how
        // the pairwise passes happened to pair them. NewValue encodes run * 1000 + i, so within a key the run index must ascend.
        for (var i = 1; i < seen.Count; i++)
        {
            if (seen[i].Key == seen[i - 1].Key)
            {
                Assert.That(seen[i].NewValue / 1000, Is.GreaterThan(seen[i - 1].NewValue / 1000),
                    $"equal keys must keep the order their runs were gathered in; broke at index {i}");
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // #926 — the Migrate phase's zone-map batching
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Total indexed fields on the archetype — the multiplier the batch-open identity is stated against.</summary>
    private static int IndexedFieldCount(ArchetypeClusterState cs)
    {
        var slots = cs.IndexSlots;
        if (slots == null)
        {
            return 0;
        }

        var fields = 0;
        for (var i = 0; i < slots.Length; i++)
        {
            fields += slots[i].Fields.Length;
        }

        return fields;
    }

    /// <summary>Moves an entity without touching its indexed Tag — the zone-map tests assert on that tag at the destination.</summary>
    private static void MoveKeepingTag(DatabaseEngine dbe, EntityId id, float x, float y)
    {
        using var tx = dbe.CreateQuickTransaction();
        var eref = tx.OpenMut(id);
        ref var pos = ref eref.Write(ClMigUnit.Pos);
        pos.Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y };
        tx.Commit();
    }

    /// <summary>
    /// The zone map's grow latch must be taken once per indexed field per Migrate SLICE, never once per migrant.
    /// </summary>
    /// <remarks>
    /// <para><b>The assertion is an independence, not a threshold.</b> Two ticks on the same engine migrate wildly different numbers of entities through the
    /// same one-slice serial fence; the batch-open count must come out identical, because it is <c>slices x indexed fields</c> and neither term mentions the
    /// migration count. That is exactly the property that fails the moment a per-migrant acquire survives anywhere on the path — and it fails with no timing
    /// and no threshold, which is what makes it a suite test rather than a benchmark.</para>
    /// <para>Before #926 the Migrate loop called <c>ZoneMapArray.Widen</c> per migrant per field, and each call took an archetype-wide latch shared and
    /// released it — two locked read-modify-writes on ONE cache line, from every worker. Rule MD-03 forbids that shape, and #886 had already measured it on
    /// the Prep path ("that word bounced between cores ~4 000 times a tick and the zone-map step's CPU tripled").</para>
    /// </remarks>
    [Test]
    [VerifiesRule("MD-03")]
    public void ZoneMapBatchOpens_TrackTheSliceCountAndFields_NotTheMigrationCount()
    {
        using var dbe = SetupEngineWithGrid();
        var meta = Archetype<ClMigUnit>.Metadata;
        var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        var clusterSize = meta.ClusterLayout.ClusterSize;

        var ids = new List<EntityId>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < clusterSize * 2; i++)
            {
                ids.Add(tx.Spawn<ClMigUnit>(
                    ClMigUnit.Pos.Set(PointAt(50f, 50f, i)),
                    ClMigUnit.Scratch.Set(ScratchOf(i, i))));
            }

            tx.Commit();
        }

        // Sampled per tick from inside the runtime, and the tick's migration count is deliberately made to VARY: a fixed workload would let the identity
        // hold by coincidence. What must hold is that the batch count follows slices x fields on every tick while the migration count moves underneath it.
        var samples = new List<(int Batches, int Slices, long Migrations)>();
        var ticks = 0;
        var cursor = 0;
        Exception unhandled = null;

        using (var runtime = TyphonRuntime.Create(dbe, schedule =>
               {
                   schedule.PublicTrack.DeclareDag("Batch").CallbackSystem("Move", _ =>
                   {
                       var n = Interlocked.Increment(ref ticks);
                       if (n > 8)
                       {
                           return;
                       }

                       // Read BEFORE this tick's fence, so it describes the previous tick's completed one.
                       if (n > 1)
                       {
                           var t = dbe.GetSpatialTelemetry(meta.ArchetypeId);
                           lock (samples)
                           {
                               samples.Add((t.ZoneMapBatchOpens, t.MigrationSliceCount, t.MigrationCount));
                           }
                       }

                       // A different number of migrants every tick: 1, then 2, then 4, then 8...
                       var batch = 1 << (n - 1);
                       for (var k = 0; k < batch && cursor < ids.Count; k++, cursor++)
                       {
                           MoveKeepingTag(dbe, ids[cursor], 150f + 100f * (cursor % 5), 250f);
                       }
                   });
               }, new RuntimeOptions { WorkerCount = 4, BaseTickRate = 200, EnableParallelFence = true }))
        {
            runtime.Scheduler.UnhandledExceptionCallback = (_, _, ex) =>
            {
                if (ex is not ObjectDisposedException)
                {
                    Interlocked.CompareExchange(ref unhandled, ex, null);
                }
            };
            runtime.Start();
            SpinWait.SpinUntil(() => Volatile.Read(ref ticks) >= 8 && runtime.CurrentTickNumber >= 8, TimeSpan.FromSeconds(15));
            runtime.Shutdown();
        }

        var fields = IndexedFieldCount(cs);
        var mismatches = new List<string>();
        var migrating = 0;
        var distinctMigrationCounts = new HashSet<long>();
        lock (samples)
        {
            foreach (var (batches, slices, migrations) in samples)
            {
                if (batches != slices * fields)
                {
                    mismatches.Add($"batches={batches} but slices={slices} x fields={fields} = {slices * fields} (migrations={migrations})");
                }

                if (migrations > 0)
                {
                    migrating++;
                    distinctMigrationCounts.Add(migrations);
                }
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(unhandled, Is.Null, $"the parallel fence must not throw while batching zone maps. Got: {unhandled}");
            Assert.That(fields, Is.GreaterThan(0), "PRECONDITION: the archetype must have an indexed field, or there is no batch to count");
            Assert.That(migrating, Is.GreaterThan(1), "PRECONDITION: at least two sampled ticks must have migrated, or the identity is asserted against nothing");
            Assert.That(distinctMigrationCounts, Has.Count.GreaterThan(1),
                "PRECONDITION: the migration count must actually VARY across ticks, or independence from it is untested");
            Assert.That(mismatches, Is.Empty, "one batch per indexed field per slice, whatever the slice moved");
        });
    }

    /// <summary>
    /// The serial fence opens no zone-map batches at all, and that is a correctness property rather than a tuning one.
    /// </summary>
    /// <remarks>
    /// Batching exists to stop W workers taking one archetype-wide latch per migrant; a serial fence has one writer and nothing to amortise. It also cannot
    /// safely hold one: a destination past the pinned generation has to fall back to the growing <c>Widen</c>, and growing while holding the batch's own
    /// shared count waits for that count to drain to zero — from the thread that holds it, under an unbounded wait. Opening no batch is what keeps the serial
    /// fallback available, so a non-zero count here would mean the deadlock has been reintroduced.
    /// </remarks>
    [Test]
    [VerifiesRule("MD-03")]
    public void ZoneMapBatchOpens_AreZeroOnTheSerialFence_WhichMustKeepItsGrowingFallback()
    {
        using var dbe = SetupEngineWithGrid();
        var meta = Archetype<ClMigUnit>.Metadata;

        var ids = new List<EntityId>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 20; i++)
            {
                ids.Add(tx.Spawn<ClMigUnit>(
                    ClMigUnit.Pos.Set(PointAt(50f, 50f, i)),
                    ClMigUnit.Scratch.Set(ScratchOf(i, i))));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);
        for (var i = 0; i < ids.Count; i++)
        {
            MoveKeepingTag(dbe, ids[i], 250f, 50f);
        }

        dbe.WriteTickFence(2);
        var telemetry = dbe.GetSpatialTelemetry(meta.ArchetypeId);

        Assert.Multiple(() =>
        {
            Assert.That(telemetry.MigrationCount, Is.GreaterThan(0), "PRECONDITION: the serial fence really did migrate, or zero below is trivially true");
            Assert.That(telemetry.ZoneMapBatchOpens, Is.Zero, "the serial fence must not batch — its growing fallback depends on holding no shared count");
        });
    }

    /// <summary>
    /// A migrant landing in a cluster the Migrate phase itself allocated must still be recorded in that cluster's zone map.
    /// </summary>
    /// <remarks>
    /// <para><b>This is the risk the batching introduces, so it is the thing to pin.</b> A batch pins one store generation for its whole slice, and the
    /// destination clusters a slice allocates have chunk ids that did not exist when the batch opened. If the pre-size does not cover them the widen is
    /// refused and the bound is never recorded — and a MISSING bound is not a wrong answer some other test would trip over, it is a silently narrowed one:
    /// <c>MayContain</c> answers conservatively for an unrecorded cluster, so queries stay correct and only the pruning is lost.</para>
    /// <para><c>TryGetBounds</c> is therefore the right instrument and <c>MayContain</c> is not: it is the only way to tell a recorded bound from an absent
    /// one, which is exactly the distinction a lost widen turns on.</para>
    /// </remarks>
    [Test]
    [VerifiesRule("MD-03")]
    public void MigrantsLandingInFreshlyAllocatedClusters_AreStillRecordedInTheZoneMap()
    {
        using var dbe = SetupEngineWithGrid();
        var meta = Archetype<ClMigUnit>.Metadata;
        var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        var clusterSize = meta.ClusterLayout.ClusterSize;

        // Several clusters' worth in one cell, so moving them into an EMPTY cell has to allocate fresh destination clusters rather than fill existing ones.
        var ids = new List<EntityId>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < clusterSize * 3; i++)
            {
                ids.Add(tx.Spawn<ClMigUnit>(
                    ClMigUnit.Pos.Set(PointAt(50f, 50f, 1000 + i)),
                    ClMigUnit.Scratch.Set(ScratchOf(i, i))));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);
        var clustersBefore = cs.ActiveClusterCount;

        // HALF of them, not all. Moving the whole cell frees every source cluster as fast as the destination allocates, so the active count nets out
        // unchanged and the test would be asserting against a scenario that allocated nothing. Leaving the sources occupied forces the destination cell to
        // allocate clusters that did not exist when the slice opened its batch, which is the case AC-3 is about.
        var moved = ids.Count / 2;
        for (var i = 0; i < moved; i++)
        {
            MoveKeepingTag(dbe, ids[i], 550f, 550f);
        }

        dbe.WriteTickFence(2);

        var zoneMap = cs.IndexSlots[0].Fields[0].ZoneMap;
        Assert.That(zoneMap, Is.Not.Null, "PRECONDITION: the indexed Tag field must have a zone map");

        var missing = new List<string>();
        for (var i = 0; i < moved; i++)
        {
            var (chunkId, _) = ReadLocation(dbe, ids[i]);
            long tag = 1000 + i;
            if (!zoneMap.TryGetBounds(chunkId, out var min, out var max))
            {
                missing.Add($"entity {i} in cluster {chunkId}: no bounds recorded at all");
            }
            else if (tag < min || tag > max)
            {
                missing.Add($"entity {i} in cluster {chunkId}: tag {tag} outside recorded [{min}, {max}]");
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(cs.ActiveClusterCount, Is.GreaterThan(clustersBefore),
                "PRECONDITION: the migration must actually have allocated fresh destination clusters, or nothing new was covered");
            Assert.That(missing, Is.Empty, "every migrant's value must lie inside its destination cluster's recorded zone map");
        });
    }
}
