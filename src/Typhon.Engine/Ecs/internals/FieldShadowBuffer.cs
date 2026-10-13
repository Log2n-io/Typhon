using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Captured old index key for a single entity-field pair, stored before the first SV in-place mutation per tick.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ShadowEntry
{
    public int ChunkId;

    /// <summary>Full <see cref="EntityId"/> of the mutated entity — typed, not a bare key or chunk id (issue #660).</summary>
    public EntityId EntityPK;

    public KeyBytes8 OldKey;
}

/// <summary>
/// Per-indexed-field append buffer that captures old index keys before SV in-place mutations.
/// <para>
/// <b>Write path (concurrent):</b> <see cref="Append"/> is called from <c>EntityRefMut.Write&lt;T&gt;()</c> on the first mutation per entity per tick
/// (guarded by <see cref="DirtyBitmap.TestAndSet"/>).
/// Multiple threads may append concurrently for different entities.
/// </para>
/// <para>
/// <b>Tick boundary (single-threaded):</b> Consumer iterates <see cref="Count"/> entries via indexer, then calls <see cref="Reset"/>. No concurrent appends
/// during drain (tick boundary is a sync point).
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// <b>Storage is segmented, and that is load-bearing (#558).</b> Entries live in fixed-size blocks reached by index; a block is allocated on first use and is
/// never moved, resized or freed. That is what lets <see cref="Append"/> be wait-free: a writer reserves its index with a single
/// <see cref="Interlocked.Increment(ref int)"/> and writes into a slot no other writer can claim, with no lock and no chance of a concurrent grow relocating
/// the array underneath it.
/// </para>
/// <para>
/// The previous implementation was a single array guarded by a per-append lock. On a 20 001-entity archetype at 120 Hz that lock measured
/// <c>Total 17 309 ms / Own 107 ms</c> — a ten-instruction body spending 99 % of its wall time blocked, and the largest single cost in the engine, ~73x the
/// entire tick fence. Every parallel system write funnelled through one lock per indexed field.
/// </para>
/// <para>
/// A flat pre-sized array is NOT a valid alternative: the gate that admits appends (<see cref="DirtyBitmap.TestAndSet"/>) grows on demand, so after a segment
/// grows more entities can shadow than any capacity computed at construction allowed.
/// </para>
/// <para>
/// <b>Memory-ordering contract.</b> The reservation increments the count BEFORE the entry is written (the old lock-based version incremented it after). Reads
/// are therefore only valid at the tick boundary, once the tick has drained and every writer has retired — which is exactly the contract
/// <see cref="Count"/> and the indexer already document. Do not read this buffer concurrently with appends.
/// </para>
/// </remarks>
internal sealed class FieldShadowBuffer
{
    /// <summary>log2 of the entries per block. 4096 x 24 B = 96 KB per block — big enough that crossing a boundary is rare, small enough not to waste memory
    /// on archetypes that shadow a handful of entities per tick.</summary>
    private const int BlockShift = 12;
    private const int BlockSize = 1 << BlockShift;
    private const int BlockMask = BlockSize - 1;

    /// <summary>Block table. Replaced wholesale when it needs to grow; existing block references are copied across, so a writer holding an older table still
    /// resolves the same arrays for every index it can address.</summary>
    private ShadowEntry[][] _blocks;
    private int _count;
    private readonly Lock _growLock = new();

    internal FieldShadowBuffer(int initialCapacity = 256)
    {
        var blockCount = Math.Max(1, (initialCapacity + BlockSize - 1) / BlockSize);
        _blocks = new ShadowEntry[blockCount][];
        _blocks[0] = new ShadowEntry[BlockSize];
    }

    /// <summary>Append a shadow entry. Wait-free: one interlocked reservation, then a write into a slot owned exclusively by this call.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Append(int chunkId, EntityId entityPK, KeyBytes8 oldKey)
    {
        var index = Interlocked.Increment(ref _count) - 1;
        var blockIndex = index >> BlockShift;

        var blocks = _blocks;
        var block = blockIndex < blocks.Length ? blocks[blockIndex] : null;
        if (block == null)
        {
            block = EnsureBlock(blockIndex);
        }

        block[index & BlockMask] = new ShadowEntry { ChunkId = chunkId, EntityPK = entityPK, OldKey = oldKey };
    }

    /// <summary>
    /// Reserves <paramref name="count"/> consecutive entries in one interlocked operation and returns the first one's index: the batched capture's append
    /// (#1346). Each is written through <see cref="WriteAt"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int Reserve(int count) => Interlocked.Add(ref _count, count) - count;

    /// <summary>Writes the entry at <paramref name="index"/>, an index <see cref="Reserve"/> handed out, allocating its block on first use.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void WriteAt(int index, int chunkId, EntityId entityPK, KeyBytes8 oldKey)
    {
        var blockIndex = index >> BlockShift;
        var blocks = _blocks;
        var block = blockIndex < blocks.Length ? blocks[blockIndex] : null;
        if (block == null)
        {
            block = EnsureBlock(blockIndex);
        }

        block[index & BlockMask] = new ShadowEntry { ChunkId = chunkId, EntityPK = entityPK, OldKey = oldKey };
    }

    /// <summary>Allocates the block for <paramref name="blockIndex"/>, growing the block table if needed. Off the hot path — reached only the first time a
    /// tick's appends cross into a new block.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private ShadowEntry[] EnsureBlock(int blockIndex)
    {
        lock (_growLock)
        {
            var blocks = _blocks;
            if (blockIndex >= blocks.Length)
            {
                var newLength = blocks.Length;
                while (newLength <= blockIndex)
                {
                    newLength *= 2;
                }

                // Copy the block REFERENCES into a wider table. The blocks themselves are untouched, so a writer that already read the old table keeps a
                // valid reference for every index it could have reserved against it.
                var grown = new ShadowEntry[newLength][];
                blocks.CopyTo(grown, 0);
                _blocks = grown;
                blocks = grown;
            }

            return blocks[blockIndex] ??= new ShadowEntry[BlockSize];
        }
    }

    /// <summary>Number of entries. Read at tick boundary (no concurrent appends).</summary>
    internal int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _count;
    }

    /// <summary>Access entry by index. Read at tick boundary (no concurrent appends).</summary>
    internal ref ShadowEntry this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref _blocks[index >> BlockShift][index & BlockMask];
    }

    /// <summary>Reset count to zero for next tick. Not thread-safe — call only at tick boundary. Allocated blocks are retained, so a steady-state tick
    /// allocates nothing.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Reset()
    {
        _count = 0;
        _moveCount = 0;
        MoveRangeCount = 0;
        MoveRangeFailure = null;
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // Staged moves (#1346). The drain no longer moves an index entry per changed key: it stages the move here, and the whole field's moves are applied in
    // one batch (BTreeBase.ApplyMoves) — at the end of the field's drain on the atomic path; after the Prep slices, in key ranges on the workers (the
    // IndexMoves phase), finished by the serial tail. At most one move per entry, so the entry count bounds them.
    // ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
    private IndexMove[] _moves = [];
    private int _moveCount;

    /// <summary>
    /// The staged moves' batch as the parallel fence's IndexMoves phase prepared it — the tree's leaf-disjoint key ranges, one work item each — or
    /// <see cref="MoveRangeCount"/> zero when no phase did (the serial and atomic paths apply the batch whole). The tail finishes a prepared batch.
    /// </summary>
    internal IndexMoveRange[] MoveRanges = [];
    internal int MoveRangeCount;

    /// <summary>The first exception a range's work item raised. One that faulted the tree is raised again by the tree itself; one that did not left the
    /// range unapplied, and the tail applies it and raises this once the batch is finished, so the fence fails loudly without a batch half applied.</summary>
    internal Exception MoveRangeFailure;

    /// <summary>Empties the staged moves once their batch is applied, keeping the staging's size for the next tick (<see cref="PrepareMoves"/> sizes it,
    /// and gives a burst's back, at the drain's start).</summary>
    internal void ClearStagedMoves()
    {
        _moveCount = 0;
        _stagedAtCount = 0;
        MoveRangeCount = 0;
        MoveRangeFailure = null;
    }

    private bool[] _stagedAt = [];
    private int _stagedAtCount;   // > 0 while the slices' staging awaits its compaction

    /// <summary>Sizes the staging for <paramref name="count"/> entries and empties it. Single-threaded: the atomic drain's field start.</summary>
    internal void PrepareMoves(int count)
    {
        if (_moves.Length < count)
        {
            _moves = new IndexMove[Math.Max(count, Math.Max(256, _moves.Length * 2))];
        }
        else if (_moves.Length > 65_536 && _moves.Length > 8L * count)
        {
            _moves = new IndexMove[Math.Max(count, 256)];   // a burst passed: give its staging back rather than hold it for good
        }

        _moveCount = 0;
        _stagedAtCount = 0;
        MoveRangeCount = 0;
        MoveRangeFailure = null;
    }

    /// <summary>
    /// Sizes the slices' staging for the plan's <paramref name="count"/> entries: a slice stages a move at its entry's plan position, which only it owns, so
    /// no write is shared and the moves stay in cluster order. <see cref="CompactStagedMoves"/> packs them for the tail. Single-threaded: the Prep head.
    /// </summary>
    internal void PrepareMovesAtPlanPositions(int count)
    {
        PrepareMoves(count);
        if (_stagedAt.Length < count)
        {
            _stagedAt = new bool[Math.Max(count, Math.Max(256, _stagedAt.Length * 2))];   // geometric: a growing population reallocates it rarely
        }
        else if (_stagedAt.Length > 65_536 && _stagedAt.Length > 8L * count)
        {
            _stagedAt = new bool[Math.Max(count, 256)];
        }
        else
        {
            Array.Clear(_stagedAt, 0, count);
        }

        _stagedAtCount = count;
    }

    /// <summary>Stages a move, from the one thread draining this buffer (the atomic path).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void StageMove(in IndexMove move) => _moves[_moveCount++] = move;

    /// <summary>Stages a move from a Prep slice at its entry's plan position (see <see cref="PrepareMovesAtPlanPositions"/>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void StageMoveAt(int planPosition, in IndexMove move)
    {
        _moves[planPosition] = move;
        _stagedAt[planPosition] = true;
    }

    /// <summary>The plan position of cluster <paramref name="firstCluster"/>'s first entry: where a slice starting there stages.</summary>
    internal int PlanPositionOf(int firstCluster) => _planSpan == 0 ? 0 : StartOf(firstCluster);

    /// <summary>Packs the moves the slices staged at their plan positions, in plan order, once every slice retired (the phase join). Once: a second call
    /// finds nothing left to pack and keeps the packed moves.</summary>
    internal void CompactStagedMoves()
    {
        if (_stagedAtCount == 0)
        {
            return;
        }

        var n = 0;
        for (var i = 0; i < _stagedAtCount; i++)
        {
            if (_stagedAt[i])
            {
                _moves[n++] = _moves[i];
            }
        }

        _moveCount = n;
        _stagedAtCount = 0;
    }

    /// <summary>The moves staged this tick. Read once every stager has retired (and, on the sliced path, after <see cref="CompactStagedMoves"/>).</summary>
    internal Span<IndexMove> StagedMoves => _moves.AsSpan(0, _moveCount);

    // ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // Per-tick drain plan (#886 lead D). Built ONCE by the Prep head, read by every Prep slice, cleared by the tail.
    //
    // A slice drains only the entries of its own clusters. Having every slice scan the whole buffer to find them was measured at 13× the drain's CPU
    // (three passes over 16 000 entries, times 31 slices, per field, per tick), which is more than the whole atomic drain cost. So the ordering is done once,
    // serially, and what a slice gets is a contiguous range of an order that is already ascending by cluster: the histogram's end offsets, kept rather than
    // cleared, are exactly the per-cluster boundaries the range lookup needs.
    // ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
    private int[] _planOrder = [];
    private int[] _planEnds = [];
    private int _planMin;
    private int _planSpan;
    private int _planCount;

    /// <summary>Orders the buffer's entries by ascending cluster chunk id and keeps the per-cluster boundaries. Same algorithm as
    /// <c>ArchetypeClusterState.BuildDrainOrder</c>; the difference is what is retained.</summary>
    internal void BuildDrainPlan()
    {
        // The histogram must start clean. The tail clears it (ClearDrainPlan), but a throw between head and tail — a non-finite position, a fault —
        // skips the tail, and a build on top of last tick's end offsets scatters past the order array. Clearing here is the same memset and makes the
        // build idempotent.
        ClearDrainPlan();
        var count = _count;
        PrepareMovesAtPlanPositions(count);   // the slices stage this tick's moves at their plan positions, in a staging sized from the same count
        _planCount = count;
        if (count <= 0)
        {
            return;
        }

        if (_planOrder.Length < count)
        {
            _planOrder = new int[Math.Max(count, Math.Max(256, _planOrder.Length * 2))];
        }

        var min = int.MaxValue;
        var max = int.MinValue;
        for (var e = 0; e < count; e++)
        {
            var c = this[e].ChunkId >> 6;
            if (c < min)
            {
                min = c;
            }

            if (c > max)
            {
                max = c;
            }
        }

        var span = max - min + 1;
        if (_planEnds.Length < span)
        {
            _planEnds = new int[Math.Max(span, Math.Max(256, _planEnds.Length * 2))];
        }

        var ends = _planEnds;
        for (var e = 0; e < count; e++)
        {
            ends[(this[e].ChunkId >> 6) - min]++;
        }

        var running = 0;
        for (var b = 0; b < span; b++)
        {
            var c = ends[b];
            ends[b] = running;
            running += c;
        }

        var order = _planOrder;
        for (var e = 0; e < count; e++)
        {
            order[ends[(this[e].ChunkId >> 6) - min]++] = e;
        }

        // After the scatter ends[b] is the END of bucket b — the start of bucket b + 1 — which is what DrainOrderForClusters slices on.
        _planMin = min;
        _planSpan = span;
    }

    /// <summary>The planned order's entries whose cluster chunk id lies in <c>[firstCluster, lastClusterExclusive)</c> — a contiguous range, in ascending
    /// cluster order. Empty when no plan was built.</summary>
    internal ReadOnlySpan<int> DrainOrderForClusters(int firstCluster, int lastClusterExclusive)
    {
        if (_planSpan == 0)
        {
            return default;
        }

        var lo = StartOf(firstCluster);
        var hi = StartOf(lastClusterExclusive);
        return hi > lo ? new ReadOnlySpan<int>(_planOrder, lo, hi - lo) : default;
    }

    private int StartOf(int cluster)
    {
        if (cluster <= _planMin)
        {
            return 0;
        }

        if (cluster >= _planMin + _planSpan)
        {
            return _planCount;
        }

        return _planEnds[cluster - _planMin - 1];
    }

    /// <summary>Forgets the plan and zeroes the histogram over the span it used, so the next build starts clean. See the clear-cost remark on
    /// <c>ArchetypeClusterState.BuildDrainOrder</c> for why the whole span and not a retrace.</summary>
    internal void ClearDrainPlan()
    {
        if (_planSpan > 0)
        {
            Array.Clear(_planEnds, 0, _planSpan);
        }

        _planSpan = 0;
        _planCount = 0;
    }
}
