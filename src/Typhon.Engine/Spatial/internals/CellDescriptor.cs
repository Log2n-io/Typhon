using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// Per-cell runtime state for the shared spatial grid. 16 bytes, one per cell.
/// </summary>
/// <remarks>
/// <para>All cell state is transient — it is rebuilt at startup from entity positions (Decisions Q2 and Q6 in <c>claude/design/spatial-tiers/01-spatial-clusters.md</c>).
/// Nothing here is persisted to disk.</para>
/// <para>For a 100x100 grid (10 000 cells) the descriptor array is 160 KB — fits in L2. For a Morton-padded 128x128 grid (16 384 cells) it's 256 KB.</para>
/// <para>Issue #229 Q10 resolution: <see cref="ClusterCount"/> and <see cref="EntityCount"/> are global sums across every cluster-spatial archetype
/// sharing this grid. Per-archetype cluster lists live inside each <see cref="ArchetypeClusterState"/>'s own <c>CellClusterPool</c> — the pool's head
/// pointers used to live on this struct but were moved out so N archetypes could share a grid without colliding on cluster chunk IDs.</para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct CellDescriptor
{
    /// <summary>SimTier value assigned by the game code each tick. Reserved — not used in Phase 1+2.</summary>
    public byte Tier;

    /// <summary>Flag byte for future use (checkerboard colour, dirty marker, etc.). Reserved in Phase 1+2.</summary>
    public byte Flags;

    /// <summary>Padding to keep the struct 16-byte aligned.</summary>
    public ushort Reserved;

    /// <summary>Total number of clusters currently attached to this cell, summed across every cluster-spatial archetype sharing this grid. Bumped by
    /// <see cref="ArchetypeClusterState.ClaimSlotInCell"/> on new-cluster allocation, decremented by <see cref="ArchetypeClusterState"/> when a cluster's
    /// last entity is released. Per-archetype cluster lists live on each archetype's own <c>CellClusterPool</c>.</summary>
    public int ClusterCount;

    /// <summary>Padding to keep EntityCount 16-byte aligned (ClusterCount is 4 bytes, this keeps the struct size stable after removing ClusterListHead).</summary>
    public int Reserved2;

    /// <summary>
    /// Sum of <c>PopCount(OccupancyBits)</c> across every cluster attached to this cell, summed across every cluster-spatial archetype sharing this
    /// grid. Maintained incrementally by <see cref="ArchetypeClusterState.ClaimSlotInCell"/> / slot release, and by <c>RebuildCellState</c> at startup.
    /// </summary>
    public int EntityCount;
}
