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

    // ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // #926 — the batched write path the Migrate phase uses.
    // ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>WidenInto</c> must refuse an index past the store the batch pinned, rather than writing through it.
    /// </summary>
    /// <remarks>
    /// A batch holds the grow latch shared for its whole run and hands the caller ONE <see cref="ZoneMapArray.Store"/> generation. An index past that
    /// generation is not a slow path to grow through — the caller cannot grow (it holds shared access, and taking exclusive would deadlock against its own
    /// wait for the shared count to drain), and writing anyway is either out of range or a write into a generation a concurrent grow has abandoned. The
    /// second is the lost widen this class promises cannot happen, so the refusal is the contract and the bool is how the caller learns of it.
    /// </remarks>
    [Test]
    [VerifiesRule("MD-03")]
    public void WidenInto_RefusesAnIndexPastTheBatchStore_RatherThanWritingIntoAnAbandonedGeneration()
    {
        var map = new ZoneMapArray(16, sizeof(long), isFloat: false, isDouble: false);

        var inside = 7L;
        var outside = 999L;
        var store = map.BeginBatchAtCapacity();
        bool acceptedInside;
        bool acceptedOutside;
        try
        {
            acceptedInside = map.WidenInto(store, 7, (byte*)&inside);
            acceptedOutside = map.WidenInto(store, 999, (byte*)&outside);
        }
        finally
        {
            map.EndBatch();
        }

        Assert.Multiple(() =>
        {
            Assert.That(acceptedInside, Is.True, "7 is inside the 16-cluster store the batch pinned");
            Assert.That(acceptedOutside, Is.False, "999 is past it, and the refusal is what stops a write into an abandoned generation");
            Assert.That(map.TryGetBounds(7, out var min, out var max), Is.True);
            Assert.That(min, Is.EqualTo(7L));
            Assert.That(max, Is.EqualTo(7L));
        });
    }

    /// <summary>
    /// The refusal must hold for an index the MAP covers but the pinned batch does not — the only case that can actually lose a widen.
    /// </summary>
    /// <remarks>
    /// <b>This is the load-bearing half, and the obvious version of it proves nothing.</b> Asserting <c>TryGetBounds</c> is false for an index past the
    /// map's own capacity is tautological: it short-circuits on the capacity check whatever <c>WidenInto</c> did, so the assertion passes even against a
    /// <c>WidenInto</c> that wrote out of bounds — which would have thrown — or one that silently did nothing correct. The case with teeth is an index the
    /// map has SINCE grown to cover while a batch still pins the older, smaller generation: a write through the pinned store would land in an array nothing
    /// reads again, and <c>TryGetBounds</c> — which reads the CURRENT generation — would report it absent. That is the lost widen, and it is observable.
    /// </remarks>
    [Test]
    [VerifiesRule("MD-03")]
    public void WidenInto_RefusesAnIndexTheMapHasGrownToCover_WhileTheBatchStillPinsTheOlderGeneration()
    {
        var map = new ZoneMapArray(16, sizeof(long), isFloat: false, isDouble: false);

        // Pin the small generation FIRST, then grow the map behind it from another thread — a grower needs exclusive access, so it cannot run while this
        // batch is held; the batch is released, the grow lands, and the STALE store reference is what the write is then attempted through.
        var pinned = map.BeginBatchAtCapacity();
        var pinnedCapacity = pinned.Capacity;
        map.EndBatch();

        map.EnsureCapacity(512);

        var value = 4242L;
        var accepted = map.WidenInto(pinned, 300, (byte*)&value);

        Assert.Multiple(() =>
        {
            Assert.That(pinnedCapacity, Is.EqualTo(16), "PRECONDITION: the pinned generation is the small one");
            Assert.That(map.MayContain(300, long.MinValue, long.MaxValue), Is.True, "PRECONDITION: the map itself now covers 300");
            Assert.That(accepted, Is.False, "300 is inside the map but outside the pinned generation — writing there is the lost widen");
            Assert.That(map.TryGetBounds(300, out _, out _), Is.False,
                "and nothing may have been recorded: a bound written through the abandoned generation would be invisible to every reader");
        });
    }

    /// <summary>
    /// Growth must be refused while this thread holds a batch, and allowed when it does not — the two halves are different conditions.
    /// </summary>
    /// <remarks>
    /// <para><b>The refusal is a deadlock guard before it is a correctness guard.</b> <c>Grow</c> takes the latch exclusively, which waits for the shared
    /// count to reach zero; the shared counter is not per-thread, so a thread holding a batch waits for itself, under an unbounded <c>WaitContext.Null</c>.
    /// That is a permanent hang, not a slow path.</para>
    /// <para><b>The second half is what stops the guard being written too broadly.</b> A Migrate slice that holds no batch may grow perfectly safely — it
    /// exits shared first and the exclusive acquire excludes every sibling's shared window, which is how the engine worked before batching and how the
    /// batching-off comparison arm still has to work. A guard keyed on "in a Migrate slice" rather than "holds a batch" breaks that arm.</para>
    /// </remarks>
    [Test]
    [VerifiesRule("MD-03")]
    public void Grow_IsRefusedWhileThisThreadHoldsABatch_ButAllowedInAMigrateSliceThatHoldsNone()
    {
        var map = new ZoneMapArray(16, sizeof(long), isFloat: false, isDouble: false);
        ArchetypeClusterState.EnterMigrateSlice();
        try
        {
            // No batch open: growth from a Migrate slice is sound and must stay available.
            Assert.DoesNotThrow(() => map.EnsureCapacity(128), "a Migrate slice holding no batch may grow — that is the pre-batching path");

            map.BeginBatchAtCapacity();
            try
            {
                Assert.Throws<InvalidOperationException>(() => map.EnsureCapacity(4096),
                    "growing while holding a batch would wait forever on this thread's own shared count");
            }
            finally
            {
                map.EndBatch();
            }
        }
        finally
        {
            ArchetypeClusterState.ExitMigrateSlice();
        }
    }

    /// <summary>
    /// <c>BeginBatchAtCapacity</c> must not grow, whatever the map's capacity is — that is the whole difference from <c>BeginBatch</c>.
    /// </summary>
    /// <remarks>
    /// The Migrate phase opens its batches from worker threads that run concurrently with siblings holding their own batches on the same field. Growing at
    /// open would replace the store under them, which is the abandonment <c>ZoneMapArray.Grow</c> refuses outright inside a Migrate slice. Pinned here so the
    /// two forms cannot be swapped back by someone reading them as synonyms.
    /// </remarks>
    [Test]
    public void BeginBatchAtCapacity_DoesNotGrow_UnlikeBeginBatch()
    {
        var map = new ZoneMapArray(16, sizeof(long), isFloat: false, isDouble: false);

        var store = map.BeginBatchAtCapacity();
        var capacityAtOpen = store.Capacity;
        map.EndBatch();

        var grown = map.BeginBatch(200);
        var capacityAfterBeginBatch = grown.Capacity;
        map.EndBatch();

        Assert.Multiple(() =>
        {
            Assert.That(capacityAtOpen, Is.EqualTo(16), "the grow-free form opens over exactly what the map already covers");
            Assert.That(capacityAfterBeginBatch, Is.GreaterThanOrEqualTo(200), "PRECONDITION: BeginBatch does grow, so the two really are different");
        });
    }
}
