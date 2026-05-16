using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// Per-cell runtime state for the shared spatial grid. Padded to one full cache line (64 bytes) so concurrent <see cref="System.Threading.Interlocked"/>
/// mutations on <see cref="EntityCount"/> / <see cref="ClusterCount"/> for different cells cannot false-share with each other.
/// </summary>
/// <remarks>
/// <para>All cell state is transient — it is rebuilt at startup from entity positions (Decisions Q2 and Q6 in
/// <c>claude/design/Spatial/SpatialTiers/01-spatial-clusters.md</c>). Nothing here is persisted to disk.</para>
/// <para>Memory cost: 64 bytes per cell. A 200×200 grid (40K cells) = 2.5 MB. A 1024×1024 server grid = 64 MB. 52 bytes of reserved tail capacity for future
/// per-cell grid-level state (tier-budget metrics, per-cell wake counters, broadphase prefilter masks).</para>
/// <para>Issue #229 Q10 resolution: <see cref="ClusterCount"/> and <see cref="EntityCount"/> are global sums across every cluster-spatial archetype sharing
/// this grid. Per-archetype cluster lists live inside each <see cref="ArchetypeClusterState"/>'s own <c>CellClusterPool</c>.</para>
/// <para><b>Thread safety.</b> Parallel fence workers mutate <see cref="EntityCount"/> / <see cref="ClusterCount"/> concurrently — all writes MUST go through
/// <see cref="System.Threading.Interlocked.Increment(ref int)"/> / <see cref="System.Threading.Interlocked.Decrement(ref int)"/>. The cache-line padding here
/// is what makes those atomics scale across cores without inter-cell ping-pong. See rule MD-03 in <c>claude/rules/spatial.md</c>.</para>
/// </remarks>
[StructLayout(LayoutKind.Explicit, Size = 64)]
internal struct CellState
{
    /// <summary>SimTier value assigned by the game code each tick. Reserved — not used in Phase 1+2.</summary>
    [FieldOffset(0)] public byte Tier;

    /// <summary>Flag byte for future use (checkerboard colour, dirty marker, etc.). Reserved in Phase 1+2.</summary>
    [FieldOffset(1)] public byte Flags;

    /// <summary>Padding kept for layout stability with the prior 16-byte CellDescriptor.</summary>
    [FieldOffset(2)] public ushort Reserved;

    /// <summary>Total number of clusters currently attached to this cell, summed across every cluster-spatial archetype sharing this grid. Mutated via
    /// <see cref="System.Threading.Interlocked.Increment(ref int)"/> / <see cref="System.Threading.Interlocked.Decrement(ref int)"/> only.</summary>
    [FieldOffset(4)] public int ClusterCount;

    /// <summary>Sum of <c>PopCount(OccupancyBits)</c> across every cluster attached to this cell, summed across every cluster-spatial archetype sharing this
    /// grid. Mutated via <see cref="System.Threading.Interlocked.Increment(ref int)"/> / <see cref="System.Threading.Interlocked.Decrement(ref int)"/> only.</summary>
    [FieldOffset(8)] public int EntityCount;

    // [FieldOffset(12) .. FieldOffset(63)] — 52 bytes of reserved tail capacity (cache-line padding).
}
