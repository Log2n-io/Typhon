using BenchmarkDotNet.Attributes;

namespace Typhon.Benchmark;

// ═══════════════════════════════════════════════════════════════════════
// BTree: Tree Depth Impact — How performance degrades as entity count grows
// Real-world: "my table has 100K entities, how much slower are lookups?"
//
// Profile mapping:
//   Fast:   not included
//   Medium: TreeSize = [1_000, 10_000]
//   Full:   TreeSize = [100, 1_000, 10_000, 100_000]
// ═══════════════════════════════════════════════════════════════════════

[SimpleJob(warmupCount: 3, iterationCount: 5)]
[MemoryDiagnoser]
[BenchmarkCategory("BTree", "BTreeFull")]
public class BTreeScaleBenchmarks
{
    private BTreeBenchmarkHelper _helper;
    private ChunkBasedSegment<PersistentStore> _segment;
    private LongSingleBTree<PersistentStore> _tree;
    private long[] _randomKeys;
    private int _randomIndex;
    private long _nextSeqKey;

    [Params(100, 1_000, 10_000, 100_000)]
    public int TreeSize;

    [GlobalSetup]
    public unsafe void GlobalSetup()
    {
        _helper = new BTreeBenchmarkHelper();
        // 100K entries need more pages: ~12K nodes for L64 (9 keys/node)
        var pages = TreeSize >= 100_000 ? 5000 : 500;
        _helper.Setup(pages);

        _segment = _helper.AllocateSegment<Index64Chunk>(pages);
        _tree = new LongSingleBTree<PersistentStore>(_segment);
        BTreeBenchmarkHelper.PreFillLong(_tree, _segment, TreeSize);

        _randomKeys = BTreeBenchmarkHelper.GenerateRandomLongKeys(10_000, TreeSize);
        _randomIndex = 0;
        _nextSeqKey = TreeSize + 1;
    }

    [GlobalCleanup]
    public void GlobalCleanup() => _helper?.Dispose();

    /// <summary>
    /// Lookup in trees of varying depth. At 100 entries: ~2 levels. At 100K: ~5-6 levels.
    /// Each extra level is one more cache line access.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("Lookup")]
    public void Lookup_Hit()
    {
        var accessor = _segment.CreateChunkAccessor();
        var key = _randomKeys[_randomIndex++ % _randomKeys.Length];
        _tree.TryGet(key, ref accessor);
        accessor.Dispose();
    }

    /// <summary>
    /// Random insert at varying tree sizes. Deeper trees require more traversal steps.
    /// Uses remove+reinsert to maintain stable tree size across iterations.
    /// </summary>
    [Benchmark(OperationsPerInvoke = 2)]
    [BenchmarkCategory("Insert")]
    public void Insert_Random()
    {
        var accessor = _segment.CreateChunkAccessor();
        var key = _randomKeys[_randomIndex++ % _randomKeys.Length];
        _tree.Remove(key, out var val, ref accessor);
        _tree.Add(key, val, ref accessor);
        accessor.Dispose();
    }
}
