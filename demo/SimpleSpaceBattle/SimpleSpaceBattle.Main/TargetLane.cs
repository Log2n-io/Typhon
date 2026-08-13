using System.Runtime.CompilerServices;

namespace SimpleSpaceBattle;

/// <summary>
/// A flat mirror of <see cref="TargetingComponent.TargetRawId"/>, indexed by the dense, tick-stable coordinate that
/// <c>ClusterSpatialQueryResult</c> already carries: <c>ClusterChunkId × ClusterSize + SlotIndex</c>.
///
/// <para><b>Why this exists.</b> <c>Resolution</c> must read the <i>target of every neighbour</i> it scans. The hit
/// struct hands over <c>ClusterChunkId</c> and <c>SlotIndex</c> precisely so a caller can locate the entry in O(1) —
/// but there is no public <c>GetCluster(chunkId)</c> on <c>EntityAccessor</c>; cluster access is enumerator-only
/// (<c>EntityAccessor.ECS.cs:38-77</c>). Reading a neighbour's component through the ECS would therefore cost a
/// per-candidate enumerator setup: ~28 candidates × 50 000 ships × ~75 ns ≈ 3 ms/tick of pure overhead. This array
/// turns that into one L2/L3 load.</para>
///
/// <para><b>Why it is safe.</b> Written only in <c>Acquire</c>, read only in <c>Fire</c> — the same strict phase
/// separation <c>Hull</c> gets. No concurrent reader/writer ever exists, so the read set of the Fire phase is
/// immutable and the result is deterministic regardless of worker count. Notably <c>Resolution</c> invalidating a
/// lost lock writes the <i>component</i> and deliberately NOT the lane, which is what preserves that property.</para>
///
/// <para><b>The component remains the source of truth</b> — persisted, queryable, visible in the Workbench. This is a
/// derived, in-memory, rebuilt-every-tick projection. A <c>GetCluster(chunkId)</c> accessor on the engine would
/// delete it outright (DESIGN.md §6.2, §14).</para>
///
/// <para>Slot coordinates are stable for the whole tick: slot assignment changes only at spawn and destroy, which
/// happen in <c>Reap</c> or at bootstrap, never inside the parallel phases.</para>
/// </summary>
internal sealed class TargetLane
{
    private long[] _lane;
    private readonly int _clusterSize;

    public TargetLane(int clusterSize, int initialClusterCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(clusterSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(initialClusterCapacity);
        _clusterSize = clusterSize;
        _lane = new long[(long)initialClusterCapacity * clusterSize <= int.MaxValue
            ? initialClusterCapacity * clusterSize
            : throw new ArgumentOutOfRangeException(nameof(initialClusterCapacity))];
    }

    public int ClusterSize => _clusterSize;

    public int Capacity => _lane.Length;

    /// <summary>
    /// Grow to cover <paramref name="clusterChunkId"/>. Called from the single-threaded <c>Reap</c> phase only —
    /// never while a parallel phase holds a reference, because a resize swaps the backing array.
    /// </summary>
    public void EnsureCluster(int clusterChunkId)
    {
        int required = (clusterChunkId + 1) * _clusterSize;
        if (required <= _lane.Length)
        {
            return;
        }

        int next = _lane.Length == 0 ? required : _lane.Length;
        while (next < required)
        {
            next *= 2;
        }

        Array.Resize(ref _lane, next);
    }

    /// <summary>
    /// The backing array. Hoisted into a local at the top of a system body so the per-candidate read is a plain
    /// array index with no field load and no bounds-check hoisting barrier.
    /// </summary>
    public long[] Backing
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _lane;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int IndexOf(int clusterChunkId, int slotIndex) => clusterChunkId * _clusterSize + slotIndex;

    /// <summary>Read a neighbour's published target. Out-of-range reads yield <c>Unlocked</c> rather than throwing:
    /// a cluster created after the lane was last grown simply has no published target yet.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long Read(int clusterChunkId, int slotIndex)
    {
        int index = IndexOf(clusterChunkId, slotIndex);
        long[] lane = _lane;
        return (uint)index < (uint)lane.Length ? lane[index] : TargetingComponent.Unlocked;
    }
}
