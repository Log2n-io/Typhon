using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.IO;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Typhon.Benchmark;

/// <summary>
/// #872 step 6's gate numbers: how the leaf-snapped parallel apply scales with W, and what the serial prep it depends on actually costs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two numbers, reported separately on purpose.</b> The phase is a serial prep (merge the per-worker staging buffers, sort by key, snap the part
/// boundaries to leaf edges) followed by a parallel apply. <c>AC-6.5</c> asks for 0.7 x W to W = 8, and quoting that against the apply alone would be
/// flattering nonsense: what a tick pays is prep + apply, and Amdahl's law on the prep is precisely what decides whether the gate is reachable. Both
/// speedups are printed.
/// </para>
/// <para>
/// <b>The sort is the part the design assumed away.</b> §5.5 says "the batch is already sorted by key (the partitioning descent requires it)". The batch
/// that migration produces is sorted by DESTINATION CELL, not by index key, so the conversion adds a sort nobody budgeted for. It is measured here as its
/// own column rather than folded into a phase total, because if it dominates then the honest answer is to say so and drop the conversion.
/// </para>
/// <para>
/// <b>Two shapes, and the clustered one is expected to lose.</b> §5.5 already says it: "10 000 updates over ~400 leaves is ~26 us single-threaded -
/// spreading that across 16 workers saves ~24 us and costs two barriers, which may be a wash." The uniform shape is where parallelism is supposed to pay,
/// so that is where the gate is read; the clustered number is reported as the known wash rather than tuned toward a threshold.
/// </para>
/// </remarks>
internal static class FenceParallelBench
{
    private static int _run;

    public static void Run(int treeSize = 1_000_000, int n = 10_000)
    {
        Console.WriteLine();
        Console.WriteLine($"#872 step 6 - parallel index mass update ({treeSize:N0}-entry tree, {n:N0} updates)");

        // Two discarded measurements first. Tiered compilation is process-wide, so without them the FIRST row of the sweep runs tier-0 code throughout and
        // reads as if W = 1 were slow: the first shape of this harness reported merge+sort at 1 968 us for W = 1 and 2, then 338 us for W = 4, 8 and 16 - a
        // 5.8x "improvement" from a phase that does strictly MORE work as W rises. Every row must be measured against the same JIT tier or the sweep is
        // measuring the runtime warming up.
        Measure(treeSize, n, clustered: false, workers: 4);
        Measure(treeSize, n, clustered: true, workers: 4);

        Console.WriteLine();

        foreach (var clustered in new[] { false, true })
        {
            Console.WriteLine(clustered
                ? "CLUSTERED keys - one contiguous run, which is what re-clustering a cell produces"
                : "UNIFORM keys - scattered across the tree, which is where §5.5 says parallelism pays");
            Console.WriteLine(
                $"{"W",3} {"parts",6} {"leaves",7} {"merge+sort us",14} {"apply us",10} {"total us",10} {"apply x",8} {"total x",8} {"eff",6} "
                + $"{"vs R+A",8}");
            Console.WriteLine(new string('-', 102));

            double applyAtOne = 0;
            double totalAtOne = 0;

            // What migration did before step 6: two root-to-leaf descents per entity per indexed field, single-threaded, inline in the Migrate phase. This
            // is the number the conversion has to beat, and it is the whole reason increment 6 exists.
            var removeAddUs = MeasureRemoveAddBaseline(treeSize, n, clustered);

            foreach (var w in new[] { 1, 2, 4, 8, 16 })
            {
                var r = Measure(treeSize, n, clustered, w);
                if (w == 1)
                {
                    applyAtOne = r.ApplyUs;
                    totalAtOne = r.PrepUs + r.ApplyUs;
                }

                var total = r.PrepUs + r.ApplyUs;
                var applySpeedup = applyAtOne / r.ApplyUs;
                var totalSpeedup = totalAtOne / total;
                Console.WriteLine(
                    $"{w,3} {r.Parts,6} {r.Leaves,7:N0} {r.PrepUs,14:F1} {r.ApplyUs,10:F1} {total,10:F1} "
                    + $"{applySpeedup,7:F2}x {totalSpeedup,7:F2}x {100.0 * applySpeedup / w,5:F0}% "
                    + $"{removeAddUs / total,8:F2}x");
            }

            Console.WriteLine($"    Remove+Add baseline (what migration did before step 6): {removeAddUs:F1} us "
                + $"= {removeAddUs * 1000.0 / n:F1} ns/update");
            Console.WriteLine();
        }
    }

    private readonly struct Result
    {
        public readonly int Parts;
        public readonly int Leaves;
        public readonly double PrepUs;
        public readonly double ApplyUs;

        public Result(int parts, int leaves, double prepUs, double applyUs)
        {
            Parts = parts;
            Leaves = leaves;
            PrepUs = prepUs;
            ApplyUs = applyUs;
        }
    }

    /// <summary>
    /// Builds a private tree, then times the serial prep and the W-way apply separately, best of <c>MeasuredPasses</c>.
    /// </summary>
    /// <remarks>
    /// A private page cache per measurement, for the reason <c>BulkUpdateBench</c> records: sharing one cache across measurements made whichever ran last
    /// measure under eviction pressure the earlier ones never saw, and moved the clustered figure 93 -> 168 us run to run purely by ORDER.
    /// <para>
    /// The prep pass starts from a pristine UNSORTED copy every time. Sorting an already-sorted array measures introsort's best case and would price the
    /// staging at a fraction of what the migration batch actually costs, which is the one number this harness exists to get right.
    /// </para>
    /// </remarks>
    private static unsafe Result Measure(int treeSize, int n, bool clustered, int workers)
    {
        const int MeasuredPasses = 9;

        var pristine = BuildUnsortedBatch(treeSize, n, clustered);
        var working = new BTreeValueUpdate<int>[n];
        var sortScratch = new BTreeValueUpdate<int>[n];
        var radixCounts = new int[RadixSort.Buckets];

        var services = new ServiceCollection();
        services
            .AddLogging(c => c.SetMinimumLevel(LogLevel.Error))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddScopedManagedPagedMemoryMappedFile(o =>
            {
                o.DatabaseName = $"FenceParallelBench_{Environment.ProcessId}_{Interlocked.Increment(ref _run)}";
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
            var tree = new IntSingleBTree<PersistentStore>(segment);
            try
            {
                for (var i = 1; i <= treeSize; i++)
                {
                    tree.Add(i * 10, i, ref accessor);
                }

                var boundaries = new int[workers + 1];
                var parts = 0;
                var leaves = 0;
                var bestPrep = double.MaxValue;
                var bestApply = double.MaxValue;

                using var pool = new WorkerPool(workers, segment, epochManager);

                // Warm: faults in the pages this batch touches, JITs both halves, and gets every worker thread scheduled at least once.
                for (var warm = 0; warm < 3; warm++)
                {
                    Array.Copy(pristine, working, n);
                    tree.SortBulkEntries(MemoryMarshal.AsBytes(working.AsSpan()), MemoryMarshal.AsBytes(sortScratch.AsSpan()), radixCounts, multi: false);
                    parts = tree.PartitionByLeafBoundaries(working, workers, boundaries, ref accessor);
                    pool.Run(tree, working, boundaries, parts, out leaves);
                }

                for (var pass = 0; pass < MeasuredPasses; pass++)
                {
                    var sw = Stopwatch.StartNew();
                    Array.Copy(pristine, working, n);           // the Merge the phase does across per-worker staging buffers
                    tree.SortBulkEntries(MemoryMarshal.AsBytes(working.AsSpan()), MemoryMarshal.AsBytes(sortScratch.AsSpan()), radixCounts,
                        multi: false);   // the sort §5.5 assumed away
                    parts = tree.PartitionByLeafBoundaries(working, workers, boundaries, ref accessor);
                    sw.Stop();
                    var prepUs = sw.Elapsed.TotalMilliseconds * 1000.0;

                    sw.Restart();
                    var applied = pool.Run(tree, working, boundaries, parts, out leaves);
                    sw.Stop();
                    var applyUs = sw.Elapsed.TotalMilliseconds * 1000.0;

                    if (applied != n)
                    {
                        throw new InvalidOperationException(
                            $"the parallel apply wrote {applied} of {n} entries, so the timing measures rejections rather than work.");
                    }

                    if (prepUs < bestPrep)
                    {
                        bestPrep = prepUs;
                    }

                    if (applyUs < bestApply)
                    {
                        bestApply = applyUs;
                    }
                }

                return new Result(parts, leaves, bestPrep, bestApply);
            }
            finally
            {
                accessor.Dispose();
            }
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    /// <summary>
    /// A fixed set of worker threads, started once and driven across every measured pass by two barriers.
    /// </summary>
    /// <remarks>
    /// <b>Creating threads inside the timed region measures the operating system, not the phase.</b> The first shape of this harness did exactly that and
    /// produced a clustered row where apply time ROSE from 181 to 922 us going from W = 1 to W = 16 — a clean 5x "regression" that was almost entirely 16
    /// thread creations at roughly 50 us each. The real phase dispatches onto the scheduler's existing worker pool and pays none of it, so a pool that is
    /// stood up once and reused is the only honest model of it.
    /// </remarks>
    private sealed class WorkerPool : IDisposable
    {
        private readonly Thread[] _threads;
        private readonly Barrier _start;
        private readonly Barrier _done;
        private volatile bool _stopping;

        private IntSingleBTree<PersistentStore> _tree;
        private BTreeValueUpdate<int>[] _batch;
        private int[] _boundaries;
        private int _parts;
        private readonly int[] _applied;
        private readonly int[] _leaves;
        private Exception _failure;

        public WorkerPool(int workers, ChunkBasedSegment<PersistentStore> segment, EpochManager epochManager)
        {
            _threads = new Thread[workers];
            _applied = new int[workers];
            _leaves = new int[workers];
            _start = new Barrier(workers + 1);
            _done = new Barrier(workers + 1);

            for (var w = 0; w < workers; w++)
            {
                var index = w;
                _threads[w] = new Thread(() =>
                {
                    // One epoch scope and one accessor for the thread's whole life, matching a fence worker: EP-01 pins pages for the life of the scope, and
                    // re-entering it per pass would put scope bookkeeping inside the measurement.
                    var depth = epochManager.EnterScope();
                    var acc = segment.CreateChunkAccessor();
                    try
                    {
                        while (true)
                        {
                            _start.SignalAndWait();
                            if (_stopping)
                            {
                                return;
                            }

                            try
                            {
                                if (index < _parts)
                                {
                                    var from = _boundaries[index];
                                    var to = _boundaries[index + 1];
                                    _applied[index] = _tree.UpdateValues(
                                        new ReadOnlySpan<BTreeValueUpdate<int>>(_batch, from, to - from), ref acc, out var st);
                                    _leaves[index] = st.LeavesTouched;
                                }
                                else
                                {
                                    _applied[index] = 0;
                                    _leaves[index] = 0;
                                }
                            }
                            catch (Exception ex)
                            {
                                Volatile.Write(ref _failure, ex);
                            }

                            _done.SignalAndWait();
                        }
                    }
                    finally
                    {
                        acc.Dispose();
                        epochManager.ExitScope(depth);
                    }
                })
                {
                    IsBackground = true,
                    Name = $"fence-bench-{w}",
                };

                _threads[w].Start();
            }
        }

        /// <summary>Runs one pass. Everything outside the two barrier waits is the work being timed.</summary>
        public int Run(IntSingleBTree<PersistentStore> tree, BTreeValueUpdate<int>[] batch, int[] boundaries, int parts, out int leaves)
        {
            _tree = tree;
            _batch = batch;
            _boundaries = boundaries;
            _parts = parts;

            _start.SignalAndWait();
            _done.SignalAndWait();

            if (_failure != null)
            {
                throw new InvalidOperationException("a worker threw during the parallel apply", _failure);
            }

            var total = 0;
            leaves = 0;
            for (var w = 0; w < _threads.Length; w++)
            {
                total += _applied[w];
                leaves += _leaves[w];
            }

            return total;
        }

        public void Dispose()
        {
            _stopping = true;
            _start.SignalAndWait();
            foreach (var t in _threads)
            {
                t.Join();
            }

            _start.Dispose();
            _done.Dispose();
        }
    }

    /// <summary>
    /// Times the per-entity <c>Remove(key)</c> + <c>Add(key, newValue)</c> pair that migration ran inline before step 6.
    /// </summary>
    /// <remarks>
    /// No sort and no partition, because that path needed neither — which is exactly what makes the comparison fair rather than flattering. The staged path
    /// has to pay a merge, a sort and a leaf-snap that this one does not, so the honest question is whether the descent it buys covers them.
    /// </remarks>
    private static unsafe double MeasureRemoveAddBaseline(int treeSize, int n, bool clustered)
    {
        const int MeasuredPasses = 9;

        var pristine = BuildUnsortedBatch(treeSize, n, clustered);

        var services = new ServiceCollection();
        services
            .AddLogging(c => c.SetMinimumLevel(LogLevel.Error))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddScopedManagedPagedMemoryMappedFile(o =>
            {
                o.DatabaseName = $"FenceParallelBenchRA_{Environment.ProcessId}_{Interlocked.Increment(ref _run)}";
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

                var best = double.MaxValue;
                for (var pass = 0; pass < MeasuredPasses + 3; pass++)
                {
                    var sw = Stopwatch.StartNew();
                    var applied = 0;
                    for (var i = 0; i < n; i++)
                    {
                        if (tree.Remove(pristine[i].Key, out _, ref accessor))
                        {
                            applied++;
                        }

                        tree.Add(pristine[i].Key, pristine[i].NewValue, ref accessor);
                    }

                    sw.Stop();
                    if (applied != n)
                    {
                        throw new InvalidOperationException(
                            $"Remove+Add applied {applied} of {n}, so the baseline measures rejections rather than work.");
                    }

                    // The first three passes are warm-up, same as the staged path gets.
                    var us = sw.Elapsed.TotalMilliseconds * 1000.0;
                    if (pass >= 3 && us < best)
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
        }
    }

    /// <summary>
    /// The batch as the producer hands it over: NOT key-sorted.
    /// </summary>
    /// <remarks>
    /// Migration emits in destination-cell order, so the staged batch arrives grouped by where entities went rather than by index key. Shuffling here is
    /// what makes the sort column mean anything.
    /// </remarks>
    private static BTreeValueUpdate<int>[] BuildUnsortedBatch(int treeSize, int n, bool clustered)
    {
        var batch = new BTreeValueUpdate<int>[n];
        if (clustered)
        {
            var start = treeSize / 3;
            for (var i = 0; i < n; i++)
            {
                batch[i] = new BTreeValueUpdate<int>((start + i) * 10, 900_000 + i);
            }
        }
        else
        {
            var stride = Math.Max(1, treeSize / n);
            for (var i = 0; i < n; i++)
            {
                batch[i] = new BTreeValueUpdate<int>((1 + i * stride) * 10, 900_000 + i);
            }
        }

        var rng = new Random(20260901);
        for (var i = n - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (batch[i], batch[j]) = (batch[j], batch[i]);
        }

        return batch;
    }
}
