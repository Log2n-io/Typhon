using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Flat-array pool holding per-cell cluster lists for a single archetype. Each cell gets a contiguous segment inside <see cref="_pool"/>; iteration is a
/// single sequential read, matching the layout the design doc describes as "Option B: Compact array per cell" in
/// <c>claude/design/Spatial/SpatialTiers/01-spatial-clusters.md</c>.
/// </summary>
/// <remarks>
/// <para>Issue #229 Q10 resolution (issue #230 follow-up): this pool was originally owned by <c>SpatialGrid</c> and shared across archetypes, which
/// conflated cluster chunk IDs that are only meaningful inside a single archetype's <see cref="ArchetypeClusterState.ClusterSegment"/>. Under Q10 the pool
/// is instead owned by each <see cref="ArchetypeClusterState"/> (one instance per cluster-spatial archetype) so two archetypes sharing the same grid cell
/// no longer collide on chunk IDs. The pool is now fully self-contained — it owns its own per-cell head / count / capacity arrays and does not touch
/// <see cref="CellState"/> at all. Global per-cell totals (<see cref="CellState.ClusterCount"/> and <see cref="CellState.EntityCount"/>)
/// are maintained separately by the archetype state call sites.</para>
/// <para>Growth strategy: each cell starts with zero capacity. On the first insert we allocate a small tail segment (capacity 4) at the current
/// <see cref="_tail"/> offset and record its head. When that segment fills up we allocate a new tail segment at 2× capacity, copy the old entries across,
/// and update the per-cell head. The abandoned segment becomes dead space inside the pool — acceptable because cell cluster counts change slowly, cell
/// grids are small (a few hundred KB per archetype), and compacting would complicate lookups without any measurable benefit at our scales.</para>
/// <para><b>The `_pool` array itself is still resized in place</b> (<see cref="EnsurePoolCapacity"/>), so a <see cref="ReadOnlySpan{T}"/> from
/// <see cref="GetClusters"/> must not be held across a concurrent <see cref="AddCluster"/> on ANOTHER cell — the span would point into the orphaned
/// array. That is unchanged from before the per-cell arrays were chunked, and it is why they were: those four are read by every claim path, whereas the
/// pool span is consumed immediately by its caller. Chunking `_pool` as well would remove the caveat and is the obvious next step if a caller ever needs
/// to hold one.</para>
/// <para>Removal uses swap-with-last — the per-cell count shrinks; the last entry in the segment moves into the vacated slot. This means clusters attached
/// to a cell have no stable index inside the pool; callers must not cache positions.</para>
/// <para><b>Single-writer contract.</b> <see cref="AddCluster"/> and <see cref="RemoveCluster"/> must never run concurrently with each other on one pool
/// instance. That is the caller's job and it already does it: both <c>ClaimSlotInCell</c> overloads call <see cref="AddCluster"/> under
/// <c>_finalizeLock</c>'s exclusive access, the startup rebuild calls it from its serial reduce, and both <see cref="RemoveCluster"/> callers are serial for
/// the archetype (<c>DrainPendingClusterFinalizations</c> runs after the fence phase barriers; the inline <c>ReleaseSlot</c> path has a single-threaded
/// caller). The pool holds no lock of its own, and one over the side arrays would be theatre: <see cref="AddCluster"/> goes on to write <c>_pool</c>, bump
/// <c>_tail</c> and <c>Array.Resize</c> the pool array, none of which such a lock would cover — two genuinely racing writers would corrupt the pool with it
/// held. Rather than a comment asserting the contract, <see cref="EnterWriter"/> DETECTS a breach; see its remarks.</para>
/// <para><b>Readers are a different matter and are genuinely concurrent.</b> <c>ClaimSlotInCell</c>'s hot path calls <see cref="GetClusters"/>,
/// <see cref="GetClusterCount"/> and the scan-cursor accessors from fence workers AND from user threads committing spawns while another thread is inside
/// <see cref="AddCluster"/>. They are safe because a segment, once published, is never moved, and because reader and writer agree on an ORDER — not
/// because of any mutual exclusion. The writer publishes with release stores in the order <c>_pool</c> (a grown array, entries copied) → segment head →
/// the new entry → count; the reader takes acquire loads in the reverse order count → head → <c>_pool</c>, so whatever count it observes, the head and
/// the array it then loads are at least as new as the store that produced that count. The two dangerous pairings are a NEW count against an OLD head — the
/// span runs past the old segment into the next cell's, and a claim lands in a cluster of another cell (CC-02; observed 2 in ~60 runs of
/// <c>ClusterPlacementTests.ConcurrentSpawnsAndBoundGrowthKeepClustersInTheirCell</c> when these were plain loads, step 15) — and a NEW head against an
/// OLD <c>_pool</c>, an index past the orphaned array. Plain element loads were program-ordered on x64 but nothing stopped the JIT or arm64 from
/// reordering them; the acquire/release pairs cost nothing on x64. The scan cursors stay outside the protocol deliberately: they are per-cell
/// single-writer hints (see <see cref="_cellScanCursor"/>) on the hot claim path, where an interlocked round trip would cost more than the staleness it
/// prevents.</para>
/// </remarks>
internal sealed class CellClusterPool
{
    /// <summary>Cells per chunk of the per-cell side arrays. Four <c>int[256]</c> chunks = 4 KiB per archetype per 256 occupied cells.</summary>
    private const int CellChunkShift = 8;
    private const int CellChunkSize = 1 << CellChunkShift;
    private const int CellChunkMask = CellChunkSize - 1;

    private int[] _pool;
    private int _tail;

    /// <summary>Start index of each cell's segment inside <see cref="_pool"/>. <c>-1</c> when the cell has no segment allocated yet. Indexed by cell key.</summary>
    private int[][] _cellHeads = new int[4][];

    /// <summary>Number of cluster chunk IDs currently stored in each cell's segment. Indexed by cell key.</summary>
    private int[][] _cellCounts = new int[4][];

    /// <summary>Allocated capacity of each cell's segment. Indexed by cell key.</summary>
    private int[][] _cellCapacities = new int[4][];

    /// <summary>
    /// Per-cell scan cursor: the logical index (into the <c>0..count</c> list <see cref="GetClusters"/> returns) of the first cluster that <em>might</em> still
    /// have a free slot. Spatial slot claims (<c>ArchetypeClusterState.ClaimSlotInCell</c>) start their scan here instead of 0, collapsing the otherwise
    /// O(M²) re-scan of already-full clusters during an append-only spawn to O(1) amortized. Indexed by cell key.
    /// <para>This is a <b>hint only</b> — same status as <c>ArchetypeClusterState.FreeClusterHead</c>. A stale value can only cause a redundant scan or a
    /// skipped free slot (mild fragmentation); never incorrectness, since the claim path's CAS and allocate-new fallback remain authoritative. It is
    /// advanced monotonically by the claim path and reset to 0 whenever a slot is freed in the cell, so freed-slot reuse is preserved in steady state.</para>
    /// </summary>
    private int[][] _cellScanCursor = new int[4][];

    /// <summary>Managed thread id of the writer currently inside <see cref="AddCluster"/> or <see cref="RemoveCluster"/>; zero when there is none.</summary>
    private int _writerInFlight;

    /// <summary>
    /// Build an empty pool. <paramref name="initialCellCapacity"/> is a sizing HINT, not a bound: cell keys are pool slots handed out lazily by the VDB grid
    /// (#872 step 8), so the pool cannot know how many cells will exist and grows a chunk at a time as new keys arrive.
    /// </summary>
    /// <remarks>
    /// Before step 8 this took the grid's total cell count and allocated four <c>int[cellCount]</c> arrays up front — 16 B per cell per spatial archetype,
    /// whether the cell held anything or not, and with no growth path at all: a key past the end was an unguarded <c>IndexOutOfRangeException</c>. The side
    /// arrays are now CHUNKED rather than resized, because a resize hands a concurrent writer on another cell a stale array and silently loses its update.
    /// A chunk, once allocated, is never moved.
    /// </remarks>
    public CellClusterPool(int initialCellCapacity = 0, int initialPoolCapacity = 256)
    {
        _pool = new int[Math.Max(initialPoolCapacity, 16)];
        _tail = 0;
        if (initialCellCapacity > 0)
        {
            EnsureCell(initialCellCapacity - 1);
        }
    }

    /// <summary>Allocate the side-array chunk holding <paramref name="cellKey"/> if it is not there yet.</summary>
    private void EnsureCell(int cellKey)
    {
        // Unsigned, because -1 is a legitimate value in this system now: SpatialGridAccessor returns it for a coordinate with no cell, and a caller that
        // forwards it must land in the guard rather than index heads[-1].
        if (cellKey < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cellKey), cellKey, "Cell keys are non-negative pool slots.");
        }

        int chunk = cellKey >> CellChunkShift;
        var heads = Volatile.Read(ref _cellHeads);
        if ((uint)chunk < (uint)heads.Length && Volatile.Read(ref heads[chunk]) != null)
        {
            return;
        }

        // No lock. The caller serialises writers (see the class remarks) and EnterWriter fails loudly if it ever stops doing so; what the concurrent READERS
        // need from this method is publication order, not mutual exclusion, and the Volatile.Writes below are what provide that.
        if (chunk >= _cellHeads.Length)
        {
            // Grow the three siblings first and publish _cellHeads last, for the same reason the per-chunk arrays are published in that order: a reader
            // that observes a longer _cellHeads must find the sibling outer arrays already at least that long.
            int outer = Math.Max(_cellHeads.Length * 2, chunk + 1);
            Volatile.Write(ref _cellCounts, Grow(_cellCounts, outer));
            Volatile.Write(ref _cellCapacities, Grow(_cellCapacities, outer));
            Volatile.Write(ref _cellScanCursor, Grow(_cellScanCursor, outer));
            Volatile.Write(ref _cellHeads, Grow(_cellHeads, outer));
        }

        if (_cellHeads[chunk] == null)
        {
            var newHeads = new int[CellChunkSize];
            Array.Fill(newHeads, -1);
            _cellCounts[chunk] = new int[CellChunkSize];
            _cellCapacities[chunk] = new int[CellChunkSize];
            _cellScanCursor[chunk] = new int[CellChunkSize];

            // Published LAST, and this is the whole release edge: HasCell tests `_cellHeads[chunk] != null` as its "this chunk is usable" signal, so the
            // three siblings and the -1 fill must already be in place when it turns non-null. Its acquire is HasCell's Volatile.Read of the same element.
            Volatile.Write(ref _cellHeads[chunk], newHeads);
        }
    }

    /// <summary>
    /// Claim the pool for the calling thread for the duration of one structural mutation, and throw when another thread is already inside one.
    /// </summary>
    /// <remarks>
    /// <para><b>A detector, not a lock.</b> It excludes nobody — by the time it fires the damage would already be done. It exists because the pool's safety
    /// rests entirely on a caller-side contract (class remarks) that no signature expresses, and a contract nothing checks is a comment. The breach is silent
    /// otherwise: two concurrent <see cref="AddCluster"/> calls lose one another's <c>Count(cellKey) = count + 1</c>, leaving a cluster attached to a cell
    /// that <see cref="GetClusters"/> never returns — entities in a cell no query finds, an <c>SQ-01</c> false negative with no exception near it.</para>
    /// <para><b>Always compiled, not <c>[Conditional("DEBUG")]</c></b>, for the reason <c>ExclusiveWindow</c> gives: the merge gate runs Release, so a
    /// Debug-only guard is one the gate never executes. The cost is one uncontended <see cref="Interlocked"/> exchange per structural mutation, and both are
    /// rare — <see cref="AddCluster"/> runs only when a cell needs a whole new 64-entity cluster, <see cref="RemoveCluster"/> only when one drains. That is
    /// strictly cheaper than the <c>Lock</c> it replaces, which cost the same class of atomic and covered a quarter of the mutation.</para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void EnterWriter(string site)
    {
        int prior = Interlocked.Exchange(ref _writerInFlight, Environment.CurrentManagedThreadId);
        if (prior != 0)
        {
            ThrowConcurrentWriter(site, prior);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ExitWriter() => Volatile.Write(ref _writerInFlight, 0);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowConcurrentWriter(string site, int priorThreadId) =>
        throw new InvalidOperationException(
            $"CellClusterPool.{site} ran concurrently with a structural mutation already in flight on thread {priorThreadId} (this is thread "
            + $"{Environment.CurrentManagedThreadId}). The pool is single-writer by contract — see its class remarks for who is supposed to serialise it.");

    private static int[][] Grow(int[][] outer, int newLength)
    {
        var grown = new int[newLength][];
        Array.Copy(outer, grown, outer.Length);
        return grown;
    }

    /// <summary>
    /// True when <paramref name="cellKey"/>'s side-array chunk exists. A key never added to is legitimately absent, and reads answer with defaults.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool HasCell(int cellKey)
    {
        int chunk = cellKey >> CellChunkShift;
        var heads = Volatile.Read(ref _cellHeads);
        return (uint)chunk < (uint)heads.Length && Volatile.Read(ref heads[chunk]) != null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref int Head(int cellKey) => ref Volatile.Read(ref _cellHeads)[cellKey >> CellChunkShift][cellKey & CellChunkMask];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref int Count(int cellKey) => ref Volatile.Read(ref _cellCounts)[cellKey >> CellChunkShift][cellKey & CellChunkMask];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref int Capacity(int cellKey) => ref Volatile.Read(ref _cellCapacities)[cellKey >> CellChunkShift][cellKey & CellChunkMask];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref int Cursor(int cellKey) => ref Volatile.Read(ref _cellScanCursor)[cellKey >> CellChunkShift][cellKey & CellChunkMask];

    /// <summary>Number of ints currently allocated inside the pool (including dead tail segments).</summary>
    public int PoolTail => _tail;

    /// <summary>Total allocated pool size, in ints. Used by tests.</summary>
    public int PoolCapacity => _pool.Length;

    /// <summary>Number of cluster chunk IDs currently in the specified cell's segment. Zero for a cell nothing has been added to.</summary>
    public int GetClusterCount(int cellKey) => HasCell(cellKey) ? Volatile.Read(ref Count(cellKey)) : 0;

    /// <summary>
    /// Logical index the next spatial slot claim should start its cluster scan from. See <see cref="_cellScanCursor"/>. The caller must still clamp this
    /// against the current cluster count — a draining release can shrink the list below a previously advanced cursor.
    /// </summary>
    public int GetScanCursor(int cellKey) => HasCell(cellKey) ? Cursor(cellKey) : 0;

    /// <summary>
    /// Advance the cell's scan cursor to <paramref name="value"/> if it moves forward. Monotonic — never moves the cursor backward, so concurrent claimers
    /// racing on the same cell cannot un-advance each other's progress. See <see cref="_cellScanCursor"/>.
    /// </summary>
    public void AdvanceScanCursor(int cellKey, int value)
    {
        if (!HasCell(cellKey))
        {
            return;
        }

        ref var cursor = ref Cursor(cellKey);
        if (value > cursor)
        {
            cursor = value;
        }
    }

    /// <summary>
    /// Reset the cell's scan cursor to 0, forcing the next claim to scan the full cluster list. Called whenever a slot is freed in the cell so a reusable
    /// free slot ahead of the cursor is not skipped. See <see cref="_cellScanCursor"/>.
    /// </summary>
    public void ResetScanCursor(int cellKey)
    {
        if (HasCell(cellKey))
        {
            Cursor(cellKey) = 0;
        }
    }

    /// <summary>
    /// Unconditionally set the cell's scan cursor to <paramref name="value"/>. Unlike <see cref="AdvanceScanCursor"/> this may move the cursor <b>backward</b>
    /// — used by <c>ArchetypeClusterState.ClaimSlotInCell</c>'s phase-2 self-healing scan when it reclaims a free slot behind a stale-high cursor. Safe as a
    /// plain write because every cell's cursor is single-writer across all call paths: serial entity spawn, worker-exclusive migration destination cell, and
    /// serial entity destroy. See <see cref="_cellScanCursor"/>.
    /// </summary>
    public void SetScanCursor(int cellKey, int value)
    {
        if (HasCell(cellKey))
        {
            Cursor(cellKey) = value;
        }
    }

    /// <summary>
    /// Read-only span of the cluster chunk IDs currently attached to <paramref name="cellKey"/>. May be empty.
    /// </summary>
    public ReadOnlySpan<int> GetClusters(int cellKey)
    {
        if (!HasCell(cellKey))
        {
            return ReadOnlySpan<int>.Empty;
        }

        // Acquire loads in THIS order — count, head, pool — the reverse of AddCluster's release order; see the class remarks for the two pairings this
        // rules out. A span built from a consistent (head, count) stays valid across a concurrent grow: the old segment is orphaned, never overwritten.
        var count = Volatile.Read(ref Count(cellKey));
        if (count == 0)
        {
            return ReadOnlySpan<int>.Empty;
        }

        var head = Volatile.Read(ref Head(cellKey));
        var pool = Volatile.Read(ref _pool);
        return pool.AsSpan(head, count);
    }

    /// <summary>
    /// Append <paramref name="clusterChunkId"/> to the list attached to <paramref name="cellKey"/>, growing the cell's segment if necessary.
    /// </summary>
    public void AddCluster(int cellKey, int clusterChunkId)
    {
        EnterWriter(nameof(AddCluster));
        try
        {
            EnsureCell(cellKey);

            int capacity = Capacity(cellKey);
            int count = Count(cellKey);
            if (Head(cellKey) < 0 || count >= capacity)
            {
                GrowCellSegment(cellKey, ref capacity);
            }

            _pool[Head(cellKey) + count] = clusterChunkId;
            Volatile.Write(ref Count(cellKey), count + 1);   // release: the entry, the head and the pool are visible to a reader that sees this count
        }
        finally
        {
            ExitWriter();
        }
    }

    /// <summary>
    /// Remove <paramref name="clusterChunkId"/> from the list attached to <paramref name="cellKey"/> using swap-with-last.
    /// Returns <c>false</c> if the cluster is not in the list.
    /// </summary>
    public bool RemoveCluster(int cellKey, int clusterChunkId)
    {
        EnterWriter(nameof(RemoveCluster));
        try
        {
            if (!HasCell(cellKey))
            {
                return false;
            }

            int count = Count(cellKey);
            if (count == 0 || Head(cellKey) < 0)
            {
                return false;
            }

            var span = _pool.AsSpan(Head(cellKey), count);
            for (int i = 0; i < span.Length; i++)
            {
                if (span[i] != clusterChunkId)
                {
                    continue;
                }

                // Swap-with-last (no-op when i is already the last entry). A concurrent reader may still see the old count and the moved entry twice —
                // a duplicate chunk id costs a redundant claim attempt, never a wrong cell.
                span[i] = span[^1];
                Volatile.Write(ref Count(cellKey), count - 1);
                return true;
            }
            return false;
        }
        finally
        {
            ExitWriter();
        }
    }

    private void GrowCellSegment(int cellKey, ref int capacity)
    {
        // Compute the new capacity and the resulting pool tail in long arithmetic. Both capacity*2 and _tail+newCapacity can overflow int on a
        // pathologically large pool; EnsurePoolCapacity validates the long total against Array.MaxLength before anything is narrowed back to int.
        long newCapacityLong = capacity == 0 ? 4L : (long)capacity * 2;
        long requiredLong = _tail + newCapacityLong;
        EnsurePoolCapacity(requiredLong);

        // EnsurePoolCapacity returned without throwing ⇒ requiredLong <= Array.MaxLength, so both newCapacity and the updated _tail fit in int.
        int newCapacity = (int)newCapacityLong;
        int newHead = _tail;
        int currentCount = Count(cellKey);
        if (currentCount > 0)
        {
            // Copy the existing entries into the fresh tail segment. The old segment leaks as dead space — see class remarks.
            Array.Copy(_pool, Head(cellKey), _pool, newHead, currentCount);
        }

        Volatile.Write(ref Head(cellKey), newHead);   // release: the copied entries precede the head a reader pairs with its count
        _tail += newCapacity;
        Capacity(cellKey) = newCapacity;
        capacity = newCapacity;
    }

    private void EnsurePoolCapacity(long required)
    {
        if (required <= _pool.Length)
        {
            return;
        }
        // .NET arrays cannot exceed Array.MaxLength (~2.147 B for an int[]) — well below int.MaxValue. Guard against it here with a clear error
        // instead of letting Array.Resize throw a generic OutOfMemoryException further down. This is the reachable form of the check (the old
        // `newSize == int.MaxValue && newSize < required` guard was dead: an int `required` can never exceed int.MaxValue).
        if (required > Array.MaxLength)
        {
            throw new OutOfMemoryException($"CellClusterPool capacity ({required}) exceeds the maximum array length ({Array.MaxLength}).");
        }
        int newSize = _pool.Length;
        while (newSize < required)
        {
            newSize = (int)Math.Min((long)newSize * 2, Array.MaxLength);
        }

        // Copy-then-publish with a release store, so a reader that later observes a head inside the grown region also observes the grown array. The
        // orphaned array keeps its content: a span a reader built over it stays valid.
        var grown = new int[newSize];
        Array.Copy(_pool, grown, _pool.Length);
        Volatile.Write(ref _pool, grown);
    }
}
