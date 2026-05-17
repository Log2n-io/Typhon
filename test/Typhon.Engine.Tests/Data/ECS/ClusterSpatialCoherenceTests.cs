using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ═══════════════════════════════════════════════════════════════════════════════
// Test-only components for spatial coherence tests (issue #229 Phase 1+2).
// Uses AABB2F (the only field type supported by the Phase 1+2 spatial grid).
// ═══════════════════════════════════════════════════════════════════════════════

[Component("Typhon.Test.ClCoh.Pos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct ClCohPos
{
    [Field]
    [SpatialIndex(1.0f)]
    public AABB2F Bounds;

    [Field]
    public float Mass;
}

[Archetype(840)]
partial class ClCohUnit : Archetype<ClCohUnit>
{
    public static readonly Comp<ClCohPos> Pos = Register<ClCohPos>();
}

// Secondary spatial archetype used only by the multi-archetype guard test.
[Component("Typhon.Test.ClCoh.Pos2", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct ClCohPos2
{
    [Field]
    [SpatialIndex(1.0f)]
    public AABB2F Bounds;
}

[Archetype(841)]
partial class ClCohUnit2 : Archetype<ClCohUnit2>
{
    public static readonly Comp<ClCohPos2> Pos = Register<ClCohPos2>();
}

[TestFixture]
[NonParallelizable]
class ClusterSpatialCoherenceTests : TestBase<ClusterSpatialCoherenceTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        Archetype<ClCohUnit>.Touch();
        Archetype<ClCohUnit2>.Touch();
    }

    private static ClCohPos PointAt(float x, float y) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Mass = 1.0f };

    private DatabaseEngine SetupEngineWithGrid(float cellSize = 100f, float worldMax = 1000f)
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClCohPos>();
        dbe.ConfigureSpatialGrid(new SpatialGridConfig(
            worldMin: new Vector2(0, 0),
            worldMax: new Vector2(worldMax, worldMax),
            cellSize: cellSize));
        dbe.InitializeArchetypes();
        return dbe;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Grid configuration + opt-in behaviour
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void ConfigureSpatialGrid_AfterInitializeArchetypes_Throws()
    {
        using var dbe = SetupEngineWithGrid();
        Assert.Throws<System.InvalidOperationException>(() =>
            dbe.ConfigureSpatialGrid(new SpatialGridConfig(new Vector2(0, 0), new Vector2(500, 500), 50f)));
    }

    [Test]
    public void SpatialArchetype_WithoutGridConfig_Throws()
    {
        // Issue #230 Phase 3 Option B: ConfigureSpatialGrid() is required for cluster spatial archetypes. The pre-Option-B legacy fallback is gone —
        // InitializeArchetypes throws an InvalidOperationException naming the archetype.
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClCohPos>();
        try
        {
            Assert.Throws<System.InvalidOperationException>(() => dbe.InitializeArchetypes());
        }
        finally
        {
            dbe.Dispose();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Spawn placement — same cell, different cells, overflow
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Spawn_ManyEntitiesInSameCell_LandInSameCluster()
    {
        using var dbe = SetupEngineWithGrid();
        var meta = Archetype<ClCohUnit>.Metadata;

        using (var tx = dbe.CreateQuickTransaction())
        {
            // All positions inside cell (1, 2) — world (100..200, 200..300)
            for (int i = 0; i < 5; i++)
            {
                tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(150f + i, 250f)));
            }
            tx.Commit();
        }

        var clusterState = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        // All 5 entities landed in a single cluster (cluster size is much larger than 5)
        Assert.That(clusterState.ActiveClusterCount, Is.EqualTo(1));

        // That cluster is attached to exactly one cell (cell (1, 2))
        int expectedCellKey = dbe.SpatialGrid.WorldToCellKey(150f, 250f);
        ref var cell = ref dbe.SpatialGrid.GetCell(expectedCellKey);
        Assert.That(cell.EntityCount, Is.EqualTo(5));
        Assert.That(cell.ClusterCount, Is.EqualTo(1));

        // And the cluster-cell map agrees
        int chunkId = clusterState.ActiveClusterIds[0];
        Assert.That(clusterState.ClusterCellMap[chunkId], Is.EqualTo(expectedCellKey));
    }

    [Test]
    public void Spawn_InDifferentCells_LandInDifferentClusters()
    {
        using var dbe = SetupEngineWithGrid();
        var meta = Archetype<ClCohUnit>.Metadata;

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(50f, 50f)));      // cell (0, 0)
            tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(550f, 350f)));    // cell (5, 3)
            tx.Commit();
        }

        // Two clusters, each in its own cell with one entity.
        var clusterState = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        Assert.That(clusterState.ActiveClusterCount, Is.EqualTo(2));

        int cellA = dbe.SpatialGrid.WorldToCellKey(50f, 50f);
        int cellB = dbe.SpatialGrid.WorldToCellKey(550f, 350f);
        Assert.That(cellA, Is.Not.EqualTo(cellB));
        Assert.That(dbe.SpatialGrid.GetCell(cellA).ClusterCount, Is.EqualTo(1));
        Assert.That(dbe.SpatialGrid.GetCell(cellA).EntityCount, Is.EqualTo(1));
        Assert.That(dbe.SpatialGrid.GetCell(cellB).ClusterCount, Is.EqualTo(1));
        Assert.That(dbe.SpatialGrid.GetCell(cellB).EntityCount, Is.EqualTo(1));

        // Active clusters belong to exactly these two cells (order undefined)
        int c0 = clusterState.ActiveClusterIds[0];
        int c1 = clusterState.ActiveClusterIds[1];
        var mapped = new System.Collections.Generic.HashSet<int>
        {
            clusterState.ClusterCellMap[c0],
            clusterState.ClusterCellMap[c1],
        };
        Assert.That(mapped, Is.EquivalentTo(new[] { cellA, cellB }));
    }

    [Test]
    public void Spawn_BeyondClusterCapacity_AllocatesSecondClusterInSameCell()
    {
        using var dbe = SetupEngineWithGrid();
        var meta = Archetype<ClCohUnit>.Metadata;
        int clusterSize = meta.ClusterLayout.ClusterSize;

        using (var tx = dbe.CreateQuickTransaction())
        {
            // Spawn enough entities to overflow one cluster inside a single cell (50, 50)
            for (int i = 0; i < clusterSize + 3; i++)
            {
                tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(50f, 50f)));
            }
            tx.Commit();
        }

        var clusterState = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        Assert.That(clusterState.ActiveClusterCount, Is.EqualTo(2),
            "overflowing one cluster should allocate a second one in the same cell");

        int cellKey = dbe.SpatialGrid.WorldToCellKey(50f, 50f);
        ref var cell = ref dbe.SpatialGrid.GetCell(cellKey);
        Assert.That(cell.ClusterCount, Is.EqualTo(2));
        Assert.That(cell.EntityCount, Is.EqualTo(clusterSize + 3));

        // Both clusters are mapped to the same cell
        int c0 = clusterState.ActiveClusterIds[0];
        int c1 = clusterState.ActiveClusterIds[1];
        Assert.That(clusterState.ClusterCellMap[c0], Is.EqualTo(cellKey));
        Assert.That(clusterState.ClusterCellMap[c1], Is.EqualTo(cellKey));
    }

    [Test]
    public void Spawn_OverflowsManyClusters_ScanCursorAdvancesPastFullPrefix()
    {
        // Regression guard for the ClaimSlotInCell per-cell scan cursor. Filling K clusters in one cell must leave the cursor at the last cluster's logical
        // index — i.e. the scan advanced past the full prefix instead of restarting at 0 on every claim (the old O(M²) behaviour).
        using var dbe = SetupEngineWithGrid();
        var meta = Archetype<ClCohUnit>.Metadata;
        int clusterSize = meta.ClusterLayout.ClusterSize;

        // Fill exactly 3 clusters and start a 4th — all inside cell (50, 50).
        int spawnCount = clusterSize * 3 + 1;
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < spawnCount; i++)
            {
                tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(50f, 50f)));
            }
            tx.Commit();
        }

        var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        int cellKey = dbe.SpatialGrid.WorldToCellKey(50f, 50f);

        Assert.That(cs.ActiveClusterCount, Is.EqualTo(4), "3 full clusters + 1 partial");
        Assert.That(cs.CellClusterPool.GetScanCursor(cellKey), Is.EqualTo(3),
            "cursor must point at the last (only non-full) cluster — proves the scan does not restart at 0");
    }

    [Test]
    public void Destroy_ResetsScanCursor_SoFreedSlotIsReusable()
    {
        // A release must reset the cell's scan cursor to 0 — otherwise a slot freed ahead of the cursor would be permanently skipped.
        using var dbe = SetupEngineWithGrid();
        var meta = Archetype<ClCohUnit>.Metadata;
        int clusterSize = meta.ClusterLayout.ClusterSize;

        EntityId toKill = default;
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < clusterSize * 2 + 1; i++)
            {
                var id = tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(50f, 50f)));
                if (i == 0) { toKill = id; } // an entity in the first (full) cluster
            }
            tx.Commit();
        }

        var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        int cellKey = dbe.SpatialGrid.WorldToCellKey(50f, 50f);
        Assert.That(cs.CellClusterPool.GetScanCursor(cellKey), Is.GreaterThan(0), "cursor advanced during spawn");

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(toKill);
            tx.Commit();
        }

        Assert.That(cs.CellClusterPool.GetScanCursor(cellKey), Is.EqualTo(0),
            "releasing a slot must reset the cursor so the freed slot can be reused");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Destroy — cell state maintenance
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Destroy_LastEntityInCluster_RemovesClusterFromCell()
    {
        using var dbe = SetupEngineWithGrid();
        var meta = Archetype<ClCohUnit>.Metadata;

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(150f, 250f)));
            tx.Commit();
        }

        int cellKey = dbe.SpatialGrid.WorldToCellKey(150f, 250f);
        Assert.That(dbe.SpatialGrid.GetCell(cellKey).ClusterCount, Is.EqualTo(1));
        Assert.That(dbe.SpatialGrid.GetCell(cellKey).EntityCount, Is.EqualTo(1));

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(id);
            tx.Commit();
        }

        ref var cellAfter = ref dbe.SpatialGrid.GetCell(cellKey);
        Assert.That(cellAfter.ClusterCount, Is.EqualTo(0), "empty cluster must detach from its cell");
        Assert.That(cellAfter.EntityCount, Is.EqualTo(0));
    }

    [Test]
    public void Destroy_OneOfManyEntities_DecrementsCellCount_KeepsCluster()
    {
        using var dbe = SetupEngineWithGrid();
        var meta = Archetype<ClCohUnit>.Metadata;

        EntityId toKill;
        using (var tx = dbe.CreateQuickTransaction())
        {
            toKill = tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(50f, 50f)));
            for (int i = 0; i < 4; i++)
            {
                tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(50f + i, 50f)));
            }
            tx.Commit();
        }

        int cellKey = dbe.SpatialGrid.WorldToCellKey(50f, 50f);
        Assert.That(dbe.SpatialGrid.GetCell(cellKey).EntityCount, Is.EqualTo(5));

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(toKill);
            tx.Commit();
        }

        ref var cellAfter = ref dbe.SpatialGrid.GetCell(cellKey);
        Assert.That(cellAfter.EntityCount, Is.EqualTo(4));
        Assert.That(cellAfter.ClusterCount, Is.EqualTo(1), "cluster still contains other entities, must stay");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Validation of unsupported spatial field types
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void ValidateSupportedFieldType_F32Variants_Accepted()
    {
        // Issue #230 Phase 3 extended support from 2D-only to all f32 tiers (2D and 3D). Cells are still 2D (XY) — 3D archetypes bucket into cells by their
        // XY center and get Z filtering at the query narrowphase.
        SpatialGrid.ValidateSupportedFieldType(SpatialFieldType.AABB2F, "MyArch");
        SpatialGrid.ValidateSupportedFieldType(SpatialFieldType.BSphere2F, "MyArch");
        SpatialGrid.ValidateSupportedFieldType(SpatialFieldType.AABB3F, "MyArch");
        SpatialGrid.ValidateSupportedFieldType(SpatialFieldType.BSphere3F, "MyArch");
    }

    [Test]
    public void ValidateSupportedFieldType_F64Variants_Throw()
    {
        // f64 spatial tiers are still deferred to a follow-up sub-issue of #228.
        Assert.Throws<System.NotSupportedException>(
            () => SpatialGrid.ValidateSupportedFieldType(SpatialFieldType.AABB2D, "MyArch"));
        Assert.Throws<System.NotSupportedException>(
            () => SpatialGrid.ValidateSupportedFieldType(SpatialFieldType.AABB3D, "MyArch"));
        Assert.Throws<System.NotSupportedException>(
            () => SpatialGrid.ValidateSupportedFieldType(SpatialFieldType.BSphere2D, "MyArch"));
        Assert.Throws<System.NotSupportedException>(
            () => SpatialGrid.ValidateSupportedFieldType(SpatialFieldType.BSphere3D, "MyArch"));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Reopen — RebuildCellState reconstructs the mapping from persisted data
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Reopen_RebuildsClusterCellMap_FromPersistedData()
    {
        var dbName = $"T_ClusterCellRebuild_{Environment.ProcessId}";

        int cellKey1, cellKey2;
        int chunkId1, chunkId2;

        // Session 1: spawn entities in two distinct cells, note the cluster→cell mapping
        {
            using var dbe = CreateNamedEngineWithGrid(dbName);
            using (var tx = dbe.CreateQuickTransaction())
            {
                tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(150f, 250f))); // cell (1, 2)
                tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(550f, 750f))); // cell (5, 7)
                tx.Commit();
            }

            cellKey1 = dbe.SpatialGrid.WorldToCellKey(150f, 250f);
            cellKey2 = dbe.SpatialGrid.WorldToCellKey(550f, 750f);

            var meta = Archetype<ClCohUnit>.Metadata;
            var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
            Assert.That(cs.ActiveClusterCount, Is.EqualTo(2));

            // Record which cluster chunk IDs correspond to which cell
            chunkId1 = chunkId2 = 0;
            for (int i = 0; i < cs.ActiveClusterCount; i++)
            {
                int id = cs.ActiveClusterIds[i];
                if (cs.ClusterCellMap[id] == cellKey1) { chunkId1 = id; }
                else if (cs.ClusterCellMap[id] == cellKey2) { chunkId2 = id; }
            }
            Assert.That(chunkId1, Is.GreaterThan(0));
            Assert.That(chunkId2, Is.GreaterThan(0));
        }

        // Session 2: reopen, verify cell state reconstructed by RebuildCellState
        {
            using var dbe = CreateNamedEngineWithGrid(dbName);
            var meta = Archetype<ClCohUnit>.Metadata;
            var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;

            Assert.That(cs.ActiveClusterCount, Is.EqualTo(2), "active clusters preserved after reopen");
            Assert.That(cs.ClusterCellMap, Is.Not.Null, "ClusterCellMap rebuilt on reopen");
            Assert.That(cs.ClusterCellMap[chunkId1], Is.EqualTo(cellKey1),
                "cluster 1 must re-attach to its original cell");
            Assert.That(cs.ClusterCellMap[chunkId2], Is.EqualTo(cellKey2),
                "cluster 2 must re-attach to its original cell");

            // Each cell has one cluster with one entity after rebuild
            Assert.That(dbe.SpatialGrid.GetCell(cellKey1).ClusterCount, Is.EqualTo(1));
            Assert.That(dbe.SpatialGrid.GetCell(cellKey1).EntityCount, Is.EqualTo(1));
            Assert.That(dbe.SpatialGrid.GetCell(cellKey2).ClusterCount, Is.EqualTo(1));
            Assert.That(dbe.SpatialGrid.GetCell(cellKey2).EntityCount, Is.EqualTo(1));
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Guard tests for code-review fixes
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void TwoSpatialArchetypes_ShareGrid_SucceedsAndIsolatesQueries()
    {
        // Issue #229 Q10 resolution: two cluster-spatial archetypes can share a single SpatialGrid because each archetype owns its own per-cell
        // CellClusterPool. This test exercises the full stack — registration, spawn into the same cell from two different archetypes, query isolation,
        // and the global CellState.EntityCount / ClusterCount aggregation that tests have always asserted on.
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClCohPos>();
        dbe.RegisterComponentFromAccessor<ClCohPos2>();
        dbe.ConfigureSpatialGrid(new SpatialGridConfig(
            worldMin: new Vector2(0, 0),
            worldMax: new Vector2(1000, 1000),
            cellSize: 100f));
        dbe.InitializeArchetypes(); // No throw — the Q10 gate has been removed.

        // Spawn one entity of each archetype into the SAME cell (150, 250 → cell (1, 2)).
        EntityId id1, id2;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id1 = tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(150f, 250f)));
            var pos2 = new ClCohPos2 { Bounds = new AABB2F { MinX = 155f, MinY = 255f, MaxX = 155f, MaxY = 255f } };
            id2 = tx.Spawn<ClCohUnit2>(ClCohUnit2.Pos.Set(in pos2));
            tx.Commit();
        }

        int cellKey = dbe.SpatialGrid.WorldToCellKey(150f, 250f);
        ref var cell = ref dbe.SpatialGrid.GetCell(cellKey);

        // Global aggregation: both entities live in the same cell, cluster count is the sum across archetypes.
        Assert.That(cell.EntityCount, Is.EqualTo(2), "CellState.EntityCount is the sum across archetypes");
        Assert.That(cell.ClusterCount, Is.EqualTo(2), "Two cluster-spatial archetypes each allocated one cluster in this cell");

        // Per-archetype pool isolation: each archetype sees only its own cluster id in the cell.
        var meta1 = Archetype<ClCohUnit>.Metadata;
        var meta2 = Archetype<ClCohUnit2>.Metadata;
        var cs1 = dbe._archetypeStates[meta1.ArchetypeId].ClusterState;
        var cs2 = dbe._archetypeStates[meta2.ArchetypeId].ClusterState;
        Assert.That(cs1.CellClusterPool.GetClusterCount(cellKey), Is.EqualTo(1), "Archetype 1 pool sees its own cluster");
        Assert.That(cs2.CellClusterPool.GetClusterCount(cellKey), Is.EqualTo(1), "Archetype 2 pool sees its own cluster");

        // Query isolation: querying each archetype returns only its own entity. WhereInAABB's 6-arg signature packs 2D bounds as (minX, minY, maxX, maxY, _, _)
        // per the existing EcsQuery cluster 2D dispatch (CoordCount==4 path reads maxX from _spatialParams[2] and maxY from _spatialParams[3]).
        using (var tx = dbe.CreateQuickTransaction())
        {
            var r1 = tx.Query<ClCohUnit>().WhereInAABB<ClCohPos>(0, 0, 300, 300, 0, 0).Execute();
            Assert.That(r1, Does.Contain(id1));
            Assert.That(r1, Does.Not.Contain(id2));
        }
        using (var tx = dbe.CreateQuickTransaction())
        {
            var r2 = tx.Query<ClCohUnit2>().WhereInAABB<ClCohPos2>(0, 0, 300, 300, 0, 0).Execute();
            Assert.That(r2, Does.Contain(id2));
            Assert.That(r2, Does.Not.Contain(id1));
        }

        // Destroy one entity and verify per-archetype bookkeeping converges independently.
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(id1);
            tx.Commit();
        }

        ref var cellAfter = ref dbe.SpatialGrid.GetCell(cellKey);
        Assert.That(cellAfter.EntityCount, Is.EqualTo(1), "Destroying one entity decrements the global count");
        Assert.That(cellAfter.ClusterCount, Is.EqualTo(1), "Archetype 1's cluster emptied and detached; archetype 2's cluster remains");
        Assert.That(cs1.CellClusterPool.GetClusterCount(cellKey), Is.EqualTo(0), "Archetype 1 pool is now empty for this cell");
        Assert.That(cs2.CellClusterPool.GetClusterCount(cellKey), Is.EqualTo(1), "Archetype 2 pool is untouched by the unrelated destroy");
    }

    [Test]
    public unsafe void ReleaseSlot_OnAlreadyEmptySlot_DoesNotUnderflowEntityCount()
    {
        // Spawn one entity, then directly call ReleaseSlot on a slot that was never occupied.
        // The wasOccupied guard (Fix #3) should leave cell.EntityCount untouched.
        using var dbe = SetupEngineWithGrid();
        var meta = Archetype<ClCohUnit>.Metadata;

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(150f, 250f)));
            tx.Commit();
        }

        int cellKey = dbe.SpatialGrid.WorldToCellKey(150f, 250f);
        Assert.That(dbe.SpatialGrid.GetCell(cellKey).EntityCount, Is.EqualTo(1));

        var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        int chunkId = cs.ActiveClusterIds[0];

        // Pick a slot that was never occupied. The spawned entity is in slot 0; slot 5 is empty.
        using (var epoch = EpochGuard.Enter(dbe.EpochManager))
        {
            var changeSet = dbe.MMF.CreateChangeSet();
            var accessor = cs.ClusterSegment.CreateChunkAccessor(changeSet);
            try
            {
                cs.ReleaseSlot(ref accessor, chunkId, slotIndex: 5, changeSet, dbe.SpatialGrid);
            }
            finally
            {
                accessor.Dispose();
                changeSet.SaveChanges();
            }
        }

        Assert.That(dbe.SpatialGrid.GetCell(cellKey).EntityCount, Is.EqualTo(1),
            "releasing a never-occupied slot must not decrement EntityCount");
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
          .AddScopedDatabaseEngine(o => { o.Wal = null; });

        var sp = sc.BuildServiceProvider();
        var dbe = sp.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClCohPos>();
        dbe.ConfigureSpatialGrid(new SpatialGridConfig(
            worldMin: new Vector2(0, 0),
            worldMax: new Vector2(1000, 1000),
            cellSize: 100f));
        dbe.InitializeArchetypes();
        return dbe;
    }
}
