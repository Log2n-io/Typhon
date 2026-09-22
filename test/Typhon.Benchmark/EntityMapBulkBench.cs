using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace Typhon.Benchmark;

/// <summary>
/// <c>AC-7.5</c> — what an EntityMap location update actually costs, per entity (#872 step 7, §5.4).
/// </summary>
/// <remarks>
/// <para>
/// <b>The number IS the deliverable.</b> §5.4 calls the EntityMap the least-analysed and most likely dominant term in the re-clustering budget, and §9 Q4's
/// whole budget-versus-tightness exchange rate hangs off this figure. So the gate here is "ns/entity measured", not a speedup threshold — a small win
/// reported honestly is a valid outcome and a large one is not assumed.
/// </para>
/// <para>
/// <b>The sort is reported on its own line.</b> Step 6's lesson, paid for: a serial prepare folded into a phase total is how that phase ended up 88 % serial
/// with nobody noticing until the scaling curve refused to move. The bulk path's amortisation is bought with a sort the per-entity loop does not pay, so a
/// total that hides it can show a win the caller will never see.
/// </para>
/// <para>
/// <b>Two batch shapes, because they partition differently.</b> The CLUSTERED shape is migration's: keys dense in a small set of buckets, where a bucket's
/// run cannot be split across workers and the partition is therefore lumpy by construction. The UNIFORM shape spreads across the whole map. Reporting both,
/// with the imbalance, is the alternative to tuning toward whichever one flatters the design.
/// </para>
/// </remarks>
internal static unsafe class EntityMapBulkBench
{
    private const int ValueSize = 8;

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

    private struct LocInPlace : IRawValueUpdater
    {
        public long Payload;

        public void Update(byte* valueBytes) => *(long*)valueBytes = Payload;
    }

    public static void Run(int mapSize = 400_000, int[] batchSizes = null)
    {
        batchSizes ??= [10_000, 50_000, 100_000];

        Console.WriteLine();
        Console.WriteLine($"EntityMap bulk location update — map of {mapSize:N0} entries (#872 step 7, AC-7.5)");
        Console.WriteLine();

        var sc = new ServiceCollection();
        sc.AddLogging()
          .AddResourceRegistry()
          .AddMemoryAllocator()
          .AddEpochManager()
          .AddScopedManagedPagedMemoryMappedFile(options =>
          {
              options.DatabaseName = "embulk_bench";
              options.DatabaseCacheSize = 1UL << 30;
          });

        using var sp = sc.BuildServiceProvider();
        sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
        using var pmmf = sp.GetRequiredService<ManagedPagedMMF>();
        using var epochs = sp.GetRequiredService<EpochManager>();

        var stride = RawValuePagedHashMap<long, PersistentStore>.RecommendedStride(ValueSize);
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 3_000, stride);
        var map = RawValuePagedHashMap<long, PersistentStore>.Create(segment, 256, ValueSize);

        var depth = epochs.EnterScope();
        var accessor = segment.CreateChunkAccessor();
        try
        {
            long payload;
            for (var k = 1L; k <= mapSize; k++)
            {
                payload = k * 10;
                map.Insert(k, (byte*)&payload, ref accessor, null);
            }

            Console.WriteLine($"  map built: {map.EntryCount:N0} entries, bucket capacity {map.BucketCapacity}");
            Console.WriteLine();

            // Every buffer allocated ONCE, up front, before any measurement.
            //
            // Allocating them per measurement made the LAST row measured come out at ~40 ns/entity for the sort against ~125 for every other row — and a
            // control run with the two shapes swapped moved the anomaly to the new last row, so it followed POSITION, not shape. That is a harness artifact:
            // a 1.6 MB LocUpdate[] is an LOH allocation, and the rows measured earlier are paying for GC work that the final one, with nothing allocated
            // after it, escapes. Hoisting the buffers and settling the GC before each timed body removes the contamination rather than reporting through it.
            var scratch = new Dictionary<int, (LocUpdate[] Unsorted, LocUpdate[] Sorted, int[] Buckets)>();
            foreach (var batchSize in batchSizes)
            {
                scratch[batchSize] = (new LocUpdate[batchSize], new LocUpdate[batchSize], new int[batchSize]);
            }

            foreach (var shape in new[] { "clustered", "uniform" })
            {
                foreach (var batchSize in batchSizes)
                {
                    Measure(map, segment, epochs, ref accessor, mapSize, batchSize, shape, scratch[batchSize]);
                }
            }
        }
        finally
        {
            accessor.Dispose();
            epochs.ExitScope(depth);
        }

        Console.WriteLine();
        Console.WriteLine("  loop ns    = TryUpdateInPlace, one call per entity — the cost today.");
        Console.WriteLine("  sort ns    = building the bucket-sorted batch, per entity. The bulk path pays it and the loop does not, so it is NOT folded in.");
        Console.WriteLine("  bulk ns    = UpdateValuesBulk single-threaded, apply only.");
        Console.WriteLine("  total ns   = sort + bulk. This is the honest comparison against `loop ns`.");
        Console.WriteLine("  W=n ns     = apply only, WALL CLOCK from first worker start to last worker finish, not summed CPU.");
        Console.WriteLine("  imbalance  = largest part / mean part. A bucket's run cannot be split, so a clustered batch is lumpy by construction.");
    }

    private static void Measure(RawValuePagedHashMap<long, PersistentStore> map, ChunkBasedSegment<PersistentStore> segment, EpochManager epochs,
        ref ChunkAccessor<PersistentStore> accessor, int mapSize, int batchSize, string shape,
        (LocUpdate[] Unsorted, LocUpdate[] Sorted, int[] Buckets) scratch)
    {
        var keys = BuildKeys(mapSize, batchSize, shape);

        // ── The per-entity loop, which is what this replaces ──────────────────────────
        var unsorted = scratch.Unsorted;
        for (var i = 0; i < keys.Length; i++)
        {
            unsorted[i] = new LocUpdate(keys[i], keys[i] * 1000 + 7, map.BucketIndexOf(keys[i]));
        }

        var loopUs = BestOf(3, (ref ChunkAccessor<PersistentStore> a) =>
        {
            for (var i = 0; i < unsorted.Length; i++)
            {
                var updater = new LocInPlace { Payload = unsorted[i].Payload };
                map.TryUpdateInPlace(unsorted[i].Key, ref updater, ref a);
            }
        }, ref accessor);

        // ── The sort, on its own ─────────────────────────────────────────────────────
        var sorted = scratch.Sorted;
        var bucketScratch = scratch.Buckets;
        var sortUs = BestOf(3, (ref ChunkAccessor<PersistentStore> a) => SortByBucket(map, unsorted, sorted, bucketScratch), ref accessor);

        // ── The bulk apply, single-threaded ──────────────────────────────────────────
        var bulkUs = BestOf(3, (ref ChunkAccessor<PersistentStore> a) => map.UpdateValuesBulk<LocUpdate, LocApplier>(sorted, ref a), ref accessor);

        Console.WriteLine($"  {shape,-9} n={batchSize,-7:N0}  loop {1000.0 * loopUs / batchSize,6:F1} ns   sort {1000.0 * sortUs / batchSize,6:F1} ns   "
            + $"bulk {1000.0 * bulkUs / batchSize,6:F1} ns   total {1000.0 * (sortUs + bulkUs) / batchSize,6:F1} ns   "
            + $"[{(loopUs / (sortUs + bulkUs)):F2}x]");

        // ── Scaled across workers ────────────────────────────────────────────────────
        var boundaries = new int[17];
        foreach (var w in new[] { 1, 2, 4, 8, 16 })
        {
            var parts = map.PartitionByBucketRuns<LocUpdate, LocApplier>(sorted, w, boundaries);
            var spanUs = BestOfParallel(3, map, segment, epochs, sorted, boundaries, parts);

            var largest = 0;
            for (var p = 0; p < parts; p++)
            {
                largest = Math.Max(largest, boundaries[p + 1] - boundaries[p]);
            }

            var imbalance = parts == 0 ? 1.0 : largest / ((double)sorted.Length / parts);
            Console.WriteLine($"      W={w,-2} parts={parts,-2}  span {spanUs,8:F1} us   {1000.0 * spanUs / batchSize,6:F1} ns/entity   "
                + $"{bulkUs / spanUs,5:F2}x   imbalance {imbalance,5:F2}");
        }

        Console.WriteLine();
    }

    /// <summary>
    /// A timed body that still gets the caller's accessor by <c>ref</c> — a lambda cannot capture a <c>ref</c> parameter, but it can take one.
    /// </summary>
    private delegate void TimedBody(ref ChunkAccessor<PersistentStore> accessor);

    private static double BestOf(int iterations, TimedBody body, ref ChunkAccessor<PersistentStore> accessor)
    {
        // Ten warm-up passes, not one. Tiered compilation promotes a method to tier-1 only after roughly thirty calls, so with a single warm-up the FIRST
        // measurement in the process runs tier-0 code and every later one runs progressively better code — which showed up as `sort ns` and `loop ns`
        // falling monotonically down the report, a ramp that has nothing to do with batch size or shape.
        for (var w = 0; w < 10; w++)
        {
            body(ref accessor);
        }

        // Settle the heap so this measurement is not charged for the previous one's garbage — see the buffer-hoisting note in Run.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var best = double.MaxValue;
        for (var i = 0; i < iterations; i++)
        {
            var t0 = Stopwatch.GetTimestamp();
            body(ref accessor);
            var us = (Stopwatch.GetTimestamp() - t0) * (1_000_000.0 / Stopwatch.Frequency);
            best = Math.Min(best, us);
        }

        return best;
    }

    private static double BestOfParallel(int iterations, RawValuePagedHashMap<long, PersistentStore> map, ChunkBasedSegment<PersistentStore> segment,
        EpochManager epochs, LocUpdate[] sorted, int[] boundaries, int parts)
    {
        var best = double.MaxValue;
        for (var i = 0; i <= iterations; i++)
        {
            // Threads are created BEFORE the clock starts and released by a gate, because creating them inside the timed region reads as a clean 5x
            // regression at high W — step 6 paid for that lesson too.
            var gate = new ManualResetEventSlim(false);
            var ready = new CountdownEvent(parts);
            var done = new CountdownEvent(parts);
            var threads = new Thread[parts];
            for (var p = 0; p < parts; p++)
            {
                var lo = boundaries[p];
                var hi = boundaries[p + 1];
                threads[p] = new Thread(() =>
                {
                    var depth = epochs.EnterScope();
                    var partAccessor = segment.CreateChunkAccessor();
                    try
                    {
                        ready.Signal();
                        gate.Wait();
                        map.UpdateValuesBulk<LocUpdate, LocApplier>(sorted.AsSpan(lo, hi - lo), ref partAccessor);
                    }
                    finally
                    {
                        partAccessor.Dispose();
                        epochs.ExitScope(depth);
                        done.Signal();
                    }
                })
                { IsBackground = true };
                threads[p].Start();
            }

            ready.Wait();
            var t0 = Stopwatch.GetTimestamp();
            gate.Set();
            done.Wait();
            var us = (Stopwatch.GetTimestamp() - t0) * (1_000_000.0 / Stopwatch.Frequency);

            if (i > 0)   // iteration 0 is the warm-up
            {
                best = Math.Min(best, us);
            }

            gate.Dispose();
            ready.Dispose();
            done.Dispose();
        }

        return best;
    }

    private static void SortByBucket(RawValuePagedHashMap<long, PersistentStore> map, LocUpdate[] source, LocUpdate[] destination, int[] bucketScratch)
    {
        for (var i = 0; i < source.Length; i++)
        {
            bucketScratch[i] = source[i].Bucket;
            destination[i] = source[i];
        }

        Array.Sort(bucketScratch, destination);
    }

    private static long[] BuildKeys(int mapSize, int batchSize, string shape)
    {
        var rng = new Random(20260901);
        var keys = new long[batchSize];

        if (shape == "uniform")
        {
            var seen = new HashSet<long>();
            var i = 0;
            while (i < batchSize)
            {
                var k = rng.NextInt64(1, mapSize + 1);
                if (seen.Add(k))
                {
                    keys[i++] = k;
                }
            }

            return keys;
        }

        // Clustered: a contiguous run of entity ids, which is what a cell's worth of migrants looks like — dense keys landing in comparatively few buckets.
        var start = rng.NextInt64(1, Math.Max(2, mapSize - batchSize));
        for (var i = 0; i < batchSize; i++)
        {
            keys[i] = start + i;
        }

        return keys;
    }
}
