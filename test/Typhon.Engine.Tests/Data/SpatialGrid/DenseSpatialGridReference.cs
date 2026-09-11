using System;
using System.Collections.Generic;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// A trivially-correct dense cell grid, kept as the differential ORACLE for the VDB grid (#872 step 8, AC-8.1 and §8.4's <c>SQ-01</c> guard).
/// </summary>
/// <remarks>
/// <para>Design §7.6 is explicit about where this belongs: <i>"Ship exactly two implementations: VDB (production) and a dense reference used only as a test
/// oracle, never offered as a production option."</i> Living in the test assembly is what makes that structural rather than aspirational — nothing in
/// <c>src/</c> can reach it, so it cannot become the second production implementation that §7.5's con 4 warns about ("dead alternatives rot").</para>
/// <para><b>It is deliberately dumb.</b> One array covering every cell the world bounds imply, indexed row-major, with the clamp written out longhand. It
/// exists to be obviously right, not fast — an oracle that shares an optimisation with the thing it checks cannot catch a bug in that optimisation. In
/// particular it does no block arithmetic, holds no hash map, and never defers a cell's creation.</para>
/// </remarks>
sealed class DenseSpatialGridReference
{
    private readonly SpatialGridConfig _config;
    private readonly bool[] _occupied;

    public DenseSpatialGridReference(SpatialGridConfig config)
    {
        _config = config;
        _occupied = new bool[config.CellCount];
    }

    /// <summary>Row-major dense key. Not comparable with a VDB cell key — the VDB's is a pool slot — so tests compare COORDINATES, never keys.</summary>
    public int KeyOf(int cellX, int cellY, int cellZ) => ((cellZ * _config.GridHeight) + cellY) * _config.GridWidth + cellX;

    public (int x, int y, int z) CoordsOf(int denseKey)
    {
        int plane = _config.GridWidth * _config.GridHeight;
        int z = denseKey / plane;
        int rem = denseKey - (z * plane);
        return (rem % _config.GridWidth, rem / _config.GridWidth, z);
    }

    /// <summary>Clamped cell coordinates of a world point. Longhand on purpose — the production path folds all three axes through one helper.</summary>
    /// <remarks>
    /// Multiplies by <c>InverseCellSize</c> rather than dividing by <c>CellSize</c>, and that is not a stylistic echo of the implementation. The two differ
    /// in the last bit for most cell sizes — measured: 0 disagreements at cellSize 100 over 5 M draws, but 703 at 3, 868 at 7 and 1 472 at 1000. An oracle
    /// that disagrees on float rounding reds the differential for a reason that has nothing to do with the structure under test, and the blame lands on the
    /// VDB grid. <c>InverseCellSize</c> is part of the SPECIFICATION — it is a field on the config, not a derived detail of the grid — so sharing it costs no
    /// independence. What makes this an oracle is the dense array, the row-major indexing and the eager allocation, none of which the grid has.
    /// </remarks>
    public (int x, int y, int z) CellOfPoint(float worldX, float worldY, float worldZ)
    {
        // Math.Floor, not MathF: the oracle has to floor in the SAME precision the grid does, or it stops being an oracle at the magnitudes #914's f64
        // world frame exists for — the two would disagree on a coordinate near a cell edge and the differential test would blame the grid.
        int cx = (int)Math.Floor((worldX - _config.WorldMin.X) * _config.InverseCellSize);
        int cy = (int)Math.Floor((worldY - _config.WorldMin.Y) * _config.InverseCellSize);
        int cz = (int)Math.Floor((worldZ - _config.WorldMin.Z) * _config.InverseCellSize);

        if (cx < 0) { cx = 0; }
        if (cy < 0) { cy = 0; }
        if (cz < 0) { cz = 0; }
        if (cx > _config.GridWidth - 1) { cx = _config.GridWidth - 1; }
        if (cy > _config.GridHeight - 1) { cy = _config.GridHeight - 1; }
        if (cz > _config.GridDepth - 1) { cz = _config.GridDepth - 1; }
        return (cx, cy, cz);
    }

    /// <summary>Mark a cell occupied — the oracle's equivalent of the VDB creating one.</summary>
    public void Occupy(int cellX, int cellY, int cellZ) => _occupied[KeyOf(cellX, cellY, cellZ)] = true;

    public bool IsOccupied(int cellX, int cellY, int cellZ) =>
        (uint)cellX < (uint)_config.GridWidth
        && (uint)cellY < (uint)_config.GridHeight
        && (uint)cellZ < (uint)_config.GridDepth
        && _occupied[KeyOf(cellX, cellY, cellZ)];

    public int OccupiedCount
    {
        get
        {
            int n = 0;
            for (int i = 0; i < _occupied.Length; i++)
            {
                if (_occupied[i]) { n++; }
            }
            return n;
        }
    }

    /// <summary>Every occupied cell's coordinates, ascending by dense key — a stable order both sides can be compared in.</summary>
    public List<(int x, int y, int z)> OccupiedCells()
    {
        var cells = new List<(int, int, int)>();
        for (int i = 0; i < _occupied.Length; i++)
        {
            if (_occupied[i]) { cells.Add(CoordsOf(i)); }
        }
        return cells;
    }

    /// <summary>The occupied members of a cell's 26-neighbourhood, in a fixed order.</summary>
    public List<(int x, int y, int z)> OccupiedNeighbours(int cellX, int cellY, int cellZ)
    {
        var found = new List<(int, int, int)>();
        for (int dz = -1; dz <= 1; dz++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0 && dz == 0) { continue; }
                    if (IsOccupied(cellX + dx, cellY + dy, cellZ + dz)) { found.Add((cellX + dx, cellY + dy, cellZ + dz)); }
                }
            }
        }
        return found;
    }
}
