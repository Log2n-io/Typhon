using BenchmarkDotNet.Attributes;
using System.Threading;

namespace Typhon.Benchmark;

// ═══════════════════════════════════════════════════════════════════════
// BTree: Concurrent Write Scaling — Quantifies whole-tree exclusive lock
// serialization. With the current model, only ONE writer at a time,
// regardless of how many threads are active or which leaves they target.
//
// After OLC Phase 1: writers latch only the target leaf. Concurrent inserts
// to different leaves proceed in parallel. Monotonic inserts still serialize
// on the rightmost leaf until Phase 2 Contention Split.
//
// Real-world:
//   Insert_Random:    creating entities with random/hash IDs
//   Insert_Monotonic: creating entities with auto-increment IDs (worst case)
//   Delete_Random:    cleanup/GC removing entities from the index
//
// Profile mapping:
//   Fast:   ThreadCount = [1, 32], Insert_Random only
//   Medium: ThreadCount = [1, 8, 32]
//   Full:   ThreadCount = [1, 4, 8, 16, 32]
// ═══════════════════════════════════════════════════════════════════════

[SimpleJob(warmupCount: 2, iterationCount: 5)]
[BenchmarkCategory("BTree", "Concurrency", "BTreeMedium")]
public class BTreeConcurrentWriteBenchmarks
{
    private BTreeBenchmarkHelper _helper;
    private EpochManager _epochManager;

    // Each benchmark uses its own tree to avoid cross-benchmark state pollution
    private ChunkBasedSegment<PersistentStore> _segRandom;
    private LongSingleBTree<PersistentStore> _treeRandom;
    private long[][] _perThreadRandomKeys;

    private ChunkBasedSegment<PersistentStore> _segMonotonic;
    private LongSingleBTree<PersistentStore> _treeMonotonic;
    private long _monotonicCounter;

    private ChunkBasedSegment<PersistentStore> _segDelete;
    private LongSingleBTree<PersistentStore> _treeDelete;
    private long[][] _perThreadDeleteKeys;

    private const int PreFillCount = 10_000;
    private const int OpsPerInvocation = 50_000;

    [Params(1, 4, 8, 16, 32)]
    public int ThreadCount;

    [GlobalSetup]
    public unsafe void GlobalSetup()
    {
        _helper = new BTreeBenchmarkHelper();
        _helper.Setup(2000);
        _epochManager = _helper.EpochManager;

        var opsPerThread = OpsPerInvocation / ThreadCount;

        // ── Random insert tree ───────────────────────────────────────────
        _segRandom = _helper.AllocateSegment<Index64Chunk>(1000);
        _treeRandom = new LongSingleBTree<PersistentStore>(_segRandom);
        BTreeBenchmarkHelper.PreFillLong(_treeRandom, _segRandom, PreFillCount);

        // Per-thread disjoint key ranges for remove+reinsert (no collision between threads)
        _perThreadRandomKeys = new long[ThreadCount][];
        for (int t = 0; t < ThreadCount; t++)
        {
            var rangeStart = 1 + t * (PreFillCount / ThreadCount);
            var rangeEnd = (t == ThreadCount - 1) ? PreFillCount : rangeStart + (PreFillCount / ThreadCount) - 1;
            _perThreadRandomKeys[t] = BTreeBenchmarkHelper.GenerateRandomLongKeys(
                opsPerThread, rangeEnd, seed: 100 + t);
            // Clamp keys to thread's range
            for (int i = 0; i < opsPerThread; i++)
            {
                var k = _perThreadRandomKeys[t][i];
                _perThreadRandomKeys[t][i] = rangeStart + (k % (rangeEnd - rangeStart + 1));
            }
        }

        // Monotonic tree is allocated per-iteration (see IterationSetup) to avoid unbounded growth.

        // ── Delete tree ──────────────────────────────────────────────────
        _segDelete = _helper.AllocateSegment<Index64Chunk>(1000);
        _treeDelete = new LongSingleBTree<PersistentStore>(_segDelete);
        BTreeBenchmarkHelper.PreFillLong(_treeDelete, _segDelete, PreFillCount);

        // Same disjoint ranges as random
        _perThreadDeleteKeys = new long[ThreadCount][];
        for (int t = 0; t < ThreadCount; t++)
        {
            var rangeStart = 1 + t * (PreFillCount / ThreadCount);
            var rangeEnd = (t == ThreadCount - 1) ? PreFillCount : rangeStart + (PreFillCount / ThreadCount) - 1;
            _perThreadDeleteKeys[t] = BTreeBenchmarkHelper.GenerateRandomLongKeys(
                opsPerThread, rangeEnd, seed: 200 + t);
            for (int i = 0; i < opsPerThread; i++)
            {
                var k = _perThreadDeleteKeys[t][i];
                _perThreadDeleteKeys[t][i] = rangeStart + (k % (rangeEnd - rangeStart + 1));
            }
        }
    }

    [GlobalCleanup]
    public void GlobalCleanup() => _helper?.Dispose();

    /// <summary>
    /// Reset the monotonic tree before each iteration to prevent unbounded growth.
    /// Without this, each iteration adds 50K keys that are never removed, causing
    /// progressively slower iterations as the tree deepens.
    /// </summary>
    [IterationSetup(Target = nameof(ConcurrentInsert_Monotonic))]
    public unsafe void ResetMonotonicTree()
    {
        _segMonotonic = _helper.AllocateSegment<Index64Chunk>(500);
        _treeMonotonic = new LongSingleBTree<PersistentStore>(_segMonotonic);
        BTreeBenchmarkHelper.PreFillLong(_treeMonotonic, _segMonotonic, PreFillCount);
        _monotonicCounter = PreFillCount + 1;
    }

    /// <summary>
    /// Concurrent inserts at random positions. Each thread operates on a disjoint key range
    /// (no key collisions). Uses remove+reinsert to keep tree size stable.
    ///
    /// With whole-tree lock: throughput plateaus at 1 thread (all serialize on _access).
    /// After OLC: threads hitting different leaves proceed in parallel → near-linear scaling.
    /// </summary>
    // #765 S4. "Regression" is what puts a benchmark into benchmark/history: bench/aws/benchmark.sky.yaml maps the default profile to
    // `--allCategories Regression`, and all 19 classes ever recorded carry it. This class carried BTree/Concurrency/BTreeMedium, which only the manually
    // dispatched btree-medium profile selects — and nobody ever dispatched it, so the concurrency numbers this whole design was justified by have never once
    // been recorded. Same shape as the [Explicit] finding in S0: a label that quietly removes something from the only tier that runs it.
    [Benchmark(OperationsPerInvoke = OpsPerInvocation)]
    [BenchmarkCategory("BTreeFast", "Regression")]
    public void ConcurrentInsert_Random()
    {
        var opsPerThread = OpsPerInvocation / ThreadCount;
        BTreeBenchmarkHelper.RunConcurrentWithBarrier(ThreadCount, _epochManager, threadId =>
        {
            var accessor = _segRandom.CreateChunkAccessor();
            var keys = _perThreadRandomKeys[threadId];
            for (int i = 0; i < opsPerThread; i++)
            {
                var key = keys[i % keys.Length];
                if (_treeRandom.Remove(key, out var val, ref accessor))
                {
                    _treeRandom.Add(key, val, ref accessor);
                }
            }
            accessor.Dispose();
        });
    }

    /// <summary>
    /// All threads insert monotonically increasing keys via shared atomic counter.
    /// Every insert targets the rightmost leaf — the worst case for B+Tree concurrency.
    ///
    /// With whole-tree lock: completely serialized (same as 1 thread).
    /// After OLC Phase 1: still serialized on rightmost leaf (same node).
    /// After OLC Phase 2 (Contention Split): hot leaf splits proactively → distributes load.
    /// </summary>
    // Deliberately NOT [BenchmarkCategory("Regression")], unlike its two siblings, and this is a measurement decision rather than an oversight.
    //
    // The workload mutates the thing it measures and never resets it: every operation inserts a NEW key drawn from a shared counter, so the tree grows by
    // OpsPerInvocation every iteration and keeps growing through warmup. Iteration 5 measures a materially taller tree than iteration 1. That is not noise an
    // average removes, it is drift, and it shows: measured 2026-08-10 on a 7950X at 5 iterations, the error half-widths were 648 ns on a 1,087 ns mean at 4
    // threads and 2,895 ns on a 2,948 ns mean at 16 — roughly the size of the measurement. Its siblings, which reuse a fixed key set, came in at 3.76 ns on
    // 196 ns and 50 ns on 302 ns.
    //
    // Tracking a metric whose error bar is its own magnitude produces alarms nobody can act on, and this feature exists because of instruments nobody could
    // act on. To qualify this one, give it an [IterationSetup] that rebuilds the tree to a fixed size — then the number means "insert into a 10k-key tree"
    // instead of "insert into a tree of some size between 10k and 260k depending on when you looked".
    //
    // Recorded anyway because the SHAPE is unambiguous and monotone across five points, which noise does not produce: 517.6 ns at 1 thread, 1,086.6 at 4,
    // 1,700.8 at 8, 2,948.0 at 16, 4,138.3 at 32. That is 8x SLOWER at 32 threads than at 1, on the workload whose docstring claims contention splits
    // distribute the load. Whatever the contention-split mechanism is doing for the rightmost-leaf case, it is not this.
    [Benchmark(OperationsPerInvoke = OpsPerInvocation)]
    public void ConcurrentInsert_Monotonic()
    {
        var opsPerThread = OpsPerInvocation / ThreadCount;
        BTreeBenchmarkHelper.RunConcurrentWithBarrier(ThreadCount, _epochManager, threadId =>
        {
            var accessor = _segMonotonic.CreateChunkAccessor();
            for (int i = 0; i < opsPerThread; i++)
            {
                var key = Interlocked.Increment(ref _monotonicCounter);
                _treeMonotonic.Add(key, 42, ref accessor);
            }
            accessor.Dispose();
        });
    }

    /// <summary>
    /// Concurrent delete+reinsert at random positions. Same disjoint key range pattern.
    /// Exercises both the Remove path (potential merge) and Add path (potential split).
    ///
    /// Real-world: entity cleanup/GC while other operations are in flight.
    /// </summary>
    [Benchmark(OperationsPerInvoke = OpsPerInvocation)]
    [BenchmarkCategory("Regression")]
    public void ConcurrentDelete_Random()
    {
        var opsPerThread = OpsPerInvocation / ThreadCount;
        BTreeBenchmarkHelper.RunConcurrentWithBarrier(ThreadCount, _epochManager, threadId =>
        {
            var accessor = _segDelete.CreateChunkAccessor();
            var keys = _perThreadDeleteKeys[threadId];
            for (int i = 0; i < opsPerThread; i++)
            {
                var key = keys[i % keys.Length];
                if (_treeDelete.Remove(key, out var val, ref accessor))
                {
                    _treeDelete.Add(key, val, ref accessor);
                }
            }
            accessor.Dispose();
        });
    }
}
