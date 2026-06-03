using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ═══════════════════════════════════════════════════════════════════════════════
// Test-only indexed SV components for cluster index integration
// ═══════════════════════════════════════════════════════════════════════════════

[Component("Typhon.Test.ClIdx.Health", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct ClIdxHealth
{
    [Index]
    public int Current;

    public int Max;

    public ClIdxHealth(int cur, int max)
    {
        Current = cur;
        Max = max;
    }
}

[Archetype(525)]
partial class ClIdxUnit : Archetype<ClIdxUnit>
{
    public static readonly Comp<ClPosition> Position = Register<ClPosition>();
    public static readonly Comp<ClIdxHealth> Health = Register<ClIdxHealth>();
}

// ═══════════════════════════════════════════════════════════════════════════════
// Tests: Per-archetype B+Tree index integration with cluster storage
// ═══════════════════════════════════════════════════════════════════════════════

[TestFixture]
[NonParallelizable]
class ClusterIndexTests : TestBase<ClusterIndexTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        Archetype<ClIdxUnit>.Touch();
        Archetype<ClUnit>.Touch();
    }

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClPosition>();
        dbe.RegisterComponentFromAccessor<ClMovement>();
        dbe.RegisterComponentFromAccessor<ClIdxHealth>();
        dbe.RegisterComponentFromAccessor<ClVHealth>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Infrastructure verification
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void ClusterEligible_IndexedSvArchetype_HasClusterState()
    {
        using var dbe = SetupEngine();
        var meta = Archetype<ClIdxUnit>.Metadata;
        Assert.That(meta.IsClusterEligible, Is.True, "Indexed SV archetype should be cluster-eligible after Phase 3a");
        Assert.That(meta.HasClusterIndexes, Is.True, "Should have per-archetype indexes");

        var es = dbe._archetypeStates[meta.ArchetypeId];
        Assert.That(es.ClusterState, Is.Not.Null, "ClusterState should exist");
        Assert.That(es.ClusterState.IndexSlots, Is.Not.Null, "IndexSlots should be initialized");
        Assert.That(es.ClusterState.IndexSlots.Length, Is.GreaterThan(0), "Should have at least one index slot");
        Assert.That(es.ClusterState.ClusterShadowBitmap, Is.Not.Null, "Shadow bitmap should be initialized");
        Assert.That(es.ClusterState.IndexSegment, Is.Not.Null, "Index segment should be allocated");
    }

    [Test]
    public void ClusterIndexSegment_ResolvesToOwningArchetypeName()
    {
        // Regression (File Map): the per-archetype cluster index rendered as a bare "Index #id" because the owner
        // resolver only knew component-table indexes (Default/String64/Tail), not the archetype-owned cluster index
        // (ClusterState.IndexSegment). TryGetSegmentOwnerArchetypeName must name it like Cluster / EntityMap do, so the
        // Workbench's short-name labeller can show "Index · ClIdxUnit" instead of "Index #id".
        using var dbe = SetupEngine();
        var meta = Archetype<ClIdxUnit>.Metadata;
        var es = dbe._archetypeStates[meta.ArchetypeId];
        Assert.That(es.ClusterState?.IndexSegment, Is.Not.Null, "indexed cluster archetype must have an index segment");

        var indexRoot = es.ClusterState.IndexSegment.RootPageIndex;
        Assert.That(dbe.TryGetSegmentOwnerArchetypeName(indexRoot, StorageSegmentKind.Index, out var owner), Is.True,
            "the cluster index segment must resolve to its owning archetype");
        Assert.That(owner, Does.Contain(nameof(ClIdxUnit)), "cluster index owner name is the owning archetype");

        // A page owned by no cluster index must not resolve as an Index owner (page 0 is the reserved file header).
        Assert.That(dbe.TryGetSegmentOwnerArchetypeName(0, StorageSegmentKind.Index, out _), Is.False,
            "a page owned by no cluster index must not resolve");
    }

    [Test]
    public void ClusterEligible_VersionedArchetype_NoClusterIndexes()
    {
        using var dbe = SetupEngine();
        var meta = Archetype<ClUnit>.Metadata;
        Assert.That(meta.IsClusterEligible, Is.False, "Versioned archetype should not be cluster-eligible");
        Assert.That(meta.HasClusterIndexes, Is.False);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Index CRUD
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public unsafe void Spawn_WithIndexedField_PerArchetypeBTreeContainsEntry()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();
        tx.Spawn<ClIdxUnit>(ClIdxUnit.Health.Set(new ClIdxHealth(100, 200)));
        tx.Commit();

        // Verify per-archetype B+Tree has the entry
        var cs = dbe._archetypeStates[Archetype<ClIdxUnit>.Metadata.ArchetypeId].ClusterState;
        ref var field = ref cs.IndexSlots[0].Fields[0]; // Health.Current is the only indexed field
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var accessor = field.Index.Segment.CreateChunkAccessor();
        try
        {
            int key = 100;
            var result = field.Index.TryGet(&key, ref accessor);
            Assert.That(result.IsSuccess, Is.True, "Per-archetype B+Tree should contain spawned entity's key");
        }
        finally
        {
            accessor.Dispose();
        }
    }

    [Test]
    public unsafe void Spawn_MultipleEntities_AllIndexed()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();
        for (int i = 0; i < 10; i++)
        {
            tx.Spawn<ClIdxUnit>(ClIdxUnit.Health.Set(new ClIdxHealth(i * 10, 100)));
        }
        tx.Commit();

        var cs = dbe._archetypeStates[Archetype<ClIdxUnit>.Metadata.ArchetypeId].ClusterState;
        Assert.That(cs.ActiveClusterCount, Is.GreaterThan(0));

        ref var field = ref cs.IndexSlots[0].Fields[0];
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var accessor = field.Index.Segment.CreateChunkAccessor();
        try
        {
            for (int i = 0; i < 10; i++)
            {
                int key = i * 10;
                var result = field.Index.TryGet(&key, ref accessor);
                Assert.That(result.IsSuccess, Is.True, $"Key {key} should be in per-archetype B+Tree");
            }
        }
        finally
        {
            accessor.Dispose();
        }
    }

    [Test]
    public unsafe void Destroy_RemovesFromPerArchetypeBTree()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<ClIdxUnit>(ClIdxUnit.Health.Set(new ClIdxHealth(42, 100)));
            tx.Commit();
        }

        // Verify entry exists before destroy
        var cs = dbe._archetypeStates[Archetype<ClIdxUnit>.Metadata.ArchetypeId].ClusterState;
        ref var field = ref cs.IndexSlots[0].Fields[0];
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var accessor = field.Index.Segment.CreateChunkAccessor();

        int key = 42;
        Assert.That(field.Index.TryGet(&key, ref accessor).IsSuccess, Is.True, "Entry should exist before destroy");
        accessor.Dispose();

        // Destroy
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(id);
            tx.Commit();
        }

        // Verify entry removed
        accessor = field.Index.Segment.CreateChunkAccessor();
        Assert.That(field.Index.TryGet(&key, ref accessor).IsSuccess, Is.False, "Entry should be removed after destroy");
        accessor.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Shadow capture & tick fence
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public unsafe void TickFence_FieldMutation_BTreeMoveExecuted()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<ClIdxUnit>(ClIdxUnit.Health.Set(new ClIdxHealth(10, 100)));
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        // Mutate Current from 10 → 20
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(id).Write(ClIdxUnit.Health) = new ClIdxHealth(20, 200);
            tx.Commit();
        }
        dbe.WriteTickFence(2);

        var cs = dbe._archetypeStates[Archetype<ClIdxUnit>.Metadata.ArchetypeId].ClusterState;
        ref var field = ref cs.IndexSlots[0].Fields[0];
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var accessor = field.Index.Segment.CreateChunkAccessor();
        try
        {
            int newKey = 20;
            int oldKey = 10;
            Assert.That(field.Index.TryGet(&newKey, ref accessor).IsSuccess, Is.True, "New key should be in B+Tree after tick fence");
            Assert.That(field.Index.TryGet(&oldKey, ref accessor).IsSuccess, Is.False, "Old key should be removed from B+Tree after tick fence");
        }
        finally
        {
            accessor.Dispose();
        }
    }

    [Test]
    public unsafe void TickFence_NoChange_NoMove()
    {
        using var dbe = SetupEngine();

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<ClIdxUnit>(ClIdxUnit.Health.Set(new ClIdxHealth(50, 100)));
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        // Write same value — should be no-op in shadow processing
        using (var tx = dbe.CreateQuickTransaction())
        {
            var e = tx.OpenMut(tx.Query<ClIdxUnit>().Execute().First());
            e.Write(ClIdxUnit.Health) = new ClIdxHealth(50, 100);
            tx.Commit();
        }
        dbe.WriteTickFence(2);

        var cs = dbe._archetypeStates[Archetype<ClIdxUnit>.Metadata.ArchetypeId].ClusterState;
        ref var field = ref cs.IndexSlots[0].Fields[0];
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var accessor = field.Index.Segment.CreateChunkAccessor();
        try
        {
            int key = 50;
            Assert.That(field.Index.TryGet(&key, ref accessor).IsSuccess, Is.True, "Key should still exist after no-change write");
        }
        finally
        {
            accessor.Dispose();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Query integration
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void TargetedQuery_ClusterArchetype_ReturnsCorrectEntities()
    {
        using var dbe = SetupEngine();

        EntityId id1, id2;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id1 = tx.Spawn<ClIdxUnit>(ClIdxUnit.Health.Set(new ClIdxHealth(100, 200)));
            tx.Spawn<ClIdxUnit>(ClIdxUnit.Health.Set(new ClIdxHealth(50, 200)));
            id2 = tx.Spawn<ClIdxUnit>(ClIdxUnit.Health.Set(new ClIdxHealth(200, 300)));
            tx.Commit();
        }

        using var tx2 = dbe.CreateQuickTransaction();
        // Range query: Current >= 100 should match id1 (100) and id2 (200), not the 50 entity
        var results = tx2.Query<ClIdxUnit>().WhereField<ClIdxHealth>(h => h.Current >= 100).Execute();
        Assert.That(results, Has.Count.EqualTo(2));
        Assert.That(results, Does.Contain(id1));
        Assert.That(results, Does.Contain(id2));
    }

    [Test]
    public void TargetedQuery_RangeFilter_ReturnsCorrectEntities()
    {
        using var dbe = SetupEngine();

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 20; i++)
            {
                tx.Spawn<ClIdxUnit>(ClIdxUnit.Health.Set(new ClIdxHealth(i * 10, 100)));
            }
            tx.Commit();
        }

        using var tx2 = dbe.CreateQuickTransaction();
        var results = tx2.Query<ClIdxUnit>().WhereField<ClIdxHealth>(h => h.Current >= 100 && h.Current <= 150).Execute();
        // Values: 100, 110, 120, 130, 140, 150 → 6 entities
        Assert.That(results, Has.Count.EqualTo(6));
    }

    [Test]
    public void TargetedQuery_Count_WorksForClusterArchetype()
    {
        using var dbe = SetupEngine();

        // Unique index on Current — each value must be distinct
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 10; i++)
            {
                tx.Spawn<ClIdxUnit>(ClIdxUnit.Health.Set(new ClIdxHealth(i * 10, 100)));
            }
            tx.Commit();
        }

        using var tx2 = dbe.CreateQuickTransaction();
        // Range queries on unique index
        Assert.That(tx2.Query<ClIdxUnit>().WhereField<ClIdxHealth>(h => h.Current >= 0 && h.Current <= 40).Count(), Is.EqualTo(5)); // 0,10,20,30,40
        Assert.That(tx2.Query<ClIdxUnit>().WhereField<ClIdxHealth>(h => h.Current >= 50).Count(), Is.EqualTo(5)); // 50,60,70,80,90
        Assert.That(tx2.Query<ClIdxUnit>().WhereField<ClIdxHealth>(h => h.Current == 999).Count(), Is.EqualTo(0));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Zone maps
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void ZoneMap_SpawnWidens_CorrectBounds()
    {
        using var dbe = SetupEngine();

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<ClIdxUnit>(ClIdxUnit.Health.Set(new ClIdxHealth(10, 100)));
            tx.Spawn<ClIdxUnit>(ClIdxUnit.Health.Set(new ClIdxHealth(50, 100)));
            tx.Spawn<ClIdxUnit>(ClIdxUnit.Health.Set(new ClIdxHealth(30, 100)));
            tx.Commit();
        }

        var cs = dbe._archetypeStates[Archetype<ClIdxUnit>.Metadata.ArchetypeId].ClusterState;
        var zoneMap = cs.IndexSlots[0].Fields[0].ZoneMap;
        Assert.That(zoneMap, Is.Not.Null, "Zone map should exist for indexed field");

        // All entities in same cluster (chunkId determined by ClaimSlot)
        int clusterChunkId = cs.ActiveClusterIds[0];
        Assert.That(zoneMap.MayContain(clusterChunkId, 10, 50), Is.True, "Range [10,50] should overlap cluster bounds");
        Assert.That(zoneMap.MayContain(clusterChunkId, 0, 5), Is.False, "Range [0,5] should NOT overlap cluster bounds [10,50]");
        Assert.That(zoneMap.MayContain(clusterChunkId, 60, 100), Is.False, "Range [60,100] should NOT overlap cluster bounds [10,50]");
    }

    [Test]
    public void ZoneMap_TickFenceRecomputes_AfterMutation()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<ClIdxUnit>(ClIdxUnit.Health.Set(new ClIdxHealth(10, 100)));
            tx.Spawn<ClIdxUnit>(ClIdxUnit.Health.Set(new ClIdxHealth(20, 100)));
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        // Mutate first entity: 10 → 5
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(id).Write(ClIdxUnit.Health) = new ClIdxHealth(5, 100);
            tx.Commit();
        }
        dbe.WriteTickFence(2);

        var cs = dbe._archetypeStates[Archetype<ClIdxUnit>.Metadata.ArchetypeId].ClusterState;
        var zoneMap = cs.IndexSlots[0].Fields[0].ZoneMap;
        int clusterChunkId = cs.ActiveClusterIds[0];

        // After recompute, min should be 5 (mutated), max should be 20
        Assert.That(zoneMap.MayContain(clusterChunkId, 5, 20), Is.True, "Range [5,20] should match after recompute");
        Assert.That(zoneMap.MayContain(clusterChunkId, 0, 4), Is.False, "Range [0,4] should NOT match after recompute");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Edge cases
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public unsafe void Write_ThenDestroy_SameTick_ShadowHandlesCorrectly()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<ClIdxUnit>(ClIdxUnit.Health.Set(new ClIdxHealth(77, 100)));
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        // Mutate then destroy — same tick
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(id).Write(ClIdxUnit.Health) = new ClIdxHealth(88, 0);
            tx.Commit();
        }
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(id);
            tx.Commit();
        }
        dbe.WriteTickFence(2);

        var cs = dbe._archetypeStates[Archetype<ClIdxUnit>.Metadata.ArchetypeId].ClusterState;
        ref var field = ref cs.IndexSlots[0].Fields[0];
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var accessor = field.Index.Segment.CreateChunkAccessor();
        try
        {
            int key77 = 77;
            int key88 = 88;
            Assert.That(field.Index.TryGet(&key77, ref accessor).IsSuccess, Is.False, "Old value should be removed");
            Assert.That(field.Index.TryGet(&key88, ref accessor).IsSuccess, Is.False, "Mutated value should be removed");
        }
        finally
        {
            accessor.Dispose();
        }
    }

    [Test]
    public void BulkSpawn_AllIndexed()
    {
        using var dbe = SetupEngine();

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 200; i++)
            {
                tx.Spawn<ClIdxUnit>(ClIdxUnit.Health.Set(new ClIdxHealth(i, 100)));
            }
            tx.Commit();
        }

        using var tx2 = dbe.CreateQuickTransaction();
        var all = tx2.Query<ClIdxUnit>().WhereField<ClIdxHealth>(h => h.Current >= 0).Execute();
        Assert.That(all, Has.Count.EqualTo(200));
    }

    [Test]
    public void EmptyCluster_TickFence_NoCrash()
    {
        using var dbe = SetupEngine();

        // Spawn and destroy everything — empty cluster state
        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<ClIdxUnit>(ClIdxUnit.Health.Set(new ClIdxHealth(10, 100)));
            tx.Commit();
        }
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(id);
            tx.Commit();
        }

        // Should not crash
        Assert.DoesNotThrow(() => dbe.WriteTickFence(1));
    }

    [Test]
    public void SharedBTree_ExcludesClusterArchetypeEntries()
    {
        using var dbe = SetupEngine();

        // Spawn entities in a cluster-eligible indexed archetype
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 5; i++)
            {
                tx.Spawn<ClIdxUnit>(ClIdxUnit.Health.Set(new ClIdxHealth(i, 100)));
            }
            tx.Commit();
        }

        // The shared ComponentTable B+Tree should have 0 entries from ClIdxUnit
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var table = dbe.GetComponentTable<ClIdxHealth>();
        var ifi = table.IndexedFieldInfos[0];
        Assert.That(ifi.PersistentIndex.EntryCount, Is.EqualTo(0),
            "Shared ComponentTable B+Tree should have 0 entries for cluster archetype (entries go to per-archetype tree)");
    }

    [Test]
    public void TargetedQuery_AfterMutation_ReturnsUpdatedResults()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<ClIdxUnit>(ClIdxUnit.Health.Set(new ClIdxHealth(1, 100)));
            tx.Spawn<ClIdxUnit>(ClIdxUnit.Health.Set(new ClIdxHealth(2, 100)));
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        // Mutate first entity from 1 → 2
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(id).Write(ClIdxUnit.Health) = new ClIdxHealth(2, 100);
            tx.Commit();
        }
        dbe.WriteTickFence(2);

        using var tx2 = dbe.CreateQuickTransaction();
        Assert.That(tx2.Query<ClIdxUnit>().WhereField<ClIdxHealth>(h => h.Current == 1).Count(), Is.EqualTo(0), "No entities with old value");
        Assert.That(tx2.Query<ClIdxUnit>().WhereField<ClIdxHealth>(h => h.Current == 2).Count(), Is.EqualTo(2), "Both entities now have value 2");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Code review — missing test paths
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Destroy_WithoutWrite_IndexEntryRemoved()
    {
        // Tests the non-shadow destroy path (no Write before Destroy)
        using var dbe = SetupEngine();

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<ClIdxUnit>(ClIdxUnit.Health.Set(new ClIdxHealth(99, 100)));
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        // Destroy without any Write — index entry should be removed directly by PrepareEcsDestroys
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(id);
            tx.Commit();
        }
        dbe.WriteTickFence(2);

        using var tx2 = dbe.CreateQuickTransaction();
        Assert.That(tx2.Query<ClIdxUnit>().WhereField<ClIdxHealth>(h => h.Current == 99).Count(), Is.EqualTo(0));
    }

    [Test]
    public void Destroy_MutateAndDestroy_SameTransaction()
    {
        // Tests mutation + destroy in same transaction (not just same tick)
        using var dbe = SetupEngine();

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<ClIdxUnit>(ClIdxUnit.Health.Set(new ClIdxHealth(33, 100)));
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        // Mutate AND destroy in same transaction
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(id).Write(ClIdxUnit.Health) = new ClIdxHealth(44, 200);
            tx.Destroy(id);
            tx.Commit();
        }
        dbe.WriteTickFence(2);

        using var tx2 = dbe.CreateQuickTransaction();
        Assert.That(tx2.Query<ClIdxUnit>().WhereField<ClIdxHealth>(h => h.Current == 33).Count(), Is.EqualTo(0), "Original value removed");
        Assert.That(tx2.Query<ClIdxUnit>().WhereField<ClIdxHealth>(h => h.Current == 44).Count(), Is.EqualTo(0), "Mutated value removed");
    }

    [Test]
    public void EqualityQuery_ExactMatch()
    {
        using var dbe = SetupEngine();

        EntityId target;
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<ClIdxUnit>(ClIdxUnit.Health.Set(new ClIdxHealth(10, 100)));
            target = tx.Spawn<ClIdxUnit>(ClIdxUnit.Health.Set(new ClIdxHealth(20, 100)));
            tx.Spawn<ClIdxUnit>(ClIdxUnit.Health.Set(new ClIdxHealth(30, 100)));
            tx.Commit();
        }

        using var tx2 = dbe.CreateQuickTransaction();
        var results = tx2.Query<ClIdxUnit>().WhereField<ClIdxHealth>(h => h.Current == 20).Execute();
        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results, Does.Contain(target));
    }

    [Test]
    public void BulkSpawn_MultipleCluster_ZoneMapPerCluster()
    {
        using var dbe = SetupEngine();

        // Spawn enough entities to span multiple clusters
        // ClIdxUnit has Position(8B) + Health(8B) → cluster size is large, so we need many entities
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 500; i++)
            {
                tx.Spawn<ClIdxUnit>(ClIdxUnit.Health.Set(new ClIdxHealth(i, 100)));
            }
            tx.Commit();
        }

        var cs = dbe._archetypeStates[Archetype<ClIdxUnit>.Metadata.ArchetypeId].ClusterState;
        Assert.That(cs.ActiveClusterCount, Is.GreaterThan(1), "Should span multiple clusters");

        var zoneMap = cs.IndexSlots[0].Fields[0].ZoneMap;

        // Each cluster's zone map should cover a subset of the total range [0, 499]
        // Verify at least one cluster doesn't cover the full range
        bool foundPartial = false;
        for (int c = 0; c < cs.ActiveClusterCount; c++)
        {
            int chunkId = cs.ActiveClusterIds[c];
            if (!zoneMap.MayContain(chunkId, 400, 499))
            {
                foundPartial = true;
                break;
            }
        }
        Assert.That(foundPartial, Is.True, "At least one cluster should not cover [400,499] — zone maps should be per-cluster");
    }

    [Test]
    public unsafe void PrepareEcsDestroys_ClusterEntity_DoesNotReadLegacyRecord()
    {
        // Regression test for CRITICAL bug: PrepareEcsDestroys must not call
        // EntityRecordAccessor.GetLocation on ClusterEntityRecord (19 bytes).
        using var dbe = SetupEngine();

        // Spawn multiple entities to exercise slot > 0
        var ids = new EntityId[10];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 10; i++)
            {
                ids[i] = tx.Spawn<ClIdxUnit>(ClIdxUnit.Health.Set(new ClIdxHealth(i * 100, 200)));
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        // Destroy all — should not crash or corrupt
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 10; i++)
            {
                tx.Destroy(ids[i]);
            }
            tx.Commit();
        }
        dbe.WriteTickFence(2);

        // All entries should be removed from per-archetype B+Tree
        var cs = dbe._archetypeStates[Archetype<ClIdxUnit>.Metadata.ArchetypeId].ClusterState;
        ref var field = ref cs.IndexSlots[0].Fields[0];
        Assert.That(field.Index.EntryCount, Is.EqualTo(0), "All B+Tree entries should be removed after destroying all entities");
    }
}
