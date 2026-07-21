using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.IO;

namespace Typhon.Benchmark;

// ═══════════════════════════════════════════════════════════════════════
// BTree: Individual B+Tree Operation Microbenchmarks
// ═══════════════════════════════════════════════════════════════════════

[SimpleJob(warmupCount: 3, iterationCount: 5)]
[MemoryDiagnoser]
[BenchmarkCategory("BTree", "Regression")]
public class BTreeMicroBenchmarks
{
    private ServiceProvider _serviceProvider;
    private ManagedPagedMMF _pmmf;
    private EpochManager _epochManager;
    private ChunkBasedSegment<PersistentStore> _segment;
    private LongSingleBTree<PersistentStore> _tree;
    private string _databaseName;
    private int _epochDepth;

    private const int PreFillCount = 10_000;
    private long _nextInsertKey = PreFillCount + 1;
    private long _deleteKeyToggle;
    private long[] _randomInsertKeys;
    private int _randomInsertIndex;

    [GlobalSetup]
    public unsafe void GlobalSetup()
    {
        _databaseName = $"BTreeMicroBench_{Environment.ProcessId}";

        var dcs = 200 * 1024;
        dcs *= PagedMMF.PageSize;

        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
          .AddResourceRegistry()
          .AddMemoryAllocator()
          .AddEpochManager()
          .AddScopedManagedPagedMemoryMappedFile(options =>
          {
              options.DatabaseName = _databaseName;
              options.DatabaseCacheSize = (ulong)dcs;
              options.PagesDebugPattern = false;
          });

        _serviceProvider = sc.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        _pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        _epochManager = _serviceProvider.GetRequiredService<EpochManager>();

        _segment = _pmmf.AllocateChunkBasedSegment(PageBlockType.None, 500, sizeof(Index64Chunk));
        _epochDepth = _epochManager.EnterScope();

        _tree = new LongSingleBTree<PersistentStore>(_segment);

        // Pre-fill with 10,000 entries: keys 1..10000
        var accessor = _segment.CreateChunkAccessor();
        for (int i = 1; i <= PreFillCount; i++)
        {
            _tree.Add(i, i * 10, ref accessor);
        }
        accessor.Dispose();

        // Pre-generate random keys for Insert_Random benchmark.
        // Keys are within the existing range [1..PreFillCount] for remove-then-reinsert at random positions.
        const int randomKeyCount = 100_000;
        _randomInsertKeys = new long[randomKeyCount];
        var rng = new Random(42); // fixed seed for reproducibility
        for (int i = 0; i < randomKeyCount; i++)
        {
            _randomInsertKeys[i] = rng.NextInt64(1, PreFillCount + 1);
        }
        _randomInsertIndex = 0;
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _epochManager.ExitScope(_epochDepth);
        _epochManager?.Dispose();
        _pmmf?.Dispose();
        _serviceProvider?.Dispose();

        try { File.Delete($"{_databaseName}.bin"); } catch { }
    }

    /// <summary>
    /// Look up a key that exists in the tree. Measures B+Tree traversal for a hit.
    /// </summary>
    [Benchmark]
    public void Lookup_Hit()
    {
        var accessor = _segment.CreateChunkAccessor();
        _tree.TryGet(5000, ref accessor);
        accessor.Dispose();
    }

    /// <summary>
    /// Look up a key that does NOT exist. Measures traversal + failure path.
    /// </summary>
    [Benchmark]
    public void Lookup_Miss()
    {
        var accessor = _segment.CreateChunkAccessor();
        _tree.TryGet(-1, ref accessor);
        accessor.Dispose();
    }

    /// <summary>
    /// Insert a new sequential key (append fast-path). Measures best-case O(1) insert.
    /// </summary>
    [Benchmark]
    public void Insert_Sequential()
    {
        var accessor = _segment.CreateChunkAccessor();
        _tree.Add(_nextInsertKey++, 42, ref accessor);
        accessor.Dispose();
    }

    /// <summary>
    /// Remove a random key then reinsert it. The reinsert lands at a random tree position,
    /// exercising full tree traversal + leaf insert (not the O(1) append fast-path).
    /// OperationsPerInvoke=2 reports per-operation cost (one remove + one insert).
    /// </summary>
    [Benchmark(OperationsPerInvoke = 2)]
    public void Insert_Random()
    {
        var accessor = _segment.CreateChunkAccessor();
        var key = _randomInsertKeys[_randomInsertIndex++ % _randomInsertKeys.Length];
        _tree.Remove(key, out var val, ref accessor);
        _tree.Add(key, val, ref accessor);
        accessor.Dispose();
    }

    /// <summary>
    /// Delete a key then immediately re-insert it to maintain tree state.
    /// Reports the combined remove+reinsert cost as a single operation.
    /// </summary>
    [Benchmark]
    public void Delete_Reinsert()
    {
        var accessor = _segment.CreateChunkAccessor();
        var key = (_deleteKeyToggle++ & 1) == 0 ? 3000L : 7000L;
        if (_tree.Remove(key, out var val, ref accessor))
        {
            _tree.Add(key, val, ref accessor);
        }
        accessor.Dispose();
    }


    /// <summary>
    /// Read 100 consecutive keys. Measures sequential access locality in the B+Tree.
    /// </summary>
    [Benchmark(OperationsPerInvoke = 100)]
    public void SequentialScan_100()
    {
        var accessor = _segment.CreateChunkAccessor();
        for (int i = 1; i <= 100; i++)
        {
            _tree.TryGet(i, ref accessor);
        }
        accessor.Dispose();
    }
}
