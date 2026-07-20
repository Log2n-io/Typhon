using System;
using System.Collections.Concurrent;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// Stress tests for SV/Transient/Mixed storage modes under concurrency.
/// Validates DirtyBitmap correctness, tick-fence serialization, transient heap contention,
/// and mixed-mode writes crossing epoch refresh boundaries.
/// </summary>
class StorageModeStressTests : TestBase<StorageModeStressTests>
{
    private const int ThreadCount = 4;
    private const int EntitiesPerThread = 50;

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CompSmSingleVersion>();
        dbe.RegisterComponentFromAccessor<CompSmTransient>();
        dbe.RegisterComponentFromAccessor<CompSmVersioned>();
        dbe.RegisterComponentFromAccessor<CompSmVersionedMix>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Category 1: Concurrent SV writes via ECS — DirtyBitmap correctness
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void SV_ConcurrentWrite_DirtyBitmapCorrectness()
    {
        using var dbe = SetupEngine();
        var table = dbe.GetComponentTable<CompSmSingleVersion>();

        // Spawn all entities in a single transaction (Spawn is not designed for concurrent calls)
        int totalEntities = ThreadCount * EntitiesPerThread;
        var allIds = new EntityId[totalEntities];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < totalEntities; i++)
            {
                var comp = new CompSmSingleVersion(i);
                allIds[i] = tx.Spawn<SvTestArchetype>(SvTestArchetype.SvComp.Set(in comp));
            }
            tx.Commit();
        }

        // Clear any dirty bits from spawn
        var clusterState = dbe._archetypeStates[Archetype<SvTestArchetype>.Metadata.ArchetypeId]?.ClusterState;
        if (clusterState != null)
        {
            clusterState.ClusterDirtyBitmap.Snapshot();
        }
        else
        {
            table.DirtyBitmap.Snapshot();
        }

        // Each thread writes its own disjoint slice — no write-write conflicts
        var errors = new ConcurrentBag<string>();
        var barrier = new Barrier(ThreadCount);
        var tasks = new Task[ThreadCount];

        for (int t = 0; t < ThreadCount; t++)
        {
            int threadId = t;
            tasks[t] = Task.Run(() =>
            {
                try
                {
                    barrier.SignalAndWait();
                    using var tx = dbe.CreateQuickTransaction();
                    int start = threadId * EntitiesPerThread;
                    for (int i = 0; i < EntitiesPerThread; i++)
                    {
                        tx.OpenMut(allIds[start + i]).Write(SvTestArchetype.SvComp).Value = 1000 + threadId * 100 + i;
                    }
                    tx.Commit();
                }
                catch (Exception ex)
                {
                    errors.Add($"Thread {threadId}: {ex}");
                }
            });
        }

        Task.WaitAll(tasks);
        Assert.That(errors, Is.Empty, () => $"Concurrent SV writes failed: {string.Join("; ", errors)}");

        // DirtyBitmap should reflect all concurrent writes
        if (clusterState != null)
        {
            Assert.That(clusterState.ClusterDirtyBitmap.HasDirty, Is.True, "ClusterDirtyBitmap must reflect concurrent SV writes");
        }
        else
        {
            Assert.That(table.DirtyBitmap.HasDirty, Is.True, "DirtyBitmap must reflect concurrent SV writes");
        }

        var snapshot = clusterState != null
            ? clusterState.ClusterDirtyBitmap.Snapshot()
            : table.DirtyBitmap.Snapshot();
        int dirtyCount = 0;
        for (int i = 0; i < snapshot.Length; i++)
        {
            dirtyCount += BitOperations.PopCount((ulong)snapshot[i]);
        }
        Assert.That(dirtyCount, Is.GreaterThanOrEqualTo(totalEntities),
            "DirtyBitmap should have at least one bit per written entity");

        // Verify all values are correct
        using var txRead = dbe.CreateQuickTransaction();
        for (int t = 0; t < ThreadCount; t++)
        {
            int start = t * EntitiesPerThread;
            for (int i = 0; i < EntitiesPerThread; i++)
            {
                var val = txRead.Open(allIds[start + i]).Read(SvTestArchetype.SvComp).Value;
                Assert.That(val, Is.EqualTo(1000 + t * 100 + i), $"Entity [{start + i}] value mismatch");
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Category 1b: SV concurrent writes + WriteTickFence serialization
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void SV_ConcurrentWrite_ThenTickFence_ClearsBitmap()
    {
        using var dbe = SetupEngine();
        var table = dbe.GetComponentTable<CompSmSingleVersion>();

        int totalEntities = ThreadCount * EntitiesPerThread;
        var ids = new EntityId[totalEntities];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < totalEntities; i++)
            {
                var comp = new CompSmSingleVersion(i);
                ids[i] = tx.Spawn<SvTestArchetype>(SvTestArchetype.SvComp.Set(in comp));
            }
            tx.Commit();
        }

        var clusterState = dbe._archetypeStates[Archetype<SvTestArchetype>.Metadata.ArchetypeId]?.ClusterState;
        if (clusterState != null)
        {
            clusterState.ClusterDirtyBitmap.Snapshot(); // clear spawn-time dirty bits
        }
        else
        {
            table.DirtyBitmap.Snapshot(); // clear spawn-time dirty bits
        }

        // Concurrent writes
        var errors = new ConcurrentBag<string>();
        var barrier = new Barrier(ThreadCount);
        var tasks = new Task[ThreadCount];

        for (int t = 0; t < ThreadCount; t++)
        {
            int threadId = t;
            tasks[t] = Task.Run(() =>
            {
                try
                {
                    barrier.SignalAndWait();
                    using var tx = dbe.CreateQuickTransaction();
                    int start = threadId * EntitiesPerThread;
                    for (int i = 0; i < EntitiesPerThread; i++)
                    {
                        tx.OpenMut(ids[start + i]).Write(SvTestArchetype.SvComp).Value = threadId * 1000 + i;
                    }
                    tx.Commit();
                }
                catch (Exception ex)
                {
                    errors.Add($"Thread {threadId}: {ex}");
                }
            });
        }

        Task.WaitAll(tasks);
        Assert.That(errors, Is.Empty, () => $"Concurrent writes failed: {string.Join("; ", errors)}");

        // TickFence snapshots and clears the bitmap
        if (clusterState != null)
        {
            Assert.That(clusterState.ClusterDirtyBitmap.HasDirty, Is.True, "Should be dirty before tick fence");
        }
        else
        {
            Assert.That(table.DirtyBitmap.HasDirty, Is.True, "Should be dirty before tick fence");
        }
        dbe.WriteTickFence(1);
        if (clusterState != null)
        {
            Assert.That(clusterState.ClusterDirtyBitmap.HasDirty, Is.False, "ClusterDirtyBitmap must be cleared by WriteTickFence");
        }
        else
        {
            Assert.That(table.DirtyBitmap.HasDirty, Is.False, "DirtyBitmap must be cleared by WriteTickFence");
        }

        // New writes after tick fence produce fresh dirty bits
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(ids[0]).Write(SvTestArchetype.SvComp).Value = 9999;
            tx.Commit();
        }
        if (clusterState != null)
        {
            Assert.That(clusterState.ClusterDirtyBitmap.HasDirty, Is.True, "Writes after tick fence must set new dirty bits");
        }
        else
        {
            Assert.That(table.DirtyBitmap.HasDirty, Is.True, "Writes after tick fence must set new dirty bits");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Category 2: Transient Spawn/Destroy at scale
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    [Ignore("Flaky: throughput threshold assertion fails intermittently under parallel test load")]
    public void Transient_SpawnDestroy_Throughput()
    {
        using var dbe = SetupEngine();
        const int entitiesPerThread = 100;

        var errors = new ConcurrentBag<string>();
        var barrier = new Barrier(ThreadCount);
        var tasks = new Task[ThreadCount];

        for (int t = 0; t < ThreadCount; t++)
        {
            int threadId = t;
            tasks[t] = Task.Run(() =>
            {
                try
                {
                    barrier.SignalAndWait();

                    // Spawn phase
                    var localIds = new EntityId[entitiesPerThread];
                    using (var tx = dbe.CreateQuickTransaction())
                    {
                        for (int i = 0; i < entitiesPerThread; i++)
                        {
                            var comp = new CompSmTransient(threadId * 1000 + i);
                            localIds[i] = tx.Spawn<TransientTestArchetype>(
                                TransientTestArchetype.TransComp.Set(in comp));
                        }
                        tx.Commit();
                    }

                    // Verify spawn
                    using (var txr = dbe.CreateQuickTransaction())
                    {
                        for (int i = 0; i < entitiesPerThread; i++)
                        {
                            var val = txr.Open(localIds[i]).Read(TransientTestArchetype.TransComp).Value;
                            if (val != threadId * 1000 + i)
                            {
                                errors.Add($"Thread {threadId}: entity {i} read {val}, expected {threadId * 1000 + i}");
                            }
                        }
                    }

                    // Destroy phase
                    using (var tx = dbe.CreateQuickTransaction())
                    {
                        for (int i = 0; i < entitiesPerThread; i++)
                        {
                            tx.Destroy(localIds[i]);
                        }
                        tx.Commit();
                    }

                    // Verify destroy
                    using (var txv = dbe.CreateQuickTransaction())
                    {
                        for (int i = 0; i < entitiesPerThread; i++)
                        {
                            if (txv.IsAlive(localIds[i]))
                            {
                                errors.Add($"Thread {threadId}: entity {i} still alive after destroy");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"Thread {threadId}: {ex}");
                }
            });
        }

        Task.WaitAll(tasks);
        Assert.That(errors, Is.Empty, () => $"Transient spawn/destroy failed: {string.Join("; ", errors)}");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Category 3: Mixed-mode concurrent contention
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Mixed_ConcurrentWriteAllModes()
    {
        using var dbe = SetupEngine();
        var svTable = dbe.GetComponentTable<CompSmSingleVersion>();

        // Spawn mixed-mode entities: Versioned + SV + Transient in one archetype
        const int entityCount = 40; // 10 per thread
        var ids = new EntityId[entityCount];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < entityCount; i++)
            {
                var v = new CompSmVersionedMix(i);
                var sv = new CompSmSingleVersion(i + 1000);
                var tr = new CompSmTransient(i + 2000);
                ids[i] = tx.Spawn<MixedModeArchetype>(
                    MixedModeArchetype.Versioned.Set(in v),
                    MixedModeArchetype.SV.Set(in sv),
                    MixedModeArchetype.Trans.Set(in tr));
            }
            tx.Commit();
        }

        svTable.DirtyBitmap.Snapshot(); // clear spawn dirty bits

        // Each thread writes all 3 modes on its own entity slice
        var errors = new ConcurrentBag<string>();
        var barrier = new Barrier(ThreadCount);
        var tasks = new Task[ThreadCount];
        int perThread = entityCount / ThreadCount;

        for (int t = 0; t < ThreadCount; t++)
        {
            int threadId = t;
            tasks[t] = Task.Run(() =>
            {
                try
                {
                    barrier.SignalAndWait();
                    using var tx = dbe.CreateQuickTransaction();
                    int start = threadId * perThread;
                    for (int i = 0; i < perThread; i++)
                    {
                        var entity = tx.OpenMut(ids[start + i]);
                        entity.Write(MixedModeArchetype.Versioned).Value = 5000 + threadId * 100 + i;
                        entity.Write(MixedModeArchetype.SV).Value = 6000 + threadId * 100 + i;
                        entity.Write(MixedModeArchetype.Trans).Value = 7000 + threadId * 100 + i;
                    }
                    tx.Commit();
                }
                catch (Exception ex)
                {
                    errors.Add($"Thread {threadId}: {ex}");
                }
            });
        }

        Task.WaitAll(tasks);
        Assert.That(errors, Is.Empty, () => $"Mixed concurrent writes failed: {string.Join("; ", errors)}");

        // Dirty tracking: cluster-eligible archetypes use ClusterDirtyBitmap, not per-ComponentTable DirtyBitmap
        var meta = ArchetypeRegistry.GetMetadata<MixedModeArchetype>();
        if (meta.IsClusterEligible)
        {
            var clusterState = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
            Assert.That(clusterState.ClusterDirtyBitmap.HasDirty, Is.True, "ClusterDirtyBitmap must reflect writes");
        }
        else
        {
            Assert.That(svTable.DirtyBitmap.HasDirty, Is.True, "SV DirtyBitmap must reflect writes");
        }

        // Verify all 3 modes read back correctly
        using var txRead = dbe.CreateQuickTransaction();
        for (int t = 0; t < ThreadCount; t++)
        {
            int start = t * perThread;
            for (int i = 0; i < perThread; i++)
            {
                var entity = txRead.Open(ids[start + i]);
                Assert.That(entity.Read(MixedModeArchetype.Versioned).Value, Is.EqualTo(5000 + t * 100 + i));
                Assert.That(entity.Read(MixedModeArchetype.SV).Value, Is.EqualTo(6000 + t * 100 + i));
                Assert.That(entity.Read(MixedModeArchetype.Trans).Value, Is.EqualTo(7000 + t * 100 + i));
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Category 4: SV + epoch refresh under load
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void SV_ManyWrites_CrossesEpochRefresh()
    {
        using var dbe = SetupEngine();
        var table = dbe.GetComponentTable<CompSmSingleVersion>();

        // 200 entities crosses the 128-op FlushAndRefreshEpoch threshold
        const int entityCount = 200;
        var ids = new EntityId[entityCount];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < entityCount; i++)
            {
                var comp = new CompSmSingleVersion(i);
                ids[i] = tx.Spawn<SvTestArchetype>(SvTestArchetype.SvComp.Set(in comp));
            }
            tx.Commit();
        }

        var clusterState = dbe._archetypeStates[Archetype<SvTestArchetype>.Metadata.ArchetypeId]?.ClusterState;
        if (clusterState != null)
        {
            clusterState.ClusterDirtyBitmap.Snapshot(); // clear
        }
        else
        {
            table.DirtyBitmap.Snapshot(); // clear
        }

        // Single transaction writes all 200 — epoch refresh fires at ~128 ops
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < entityCount; i++)
            {
                tx.OpenMut(ids[i]).Write(SvTestArchetype.SvComp).Value = i + 5000;
            }
            tx.Commit();
        }

        // All values must survive the mid-transaction epoch refresh
        using var txRead = dbe.CreateQuickTransaction();
        for (int i = 0; i < entityCount; i++)
        {
            Assert.That(txRead.Open(ids[i]).Read(SvTestArchetype.SvComp).Value, Is.EqualTo(i + 5000),
                $"Entity {i} value mismatch after epoch refresh crossing");
        }

        // DirtyBitmap must still reflect writes (epoch refresh caps DirtyCounter, not DirtyBitmap)
        if (clusterState != null)
        {
            Assert.That(clusterState.ClusterDirtyBitmap.HasDirty, Is.True,
                "ClusterDirtyBitmap must persist through epoch refresh — only WriteTickFence clears it");
        }
        else
        {
            Assert.That(table.DirtyBitmap.HasDirty, Is.True,
                "DirtyBitmap must persist through epoch refresh — only WriteTickFence clears it");
        }
    }
}
