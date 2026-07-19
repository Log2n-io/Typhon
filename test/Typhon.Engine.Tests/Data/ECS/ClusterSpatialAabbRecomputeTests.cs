using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

[TestFixture]
[NonParallelizable]
class ClusterSpatialAabbRecomputeTests : TestBase<ClusterSpatialAabbRecomputeTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
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

    private static ArchetypeClusterState GetClusterState(DatabaseEngine dbe)
    {
        var meta = Archetype<ClCohUnit>.Metadata;
        return dbe._archetypeStates[meta.ArchetypeId].ClusterState;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // RebuildClusterAabbs
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void RebuildClusterAabbs_FreshEngineWithNoEntities_IsNoOp()
    {
        using var dbe = SetupEngineWithGrid();
        var cs = GetClusterState(dbe);

        using (var epoch = EpochGuard.Enter(dbe.EpochManager))
        {
            cs.RebuildClusterAabbs();
        }

        // No clusters → no AABBs allocated.
        Assert.That(cs.ActiveClusterCount, Is.EqualTo(0));
        // ClusterAabbs may still be null (early-return path).
    }

    [Test]
    public void RebuildClusterAabbs_SingleEntity_ComputesTightAabbMatchingEntityBounds()
    {
        using var dbe = SetupEngineWithGrid();

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(50f, 50f)));
            tx.Commit();
        }

        var cs = GetClusterState(dbe);

        // Simulate a fresh startup: call RebuildClusterAabbs explicitly. The production path calls it
        // inside InitializeArchetypes at reopen time; calling it again here is idempotent for a single
        // entity (it re-derives the same AABB from the same position).
        using (var epoch = EpochGuard.Enter(dbe.EpochManager))
        {
            cs.RebuildClusterAabbs();
        }

        Assert.That(cs.ClusterAabbs, Is.Not.Null, "ClusterAabbs should be allocated after rebuild");

        int chunkId = cs.ActiveClusterIds[0];
        ClusterSpatialAabb aabb = cs.ClusterAabbs[chunkId];
        Assert.That(aabb.MinX, Is.EqualTo(50f));
        Assert.That(aabb.MinY, Is.EqualTo(50f));
        Assert.That(aabb.MaxX, Is.EqualTo(50f));
        Assert.That(aabb.MaxY, Is.EqualTo(50f));
        Assert.That(aabb.CategoryMask, Is.EqualTo(uint.MaxValue),
            "Phase 1 default category mask is uint.MaxValue (every query matches)");
    }

    [Test]
    public void RebuildClusterAabbs_MultipleEntitiesInSameCluster_UnionsAllBounds()
    {
        using var dbe = SetupEngineWithGrid();

        // Spawn 5 entities in the same cell → all go to the same cluster.
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(10f, 20f)));
            tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(30f, 40f)));
            tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(50f, 15f)));
            tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(25f, 70f)));
            tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(45f, 55f)));
            tx.Commit();
        }

        var cs = GetClusterState(dbe);
        using (var epoch = EpochGuard.Enter(dbe.EpochManager))
        {
            cs.RebuildClusterAabbs();
        }

        Assert.That(cs.ActiveClusterCount, Is.EqualTo(1),
            "all 5 entities should be in the same cluster (same cell)");
        int chunkId = cs.ActiveClusterIds[0];
        ClusterSpatialAabb aabb = cs.ClusterAabbs[chunkId];

        // Tight union of the 5 points:
        //   x: 10..50, y: 15..70
        Assert.That(aabb.MinX, Is.EqualTo(10f));
        Assert.That(aabb.MinY, Is.EqualTo(15f));
        Assert.That(aabb.MaxX, Is.EqualTo(50f));
        Assert.That(aabb.MaxY, Is.EqualTo(70f));
    }

    [Test]
    public void RebuildClusterAabbs_EntitiesInMultipleCells_AllocatesPerCellIndex()
    {
        using var dbe = SetupEngineWithGrid(cellSize: 100f);

        // 3 cells: (0,0), (1,0), (0,1). Each gets its own cluster.
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(50f, 50f)));   // cell (0,0)
            tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(150f, 50f)));  // cell (1,0)
            tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(50f, 150f)));  // cell (0,1)
            tx.Commit();
        }

        var cs = GetClusterState(dbe);
        using (var epoch = EpochGuard.Enter(dbe.EpochManager))
        {
            cs.RebuildClusterAabbs();
        }

        Assert.That(cs.ActiveClusterCount, Is.EqualTo(3),
            "three distinct cells → three clusters");
        Assert.That(cs.PerCellIndex, Is.Not.Null);

        int nonNullSlots = 0;
        int totalClustersInIndex = 0;
        for (int i = 0; i < cs.PerCellIndex.Length; i++)
        {
            if (cs.PerCellIndex[i] != null && cs.PerCellIndex[i].DynamicIndex != null)
            {
                nonNullSlots++;
                totalClustersInIndex += cs.PerCellIndex[i].DynamicIndex.ClusterCount;
            }
        }
        Assert.That(nonNullSlots, Is.EqualTo(3), "three cells should have per-cell spatial slots");
        Assert.That(totalClustersInIndex, Is.EqualTo(3), "each cell holds exactly one cluster");
    }

    [Test]
    public void RebuildClusterAabbs_BackPointersConsistentWithPerCellIndexSlots()
    {
        using var dbe = SetupEngineWithGrid();

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(50f, 50f)));   // cell (0,0)
            tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(150f, 150f))); // cell (1,1)
            tx.Commit();
        }

        var cs = GetClusterState(dbe);
        using (var epoch = EpochGuard.Enter(dbe.EpochManager))
        {
            cs.RebuildClusterAabbs();
        }

        // For each active cluster, verify the back-pointer correctly locates it in its cell's DynamicIndex.
        for (int i = 0; i < cs.ActiveClusterCount; i++)
        {
            int chunkId = cs.ActiveClusterIds[i];
            int backPointer = cs.ClusterSpatialIndexSlot[chunkId];
            Assert.That(backPointer, Is.GreaterThanOrEqualTo(0),
                $"cluster {chunkId} must have a valid back-pointer into its cell's DynamicIndex");

            int cellKey = cs.ClusterCellMap[chunkId];
            var dynamicIndex = cs.PerCellIndex[cellKey].DynamicIndex;
            Assert.That(dynamicIndex.ClusterIds[backPointer], Is.EqualTo(chunkId),
                $"back-pointer must resolve to the same cluster id " +
                $"(chunkId={chunkId}, backPointer={backPointer})");
        }
    }

    // Note: RebuildClusterAabbs_WithoutGridOptIn_IsNoOp was removed in issue #230 Phase 3 Option B. It asserted that without ConfigureSpatialGrid(), the
    // per-cell index stays null — but Option B's grid-required gate in InitializeArchetypes now throws for any cluster spatial archetype without a grid,
    // so the test's precondition is no longer reachable. The no-grid fallback is gone.

    // ═══════════════════════════════════════════════════════════════════════
    // Phase 2: Tick-fence AABB recompute pass (issue #230)
    //
    // These tests exercise the RecomputeDirtyClusterAabbs pass wired into
    // WriteClusterTickFence. Each one pairs a movement/write with a tick
    // advancement and inspects the stored AABB + per-cell index entry afterward.
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// T1: Primary Phase 2 assertion — when an entity moves within its cluster (no cross-cell migration),
    /// the stored cluster AABB tightens on the next tick fence. Before Phase 2, the stored AABB would stay
    /// at the old loose containing bounds until the cluster was destroyed.
    /// </summary>
    [Test]
    public void TickFence_InCellMovement_TightensStoredAabb()
    {
        using var dbe = SetupEngineWithGrid(cellSize: 100f);

        // Spawn two entities in the same cell (cellSize=100 → cell (0,0) spans [0, 100)).
        EntityId e1, e2;
        using (var tx = dbe.CreateQuickTransaction())
        {
            e1 = tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(10f, 10f)));
            e2 = tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(80f, 80f)));
            tx.Commit();
        }

        // Settle the initial state with a first tick fence. Spawn unions the AABB incrementally,
        // but we want a single stable snapshot to compare against.
        dbe.WriteTickFence(1);

        var cs = GetClusterState(dbe);
        Assert.That(cs.ActiveClusterCount, Is.EqualTo(1),
            "both entities must share one cluster (same cell, cluster not yet full)");
        int chunkId = cs.ActiveClusterIds[0];
        int cellKey = cs.ClusterCellMap[chunkId];
        int indexSlot = cs.ClusterSpatialIndexSlot[chunkId];
        Assert.That(indexSlot, Is.GreaterThanOrEqualTo(0), "cluster must be in the per-cell index");

        // Initial AABB should span the two spawn positions.
        var before = cs.ClusterAabbs[chunkId];
        Assert.That(before.MinX, Is.EqualTo(10f));
        Assert.That(before.MinY, Is.EqualTo(10f));
        Assert.That(before.MaxX, Is.EqualTo(80f));
        Assert.That(before.MaxY, Is.EqualTo(80f));

        // Move e2 close to e1 — same cluster, same cell, no migration.
        using (var tx = dbe.CreateQuickTransaction())
        {
            var eref = tx.OpenMut(e2);
            ref var pos = ref eref.Write(ClCohUnit.Pos);
            pos.Bounds = new AABB2F { MinX = 15f, MinY = 15f, MaxX = 15f, MaxY = 15f };
            tx.Commit();
        }

        // Phase 2: tick fence must tighten the cluster AABB.
        dbe.WriteTickFence(2);

        var after = cs.ClusterAabbs[chunkId];
        Assert.That(after.MinX, Is.EqualTo(10f), "min X unchanged (still e1)");
        Assert.That(after.MinY, Is.EqualTo(10f), "min Y unchanged (still e1)");
        Assert.That(after.MaxX, Is.EqualTo(15f), "max X tightened — Phase 2 primary assertion");
        Assert.That(after.MaxY, Is.EqualTo(15f), "max Y tightened — Phase 2 primary assertion");

        // The per-cell index SoA entry must reflect the tightened AABB.
        var idx = cs.PerCellIndex[cellKey].DynamicIndex;
        Assert.That(idx.MinX[indexSlot], Is.EqualTo(10f));
        Assert.That(idx.MinY[indexSlot], Is.EqualTo(10f));
        Assert.That(idx.MaxX[indexSlot], Is.EqualTo(15f));
        Assert.That(idx.MaxY[indexSlot], Is.EqualTo(15f));
    }

    /// <summary>
    /// T2: Sanity — a tick fence with no entity writes does not perturb the stored AABB. Proves
    /// the recompute pass is gated by the dirty bitmap (no-work path exercises the per-word zero check).
    /// </summary>
    [Test]
    public void TickFence_NoDirty_AabbUnchanged()
    {
        using var dbe = SetupEngineWithGrid();

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(20f, 20f)));
            tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(40f, 60f)));
            tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(70f, 30f)));
            tx.Commit();
        }

        dbe.WriteTickFence(1);

        var cs = GetClusterState(dbe);
        int chunkId = cs.ActiveClusterIds[0];
        var before = cs.ClusterAabbs[chunkId];

        // Second tick fence with zero writes.
        dbe.WriteTickFence(2);

        var after = cs.ClusterAabbs[chunkId];
        Assert.That(after.MinX, Is.EqualTo(before.MinX));
        Assert.That(after.MinY, Is.EqualTo(before.MinY));
        Assert.That(after.MaxX, Is.EqualTo(before.MaxX));
        Assert.That(after.MaxY, Is.EqualTo(before.MaxY));
        Assert.That(after.CategoryMask, Is.EqualTo(before.CategoryMask));
    }

    /// <summary>
    /// T3: Multi-cluster fan-out — many dirty clusters in a single tick all get tightened.
    /// Proves the recompute loop iterates the entire dirty bitmap, not just the first hit.
    /// </summary>
    [Test]
    public void TickFence_MultipleDirtyClusters_AllTighten()
    {
        using var dbe = SetupEngineWithGrid(cellSize: 100f, worldMax: 2000f);

        // Spawn 6 entity pairs, each pair in a different cell (→ different cluster).
        // Each pair is "far apart" within its cell so the initial AABB is loose.
        var cellCenters = new (float x, float y)[]
        {
            (50f, 50f),    // cell (0,0)
            (150f, 50f),   // cell (1,0)
            (250f, 50f),   // cell (2,0)
            (50f, 150f),   // cell (0,1)
            (150f, 150f),  // cell (1,1)
            (250f, 150f),  // cell (2,1)
        };
        var movedEntities = new EntityId[cellCenters.Length];

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < cellCenters.Length; i++)
            {
                // Anchor at the cell's lower-left area.
                tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(cellCenters[i].x - 40f, cellCenters[i].y - 40f)));
                // Far corner — this is the one we'll later move closer.
                movedEntities[i] = tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(cellCenters[i].x + 40f, cellCenters[i].y + 40f)));
            }
            tx.Commit();
        }

        dbe.WriteTickFence(1);

        var cs = GetClusterState(dbe);
        Assert.That(cs.ActiveClusterCount, Is.EqualTo(cellCenters.Length),
            "each cell must host its own cluster");

        // Move each "far" entity to sit right next to its anchor (still within the same cell).
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < cellCenters.Length; i++)
            {
                var eref = tx.OpenMut(movedEntities[i]);
                ref var pos = ref eref.Write(ClCohUnit.Pos);
                // One unit to the right/up of the anchor — gives a 1x1 tight AABB.
                pos.Bounds = new AABB2F
                {
                    MinX = cellCenters[i].x - 39f,
                    MinY = cellCenters[i].y - 39f,
                    MaxX = cellCenters[i].x - 39f,
                    MaxY = cellCenters[i].y - 39f,
                };
            }
            tx.Commit();
        }

        dbe.WriteTickFence(2);

        // Every cluster's AABB should now cover [center-40 .. center-39] in both axes.
        for (int i = 0; i < cs.ActiveClusterCount; i++)
        {
            int chunkId = cs.ActiveClusterIds[i];
            var aabb = cs.ClusterAabbs[chunkId];
            // Match this cluster back to a cell center via ClusterCellMap.
            int cellKey = cs.ClusterCellMap[chunkId];
            Assert.That(cellKey, Is.GreaterThanOrEqualTo(0));

            // AABB extent must be 1 unit on each axis (from anchor at -40 to moved at -39).
            float width = aabb.MaxX - aabb.MinX;
            float height = aabb.MaxY - aabb.MinY;
            Assert.That(width, Is.EqualTo(1f),
                $"cluster {chunkId} (cell {cellKey}) must have tightened to 1-unit width, got {width}");
            Assert.That(height, Is.EqualTo(1f),
                $"cluster {chunkId} (cell {cellKey}) must have tightened to 1-unit height, got {height}");
        }
    }

    /// <summary>
    /// T4 (GAP-1 from code review): Migration destination cluster tightens correctly when the pre-migration
    /// stored AABB was wider than the post-migration tight bound. This is the main reason the Phase 2 recompute
    /// pass is positioned AFTER <c>ExecuteMigrations</c> — the migration's union-based AABB update produces
    /// conservative bounds, and the recompute must replace them with the tight bound over live entities.
    /// <para>
    /// Scenario: cell B holds two widely-spaced entities m1 at (110,10) and m2 at (190,90) — stored AABB is
    /// (110,10)-(190,90). One entity (m2) is destroyed in tick 2 and simultaneously a migrant from cell A
    /// crosses into cell B. After migration the cluster holds {m1, migrant}. The migration path unions the
    /// migrant's bounds into the already-loose stored AABB (no-op since migrant is inside). The recompute
    /// then scans all live slots and should produce a tight AABB spanning only m1 and the migrant, NOT the
    /// destroyed m2.
    /// </para>
    /// <para>
    /// Note: destroy alone does NOT set any cluster dirty bits (<c>ReleaseSlot</c> only clears occupancy),
    /// so without the concurrent migration the recompute would skip cell B entirely. The test therefore
    /// exercises the specific interaction where migration provides the wake-up dirty bit and the recompute
    /// correctly observes the post-destroy occupancy state.
    /// </para>
    /// </summary>
    [Test]
    public void TickFence_MigrationDestTightens_ExcludingDestroyedSiblingEntity()
    {
        using var dbe = SetupEngineWithGrid(cellSize: 100f);

        // cell A = [0,100)² ; cell B = [100,200) × [0,100)
        EntityId m1, m2, migrant;
        using (var tx = dbe.CreateQuickTransaction())
        {
            m1 = tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(110f, 10f)));
            m2 = tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(190f, 90f)));
            migrant = tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(50f, 50f))); // cell A
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        var cs = GetClusterState(dbe);

        // Sanity: two clusters (A and B), cell B's stored AABB is the wide union of m1 and m2.
        Assert.That(cs.ActiveClusterCount, Is.EqualTo(2));
        int cellB = dbe.SpatialGrid.WorldToCellKey(110f, 10f);
        int cbChunkId = -1;
        for (int i = 0; i < cs.ActiveClusterCount; i++)
        {
            int cid = cs.ActiveClusterIds[i];
            if (cs.ClusterCellMap[cid] == cellB) { cbChunkId = cid; break; }
        }
        Assert.That(cbChunkId, Is.GreaterThanOrEqualTo(0), "cluster in cell B must exist");
        var before = cs.ClusterAabbs[cbChunkId];
        Assert.That(before.MinX, Is.EqualTo(110f));
        Assert.That(before.MaxX, Is.EqualTo(190f));
        Assert.That(before.MinY, Is.EqualTo(10f));
        Assert.That(before.MaxY, Is.EqualTo(90f));

        // Destroy m2 in one transaction (no dirty bit set by destroy alone).
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(m2);
            tx.Commit();
        }

        // Move migrant across the cell boundary in a separate transaction. This write sets a dirty bit in
        // cluster A. At tick fence, DetectClusterMigrations detects the cross-boundary crossing and
        // ExecuteMigrations claims a slot in cluster B (likely reclaiming m2's freed slot).
        using (var tx = dbe.CreateQuickTransaction())
        {
            var eref = tx.OpenMut(migrant);
            ref var pos = ref eref.Write(ClCohUnit.Pos);
            pos.Bounds = new AABB2F { MinX = 150f, MinY = 50f, MaxX = 150f, MaxY = 50f };
            tx.Commit();
        }

        dbe.WriteTickFence(2);

        // Cluster B should now hold {m1, migrant}. The tight AABB spans (110,10) and (150,50).
        // Critically, m2's old (190,90) position must NOT contribute — the recompute scans live occupancy.
        var after = cs.ClusterAabbs[cbChunkId];
        Assert.That(after.MinX, Is.EqualTo(110f), "min X = m1");
        Assert.That(after.MinY, Is.EqualTo(10f),  "min Y = m1");
        Assert.That(after.MaxX, Is.EqualTo(150f), "max X = migrant (m2 excluded because destroyed)");
        Assert.That(after.MaxY, Is.EqualTo(50f),  "max Y = migrant (m2 excluded because destroyed)");

        // The per-cell index SoA row must mirror the tightened AABB.
        int indexSlot = cs.ClusterSpatialIndexSlot[cbChunkId];
        var idx = cs.PerCellIndex[cellB].DynamicIndex;
        Assert.That(idx.MaxX[indexSlot], Is.EqualTo(150f));
        Assert.That(idx.MaxY[indexSlot], Is.EqualTo(50f));
    }

    /// <summary>
    /// T5 (GAP-2 from code review): when an entity is destroyed in a cluster that ALSO has a concurrent
    /// write (which provides the dirty-bit wake-up), the recompute correctly tightens the AABB to exclude
    /// the destroyed entity's old bounds.
    /// <para>
    /// This test documents the Phase 2 design constraint: destroy-only ticks do NOT trigger a recompute
    /// (<see cref="ArchetypeClusterState.ReleaseSlot"/> only clears occupancy, it does not set a cluster
    /// dirty bit). Shrinkage after destroy only happens when another entity in the same cluster is also
    /// written in the same tick. Querying a cluster whose stored AABB is loose-but-containing is still
    /// correct (all live entities are inside the stored AABB) — just slightly less precise until the next
    /// accompanying write forces a recompute.
    /// </para>
    /// </summary>
    [Test]
    public void TickFence_DestroyInCluster_WithConcurrentWrite_TightensExcludingDestroyed()
    {
        using var dbe = SetupEngineWithGrid();

        EntityId e1, e2, e3;
        using (var tx = dbe.CreateQuickTransaction())
        {
            e1 = tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(10f, 10f)));
            e2 = tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(80f, 80f))); // outer — to be destroyed
            e3 = tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(50f, 50f)));
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        var cs = GetClusterState(dbe);
        Assert.That(cs.ActiveClusterCount, Is.EqualTo(1), "all three entities share one cluster");
        int chunkId = cs.ActiveClusterIds[0];
        var before = cs.ClusterAabbs[chunkId];
        Assert.That(before.MaxX, Is.EqualTo(80f));
        Assert.That(before.MaxY, Is.EqualTo(80f));

        // In one transaction: destroy e2 (outer) AND write e1 slightly (provides the dirty-bit wake-up).
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(e2);
            var eref = tx.OpenMut(e1);
            ref var pos = ref eref.Write(ClCohUnit.Pos);
            pos.Bounds = new AABB2F { MinX = 11f, MinY = 11f, MaxX = 11f, MaxY = 11f };
            tx.Commit();
        }

        dbe.WriteTickFence(2);

        // Live entities are now {e1 at (11,11), e3 at (50,50)}. The tight AABB must exclude e2's (80,80).
        var after = cs.ClusterAabbs[chunkId];
        Assert.That(after.MinX, Is.EqualTo(11f));
        Assert.That(after.MinY, Is.EqualTo(11f));
        Assert.That(after.MaxX, Is.EqualTo(50f), "max X should be e3, NOT the destroyed e2");
        Assert.That(after.MaxY, Is.EqualTo(50f), "max Y should be e3, NOT the destroyed e2");

        // Per-cell index SoA entry mirrors the tightened bounds.
        int cellKey = cs.ClusterCellMap[chunkId];
        int indexSlot = cs.ClusterSpatialIndexSlot[chunkId];
        var idx = cs.PerCellIndex[cellKey].DynamicIndex;
        Assert.That(idx.MaxX[indexSlot], Is.EqualTo(50f));
        Assert.That(idx.MaxY[indexSlot], Is.EqualTo(50f));
    }

    /// <summary>
    /// T6: Forward-safety — a pre-seeded category mask on the per-cell index entry survives the tick-fence
    /// recompute. Phase 2 always writes <see cref="uint.MaxValue"/> from <c>RecomputeClusterAabb</c>, so this
    /// test explicitly stamps a non-default mask onto the index entry, forces a recompute via an in-cell
    /// movement, and asserts the mask did not get clobbered. Guards against a Phase 3 footgun where
    /// <c>[SpatialIndex(Category=...)]</c> would otherwise silently lose its attribute-driven mask every tick.
    /// </summary>
    [Test]
    public void TickFence_CategoryMaskPreservedAcrossRecompute()
    {
        using var dbe = SetupEngineWithGrid();

        EntityId e1, e2;
        using (var tx = dbe.CreateQuickTransaction())
        {
            e1 = tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(10f, 10f)));
            e2 = tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(80f, 80f)));
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        var cs = GetClusterState(dbe);
        int chunkId = cs.ActiveClusterIds[0];
        int cellKey = cs.ClusterCellMap[chunkId];
        int indexSlot = cs.ClusterSpatialIndexSlot[chunkId];
        var idx = cs.PerCellIndex[cellKey].DynamicIndex;

        // Stamp a custom mask directly — simulating what Phase 3's archetype-level attribute will do.
        const uint customMask = 0xABCD_1234u;
        idx.CategoryMasks[indexSlot] = customMask;

        // Force a recompute by moving e2 (in-cell), then advance the tick.
        using (var tx = dbe.CreateQuickTransaction())
        {
            var eref = tx.OpenMut(e2);
            ref var pos = ref eref.Write(ClCohUnit.Pos);
            pos.Bounds = new AABB2F { MinX = 20f, MinY = 20f, MaxX = 20f, MaxY = 20f };
            tx.Commit();
        }
        dbe.WriteTickFence(2);

        // The AABB must have tightened (sanity) but the category mask must survive.
        Assert.That(cs.ClusterAabbs[chunkId].MaxX, Is.EqualTo(20f), "AABB should still tighten");
        Assert.That(idx.CategoryMasks[indexSlot], Is.EqualTo(customMask),
            "custom category mask must be preserved across the tick-fence recompute (forward-safety for Phase 3)");
    }
}
