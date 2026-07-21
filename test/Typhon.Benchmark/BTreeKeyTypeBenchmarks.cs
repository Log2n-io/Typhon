using BenchmarkDotNet.Attributes;
using System;

namespace Typhon.Benchmark;

// ═══════════════════════════════════════════════════════════════════════
// BTree: Key Type Comparison — How key size affects traversal and insert
// Real-world: schema design choice — "which field type should I index?"
//
// Profile mapping:
//   Fast:   L64 only (covered by BTreeMicroBenchmarks)
//   Medium: all key types
//   Full:   all key types (same as Medium — this class has no scaling params)
// ═══════════════════════════════════════════════════════════════════════

[SimpleJob(warmupCount: 3, iterationCount: 5)]
[MemoryDiagnoser]
[BenchmarkCategory("BTree", "BTreeMedium")]
public class BTreeKeyTypeBenchmarks
{
    private BTreeBenchmarkHelper _helper;

    // L16 (short keys, 18 entries/node, SIMD search)
    private ChunkBasedSegment<PersistentStore> _segL16;
    private ShortSingleBTree<PersistentStore> _treeL16;

    // L32 (int keys, 14 entries/node, SIMD search)
    private ChunkBasedSegment<PersistentStore> _segL32;
    private IntSingleBTree<PersistentStore> _treeL32;

    // L64 (long keys, 9 entries/node)
    private ChunkBasedSegment<PersistentStore> _segL64;
    private LongSingleBTree<PersistentStore> _treeL64;

    // String64 (64-byte keys, 4 entries/node)
    private ChunkBasedSegment<PersistentStore> _segStr;
    private String64SingleBTree<PersistentStore> _treeStr;

    private const int PreFillCount = 10_000;

    // Pre-generated random keys for random insert benchmarks
    private short[] _randomShortKeys;
    private int[] _randomIntKeys;
    private long[] _randomLongKeys;
    private string[] _randomStrKeys;
    private int _rsiL16, _rsiL32, _rsiL64, _rsiStr;
    private int _seqL16Counter;
    private int _l16Headroom; // usable range above pre-fill
    private int _nextIntKey = PreFillCount + 1;
    private long _nextLongKey = PreFillCount + 1;
    private int _nextStrKey = PreFillCount + 1;

    [GlobalSetup]
    public unsafe void GlobalSetup()
    {
        _helper = new BTreeBenchmarkHelper();
        _helper.Setup(1000);

        _segL16 = _helper.AllocateSegment<Index16Chunk>(500);
        _segL32 = _helper.AllocateSegment<Index32Chunk>(500);
        _segL64 = _helper.AllocateSegment<Index64Chunk>(500);
        _segStr = _helper.AllocateSegment<IndexString64Chunk>(500);

        _treeL16 = new ShortSingleBTree<PersistentStore>(_segL16);
        _treeL32 = new IntSingleBTree<PersistentStore>(_segL32);
        _treeL64 = new LongSingleBTree<PersistentStore>(_segL64);
        _treeStr = new String64SingleBTree<PersistentStore>(_segStr);

        // Pre-fill count capped at short.MaxValue for L16
        var l16Count = Math.Min(PreFillCount, short.MaxValue - 1);
        _l16Headroom = short.MaxValue - 1 - l16Count; // keys available above pre-fill
        BTreeBenchmarkHelper.PreFillShort(_treeL16, _segL16, l16Count);
        BTreeBenchmarkHelper.PreFillInt(_treeL32, _segL32, PreFillCount);
        BTreeBenchmarkHelper.PreFillLong(_treeL64, _segL64, PreFillCount);
        BTreeBenchmarkHelper.PreFillString64(_treeStr, _segStr, PreFillCount);

        // Random keys for insert benchmarks (within existing range for remove+reinsert pattern)
        var rng = new Random(42);
        _randomShortKeys = new short[10_000];
        _randomIntKeys = new int[10_000];
        _randomLongKeys = new long[10_000];
        _randomStrKeys = new string[10_000];
        for (int i = 0; i < 10_000; i++)
        {
            _randomShortKeys[i] = (short)rng.Next(1, l16Count + 1);
            _randomIntKeys[i] = rng.Next(1, PreFillCount + 1);
            _randomLongKeys[i] = rng.NextInt64(1, PreFillCount + 1);
            _randomStrKeys[i] = $"key_{rng.Next(1, PreFillCount + 1):D5}";
        }
    }

    [GlobalCleanup]
    public void GlobalCleanup() => _helper?.Dispose();

    // ── Lookup Hit ──────────────────────────────────────────────────────

    [Benchmark]
    [BenchmarkCategory("Lookup")]
    public void Lookup_L16()
    {
        var accessor = _segL16.CreateChunkAccessor();
        _treeL16.TryGet(5000, ref accessor);
        accessor.Dispose();
    }

    [Benchmark]
    [BenchmarkCategory("Lookup")]
    public void Lookup_L32()
    {
        var accessor = _segL32.CreateChunkAccessor();
        _treeL32.TryGet(5000, ref accessor);
        accessor.Dispose();
    }

    [Benchmark]
    [BenchmarkCategory("Lookup")]
    public void Lookup_L64()
    {
        var accessor = _segL64.CreateChunkAccessor();
        _treeL64.TryGet(5000, ref accessor);
        accessor.Dispose();
    }

    [Benchmark]
    [BenchmarkCategory("Lookup")]
    public void Lookup_String64()
    {
        var accessor = _segStr.CreateChunkAccessor();
        _treeStr.TryGet("key_05000", ref accessor);
        accessor.Dispose();
    }

    // ── Insert Sequential (monotonic append) ────────────────────────────

    [Benchmark(OperationsPerInvoke = 2)]
    [BenchmarkCategory("Insert")]
    public void InsertSeq_L16()
    {
        var accessor = _segL16.CreateChunkAccessor();
        // Modular wrap within [l16Count+1, short.MaxValue-1] to avoid short overflow.
        // BDN pilots run 30K+ invocations, far exceeding the ~22K headroom.
        // Remove-before-Add handles wrap-around cycles.
        var offset = _seqL16Counter++ % _l16Headroom;
        var key = (short)(PreFillCount + 1 + offset);
        _treeL16.Remove(key, out _, ref accessor);
        _treeL16.Add(key, 42, ref accessor);
        accessor.Dispose();
    }

    [Benchmark]
    [BenchmarkCategory("Insert")]
    public void InsertSeq_L32()
    {
        var accessor = _segL32.CreateChunkAccessor();
        _treeL32.Add(_nextIntKey++, 42, ref accessor);
        accessor.Dispose();
    }

    [Benchmark]
    [BenchmarkCategory("Insert")]
    public void InsertSeq_L64()
    {
        var accessor = _segL64.CreateChunkAccessor();
        _treeL64.Add(_nextLongKey++, 42, ref accessor);
        accessor.Dispose();
    }

    [Benchmark]
    [BenchmarkCategory("Insert")]
    public void InsertSeq_String64()
    {
        var accessor = _segStr.CreateChunkAccessor();
        _treeStr.Add($"key_{_nextStrKey++:D5}", 42, ref accessor);
        accessor.Dispose();
    }

    // ── Insert Random (remove+reinsert at random position) ──────────────

    [Benchmark(OperationsPerInvoke = 2)]
    [BenchmarkCategory("Insert")]
    public void InsertRnd_L16()
    {
        var accessor = _segL16.CreateChunkAccessor();
        var key = _randomShortKeys[_rsiL16++ % _randomShortKeys.Length];
        _treeL16.Remove(key, out var val, ref accessor);
        _treeL16.Add(key, val, ref accessor);
        accessor.Dispose();
    }

    [Benchmark(OperationsPerInvoke = 2)]
    [BenchmarkCategory("Insert")]
    public void InsertRnd_L32()
    {
        var accessor = _segL32.CreateChunkAccessor();
        var key = _randomIntKeys[_rsiL32++ % _randomIntKeys.Length];
        _treeL32.Remove(key, out var val, ref accessor);
        _treeL32.Add(key, val, ref accessor);
        accessor.Dispose();
    }

    [Benchmark(OperationsPerInvoke = 2)]
    [BenchmarkCategory("Insert")]
    public void InsertRnd_L64()
    {
        var accessor = _segL64.CreateChunkAccessor();
        var key = _randomLongKeys[_rsiL64++ % _randomLongKeys.Length];
        _treeL64.Remove(key, out var val, ref accessor);
        _treeL64.Add(key, val, ref accessor);
        accessor.Dispose();
    }

    [Benchmark(OperationsPerInvoke = 2)]
    [BenchmarkCategory("Insert")]
    public void InsertRnd_String64()
    {
        var accessor = _segStr.CreateChunkAccessor();
        var key = _randomStrKeys[_rsiStr++ % _randomStrKeys.Length];
        _treeStr.Remove(key, out var val, ref accessor);
        _treeStr.Add(key, val, ref accessor);
        accessor.Dispose();
    }
}
