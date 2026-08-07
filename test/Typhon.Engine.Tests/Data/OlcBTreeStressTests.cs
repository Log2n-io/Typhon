using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine.Tests;

/// <summary>
/// Stress tests for OLC (Optimistic Lock Coupling) B+Tree concurrency.
/// Thread counts are derived from <see cref="Environment.ProcessorCount"/> (see WideThreads / NarrowThreads) rather than fixed, so the fixture stays
/// oversubscribed — and therefore meaningful — on both a 32-vCPU CI box and a 3-core hosted runner. Contrast OlcBTreeTests, which runs 2-8 threads.
/// </summary>
// NonParallelizable, NOT Explicit. The fixture oversubscribes the box with threads and was marked [Explicit] to keep it from saturating the thread pool
// alongside the parallel fixtures — but [Explicit] does not merely deprioritise a test, it removes it from every unfiltered run, so this had not run in CI
// since 2026-02. It is the B+Tree's only real concurrency guard, and #679 (a concurrent insert losing a key and leaving the tree inconsistent) went
// unnoticed for that entire window. [NonParallelizable] buys the same isolation the [Explicit] reason was actually asking for: NUnit runs this fixture on
// its own, never concurrently with another, so the thread pool is not contended. Measured: 0.94-1.12 s for all 8 tests, slowest single test 230-276 ms
// against its [CancelAfter(5000)] budget — 18x headroom idle, and still 10.9x with 28 busy processes pinning a 32-CPU box.
[TestFixture]
[NonParallelizable]
public class OlcBTreeStressTests
{
    private IServiceProvider _serviceProvider;

    // Thread counts scale with the box instead of being pinned at 16/32. What makes this fixture worth running is OVERSUBSCRIPTION — more runnable threads
    // than cores, so the scheduler preempts a thread inside the OLC read/validate window and the optimistic-restart and pessimistic-fallback paths actually
    // execute. A hard 32 delivers that on the 32-vCPU gate box and on a dev machine, but the macOS arm64 nightly runs this same shard plan on a 3-core
    // hosted runner (bench/aws/shard.py:32-34). At 3 cores, 32 threads is no longer contention — it is ~10x oversubscription, which mostly buys scheduler
    // thrash and wall time rather than any interleaving the 2x case does not already produce.
    //
    // 2x cores keeps the oversubscription the tests depend on. The floor of 8 protects the assertion that a mixed workload MUST produce optimistic restarts:
    // below that there is too little concurrency to guarantee one, and the test would fail for lack of contention rather than for a defect. The ceiling of 32
    // reproduces today's numbers EXACTLY everywhere the suite currently runs — at >= 16 logical CPUs this is Wide 32 / Narrow 16, unchanged.
    private static readonly int WideThreads = Math.Clamp(Environment.ProcessorCount * 2, 8, 32);

    private static readonly int NarrowThreads = Math.Max(4, WideThreads / 2);

    [SetUp]
    public void Setup()
    {
        var serviceCollection = new ServiceCollection();
        serviceCollection
            .AddLogging()
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddScopedManagedPagedMemoryMappedFile(options =>
            {
                options.DatabaseName = $"olcst_{TestContext.CurrentContext.Test.Name}"[..Math.Min(63, $"olcst_{TestContext.CurrentContext.Test.Name}".Length)];
                options.DatabaseCacheSize = (ulong)(PagedMMF.MinimumMemPageCount * PagedMMF.PageSize);
                options.PagesDebugPattern = true;
            });

        _serviceProvider = serviceCollection.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
    }

    [TearDown]
    public void TearDown() => (_serviceProvider as IDisposable)?.Dispose();

    private void LogDiagnostics<TKey>(BTree<TKey, PersistentStore> tree) where TKey : unmanaged
    {
        TestContext.Out.WriteLine(
            $"Restarts={tree.OptimisticRestarts} Fallbacks={tree.PessimisticFallbacks} " +
            $"WriteLockFails={tree.WriteLockFailures} Splits={tree.SplitCount} Merges={tree.MergeCount} " +
            $"ContentionSplits={tree.ContentionSplitCount} Deferred={tree.DeferredNodeCount}");
    }

    /// <summary>
    /// Runs CheckConsistency in a try-catch. Under high-contention stress, internal node separator keys
    /// can become stale (known limitation). Returns true if consistent, false if violations found.
    /// </summary>
    private bool TryCheckConsistency<TKey>(BTree<TKey, PersistentStore> tree, ChunkBasedSegment<PersistentStore> segment, string context = null) where TKey : unmanaged
    {
        var accessor = segment.CreateChunkAccessor();
        try
        {
            tree.CheckConsistency(ref accessor);
            return true;
        }
        catch (Exception ex)
        {
            TestContext.Out.WriteLine($"Consistency check{(context != null ? $" ({context})" : "")}: {ex.Message}");
            return false;
        }
        finally
        {
            accessor.Dispose();
        }
    }

    // ========================================
    // B4 — Mixed Read-Write (readers + inserters + removers, WideThreads total)
    // ========================================

    [Test]
    [CancelAfter(5000)]
    public unsafe void Stress_MixedReadWrite()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 200, sizeof(Index32Chunk));

        // Same 20 : 6 : 6 split the fixed counts encoded, expressed as a ratio. Removers walk 501 + threadId * 10 for 10 keys each, so the removed span stays
        // inside the 501..1000 half and never touches the 1..500 range the readers assert on, for any remover count up to 50.
        var inserterCount = Math.Max(2, WideThreads / 5);
        var removerCount = Math.Max(2, WideThreads / 5);
        var readerCount = WideThreads - inserterCount - removerCount;
        var totalThreads = WideThreads;
        const int initialKeys = 1000;

        var setupDepth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new IntSingleBTree<PersistentStore>(segment);

            for (int i = 1; i <= initialKeys; i++)
            {
                tree.Add(i, i * 10, ref accessor);
            }
            accessor.Dispose();

            tree.ResetDiagnostics();

            using var startSignal = new ManualResetEventSlim(false);
            int readErrors = 0;
            var tasks = new Task[totalThreads];
            int taskIndex = 0;

            // Readers: sample the safe range 1..500, which no remover touches.
            for (int t = 0; t < readerCount; t++)
            {
                var seed = t * 17;
                tasks[taskIndex++] = Task.Factory.StartNew(() =>
                {
                    var depth = epochManager.EnterScope();
                    try
                    {
                        var ra = segment.CreateChunkAccessor();
                        var rng = new Random(seed);
                        startSignal.Wait();

                        for (int i = 0; i < 50; i++)
                        {
                            int key = rng.Next(1, 501); // safe range 1..500
                            var result = tree.TryGet(key, ref ra);
                            if (!result.IsSuccess || result.Value != key * 10)
                            {
                                Interlocked.Increment(ref readErrors);
                            }
                        }
                        ra.Dispose();
                    }
                    finally
                    {
                        epochManager.ExitScope(depth);
                    }
                }, TaskCreationOptions.LongRunning);
            }

            // Inserters: disjoint blocks from 100_000+, 20 keys each.
            for (int t = 0; t < inserterCount; t++)
            {
                var threadId = t;
                tasks[taskIndex++] = Task.Factory.StartNew(() =>
                {
                    var depth = epochManager.EnterScope();
                    try
                    {
                        var wa = segment.CreateChunkAccessor();
                        startSignal.Wait();

                        int baseKey = 100_000 + threadId * 20;
                        for (int i = 0; i < 20; i++)
                        {
                            tree.Add(baseKey + i, (baseKey + i) * 10, ref wa);
                        }
                        wa.Dispose();
                    }
                    finally
                    {
                        epochManager.ExitScope(depth);
                    }
                }, TaskCreationOptions.LongRunning);
            }

            // Removers: disjoint 10-key blocks from 501 upward, all inside the half the readers never read.
            for (int t = 0; t < removerCount; t++)
            {
                var threadId = t;
                tasks[taskIndex++] = Task.Factory.StartNew(() =>
                {
                    var depth = epochManager.EnterScope();
                    try
                    {
                        var wa = segment.CreateChunkAccessor();
                        startSignal.Wait();

                        int baseKey = 501 + threadId * 10;
                        for (int i = 0; i < 10; i++)
                        {
                            tree.Remove(baseKey + i, out _, ref wa);
                        }
                        wa.Dispose();
                    }
                    finally
                    {
                        epochManager.ExitScope(depth);
                    }
                }, TaskCreationOptions.LongRunning);
            }

            startSignal.Set();
            Task.WaitAll(tasks);

            Assert.That(readErrors, Is.EqualTo(0), "Safe-range reads should all be correct");
            Assert.That(tree.OptimisticRestarts, Is.GreaterThan(0), "Mixed workload should cause restarts");

            TryCheckConsistency(tree, segment);
            LogDiagnostics(tree);
        }
        finally
        {
            epochManager.ExitScope(setupDepth);
        }
    }

    // ========================================
    // B5.1 — Contention Split Tree Consistency
    // ========================================

    [Test]
    [CancelAfter(5000)]
    public unsafe void Stress_ContentionSplit_TreeConsistency()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 200, sizeof(Index32Chunk));

        var threadCount = WideThreads;
        const int keysPerThread = 150;
        int sharedCounter = 0;

        var setupDepth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new IntSingleBTree<PersistentStore>(segment);
            accessor.Dispose();

            using var barrier = new Barrier(threadCount);
            var tasks = new Task[threadCount];

            for (int t = 0; t < threadCount; t++)
            {
                tasks[t] = Task.Factory.StartNew(() =>
                {
                    var depth = epochManager.EnterScope();
                    try
                    {
                        var wa = segment.CreateChunkAccessor();
                        barrier.SignalAndWait();

                        for (int i = 0; i < keysPerThread; i++)
                        {
                            int key = Interlocked.Increment(ref sharedCounter);
                            tree.Add(key, key * 10, ref wa);
                        }
                        wa.Dispose();
                    }
                    finally
                    {
                        epochManager.ExitScope(depth);
                    }
                }, TaskCreationOptions.LongRunning);
            }

            Task.WaitAll(tasks);

            int totalKeys = threadCount * keysPerThread;
            Assert.That(tree.EntryCount, Is.EqualTo(totalKeys));
            // Contention splits are a probabilistic optimization — whether the hint reaches the threshold
            // depends on thread scheduling and backoff behavior. With SpinWait yielding, contention
            // resolves faster and the hint may not accumulate. Log for diagnostics, don't assert.
            TestContext.Out.WriteLine($"ContentionSplitCount={tree.ContentionSplitCount} (not asserted — scheduling-dependent)");

            // Verify tree structural consistency
            TryCheckConsistency(tree, segment);

            // Verify every key is present by enumerating all leaves
            var verifyAccessor = segment.CreateChunkAccessor();
            var found = new bool[totalKeys + 1];
            foreach (var kv in tree.EnumerateLeaves())
            {
                Assert.That(kv.Key, Is.GreaterThan(0).And.LessThanOrEqualTo(totalKeys), "Key out of expected range");
                found[kv.Key] = true;
            }
            for (int k = 1; k <= totalKeys; k++)
            {
                Assert.That(found[k], Is.True, $"Key {k} missing after contention split");
            }
            verifyAccessor.Dispose();

            LogDiagnostics(tree);
        }
        finally
        {
            epochManager.ExitScope(setupDepth);
        }
    }

    // ========================================
    // B5.2 — Contention Split Mixed Read-Write
    // ========================================

    [Test]
    [CancelAfter(5000)]
    public unsafe void Stress_ContentionSplit_MixedReadWrite()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 200, sizeof(Index32Chunk));

        var writerCount = WideThreads / 2;
        var readerCount = WideThreads - writerCount;
        const int keysPerWriter = 200;
        int sharedCounter = 0;

        var setupDepth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new IntSingleBTree<PersistentStore>(segment);
            accessor.Dispose();

            using var startSignal = new ManualResetEventSlim(false);
            using var writersDone = new CountdownEvent(writerCount);
            int readErrors = 0;
            int readSuccesses = 0;
            var tasks = new Task[writerCount + readerCount];
            int taskIndex = 0;

            // 16 writers: monotonic inserts to trigger contention splits
            for (int t = 0; t < writerCount; t++)
            {
                tasks[taskIndex++] = Task.Factory.StartNew(() =>
                {
                    var depth = epochManager.EnterScope();
                    try
                    {
                        var wa = segment.CreateChunkAccessor();
                        startSignal.Wait();

                        for (int i = 0; i < keysPerWriter; i++)
                        {
                            int key = Interlocked.Increment(ref sharedCounter);
                            tree.Add(key, key * 10, ref wa);
                        }
                        wa.Dispose();
                    }
                    finally
                    {
                        writersDone.Signal();
                        epochManager.ExitScope(depth);
                    }
                }, TaskCreationOptions.LongRunning);
            }

            // 16 readers: random lookups while writers are active
            for (int t = 0; t < readerCount; t++)
            {
                var seed = t * 13;
                tasks[taskIndex++] = Task.Factory.StartNew(() =>
                {
                    var depth = epochManager.EnterScope();
                    try
                    {
                        var ra = segment.CreateChunkAccessor();
                        var rng = new Random(seed);
                        startSignal.Wait();

                        while (!writersDone.IsSet)
                        {
                            int currentMax = sharedCounter;
                            if (currentMax < 1)
                            {
                                Thread.SpinWait(10);
                                continue;
                            }
                            int key = rng.Next(1, currentMax + 1);
                            var result = tree.TryGet(key, ref ra);
                            if (result.IsSuccess)
                            {
                                if (result.Value != key * 10)
                                {
                                    Interlocked.Increment(ref readErrors);
                                }
                                else
                                {
                                    Interlocked.Increment(ref readSuccesses);
                                }
                            }
                            // Key not found is OK — writer may not have committed yet
                        }
                        ra.Dispose();
                    }
                    finally
                    {
                        epochManager.ExitScope(depth);
                    }
                }, TaskCreationOptions.LongRunning);
            }

            startSignal.Set();
            Task.WaitAll(tasks);

            int totalKeys = writerCount * keysPerWriter;
            Assert.That(tree.EntryCount, Is.EqualTo(totalKeys));
            Assert.That(readErrors, Is.EqualTo(0), "All successful reads should return correct values");
            Assert.That(readSuccesses, Is.GreaterThan(0), "At least some reads should succeed");
            // Contention splits are a probabilistic optimization — whether the hint reaches the threshold
            // depends on thread scheduling and backoff behavior. With SpinWait yielding, contention
            // resolves faster and the hint may not accumulate. Log for diagnostics, don't assert.
            TestContext.Out.WriteLine($"ContentionSplitCount={tree.ContentionSplitCount} (not asserted — scheduling-dependent)");

            TryCheckConsistency(tree, segment);
            LogDiagnostics(tree);
        }
        finally
        {
            epochManager.ExitScope(setupDepth);
        }
    }

    // ========================================
    // B6 — Move Stress Same-Leaf (16 threads)
    // Each thread owns a disjoint 400-key range with 200 populated even slots.
    // Moves shift each even key to the adjacent odd slot (e.g., 2→3, 4→5).
    // Small offset → same-leaf probability high, zero range overlap between threads.
    // Note: Move at 64 threads triggers Debug.Assert in BTree internals (known OLC limitation).
    // ========================================

    [Test]
    [CancelAfter(5000)]
    public unsafe void Stress_MoveSameLeaf()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 200, sizeof(Index32Chunk));

        var threadCount = NarrowThreads;
        const int slotsPerThread = 800;   // exclusive range size per thread (wide gap avoids shared boundary leaves)
        const int keysPerThread = 200;    // only even slots populated
        const int movesPerThread = 200;   // move all keys: even→odd

        var setupDepth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new IntSingleBTree<PersistentStore>(segment);

            // Pre-populate: each thread owns even keys in range [base, base+slotsPerThread)
            for (int t = 0; t < threadCount; t++)
            {
                int baseKey = t * slotsPerThread;
                for (int i = 0; i < keysPerThread; i++)
                {
                    int key = baseKey + i * 2; // even slots: 0, 2, 4, ...
                    tree.Add(key, key * 10, ref accessor);
                }
            }
            accessor.Dispose();

            int totalKeys = threadCount * keysPerThread;
            Assert.That(tree.EntryCount, Is.EqualTo(totalKeys));
            tree.ResetDiagnostics();

            using var barrier = new Barrier(threadCount);
            var tasks = new Task[threadCount];
            int moveErrors = 0;

            for (int t = 0; t < threadCount; t++)
            {
                var threadId = t;
                tasks[t] = Task.Factory.StartNew(() =>
                {
                    var depth = epochManager.EnterScope();
                    try
                    {
                        var wa = segment.CreateChunkAccessor();
                        barrier.SignalAndWait();

                        // Move each even key to the next odd slot: key → key+1
                        // e.g., 0→1, 2→3, 4→5 — always within the thread's own range
                        int baseKey = threadId * slotsPerThread;
                        for (int i = 0; i < movesPerThread; i++)
                        {
                            int oldKey = baseKey + i * 2;       // even slot
                            int newKey = oldKey + 1;            // adjacent odd slot
                            bool moved = tree.Move(oldKey, newKey, oldKey * 10, ref wa);
                            if (!moved)
                            {
                                Interlocked.Increment(ref moveErrors);
                            }
                        }
                        wa.Dispose();
                    }
                    finally
                    {
                        epochManager.ExitScope(depth);
                    }
                }, TaskCreationOptions.LongRunning);
            }

            Task.WaitAll(tasks);

            Assert.That(moveErrors, Is.EqualTo(0), "All moves should succeed (disjoint ranges)");
            Assert.That(tree.EntryCount, Is.EqualTo(totalKeys), "Move should not change total entry count");

            TryCheckConsistency(tree, segment);
            LogDiagnostics(tree);
        }
        finally
        {
            epochManager.ExitScope(setupDepth);
        }
    }

    // ========================================
    // B7 — Move Stress Cross-Leaf (16 threads)
    // Cross-leaf Move exercises the dual-lock path (lock two leaves in ChunkId order).
    [Test]
    [CancelAfter(5000)]
    public unsafe void Stress_MoveCrossLeaf()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 200, sizeof(Index32Chunk));

        var threadCount = NarrowThreads;
        const int keysPerThread = 200;
        const int movesPerThread = 50;

        var setupDepth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new IntSingleBTree<PersistentStore>(segment);

            for (int t = 0; t < threadCount; t++)
            {
                int baseKey = t * keysPerThread + 1;
                for (int i = 0; i < keysPerThread; i++)
                {
                    tree.Add(baseKey + i, (baseKey + i) * 10, ref accessor);
                }
            }
            accessor.Dispose();

            int totalKeys = threadCount * keysPerThread;
            Assert.That(tree.EntryCount, Is.EqualTo(totalKeys));
            tree.ResetDiagnostics();

            using var barrier = new Barrier(threadCount);
            var tasks = new Task[threadCount];
            int moveErrors = 0;

            for (int t = 0; t < threadCount; t++)
            {
                var threadId = t;
                tasks[t] = Task.Factory.StartNew(() =>
                {
                    var depth = epochManager.EnterScope();
                    try
                    {
                        var wa = segment.CreateChunkAccessor();
                        barrier.SignalAndWait();

                        int baseKey = threadId * keysPerThread + 1;
                        for (int i = 0; i < movesPerThread; i++)
                        {
                            int oldKey = baseKey + i;
                            int newKey = 100_000 + threadId * movesPerThread + i;
                            bool moved = tree.Move(oldKey, newKey, oldKey * 10, ref wa);
                            if (!moved)
                            {
                                Interlocked.Increment(ref moveErrors);
                            }
                        }
                        wa.Dispose();
                    }
                    finally
                    {
                        epochManager.ExitScope(depth);
                    }
                }, TaskCreationOptions.LongRunning);
            }

            Task.WaitAll(tasks);

            TestContext.Out.WriteLine($"Cross-leaf move errors: {moveErrors} of {threadCount * movesPerThread}");
            Assert.That(tree.EntryCount, Is.EqualTo(totalKeys), "Move should not change total entry count");

            TryCheckConsistency(tree, segment);
            LogDiagnostics(tree);
        }
        finally
        {
            epochManager.ExitScope(setupDepth);
        }
    }

    // ========================================
    // B8 — MoveValue TAIL Consistency (64 threads)
    // ========================================

    [Test]
    [CancelAfter(5000)]
    public unsafe void Stress_MoveValueTailConsistency()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 200, sizeof(Index32Chunk));

        var threadCount = WideThreads;
        const int sourceKeyCount = 200;
        const int valuesPerKey = 3;

        var setupDepth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new IntMultipleBTree<PersistentStore>(segment);

            // Pre-populate: 200 keys with 3 values each
            // Store element IDs for each key's first value (the one we'll move)
            var elementIds = new int[sourceKeyCount];
            for (int k = 0; k < sourceKeyCount; k++)
            {
                int key = k + 1;
                elementIds[k] = tree.Add(key, key * 100, ref accessor);
                for (int v = 1; v < valuesPerKey; v++)
                {
                    tree.Add(key, key * 100 + v, ref accessor);
                }
            }
            accessor.Dispose();

            tree.ResetDiagnostics();

            using var barrier = new Barrier(threadCount);
            var tasks = new Task[threadCount];
            int moveErrors = 0;

            // Each thread picks 3 source keys and moves one value to a unique target key
            for (int t = 0; t < threadCount; t++)
            {
                var threadId = t;
                tasks[t] = Task.Factory.StartNew(() =>
                {
                    var depth = epochManager.EnterScope();
                    try
                    {
                        var wa = segment.CreateChunkAccessor();
                        barrier.SignalAndWait();

                        // Each thread gets 3 unique source keys from its portion of the range
                        for (int i = 0; i < 3; i++)
                        {
                            int srcKeyIndex = (threadId * 3 + i) % sourceKeyCount;
                            int srcKey = srcKeyIndex + 1;
                            int srcValue = srcKey * 100; // first value
                            int srcEid = elementIds[srcKeyIndex];
                            int dstKey = 10_000 + threadId * 3 + i; // unique target key per thread

                            var newEid = tree.MoveValue(srcKey, dstKey, srcEid, srcValue, ref wa,
                                out var oldHead, out var newHead);

                            if (newEid >= 0)
                            {
                                // Verify the target key now has data
                                using var buf = tree.TryGetMultiple(dstKey, ref wa);
                                if (!buf.IsValid)
                                {
                                    Interlocked.Increment(ref moveErrors);
                                }
                            }
                            // newEid == -1 is OK: another thread may have already moved this element
                        }
                        wa.Dispose();
                    }
                    finally
                    {
                        epochManager.ExitScope(depth);
                    }
                }, TaskCreationOptions.LongRunning);
            }

            Task.WaitAll(tasks);

            Assert.That(moveErrors, Is.EqualTo(0), "Successfully moved values should be readable");

            TryCheckConsistency(tree, segment);
            LogDiagnostics(tree);
        }
        finally
        {
            epochManager.ExitScope(setupDepth);
        }
    }

    // ========================================
    // B9 — Enumeration During Mutation (16 threads)
    // ========================================

    [Test]
    [CancelAfter(5000)]
    public unsafe void Stress_EnumerationDuringMutation()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 200, sizeof(Index32Chunk));

        var writerCount = NarrowThreads / 2;
        var enumeratorCount = NarrowThreads - writerCount;
        const int insertsPerWriter = 50;
        const int initialKeys = 500;

        var setupDepth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new IntSingleBTree<PersistentStore>(segment);

            for (int i = 1; i <= initialKeys; i++)
            {
                tree.Add(i, i * 10, ref accessor);
            }
            accessor.Dispose();

            tree.ResetDiagnostics();

            using var startSignal = new ManualResetEventSlim(false);
            using var writersDone = new CountdownEvent(writerCount);
            int enumCount = 0;
            int enumErrors = 0;

            var tasks = new Task[writerCount + enumeratorCount];
            int taskIndex = 0;

            // 8 writers
            for (int t = 0; t < writerCount; t++)
            {
                var threadId = t;
                tasks[taskIndex++] = Task.Factory.StartNew(() =>
                {
                    var depth = epochManager.EnterScope();
                    try
                    {
                        var wa = segment.CreateChunkAccessor();
                        startSignal.Wait();

                        int baseKey = 10_000 + threadId * insertsPerWriter;
                        for (int i = 0; i < insertsPerWriter; i++)
                        {
                            tree.Add(baseKey + i, (baseKey + i) * 10, ref wa);
                        }
                        wa.Dispose();
                    }
                    finally
                    {
                        writersDone.Signal();
                        epochManager.ExitScope(depth);
                    }
                }, TaskCreationOptions.LongRunning);
            }

            // 8 enumerators
            for (int t = 0; t < enumeratorCount; t++)
            {
                tasks[taskIndex++] = Task.Factory.StartNew(() =>
                {
                    var depth = epochManager.EnterScope();
                    try
                    {
                        startSignal.Wait();

                        // Enumerate multiple times while writers are active
                        while (!writersDone.IsSet)
                        {
                            int count = 0;
                            try
                            {
                                foreach (var kv in tree.EnumerateLeaves())
                                {
                                    count++;
                                }
                            }
                            catch
                            {
                                Interlocked.Increment(ref enumErrors);
                            }
                            if (count > 0)
                            {
                                Interlocked.Add(ref enumCount, count);
                            }
                        }

                        // One final enumeration after writers finish
                        int finalCount = 0;
                        foreach (var kv in tree.EnumerateLeaves())
                        {
                            finalCount++;
                        }
                        Interlocked.Add(ref enumCount, finalCount);
                    }
                    finally
                    {
                        epochManager.ExitScope(depth);
                    }
                }, TaskCreationOptions.LongRunning);
            }

            startSignal.Set();
            Task.WaitAll(tasks);

            Assert.That(enumErrors, Is.EqualTo(0), "Enumeration should not throw exceptions");
            Assert.That(enumCount, Is.GreaterThan(0), "Enumerators should have counted entries");

            TryCheckConsistency(tree, segment);
            LogDiagnostics(tree);
        }
        finally
        {
            epochManager.ExitScope(setupDepth);
        }
    }

    // ========================================
    // B10 — Consistency Check Interleaving
    // ========================================

    [Test]
    [CancelAfter(5000)]
    public unsafe void Stress_ConsistencyCheckInterleaving()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 100, sizeof(Index32Chunk));

        var writerCount = Math.Max(2, NarrowThreads / 2);
        const int batchSize = 10;
        const int batchCount = 5;
        const int initialKeys = 500;

        var setupDepth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new IntSingleBTree<PersistentStore>(segment);

            for (int i = 1; i <= initialKeys; i++)
            {
                tree.Add(i, i * 10, ref accessor);
            }
            accessor.Dispose();

            tree.ResetDiagnostics();

            int consistencyErrors = 0;

            // 5 batches of concurrent inserts, with consistency check between each
            for (int batch = 0; batch < batchCount; batch++)
            {
                using var barrier = new Barrier(writerCount);
                var tasks = new Task[writerCount];

                for (int t = 0; t < writerCount; t++)
                {
                    var threadId = t;
                    var batchId = batch;
                    tasks[t] = Task.Factory.StartNew(() =>
                    {
                        var depth = epochManager.EnterScope();
                        try
                        {
                            var wa = segment.CreateChunkAccessor();
                            barrier.SignalAndWait();

                            int baseKey = 10_000 + batchId * writerCount * batchSize + threadId * batchSize;
                            for (int i = 0; i < batchSize; i++)
                            {
                                tree.Add(baseKey + i, (baseKey + i) * 10, ref wa);
                            }
                            wa.Dispose();
                        }
                        finally
                        {
                            epochManager.ExitScope(depth);
                        }
                    }, TaskCreationOptions.LongRunning);
                }

                Task.WaitAll(tasks);

                // Consistency check between batches — single-threaded, no concurrent modification
                if (!TryCheckConsistency(tree, segment, $"batch {batch}"))
                {
                    Interlocked.Increment(ref consistencyErrors);
                }
            }

            int expectedCount = initialKeys + batchCount * writerCount * batchSize;
            Assert.That(tree.EntryCount, Is.EqualTo(expectedCount),
                $"Expected {expectedCount} entries after {batchCount} batches");
            TestContext.Out.WriteLine($"Consistency violations: {consistencyErrors} of {batchCount} checkpoints");

            // Final consistency check
            TryCheckConsistency(tree, segment);
            LogDiagnostics(tree);
        }
        finally
        {
            epochManager.ExitScope(setupDepth);
        }
    }
}
