using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Typhon.Benchmark;

/// <summary>
/// #872 step 5, AC-5.3 / AC-5.4 / AC-5.5 — the bulk partitioning descent measured at the size the design's model is stated for.
/// </summary>
/// <remarks>
/// <para>
/// Not a BenchmarkDotNet class, for <see cref="RebuildBench"/>'s reason in a different form: the <c>Remove</c>+<c>Add</c> baseline MUTATES tree structure,
/// so it needs a freshly built tree per measurement, and a 1 M-entry build is far too expensive to sit inside BDN's iteration model. The two
/// structure-preserving variants could be BDN benchmarks; putting all three here keeps them measured against the same tree in the same run, which is what
/// makes the ratio mean anything.
/// </para>
/// <para>
/// The unit test <c>UpdateValues_VisitsExactlyTheUnionOfTheKeysPaths</c> covers the same property on a 20 K tree and is what CI protects. This harness exists
/// because §5.3's published table is for 1 M entries and a model checked at the wrong size is not checked.
/// </para>
/// </remarks>
public static class BulkUpdateBench
{
    public static void Run(int treeSize = 1_000_000)
    {
        Console.WriteLine($"=== #872 step 5 - bulk value update, {treeSize:N0}-entry tree ===");
        Console.WriteLine();

        int[] batchSizes = [10, 100, 1_000, 10_000];

        Console.WriteLine($"{"N",8} {"visits",9} {"v/upd",7} {"leaves",8} {"bulk us",10} {"loop us",10} {"rm+add us",11} {"vs loop",9} {"vs rm+add",10}");
        Console.WriteLine(new string('-', 92));

        foreach (var n in batchSizes)
        {
            MeasureOne(treeSize, n, clustered: false);
        }

        Console.WriteLine();
        Console.WriteLine("Clustered batch (AC-5.5) - one cell's worth of re-clustering: contiguous keys rather than uniformly random.");
        Console.WriteLine($"{"N",8} {"visits",9} {"v/upd",7} {"leaves",8} {"bulk us",10} {"loop us",10} {"rm+add us",11} {"vs loop",9} {"vs rm+add",10}");
        Console.WriteLine(new string('-', 92));
        MeasureOne(treeSize, 10_000, clustered: true);

        Console.WriteLine();
        Console.WriteLine("Gates: AC-5.4 >= 8x vs Remove+Add at every N; AC-5.5 >= 50x on the clustered batch.");

        Console.WriteLine();
        Console.WriteLine("NON-UNIQUE (AllowMultiple) - the shape a spatial index actually has, and the one 5.3's motivating case uses.");
        Console.WriteLine("'wide' = few keys with many elements each (one cell's entities share a key); 'narrow' = one element per key.");
        Console.WriteLine($"{"shape",8} {"keys",8} {"el/key",7} {"N",8} {"visits",8} {"leaves",7} {"bulk us",10} {"at-loop us",11} {"rm+add us",11} {"vs at",7} {"vs rm+add",10}");
        Console.WriteLine(new string('-', 108));

        // 1 M elements either way, so the two rows differ only in how they are distributed across buffers.
        MeasureMulti(keyCount: 200_000, elementsPerKey: 5, n: 10_000, wide: true);
        MeasureMulti(keyCount: 20_000, elementsPerKey: 50, n: 10_000, wide: true);
        MeasureMulti(keyCount: 5_000, elementsPerKey: 200, n: 10_000, wide: true);
        MeasureMulti(keyCount: 1_000_000, elementsPerKey: 1, n: 10_000, wide: false);
    }

    /// <summary>
    /// The AllowMultiple bulk update against the two element distributions that matter, with the pair it replaces as the baseline.
    /// </summary>
    /// <remarks>
    /// The unique rows above measure a path the spatial use case does not take. A non-unique index puts the VALUE in a VSBS buffer behind the leaf slot, so
    /// per update there are two leaf resolutions plus a buffer lock, a root resolution, an element-chunk resolution and a value scan - none of which the
    /// unique path pays. Which half dominates decides which one is worth optimising, and neither had a number before this row existed.
    /// </remarks>
    private static void MeasureMulti(int keyCount, int elementsPerKey, int n, bool wide)
    {
        // The batch walks whole buffers first (wide) or one element from each of n consecutive keys (narrow). Both are sorted by key, as the API requires.
        var batchKeys = new int[n];
        var batchSlot = new int[n];
        var written = 0;
        var startKey = keyCount / 3;
        for (var k = startKey; written < n; k++)
        {
            for (var e = 0; e < elementsPerKey && written < n; e++)
            {
                batchKeys[written] = k * 10;
                batchSlot[written] = e;
                written++;
            }
        }

        var stats = default(BulkUpdateStats);

        var bulkUs = TimeOnMultiTree(keyCount, elementsPerKey, batchKeys, batchSlot,
            static (IntMultipleBTree<PersistentStore> tree, int[] keys, int[] ids, int[] current, ref ChunkAccessor<PersistentStore> accessor,
                    ref BulkUpdateStats st) =>
            {
                var batch = new BTreeMultiValueUpdate<int>[keys.Length];
                for (var i = 0; i < keys.Length; i++)
                {
                    batch[i] = new BTreeMultiValueUpdate<int>(keys[i], ids[i], current[i], current[i] + 1);
                }

                var applied = tree.UpdateValues(batch, ref accessor, out st);
                for (var i = 0; i < keys.Length; i++)
                {
                    current[i]++;
                }

                if (applied != keys.Length)
                {
                    ThrowMeasuredNothing("UpdateValues (multi)", applied, keys.Length);
                }
            },
            ref stats);

        var loopUs = TimeOnMultiTree(keyCount, elementsPerKey, batchKeys, batchSlot,
            static (IntMultipleBTree<PersistentStore> tree, int[] keys, int[] ids, int[] current, ref ChunkAccessor<PersistentStore> accessor,
                    ref BulkUpdateStats st) =>
            {
                var applied = 0;
                for (var i = 0; i < keys.Length; i++)
                {
                    if (tree.TryUpdateValueAt(keys[i], ids[i], current[i], current[i] + 1, ref accessor))
                    {
                        applied++;
                    }

                    current[i]++;
                }

                if (applied != keys.Length)
                {
                    ThrowMeasuredNothing("TryUpdateValueAt loop", applied, keys.Length);
                }
            },
            ref stats);

        var removeAddUs = TimeOnMultiTree(keyCount, elementsPerKey, batchKeys, batchSlot,
            static (IntMultipleBTree<PersistentStore> tree, int[] keys, int[] ids, int[] current, ref ChunkAccessor<PersistentStore> accessor,
                    ref BulkUpdateStats st) =>
            {
                // What migration does today for a non-unique index: drop the element and append the replacement, which hands back a NEW element id.
                var applied = 0;
                for (var i = 0; i < keys.Length; i++)
                {
                    if (tree.RemoveValue(keys[i], ids[i], current[i], ref accessor))
                    {
                        applied++;
                    }

                    ids[i] = tree.Add(keys[i], current[i] + 1, ref accessor);
                    current[i]++;
                }

                if (applied != keys.Length)
                {
                    ThrowMeasuredNothing("RemoveValue+Add loop", applied, keys.Length);
                }
            },
            ref stats);

        Console.WriteLine(
            $"{(wide ? "wide" : "narrow"),8} {keyCount,8:N0} {elementsPerKey,7} {n,8:N0} {stats.NodeVisits,8:N0} {stats.LeavesTouched,7:N0} "
            + $"{bulkUs,10:F1} {loopUs,11:F1} {removeAddUs,11:F1} {loopUs / bulkUs,6:F1}x {removeAddUs / bulkUs,9:F1}x");

    }

    private delegate void MultiTreeWork(
        IntMultipleBTree<PersistentStore> tree,
        int[] keys,
        int[] ids,
        int[] current,
        ref ChunkAccessor<PersistentStore> accessor,
        ref BulkUpdateStats stats);

    /// <summary>Builds an AllowMultiple tree of <paramref name="keyCount"/> keys x <paramref name="elementsPerKey"/> elements, warms once, measures 15.</summary>
    private static unsafe double TimeOnMultiTree(int keyCount, int elementsPerKey, int[] batchKeys, int[] batchSlot, MultiTreeWork work,
        ref BulkUpdateStats stats)
    {
        const int MeasuredPasses = 15;

        var services = new ServiceCollection();
        services
            .AddLogging(c => c.SetMinimumLevel(LogLevel.Error))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddScopedManagedPagedMemoryMappedFile(o =>
            {
                o.DatabaseName = $"BulkUpdateBenchM_{Environment.ProcessId}_{Interlocked.Increment(ref _run)}";
                o.DatabaseDirectory = Path.GetTempPath();
                o.DatabaseCacheSize = 1024UL * 1024 * 1024;
                o.PagesDebugPattern = false;
            });

        using var provider = services.BuildServiceProvider();
        provider.EnsureFileDeleted<ManagedPagedMMFOptions>();

        var mpmmf = provider.GetRequiredService<ManagedPagedMMF>();
        var epochManager = provider.GetRequiredService<EpochManager>();

        var depth = epochManager.EnterScope();
        try
        {
            var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 65_536, sizeof(Index32Chunk));
            var accessor = segment.CreateChunkAccessor();
            try
            {
                var tree = new IntMultipleBTree<PersistentStore>(segment);

                // elementIds are returned by Add and must be kept: the batch addresses elements by id, and Remove+Add reassigns them.
                var idsByKeySlot = new int[keyCount + 1][];
                for (var k = 1; k <= keyCount; k++)
                {
                    var perKey = new int[elementsPerKey];
                    for (var e = 0; e < elementsPerKey; e++)
                    {
                        perKey[e] = tree.Add(k * 10, k * 100 + e, ref accessor);
                    }

                    idsByKeySlot[k] = perKey;
                }

                var ids = new int[batchKeys.Length];
                var current = new int[batchKeys.Length];
                for (var i = 0; i < batchKeys.Length; i++)
                {
                    var k = batchKeys[i] / 10;
                    ids[i] = idsByKeySlot[k][batchSlot[i]];
                    current[i] = k * 100 + batchSlot[i];
                }

                work(tree, batchKeys, ids, current, ref accessor, ref stats);   // warm

                var best = double.MaxValue;
                for (var pass = 0; pass < MeasuredPasses; pass++)
                {
                    var sw = Stopwatch.StartNew();
                    work(tree, batchKeys, ids, current, ref accessor, ref stats);
                    sw.Stop();
                    var us = sw.Elapsed.TotalMilliseconds * 1000.0;
                    if (us < best)
                    {
                        best = us;
                    }
                }

                return best;
            }
            finally
            {
                accessor.Dispose();
            }
        }
        finally
        {
            epochManager.ExitScope(depth);
            provider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        }
    }

    private static void MeasureOne(int treeSize, int n, bool clustered)
    {
        var keys = BuildBatchKeys(treeSize, n, clustered);
        var bulkBatch = new BTreeValueUpdate<int>[n];
        for (var i = 0; i < n; i++)
        {
            bulkBatch[i] = new BTreeValueUpdate<int>(keys[i], 900_000 + i);
        }

        BulkUpdateStats stats = default;

        // Every variant asserts it did the work. A path that silently rejects every entry reads as spectacularly fast and would pass both printed gates —
        // the same hazard BTreeMicroBenchmarks.ThrowBenchmarkDidNoWork exists for, and the reason Move_SameLeaf's remarks were written.
        var bulkUs = TimeOnFreshTree(treeSize, (IntSingleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor) =>
        {
            if (tree.UpdateValues(bulkBatch, ref accessor, out stats) != bulkBatch.Length)
            {
                ThrowMeasuredNothing("UpdateValues", stats.Applied, bulkBatch.Length);
            }
        });

        var loopUs = TimeOnFreshTree(treeSize, (IntSingleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor) =>
        {
            var applied = 0;
            for (var i = 0; i < bulkBatch.Length; i++)
            {
                if (tree.TryUpdateValue(bulkBatch[i].Key, bulkBatch[i].NewValue, ref accessor))
                {
                    applied++;
                }
            }

            if (applied != bulkBatch.Length)
            {
                ThrowMeasuredNothing("TryUpdateValue loop", applied, bulkBatch.Length);
            }
        });

        var removeAddUs = TimeOnFreshTree(treeSize, (IntSingleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor) =>
        {
            var applied = 0;
            for (var i = 0; i < bulkBatch.Length; i++)
            {
                if (tree.Remove(bulkBatch[i].Key, out _, ref accessor))
                {
                    applied++;
                }

                tree.Add(bulkBatch[i].Key, bulkBatch[i].NewValue, ref accessor);
            }

            if (applied != bulkBatch.Length)
            {
                ThrowMeasuredNothing("Remove+Add loop", applied, bulkBatch.Length);
            }
        });

        Console.WriteLine(
            $"{n,8:N0} {stats.NodeVisits,9:N0} {stats.NodeVisits / (double)n,7:F2} {stats.LeavesTouched,8:N0} "
            + $"{bulkUs,10:F1} {loopUs,10:F1} {removeAddUs,11:F1} {loopUs / bulkUs,8:F1}x {removeAddUs / bulkUs,9:F1}x");
    }

    private static void ThrowMeasuredNothing(string variant, int applied, int expected) =>
        throw new InvalidOperationException(
            $"{variant} applied {applied} of {expected} entries, so the timing measures rejections rather than work and the ratio is meaningless.");

    private static int[] BuildBatchKeys(int treeSize, int n, bool clustered)
    {
        var keys = new int[n];
        if (clustered)
        {
            // What re-clustering actually produces: one contiguous run of keys, which is why 5.2 and 5.3 are one design rather than two.
            var start = treeSize / 3;
            for (var i = 0; i < n; i++)
            {
                keys[i] = (start + i) * 10;
            }

            return keys;
        }

        var rng = new Random(4242);
        var set = new System.Collections.Generic.SortedSet<int>();
        while (set.Count < n)
        {
            set.Add(rng.Next(1, treeSize + 1) * 10);
        }

        set.CopyTo(keys);
        return keys;
    }

    private delegate void TreeWork(IntSingleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor);

    /// <summary>
    /// Stands up a private page cache, builds a tree of <paramref name="treeSize"/> entries, warms <paramref name="work"/> once and measures it fifteen
    /// times, returning the BEST in microseconds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A private cache per measurement is not tidiness, it is the measurement.</b> Sharing one 1 GiB cache across all thirteen trees put ~870 MiB of
    /// 8 192-page segments into it, so whichever variants ran last measured under eviction pressure the earlier ones never saw. Run to run that moved the
    /// clustered figure between 93 and 168 us - a 1.8x swing on the number a gate is being read off - and it was ORDER-dependent, which is the same
    /// methodology fault step 2's rebuild benchmark had in a different costume.
    /// </para>
    /// <para>
    /// The warm-up pass is equally load-bearing: a single measured pass over a freshly built tree charges the whole page-cache fault-in to whoever ran first,
    /// and it once reported 10 updates costing 1 655 us - 165 us per update, against a single-entry path measured at 208 ns. Every variant here is
    /// structure-preserving except Remove+Add, whose repeats re-add exactly the keys they removed, so repeating is sound for all of them.
    /// </para>
    /// </remarks>
    private static unsafe double TimeOnFreshTree(int treeSize, TreeWork work)
    {
        const int MeasuredPasses = 15;

        var services = new ServiceCollection();
        services
            .AddLogging(c => c.SetMinimumLevel(LogLevel.Error))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddScopedManagedPagedMemoryMappedFile(o =>
            {
                o.DatabaseName = $"BulkUpdateBench_{Environment.ProcessId}_{Interlocked.Increment(ref _run)}";
                o.DatabaseDirectory = Path.GetTempPath();
                o.DatabaseCacheSize = 1024UL * 1024 * 1024;
                o.PagesDebugPattern = false;
            });

        using var provider = services.BuildServiceProvider();
        provider.EnsureFileDeleted<ManagedPagedMMFOptions>();

        var mpmmf = provider.GetRequiredService<ManagedPagedMMF>();
        var epochManager = provider.GetRequiredService<EpochManager>();

        var depth = epochManager.EnterScope();
        try
        {
            var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 8_192, sizeof(Index32Chunk));
            var accessor = segment.CreateChunkAccessor();
            try
            {
                var tree = new IntSingleBTree<PersistentStore>(segment);
                for (var i = 1; i <= treeSize; i++)
                {
                    tree.Add(i * 10, i, ref accessor);
                }

                work(tree, ref accessor);   // warm: faults in the pages this batch touches, and JITs the path

                var best = double.MaxValue;
                for (var pass = 0; pass < MeasuredPasses; pass++)
                {
                    var sw = Stopwatch.StartNew();
                    work(tree, ref accessor);
                    sw.Stop();
                    var us = sw.Elapsed.TotalMilliseconds * 1000.0;
                    if (us < best)
                    {
                        best = us;
                    }
                }

                return best;
            }
            finally
            {
                accessor.Dispose();
            }
        }
        finally
        {
            epochManager.ExitScope(depth);

            // Each measurement stands up its own database, and there are fifteen of them per run at 8 192 pages each. Without this the harness leaves
            // roughly a gigabyte in the temp directory every time it is invoked, and the pid in the name means nothing ever reuses it.
            provider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        }
    }

    private static int _run;
}
