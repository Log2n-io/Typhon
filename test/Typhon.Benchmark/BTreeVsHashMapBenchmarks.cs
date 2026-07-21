using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.IO;

namespace Typhon.Benchmark;

// ═══════════════════════════════════════════════════════════════════════
// B+Tree vs HashMap: Bulk Insert Benchmarks
//
// Compares the cost of inserting N items into empty data structures.
// Both use long keys, int values, 256-byte chunks on PersistentStore.
// IterationSetup creates a fresh PMMF per iteration for isolation.
// ═══════════════════════════════════════════════════════════════════════

[SimpleJob(warmupCount: 2, iterationCount: 5)]
[MemoryDiagnoser]
[BenchmarkCategory("Index")]
public unsafe class BTreeVsHashMap_BulkBenchmarks
{
    [Params(1_000, 10_000, 100_000)]
    public int N;

    private ServiceProvider _sp;
    private ManagedPagedMMF _pmmf;
    private EpochManager _epochManager;
    private int _epochDepth;
    private string _dbName;

    /// <summary>Fisher-Yates shuffled keys [1..N] for random insert benchmarks.</summary>
    private long[] _randomOrder;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _randomOrder = new long[N];
        for (int i = 0; i < N; i++)
        {
            _randomOrder[i] = i + 1;
        }

        var rng = new Random(42);
        for (int i = _randomOrder.Length - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (_randomOrder[i], _randomOrder[j]) = (_randomOrder[j], _randomOrder[i]);
        }
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _dbName = $"BTreeVsHM_Bulk_{Environment.ProcessId}";

        var dcs = 200 * 1024;
        dcs *= PagedMMF.PageSize;

        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
          .AddResourceRegistry()
          .AddMemoryAllocator()
          .AddEpochManager()
          .AddScopedManagedPagedMemoryMappedFile(options =>
          {
              options.DatabaseName = _dbName;
              options.DatabaseCacheSize = (ulong)dcs;
              options.PagesDebugPattern = false;
          });

        _sp = sc.BuildServiceProvider();
        _sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
        _pmmf = _sp.GetRequiredService<ManagedPagedMMF>();
        _epochManager = _sp.GetRequiredService<EpochManager>();
        _epochDepth = _epochManager.EnterScope();
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        _epochManager.ExitScope(_epochDepth);
        _epochManager?.Dispose();
        _pmmf?.Dispose();
        _sp?.Dispose();
        try { File.Delete($"{_dbName}.bin"); } catch { }
    }

    // ─── Sequential insert: keys 1, 2, 3, ..., N ───────────────────

    [Benchmark]
    public void BTree_Sequential()
    {
        var segment = _pmmf.AllocateChunkBasedSegment(PageBlockType.None, 3000, sizeof(Index64Chunk));
        var tree = new LongSingleBTree<PersistentStore>(segment);
        var acc = segment.CreateChunkAccessor();
        for (int i = 1; i <= N; i++)
        {
            tree.Add(i, i * 10, ref acc);
        }
        acc.Dispose();
    }

    [Benchmark]
    public void HashMap_Sequential()
    {
        int stride = PagedHashMap<long, int, PersistentStore>.RecommendedStride();
        var segment = _pmmf.AllocateChunkBasedSegment(PageBlockType.None, 3000, stride);
        var map = PagedHashMap<long, int, PersistentStore>.Create(segment);
        var acc = segment.CreateChunkAccessor();
        for (int i = 1; i <= N; i++)
        {
            map.Insert(i, i * 10, ref acc, null);
        }
        acc.Dispose();
    }

    // ─── Random insert: shuffled keys ───────────────────────────────

    [Benchmark]
    public void BTree_Random()
    {
        var segment = _pmmf.AllocateChunkBasedSegment(PageBlockType.None, 3000, sizeof(Index64Chunk));
        var tree = new LongSingleBTree<PersistentStore>(segment);
        var acc = segment.CreateChunkAccessor();
        for (int i = 0; i < N; i++)
        {
            tree.Add(_randomOrder[i], (int)_randomOrder[i] * 10, ref acc);
        }
        acc.Dispose();
    }

    [Benchmark]
    public void HashMap_Random()
    {
        int stride = PagedHashMap<long, int, PersistentStore>.RecommendedStride();
        var segment = _pmmf.AllocateChunkBasedSegment(PageBlockType.None, 3000, stride);
        var map = PagedHashMap<long, int, PersistentStore>.Create(segment);
        var acc = segment.CreateChunkAccessor();
        for (int i = 0; i < N; i++)
        {
            map.Insert(_randomOrder[i], (int)_randomOrder[i] * 10, ref acc, null);
        }
        acc.Dispose();
    }
}

// ═══════════════════════════════════════════════════════════════════════
// B+Tree vs HashMap: Point Operation Benchmarks
//
// Measures per-operation latency on pre-filled data structures.
// Both are pre-loaded with N entries (keys 1..N) in GlobalSetup.
// Shows how O(log N) vs O(1) plays out at different tree sizes.
// ═══════════════════════════════════════════════════════════════════════

[SimpleJob(warmupCount: 3, iterationCount: 5)]
[MemoryDiagnoser]
[BenchmarkCategory("Index")]
public unsafe class BTreeVsHashMap_PointBenchmarks
{
    [Params(1_000, 10_000, 100_000)]
    public int N;

    private BTreeBenchmarkHelper _helper;

    // BTree
    private ChunkBasedSegment<PersistentStore> _btreeSegment;
    private LongSingleBTree<PersistentStore> _btree;

    // HashMap
    private ChunkBasedSegment<PersistentStore> _hmSegment;
    private PagedHashMap<long, int, PersistentStore> _pagedHashMap;

    /// <summary>Pre-generated random existing keys in [1..N].</summary>
    private long[] _randomKeys;
    private int _opIndex;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _helper = new BTreeBenchmarkHelper();
        _helper.Setup();

        // Allocate separate segments for each data structure
        _btreeSegment = _helper.Pmmf.AllocateChunkBasedSegment(PageBlockType.None, 3000, sizeof(Index64Chunk));
        int stride = PagedHashMap<long, int, PersistentStore>.RecommendedStride();
        _hmSegment = _helper.Pmmf.AllocateChunkBasedSegment(PageBlockType.None, 3000, stride);

        // Create + pre-fill BTree
        _btree = new LongSingleBTree<PersistentStore>(_btreeSegment);
        BTreeBenchmarkHelper.PreFillLong(_btree, _btreeSegment, N);

        // Create + pre-fill HashMap
        _pagedHashMap = PagedHashMap<long, int, PersistentStore>.Create(_hmSegment);
        {
            var acc = _hmSegment.CreateChunkAccessor();
            for (int i = 1; i <= N; i++)
            {
                _pagedHashMap.Insert(i, i * 10, ref acc, null);
            }
            acc.Dispose();
        }

        // Pre-generate random lookup keys (uniform over existing keys)
        var rng = new Random(42);
        _randomKeys = new long[100_000];
        for (int i = 0; i < _randomKeys.Length; i++)
        {
            _randomKeys[i] = rng.NextInt64(1, N + 1);
        }

        _opIndex = 0;
    }

    [GlobalCleanup]
    public void GlobalCleanup() => _helper?.Dispose();

    // ═══════════════════════════════════════════════════════════════════
    // Single Lookup: Hit (key exists)
    // ═══════════════════════════════════════════════════════════════════

    [Benchmark]
    public int BTree_Lookup_Hit()
    {
        var acc = _btreeSegment.CreateChunkAccessor();
        var r = _btree.TryGet(N / 2, ref acc);
        acc.Dispose();
        return r.Value;
    }

    [Benchmark]
    public int HashMap_Lookup_Hit()
    {
        var acc = _hmSegment.CreateChunkAccessor();
        _pagedHashMap.TryGet(N / 2, out var v, ref acc);
        acc.Dispose();
        return v;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Single Lookup: Miss (key does not exist)
    // ═══════════════════════════════════════════════════════════════════

    [Benchmark]
    public bool BTree_Lookup_Miss()
    {
        var acc = _btreeSegment.CreateChunkAccessor();
        var r = _btree.TryGet(-1, ref acc);
        acc.Dispose();
        return r.IsSuccess;
    }

    [Benchmark]
    public bool HashMap_Lookup_Miss()
    {
        var acc = _hmSegment.CreateChunkAccessor();
        var found = _pagedHashMap.TryGet(-1, out _, ref acc);
        acc.Dispose();
        return found;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Batch Random Lookup: 100 random existing keys per invocation
    // Reports per-operation cost via OperationsPerInvoke.
    // ═══════════════════════════════════════════════════════════════════

    [Benchmark(OperationsPerInvoke = 100)]
    public void BTree_Lookup_Random_100()
    {
        var acc = _btreeSegment.CreateChunkAccessor();
        int start = _opIndex;
        _opIndex = (start + 100) % _randomKeys.Length;
        for (int i = 0; i < 100; i++)
        {
            _btree.TryGet(_randomKeys[(start + i) % _randomKeys.Length], ref acc);
        }
        acc.Dispose();
    }

    [Benchmark(OperationsPerInvoke = 100)]
    public void HashMap_Lookup_Random_100()
    {
        var acc = _hmSegment.CreateChunkAccessor();
        int start = _opIndex;
        _opIndex = (start + 100) % _randomKeys.Length;
        for (int i = 0; i < 100; i++)
        {
            _pagedHashMap.TryGet(_randomKeys[(start + i) % _randomKeys.Length], out _, ref acc);
        }
        acc.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════════
    // Mutation: Remove + Reinsert a random existing key
    // Reports per-operation cost (remove + insert = 2 ops).
    // Maintains constant data structure size.
    // ═══════════════════════════════════════════════════════════════════

    [Benchmark(OperationsPerInvoke = 2)]
    public void BTree_Mutate_Random()
    {
        var acc = _btreeSegment.CreateChunkAccessor();
        var key = _randomKeys[_opIndex++ % _randomKeys.Length];
        if (_btree.Remove(key, out var val, ref acc))
        {
            _btree.Add(key, val, ref acc);
        }
        acc.Dispose();
    }

    [Benchmark(OperationsPerInvoke = 2)]
    public void HashMap_Mutate_Random()
    {
        var acc = _hmSegment.CreateChunkAccessor();
        var key = _randomKeys[_opIndex++ % _randomKeys.Length];
        if (_pagedHashMap.Remove(key, out var val, ref acc, null))
        {
            _pagedHashMap.Insert(key, val, ref acc, null);
        }
        acc.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════════
    // Range Scan: 100 consecutive keys (BTree-only capability)
    // Included to show B+Tree's ordered-traversal advantage.
    // ═══════════════════════════════════════════════════════════════════

    [Benchmark(OperationsPerInvoke = 100)]
    public int BTree_RangeScan_100()
    {
        long start = N / 2;
        int count = 0;
        foreach (var item in _btree.EnumerateRange(start, start + 99))
        {
            count++;
        }
        return count;
    }
}

// ═══════════════════════════════════════════════════════════════════════
// RawValueHashMap: Isolating the memcpy overhead at EntityMap record sizes
//
// RawValueHashMap is what EntityMap actually uses. Unlike typed HashMap,
// every TryGet does Unsafe.CopyBlock of the full record to a stack buffer.
// This benchmark measures that cost at realistic entity record sizes:
//   18B = 1 component,  26B = 3 components,
//   46B = 8 components, 78B = 16 components (max)
// Typed HashMap<long,int> is included as a zero-copy baseline.
// ═══════════════════════════════════════════════════════════════════════

[SimpleJob(warmupCount: 3, iterationCount: 5)]
[MemoryDiagnoser]
[BenchmarkCategory("Index")]
public unsafe class RawValueHashMap_PointBenchmarks
{
    /// <summary>Entity record size: 14B header + 4B × componentCount.</summary>
    [Params(18, 26, 46, 78)]
    public int ValueSize;

    private const int N = 10_000;

    private BTreeBenchmarkHelper _helper;

    // RawValueHashMap (entity-record style)
    private ChunkBasedSegment<PersistentStore> _rawSegment;
    private RawValuePagedHashMap<long, PersistentStore> _rawMap;

    // Typed HashMap baseline (4-byte int value, same hash function)
    private ChunkBasedSegment<PersistentStore> _typedSegment;
    private PagedHashMap<long, int, PersistentStore> _typedMap;

    private long[] _randomKeys;
    private int _opIndex;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _helper = new BTreeBenchmarkHelper();
        _helper.Setup();

        // ─── RawValueHashMap with variable-size records ─────────────
        int rawStride = RawValuePagedHashMap<long, PersistentStore>.RecommendedStride(ValueSize);
        _rawSegment = _helper.Pmmf.AllocateChunkBasedSegment(PageBlockType.None, 3000, rawStride);
        _rawMap = RawValuePagedHashMap<long, PersistentStore>.Create(_rawSegment, 64, ValueSize);

        {
            byte* buf = stackalloc byte[ValueSize];
            var acc = _rawSegment.CreateChunkAccessor();
            for (int i = 1; i <= N; i++)
            {
                *(long*)buf = i * 10;
                _rawMap.Insert(i, buf, ref acc, null);
            }
            acc.Dispose();
        }

        // ─── Typed HashMap baseline ─────────────────────────────────
        int typedStride = PagedHashMap<long, int, PersistentStore>.RecommendedStride();
        _typedSegment = _helper.Pmmf.AllocateChunkBasedSegment(PageBlockType.None, 3000, typedStride);
        _typedMap = PagedHashMap<long, int, PersistentStore>.Create(_typedSegment);

        {
            var acc = _typedSegment.CreateChunkAccessor();
            for (int i = 1; i <= N; i++)
            {
                _typedMap.Insert(i, i * 10, ref acc, null);
            }
            acc.Dispose();
        }

        // Random existing keys
        var rng = new Random(42);
        _randomKeys = new long[100_000];
        for (int i = 0; i < _randomKeys.Length; i++)
        {
            _randomKeys[i] = rng.NextInt64(1, N + 1);
        }
        _opIndex = 0;
    }

    [GlobalCleanup]
    public void GlobalCleanup() => _helper?.Dispose();

    // ─── RawMap: TryGet (copies record to stack buffer) ─────────────

    [Benchmark]
    public bool RawMap_TryGet()
    {
        byte* buf = stackalloc byte[ValueSize];
        var acc = _rawSegment.CreateChunkAccessor();
        var key = _randomKeys[_opIndex++ % _randomKeys.Length];
        var found = _rawMap.TryGet(key, buf, ref acc);
        acc.Dispose();
        return found;
    }

    // ─── RawMap: TryGetPtr (zero-copy, returns pointer into page) ───

    [Benchmark]
    public long RawMap_TryGetPtr()
    {
        var acc = _rawSegment.CreateChunkAccessor();
        var key = _randomKeys[_opIndex++ % _randomKeys.Length];
        var ptr = _rawMap.TryGetPtr(key, ref acc);
        long val = ptr != null ? *(long*)ptr : 0;
        acc.Dispose();
        return val;
    }

    // ─── Typed HashMap baseline (4-byte value, no memcpy) ───────────

    [Benchmark]
    public int TypedMap_TryGet()
    {
        var acc = _typedSegment.CreateChunkAccessor();
        var key = _randomKeys[_opIndex++ % _randomKeys.Length];
        _typedMap.TryGet(key, out var val, ref acc);
        acc.Dispose();
        return val;
    }

    // ─── RawMap: Remove + Reinsert ──────────────────────────────────

    [Benchmark(OperationsPerInvoke = 2)]
    public void RawMap_Mutate()
    {
        byte* buf = stackalloc byte[ValueSize];
        var acc = _rawSegment.CreateChunkAccessor();
        var key = _randomKeys[_opIndex++ % _randomKeys.Length];
        if (_rawMap.Remove(key, ref acc, null))
        {
            *(long*)buf = key * 10;
            _rawMap.Insert(key, buf, ref acc, null);
        }
        acc.Dispose();
    }

    // ─── Typed HashMap: Remove + Reinsert ───────────────────────────

    [Benchmark(OperationsPerInvoke = 2)]
    public void TypedMap_Mutate()
    {
        var acc = _typedSegment.CreateChunkAccessor();
        var key = _randomKeys[_opIndex++ % _randomKeys.Length];
        if (_typedMap.Remove(key, out var val, ref acc, null))
        {
            _typedMap.Insert(key, val, ref acc, null);
        }
        acc.Dispose();
    }
}
