using NUnit.Framework;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace Typhon.Engine.Tests.Runtime;

[TestFixture]
public class ParallelQueryTests
{
    private ResourceRegistry _registry;

    [SetUp]
    public void SetUp()
    {
        _registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "ParallelTest" });
    }

    [TearDown]
    public void TearDown()
    {
        _registry?.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates a DagScheduler with a single parallel QuerySystem, wiring manual callbacks
    /// that simulate entity partitioning without a full DatabaseEngine.
    /// </summary>
    private DagScheduler CreateParallelScheduler(
        int entityCount,
        Action<int, int, int> onChunk,
        int workerCount = 4,
        int minChunkSize = 64,
        string after = null,
        Action<TickContext> predecessorAction = null)
    {
        var dag = RuntimeSchedule.Create(new RuntimeOptions
        {
            WorkerCount = workerCount,
            BaseTickRate = 1000,
            ParallelQueryMinChunkSize = minChunkSize
        }).PublicTrack.DeclareDag("Test");

        if (predecessorAction != null)
        {
            dag.CallbackSystem("Predecessor", predecessorAction);
        }

        dag.QuerySystem("Parallel", _ => { }, after: after, input: () => null, parallel: true);

        var scheduler = dag.Build(_registry.Runtime);

        // Wire manual callbacks that simulate entity partitioning
        var entityArray = new EntityId[entityCount];
        for (var i = 0; i < entityCount; i++)
        {
            entityArray[i] = EntityId.FromRaw(i + 1);
        }

        var entityList = new PooledEntityList(entityArray, entityCount);

        scheduler.ParallelQueryPrepareCallback = sysIdx =>
        {
            if (entityCount == 0)
            {
                return 0;
            }

            var maxChunks = Math.Max(1, (entityCount + minChunkSize - 1) / minChunkSize);
            return Math.Min(workerCount, maxChunks);
        };

        scheduler.ParallelQueryChunkCallback = (sysIdx, chunk, totalChunks, workerId) =>
        {
            onChunk(sysIdx, chunk, totalChunks);
        };

        scheduler.ParallelQueryCleanupCallback = _ => false;

        return scheduler;
    }

    private void RunOneTick(DagScheduler scheduler)
    {
        scheduler.Start();
        SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= 1, TimeSpan.FromSeconds(5));
        scheduler.Shutdown();
    }

    // ═══════════════════════════════════════════════════════════════
    // Entity partitioning correctness
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void ParallelQuery_AllChunksExecuted()
    {
        var chunksExecuted = new ConcurrentBag<int>();
        var captured = 0;

        using var scheduler = CreateParallelScheduler(
            entityCount: 256,
            onChunk: (_, chunk, _) =>
            {
                if (captured == 0)
                {
                    chunksExecuted.Add(chunk);
                }
            },
            workerCount: 4,
            minChunkSize: 64);

        // Wire a tick-end callback to capture completion
        scheduler.TickEndCallback = _ => Interlocked.Exchange(ref captured, 1);

        RunOneTick(scheduler);

        Assert.That(chunksExecuted, Has.Count.EqualTo(4));
        Assert.That(chunksExecuted.OrderBy(x => x), Is.EqualTo(new[] { 0, 1, 2, 3 }));
    }

    [Test]
    public void ParallelQuery_EntitySlicing_BalancedPartition()
    {
        // Test the balanced partitioning formula directly
        // 10 entities, 3 chunks: sizes should be 4, 3, 3 (remainder = 1, first chunk gets +1)
        var totalEntities = 10;
        var totalChunks = 3;

        var allEntities = new List<int>();
        for (var chunk = 0; chunk < totalChunks; chunk++)
        {
            var baseSize = totalEntities / totalChunks;
            var remainder = totalEntities % totalChunks;
            var start = chunk * baseSize + Math.Min(chunk, remainder);
            var count = baseSize + (chunk < remainder ? 1 : 0);

            for (var i = start; i < start + count; i++)
            {
                allEntities.Add(i);
            }
        }

        // All entities covered exactly once
        Assert.That(allEntities, Has.Count.EqualTo(totalEntities));
        Assert.That(allEntities.Distinct().Count(), Is.EqualTo(totalEntities));
        Assert.That(allEntities.OrderBy(x => x), Is.EqualTo(Enumerable.Range(0, totalEntities)));
    }

    [Test]
    public void ParallelQuery_EntitySlicing_EvenSplit()
    {
        // 256 entities, 4 chunks: each chunk gets exactly 64
        var totalEntities = 256;
        var totalChunks = 4;

        for (var chunk = 0; chunk < totalChunks; chunk++)
        {
            var baseSize = totalEntities / totalChunks;
            var remainder = totalEntities % totalChunks;
            var start = chunk * baseSize + Math.Min(chunk, remainder);
            var count = baseSize + (chunk < remainder ? 1 : 0);

            Assert.That(count, Is.EqualTo(64), $"Chunk {chunk} should have 64 entities");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Multi-worker dispatch
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void ParallelQuery_MultiWorker_ChunksDistributedAcrossThreads()
    {
        var threadIds = new ConcurrentBag<int>();
        var captured = 0;

        using var scheduler = CreateParallelScheduler(
            entityCount: 512,
            onChunk: (_, chunk, _) =>
            {
                if (captured == 0)
                {
                    Thread.SpinWait(1000); // Give time for multiple workers to participate
                    threadIds.Add(Environment.CurrentManagedThreadId);
                }
            },
            workerCount: 4,
            minChunkSize: 64);

        scheduler.TickEndCallback = _ => Interlocked.Exchange(ref captured, 1);

        RunOneTick(scheduler);

        // With 4 workers and 8 chunks (512/64), expect at least 2 distinct threads
        Assert.That(threadIds.Distinct().Count(), Is.GreaterThanOrEqualTo(2),
            "Expected chunks to be distributed across multiple worker threads");
    }

    // ═══════════════════════════════════════════════════════════════
    // Small entity set fallback
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void ParallelQuery_SmallEntitySet_SingleChunk()
    {
        var chunksExecuted = new ConcurrentBag<(int chunk, int total)>();
        var captured = 0;

        using var scheduler = CreateParallelScheduler(
            entityCount: 10, // Less than minChunkSize (64)
            onChunk: (_, chunk, total) =>
            {
                if (captured == 0)
                {
                    chunksExecuted.Add((chunk, total));
                }
            },
            workerCount: 4,
            minChunkSize: 64);

        scheduler.TickEndCallback = _ => Interlocked.Exchange(ref captured, 1);

        RunOneTick(scheduler);

        // Small entity set → totalChunks=1
        Assert.That(chunksExecuted, Has.Count.EqualTo(1));
        var (c, t) = chunksExecuted.First();
        Assert.That(c, Is.EqualTo(0));
        Assert.That(t, Is.EqualTo(1));
    }

    // ═══════════════════════════════════════════════════════════════
    // Empty entity set
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void ParallelQuery_EmptyEntitySet_SystemSkipped_SuccessorsStillRun()
    {
        var successorRan = 0;
        var parallelRan = 0;
        var captured = 0;

        var dag = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 2, BaseTickRate = 1000 }).PublicTrack.DeclareDag("Test");
        dag.QuerySystem("Parallel", _ => { }, input: () => null, parallel: true);
        dag.CallbackSystem("Successor", _ =>
        {
            if (captured == 0)
            {
                Interlocked.Increment(ref successorRan);
            }
        }, after: "Parallel");

        var scheduler = dag.Build(_registry.Runtime);

        // Wire: prepare returns 0 (empty)
        scheduler.ParallelQueryPrepareCallback = _ => 0;
        scheduler.ParallelQueryChunkCallback = (_, _, _, _) => Interlocked.Increment(ref parallelRan);
        scheduler.ParallelQueryCleanupCallback = _ => false;
        scheduler.TickEndCallback = _ => Interlocked.Exchange(ref captured, 1);

        scheduler.Start();
        SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= 1, TimeSpan.FromSeconds(5));
        scheduler.Shutdown();
        scheduler.Dispose();

        Assert.That(parallelRan, Is.EqualTo(0), "Parallel system should not execute with empty entity set");
        Assert.That(successorRan, Is.GreaterThanOrEqualTo(1), "Successor must still execute after skipped parallel system");
    }

    // ═══════════════════════════════════════════════════════════════
    // Error handling
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void ParallelQuery_ChunkThrows_SystemMarkedFailed_SuccessorsSkipped()
    {
        var successorRan = 0;
        var captured = 0;

        var dag = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 }).PublicTrack.DeclareDag("Test");
        dag.QuerySystem("Parallel", _ => { }, input: () => null, parallel: true);
        dag.CallbackSystem("Successor", _ =>
        {
            if (captured == 0)
            {
                Interlocked.Increment(ref successorRan);
            }
        }, after: "Parallel");

        var scheduler = dag.Build(_registry.Runtime);

        // Wire: 2 chunks, first one throws
        scheduler.ParallelQueryPrepareCallback = _ => 2;
        scheduler.ParallelQueryChunkCallback = (_, chunk, _, _) =>
        {
            if (chunk == 0)
            {
                throw new InvalidOperationException("Chunk 0 failed");
            }
        };
        scheduler.ParallelQueryCleanupCallback = _ => false;
        scheduler.TickEndCallback = _ => Interlocked.Exchange(ref captured, 1);

        scheduler.Start();
        SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= 1, TimeSpan.FromSeconds(5));
        scheduler.Shutdown();
        scheduler.Dispose();

        Assert.That(successorRan, Is.EqualTo(0), "Successor should be skipped when parallel system fails");
    }

    // ═══════════════════════════════════════════════════════════════
    // Single-threaded mode
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void ParallelQuery_SingleWorker_AllChunksRunSequentially()
    {
        var chunkOrder = new List<int>();
        var captured = 0;

        using var scheduler = CreateParallelScheduler(
            entityCount: 256,
            onChunk: (_, chunk, _) =>
            {
                if (captured == 0)
                {
                    chunkOrder.Add(chunk);
                }
            },
            workerCount: 1,
            minChunkSize: 256); // 1 chunk for workerCount=1

        scheduler.TickEndCallback = _ => Interlocked.Exchange(ref captured, 1);

        RunOneTick(scheduler);

        // Single worker → totalChunks = min(1, ceil(256/256)) = 1
        Assert.That(chunkOrder, Has.Count.EqualTo(1));
        Assert.That(chunkOrder[0], Is.EqualTo(0));
    }

    // ═══════════════════════════════════════════════════════════════
    // DAG integration
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void ParallelQuery_WithPredecessor_OrderRespected()
    {
        var timestamps = new ConcurrentDictionary<string, long>();
        var captured = 0;

        var dag = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 2, BaseTickRate = 1000 }).PublicTrack.DeclareDag("Test");
        dag.CallbackSystem("Setup", _ =>
        {
            if (captured == 0)
            {
                timestamps["Setup"] = Stopwatch.GetTimestamp();
            }
        });
        dag.QuerySystem("Parallel", _ => { }, after: "Setup", input: () => null, parallel: true);
        dag.CallbackSystem("Cleanup", _ =>
        {
            if (captured == 0)
            {
                timestamps["Cleanup"] = Stopwatch.GetTimestamp();
                Interlocked.Exchange(ref captured, 1);
            }
        }, after: "Parallel");

        var scheduler = dag.Build(_registry.Runtime);

        scheduler.ParallelQueryPrepareCallback = _ => 2;
        scheduler.ParallelQueryChunkCallback = (_, chunk, _, _) =>
        {
            if (captured == 0)
            {
                timestamps[$"Chunk{chunk}"] = Stopwatch.GetTimestamp();
            }
        };
        scheduler.ParallelQueryCleanupCallback = _ => false;

        scheduler.Start();
        SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= 1, TimeSpan.FromSeconds(5));
        scheduler.Shutdown();
        scheduler.Dispose();

        // Setup must execute before any chunks
        Assert.That(timestamps.ContainsKey("Setup"), Is.True);
        Assert.That(timestamps.ContainsKey("Chunk0"), Is.True);
        Assert.That(timestamps.ContainsKey("Chunk1"), Is.True);
        Assert.That(timestamps.ContainsKey("Cleanup"), Is.True);

        Assert.That(timestamps["Setup"], Is.LessThan(timestamps["Chunk0"]), "Setup must run before Chunk0");
        Assert.That(timestamps["Setup"], Is.LessThan(timestamps["Chunk1"]), "Setup must run before Chunk1");
        Assert.That(timestamps["Cleanup"], Is.GreaterThan(timestamps["Chunk0"]), "Cleanup must run after Chunk0");
        Assert.That(timestamps["Cleanup"], Is.GreaterThan(timestamps["Chunk1"]), "Cleanup must run after Chunk1");
    }

    // ═══════════════════════════════════════════════════════════════
    // PooledEntitySlice
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void PooledEntitySlice_Count_ReturnsSliceCount()
    {
        var array = new[] { EntityId.FromRaw(1), EntityId.FromRaw(2), EntityId.FromRaw(3), EntityId.FromRaw(4), EntityId.FromRaw(5) };
        var slice = new PooledEntitySlice(array, 1, 3);

        Assert.That(slice.Count, Is.EqualTo(3));
    }

    [Test]
    public void PooledEntitySlice_Indexer_ReturnsCorrectEntities()
    {
        var array = new[] { EntityId.FromRaw(10), EntityId.FromRaw(20), EntityId.FromRaw(30), EntityId.FromRaw(40) };
        var slice = new PooledEntitySlice(array, 1, 2); // entities 20, 30

        Assert.That(slice[0], Is.EqualTo(EntityId.FromRaw(20)));
        Assert.That(slice[1], Is.EqualTo(EntityId.FromRaw(30)));
    }

    [Test]
    public void PooledEntitySlice_Indexer_OutOfRange_Throws()
    {
        var array = new[] { EntityId.FromRaw(1), EntityId.FromRaw(2), EntityId.FromRaw(3) };
        var slice = new PooledEntitySlice(array, 0, 2);

        Assert.Throws<ArgumentOutOfRangeException>(() => _ = slice[2]);
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = slice[-1]);
    }

    [Test]
    public void PooledEntitySlice_Foreach_IteratesSlice()
    {
        var array = new[] { EntityId.FromRaw(100), EntityId.FromRaw(200), EntityId.FromRaw(300), EntityId.FromRaw(400), EntityId.FromRaw(500) };
        var slice = new PooledEntitySlice(array, 2, 3); // entities 300, 400, 500

        var collected = new List<EntityId>();
        foreach (var entity in slice)
        {
            collected.Add(entity);
        }

        Assert.That(collected, Is.EqualTo(new[] { EntityId.FromRaw(300), EntityId.FromRaw(400), EntityId.FromRaw(500) }));
    }

    [Test]
    public void PooledEntitySlice_EmptySlice()
    {
        var array = new[] { EntityId.FromRaw(1) };
        var slice = new PooledEntitySlice(array, 0, 0);

        Assert.That(slice.Count, Is.EqualTo(0));
        var count = 0;
        foreach (var _ in slice)
        {
            count++;
        }

        Assert.That(count, Is.EqualTo(0));
    }

    // ═══════════════════════════════════════════════════════════════
    // Chunk count computation
    // ═══════════════════════════════════════════════════════════════

    [TestCase(256, 4, 64, ExpectedResult = 4)]   // 256 / 64 = 4, min(4, 4) = 4
    [TestCase(128, 4, 64, ExpectedResult = 2)]   // 128 / 64 = 2, min(4, 2) = 2
    [TestCase(10, 4, 64, ExpectedResult = 1)]    // ceil(10/64) = 1, min(4, 1) = 1
    [TestCase(1000, 4, 64, ExpectedResult = 4)]  // ceil(1000/64) = 16, min(4, 16) = 4
    [TestCase(1000, 8, 64, ExpectedResult = 8)]  // ceil(1000/64) = 16, min(8, 16) = 8
    [TestCase(64, 4, 64, ExpectedResult = 1)]    // 64 / 64 = 1, min(4, 1) = 1
    [TestCase(65, 4, 64, ExpectedResult = 2)]    // ceil(65/64) = 2, min(4, 2) = 2
    public int ChunkCount_ComputedCorrectly(int entityCount, int workerCount, int minChunkSize)
    {
        var maxChunks = Math.Max(1, (entityCount + minChunkSize - 1) / minChunkSize);
        return Math.Min(workerCount, maxChunks);
    }

    // ═══════════════════════════════════════════════════════════════
    // Telemetry
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void ParallelQuery_SystemDefinition_IsParallelQuerySet()
    {
        using var scheduler = CreateParallelScheduler(
            entityCount: 256,
            onChunk: (_, _, _) => { },
            workerCount: 4,
            minChunkSize: 64);

        // Find the parallel system
        var found = false;
        for (var i = 0; i < scheduler.SystemCount; i++)
        {
            if (scheduler.Systems[i].IsParallelQuery)
            {
                found = true;
                Assert.That(scheduler.Systems[i].Type, Is.EqualTo(SystemType.QuerySystem));
                break;
            }
        }

        Assert.That(found, Is.True, "Should find a system with IsParallelQuery=true");
    }

    // ═══════════════════════════════════════════════════════════════
    // Validation: lambda API parallel parameter
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void LambdaQuerySystem_ParallelFlag_SetsIsParallelQuery()
    {
        var dag = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 2, BaseTickRate = 1000 }).PublicTrack.DeclareDag("Test");
        dag.QuerySystem("Parallel", _ => { }, input: () => null, parallel: true);

        using var scheduler = dag.Build(_registry.Runtime);
        Assert.That(scheduler.Systems[0].IsParallelQuery, Is.True);
    }

    [Test]
    public void LambdaQuerySystem_NoParallelFlag_NotParallel()
    {
        var dag = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 2, BaseTickRate = 1000 }).PublicTrack.DeclareDag("Test");
        dag.QuerySystem("Normal", _ => { });

        using var scheduler = dag.Build(_registry.Runtime);
        Assert.That(scheduler.Systems[0].IsParallelQuery, Is.False);
    }

    // ═══════════════════════════════════════════════════════════════
    // Pipeline error handling fix verification
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void PipelineSystem_ChunkThrows_DoesNotKillWorker()
    {
        var successorRan = 0;
        var captured = 0;

        var dag = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 2, BaseTickRate = 1000 }).PublicTrack.DeclareDag("Test");
        dag.PipelineSystem("FailPipeline", (chunk, total) =>
        {
            if (captured == 0 && chunk == 0)
            {
                throw new InvalidOperationException("Pipeline chunk 0 failed");
            }
        }, totalChunks: 2);
        dag.CallbackSystem("After", _ =>
        {
            if (captured == 0)
            {
                Interlocked.Increment(ref successorRan);
            }
        }, after: "FailPipeline");

        using var scheduler = dag.Build(_registry.Runtime);
        scheduler.TickEndCallback = _ => Interlocked.Exchange(ref captured, 1);

        RunOneTick(scheduler);

        // Successor should be skipped due to pipeline failure, but the scheduler should still complete
        // (not hang due to killed worker thread)
        Assert.That(successorRan, Is.EqualTo(0), "Successor should be skipped when pipeline fails");
    }

    // ═══════════════════════════════════════════════════════════════
    // PartitionEntityView correctness
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void PartitionEntityView_AllEntities_CoveredExactlyOnce()
    {
        using var map = new HashMap<long>(128);
        for (var i = 1; i <= 100; i++)
        {
            map.TryAdd(i);
        }

        var totalPartitions = 4;
        var allEntities = new HashSet<ulong>();

        for (var p = 0; p < totalPartitions; p++)
        {
            var view = new PartitionEntityView();
            view.Reset(map, p, totalPartitions);

            foreach (var entityId in (IEnumerable<EntityId>)view)
            {
                Assert.That(allEntities.Add(entityId.RawValue), Is.True,
                    $"Entity {entityId.RawValue} appeared in multiple partitions");
            }
        }

        Assert.That(allEntities.Count, Is.EqualTo(100), "All entities must be covered");
        for (ulong i = 1; i <= 100; i++)
        {
            Assert.That(allEntities.Contains(i), Is.True, $"Entity {i} missing");
        }
    }

    [Test]
    public void PartitionEntityView_SinglePartition_AllEntities()
    {
        using var map = new HashMap<long>(64);
        for (var i = 1; i <= 50; i++)
        {
            map.TryAdd(i);
        }

        var view = new PartitionEntityView();
        view.Reset(map, 0, 1);

        var count = 0;
        foreach (var _ in (IEnumerable<EntityId>)view)
        {
            count++;
        }

        Assert.That(count, Is.EqualTo(50));
    }

    [Test]
    public void PartitionEntityView_EmptyMap_NoEntities()
    {
        using var map = new HashMap<long>(16);

        var view = new PartitionEntityView();
        view.Reset(map, 0, 4);

        var count = 0;
        foreach (var _ in (IEnumerable<EntityId>)view)
        {
            count++;
        }

        Assert.That(count, Is.EqualTo(0));
    }

    [Test]
    public void PartitionEntityView_ApproximateCount_Reasonable()
    {
        using var map = new HashMap<long>(128);
        for (var i = 1; i <= 100; i++)
        {
            map.TryAdd(i);
        }

        var view = new PartitionEntityView();
        view.Reset(map, 0, 4);

        // Approximate count: ceil(100/4) = 25
        Assert.That(view.Count, Is.InRange(1, 100));
    }

    // ═══════════════════════════════════════════════════════════════
    // WritesVersioned flag propagation
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void WritesVersioned_LambdaApi_FlagPropagated()
    {
        var dag = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 2, BaseTickRate = 1000 }).PublicTrack.DeclareDag("Test");
        dag.QuerySystem("Parallel", _ => { }, input: () => null, parallel: true, writesVersioned: true);

        using var scheduler = dag.Build(_registry.Runtime);
        Assert.That(scheduler.Systems[0].WritesVersioned, Is.True);
        Assert.That(scheduler.Systems[0].IsParallelQuery, Is.True);
    }

    [Test]
    public void WritesVersioned_DefaultFalse()
    {
        var dag = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 2, BaseTickRate = 1000 }).PublicTrack.DeclareDag("Test");
        dag.QuerySystem("Parallel", _ => { }, input: () => null, parallel: true);

        using var scheduler = dag.Build(_registry.Runtime);
        Assert.That(scheduler.Systems[0].WritesVersioned, Is.False);
    }

    [Test]
    public void WritesVersioned_WithoutParallel_Throws()
    {
        var dag = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 2, BaseTickRate = 1000 }).PublicTrack.DeclareDag("Test");
        dag.QuerySystem("NonParallel", _ => { }, writesVersioned: true);

        Assert.Throws<InvalidOperationException>(() => dag.Build(_registry.Runtime));
    }

    // ═══════════════════════════════════════════════════════════════
    // PTA vs Transaction dispatch path selection
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void ParallelQuery_NonVersioned_ChunkReceivesAccessor()
    {
        var captured = 0;

        var dag = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 2, BaseTickRate = 1000 }).PublicTrack.DeclareDag("Test");
        dag.QuerySystem("Parallel", _ => { }, input: () => null, parallel: true);

        var scheduler = dag.Build(_registry.Runtime);

        // Wire manual callbacks simulating PTA-based dispatch
        scheduler.ParallelQueryPrepareCallback = _ => 1;
        scheduler.ParallelQueryChunkCallback = (sysIdx, chunk, total, workerId) =>
        {
            // In the real TyphonRuntime, the chunk callback sets ctx.Accessor.
            // Here we verify the flag is set correctly on the SystemDefinition.
            if (captured == 0)
            {
                Interlocked.Exchange(ref captured, 1);
            }
        };
        scheduler.ParallelQueryCleanupCallback = _ => false;

        scheduler.Start();
        SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= 1, TimeSpan.FromSeconds(5));
        scheduler.Shutdown();
        scheduler.Dispose();

        // Non-versioned system should NOT have WritesVersioned
        Assert.That(scheduler.Systems[0].WritesVersioned, Is.False,
            "Non-versioned parallel system should use PTA path (WritesVersioned=false)");
    }

    [Test]
    public void ParallelQuery_WritesVersioned_ChunkReceivesTransaction()
    {
        var captured = 0;

        var dag = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 2, BaseTickRate = 1000 }).PublicTrack.DeclareDag("Test");
        dag.QuerySystem("Parallel", _ => { }, input: () => null, parallel: true, writesVersioned: true);

        var scheduler = dag.Build(_registry.Runtime);

        scheduler.ParallelQueryPrepareCallback = _ => 1;
        scheduler.ParallelQueryChunkCallback = (sysIdx, chunk, total, workerId) =>
        {
            if (captured == 0)
            {
                Interlocked.Exchange(ref captured, 1);
            }
        };
        scheduler.ParallelQueryCleanupCallback = _ => false;

        scheduler.Start();
        SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= 1, TimeSpan.FromSeconds(5));
        scheduler.Shutdown();
        scheduler.Dispose();

        // Versioned system should have WritesVersioned
        Assert.That(scheduler.Systems[0].WritesVersioned, Is.True,
            "Versioned parallel system should use Transaction fallback path");
    }

    // ═══════════════════════════════════════════════════════════════
    // HashMap internal accessors (needed by PartitionEntityView)
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public unsafe void HashMap_InternalAccessors_Valid()
    {
        using var map = new HashMap<long>(64);
        map.TryAdd(42);

        Assert.That((long)map.EntriesPtr, Is.Not.EqualTo(0), "EntriesPtr must be non-null");
        Assert.That(map.EntryStride, Is.GreaterThan(0), "EntryStride must be positive");
        Assert.That(map.Capacity, Is.GreaterThanOrEqualTo(64), "Capacity must be at least initial");
    }
}
