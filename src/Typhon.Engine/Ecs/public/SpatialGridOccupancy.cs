using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// A snapshot of the sparse spatial grid's occupancy and memory (#872 step 8). Obtained from <see cref="DatabaseEngine.GetSpatialGridOccupancy"/>;
/// all-zero when no grid is configured.
/// </summary>
/// <remarks>
/// A <c>readonly struct</c> with init-only members, matching <see cref="SpatialMigrationTelemetry"/> — its sibling snapshot in this namespace. A snapshot
/// that its reader can mutate invites a caller to "fix up" a field and pass it on as if the engine had reported it.
/// </remarks>
[PublicAPI]
public readonly struct SpatialGridOccupancy
{
    /// <summary>Allocated blocks — one per occupied block-sized region of the world.</summary>
    public int BlockCount { get; init; }

    /// <summary>Cells that actually exist. A cell is created when something first occupies it and is never removed while the grid lives.</summary>
    public int OccupiedCellCount { get; init; }

    /// <summary>Cells one block can hold: <see cref="BlockDimX"/> x <see cref="BlockDimY"/> x <see cref="BlockDimZ"/>.</summary>
    public int BlockCellCapacity { get; init; }

    /// <summary>Block extent on X, derived from the world as <c>clamp(nextPow2(extentInCells), 1, 16)</c>.</summary>
    public int BlockDimX { get; init; }

    /// <summary>Block extent on Y.</summary>
    public int BlockDimY { get; init; }

    /// <summary>Block extent on Z. <c>1</c> for a flat world.</summary>
    public int BlockDimZ { get; init; }

    /// <summary>
    /// Mean fraction of a block's index slots that name a live cell — <c>OccupiedCellCount / (BlockCount * BlockCellCapacity)</c>. The measurement that
    /// decides whether the dense per-block index array is the right payload.
    /// </summary>
    public double IntraBlockFill { get; init; }

    /// <summary>Bytes the grid's block index arrays and cell chunks hold. Excludes the root map and the per-archetype cluster pools.</summary>
    public long ResidentBytes { get; init; }

    /// <summary>Bytes a dense grid over the same world would have held: 64 per cell the bounds imply, occupied or not.</summary>
    public long DenseEquivalentBytes { get; init; }
}
