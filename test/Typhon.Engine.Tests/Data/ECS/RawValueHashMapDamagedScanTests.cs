using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// <see cref="RawValuePagedHashMap{TKey,TStore}.ForEachEntryQuiescent{TAction}"/>: the walk an open makes over a persisted map it is about to replace,
/// which may be torn. It must end, and must not read outside the segment, whatever the damage. The optimistic walk retried a bucket whose version word was
/// torn into "locked" forever: a crash reopen hung in the EntityMap rebuild.
/// </summary>
[TestFixture]
unsafe class RawValueHashMapDamagedScanTests
{
    private const int ValueSize = 26;

    private ServiceProvider _serviceProvider;
    private string CurrentDatabaseName => $"DmgScan_{TestContext.CurrentContext.Test.Name[..Math.Min(40, TestContext.CurrentContext.Test.Name.Length)]}";

    [SetUp]
    public void Setup()
    {
        var services = new ServiceCollection();
        services
            .AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddScopedManagedPagedMemoryMappedFile(options =>
            {
                options.DatabaseName = CurrentDatabaseName;
                options.DatabaseCacheSize = (ulong)PagedMMF.MinimumCacheSize * 16;
            });

        _serviceProvider = services.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
    }

    [TearDown]
    public void TearDown() => _serviceProvider?.Dispose();

    private struct CountValid : RawValuePagedHashMap<long, PersistentStore>.IEntryAction<long>
    {
        public int Valid;

        public bool Process(long key, byte* value)
        {
            if (*(long*)value == key)
            {
                Valid++;
            }

            return true;
        }
    }

    /// <summary>A 256-bucket map holding keys 1..<paramref name="count"/>, each value starting with its key.</summary>
    private static RawValuePagedHashMap<long, PersistentStore> Build(ManagedPagedMMF mpmmf, ref ChunkAccessor<PersistentStore> accessor,
        ChunkBasedSegment<PersistentStore> segment, int count)
    {
        var map = RawValuePagedHashMap<long, PersistentStore>.Create(segment, 256, ValueSize);
        var record = stackalloc byte[ValueSize];
        for (long k = 1; k <= count; k++)
        {
            *(long*)record = k;
            map.Insert(k, record, ref accessor, null);
        }

        return map;
    }

    private static ref PagedHashMapBucketHeader HeaderOf(ref ChunkAccessor<PersistentStore> accessor, int chunkId) =>
        ref *(PagedHashMapBucketHeader*)accessor.GetChunkAddress(chunkId, true);

    [Test]
    public void OnAHealthyMap_ItVisitsWhatTheOptimisticWalkVisits()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var guard = EpochGuard.Enter(_serviceProvider.GetRequiredService<EpochManager>());
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, RawValuePagedHashMap<long, PersistentStore>.RecommendedStride(ValueSize));
        var accessor = segment.CreateChunkAccessor();
        var map = Build(mpmmf, ref accessor, segment, 3000);

        var optimistic = new CountValid();
        var quiescent = new CountValid();
        Assert.That(map.ForEachEntry(ref accessor, ref optimistic), Is.EqualTo(3000));
        Assert.That(map.ForEachEntryQuiescent(ref accessor, ref quiescent), Is.EqualTo(3000));
        Assert.That(quiescent.Valid, Is.EqualTo(3000));
        accessor.Dispose();
    }

    /// <summary>
    /// A bucket head whose version word reads "locked" on disk: no writer exists to release it, so the walk reads the bucket instead of waiting. The optimistic
    /// walk spins here for good, which is why this test never calls it.
    /// </summary>
    [Test]
    public void ABucketLeftLockedOnDisk_IsReadNotWaitedFor()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var guard = EpochGuard.Enter(_serviceProvider.GetRequiredService<EpochManager>());
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, RawValuePagedHashMap<long, PersistentStore>.RecommendedStride(ValueSize));
        var accessor = segment.CreateChunkAccessor();
        var map = Build(mpmmf, ref accessor, segment, 3000);

        for (var b = 0; b < 256; b += 7)
        {
            HeaderOf(ref accessor, map.GetBucketChunkIdForTest(b, ref accessor)).OlcVersion |= 0b11;   // locked and obsolete
        }

        var probe = new CountValid();
        Assert.That(map.ForEachEntryQuiescent(ref accessor, ref probe), Is.EqualTo(3000));
        Assert.That(probe.Valid, Is.EqualTo(3000));
        accessor.Dispose();
    }

    /// <summary>
    /// A chain that loops back on itself, an overflow id far outside the segment, and a directory chunk id that is negative: the walk ends, skips what it
    /// cannot reach, and still visits every bucket the damage did not touch. A negative directory id read unchecked lands before the page's chunk area.
    /// </summary>
    [Test]
    public void LoopsAndWildChunkIds_EndTheirBucket_AndLeaveTheRestReadable()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var guard = EpochGuard.Enter(_serviceProvider.GetRequiredService<EpochManager>());
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, RawValuePagedHashMap<long, PersistentStore>.RecommendedStride(ValueSize));
        var accessor = segment.CreateChunkAccessor();
        var map = Build(mpmmf, ref accessor, segment, 3000);

        // What the damage below cuts off: every entry of buckets 0..63 (directory chunk 0), and whatever follows the head chunk of buckets 100 and 200.
        var lost = 0;
        for (long k = 1; k <= 3000; k++)
        {
            lost += map.BucketIndexOf(k) < 64 ? 1 : 0;
        }

        var loopHead = map.GetBucketChunkIdForTest(100, ref accessor);
        var wildHead = map.GetBucketChunkIdForTest(200, ref accessor);
        lost += EntriesAfterHead(ref accessor, loopHead) + EntriesAfterHead(ref accessor, wildHead);

        accessor.GetChunk<PagedHashMapMeta>(0, true).DirectoryChunkIds[0] = -7;
        HeaderOf(ref accessor, loopHead).OverflowChunkId = loopHead;
        HeaderOf(ref accessor, wildHead).OverflowChunkId = int.MaxValue - 3;

        var probe = new CountValid();
        var visited = map.ForEachEntryQuiescent(ref accessor, ref probe);

        Assert.That(lost, Is.InRange(1, 2999), "premise: the damage cuts some entries off, not all");
        Assert.That(probe.Valid, Is.GreaterThanOrEqualTo(3000 - lost), "every entry the damage did not cut off is still read");
        Assert.That(visited, Is.GreaterThan(3000 - lost), "the looping chain is read again and again until the walk cuts it, then the walk goes on");
        accessor.Dispose();
    }

    private static int EntriesAfterHead(ref ChunkAccessor<PersistentStore> accessor, int head)
    {
        var n = 0;
        for (var c = RawValuePagedHashMap<long, PersistentStore>.BucketOverflowChunkIdForTest(head, ref accessor); c >= 0;
             c = RawValuePagedHashMap<long, PersistentStore>.BucketOverflowChunkIdForTest(c, ref accessor))
        {
            n += HeaderOf(ref accessor, c).EntryCount;
        }

        return n;
    }
}
