using System;
using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Game-facing read/write accessor for the engine's <see cref="SpatialGrid"/> (issue #232). Exposed on <see cref="TickContext.SpatialGrid"/>
/// so system callbacks can assign cell tiers, query cell coordinates, and use multi-observer helpers (<see cref="SetCellTierMin"/>,
/// <see cref="ResetAllTiers"/>, <see cref="SetTierInAABB"/>).
/// </summary>
/// <remarks>
/// <para><b>8-byte readonly struct</b> (one managed reference). The design doc specifies <c>ref struct</c> but <see cref="TickContext"/> is a
/// regular struct — ref structs cannot be fields of non-ref structs. Using <c>readonly struct</c> with a single reference field achieves the
/// same zero-allocation, thin-wrapper semantics without the composability constraint.</para>
/// <para>All methods delegate to the internal <see cref="SpatialGrid"/>. No logic duplication.</para>
/// </remarks>
[PublicAPI]
public readonly struct SpatialGridAccessor
{
    private readonly SpatialGrid _grid;

    internal SpatialGridAccessor(SpatialGrid grid) => _grid = grid;

    /// <summary>True when the engine has a configured spatial grid. False when no grid was set up (non-spatial game) or during shutdown.</summary>
    public bool IsValid => _grid != null;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private void ThrowIfInvalid()
    {
        if (_grid == null)
        {
            throw new InvalidOperationException("SpatialGridAccessor is not valid. Check IsValid before calling grid methods, or configure a SpatialGrid via DatabaseEngine.ConfigureSpatialGrid.");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Grid metadata
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Total number of cells in the grid.</summary>
    public int CellCount { get { ThrowIfInvalid(); return _grid.CellCount; } }

    /// <summary>Grid width in cells.</summary>
    public int GridWidth { get { ThrowIfInvalid(); return _grid.Config.GridWidth; } }

    /// <summary>Grid height in cells.</summary>
    public int GridHeight { get { ThrowIfInvalid(); return _grid.Config.GridHeight; } }

    /// <summary>Cell size in world units.</summary>
    public float CellSize { get { ThrowIfInvalid(); return _grid.Config.CellSize; } }

    /// <summary>Compute the flat cell key for the given cell coordinates. Accounts for Morton encoding when enabled.</summary>
    public int ComputeCellKey(int cellX, int cellY) { ThrowIfInvalid(); return _grid.ComputeCellKey(cellX, cellY); }

    // ═══════════════════════════════════════════════════════════════
    // Cell access
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Access a cell descriptor by cell key. Internal because <see cref="CellDescriptor"/> is an internal type.</summary>
    internal ref CellDescriptor GetCell(int cellKey) { ThrowIfInvalid(); return ref _grid.GetCell(cellKey); }

    /// <summary>Access a cell descriptor by grid coordinates. Internal because <see cref="CellDescriptor"/> is an internal type.</summary>
    internal ref CellDescriptor GetCell(int cellX, int cellY) { ThrowIfInvalid(); return ref _grid.GetCell(_grid.ComputeCellKey(cellX, cellY)); }

    // ═══════════════════════════════════════════════════════════════
    // Tier assignment
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Set a cell's tier by grid coordinates. Requires a single-bit <see cref="SimTier"/> flag.</summary>
    public void SetCellTier(int cellX, int cellY, SimTier tier) { ThrowIfInvalid(); _grid.SetCellTier(_grid.ComputeCellKey(cellX, cellY), tier); }

    /// <summary>
    /// Set a cell's tier using min (promote-only) semantics (Q7). If the cell already has a higher-priority tier (lower flag value),
    /// the call is a no-op. Enables multi-observer union: <c>ResetAllTiers(Tier3)</c> then <c>SetCellTierMin</c> per observer.
    /// </summary>
    public void SetCellTierMin(int cellX, int cellY, SimTier tier) { ThrowIfInvalid(); _grid.SetCellTierMin(_grid.ComputeCellKey(cellX, cellY), tier); }

    /// <summary>Bulk-set all cells to the specified tier. Typically called at the start of <c>TierAssignment</c>.</summary>
    public void ResetAllTiers(SimTier tier) { ThrowIfInvalid(); _grid.ResetAllTiers(tier); }

    /// <summary>Set tiers for all cells overlapping a world-space AABB, using min (promote-only) semantics.</summary>
    public void SetTierInAABB(float minX, float minY, float maxX, float maxY, SimTier tier)
    {
        ThrowIfInvalid();
        _grid.SetTierInAABB(minX, minY, maxX, maxY, tier);
    }

    // ═══════════════════════════════════════════════════════════════
    // Coordinate conversion
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Convert a world-space 2D point to a grid cell key. Points outside bounds are clamped.</summary>
    public int WorldToCell(float worldX, float worldY) { ThrowIfInvalid(); return _grid.WorldToCellKey(worldX, worldY); }

    /// <summary>Convert a cell key back to grid coordinates (x, y).</summary>
    public (int x, int y) GetCellCoords(int cellKey) { ThrowIfInvalid(); return _grid.CellKeyToCoords(cellKey); }
}
