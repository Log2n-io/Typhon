using System;
using System.Runtime.CompilerServices;
using JetBrains.Annotations;

namespace Typhon.Engine.Internals;

/// <summary>
/// Engine-wide coarse spatial grid. Owns the per-cell descriptor array and the pool holding each cell's cluster list.
/// One instance per <see cref="DatabaseEngine"/>, configured once at startup via <see cref="DatabaseEngine.ConfigureSpatialGrid"/>.
/// </summary>
/// <remarks>
/// <para>Phase 1+2 scope of issue #229: this grid exists, is wired into the spawn path for spatial archetypes, and is rebuilt at startup.
/// It does <em>not</em> yet participate in migration (Phase 3) or per-cell R-Tree queries (#230).</para>
/// <para>The grid stores only transient state. Nothing in <see cref="CellState"/> is persisted;
/// <c>RebuildCellState</c> reconstructs everything from entity positions after a reopen.</para>
/// </remarks>
[PublicAPI]
internal sealed unsafe class SpatialGrid
{
    private readonly SpatialGridConfig _config;
    private readonly CellState[] _cells;

    // Issue #231: bumped every time SetCellTier actually changes a cell's tier byte. The per-archetype
    // TierClusterIndex uses this to skip its rebuild when no cell tier has changed since the last dispatch.
    private int _tierVersion;

    public SpatialGrid(SpatialGridConfig config)
    {
        _config = config;
        _cells = new CellState[config.CellCount];
    }

    public ref readonly SpatialGridConfig Config => ref _config;

    public int CellCount => _cells.Length;

    /// <summary>
    /// Monotonic version counter, incremented each time a <see cref="SetCellTier"/> call actually flips a cell's tier byte.
    /// Consumed by per-archetype <see cref="TierClusterIndex"/> to short-circuit rebuilds when nothing changed (issue #231).
    /// </summary>
    internal int TierVersion => _tierVersion;

    /// <summary>
    /// Assign a <see cref="SimTier"/> to a single cell. No-op when the cell already has the requested tier (avoids spurious version bumps).
    /// Passing <see cref="SimTier.None"/> clears the cell's tier — the tier index will then skip the cell entirely during rebuild.
    /// </summary>
    /// <remarks>
    /// <para>The tier byte stored on <see cref="CellState.Tier"/> is a single-bit flag value from <see cref="SimTier"/>.
    /// Callers must pass a single-bit tier; multi-bit combinations (e.g. <see cref="SimTier.Near"/>) are rejected because the rebuild path
    /// uses <see cref="System.Numerics.BitOperations.TrailingZeroCount(uint)"/> to map the byte to an array index.</para>
    /// <para>Intentionally <c>internal</c>: issue #231 exposes only the minimum needed for tests. The public <c>SpatialGridAccessor</c> /
    /// <c>TickContext.SpatialGrid</c> surface lands with the tick-lifecycle integration in #232.</para>
    /// </remarks>
    internal void SetCellTier(int cellKey, SimTier tier)
    {
        if (tier != SimTier.None && !tier.IsSingleTier())
        {
            throw new ArgumentException(
                $"SetCellTier requires a single-bit SimTier flag, got '{tier}'. Multi-tier combinations (e.g. SimTier.Near) are not valid at the cell level.",
                nameof(tier));
        }

        // Descriptive bounds error rather than the bare IndexOutOfRangeException from the array access.
        if ((uint)cellKey >= (uint)_cells.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(cellKey), cellKey,
                $"SetCellTier: cellKey {cellKey} is outside the valid range [0, {_cells.Length}). " +
                $"Compute cell keys via WorldToCellKey or ComputeCellKey to stay in range.");
        }

        ref var cell = ref _cells[cellKey];
        byte newTier = (byte)tier;
        if (cell.Tier != newTier)
        {
            byte oldTier = cell.Tier;
            cell.Tier = newTier;
            _tierVersion++;
            TyphonEvent.EmitSpatialGridCellTierChange(cellKey, oldTier, newTier);
        }
    }

    /// <summary>
    /// Access a cell descriptor by cell key for read + write (callers bump <see cref="CellState.EntityCount"/>
    /// and <see cref="CellState.ClusterCount"/> directly).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref CellState GetCell(int cellKey) => ref _cells[cellKey];

    /// <summary>
    /// Convert a world-space 2D point to a grid cell key. Points outside the configured bounds are clamped to the nearest valid cell — callers that care
    /// about "out of bounds" should test bounds themselves before calling.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int WorldToCellKey(float worldX, float worldY)
    {
        // Guard against NaN / ±Infinity: relational comparisons with NaN return false on both sides,
        // so the clamp below wouldn't catch a NaN — it would slip through as cellX=0 (or whatever the
        // implementation-defined (int)NaN returns on the current runtime). Rather than produce a
        // silently wrong cell key, throw so the caller fixes the upstream bug.
        if (!float.IsFinite(worldX) || !float.IsFinite(worldY))
        {
            throw new ArgumentException(
                $"WorldToCellKey received a non-finite coordinate: ({worldX}, {worldY}). " +
                $"Position data is corrupted upstream — spatial grid cannot place a NaN/Infinity entity.");
        }

        // Convert to cell coordinates
        int cellX = (int)MathF.Floor((worldX - _config.WorldMin.X) * _config.InverseCellSize);
        int cellY = (int)MathF.Floor((worldY - _config.WorldMin.Y) * _config.InverseCellSize);

        // Clamp to valid grid range
        if (cellX < 0)
        {
            cellX = 0;
        }
        else if (cellX >= _config.GridWidth)
        {
            cellX = _config.GridWidth - 1;
        }

        if (cellY < 0)
        {
            cellY = 0;
        }
        else if (cellY >= _config.GridHeight)
        {
            cellY = _config.GridHeight - 1;
        }

        return ComputeCellKey(cellX, cellY);
    }

    /// <summary>
    /// Convert a world-space 2D AABB to the inclusive cell-coordinate range it overlaps. Used by query
    /// paths that iterate all cells touched by a query rectangle (issue #230). Out-of-bounds inputs are
    /// clamped to the grid extent; <see cref="float.NaN"/> / <see cref="float.PositiveInfinity"/> inputs
    /// throw because they would produce meaningless cell indices.
    /// </summary>
    /// <param name="minX">Query AABB minimum X in world units.</param>
    /// <param name="minY">Query AABB minimum Y in world units.</param>
    /// <param name="maxX">Query AABB maximum X in world units.</param>
    /// <param name="maxY">Query AABB maximum Y in world units.</param>
    /// <param name="cellMinX">Inclusive minimum cell X coordinate.</param>
    /// <param name="cellMinY">Inclusive minimum cell Y coordinate.</param>
    /// <param name="cellMaxX">Inclusive maximum cell X coordinate.</param>
    /// <param name="cellMaxY">Inclusive maximum cell Y coordinate.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WorldToCellRange(float minX, float minY, float maxX, float maxY, out int cellMinX, out int cellMinY, out int cellMaxX, out int cellMaxY)
    {
        if (!float.IsFinite(minX) || !float.IsFinite(minY) || !float.IsFinite(maxX) || !float.IsFinite(maxY))
        {
            throw new ArgumentException(
                $"WorldToCellRange received non-finite coordinates: ({minX}, {minY}, {maxX}, {maxY}). " +
                $"Query data is corrupted upstream — spatial grid cannot compute a cell range for a NaN/Infinity AABB.");
        }

        int rawMinX = (int)MathF.Floor((minX - _config.WorldMin.X) * _config.InverseCellSize);
        int rawMinY = (int)MathF.Floor((minY - _config.WorldMin.Y) * _config.InverseCellSize);
        int rawMaxX = (int)MathF.Floor((maxX - _config.WorldMin.X) * _config.InverseCellSize);
        int rawMaxY = (int)MathF.Floor((maxY - _config.WorldMin.Y) * _config.InverseCellSize);

        cellMinX = Math.Clamp(rawMinX, 0, _config.GridWidth - 1);
        cellMinY = Math.Clamp(rawMinY, 0, _config.GridHeight - 1);
        cellMaxX = Math.Clamp(rawMaxX, 0, _config.GridWidth - 1);
        cellMaxY = Math.Clamp(rawMaxY, 0, _config.GridHeight - 1);
    }

    /// <summary>
    /// Extract a 2D centre point from a spatial field pointer. Supports <see cref="SpatialFieldType.AABB2F"/>
    /// (centre of the AABB) and <see cref="SpatialFieldType.BSphere2F"/> (sphere centre). Other field types
    /// are unsupported in Phase 1+2 and will throw at config time, so this method does not re-validate.
    /// </summary>
    /// <remarks>
    /// Shared by <see cref="WorldToCellKeyFromSpatialField"/> and the cell-crossing detection loop in
    /// <c>DatabaseEngine.DetectClusterMigrations</c> (issue #229 Phase 3). The detection path reuses the
    /// extracted center for both the hysteresis bounds check and the fallback <see cref="WorldToCellKey"/> call,
    /// avoiding a double read of the field memory.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ReadSpatialCenter2D(byte* fieldPtr, SpatialFieldType fieldType, out float posX, out float posY)
    {
        switch (fieldType)
        {
            case SpatialFieldType.AABB2F:
            {
                float minX = *(float*)fieldPtr;
                float minY = *(float*)(fieldPtr + sizeof(float));
                float maxX = *(float*)(fieldPtr + 2 * sizeof(float));
                float maxY = *(float*)(fieldPtr + 3 * sizeof(float));
                posX = (minX + maxX) * 0.5f;
                posY = (minY + maxY) * 0.5f;
                return;
            }
            case SpatialFieldType.AABB3F:
            {
                // 3D AABB layout is [minX, minY, minZ, maxX, maxY, maxZ]. For 2D cell bucketing we use only the XY center — Z is used at narrowphase.
                // Issue #230 Phase 3.
                float minX = *(float*)fieldPtr;
                float minY = *(float*)(fieldPtr + sizeof(float));
                float maxX = *(float*)(fieldPtr + 3 * sizeof(float));
                float maxY = *(float*)(fieldPtr + 4 * sizeof(float));
                posX = (minX + maxX) * 0.5f;
                posY = (minY + maxY) * 0.5f;
                return;
            }
            case SpatialFieldType.BSphere2F:
            {
                // BSphere2F — CenterX, CenterY, Radius
                posX = *(float*)fieldPtr;
                posY = *(float*)(fieldPtr + sizeof(float));
                return;
            }
            case SpatialFieldType.BSphere3F:
            {
                // BSphere3F — CenterX, CenterY, CenterZ, Radius. Same 2D bucketing approach as AABB3F.
                posX = *(float*)fieldPtr;
                posY = *(float*)(fieldPtr + sizeof(float));
                return;
            }
            default:
                // ValidateSupportedFieldType rejects f64 tiers at ConfigureSpatialGrid time, so this path should not be reachable. Defensive fallback
                // to help diagnose any future field-type addition that forgot to update this dispatch.
                throw new NotSupportedException(
                    $"ReadSpatialCenter2D: field type '{fieldType}' is not supported. f32 tiers (2D and 3D) only.");
        }
    }

    /// <summary>
    /// Extract a 2D centre point from a spatial field pointer and convert it to a cell key. Supports <see cref="SpatialFieldType.AABB2F"/> (centre of the AABB)
    /// and <see cref="SpatialFieldType.BSphere2F"/> (sphere centre). Other field types are unsupported in Phase 1+2 and will throw at config time, so this
    /// method does not re-validate.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int WorldToCellKeyFromSpatialField(byte* fieldPtr, SpatialFieldType fieldType)
    {
        ReadSpatialCenter2D(fieldPtr, fieldType, out float posX, out float posY);
        return WorldToCellKey(posX, posY);
    }

    /// <summary>
    /// Throws if <paramref name="fieldType"/> is not supported by the spatial grid. Issue #230 Phase 3 extended support from 2D-only to both 2D and 3D f32
    /// tiers — cells are always 2D (XY) and 3D archetypes bucket entities by their XY center, ignoring Z. Z-axis filtering happens at the query narrowphase.
    /// f64 tiers remain deferred to a follow-up sub-issue of #228.
    /// </summary>
    public static void ValidateSupportedFieldType(SpatialFieldType fieldType, string archetypeName)
    {
        if (fieldType is SpatialFieldType.AABB2F or SpatialFieldType.BSphere2F or SpatialFieldType.AABB3F or SpatialFieldType.BSphere3F)
        {
            return;
        }
        throw new NotSupportedException(
            $"Spatial archetype '{archetypeName}' uses field type '{fieldType}'. " +
            $"The spatial grid currently supports f32 spatial fields only (AABB2F, BSphere2F, AABB3F, BSphere3F). " +
            $"f64 variants are a planned follow-up.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ComputeCellKey(int cellX, int cellY)
    {
        if (SpatialConfig.UseMortonCellKeys)
        {
            return MortonKeys.Encode2D(cellX, cellY);
        }
#pragma warning disable CS0162 // Unreachable code — deliberate const-bool feature flag
        // ReSharper disable once HeuristicUnreachableCode
        return cellY * _config.GridWidth + cellX;
#pragma warning restore CS0162
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (int x, int y) CellKeyToCoords(int cellKey)
    {
        if (SpatialConfig.UseMortonCellKeys)
        {
            return MortonKeys.Decode2D(cellKey);
        }
#pragma warning disable CS0162 // Unreachable code — deliberate const-bool feature flag
        // ReSharper disable once HeuristicUnreachableCode
        return (cellKey % _config.GridWidth, cellKey / _config.GridWidth);
#pragma warning restore CS0162
    }

    /// <summary>
    /// Drop all cell state. Called by <c>RebuildCellState</c> before reconstructing the mapping from entity positions. Each archetype's own
    /// <c>CellClusterPool</c> is reset separately by the archetype itself — this method only clears per-cell global counters (Q10).
    /// </summary>
    public void ResetCellState() => Array.Clear(_cells);

    // ═══════════════════════════════════════════════════════════════════════
    // Issue #234: multi-observer helpers
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Set a cell's tier using min (promote-only) semantics (issue #234 Q7). If the cell's current tier is already higher priority
    /// (lower flag value, e.g. <see cref="SimTier.Tier0"/> = 1 vs <see cref="SimTier.Tier1"/> = 2), the call is a no-op. If the cell
    /// is unset (<see cref="SimTier.None"/> / 0), any tier overrides it. Bumps <see cref="TierVersion"/> only when the cell actually changes.
    /// </summary>
    internal void SetCellTierMin(int cellKey, SimTier tier)
    {
        if (tier == SimTier.None || !tier.IsSingleTier())
        {
            return;
        }

        if ((uint)cellKey >= (uint)_cells.Length)
        {
            return; // Silently ignore out-of-bounds (AABB iteration may produce edge cells)
        }

        ref var cell = ref _cells[cellKey];
        byte newTier = (byte)tier;
        // Min semantics: 0 (None/unset) is overridden by any tier. Among set tiers, keep the lower value (higher priority).
        if (cell.Tier == 0 || newTier < cell.Tier)
        {
            cell.Tier = newTier;
            _tierVersion++;
        }
    }

    /// <summary>
    /// Bulk-set all cells to the specified tier (issue #234 Q7). Typically called at the start of <c>TierAssignment</c> to reset all cells
    /// to <see cref="SimTier.Tier3"/> before applying per-observer <see cref="SetCellTierMin"/> or <see cref="SetTierInAABB"/>.
    /// Bumps <see cref="TierVersion"/> once.
    /// </summary>
    internal void ResetAllTiers(SimTier tier)
    {
        byte val = (byte)tier;
        bool changed = false;
        for (int i = 0; i < _cells.Length; i++)
        {
            if (_cells[i].Tier != val)
            {
                _cells[i].Tier = val;
                changed = true;
            }
        }
        if (changed)
        {
            _tierVersion++;
        }
    }

    /// <summary>
    /// Set tiers for all cells overlapping a world-space AABB, using min (promote-only) semantics (issue #234 Q7).
    /// Combines <see cref="WorldToCellRange"/> with <see cref="SetCellTierMin"/>. Useful for multi-observer tier assignment:
    /// <c>grid.ResetAllTiers(SimTier.Tier3); foreach observer: grid.SetTierInAABB(obs.ViewAABB, SimTier.Tier0);</c>
    /// </summary>
    internal void SetTierInAABB(float minX, float minY, float maxX, float maxY, SimTier tier)
    {
        WorldToCellRange(minX, minY, maxX, maxY, out int cellMinX, out int cellMinY, out int cellMaxX, out int cellMaxY);
        for (int cy = cellMinY; cy <= cellMaxY; cy++)
        {
            for (int cx = cellMinX; cx <= cellMaxX; cx++)
            {
                int cellKey = ComputeCellKey(cx, cy);
                SetCellTierMin(cellKey, tier);
            }
        }
    }
}
