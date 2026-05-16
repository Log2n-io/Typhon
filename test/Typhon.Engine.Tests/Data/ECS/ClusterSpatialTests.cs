using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ═══════════════════════════════════════════════════════════════════════════════
// Test-only spatial SV components for cluster spatial integration (Phase 3b)
// ═══════════════════════════════════════════════════════════════════════════════

[Component("Typhon.Test.ClSp.Pos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct ClSpatialPos
{
    [Field]
    [SpatialIndex(5.0f)]
    public AABB3F Bounds;

    [Field]
    public float Speed;
}

[Component("Typhon.Test.ClSp.Meta", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct ClSpatialMeta
{
    [Field]
    public long Tag;
}

[Archetype(830)]
partial class ClSpatialUnit : Archetype<ClSpatialUnit>
{
    public static readonly Comp<ClSpatialPos> Pos = Register<ClSpatialPos>();
    public static readonly Comp<ClSpatialMeta> Meta = Register<ClSpatialMeta>();
}

// Non-cluster archetype sharing the same spatial component (Versioned → not cluster-eligible)
[Component("Typhon.Test.ClSp.VData", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential)]
struct ClSpatialVData
{
    [Field]
    public int Value;
    [Field]
    public int Padding;
}

// Static spatial component for Phase 3c test
[Component("Typhon.Test.ClSp.StaticPos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct ClSpatialStaticPos
{
    [Field]
    [SpatialIndex(0.0f, Mode = SpatialMode.Static)]
    public AABB3F Bounds;
}

[Archetype(831)]
partial class ClSpatialNonClusterUnit : Archetype<ClSpatialNonClusterUnit>
{
    public static readonly Comp<ClSpatialPos> Pos = Register<ClSpatialPos>();
    public static readonly Comp<ClSpatialVData> VData = Register<ClSpatialVData>();
}

[Archetype(832)]
partial class ClSpatialStaticUnit : Archetype<ClSpatialStaticUnit>
{
    public static readonly Comp<ClSpatialStaticPos> StaticPos = Register<ClSpatialStaticPos>();
    public static readonly Comp<ClSpatialMeta> Meta = Register<ClSpatialMeta>();
}

// ═══════════════════════════════════════════════════════════════════════════════
// Tests: Per-archetype Spatial R-Tree integration with cluster storage
// ═══════════════════════════════════════════════════════════════════════════════

[TestFixture]
[NonParallelizable]
class ClusterSpatialTests : TestBase<ClusterSpatialTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        Archetype<ClSpatialUnit>.Touch();
        Archetype<ClSpatialStaticUnit>.Touch();
        // ClSpatialNonClusterUnit is no longer touched — the Mixed_* tests that used it were deleted in issue #230 Phase 3 Option B (see note above).
    }

    // Issue #230 Phase 3 Option B: cluster spatial archetypes require a configured SpatialGrid. We register ONLY ClSpatialPos + ClSpatialMeta so
    // ClSpatialNonClusterUnit (needs ClSpatialVData) and ClSpatialStaticUnit (needs ClSpatialStaticPos) are skipped by InitializeArchetypes'
    // "all components registered" gate. Under the #229 Q10 "one spatial archetype per grid" constraint, this is the only viable pattern — see the
    // StaticSpatial test below which uses a dedicated SetupStaticEngine for its own single-archetype engine.
    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClSpatialPos>();
        dbe.RegisterComponentFromAccessor<ClSpatialMeta>();
        dbe.ConfigureSpatialGrid(new SpatialGridConfig(
            worldMin: new Vector2(-10_000, -10_000),
            worldMax: new Vector2(10_000, 10_000),
            cellSize: 100f));
        dbe.InitializeArchetypes();
        return dbe;
    }

    private DatabaseEngine SetupStaticEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClSpatialStaticPos>();
        dbe.RegisterComponentFromAccessor<ClSpatialMeta>();
        dbe.ConfigureSpatialGrid(new SpatialGridConfig(
            worldMin: new Vector2(-10_000, -10_000),
            worldMax: new Vector2(10_000, 10_000),
            cellSize: 100f));
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static ClSpatialPos MakePos(float x, float y, float z, float size = 1.0f, float speed = 0f) =>
        new() { Bounds = new AABB3F { MinX = x - size, MinY = y - size, MinZ = z - size, MaxX = x + size, MaxY = y + size, MaxZ = z + size }, Speed = speed };

    // ── Query-level cardinality helpers (issue #230 Option B) ───────────────
    // These replace the pre-Option-B `cs.SpatialSlot.Tree.EntityCount` structural checks. They run a high-level AABB query over a very large world-bounds
    // region and count the results, which is a semantic equivalent: "how many entities of this archetype currently live in the spatial index?"
    // Using the high-level Query<T>().WhereInAABB<TComp>() API means the count is served by whichever path the engine currently routes to — legacy tree before
    // Option B commit 2, new per-cell index after. This keeps the assertions stable across the commit boundary.
    private static int CountClSpatialEntities(DatabaseEngine dbe)
    {
        using var tx = dbe.CreateQuickTransaction();
        var results = tx.Query<ClSpatialUnit>().WhereInAABB<ClSpatialPos>(-1_000_000, -1_000_000, -1_000_000, 1_000_000, 1_000_000, 1_000_000).Execute();
        return results.Count;
    }

    private static int CountClSpatialStaticEntities(DatabaseEngine dbe)
    {
        using var tx = dbe.CreateQuickTransaction();
        var results = tx.Query<ClSpatialStaticUnit>().WhereInAABB<ClSpatialStaticPos>(-1_000_000, -1_000_000, -1_000_000, 1_000_000, 1_000_000, 1_000_000).Execute();
        return results.Count;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Infrastructure verification
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void ClusterEligible_DynamicSpatial_HasClusterSpatialTrue()
    {
        using var dbe = SetupEngine();
        var meta = Archetype<ClSpatialUnit>.Metadata;
        Assert.That(meta.IsClusterEligible, Is.True, "Dynamic SV spatial archetype should be cluster-eligible");
        Assert.That(meta.HasClusterSpatial, Is.True, "Should have per-archetype spatial");

        var es = dbe._archetypeStates[meta.ArchetypeId];
        Assert.That(es.ClusterState, Is.Not.Null);
        Assert.That(es.ClusterState.SpatialSlot.HasSpatialIndex, Is.True, "Cluster spatial slot should be configured");
        Assert.That(es.ClusterState.ClusterDirtyRing, Is.Not.Null, "Per-archetype DirtyBitmapRing should exist");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // CRUD
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Spawn_WithSpatialField_PerArchetypeRTreeContainsEntry()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();

        var pos = MakePos(10, 20, 30);
        var met = new ClSpatialMeta { Tag = 42 };
        tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in pos), ClSpatialUnit.Meta.Set(in met));
        tx.Commit();

        Assert.That(CountClSpatialEntities(dbe), Is.EqualTo(1), "Spatial index should have 1 entry");
    }

    [Test]
    public void Spawn_MultipleEntities_AllInTree()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();

        for (int i = 0; i < 50; i++)
        {
            var pos = MakePos(i * 10, 0, 0);
            var met = new ClSpatialMeta { Tag = i };
            tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in pos), ClSpatialUnit.Meta.Set(in met));
        }
        tx.Commit();

        Assert.That(CountClSpatialEntities(dbe), Is.EqualTo(50));
    }

    [Test]
    public void Destroy_RemovesFromPerArchetypeRTree()
    {
        using var dbe = SetupEngine();
        EntityId id;
        {
            using var tx = dbe.CreateQuickTransaction();
            var pos = MakePos(10, 20, 30);
            var met = new ClSpatialMeta { Tag = 1 };
            id = tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in pos), ClSpatialUnit.Meta.Set(in met));
            tx.Commit();
        }

        Assert.That(CountClSpatialEntities(dbe), Is.EqualTo(1));

        {
            using var tx = dbe.CreateQuickTransaction();
            tx.Destroy(id);
            tx.Commit();
        }

        Assert.That(CountClSpatialEntities(dbe), Is.EqualTo(0), "Destroyed entity removed from spatial index");
    }

    [Test]
    public void SharedRTree_EmptyAfterClusterEntitySpawn()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();

        var pos = MakePos(10, 20, 30);
        var met = new ClSpatialMeta { Tag = 1 };
        tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in pos), ClSpatialUnit.Meta.Set(in met));
        tx.Commit();

        var table = dbe.GetComponentTable<ClSpatialPos>();
        Assert.That(table.SpatialIndex, Is.Not.Null);
        Assert.That(table.SpatialIndex.DynamicTree.EntityCount, Is.EqualTo(0),
            "Shared per-table R-Tree should be empty — cluster entities use per-archetype tree");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Spatial Query
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void SpatialQuery_AABB_ReturnsClusterEntities()
    {
        using var dbe = SetupEngine();

        EntityId id1, id2;
        {
            using var tx = dbe.CreateQuickTransaction();
            var pos1 = MakePos(10, 10, 10);
            var pos2 = MakePos(100, 100, 100);
            var met = new ClSpatialMeta { Tag = 1 };
            id1 = tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in pos1), ClSpatialUnit.Meta.Set(in met));
            met.Tag = 2;
            id2 = tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in pos2), ClSpatialUnit.Meta.Set(in met));
            tx.Commit();
        }

        // Query near (10,10,10) should find id1 but not id2
        using var tx2 = dbe.CreateQuickTransaction();
        var results = tx2.Query<ClSpatialUnit>().WhereInAABB<ClSpatialPos>(0, 0, 0, 20, 20, 20).Execute();
        Assert.That(results, Does.Contain(id1));
        Assert.That(results, Does.Not.Contain(id2));
    }

    [Test]
    public void SpatialQuery_Radius_ReturnsClusterEntities()
    {
        using var dbe = SetupEngine();

        EntityId id1, id2;
        {
            using var tx = dbe.CreateQuickTransaction();
            var pos1 = MakePos(5, 5, 5);
            var pos2 = MakePos(500, 500, 500);
            var met = new ClSpatialMeta { Tag = 1 };
            id1 = tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in pos1), ClSpatialUnit.Meta.Set(in met));
            met.Tag = 2;
            id2 = tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in pos2), ClSpatialUnit.Meta.Set(in met));
            tx.Commit();
        }

        using var tx2 = dbe.CreateQuickTransaction();
        var results = tx2.Query<ClSpatialUnit>().WhereNearby<ClSpatialPos>(5, 5, 5, 15).Execute();
        Assert.That(results, Does.Contain(id1));
        Assert.That(results, Does.Not.Contain(id2));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Tick Fence
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void TickFence_MovedEntity_FatAABBEscape_RTreeUpdated()
    {
        using var dbe = SetupEngine();

        EntityId id;
        {
            using var tx = dbe.CreateQuickTransaction();
            var pos = MakePos(10, 10, 10);
            var met = new ClSpatialMeta { Tag = 1 };
            id = tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in pos), ClSpatialUnit.Meta.Set(in met));
            tx.Commit();
        }

        // Write new position far away (escapes fat AABB)
        {
            using var tx = dbe.CreateQuickTransaction();
            var eref = tx.OpenMut(id);
            ref var pos = ref eref.Write(ClSpatialUnit.Pos);
            pos = MakePos(200, 200, 200);
            tx.Commit();
        }

        dbe.WriteTickFence(1);

        // Query at new position should find entity
        {
            using var tx = dbe.CreateQuickTransaction();
            var results = tx.Query<ClSpatialUnit>().WhereInAABB<ClSpatialPos>(190, 190, 190, 210, 210, 210).Execute();
            Assert.That(results, Does.Contain(id), "Entity should be findable at new position after tick fence");
        }

        // Query at old position should NOT find entity
        {
            using var tx = dbe.CreateQuickTransaction();
            var results = tx.Query<ClSpatialUnit>().WhereInAABB<ClSpatialPos>(0, 0, 0, 20, 20, 20).Execute();
            Assert.That(results, Does.Not.Contain(id), "Entity should NOT be at old position after tick fence");
        }
    }

    [Test]
    public void TickFence_DirectSpanWrite_NoMarkSlotDirty_AABB_StillRefreshed()
    {
        // Regression test (2026-05-13): the cluster-direct-memory API ClusterRef.GetSpan<T>() returns
        // a raw Span<T> into cluster memory and does NOT auto-mark slots dirty. A system that integrates
        // positions via cluster.GetSpan<Pos>() without calling MarkSlotDirty previously left the
        // ClusterDirtyBitmap empty, which made RecomputeDirtyClusterAabbs skip the cluster — freezing its
        // stored AABB at spawn values while the entity positions drifted. Small-radius spatial queries
        // then silently returned 0 hits because the broadphase rejected the cluster's stale-tight AABB.
        // Found via AntHill's spider chase code: chase at radius 1000 found the ant, kill at radius 80
        // missed it (the wider chase query overlapped the stale AABB; the tight kill did not).
        // This test makes the bug pattern explicit and verifies the fix.
        using var dbe = SetupEngine();

        // Spawn 4 entities clustered around (10, 10, 10) so they all land in one cluster.
        var ids = new EntityId[4];
        {
            using var tx = dbe.CreateQuickTransaction();
            for (int i = 0; i < ids.Length; i++)
            {
                var pos = MakePos(10, 10, 10, size: 0.5f);
                var met = new ClSpatialMeta { Tag = i };
                ids[i] = tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in pos), ClSpatialUnit.Meta.Set(in met));
            }
            tx.Commit();
        }

        // Sanity: query at spawn position finds all four.
        {
            using var tx = dbe.CreateQuickTransaction();
            var found = tx.Query<ClSpatialUnit>().WhereNearby<ClSpatialPos>(10, 10, 10, 5).Execute();
            Assert.That(found.Count, Is.EqualTo(4), "All spawned entities visible at spawn position");
        }

        // Mutate positions via cluster span — the exact pattern that bypasses dirty tracking.
        // Move every entity ~200 units away from spawn so the new positions are FAR outside the
        // cluster's stored (spawn-time) AABB.
        {
            using var tx = dbe.CreateQuickTransaction();
            var accessor = tx.For<ClSpatialUnit>();
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                var positions = cluster.GetSpan(ClSpatialUnit.Pos);
                var bits = cluster.OccupancyBits;
                while (bits != 0)
                {
                    int slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    ref var p = ref positions[slot];
                    p.Bounds.MinX = 200f - 0.5f; p.Bounds.MaxX = 200f + 0.5f;
                    p.Bounds.MinY = 200f - 0.5f; p.Bounds.MaxY = 200f + 0.5f;
                    p.Bounds.MinZ = 200f - 0.5f; p.Bounds.MaxZ = 200f + 0.5f;
                    // Deliberately NO MarkSlotDirty — that's the bug pattern under test.
                }
            }
            accessor.Dispose();
            tx.Commit();
        }

        // Tick fence — engine should refresh cluster AABBs from current entity positions.
        dbe.WriteTickFence(1);

        // Tight-radius query at NEW position must find all four entities.
        // Pre-fix: returned 0 (stale cluster AABB at spawn position doesn't overlap query bubble).
        // Post-fix: returns all four because RecomputeDirtyClusterAabbs scans every active cluster.
        using var qtx = dbe.CreateQuickTransaction();
        var hits = qtx.Query<ClSpatialUnit>().WhereNearby<ClSpatialPos>(200, 200, 200, 5).Execute();
        Assert.That(hits.Count, Is.EqualTo(4),
            "After GetSpan write + tick fence, tight-radius query at new position must find all moved entities");
        foreach (var id in ids)
        {
            Assert.That(hits, Does.Contain(id));
        }
    }

    [Test]
    public void TickFence_SmallMove_NoEscape_FastPath()
    {
        using var dbe = SetupEngine();

        EntityId id;
        {
            using var tx = dbe.CreateQuickTransaction();
            var pos = MakePos(10, 10, 10);
            var met = new ClSpatialMeta { Tag = 1 };
            id = tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in pos), ClSpatialUnit.Meta.Set(in met));
            tx.Commit();
        }

        // Write small move (within margin=5 → fat AABB [4,4,4,16,16,16])
        {
            using var tx = dbe.CreateQuickTransaction();
            var eref = tx.OpenMut(id);
            ref var pos = ref eref.Write(ClSpatialUnit.Pos);
            pos = MakePos(11, 11, 11); // +1 from center, within fat AABB
            tx.Commit();
        }

        dbe.WriteTickFence(1);

        // Entity should still be queryable
        using var tx2 = dbe.CreateQuickTransaction();
        var results = tx2.Query<ClSpatialUnit>().WhereNearby<ClSpatialPos>(11, 11, 11, 10).Execute();
        Assert.That(results, Does.Contain(id));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Edge Cases
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void SpawnAndDestroySameTx_NoSpatialLeak()
    {
        using var dbe = SetupEngine();

        {
            using var tx = dbe.CreateQuickTransaction();
            var pos = MakePos(10, 10, 10);
            var met = new ClSpatialMeta { Tag = 1 };
            var id = tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in pos), ClSpatialUnit.Meta.Set(in met));
            tx.Destroy(id);
            tx.Commit();
        }

        Assert.That(CountClSpatialEntities(dbe), Is.EqualTo(0), "Spawn+destroy in same tx: no spatial index leak");
    }

    [Test]
    public void BulkSpawn_100Entities_AllInTree()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();

        for (int i = 0; i < 100; i++)
        {
            var pos = MakePos(i * 20, 0, 0, 2.0f);
            var met = new ClSpatialMeta { Tag = i };
            tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in pos), ClSpatialUnit.Meta.Set(in met));
        }
        tx.Commit();

        Assert.That(CountClSpatialEntities(dbe), Is.EqualTo(100));
    }

    [Test]
    public void TickFence_DestroyedEntity_NoCrash()
    {
        using var dbe = SetupEngine();

        EntityId id;
        {
            using var tx = dbe.CreateQuickTransaction();
            var pos = MakePos(10, 10, 10);
            var met = new ClSpatialMeta { Tag = 1 };
            id = tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in pos), ClSpatialUnit.Meta.Set(in met));
            tx.Commit();
        }

        // Write then destroy in separate tx
        {
            using var tx = dbe.CreateQuickTransaction();
            var eref = tx.OpenMut(id);
            ref var pos = ref eref.Write(ClSpatialUnit.Pos);
            pos = MakePos(200, 200, 200);
            tx.Destroy(id);
            tx.Commit();
        }

        Assert.DoesNotThrow(() => dbe.WriteTickFence(1));

        Assert.That(CountClSpatialEntities(dbe), Is.EqualTo(0));
    }

    [Test]
    public void MultipleSpawnAndDestroy_TreeCountCorrect()
    {
        using var dbe = SetupEngine();
        var ids = new EntityId[10];

        {
            using var tx = dbe.CreateQuickTransaction();
            for (int i = 0; i < 10; i++)
            {
                var pos = MakePos(i * 20, 0, 0);
                var met = new ClSpatialMeta { Tag = i };
                ids[i] = tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in pos), ClSpatialUnit.Meta.Set(in met));
            }
            tx.Commit();
        }

        Assert.That(CountClSpatialEntities(dbe), Is.EqualTo(10));

        {
            using var tx = dbe.CreateQuickTransaction();
            for (int i = 0; i < 5; i++)
            {
                tx.Destroy(ids[i]);
            }
            tx.Commit();
        }

        Assert.That(CountClSpatialEntities(dbe), Is.EqualTo(5));
    }

    // Note: the Mixed_ClusterAndNonCluster_BothQueryable and Mixed_TriggerRegion_DetectsEntitiesFromBothPaths tests were deleted in issue #230 Phase 3
    // Option B. They exercised the legacy no-grid fallback with TWO cluster-spatial archetypes registered against the same grid — a scenario the #229 Q10
    // "one spatial archetype per grid" gate blocks and which Option B's grid-required rule makes entirely infeasible. When Q10 is resolved (split per-cell
    // cluster lists per archetype), these scenarios should be re-added as fixtures configuring a multi-archetype grid.

    // ═══════════════════════════════════════════════════════════════════════
    // Back-pointer swap consistency under bulk remove
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void BulkRemove_BackPointerConsistency_AllRemainingQueryable()
    {
        using var dbe = SetupEngine();
        var ids = new EntityId[50];

        {
            using var tx = dbe.CreateQuickTransaction();
            for (int i = 0; i < 50; i++)
            {
                var pos = MakePos(i * 20, 0, 0, 2.0f);
                var met = new ClSpatialMeta { Tag = i };
                ids[i] = tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in pos), ClSpatialUnit.Meta.Set(in met));
            }
            tx.Commit();
        }

        Assert.That(CountClSpatialEntities(dbe), Is.EqualTo(50));

        // Remove every other entity (forces many swap-on-remove operations)
        {
            using var tx = dbe.CreateQuickTransaction();
            for (int i = 0; i < 50; i += 2)
            {
                tx.Destroy(ids[i]);
            }
            tx.Commit();
        }

        Assert.That(CountClSpatialEntities(dbe), Is.EqualTo(25));

        // Every surviving entity should be queryable at its correct position
        for (int i = 1; i < 50; i += 2)
        {
            float x = i * 20;
            using var tx = dbe.CreateQuickTransaction();
            var results = tx.Query<ClSpatialUnit>()
                .WhereInAABB<ClSpatialPos>(x - 10, -10, -10, x + 10, 10, 10).Execute();
            Assert.That(results, Does.Contain(ids[i]), $"Entity {i} at x={x} should be queryable after bulk remove");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Static spatial mode (Phase 3c)
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void StaticSpatial_ClusterEligible_SpawnAndQuery()
    {
        using var dbe = SetupStaticEngine();
        var meta = Archetype<ClSpatialStaticUnit>.Metadata;
        Assert.That(meta.IsClusterEligible, Is.True, "Static SV spatial archetype should be cluster-eligible");
        Assert.That(meta.HasClusterSpatial, Is.True);

        var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        Assert.That(cs.SpatialSlot.HasSpatialIndex, Is.True, "Cluster spatial slot should be configured for Static spatial");

        // Spawn static entities
        EntityId id1, id2;
        {
            using var tx = dbe.CreateQuickTransaction();
            var pos1 = new ClSpatialStaticPos { Bounds = new AABB3F { MinX = 10, MinY = 10, MinZ = 10, MaxX = 12, MaxY = 12, MaxZ = 12 } };
            var pos2 = new ClSpatialStaticPos { Bounds = new AABB3F { MinX = 50, MinY = 50, MinZ = 50, MaxX = 52, MaxY = 52, MaxZ = 52 } };
            var met = new ClSpatialMeta { Tag = 1 };
            id1 = tx.Spawn<ClSpatialStaticUnit>(ClSpatialStaticUnit.StaticPos.Set(in pos1), ClSpatialStaticUnit.Meta.Set(in met));
            met.Tag = 2;
            id2 = tx.Spawn<ClSpatialStaticUnit>(ClSpatialStaticUnit.StaticPos.Set(in pos2), ClSpatialStaticUnit.Meta.Set(in met));
            tx.Commit();
        }

        Assert.That(CountClSpatialStaticEntities(dbe), Is.EqualTo(2));

        // Query should find both
        {
            using var tx = dbe.CreateQuickTransaction();
            var results = tx.Query<ClSpatialStaticUnit>().WhereInAABB<ClSpatialStaticPos>(0, 0, 0, 60, 60, 60).Execute();
            Assert.That(results.Count, Is.EqualTo(2));
            Assert.That(results, Does.Contain(id1));
            Assert.That(results, Does.Contain(id2));
        }

        // Tick fence should NOT affect static spatial entities (no crash, no change in count)
        dbe.WriteTickFence(1);
        Assert.That(CountClSpatialStaticEntities(dbe), Is.EqualTo(2), "Static spatial unaffected by tick fence");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Database reopen with persisted spatial data
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Reopen_PerArchetypeSpatialLoaded_QueryWorks()
    {
        var dbName = $"T_ClSpatialReopen_{Environment.ProcessId}";
        EntityId id1, id2;

        // Session 1: create database, spawn entities
        {
            using var dbe = CreateNamedEngine(dbName);
            var pos1 = MakePos(10, 10, 10);
            var pos2 = MakePos(50, 50, 50);
            var met = new ClSpatialMeta { Tag = 1 };
            using var tx = dbe.CreateQuickTransaction();
            id1 = tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in pos1), ClSpatialUnit.Meta.Set(in met));
            met.Tag = 2;
            id2 = tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in pos2), ClSpatialUnit.Meta.Set(in met));
            tx.Commit();

            Assert.That(CountClSpatialEntities(dbe), Is.EqualTo(2));
        }

        // Session 2: reopen, verify spatial query works
        {
            using var dbe = CreateNamedEngine(dbName);
            var meta = Archetype<ClSpatialUnit>.Metadata;
            var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
            Assert.That(cs, Is.Not.Null, "ClusterState should exist after reopen");
            Assert.That(cs.SpatialSlot.HasSpatialIndex, Is.True, "Cluster spatial slot should be configured after reopen");
            Assert.That(CountClSpatialEntities(dbe), Is.EqualTo(2), "Spatial index should have 2 entities after reopen");

            // Spatial query should find both entities
            using var tx = dbe.CreateQuickTransaction();
            var results = tx.Query<ClSpatialUnit>().WhereInAABB<ClSpatialPos>(0, 0, 0, 60, 60, 60).Execute();
            Assert.That(results.Count, Is.EqualTo(2), "Both entities should be queryable after reopen");
        }
    }

    private DatabaseEngine CreateNamedEngine(string dbName)
    {
        var sc = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Critical))
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
        dbe.RegisterComponentFromAccessor<ClSpatialPos>();
        dbe.RegisterComponentFromAccessor<ClSpatialMeta>();
        dbe.ConfigureSpatialGrid(new SpatialGridConfig(
            worldMin: new Vector2(-10_000, -10_000),
            worldMax: new Vector2(10_000, 10_000),
            cellSize: 100f));
        dbe.InitializeArchetypes();
        return dbe;
    }
}
