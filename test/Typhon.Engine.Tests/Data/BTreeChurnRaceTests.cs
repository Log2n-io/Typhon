using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// #1322 (found on #1321): an AllowMultiple B+Tree under concurrent insert/remove churn of random keys — the shape <c>ChaosStressTests.UltimateStress_AllSubsystems</c> puts
/// on CompD's double index (2 creators adding random keys, 2 deleters removing them), stripped of transactions, the WAL and the ECS.
/// </summary>
[TestFixture]
[NonParallelizable]
class BTreeChurnRaceTests
{
    private IServiceProvider _serviceProvider;

    [SetUp]
    public void Setup()
    {
        var name = $"btchurn_{TestContext.CurrentContext.Test.Name}";
        var services = new ServiceCollection();
        services
            .AddLogging()
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddScopedManagedPagedMemoryMappedFile(options =>
            {
                options.DatabaseName = name[..Math.Min(63, name.Length)];
                options.DatabaseCacheSize = 64UL * 1024 * 1024;
            });

        _serviceProvider = services.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
    }

    [TearDown]
    public void TearDown() => (_serviceProvider as IDisposable)?.Dispose();

    private readonly record struct Entry(double Key, int ElementId, int Value);

    private const int MaxLive = 20_000;

    private static string FirstFrame(Exception ex) => ex.StackTrace?.Split((char)10)[0].Trim();

    /// <summary>
    /// #1321: removing a key that is ABSENT from its leaf, where the key falls between the leaf's last entry and its HighKey. The B-link right-walk in
    /// OptimisticDescendToLeaf decides "past this leaf" by content (key &gt; last), so it overshot into the next leaf; the authoritative-leaf search then
    /// rejected that leaf (key below its lower bound) and re-descended into the same overshoot until it threw "no progress in 200 retries" — past the WAL
    /// append in the engine, which halts it. Single-threaded and deterministic: a random removal order puts many removed keys in that band.
    /// </summary>
    [Test]
    public unsafe void RemoveOfAnAbsentKeyBetweenLastAndHighKey_ReportsNotFound()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 400, sizeof(Index64Chunk));
        var tree = new DoubleMultipleBTree<PersistentStore>(segment);

        var scope = epochManager.EnterScope();
        var accessor = segment.CreateChunkAccessor();
        try
        {
            const int n = 2000;
            var elementIds = new int[n + 1];
            for (var i = 1; i <= n; i++)
            {
                elementIds[i] = tree.Add(i, i, ref accessor);
            }

            var order = new int[n];
            for (var i = 0; i < n; i++)
            {
                order[i] = i + 1;
            }
            new Random(1321).Shuffle(order);

            foreach (var k in order)
            {
                Assert.That(tree.RemoveValue(k, elementIds[k], k, ref accessor), Is.True, $"present key {k}");
                Assert.That(tree.RemoveValue(k, elementIds[k], k, ref accessor), Is.False, $"absent key {k} must report not-found");
            }
        }
        finally
        {
            accessor.Dispose();
            epochManager.ExitScope(scope);
        }
    }

    /// <summary>
    /// #1321: a write that throws while it holds a node latch faults the tree, so the next write throws instead of spinning on the latch it left held. The
    /// throw comes from the root collapse's probe, which runs with the root's write latch held (IXW-10). It used to come from a new AllowMultiple key's buffer
    /// allocation under the leaf latch, the one allocation #1190 moved out of every latch.
    /// </summary>
    [Test]
    [VerifiesRule("IXW-09")]
    [CancelAfter(20_000)]
    public unsafe void AFaultUnderANodeLatch_FaultsTheTree_SoLaterWritesThrowInsteadOfSpinning()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 40, sizeof(Index32Chunk));
        var tree = new IntSingleBTree<PersistentStore>(segment);

        var scope = epochManager.EnterScope();
        var accessor = segment.CreateChunkAccessor();
        var top = 0;
        try
        {
            while (tree.Height < 2)
            {
                top++;
                tree.Add(top, top, ref accessor);
            }
        }
        finally
        {
            accessor.Dispose();
            epochManager.ExitScope(scope);
        }

        void Remove(int key)
        {
            var s = epochManager.EnterScope();
            var a = segment.CreateChunkAccessor();
            try
            {
                tree.Remove(key, out _, ref a);
            }
            finally
            {
                a.Dispose();
                epochManager.ExitScope(s);
            }
        }

        var faultingThread = Environment.CurrentManagedThreadId;
        OlcDescentTrace.OnRootCollapse = () =>
        {
            if (Environment.CurrentManagedThreadId == faultingThread)
            {
                throw new System.IO.IOException("injected fault under the root latch");
            }
        };
        var thrown = false;
        try
        {
            for (var k = top; k > 0 && !thrown; k--)
            {
                try
                {
                    Remove(k);
                }
                catch (System.IO.IOException)
                {
                    thrown = true;
                }
            }
        }
        finally
        {
            OlcDescentTrace.OnRootCollapse = null;
        }

        Assert.That(thrown, Is.True, "premise: the removals reached the root collapse");
        Assert.That(tree.IsFaulted, Is.True);
        var later = Task.Run(() => Remove(1));
        Assert.That(() => later.Wait(TimeSpan.FromSeconds(5)), Throws.InstanceOf<AggregateException>()
            .With.InnerException.TypeOf<InvalidOperationException>().And.InnerException.Message.Contains("faulted"),
            "the next write throws instead of spinning on the abandoned latch");
    }

    [TestCase(2, 2, 0, 0, 500)]
    [TestCase(4, 4, 0, 0, 500)]
    [TestCase(2, 2, 1, 0, 500)]
    [TestCase(2, 2, 2, 0, 500)]
    [CancelAfter(30_000)]
    public unsafe void DoubleMultiple_RandomInsertRemoveChurn_NeverWedgesAndLosesNothing(int inserters, int removers, int movers, int prefill, int durationMs)
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 400, sizeof(Index64Chunk));
        var tree = new DoubleMultipleBTree<PersistentStore>(segment);

        var pool = new ConcurrentQueue<Entry>();
        var errors = new ConcurrentBag<string>();
        var removed = new ConcurrentDictionary<int, byte>();
        var inserted = new ConcurrentDictionary<int, Entry>();
        int nextValue = 0;
        var stop = Stopwatch.StartNew();
        using var start = new ManualResetEventSlim(false);
        var tasks = new List<Task>();
        var maxLive = Math.Max(MaxLive, prefill + 5_000);

        // Prefill: a multi-level tree, so leaves have cousins (siblings under different parents) and separators live above the leaves' parents.
        if (prefill > 0)
        {
            var scope = epochManager.EnterScope();
            var accessor = segment.CreateChunkAccessor();
            try
            {
                var rand = new Random(42);
                for (var i = 0; i < prefill; i++)
                {
                    var value = Interlocked.Increment(ref nextValue);
                    var key = rand.NextDouble();
                    var e = new Entry(key, tree.Add(key, value, ref accessor), value);
                    inserted[value] = e;
                    pool.Enqueue(e);
                }
            }
            finally
            {
                accessor.Dispose();
                epochManager.ExitScope(scope);
            }
        }

        for (var t = 0; t < inserters; t++)
        {
            var seed = 1000 + t;
            tasks.Add(Task.Factory.StartNew(() =>
            {
                var rand = new Random(seed);
                start.Wait();
                while (stop.ElapsedMilliseconds < durationMs && errors.IsEmpty)
                {
                    // Bound the live population: this bare harness has no checkpointer, so an unbounded tree fills the cache with dirty pages and the run ends on
                    // page-cache back-pressure instead of on the tree. The race lives at small populations anyway (the tree empties and refills constantly).
                    if (pool.Count > maxLive)
                    {
                        Thread.SpinWait(50);
                        continue;
                    }

                    var scope = epochManager.EnterScope();
                    var accessor = segment.CreateChunkAccessor();
                    try
                    {
                        var value = Interlocked.Increment(ref nextValue);
                        var key = rand.NextDouble();
                        var elementId = tree.Add(key, value, ref accessor);
                        var e = new Entry(key, elementId, value);
                        inserted[value] = e;
                        pool.Enqueue(e);
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"insert: {ex.GetType().Name}: {ex.Message} @ {FirstFrame(ex)}");
                    }
                    finally
                    {
                        accessor.Dispose();
                        epochManager.ExitScope(scope);
                    }
                }
            }, TaskCreationOptions.LongRunning));
        }

        for (var t = 0; t < removers; t++)
        {
            tasks.Add(Task.Factory.StartNew(() =>
            {
                start.Wait();
                while (stop.ElapsedMilliseconds < durationMs && errors.IsEmpty)
                {
                    if (!pool.TryDequeue(out var e))
                    {
                        Thread.SpinWait(50);
                        continue;
                    }

                    var scope = epochManager.EnterScope();
                    var accessor = segment.CreateChunkAccessor();
                    try
                    {
                        if (!tree.RemoveValue(e.Key, e.ElementId, e.Value, ref accessor))
                        {
                            errors.Add($"remove: value {e.Value} under key {e.Key} not found");
                        }
                        else
                        {
                            removed[e.Value] = 0;
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"remove: {ex.GetType().Name}: {ex.Message} @ {FirstFrame(ex)}");
                    }
                    finally
                    {
                        accessor.Dispose();
                        epochManager.ExitScope(scope);
                    }
                }
            }, TaskCreationOptions.LongRunning));
        }

        // Movers: UltimateStress's role 9 — re-key a live entry (MoveValue) while inserts and removes churn the same tree. An entry is owned by whoever
        // dequeued it, so a move and a remove never target the same element at once.
        for (var t = 0; t < movers; t++)
        {
            var seed = 5000 + t;
            tasks.Add(Task.Factory.StartNew(() =>
            {
                var rand = new Random(seed);
                start.Wait();
                while (stop.ElapsedMilliseconds < durationMs && errors.IsEmpty)
                {
                    if (!pool.TryDequeue(out var e))
                    {
                        Thread.SpinWait(50);
                        continue;
                    }

                    var scope = epochManager.EnterScope();
                    var accessor = segment.CreateChunkAccessor();
                    try
                    {
                        var newKey = rand.NextDouble();
                        var newElementId = tree.MoveValue(e.Key, newKey, e.ElementId, e.Value, ref accessor, out _, out _);
                        if (newElementId < 0)
                        {
                            errors.Add($"move: value {e.Value} under key {e.Key} not found");
                        }
                        else
                        {
                            var moved = new Entry(newKey, newElementId, e.Value);
                            inserted[e.Value] = moved;
                            pool.Enqueue(moved);
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"move: {ex.GetType().Name}: {ex.Message} @ {ex.StackTrace?.Split((char)10)[0]?.Trim()}");
                    }
                    finally
                    {
                        accessor.Dispose();
                        epochManager.ExitScope(scope);
                    }
                }
            }, TaskCreationOptions.LongRunning));
        }

        stop.Restart();
        start.Set();
        Task.WaitAll(tasks.ToArray(), TestContext.CurrentContext.CancellationToken);

        Assert.That(errors, Is.Empty, $"{errors.Count} errors after {nextValue} inserts / {removed.Count} removes; first: "
            + string.Join(" | ", new List<string>(errors).GetRange(0, Math.Min(5, errors.Count))));

        // Census: every inserted-and-not-removed value is present exactly once under its key; nothing removed is still there. Inside an epoch scope, as
        // every reader of the tree: outside one, Debug builds fail CreateScanAccessor's assert — the Release gate does not see it.
        var seen = new HashSet<int>();
        var censusScope = epochManager.EnterScope();
        try
        {
            using (var en = tree.EnumerateRangeMultiple(double.MinValue, double.MaxValue))
            {
                while (en.MoveNextKey())
                {
                    do
                    {
                        var values = en.CurrentValues;
                        for (var i = 0; i < values.Length; i++)
                        {
                            Assert.That(seen.Add(values[i]), Is.True, $"value {values[i]} listed twice");
                            Assert.That(removed.ContainsKey(values[i]), Is.False, $"removed value {values[i]} still listed");
                        }
                    }
                    while (en.NextChunk());
                }
            }
        }
        finally
        {
            epochManager.ExitScope(censusScope);
        }

        Assert.That(seen.Count, Is.EqualTo(inserted.Count - removed.Count), "live values in the tree");
    }

    /// <summary>
    /// IXS-09: a range cursor's position is the key's index in the leaf it then reads, taken under that read's version. A removal below the key landing
    /// between the descent and the read shifts the key left, and the descent's own index then names the entry above it — past the bound, so a single-key
    /// read answered empty for a key the tree holds (#1235).
    /// </summary>
    [Test]
    [VerifiesRule("IXS-09")]
    public unsafe void ARemovalBetweenTheSeekAndTheLeafRead_LosesNoKey()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 200, sizeof(Index32Chunk));
        var tree = new IntSingleBTree<PersistentStore>(segment);
        var scope = epochManager.EnterScope();
        var accessor = segment.CreateChunkAccessor();
        try
        {
            for (var k = 0; k < 12; k++)
            {
                tree.Add(k, 1_000 + k, ref accessor);
            }

            var fired = false;
            BTree<int, PersistentStore>.RangeEnumerator.SeekProbe = () =>
            {
                if (!fired)
                {
                    fired = true;
                    tree.Remove(9, out _, ref accessor);
                }
            };
            try
            {
                var found = new List<int>();
                using (var e = tree.EnumerateRange(10, 10))
                {
                    while (e.MoveNext())
                    {
                        found.Add(e.Current.Key);
                    }
                }

                Assert.That(fired, Is.True, "premise: the removal ran between the descent and the leaf read");
                Assert.That(found, Is.EqualTo(new[] { 10 }), "a read of key 10 after a removal below it shifted the leaf");
            }
            finally
            {
                BTree<int, PersistentStore>.RangeEnumerator.SeekProbe = null;
            }
        }
        finally
        {
            accessor.Dispose();
            epochManager.ExitScope(scope);
        }
    }

    /// <summary>
    /// IXW-11: a move that takes the tree's last key away leaves no duplicate behind. The append fast path compares a new key with a cached last key, which
    /// a removal cleared and a move did not: a unique key moved off the end then read as a duplicate to the next insert of it, and the insert threw —
    /// past a commit's append, the engine stopped (#1263).
    /// </summary>
    [Test]
    [VerifiesRule("IXW-11")]
    public unsafe void AKeyMovedOffTheEndOfAUniqueTree_CanBeInsertedAgain()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 200, sizeof(Index32Chunk));
        var tree = new IntSingleBTree<PersistentStore>(segment);
        var scope = epochManager.EnterScope();
        var accessor = segment.CreateChunkAccessor();
        try
        {
            tree.Add(10, 100, ref accessor);
            tree.Add(60, 600, ref accessor);
            Assert.That(tree.Move(60, 30, 600, ref accessor), Is.True, "premise: the last key moves");
            Assert.DoesNotThrow(() => tree.Add(60, 601, ref accessor), "a key no entry holds was refused as a duplicate");
            Assert.Throws<UniqueConstraintViolationException>(() => tree.Add(60, 602, ref accessor), "a key an entry holds was accepted");
            var found = tree.TryGet(60, ref accessor);
            Assert.That(found.IsSuccess && found.Value == 601, Is.True, "the reinserted key does not hold its value");
        }
        finally
        {
            accessor.Dispose();
            epochManager.ExitScope(scope);
        }
    }

    /// <summary>
    /// IXW-11, multi-value: a key whose only value moves off the end of the tree, then appended to again, completes. On the stale cache the append re-checked
    /// the leaf, restarted, and met the same cache on every pass.
    /// </summary>
    [Test]
    [VerifiesRule("IXW-11")]
    public unsafe void AKeyMovedOffTheEndOfAMultiValueTree_CanBeAppendedAgain()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 200, sizeof(Index32Chunk));
        var tree = new IntMultipleBTree<PersistentStore>(segment);
        var scope = epochManager.EnterScope();
        var accessor = segment.CreateChunkAccessor();
        try
        {
            tree.Add(10, 100, ref accessor);
            var element = tree.Add(60, 600, ref accessor);
            tree.MoveValue(60, 30, element, 600, ref accessor, out _, out _);
            Assert.DoesNotThrow(() => tree.Add(60, 601, ref accessor), "an append to a key moved off the end did not complete");
            var values = new List<int>();
            using (var e = tree.EnumerateRangeMultiple(60, 60))
            {
                while (e.MoveNextKey())
                {
                    do
                    {
                        foreach (var v in e.CurrentValues)
                        {
                            values.Add(v);
                        }
                    } while (e.NextChunk());
                }
            }

            Assert.That(values, Is.EqualTo(new[] { 601 }), "key 60 does not hold exactly its new value");
        }
        finally
        {
            accessor.Dispose();
            epochManager.ExitScope(scope);
        }
    }
}
