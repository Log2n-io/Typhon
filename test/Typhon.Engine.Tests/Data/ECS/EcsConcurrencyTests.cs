using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// Multi-threaded ECS operation tests. Verifies correctness under concurrent access:
/// parallel spawns, concurrent reads, read-while-commit, and parallel queries.
/// Each thread uses its own Transaction (thread affinity requirement).
/// </summary>
[TestFixture]
[NonParallelizable]
class EcsConcurrencyTests : TestBase<EcsConcurrencyTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<EcsPosition>();
        dbe.RegisterComponentFromAccessor<EcsVelocity>();
        dbe.RegisterComponentFromAccessor<EcsHealth>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    // ═══════════════════════════════════════════════════════════════
    // Parallel Spawn
    // ═══════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(15000)]
    public void ParallelSpawn_SameArchetype_AllEntitiesUnique()
    {
        using var dbe = SetupEngine();
        const int threadCount = 8;
        const int entitiesPerThread = 200;

        var allIds = new ConcurrentBag<EntityId>();
        var barrier = new Barrier(threadCount);

        Parallel.For(0, threadCount, i =>
        {
            barrier.SignalAndWait();
            using var t = dbe.CreateQuickTransaction();
            for (int j = 0; j < entitiesPerThread; j++)
            {
                var pos = new EcsPosition(i * 1000 + j, 0, 0);
                var id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos));
                allIds.Add(id);
            }
            t.Commit();
        });

        // All entity IDs must be unique (Interlocked.Increment on EntityKey)
        var uniqueIds = new HashSet<EntityId>(allIds);
        Assert.That(uniqueIds.Count, Is.EqualTo(threadCount * entitiesPerThread),
            "All spawned entity IDs must be unique across threads");
    }

    [Test]
    [CancelAfter(15000)]
    public void ParallelSpawn_AllEntitiesReadableAfterCommit()
    {
        using var dbe = SetupEngine();
        const int threadCount = 4;
        const int entitiesPerThread = 100;

        var allIds = new ConcurrentBag<(EntityId Id, float X)>();

        // Phase 1: Parallel spawns
        Parallel.For(0, threadCount, i =>
        {
            using var t = dbe.CreateQuickTransaction();
            for (int j = 0; j < entitiesPerThread; j++)
            {
                float x = i * 1000 + j;
                var pos = new EcsPosition(x, 0, 0);
                var id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos));
                allIds.Add((id, x));
            }
            t.Commit();
        });

        // Phase 2: Verify all entities are readable from a new transaction
        using var readTx = dbe.CreateQuickTransaction();
        foreach (var (id, expectedX) in allIds)
        {
            var entity = readTx.Open(id);
            ref readonly var pos = ref entity.Read(EcsUnit.Position);
            Assert.That(pos.X, Is.EqualTo(expectedX));
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Concurrent Read
    // ═══════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(15000)]
    public void ConcurrentReads_SameEntity_AllSeeConsistentData()
    {
        using var dbe = SetupEngine();

        // Pre-populate
        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(42, 43, 44);
            var vel = new EcsVelocity(1, 2, 3);
            id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
            t.Commit();
        }

        // Parallel reads — all threads should see the same data
        const int threadCount = 8;
        var errors = new ConcurrentBag<string>();

        Parallel.For(0, threadCount, i =>
        {
            using var t = dbe.CreateQuickTransaction();
            var entity = t.Open(id);
            ref readonly var pos = ref entity.Read(EcsUnit.Position);
            ref readonly var vel = ref entity.Read(EcsUnit.Velocity);

            if (pos.X != 42 || pos.Y != 43 || pos.Z != 44)
            {
                errors.Add($"Thread {i}: Position mismatch ({pos.X}, {pos.Y}, {pos.Z})");
            }
            if (vel.Dx != 1 || vel.Dy != 2 || vel.Dz != 3)
            {
                errors.Add($"Thread {i}: Velocity mismatch ({vel.Dx}, {vel.Dy}, {vel.Dz})");
            }
        });

        Assert.That(errors, Is.Empty, string.Join("\n", errors));
    }

    // ═══════════════════════════════════════════════════════════════
    // Read while Write (MVCC isolation)
    // ═══════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(15000)]
    public void ReadWhileWrite_SnapshotIsolation_ReaderSeesOldValue()
    {
        using var dbe = SetupEngine();

        // Create entity with initial value
        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(100, 0, 0);
            id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos));
            t.Commit();
        }

        // Open a read transaction BEFORE the write (snapshot at current TSN)
        using var readerTx = dbe.CreateQuickTransaction();
        var beforeEntity = readerTx.Open(id);
        ref readonly var beforePos = ref beforeEntity.Read(EcsUnit.Position);
        Assert.That(beforePos.X, Is.EqualTo(100f));

        // Write new value and commit on another transaction
        using (var writerTx = dbe.CreateQuickTransaction())
        {
            var mut = writerTx.OpenMut(id);
            mut.Write(EcsUnit.Position).X = 999;
            writerTx.Commit();
        }

        // Reader should still see old value (MVCC snapshot isolation)
        var afterEntity = readerTx.Open(id);
        ref readonly var afterPos = ref afterEntity.Read(EcsUnit.Position);
        Assert.That(afterPos.X, Is.EqualTo(100f), "Reader with older TSN should see pre-write value (MVCC)");
    }

    // ═══════════════════════════════════════════════════════════════
    // Parallel Queries
    // ═══════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(15000)]
    public void ParallelQueries_SameArchetype_AllReturnConsistentResults()
    {
        using var dbe = SetupEngine();
        const int entityCount = 200;

        // Pre-populate
        using (var t = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < entityCount; i++)
            {
                var pos = new EcsPosition(i, 0, 0);
                t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos));
            }
            t.Commit();
        }

        // Parallel queries — all should return the same count
        const int threadCount = 8;
        var counts = new ConcurrentBag<int>();

        Parallel.For(0, threadCount, _ =>
        {
            using var t = dbe.CreateQuickTransaction();
            counts.Add(t.Query<EcsUnit>().Count());
        });

        foreach (var count in counts)
        {
            Assert.That(count, Is.EqualTo(entityCount),
                "All parallel queries should return the same count");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Spawn + Destroy interleaved
    // ═══════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(15000)]
    public void ParallelSpawnAndDestroy_NoCorruption()
    {
        using var dbe = SetupEngine();
        const int iterations = 50;

        // Pre-populate entities to destroy
        var idsToDestroy = new EntityId[iterations];
        using (var t = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < iterations; i++)
            {
                var pos = new EcsPosition(i, 0, 0);
                idsToDestroy[i] = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos));
            }
            t.Commit();
        }

        var spawnedIds = new ConcurrentBag<EntityId>();
        var destroyedIds = new ConcurrentBag<EntityId>();

        // Thread 1: spawn new entities
        // Thread 2: destroy existing entities
        var tasks = new Task[2];
        tasks[0] = Task.Run(() =>
        {
            for (int i = 0; i < iterations; i++)
            {
                using var t = dbe.CreateQuickTransaction();
                var pos = new EcsPosition(1000 + i, 0, 0);
                var id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos));
                spawnedIds.Add(id);
                t.Commit();
            }
        });
        tasks[1] = Task.Run(() =>
        {
            for (int i = 0; i < iterations; i++)
            {
                using var t = dbe.CreateQuickTransaction();
                t.Destroy(idsToDestroy[i]);
                destroyedIds.Add(idsToDestroy[i]);
                t.Commit();
            }
        });

        Task.WaitAll(tasks);

        // Verify: all spawned entities are alive, all destroyed are dead
        using var verifyTx = dbe.CreateQuickTransaction();
        foreach (var id in spawnedIds)
        {
            Assert.That(verifyTx.IsAlive(id), Is.True, $"Spawned entity {id} should be alive");
        }
        foreach (var id in destroyedIds)
        {
            Assert.That(verifyTx.IsAlive(id), Is.False, $"Destroyed entity {id} should be dead");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Read-while-Destroy (MVCC isolation)
    // ═══════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(15000)]
    public void ReadWhileDestroy_SnapshotIsolation_ReaderStillSeesEntity()
    {
        using var dbe = SetupEngine();

        // Create entity
        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(77, 88, 99);
            id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos));
            t.Commit();
        }

        // Open a reader BEFORE the destroy (snapshot at current TSN)
        using var readerTx = dbe.CreateQuickTransaction();
        Assert.That(readerTx.IsAlive(id), Is.True);
        var entity = readerTx.Open(id);
        ref readonly var pos1 = ref entity.Read(EcsUnit.Position);
        Assert.That(pos1.X, Is.EqualTo(77f));

        // Destroy on another transaction and commit
        using (var destroyTx = dbe.CreateQuickTransaction())
        {
            destroyTx.Destroy(id);
            destroyTx.Commit();
        }

        // Reader with older TSN should still see the entity alive (MVCC)
        Assert.That(readerTx.IsAlive(id), Is.True, "Reader with older TSN should still see entity alive");
        var entity2 = readerTx.Open(id);
        ref readonly var pos2 = ref entity2.Read(EcsUnit.Position);
        Assert.That(pos2.X, Is.EqualTo(77f), "Reader should still read correct data after concurrent destroy");

        // New transaction should see it dead
        using (var newTx = dbe.CreateQuickTransaction())
        {
            Assert.That(newTx.IsAlive(id), Is.False, "New transaction should see entity as dead");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Concurrent writes to same entity (conflict)
    // ═══════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(15000)]
    public void ConcurrentWrites_SameEntity_LastWriterWins()
    {
        using var dbe = SetupEngine();

        // Create entity
        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(1, 2, 3);
            id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos));
            t.Commit();
        }

        // tx1 and tx2 both open for writing
        using var tx1 = dbe.CreateQuickTransaction();
        using var tx2 = dbe.CreateQuickTransaction();

        // Both write to the same entity
        var e1 = tx1.OpenMut(id);
        e1.Write(EcsUnit.Position).X = 100;

        var e2 = tx2.OpenMut(id);
        e2.Write(EcsUnit.Position).X = 200;

        // Both commits succeed — ECS uses revision chains, last writer's revision becomes head
        bool first = tx1.Commit();
        Assert.That(first, Is.True, "First writer should commit successfully");

        bool second = tx2.Commit();
        Assert.That(second, Is.True, "Second writer also succeeds (revision chain append)");

        // Final value reflects the later commit (tx2 committed after tx1)
        using (var verifyTx = dbe.CreateQuickTransaction())
        {
            var entity = verifyTx.Open(id);
            ref readonly var pos = ref entity.Read(EcsUnit.Position);
            Assert.That(pos.X, Is.EqualTo(200f), "Value should reflect last committer (tx2)");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Parallel polymorphic queries
    // ═══════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(15000)]
    public void ParallelPolymorphicQueries_ConsistentResults()
    {
        using var dbe = SetupEngine();

        // Spawn EcsUnit and EcsSoldier entities (EcsSoldier extends EcsUnit)
        using (var t = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 50; i++)
            {
                var pos = new EcsPosition(i, 0, 0);
                t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos));
            }
            for (int i = 0; i < 30; i++)
            {
                var pos = new EcsPosition(100 + i, 0, 0);
                var hp = new EcsHealth(100, 100);
                t.Spawn<EcsSoldier>(EcsUnit.Position.Set(in pos), EcsSoldier.Health.Set(in hp));
            }
            t.Commit();
        }

        const int threadCount = 8;
        var baseCounts = new ConcurrentBag<int>();
        var exactCounts = new ConcurrentBag<int>();

        Parallel.For(0, threadCount, _ =>
        {
            using var t = dbe.CreateQuickTransaction();
            baseCounts.Add(t.Query<EcsUnit>().Count());       // polymorphic: EcsUnit + EcsSoldier
            exactCounts.Add(t.QueryExact<EcsUnit>().Count()); // exact: EcsUnit only
        });

        foreach (var c in baseCounts)
        {
            Assert.That(c, Is.EqualTo(80), "Polymorphic query should find 50 + 30 = 80");
        }
        foreach (var c in exactCounts)
        {
            Assert.That(c, Is.EqualTo(50), "Exact query should find only 50 EcsUnit");
        }
    }
}
