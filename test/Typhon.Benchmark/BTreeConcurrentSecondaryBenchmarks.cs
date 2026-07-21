using BenchmarkDotNet.Attributes;
using System;

namespace Typhon.Benchmark;

// ═══════════════════════════════════════════════════════════════════════
// BTree: Concurrent Secondary Index Updates — Multiple threads performing
// the Remove+Add pattern that IndexMaintainer uses for field updates.
//
// This is the exact hotspot that compound Move eliminates.
// "Before": 2 exclusive lock cycles per update (Remove + Add)
// "After":  1 compound Move (1 lock for same-leaf, 2 locks for cross-leaf)
//
// Two contention patterns:
//   HotLeaf:    all threads target the same narrow key range → max leaf contention
//   SpreadKeys: each thread uses its own disjoint key range → min leaf contention
//
// With whole-tree locking both patterns serialize equally. Post-OLC,
// SpreadKeys should show near-linear scaling while HotLeaf still
// serializes on the same leaf latch.
//
// Profile mapping:
//   Fast:   ThreadCount = [1, 32], HotLeaf only
//   Medium: ThreadCount = [1, 8, 32]
//   Full:   ThreadCount = [1, 4, 8, 16, 32]
// ═══════════════════════════════════════════════════════════════════════

[SimpleJob(warmupCount: 2, iterationCount: 5)]
[BenchmarkCategory("BTree", "Concurrency", "BTreeMedium")]
public class BTreeConcurrentSecondaryBenchmarks
{
    private BTreeBenchmarkHelper _helper;
    private EpochManager _epochManager;

    // Separate trees per benchmark to avoid state cross-contamination
    private ChunkBasedSegment<PersistentStore> _segHot;
    private IntSingleBTree<PersistentStore> _treeHot;
    private int[][] _perThreadHotKeys;

    private ChunkBasedSegment<PersistentStore> _segSpread;
    private IntSingleBTree<PersistentStore> _treeSpread;
    private int[][] _perThreadSpreadKeys;

    private const int PreFillCount = 10_000;
    private const int OpsPerInvocation = 50_000;

    // Hot range: all threads compete on the same 100 keys (fits in ~7 leaves for L32)
    private const int HotRangeStart = 4950;
    private const int HotRangeSize = 100;

    [Params(1, 4, 8, 16, 32)]
    public int ThreadCount;

    [GlobalSetup]
    public unsafe void GlobalSetup()
    {
        _helper = new BTreeBenchmarkHelper();
        _helper.Setup(1000);
        _epochManager = _helper.EpochManager;

        var opsPerThread = OpsPerInvocation / ThreadCount;

        // ── Hot leaf tree: all threads hit the same narrow range ────────
        _segHot = _helper.AllocateSegment<Index32Chunk>(500);
        _treeHot = new IntSingleBTree<PersistentStore>(_segHot);
        BTreeBenchmarkHelper.PreFillInt(_treeHot, _segHot, PreFillCount);

        // All threads share the same hot key range
        _perThreadHotKeys = new int[ThreadCount][];
        for (int t = 0; t < ThreadCount; t++)
        {
            _perThreadHotKeys[t] = new int[opsPerThread];
            var rng = new Random(500 + t);
            for (int i = 0; i < opsPerThread; i++)
            {
                _perThreadHotKeys[t][i] = HotRangeStart + rng.Next(0, HotRangeSize);
            }
        }

        // ── Spread tree: each thread has disjoint key range ─────────────
        _segSpread = _helper.AllocateSegment<Index32Chunk>(500);
        _treeSpread = new IntSingleBTree<PersistentStore>(_segSpread);
        BTreeBenchmarkHelper.PreFillInt(_treeSpread, _segSpread, PreFillCount);

        _perThreadSpreadKeys = new int[ThreadCount][];
        for (int t = 0; t < ThreadCount; t++)
        {
            var rangeStart = 1 + t * (PreFillCount / ThreadCount);
            var rangeEnd = (t == ThreadCount - 1) ? PreFillCount : rangeStart + (PreFillCount / ThreadCount) - 1;
            var rangeSize = rangeEnd - rangeStart + 1;
            _perThreadSpreadKeys[t] = new int[opsPerThread];
            var rng = new Random(600 + t);
            for (int i = 0; i < opsPerThread; i++)
            {
                _perThreadSpreadKeys[t][i] = rangeStart + rng.Next(0, rangeSize);
            }
        }
    }

    [GlobalCleanup]
    public void GlobalCleanup() => _helper?.Dispose();

    /// <summary>
    /// All threads compete on the same 100 keys (7 leaves in L32 tree).
    /// Maximum leaf contention — every thread hits the same hot region.
    ///
    /// Real-world: popular entity group (e.g., "all players in spawn zone")
    /// with frequent field updates. Everyone updating the same index range.
    ///
    /// Post-OLC: still serializes on the same leaf latch. Minimal improvement.
    /// </summary>
    [Benchmark(OperationsPerInvoke = OpsPerInvocation)]
    [BenchmarkCategory("BTreeFast")]
    public void SecondaryUpdate_HotLeaf()
    {
        var opsPerThread = OpsPerInvocation / ThreadCount;
        BTreeBenchmarkHelper.RunConcurrentWithBarrier(ThreadCount, _epochManager, threadId =>
        {
            var accessor = _segHot.CreateChunkAccessor();
            var keys = _perThreadHotKeys[threadId];
            for (int i = 0; i < opsPerThread; i++)
            {
                var key = keys[i % keys.Length];
                if (_treeHot.Remove(key, out var val, ref accessor))
                {
                    _treeHot.Add(key, val, ref accessor);
                }
            }
            accessor.Dispose();
        });
    }

    /// <summary>
    /// Each thread operates on its own disjoint key range (spread across tree).
    /// Minimum leaf contention — threads touch completely different leaves.
    ///
    /// Real-world: independent entity updates across different regions/groups.
    /// No structural overlap between threads.
    ///
    /// Post-OLC: near-linear scaling since each thread only latches its own leaves.
    /// </summary>
    [Benchmark(OperationsPerInvoke = OpsPerInvocation)]
    public void SecondaryUpdate_SpreadKeys()
    {
        var opsPerThread = OpsPerInvocation / ThreadCount;
        BTreeBenchmarkHelper.RunConcurrentWithBarrier(ThreadCount, _epochManager, threadId =>
        {
            var accessor = _segSpread.CreateChunkAccessor();
            var keys = _perThreadSpreadKeys[threadId];
            for (int i = 0; i < opsPerThread; i++)
            {
                var key = keys[i % keys.Length];
                if (_treeSpread.Remove(key, out var val, ref accessor))
                {
                    _treeSpread.Add(key, val, ref accessor);
                }
            }
            accessor.Dispose();
        });
    }
}
