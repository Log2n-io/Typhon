using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
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
    [SpatialIndex(1.0f)]
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

[Archetype(842)]
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
        Archetype<ClMigUnit>.Touch();
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
        dbe.ConfigureSpatialGrid(new SpatialGridConfig(
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
                        int slot = System.Numerics.BitOperations.TrailingZeroCount(occupancy);
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

        int srcCell = dbe.SpatialGrid.WorldToCellKey(50f, 50f);
        int dstCell = dbe.SpatialGrid.WorldToCellKey(150f, 250f);
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
        int srcCell = dbe.SpatialGrid.WorldToCellKey(50f, 50f);
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

        int dstCell = dbe.SpatialGrid.WorldToCellKey(50f, 50f);

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

        int srcCell = dbe.SpatialGrid.WorldToCellKey(95f, 50f);

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

        int dstCell = dbe.SpatialGrid.WorldToCellKey(110f, 50f);
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

        int srcCell = dbe.SpatialGrid.WorldToCellKey(50f, 50f);
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
            int dst = dbe.SpatialGrid.WorldToCellKey(x, y);
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

        int srcCell = dbe.SpatialGrid.WorldToCellKey(50f, 50f);
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

        int srcCell = dbe.SpatialGrid.WorldToCellKey(50f, 50f);
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

        int srcCell = dbe.SpatialGrid.WorldToCellKey(50f, 50f);

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
        var dbName = $"T_ClMigReopen_{Environment.ProcessId}";

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
            srcCellKey = dbe.SpatialGrid.WorldToCellKey(50f, 50f);

            using (var tx = dbe.CreateQuickTransaction())
            {
                var eref = tx.OpenMut(id);
                ref var pos = ref eref.Write(ClMigUnit.Pos);
                pos.Bounds = new AABB2F { MinX = 550f, MinY = 750f, MaxX = 550f, MaxY = 750f };
                tx.Commit();
            }
            dstCellKey = dbe.SpatialGrid.WorldToCellKey(550f, 750f);

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
            // Source cell is empty; destination cell has 1 entity in exactly one cluster.
            Assert.That(dbe.SpatialGrid.GetCell(srcCellKey).EntityCount, Is.EqualTo(0),
                "source cell must remain empty after reopen — migration was persisted");
            Assert.That(dbe.SpatialGrid.GetCell(dstCellKey).EntityCount, Is.EqualTo(1),
                "destination cell must be reconstructed with the migrated entity");
            Assert.That(dbe.SpatialGrid.GetCell(dstCellKey).ClusterCount, Is.EqualTo(1));
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

        int srcCell = dbe.SpatialGrid.WorldToCellKey(50f, 50f);
        int dstCell = dbe.SpatialGrid.WorldToCellKey(550f, 750f);

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
        int dstCell = dbe.SpatialGrid.WorldToCellKey(450f, 550f);
        Assert.That(cs.ClusterCellMap[postChunk], Is.EqualTo(dstCell));
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
        dbe.ConfigureSpatialGrid(new SpatialGridConfig(
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

        int srcCell = dbe.SpatialGrid.WorldToCellKey(50f, 50f);
        int dstCell = dbe.SpatialGrid.WorldToCellKey(250f, 250f);
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
}
