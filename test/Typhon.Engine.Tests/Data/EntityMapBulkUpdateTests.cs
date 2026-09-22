using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// <c>UpdateValuesBulk</c> — the EntityMap's bulk value update (#872 step 7, §5.4).
/// </summary>
/// <remarks>
/// §5.4 calls the EntityMap the <b>least-analysed and most likely dominant</b> term in the re-clustering budget: <c>TryUpdateInPlace</c> already writes only
/// the bytes that change, but it still pays a hash, a bucket resolve, a directory lookup, a dirty-mark and a latch pair <i>per entity</i>. Sorting the batch
/// by bucket amortises all of those. What these tests protect is that the amortisation changes nothing observable — the same result as a
/// <c>TryUpdateInPlace</c> loop, the same result at any worker count, and a location-hint cache that is still telling the truth afterwards.
/// </remarks>
[TestFixture]
[NonParallelizable]
unsafe class EntityMapBulkUpdateTests
{
    private const int ValueSize = 8;

    private int _mapOrdinal;

    [SetUp]
    public void Setup() => _mapOrdinal = 0;

    /// <summary>
    /// A FRESH provider per map, not one shared by the fixture.
    /// </summary>
    /// <remarks>
    /// <c>ManagedPagedMMF</c> is registered scoped, so a second <see cref="WithMap"/> in one test would resolve the instance the first one disposed and
    /// <c>Create</c> would throw a bare <see cref="NullReferenceException"/> from inside the engine — which reads as an engine defect rather than as a test
    /// harness one. Several tests here need two independent maps to compare, so each gets its own provider and its own database name.
    /// </remarks>
    private ServiceProvider BuildProvider()
    {
        var name = $"embu{_mapOrdinal++}_{TestContext.CurrentContext.Test.Name}".Replace("(", "_").Replace(")", "_").Replace(",", "_");
        var serviceCollection = new ServiceCollection();
        serviceCollection
            .AddLogging()
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddScopedManagedPagedMemoryMappedFile(options =>
            {
                options.DatabaseName = name[..Math.Min(63, name.Length)];
                options.DatabaseCacheSize = (ulong)(PagedMMF.MinimumMemPageCount * PagedMMF.PageSize);
                options.PagesDebugPattern = true;
            });

        var provider = serviceCollection.BuildServiceProvider();
        provider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        return provider;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Batch entry + the two appliers it drives
    // ══════════════════════════════════════════════════════════════════════

    private struct LocUpdate
    {
        public long Key;
        public long Payload;
        public int Bucket;

        public LocUpdate(long key, long payload, int bucket)
        {
            Key = key;
            Payload = payload;
            Bucket = bucket;
        }
    }

    private struct LocApplier : IRawBulkUpdater<long, LocUpdate>
    {
        public long KeyOf(in LocUpdate entry) => entry.Key;

        public int BucketOf(in LocUpdate entry) => entry.Bucket;

        public void Update(ref LocUpdate entry, byte* valueBytes) => *(long*)valueBytes = entry.Payload;
    }

    /// <summary>
    /// The per-entity twin, so <c>AC-7.1</c> compares against the primitive the bulk path replaces rather than a re-derivation of it.
    /// </summary>
    private struct LocInPlace : IRawValueUpdater
    {
        public long Payload;

        public void Update(byte* valueBytes) => *(long*)valueBytes = Payload;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Harness
    // ══════════════════════════════════════════════════════════════════════

    private delegate void MapAction(RawValuePagedHashMap<long, PersistentStore> map, ChunkBasedSegment<PersistentStore> segment, EpochManager epochs,
        ref ChunkAccessor<PersistentStore> accessor);

    /// <summary>
    /// Builds a map holding <paramref name="count"/> entries keyed 1..count, each valued <c>key * 10</c>, then runs <paramref name="body"/>.
    /// </summary>
    private void WithMap(int count, MapAction body)
    {
        using var provider = BuildProvider();
        using var mpmmf = provider.GetRequiredService<ManagedPagedMMF>();
        using var epochs = provider.GetRequiredService<EpochManager>();
        var stride = RawValuePagedHashMap<long, PersistentStore>.RecommendedStride(ValueSize);
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 200, stride);
        var map = RawValuePagedHashMap<long, PersistentStore>.Create(segment, 4, ValueSize);

        var depth = epochs.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            try
            {
                long payload;
                for (var k = 1L; k <= count; k++)
                {
                    payload = k * 10;
                    map.Insert(k, (byte*)&payload, ref accessor, null);
                }

                body(map, segment, epochs, ref accessor);
            }
            finally
            {
                accessor.Dispose();
            }
        }
        finally
        {
            epochs.ExitScope(depth);
        }
    }

    private static long ReadValue(RawValuePagedHashMap<long, PersistentStore> map, long key, ref ChunkAccessor<PersistentStore> accessor)
    {
        long buf = 0;
        return map.TryGet(key, (byte*)&buf, ref accessor) ? buf : long.MinValue;
    }

    /// <summary>Builds a batch of <paramref name="keys"/> with fresh payloads, sorted by bucket exactly as a caller must.</summary>
    private static LocUpdate[] BuildSortedBatch(RawValuePagedHashMap<long, PersistentStore> map, IReadOnlyList<long> keys)
    {
        var batch = new LocUpdate[keys.Count];
        for (var i = 0; i < keys.Count; i++)
        {
            batch[i] = new LocUpdate(keys[i], keys[i] * 1000 + 7, map.BucketIndexOf(keys[i]));
        }

        // A stable sort, and it matters: two entries in one bucket must keep the order the caller gave them, or "last wins" on a duplicated key stops being
        // the same answer a TryUpdateInPlace loop gives.
        var buckets = new int[batch.Length];
        for (var i = 0; i < batch.Length; i++)
        {
            buckets[i] = batch[i].Bucket;
        }

        var order = new int[batch.Length];
        for (var i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        Array.Sort(order, (a, b) => buckets[a] != buckets[b] ? buckets[a].CompareTo(buckets[b]) : a.CompareTo(b));

        var sorted = new LocUpdate[batch.Length];
        for (var i = 0; i < order.Length; i++)
        {
            sorted[i] = batch[order[i]];
        }

        return sorted;
    }

    // ══════════════════════════════════════════════════════════════════════
    // AC-7.1 — identical to the loop it replaces
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(15_000)]
    public void BulkUpdate_MatchesTryUpdateInPlaceLoop()
    {
        const int Count = 2_000;

        // Reference pass: the per-entity primitive, whose behaviour is the definition of correct here.
        var expected = new Dictionary<long, long>();
        WithMap(Count, (RawValuePagedHashMap<long, PersistentStore> map, ChunkBasedSegment<PersistentStore> segment, EpochManager epochs,
            ref ChunkAccessor<PersistentStore> accessor) =>
        {
            var keys = SelectKeys(Count, every: 3);
            var batch = BuildSortedBatch(map, keys);
            for (var i = 0; i < batch.Length; i++)
            {
                var updater = new LocInPlace { Payload = batch[i].Payload };
                map.TryUpdateInPlace(batch[i].Key, ref updater, ref accessor);
            }

            for (var k = 1L; k <= Count; k++)
            {
                expected[k] = ReadValue(map, k, ref accessor);
            }
        });

        WithMap(Count, (RawValuePagedHashMap<long, PersistentStore> map, ChunkBasedSegment<PersistentStore> segment, EpochManager epochs,
            ref ChunkAccessor<PersistentStore> accessor) =>
        {
            var keys = SelectKeys(Count, every: 3);
            var batch = BuildSortedBatch(map, keys);
            var applied = map.UpdateValuesBulk<LocUpdate, LocApplier>(batch, ref accessor);

            Assert.That(applied, Is.EqualTo(batch.Length), "every batch key exists in the map, so every one must have been applied");

            for (var k = 1L; k <= Count; k++)
            {
                Assert.That(ReadValue(map, k, ref accessor), Is.EqualTo(expected[k]),
                    $"key {k} must hold exactly what the TryUpdateInPlace loop left there — touched or untouched");
            }
        });
    }

    private static List<long> SelectKeys(int count, int every)
    {
        var keys = new List<long>();
        for (var k = 1L; k <= count; k += every)
        {
            keys.Add(k);
        }

        return keys;
    }

    // ══════════════════════════════════════════════════════════════════════
    // AC-7.2 — the location-hint cache still tells the truth
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(15_000)]
    public void BulkUpdate_LeavesHintCacheValid()
    {
        // A hint is a packed (chunkId, index) — WHERE an entry lives, not what it holds — so a value-only update should leave every one of them correct. The
        // design says to assert that rather than assume it, "because a stale hint that survives here is a silent wrong-address read".
        const int Count = 1_500;

        WithMap(Count, (RawValuePagedHashMap<long, PersistentStore> map, ChunkBasedSegment<PersistentStore> segment, EpochManager epochs,
            ref ChunkAccessor<PersistentStore> accessor) =>
        {
            var keys = SelectKeys(Count, every: 2);

            // Warm the cache: hints are only stored by the hint-aware read path, on root-chunk hits.
            long scratch = 0;
            for (var i = 0; i < keys.Count; i++)
            {
                map.TryGetWithHint(keys[i], (byte*)&scratch, ref accessor);
            }

            var before = new long[keys.Count];
            var warmed = 0;
            for (var i = 0; i < keys.Count; i++)
            {
                before[i] = map.HintSlotForTest(keys[i]);
                if (before[i] != 0)
                {
                    warmed++;
                }
            }

            // Not `> 0`: one warmed hint out of 750 satisfies that while leaving the comparison below almost entirely about the fallback path.
            Assert.That(warmed, Is.GreaterThan(keys.Count / 2),
                $"only {warmed} of {keys.Count} hints were stored, so this is mostly exercising the full-lookup fallback rather than hint survival");

            var batch = BuildSortedBatch(map, keys);
            map.UpdateValuesBulk<LocUpdate, LocApplier>(batch, ref accessor);

            for (var i = 0; i < keys.Count; i++)
            {
                // The slot itself, not just the answer: an implementation that cleared the cache would still agree with TryGet, because the fallback would
                // cover for it. This distinguishes "the hint survived" from "the hint was thrown away".
                Assert.That(map.HintSlotForTest(keys[i]), Is.EqualTo(before[i]),
                    $"key {keys[i]}'s hint moved, but a value-only update relocates nothing");

                long hinted = 0;
                long full = 0;
                var hitHint = map.TryGetWithHint(keys[i], (byte*)&hinted, ref accessor);
                var hitFull = map.TryGet(keys[i], (byte*)&full, ref accessor);

                Assert.That(hitHint, Is.EqualTo(hitFull), $"key {keys[i]}: the hinted and full lookups disagree on existence");
                Assert.That(hinted, Is.EqualTo(full), $"key {keys[i]}: the hinted lookup returned a different record than the full one");
                Assert.That(hinted, Is.EqualTo(keys[i] * 1000 + 7), $"key {keys[i]}: the hinted lookup returned a stale value");
            }
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    // AC-7.3 — partitioning, disjointness, and W-independence
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(15_000)]
    public void Partition_RunsAreNeverSplitAndCoverTheBatch()
    {
        const int Count = 4_000;

        WithMap(Count, (RawValuePagedHashMap<long, PersistentStore> map, ChunkBasedSegment<PersistentStore> segment, EpochManager epochs,
            ref ChunkAccessor<PersistentStore> accessor) =>
        {
            var batch = BuildSortedBatch(map, SelectKeys(Count, every: 1));
            var boundaries = new int[17];
            var parts = map.PartitionByBucketRuns<LocUpdate, LocApplier>(batch, 16, boundaries);

            Assert.That(parts, Is.GreaterThan(0));
            Assert.That(boundaries[0], Is.Zero);
            Assert.That(boundaries[parts], Is.EqualTo(batch.Length), "the parts must cover the whole batch");

            var seenBuckets = new HashSet<int>();
            for (var p = 0; p < parts; p++)
            {
                Assert.That(boundaries[p], Is.LessThan(boundaries[p + 1]), $"part {p} is empty, which only costs a dispatch");

                // The property the whole exclusivity argument rests on: a bucket belongs to exactly one part, so two workers can never be inside one
                // bucket chunk. Checked against BucketIndexOf rather than against the partitioner's own arithmetic.
                var partBuckets = new HashSet<int>();
                for (var i = boundaries[p]; i < boundaries[p + 1]; i++)
                {
                    partBuckets.Add(map.BucketIndexOf(batch[i].Key));
                }

                foreach (var b in partBuckets)
                {
                    Assert.That(seenBuckets.Add(b), Is.True, $"bucket {b} appears in part {p} and in an earlier part — the run was split");
                }
            }
        });
    }

    [Test]
    [CancelAfter(15_000)]
    public void Partition_Degenerate_EmptyBatchSinglePartAndOversizedRequest()
    {
        WithMap(64, (RawValuePagedHashMap<long, PersistentStore> map, ChunkBasedSegment<PersistentStore> segment, EpochManager epochs,
            ref ChunkAccessor<PersistentStore> accessor) =>
        {
            var boundaries = new int[65];

            Assert.That(map.PartitionByBucketRuns<LocUpdate, LocApplier>(ReadOnlySpan<LocUpdate>.Empty, 8, boundaries), Is.Zero,
                "an empty batch must produce no parts at all, not one empty part");

            var batch = BuildSortedBatch(map, SelectKeys(64, every: 1));
            Assert.That(map.PartitionByBucketRuns<LocUpdate, LocApplier>(batch, 1, boundaries), Is.EqualTo(1));
            Assert.That(boundaries[1], Is.EqualTo(batch.Length));

            // More parts requested than there are buckets to give out: the extras must collapse, never appear as empty parts.
            var many = map.PartitionByBucketRuns<LocUpdate, LocApplier>(batch, 64, boundaries);
            Assert.That(boundaries[many], Is.EqualTo(batch.Length));
            for (var p = 0; p < many; p++)
            {
                Assert.That(boundaries[p], Is.LessThan(boundaries[p + 1]), $"part {p} came back empty");
            }
        });
    }

    [Test]
    [CancelAfter(15_000)]
    [Category("Sensitive")]
    public void BulkUpdate_IdenticalAcrossWorkerCounts()
    {
        const int Count = 3_000;
        var results = new Dictionary<int, Dictionary<long, long>>();

        foreach (var w in new[] { 1, 2, 8 })
        {
            var workers = w;
            var snapshot = new Dictionary<long, long>();
            WithMap(Count, (RawValuePagedHashMap<long, PersistentStore> map, ChunkBasedSegment<PersistentStore> segment, EpochManager epochs,
                ref ChunkAccessor<PersistentStore> accessor) =>
            {
                var batch = BuildSortedBatch(map, SelectKeys(Count, every: 1));
                var boundaries = new int[workers + 1];
                var parts = map.PartitionByBucketRuns<LocUpdate, LocApplier>(batch, workers, boundaries);

                var applied = 0;
                var tasks = new Task[parts];
                for (var p = 0; p < parts; p++)
                {
                    var lo = boundaries[p];
                    var hi = boundaries[p + 1];
                    tasks[p] = Task.Run(() =>
                    {
                        // Its own epoch pin and accessor: EpochManager pins the CALLING thread and ChunkAccessor asserts the caller is pinned.
                        var depth = epochs.EnterScope();
                        var partAccessor = segment.CreateChunkAccessor();
                        try
                        {
                            Interlocked.Add(ref applied, map.UpdateValuesBulk<LocUpdate, LocApplier>(batch.AsSpan(lo, hi - lo), ref partAccessor));
                        }
                        finally
                        {
                            partAccessor.Dispose();
                            epochs.ExitScope(depth);
                        }
                    });
                }

                Assert.That(Task.WaitAll(tasks, TimeSpan.FromSeconds(10)), Is.True, "the workers must finish — a held bucket latch shows up here first");
                Assert.That(Volatile.Read(ref applied), Is.EqualTo(batch.Length), $"W={workers}: every batch entry must be applied exactly once");

                for (var k = 1L; k <= Count; k++)
                {
                    snapshot[k] = ReadValue(map, k, ref accessor);
                }
            });

            results[w] = snapshot;
        }

        foreach (var w in new[] { 2, 8 })
        {
            for (var k = 1L; k <= Count; k++)
            {
                Assert.That(results[w][k], Is.EqualTo(results[1][k]),
                    $"key {k} differs between W=1 and W={w} — the result must be a function of the batch, never of how it was split");
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // AC-7.4 — zero allocation
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(15_000)]
    public void BulkUpdate_AllocatesNothing()
    {
        const int Count = 1_000;

        WithMap(Count, (RawValuePagedHashMap<long, PersistentStore> map, ChunkBasedSegment<PersistentStore> segment, EpochManager epochs,
            ref ChunkAccessor<PersistentStore> accessor) =>
        {
            var batch = BuildSortedBatch(map, SelectKeys(Count, every: 1));

            // Warm first: the measured call must not be the one that JITs the generic instantiation.
            map.UpdateValuesBulk<LocUpdate, LocApplier>(batch, ref accessor);

            var before = GC.GetAllocatedBytesForCurrentThread();
            map.UpdateValuesBulk<LocUpdate, LocApplier>(batch, ref accessor);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.That(allocated, Is.Zero,
                $"the apply runs once per migrant per tick, so anything it allocates is multiplied by 58 000 — got {allocated} bytes");
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    // AC-7.6 — shared buckets, overflow chains, absent keys
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(15_000)]
    public void BulkUpdate_TwoEntriesSharingOneBucket_BothApplied()
    {
        const int Count = 512;

        WithMap(Count, (RawValuePagedHashMap<long, PersistentStore> map, ChunkBasedSegment<PersistentStore> segment, EpochManager epochs,
            ref ChunkAccessor<PersistentStore> accessor) =>
        {
            // Find a bucket that genuinely holds two of our keys, rather than hoping the batch happens to contain one.
            var byBucket = new Dictionary<int, List<long>>();
            for (var k = 1L; k <= Count; k++)
            {
                var b = map.BucketIndexOf(k);
                if (!byBucket.TryGetValue(b, out var list))
                {
                    list = [];
                    byBucket[b] = list;
                }

                list.Add(k);
            }

            List<long> shared = null;
            foreach (var pair in byBucket)
            {
                if (pair.Value.Count >= 2)
                {
                    shared = pair.Value;
                    break;
                }
            }

            Assert.That(shared, Is.Not.Null, "no bucket holds two keys, so this test cannot exercise a shared-bucket run");

            var batch = BuildSortedBatch(map, shared);
            var applied = map.UpdateValuesBulk<LocUpdate, LocApplier>(batch, ref accessor);

            Assert.That(applied, Is.EqualTo(batch.Length), "every entry of a single bucket's run must be applied, not just the first one found");
            for (var i = 0; i < batch.Length; i++)
            {
                Assert.That(ReadValue(map, batch[i].Key, ref accessor), Is.EqualTo(batch[i].Payload),
                    $"key {batch[i].Key} in the shared bucket was not written");
            }
        });
    }

    [Test]
    [CancelAfter(15_000)]
    public void BulkUpdate_BucketChainSpanningOverflowChunks_UpdatesEntriesPastTheRoot()
    {
        // The failure this guards is a chain walk that stops at the root chunk: every key in the root updates, every key past it silently does not, and the
        // return count is the only tell. Constructed rather than hoped for — the test fails loudly if no overflow chain exists to exercise.
        const int Count = 20_000;

        WithMap(Count, (RawValuePagedHashMap<long, PersistentStore> map, ChunkBasedSegment<PersistentStore> segment, EpochManager epochs,
            ref ChunkAccessor<PersistentStore> accessor) =>
        {
            List<long> chained = null;
            for (var k = 1L; k <= Count && chained == null; k++)
            {
                var bucket = map.BucketIndexOf(k);
                var rootChunkId = map.GetBucketChunkIdForTest(bucket, ref accessor);
                if (RawValuePagedHashMap<long, PersistentStore>.BucketOverflowChunkIdForTest(rootChunkId, ref accessor) == -1)
                {
                    continue;
                }

                chained = [];
                for (var j = 1L; j <= Count; j++)
                {
                    if (map.BucketIndexOf(j) == bucket)
                    {
                        chained.Add(j);
                    }
                }
            }

            Assert.That(chained, Is.Not.Null, "no bucket in the map has an overflow chunk, so AC-7.6's overflow half was not exercised");
            Assert.That(chained, Has.Count.GreaterThan(map.BucketCapacity), "the chained bucket must hold more entries than one chunk can");

            var batch = BuildSortedBatch(map, chained);
            var applied = map.UpdateValuesBulk<LocUpdate, LocApplier>(batch, ref accessor);

            Assert.That(applied, Is.EqualTo(batch.Length), "entries past the root chunk must be updated too — a root-only walk shows up exactly here");
            for (var i = 0; i < batch.Length; i++)
            {
                Assert.That(ReadValue(map, batch[i].Key, ref accessor), Is.EqualTo(batch[i].Payload), $"key {batch[i].Key} was not written");
            }
        });
    }

    [Test]
    [CancelAfter(15_000)]
    public void BulkUpdate_SameKeyTwiceInOneBatch_LastWinsLikeTheLoop()
    {
        // AC-7.1 says "identical to a TryUpdateInPlace loop", and a batch carrying one key twice is where the two implementations can legitimately diverge:
        // a loop applies both in order and reports two successes, whereas a bulk apply that stopped at the first match per chain entry would silently be
        // FIRST-wins and drop the second. Nothing else in this fixture distinguishes them, so without this test the choice is unverified.
        const int Count = 128;

        WithMap(Count, (RawValuePagedHashMap<long, PersistentStore> map, ChunkBasedSegment<PersistentStore> segment, EpochManager epochs,
            ref ChunkAccessor<PersistentStore> accessor) =>
        {
            // Same key twice, adjacent (they share a bucket by definition, so a bucket sort keeps them together and stable).
            var bucket = map.BucketIndexOf(42L);
            var batch = new[] { new LocUpdate(42L, 111L, bucket), new LocUpdate(42L, 222L, bucket) };

            var applied = map.UpdateValuesBulk<LocUpdate, LocApplier>(batch, ref accessor);

            Assert.That(applied, Is.EqualTo(2), "a loop would report two successes for two entries, even though they address one slot");
            Assert.That(ReadValue(map, 42L, ref accessor), Is.EqualTo(222L), "the LAST entry must win, as it would in a TryUpdateInPlace loop");
        });
    }

    [Test]
    [CancelAfter(15_000)]
    public void BulkUpdate_KeyAbsentFromTheMap_IsSkippedAndNotCounted()
    {
        const int Count = 256;

        WithMap(Count, (RawValuePagedHashMap<long, PersistentStore> map, ChunkBasedSegment<PersistentStore> segment, EpochManager epochs,
            ref ChunkAccessor<PersistentStore> accessor) =>
        {
            var keys = SelectKeys(Count, every: 4);
            keys.Add(Count + 5_000);   // never inserted — migration's destroyed-in-flight case
            var batch = BuildSortedBatch(map, keys);

            var applied = map.UpdateValuesBulk<LocUpdate, LocApplier>(batch, ref accessor);

            Assert.That(applied, Is.EqualTo(batch.Length - 1),
                "an absent key is skipped rather than an error, and the shortfall in the count is how a caller detects it");
            Assert.That(ReadValue(map, Count + 5_000, ref accessor), Is.EqualTo(long.MinValue), "the absent key must not have been created");
        });
    }
}
