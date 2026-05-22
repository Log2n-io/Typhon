using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ── Test components (SV, no indexes) ──────────────────────────────────
[Component("Typhon.Test.Cluster.Position", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct ClPosition
{
    public float X, Y;
    public ClPosition(float x, float y) { X = x; Y = y; }
}

[Component("Typhon.Test.Cluster.Movement", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct ClMovement
{
    public float VX, VY;
    public ClMovement(float vx, float vy) { VX = vx; VY = vy; }
}

[Archetype(520)]
partial class ClAnt : Archetype<ClAnt>
{
    public static readonly Comp<ClPosition> Position = Register<ClPosition>();
    public static readonly Comp<ClMovement> Movement = Register<ClMovement>();
}

// ── Versioned archetype for coexistence tests ──────────────────────────
[Component("Typhon.Test.Cluster.VHealth", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential)]
struct ClVHealth
{
    public int Current, Max;
    public ClVHealth(int cur, int max) { Current = cur; Max = max; }
}

[Archetype(521)]
partial class ClUnit : Archetype<ClUnit>
{
    public static readonly Comp<ClVHealth> Health = Register<ClVHealth>();
}

[TestFixture]
[NonParallelizable]
class ClusterStorageTests : TestBase<ClusterStorageTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        Archetype<ClAnt>.Touch();
        Archetype<ClUnit>.Touch();
    }

    private DatabaseEngine SetupClusterEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClPosition>();
        dbe.RegisterComponentFromAccessor<ClMovement>();
        dbe.RegisterComponentFromAccessor<ClVHealth>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Layout Tests
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void ClusterInfo_TwoSmallComponents_CorrectLayout()
    {
        // Position(8B) + Movement(8B), both SV
        var info = ArchetypeClusterInfo.Compute(2, [8, 8]);
        Assert.That(info.ClusterSize, Is.GreaterThanOrEqualTo(8));
        Assert.That(info.ClusterSize, Is.LessThanOrEqualTo(64));
        Assert.That(info.ClusterStride, Is.GreaterThan(0));
        Assert.That(info.HeaderSize, Is.EqualTo(8 + 8 * 2)); // OccupancyBits + 2 EnabledBits
        Assert.That(info.EntityIdsOffset, Is.EqualTo(info.HeaderSize));
        Assert.That(info.ComponentOffset(0), Is.GreaterThan(info.EntityIdsOffset));
        Assert.That(info.ComponentOffset(1), Is.GreaterThan(info.ComponentOffset(0)));
        Assert.That(info.ComponentSize(0), Is.EqualTo(8));
        Assert.That(info.ComponentSize(1), Is.EqualTo(8));
        Assert.That(info.FullMask, Is.EqualTo((info.ClusterSize == 64) ? ulong.MaxValue : (1UL << info.ClusterSize) - 1));
    }

    [Test]
    public void ClusterInfo_LargeComponents_SmallN()
    {
        // 4 components at 200B each = 800B per entity + 8B key = 808B
        // FixedHeader = 8 + 8*4 = 40
        // stride(8) = 40 + 808*8 = 6504 → fits
        // stride(9) = 40 + 808*9 = 7312 → fits
        // stride(10) = 40 + 808*10 = 8120 → too big
        var info = ArchetypeClusterInfo.Compute(4, [200, 200, 200, 200]);
        Assert.That(info.ClusterSize, Is.LessThanOrEqualTo(10));
        Assert.That(info.ClusterSize, Is.GreaterThanOrEqualTo(8));
    }

    [Test]
    public void ClusterInfo_TooLarge_Throws()
    {
        // Single 1000B component → stride(8) = 8+8*1 + (8+1000)*8 = 8080 > 8000
        Assert.Throws<InvalidOperationException>(() =>
            ArchetypeClusterInfo.Compute(1, [1000]));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Cluster Eligibility
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void ClusterEligible_SvArchetype_HasClusterState()
    {
        using var dbe = SetupClusterEngine();
        var meta = ArchetypeRegistry.GetMetadata<ClAnt>();
        Assert.That(meta.IsClusterEligible, Is.True);
        Assert.That(meta.ClusterLayout, Is.Not.Null);
        var es = dbe._archetypeStates[meta.ArchetypeId];
        Assert.That(es.ClusterState, Is.Not.Null);
    }

    [Test]
    public void ClusterEligible_VersionedArchetype_NoClusterState()
    {
        using var dbe = SetupClusterEngine();
        var meta = ArchetypeRegistry.GetMetadata<ClUnit>();
        Assert.That(meta.IsClusterEligible, Is.False);
        var es = dbe._archetypeStates[meta.ArchetypeId];
        Assert.That(es.ClusterState, Is.Null);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Spawn Tests
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Spawn_SingleEntity_ClusterCreated()
    {
        using var dbe = SetupClusterEngine();
        using var tx = dbe.CreateQuickTransaction();
        var pos = new ClPosition(10, 20);
        var mov = new ClMovement(1, 2);
        var id = tx.Spawn<ClAnt>(ClAnt.Position.Set(in pos), ClAnt.Movement.Set(in mov));
        tx.Commit();

        var es = dbe._archetypeStates[ArchetypeRegistry.GetMetadata<ClAnt>().ArchetypeId];
        Assert.That(es.ClusterState.ActiveClusterCount, Is.EqualTo(1));
    }

    [Test]
    public void TryGetClusterLayout_ResolvesLayoutByClusterSegmentRootPage()
    {
        using var dbe = SetupClusterEngine();
        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = new ClPosition(10, 20);
            var mov = new ClMovement(1, 2);
            tx.Spawn<ClAnt>(ClAnt.Position.Set(in pos), ClAnt.Movement.Set(in mov));
            tx.Commit();
        }

        var meta = ArchetypeRegistry.GetMetadata<ClAnt>();
        var clusterState = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        var rootPage = clusterState.ClusterSegment.RootPageIndex;

        var found = dbe.TryGetClusterLayout(rootPage, out var n, out var headerSize, out var componentCount, out var entityIdsOffset);

        Assert.That(found, Is.True);
        Assert.That(componentCount, Is.EqualTo(2), "ClAnt has Position + Movement");
        Assert.That(n, Is.EqualTo(clusterState.Layout.ClusterSize));
        Assert.That(headerSize, Is.EqualTo(8 + 8 * componentCount), "OccupancyBits + EnabledBits[C]");
        Assert.That(entityIdsOffset, Is.EqualTo(headerSize), "entity-id array begins right after the fixed header");
    }

    [Test]
    public void TryGetClusterLayout_NonClusterRootPage_ReturnsFalse()
    {
        using var dbe = SetupClusterEngine();
        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = new ClPosition(10, 20);
            var mov = new ClMovement(1, 2);
            tx.Spawn<ClAnt>(ClAnt.Position.Set(in pos), ClAnt.Movement.Set(in mov));
            tx.Commit();
        }

        // The occupancy segment root page is not a cluster segment.
        var occupancyRoot = -1;
        foreach (var seg in dbe.EnumerateStorageSegments())
        {
            if (seg.Kind == StorageSegmentKind.Occupancy)
            {
                occupancyRoot = seg.RootPageIndex;
                break;
            }
        }
        Assert.That(occupancyRoot, Is.GreaterThanOrEqualTo(0));

        Assert.That(dbe.TryGetClusterLayout(occupancyRoot, out _, out _, out _, out _), Is.False);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // A6 harvest summary accessors
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void TryGetClusterStats_ReportsEntityAndClusterCounts()
    {
        using var dbe = SetupClusterEngine();
        var meta = ArchetypeRegistry.GetMetadata<ClAnt>();
        int n = meta.ClusterLayout.ClusterSize;
        int spawnCount = n + 3; // one full cluster + a partial one ⇒ 2 active clusters

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < spawnCount; i++)
            {
                var pos = new ClPosition(i, 0);
                var mov = new ClMovement(0, 0);
                tx.Spawn<ClAnt>(ClAnt.Position.Set(in pos), ClAnt.Movement.Set(in mov));
            }
            tx.Commit();
        }

        var clusterState = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        var rootPage = clusterState.ClusterSegment.RootPageIndex;

        var found = dbe.TryGetClusterStats(rootPage, out var entityCount, out var activeClusters, out var clusterSize);

        Assert.That(found, Is.True);
        Assert.That(entityCount, Is.EqualTo(spawnCount));
        Assert.That(clusterSize, Is.EqualTo(n));
        Assert.That(activeClusters, Is.EqualTo(2));
        Assert.That(activeClusters, Is.EqualTo(clusterState.ActiveClusterCount));
    }

    [Test]
    public void TryGetClusterStats_NonClusterRootPage_ReturnsFalse()
    {
        using var dbe = SetupClusterEngine();
        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = new ClPosition(1, 2);
            var mov = new ClMovement(3, 4);
            tx.Spawn<ClAnt>(ClAnt.Position.Set(in pos), ClAnt.Movement.Set(in mov));
            tx.Commit();
        }

        Assert.That(dbe.TryGetClusterStats(-1, out _, out _, out _), Is.False);
    }

    [Test]
    public void TryGetEntityMapStats_ReportsEntryAndBucketCounts()
    {
        using var dbe = SetupClusterEngine();
        var meta = ArchetypeRegistry.GetMetadata<ClAnt>();
        int spawnCount = 50;

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < spawnCount; i++)
            {
                var pos = new ClPosition(i, 0);
                var mov = new ClMovement(0, 0);
                tx.Spawn<ClAnt>(ClAnt.Position.Set(in pos), ClAnt.Movement.Set(in mov));
            }
            tx.Commit();
        }

        var mapRoot = dbe._archetypeStates[meta.ArchetypeId].EntityMap.Segment.RootPageIndex;

        var found = dbe.TryGetEntityMapStats(mapRoot, out var stats);

        Assert.That(found, Is.True);
        Assert.That(stats.EntryCount, Is.EqualTo(spawnCount));
        Assert.That(stats.BucketCount, Is.GreaterThan(0));
        Assert.That(stats.LoadFactor, Is.GreaterThan(0.0));

        // The fill histogram partitions every primary bucket exactly once.
        var histogram = stats.FillEmpty + stats.FillQuarter + stats.FillHalf + stats.FillThreeQuarter + stats.FillFull;
        Assert.That(histogram, Is.EqualTo(stats.BucketCount));
    }

    [Test]
    public void TryGetEntityMapStats_NonEntityMapRootPage_ReturnsFalse()
    {
        using var dbe = SetupClusterEngine();
        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = new ClPosition(1, 2);
            var mov = new ClMovement(3, 4);
            tx.Spawn<ClAnt>(ClAnt.Position.Set(in pos), ClAnt.Movement.Set(in mov));
            tx.Commit();
        }

        Assert.That(dbe.TryGetEntityMapStats(-1, out _), Is.False);
    }

    [Test]
    public void TryGetHashMapLayout_ReportsWidthsCapacityAndNonDataChunks()
    {
        using var dbe = SetupClusterEngine();
        var meta = ArchetypeRegistry.GetMetadata<ClAnt>();

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 50; i++)
            {
                var pos = new ClPosition(i, 0);
                var mov = new ClMovement(0, 0);
                tx.Spawn<ClAnt>(ClAnt.Position.Set(in pos), ClAnt.Movement.Set(in mov));
            }
            tx.Commit();
        }

        var mapRoot = dbe._archetypeStates[meta.ArchetypeId].EntityMap.Segment.RootPageIndex;

        // Stride comes off the descriptor — the bucket-capacity denominator the renderer can't derive from a chunk alone.
        var stride = 0;
        foreach (var seg in dbe.EnumerateStorageSegments())
        {
            if (seg.RootPageIndex == mapRoot)
            {
                stride = seg.Stride;
                break;
            }
        }
        Assert.That(stride, Is.GreaterThan(0));

        var found = dbe.TryGetHashMapLayout(mapRoot, out var keyWidth, out var valueWidth, out var bucketCapacity, out var nonDataChunkIds);

        Assert.That(found, Is.True);
        Assert.That(keyWidth, Is.EqualTo(8), "EntityKey is a long");
        Assert.That(valueWidth, Is.GreaterThan(0));
        Assert.That(bucketCapacity, Is.EqualTo((stride - 12) / (keyWidth + valueWidth)), "capacity = (stride − header) / (key + value)");
        // The structural set is meta (chunk 0) plus every directory chunk — at least 2 ids, and chunk 0 must be present.
        Assert.That(nonDataChunkIds, Does.Contain(0), "the meta chunk is always chunk 0");
        Assert.That(nonDataChunkIds.Length, Is.GreaterThanOrEqualTo(2), "meta + at least one directory chunk");
    }

    [Test]
    public void TryGetHashMapLayout_NonEntityMapRootPage_ReturnsFalse()
    {
        using var dbe = SetupClusterEngine();
        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = new ClPosition(1, 2);
            var mov = new ClMovement(3, 4);
            tx.Spawn<ClAnt>(ClAnt.Position.Set(in pos), ClAnt.Movement.Set(in mov));
            tx.Commit();
        }

        Assert.That(dbe.TryGetHashMapLayout(-1, out _, out _, out _, out _), Is.False);
    }

    [Test]
    public void EnumerateStorageSegments_ClusterSegmentReportsChunkCounts()
    {
        using var dbe = SetupClusterEngine();
        var meta = ArchetypeRegistry.GetMetadata<ClAnt>();
        int spawnCount = meta.ClusterLayout.ClusterSize + 1; // 2 active clusters

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < spawnCount; i++)
            {
                var pos = new ClPosition(i, 0);
                var mov = new ClMovement(0, 0);
                tx.Spawn<ClAnt>(ClAnt.Position.Set(in pos), ClAnt.Movement.Set(in mov));
            }
            tx.Commit();
        }

        var rootPage = dbe._archetypeStates[meta.ArchetypeId].ClusterState.ClusterSegment.RootPageIndex;
        StorageSegmentDescriptor cluster = default;
        var found = false;
        foreach (var seg in dbe.EnumerateStorageSegments())
        {
            if (seg.RootPageIndex == rootPage)
            {
                cluster = seg;
                found = true;
                break;
            }
        }

        Assert.That(found, Is.True);
        Assert.That(cluster.IsChunkBased, Is.True);
        Assert.That(cluster.ChunkCapacity, Is.GreaterThan(0));
        Assert.That(cluster.AllocatedChunkCount, Is.GreaterThan(0));
        Assert.That(cluster.AllocatedChunkCount, Is.LessThanOrEqualTo(cluster.ChunkCapacity));
        Assert.That(cluster.FreeChunkCount, Is.EqualTo(cluster.ChunkCapacity - cluster.AllocatedChunkCount));
    }

    [Test]
    public void TryGetClusterComponentNames_ReturnsSlotOrderedNames()
    {
        using var dbe = SetupClusterEngine();
        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = new ClPosition(1, 2);
            var mov = new ClMovement(3, 4);
            tx.Spawn<ClAnt>(ClAnt.Position.Set(in pos), ClAnt.Movement.Set(in mov));
            tx.Commit();
        }

        var rootPage = dbe._archetypeStates[ArchetypeRegistry.GetMetadata<ClAnt>().ArchetypeId].ClusterState.ClusterSegment.RootPageIndex;

        var found = dbe.TryGetClusterComponentNames(rootPage, out var names);

        Assert.That(found, Is.True);
        Assert.That(names, Has.Length.EqualTo(2), "ClAnt has Position + Movement");
        Assert.That(names, Does.Contain("Typhon.Test.Cluster.Position"));
        Assert.That(names, Does.Contain("Typhon.Test.Cluster.Movement"));
    }

    [Test]
    public void TryGetClusterComponentNames_NonClusterRootPage_ReturnsFalse()
    {
        using var dbe = SetupClusterEngine();
        Assert.That(dbe.TryGetClusterComponentNames(-1, out _), Is.False);
    }

    [Test]
    public void Read_SpawnedEntity_CorrectData()
    {
        using var dbe = SetupClusterEngine();
        using var tx = dbe.CreateQuickTransaction();
        var pos = new ClPosition(42, 84);
        var mov = new ClMovement(7, 3);
        var id = tx.Spawn<ClAnt>(ClAnt.Position.Set(in pos), ClAnt.Movement.Set(in mov));
        tx.Commit();

        using var readTx = dbe.CreateQuickTransaction();
        var entity = readTx.Open(id);
        Assert.That(entity.IsValid, Is.True);
        ref readonly var readPos = ref entity.Read(ClAnt.Position);
        ref readonly var readMov = ref entity.Read(ClAnt.Movement);
        Assert.That(readPos.X, Is.EqualTo(42f));
        Assert.That(readPos.Y, Is.EqualTo(84f));
        Assert.That(readMov.VX, Is.EqualTo(7f));
        Assert.That(readMov.VY, Is.EqualTo(3f));
    }

    [Test]
    public void Write_Entity_DataPersisted()
    {
        using var dbe = SetupClusterEngine();
        using var tx = dbe.CreateQuickTransaction();
        var pos = new ClPosition(10, 20);
        var mov = new ClMovement(1, 2);
        var id = tx.Spawn<ClAnt>(ClAnt.Position.Set(in pos), ClAnt.Movement.Set(in mov));
        tx.Commit();

        // Write new position
        using var writeTx = dbe.CreateQuickTransaction();
        var entity = writeTx.OpenMut(id);
        ref var writePos = ref entity.Write(ClAnt.Position);
        writePos.X = 99;
        writePos.Y = 88;
        writeTx.Commit();

        // Read back
        using var verifyTx = dbe.CreateQuickTransaction();
        var verify = verifyTx.Open(id);
        ref readonly var verifyPos = ref verify.Read(ClAnt.Position);
        Assert.That(verifyPos.X, Is.EqualTo(99f));
        Assert.That(verifyPos.Y, Is.EqualTo(88f));
    }

    [Test]
    public void Spawn_FillOneCluster_AllReadable()
    {
        using var dbe = SetupClusterEngine();
        var meta = ArchetypeRegistry.GetMetadata<ClAnt>();
        int N = meta.ClusterLayout.ClusterSize;

        using var tx = dbe.CreateQuickTransaction();
        var ids = new EntityId[N];
        for (int i = 0; i < N; i++)
        {
            var pos = new ClPosition(i, i * 10);
            var mov = new ClMovement(i * 0.1f, i * 0.2f);
            ids[i] = tx.Spawn<ClAnt>(ClAnt.Position.Set(in pos), ClAnt.Movement.Set(in mov));
        }
        tx.Commit();

        // Verify all entities
        using var readTx = dbe.CreateQuickTransaction();
        for (int i = 0; i < N; i++)
        {
            var entity = readTx.Open(ids[i]);
            Assert.That(entity.IsValid, Is.True, $"Entity {i} not valid");
            ref readonly var readPos = ref entity.Read(ClAnt.Position);
            Assert.That(readPos.X, Is.EqualTo((float)i), $"Entity {i} Position.X");
            Assert.That(readPos.Y, Is.EqualTo(i * 10f), $"Entity {i} Position.Y");
        }

        var es = dbe._archetypeStates[meta.ArchetypeId];
        Assert.That(es.ClusterState.ActiveClusterCount, Is.EqualTo(1));
    }

    [Test]
    public void Spawn_Overflow_SecondCluster()
    {
        using var dbe = SetupClusterEngine();
        var meta = ArchetypeRegistry.GetMetadata<ClAnt>();
        int N = meta.ClusterLayout.ClusterSize;

        using var tx = dbe.CreateQuickTransaction();
        for (int i = 0; i < N + 1; i++)
        {
            var pos = new ClPosition(i, 0);
            var mov = new ClMovement(0, 0);
            tx.Spawn<ClAnt>(ClAnt.Position.Set(in pos), ClAnt.Movement.Set(in mov));
        }
        tx.Commit();

        var es = dbe._archetypeStates[meta.ArchetypeId];
        Assert.That(es.ClusterState.ActiveClusterCount, Is.EqualTo(2));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Destroy Tests
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Destroy_Entity_OccupancyCleared()
    {
        using var dbe = SetupClusterEngine();
        using var tx = dbe.CreateQuickTransaction();
        var pos = new ClPosition(1, 2);
        var mov = new ClMovement(3, 4);
        var id1 = tx.Spawn<ClAnt>(ClAnt.Position.Set(in pos), ClAnt.Movement.Set(in mov));
        var id2 = tx.Spawn<ClAnt>(ClAnt.Position.Set(in pos), ClAnt.Movement.Set(in mov));
        tx.Commit();

        using var dtx = dbe.CreateQuickTransaction();
        dtx.Destroy(id1);
        dtx.Commit();

        // id2 should still be readable
        using var readTx = dbe.CreateQuickTransaction();
        var entity2 = readTx.Open(id2);
        Assert.That(entity2.IsValid, Is.True);
    }

    [Test]
    public void Destroy_AllInCluster_ClusterFreed()
    {
        using var dbe = SetupClusterEngine();
        using var tx = dbe.CreateQuickTransaction();
        var pos = new ClPosition(1, 2);
        var mov = new ClMovement(3, 4);
        var id = tx.Spawn<ClAnt>(ClAnt.Position.Set(in pos), ClAnt.Movement.Set(in mov));
        tx.Commit();

        using var dtx = dbe.CreateQuickTransaction();
        dtx.Destroy(id);
        dtx.Commit();

        var es = dbe._archetypeStates[ArchetypeRegistry.GetMetadata<ClAnt>().ArchetypeId];
        Assert.That(es.ClusterState.ActiveClusterCount, Is.EqualTo(0));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Iteration Tests (ClusterRef + ClusterEnumerator)
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Iteration_AllEntities_ProcessedOnce()
    {
        using var dbe = SetupClusterEngine();
        int entityCount = 100;
        using var tx = dbe.CreateQuickTransaction();
        for (int i = 0; i < entityCount; i++)
        {
            var pos = new ClPosition(i, i);
            var mov = new ClMovement(0, 0);
            tx.Spawn<ClAnt>(ClAnt.Position.Set(in pos), ClAnt.Movement.Set(in mov));
        }
        tx.Commit();

        // Iterate via clusters
        using var readTx = dbe.CreateQuickTransaction();
        var accessor = readTx.For<ClAnt>();
        Assert.That(accessor.HasClusterStorage, Is.True);

        int count = 0;
        foreach (var cluster in accessor.GetClusterEnumerator())
        {
            count += cluster.LiveCount;
        }
        accessor.Dispose();

        Assert.That(count, Is.EqualTo(entityCount));
    }

    [Test]
    public void Iteration_WithHoles_SkipsEmpty()
    {
        using var dbe = SetupClusterEngine();
        var meta = ArchetypeRegistry.GetMetadata<ClAnt>();
        int N = meta.ClusterLayout.ClusterSize;
        int spawnCount = N; // Fill one cluster exactly

        using var tx = dbe.CreateQuickTransaction();
        var ids = new EntityId[spawnCount];
        for (int i = 0; i < spawnCount; i++)
        {
            var pos = new ClPosition(i, 0);
            var mov = new ClMovement(0, 0);
            ids[i] = tx.Spawn<ClAnt>(ClAnt.Position.Set(in pos), ClAnt.Movement.Set(in mov));
        }
        tx.Commit();

        // Destroy half the entities
        using var dtx = dbe.CreateQuickTransaction();
        for (int i = 0; i < spawnCount; i += 2)
        {
            dtx.Destroy(ids[i]);
        }
        dtx.Commit();

        // Iterate — should see only surviving entities
        using var readTx = dbe.CreateQuickTransaction();
        var accessor = readTx.For<ClAnt>();
        int count = 0;
        float sum = 0;
        foreach (var cluster in accessor.GetClusterEnumerator())
        {
            var positions = cluster.GetReadOnlySpan<ClPosition>(ClAnt.Position);
            ulong bits = cluster.OccupancyBits;
            while (bits != 0)
            {
                int idx = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                sum += positions[idx].X;
                count++;
            }
        }
        accessor.Dispose();

        int expectedCount = spawnCount / 2; // odd indices survive
        Assert.That(count, Is.EqualTo(expectedCount));
    }

    [Test]
    public void Iteration_ClusterRef_SpanAccess()
    {
        using var dbe = SetupClusterEngine();
        using var tx = dbe.CreateQuickTransaction();
        int entityCount = 10;
        for (int i = 0; i < entityCount; i++)
        {
            var pos = new ClPosition(i * 10, i * 20);
            var mov = new ClMovement(i, i * 2);
            tx.Spawn<ClAnt>(ClAnt.Position.Set(in pos), ClAnt.Movement.Set(in mov));
        }
        tx.Commit();

        using var readTx = dbe.CreateQuickTransaction();
        var accessor = readTx.For<ClAnt>();
        float sumX = 0;
        foreach (var cluster in accessor.GetClusterEnumerator())
        {
            var positions = cluster.GetReadOnlySpan<ClPosition>(ClAnt.Position);
            ulong bits = cluster.OccupancyBits;
            while (bits != 0)
            {
                int idx = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                sumX += positions[idx].X;
            }
        }
        accessor.Dispose();

        // Sum of 0, 10, 20, ..., 90 = 450
        Assert.That(sumX, Is.EqualTo(450f));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Random Access Tests
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void RandomAccess_ViaArchetypeAccessor_CorrectData()
    {
        using var dbe = SetupClusterEngine();
        using var tx = dbe.CreateQuickTransaction();
        var pos = new ClPosition(123, 456);
        var mov = new ClMovement(7, 8);
        var id = tx.Spawn<ClAnt>(ClAnt.Position.Set(in pos), ClAnt.Movement.Set(in mov));
        tx.Commit();

        using var readTx = dbe.CreateQuickTransaction();
        var accessor = readTx.For<ClAnt>();
        var entity = accessor.Open(id);
        Assert.That(entity.IsValid, Is.True);
        ref readonly var rp = ref entity.Read(ClAnt.Position);
        Assert.That(rp.X, Is.EqualTo(123f));
        Assert.That(rp.Y, Is.EqualTo(456f));
        ref readonly var rm = ref entity.Read(ClAnt.Movement);
        Assert.That(rm.VX, Is.EqualTo(7f));
        Assert.That(rm.VY, Is.EqualTo(8f));
        accessor.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Coexistence Tests
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void MixedArchetypes_ClusterAndLegacy_BothWork()
    {
        using var dbe = SetupClusterEngine();

        // Spawn SV entity (cluster storage)
        using var tx1 = dbe.CreateQuickTransaction();
        var pos = new ClPosition(10, 20);
        var mov = new ClMovement(1, 2);
        var antId = tx1.Spawn<ClAnt>(ClAnt.Position.Set(in pos), ClAnt.Movement.Set(in mov));
        tx1.Commit();

        // Spawn Versioned entity (legacy storage)
        using var tx2 = dbe.CreateQuickTransaction();
        var health = new ClVHealth(100, 100);
        var unitId = tx2.Spawn<ClUnit>(ClUnit.Health.Set(in health));
        tx2.Commit();

        // Both should be readable
        using var readTx = dbe.CreateQuickTransaction();
        var ant = readTx.Open(antId);
        Assert.That(ant.IsValid, Is.True);
        Assert.That(ant.Read(ClAnt.Position).X, Is.EqualTo(10f));

        var unit = readTx.Open(unitId);
        Assert.That(unit.IsValid, Is.True);
        Assert.That(unit.Read(ClUnit.Health).Current, Is.EqualTo(100));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Edge Cases
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void EmptyEngine_NoActiveClusters()
    {
        using var dbe = SetupClusterEngine();
        using var readTx = dbe.CreateQuickTransaction();
        var accessor = readTx.For<ClAnt>();
        int count = 0;
        foreach (var cluster in accessor.GetClusterEnumerator())
        {
            count += cluster.LiveCount;
        }
        accessor.Dispose();
        Assert.That(count, Is.EqualTo(0));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Bug regression tests
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Bug1_EcsQuery_ClusterArchetype_ReturnsCorrectData()
    {
        using var dbe = SetupClusterEngine();
        using var tx = dbe.CreateQuickTransaction();
        var pos = new ClPosition(42, 84);
        var mov = new ClMovement(7, 3);
        var id = tx.Spawn<ClAnt>(ClAnt.Position.Set(in pos), ClAnt.Movement.Set(in mov));
        tx.Commit();

        // Query should find the entity and the entity should be readable
        using var qTx = dbe.CreateQuickTransaction();
        var result = qTx.Query<ClAnt>().Execute();
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result, Does.Contain(id));

        // foreach iteration should return correct data
        foreach (var entity in qTx.Query<ClAnt>())
        {
            ref readonly var rp = ref entity.Read(ClAnt.Position);
            Assert.That(rp.X, Is.EqualTo(42f));
            Assert.That(rp.Y, Is.EqualTo(84f));
            ref readonly var rm = ref entity.Read(ClAnt.Movement);
            Assert.That(rm.VX, Is.EqualTo(7f));
            Assert.That(rm.VY, Is.EqualTo(3f));
        }
    }

    [Test]
    public void Bug2_TryRead_ClusterEntity_ReturnsCorrectValue()
    {
        using var dbe = SetupClusterEngine();
        using var tx = dbe.CreateQuickTransaction();
        var pos = new ClPosition(55, 66);
        var mov = new ClMovement(1, 2);
        var id = tx.Spawn<ClAnt>(ClAnt.Position.Set(in pos), ClAnt.Movement.Set(in mov));
        tx.Commit();

        using var readTx = dbe.CreateQuickTransaction();
        var entity = readTx.Open(id);
        bool found = entity.TryRead<ClPosition>(out var value);
        Assert.That(found, Is.True);
        Assert.That(value.X, Is.EqualTo(55f));
        Assert.That(value.Y, Is.EqualTo(66f));
    }

    [Test]
    public void Bug3_Transaction_MultipleOpens_SameArchetype_DataCorrect()
    {
        // Verifies the cluster accessor cache in Transaction.ResolveEntity works across multiple Opens
        using var dbe = SetupClusterEngine();
        using var tx = dbe.CreateQuickTransaction();
        var ids = new EntityId[10];
        for (int i = 0; i < 10; i++)
        {
            var pos = new ClPosition(i * 10, i * 20);
            var mov = new ClMovement(i, i * 2);
            ids[i] = tx.Spawn<ClAnt>(ClAnt.Position.Set(in pos), ClAnt.Movement.Set(in mov));
        }
        tx.Commit();

        // Open all 10 entities in a single transaction — exercises cluster cache reuse
        using var readTx = dbe.CreateQuickTransaction();
        for (int i = 0; i < 10; i++)
        {
            var entity = readTx.Open(ids[i]);
            Assert.That(entity.IsValid, Is.True, $"Entity {i} not valid");
            ref readonly var rp = ref entity.Read(ClAnt.Position);
            Assert.That(rp.X, Is.EqualTo(i * 10f), $"Entity {i} Position.X wrong");
            Assert.That(rp.Y, Is.EqualTo(i * 20f), $"Entity {i} Position.Y wrong");
        }
    }

    [Test]
    public void Issue4_EnableDisable_VisibleInClusterIteration()
    {
        using var dbe = SetupClusterEngine();
        using var tx = dbe.CreateQuickTransaction();
        var pos = new ClPosition(1, 2);
        var mov = new ClMovement(3, 4);
        var id = tx.Spawn<ClAnt>(ClAnt.Position.Set(in pos), ClAnt.Movement.Set(in mov));
        tx.Commit();

        // Disable Movement component
        using var disableTx = dbe.CreateQuickTransaction();
        var entity = disableTx.OpenMut(id);
        entity.Disable(ClAnt.Movement);
        disableTx.Commit();

        // Cluster iteration filtering by Movement enabled should find 0 entities
        using var readTx = dbe.CreateQuickTransaction();
        var accessor = readTx.For<ClAnt>();
        int countWithMovement = 0;
        var meta = ArchetypeRegistry.GetMetadata<ClAnt>();
        byte movSlot = meta.GetSlot(ClAnt.Movement._componentTypeId);
        foreach (var cluster in accessor.GetClusterEnumerator())
        {
            ulong active = cluster.OccupancyBits & cluster.EnabledBits(movSlot);
            countWithMovement += BitOperations.PopCount(active);
        }
        accessor.Dispose();

        Assert.That(countWithMovement, Is.EqualTo(0), "Disabled component should not appear in cluster EnabledBits");
    }

    [Test]
    public void Issue6_BulkSpawn_LargeCount_CompletesQuickly()
    {
        using var dbe = SetupClusterEngine();
        var meta = ArchetypeRegistry.GetMetadata<ClAnt>();
        int entityCount = 5000;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var tx = dbe.CreateQuickTransaction();
        for (int i = 0; i < entityCount; i++)
        {
            var pos = new ClPosition(i, 0);
            var mov = new ClMovement(0, 0);
            tx.Spawn<ClAnt>(ClAnt.Position.Set(in pos), ClAnt.Movement.Set(in mov));
        }
        tx.Commit();
        sw.Stop();

        // Should complete well under 1 second (quadratic bug would make it slow)
        Assert.That(sw.ElapsedMilliseconds, Is.LessThan(5000), "Bulk spawn took too long — possible quadratic behavior");

        // Verify all entities are accessible
        using var readTx = dbe.CreateQuickTransaction();
        var accessor = readTx.For<ClAnt>();
        int count = 0;
        foreach (var cluster in accessor.GetClusterEnumerator())
        {
            count += cluster.LiveCount;
        }
        accessor.Dispose();
        Assert.That(count, Is.EqualTo(entityCount));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Batch and scale tests
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void BatchSpawn_MultipleClustersFilled()
    {
        using var dbe = SetupClusterEngine();
        var meta = ArchetypeRegistry.GetMetadata<ClAnt>();
        int N = meta.ClusterLayout.ClusterSize;
        int entityCount = N * 3 + 5; // 3 full clusters + partial

        using var tx = dbe.CreateQuickTransaction();
        for (int i = 0; i < entityCount; i++)
        {
            var pos = new ClPosition(i, 0);
            var mov = new ClMovement(0, 0);
            tx.Spawn<ClAnt>(ClAnt.Position.Set(in pos), ClAnt.Movement.Set(in mov));
        }
        tx.Commit();

        var es = dbe._archetypeStates[meta.ArchetypeId];
        Assert.That(es.ClusterState.ActiveClusterCount, Is.EqualTo(4)); // 3 full + 1 partial

        // Verify iteration finds all entities
        using var readTx = dbe.CreateQuickTransaction();
        var accessor = readTx.For<ClAnt>();
        int count = 0;
        foreach (var cluster in accessor.GetClusterEnumerator())
        {
            count += cluster.LiveCount;
        }
        accessor.Dispose();
        Assert.That(count, Is.EqualTo(entityCount));
    }
}
