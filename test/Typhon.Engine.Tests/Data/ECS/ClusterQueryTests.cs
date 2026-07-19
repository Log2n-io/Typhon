using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ═══════════════════════════════════════════════════════════════════════
// Test components — SV with [Index] fields for cluster eligibility
// ═══════════════════════════════════════════════════════════════════════

[Component("Typhon.Test.ClQ.Stats", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct ClQStats
{
    [Index(AllowMultiple = true)]
    public int Score;

    [Field]
    public int Level;

    public ClQStats(int score, int level)
    {
        Score = score;
        Level = level;
    }
}

[Component("Typhon.Test.ClQ.Tag", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct ClQTag
{
    [Field]
    public long Id;

    [Field]
    public long Pad;

    public ClQTag(long id)
    {
        Id = id;
        Pad = 0;
    }
}

[Archetype]
partial class ClQUnit : Archetype<ClQUnit>
{
    public static readonly Comp<ClQStats> Stats = Register<ClQStats>();
    public static readonly Comp<ClQTag> Tag = Register<ClQTag>();
}

// Non-cluster archetype for mixed tests (Versioned component makes it non-cluster)
[Component("Typhon.Test.ClQ.VData", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential)]
struct ClQVData
{
    [Field]
    public int Value;

    [Field]
    public int Pad;
}

[Component("Typhon.Test.ClQ.NCStats", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct ClQNCStats
{
    [Index]
    public int Score;

    [Field]
    public int Pad;

    public ClQNCStats(int score)
    {
        Score = score;
        Pad = 0;
    }
}

[Archetype]
partial class ClQNonCluster : Archetype<ClQNonCluster>
{
    public static readonly Comp<ClQNCStats> NCStats = Register<ClQNCStats>();
    public static readonly Comp<ClQVData> VData = Register<ClQVData>();
}

// Float-indexed component for zone map sign-flip tests (M8)
[Component("Typhon.Test.ClQ.FloatData", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct ClQFloatData
{
    [Index(AllowMultiple = true)]
    public float Value;

    [Field]
    public int Pad;

    public ClQFloatData(float value)
    {
        Value = value;
        Pad = 0;
    }
}

[Archetype]
partial class ClQFloatUnit : Archetype<ClQFloatUnit>
{
    public static readonly Comp<ClQFloatData> Data = Register<ClQFloatData>();
}

// ═══════════════════════════════════════════════════════════════════════
// Tests
// ═══════════════════════════════════════════════════════════════════════

[NonParallelizable]
[TestFixture]
class ClusterQueryTests : TestBase<ClusterQueryTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClQStats>();
        dbe.RegisterComponentFromAccessor<ClQTag>();
        dbe.RegisterComponentFromAccessor<ClQNCStats>();
        dbe.RegisterComponentFromAccessor<ClQVData>();
        dbe.RegisterComponentFromAccessor<ClQFloatData>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    // ═══════════════════════════════════════════════════════════════
    // ViewRegistry Fix (Step 1)
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void IncrementalView_ClusterEntityFieldChange_StaysInView()
    {
        using var dbe = SetupEngine();
        var meta = ArchetypeRegistry.GetMetadata<ClQUnit>();
        Assert.That(meta.IsClusterEligible, Is.True, "Archetype must be cluster-eligible for this test");

        // Spawn entity with Score=100 (inside view range)
        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var stats = new ClQStats(100, 1);
            var tag = new ClQTag(42);
            id = tx.Spawn<ClQUnit>(ClQUnit.Stats.Set(in stats), ClQUnit.Tag.Set(in tag));
            tx.Commit();
        }

        // Verify entity exists via plain query first
        using var verifyTx = dbe.CreateQuickTransaction();
        var queryResult = verifyTx.Query<ClQUnit>().Execute();
        Assert.That(queryResult, Has.Count.EqualTo(1), "Entity should be queryable");

        // Create pull view for all ClQUnit entities — entity should be present
        using var viewTx = dbe.CreateQuickTransaction();
        using var view = viewTx.Query<ClQUnit>().ToView();
        Assert.That(view.Count, Is.EqualTo(1), "Entity should be in initial view");

        // Mutate Score (100 → 200)
        using (var tx = dbe.CreateQuickTransaction())
        {
            ref var s = ref tx.OpenMut(id).Write(ClQUnit.Stats);
            s.Score = 200;
            tx.Commit();
        }

        dbe.WriteTickFence(1);

        // Refresh — entity should still be in view
        using var refreshTx = dbe.CreateQuickTransaction();
        view.Refresh(refreshTx);

        Assert.That(view.Count, Is.EqualTo(1), "Entity should still be in view after mutation");
    }

    [Test]
    public void IncrementalView_ClusterEntityBoundaryCrossing_AddedDetected()
    {
        using var dbe = SetupEngine();

        // Spawn entity with Score=30 (outside view range)
        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var stats = new ClQStats(30, 1);
            var tag = new ClQTag(1);
            id = tx.Spawn<ClQUnit>(ClQUnit.Stats.Set(in stats), ClQUnit.Tag.Set(in tag));
            tx.Commit();
        }

        // Create view for Score >= 50
        using var viewTx = dbe.CreateQuickTransaction();
        using var view = viewTx.Query<ClQUnit>().WhereField<ClQStats>(s => s.Score >= 50).ToView();
        Assert.That(view.Count, Is.EqualTo(0), "Entity with Score=30 should not be in view");

        // Clear spawn shadows
        dbe.WriteTickFence(1);

        // Mutate Score (30 → 60, crosses into view range)
        using (var tx = dbe.CreateQuickTransaction())
        {
            ref var s = ref tx.OpenMut(id).Write(ClQUnit.Stats);
            s.Score = 60;
            tx.Commit();
        }

        dbe.WriteTickFence(2);

        using var refreshTx = dbe.CreateQuickTransaction();
        view.Refresh(refreshTx);

        Assert.That(view.Count, Is.EqualTo(1), "Entity should now be in view after crossing threshold");
        Assert.That(view.Added, Has.Count.EqualTo(1), "Entity should appear in Added list");
        Assert.That(view.Added[0], Is.EqualTo(id));
    }

    [Test]
    public void IncrementalView_ClusterEntitySpawn_DetectedByPullRefresh()
    {
        using var dbe = SetupEngine();

        // Create pull view (no WhereField → pull mode) for all ClQUnit entities
        using var viewTx = dbe.CreateQuickTransaction();
        using var view = viewTx.Query<ClQUnit>().ToView();
        Assert.That(view.Count, Is.EqualTo(0));

        // Spawn entity
        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var stats = new ClQStats(80, 1);
            var tag = new ClQTag(1);
            id = tx.Spawn<ClQUnit>(ClQUnit.Stats.Set(in stats), ClQUnit.Tag.Set(in tag));
            tx.Commit();
        }

        dbe.WriteTickFence(1);

        using var refreshTx = dbe.CreateQuickTransaction();
        view.Refresh(refreshTx);

        Assert.That(view.Count, Is.EqualTo(1), "Spawned entity should be in view after pull refresh");
        Assert.That(view.Added, Has.Count.EqualTo(1));
        Assert.That(view.Added[0], Is.EqualTo(id));
    }

    // ═══════════════════════════════════════════════════════════════
    // Zone Map Pruning (Step 3)
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void ZoneMapPrune_AllOutOfRange_EmptyResult()
    {
        using var dbe = SetupEngine();

        // Spawn 200 entities all with Score=100
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 200; i++)
            {
                var stats = new ClQStats(100, 1);
                var tag = new ClQTag(i);
                tx.Spawn<ClQUnit>(ClQUnit.Stats.Set(in stats), ClQUnit.Tag.Set(in tag));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);

        // Query for Score < 10 — all clusters should be pruned by zone map
        using var tx2 = dbe.CreateQuickTransaction();
        var result = tx2.Query<ClQUnit>().WhereField<ClQStats>(s => s.Score < 10).Execute();
        Assert.That(result, Is.Empty, "No entities match Score < 10");
    }

    [Test]
    public void ZoneMapPrune_SomeClustersSkipped_CorrectResult()
    {
        using var dbe = SetupEngine();

        // Spawn entities with varying scores: 10, 20, ..., 1000
        var ids = new EntityId[100];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 100; i++)
            {
                var stats = new ClQStats((i + 1) * 10, 1);
                var tag = new ClQTag(i);
                ids[i] = tx.Spawn<ClQUnit>(ClQUnit.Stats.Set(in stats), ClQUnit.Tag.Set(in tag));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);

        // Query for Score <= 50 — should match entities with scores 10, 20, 30, 40, 50
        using var tx2 = dbe.CreateQuickTransaction();
        var result = tx2.Query<ClQUnit>().WhereField<ClQStats>(s => s.Score <= 50).Execute();
        Assert.That(result, Has.Count.EqualTo(5));
    }

    [Test]
    public void ZoneMapPrune_EqualityQuery_CorrectResult()
    {
        using var dbe = SetupEngine();

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 50; i++)
            {
                var stats = new ClQStats(i * 10, 1);
                var tag = new ClQTag(i);
                tx.Spawn<ClQUnit>(ClQUnit.Stats.Set(in stats), ClQUnit.Tag.Set(in tag));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);

        // Equality query — zone map should narrow down to clusters containing value 200
        using var tx2 = dbe.CreateQuickTransaction();
        var result = tx2.Query<ClQUnit>().WhereField<ClQStats>(s => s.Score == 200).Execute();
        Assert.That(result, Has.Count.EqualTo(1));
    }

    // ═══════════════════════════════════════════════════════════════
    // Path A / B / Planner (Steps 4-5)
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void SelectiveQuery_FewResults_MatchesBruteForce()
    {
        using var dbe = SetupEngine();

        // Spawn 500 entities with Score = 1..500
        var allIds = new EntityId[500];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 500; i++)
            {
                var stats = new ClQStats(i + 1, 1);
                var tag = new ClQTag(i);
                allIds[i] = tx.Spawn<ClQUnit>(ClQUnit.Stats.Set(in stats), ClQUnit.Tag.Set(in tag));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);

        // Selective query: Score == 42 (1 out of 500 = 0.2% selectivity → should use Path A)
        using var tx2 = dbe.CreateQuickTransaction();
        var result = tx2.Query<ClQUnit>().WhereField<ClQStats>(s => s.Score == 42).Execute();

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result.Contains(allIds[41]), Is.True); // Score = 42 is index 41
    }

    [Test]
    public void BroadQuery_ManyResults_CorrectCount()
    {
        using var dbe = SetupEngine();

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 200; i++)
            {
                var stats = new ClQStats(i, 1);
                var tag = new ClQTag(i);
                tx.Spawn<ClQUnit>(ClQUnit.Stats.Set(in stats), ClQUnit.Tag.Set(in tag));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);

        // Broad query: Score >= 100 (100 out of 200 = 50% → Path B with zone map pruning)
        using var tx2 = dbe.CreateQuickTransaction();
        var result = tx2.Query<ClQUnit>().WhereField<ClQStats>(s => s.Score >= 100).Execute();
        Assert.That(result, Has.Count.EqualTo(100));
    }

    // ═══════════════════════════════════════════════════════════════
    // Edge Cases
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void SpawnAndQuerySameTx_PendingSpawnsIncluded()
    {
        using var dbe = SetupEngine();

        using var tx = dbe.CreateQuickTransaction();
        var stats = new ClQStats(99, 1);
        var tag = new ClQTag(1);
        var id = tx.Spawn<ClQUnit>(ClQUnit.Stats.Set(in stats), ClQUnit.Tag.Set(in tag));

        // Query in same transaction — pending spawn should be included
        var result = tx.Query<ClQUnit>().Execute();
        Assert.That(result.Contains(id), Is.True, "Pending spawn should be visible in same-tx query");
    }

    [Test]
    public void DestroyedEntity_NotInQueryResults()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var stats = new ClQStats(100, 1);
            var tag = new ClQTag(1);
            id = tx.Spawn<ClQUnit>(ClQUnit.Stats.Set(in stats), ClQUnit.Tag.Set(in tag));
            tx.Commit();
        }

        dbe.WriteTickFence(1);

        // Destroy the entity
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(id);
            tx.Commit();
        }

        // Query should not return the destroyed entity
        using var tx2 = dbe.CreateQuickTransaction();
        var result = tx2.Query<ClQUnit>().WhereField<ClQStats>(s => s.Score >= 50).Execute();
        Assert.That(result.Contains(id), Is.False, "Destroyed entity should not appear in query results");
    }

    [Test]
    public void EmptyArchetype_NoResults()
    {
        using var dbe = SetupEngine();

        dbe.WriteTickFence(1);

        using var tx = dbe.CreateQuickTransaction();
        var result = tx.Query<ClQUnit>().WhereField<ClQStats>(s => s.Score >= 0).Execute();
        Assert.That(result, Is.Empty);
    }

    [Test]
    public void BulkSpawn_500Entities_AllQueryable()
    {
        using var dbe = SetupEngine();

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 500; i++)
            {
                var stats = new ClQStats(i, i / 10);
                var tag = new ClQTag(i);
                tx.Spawn<ClQUnit>(ClQUnit.Stats.Set(in stats), ClQUnit.Tag.Set(in tag));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);

        using var tx2 = dbe.CreateQuickTransaction();
        var all = tx2.Query<ClQUnit>().Execute();
        Assert.That(all, Has.Count.EqualTo(500));

        var rangeResult = tx2.Query<ClQUnit>().WhereField<ClQStats>(s => s.Score >= 200 && s.Score < 300).Execute();
        Assert.That(rangeResult, Has.Count.EqualTo(100));
    }

    // ═══════════════════════════════════════════════════════════════
    // Cluster Iteration
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void ScopedClusterEnumerator_CorrectRange()
    {
        using var dbe = SetupEngine();

        // Spawn enough entities to have multiple clusters
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 200; i++)
            {
                var stats = new ClQStats(i, 1);
                var tag = new ClQTag(i);
                tx.Spawn<ClQUnit>(ClQUnit.Stats.Set(in stats), ClQUnit.Tag.Set(in tag));
            }

            tx.Commit();
        }

        var meta = ArchetypeRegistry.GetMetadata<ClQUnit>();
        var es = dbe._archetypeStates[meta.ArchetypeId];
        var cs = es.ClusterState;

        Assert.That(cs.ActiveClusterCount, Is.GreaterThan(1), "Need multiple clusters for range test");

        // Full enumerator — count all entities
        int fullCount = 0;
        using var tx2 = dbe.CreateQuickTransaction();
        var accessor = tx2.For<ClQUnit>();
        foreach (var cluster in accessor.GetClusterEnumerator())
        {
            fullCount += cluster.LiveCount;
        }

        Assert.That(fullCount, Is.EqualTo(200));

        // Scoped enumerator — first half of clusters
        int halfPoint = cs.ActiveClusterCount / 2;
        int scopedCount = 0;
        var scoped = ClusterEnumerator<ClQUnit>.CreateScoped(cs, meta, cs.ClusterSegment, cs.TransientSegment, 0, halfPoint);
        try
        {
            while (scoped.MoveNext())
            {
                scopedCount += scoped.Current.LiveCount;
            }
        }
        finally
        {
            scoped.Dispose();
        }

        Assert.That(scopedCount, Is.GreaterThan(0));
        Assert.That(scopedCount, Is.LessThan(fullCount), "Scoped enumerator should cover partial range");
    }

    [Test]
    public void ClusterRangeEntityView_AllEntitiesProcessed_NoDuplicates()
    {
        using var dbe = SetupEngine();

        var spawnedIds = new HashSet<EntityId>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 200; i++)
            {
                var stats = new ClQStats(i, 1);
                var tag = new ClQTag(i);
                spawnedIds.Add(tx.Spawn<ClQUnit>(ClQUnit.Stats.Set(in stats), ClQUnit.Tag.Set(in tag)));
            }

            tx.Commit();
        }

        var meta = ArchetypeRegistry.GetMetadata<ClQUnit>();
        var es = dbe._archetypeStates[meta.ArchetypeId];
        var cs = es.ClusterState;

        // ClusterRangeEntityView needs epoch scope — use a transaction context
        using var tx2 = dbe.CreateQuickTransaction();

        var rangeView = new ClusterRangeEntityView();
        rangeView.Reset(cs, cs.ClusterSegment, cs.ActiveClusterIds, 0, cs.ActiveClusterCount);

        var iteratedIds = new HashSet<EntityId>();
        foreach (var entityId in rangeView)
        {
            Assert.That(iteratedIds.Add(entityId), Is.True, $"Duplicate EntityId: {entityId}");
        }

        rangeView.Dispose();

        Assert.That(iteratedIds.Count, Is.EqualTo(200), "All entities should be iterated");
        Assert.That(iteratedIds.SetEquals(spawnedIds), Is.True, "Iterated entities should match spawned entities");
    }

    // ═══════════════════════════════════════════════════════════════
    // Change Filter Fix (Step 2)
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void ChangeFilter_ClusterEntityDirty_PreviousTickDirtySnapshotSet()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var stats = new ClQStats(100, 1);
            var tag = new ClQTag(1);
            id = tx.Spawn<ClQUnit>(ClQUnit.Stats.Set(in stats), ClQUnit.Tag.Set(in tag));
            tx.Commit();
        }

        // First tick fence: clears spawn state
        dbe.WriteTickFence(1);

        var meta = ArchetypeRegistry.GetMetadata<ClQUnit>();
        var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;

        // After first tick with no mutations, snapshot should be null (no dirty entities)
        Assert.That(cs.PreviousTickDirtySnapshot, Is.Null, "No dirty entities after first fence with no mutations");

        // Mutate entity
        using (var tx = dbe.CreateQuickTransaction())
        {
            ref var s = ref tx.OpenMut(id).Write(ClQUnit.Stats);
            s.Score = 999;
            tx.Commit();
        }

        // Second tick fence: should capture dirty snapshot
        dbe.WriteTickFence(2);

        Assert.That(cs.PreviousTickDirtySnapshot, Is.Not.Null, "Dirty snapshot should be set after mutation");

        // Verify the dirty bitmap has at least one set bit
        bool hasDirty = false;
        for (int i = 0; i < cs.PreviousTickDirtySnapshot.Length; i++)
        {
            if (cs.PreviousTickDirtySnapshot[i] != 0)
            {
                hasDirty = true;
                break;
            }
        }

        Assert.That(hasDirty, Is.True, "Dirty snapshot should have at least one dirty entity");
    }

    [Test]
    public void ChangeFilter_ClusterEntityClean_SnapshotCleared()
    {
        using var dbe = SetupEngine();

        using (var tx = dbe.CreateQuickTransaction())
        {
            var stats = new ClQStats(100, 1);
            var tag = new ClQTag(1);
            tx.Spawn<ClQUnit>(ClQUnit.Stats.Set(in stats), ClQUnit.Tag.Set(in tag));
            tx.Commit();
        }

        // Two tick fences with no mutations between them
        dbe.WriteTickFence(1);
        dbe.WriteTickFence(2);

        var meta = ArchetypeRegistry.GetMetadata<ClQUnit>();
        var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;

        Assert.That(cs.PreviousTickDirtySnapshot, Is.Null, "Snapshot should be null when no entities were dirty");
    }

    // ═══════════════════════════════════════════════════════════════
    // Float Zone Map (M8: sign-flip encoding correctness)
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void ZoneMapPrune_Float_PositiveRange_CorrectResult()
    {
        using var dbe = SetupEngine();

        // Spawn entities with float values: 1.0, 2.0, ..., 100.0
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 100; i++)
            {
                var data = new ClQFloatData((i + 1) * 1.0f);
                tx.Spawn<ClQFloatUnit>(ClQFloatUnit.Data.Set(in data));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);

        using var tx2 = dbe.CreateQuickTransaction();
        var result = tx2.Query<ClQFloatUnit>().WhereField<ClQFloatData>(d => d.Value <= 10.0f).Execute();
        Assert.That(result, Has.Count.EqualTo(10));
    }

    [Test]
    public void ZoneMapPrune_Float_NegativeValues_CorrectResult()
    {
        using var dbe = SetupEngine();

        var meta = ArchetypeRegistry.GetMetadata<ClQFloatUnit>();
        Assert.That(meta.IsClusterEligible, Is.True, "Float archetype must be cluster-eligible");
        Assert.That(meta.HasClusterIndexes, Is.True, "Float archetype must have cluster indexes");

        // Spawn entities with negative floats: -50.0, -49.0, ..., 49.0
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 100; i++)
            {
                var data = new ClQFloatData(i - 50.0f);
                tx.Spawn<ClQFloatUnit>(ClQFloatUnit.Data.Set(in data));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);

        // Verify all 100 are queryable first
        using var txAll = dbe.CreateQuickTransaction();
        var all = txAll.Query<ClQFloatUnit>().Execute();
        Assert.That(all, Has.Count.EqualTo(100), "All 100 entities should be queryable");

        // Query for non-negative values: Value >= 0
        using var tx2 = dbe.CreateQuickTransaction();
        var geResult = tx2.Query<ClQFloatUnit>().WhereField<ClQFloatData>(d => d.Value >= 0.0f).Execute();
        Assert.That(geResult, Has.Count.EqualTo(50), "Should match entities with values 0..49");

        // Query for negative values: Value < 0
        using var tx3 = dbe.CreateQuickTransaction();
        var ltResult = tx3.Query<ClQFloatUnit>().WhereField<ClQFloatData>(d => d.Value < 0.0f).Execute();
        Assert.That(ltResult, Has.Count.EqualTo(50), "Should match entities with values -50..-1");
    }

    // ═══════════════════════════════════════════════════════════════
    // Empty Range (L3: ClusterRangeEntityView with 0 clusters)
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void ClusterRangeEntityView_EmptyRange_NoEntities()
    {
        using var dbe = SetupEngine();

        // Spawn some entities so the archetype has clusters
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 10; i++)
            {
                var stats = new ClQStats(i, 1);
                var tag = new ClQTag(i);
                tx.Spawn<ClQUnit>(ClQUnit.Stats.Set(in stats), ClQUnit.Tag.Set(in tag));
            }

            tx.Commit();
        }

        var meta = ArchetypeRegistry.GetMetadata<ClQUnit>();
        var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;

        using var tx2 = dbe.CreateQuickTransaction();

        // Empty range: start == end
        var view = new ClusterRangeEntityView();
        view.Reset(cs, cs.ClusterSegment, cs.ActiveClusterIds, 0, 0);

        int count = 0;
        foreach (var _ in view)
        {
            count++;
        }

        view.Dispose();

        Assert.That(count, Is.EqualTo(0), "Empty range should yield no entities");
    }
}
