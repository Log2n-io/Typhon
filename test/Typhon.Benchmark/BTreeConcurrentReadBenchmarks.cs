using BenchmarkDotNet.Attributes;

namespace Typhon.Benchmark;

// ═══════════════════════════════════════════════════════════════════════
// BTree: Concurrent Read Scaling — Measures CAS contention on
// AccessControl's shared counter as reader count grows.
//
// Under the current whole-tree shared lock, each reader CAS-increments
// and CAS-decrements a shared counter. At 32 threads, this generates
// significant cache-line bouncing (100-400 ns cross-socket).
//
// After OLC: readers become fully lock-free (version reads only, no CAS).
// The scaling curve should go from sublinear to near-linear.
//
// Real-world:
//   RandomKeys:  entity queries hitting primary key index
//   HotKey:      all queries for the same entity (worst-case cache bounce)
//   Scan_100:    range-like sequential reads (e.g., nearby entities)
//
// Profile mapping:
//   Fast:   ThreadCount = [1, 32], RandomKeys only
//   Medium: ThreadCount = [1, 8, 32]
//   Full:   ThreadCount = [1, 4, 8, 16, 32]
// ═══════════════════════════════════════════════════════════════════════

[SimpleJob(warmupCount: 2, iterationCount: 5)]
[BenchmarkCategory("BTree", "Concurrency", "BTreeMedium")]
public class BTreeConcurrentReadBenchmarks
{
    private BTreeBenchmarkHelper _helper;
    private ChunkBasedSegment<PersistentStore> _segment;
    private LongSingleBTree<PersistentStore> _tree;
    private long[][] _perThreadKeys;

    private const int PreFillCount = 10_000;
    private const int OpsPerInvocation = 100_000;

    [Params(1, 4, 8, 16, 32)]
    public int ThreadCount;

    [GlobalSetup]
    public unsafe void GlobalSetup()
    {
        _helper = new BTreeBenchmarkHelper();
        _helper.Setup();

        _segment = _helper.AllocateSegment<Index64Chunk>();
        _tree = new LongSingleBTree<PersistentStore>(_segment);
        BTreeBenchmarkHelper.PreFillLong(_tree, _segment, PreFillCount);

        // Pre-generate per-thread random key arrays (different seeds per thread)
        var opsPerThread = OpsPerInvocation / ThreadCount;
        _perThreadKeys = new long[ThreadCount][];
        for (int t = 0; t < ThreadCount; t++)
        {
            _perThreadKeys[t] = BTreeBenchmarkHelper.GenerateRandomLongKeys(opsPerThread, PreFillCount, seed: 42 + t);
        }
    }

    [GlobalCleanup]
    public void GlobalCleanup() => _helper?.Dispose();

    /// <summary>
    /// Random point queries distributed across the entire tree.
    /// Each thread queries different random keys — measures lock contention, not data contention.
    /// At 32 threads: current model = 32 threads CAS-fighting on _access shared counter.
    /// After OLC: 32 threads reading version counters with zero writes to shared state.
    /// </summary>
    [Benchmark(OperationsPerInvoke = OpsPerInvocation)]
    [BenchmarkCategory("BTreeFast")]
    public void ConcurrentLookup_RandomKeys()
    {
        var opsPerThread = OpsPerInvocation / ThreadCount;
        BTreeBenchmarkHelper.RunConcurrentWithBarrier(ThreadCount, _helper.EpochManager, threadId =>
        {
            var accessor = _segment.CreateChunkAccessor();
            var keys = _perThreadKeys[threadId];
            for (int i = 0; i < opsPerThread; i++)
            {
                _tree.TryGet(keys[i], ref accessor);
            }
            accessor.Dispose();
        });
    }

    /// <summary>
    /// All threads query the exact same key. Worst-case cache-line contention:
    /// all threads read the same leaf node AND fight over the same AccessControl counter.
    /// After OLC: all threads read the same leaf's version counter (read-only, no bouncing).
    /// </summary>
    [Benchmark(OperationsPerInvoke = OpsPerInvocation)]
    public void ConcurrentLookup_HotKey()
    {
        var opsPerThread = OpsPerInvocation / ThreadCount;
        BTreeBenchmarkHelper.RunConcurrentWithBarrier(ThreadCount, _helper.EpochManager, threadId =>
        {
            var accessor = _segment.CreateChunkAccessor();
            for (int i = 0; i < opsPerThread; i++)
            {
                _tree.TryGet(5000, ref accessor);
            }
            accessor.Dispose();
        });
    }

    /// <summary>
    /// Each thread scans 100 consecutive keys (simulating range-like access).
    /// Tests sequential access locality under concurrent shared-lock pressure.
    /// After OLC: per-leaf version validation instead of tree-wide shared lock.
    /// </summary>
    [Benchmark(OperationsPerInvoke = OpsPerInvocation)]
    public void ConcurrentScan_100Keys()
    {
        var scansPerThread = OpsPerInvocation / ThreadCount / 100;
        BTreeBenchmarkHelper.RunConcurrentWithBarrier(ThreadCount, _helper.EpochManager, threadId =>
        {
            var accessor = _segment.CreateChunkAccessor();
            // Each thread scans a different region to avoid false sharing
            var baseKey = 1 + (threadId * (PreFillCount / ThreadCount));
            for (int scan = 0; scan < scansPerThread; scan++)
            {
                var start = baseKey + (scan % 50) * 100;
                if (start + 100 > PreFillCount)
                {
                    start = 1;
                }
                for (int i = 0; i < 100; i++)
                {
                    _tree.TryGet(start + i, ref accessor);
                }
            }
            accessor.Dispose();
        });
    }
}
