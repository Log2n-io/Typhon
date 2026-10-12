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
                        errors.Add($"insert: {ex.GetType().Name}: {ex.Message} @ {ex.StackTrace?.Split((char)10)[0]?.Trim()} <- {(ex.StackTrace?.Split((char)10).Length > 1 ? ex.StackTrace.Split((char)10)[1].Trim() : "")}");
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
                        errors.Add($"remove: {ex.GetType().Name}: {ex.Message} @ {ex.StackTrace?.Split((char)10)[0]?.Trim()} <- {(ex.StackTrace?.Split((char)10).Length > 1 ? ex.StackTrace.Split((char)10)[1].Trim() : "")}");
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

        // Census: every inserted-and-not-removed value is present exactly once under its key; nothing removed is still there.
        var seen = new HashSet<int>();
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

        Assert.That(seen.Count, Is.EqualTo(inserted.Count - removed.Count), "live values in the tree");
    }
}
