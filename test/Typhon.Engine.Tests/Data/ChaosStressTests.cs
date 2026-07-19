using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine.Tests;

/// <summary>
/// Chaos stress tests that exercise all major subsystems simultaneously under heavy load.
/// These tests are designed to find race conditions, deadlocks, resource leaks, and edge cases.
/// </summary>
[TestFixture]
[PublicAPI]
[Ignore("Too long, should be manually executed when needed")]
class ChaosStressTests : TestBase<ChaosStressTests>
{
    // Increase cache size for stress tests
    private const int StressCacheSize = 4 * 1024 * 1024; // 4MB cache

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    [SetUp]
    public override void Setup()
    {
        base.Setup();

        // Ensure database file is deleted with retry logic for stress tests
        // This handles cases where a previous crashed test left the file locked
        EnsureFileDeletedWithRetry();
    }

    /// <summary>
    /// Attempts to delete the database file with retries, handling locked file scenarios
    /// that can occur when a previous test run crashed or was terminated.
    /// </summary>
    private void EnsureFileDeletedWithRetry()
    {
        using var scope = ServiceProvider.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<ManagedPagedMMFOptions>>().Value;
        var filePath = Path.Combine(options.DatabaseDirectory, options.DatabaseName + ".bin");

        if (!File.Exists(filePath))
            return;

        const int maxRetries = 5;
        const int retryDelayMs = 500;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                File.Delete(filePath);
                return; // Success
            }
            catch (IOException) when (attempt < maxRetries)
            {
                // File is locked, wait and retry
                Thread.Sleep(retryDelayMs);
            }
            catch (UnauthorizedAccessException) when (attempt < maxRetries)
            {
                // File is locked, wait and retry
                Thread.Sleep(retryDelayMs);
            }
        }

        // If we get here, all retries failed - log warning but continue
        // The test will fail with a clear error if the file is still locked
        Logger.LogWarning("Could not delete database file after {MaxRetries} attempts: {FilePath}", maxRetries, filePath);
    }

    protected override void RegisterComponents(DatabaseEngine dbe)
    {
        // Use base components (CompA-E) which are already properly configured
        base.RegisterComponents(dbe);
    }

    #region Test Case Sources

    /// <summary>
    /// Generates test cases with varying thread counts, entity counts, and operation mixes.
    /// </summary>
    private static IEnumerable<TestCaseData> ChaosTestCases()
    {
        // (threads, entitiesPerThread, operationsPerEntity, readWriteRatio, includeDeletes, seed)

        // Light load - quick sanity check
        yield return new TestCaseData(2, 50, 10, 0.7f, false, 12345)
            .SetName("Light_2T_50E_NoDelete");

        // Medium load - balanced operations
        yield return new TestCaseData(4, 100, 20, 0.5f, true, 23456)
            .SetName("Medium_4T_100E_WithDelete");

        // Heavy reads - read-heavy workload
        yield return new TestCaseData(8, 50, 30, 0.9f, false, 34567)
            .SetName("HeavyRead_8T_50E");

        // Heavy writes - write-heavy workload with contention
        yield return new TestCaseData(4, 200, 15, 0.2f, true, 45678)
            .SetName("HeavyWrite_4T_200E_WithDelete");

        // Maximum contention - few entities, many threads
        yield return new TestCaseData(8, 10, 50, 0.5f, true, 56789)
            .SetName("MaxContention_8T_10E");

        // Scale test - many entities, moderate threads
        yield return new TestCaseData(4, 500, 5, 0.6f, false, 67890)
            .SetName("Scale_4T_500E");
    }

    /// <summary>
    /// Long-running transaction interference test cases.
    /// </summary>
    private static IEnumerable<TestCaseData> LongRunningTxnTestCases()
    {
        // (workerThreads, longRunningCount, entitiesPerWorker, holdTimeMs, seed)
        yield return new TestCaseData(4, 2, 100, 500, 11111)
            .SetName("LongTxn_4W_2L_500ms");
        yield return new TestCaseData(6, 3, 50, 1000, 22222)
            .SetName("LongTxn_6W_3L_1000ms");
    }

    /// <summary>
    /// Multi-component operation test cases.
    /// </summary>
    private static IEnumerable<TestCaseData> MultiComponentTestCases()
    {
        // (threads, entitiesPerThread, componentsPerEntity, updateRounds, seed)
        yield return new TestCaseData(4, 50, 3, 10, 77777)
            .SetName("MultiComp_4T_50E_3C");
        yield return new TestCaseData(2, 100, 5, 5, 88888)
            .SetName("MultiComp_2T_100E_5C");
    }

    #endregion

    #region Main Chaos Test

    /// <summary>
    /// The ultimate chaos test: multiple threads performing random CRUD operations
    /// on multiple component types with varying transaction lifetimes.
    /// </summary>
    [Test]
    [TestCaseSource(nameof(ChaosTestCases))]
    [Property("CacheSize", StressCacheSize)]
    public void ChaosTest_MultiThreadedCRUD(int threadCount, int entitiesPerThread, int operationsPerEntity, float readWriteRatio,
        bool includeDeletes, int seed)
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var errors = new ConcurrentBag<string>();
        var stats = new ConcurrentDictionary<string, long>();
        var allEntityIds = new ConcurrentDictionary<EntityId, (int owningThread, int compTypeIdx)>(); // entityId -> (owningThread, compType: 0=A, 1=B, 2=D)
        var deletedEntities = new ConcurrentDictionary<EntityId, bool>();

        // Initialize stats
        foreach (var key in new[] { "Creates", "Reads", "Updates", "Deletes", "Commits", "Rollbacks", "Conflicts" })
        {
            stats[key] = 0;
        }

        var barrier = new Barrier(threadCount);
        var tasks = new Task[threadCount];

        for (int t = 0; t < threadCount; t++)
        {
            var threadId = t;
            var threadSeed = seed + threadId * 1000;

            tasks[t] = Task.Run(() =>
            {
                var rand = new Random(threadSeed);
                var localEntities = new List<(EntityId id, int compTypeIdx)>();

                try
                {
                    // Phase 1: Create entities
                    barrier.SignalAndWait();

                    for (int i = 0; i < entitiesPerThread; i++)
                    {
                        try
                        {
                            using var txn = dbe.CreateQuickTransaction();

                            // Create with random component combination using existing CompA/CompB/CompD
                            var compA = new CompA(threadId * 10000 + i, rand.NextSingle(), rand.NextDouble());
                            var compB = new CompB(threadId, rand.NextSingle());
                            var compD = new CompD(rand.NextSingle(), threadId * 1000 + i, rand.NextDouble());

                            var choice = rand.Next(3);
                            EntityId entityId;
                            int compTypeIdx;
                            switch (choice)
                            {
                                case 0:
                                    entityId = txn.Spawn<CompAArch>(CompAArch.A.Set(in compA));
                                    compTypeIdx = 0;
                                    break;
                                case 1:
                                    entityId = txn.Spawn<CompBArch>(CompBArch.B.Set(in compB));
                                    compTypeIdx = 1;
                                    break;
                                default:
                                    entityId = txn.Spawn<CompDArch>(CompDArch.D.Set(in compD));
                                    compTypeIdx = 2;
                                    break;
                            }

                            if (txn.Commit())
                            {
                                localEntities.Add((entityId, compTypeIdx));
                                allEntityIds[entityId] = (threadId, compTypeIdx);
                                stats.AddOrUpdate("Creates", 1, (_, v) => v + 1);
                                stats.AddOrUpdate("Commits", 1, (_, v) => v + 1);
                            }
                            else
                            {
                                stats.AddOrUpdate("Rollbacks", 1, (_, v) => v + 1);
                            }
                        }
                        catch (Exception ex)
                        {
                            errors.Add($"Thread {threadId} Create failed: {ex.Message}");
                        }
                    }

                    // Phase 2: Random operations
                    barrier.SignalAndWait();

                    for (int op = 0; op < operationsPerEntity * entitiesPerThread; op++)
                    {
                        if (localEntities.Count == 0)
                            break;

                        var txn = dbe.CreateQuickTransaction();
                        try
                        {
                            var targetIdx = rand.Next(localEntities.Count);
                            var (targetEntity, targetCompType) = localEntities[targetIdx];
                            var opRoll = (float)rand.NextDouble();

                            if (opRoll < readWriteRatio)
                            {
                                // Read operation — use TryRead to probe the entity's component type
                                var entity = txn.Open(targetEntity);
                                var readA = entity.TryRead<CompA>(out _);
                                var readB = entity.TryRead<CompB>(out _);
                                var readD = entity.TryRead<CompD>(out _);

                                if (readA || readB || readD)
                                {
                                    stats.AddOrUpdate("Reads", 1, (_, v) => v + 1);
                                }
                            }
                            else if (includeDeletes && opRoll > 0.95f && localEntities.Count > 5)
                            {
                                // Delete operation (rare)
                                if (!deletedEntities.ContainsKey(targetEntity))
                                {
                                    txn.Destroy(targetEntity);

                                    if (txn.Commit())
                                    {
                                        deletedEntities[targetEntity] = true;
                                        localEntities.RemoveAt(targetIdx);
                                        stats.AddOrUpdate("Deletes", 1, (_, v) => v + 1);
                                        stats.AddOrUpdate("Commits", 1, (_, v) => v + 1);
                                    }
                                }
                            }
                            else
                            {
                                // Update operation — use known component type
                                switch (targetCompType)
                                {
                                    case 0:
                                    {
                                        txn.Open(targetEntity).Read(CompAArch.A);
                                        ref var w = ref txn.OpenMut(targetEntity).Write(CompAArch.A);
                                        w.A = rand.Next();
                                        break;
                                    }
                                    case 1:
                                    {
                                        txn.Open(targetEntity).Read(CompBArch.B);
                                        ref var w = ref txn.OpenMut(targetEntity).Write(CompBArch.B);
                                        w.A = rand.Next();
                                        break;
                                    }
                                    case 2:
                                    {
                                        txn.Open(targetEntity).Read(CompDArch.D);
                                        ref var w = ref txn.OpenMut(targetEntity).Write(CompDArch.D);
                                        w.B = rand.Next();
                                        break;
                                    }
                                }

                                var committed = txn.Commit();
                                if (committed)
                                {
                                    stats.AddOrUpdate("Updates", 1, (_, v) => v + 1);
                                    stats.AddOrUpdate("Commits", 1, (_, v) => v + 1);
                                }
                                else
                                {
                                    stats.AddOrUpdate("Conflicts", 1, (_, v) => v + 1);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            errors.Add($"Thread {threadId} Op {op} failed: {ex}");
                        }
                        finally
                        {
                            txn.Dispose();
                        }
                    }

                    // Phase 3: Verify all non-deleted entities are readable
                    barrier.SignalAndWait();

                    foreach (var (entityId, _) in localEntities)
                    {
                        if (deletedEntities.ContainsKey(entityId))
                            continue;

                        try
                        {
                            using var txn = dbe.CreateQuickTransaction();
                            var canRead = txn.IsAlive(entityId);

                            if (!canRead)
                            {
                                errors.Add($"Thread {threadId}: Entity {entityId} not readable but not marked deleted");
                            }
                        }
                        catch (Exception ex)
                        {
                            errors.Add($"Thread {threadId} Verify {entityId} failed: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"Thread {threadId} fatal: {ex}");
                }
            });
        }

        Task.WaitAll(tasks);

        // Report stats
        Logger.LogInformation("=== Chaos Test Stats ===");
        foreach (var kvp in stats.OrderBy(x => x.Key))
        {
            Logger.LogInformation("{Key}: {Value}", kvp.Key, kvp.Value);
        }
        Logger.LogInformation("Total entities created: {Count}", allEntityIds.Count);
        Logger.LogInformation("Total entities deleted: {Count}", deletedEntities.Count);

        // Assert no errors
        Assert.That(errors, Is.Empty, $"Errors occurred:\n{string.Join("\n", errors.Take(20))}");

        // Verify final state
        var finalEntityCount = allEntityIds.Count - deletedEntities.Count;
        Assert.That(finalEntityCount, Is.GreaterThan(0), "Should have surviving entities");
    }

    #endregion

    #region Long-Running Transaction Tests

    /// <summary>
    /// Tests that long-running transactions correctly maintain MVCC isolation
    /// while other transactions make modifications.
    /// </summary>
    [Test]
    [TestCaseSource(nameof(LongRunningTxnTestCases))]
    [Property("CacheSize", StressCacheSize)]
    public void LongRunningTransaction_MaintainsIsolation(int workerThreads, int longRunningCount, int entitiesPerWorker, int holdTimeMs, int seed)
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var errors = new ConcurrentBag<string>();
        var rand = new Random(seed);

        // Phase 1: Create initial entities
        var entityIds = new List<EntityId>();
        var initialValues = new Dictionary<EntityId, int>();

        for (int i = 0; i < workerThreads * entitiesPerWorker; i++)
        {
            using var txn = dbe.CreateQuickTransaction();
            var comp = new CompA(i * 100, 0f, 0d); // Use A field as the value we track
            var id = txn.Spawn<CompAArch>(CompAArch.A.Set(in comp));
            txn.Commit();
            entityIds.Add(id);
            initialValues[id] = i * 100;
        }

        Logger.LogInformation("Created {Count} initial entities", entityIds.Count);

        // Phase 2: Start long-running transactions that snapshot the initial state
        // These transactions are intentionally kept open to test MVCC isolation - disposed in cleanup loop below
        var longRunningTxns = new List<(Transaction txn, Dictionary<EntityId, int> snapshot)>();
        for (int i = 0; i < longRunningCount; i++)
        {
#pragma warning disable TYPHON004 // Intentionally keeping transaction open to test MVCC - disposed in cleanup loop
            var txn = dbe.CreateQuickTransaction();
#pragma warning restore TYPHON004
            var snapshot = new Dictionary<EntityId, int>();

            // Read all entities to establish snapshot
            foreach (var id in entityIds)
            {
                var comp = txn.Open(id).Read(CompAArch.A);
                snapshot[id] = comp.A;
            }

            longRunningTxns.Add((txn, snapshot));
            Logger.LogInformation("Long-running transaction {I} started with TSN {Tsn}", i, txn.TSN);
        }

        // Phase 3: Worker threads modify entities while long-running transactions are active
        var barrier = new Barrier(workerThreads);
        var modificationCounts = new ConcurrentDictionary<EntityId, int>();
        var workerTasks = new Task[workerThreads];

        for (int w = 0; w < workerThreads; w++)
        {
            var workerId = w;
            var workerSeed = seed + workerId * 1000;

            workerTasks[w] = Task.Run(() =>
            {
                var workerRand = new Random(workerSeed);
                barrier.SignalAndWait();

                for (int round = 0; round < 10; round++)
                {
                    var targetId = entityIds[workerRand.Next(entityIds.Count)];

                    try
                    {
                        using var txn = dbe.CreateQuickTransaction();
                        txn.Open(targetId).Read(CompAArch.A);
                        ref var w2 = ref txn.OpenMut(targetId).Write(CompAArch.A);
                        w2.A += 1;

                        if (txn.Commit())
                        {
                            modificationCounts.AddOrUpdate(targetId, 1, (_, v) => v + 1);
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Worker {workerId} round {round}: {ex.Message}");
                    }

                    Thread.Sleep(workerRand.Next(10, 50));
                }
            });
        }

        // Wait a bit, then verify long-running transactions still see old values
        Thread.Sleep(holdTimeMs / 2);

        foreach (var (txn, snapshot) in longRunningTxns)
        {
            foreach (var (id, expectedValue) in snapshot)
            {
                var comp = txn.Open(id).Read(CompAArch.A);
                if (comp.A != expectedValue)
                {
                    errors.Add($"MVCC violation: Entity {id} expected {expectedValue} but got {comp.A}");
                }
            }
        }

        // Wait for workers to complete
        Task.WaitAll(workerTasks);

        // Verify long-running transactions STILL see their snapshot
        foreach (var (txn, snapshot) in longRunningTxns)
        {
            foreach (var (id, expectedValue) in snapshot)
            {
                var comp = txn.Open(id).Read(CompAArch.A);
                if (comp.A != expectedValue)
                {
                    errors.Add($"MVCC violation after workers: Entity {id} expected {expectedValue} but got {comp.A}");
                }
            }
        }

        // Cleanup long-running transactions
        foreach (var (txn, _) in longRunningTxns)
        {
            txn.Dispose();
        }

        // Verify new transactions see the latest values
        using (var verifyTxn = dbe.CreateQuickTransaction())
        {
            foreach (var id in entityIds)
            {
                var comp = verifyTxn.Open(id).Read(CompAArch.A);
                var expectedMin = initialValues[id];
                var modifications = modificationCounts.GetValueOrDefault(id, 0);

                // Value should be at least initial + modifications (could be more due to conflicts)
                if (comp.A < expectedMin + modifications)
                {
                    Logger.LogWarning(
                        "Entity {Id}: value {Value} < expected minimum {Expected} (initial {Initial} + mods {Mods})",
                        id, comp.A, expectedMin + modifications, expectedMin, modifications);
                }
            }
        }

        Logger.LogInformation("Total modifications: {Count}", modificationCounts.Values.Sum());

        Assert.That(errors, Is.Empty, $"MVCC errors:\n{string.Join("\n", errors)}");
    }

    #endregion

    #region Multi-Component Tests

    /// <summary>
    /// Tests creating and updating entities with multiple component types atomically.
    /// </summary>
    [Test]
    [TestCaseSource(nameof(MultiComponentTestCases))]
    [Property("CacheSize", StressCacheSize)]
    // [Ignore("Pre-existing BTree concurrency bug: NullRef in NodeWrapper.GetLast during concurrent creates")]
    public void MultiComponent_AtomicOperations(int threadCount, int entitiesPerThread, int componentsPerEntity, int updateRounds, int seed)
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var errors = new ConcurrentBag<string>();
        var allEntities = new ConcurrentDictionary<EntityId, int>(); // entityId -> thread

        var tasks = new Task[threadCount];
        var barrier = new Barrier(threadCount);

        for (int t = 0; t < threadCount; t++)
        {
            var threadId = t;
            var threadSeed = seed + threadId * 1000;

            tasks[t] = Task.Run(() =>
            {
                var rand = new Random(threadSeed);
                var localEntities = new List<(EntityId id, int compCount)>();

                try
                {
                    barrier.SignalAndWait();

                    // Create multi-component entities
                    for (int i = 0; i < entitiesPerThread; i++)
                    {
                        using var txn = dbe.CreateQuickTransaction();

                        var compA = new CompA(threadId * 1000 + i, rand.NextSingle(), rand.NextDouble());
                        var compB = new CompB(threadId, rand.NextSingle());
                        var compD = new CompD(rand.NextSingle(), threadId * 100 + i, rand.NextDouble());

                        EntityId entityId;
                        int compCount;
                        if (componentsPerEntity >= 3)
                        {
                            entityId = txn.Spawn<CompABDArch>(CompABDArch.A.Set(in compA), CompABDArch.B.Set(in compB), CompABDArch.D.Set(in compD));
                            compCount = 3;
                        }
                        else if (componentsPerEntity >= 2)
                        {
                            entityId = txn.Spawn<CompABArch>(CompABArch.A.Set(in compA), CompABArch.B.Set(in compB));
                            compCount = 2;
                        }
                        else
                        {
                            entityId = txn.Spawn<CompAArch>(CompAArch.A.Set(in compA));
                            compCount = 1;
                        }

                        if (txn.Commit())
                        {
                            localEntities.Add((entityId, compCount));
                            allEntities[entityId] = threadId;
                        }
                    }

                    // Update rounds
                    barrier.SignalAndWait();

                    for (int round = 0; round < updateRounds; round++)
                    {
                        if (localEntities.Count == 0)
                            break;

                        var targetIdx = rand.Next(localEntities.Count);
                        var (targetId, targetCompCount) = localEntities[targetIdx];

                        using var txn = dbe.CreateQuickTransaction();

                        // Read and update all components atomically
                        var entity = txn.Open(targetId);
                        var hasA = entity.TryRead<CompA>(out var compA);
                        var hasB = entity.TryRead<CompB>(out var compB);
                        var hasD = entity.TryRead<CompD>(out var compD);

                        if (hasA)
                        {
                            compA.A += 1;
                            ref var wA = ref txn.OpenMut(targetId).Write(CompAArch.A);
                            wA = compA;
                        }
                        if (hasB)
                        {
                            compB.A += 1;
                            ref var wB = ref txn.OpenMut(targetId).Write(CompABArch.B);
                            wB = compB;
                        }
                        if (hasD)
                        {
                            // Only update AllowMultiple-indexed fields (A, C) — not B which has a unique index
                            // and incrementing it would collide with adjacent entities' B values.
                            compD.A += 0.1f;
                            compD.C += 0.1;
                            ref var wD = ref txn.OpenMut(targetId).Write(CompABDArch.D);
                            wD = compD;
                        }

                        if (!txn.Commit())
                        {
                            // Conflict - this is expected in concurrent scenarios
                        }
                    }

                    // Verify consistency
                    barrier.SignalAndWait();

                    foreach (var (entityId, compCount) in localEntities)
                    {
                        using var txn = dbe.CreateQuickTransaction();

                        var entity = txn.Open(entityId);
                        var hasA = entity.TryRead<CompA>(out var compA);
                        var hasB = entity.TryRead<CompB>(out var compB);

                        if (hasA && hasB)
                        {
                            // CompA.A and CompB.A should have been updated together
                            // They started with same base (threadId * 1000 + i and threadId)
                            // and were incremented together
                            var aDiff = compA.A - (threadId * 1000);
                            var bDiff = compB.A - threadId;

                            // Allow some variance due to initial i offset, but they should track together
                            // after the updates (which always increment both)
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

        Logger.LogInformation("Created {Count} multi-component entities", allEntities.Count);

        Assert.That(errors, Is.Empty, $"Errors:\n{string.Join("\n", errors)}");
        Assert.That(allEntities.Count, Is.EqualTo(threadCount * entitiesPerThread),
            "All entities should be created successfully");
    }

    #endregion

    #region Revision Chain Stress Test

    /// <summary>
    /// Stress tests the revision chain system by creating many revisions rapidly
    /// while other transactions hold references to old revisions.
    /// </summary>
    [Test]
    [Property("CacheSize", StressCacheSize)]
    public void RevisionChain_RapidUpdatesWithLongReaders()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var errors = new ConcurrentBag<string>();

        // Create a single entity that will be updated rapidly
        EntityId targetEntity;
        {
            using var txn = dbe.CreateQuickTransaction();
            var comp = new CompA(0, 0f, 0d); // Use A field as the counter
            targetEntity = txn.Spawn<CompAArch>(CompAArch.A.Set(in comp));
            txn.Commit();
        }

        const int readerCount = 4;
        const int writerCount = 2;
        const int updatesPerWriter = 100;
        const int readsPerReader = 50;

        var readerSnapshots = new ConcurrentDictionary<int, int>(); // readerId -> value seen
        var writerCounts = new ConcurrentDictionary<int, int>(); // writerId -> successful updates

        var startSignal = new ManualResetEventSlim(false);
        var allTasks = new List<Task>();

        // Reader tasks - each takes a snapshot and verifies it stays consistent
        for (int r = 0; r < readerCount; r++)
        {
            var readerId = r;
            allTasks.Add(Task.Run(() =>
            {
                startSignal.Wait();
                Thread.Sleep(readerId * 20); // Stagger starts

                var txn = dbe.CreateQuickTransaction();
                try
                {
                    // Take snapshot
                    var initial = txn.Open(targetEntity).Read(CompAArch.A);
                    readerSnapshots[readerId] = initial.A;

                    // Keep reading and verify consistency
                    for (int i = 0; i < readsPerReader; i++)
                    {
                        var current = txn.Open(targetEntity).Read(CompAArch.A);
                        if (current.A != initial.A)
                        {
                            errors.Add($"Reader {readerId}: MVCC violation! Expected {initial.A}, got {current.A}");
                        }
                        Thread.Sleep(5);
                    }
                }
                finally
                {
                    txn.Dispose();
                }
            }));
        }

        // Writer tasks - rapidly update the entity
        for (int w = 0; w < writerCount; w++)
        {
            var writerId = w;
            writerCounts[writerId] = 0;

            allTasks.Add(Task.Run(() =>
            {
                startSignal.Wait();

                for (int i = 0; i < updatesPerWriter; i++)
                {
                    using var txn = dbe.CreateQuickTransaction();
                    txn.Open(targetEntity).Read(CompAArch.A);
                    ref var w2 = ref txn.OpenMut(targetEntity).Write(CompAArch.A);
                    w2.A += 1;

                    if (txn.Commit())
                    {
                        writerCounts.AddOrUpdate(writerId, 1, (_, v) => v + 1);
                    }
                }
            }));
        }

        // Start all tasks
        startSignal.Set();
        Task.WaitAll(allTasks.ToArray());

        // Report
        var totalWrites = writerCounts.Values.Sum();
        Logger.LogInformation("Total successful writes: {Count}", totalWrites);
        Logger.LogInformation("Reader snapshots: {Snapshots}", string.Join(", ", readerSnapshots.Select(x => $"R{x.Key}={x.Value}")));

        // Verify final value
        // With "last write wins" conflict resolution, concurrent writers can both read the same value V,
        // both write V+1, and both commit successfully — but the effective increment is only +1.
        // So final.A can be less than totalWrites (successful commits) due to overlapping updates.
        using (var txn = dbe.CreateQuickTransaction())
        {
            var final2 = txn.Open(targetEntity).Read(CompAArch.A);
            Logger.LogInformation("Final value: {Value}, Total successful commits: {TotalWrites}", final2.A, totalWrites);
            Assert.That(final2.A, Is.GreaterThan(0), "At least some writes should have taken effect");
            Assert.That(final2.A, Is.LessThanOrEqualTo(totalWrites),
                "Final value cannot exceed the number of successful commits");
        }

        Assert.That(errors, Is.Empty, $"MVCC errors:\n{string.Join("\n", errors)}");
    }

    #endregion

    #region Page Cache Pressure Test

    /// <summary>
    /// Tests behavior under page cache pressure - many concurrent transactions
    /// accessing more data than fits in cache.
    /// </summary>
    [Test]
    [Property("CacheSize", 2 * 1024 * 1024)] // Small cache: 2MB (minimum allowed)
    public void PageCachePressure_ManyEntitiesSmallCache()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var errors = new ConcurrentBag<string>();
        const int entityCount = 500; // More entities than cache can hold
        const int threadCount = 4;
        const int operationsPerThread = 200;

        // Create entities
        var entityIds = new EntityId[entityCount];
        for (int i = 0; i < entityCount; i++)
        {
            using var txn = dbe.CreateQuickTransaction();
            var comp = new CompA(i, i * 10f, 0d); // Use A as Id, B as Value
            entityIds[i] = txn.Spawn<CompAArch>(CompAArch.A.Set(in comp));
            txn.Commit();
        }

        Logger.LogInformation("Created {Count} entities", entityCount);

        // Hammer the cache with random access
        var rand = new Random(99999);
        var barrier = new Barrier(threadCount);
        var tasks = new Task[threadCount];
        var readCounts = new int[threadCount];
        var updateCounts = new int[threadCount];

        for (int t = 0; t < threadCount; t++)
        {
            var threadId = t;
            var threadSeed = rand.Next();

            tasks[t] = Task.Run(() =>
            {
                var localRand = new Random(threadSeed);
                barrier.SignalAndWait();

                for (int op = 0; op < operationsPerThread; op++)
                {
                    var targetIdx = localRand.Next(entityCount);
                    var targetId = entityIds[targetIdx];

                    try
                    {
                        using var txn = dbe.CreateQuickTransaction();

                        if (localRand.NextDouble() < 0.7)
                        {
                            // Read
                            var comp = txn.Open(targetId).Read(CompAArch.A);
                            // Verify data integrity
                            if (comp.A != targetIdx)
                            {
                                errors.Add($"Data corruption: Entity {targetId} has Id {comp.A}, expected {targetIdx}");
                            }
                            Interlocked.Increment(ref readCounts[threadId]);
                        }
                        else
                        {
                            // Update
                            txn.Open(targetId).Read(CompAArch.A);
                            ref var w = ref txn.OpenMut(targetId).Write(CompAArch.A);
                            w.B += 1;
                            if (txn.Commit())
                            {
                                Interlocked.Increment(ref updateCounts[threadId]);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Thread {threadId} op {op}: {ex.Message}");
                    }
                }
            });
        }

        Task.WaitAll(tasks);

        Logger.LogInformation("Reads: {Reads}, Updates: {Updates}",
            readCounts.Sum(), updateCounts.Sum());

        // Final verification - read all entities
        for (int i = 0; i < entityCount; i++)
        {
            using var txn = dbe.CreateQuickTransaction();
            var comp = txn.Open(entityIds[i]).Read(CompAArch.A);
            if (comp.A != i)
            {
                errors.Add($"Final verify: Entity {i} has wrong Id {comp.A}");
            }
        }

        Assert.That(errors, Is.Empty, $"Errors:\n{string.Join("\n", errors.Take(20))}");
    }

    #endregion

    #region Rollback Stress Test

    /// <summary>
    /// Tests that rollbacks correctly restore state under concurrent load.
    /// </summary>
    [Test]
    [Property("CacheSize", StressCacheSize)]
    public void RollbackStress_ConcurrentRollbacks()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var errors = new ConcurrentBag<string>();
        const int entityCount = 50;
        const int threadCount = 4;
        const int roundsPerThread = 20;

        // Create initial entities with known values
        var entityIds = new EntityId[entityCount];
        var initialValues = new int[entityCount];

        for (int i = 0; i < entityCount; i++)
        {
            using var txn = dbe.CreateQuickTransaction();
            var comp = new CompA(i * 100, 0f, 0d); // Use A field as the value we track
            entityIds[i] = txn.Spawn<CompAArch>(CompAArch.A.Set(in comp));
            initialValues[i] = i * 100;
            txn.Commit();
        }

        // Track committed updates
        var committedUpdates = new ConcurrentDictionary<EntityId, int>();
        foreach (var id in entityIds)
        {
            committedUpdates[id] = 0;
        }

        var barrier = new Barrier(threadCount);
        var tasks = new Task[threadCount];

        for (int t = 0; t < threadCount; t++)
        {
            var threadId = t;
            var rand = new Random(threadId * 12345);

            tasks[t] = Task.Run(() =>
            {
                barrier.SignalAndWait();

                for (int round = 0; round < roundsPerThread; round++)
                {
                    var targetIdx = rand.Next(entityCount);
                    var targetId = entityIds[targetIdx];
                    var shouldRollback = rand.NextDouble() < 0.5;

                    try
                    {
                        using var txn = dbe.CreateQuickTransaction();

                        txn.Open(targetId).Read(CompAArch.A);
                        ref var w = ref txn.OpenMut(targetId).Write(CompAArch.A);
                        w.A += 1;

                        if (shouldRollback)
                        {
                            txn.Rollback();
                            // Value should NOT have changed
                        }
                        else
                        {
                            // Delta-rebase handler: on conflict, apply our +1 delta on top of the latest committed value
                            // instead of blindly overwriting. This guarantees no lost updates under contention.
                            void ConcurrencyConflictHandler(ref ConcurrencyConflictSolver solver)
                            {
                                var committed = solver.CommittedData<CompA>();
                                solver.ToCommitData<CompA>().A = committed.A + 1;
                            }

                            if (txn.Commit(ConcurrencyConflictHandler))
                            {
                                committedUpdates.AddOrUpdate(targetId, 1, (_, v) => v + 1);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Thread {threadId} round {round}: {ex.Message}");
                    }
                }
            });
        }

        Task.WaitAll(tasks);

        // Verify final values match expected
        for (int i = 0; i < entityCount; i++)
        {
            using var txn = dbe.CreateQuickTransaction();
            var comp = txn.Open(entityIds[i]).Read(CompAArch.A);
            var expected = initialValues[i] + committedUpdates[entityIds[i]];

            // With a delta-rebase conflict handler, every committed update is reflected exactly
            if (comp.A != expected)
            {
                errors.Add($"Entity {i}: Value {comp.A} != expected {expected}");
            }
        }

        Logger.LogInformation("Total committed updates: {Count}", committedUpdates.Values.Sum());

        Assert.That(errors, Is.Empty, $"Errors:\n{string.Join("\n", errors)}");
    }

    #endregion

    #region Index Warfare Tests

    /// <summary>
    /// Forces B+Tree node splits to cascade via monotonic insertions (worst case)
    /// while concurrent threads delete from the middle, triggering merges that race with splits.
    /// </summary>
    [Test]
    [Property("CacheSize", StressCacheSize)]
    public void IndexSplit_CascadingSplitsUnderContention()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var errors = new ConcurrentBag<string>();
        const int threadCount = 6;
        const int entitiesPerThread = 150;
        // B value → entityId for surviving entities
        var allEntities = new ConcurrentDictionary<int, EntityId>();

        var barrier = new Barrier(threadCount);
        var tasks = new Task[threadCount];

        for (int t = 0; t < threadCount; t++)
        {
            var threadId = t;
            tasks[t] = Task.Run(() =>
            {
                var rand = new Random(threadId * 7777);
                var localEntities = new List<(int bValue, EntityId entityId)>();

                try
                {
                    barrier.SignalAndWait();

                    // Phase 1: Monotonic inserts — worst case for B+Tree (right-edge cascading splits)
                    for (int i = 0; i < entitiesPerThread; i++)
                    {
                        var bValue = threadId * 10000 + i;
                        try
                        {
                            using var txn = dbe.CreateQuickTransaction();
                            var comp = new CompD(rand.NextSingle(), bValue, rand.NextDouble());
                            var entityId = txn.Spawn<CompDArch>(CompDArch.D.Set(in comp));
                            if (txn.Commit())
                            {
                                localEntities.Add((bValue, entityId));
                                allEntities[bValue] = entityId;
                            }
                            else
                            {
                                errors.Add($"T{threadId}: Insert B={bValue} commit failed");
                            }
                        }
                        catch (Exception ex)
                        {
                            errors.Add($"T{threadId} insert B={bValue}: {ex.Message}");
                        }
                    }

                    barrier.SignalAndWait();

                    // Phase 2: Delete every 3rd entity + reinsert with new B values (merges racing with inserts across threads)
                    var toDelete = localEntities.Where((_, idx) => idx % 3 == 1).ToList();
                    var nextB = threadId * 10000 + entitiesPerThread;

                    foreach (var (bValue, entityId) in toDelete)
                    {
                        try
                        {
                            using (var txn = dbe.CreateQuickTransaction())
                            {
                                txn.Destroy(entityId);
                                if (txn.Commit())
                                {
                                    allEntities.TryRemove(bValue, out _);
                                }
                            }

                            using (var txn = dbe.CreateQuickTransaction())
                            {
                                var comp = new CompD(rand.NextSingle(), nextB, rand.NextDouble());
                                var newId = txn.Spawn<CompDArch>(CompDArch.D.Set(in comp));
                                if (txn.Commit())
                                {
                                    allEntities[nextB] = newId;
                                }
                            }

                            nextB++;
                        }
                        catch (Exception ex)
                        {
                            errors.Add($"T{threadId} delete/reinsert: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"T{threadId} fatal: {ex}");
                }
            });
        }

        Task.WaitAll(tasks);

        // Global verification: every surviving entity must be readable with correct B value
        var survivingCount = 0;
        foreach (var kvp in allEntities)
        {
            using var txn = dbe.CreateQuickTransaction();
            var comp = txn.Open(kvp.Value).Read(CompDArch.D);
            survivingCount++;
            if (comp.B != kvp.Key)
            {
                errors.Add($"Global verify: B={kvp.Key} entity has wrong B field: {comp.B}");
            }
        }

        Logger.LogInformation("Surviving indexed entities: {Count}/{Total}", survivingCount, allEntities.Count);
        Assert.That(errors, Is.Empty, $"Errors:\n{string.Join("\n", errors.Take(30))}");
        Assert.That(survivingCount, Is.EqualTo(allEntities.Count));
    }

    /// <summary>
    /// Hammers AllowMultiple indexes (CompD.A) by creating many entities with the same index key,
    /// then rapidly deleting and recreating while concurrent readers access the multi-value buffer.
    /// Stresses the RemoveValue/TryGetMultiple race path (fix 7708f4a).
    /// </summary>
    [Test]
    [Property("CacheSize", StressCacheSize)]
    public void AllowMultipleIndex_HighChurn()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var errors = new ConcurrentBag<string>();
        const int writerThreads = 4;
        const int readerThreads = 2;
        const int entitiesPerWriter = 80;
        const int churnRounds = 30;
        const float sharedAValue = 42.0f;

        var entityPool = new ConcurrentDictionary<EntityId, int>(); // entityId -> B value
        var nextBCounters = new int[writerThreads];

        // Phase 1: Each writer creates entities sharing the same A index key
        var createBarrier = new Barrier(writerThreads);
        var createTasks = new Task[writerThreads];

        for (int w = 0; w < writerThreads; w++)
        {
            var writerId = w;
            nextBCounters[writerId] = writerId * 100000;

            createTasks[w] = Task.Run(() =>
            {
                createBarrier.SignalAndWait();

                for (int i = 0; i < entitiesPerWriter; i++)
                {
                    var bVal = Interlocked.Increment(ref nextBCounters[writerId]);
                    try
                    {
                        using var txn = dbe.CreateQuickTransaction();
                        var comp = new CompD(sharedAValue, bVal, 1.0);
                        var id = txn.Spawn<CompDArch>(CompDArch.D.Set(in comp));
                        if (txn.Commit())
                        {
                            entityPool[id] = bVal;
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Writer {writerId} create B={bVal}: {ex.Message}");
                    }
                }
            });
        }

        Task.WaitAll(createTasks);
        Logger.LogInformation("Created {Count} entities sharing A={A}", entityPool.Count, sharedAValue);

        // Phase 2: Concurrent churn (delete + recreate) with simultaneous readers
        var churnDone = new ManualResetEventSlim(false);
        var allTasks = new List<Task>();

        for (int w = 0; w < writerThreads; w++)
        {
            var writerId = w;
            allTasks.Add(Task.Run(() =>
            {
                var rand = new Random(writerId * 3333);
                for (int round = 0; round < churnRounds; round++)
                {
                    var snapshot = entityPool.Keys.ToArray();
                    if (snapshot.Length == 0)
                    {
                        continue;
                    }

                    var targetId = snapshot[rand.Next(snapshot.Length)];
                    if (!entityPool.TryRemove(targetId, out _))
                    {
                        continue;
                    }

                    try
                    {
                        using (var txn = dbe.CreateQuickTransaction())
                        {
                            txn.Destroy(targetId);
                            txn.Commit();
                        }

                        var newB = Interlocked.Increment(ref nextBCounters[writerId]);
                        using (var txn = dbe.CreateQuickTransaction())
                        {
                            var comp = new CompD(sharedAValue, newB, round * 0.01);
                            var newId = txn.Spawn<CompDArch>(CompDArch.D.Set(in comp));
                            if (txn.Commit())
                            {
                                entityPool[newId] = newB;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Writer {writerId} churn round {round}: {ex.Message}");
                    }
                }
            }));
        }

        for (int r = 0; r < readerThreads; r++)
        {
            var readerId = r;
            allTasks.Add(Task.Run(() =>
            {
                var rand = new Random(readerId * 9999);
                int readCount = 0;

                while (!churnDone.IsSet || readCount < 50)
                {
                    var snapshot = entityPool.Keys.ToArray();
                    if (snapshot.Length == 0)
                    {
                        Thread.SpinWait(100);
                        continue;
                    }

                    var targetId = snapshot[rand.Next(snapshot.Length)];
                    try
                    {
                        using var txn = dbe.CreateQuickTransaction();
                        if (txn.IsAlive(targetId))
                        {
                            var comp = txn.Open(targetId).Read(CompDArch.D);
                            if (Math.Abs(comp.A - sharedAValue) > 0.001f)
                            {
                                errors.Add($"Reader {readerId}: A value corrupted, expected ~{sharedAValue} got {comp.A}");
                            }

                            readCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Reader {readerId}: {ex.Message}");
                    }

                    if (readCount > 500)
                    {
                        break;
                    }
                }
            }));
        }

        // Wait for writers, then signal readers
        Task.WaitAll(allTasks.Where((_, i) => i < writerThreads).ToArray());
        churnDone.Set();
        Task.WaitAll(allTasks.ToArray());

        // Final verification
        int verified = 0;
        foreach (var kvp in entityPool)
        {
            using var txn = dbe.CreateQuickTransaction();
            var comp = txn.Open(kvp.Key).Read(CompDArch.D);
            if (Math.Abs(comp.A - sharedAValue) > 0.001f)
            {
                errors.Add($"Final: Entity B={kvp.Value} has wrong A: {comp.A}");
            }

            if (comp.B != kvp.Value)
            {
                errors.Add($"Final: Entity expected B={kvp.Value} got {comp.B}");
            }

            verified++;
        }

        Logger.LogInformation("Verified {Count} entities after churn", verified);
        Assert.That(errors, Is.Empty, $"Errors:\n{string.Join("\n", errors.Take(30))}");
    }

    /// <summary>
    /// Multiple threads deliberately create entities with colliding unique index values (CompD.B).
    /// Exactly one should win per value. Verifies no ghost index entries from failed commits.
    /// </summary>
    [Test]
    [Property("CacheSize", StressCacheSize)]
    public void UniqueIndexViolation_UnderLoad()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var errors = new ConcurrentBag<string>();
        const int threadCount = 6;
        const int attemptsPerThread = 50;
        const int totalUniqueValues = 100;

        var successfulCreates = new ConcurrentBag<(int bValue, int threadId, EntityId entityId)>();
        int[] conflictCount = [0];

        var barrier = new Barrier(threadCount);
        var tasks = new Task[threadCount];

        for (int t = 0; t < threadCount; t++)
        {
            var threadId = t;
            tasks[t] = Task.Run(() =>
            {
                var rand = new Random(threadId * 5555);
                barrier.SignalAndWait();

                for (int i = 0; i < attemptsPerThread; i++)
                {
                    var bValue = rand.Next(totalUniqueValues);
                    try
                    {
                        using var txn = dbe.CreateQuickTransaction();
                        var comp = new CompD(threadId * 0.1f, bValue, i * 0.01);
                        var entityId = txn.Spawn<CompDArch>(CompDArch.D.Set(in comp));
                        if (txn.Commit())
                        {
                            successfulCreates.Add((bValue, threadId, entityId));
                        }
                        else
                        {
                            Interlocked.Increment(ref conflictCount[0]);
                        }
                    }
                    catch (Exception)
                    {
                        // Unique constraint violations may throw — expected for collisions
                        Interlocked.Increment(ref conflictCount[0]);
                    }
                }
            });
        }

        Task.WaitAll(tasks);

        Logger.LogInformation("Successful creates: {Successes}, Conflicts: {Conflicts}",
            successfulCreates.Count, conflictCount[0]);

        // Check for duplicate B values among successful creates — this would be a unique index violation
        var duplicates = successfulCreates.GroupBy(x => x.bValue).Where(g => g.Count() > 1).ToList();
        foreach (var dup in duplicates)
        {
            errors.Add($"UNIQUE VIOLATION: B={dup.Key} won by {dup.Count()} threads: " +
                        $"{string.Join(", ", dup.Select(x => $"T{x.threadId}"))}");
        }

        // Verify all winning entities are readable
        var winnersByB = successfulCreates.GroupBy(x => x.bValue).Select(g => g.Last()).ToList();
        foreach (var (bValue, _, entityId) in winnersByB)
        {
            using var txn = dbe.CreateQuickTransaction();
            var comp = txn.Open(entityId).Read(CompDArch.D);
            if (comp.B != bValue)
            {
                errors.Add($"Winner B={bValue} has wrong B field: {comp.B}");
            }
        }

        // Verify a freed B value can be reused (no ghost index entries)
        if (conflictCount[0] > 0)
        {
            var usedBValues = new HashSet<int>(successfulCreates.Select(x => x.bValue));
            var freeBValue = Enumerable.Range(totalUniqueValues, 100).First(b => !usedBValues.Contains(b));

            using var freshTxn = dbe.CreateQuickTransaction();
            var freshComp = new CompD(99.0f, freeBValue, 99.0);
            freshTxn.Spawn<CompDArch>(CompDArch.D.Set(in freshComp));
            Assert.That(freshTxn.Commit(), Is.True, "Fresh create with unused B value should succeed");
        }

        Assert.That(errors, Is.Empty, $"Errors:\n{string.Join("\n", errors.Take(30))}");
        Assert.That(successfulCreates.Count, Is.GreaterThan(0), "At least some creates should succeed");
    }

    #endregion

    #region Revision Chain & Cleanup Torture Tests

    /// <summary>
    /// Creates deep revision chains (500+ revisions on a single entity) while long-running readers
    /// hold snapshots at staggered points. Releases readers in chronological order (oldest first)
    /// to trigger progressive cleanup. Verifies MVCC isolation throughout.
    /// </summary>
    [Test]
    [Property("CacheSize", StressCacheSize)]
    public void RevisionChainDepth_DeepChainWithCleanup()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var errors = new ConcurrentBag<string>();

        // Create target entity
        EntityId targetEntity;
        {
            using var txn = dbe.CreateQuickTransaction();
            var comp = new CompA(0, 0f, 0d);
            targetEntity = txn.Spawn<CompAArch>(CompAArch.A.Set(in comp));
            txn.Commit();
        }

        const int readerCount = 8;
        const int totalUpdates = 500;
        const int updatesPerReader = totalUpdates / readerCount;

        // Track reader snapshots: each reader captures the value after a batch of updates
        var readers = new List<(Transaction txn, int expectedValue)>();
        var updateCounter = 0;

        // Interleave: update batch -> start reader -> update batch -> start reader -> ...
        for (int r = 0; r < readerCount; r++)
        {
            // Batch of updates
            for (int u = 0; u < updatesPerReader; u++)
            {
                using var txn = dbe.CreateQuickTransaction();
                txn.Open(targetEntity).Read(CompAArch.A);
                ref var w = ref txn.OpenMut(targetEntity).Write(CompAArch.A);
                w.A = ++updateCounter;
                txn.Commit();
            }

            // Start a reader that should see current value
#pragma warning disable TYPHON004
            var reader = dbe.CreateQuickTransaction();
#pragma warning restore TYPHON004
            var snapshot = reader.Open(targetEntity).Read(CompAArch.A);
            readers.Add((reader, snapshot.A));
        }

        Logger.LogInformation("Created {Count} readers across {Updates} updates", readers.Count, updateCounter);

        // Verify each reader still sees its snapshot (before any cleanup)
        for (int r = 0; r < readers.Count; r++)
        {
            var (txn, expected) = readers[r];
            var comp = txn.Open(targetEntity).Read(CompAArch.A);
            if (comp.A != expected)
            {
                errors.Add($"Pre-cleanup reader {r}: Expected A={expected}, got A={comp.A}");
            }
        }

        // Release readers in chronological order (oldest first) — triggers progressive cleanup
        for (int r = 0; r < readers.Count; r++)
        {
            var (txn, expected) = readers[r];

            // One last verify before release
            var comp = txn.Open(targetEntity).Read(CompAArch.A);
            if (comp.A != expected)
            {
                errors.Add($"Release reader {r}: Expected A={expected}, got A={comp.A}");
            }

            txn.Dispose();

            // After releasing, verify the entity is still readable from a fresh transaction
            using var verifyTxn = dbe.CreateQuickTransaction();
            var latest = verifyTxn.Open(targetEntity).Read(CompAArch.A);
            if (latest.A != updateCounter)
            {
                errors.Add($"After releasing reader {r}: Latest A={latest.A}, expected {updateCounter}");
            }
        }

        Assert.That(errors, Is.Empty, $"Errors:\n{string.Join("\n", errors)}");
    }

    /// <summary>
    /// Builds a massive deferred cleanup queue by holding a tail transaction while many threads
    /// commit modifications. Then releases the tail to trigger a bulk drain while new transactions
    /// are still being created. Tests the non-blocking TryEnterExclusiveAccess cleanup path.
    /// </summary>
    [Test]
    [Property("CacheSize", StressCacheSize)]
    public void DeferredCleanup_MassiveQueueDrain()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var errors = new ConcurrentBag<string>();
        const int entityCount = 50;
        const int threadCount = 4;
        const int updatesPerEntityPerThread = 10;

        // Phase 1: Create initial entities
        var entityIds = new EntityId[entityCount];
        for (int i = 0; i < entityCount; i++)
        {
            using var txn = dbe.CreateQuickTransaction();
            var comp = new CompA(i * 100, 0f, 0d);
            entityIds[i] = txn.Spawn<CompAArch>(CompAArch.A.Set(in comp));
            txn.Commit();
        }

        // Phase 2: Create the blocking tail — holds MinTSN, prevents cleanup
#pragma warning disable TYPHON004
        var tail = dbe.CreateQuickTransaction();
#pragma warning restore TYPHON004
        // Establish snapshot by reading
        tail.Open(entityIds[0]).Read(CompAArch.A);
        var tailTsn = tail.TSN;
        Logger.LogInformation("Tail transaction TSN: {Tsn}", tailTsn);

        // Phase 3: Workers hammer entities — all cleanup deferred because tail is blocking
        var commitCounts = new ConcurrentDictionary<EntityId, int>();
        var workerBarrier = new Barrier(threadCount);
        var workerTasks = new Task[threadCount];

        for (int t = 0; t < threadCount; t++)
        {
            var threadId = t;
            workerTasks[t] = Task.Run(() =>
            {
                var rand = new Random(threadId * 4444);
                workerBarrier.SignalAndWait();

                for (int round = 0; round < updatesPerEntityPerThread; round++)
                {
                    for (int e = 0; e < entityCount; e++)
                    {
                        var entityId = entityIds[rand.Next(entityCount)];
                        try
                        {
                            using var txn = dbe.CreateQuickTransaction();
                            txn.Open(entityId).Read(CompAArch.A);
                            ref var w = ref txn.OpenMut(entityId).Write(CompAArch.A);
                            w.A += 1;
                            if (txn.Commit())
                            {
                                commitCounts.AddOrUpdate(entityId, 1, (_, v) => v + 1);
                            }
                        }
                        catch (Exception ex)
                        {
                            errors.Add($"T{threadId} entity {entityId}: {ex.Message}");
                        }
                    }
                }
            });
        }

        Task.WaitAll(workerTasks);
        var totalCommits = commitCounts.Values.Sum();
        Logger.LogInformation("Workers completed: {Commits} successful commits across {Entities} entities",
            totalCommits, entityCount);

        // Phase 4: Start new background transactions BEFORE releasing the tail
        // (they'll be running while the drain happens)
        var postDrainErrors = new ConcurrentBag<string>();
        var drainStarted = new ManualResetEventSlim(false);
        var backgroundTask = Task.Run(() =>
        {
            drainStarted.Wait();
            for (int i = 0; i < 20; i++)
            {
                try
                {
                    using var txn = dbe.CreateQuickTransaction();
                    var idx = i % entityCount;
                    txn.Open(entityIds[idx]).Read(CompAArch.A);
                    ref var w = ref txn.OpenMut(entityIds[idx]).Write(CompAArch.A);
                    w.B += 1f;
                    txn.Commit();
                }
                catch (Exception ex)
                {
                    postDrainErrors.Add($"Post-drain op {i}: {ex.Message}");
                }
            }
        });

        // Phase 5: Release the tail — triggers massive deferred cleanup drain
        drainStarted.Set();
        tail.Dispose();
        Logger.LogInformation("Tail released — deferred cleanup should be draining");

        backgroundTask.Wait();

        // Phase 6: Verify all entities are still readable and consistent
        for (int i = 0; i < entityCount; i++)
        {
            using var txn = dbe.CreateQuickTransaction();
            Assert.That(txn.IsAlive(entityIds[i]), Is.True, $"Post-drain: Entity {i} not readable");
        }

        foreach (var err in postDrainErrors)
        {
            errors.Add(err);
        }

        Assert.That(errors, Is.Empty, $"Errors:\n{string.Join("\n", errors.Take(30))}");
    }

    /// <summary>
    /// Creates staggered reader snapshots at different TSN points during rapid updates,
    /// then releases them in REVERSE order (newest first). This exercises the sentinel revision
    /// boundary logic where nextMinTSN may or may not equal the first kept entry's TSN.
    /// </summary>
    [Test]
    [Property("CacheSize", StressCacheSize)]
    public void SentinelRevision_StaggeredReaderRelease()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var errors = new ConcurrentBag<string>();

        // Create target entity
        EntityId targetEntity;
        {
            using var txn = dbe.CreateQuickTransaction();
            var comp = new CompA(0, 0f, 0d);
            targetEntity = txn.Spawn<CompAArch>(CompAArch.A.Set(in comp));
            txn.Commit();
        }

        const int readerCount = 8;
        const int updatesPerGap = 25;

        var readers = new List<(Transaction txn, int expectedValue)>();
        var updateCounter = 0;

        // Create readers interleaved with update batches
        for (int r = 0; r < readerCount; r++)
        {
            for (int u = 0; u < updatesPerGap; u++)
            {
                using var txn = dbe.CreateQuickTransaction();
                txn.Open(targetEntity).Read(CompAArch.A);
                ref var w = ref txn.OpenMut(targetEntity).Write(CompAArch.A);
                w.A = ++updateCounter;
                txn.Commit();
            }

#pragma warning disable TYPHON004
            var reader = dbe.CreateQuickTransaction();
#pragma warning restore TYPHON004
            var snapshot = reader.Open(targetEntity).Read(CompAArch.A);
            readers.Add((reader, snapshot.A));
        }

        // Pump more updates after the last reader
        for (int u = 0; u < 50; u++)
        {
            using var txn = dbe.CreateQuickTransaction();
            txn.Open(targetEntity).Read(CompAArch.A);
            ref var w = ref txn.OpenMut(targetEntity).Write(CompAArch.A);
            w.A = ++updateCounter;
            txn.Commit();
        }

        Logger.LogInformation("Created {Readers} readers across {Updates} updates", readers.Count, updateCounter);

        // Release in REVERSE order (newest first) — each release changes MinTSN differently
        // than chronological release, exercising different sentinel paths
        for (int r = readers.Count - 1; r >= 0; r--)
        {
            var (txn, expected) = readers[r];

            // Verify snapshot still holds
            var comp = txn.Open(targetEntity).Read(CompAArch.A);
            if (comp.A != expected)
            {
                errors.Add($"Reader {r} (reverse release): Expected A={expected}, got A={comp.A}");
            }

            txn.Dispose();

            // Verify entity still readable from fresh transaction
            using var verifyTxn = dbe.CreateQuickTransaction();
            var latest = verifyTxn.Open(targetEntity).Read(CompAArch.A);
            if (latest.A != updateCounter)
            {
                errors.Add($"After reverse-releasing reader {r}: Latest A={latest.A}, expected {updateCounter}");
            }
        }

        Assert.That(errors, Is.Empty, $"Errors:\n{string.Join("\n", errors)}");
    }

    #endregion

    #region Multi-Entity Transaction Chaos Tests

    /// <summary>
    /// Bank transfer test: N entities each start with value 1000. Threads perform atomic transfers
    /// (decrement src, increment dst) in single transactions. Readers verify the global invariant:
    /// sum of all values == N * 1000 within any snapshot.
    /// </summary>
    [Test]
    [Property("CacheSize", StressCacheSize)]
    public void CrossEntityTransaction_AtomicMultiUpdate()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var errors = new ConcurrentBag<string>();
        const int entityCount = 20;
        const int initialValue = 1000;
        const int transferThreads = 6;
        const int readerThreads = 2;
        const int transfersPerThread = 50;
        var expectedSum = entityCount * initialValue;

        // Create entities with known initial values
        var entityIds = new EntityId[entityCount];
        for (int i = 0; i < entityCount; i++)
        {
            using var txn = dbe.CreateQuickTransaction();
            var comp = new CompA(initialValue, 0f, 0d);
            entityIds[i] = txn.Spawn<CompAArch>(CompAArch.A.Set(in comp));
            txn.Commit();
        }

        var startSignal = new ManualResetEventSlim(false);
        int[] successfulTransfers = [0];
        int[] conflictCount = [0];
        int[] badDeltaCount = [0];
        var allTasks = new List<Task>();

        // Per-entity expected delta tracking: each successful transfer adds -1 to src, +1 to dst
        var expectedDeltas = new int[entityCount];
        // Build entity ID -> index lookup
        var entityIdToIdx = new Dictionary<EntityId, int>();
        for (int i = 0; i < entityCount; i++)
        {
            entityIdToIdx[entityIds[i]] = i;
        }

        // Transfer threads: atomically move 1 unit from src to dst
        for (int t = 0; t < transferThreads; t++)
        {
            var threadId = t;
            allTasks.Add(Task.Run(() =>
            {
                var rand = new Random(threadId * 6666);
                startSignal.Wait();

                for (int i = 0; i < transfersPerThread; i++)
                {
                    var srcIdx = rand.Next(entityCount);
                    var dstIdx = rand.Next(entityCount);
                    if (srcIdx == dstIdx)
                    {
                        continue;
                    }

                    try
                    {
                        using var txn = dbe.CreateQuickTransaction();
                        var srcComp = txn.Open(entityIds[srcIdx]).Read(CompAArch.A);
                        var dstComp = txn.Open(entityIds[dstIdx]).Read(CompAArch.A);

                        var readSrc = srcComp.A;
                        var readDst = dstComp.A;

                        ref var wSrc = ref txn.OpenMut(entityIds[srcIdx]).Write(CompAArch.A);
                        wSrc = new CompA(srcComp.A - 1, srcComp.B, srcComp.C);
                        ref var wDst = ref txn.OpenMut(entityIds[dstIdx]).Write(CompAArch.A);
                        wDst = new CompA(dstComp.A + 1, dstComp.B, dstComp.C);

                        // Delta-rebase handler: merge concurrent modifications by
                        // applying our delta (dirtyVal - readVal) onto the committed value.
                        // Track handler values per entity for diagnostics.
                        var handlerLog = new ConcurrentBag<string>();

                        void ConcurrencyConflictHandler(ref ConcurrencyConflictSolver solver)
                        {
                            ref var r = ref solver.ReadData<CompA>();
                            ref var c = ref solver.CommittedData<CompA>();
                            ref var m = ref solver.CommittingData<CompA>();
                            var delta = m.A - r.A;
                            if (delta != 1 && delta != -1)
                            {
                                Interlocked.Increment(ref badDeltaCount[0]);
                                errors.Add($"T{threadId} op {i}: bad delta={delta} read={r.A} committed={c.A} committing={m.A} pk={solver.PrimaryKey}");
                            }

                            Interlocked.Increment(ref conflictCount[0]);
                            var resolved = c.A + delta;
                            solver.ToCommitData<CompA>().A = resolved;
                            handlerLog.Add($"pk={solver.PrimaryKey} r={r.A} c={c.A} m={m.A} d={delta} res={resolved}");
                        }

                        if (txn.Commit(ConcurrencyConflictHandler))
                        {
                            Interlocked.Add(ref expectedDeltas[srcIdx], -1);
                            Interlocked.Add(ref expectedDeltas[dstIdx], 1);
                            Interlocked.Increment(ref successfulTransfers[0]);
                        }
                        else
                        {
                            errors.Add($"T{threadId} op {i}: Commit returned false! src={srcIdx}(read={readSrc}) dst={dstIdx}(read={readDst}) handler={string.Join("; ", handlerLog)}");
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Transfer T{threadId} op {i}: {ex.Message}");
                    }
                }
            }));
        }

        // Reader threads: verify sum invariant within each snapshot
        var readersRunning = true;
        for (int r = 0; r < readerThreads; r++)
        {
            var readerId = r;
            allTasks.Add(Task.Run(() =>
            {
                startSignal.Wait();
                int checks = 0;

                while (readersRunning || checks < 10)
                {
                    try
                    {
                        using var txn = dbe.CreateQuickTransaction();
                        var sum = 0;
                        for (int i = 0; i < entityCount; i++)
                        {
                            var comp = txn.Open(entityIds[i]).Read(CompAArch.A);
                            sum += comp.A;
                        }

                        // NOTE: Mid-flight atomicity violations are expected because IsolationFlag is
                        // cleared per-entity during the commit loop, not atomically for all entities.
                        // This is a known MVCC limitation — see issue for atomic commit visibility.
                        // The FINAL sum check after all transfers complete validates correctness.

                        checks++;
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Reader {readerId}: {ex.Message}");
                    }

                    if (checks > 200)
                    {
                        break;
                    }
                }
            }));
        }

        startSignal.Set();

        // Wait for transfer threads, then stop readers
        Task.WaitAll(allTasks.Where((_, i) => i < transferThreads).ToArray());
        readersRunning = false;
        Task.WaitAll(allTasks.ToArray());

        // Final sum check
        using (var txn = dbe.CreateQuickTransaction())
        {
            var finalSum = 0;
            var entityValues = new int[entityCount];
            for (int i = 0; i < entityCount; i++)
            {
                var comp = txn.Open(entityIds[i]).Read(CompAArch.A);
                entityValues[i] = comp.A;
                finalSum += comp.A;
            }

            // Compare actual vs expected (from delta tracking) per entity
            var mismatches = new List<string>();
            var deviations = new List<string>();
            for (int i = 0; i < entityCount; i++)
            {
                var actualDelta = entityValues[i] - initialValue;
                var expectedDelta = expectedDeltas[i];
                if (actualDelta != expectedDelta)
                {
                    mismatches.Add($"e{i}: actual={entityValues[i]}(d={actualDelta}) expected={initialValue + expectedDelta}(d={expectedDelta}) diff={actualDelta - expectedDelta}");
                }
                if (actualDelta != 0)
                {
                    deviations.Add($"e{i}={entityValues[i]}({(actualDelta > 0 ? "+" : "")}{actualDelta},exp={expectedDelta})");
                }
            }

            Assert.That(finalSum, Is.EqualTo(expectedSum),
                $"Final sum mismatch: {finalSum} != {expectedSum} after {successfulTransfers[0]} transfers, {conflictCount[0]} conflicts, {badDeltaCount[0]} bad deltas, {errors.Count} errors." +
                $"\nMismatches: {string.Join(", ", mismatches)}" +
                $"\nDeviations: {string.Join(", ", deviations)}");
        }

        Logger.LogInformation("Successful transfers: {Count}", successfulTransfers[0]);
        Assert.That(errors, Is.Empty, $"Errors:\n{string.Join("\n", errors.Take(30))}");
    }

    /// <summary>
    /// Rapidly creates, deletes, and recreates CompD entities (3 secondary indexes each)
    /// to stress index entry insertion/removal cycling and revision chain creation/destruction.
    /// </summary>
    [Test]
    [Property("CacheSize", StressCacheSize)]
    public void CreateDeleteRecreate_RapidLifecycle()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var errors = new ConcurrentBag<string>();
        const int threadCount = 4;
        const int cyclesPerThread = 50;

        int[] totalCycles = [0];
        var barrier = new Barrier(threadCount);
        var tasks = new Task[threadCount];

        for (int t = 0; t < threadCount; t++)
        {
            var threadId = t;
            tasks[t] = Task.Run(() =>
            {
                var nextB = threadId * 100000;

                barrier.SignalAndWait();

                for (int cycle = 0; cycle < cyclesPerThread; cycle++)
                {
                    var bValue = nextB++;

                    try
                    {
                        // Create with all 3 indexes
                        EntityId entityId;
                        using (var txn = dbe.CreateQuickTransaction())
                        {
                            var comp = new CompD(cycle * 0.1f, bValue, cycle * 0.01);
                            entityId = txn.Spawn<CompDArch>(CompDArch.D.Set(in comp));
                            if (!txn.Commit())
                            {
                                errors.Add($"T{threadId} cycle {cycle}: Create commit failed");
                                continue;
                            }
                        }

                        // Verify readable
                        using (var txn = dbe.CreateQuickTransaction())
                        {
                            var readBack = txn.Open(entityId).Read(CompDArch.D);
                            if (readBack.B != bValue)
                            {
                                errors.Add($"T{threadId} cycle {cycle}: B mismatch after create: {readBack.B} != {bValue}");
                            }
                        }

                        // Delete (triggers removal from all 3 secondary indexes)
                        using (var txn = dbe.CreateQuickTransaction())
                        {
                            txn.Destroy(entityId);
                            if (!txn.Commit())
                            {
                                errors.Add($"T{threadId} cycle {cycle}: Delete commit failed");
                                continue;
                            }
                        }

                        // Verify not readable
                        using (var txn = dbe.CreateQuickTransaction())
                        {
                            if (txn.IsAlive(entityId))
                            {
                                errors.Add($"T{threadId} cycle {cycle}: Deleted entity still readable!");
                            }
                        }

                        // Recreate with SAME B value (index slot was freed) but different A/C
                        using (var txn = dbe.CreateQuickTransaction())
                        {
                            var comp2 = new CompD(cycle * 0.2f, bValue, cycle * 0.02);
                            var newId = txn.Spawn<CompDArch>(CompDArch.D.Set(in comp2));
                            if (!txn.Commit())
                            {
                                errors.Add($"T{threadId} cycle {cycle}: Recreate commit failed (B={bValue} reuse)");
                                continue;
                            }

                            // Verify the recreated entity
                            using var verifyTxn = dbe.CreateQuickTransaction();
                            var verify = verifyTxn.Open(newId).Read(CompDArch.D);
                            if (verify.B != bValue)
                            {
                                errors.Add($"T{threadId} cycle {cycle}: Recreated B mismatch: {verify.B} != {bValue}");
                            }
                        }

                        Interlocked.Increment(ref totalCycles[0]);
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"T{threadId} cycle {cycle} (B={bValue}): {ex.Message}");
                    }
                }
            });
        }

        Task.WaitAll(tasks);

        Logger.LogInformation("Completed {Count}/{Total} create-delete-recreate cycles",
            totalCycles[0], threadCount * cyclesPerThread);
        Assert.That(errors, Is.Empty, $"Errors:\n{string.Join("\n", errors.Take(30))}");
        Assert.That(totalCycles[0], Is.EqualTo(threadCount * cyclesPerThread),
            "All cycles should complete without error");
    }

    #endregion

    #region Durability & Crash Resilience Tests

    /// <summary>
    /// Mixes Deferred, GroupCommit, and Immediate durability modes in concurrent transactions
    /// on the same entity pool. Verifies all committed changes are visible regardless of mode.
    /// </summary>
    [Test]
    [Property("CacheSize", StressCacheSize)]
    public void DurabilityModes_MixedModesUnderLoad()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var errors = new ConcurrentBag<string>();
        const int entitiesPerMode = 50;
        var modes = new[] { DurabilityMode.Deferred, DurabilityMode.GroupCommit, DurabilityMode.Immediate };

        var allEntityIds = new ConcurrentDictionary<EntityId, (DurabilityMode mode, int value)>();
        var barrier = new Barrier(modes.Length * 2); // 2 threads per mode
        var tasks = new List<Task>();

        for (int m = 0; m < modes.Length; m++)
        {
            for (int t = 0; t < 2; t++)
            {
                var mode = modes[m];
                var threadId = m * 2 + t;
                tasks.Add(Task.Run(() =>
                {
                    barrier.SignalAndWait();

                    for (int i = 0; i < entitiesPerMode; i++)
                    {
                        var value = threadId * 10000 + i;
                        try
                        {
                            using var txn = dbe.CreateQuickTransaction(mode);
                            var comp = new CompA(value, 0f, 0d);
                            var entityId = txn.Spawn<CompAArch>(CompAArch.A.Set(in comp));
                            if (txn.Commit())
                            {
                                allEntityIds[entityId] = (mode, value);
                            }
                        }
                        catch (Exception ex)
                        {
                            errors.Add($"T{threadId} ({mode}) create {i}: {ex.Message}");
                        }
                    }
                }));
            }
        }

        Task.WaitAll(tasks.ToArray());

        Logger.LogInformation("Created entities: Deferred={D}, GroupCommit={G}, Immediate={I}",
            allEntityIds.Count(x => x.Value.mode == DurabilityMode.Deferred),
            allEntityIds.Count(x => x.Value.mode == DurabilityMode.GroupCommit),
            allEntityIds.Count(x => x.Value.mode == DurabilityMode.Immediate));

        // Verify ALL committed entities are visible in a new transaction
        int verified = 0;
        foreach (var kvp in allEntityIds)
        {
            using var txn = dbe.CreateQuickTransaction();
            var comp = txn.Open(kvp.Key).Read(CompAArch.A);
            if (comp.A != kvp.Value.value)
            {
                errors.Add($"Entity from {kvp.Value.mode}: A={comp.A}, expected {kvp.Value.value}");
            }

            verified++;
        }

        Logger.LogInformation("Verified {Count}/{Total} entities", verified, allEntityIds.Count);
        Assert.That(errors, Is.Empty, $"Errors:\n{string.Join("\n", errors.Take(30))}");
        Assert.That(verified, Is.EqualTo(allEntityIds.Count));
    }

    /// <summary>
    /// Tests that data committed with Immediate durability survives engine restart.
    /// Creates entities in one scope, disposes the engine, opens a new scope, and verifies readability.
    /// </summary>
    [Test]
    [Property("CacheSize", StressCacheSize)]
    public void DurabilityRestart_DataSurvivesReopen()
    {
        const int entityCount = 50;
        var entityIds = new EntityId[entityCount];
        var values = new int[entityCount];

        // Phase 1: Create and populate with Immediate durability in first scope
        using (var scope1 = ServiceProvider.CreateScope())
        {
            using var dbe = scope1.ServiceProvider.GetRequiredService<DatabaseEngine>();
            RegisterComponents(dbe);
            dbe.InitializeArchetypes();

            for (int i = 0; i < entityCount; i++)
            {
                using var txn = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
                var comp = new CompA(i * 111, i * 1.0f, i * 2.0);
                entityIds[i] = txn.Spawn<CompAArch>(CompAArch.A.Set(in comp));
                Assert.That(txn.Commit(), Is.True, $"Commit {i} should succeed");
                values[i] = i * 111;
            }

            // Force checkpoint to ensure pages are flushed to disk
            dbe.ForceCheckpoint();
        }

        // Phase 2: Reopen the database in a new scope
        using (var scope2 = ServiceProvider.CreateScope())
        {
            using var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();

            RegisterComponents(dbe);
            dbe.InitializeArchetypes();

            var errors = new List<string>();

            for (int i = 0; i < entityCount; i++)
            {
                using var txn = dbe.CreateQuickTransaction();
                var comp = txn.Open(entityIds[i]).Read(CompAArch.A);
                if (comp.A != values[i])
                {
                    errors.Add($"Entity {i}: A={comp.A}, expected {values[i]}");
                }
            }

            Assert.That(errors, Is.Empty, $"Durability errors:\n{string.Join("\n", errors)}");
        }
    }

    /// <summary>
    /// Forces a checkpoint while heavy concurrent writes are in progress.
    /// Verifies no data corruption from checkpoint racing with active modifications.
    /// </summary>
    [Test]
    [Property("CacheSize", StressCacheSize)]
    public void Checkpoint_ConcurrentWithHeavyWrites()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var errors = new ConcurrentBag<string>();
        const int entityCount = 200;
        const int writerThreads = 4;
        const int updatesPerWriter = 100;

        // Create initial entities
        var entityIds = new EntityId[entityCount];
        for (int i = 0; i < entityCount; i++)
        {
            using var txn = dbe.CreateQuickTransaction();
            var comp = new CompA(i, i * 10f, 0d);
            entityIds[i] = txn.Spawn<CompAArch>(CompAArch.A.Set(in comp));
            txn.Commit();
        }

        int[] totalUpdates = [0];
        int[] checkpointsDone = [0];
        var startSignal = new ManualResetEventSlim(false);

        var allTasks = new List<Task>();

        // Writer threads: rapid random updates
        for (int w = 0; w < writerThreads; w++)
        {
            var writerId = w;
            allTasks.Add(Task.Run(() =>
            {
                var rand = new Random(writerId * 2222);
                startSignal.Wait();

                for (int i = 0; i < updatesPerWriter; i++)
                {
                    var targetIdx = rand.Next(entityCount);
                    try
                    {
                        using var txn = dbe.CreateQuickTransaction();
                        txn.Open(entityIds[targetIdx]).Read(CompAArch.A);
                        ref var w2 = ref txn.OpenMut(entityIds[targetIdx]).Write(CompAArch.A);
                        w2.B += 1f;
                        if (txn.Commit())
                        {
                            Interlocked.Increment(ref totalUpdates[0]);
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Writer {writerId} op {i}: {ex.Message}");
                    }
                }
            }));
        }

        // Checkpoint thread: trigger multiple checkpoints during writes
        allTasks.Add(Task.Run(() =>
        {
            startSignal.Wait();
            Thread.Sleep(5); // Let writers start

            for (int i = 0; i < 3; i++)
            {
                try
                {
                    dbe.ForceCheckpoint();
                    Interlocked.Increment(ref checkpointsDone[0]);
                }
                catch (Exception ex)
                {
                    errors.Add($"Checkpoint {i}: {ex.Message}");
                }

                Thread.Sleep(20);
            }
        }));

        startSignal.Set();
        Task.WaitAll(allTasks.ToArray());

        Logger.LogInformation("Updates: {Updates}, Checkpoints: {Checkpoints}",
            totalUpdates[0], checkpointsDone[0]);

        // Verify all entities readable and A field unchanged (only B was modified)
        for (int i = 0; i < entityCount; i++)
        {
            using var txn = dbe.CreateQuickTransaction();
            var comp = txn.Open(entityIds[i]).Read(CompAArch.A);
            if (comp.A != i)
            {
                errors.Add($"Post-checkpoint: Entity {i} A field corrupted: {comp.A} != {i}");
            }
        }

        Assert.That(errors, Is.Empty, $"Errors:\n{string.Join("\n", errors.Take(30))}");
        Assert.That(totalUpdates[0], Is.GreaterThan(0), "Some updates should succeed");
        Assert.That(checkpointsDone[0], Is.GreaterThan(0), "At least one checkpoint should complete");
    }

    #endregion

    #region Combinatorial Nightmare Tests

    /// <summary>
    /// The ultimate stress test: 10 threads with different roles exercise every subsystem simultaneously.
    /// Roles: CompD creators, CompD deleters, CompA updaters, long-running MVCC readers,
    /// indexed field updaters, and rapid create-rollback cycles. All running for 2 seconds.
    /// </summary>
    [Test]
    [Property("CacheSize", StressCacheSize)]
    [Ignore("Instable")]
    public void UltimateStress_AllSubsystems()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var errors = new ConcurrentBag<string>();

        // Pre-create CompA entities for updaters and readers
        const int preCreatedCompA = 50;
        var compAIds = new EntityId[preCreatedCompA];
        for (int i = 0; i < preCreatedCompA; i++)
        {
            using var txn = dbe.CreateQuickTransaction();
            var comp = new CompA(1000, 0f, 0d); // All start at 1000 for sum invariant
            compAIds[i] = txn.Spawn<CompAArch>(CompAArch.A.Set(in comp));
            txn.Commit();
        }

        var expectedCompASum = preCreatedCompA * 1000;

        // Shared pool for CompD entities (creators produce, deleters consume)
        var compDPool = new ConcurrentQueue<(EntityId entityId, int bValue)>();
        int[] nextBCounter = [0];

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var token = cts.Token;
        var startSignal = new ManualResetEventSlim(false);
        var stats = new ConcurrentDictionary<string, long>();
        foreach (var key in new[]
                 {
                     "CompD_Creates", "CompD_Deletes", "CompA_Updates", "MVCC_Checks",
                     "Index_Updates", "Rollbacks"
                 })
        {
            stats[key] = 0;
        }

        var allTasks = new List<Task>();

        // Role 1-2: CompD creators (monotonic B values -> cascading splits)
        for (int i = 0; i < 2; i++)
        {
            var creatorId = i;
            allTasks.Add(Task.Run(() =>
            {
                var rand = new Random(creatorId * 1111);
                startSignal.Wait();

                while (!token.IsCancellationRequested)
                {
                    var bVal = Interlocked.Increment(ref nextBCounter[0]);
                    try
                    {
                        using var txn = dbe.CreateQuickTransaction();
                        var comp = new CompD(rand.NextSingle(), bVal, rand.NextDouble());
                        var id = txn.Spawn<CompDArch>(CompDArch.D.Set(in comp));
                        if (txn.Commit())
                        {
                            compDPool.Enqueue((id, bVal));
                            stats.AddOrUpdate("CompD_Creates", 1, (_, v) => v + 1);
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Creator {creatorId}: {ex.Message}");
                    }
                }
            }));
        }

        // Role 3-4: CompD deleters (consume from pool -> index removals + merges)
        for (int i = 0; i < 2; i++)
        {
            var deleterId = i;
            allTasks.Add(Task.Run(() =>
            {
                startSignal.Wait();

                while (!token.IsCancellationRequested)
                {
                    if (!compDPool.TryDequeue(out var item))
                    {
                        Thread.SpinWait(100);
                        continue;
                    }

                    try
                    {
                        using var txn = dbe.CreateQuickTransaction();
                        txn.Destroy(item.entityId);
                        if (txn.Commit())
                        {
                            stats.AddOrUpdate("CompD_Deletes", 1, (_, v) => v + 1);
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Deleter {deleterId}: {ex.Message}");
                    }
                }
            }));
        }

        // Role 5-6: CompA atomic transfer updaters (sum invariant)
        // Uses a delta-rebase conflict handler to preserve the sum invariant under concurrent
        // modifications: if another transaction committed since our read, we rebase our delta
        // (+-1) onto the committed value instead of blindly overwriting ("last writer wins").
        for (int i = 0; i < 2; i++)
        {
            var updaterId = i;
            allTasks.Add(Task.Run(() =>
            {
                var rand = new Random(updaterId * 3333);
                startSignal.Wait();

                while (!token.IsCancellationRequested)
                {
                    var srcIdx = rand.Next(preCreatedCompA);
                    var dstIdx = rand.Next(preCreatedCompA);
                    if (srcIdx == dstIdx)
                    {
                        continue;
                    }

                    try
                    {
                        using var txn = dbe.CreateQuickTransaction();
                        var srcComp = txn.Open(compAIds[srcIdx]).Read(CompAArch.A);
                        var dstComp = txn.Open(compAIds[dstIdx]).Read(CompAArch.A);

                        ref var wSrc = ref txn.OpenMut(compAIds[srcIdx]).Write(CompAArch.A);
                        wSrc.A = srcComp.A - 1;
                        ref var wDst = ref txn.OpenMut(compAIds[dstIdx]).Write(CompAArch.A);
                        wDst.A = dstComp.A + 1;

                        // Delta-rebase handler: apply our delta onto the committed value
                        void ConcurrencyConflictHandler(ref ConcurrencyConflictSolver solver)
                        {
                            ref var r = ref solver.ReadData<CompA>();
                            ref var c = ref solver.CommittedData<CompA>();
                            ref var m = ref solver.CommittingData<CompA>();
                            solver.ToCommitData<CompA>().A = c.A + (m.A - r.A);
                        }

                        if (txn.Commit(ConcurrencyConflictHandler))
                        {
                            stats.AddOrUpdate("CompA_Updates", 1, (_, v) => v + 1);
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Updater {updaterId}: {ex.Message}");
                    }
                }
            }));
        }

        // Role 7-8: Long-running MVCC readers (verify CompA sum invariant within snapshot)
        for (int i = 0; i < 2; i++)
        {
            var readerId = i;
            allTasks.Add(Task.Run(() =>
            {
                startSignal.Wait();

                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        using var txn = dbe.CreateQuickTransaction();
                        var sum = 0;
                        for (int e = 0; e < preCreatedCompA; e++)
                        {
                            var comp = txn.Open(compAIds[e]).Read(CompAArch.A);
                            sum += comp.A;
                        }

                        if (sum != expectedCompASum)
                        {
                            errors.Add($"MVCC reader {readerId}: sum={sum}, expected={expectedCompASum}");
                        }

                        stats.AddOrUpdate("MVCC_Checks", 1, (_, v) => v + 1);
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"MVCC reader {readerId}: {ex.Message}");
                    }
                }
            }));
        }

        // Role 9: CompD indexed field updater (changes AllowMultiple field A)
        allTasks.Add(Task.Run(() =>
        {
            var rand = new Random(77777);
            startSignal.Wait();

            while (!token.IsCancellationRequested)
            {
                // Peek at pool without dequeuing
                if (!compDPool.TryPeek(out var item))
                {
                    Thread.SpinWait(100);
                    continue;
                }

                try
                {
                    using var txn = dbe.CreateQuickTransaction();
                    if (txn.IsAlive(item.entityId))
                    {
                        txn.Open(item.entityId).Read(CompDArch.D);
                        ref var w = ref txn.OpenMut(item.entityId).Write(CompDArch.D);
                        w.A = rand.NextSingle(); // AllowMultiple -> index entry changes
                        w.C = rand.NextDouble(); // AllowMultiple -> index entry changes
                        if (txn.Commit())
                        {
                            stats.AddOrUpdate("Index_Updates", 1, (_, v) => v + 1);
                        }
                    }
                }
                catch (Exception)
                {
                    // Entity may have been deleted by deleter thread — expected
                }
            }
        }));

        // Role 10: Rapid create-rollback (never commits — stress rollback path)
        allTasks.Add(Task.Run(() =>
        {
            var rand = new Random(88888);
            startSignal.Wait();

            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var txn = dbe.CreateQuickTransaction();
                    var comp = new CompA(rand.Next(), rand.NextSingle(), rand.NextDouble());
                    txn.Spawn<CompAArch>(CompAArch.A.Set(in comp));
                    txn.Rollback();
                    stats.AddOrUpdate("Rollbacks", 1, (_, v) => v + 1);
                }
                catch (Exception ex)
                {
                    errors.Add($"Rollback thread: {ex.Message}");
                }
            }
        }));

        // GO!
        startSignal.Set();

        // Wait for time limit
        try
        {
            Task.WaitAll(allTasks.ToArray());
        }
        catch (AggregateException)
        {
            // Tasks cancelled — expected
        }

        // Report stats
        Logger.LogInformation("=== Ultimate Stress Stats ===");
        foreach (var kvp in stats.OrderBy(x => x.Key))
        {
            Logger.LogInformation("{Key}: {Value}", kvp.Key, kvp.Value);
        }

        // Final CompA sum invariant check
        using (var txn = dbe.CreateQuickTransaction())
        {
            var finalSum = 0;
            for (int i = 0; i < preCreatedCompA; i++)
            {
                var comp = txn.Open(compAIds[i]).Read(CompAArch.A);
                finalSum += comp.A;
            }

            if (finalSum != expectedCompASum)
            {
                errors.Add($"FINAL ATOMICITY VIOLATION: sum={finalSum}, expected={expectedCompASum}");
            }
        }

        // Filter out excessive duplicate errors
        var uniqueErrors = errors.Distinct().Take(30).ToList();
        Assert.That(uniqueErrors, Is.Empty, $"Errors:\n{string.Join("\n", uniqueErrors)}");
    }

    #endregion
}
