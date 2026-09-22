using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// <see cref="SpatialQueryAccessorCache"/>: the per-thread warm page window the cluster spatial query borrows. Reuse across queries is the point. The
/// failures are two live queries sharing one window, a stale return releasing a window another query holds, and pins that nothing can reclaim.
/// </summary>
[TestFixture]
class SpatialQueryAccessorCacheTests : TestBase<SpatialQueryAccessorCacheTests>
{
    private static DatabaseEngine SetupEngine(IServiceProvider services, float cellSize = 100f, float worldMax = 1_000f, int promoteThreshold = 0)
    {
        var dbe = services.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClCohPos>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(worldMax, worldMax), cellSize));
        if (promoteThreshold > 0)
        {
            // Count-only promotion: these clusters are scattered over the cell on purpose, the shape the tightness gate refuses.
            dbe.ClusterCellTreePromoteThreshold = promoteThreshold;
            dbe.ClusterCellTreePromoteTightness = 1f;
        }

        dbe.InitializeArchetypes();
        return dbe;
    }

    private static void Spawn(DatabaseEngine dbe, int count, int seed, float extent = 1_000f)
    {
        var rng = new Random(seed);
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < count; i++)
            {
                float x = 5f + ((float)rng.NextDouble() * (extent - 10f));
                float y = 5f + ((float)rng.NextDouble() * (extent - 10f));
                var pos = new ClCohPos { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Mass = 1f };
                tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(in pos));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);
    }

    private static ArchetypeClusterState StateOf(DatabaseEngine dbe) => dbe._archetypeStates[Archetype<ClCohUnit>.Metadata.ArchetypeId].ClusterState;

    private static ChunkBasedSegment<PersistentStore> SegmentOf(DatabaseEngine dbe) => StateOf(dbe).ClusterSegment;

    private static int CountInBox(DatabaseEngine dbe, float minX, float minY, float maxX, float maxY)
    {
        var box = new AABB2F { MinX = minX, MinY = minY, MaxX = maxX, MaxY = maxY };
        var e = dbe.ClusterSpatialQuery<ClCohUnit>().AABB(in box);
        try
        {
            return e.Count();
        }
        finally
        {
            e.Dispose();
        }
    }

    /// <summary>
    /// The window this thread's cache holds over <paramref name="segment"/>: a rent handed straight back, so the query that follows reuses it.
    /// </summary>
    private static SpatialQueryAccessorCache.Entry PeekWindow(ChunkBasedSegment<PersistentStore> segment)
    {
        var window = SpatialQueryAccessorCache.Instance.Rent(segment, out var token);
        SpatialQueryAccessorCache.Return(window, token);
        return window;
    }

    [Test]
    public void ConsecutiveRents_OnOneThread_ReuseOneWindow()
    {
        using var dbe = SetupEngine(ServiceProvider);
        Spawn(dbe, 200, seed: 1);
        var segment = SegmentOf(dbe);

        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var first = PeekWindow(segment);
        var second = PeekWindow(segment);

        Assert.That(second, Is.SameAs(first), "a free window over the same segment must be reused, or the cache keeps nothing warm");
    }

    [Test]
    [VerifiesRule("SQ-05")]
    public void ARentedWindow_IsNeverHandedToASecondRent()
    {
        using var dbe = SetupEngine(ServiceProvider);
        Spawn(dbe, 200, seed: 2);
        var segment = SegmentOf(dbe);
        var cache = SpatialQueryAccessorCache.Instance;

        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var a = cache.Rent(segment, out var ta);
        var b = cache.Rent(segment, out var tb);
        try
        {
            Assert.That(b, Is.Not.SameAs(a), "two live queries on one thread must never share a page window");
        }
        finally
        {
            SpatialQueryAccessorCache.Return(b, tb);
            SpatialQueryAccessorCache.Return(a, ta);
        }
    }

    [Test]
    [VerifiesRule("SQ-05")]
    public void AStaleReturn_DoesNotReleaseAWindowRentedSince()
    {
        using var dbe = SetupEngine(ServiceProvider);
        Spawn(dbe, 200, seed: 3);
        var segment = SegmentOf(dbe);
        var cache = SpatialQueryAccessorCache.Instance;

        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var window = cache.Rent(segment, out var stale);
        SpatialQueryAccessorCache.Return(window, stale);
        var again = cache.Rent(segment, out var current);
        Assert.That(again, Is.SameAs(window), "precondition: the freed window is the one the next rent takes");

        // What a copy of the first enumerator does when it is disposed late: GetEnumerator() returns a copy, and both copies carry the rent.
        SpatialQueryAccessorCache.Return(window, stale);

        var third = cache.Rent(segment, out var t3);
        try
        {
            Assert.That(third, Is.Not.SameAs(window), "a stale return released a window that another query still holds");
        }
        finally
        {
            SpatialQueryAccessorCache.Return(third, t3);
            SpatialQueryAccessorCache.Return(window, current);
        }
    }

    [Test]
    [VerifiesRule("SQ-05")]
    public void RentsPastTrimAbove_AreServedByOverflowWindows_ThatAreNotKept()
    {
        using var dbe = SetupEngine(ServiceProvider);
        Spawn(dbe, 100, seed: 4);
        var segment = SegmentOf(dbe);
        var cache = SpatialQueryAccessorCache.Instance;

        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var rents = new List<(SpatialQueryAccessorCache.Entry Entry, int Token)>();
        for (int i = 0; i < SpatialQueryAccessorCache.TrimAbove + 3; i++)
        {
            var entry = cache.Rent(segment, out var token);
            rents.Add((entry, token));
        }

        try
        {
            Assert.Multiple(() =>
            {
                Assert.That(rents.Select(r => r.Entry).Distinct().Count(), Is.EqualTo(rents.Count), "every live rent must hold its own window");
                Assert.That(cache.EntryCount, Is.LessThanOrEqualTo(SpatialQueryAccessorCache.TrimAbove), "the pool grew past TrimAbove");
                Assert.That(rents.Count(r => !r.Entry.Pooled), Is.GreaterThanOrEqualTo(3), "the rents past TrimAbove must be overflow windows");
            });
        }
        finally
        {
            foreach (var (entry, token) in rents)
            {
                SpatialQueryAccessorCache.Return(entry, token);
            }
        }

        Assert.That(cache.EntryCount, Is.LessThanOrEqualTo(SpatialQueryAccessorCache.TrimAbove), "returned overflow windows must not join the pool");
    }

    [Test]
    [VerifiesRule("SQ-05")]
    public void NestedQueries_OnOneSegment_AnswerAsTheyDoAlone()
    {
        using var dbe = SetupEngine(ServiceProvider);
        Spawn(dbe, 2_000, seed: 5);
        AssertNestedQueriesAnswerAsAlone(dbe, outerMax: 1_000f);
    }

    [Test]
    [VerifiesRule("SQ-05")]
    public void NestedQueries_OverAPromotedCell_AnswerAsTheyDoAlone()
    {
        using var dbe = SetupEngine(ServiceProvider, cellSize: 1_000f, worldMax: 4_000f, promoteThreshold: 24);
        Spawn(dbe, 3_000, seed: 6);
        Assert.That(StateOf(dbe).PromotedCellCount, Is.GreaterThan(0), "precondition: the cell must promote, so both queries pop tree hits from their windows");
        AssertNestedQueriesAnswerAsAlone(dbe, outerMax: 1_000f);
    }

    /// <summary>
    /// An outer MoveNext query over everything, with an inner Count query run every 97 hits: both must answer exactly what they answer alone.
    /// </summary>
    private static void AssertNestedQueriesAnswerAsAlone(DatabaseEngine dbe, float outerMax)
    {
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var innerAlone = CountInBox(dbe, 300f, 300f, 600f, 600f);
        var all = new AABB2F { MinX = 0f, MinY = 0f, MaxX = outerMax, MaxY = outerMax };

        var outerAlone = new List<long>();
        var e = dbe.ClusterSpatialQuery<ClCohUnit>().AABB(in all);
        try
        {
            while (e.MoveNext())
            {
                outerAlone.Add(unchecked((long)e.Current.Entity.RawValue));
            }
        }
        finally
        {
            e.Dispose();
        }

        var outer = new List<long>();
        var innerRuns = new List<int>();
        var o = dbe.ClusterSpatialQuery<ClCohUnit>().AABB(in all);
        try
        {
            while (o.MoveNext())
            {
                outer.Add(unchecked((long)o.Current.Entity.RawValue));
                if (outer.Count % 97 == 0)
                {
                    // The inner query walks the same segment's pages while the outer one holds a cluster open.
                    innerRuns.Add(CountInBox(dbe, 300f, 300f, 600f, 600f));
                }
            }
        }
        finally
        {
            o.Dispose();
        }

        Assert.Multiple(() =>
        {
            Assert.That(innerAlone, Is.GreaterThan(0), "precondition: the inner query must hit something");
            Assert.That(innerRuns, Is.Not.Empty, "precondition: the outer query must run long enough to nest");
            Assert.That(outer, Is.EqualTo(outerAlone), "the outer query answered differently with queries nested inside it");
            Assert.That(innerRuns, Is.All.EqualTo(innerAlone), "a nested query answered differently from the same query run alone");
        });
    }

    [Test]
    public void TreeHits_GrowPastTheirFirst64_AndTheQueryStillAnswersEverything()
    {
        using var dbe = SetupEngine(ServiceProvider, cellSize: 1_000f, worldMax: 4_000f, promoteThreshold: 24);
        Spawn(dbe, 5_000, seed: 7);
        var cs = StateOf(dbe);
        Assert.That(cs.PromotedCellCount, Is.GreaterThan(0), "precondition: the cell must promote");

        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        Assert.That(CountInBox(dbe, 0f, 0f, 1_000f, 1_000f), Is.EqualTo(5_000), "every entity sits in the promoted cell, and the box covers it");
        Assert.That(PeekWindow(cs.ClusterSegment).TreeHits.Length, Is.GreaterThan(64),
            "precondition: the promoted half must have yielded more than 64 cluster ids, so the buffer grew mid-collection");
    }

    [Test]
    public void Release_UnpinsFreeWindows_AndTheNextQueryRefillsTheSameEntryWithoutAllocating()
    {
        using var dbe = SetupEngine(ServiceProvider);
        Spawn(dbe, 400, seed: 8);
        var segment = SegmentOf(dbe);

        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        Assert.That(CountInBox(dbe, 0f, 0f, 1_000f, 1_000f), Is.EqualTo(400));
        var window = PeekWindow(segment);
        Assert.That(window.Segment, Is.SameAs(segment), "precondition: the query left a warm window over its segment");
        int entries = SpatialQueryAccessorCache.Instance.EntryCount;

        SpatialQueryAccessorCache.Release(dbe.MMF);
        Assert.That(window.Segment, Is.Null, "a free window survived its engine's release, so back-pressure could not have reclaimed its pages");

        // A new entry is an object and a 64-int array: exactly what a warm query path must not hand the GC.
        long before = GC.GetAllocatedBytesForCurrentThread();
        int count = CountInBox(dbe, 0f, 0f, 1_000f, 1_000f);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Multiple(() =>
        {
            Assert.That(count, Is.EqualTo(400), "the query after a release must re-warm and answer");
            Assert.That(PeekWindow(segment), Is.SameAs(window), "the emptied entry must be refilled, not replaced");
            Assert.That(SpatialQueryAccessorCache.Instance.EntryCount, Is.EqualTo(entries));
            Assert.That(allocated, Is.Zero, "re-warming a released window allocated");
        });
    }

    /// <summary>
    /// A release reaches only the windows over the releasing engine's pages. In a process running several engines — a server with shards, or this test suite
    /// — one engine's dispose or back-pressure must not empty the others' windows and make their next query allocate a new entry.
    /// </summary>
    [Test]
    public void ReleasingAnotherPageCache_LeavesThisEnginesWindowWarm()
    {
        // The call another engine's dispose makes, on a page cache that is not this engine's.
        ManagedPagedMMF otherCache;
        using (var first = ServiceProvider.CreateScope())
        {
            var other = SetupEngine(first.ServiceProvider);
            otherCache = other.MMF;
            other.Dispose();
        }

        ServiceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        using var scope = ServiceProvider.CreateScope();
        using var dbe = SetupEngine(scope.ServiceProvider);
        Spawn(dbe, 300, seed: 12);
        var segment = SegmentOf(dbe);

        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        Assert.That(CountInBox(dbe, 0f, 0f, 1_000f, 1_000f), Is.EqualTo(300));
        var window = PeekWindow(segment);
        Assert.That(window.Segment, Is.SameAs(segment), "precondition: the query left a warm window over its segment");

        SpatialQueryAccessorCache.Release(otherCache);

        Assert.That(window.Segment, Is.SameAs(segment), "another page cache's release emptied this engine's window");
    }

    [Test]
    public void DisposingTheEngine_ReleasesAnotherThreadsWindow()
    {
        var dbe = SetupEngine(ServiceProvider);
        Spawn(dbe, 300, seed: 9);
        var segment = SegmentOf(dbe);
        SpatialQueryAccessorCache.Entry otherThreadsWindow = null;
        Exception failure = null;
        using var queried = new ManualResetEventSlim();
        using var released = new ManualResetEventSlim();

        var thread = new Thread(() =>
        {
            try
            {
                using (var epoch = EpochGuard.Enter(dbe.EpochManager))
                {
                    CountInBox(dbe, 0f, 0f, 1_000f, 1_000f);
                    otherThreadsWindow = PeekWindow(segment);
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            queried.Set();
            // Stay alive while the engine is disposed: a DEAD thread's window is the finalizer's business, a live one's is dispose's.
            released.Wait();
        });
        thread.Start();
        queried.Wait();

        try
        {
            Assert.That(failure, Is.Null);
            Assert.That(otherThreadsWindow.Segment, Is.SameAs(segment), "precondition: the other thread holds a warm window over the segment");
            dbe.Dispose();
            Assert.That(otherThreadsWindow.Segment, Is.Null, "the engine was disposed with another thread's window still pinning its pages");
        }
        finally
        {
            released.Set();
            thread.Join();
        }
    }

    [Test]
    public void AQueryOnANewEngine_DoesNotReuseTheOldEnginesWindow()
    {
        SpatialQueryAccessorCache.Entry oldWindow;
        using (var first = ServiceProvider.CreateScope())
        {
            var dbe1 = SetupEngine(first.ServiceProvider);
            Spawn(dbe1, 300, seed: 10);
            using (var epoch1 = EpochGuard.Enter(dbe1.EpochManager))
            {
                Assert.That(CountInBox(dbe1, 0f, 0f, 1_000f, 1_000f), Is.EqualTo(300));
                oldWindow = PeekWindow(SegmentOf(dbe1));
            }

            dbe1.Dispose();
        }

        Assert.That(oldWindow.Segment, Is.Null, "disposing the engine must release the window over its segment");

        ServiceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        using var second = ServiceProvider.CreateScope();
        using var dbe2 = SetupEngine(second.ServiceProvider);
        Spawn(dbe2, 150, seed: 11);

        using var epoch2 = EpochGuard.Enter(dbe2.EpochManager);
        Assert.That(CountInBox(dbe2, 0f, 0f, 1_000f, 1_000f), Is.EqualTo(150), "a window left over from a disposed engine answered for the new one");
    }

    [Test]
    public void QueriesOnManyThreads_EachAnswerAsOneThreadDoes()
    {
        using var dbe = SetupEngine(ServiceProvider);
        Spawn(dbe, 3_000, seed: 12);

        var rng = new Random(13);
        var boxes = new (float minX, float minY, float maxX, float maxY)[48];
        var expected = new int[boxes.Length];
        using (var epoch = EpochGuard.Enter(dbe.EpochManager))
        {
            for (int i = 0; i < boxes.Length; i++)
            {
                float x = (float)rng.NextDouble() * 800f;
                float y = (float)rng.NextDouble() * 800f;
                boxes[i] = (x, y, x + 50f + ((float)rng.NextDouble() * 150f), y + 50f + ((float)rng.NextDouble() * 150f));
                expected[i] = CountInBox(dbe, boxes[i].minX, boxes[i].minY, boxes[i].maxX, boxes[i].maxY);
            }
        }

        const int ThreadCount = 8;
        var mismatches = new ConcurrentBag<string>();
        using var start = new Barrier(ThreadCount);
        var threads = new Thread[ThreadCount];
        for (int w = 0; w < ThreadCount; w++)
        {
            int worker = w;
            threads[w] = new Thread(() =>
            {
                try
                {
                    // All eight query at once, each through its own thread's cache, rather than one after another on a shared pool thread.
                    start.SignalAndWait();
                    using var epoch = EpochGuard.Enter(dbe.EpochManager);
                    for (int rep = 0; rep < 10; rep++)
                    {
                        for (int i = 0; i < boxes.Length; i++)
                        {
                            var n = CountInBox(dbe, boxes[i].minX, boxes[i].minY, boxes[i].maxX, boxes[i].maxY);
                            if (n != expected[i])
                            {
                                mismatches.Add($"worker {worker} box {i}: {n} != {expected[i]}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    mismatches.Add($"worker {worker}: {ex}");
                }
            });
            threads[w].Start();
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }

        Assert.That(mismatches, Is.Empty);
    }
}
