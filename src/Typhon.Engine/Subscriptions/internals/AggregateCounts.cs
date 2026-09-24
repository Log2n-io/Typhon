using System;
using System.Collections.Generic;

namespace Typhon.Engine.Internals;

/// <summary>
/// One aggregate grid's counts (09 § 8, SUB-24): per tile, the live entities of each of its archetypes whose v̂ lies in it, and the last tick they changed.
/// </summary>
/// <remarks>
/// <para>
/// <b>From the push step's events, not from a scan.</b> A spawn adds one where it appears, a destroy removes one where it was, a mover that changed cell
/// moves one — the same deltas the occupancy (SUB-24) takes, per archetype and tile, applied serially when the index finishes. A tick the occupancy recounts,
/// the counts are recounted too, from the blocks.
/// </para>
/// <para>
/// <b>Rows, not a dense grid.</b> A tile gets a row the first time an entity is counted in it and keeps it: a world's visited tiles, not its extent. Rows
/// grow past their high-water mark only.
/// </para>
/// </remarks>
internal sealed class AggregateCounts
{
    private readonly Dictionary<uint, int> _rowOf = [];

    public AggregateCounts(int gridIdx, double originX, double originY, double originZ, double tileM, int dimX, int dimY, int dimZ, int[] columns,
        int archetypeCount)
    {
        GridIdx = gridIdx;
        OriginX = originX;
        OriginY = originY;
        OriginZ = originZ;
        TileM = tileM;
        DimX = dimX;
        DimY = dimY;
        DimZ = dimZ;
        Columns = columns;
        ArchetypeCount = archetypeCount;
        Counts = new int[64 * archetypeCount];
    }

    /// <summary>The catalog's grid index, which an <c>AGG</c> block names.</summary>
    public int GridIdx { get; }

    public double OriginX { get; }

    public double OriginY { get; }

    public double OriginZ { get; }

    public double TileM { get; }

    public int DimX { get; }

    public int DimY { get; }

    public int DimZ { get; }

    /// <summary>By plan index: the archetype's column in this grid, −1 when the grid does not count it.</summary>
    public int[] Columns { get; }

    public int ArchetypeCount { get; }

    /// <summary>Each row's tile index, row-major (03 § 12: axis 0 fastest).</summary>
    public uint[] Tiles = new uint[64];

    /// <summary>Row-major counts, <see cref="ArchetypeCount"/> per row.</summary>
    public int[] Counts;

    /// <summary>Each row's last tick of change.</summary>
    public uint[] Stamps = new uint[64];

    public int Rows;

    /// <summary>The tile a point lies in, clamped to the grid.</summary>
    public uint TileOf(float x, float y, float z)
    {
        var tx = Math.Clamp((int)Math.Floor((x - OriginX) / TileM), 0, DimX - 1);
        var ty = Math.Clamp((int)Math.Floor((y - OriginY) / TileM), 0, DimY - 1);
        var tz = DimZ > 1 ? Math.Clamp((int)Math.Floor((z - OriginZ) / TileM), 0, DimZ - 1) : 0;
        return (uint)(tx + (DimX * (ty + ((long)DimY * tz))));
    }

    /// <summary>Whether a tile's box meets a sphere (a disc in a flat grid).</summary>
    public bool Meets(uint tile, double cx, double cy, double cz, double radius)
    {
        var tx = (int)(tile % (uint)DimX);
        var rest = tile / (uint)DimX;
        var ty = (int)(rest % (uint)DimY);
        var tz = (int)(rest / (uint)DimY);
        var x0 = OriginX + (tx * TileM);
        var y0 = OriginY + (ty * TileM);
        var dx = Math.Max(Math.Max(x0 - cx, 0d), cx - (x0 + TileM));
        var dy = Math.Max(Math.Max(y0 - cy, 0d), cy - (y0 + TileM));
        var d2 = (dx * dx) + (dy * dy);
        if (DimZ > 1)
        {
            var z0 = OriginZ + (tz * TileM);
            var dz = Math.Max(Math.Max(z0 - cz, 0d), cz - (z0 + TileM));
            d2 += dz * dz;
        }

        return d2 <= radius * radius;
    }

    /// <summary>Counts <paramref name="delta"/> entities of plan archetype <paramref name="plan"/> at a point.</summary>
    public void Add(int plan, float x, float y, float z, int delta, uint tick)
    {
        var column = (uint)plan < (uint)Columns.Length ? Columns[plan] : -1;
        if (column < 0)
        {
            return;
        }

        var row = RowOf(TileOf(x, y, z));
        Counts[(row * ArchetypeCount) + column] += delta;
        Stamps[row] = tick;
    }

    /// <summary>Zeroes every count before a recount, stamping every row: a recount may have moved any of them.</summary>
    public void Zero(uint tick)
    {
        Array.Clear(Counts, 0, Rows * ArchetypeCount);
        Array.Fill(Stamps, tick, 0, Rows);
    }

    /// <summary>An empty grid of the same shape: a recount's target.</summary>
    public AggregateCounts EmptyCopy() => new(GridIdx, OriginX, OriginY, OriginZ, TileM, DimX, DimY, DimZ, Columns, ArchetypeCount);

    /// <summary>Tests: the (tile, column) pairs whose counts differ between two grids of one shape.</summary>
    public int Differences(AggregateCounts other)
    {
        var differences = 0;
        foreach (var (tile, row) in _rowOf)
        {
            for (var c = 0; c < ArchetypeCount; c++)
            {
                var mine = Counts[(row * ArchetypeCount) + c];
                var theirs = other._rowOf.TryGetValue(tile, out var o) ? other.Counts[(o * ArchetypeCount) + c] : 0;
                differences += mine != theirs ? 1 : 0;
            }
        }

        foreach (var (tile, row) in other._rowOf)
        {
            for (var c = 0; c < ArchetypeCount; c++)
            {
                differences += !_rowOf.ContainsKey(tile) && other.Counts[(row * ArchetypeCount) + c] != 0 ? 1 : 0;
            }
        }

        return differences;
    }

    /// <summary>Tests: a tile's count for plan archetype <paramref name="plan"/>.</summary>
    public int CountAt(uint tile, int plan) =>
        _rowOf.TryGetValue(tile, out var row) && (uint)plan < (uint)Columns.Length && Columns[plan] >= 0 ? Counts[(row * ArchetypeCount) + Columns[plan]] : 0;

    private int RowOf(uint tile)
    {
        if (_rowOf.TryGetValue(tile, out var row))
        {
            return row;
        }

        if (Rows == Tiles.Length)
        {
            Array.Resize(ref Tiles, Rows * 2);
            Array.Resize(ref Stamps, Rows * 2);
            Array.Resize(ref Counts, Rows * 2 * ArchetypeCount);
        }

        Tiles[Rows] = tile;
        _rowOf[tile] = Rows;
        return Rows++;
    }
}
