using BenchmarkDotNet.Attributes;

namespace Typhon.Benchmark;

// ═══════════════════════════════════════════════════════════════════════
// BTree: Secondary Index Update Patterns — The exact operations
// IndexMaintainer.UpdateIndices() performs during transaction commit.
// These are the "before" numbers for the OLC Move/MoveValue optimization.
//
// Real-world:
//   SingleIndex_Update:       entity.Position.X changed (unique index)
//   MultiIndex_Update_4Ops:   entity.TeamId changed (non-unique index with TAIL)
//   SmallDelta_Update:        position nudge (+3), status transition (same leaf)
//   LargeDelta_Update:        entity reassigned to different group (cross-leaf)
//
// Profile mapping:
//   Fast:   SmallDelta_Update, LargeDelta_Update only
//   Medium: all benchmarks
//   Full:   all benchmarks (same as Medium — no scaling params)
// ═══════════════════════════════════════════════════════════════════════

[SimpleJob(warmupCount: 3, iterationCount: 5)]
[MemoryDiagnoser]
[BenchmarkCategory("BTree", "BTreeMedium")]
public class BTreeSecondaryIndexBenchmarks
{
    private BTreeBenchmarkHelper _helper;

    // Single-value index (unique secondary index like entity.Name)
    private ChunkBasedSegment<PersistentStore> _segSingle;
    private IntSingleBTree<PersistentStore> _treeSingle;

    // Multi-value index (non-unique like entity.TeamId)
    private ChunkBasedSegment<PersistentStore> _segMulti;
    private IntMultipleBTree<PersistentStore> _treeMulti;

    // Element IDs returned by Add for multi-value benchmarks
    private int[] _multiElementIds;

    private const int PreFillCount = 10_000;
    private int[] _randomKeys;
    private int _rkIndex;
    private int _smallDeltaToggle;
    private int _largeDeltaToggle;

    [GlobalSetup]
    public unsafe void GlobalSetup()
    {
        _helper = new BTreeBenchmarkHelper();
        _helper.Setup(1000);

        _segSingle = _helper.AllocateSegment<Index32Chunk>(500);
        _segMulti = _helper.AllocateSegment<Index32Chunk>(500);

        _treeSingle = new IntSingleBTree<PersistentStore>(_segSingle);
        _treeMulti = new IntMultipleBTree<PersistentStore>(_segMulti);

        BTreeBenchmarkHelper.PreFillInt(_treeSingle, _segSingle, PreFillCount);

        // Pre-fill multi-value tree: each key [1..PreFillCount] gets one value
        _multiElementIds = new int[PreFillCount + 1];
        var accessor = _segMulti.CreateChunkAccessor();
        for (int i = 1; i <= PreFillCount; i++)
        {
            _multiElementIds[i] = _treeMulti.Add(i, i * 10, ref accessor);
        }
        accessor.Dispose();

        _randomKeys = BTreeBenchmarkHelper.GenerateRandomIntKeys(10_000, PreFillCount);

        // Remove "landing" keys for SmallDelta/LargeDelta toggle benchmarks.
        // The toggle alternates between two keys (e.g., 5000↔5003), so the
        // destination key must NOT exist initially. Pre-fill created all keys
        // 1..10000, so we remove the destinations here.
        var setupAccessor = _segSingle.CreateChunkAccessor();
        _treeSingle.Remove(5003, out _, ref setupAccessor);  // SmallDelta landing
        _treeSingle.Remove(7000, out _, ref setupAccessor);  // LargeDelta landing
        setupAccessor.Dispose();
    }

    [GlobalCleanup]
    public void GlobalCleanup() => _helper?.Dispose();

    // ── Single-value index: Remove + Add (current UpdateIndices pattern) ─

    /// <summary>
    /// The current IndexMaintainer pattern for single-value secondary indexes:
    /// Remove(oldKey) + Add(newKey). Two lock cycles, two traversals.
    /// This is the baseline that Move will replace.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("SecondaryIndex")]
    public void SingleIndex_Update()
    {
        var accessor = _segSingle.CreateChunkAccessor();
        var key = _randomKeys[_rkIndex++ % _randomKeys.Length];
        if (_treeSingle.Remove(key, out var val, ref accessor))
        {
            _treeSingle.Add(key, val, ref accessor);
        }
        accessor.Dispose();
    }

    // ── Multi-value index: RemoveValue + TryGet + Add + TryGet (4 ops) ──

    /// <summary>
    /// The current IndexMaintainer pattern for AllowMultiple indexes with TAIL tracking:
    /// 1. RemoveValue(oldKey, elemId, value) — exclusive lock
    /// 2. TryGet(oldKey) — shared lock (get HEAD buffer ID for TAIL tombstone)
    /// 3. Add(newKey, value) — exclusive lock
    /// 4. TryGet(newKey) — shared lock (get HEAD buffer ID for TAIL active entry)
    /// Four lock cycles, four traversals. MoveValue collapses this to one.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("SecondaryIndex")]
    public void MultiIndex_Update_4Ops()
    {
        var accessor = _segMulti.CreateChunkAccessor();
        var key = _randomKeys[_rkIndex++ % _randomKeys.Length];
        var elemId = _multiElementIds[key];

        // Step 1: RemoveValue
        _treeMulti.RemoveValue(key, elemId, key * 10, ref accessor);

        // Step 2: TryGet for old HEAD buffer ID
        _treeMulti.TryGet(key, ref accessor);

        // Step 3: Add with new key (reinsert same key to keep tree stable)
        var newElemId = _treeMulti.Add(key, key * 10, ref accessor);
        _multiElementIds[key] = newElemId;

        // Step 4: TryGet for new HEAD buffer ID
        _treeMulti.TryGet(key, ref accessor);

        accessor.Dispose();
    }

    // ── Multi-value: standalone insert ──────────────────────────────────

    /// <summary>
    /// Adding a new entity to a non-unique index (e.g., joining a team).
    /// Single Add operation on an AllowMultiple tree.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("SecondaryIndex")]
    public void MultiIndex_Insert()
    {
        var accessor = _segMulti.CreateChunkAccessor();
        // Add to a random existing key (appends to its buffer)
        var key = _randomKeys[_rkIndex++ % _randomKeys.Length];
        var elemId = _treeMulti.Add(key, 99999, ref accessor);
        // Remove it to keep tree stable
        _treeMulti.RemoveValue(key, elemId, 99999, ref accessor);
        accessor.Dispose();
    }

    // ── Multi-value: standalone lookup ──────────────────────────────────

    /// <summary>
    /// Query all entities in a group (e.g., "all entities on team 42").
    /// TryGetMultiple on an AllowMultiple tree.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("SecondaryIndex")]
    public void MultiIndex_Lookup()
    {
        var accessor = _segMulti.CreateChunkAccessor();
        var key = _randomKeys[_rkIndex++ % _randomKeys.Length];
        using var a = _treeMulti.TryGetMultiple(key, ref accessor);
        if (a.IsValid)
        {
            do
            {
                _ = a.ReadOnlyElements.Length;
            } while (a.NextChunk());
        }
        accessor.Dispose();
    }

    // ── Small delta: same-leaf likely (position nudge, status transition) ─

    /// <summary>
    /// Key changes by ±3. In a 10K-entry L32 tree (14 keys/node), keys within
    /// ~14 of each other are likely in the same leaf. This is the fast path
    /// for Move (same-leaf: 1 traversal, 1 lock).
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("SecondaryIndex", "BTreeFast")]
    public void SmallDelta_Update()
    {
        var accessor = _segSingle.CreateChunkAccessor();
        // Alternate between two keys that are 3 apart
        var toggle = _smallDeltaToggle++ & 1;
        var oldKey = toggle == 0 ? 5000 : 5003;
        var newKey = toggle == 0 ? 5003 : 5000;

        if (_treeSingle.Remove(oldKey, out var val, ref accessor))
        {
            _treeSingle.Add(newKey, val, ref accessor);
        }
        accessor.Dispose();
    }

    // ── Large delta: cross-leaf certain (group reassignment) ─────────────

    /// <summary>
    /// Key changes by ±5000. In a 10K-entry tree, keys 5000 apart are in
    /// completely different leaves (and likely different subtrees). This is
    /// the different-leaf path for Move (2 traversals, 2 locks).
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("SecondaryIndex", "BTreeFast")]
    public void LargeDelta_Update()
    {
        var accessor = _segSingle.CreateChunkAccessor();
        // Alternate between two keys that are 5000 apart
        var toggle = _largeDeltaToggle++ & 1;
        var oldKey = toggle == 0 ? 2000 : 7000;
        var newKey = toggle == 0 ? 7000 : 2000;

        if (_treeSingle.Remove(oldKey, out var val, ref accessor))
        {
            _treeSingle.Add(newKey, val, ref accessor);
        }
        accessor.Dispose();
    }
}
