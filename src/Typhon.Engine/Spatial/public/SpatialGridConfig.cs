using System;
using System.Numerics;
using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Immutable configuration for the engine-wide spatial grid. Set once via <see cref="DatabaseEngine.ConfigureSpatialGrid"/> before archetypes are initialized.
/// </summary>
/// <remarks>
/// <para>All spatial archetypes share a single coarse grid with one cell size (Decision Q1 in <c>claude/design/spatial-tiers/01-spatial-clusters.md</c>).
/// Per-archetype differences are expressed at the system level via tier filters, not at the grid level.</para>
/// <para>Grid dimensions are derived from (WorldMax - WorldMin) / CellSize and rounded up to the nearest power of two when Morton cell keys are enabled — this
/// keeps the Morton decode well-defined without needing a per-axis width.</para>
/// </remarks>
[PublicAPI]
public readonly struct SpatialGridConfig
{
    /// <summary>World-space minimum corner (inclusive).</summary>
    public readonly Vector2 WorldMin;

    /// <summary>World-space maximum corner (exclusive — the grid excludes the max edge).</summary>
    public readonly Vector2 WorldMax;

    /// <summary>Size of a single grid cell, in world units. Must be &gt; 0.</summary>
    public readonly float CellSize;

    /// <summary>
    /// Fractional dead zone applied per axis during entity migration, as a fraction of cell size.
    /// Default 0.05 (5 % of cell size). Unused in Phase 1+2 — reserved for the Phase 3 migration path.
    /// </summary>
    public readonly float MigrationHysteresisRatio;

    // ── Derived values, computed in the constructor ────────────────────────

    /// <summary>
    /// Number of real cells along the X axis — derived from (WorldMax.X - WorldMin.X) / CellSize, rounded up. This is the count of cells entities can actually occupy.
    /// </summary>
    public readonly int GridWidth;

    /// <summary>Number of real cells along the Y axis.</summary>
    public readonly int GridHeight;

    /// <summary>
    /// Cell key space size per axis. Equal to <see cref="GridWidth"/>/<see cref="GridHeight"/> for row-major, or padded to the next power of two (matching the
    /// larger of the two) for Morton.
    /// Used only for descriptor array sizing — not for world-to-cell clamping.
    /// </summary>
    public readonly int KeySpaceDim;

    /// <summary>Precomputed 1 / <see cref="CellSize"/>.</summary>
    public readonly float InverseCellSize;

    /// <summary>Total number of descriptor slots. Equals <see cref="KeySpaceDim"/>² for Morton keys.</summary>
    public readonly int CellCount;

    public SpatialGridConfig(Vector2 worldMin, Vector2 worldMax, float cellSize, float migrationHysteresisRatio = 0.05f)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cellSize);
        if (worldMax.X <= worldMin.X || worldMax.Y <= worldMin.Y)
        {
            throw new ArgumentException("WorldMax must be strictly greater than WorldMin on both axes.", nameof(worldMax));
        }

        WorldMin = worldMin;
        WorldMax = worldMax;
        CellSize = cellSize;
        MigrationHysteresisRatio = migrationHysteresisRatio;
        InverseCellSize = 1.0f / cellSize;

        GridWidth  = (int)MathF.Ceiling((worldMax.X - worldMin.X) * InverseCellSize);
        GridHeight = (int)MathF.Ceiling((worldMax.Y - worldMin.Y) * InverseCellSize);

        // When Morton keys are enabled, we pad the descriptor array so that cell keys form a contiguous [0, dim*dim) range. Some descriptor slots past the
        // real world bounds stay unused — cheap (~1-2× the descriptor memory for typical grids) but keeps cell-key arithmetic branch-free.
        if (SpatialConfig.UseMortonCellKeys)
        {
            KeySpaceDim = NextPowerOfTwo(Math.Max(GridWidth, GridHeight));
            CellCount   = KeySpaceDim * KeySpaceDim;
        }
        else
        {
#pragma warning disable CS0162 // Unreachable code — deliberate const-bool feature flag
            KeySpaceDim = Math.Max(GridWidth, GridHeight);
            CellCount   = GridWidth * GridHeight;
#pragma warning restore CS0162
        }

        // Morton encoding interleaves 16 bits per axis into a 32-bit key. Cast to int, that tops out at
        // 32 768 (0x8000) per axis before the sign bit gets set and cell-key math goes negative. Reject
        // oversize grids at config time with a clear error — long-based Morton is a follow-up.
        if (KeySpaceDim > 32_768)
        {
            throw new ArgumentOutOfRangeException(nameof(cellSize),
                $"Grid dimensions produce a KeySpaceDim of {KeySpaceDim} per axis, which exceeds the 32 768 " +
                $"limit imposed by the 32-bit Morton encoding. Use a larger cell size, a smaller world, or " +
                $"wait for the long-based Morton follow-up.");
        }
    }

    private static int NextPowerOfTwo(int value)
    {
        if (value <= 1)
        {
            return 1;
        }
        int v = value - 1;
        v |= v >> 1;
        v |= v >> 2;
        v |= v >> 4;
        v |= v >> 8;
        v |= v >> 16;
        return v + 1;
    }
}
