using System;
using System.Threading;
using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// ZoneMapArray under concurrent growth (review M5). The class publishes three arrays plus a capacity that must agree with each other, and it is written from
// every worker committing into the same archetype — TyphonRuntime.ExecuteChunkWithTransaction runs per-chunk transactions on all workers with no
// archetype-level lock, so Transaction.cs:2089's Widen genuinely races.
//
// The failure is NOT only an arm64 store-ordering argument; it is reachable on x64, which is what makes it testable. Array.Resize(ref _mins, …) RE-READS the
// field, so with the pre-fix grow:
//
//     T2: resizes all three to 201, publishes _capacity = 201
//     T1: already past the guard with newCap = 101, resizes _mins — the array T2 just published — back DOWN to 101
//
// leaves _mins.Length = 101 against _maxs.Length = 201 and _capacity = 201. The next write to _mins[150] throws, and a reader that got past the capacity
// check sees a mins/maxs pair from two different generations.
//
// These tests drive ZoneMapArray directly rather than through the engine: the race is in the class, and going through spawn/commit would add the tick fence,
// which self-heals the symptom and would hide it.
// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Concurrent growth of <see cref="ZoneMapArray"/> must not tear its arrays apart or lose a widen, per the class contract "false negatives impossible".
/// </summary>
/// <remarks>
/// <see cref="NonParallelizableAttribute"/>: these tests need real thread overlap to interleave a grow with a write, and competing with the rest of the suite
/// for cores makes that less likely, not more.
/// </remarks>
[NonParallelizable]
unsafe class ZoneMapConcurrentGrowthTests
{
    private const int ThreadCount = 6;
    private const int ClustersPerThread = 400;

    /// <summary>Widen takes the field by pointer, so a value has to live somewhere addressable.</summary>
    private static void WidenLong(ZoneMapArray map, int clusterChunkId, long value)
    {
        var v = value;
        map.Widen(clusterChunkId, (byte*)&v);
    }

    /// <summary>
    /// Interleaved ids across threads, so the growth points collide rather than each thread growing its own disjoint tail. Thread t owns
    /// {t, t + ThreadCount, t + 2*ThreadCount, …} — ascending within a thread, so every thread is pushing the capacity up at roughly the same moment.
    /// </summary>
    private static int IdFor(int thread, int step) => thread + step * ThreadCount;

    private static void RunStorm(ZoneMapArray map, Action<int, int> perStep, Action readerBody = null)
    {
        var errors = new Exception[ThreadCount + 1];
        var start = new Barrier(ThreadCount + (readerBody != null ? 1 : 0));
        var threads = new Thread[ThreadCount + (readerBody != null ? 1 : 0)];
        var done = 0;

        for (var t = 0; t < ThreadCount; t++)
        {
            var thread = t;
            threads[t] = new Thread(() =>
            {
                try
                {
                    start.SignalAndWait();
                    for (var step = 0; step < ClustersPerThread; step++)
                    {
                        perStep(thread, step);
                    }
                }
                catch (Exception ex)
                {
                    errors[thread] = ex;
                }
                finally
                {
                    Interlocked.Increment(ref done);
                }
            });
        }

        if (readerBody != null)
        {
            threads[ThreadCount] = new Thread(() =>
            {
                try
                {
                    start.SignalAndWait();
                    while (Volatile.Read(ref done) < ThreadCount)
                    {
                        readerBody();
                    }
                }
                catch (Exception ex)
                {
                    errors[ThreadCount] = ex;
                }
            });
        }

        foreach (var th in threads)
        {
            th?.Start();
        }

        foreach (var th in threads)
        {
            th?.Join();
        }

        for (var i = 0; i < errors.Length; i++)
        {
            if (errors[i] != null)
            {
                Assert.Fail($"thread {i} threw {errors[i].GetType().Name}: {errors[i].Message}");
            }
        }
    }

    /// <summary>
    /// The mutation guard. Against the pre-fix <c>EnsureCapacity</c> this throws — two growers leave the three arrays at different lengths and a subsequent
    /// element write runs off the short one.
    /// </summary>
    [Test]
    public void ConcurrentGrowth_DoesNotTearTheArrays()
    {
        var map = new ZoneMapArray(16, sizeof(long), isFloat: false, isDouble: false);
        RunStorm(map, (thread, step) => WidenLong(map, IdFor(thread, step), IdFor(thread, step)));
    }

    /// <summary>
    /// The contract, not just the crash: <c>ZoneMapArray</c> promises false negatives are impossible, so every value that was widened in must still be inside
    /// its cluster's bounds afterwards. A resize whose result is discarded loses the widen silently — no exception, just a cluster the planner will prune out
    /// of a query that should have matched it.
    /// </summary>
    [Test]
    public void ConcurrentGrowth_KeepsEveryWrittenValueInBounds()
    {
        var map = new ZoneMapArray(16, sizeof(long), isFloat: false, isDouble: false);
        RunStorm(map, (thread, step) => WidenLong(map, IdFor(thread, step), IdFor(thread, step)));

        for (var t = 0; t < ThreadCount; t++)
        {
            for (var step = 0; step < ClustersPerThread; step++)
            {
                var id = IdFor(t, step);
                Assert.That(map.MayContain(id, id, id), Is.True, $"cluster {id} was widened to include {id} and must still admit it");
            }
        }
    }

    /// <summary>
    /// Readers run against a map that is growing under them. <c>MayContain</c> bounds-checks against the capacity and then indexes three arrays; if it can see
    /// a capacity from one generation and an array from another, this is where it faults.
    /// </summary>
    [Test]
    public void ConcurrentGrowth_WithConcurrentReaders_NeitherSideFaults()
    {
        var map = new ZoneMapArray(16, sizeof(long), isFloat: false, isDouble: false);
        var sink = 0;

        RunStorm(
            map,
            (thread, step) => WidenLong(map, IdFor(thread, step), IdFor(thread, step)),
            () =>
            {
                for (var id = 0; id < ThreadCount * ClustersPerThread; id += 17)
                {
                    if (map.MayContain(id, id, id))
                    {
                        sink++;
                    }
                }
            });

        Assert.That(sink, Is.GreaterThan(0), "premise: the reader thread actually ran");
    }
}
