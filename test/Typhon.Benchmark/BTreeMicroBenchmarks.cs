using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Runtime.CompilerServices;

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
    private long _moveToggle;
    private int _updateToggle;
    private readonly BTreeValueUpdate<long>[] _singleEntryBatch = new BTreeValueUpdate<long>[1];
    private long _moveCrossToggle;
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
        // #765 S8: punch two holes so the Move benchmarks have a free destination. The pre-fill is 1..10000 with no gaps, so without this there is no vacant
        // slot to move INTO and Move would return false without doing any work — a benchmark measuring a rejection. 4001 sits beside 4000 in the same leaf;
        // 8001 is far enough from 2000 to guarantee a different one.
        _tree.Remove(4001, out _, ref accessor);
        _tree.Remove(8001, out _, ref accessor);

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

        try { File.Delete($"{_databaseName}.bin"); }
        catch
        {
            // ignored
        }
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
    /// The pair #872 step 4 replaces: change a value under an UNCHANGED key by removing the entry and adding it back.
    /// </summary>
    /// <remarks>
    /// Two full root-to-leaf descents plus a structural remove and a structural insert, to move four bytes. This is the baseline
    /// <see cref="UpdateValue_SameKey"/> has to beat by 2x, and pairing them here — same tree, same key, same setup — is what makes the ratio mean
    /// something. Toggling the value keeps the tree in its initial state every two invocations so neither benchmark drifts.
    /// </remarks>
    [Benchmark]
    [BenchmarkCategory("Regression")]
    public void RemoveAdd_SameKey()
    {
        var accessor = _segment.CreateChunkAccessor();
        // Both halves are checked. Move_SameLeaf's own remarks are about exactly this hazard — a benchmark that quietly times a REJECTION reads as a very
        // fast implementation — and a ratio between two operations is only meaningful if both of them did the work.
        if (!_tree.Remove(5000, out _, ref accessor))
        {
            ThrowBenchmarkDidNoWork(nameof(RemoveAdd_SameKey));
        }

        _tree.Add(5000, (_updateToggle++ & 1) == 0 ? 50_000 : 50_001, ref accessor);
        accessor.Dispose();
    }

    /// <summary>
    /// In-place value update under an unchanged key — one descent and one 4-byte store (#872 step 4, AC-4.6).
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("Regression")]
    public void UpdateValue_SameKey()
    {
        var accessor = _segment.CreateChunkAccessor();
        if (!_tree.TryUpdateValue(5000, (_updateToggle++ & 1) == 0 ? 50_000 : 50_001, ref accessor))
        {
            ThrowBenchmarkDidNoWork(nameof(UpdateValue_SameKey));
        }

        accessor.Dispose();
    }

    /// <summary>
    /// #872 step 5, AC-5.6 — the BULK entry point at N = 1, which must not be slower than the single-entry path it generalises.
    /// </summary>
    /// <remarks>
    /// The pairing with <c>UpdateValue_SameKey</c> is the whole point: a batch of one walks the same nodes, so any difference is the bulk path's own overhead
    /// — the span setup, the generic applier, and the extra indirection through the partition loop. It should be at or below the single-entry cost, because it
    /// also skips that path's OLC version reads and validations.
    /// </remarks>
    [Benchmark]
    [BenchmarkCategory("Regression")]
    public void UpdateValues_SingleEntry()
    {
        var accessor = _segment.CreateChunkAccessor();
        _singleEntryBatch[0] = new BTreeValueUpdate<long>(5000, (_updateToggle++ & 1) == 0 ? 50_000 : 50_001);
        if (_tree.UpdateValues(_singleEntryBatch, ref accessor, out _) != 1)
        {
            ThrowBenchmarkDidNoWork(nameof(UpdateValues_SingleEntry));
        }

        accessor.Dispose();
    }

    /// <summary>Kept out of line so the check above is a predictable not-taken branch rather than inlined throw setup on the measured path.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowBenchmarkDidNoWork(string benchmark) =>
        throw new InvalidOperationException($"{benchmark} measured a rejected operation, not the operation — the tree is not in the shape it assumes.");

    /// <summary>
    /// Move a key to a free adjacent slot and back. Both keys live in the same leaf, which is what a spatial or positional index does on almost every update.
    /// </summary>
    /// <remarks>
    /// #765 S8. There was no <c>Move</c> benchmark anywhere in this project, which is the reason #221's original "40-50%" claim could never be checked and why
    /// the assessment had to re-derive it from source reading. Move is a compound operation with its own OLC protocol — two descents, a same-leaf fast path and
    /// a two-leaf path with ordered locking — and none of it was measured by anything.
    /// <para>
    /// The pair moves 4000 to 4001 and back, so the tree returns to its starting state every two invocations and the measurement cannot drift the way
    /// <c>ConcurrentInsert_Monotonic</c> does. <c>GlobalSetup</c> removes 4001 from the gapless pre-fill to create the vacant destination — without it, Move
    /// would find the key occupied, return false immediately, and the benchmark would be timing a rejection.
    /// </para>
    /// </remarks>
    [Benchmark]
    [BenchmarkCategory("Regression")]
    public void Move_SameLeaf()
    {
        var accessor = _segment.CreateChunkAccessor();
        // Alternate direction so the tree returns to its initial state every two invocations and the measurement cannot drift.
        if ((_moveToggle++ & 1) == 0)
        {
            _tree.Move(4000, 4001, 40_000, ref accessor);
        }
        else
        {
            _tree.Move(4001, 4000, 40_000, ref accessor);
        }
        accessor.Dispose();
    }

    /// <summary>
    /// Move a key to a slot far enough away that it lands in a different leaf, exercising the two-leaf path with ChunkId-ordered locking.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("Regression")]
    public void Move_CrossLeaf()
    {
        var accessor = _segment.CreateChunkAccessor();
        if ((_moveCrossToggle++ & 1) == 0)
        {
            _tree.Move(2000, 8001, 20_000, ref accessor);
        }
        else
        {
            _tree.Move(8001, 2000, 20_000, ref accessor);
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
