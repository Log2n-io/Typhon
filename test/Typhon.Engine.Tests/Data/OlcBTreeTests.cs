using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine.Tests;

[TestFixture]
public class OlcBTreeTests
{
    private IServiceProvider _serviceProvider;

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
                options.DatabaseName = $"olcbt_{TestContext.CurrentContext.Test.Name}"[..Math.Min(63, $"olcbt_{TestContext.CurrentContext.Test.Name}".Length)];
                // 256 pages (MinimumMemPageCount) is a cache-stress size; the concurrent enumerate-during-write tests
                // (EnumerateLeaves/FullIntegration) hold epoch scopes over a growing tree and cannot run that tight —
                // they dead-locked on PageCacheBackpressureTimeout. Dedicated cache-stress coverage lives in
                // OlcBTreeStressTests / OlcBTreeRaceStressTests; this functional fixture gets an adequate cache.
                options.DatabaseCacheSize = (ulong)(8192 * PagedMMF.PageSize);
                options.PagesDebugPattern = true;
            });

        _serviceProvider = serviceCollection.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
    }

    [TearDown]
    public void TearDown() => (_serviceProvider as IDisposable)?.Dispose();

    // ========================================
    // #111 — OLC Optimistic Read Path
    // ========================================

    [Test]
    [CancelAfter(5000)]
    public unsafe void TryGet_SingleThread_ReturnsCorrectValues()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(Index32Chunk));
        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new IntSingleBTree<PersistentStore>(segment);

            // Insert known values
            for (int i = 1; i <= 100; i++)
            {
                tree.Add(i, i * 10, ref accessor);
            }

            // Verify all values via TryGet (now uses OLC optimistic path)
            for (int i = 1; i <= 100; i++)
            {
                var result = tree.TryGet(i, ref accessor);
                Assert.That(result.IsSuccess, Is.True, $"Key {i} should be found");
                Assert.That(result.Value, Is.EqualTo(i * 10), $"Key {i} should have value {i * 10}");
            }

            // Verify not-found
            var notFound = tree.TryGet(999, ref accessor);
            Assert.That(notFound.IsFailure, Is.True);

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    [CancelAfter(5000)]
    public unsafe void TryGet_ConcurrentReaders_ZeroWriters_AllSucceed()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(Index32Chunk));

        const int entryCount = 200;
        const int readerCount = 8;

        // Populate tree under epoch scope
        var setupDepth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new IntSingleBTree<PersistentStore>(segment);

            for (int i = 1; i <= entryCount; i++)
            {
                tree.Add(i, i * 10, ref accessor);
            }
            accessor.Dispose();

            // Concurrent readers — no writers, so zero restarts expected
            using var barrier = new Barrier(readerCount);
            int errors = 0;

            var tasks = new Task[readerCount];
            for (int r = 0; r < readerCount; r++)
            {
                tasks[r] = Task.Factory.StartNew(() =>
                {
                    var readerDepth = epochManager.EnterScope();
                    try
                    {
                        var ra = segment.CreateChunkAccessor();
                        barrier.SignalAndWait();

                        for (int i = 1; i <= entryCount; i++)
                        {
                            var result = tree.TryGet(i, ref ra);
                            if (!result.IsSuccess || result.Value != i * 10)
                            {
                                Interlocked.Increment(ref errors);
                            }
                        }
                        ra.Dispose();
                    }
                    finally
                    {
                        epochManager.ExitScope(readerDepth);
                    }
                }, TaskCreationOptions.LongRunning);
            }

            Task.WaitAll(tasks);
            Assert.That(errors, Is.EqualTo(0), "All concurrent readers should get correct values");
        }
        finally
        {
            epochManager.ExitScope(setupDepth);
        }
    }

    [Test]
    [CancelAfter(5000)]
    public unsafe void TryGet_ConcurrentReadersWithOneWriter_ReadersGetCorrectValues()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 20, sizeof(Index32Chunk));

        const int initialEntries = 100;
        const int readerCount = 8;
        const int writerInserts = 200;

        var setupDepth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new IntSingleBTree<PersistentStore>(segment);

            // Populate initial entries (keys 1..100, values key*10)
            for (int i = 1; i <= initialEntries; i++)
            {
                tree.Add(i, i * 10, ref accessor);
            }
            accessor.Dispose();

            using var startSignal = new ManualResetEventSlim(false);
            using var writerDone = new ManualResetEventSlim(false);
            int readerErrors = 0;

            // Writer thread: inserts keys 1001..1200
            var writerTask = Task.Factory.StartNew(() =>
            {
                var writerDepth = epochManager.EnterScope();
                try
                {
                    var wa = segment.CreateChunkAccessor();
                    startSignal.Wait();

                    for (int i = 1001; i <= 1000 + writerInserts; i++)
                    {
                        tree.Add(i, i * 10, ref wa);
                    }
                    wa.CommitChanges();
                    wa.Dispose();
                }
                finally
                {
                    epochManager.ExitScope(writerDepth);
                    writerDone.Set();
                }
            }, TaskCreationOptions.LongRunning);

            // Reader threads: repeatedly query initial keys while writer is active
            var readerTasks = new Task[readerCount];
            for (int r = 0; r < readerCount; r++)
            {
                readerTasks[r] = Task.Factory.StartNew(() =>
                {
                    var readerDepth = epochManager.EnterScope();
                    try
                    {
                        var ra = segment.CreateChunkAccessor();
                        startSignal.Wait();

                        // Keep reading while writer is active
                        while (!writerDone.IsSet)
                        {
                            for (int i = 1; i <= initialEntries; i++)
                            {
                                var result = tree.TryGet(i, ref ra);
                                // Key must always be found with correct value
                                if (!result.IsSuccess || result.Value != i * 10)
                                {
                                    Interlocked.Increment(ref readerErrors);
                                }
                            }
                        }
                        ra.Dispose();
                    }
                    finally
                    {
                        epochManager.ExitScope(readerDepth);
                    }
                }, TaskCreationOptions.LongRunning);
            }

            startSignal.Set();
            Task.WaitAll(readerTasks.Append(writerTask).ToArray());

            Assert.That(readerErrors, Is.EqualTo(0), "Readers should always get correct values for initial keys during concurrent writes");
            Assert.That(tree.EntryCount, Is.EqualTo(initialEntries + writerInserts));
        }
        finally
        {
            epochManager.ExitScope(setupDepth);
        }
    }

    [Test]
    [CancelAfter(5000)]
    public unsafe void TryGet_EmptyTree_ReturnsNotFound()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(Index32Chunk));
        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new IntSingleBTree<PersistentStore>(segment);

            var result = tree.TryGet(42, ref accessor);
            Assert.That(result.IsFailure, Is.True);

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    // ========================================
    // #112 — OLC Write Path (Insert)
    // ========================================

    [Test]
    [CancelAfter(5000)]
    public unsafe void Add_ConcurrentDisjointKeys_AllInserted()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 50, sizeof(Index32Chunk));

        const int threadCount = 4;
        const int keysPerThread = 200;

        var setupDepth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new IntSingleBTree<PersistentStore>(segment);
            accessor.Dispose();

            using var barrier = new Barrier(threadCount);
            var exceptions = new ConcurrentBag<Exception>();

            var tasks = new Task[threadCount];
            for (int t = 0; t < threadCount; t++)
            {
                int threadId = t;
                tasks[t] = Task.Factory.StartNew(() =>
                {
                    var writerDepth = epochManager.EnterScope();
                    try
                    {
                        var wa = segment.CreateChunkAccessor();
                        barrier.SignalAndWait();

                        // Each thread inserts a non-overlapping key range
                        int start = threadId * keysPerThread + 1;
                        for (int i = start; i < start + keysPerThread; i++)
                        {
                            tree.Add(i, i * 10, ref wa);
                        }
                        wa.CommitChanges();
                        wa.Dispose();
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                    finally
                    {
                        epochManager.ExitScope(writerDepth);
                    }
                }, TaskCreationOptions.LongRunning);
            }

            Task.WaitAll(tasks);
            Assert.That(exceptions, Is.Empty, () =>
                $"Workers threw {exceptions.Count} exception(s) during concurrent inserts (EntryCount={tree.EntryCount}); first:\n{exceptions.First()}");
            Assert.That(tree.EntryCount, Is.EqualTo(threadCount * keysPerThread));

            // Verify all values are correct
            var verifyAccessor = segment.CreateChunkAccessor();
            var missingKeys = new System.Collections.Generic.List<int>();
            for (int i = 1; i <= threadCount * keysPerThread; i++)
            {
                var result = tree.TryGet(i, ref verifyAccessor);
                if (!result.IsSuccess)
                {
                    missingKeys.Add(i);
                }
                else
                {
                    Assert.That(result.Value, Is.EqualTo(i * 10), $"Key {i} should have value {i * 10}");
                }
            }

            if (missingKeys.Count > 0)
            {
                // Scan all leaves via linked list to find where the missing keys actually are
                var scanResults = new System.Text.StringBuilder();
                scanResults.AppendLine($"Missing {missingKeys.Count} keys: [{string.Join(", ", missingKeys.Take(20))}]");
                scanResults.AppendLine($"EntryCount={tree.EntryCount}, Height={tree.Height}");

                // Walk the leaf linked list and find any leaf containing a missing key
                try
                {
                    tree.CheckConsistency(ref verifyAccessor);
                    scanResults.AppendLine("CheckConsistency: PASSED");
                }
                catch (Exception ex)
                {
                    scanResults.AppendLine($"CheckConsistency: FAILED - {ex.Message}");
                }

                Assert.Fail(scanResults.ToString());
            }
            verifyAccessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(setupDepth);
        }
    }

    [Test]
    [CancelAfter(15000)]
    public unsafe void Add_ConcurrentInsertsCausingSplits_TreeConsistent()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 100, sizeof(Index32Chunk));

        const int threadCount = 4;
        const int keysPerThread = 500;

        var setupDepth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new IntSingleBTree<PersistentStore>(segment);
            accessor.Dispose();

            using var barrier = new Barrier(threadCount);
            var exceptions = new ConcurrentBag<Exception>();

            var tasks = new Task[threadCount];
            for (int t = 0; t < threadCount; t++)
            {
                int threadId = t;
                tasks[t] = Task.Factory.StartNew(() =>
                {
                    var writerDepth = epochManager.EnterScope();
                    try
                    {
                        var wa = segment.CreateChunkAccessor();
                        barrier.SignalAndWait();

                        // Interleaved keys force splits at various levels
                        for (int i = 0; i < keysPerThread; i++)
                        {
                            int key = i * threadCount + threadId + 1;
                            tree.Add(key, key * 10, ref wa);
                        }
                        wa.CommitChanges();
                        wa.Dispose();
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                    finally
                    {
                        epochManager.ExitScope(writerDepth);
                    }
                }, TaskCreationOptions.LongRunning);
            }

            Task.WaitAll(tasks);
            Assert.That(exceptions, Is.Empty, () =>
                $"Workers threw {exceptions.Count} exception(s) during concurrent inserts; first:\n{exceptions.First()}");
            Assert.That(tree.EntryCount, Is.EqualTo(threadCount * keysPerThread));
            Assert.That(tree.Height, Is.GreaterThan(1), "Tree should have multiple levels after many inserts");

            // Verify all keys are readable with correct values.
            // B-link following ensures correct reads even if separators are transiently stale.
            var verifyAccessor = segment.CreateChunkAccessor();
            for (int i = 1; i <= threadCount * keysPerThread; i++)
            {
                var result = tree.TryGet(i, ref verifyAccessor);
                Assert.That(result.IsSuccess, Is.True, $"Key {i} should be found");
                Assert.That(result.Value, Is.EqualTo(i * 10), $"Key {i} should have value {i * 10}");
            }
            verifyAccessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(setupDepth);
        }
    }

    [Test]
    [CancelAfter(5000)]
    public unsafe void Add_AppendFastPathConcurrent_SequentialInserts()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 50, sizeof(Index32Chunk));

        const int threadCount = 4;
        const int keysPerThread = 200;

        var setupDepth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new IntSingleBTree<PersistentStore>(segment);
            accessor.Dispose();

            using var barrier = new Barrier(threadCount);
            var exceptions = new ConcurrentBag<Exception>();

            // Each thread inserts its own ascending sequence (non-overlapping ranges)
            var tasks = new Task[threadCount];
            for (int t = 0; t < threadCount; t++)
            {
                int threadId = t;
                tasks[t] = Task.Factory.StartNew(() =>
                {
                    var writerDepth = epochManager.EnterScope();
                    try
                    {
                        var wa = segment.CreateChunkAccessor();
                        barrier.SignalAndWait();

                        int start = threadId * keysPerThread * 10 + 1;
                        for (int i = 0; i < keysPerThread; i++)
                        {
                            tree.Add(start + i, (start + i) * 10, ref wa);
                        }
                        wa.CommitChanges();
                        wa.Dispose();
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                    finally
                    {
                        epochManager.ExitScope(writerDepth);
                    }
                }, TaskCreationOptions.LongRunning);
            }

            Task.WaitAll(tasks);
            Assert.That(exceptions, Is.Empty, () =>
                $"Workers threw {exceptions.Count} exception(s) during concurrent sequential inserts; first:\n{exceptions.First()}");
            Assert.That(tree.EntryCount, Is.EqualTo(threadCount * keysPerThread));
        }
        finally
        {
            epochManager.ExitScope(setupDepth);
        }
    }

    [Test]
    [CancelAfter(5000)]
    public unsafe void Add_ConcurrentWritersWithReaders_ReadersGetCorrectValues()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 50, sizeof(Index32Chunk));

        const int initialEntries = 50;
        const int writerCount = 2;
        const int readerCount = 4;
        const int insertsPerWriter = 200;

        var setupDepth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new IntSingleBTree<PersistentStore>(segment);

            // Populate initial entries
            for (int i = 1; i <= initialEntries; i++)
            {
                tree.Add(i, i * 10, ref accessor);
            }
            accessor.Dispose();

            using var startSignal = new ManualResetEventSlim(false);
            using var writersDone = new CountdownEvent(writerCount);
            int readerErrors = 0;

            // Writer threads: insert disjoint key ranges
            var writerTasks = new Task[writerCount];
            for (int w = 0; w < writerCount; w++)
            {
                int writerId = w;
                writerTasks[w] = Task.Factory.StartNew(() =>
                {
                    var writerDepth = epochManager.EnterScope();
                    try
                    {
                        var wa = segment.CreateChunkAccessor();
                        startSignal.Wait();

                        int start = 10_000 + writerId * insertsPerWriter;
                        for (int i = start; i < start + insertsPerWriter; i++)
                        {
                            tree.Add(i, i * 10, ref wa);
                        }
                        wa.CommitChanges();
                        wa.Dispose();
                    }
                    finally
                    {
                        epochManager.ExitScope(writerDepth);
                        writersDone.Signal();
                    }
                }, TaskCreationOptions.LongRunning);
            }

            // Reader threads: repeatedly query initial keys while writers are active
            var readerTasks = new Task[readerCount];
            for (int r = 0; r < readerCount; r++)
            {
                readerTasks[r] = Task.Factory.StartNew(() =>
                {
                    var readerDepth = epochManager.EnterScope();
                    try
                    {
                        var ra = segment.CreateChunkAccessor();
                        startSignal.Wait();

                        while (!writersDone.IsSet)
                        {
                            for (int i = 1; i <= initialEntries; i++)
                            {
                                var result = tree.TryGet(i, ref ra);
                                if (!result.IsSuccess || result.Value != i * 10)
                                {
                                    Interlocked.Increment(ref readerErrors);
                                }
                            }
                        }
                        ra.Dispose();
                    }
                    finally
                    {
                        epochManager.ExitScope(readerDepth);
                    }
                }, TaskCreationOptions.LongRunning);
            }

            startSignal.Set();
            Task.WaitAll(writerTasks.Concat(readerTasks).ToArray());

            Assert.That(readerErrors, Is.EqualTo(0), "Readers should always get correct values during concurrent writes");
            Assert.That(tree.EntryCount, Is.EqualTo(initialEntries + writerCount * insertsPerWriter));
        }
        finally
        {
            epochManager.ExitScope(setupDepth);
        }
    }

    // ========================================
    // #113 — OLC Write Path (Remove)
    // ========================================

    [Test]
    [CancelAfter(5000)]
    public unsafe void Remove_ConcurrentDisjointKeys_AllRemoved()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 50, sizeof(Index32Chunk));

        const int threadCount = 4;
        const int keysPerThread = 100;
        const int totalKeys = threadCount * keysPerThread;

        var setupDepth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new IntSingleBTree<PersistentStore>(segment);

            // Populate tree with all keys
            for (int i = 1; i <= totalKeys; i++)
            {
                tree.Add(i, i * 10, ref accessor);
            }
            accessor.Dispose();
            Assert.That(tree.EntryCount, Is.EqualTo(totalKeys));

            // Each thread removes its own non-overlapping key range concurrently
            using var barrier = new Barrier(threadCount);
            int errors = 0;

            var tasks = new Task[threadCount];
            for (int t = 0; t < threadCount; t++)
            {
                int threadId = t;
                tasks[t] = Task.Factory.StartNew(() =>
                {
                    var writerDepth = epochManager.EnterScope();
                    try
                    {
                        var wa = segment.CreateChunkAccessor();
                        barrier.SignalAndWait();

                        int start = threadId * keysPerThread + 1;
                        for (int i = start; i < start + keysPerThread; i++)
                        {
                            bool removed = tree.Remove(i, out var value, ref wa);
                            if (!removed || value != i * 10)
                            {
                                Interlocked.Increment(ref errors);
                            }
                        }
                        wa.CommitChanges();
                        wa.Dispose();
                    }
                    finally
                    {
                        epochManager.ExitScope(writerDepth);
                    }
                }, TaskCreationOptions.LongRunning);
            }

            Task.WaitAll(tasks);
            Assert.That(errors, Is.EqualTo(0), "All removes should succeed with correct values");
            Assert.That(tree.EntryCount, Is.EqualTo(0), "Tree should be empty after removing all keys");
        }
        finally
        {
            epochManager.ExitScope(setupDepth);
        }
    }

    [Test]
    [CancelAfter(5000)]
    public unsafe void Remove_ConcurrentRemovesCausingMerges_TreeConsistent()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 100, sizeof(Index32Chunk));

        const int threadCount = 4;
        const int totalKeys = 2000;
        const int keysToRemovePerThread = 200;

        var setupDepth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new IntSingleBTree<PersistentStore>(segment);

            // Fill tree with many keys to create a multi-level tree
            for (int i = 1; i <= totalKeys; i++)
            {
                tree.Add(i, i * 10, ref accessor);
            }
            accessor.Dispose();
            Assert.That(tree.Height, Is.GreaterThan(1), "Tree should have multiple levels");

            // Each thread removes interleaved keys (forces merges at various leaves)
            using var barrier = new Barrier(threadCount);
            var exceptions = new ConcurrentBag<Exception>();

            var tasks = new Task[threadCount];
            for (int t = 0; t < threadCount; t++)
            {
                int threadId = t;
                tasks[t] = Task.Factory.StartNew(() =>
                {
                    var writerDepth = epochManager.EnterScope();
                    try
                    {
                        var wa = segment.CreateChunkAccessor();
                        barrier.SignalAndWait();

                        // Remove interleaved keys: thread 0 removes 1,5,9,...; thread 1 removes 2,6,10,...
                        for (int i = 0; i < keysToRemovePerThread; i++)
                        {
                            int key = i * threadCount + threadId + 1;
                            if (key <= totalKeys)
                            {
                                tree.Remove(key, out _, ref wa);
                            }
                        }
                        wa.CommitChanges();
                        wa.Dispose();
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                    finally
                    {
                        epochManager.ExitScope(writerDepth);
                    }
                }, TaskCreationOptions.LongRunning);
            }

            Task.WaitAll(tasks);
            Assert.That(exceptions, Is.Empty, () =>
                $"Workers threw {exceptions.Count} exception(s) during concurrent removes; first:\n{exceptions.First()}");
            Assert.That(tree.EntryCount, Is.EqualTo(totalKeys - threadCount * keysToRemovePerThread));

            // Verify tree structural integrity
            var verifyAccessor = segment.CreateChunkAccessor();
            tree.CheckConsistency(ref verifyAccessor);
            verifyAccessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(setupDepth);
        }
    }

    [Test]
    [CancelAfter(5000)]
    public unsafe void Remove_MixedInsertAndRemove_NoCorruption()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 100, sizeof(Index32Chunk));

        const int initialEntries = 500;
        const int writerCount = 2;
        const int removerCount = 2;
        const int insertsPerWriter = 300;
        const int removesPerRemover = 100;

        var setupDepth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new IntSingleBTree<PersistentStore>(segment);

            // Populate initial entries
            for (int i = 1; i <= initialEntries; i++)
            {
                tree.Add(i, i * 10, ref accessor);
            }
            accessor.Dispose();

            using var startSignal = new ManualResetEventSlim(false);
            var exceptions = new ConcurrentBag<Exception>();

            // Insert threads: add keys in high range (no overlap with removes)
            var insertTasks = new Task[writerCount];
            for (int w = 0; w < writerCount; w++)
            {
                int writerId = w;
                insertTasks[w] = Task.Factory.StartNew(() =>
                {
                    var writerDepth = epochManager.EnterScope();
                    try
                    {
                        var wa = segment.CreateChunkAccessor();
                        startSignal.Wait();

                        int start = 10_000 + writerId * insertsPerWriter;
                        for (int i = start; i < start + insertsPerWriter; i++)
                        {
                            tree.Add(i, i * 10, ref wa);
                        }
                        wa.CommitChanges();
                        wa.Dispose();
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                    finally
                    {
                        epochManager.ExitScope(writerDepth);
                    }
                }, TaskCreationOptions.LongRunning);
            }

            // Remove threads: remove from initial entries (low range)
            var removeTasks = new Task[removerCount];
            for (int r = 0; r < removerCount; r++)
            {
                int removerId = r;
                removeTasks[r] = Task.Factory.StartNew(() =>
                {
                    var removerDepth = epochManager.EnterScope();
                    try
                    {
                        var wa = segment.CreateChunkAccessor();
                        startSignal.Wait();

                        // Each remover removes a disjoint subset of initial keys
                        int start = removerId * removesPerRemover + 1;
                        for (int i = start; i < start + removesPerRemover; i++)
                        {
                            tree.Remove(i, out _, ref wa);
                        }
                        wa.CommitChanges();
                        wa.Dispose();
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                    finally
                    {
                        epochManager.ExitScope(removerDepth);
                    }
                }, TaskCreationOptions.LongRunning);
            }

            startSignal.Set();
            Task.WaitAll(insertTasks.Concat(removeTasks).ToArray());

            Assert.That(exceptions, Is.Empty, () =>
                $"Workers threw {exceptions.Count} exception(s) during mixed insert+remove; first:\n{exceptions.First()}");

            int expectedCount = initialEntries + writerCount * insertsPerWriter - removerCount * removesPerRemover;
            Assert.That(tree.EntryCount, Is.EqualTo(expectedCount));

            // Verify tree structural integrity after concurrent mixed operations
            var verifyAccessor = segment.CreateChunkAccessor();
            tree.CheckConsistency(ref verifyAccessor);
            verifyAccessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(setupDepth);
        }
    }

    [Test]
    [CancelAfter(5000)]
    public unsafe void Remove_ConcurrentRemoveWithReaders_ReadersGetCorrectValues()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 50, sizeof(Index32Chunk));

        const int totalKeys = 400;
        const int safeKeyStart = 301; // keys 301-400 are never removed — readers verify these
        const int readerCount = 4;
        const int removerCount = 2;
        const int removesPerRemover = 100;

        var setupDepth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new IntSingleBTree<PersistentStore>(segment);

            for (int i = 1; i <= totalKeys; i++)
            {
                tree.Add(i, i * 10, ref accessor);
            }
            accessor.Dispose();

            using var startSignal = new ManualResetEventSlim(false);
            using var removersDone = new CountdownEvent(removerCount);
            int readerErrors = 0;

            // Remover threads: remove keys 1-200 (disjoint ranges per thread)
            var removerTasks = new Task[removerCount];
            for (int r = 0; r < removerCount; r++)
            {
                int removerId = r;
                removerTasks[r] = Task.Factory.StartNew(() =>
                {
                    var removerDepth = epochManager.EnterScope();
                    try
                    {
                        var wa = segment.CreateChunkAccessor();
                        startSignal.Wait();

                        int start = removerId * removesPerRemover + 1;
                        for (int i = start; i < start + removesPerRemover; i++)
                        {
                            tree.Remove(i, out _, ref wa);
                        }
                        wa.CommitChanges();
                        wa.Dispose();
                    }
                    finally
                    {
                        epochManager.ExitScope(removerDepth);
                        removersDone.Signal();
                    }
                }, TaskCreationOptions.LongRunning);
            }

            // Reader threads: continuously verify safe keys (301-400) are always correct
            var readerTasks = new Task[readerCount];
            for (int rd = 0; rd < readerCount; rd++)
            {
                readerTasks[rd] = Task.Factory.StartNew(() =>
                {
                    var readerDepth = epochManager.EnterScope();
                    try
                    {
                        var ra = segment.CreateChunkAccessor();
                        startSignal.Wait();

                        while (!removersDone.IsSet)
                        {
                            for (int i = safeKeyStart; i <= totalKeys; i++)
                            {
                                var result = tree.TryGet(i, ref ra);
                                if (!result.IsSuccess || result.Value != i * 10)
                                {
                                    Interlocked.Increment(ref readerErrors);
                                }
                            }
                        }
                        ra.Dispose();
                    }
                    finally
                    {
                        epochManager.ExitScope(readerDepth);
                    }
                }, TaskCreationOptions.LongRunning);
            }

            startSignal.Set();
            Task.WaitAll(removerTasks.Concat(readerTasks).ToArray());

            Assert.That(readerErrors, Is.EqualTo(0), "Readers should always get correct values for safe keys during concurrent removes");
            Assert.That(tree.EntryCount, Is.EqualTo(totalKeys - removerCount * removesPerRemover));
        }
        finally
        {
            epochManager.ExitScope(setupDepth);
        }
    }

    // ========================================
    // #115 — Epoch-Deferred Deallocation
    // ========================================

    [Test]
    [CancelAfter(5000)]
    public unsafe void DeferredDeallocation_MergeTriggersMarkObsolete()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(Index32Chunk));

        IntSingleBTree<PersistentStore> tree;
        int allocatedBefore;

        // Phase 1: Build tree with 600 keys (multi-leaf, depth >= 2) in its own epoch scope.
        // With capacity 29, 600 keys create ~40 leaves — ensures enough structure for merge testing.
        {
            var depth = epochManager.EnterScope();
            var accessor = segment.CreateChunkAccessor();
            tree = new IntSingleBTree<PersistentStore>(segment);
            for (int i = 0; i < 600; i++)
            {
                tree.Add(i, i * 10, ref accessor);
            }
            Assert.That(tree.EntryCount, Is.EqualTo(600));
            allocatedBefore = segment.AllocatedChunkCount;
            accessor.Dispose();
            epochManager.ExitScope(depth); // Advances global epoch
        }

        // Phase 2: Remove keys to trigger merges, building up deferred nodes.
        // Keep 100 keys (0-49, 550-599) so the tree retains multiple non-root leaves.
        {
            var depth = epochManager.EnterScope();
            var accessor = segment.CreateChunkAccessor();
            for (int i = 50; i < 550; i++)
            {
                tree.Remove(i, out _, ref accessor);
            }
            Assert.That(tree.EntryCount, Is.EqualTo(100));

            // Within the same epoch scope, deferred nodes can't be reclaimed yet.
            var deferredAfterRemoves = tree.DeferredNodeCount;
            Assert.That(deferredAfterRemoves, Is.GreaterThan(0),
                "Merges should have produced deferred nodes pending reclamation");

            accessor.Dispose();
            epochManager.ExitScope(depth); // Advances global epoch
        }

        // Phase 3: Enter a new epoch. Remove keys from non-root leaves to trigger pessimistic
        // fallback, which runs Reclaim and frees deferred nodes from Phase 2.
        // With 100 remaining keys in multiple leaves, removes trigger pessimistic once
        // count drops below capacity/2.
        {
            var depth = epochManager.EnterScope();
            var accessor = segment.CreateChunkAccessor();
            var deferredBefore = tree.DeferredNodeCount;
            // Remove enough keys to force pessimistic on non-root leaves
            for (int i = 550; i < 600; i++)
            {
                tree.Remove(i, out _, ref accessor);
            }

            var deferredAfter = tree.DeferredNodeCount;
            Assert.That(deferredAfter, Is.LessThan(deferredBefore),
                $"Deferred nodes should decrease after Reclaim: before={deferredBefore}, after={deferredAfter}");

            var allocatedAfter = segment.AllocatedChunkCount;
            Assert.That(allocatedAfter, Is.LessThan(allocatedBefore),
                $"Merged nodes should have been reclaimed: allocated before={allocatedBefore}, after={allocatedAfter}");

            // Tree should still be consistent
            tree.CheckConsistency(ref accessor);
            accessor.Dispose();
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    [CancelAfter(5000)]
    public unsafe void DeferredDeallocation_PinnedEpoch_DefersReclamation()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(Index32Chunk));

        IntSingleBTree<PersistentStore> tree;
        int allocatedBefore;

        // Phase 1: Build tree in its own epoch scope (exiting advances global epoch).
        // With capacity 29, 500 keys create ~34 leaves — enough structure for merge testing.
        {
            var setupDepth = epochManager.EnterScope();
            var accessor = segment.CreateChunkAccessor();
            tree = new IntSingleBTree<PersistentStore>(segment);
            for (int i = 0; i < 500; i++)
            {
                tree.Add(i, i * 10, ref accessor);
            }
            accessor.Dispose();
            allocatedBefore = segment.AllocatedChunkCount;
            epochManager.ExitScope(setupDepth); // Advances global epoch
        }

        // Phase 2: Pin an epoch on a background thread (simulates a reader holding an old epoch)
        using var readerHoldingEpoch = new ManualResetEventSlim(false);
        using var allowReaderExit = new ManualResetEventSlim(false);
        var readerTask = Task.Factory.StartNew(() =>
        {
            var readerDepth = epochManager.EnterScope(); // Pins at current global epoch
            readerHoldingEpoch.Set();  // Signal that epoch is pinned
            allowReaderExit.Wait();    // Hold the epoch until told to release
            epochManager.ExitScope(readerDepth);
        }, TaskCreationOptions.LongRunning);

        readerHoldingEpoch.Wait(); // Wait until reader has pinned the epoch

        // Phase 3: Remove keys (main thread enters new scope → higher epoch than reader's pinned one).
        // retireEpoch will be > reader's pinned epoch, so reclamation should be deferred.
        {
            var removeDepth = epochManager.EnterScope();
            var accessor = segment.CreateChunkAccessor();
            for (int i = 100; i < 400; i++)
            {
                tree.Remove(i, out _, ref accessor);
            }
            Assert.That(tree.EntryCount, Is.EqualTo(200));
            tree.CheckConsistency(ref accessor);
            accessor.Dispose();
            epochManager.ExitScope(removeDepth); // Advances global epoch again
        }

        // Phase 4: Release the reader epoch, then trigger reclamation via pessimistic removes
        allowReaderExit.Set();
        readerTask.Wait(); // Reader exits scope → epoch advances

        {
            var reclaimDepth = epochManager.EnterScope();
            var accessor = segment.CreateChunkAccessor();
            // Remove enough keys to force pessimistic fallback (which triggers Reclaim)
            var remaining = tree.EntryCount;
            for (int i = 0; i < 50; i++)
            {
                tree.Remove(i, out _, ref accessor);
            }
            Assert.That(tree.EntryCount, Is.EqualTo(remaining - 50));

            var allocatedAfter = segment.AllocatedChunkCount;
            Assert.That(allocatedAfter, Is.LessThan(allocatedBefore), "Deferred nodes should be reclaimed after reader exits epoch");

            tree.CheckConsistency(ref accessor);
            accessor.Dispose();
            epochManager.ExitScope(reclaimDepth);
        }
    }

    [Test]
    [CancelAfter(5000)]
    public unsafe void DeferredDeallocation_InsertDeleteCycles_NoMemoryLeak()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(Index32Chunk));

        IntSingleBTree<PersistentStore> tree;
        int baselineAllocated;

        // Build initial tree
        {
            var depth = epochManager.EnterScope();
            var accessor = segment.CreateChunkAccessor();
            tree = new IntSingleBTree<PersistentStore>(segment);
            for (int i = 0; i < 100; i++)
            {
                tree.Add(i, i * 10, ref accessor);
            }
            baselineAllocated = segment.AllocatedChunkCount;
            accessor.Dispose();
            epochManager.ExitScope(depth);
        }

        // Run 5 cycles of bulk delete + re-insert, each in its own epoch scope.
        // Epoch advances between scopes, enabling reclamation of deferred nodes.
        for (int cycle = 0; cycle < 5; cycle++)
        {
            // Delete most keys to trigger many merges
            {
                var depth = epochManager.EnterScope();
                var accessor = segment.CreateChunkAccessor();
                for (int i = 10; i < 90; i++)
                {
                    tree.Remove(i, out _, ref accessor);
                }
                Assert.That(tree.EntryCount, Is.EqualTo(20));
                accessor.Dispose();
                epochManager.ExitScope(depth);
            }

            // Re-insert all keys (Reclaim runs inside AddOrUpdateCorePessimistic, freeing deferred nodes)
            {
                var depth = epochManager.EnterScope();
                var accessor = segment.CreateChunkAccessor();
                for (int i = 10; i < 90; i++)
                {
                    tree.Add(i, (cycle + 1) * 1000 + i, ref accessor);
                }
                Assert.That(tree.EntryCount, Is.EqualTo(100));
                accessor.Dispose();
                epochManager.ExitScope(depth);
            }
        }

        // Final check: allocated count should not have grown unboundedly
        {
            var depth = epochManager.EnterScope();
            var accessor = segment.CreateChunkAccessor();

            // Trigger one more operation to reclaim any remaining deferred nodes from last cycle
            tree.Remove(0, out _, ref accessor);
            tree.Add(0, 99999, ref accessor);

            // Force-flush deferred nodes (DeferredReclaim batches every 64 mutations, so the 2 ops above won't trigger it)
            tree.FlushDeferredNodes();

            var finalAllocated = segment.AllocatedChunkCount;
            // Allow 3x headroom (tree may have different shape after cycles, but shouldn't grow unboundedly)
            Assert.That(finalAllocated, Is.LessThanOrEqualTo(baselineAllocated * 3),
                $"Allocated chunks ({finalAllocated}) should not grow unboundedly vs baseline ({baselineAllocated})");

            // No deferred nodes pending
            Assert.That(tree.DeferredNodeCount, Is.EqualTo(0));

            // Tree is consistent
            tree.CheckConsistency(ref accessor);
            accessor.Dispose();
            epochManager.ExitScope(depth);
        }
    }

    // ========================================
    // #114 — Compound Move/MoveValue
    // ========================================

    [Test]
    [CancelAfter(5000)]
    public unsafe void Move_SameLeaf_MovesEntry()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(Index32Chunk));
        var depth = epochManager.EnterScope();

        var accessor = segment.CreateChunkAccessor();
        var tree = new IntSingleBTree<PersistentStore>(segment);

        // Insert keys 1-10 — all fit in root leaf (capacity=13), so move is same-leaf
        for (int i = 1; i <= 10; i++)
        {
            tree.Add(i, i * 100, ref accessor);
        }

        // Move key 5 → key 50 (same leaf)
        var result = tree.Move(5, 50, 500, ref accessor);
        Assert.That(result, Is.True, "Move should succeed");

        // Verify old key is gone
        Assert.That(tree.TryGet(5, ref accessor).IsFailure, Is.True, "Old key 5 should be gone");

        // Verify new key exists with correct value
        var getResult = tree.TryGet(50, ref accessor);
        Assert.That(getResult.IsSuccess, Is.True, "New key 50 should exist");
        Assert.That(getResult.Value, Is.EqualTo(500));

        // EntryCount unchanged (1 remove + 1 insert)
        Assert.That(tree.EntryCount, Is.EqualTo(10));
        tree.CheckConsistency(ref accessor);
        accessor.Dispose();
        epochManager.ExitScope(depth);
    }

    [Test]
    [CancelAfter(5000)]
    public unsafe void Move_DifferentLeaves_MovesEntry()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(Index32Chunk));
        var depth = epochManager.EnterScope();

        var accessor = segment.CreateChunkAccessor();
        var tree = new IntSingleBTree<PersistentStore>(segment);

        // Insert enough keys to force multiple leaves (capacity=13, need >13 keys)
        for (int i = 1; i <= 50; i++)
        {
            tree.Add(i, i * 100, ref accessor);
        }

        // Move key 3 (first leaf) → key 300 (well beyond last leaf)
        var result = tree.Move(3, 300, 12345, ref accessor);
        Assert.That(result, Is.True, "Move should succeed");

        // Verify old key is gone
        Assert.That(tree.TryGet(3, ref accessor).IsFailure, Is.True, "Old key 3 should be gone");

        // Verify new key exists
        var getResult = tree.TryGet(300, ref accessor);
        Assert.That(getResult.IsSuccess, Is.True, "New key 300 should exist");
        Assert.That(getResult.Value, Is.EqualTo(12345));

        Assert.That(tree.EntryCount, Is.EqualTo(50));
        tree.CheckConsistency(ref accessor);
        accessor.Dispose();
        epochManager.ExitScope(depth);
    }

    [Test]
    [CancelAfter(5000)]
    public unsafe void Move_OldKeyNotFound_ReturnsFalse()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(Index32Chunk));
        var depth = epochManager.EnterScope();

        var accessor = segment.CreateChunkAccessor();
        var tree = new IntSingleBTree<PersistentStore>(segment);

        for (int i = 1; i <= 10; i++)
        {
            tree.Add(i, i * 100, ref accessor);
        }

        // Move non-existent key
        var result = tree.Move(99, 50, 500, ref accessor);
        Assert.That(result, Is.False, "Move should fail for non-existent key");
        Assert.That(tree.EntryCount, Is.EqualTo(10), "EntryCount should be unchanged");
        tree.CheckConsistency(ref accessor);
        accessor.Dispose();
        epochManager.ExitScope(depth);
    }

    [Test]
    [CancelAfter(5000)]
    public unsafe void Move_NewKeyAlreadyExists_ReturnsFalse()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(Index32Chunk));
        var depth = epochManager.EnterScope();

        var accessor = segment.CreateChunkAccessor();
        var tree = new IntSingleBTree<PersistentStore>(segment);

        for (int i = 1; i <= 10; i++)
        {
            tree.Add(i, i * 100, ref accessor);
        }

        // Move key 5 → key 8 (already exists)
        var result = tree.Move(5, 8, 500, ref accessor);
        Assert.That(result, Is.False, "Move should fail when newKey already exists");

        // Verify key 5 is still present (no modification made)
        Assert.That(tree.TryGet(5, ref accessor).IsSuccess, Is.True, "Old key 5 should still exist");
        Assert.That(tree.EntryCount, Is.EqualTo(10), "EntryCount should be unchanged");
        tree.CheckConsistency(ref accessor);
        accessor.Dispose();
        epochManager.ExitScope(depth);
    }

    [Test]
    [CancelAfter(5000)]
    public unsafe void Move_ConcurrentOppositeDirections_DeadlockFree()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(Index32Chunk));
        var depth = epochManager.EnterScope();

        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new IntSingleBTree<PersistentStore>(segment);

            // Insert enough keys to fill multiple leaves
            for (int i = 1; i <= 50; i++)
            {
                tree.Add(i, i * 100, ref accessor);
            }
            accessor.Dispose();

            // Two threads simultaneously move keys in opposite directions between distant leaves
            // This tests deadlock freedom via ChunkId-ordered locking
            var barrier = new Barrier(2);
            var exceptions = new Exception[2];

            var t1 = new Thread(() =>
            {
                var d = epochManager.EnterScope();
                try
                {
                    barrier.SignalAndWait();
                    var acc = segment.CreateChunkAccessor();
                    // Move key from low range → high range
                    tree.Move(2, 200, 222, ref acc);
                    acc.CommitChanges();
                    acc.Dispose();
                }
                catch (Exception ex) { exceptions[0] = ex; }
                finally { epochManager.ExitScope(d); }
            });
            var t2 = new Thread(() =>
            {
                var d = epochManager.EnterScope();
                try
                {
                    barrier.SignalAndWait();
                    var acc = segment.CreateChunkAccessor();
                    // Move key from high range → low range
                    tree.Move(49, 0, 111, ref acc);
                    acc.CommitChanges();
                    acc.Dispose();
                }
                catch (Exception ex) { exceptions[1] = ex; }
                finally { epochManager.ExitScope(d); }
            });

            t1.Start();
            t2.Start();
            t1.Join(3000);
            t2.Join(3000);

            Assert.That(exceptions[0], Is.Null, $"Thread 1 failed: {exceptions[0]}");
            Assert.That(exceptions[1], Is.Null, $"Thread 2 failed: {exceptions[1]}");

            // Verify tree is consistent (some moves may or may not have succeeded depending on timing)
            var verifyAccessor = segment.CreateChunkAccessor();
            tree.CheckConsistency(ref verifyAccessor);
            verifyAccessor.Dispose();
        }
        epochManager.ExitScope(depth);
    }

    [Test]
    [CancelAfter(5000)]
    public unsafe void MoveValue_SameLeaf_MovesElement()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(Index32Chunk));
        var depth = epochManager.EnterScope();

        var accessor = segment.CreateChunkAccessor();
        var tree = new IntMultipleBTree<PersistentStore>(segment);

        // Add multiple values under key 1 and key 2
        var eid0 = tree.Add(1, 10, ref accessor);
        var eid1 = tree.Add(1, 11, ref accessor);
        var eid2 = tree.Add(2, 20, ref accessor);

        // Move element eid0 (value=10) from key 1 → key 2
        var newEid = tree.MoveValue(1, 2, eid0, 10, ref accessor, out var oldHead, out var newHead);
        Assert.That(newEid, Is.GreaterThanOrEqualTo(0), "MoveValue should return valid element ID");
        Assert.That(oldHead, Is.GreaterThan(0), "Old HEAD buffer ID should be valid");
        Assert.That(newHead, Is.GreaterThan(0), "New HEAD buffer ID should be valid");

        // Key 1 should still exist with 1 element (eid1=11)
        {
            using var buf = tree.TryGetMultiple(1, ref accessor);
            Assert.That(buf.IsValid, Is.True, "Key 1 should still have elements");
            Assert.That(buf.ReadOnlyElements.Length, Is.EqualTo(1), "Key 1 should have 1 element left");
        }

        // Key 2 should have 2 elements (eid2=20 + moved eid0=10)
        {
            using var buf = tree.TryGetMultiple(2, ref accessor);
            Assert.That(buf.IsValid, Is.True, "Key 2 should have elements");
            Assert.That(buf.ReadOnlyElements.Length, Is.EqualTo(2), "Key 2 should have 2 elements");
        }

        tree.CheckConsistency(ref accessor);
        accessor.Dispose();
        epochManager.ExitScope(depth);
    }

    [Test]
    [CancelAfter(5000)]
    public unsafe void MoveValue_ToNewKey_CreatesKeyAndBuffer()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(Index32Chunk));
        var depth = epochManager.EnterScope();

        var accessor = segment.CreateChunkAccessor();
        var tree = new IntMultipleBTree<PersistentStore>(segment);

        // Add 2 values under key 1
        var eid0 = tree.Add(1, 10, ref accessor);
        var eid1 = tree.Add(1, 11, ref accessor);

        Assert.That(tree.EntryCount, Is.EqualTo(1), "Should have 1 BTree entry (key 1)");

        // Move element eid0 (value=10) from key 1 → key 99 (new key)
        var newEid = tree.MoveValue(1, 99, eid0, 10, ref accessor, out var oldHead, out var newHead);
        Assert.That(newEid, Is.GreaterThanOrEqualTo(0), "MoveValue should return valid element ID");

        Assert.That(tree.EntryCount, Is.EqualTo(2), "Should have 2 BTree entries (key 1 + key 99)");

        // Key 99 should exist with 1 element
        {
            using var buf = tree.TryGetMultiple(99, ref accessor);
            Assert.That(buf.IsValid, Is.True, "Key 99 should exist");
            Assert.That(buf.ReadOnlyElements.Length, Is.EqualTo(1), "Key 99 should have 1 element");
        }

        tree.CheckConsistency(ref accessor);
        accessor.Dispose();
        epochManager.ExitScope(depth);
    }

    [Test]
    [CancelAfter(5000)]
    public unsafe void MoveValue_LastElement_RemovesOldKey()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(Index32Chunk));
        var depth = epochManager.EnterScope();

        var accessor = segment.CreateChunkAccessor();
        var tree = new IntMultipleBTree<PersistentStore>(segment);

        // Add single value under key 1
        var eid0 = tree.Add(1, 10, ref accessor);
        var eid1 = tree.Add(2, 20, ref accessor);

        Assert.That(tree.EntryCount, Is.EqualTo(2));

        // Move the only element from key 1 → key 2 — should remove key 1
        var newEid = tree.MoveValue(1, 2, eid0, 10, ref accessor, out _, out _);
        Assert.That(newEid, Is.GreaterThanOrEqualTo(0));

        // Key 1 should be gone (buffer was emptied → BTree entry removed)
        Assert.That(tree.EntryCount, Is.EqualTo(1), "Key 1 BTree entry should have been removed");
        {
            using var buf = tree.TryGetMultiple(1, ref accessor);
            Assert.That(buf.IsValid, Is.False, "Key 1 should not exist after moving its last element");
        }

        // Key 2 should have 2 elements
        {
            using var buf = tree.TryGetMultiple(2, ref accessor);
            Assert.That(buf.IsValid, Is.True);
            Assert.That(buf.ReadOnlyElements.Length, Is.EqualTo(2));
        }

        tree.CheckConsistency(ref accessor);
        accessor.Dispose();
        epochManager.ExitScope(depth);
    }

    [Test]
    [CancelAfter(5000)]
    public unsafe void MoveValue_OldKeyNotFound_ReturnsMinusOne()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(Index32Chunk));
        var depth = epochManager.EnterScope();

        var accessor = segment.CreateChunkAccessor();
        var tree = new IntMultipleBTree<PersistentStore>(segment);

        tree.Add(1, 10, ref accessor);

        // Move from non-existent key
        var newEid = tree.MoveValue(99, 1, 0, 10, ref accessor, out var oldHead, out var newHead);
        Assert.That(newEid, Is.EqualTo(-1), "MoveValue should return -1 for non-existent key");
        Assert.That(oldHead, Is.EqualTo(-1));
        Assert.That(newHead, Is.EqualTo(-1));

        tree.CheckConsistency(ref accessor);
        accessor.Dispose();
        epochManager.ExitScope(depth);
    }

    [Test]
    [CancelAfter(5000)]
    public unsafe void MoveValue_DifferentLeaves_MovesElement()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(Index32Chunk));
        var depth = epochManager.EnterScope();

        var accessor = segment.CreateChunkAccessor();
        var tree = new IntMultipleBTree<PersistentStore>(segment);

        // Insert enough distinct keys to force multiple leaves
        // capacity=13 for L32, so 20+ keys should split
        for (int i = 1; i <= 20; i++)
        {
            tree.Add(i, i * 100, ref accessor);
        }

        // Add extra element under key 1
        var eid = tree.Add(1, 11, ref accessor);

        // Move element from key 1 (low leaf) → key 20 (high leaf, different leaf)
        var newEid = tree.MoveValue(1, 20, eid, 11, ref accessor, out var oldHead, out var newHead);
        Assert.That(newEid, Is.GreaterThanOrEqualTo(0), "MoveValue across leaves should succeed");

        // Key 1 should still exist with 1 element
        {
            using var buf = tree.TryGetMultiple(1, ref accessor);
            Assert.That(buf.IsValid, Is.True);
            Assert.That(buf.ReadOnlyElements.Length, Is.EqualTo(1));
        }

        // Key 20 should have 2 elements
        {
            using var buf = tree.TryGetMultiple(20, ref accessor);
            Assert.That(buf.IsValid, Is.True);
            Assert.That(buf.ReadOnlyElements.Length, Is.EqualTo(2));
        }

        tree.CheckConsistency(ref accessor);
        accessor.Dispose();
        epochManager.ExitScope(depth);
    }

    // ========================================
    // #116 — EnumerateLeaves + Cleanup
    // ========================================

    [Test]
    [CancelAfter(5000)]
    public unsafe void EnumerateLeaves_SingleThread_MatchesTreeContents()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 100, sizeof(Index32Chunk));
        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new IntSingleBTree<PersistentStore>(segment);

            // Insert enough items to span multiple leaves
            const int count = 200;
            for (int i = 0; i < count; i++)
            {
                tree.Add(i, i * 10, ref accessor);
            }

            // Enumerate and verify exact match
            var keys = new System.Collections.Generic.List<int>();
            var values = new System.Collections.Generic.List<int>();
            foreach (var kv in tree.EnumerateLeaves())
            {
                keys.Add(kv.Key);
                values.Add(kv.Value);
            }

            Assert.That(keys.Count, Is.EqualTo(count));
            for (int i = 0; i < count; i++)
            {
                Assert.That(keys[i], Is.EqualTo(i), $"Key mismatch at index {i}");
                Assert.That(values[i], Is.EqualTo(i * 10), $"Value mismatch at index {i}");
            }

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    [CancelAfter(5000)]
    public unsafe void EnumerateLeaves_DuringConcurrentWrites_NoCorruption()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 300, sizeof(Index32Chunk));
        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new IntSingleBTree<PersistentStore>(segment);

            // Pre-populate with 100 entries
            for (int i = 0; i < 100; i++)
            {
                tree.Add(i, i * 10, ref accessor);
            }

            var stop = new ManualResetEventSlim(false);
            var writerDone = new ManualResetEventSlim(false);
            int enumerationCount = 0;

            // Writer thread: continuously adds entries 1000+
            var writer = Task.Run(() =>
            {
                var ed = epochManager.EnterScope();
                try
                {
                    var wa = segment.CreateChunkAccessor();
                    for (int i = 1000; i < 1200 && !stop.IsSet; i++)
                    {
                        tree.Add(i, i * 10, ref wa);
                    }
                    wa.Dispose();
                }
                finally
                {
                    epochManager.ExitScope(ed);
                    writerDone.Set();
                }
            });

            // Main thread: enumerate multiple times while writer is active.
            // Per-leaf OLC does NOT guarantee global order under concurrent writes (§7.4).
            // We verify: no crash, reasonable count, and enumeration terminates.
            for (int round = 0; round < 5; round++)
            {
                int count = 0;
                foreach (var kv in tree.EnumerateLeaves())
                {
                    count++;
                }
                enumerationCount += count;
            }

            stop.Set();
            writerDone.Wait(3000);

            // We successfully enumerated without crash or corruption
            Assert.That(enumerationCount, Is.GreaterThan(0), "Should have enumerated some entries");

            tree.CheckConsistency(ref accessor);
            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    [CancelAfter(5000)]
    public unsafe void FullIntegration_ConcurrentReadWriteEnumerate_NoCorruption()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 300, sizeof(Index32Chunk));
        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new IntSingleBTree<PersistentStore>(segment);

            // Pre-populate
            for (int i = 0; i < 50; i++)
            {
                tree.Add(i, i * 10, ref accessor);
            }

            var barrier = new Barrier(4);
            var stop = new ManualResetEventSlim(false);
            int readSuccesses = 0;
            int writeSuccesses = 0;
            int enumSuccesses = 0;
            Exception fault = null;

            // 2 reader threads
            var readers = Enumerable.Range(0, 2).Select(tid => Task.Run(() =>
            {
                var ed = epochManager.EnterScope();
                try
                {
                    var ra = segment.CreateChunkAccessor();
                    barrier.SignalAndWait();
                    var rng = new Random(tid);
                    while (!stop.IsSet)
                    {
                        int key = rng.Next(0, 50);
                        var result = tree.TryGet(key, ref ra);
                        if (result.IsSuccess)
                        {
                            Interlocked.Increment(ref readSuccesses);
                        }
                    }
                    ra.Dispose();
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref fault, ex, null);
                }
                finally
                {
                    epochManager.ExitScope(ed);
                }
            })).ToArray();

            // 1 writer thread
            var writer = Task.Run(() =>
            {
                var ed = epochManager.EnterScope();
                try
                {
                    var wa = segment.CreateChunkAccessor();
                    barrier.SignalAndWait();
                    int key = 1000;
                    while (!stop.IsSet && key < 1200)
                    {
                        tree.Add(key, key * 10, ref wa);
                        Interlocked.Increment(ref writeSuccesses);
                        key++;
                    }
                    wa.Dispose();
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref fault, ex, null);
                }
                finally
                {
                    epochManager.ExitScope(ed);
                }
            });

            // 1 enumerator thread — per-leaf OLC does NOT guarantee global order under
            // concurrent writes (§7.4: "entries may be seen twice or missed"). We only verify
            // that enumeration completes without crash and yields a reasonable count.
            var enumerator = Task.Run(() =>
            {
                var ed = epochManager.EnterScope();
                try
                {
                    barrier.SignalAndWait();
                    while (!stop.IsSet)
                    {
                        int count = 0;
                        foreach (var kv in tree.EnumerateLeaves())
                        {
                            count++;
                        }
                        if (count > 0)
                        {
                            Interlocked.Increment(ref enumSuccesses);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref fault, ex, null);
                }
                finally
                {
                    epochManager.ExitScope(ed);
                }
            });

            // Let them run for a short time
            Thread.Sleep(200);
            stop.Set();
            Task.WaitAll([..readers, writer, enumerator], 3000);

            Assert.That(fault, Is.Null, $"Concurrent operation failed: {fault?.Message}");
            Assert.That(readSuccesses, Is.GreaterThan(0), "Readers should have succeeded");
            Assert.That(writeSuccesses, Is.GreaterThan(0), "Writer should have succeeded");
            Assert.That(enumSuccesses, Is.GreaterThan(0), "Enumerator should have completed at least once");

            tree.CheckConsistency(ref accessor);
            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }
}
