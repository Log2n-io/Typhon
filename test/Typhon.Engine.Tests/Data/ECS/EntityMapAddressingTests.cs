using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// #1205: the entity map finds bucket <c>b</c> at chunk <c>b + 1</c> of its segment — no directory — and keeps its overflow chunks above the bucket
/// frontier with an allocation floor, moving one out when a split needs its chunk. These tests pin the addressing, the move, the deferral when the chunk
/// cannot be moved yet, the behaviour at the bucket cap, and the floor itself.
/// </summary>
[VerifiesRule("EMAP-01")]
unsafe class EntityMapAddressingTests
{
    /// <summary>The Market item's entity record (19 + 4 × 1 Versioned component): 7 entries per 256-byte bucket.</summary>
    private const int ValueSize = 23;

    private const int N0 = 256;

    private IServiceProvider _serviceProvider;
    private static string CurrentDatabaseName => $"T_EMA_{TestSeed.StableHash(TestContext.CurrentContext.Test.Name):X8}";

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

    private (ManagedPagedMMF Mmf, ChunkBasedSegment<PersistentStore> Segment, RawValuePagedHashMap<long, PersistentStore> Map) CreateMap()
    {
        var mmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = mmf.AllocateChunkBasedSegment(PageBlockType.None, 20, RawValuePagedHashMap<long, PersistentStore>.RecommendedStride(ValueSize));
        return (mmf, segment, RawValuePagedHashMap<long, PersistentStore>.Create(segment, N0, ValueSize));
    }

    private static void WriteValue(long key, byte* value)
    {
        for (var i = 0; i < ValueSize; i++)
        {
            value[i] = (byte)(key * 31 + i);
        }
    }

    private void Insert(RawValuePagedHashMap<long, PersistentStore> map, IEnumerable<long> keys)
    {
        var em = _serviceProvider.GetRequiredService<EpochManager>();
        using var guard = EpochGuard.Enter(em);
        var accessor = map.Segment.CreateChunkAccessor();
        var value = stackalloc byte[ValueSize];
        try
        {
            foreach (var key in keys)
            {
                WriteValue(key, value);
                map.InsertNew(key, value, ref accessor, null);
            }
        }
        finally
        {
            accessor.Dispose();
        }
    }

    private static IEnumerable<long> Range(long from, long count)
    {
        for (var k = from; k < from + count; k++)
        {
            yield return k;
        }
    }

    private void AssertEveryKeyFound(RawValuePagedHashMap<long, PersistentStore> map, IEnumerable<long> keys)
    {
        var em = _serviceProvider.GetRequiredService<EpochManager>();
        using var guard = EpochGuard.Enter(em);
        var accessor = map.Segment.CreateChunkAccessor();
        var value = stackalloc byte[ValueSize];
        var expected = stackalloc byte[ValueSize];
        try
        {
            foreach (var key in keys)
            {
                Assert.That(map.TryGet(key, value, ref accessor), Is.True, $"key {key} lost");
                WriteValue(key, expected);
                Assert.That(new ReadOnlySpan<byte>(value, ValueSize).SequenceEqual(new ReadOnlySpan<byte>(expected, ValueSize)), Is.True,
                    $"key {key} holds another entry's value");
            }

            Assert.That(map.VerifyIntegrity(ref accessor), Is.True, "structure: chains, entry counts, the entry total, every overflow chunk's owner");
        }
        finally
        {
            accessor.Dispose();
        }
    }

    /// <summary>Keys from <paramref name="from"/> on that the map resolves to <paramref name="bucket"/> now.</summary>
    private static List<long> KeysOfBucket(RawValuePagedHashMap<long, PersistentStore> map, int bucket, int count, long from)
    {
        var keys = new List<long>();
        for (var k = from; keys.Count < count; k++)
        {
            if (map.BucketIndexOf(k) == bucket)
            {
                keys.Add(k);
            }
        }

        return keys;
    }

    [Test]
    public void Buckets_AreAtTheirPositions()
    {
        var (_, segment, map) = CreateMap();
        Insert(map, Range(1, 30_000));
        Assert.That(map.BucketCount, Is.GreaterThan(4 * N0), "premise: the map split well past N0");

        var em = _serviceProvider.GetRequiredService<EpochManager>();
        using (EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            for (var b = 0; b < map.BucketCount; b++)
            {
                Assert.That(map.GetBucketChunkIdForTest(b, ref accessor), Is.EqualTo(b + 1));
                Assert.That(segment.IsChunkAllocated(b + 1), Is.True, $"bucket {b}'s chunk is not allocated");
            }

            accessor.Dispose();
        }

        // Nothing the allocator handed out lies in the bucket range or the runway above it.
        Assert.That(segment.AllocationFloor, Is.GreaterThan(map.BucketCount + 1));
        AssertEveryKeyFound(map, Range(1, 30_000));
    }

    [Test]
    public void AnOverflowAtTheFrontier_IsMovedByTheSplit()
    {
        var (_, segment, map) = CreateMap();

        // Stage the hazard before anything else allocates: drop the floor so the allocator's lowest free chunk — chunk 257, the next bucket's — becomes an
        // overflow chunk, then restore it.
        var floor = segment.AllocationFloor;
        segment.ResetAllocationFloor(0);
        var target = 13;
        var crowd = KeysOfBucket(map, target, 8, 1_000_000);   // 8 > 7 entries per bucket: the bucket must chain
        Insert(map, crowd);
        segment.ResetAllocationFloor(floor);

        Insert(map, Range(1, 1000));                       // under the first split (256 × 7 × 0.75 = 1 344)
        Assert.That(map.BucketCount, Is.EqualTo(N0));

        var em = _serviceProvider.GetRequiredService<EpochManager>();
        using (EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            var overflow = RawValuePagedHashMap<long, PersistentStore>.BucketOverflowChunkIdForTest(map.GetBucketChunkIdForTest(target, ref accessor),
                ref accessor);
            accessor.Dispose();
            Assert.That(overflow, Is.EqualTo(N0 + 1), "premise: the overflow chunk sits on the chunk the next bucket takes");
        }

        Insert(map, Range(2000, 2000));
        Assert.That(map.BucketCount, Is.GreaterThan(N0), "the map split past the frontier");
        Assert.That(map._overflowChunksRelocated, Is.GreaterThanOrEqualTo(1), "the split moved the overflow chunk out of its way");

        AssertEveryKeyFound(map, Range(1, 1000));
        AssertEveryKeyFound(map, crowd);
        AssertEveryKeyFound(map, Range(2000, 2000));
    }

    [Test]
    public void AFrontierHeldUnlinked_DefersTheSplit()
    {
        var (_, segment, map) = CreateMap();
        Insert(map, Range(1, 1300));

        // The next bucket's chunk, allocated and linked to nothing — what a writer holds between reserving a chunk and linking it.
        var em = _serviceProvider.GetRequiredService<EpochManager>();
        using (EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            Assert.That(segment.TryReserveChunk(N0 + 1, null, ref accessor), Is.True);
            accessor.Dispose();
        }

        Insert(map, Range(2000, 300));                      // past the threshold: splits are attempted and must wait
        Assert.That(map.BucketCount, Is.EqualTo(N0), "no split may take a chunk someone holds");
        Assert.That(map._splitsDeferred, Is.GreaterThan(0));
        AssertEveryKeyFound(map, Range(1, 1300));
        AssertEveryKeyFound(map, Range(2000, 300));

        // The holder lets go: the next insert splits.
        using (EpochGuard.Enter(em))
        {
            segment.FreeChunk(N0 + 1);
        }

        Insert(map, Range(5000, 10));
        Assert.That(map.BucketCount, Is.GreaterThan(N0));
        AssertEveryKeyFound(map, Range(1, 1300));
        AssertEveryKeyFound(map, Range(2000, 300));
        AssertEveryKeyFound(map, Range(5000, 10));
    }

    /// <summary>After a clear, a chunk that was a bucket's primary can come back as an overflow chunk: the location hints pointing at it must go.</summary>
    [Test]
    public void AClearForRebuild_DropsTheLocationHints()
    {
        var (_, segment, map) = CreateMap();
        Insert(map, Range(1, 3000));
        var em = _serviceProvider.GetRequiredService<EpochManager>();
        using (EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            var value = stackalloc byte[ValueSize];
            for (var k = 1L; k <= 3000; k++)
            {
                map.TryGetWithHint(k, value, ref accessor);
            }

            accessor.Dispose();
        }

        Assert.That(map.HasLocationHintsForTest, Is.True, "premise");
        map.ClearForRebuild(null);
        Assert.That(map.HasLocationHintsForTest, Is.False);
    }

    /// <summary>
    /// A split runs at the start of the write that triggers it, before that write takes effect: a fault in a split fails a write that changed nothing,
    /// and its caller can retry it — InsertNew has no duplicate check, so a write that had taken effect and then thrown would add its key twice. A
    /// split that cannot get its chunk or its pages (<see cref="ResourceExhaustedException"/>) is skipped instead, and counted. A failed split frees the
    /// chunk it claimed: nothing is left allocated at the frontier for the next split to defer on.
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public void AFaultInASplit_FailsOnlyAWriteThatTookNoEffect(bool resourceExhausted)
    {
        var (_, segment, map) = CreateMap();
        const int keys = 6_000;

        // Bucket 0 — the first split's — crowded past two chunks' worth, so that split must take overflow chunks for its halves, and meets the fault.
        var crowd = KeysOfBucket(map, 0, 30, 10_000_000);
        Insert(map, crowd);

        var failed = new List<long>();
        var armed = false;
        ChunkBasedSegment<PersistentStore>.AllocateFaultForTest = s =>
        {
            if (armed && ReferenceEquals(s, segment))
            {
                throw resourceExhausted
                    ? new ResourceExhaustedException("injected", "Test/Split", ResourceType.Memory, 0, 0)
                    : new IOException("injected");
            }
        };
        try
        {
            var em = _serviceProvider.GetRequiredService<EpochManager>();
            using var guard = EpochGuard.Enter(em);
            var accessor = segment.CreateChunkAccessor();
            var value = stackalloc byte[ValueSize];
            try
            {
                armed = true;
                for (var k = 1L; k <= keys; k++)
                {
                    WriteValue(k, value);
                    try
                    {
                        map.InsertNew(k, value, ref accessor, null);
                    }
                    catch (Exception e) when (e is IOException or ResourceExhaustedException)
                    {
                        failed.Add(k);
                    }
                }

                armed = false;
                Assert.That(failed, Is.Not.Empty, "premise: allocations failed");
                Assert.That(map.EntryCount, Is.EqualTo(crowd.Count + keys - failed.Count), "a write that threw took no effect");
                Assert.That(segment.IsChunkAllocated(map.BucketCount + 1), Is.False, "a failed split frees the chunk it claimed");
                if (resourceExhausted)
                {
                    Assert.That(map._splitsFailed, Is.GreaterThan(0), "premise: splits met the fault, and were skipped rather than thrown");
                }

                foreach (var k in failed)
                {
                    WriteValue(k, value);
                    map.InsertNew(k, value, ref accessor, null);
                }
            }
            finally
            {
                accessor.Dispose();
            }
        }
        finally
        {
            ChunkBasedSegment<PersistentStore>.AllocateFaultForTest = null;
        }

        Assert.That(map.EntryCount, Is.EqualTo(crowd.Count + keys), "every key once");
        Assert.That(map.BucketCount, Is.GreaterThan(N0));
        AssertEveryKeyFound(map, Range(1, keys));
        AssertEveryKeyFound(map, crowd);
        AssertIntegrity(map);
    }

    /// <summary>
    /// A fault after a split published its count — here in persisting it — leaves the new bucket live: readers already resolve to its chunk, so the
    /// split must not free it. Published is the count, not the split's own "done" flag, which the fault left unset.
    /// </summary>
    [Test]
    public void AFaultAfterASplitPublished_KeepsTheNewBucket()
    {
        var (_, segment, map) = CreateMap();
        var before = new List<long>(Range(1, 300));
        Insert(map, before);
        var bucketCount = map.BucketCount;

        // The next insert past the threshold splits; the probe throws as that split persists the count it has just published.
        var thrown = 0;
        map.PersistBucketCountProbe = () =>
        {
            thrown++;
            throw new IOException("injected");
        };
        var key = 1_000_000L;
        try
        {
            for (; thrown == 0 && key < 1_100_000; key++)
            {
                try
                {
                    Insert(map, [key]);
                }
                catch (IOException)
                {
                    break;
                }
            }
        }
        finally
        {
            map.PersistBucketCountProbe = null;
        }

        Assert.That(thrown, Is.EqualTo(1), "premise: a split met the fault after publishing");
        Assert.That(map.BucketCount, Is.EqualTo(bucketCount + 1), "the count was published before the fault");
        Assert.That(segment.IsChunkAllocated(map.BucketCount), Is.True, "the new bucket's chunk is live, not freed");
        AssertPersistedBucketCount(segment, bucketCount, "premise: the faulted persist left the meta behind");

        // The checkpoint's flush catches the persisted count up — under the split lock, so still its one writer — or a reopen would lose the bucket.
        var cs = _serviceProvider.GetRequiredService<ManagedPagedMMF>().CreateChangeSet();
        using (EpochGuard.Enter(_serviceProvider.GetRequiredService<EpochManager>()))
        {
            Assert.That(map.FlushMeta(cs), Is.True);
        }

        cs.ReleaseDirtyMarks();
        AssertPersistedBucketCount(segment, bucketCount + 1, "the flush persists the published count");

        Insert(map, [key]);   // the write that failed took no effect, and goes through on a retry
        AssertEveryKeyFound(map, before);
        AssertEveryKeyFound(map, Range(1_000_000, key - 1_000_000 + 1));
        AssertIntegrity(map);
    }

    /// <summary>
    /// The bucket count persisted in the meta chunk has one writer — the split that published it, under the split lock — and the entry count another,
    /// <see cref="PagedHashMapBase{TStore}.FlushMeta"/>. A flush that wrote both read the bucket count outside the lock, and could store an older one over
    /// a newer: after a reopen the last bucket was unreachable and its chunk read as an overflow chunk at the frontier. A flush writes only a changed
    /// entry count, and says whether it wrote.
    /// </summary>
    [Test]
    public void TheMetaCounts_EachHaveOneWriter()
    {
        var (mmf, segment, map) = CreateMap();
        Insert(map, Range(1, 3_000));
        var em = _serviceProvider.GetRequiredService<EpochManager>();
        using var guard = EpochGuard.Enter(em);
        var cs = mmf.CreateChangeSet();
        var accessor = segment.CreateChunkAccessor();
        try
        {
            ref var meta = ref accessor.GetChunk<PagedHashMapMeta>(0, true);
            Assert.That(meta.BucketCount, Is.EqualTo(map.BucketCount), "every split persists the count it published");
            Assert.That(() => map.FlushMeta(null), Throws.ArgumentNullException, "a flush no change set records would never reach the file");

            meta.BucketCount = 12_345;   // a sentinel no flush may touch
            Assert.That(map.FlushMeta(cs), Is.True, "the entry count moved since the map was created");
            Assert.That(meta.EntryCount, Is.EqualTo(3_000));
            Assert.That(meta.BucketCount, Is.EqualTo(12_345), "FlushMeta never writes a bucket count its split persisted");
            Assert.That(map.FlushMeta(cs), Is.False, "nothing new: nothing written");

            for (var k = 1L; k <= 10; k++)
            {
                map.Remove(k, ref accessor, null);
            }

            Assert.That(map.FlushMeta(cs), Is.True, "removes move the count too");
            Assert.That(meta.EntryCount, Is.EqualTo(2_990));
            meta.BucketCount = map.BucketCount;
        }
        finally
        {
            accessor.Dispose();
            cs.ReleaseDirtyMarks();
        }
    }

    /// <summary>
    /// The overflow chunk on the frontier is a chain's second, not its first: the relocation relinks an overflow chunk, not the bucket's primary. Staged:
    /// the chunk is reserved while the chain's first overflow chunk is allocated, then freed for the second.
    /// </summary>
    [Test]
    public void AnOverflowAtTheFrontier_PastAnotherOverflow_IsMoved()
    {
        var (_, segment, map) = CreateMap();
        var em = _serviceProvider.GetRequiredService<EpochManager>();
        var floor = segment.AllocationFloor;
        segment.ResetAllocationFloor(0);
        var target = 13;
        var crowd = KeysOfBucket(map, target, 15, 1_000_000);   // 15 = 7 + 7 + 1: the bucket chains twice
        using (EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            Assert.That(segment.TryReserveChunk(N0 + 1, null, ref accessor), Is.True);
            accessor.Dispose();
        }

        Insert(map, crowd.GetRange(0, 8));                         // the first overflow chunk, past the frontier's
        using (EpochGuard.Enter(em))
        {
            segment.FreeChunk(N0 + 1);
        }

        Insert(map, crowd.GetRange(8, 7));                         // the second: the lowest free chunk, the frontier's
        segment.ResetAllocationFloor(floor);
        using (EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            var first = RawValuePagedHashMap<long, PersistentStore>.BucketOverflowChunkIdForTest(map.GetBucketChunkIdForTest(target, ref accessor),
                ref accessor);
            var second = RawValuePagedHashMap<long, PersistentStore>.BucketOverflowChunkIdForTest(first, ref accessor);
            accessor.Dispose();
            Assert.That((first != N0 + 1, second), Is.EqualTo((true, N0 + 1)), "premise: the frontier's chunk is the chain's second overflow chunk");
        }

        Insert(map, Range(1, 3_000));
        Assert.That(map.BucketCount, Is.GreaterThan(N0));
        Assert.That(map._overflowChunksRelocated, Is.GreaterThanOrEqualTo(1));
        AssertEveryKeyFound(map, crowd);
        AssertEveryKeyFound(map, Range(1, 3_000));
        AssertIntegrity(map);
    }

    /// <summary>
    /// The overflow chunk on the frontier belongs to the very bucket the split is about to split: the move latches that bucket and releases it, then
    /// the split latches it again, and both see one chain.
    /// </summary>
    [Test]
    public void AnOverflowAtTheFrontier_OwnedByTheBucketBeingSplit_IsMoved()
    {
        var (_, segment, map) = CreateMap();
        var floor = segment.AllocationFloor;
        segment.ResetAllocationFloor(0);
        var crowd = KeysOfBucket(map, 0, 8, 1_000_000);         // bucket 0 is the first split's old bucket
        Insert(map, crowd);
        segment.ResetAllocationFloor(floor);

        var em = _serviceProvider.GetRequiredService<EpochManager>();
        using (EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            var overflow = RawValuePagedHashMap<long, PersistentStore>.BucketOverflowChunkIdForTest(map.GetBucketChunkIdForTest(0, ref accessor),
                ref accessor);
            accessor.Dispose();
            Assert.That(overflow, Is.EqualTo(N0 + 1), "premise: bucket 0's overflow chunk sits on the chunk its own split takes");
        }

        Insert(map, Range(1, 3_000));
        Assert.That(map.BucketCount, Is.GreaterThan(N0));
        Assert.That(map._overflowChunksRelocated, Is.GreaterThanOrEqualTo(1));
        AssertEveryKeyFound(map, crowd);
        AssertEveryKeyFound(map, Range(1, 3_000));
        AssertIntegrity(map);
    }

    /// <summary>
    /// A frontier chunk whose owner tag is stale — it names a bucket whose chain does not reach it — is not moved: the walk finds no predecessor and the
    /// split defers, and tries again on the next insert (a tagged chunk is not the "held unlinked" case the split stops trying on). Once it is freed, the
    /// map splits.
    /// </summary>
    [Test]
    public void AFrontierChunkWithAStaleOwner_DefersUntilFreed()
    {
        var (_, segment, map) = CreateMap();
        Insert(map, Range(1, 1_300));
        var em = _serviceProvider.GetRequiredService<EpochManager>();
        using (EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            Assert.That(segment.TryReserveChunk(N0 + 1, null, ref accessor), Is.True);
            Unsafe.AsRef<PagedHashMapBucketHeader>(accessor.GetChunkAddress(N0 + 1, true)).OlcVersion = 5;   // "bucket 4's", which never linked it
            accessor.Dispose();
        }

        Insert(map, Range(2_000, 300));
        Assert.That(map.BucketCount, Is.EqualTo(N0), "nothing moves a chunk its named owner does not reach");
        Assert.That(map._splitsDeferred, Is.GreaterThan(1), "a tagged chunk is retried on every insert past the threshold, not skipped");

        using (EpochGuard.Enter(em))
        {
            segment.FreeChunk(N0 + 1);
        }

        Insert(map, Range(5_000, 10));
        Assert.That(map.BucketCount, Is.GreaterThan(N0));
        AssertEveryKeyFound(map, Range(1, 1_300));
        AssertEveryKeyFound(map, Range(2_000, 300));
        AssertIntegrity(map);
    }

    /// <summary>
    /// The frontier chunk's owner is latched by another writer: the move waits a bounded while for the latch and then defers rather than stall the
    /// split; once the latch is released, a later split moves it.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public unsafe void AFrontierChunkWhoseOwnerIsLatched_DefersThenMoves()
    {
        var (_, segment, map) = CreateMap();
        var floor = segment.AllocationFloor;
        segment.ResetAllocationFloor(0);
        var target = 13;
        var crowd = KeysOfBucket(map, target, 8, 1_000_000);
        Insert(map, crowd);
        segment.ResetAllocationFloor(floor);

        var em = _serviceProvider.GetRequiredService<EpochManager>();
        var others = new List<long>();
        for (var k = 1L; others.Count < 1_500; k++)
        {
            if (map.BucketIndexOf(k) != target)
            {
                others.Add(k);   // keys that do not need the latched bucket
            }
        }

        using (EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            ref var owner = ref Unsafe.AsRef<PagedHashMapBucketHeader>(accessor.GetChunkAddress(map.GetBucketChunkIdForTest(target, ref accessor), true));
            var latch = new OlcLatch(ref owner.OlcVersion);
            Assert.That(latch.TryWriteLock(), Is.True);
            try
            {
                var inserter = new Thread(() => Insert(map, others));
                inserter.Start();
                inserter.Join();
                Assert.That(map.BucketCount, Is.EqualTo(N0), "the split waits for no latch it cannot get");
                Assert.That(map._splitsDeferred, Is.GreaterThan(0));
                Assert.That(map._overflowChunksRelocated, Is.Zero);
            }
            finally
            {
                new OlcLatch(ref Unsafe.AsRef<PagedHashMapBucketHeader>(accessor.GetChunkAddress(map.GetBucketChunkIdForTest(target, ref accessor), true))
                    .OlcVersion).WriteUnlock();
                accessor.Dispose();
            }
        }

        Insert(map, Range(1_000_000_000, 10));
        Assert.That(map._overflowChunksRelocated, Is.GreaterThanOrEqualTo(1), "released, the owner's chunk is moved");
        Assert.That(map.BucketCount, Is.GreaterThan(N0));
        AssertEveryKeyFound(map, crowd);
        AssertEveryKeyFound(map, others);
        AssertIntegrity(map);
    }

    /// <summary>
    /// The frontier chunk is held unused in the splitting thread's own reservation: the claim takes it back rather than defer on a chunk only it could
    /// release.
    /// </summary>
    [Test]
    public void AFrontierChunkInTheSplittersOwnReservation_IsTakenBack()
    {
        var (_, segment, map) = CreateMap();
        Insert(map, Range(1, 1_000));                             // under the threshold: no split yet
        var em = _serviceProvider.GetRequiredService<EpochManager>();
        using var guard = EpochGuard.Enter(em);
        var accessor = segment.CreateChunkAccessor();
        var reservation = ChunkReservation<PersistentStore>.Current;
        reservation.Begin(segment);
        try
        {
            var floor = segment.AllocationFloor;
            segment.ResetAllocationFloor(0);
            segment.RewindAllocationCursorForTest();
            reservation.Fill(1, null, ref accessor);              // the lowest free chunk: the frontier's
            segment.ResetAllocationFloor(floor);
            Assert.That(segment.IsChunkAllocated(N0 + 1), Is.True, "premise: the reservation holds the frontier's chunk");

            Assert.That(map.SplitOnceForTest(ref accessor), Is.True, "the split claims the chunk its own thread holds");
            Assert.That(map.BucketCount, Is.EqualTo(N0 + 1));
            Assert.That(map._splitsDeferred, Is.Zero);
        }
        finally
        {
            reservation.End();
            accessor.Dispose();
        }

        AssertEveryKeyFound(map, Range(1, 1_000));
        AssertIntegrity(map);
    }

    private void AssertPersistedBucketCount(ChunkBasedSegment<PersistentStore> segment, int expected, string because)
    {
        using var guard = EpochGuard.Enter(_serviceProvider.GetRequiredService<EpochManager>());
        var accessor = segment.CreateChunkAccessor();
        try
        {
            Assert.That(accessor.GetChunkReadOnly<PagedHashMapMeta>(0).BucketCount, Is.EqualTo(expected), because);
        }
        finally
        {
            accessor.Dispose();
        }
    }

    private void AssertIntegrity(RawValuePagedHashMap<long, PersistentStore> map)
    {
        var em = _serviceProvider.GetRequiredService<EpochManager>();
        using var guard = EpochGuard.Enter(em);
        var accessor = map.Segment.CreateChunkAccessor();
        try
        {
            Assert.That(map.VerifyIntegrity(ref accessor), Is.True, "every chain well formed, every overflow chunk naming its bucket, the count right");
        }
        finally
        {
            accessor.Dispose();
        }
    }

    [Test]
    public void AtTheCap_TheMapKeepsInserting()
    {
        var (_, _, map) = CreateMap();
        map.SetBucketCapForTest(512);
        Insert(map, Range(1, 20_000));                      // ~5.6× what 512 buckets hold at the split threshold

        Assert.That(map.BucketCount, Is.EqualTo(512), "the map stops splitting at its cap");
        Assert.That(map.LoadFactor, Is.GreaterThan(5), "premise: well past the threshold, the chains carry it");
        AssertEveryKeyFound(map, Range(1, 20_000));
    }

    [Test]
    public void APreSize_IsProportional()
    {
        var (_, segment, map) = CreateMap();
        var em = _serviceProvider.GetRequiredService<EpochManager>();
        using (EpochGuard.Enter(em))
        {
            map.EnsureCapacity(100_000);
        }

        // 100k entries need 100k / (7 × 0.75) = 19 048 buckets. Rounded up to the next power of two, as it was, that was 32 768.
        var needed = 100_000 / (map.BucketCapacity * 0.75);
        Assert.That(segment.ChunkCapacity, Is.GreaterThanOrEqualTo((int)needed));
        Assert.That(segment.ChunkCapacity, Is.LessThan(32_768), "grown for the entries asked for, not to the next power of two");
    }

    /// <summary>
    /// Inserters, readers and a remover at once, over enough splits that the frontier overtakes the overflow chunks allocated above it: every move runs
    /// against readers walking the moved chain and writers appending to it. Every key a reader finds carries its own value, and at the end the map holds
    /// exactly the keys inserted and not removed.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void SplitsAndMoves_UnderConcurrentReadersAndWriters()
    {
        var (_, segment, map) = CreateMap();
        const int writers = 6;
        const int perWriter = 40_000;
        var em = _serviceProvider.GetRequiredService<EpochManager>();
        var inserted = new long[writers];
        var failures = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var stop = 0;

        // A worker's exception is a failure of the test, not of the test host: an unhandled one on a thread kills the process.
        System.Threading.ThreadStart Guarded(Action body) => () =>
        {
            try
            {
                body();
            }
            catch (Exception e)
            {
                failures.Enqueue(e.ToString());
                System.Threading.Volatile.Write(ref stop, 1);
            }
        };

        var threads = new List<System.Threading.Thread>();
        for (var w = 0; w < writers; w++)
        {
            var writer = w;
            threads.Add(new System.Threading.Thread(Guarded(() =>
            {
                var value = stackalloc byte[ValueSize];
                for (var i = 0; i < perWriter && System.Threading.Volatile.Read(ref stop) == 0; i += 256)
                {
                    using var guard = EpochGuard.Enter(em);
                    var accessor = map.Segment.CreateChunkAccessor();
                    for (var j = i; j < Math.Min(perWriter, i + 256); j++)
                    {
                        var key = 1 + (long)writer * perWriter + j;
                        WriteValue(key, value);
                        map.InsertNew(key, value, ref accessor, null);
                        System.Threading.Volatile.Write(ref inserted[writer], j + 1);
                    }

                    accessor.Dispose();
                }
            })));
        }

        // Readers: look up keys already inserted; a hit must carry its own value, a miss is a lost key.
        for (var r = 0; r < 2; r++)
        {
            var seed = r;
            threads.Add(new System.Threading.Thread(Guarded(() =>
            {
                var rng = new Random(1205 + seed);
                var value = stackalloc byte[ValueSize];
                var expected = stackalloc byte[ValueSize];
                while (System.Threading.Volatile.Read(ref stop) == 0)
                {
                    using var guard = EpochGuard.Enter(em);
                    var accessor = map.Segment.CreateChunkAccessor();
                    for (var n = 0; n < 512; n++)
                    {
                        var writer = rng.Next(writers);
                        var done = System.Threading.Volatile.Read(ref inserted[writer]);
                        if (done == 0)
                        {
                            continue;
                        }

                        var j = rng.Next((int)done);
                        if (j % 10 == 0)
                        {
                            continue;   // the remover's keys
                        }

                        var key = 1 + (long)writer * perWriter + j;
                        if (!map.TryGet(key, value, ref accessor))
                        {
                            failures.Enqueue($"key {key} not found");
                            continue;
                        }

                        WriteValue(key, expected);
                        if (!new ReadOnlySpan<byte>(value, ValueSize).SequenceEqual(new ReadOnlySpan<byte>(expected, ValueSize)))
                        {
                            failures.Enqueue($"key {key} read another entry's value");
                        }
                    }

                    accessor.Dispose();
                }
            })));
        }

        // Remover: every tenth key of writer 0, once inserted — frees overflow chunks while splits move others.
        var removed = new HashSet<long>();
        threads.Add(new System.Threading.Thread(Guarded(() =>
        {
            var next = 0;
            while (next < perWriter && System.Threading.Volatile.Read(ref stop) == 0)
            {
                var done = System.Threading.Volatile.Read(ref inserted[0]);
                using var guard = EpochGuard.Enter(em);
                var accessor = map.Segment.CreateChunkAccessor();
                for (; next < done; next += 10)
                {
                    var key = 1 + (long)next;
                    if (!map.Remove(key, ref accessor, null))
                    {
                        failures.Enqueue($"key {key} not removed");
                    }

                    removed.Add(key);
                }

                accessor.Dispose();
                if (next >= done)
                {
                    System.Threading.Thread.Yield();
                }
            }
        })));

        foreach (var t in threads)
        {
            t.Start();
        }

        for (var w = 0; w < writers; w++)
        {
            threads[w].Join();
        }

        threads[^1].Join();
        System.Threading.Volatile.Write(ref stop, 1);
        foreach (var t in threads)
        {
            t.Join();
        }

        TestContext.Out.WriteLine($"buckets {map.BucketCount}, entries {map.EntryCount}, pages {segment.Length}, chunks {segment.AllocatedChunkCount}"
                                  + $"/{segment.ChunkCapacity}, relocated {map._overflowChunksRelocated}, deferred {map._splitsDeferred}");
        Assert.That(failures, Is.Empty, string.Join("\n", System.Linq.Enumerable.Take(failures, 10)));
        Assert.That(map._overflowChunksRelocated, Is.GreaterThan(0), "premise: the frontier overtook overflow chunks while all of this ran");

        var survivors = new List<long>();
        for (var k = 1L; k <= (long)writers * perWriter; k++)
        {
            if (!removed.Contains(k))
            {
                survivors.Add(k);
            }
        }

        Assert.That(map.EntryCount, Is.EqualTo(survivors.Count));
        AssertEveryKeyFound(map, survivors);
    }

    [Test]
    public void TheAllocator_StaysAboveTheFloor()
    {
        var (_, segment, _) = CreateMap();
        var em = _serviceProvider.GetRequiredService<EpochManager>();
        using var guard = EpochGuard.Enter(em);
        var accessor = segment.CreateChunkAccessor();
        try
        {
            // A floor past the segment's end: every allocation must grow it rather than take a chunk below.
            var floor = segment.ChunkCapacity + 100;
            segment.RaiseAllocationFloor(floor);
            for (var i = 0; i < 500; i++)
            {
                Assert.That(segment.AllocateChunk(null, ref accessor), Is.GreaterThanOrEqualTo(floor));
            }

            // Below the floor a chunk is still taken by its position, once.
            Assert.That(segment.IsChunkAllocated(floor - 50), Is.False);
            Assert.That(segment.TryReserveChunk(floor - 50, null, ref accessor), Is.True);
            Assert.That(segment.TryReserveChunk(floor - 50, null, ref accessor), Is.False, "an allocated chunk is not taken twice");
        }
        finally
        {
            accessor.Dispose();
        }
    }

    /// <summary>
    /// Under a floor the allocator searches only from the floor up, and every free chunk above it is found before the segment grows — stale room bits on
    /// full pages included (a page's bit is a superset: set when it may have room). The chunks below the floor are the buckets', and stay untouched.
    /// </summary>
    [Test]
    public void EveryChunkAboveTheFloor_IsUsedBeforeGrowth()
    {
        var (_, segment, _) = CreateMap();
        var em = _serviceProvider.GetRequiredService<EpochManager>();
        using var guard = EpochGuard.Enter(em);
        var accessor = segment.CreateChunkAccessor();
        try
        {
            var floor = segment.AllocationFloor;
            Assert.That(floor, Is.GreaterThan(N0), "premise: the map keeps the allocator above its buckets");

            // Free chunks above the floor on pages already in use, among allocated ones.
            var taken = new List<int>();
            for (var i = 0; i < 120; i++)
            {
                taken.Add(segment.AllocateChunk(null, ref accessor));
            }

            for (var i = 0; i < taken.Count; i += 3)
            {
                segment.FreeChunk(taken[i]);
            }

            var length = segment.Length;
            var freeAboveFloor = 0;
            for (var c = floor; c < segment.ChunkCapacity; c++)
            {
                freeAboveFloor += segment.IsChunkAllocated(c) ? 0 : 1;
            }

            Assert.That(freeAboveFloor, Is.GreaterThan(100), "premise");

            // Every page marked — stale bits on full pages and below the floor — at the start and halfway: the search sorts them out and grows nothing.
            segment.MarkEveryPageForTest();
            for (var i = 0; i < freeAboveFloor; i++)
            {
                if (i == freeAboveFloor / 2)
                {
                    segment.MarkEveryPageForTest();
                }

                var chunk = segment.AllocateChunk(null, ref accessor);
                Assert.That(chunk, Is.GreaterThanOrEqualTo(floor));
                Assert.That(segment.Length, Is.EqualTo(length), $"allocation {i} of the {freeAboveFloor} free chunks above the floor grew the segment");
            }

            // Nothing above the floor is free now: the next allocation grows, and the chunks below the floor stay untouched.
            Assert.That(segment.AllocateChunk(null, ref accessor), Is.GreaterThanOrEqualTo(floor));
            Assert.That(segment.Length, Is.GreaterThan(length));
            Assert.That(segment.IsChunkAllocated(floor - 1), Is.False, "the runway below the floor is the buckets', not the allocator's");
        }
        finally
        {
            accessor.Dispose();
        }
    }
}
