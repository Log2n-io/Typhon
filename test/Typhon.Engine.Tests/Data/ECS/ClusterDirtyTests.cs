using System;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

[TestFixture]
[NonParallelizable]
class ClusterDirtyTests : TestBase<ClusterDirtyTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        Archetype<ClAnt>.Touch();
        Archetype<ClUnit>.Touch();
    }

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClPosition>();
        dbe.RegisterComponentFromAccessor<ClMovement>();
        dbe.RegisterComponentFromAccessor<ClVHealth>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Dirty Tracking Tests
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Write_SetsClusterDirtyBitmap()
    {
        using var dbe = SetupEngine();
        var clusterState = dbe._archetypeStates[Archetype<ClAnt>.Metadata.ArchetypeId].ClusterState;
        Assert.That(clusterState, Is.Not.Null, "ClAnt should use cluster storage");
        Assert.That(clusterState.ClusterDirtyBitmap.HasDirty, Is.False, "Initially no dirty bits");

        EntityId e1;
        using (var tx = dbe.CreateQuickTransaction())
        {
            e1 = tx.Spawn<ClAnt>(ClAnt.Position.Set(new ClPosition(1, 2)), ClAnt.Movement.Set(new ClMovement(3, 4)));
            tx.Commit();
        }

        // Clear spawn-time page dirty (ClusterDirtyBitmap may have been set by spawn page marking)
        clusterState.ClusterDirtyBitmap.Snapshot();

        using (var tx = dbe.CreateQuickTransaction())
        {
            ref var pos = ref tx.OpenMut(e1).Write(ClAnt.Position);
            pos.X = 99f;
            tx.Commit();
        }

        Assert.That(clusterState.ClusterDirtyBitmap.HasDirty, Is.True, "After Write, ClusterDirtyBitmap should have dirty bits");
    }

    [Test]
    public void Write_MultipleSameCluster_AllDirtyBitsSet()
    {
        using var dbe = SetupEngine();
        var clusterState = dbe._archetypeStates[Archetype<ClAnt>.Metadata.ArchetypeId].ClusterState;

        var ids = new EntityId[5];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 5; i++)
            {
                ids[i] = tx.Spawn<ClAnt>(ClAnt.Position.Set(new ClPosition(i, 0)), ClAnt.Movement.Set(new ClMovement(0, 0)));
            }
            tx.Commit();
        }

        clusterState.ClusterDirtyBitmap.Snapshot(); // clear

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 5; i++)
            {
                tx.OpenMut(ids[i]).Write(ClAnt.Position).X = i * 10f;
            }
            tx.Commit();
        }

        var snapshot = clusterState.ClusterDirtyBitmap.Snapshot();
        int dirtyCount = 0;
        for (int i = 0; i < snapshot.Length; i++)
        {
            dirtyCount += BitOperations.PopCount((ulong)snapshot[i]);
        }

        Assert.That(dirtyCount, Is.EqualTo(5), "All 5 written entities should have dirty bits");
    }

    [Test]
    public void Write_SameEntityTwice_SingleDirtyBit()
    {
        using var dbe = SetupEngine();
        var clusterState = dbe._archetypeStates[Archetype<ClAnt>.Metadata.ArchetypeId].ClusterState;

        EntityId e1;
        using (var tx = dbe.CreateQuickTransaction())
        {
            e1 = tx.Spawn<ClAnt>(ClAnt.Position.Set(new ClPosition(1, 2)), ClAnt.Movement.Set(new ClMovement(0, 0)));
            tx.Commit();
        }

        clusterState.ClusterDirtyBitmap.Snapshot(); // clear

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(e1).Write(ClAnt.Position).X = 10f;
            tx.OpenMut(e1).Write(ClAnt.Position).X = 20f; // second write
            tx.Commit();
        }

        var snapshot = clusterState.ClusterDirtyBitmap.Snapshot();
        int dirtyCount = 0;
        for (int i = 0; i < snapshot.Length; i++)
        {
            dirtyCount += BitOperations.PopCount((ulong)snapshot[i]);
        }

        Assert.That(dirtyCount, Is.EqualTo(1), "Same entity written twice should produce single dirty bit");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Tick Fence Tests
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void TickFence_SnapshotClears_DirtyBitmap()
    {
        using var dbe = SetupEngine();
        var clusterState = dbe._archetypeStates[Archetype<ClAnt>.Metadata.ArchetypeId].ClusterState;

        EntityId e1;
        using (var tx = dbe.CreateQuickTransaction())
        {
            e1 = tx.Spawn<ClAnt>(ClAnt.Position.Set(new ClPosition(1, 2)), ClAnt.Movement.Set(new ClMovement(0, 0)));
            tx.Commit();
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(e1).Write(ClAnt.Position).X = 42f;
            tx.Commit();
        }

        Assert.That(clusterState.ClusterDirtyBitmap.HasDirty, Is.True, "Should be dirty before tick fence");
        dbe.WriteTickFence(1);
        Assert.That(clusterState.ClusterDirtyBitmap.HasDirty, Is.False, "ClusterDirtyBitmap should be cleared after WriteTickFence");
    }

    [Test]
    public void TickFence_PropagatesDirtyToComponentTable()
    {
        using var dbe = SetupEngine();
        var posTable = dbe.GetComponentTable<ClPosition>();

        EntityId e1;
        using (var tx = dbe.CreateQuickTransaction())
        {
            e1 = tx.Spawn<ClAnt>(ClAnt.Position.Set(new ClPosition(1, 2)), ClAnt.Movement.Set(new ClMovement(0, 0)));
            tx.Commit();
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(e1).Write(ClAnt.Position).X = 42f;
            tx.Commit();
        }

        dbe.WriteTickFence(1);

        Assert.That(posTable.PreviousTickHadDirtyEntities, Is.True,
            "PreviousTickHadDirtyEntities should be propagated from cluster dirty bitmap");
    }

    [Test]
    public void TickFence_NoDirtyEntities_NoFalsePositive()
    {
        using var dbe = SetupEngine();
        var posTable = dbe.GetComponentTable<ClPosition>();

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<ClAnt>(ClAnt.Position.Set(new ClPosition(1, 2)), ClAnt.Movement.Set(new ClMovement(0, 0)));
            tx.Commit();
        }

        // First tick fence clears any spawn noise
        dbe.WriteTickFence(1);

        // No writes → no dirty entities
        dbe.WriteTickFence(2);

        Assert.That(posTable.PreviousTickHadDirtyEntities, Is.False,
            "No writes means no dirty entities — change filter should not fire");
    }

    // Reopen tests (ClusterSegmentLoaded, EntitiesReadable, SpawnAfterReopen) are covered by
    // TickFenceE2ETests which use the E2E scope-based infrastructure needed for database reopen.
    // Tests TickFence_SV_WriteAndReopen_DataSurvives et al. validate cluster segment persistence.

    // ═══════════════════════════════════════════════════════════════════════
    // Coexistence Tests
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void DirtyTracking_ClusterAndLegacy_Independent()
    {
        using var dbe = SetupEngine();

        var clusterState = dbe._archetypeStates[Archetype<ClAnt>.Metadata.ArchetypeId].ClusterState;
        var healthTable = dbe.GetComponentTable<ClVHealth>();

        Assert.That(clusterState, Is.Not.Null, "ClAnt should use cluster storage");
        // ClUnit has Versioned component → NOT cluster-eligible
        Assert.That(dbe._archetypeStates[Archetype<ClUnit>.Metadata.ArchetypeId]?.ClusterState, Is.Null,
            "ClUnit should NOT use cluster storage (Versioned component)");

        EntityId antId, unitId;
        using (var tx = dbe.CreateQuickTransaction())
        {
            antId = tx.Spawn<ClAnt>(ClAnt.Position.Set(new ClPosition(1, 1)), ClAnt.Movement.Set(new ClMovement(0, 0)));
            unitId = tx.Spawn<ClUnit>(ClUnit.Health.Set(new ClVHealth(100, 100)));
            tx.Commit();
        }

        clusterState.ClusterDirtyBitmap.Snapshot(); // clear

        // Write to cluster entity
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(antId).Write(ClAnt.Position).X = 99f;
            tx.Commit();
        }

        Assert.That(clusterState.ClusterDirtyBitmap.HasDirty, Is.True, "Cluster entity should be dirty");

        // Write to Versioned entity (non-cluster) — dirty tracking is separate
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(unitId).Write(ClUnit.Health).Current = 50;
            tx.Commit();
        }

        // Both should have their respective dirty state
        Assert.That(clusterState.ClusterDirtyBitmap.HasDirty, Is.True, "Cluster dirty should persist");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Edge Cases
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Destroy_ThenTickFence_NoStaleEntry()
    {
        using var dbe = SetupEngine();
        var clusterState = dbe._archetypeStates[Archetype<ClAnt>.Metadata.ArchetypeId].ClusterState;

        EntityId e1;
        using (var tx = dbe.CreateQuickTransaction())
        {
            e1 = tx.Spawn<ClAnt>(ClAnt.Position.Set(new ClPosition(1, 2)), ClAnt.Movement.Set(new ClMovement(0, 0)));
            tx.Commit();
        }

        // Write then destroy in separate transaction
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(e1).Write(ClAnt.Position).X = 42f;
            tx.Commit();
        }

        // Entity is dirty from write
        Assert.That(clusterState.ClusterDirtyBitmap.HasDirty, Is.True);

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(e1);
            tx.Commit();
        }

        // Tick fence should process without errors (destroyed entity's cluster slot is cleared by ReleaseSlot)
        Assert.DoesNotThrow(() => dbe.WriteTickFence(1));
    }

    [Test]
    public void EmptyCluster_NoDirtyBits_Skipped()
    {
        using var dbe = SetupEngine();
        var clusterState = dbe._archetypeStates[Archetype<ClAnt>.Metadata.ArchetypeId].ClusterState;

        // Spawn and immediately clear dirty bits
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<ClAnt>(ClAnt.Position.Set(new ClPosition(0, 0)), ClAnt.Movement.Set(new ClMovement(0, 0)));
            tx.Commit();
        }

        clusterState.ClusterDirtyBitmap.Snapshot(); // clear

        // No writes → tick fence should be a no-op
        Assert.That(clusterState.ClusterDirtyBitmap.HasDirty, Is.False);
        dbe.WriteTickFence(1);
        // Just verify no crash
    }

    [Test]
    [CancelAfter(5000)]
    public void BulkSpawn_WriteAll_AllDirtyBitsSerialized()
    {
        using var dbe = SetupEngine();
        var clusterState = dbe._archetypeStates[Archetype<ClAnt>.Metadata.ArchetypeId].ClusterState;

        const int count = 500;
        var ids = new EntityId[count];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < count; i++)
            {
                ids[i] = tx.Spawn<ClAnt>(ClAnt.Position.Set(new ClPosition(i, 0)), ClAnt.Movement.Set(new ClMovement(0, 0)));
            }
            tx.Commit();
        }

        clusterState.ClusterDirtyBitmap.Snapshot(); // clear spawn noise

        // Write all entities
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < count; i++)
            {
                tx.OpenMut(ids[i]).Write(ClAnt.Position).X = i * 10f;
            }
            tx.Commit();
        }

        var snapshot = clusterState.ClusterDirtyBitmap.Snapshot();
        int dirtyCount = 0;
        for (int i = 0; i < snapshot.Length; i++)
        {
            dirtyCount += BitOperations.PopCount((ulong)snapshot[i]);
        }

        Assert.That(dirtyCount, Is.EqualTo(count), $"All {count} written entities should have dirty bits");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Bug regression: destroy + reallocation + tick fence
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Destroy_ClusterFreed_Realloc_TickFence_SerializesNewData()
    {
        using var dbe = SetupEngine();
        var clusterState = dbe._archetypeStates[Archetype<ClAnt>.Metadata.ArchetypeId].ClusterState;
        int clusterSize = clusterState.Layout.ClusterSize;

        // Fill exactly one cluster so it can be freed when all entities are destroyed
        var ids = new EntityId[clusterSize];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < clusterSize; i++)
            {
                ids[i] = tx.Spawn<ClAnt>(ClAnt.Position.Set(new ClPosition(i, 0)), ClAnt.Movement.Set(new ClMovement(0, 0)));
            }
            tx.Commit();
        }

        // Write all entities (sets dirty bits)
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < clusterSize; i++)
            {
                tx.OpenMut(ids[i]).Write(ClAnt.Position).X = 100f + i;
            }
            tx.Commit();
        }

        // Destroy all entities — cluster is freed, dirty bits remain
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < clusterSize; i++)
            {
                tx.Destroy(ids[i]);
            }
            tx.Commit();
        }

        // Spawn new entities — may reuse the freed cluster
        var newIds = new EntityId[clusterSize];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < clusterSize; i++)
            {
                newIds[i] = tx.Spawn<ClAnt>(ClAnt.Position.Set(new ClPosition(i * 10, 0)), ClAnt.Movement.Set(new ClMovement(0, 0)));
            }
            tx.Commit();
        }

        // Write new entities
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < clusterSize; i++)
            {
                tx.OpenMut(newIds[i]).Write(ClAnt.Position).X = 200f + i;
            }
            tx.Commit();
        }

        // Tick fence should serialize only the NEW entities' data, not stale old data
        Assert.DoesNotThrow(() => dbe.WriteTickFence(1));

        // Verify new entities have correct data
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < clusterSize; i++)
            {
                var pos = tx.Open(newIds[i]).Read(ClAnt.Position);
                Assert.That(pos.X, Is.EqualTo(200f + i), $"New entity {i} should have correct data after tick fence");
            }
        }
    }

    [Test]
    public void Write_ThenDestroy_SameTick_OccupancyMasked()
    {
        using var dbe = SetupEngine();
        var clusterState = dbe._archetypeStates[Archetype<ClAnt>.Metadata.ArchetypeId].ClusterState;

        EntityId e1, e2;
        using (var tx = dbe.CreateQuickTransaction())
        {
            e1 = tx.Spawn<ClAnt>(ClAnt.Position.Set(new ClPosition(1, 0)), ClAnt.Movement.Set(new ClMovement(0, 0)));
            e2 = tx.Spawn<ClAnt>(ClAnt.Position.Set(new ClPosition(2, 0)), ClAnt.Movement.Set(new ClMovement(0, 0)));
            tx.Commit();
        }

        clusterState.ClusterDirtyBitmap.Snapshot(); // clear

        // Write both, then destroy e1 in the same tick
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(e1).Write(ClAnt.Position).X = 99f;
            tx.OpenMut(e2).Write(ClAnt.Position).X = 88f;
            tx.Commit();
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(e1);
            tx.Commit();
        }

        // Tick fence: e1 is dirty but destroyed (occupancy cleared) → should be skipped
        // e2 is dirty and alive → should be serialized
        Assert.DoesNotThrow(() => dbe.WriteTickFence(1));

        // e2 should still be readable and correct
        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = tx.Open(e2).Read(ClAnt.Position);
            Assert.That(pos.X, Is.EqualTo(88f), "Surviving entity should have correct data");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Concurrent writes + tick fence
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(5000)]
    public void ConcurrentWrites_ThenTickFence_AllDirtyProcessed()
    {
        using var dbe = SetupEngine();
        var clusterState = dbe._archetypeStates[Archetype<ClAnt>.Metadata.ArchetypeId].ClusterState;

        const int entityCount = 200;
        var ids = new EntityId[entityCount];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < entityCount; i++)
            {
                ids[i] = tx.Spawn<ClAnt>(ClAnt.Position.Set(new ClPosition(i, 0)), ClAnt.Movement.Set(new ClMovement(0, 0)));
            }
            tx.Commit();
        }

        clusterState.ClusterDirtyBitmap.Snapshot(); // clear

        // Concurrent writes from 4 threads, disjoint slices
        const int threadCount = 4;
        int perThread = entityCount / threadCount;
        var barrier = new System.Threading.Barrier(threadCount);
        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
        var tasks = new System.Threading.Tasks.Task[threadCount];

        for (int t = 0; t < threadCount; t++)
        {
            int threadId = t;
            tasks[t] = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    barrier.SignalAndWait();
                    using var tx = dbe.CreateQuickTransaction();
                    int start = threadId * perThread;
                    for (int i = 0; i < perThread; i++)
                    {
                        tx.OpenMut(ids[start + i]).Write(ClAnt.Position).X = threadId * 1000f + i;
                    }
                    tx.Commit();
                }
                catch (Exception ex) { errors.Add($"Thread {threadId}: {ex}"); }
            });
        }

        System.Threading.Tasks.Task.WaitAll(tasks);
        Assert.That(errors, Is.Empty, () => $"Concurrent writes failed: {string.Join("; ", errors)}");

        // All entities should be dirty
        Assert.That(clusterState.ClusterDirtyBitmap.HasDirty, Is.True);

        // Tick fence processes all dirty entities
        Assert.DoesNotThrow(() => dbe.WriteTickFence(1));

        // Bitmap should be cleared after tick fence
        Assert.That(clusterState.ClusterDirtyBitmap.HasDirty, Is.False, "Bitmap cleared after tick fence");

        // Verify values are correct
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int t = 0; t < threadCount; t++)
            {
                int start = t * perThread;
                for (int i = 0; i < perThread; i++)
                {
                    var pos = tx.Open(ids[start + i]).Read(ClAnt.Position);
                    Assert.That(pos.X, Is.EqualTo(t * 1000f + i), $"Entity [{start + i}] value mismatch");
                }
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Sparse dirty bits across multiple clusters
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void SparseDirty_OnlyDirtyClusters_Serialized()
    {
        using var dbe = SetupEngine();
        var clusterState = dbe._archetypeStates[Archetype<ClAnt>.Metadata.ArchetypeId].ClusterState;
        int clusterSize = clusterState.Layout.ClusterSize;

        // Spawn enough entities to fill 3 clusters
        int totalEntities = clusterSize * 3;
        var ids = new EntityId[totalEntities];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < totalEntities; i++)
            {
                ids[i] = tx.Spawn<ClAnt>(ClAnt.Position.Set(new ClPosition(i, 0)), ClAnt.Movement.Set(new ClMovement(0, 0)));
            }
            tx.Commit();
        }

        clusterState.ClusterDirtyBitmap.Snapshot(); // clear

        // Write only 1 entity in cluster 1 and 1 entity in cluster 3 (skip cluster 2)
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(ids[0]).Write(ClAnt.Position).X = 999f;                          // cluster 1
            tx.OpenMut(ids[clusterSize * 2]).Write(ClAnt.Position).X = 888f;             // cluster 3
            tx.Commit();
        }

        var snapshot = clusterState.ClusterDirtyBitmap.Snapshot();
        int dirtyCount = 0;
        for (int i = 0; i < snapshot.Length; i++)
        {
            dirtyCount += BitOperations.PopCount((ulong)snapshot[i]);
        }

        Assert.That(dirtyCount, Is.EqualTo(2), "Only 2 entities should be dirty (1 in cluster 1, 1 in cluster 3)");
    }

    [Test]
    public void MultipleTickFences_Sequential_CorrectState()
    {
        using var dbe = SetupEngine();
        var clusterState = dbe._archetypeStates[Archetype<ClAnt>.Metadata.ArchetypeId].ClusterState;

        EntityId e1;
        using (var tx = dbe.CreateQuickTransaction())
        {
            e1 = tx.Spawn<ClAnt>(ClAnt.Position.Set(new ClPosition(0, 0)), ClAnt.Movement.Set(new ClMovement(0, 0)));
            tx.Commit();
        }

        // Tick 1: write + fence
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(e1).Write(ClAnt.Position).X = 10f;
            tx.Commit();
        }
        dbe.WriteTickFence(1);
        Assert.That(clusterState.ClusterDirtyBitmap.HasDirty, Is.False, "Cleared after tick 1");

        // Tick 2: no writes + fence (should be no-op)
        dbe.WriteTickFence(2);
        Assert.That(clusterState.ClusterDirtyBitmap.HasDirty, Is.False, "Still clean after tick 2");

        // Tick 3: write again + fence
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(e1).Write(ClAnt.Position).X = 20f;
            tx.Commit();
        }
        Assert.That(clusterState.ClusterDirtyBitmap.HasDirty, Is.True, "Dirty after tick 3 write");
        dbe.WriteTickFence(3);
        Assert.That(clusterState.ClusterDirtyBitmap.HasDirty, Is.False, "Cleared after tick 3");

        // Verify final value
        using (var tx = dbe.CreateQuickTransaction())
        {
            Assert.That(tx.Open(e1).Read(ClAnt.Position).X, Is.EqualTo(20f));
        }
    }
}
