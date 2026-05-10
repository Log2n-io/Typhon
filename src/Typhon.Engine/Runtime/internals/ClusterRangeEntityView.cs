using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// Zero-allocation entity view over a range of active clusters.
/// Pre-allocated per worker per parallel system, reconfigured each chunk via <see cref="Reset"/>.
/// Replaces <see cref="PartitionEntityView"/> for cluster-eligible archetypes, providing sequential
/// memory access across contiguous cluster pages (L1/L2-friendly).
/// <para>
/// Implements both <see cref="IReadOnlyCollection{T}"/> and <see cref="IEnumerator{T}"/> —
/// <see cref="GetEnumerator()"/> returns <c>this</c>, avoiding per-chunk allocation.
/// Safe because only one foreach loop runs per chunk invocation.
/// </para>
/// <para>
/// Iteration walks OccupancyBits for each cluster in the assigned range using TZCNT to find
/// occupied slots, reading EntityIds directly from cluster SoA.
/// </para>
/// </summary>
internal sealed unsafe class ClusterRangeEntityView : IReadOnlyCollection<EntityId>, IEnumerator<EntityId>
{
    private ArchetypeClusterState _state;
    private ChunkBasedSegment<PersistentStore> _segment;
    private ChunkAccessor<PersistentStore> _accessor;
    private bool _hasAccessor;

    // Issue #231: explicit cluster-id source. For non-tier-filtered dispatch this equals state.ActiveClusterIds; for tier-filtered dispatch it points at a
    // per-tier (or amortization bucket) cluster array owned by the archetype's TierClusterIndex.
    private int[] _clusterIds;
    private int _startCluster;       // inclusive index into _clusterIds
    private int _endCluster;         // exclusive index into _clusterIds
    private int _currentCluster;     // current position in range (-1 = before first)
    private ulong _currentBits;      // remaining occupancy bits in current cluster
    private byte* _currentBase;      // base pointer of current cluster page
    private int _approximateCount;
    private EntityId _current;

    /// <summary>
    /// Reconfigure this view for a new cluster range. O(1) for the configuration, creates ChunkAccessor on first use.
    /// </summary>
    /// <param name="state">Archetype cluster state (provides Layout).</param>
    /// <param name="segment">Cluster segment for creating ChunkAccessor.</param>
    /// <param name="clusterIds">
    /// Source cluster-id array. Pass <see cref="ArchetypeClusterState.ActiveClusterIds"/> for non-tier dispatch, or a <see cref="TierClusterIndex"/>-owned
    /// tier list for tier-filtered dispatch (issue #231).
    /// </param>
    /// <param name="startCluster">Inclusive start index into <paramref name="clusterIds"/>.</param>
    /// <param name="endCluster">Exclusive end index into <paramref name="clusterIds"/>.</param>
    public void Reset(ArchetypeClusterState state, ChunkBasedSegment<PersistentStore> segment, int[] clusterIds, int startCluster, int endCluster)
    {
        // Dispose previous accessor if any
        if (_hasAccessor)
        {
            _accessor.Dispose();
            _hasAccessor = false;
        }

        _state = state;
        _segment = segment;
        _clusterIds = clusterIds;
        _startCluster = startCluster;
        _endCluster = endCluster;
        _currentCluster = startCluster - 1;
        _currentBits = 0;
        _currentBase = null;
        _current = default;
        _approximateCount = (endCluster - startCluster) * state.Layout.ClusterSize;
    }

    // ═══════════════════════════════════════════════════════════════
    // IReadOnlyCollection<EntityId>
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Approximate entity count in this cluster range (range * clusterSize, upper bound).</summary>
    public int Count => _approximateCount;

    public IEnumerator<EntityId> GetEnumerator()
    {
        _currentCluster = _startCluster - 1;
        _currentBits = 0;
        _currentBase = null;
        _current = default;

        // Create accessor lazily on first enumeration
        if (!_hasAccessor)
        {
            _accessor = _segment.CreateChunkAccessor();
            _hasAccessor = true;
        }

        return this;
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // ═══════════════════════════════════════════════════════════════
    // IEnumerator<EntityId>
    // ═══════════════════════════════════════════════════════════════

    public EntityId Current => _current;
    object IEnumerator.Current => _current;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
        while (true)
        {
            // Try to consume remaining bits in current cluster
            if (_currentBits != 0)
            {
                int slot = BitOperations.TrailingZeroCount(_currentBits);
                _currentBits &= _currentBits - 1;
                _current = EntityId.FromRaw(*(long*)(_currentBase + _state.Layout.EntityIdsOffset + slot * 8));
                return true;
            }

            // Advance to next cluster in range
            _currentCluster++;
            if (_currentCluster >= _endCluster)
            {
                return false;
            }

            int chunkId = _clusterIds[_currentCluster];
            _currentBase = _accessor.GetChunkAddress(chunkId);
            _currentBits = *(ulong*)_currentBase; // OccupancyBits
        }
    }

    void IEnumerator.Reset()
    {
        _currentCluster = _startCluster - 1;
        _currentBits = 0;
        _currentBase = null;
    }

    public void Dispose()
    {
        if (_hasAccessor)
        {
            _accessor.Dispose();
            _hasAccessor = false;
        }
    }
}
