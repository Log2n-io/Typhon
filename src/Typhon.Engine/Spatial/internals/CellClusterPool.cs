using System;

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
/// <para>Removal uses swap-with-last — the per-cell count shrinks; the last entry in the segment moves into the vacated slot. This means clusters attached
/// to a cell have no stable index inside the pool; callers must not cache positions.</para>
/// </remarks>
internal sealed class CellClusterPool
{
    private int[] _pool;
    private int _tail;

    /// <summary>Start index of each cell's segment inside <see cref="_pool"/>. <c>-1</c> when the cell has no segment allocated yet. Indexed by cell key.</summary>
    private readonly int[] _cellHeads;

    /// <summary>Number of cluster chunk IDs currently stored in each cell's segment. Indexed by cell key.</summary>
    private readonly int[] _cellCounts;

    /// <summary>Allocated capacity of each cell's segment. Indexed by cell key.</summary>
    private readonly int[] _cellCapacities;

    /// <summary>
    /// Per-cell scan cursor: the logical index (into the <c>0..count</c> list <see cref="GetClusters"/> returns) of the first cluster that <em>might</em> still
    /// have a free slot. Spatial slot claims (<c>ArchetypeClusterState.ClaimSlotInCell</c>) start their scan here instead of 0, collapsing the otherwise
    /// O(M²) re-scan of already-full clusters during an append-only spawn to O(1) amortized. Indexed by cell key.
    /// <para>This is a <b>hint only</b> — same status as <c>ArchetypeClusterState.FreeClusterHead</c>. A stale value can only cause a redundant scan or a
    /// skipped free slot (mild fragmentation); never incorrectness, since the claim path's CAS and allocate-new fallback remain authoritative. It is
    /// advanced monotonically by the claim path and reset to 0 whenever a slot is freed in the cell, so freed-slot reuse is preserved in steady state.</para>
    /// </summary>
    private readonly int[] _cellScanCursor;

    public CellClusterPool(int cellCount, int initialPoolCapacity = 256)
    {
        if (cellCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cellCount));
        }

        _pool = new int[Math.Max(initialPoolCapacity, 16)];
        _tail = 0;
        _cellHeads = new int[cellCount];
        _cellCounts = new int[cellCount];
        _cellCapacities = new int[cellCount];
        _cellScanCursor = new int[cellCount];
        Array.Fill(_cellHeads, -1);
    }

    /// <summary>Number of ints currently allocated inside the pool (including dead tail segments).</summary>
    public int PoolTail => _tail;

    /// <summary>Total allocated pool size, in ints. Used by tests.</summary>
    public int PoolCapacity => _pool.Length;

    /// <summary>Number of cluster chunk IDs currently in the specified cell's segment.</summary>
    public int GetClusterCount(int cellKey) => _cellCounts[cellKey];

    /// <summary>
    /// Logical index the next spatial slot claim should start its cluster scan from. See <see cref="_cellScanCursor"/>. The caller must still clamp this
    /// against the current cluster count — a draining release can shrink the list below a previously advanced cursor.
    /// </summary>
    public int GetScanCursor(int cellKey) => _cellScanCursor[cellKey];

    /// <summary>
    /// Advance the cell's scan cursor to <paramref name="value"/> if it moves forward. Monotonic — never moves the cursor backward, so concurrent claimers
    /// racing on the same cell cannot un-advance each other's progress. See <see cref="_cellScanCursor"/>.
    /// </summary>
    public void AdvanceScanCursor(int cellKey, int value)
    {
        if (value > _cellScanCursor[cellKey])
        {
            _cellScanCursor[cellKey] = value;
        }
    }

    /// <summary>
    /// Reset the cell's scan cursor to 0, forcing the next claim to scan the full cluster list. Called whenever a slot is freed in the cell so a reusable
    /// free slot ahead of the cursor is not skipped. See <see cref="_cellScanCursor"/>.
    /// </summary>
    public void ResetScanCursor(int cellKey) => _cellScanCursor[cellKey] = 0;

    /// <summary>
    /// Read-only span of the cluster chunk IDs currently attached to <paramref name="cellKey"/>. May be empty.
    /// </summary>
    public ReadOnlySpan<int> GetClusters(int cellKey)
    {
        int count = _cellCounts[cellKey];
        if (count == 0)
        {
            return ReadOnlySpan<int>.Empty;
        }
        return _pool.AsSpan(_cellHeads[cellKey], count);
    }

    /// <summary>
    /// Append <paramref name="clusterChunkId"/> to the list attached to <paramref name="cellKey"/>, growing the cell's segment if necessary.
    /// </summary>
    public void AddCluster(int cellKey, int clusterChunkId)
    {
        int capacity = _cellCapacities[cellKey];
        int count = _cellCounts[cellKey];
        if (_cellHeads[cellKey] < 0 || count >= capacity)
        {
            GrowCellSegment(cellKey, ref capacity);
        }

        _pool[_cellHeads[cellKey] + count] = clusterChunkId;
        _cellCounts[cellKey] = count + 1;
    }

    /// <summary>
    /// Remove <paramref name="clusterChunkId"/> from the list attached to <paramref name="cellKey"/> using swap-with-last.
    /// Returns <c>false</c> if the cluster is not in the list.
    /// </summary>
    public bool RemoveCluster(int cellKey, int clusterChunkId)
    {
        int count = _cellCounts[cellKey];
        if (count == 0 || _cellHeads[cellKey] < 0)
        {
            return false;
        }

        var span = _pool.AsSpan(_cellHeads[cellKey], count);
        for (int i = 0; i < span.Length; i++)
        {
            if (span[i] != clusterChunkId)
            {
                continue;
            }

            // Swap-with-last (no-op when i is already the last entry)
            span[i] = span[^1];
            _cellCounts[cellKey] = count - 1;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Drop every cell's segment and reset the pool tail. Used by <see cref="ArchetypeClusterState.RebuildCellState"/> before reconstructing the mapping.
    /// </summary>
    public void Reset()
    {
        Array.Clear(_cellCapacities);
        Array.Clear(_cellCounts);
        Array.Clear(_cellScanCursor);
        Array.Fill(_cellHeads, -1);
        _tail = 0;
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
        int currentCount = _cellCounts[cellKey];
        if (currentCount > 0)
        {
            // Copy the existing entries into the fresh tail segment. The old segment leaks as dead space — see class remarks.
            Array.Copy(_pool, _cellHeads[cellKey], _pool, newHead, currentCount);
        }

        _cellHeads[cellKey] = newHead;
        _tail += newCapacity;
        _cellCapacities[cellKey] = newCapacity;
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
        Array.Resize(ref _pool, newSize);
    }
}
