using System;
using System.IO;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// A writer never allocates storage while it holds a node or bucket latch (IXW-07): the chunks a B+Tree split or an entity-map append and split write under
/// their latches are reserved before the latches are taken (<see cref="ChunkReservation{TStore}"/>).
/// </summary>
/// <remarks>
/// <para>
/// Two properties, asserted separately. <b>Nothing allocates under a latch</b>: counted, over workloads that split and chain hundreds of times. <b>A failed
/// allocation leaves nothing latched</b>: an injected allocation fault, then a full scan and further writes that a latch left held would make spin forever
/// (the entity map) or exhaust the retry budget (the B+Tree, IXW-01). That is what MarketHardeningTests hit: a storm frozen at 175 623 operations on a bucket
/// nobody would unlock, and two workers reporting a "liveness defect in the tree" while a split waited in its allocation.
/// </para>
/// </remarks>
[TestFixture]
unsafe class LatchFreeAllocationTests : TestBase<LatchFreeAllocationTests>
{
    private const int ValueSize = 26;

    [TearDown]
    public void ClearFault() => ChunkBasedSegment<PersistentStore>.AllocateFaultForTest = null;

    private static void FailAllocationsOf(object segment)
        => ChunkBasedSegment<PersistentStore>.AllocateFaultForTest = s =>
        {
            if (ReferenceEquals(s, segment))
            {
                throw new IOException("injected allocation fault");
            }
        };

    /// <summary>
    /// Runs <paramref name="work"/> on its own thread and fails if it has not finished in 15 s — a latch left held makes it spin, not throw.
    /// </summary>
    private static void CompletesInTime(string what, Action work)
    {
        Exception failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                work();
            }
            catch (Exception e)
            {
                failure = e;
            }
        }) { IsBackground = true, Name = "latch-check" };
        thread.Start();
        Assert.That(thread.Join(TimeSpan.FromSeconds(15)), Is.True, $"{what} did not finish: a latch was left held");
        Assert.That(failure, Is.Null, $"{what} failed: {failure}");
    }

    private struct AnyEntry : RawValuePagedHashMap<long, PersistentStore>.IEntryPredicate<long>
    {
        public bool Matches(long key, byte* value) => true;
    }

    private (DatabaseEngine dbe, ChunkBasedSegment<PersistentStore> segment, RawValuePagedHashMap<long, PersistentStore> map) NewMap()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var stride = RawValuePagedHashMap<long, PersistentStore>.RecommendedStride(ValueSize);
        using var guard = EpochGuard.Enter(dbe.EpochManager);
        var segment = dbe.MMF.AllocateChunkBasedSegment(PageBlockType.None, 10, stride);
        var map = RawValuePagedHashMap<long, PersistentStore>.Create(segment, 16, ValueSize);
        return (dbe, segment, map);
    }

    private static void Insert(DatabaseEngine dbe, ChunkBasedSegment<PersistentStore> segment, RawValuePagedHashMap<long, PersistentStore> map, long key)
    {
        var record = stackalloc byte[ValueSize];
        *(long*)record = key;
        using var guard = EpochGuard.Enter(dbe.EpochManager);
        var accessor = segment.CreateChunkAccessor();
        try
        {
            map.Insert(key, record, ref accessor, null);
        }
        finally
        {
            accessor.Dispose();
        }
    }

    private static int CountAll(DatabaseEngine dbe, ChunkBasedSegment<PersistentStore> segment, RawValuePagedHashMap<long, PersistentStore> map)
    {
        using var guard = EpochGuard.Enter(dbe.EpochManager);
        var accessor = segment.CreateScanAccessor();
        try
        {
            var all = new AnyEntry();
            return map.CountEntries(ref accessor, ref all);
        }
        finally
        {
            accessor.Dispose();
        }
    }

    [Test]
    [CancelAfter(30_000)]
    [VerifiesRule("IXW-07")]
    public void AnEntityMapThatChainsAndSplits_NeverAllocatesUnderABucketLock()
    {
        var (dbe, segment, map) = NewMap();
        var before = ChunkReservation<PersistentStore>.UnreservedAllocations;

        for (long k = 1; k <= 20_000; k++)
        {
            Insert(dbe, segment, map, k);
        }

        Assert.That(map._splitCount, Is.GreaterThan(100), "premise: the map split");
        Assert.That(map._overflowChunksChained, Is.GreaterThan(0), "premise: buckets chained overflow chunks");
        Assert.That(ChunkReservation<PersistentStore>.UnreservedAllocations - before, Is.Zero, "an append or a split allocated under a bucket lock");
        Assert.That(CountAll(dbe, segment, map), Is.EqualTo(20_000));
    }

    /// <summary>
    /// A split whose exact need passes the reservation's initial 64 chunks takes them all from the reservation. Every key hashes to bucket 0 until the map
    /// passes 4 096 buckets, so bucket 0's chain grows to hundreds of chunks and each split of it rewrites them all; a reservation capped at 64 allocated
    /// the rest under the bucket lock, mid-rewrite, where a fault lost the half already moved.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    [VerifiesRule("IXW-07")]
    public void ASplitOfAChainLongerThanTheInitialReservation_AllocatesNothingUnderItsLock()
    {
        var (dbe, segment, map) = NewMap();
        var before = ChunkReservation<PersistentStore>.UnreservedAllocations;

        var inserted = 0;
        for (long k = 1; inserted < 2_000; k++)
        {
            if ((RawValuePagedHashMap<long, PersistentStore>.ComputeHashForTest(k) & 0xFFF) == 0)
            {
                Insert(dbe, segment, map, k);
                inserted++;
            }
        }

        Assert.That(map._splitCount, Is.GreaterThan(0), "premise: the map split");
        Assert.That(map._overflowChunksChained, Is.GreaterThan(2 * ChunkReservation<PersistentStore>.InitialCapacity),
            "premise: one bucket's chain outgrew the initial reservation");
        Assert.That(ChunkReservation<PersistentStore>.UnreservedAllocations - before, Is.Zero, "a split allocated under its bucket lock");
        Assert.That(CountAll(dbe, segment, map), Is.EqualTo(2_000));
    }

    private struct BulkEntry
    {
        public long Key;
        public int Bucket;
    }

    /// <summary>Writes the key as the value, and throws on the <see cref="FailAt"/>-th value it writes: the caller's code, run under the bucket lock.</summary>
    private struct FaultingInserter : IRawBulkInserter<long, BulkEntry>
    {
        [ThreadStatic] internal static int Written;
        [ThreadStatic] internal static int FailAt;

        public long KeyOf(in BulkEntry entry) => entry.Key;

        public int BucketOf(in BulkEntry entry) => entry.Bucket;

        public void WriteValue(in BulkEntry entry, Span<byte> destination)
        {
            if (++Written == FailAt)
            {
                throw new IOException("injected write fault");
            }

            destination.Clear();
            System.Runtime.InteropServices.MemoryMarshal.Write(destination, in entry.Key);
        }
    }

    /// <summary>
    /// A fault in a bulk insert's write phase — the caller's <c>WriteValue</c>, run under the bucket lock — releases the lock, and counts the entries it
    /// had already written: a scan and further writes complete, and the map's count matches what its buckets hold.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("IXW-07")]
    public void AFaultInABulkInsertsWritePhase_LeavesNoBucketLocked()
    {
        var (dbe, segment, map) = NewMap();
        for (long k = 1; k <= 2_000; k++)
        {
            Insert(dbe, segment, map, k);
        }

        const int batchSize = 1_000;
        var batch = new BulkEntry[batchSize];
        using (EpochGuard.Enter(dbe.EpochManager))
        {
            map.AdvanceHashStateFor(batchSize);
            for (var i = 0; i < batchSize; i++)
            {
                var key = 10_000L + i;
                batch[i] = new BulkEntry { Key = key, Bucket = map.BucketIndexOf(key) };
            }
        }

        Array.Sort(batch, (a, b) => a.Bucket.CompareTo(b.Bucket));
        FaultingInserter.Written = 0;
        FaultingInserter.FailAt = batchSize / 2;
        var faulted = false;
        using (EpochGuard.Enter(dbe.EpochManager))
        {
            var accessor = segment.CreateChunkAccessor();
            try
            {
                map.InsertNewBulk<BulkEntry, FaultingInserter>(batch, ref accessor, null);
            }
            catch (IOException e) when (e.Message == "injected write fault")
            {
                faulted = true;
            }
            finally
            {
                accessor.Dispose();
            }
        }

        Assert.That(faulted, Is.True, "premise: the write phase faulted");
        CompletesInTime("a full scan and more inserts after the fault", () =>
        {
            Assert.That(CountAll(dbe, segment, map), Is.EqualTo(map.EntryCount), "the map counts exactly the entries its buckets hold");
            for (var k = 300_000L; k < 302_000; k++)
            {
                Insert(dbe, segment, map, k);
            }

            Assert.That(CountAll(dbe, segment, map), Is.EqualTo(map.EntryCount));
        });
    }

    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("IXW-07")]
    public void AnAllocationFault_DuringEntityMapWrites_LeavesNoBucketLocked()
    {
        var (dbe, segment, map) = NewMap();
        for (long k = 1; k <= 2_000; k++)
        {
            Insert(dbe, segment, map, k);
        }

        FailAllocationsOf(segment);
        var key = 2_001L;
        var faulted = false;
        for (; key <= 200_000 && !faulted; key++)
        {
            try
            {
                Insert(dbe, segment, map, key);
            }
            catch (IOException e) when (e.Message == "injected allocation fault")
            {
                faulted = true;
            }
        }

        ChunkBasedSegment<PersistentStore>.AllocateFaultForTest = null;
        Assert.That(faulted, Is.True, "premise: a write needed an allocation");

        CompletesInTime("a full scan and more inserts after the fault", () =>
        {
            CountAll(dbe, segment, map);
            for (var k = 300_000L; k < 302_000; k++)
            {
                Insert(dbe, segment, map, k);
            }

            CountAll(dbe, segment, map);
        });
    }

    private (DatabaseEngine dbe, BTree<int, PersistentStore> tree) NewTree()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<QsFat>();
        dbe.InitializeArchetypes();
        var tree = (BTree<int, PersistentStore>)dbe._archetypeStates[Archetype<QsFatArch>.Metadata.ArchetypeId].ClusterState.IndexSlots[0].Fields[0].Index;
        return (dbe, tree);
    }

    private static void Add(DatabaseEngine dbe, BTree<int, PersistentStore> tree, int key)
    {
        using var guard = EpochGuard.Enter(dbe.EpochManager);
        var accessor = tree.Segment.CreateChunkAccessor(null);
        try
        {
            tree.Add(key, key + 1, ref accessor, out _);
        }
        finally
        {
            accessor.Dispose();
        }
    }

    private static int CountLeaves(DatabaseEngine dbe, BTree<int, PersistentStore> tree)
    {
        using var guard = EpochGuard.Enter(dbe.EpochManager);
        var n = 0;
        foreach (var _ in tree.EnumerateLeaves())
        {
            n++;
        }

        return n;
    }

    [Test]
    [CancelAfter(30_000)]
    [VerifiesRule("IXW-07")]
    public void ABTreeThatSplits_NeverAllocatesUnderItsLatches()
    {
        var (dbe, tree) = NewTree();
        Add(dbe, tree, 0);   // the first root is allocated before any latch exists
        var before = ChunkReservation<PersistentStore>.UnreservedAllocations;
        var splitsBefore = tree.SplitCount;

        for (var k = 1; k <= 20_000; k++)
        {
            Add(dbe, tree, k);
        }

        Assert.That(tree.SplitCount - splitsBefore, Is.GreaterThan(50), "premise: the tree split");
        Assert.That(tree.Height, Is.GreaterThan(1), "premise: splits propagated");
        Assert.That(ChunkReservation<PersistentStore>.UnreservedAllocations - before, Is.Zero, "a split allocated under its latches");
        Assert.That(CountLeaves(dbe, tree), Is.EqualTo(20_001));
    }

    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("IXW-07")]
    public void AnAllocationFault_DuringABTreeSplit_LeavesNoNodeLatched()
    {
        var (dbe, tree) = NewTree();
        for (var k = 0; k < 1_000; k++)
        {
            Add(dbe, tree, k);
        }

        FailAllocationsOf(tree.Segment);
        var key = 1_000;
        var faulted = false;
        for (; key < 100_000; key++)
        {
            try
            {
                Add(dbe, tree, key);
            }
            catch (IOException e) when (e.Message == "injected allocation fault")
            {
                faulted = true;
                break;
            }
        }

        ChunkBasedSegment<PersistentStore>.AllocateFaultForTest = null;
        Assert.That(faulted, Is.True, "premise: an insert needed an allocation");

        var resumeAt = key;
        CompletesInTime("inserts and a full scan after the fault", () =>
        {
            for (var k = resumeAt; k < resumeAt + 5_000; k++)
            {
                Add(dbe, tree, k);
            }
        });
        Assert.That(CountLeaves(dbe, tree), Is.EqualTo(resumeAt + 5_000), "every key, the faulted one included, is in the tree");
    }
}
