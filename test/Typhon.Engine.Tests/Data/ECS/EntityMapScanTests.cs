using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Threading;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// #1351: a walk of the entity map — <c>ForEachEntry</c>, <c>CountEntries</c>, <c>AnyEntry</c>, what a broad query runs on — visits every entry present for
/// its whole duration exactly once while the map splits under it. A walk used to stop at the bucket count it read at its start, so a split of a bucket it had
/// not read yet moved entries out of its range: a broad query missed live entities whenever the archetype grew meanwhile.
/// </summary>
/// <remarks>
/// The staged tests split the map from the walk's own callback, between two buckets for <c>ForEachEntry</c> and in the middle of a bucket's read for the
/// optimistic walks, so where the split lands relative to the walk is chosen, not hoped for.
/// </remarks>
[VerifiesRule("EMAP-02")]
unsafe class EntityMapScanTests
{
    private const int ValueSize = 23;
    private const int N0 = 256;
    private const int Keys = 1_000;

    private IServiceProvider _serviceProvider;
    private static string CurrentDatabaseName => $"T_EMS_{TestSeed.StableHash(TestContext.CurrentContext.Test.Name):X8}";

    [SetUp]
    public void Setup()
    {
        var serviceCollection = new ServiceCollection();
        serviceCollection
            .AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddScopedManagedPagedMemoryMappedFile(options =>
            {
                options.DatabaseName = CurrentDatabaseName;
                options.DatabaseCacheSize = 4096UL * PagedMMF.PageSize;
            });

        _serviceProvider = serviceCollection.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
    }

    [TearDown]
    public void TearDown() => (_serviceProvider as IDisposable)?.Dispose();

    private RawValuePagedHashMap<long, PersistentStore> CreateMap(int preSplits)
    {
        var mmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = mmf.AllocateChunkBasedSegment(PageBlockType.None, 20, RawValuePagedHashMap<long, PersistentStore>.RecommendedStride(ValueSize));
        var map = RawValuePagedHashMap<long, PersistentStore>.Create(segment, N0, ValueSize);
        using var guard = EpochGuard.Enter(_serviceProvider.GetRequiredService<EpochManager>());
        var accessor = segment.CreateChunkAccessor();
        var value = stackalloc byte[ValueSize];
        try
        {
            for (var key = 1L; key <= Keys; key++)
            {
                WriteValue(key, value);
                map.InsertNew(key, value, ref accessor, null);
            }

            for (var i = 0; i < preSplits; i++)
            {
                Assert.That(map.SplitOnceForTest(ref accessor), Is.True, "premise: the pre-split ran");
            }
        }
        finally
        {
            accessor.Dispose();
        }

        Assert.That(map.BucketCount, Is.EqualTo(N0 + preSplits), "premise: the inserts split nothing");
        return map;
    }

    /// <summary>Whether the next split, of the bucket the split pointer names (level 0), moves at least one of the inserted entries.</summary>
    private static bool NextSplitMovesAnEntry(RawValuePagedHashMap<long, PersistentStore> map, int preSplits)
    {
        for (var key = 1L; key <= Keys; key++)
        {
            var h = RawValuePagedHashMap<long, PersistentStore>.ComputeHashForTest(key);
            if (map.BucketIndexOf(key) == preSplits && (h & (2 * N0 - 1)) == preSplits + N0)
            {
                return true;
            }
        }

        return false;
    }

    // Every value starts with its own key: a torn or misplaced read shows up as a mismatch.
    private static void WriteValue(long key, byte* value)
    {
        *(long*)value = key;
        for (var i = sizeof(long); i < ValueSize; i++)
        {
            value[i] = (byte)(key * 31 + i);
        }
    }

    /// <summary>Splits the map from inside a walk, on the walk's own thread: the walk holds no latch between two reads.</summary>
    private sealed class Splitter(RawValuePagedHashMap<long, PersistentStore> map)
    {
        public int BucketsBefore;

        public void Split(int times)
        {
            BucketsBefore = map.BucketCount;
            var accessor = map.Segment.CreateChunkAccessor();
            try
            {
                for (var i = 0; i < times; i++)
                {
                    Assert.That(map.SplitOnceForTest(ref accessor), Is.True, "premise: the split ran");
                }
            }
            finally
            {
                accessor.Dispose();
            }
        }
    }

    private struct VisitSplittingMidScan : RawValuePagedHashMap<long, PersistentStore>.IEntryAction<long>
    {
        public Splitter Splitter;
        public int SplitAt;
        public int Splits;
        public int Seen;
        public Dictionary<long, int> Visits;
        public int WrongValues;

        public bool Process(long key, byte* value)
        {
            if (++Seen == SplitAt)
            {
                Splitter.Split(Splits);
            }

            Visits[key] = Visits.GetValueOrDefault(key) + 1;
            if (*(long*)value != key)
            {
                WrongValues++;
            }

            return true;
        }
    }

    private struct CountSplittingMidScan : RawValuePagedHashMap<long, PersistentStore>.IEntryPredicate<long>
    {
        public Splitter Splitter;
        public int SplitAt;
        public int Splits;
        public int Calls;
        public long Only;

        public bool Matches(long key, byte* value)
        {
            if (++Calls == SplitAt)
            {
                Splitter.Split(Splits);
            }

            return Only == 0 || key == Only;
        }
    }

    // (pre-splits, the entry the walk splits at, splits): the split pointer, where the walk is when it splits, how far the map grows meanwhile.
    private static readonly object[] Cases =
    [
        new object[] { 64, 1, 1 },      // a bucket the walk has not read yet: its moved half lands past the count the walk started with
        new object[] { 0, 200, 1 },     // a bucket the walk has read: its moved half must not be read again
        new object[] { 0, 200, 300 },   // past a level: read and unread buckets split, the second level's images too
        new object[] { 0, 200, 700 },   // images of images: a bucket created during the walk splits again
        new object[] { 100, 300, 400 },
        new object[] { 0, 1, 1_000 },   // the map grows fivefold before the walk reads its second bucket
    ];

    [TestCaseSource(nameof(Cases))]
    public void ForEachEntry_VisitsEveryEntryOnce_WhileTheMapSplits(int preSplits, int splitAt, int splits)
    {
        var map = CreateMap(preSplits);
        Assert.That(NextSplitMovesAnEntry(map, preSplits), Is.True, "premise: the first split moves an entry");
        var walk = new VisitSplittingMidScan { Splitter = new Splitter(map), SplitAt = splitAt, Splits = splits, Visits = [] };
        int visited;
        using (EpochGuard.Enter(_serviceProvider.GetRequiredService<EpochManager>()))
        {
            var accessor = map.Segment.CreateChunkAccessor();
            try
            {
                visited = map.ForEachEntry(ref accessor, ref walk);
            }
            finally
            {
                accessor.Dispose();
            }
        }

        Assert.That(map.BucketCount, Is.EqualTo(N0 + preSplits + splits), "premise: the map split during the walk");
        var twice = 0;
        foreach (var (_, n) in walk.Visits)
        {
            twice += n > 1 ? 1 : 0;
        }

        Assert.That((walk.Visits.Count, twice, walk.WrongValues, visited), Is.EqualTo((Keys, 0, 0, Keys)),
            "(distinct entries visited, entries visited twice, values read torn, visits)");
    }

    [TestCaseSource(nameof(Cases))]
    public void CountEntries_CountsEveryEntryOnce_WhileTheMapSplits(int preSplits, int splitAt, int splits)
    {
        var map = CreateMap(preSplits);
        Assert.That(NextSplitMovesAnEntry(map, preSplits), Is.True, "premise: the first split moves an entry");
        var count = new CountSplittingMidScan { Splitter = new Splitter(map), SplitAt = splitAt, Splits = splits };
        int counted;
        using (EpochGuard.Enter(_serviceProvider.GetRequiredService<EpochManager>()))
        {
            var accessor = map.Segment.CreateChunkAccessor();
            try
            {
                counted = map.CountEntries(ref accessor, ref count);
            }
            finally
            {
                accessor.Dispose();
            }
        }

        Assert.That(map.BucketCount, Is.EqualTo(N0 + preSplits + splits), "premise: the map split during the walk");
        Assert.That(counted, Is.EqualTo(Keys));
    }

    /// <summary>The one matching entry sits in the bucket the split pointer names, and the split moves it past the count the walk started with.</summary>
    [Test]
    public void AnyEntry_FindsAnEntryASplitMovedPastTheStartCount()
    {
        const int preSplits = 64;
        var map = CreateMap(preSplits);
        long moved = 0;
        for (var key = 1L; key <= Keys && moved == 0; key++)
        {
            var h = RawValuePagedHashMap<long, PersistentStore>.ComputeHashForTest(key);
            if (map.BucketIndexOf(key) == preSplits && (h & (2 * N0 - 1)) == preSplits + N0)
            {
                moved = key;
            }
        }

        Assert.That(moved, Is.Not.Zero, "premise: an entry of the bucket the split pointer names moves on its split");
        var any = new CountSplittingMidScan { Splitter = new Splitter(map), SplitAt = 1, Splits = 1, Only = moved };
        bool found;
        using (EpochGuard.Enter(_serviceProvider.GetRequiredService<EpochManager>()))
        {
            var accessor = map.Segment.CreateChunkAccessor();
            try
            {
                found = map.AnyEntry(ref accessor, ref any);
            }
            finally
            {
                accessor.Dispose();
            }
        }

        Assert.That(map.BucketIndexOf(moved), Is.EqualTo(N0 + preSplits), "premise: the split moved it to the bucket it created");
        Assert.That(found, Is.True);
    }

    private struct CountStable : RawValuePagedHashMap<long, PersistentStore>.IEntryPredicate<long>
    {
        public bool Matches(long key, byte* value) => key <= Keys;
    }

    private struct VisitStable : RawValuePagedHashMap<long, PersistentStore>.IEntryAction<long>
    {
        public int[] Visits;
        public int WrongValues;

        public bool Process(long key, byte* value)
        {
            if (*(long*)value != key)
            {
                WrongValues++;
            }

            if (key <= Keys)
            {
                Visits[key]++;
            }

            return true;
        }
    }

    /// <summary>
    /// Inserters and a remover grow and churn the map, splitting it as they go, while walkers count the entries that stay throughout: each walk must find
    /// every one of them exactly once.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void Walks_SeeTheStableEntriesExactlyOnce_UnderConcurrentInsertsAndSplits()
    {
        var map = CreateMap(0);
        var em = _serviceProvider.GetRequiredService<EpochManager>();
        const int writers = 3;
        const int perWriter = 30_000;
        var failures = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var stop = 0;
        var writersDone = 0;
        var walksDuringSplits = 0;

        ThreadStart Guarded(Action body) => () =>
        {
            try
            {
                body();
            }
            catch (Exception e)
            {
                failures.Enqueue(e.ToString());
                Volatile.Write(ref stop, 1);
            }
        };

        var threads = new List<Thread>();
        for (var w = 0; w < writers; w++)
        {
            var writer = w;
            threads.Add(new Thread(Guarded(() =>
            {
                var value = stackalloc byte[ValueSize];
                for (var i = 0; i < perWriter && Volatile.Read(ref stop) == 0; i += 64)
                {
                    using var guard = EpochGuard.Enter(em);
                    var accessor = map.Segment.CreateChunkAccessor();
                    try
                    {
                        for (var j = i; j < Math.Min(perWriter, i + 64); j++)
                        {
                            var key = Keys + 1 + (long)writer * perWriter + j;
                            WriteValue(key, value);
                            map.InsertNew(key, value, ref accessor, null);
                            if (j % 3 == 0)
                            {
                                map.Remove(key, ref accessor, null);   // churn: chains shrink while others grow
                            }
                        }
                    }
                    finally
                    {
                        accessor.Dispose();
                    }
                }

                Interlocked.Increment(ref writersDone);
            })));
        }

        for (var r = 0; r < 2; r++)
        {
            threads.Add(new Thread(Guarded(() =>
            {
                var visits = new int[Keys + 1];
                while (Volatile.Read(ref writersDone) < writers && Volatile.Read(ref stop) == 0)
                {
                    using var guard = EpochGuard.Enter(em);
                    var accessor = map.Segment.CreateChunkAccessor();
                    try
                    {
                        var before = map.BucketCount;
                        Array.Clear(visits);
                        var walk = new VisitStable { Visits = visits };
                        map.ForEachEntry(ref accessor, ref walk);
                        var stable = new CountStable();
                        var counted = map.CountEntries(ref accessor, ref stable);
                        if (map.BucketCount != before)
                        {
                            Interlocked.Increment(ref walksDuringSplits);
                        }

                        for (var k = 1; k <= Keys; k++)
                        {
                            if (visits[k] != 1)
                            {
                                failures.Enqueue($"ForEachEntry visited stable key {k} {visits[k]} times (buckets {before} → {map.BucketCount})");
                                Volatile.Write(ref stop, 1);
                                break;
                            }
                        }

                        if (walk.WrongValues != 0 || counted != Keys)
                        {
                            failures.Enqueue($"{walk.WrongValues} torn values, CountEntries {counted} of {Keys} (buckets {before} → {map.BucketCount})");
                            Volatile.Write(ref stop, 1);
                        }
                    }
                    finally
                    {
                        accessor.Dispose();
                    }
                }
            })));
        }

        foreach (var t in threads)
        {
            t.Start();
        }

        foreach (var t in threads)
        {
            t.Join();
        }

        TestContext.Out.WriteLine($"buckets {map.BucketCount}, walks across a split {walksDuringSplits}");
        Assert.That(failures, Is.Empty, string.Join("\n", System.Linq.Enumerable.Take(failures, 10)));
        Assert.That(walksDuringSplits, Is.GreaterThan(0), "premise: some walks ran while the map split");
    }
}
