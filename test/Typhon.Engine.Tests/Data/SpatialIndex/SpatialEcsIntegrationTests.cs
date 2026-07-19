using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ═══════════════════════════════════════════════════════════════════════
// Spatial ECS test types
// ═══════════════════════════════════════════════════════════════════════

[Component("Typhon.Test.Spatial.Ship", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct SpatialShip
{
    [Field] [SpatialIndex(5.0f)]
    public AABB3F Bounds;

    [Field]
    public float Speed;
}

[Component("Typhon.Test.Spatial.Name", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct SpatialName
{
    [Field]
    public long Id;
}

[Component("Typhon.Test.Spatial.Building", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential)]
public struct SpatialBuilding
{
    [Field] [SpatialIndex(0.0f)]
    public AABB2F Footprint;

    [Field]
    public int OwnerId;
}

[Component("Typhon.Test.Spatial.TransientBad", 1, StorageMode = StorageMode.Transient)]
[StructLayout(LayoutKind.Sequential)]
public struct TransientBadSpatial
{
    [Field] [SpatialIndex(1.0f)]
    public AABB2F Bounds;
}

[Component("Typhon.Test.Spatial.Terrain", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct SpatialTerrain
{
    [Field] [SpatialIndex(0.0f, Mode = SpatialMode.Static)]
    public AABB3F Footprint;
}

[Archetype]
partial class SpatialShipArchetype : Archetype<SpatialShipArchetype>
{
    public static readonly Comp<SpatialShip> Ship = Register<SpatialShip>();
    public static readonly Comp<SpatialName> Name = Register<SpatialName>();
}

[Archetype]
partial class SpatialBuildingArchetype : Archetype<SpatialBuildingArchetype>
{
    public static readonly Comp<SpatialBuilding> Building = Register<SpatialBuilding>();
}

[Archetype]
partial class SpatialTerrainArchetype : Archetype<SpatialTerrainArchetype>
{
    public static readonly Comp<SpatialTerrain> Terrain = Register<SpatialTerrain>();
}

// ═══════════════════════════════════════════════════════════════════════
// Tests
// ═══════════════════════════════════════════════════════════════════════

[NonParallelizable]
class SpatialEcsIntegrationTests : TestBase<SpatialEcsIntegrationTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    // Issue #230 Phase 3 Option B: cluster spatial archetypes require a configured SpatialGrid. The setup helpers must configure one, and the SpatialTerrain
    // path (Static mode) has its own helper that registers ONLY SpatialTerrain — the legacy "SpatialTerrain + SpatialShip in the same engine" pattern is
    // blocked by the #229 Q10 "one spatial archetype per grid" gate and no longer works under grid-required.
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

    // ── Query-level cardinality helpers (issue #230 Option B) ───────────────
    // These replace the pre-Option-B `GetSpatialTree<TArch>(dbe).EntityCount` structural checks. They run a high-level AABB query over a very large
    // world-bounds region and count the results. Using the high-level Query<T>().WhereInAABB<TComp>() API means the count is served by whichever path the
    // engine currently routes to — legacy tree before Option B commit 2, new per-cell index after.
    private static int CountShipEntities(DatabaseEngine dbe)
    {
        using var tx = dbe.CreateQuickTransaction();
        var results = tx.Query<SpatialShipArchetype>().WhereInAABB<SpatialShip>(-1_000_000, -1_000_000, -1_000_000, 1_000_000, 1_000_000, 1_000_000).Execute();
        return results.Count;
    }

    private static int CountTerrainEntities(DatabaseEngine dbe)
    {
        using var tx = dbe.CreateQuickTransaction();
        var results = tx.Query<SpatialTerrainArchetype>().WhereInAABB<SpatialTerrain>(-1_000_000, -1_000_000, -1_000_000, 1_000_000, 1_000_000, 1_000_000).Execute();
        return results.Count;
    }

    // ── Schema Validation ────────────────────────────────────────────────

    [Test]
    public void Schema_TransientWithSpatialIndex_Throws()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        Assert.Throws<InvalidOperationException>(() => dbe.RegisterComponentFromAccessor<TransientBadSpatial>());
    }

    [Test]
    public void Schema_ValidSpatialField_CreatesSpatialIndex()
    {
        using var dbe = SetupEngine();
        var table = dbe.GetComponentTable<SpatialShip>();
        Assert.That(table.SpatialIndex, Is.Not.Null);
        Assert.That(table.SpatialIndex.Descriptor.CoordCount, Is.EqualTo(6)); // 3D
    }

    [Test]
    public void Schema_NoSpatialField_NullSpatialIndex()
    {
        using var dbe = SetupEngine();
        var table = dbe.GetComponentTable<SpatialName>();
        Assert.That(table.SpatialIndex, Is.Null);
    }

    [Test]
    public void Schema_CellSizeZero_NoHashmap()
    {
        using var dbe = SetupEngine();
        var table = dbe.GetComponentTable<SpatialShip>();
        // SpatialShip uses [SpatialIndex(5.0f)] — CellSize defaults to 0
        Assert.That(table.SpatialIndex.OccupancyMap, Is.Null);
    }

    [Test]
    public void CellKey2D_Lossless_DifferentInputs_DifferentKeys()
    {
        // Verify 2D lossless packing produces unique keys for distinct cell coords
        var keys = new HashSet<long>();
        for (int x = -10; x <= 10; x++)
        {
            for (int y = -10; y <= 10; y++)
            {
                double cx = x * 100.0 + 50;
                double cy = y * 100.0 + 50;
                // coordCount=4 (2D): coords = [minX, minY, maxX, maxY], center = ((min+max)/2)
                Span<double> coords = stackalloc double[] { cx - 1, cy - 1, cx + 1, cy + 1 };
                long key = SpatialMaintainer.ComputeCellKey(coords, 4, 1.0f / 100.0f);
                Assert.That(keys.Add(key), Is.True, $"Duplicate key for cell ({x},{y})");
            }
        }
        Assert.That(keys.Count, Is.EqualTo(21 * 21));
    }

    // ── Spawn + Query ────────────────────────────────────────────────────

    [Test]
    public void Spawn_EntityWithSpatialIndex_InsertedIntoTree()
    {
        using var dbe = SetupEngine();

        using (var t = dbe.CreateQuickTransaction())
        {
            var ship = new SpatialShip { Bounds = new AABB3F { MinX = 10, MinY = 20, MinZ = 30, MaxX = 12, MaxY = 22, MaxZ = 32 }, Speed = 5.0f };
            var id = t.Spawn<SpatialShipArchetype>(SpatialShipArchetype.Ship.Set(in ship), SpatialShipArchetype.Name.Set(new SpatialName { Id = 1 }));
            Assert.That(id.IsNull, Is.False);
            t.Commit();
        }

        // Verify entity count via query (FinalizeSpawns runs on commit).
        Assert.That(CountShipEntities(dbe), Is.EqualTo(1));
    }

    [Test]
    public void TryGetClusterSpatialInfo_SpatialArchetype_ReportsGridAndPerCellContext()
    {
        // Backs the Workbench's spatial-cell context (Inspector segment + cluster-chunk cards): a spatial cluster archetype buckets clusters by grid cell, so a
        // cell holding only a few entities yields one cluster with a few occupied slots — the "why mostly empty" answer. Three ships near the origin all land in
        // ONE cell → one cluster (chunk 0) holding all three.
        using var dbe = SetupEngine();
        using (var t = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 3; i++)
            {
                var ship = new SpatialShip { Bounds = new AABB3F { MinX = 1 + i, MinY = 1, MinZ = 0, MaxX = 3 + i, MaxY = 3, MaxZ = 2 }, Speed = 1f };
                t.Spawn<SpatialShipArchetype>(SpatialShipArchetype.Ship.Set(in ship), SpatialShipArchetype.Name.Set(new SpatialName { Id = i }));
            }
            t.Commit();
        }

        var meta = ArchetypeRegistry.GetMetadata<SpatialShipArchetype>();
        var clusterState = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        var rootPage = clusterState.ClusterSegment.RootPageIndex;

        var found = dbe.TryGetClusterSpatialInfo(rootPage, out var isSpatial, out var cellSize, out var gridWidth, out var gridHeight, out var mode);
        Assert.That(found, Is.True);
        Assert.That(isSpatial, Is.True);
        Assert.That(cellSize, Is.EqualTo(100f), "grid cell size from ConfigureSpatialGrid");
        Assert.That(gridWidth, Is.EqualTo(200), "(10000 − (−10000)) / 100");
        Assert.That(gridHeight, Is.EqualTo(200));
        Assert.That(mode, Is.EqualTo("Dynamic"), "SpatialShip's [SpatialIndex] defaults to Dynamic mode");

        // The sole cluster for the origin cell holds all three ships. Use its real chunk id (clusters need not start at 0) — the same global id the Workbench passes.
        Assert.That(clusterState.ActiveClusterCount, Is.EqualTo(1), "three co-located ships pack into one cluster");
        var chunkId = clusterState.ActiveClusterIds[0];
        var ok = dbe.TryGetClusterChunkSpatialInfo(rootPage, chunkId, out var cellKey, out _, out _, out var entitiesInCell, out var clustersInCell,
            out _, out _, out _, out _);
        Assert.That(ok, Is.True);
        Assert.That(cellKey, Is.GreaterThanOrEqualTo(0));
        Assert.That(entitiesInCell, Is.EqualTo(3), "all three ships bucketed into one cell");
        Assert.That(clustersInCell, Is.EqualTo(1), "one cluster serves the cell");
    }

    [Test]
    public void Spawn_MultipleEntities_AllInTree()
    {
        using var dbe = SetupEngine();

        using (var t = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 50; i++)
            {
                var ship = new SpatialShip { Bounds = new AABB3F { MinX = i * 10, MinY = 0, MinZ = 0, MaxX = i * 10 + 2, MaxY = 2, MaxZ = 2 }, Speed = 1.0f };
                t.Spawn<SpatialShipArchetype>(SpatialShipArchetype.Ship.Set(in ship), SpatialShipArchetype.Name.Set(new SpatialName { Id = i }));
            }
            t.Commit();
        }

        Assert.That(CountShipEntities(dbe), Is.EqualTo(50));
    }

    [Test]
    public void Spawn_AABB3F_QueryFindsEntity()
    {
        using var dbe = SetupEngine();

        using (var t = dbe.CreateQuickTransaction())
        {
            var ship = new SpatialShip { Bounds = new AABB3F { MinX = 10, MinY = 20, MinZ = 30, MaxX = 12, MaxY = 22, MaxZ = 32 }, Speed = 5.0f };
            t.Spawn<SpatialShipArchetype>(SpatialShipArchetype.Ship.Set(in ship), SpatialShipArchetype.Name.Set(new SpatialName { Id = 1 }));
            t.Commit();
        }

        // Query region that overlaps the entity. Use the high-level query API so this test is decoupled from the legacy-vs-new-path routing.
        using var qtx = dbe.CreateQuickTransaction();
        var hits = qtx.Query<SpatialShipArchetype>().WhereInAABB<SpatialShip>(0, 10, 20, 20, 30, 40).Execute();
        Assert.That(hits.Count, Is.GreaterThan(0));
    }

    // Regression: Count() and Any() must apply the spatial predicate, like Execute() does. Before the fix they
    // fell through to the archetype-mask broad scan and counted/answered over the WHOLE archetype, ignoring geometry.
    [Test]
    public void SpatialQuery_CountAndAny_RespectSpatialPredicate()
    {
        using var dbe = SetupEngine();

        using (var t = dbe.CreateQuickTransaction())
        {
            var near = new SpatialShip { Bounds = new AABB3F { MinX = 10, MinY = 20, MinZ = 30, MaxX = 12, MaxY = 22, MaxZ = 32 }, Speed = 1f };
            var far  = new SpatialShip { Bounds = new AABB3F { MinX = 1000, MinY = 1000, MinZ = 1000, MaxX = 1002, MaxY = 1002, MaxZ = 1002 }, Speed = 1f };
            t.Spawn<SpatialShipArchetype>(SpatialShipArchetype.Ship.Set(in near), SpatialShipArchetype.Name.Set(new SpatialName { Id = 1 }));
            t.Spawn<SpatialShipArchetype>(SpatialShipArchetype.Ship.Set(in far),  SpatialShipArchetype.Name.Set(new SpatialName { Id = 2 }));
            t.Commit();
        }
        dbe.WriteTickFence(1);   // build/refresh the spatial index (mirrors the runtime's per-tick fence)

        using var qtx = dbe.CreateQuickTransaction();

        // A region that overlaps only the near ship — selective.
        int executeCount = qtx.Query<SpatialShipArchetype>().WhereInAABB<SpatialShip>(0, 10, 20, 20, 30, 40).Execute().Count;
        int countCount   = qtx.Query<SpatialShipArchetype>().WhereInAABB<SpatialShip>(0, 10, 20, 20, 30, 40).Count();
        bool anyHit      = qtx.Query<SpatialShipArchetype>().WhereInAABB<SpatialShip>(0, 10, 20, 20, 30, 40).Any();

        Assert.Multiple(() =>
        {
            Assert.That(executeCount, Is.EqualTo(1), "Execute should match only the near ship (selectivity sanity check)");
            Assert.That(countCount, Is.EqualTo(executeCount), "Count() must apply the spatial predicate, matching Execute()");
            Assert.That(anyHit, Is.True, "Any() must report the near ship");
        });

        // A region with nothing in it: Count must be 0 and Any false (previously Count returned the archetype size, Any true).
        int emptyCount = qtx.Query<SpatialShipArchetype>().WhereInAABB<SpatialShip>(5000, 5000, 5000, 5001, 5001, 5001).Count();
        bool emptyAny  = qtx.Query<SpatialShipArchetype>().WhereInAABB<SpatialShip>(5000, 5000, 5000, 5001, 5001, 5001).Any();

        Assert.Multiple(() =>
        {
            Assert.That(emptyCount, Is.EqualTo(0), "Count() over an empty region must be 0, not the archetype size");
            Assert.That(emptyAny, Is.False, "Any() over an empty region must be false");
        });
    }

    // Composition: a spatial predicate AND a field predicate must both apply. Before the fix, the field-predicate
    // path was checked first and returned early, silently dropping the spatial predicate.
    [Test]
    public void SpatialQuery_ComposesWithFieldAndWherePredicates()
    {
        using var dbe = SetupEngine();

        using (var t = dbe.CreateQuickTransaction())
        {
            // Three ships clustered near the origin: two slow, one fast.
            SpawnShip(t, 10, 10, speed: 1f);
            SpawnShip(t, 12, 12, speed: 9f);
            SpawnShip(t, 14, 14, speed: 1f);
            // A fast ship far outside the query region.
            SpawnShip(t, 5000, 5000, speed: 9f);
            t.Commit();
        }
        dbe.WriteTickFence(1);

        using var qtx = dbe.CreateQuickTransaction();

        // Region covers only the three near ships; the field predicate keeps only the fast one of those.
        int execHits = qtx.Query<SpatialShipArchetype>()
            .WhereInAABB<SpatialShip>(0, 0, 0, 100, 100, 100).WhereField<SpatialShip>(s => s.Speed > 5f).Execute().Count;
        int countHits = qtx.Query<SpatialShipArchetype>()
            .WhereInAABB<SpatialShip>(0, 0, 0, 100, 100, 100).WhereField<SpatialShip>(s => s.Speed > 5f).Count();
        bool anyHits = qtx.Query<SpatialShipArchetype>()
            .WhereInAABB<SpatialShip>(0, 0, 0, 100, 100, 100).WhereField<SpatialShip>(s => s.Speed > 5f).Any();

        Assert.Multiple(() =>
        {
            Assert.That(execHits, Is.EqualTo(1), "spatial ∩ WhereField: only the fast ship inside the region");
            Assert.That(countHits, Is.EqualTo(1), "Count must compose spatial + WhereField");
            Assert.That(anyHits, Is.True, "Any must compose spatial + WhereField");
        });

        // The .Where(lambda) form composes with spatial too.
        int lambdaHits = qtx.Query<SpatialShipArchetype>()
            .WhereInAABB<SpatialShip>(0, 0, 0, 100, 100, 100).Where<SpatialShip>(s => s.Speed > 5f).Execute().Count;
        Assert.That(lambdaHits, Is.EqualTo(1), "spatial ∩ Where(lambda)");
    }

    // Guards: spatial-clause misuse must throw, not silently drop.
    [Test]
    public void SpatialQueryGuards_UnsupportedCombosThrow()
    {
        using var dbe = SetupEngine();
        using (var t = dbe.CreateQuickTransaction())
        {
            SpawnShip(t, 10, 10, 1f);
            t.Commit();
        }
        dbe.WriteTickFence(1);
        using var qtx = dbe.CreateQuickTransaction();

        // Two spatial predicates — the second would silently overwrite the first.
        Assert.Throws<System.InvalidOperationException>(() =>
            qtx.Query<SpatialShipArchetype>().WhereInAABB<SpatialShip>(0, 0, 0, 100, 100, 100).WhereNearby<SpatialShip>(0, 0, 0, 50));

        // foreach does not apply spatial predicates.
        Assert.Throws<System.InvalidOperationException>(() =>
        {
            foreach (var _ in qtx.Query<SpatialShipArchetype>().WhereInAABB<SpatialShip>(0, 0, 0, 100, 100, 100)) { }
        });

        // A view cannot combine WhereField with a spatial predicate.
        Assert.Throws<System.InvalidOperationException>(() =>
            qtx.Query<SpatialShipArchetype>().WhereInAABB<SpatialShip>(0, 0, 0, 100, 100, 100).WhereField<SpatialShip>(s => s.Speed > 0f).ToView());
    }

    private static void SpawnShip(Transaction t, float x, float y, float speed) =>
        t.Spawn<SpatialShipArchetype>(
            SpatialShipArchetype.Ship.Set(new SpatialShip { Bounds = new AABB3F { MinX = x, MinY = y, MinZ = 0, MaxX = x, MaxY = y, MaxZ = 0 }, Speed = speed }),
            SpatialShipArchetype.Name.Set(new SpatialName { Id = 0 }));

    // ── Destroy ──────────────────────────────────────────────────────────

    [Test]
    public void Destroy_RemovesFromTree()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            var ship = new SpatialShip { Bounds = new AABB3F { MinX = 10, MinY = 20, MinZ = 30, MaxX = 12, MaxY = 22, MaxZ = 32 }, Speed = 5.0f };
            id = t.Spawn<SpatialShipArchetype>(SpatialShipArchetype.Ship.Set(in ship), SpatialShipArchetype.Name.Set(new SpatialName { Id = 1 }));
            t.Commit();
        }

        Assert.That(CountShipEntities(dbe), Is.EqualTo(1));

        using (var t = dbe.CreateQuickTransaction())
        {
            t.Destroy(id);
            t.Commit();
        }

        Assert.That(CountShipEntities(dbe), Is.EqualTo(0));
    }

    // ── Back-pointer consistency ─────────────────────────────────────────

    [Test]
    public void Spawn_ManyEntities_TreeValidatorPasses()
    {
        using var dbe = SetupEngine();

        using (var t = dbe.CreateQuickTransaction())
        {
        for (int i = 0; i < 100; i++)
        {
            var ship = new SpatialShip
            {
                Bounds = new AABB3F
                {
                    MinX = (i % 10) * 20,
                    MinY = (i / 10) * 20,
                    MinZ = 0,
                    MaxX = (i % 10) * 20 + 2,
                    MaxY = (i / 10) * 20 + 2,
                    MaxZ = 2
                },
                Speed = 1.0f
            };
            t.Spawn<SpatialShipArchetype>(SpatialShipArchetype.Ship.Set(in ship), SpatialShipArchetype.Name.Set(new SpatialName { Id = i }));
        }
            t.Commit();
        }

        Assert.That(CountShipEntities(dbe), Is.EqualTo(100));
        // TreeValidator invariant check was legacy-tree-specific and is dropped in Option B (issue #230). The query-level count plus the spawn/query
        // round-trip covered by Spawn_AABB3F_QueryFindsEntity is sufficient regression for the mutation hook correctness.
    }

    // ── Bulk spawn (regression test for #192) ──────────────────────────

    [Test]
    [CancelAfter(10000)]
    public void BulkSpawn_2000Entities_SingleTransaction_NoOverflow()
    {
        using var dbe = SetupEngine();

        using (var t = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 2000; i++)
            {
                var ship = new SpatialShip
                {
                    Bounds = new AABB3F
                    {
                        MinX = (i % 50) * 20, MinY = (i / 50) * 20, MinZ = 0,
                        MaxX = (i % 50) * 20 + 2, MaxY = (i / 50) * 20 + 2, MaxZ = 2
                    },
                    Speed = 1.0f
                };
                t.Spawn<SpatialShipArchetype>(SpatialShipArchetype.Ship.Set(in ship), SpatialShipArchetype.Name.Set(new SpatialName { Id = i }));
            }
            t.Commit();
        }

        Assert.That(CountShipEntities(dbe), Is.EqualTo(2000));
    }

    // ── Static/Dynamic Mode (F2) ──────────────────────────────────────

    private DatabaseEngine SetupStaticEngine()
    {
        // Only SpatialTerrain is registered here (Option B + Q10: one spatial archetype per grid). Tests that need to inspect the SpatialShip component's
        // schema modes use SetupEngine instead.
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<SpatialTerrain>();
        dbe.ConfigureSpatialGrid(new SpatialGridConfig(
            worldMin: new Vector2(-10_000, -10_000),
            worldMax: new Vector2(10_000, 10_000),
            cellSize: 100f));
        dbe.InitializeArchetypes();
        return dbe;
    }

    [Test]
    [CancelAfter(5000)]
    public void Schema_StaticMode_SetsFieldInfoMode()
    {
        using var dbe = SetupStaticEngine();
        var table = dbe.GetComponentTable<SpatialTerrain>();
        Assert.That(table.SpatialIndex, Is.Not.Null);
        Assert.That(table.SpatialIndex.FieldInfo.Mode, Is.EqualTo(SpatialMode.Static));
        Assert.That(table.SpatialIndex.StaticTree, Is.Not.Null);
        Assert.That(table.SpatialIndex.DynamicTree, Is.Null);
    }

    [Test]
    [CancelAfter(5000)]
    public void Schema_DefaultMode_IsDynamic()
    {
        // Uses SetupEngine (SpatialShip + grid) — the old shared setup that also registered SpatialTerrain is blocked by #229 Q10 under Option B.
        using var dbe = SetupEngine();
        var table = dbe.GetComponentTable<SpatialShip>();
        Assert.That(table.SpatialIndex.FieldInfo.Mode, Is.EqualTo(SpatialMode.Dynamic));
        Assert.That(table.SpatialIndex.DynamicTree, Is.Not.Null);
        Assert.That(table.SpatialIndex.StaticTree, Is.Null);
    }

    // Note: BackPointer_TreeSelector_Roundtrip was removed in issue #230 Option B. It asserted on the per-archetype back-pointer CBS segment which is
    // deleted alongside the legacy per-entity cluster R-Tree. No semantic replacement exists because back-pointers are part of the removed mechanism.

    [Test]
    [CancelAfter(5000)]
    public void StaticComponent_InsertAndQuery()
    {
        using var dbe = SetupStaticEngine();

        using (var t = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 10; i++)
            {
                var terrain = new SpatialTerrain
                {
                    Footprint = new AABB3F
                    {
                        MinX = i * 20, MinY = 0, MinZ = 0,
                        MaxX = i * 20 + 10, MaxY = 10, MaxZ = 5
                    }
                };
                t.Spawn<SpatialTerrainArchetype>(SpatialTerrainArchetype.Terrain.Set(in terrain));
            }
            t.Commit();
        }

        Assert.That(CountTerrainEntities(dbe), Is.EqualTo(10));

        // Query a region that overlaps the first 3 terrain pieces — terrain pieces are at x=[0..10], [20..30], [40..50], [60..70]...
        // The query box [-5,-5,-5]→[55,15,10] covers exactly the first 3 (indices 0, 1, 2).
        using var qtx = dbe.CreateQuickTransaction();
        var hits = qtx.Query<SpatialTerrainArchetype>().WhereInAABB<SpatialTerrain>(-5, -5, -5, 55, 15, 10).Execute();
        Assert.That(hits.Count, Is.EqualTo(3));
    }

    [Test]
    [CancelAfter(5000)]
    public void StaticComponent_Remove_Works()
    {
        using var dbe = SetupStaticEngine();

        EntityId terrainId;
        using (var t = dbe.CreateQuickTransaction())
        {
            var terrain = new SpatialTerrain
            {
                Footprint = new AABB3F { MinX = 0, MinY = 0, MinZ = 0, MaxX = 10, MaxY = 10, MaxZ = 5 }
            };
            terrainId = t.Spawn<SpatialTerrainArchetype>(SpatialTerrainArchetype.Terrain.Set(in terrain));
            t.Commit();
        }

        Assert.That(CountTerrainEntities(dbe), Is.EqualTo(1));

        // Destroy the entity
        using (var t = dbe.CreateQuickTransaction())
        {
            t.Destroy(terrainId);
            t.Commit();
        }

        Assert.That(CountTerrainEntities(dbe), Is.EqualTo(0));
    }

    [Test]
    [CancelAfter(5000)]
    public void StaticComponent_TickFenceSkipped()
    {
        using var dbe = SetupStaticEngine();

        // Spawn static terrain
        using (var t = dbe.CreateQuickTransaction())
        {
            var terrain = new SpatialTerrain
            {
                Footprint = new AABB3F { MinX = 0, MinY = 0, MinZ = 0, MaxX = 10, MaxY = 10, MaxZ = 5 }
            };
            t.Spawn<SpatialTerrainArchetype>(SpatialTerrainArchetype.Terrain.Set(in terrain));
            t.Commit();
        }

        var table = dbe.GetComponentTable<SpatialTerrain>();
        int entityCountBefore = table.SpatialIndex.ActiveTree.EntityCount;
        int nodeCountBefore = table.SpatialIndex.ActiveTree.NodeCount;

        // Modify the component data (simulating an update) — for static mode, tick fence should NOT process this
        // The DirtyBitmap marks the chunk dirty, but ProcessSpatialEntries should skip it
        using (var t = dbe.CreateQuickTransaction())
        {
            // Just opening and committing should trigger a tick fence, but no spatial update for static
            t.Commit();
        }

        // Tree should be unchanged (no reinserts, no structural changes)
        Assert.That(table.SpatialIndex.ActiveTree.EntityCount, Is.EqualTo(entityCountBefore));
        Assert.That(table.SpatialIndex.ActiveTree.NodeCount, Is.EqualTo(nodeCountBefore));
    }
}
