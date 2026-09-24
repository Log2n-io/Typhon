using System;

namespace Typhon.Protocol;

/// <summary>The sub-block types inside a <c>DEBUG</c> block (03-wire-protocol § 3, W26). An unknown one is skipped by its length.</summary>
public static class DebugSubTypes
{
    /// <summary>The replication grid: its origin, cell side and cells per axis (<see cref="DebugGrid"/>).</summary>
    public const byte Grid = 0x01;

    /// <summary>Reserved: the clusters meeting the session's range. Not sent yet.</summary>
    public const byte ClusterAabbs = 0x02;

    /// <summary>Reserved: this tick's migrations in the session's range. Not sent yet.</summary>
    public const byte Migrations = 0x03;

    /// <summary>The session's push geometry: its anchor or hull and its delivered window (<see cref="PushGeometry"/>). Experimental: its layout can change.</summary>
    public const byte PushGeometry = 0x80;
}

/// <summary>
/// A <c>DEBUG</c> <c>GRID</c> sub-block (09 § 15): the replication grid every push session's cells are keyed on. Sent to a debugging session with its first
/// frame and with every <c>RESET</c>.
/// </summary>
/// <remarks>
/// <code>GRID := f64 originX | f64 originY | f64 originZ | f64 cellM | varu dimX | varu dimY | varu dimZ      (dimZ = 1 in a flat grid)</code>
/// Cell <c>(cx, cy, cz)</c> spans <c>[origin + c·cell, origin + (c + 1)·cell)</c> on each axis.
/// </remarks>
/// <param name="OriginX">The grid's lowest x, in metres.</param>
/// <param name="OriginY">The grid's lowest y.</param>
/// <param name="OriginZ">The grid's lowest z.</param>
/// <param name="CellM">The cell side, in metres.</param>
/// <param name="DimX">Cells along x.</param>
/// <param name="DimY">Cells along y.</param>
/// <param name="DimZ">Cells along z; 1 in a flat grid.</param>
public readonly record struct DebugGrid(double OriginX, double OriginY, double OriginZ, double CellM, int DimX, int DimY, int DimZ)
{
    /// <summary>The largest payload <see cref="Write"/> produces.</summary>
    public const int MaxBytes = (4 * 8) + (3 * 5);

    /// <summary>Writes the payload, without the sub-block's type and length.</summary>
    /// <param name="w">The writer.</param>
    public void Write(ref WireWriter w)
    {
        w.WriteF64(OriginX);
        w.WriteF64(OriginY);
        w.WriteF64(OriginZ);
        w.WriteF64(CellM);
        w.WriteVaru((uint)DimX);
        w.WriteVaru((uint)DimY);
        w.WriteVaru((uint)DimZ);
    }

    /// <summary>Reads a payload.</summary>
    /// <param name="payload">The sub-block's payload.</param>
    /// <returns>The grid.</returns>
    /// <exception cref="WireFormatException">The payload is truncated or has bytes left over.</exception>
    public static DebugGrid Read(ReadOnlySpan<byte> payload)
    {
        var r = new WireReader(payload);
        var grid = new DebugGrid(r.ReadF64(), r.ReadF64(), r.ReadF64(), r.ReadF64(), r.ReadVaruAtMost(int.MaxValue, "GRID dimX"),
            r.ReadVaruAtMost(int.MaxValue, "GRID dimY"), r.ReadVaruAtMost(int.MaxValue, "GRID dimZ"));
        r.ExpectEnd("GRID");
        return grid;
    }
}

/// <summary>The shape of a push session's geometry.</summary>
public enum PushShape : byte
{
    /// <summary>A sphere (a disc in a flat grid) around an anchor.</summary>
    Sphere = 0,

    /// <summary>The whole world, delivered in cell-key order.</summary>
    World = 1,

    /// <summary>A hull the client sent (<c>ClientRegion</c>).</summary>
    Region = 2,
}

/// <summary>The flags of a <see cref="PushGeometry"/>.</summary>
[Flags]
public enum PushGeometryFlags : byte
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary>Every cell the geometry needs has been delivered, as of this frame — the frame's own <c>VIEW_COMPLETE</c>.</summary>
    ViewComplete = 1,

    /// <summary>The grid is deep: the window has <c>W²</c> rows, row <c>(lz · W) + ly</c>; otherwise <c>W</c> rows, row <c>ly</c>.</summary>
    Deep = 2,
}

/// <summary>
/// A <c>DEBUG</c> <c>PUSH_GEOMETRY</c> sub-block (09 § 15): what the session holds as of the frame carrying it — its anchor or hull, its delivered cells, its
/// LOD level. Sent to a debugging session whenever it changes. In the experimental range: its layout can change without a protocol version.
/// </summary>
/// <remarks>
/// <code>
/// PUSH_GEOMETRY := u8 shape | u8 flags | body
///   SPHERE := f64 anchorX | f64 anchorY | f64 anchorZ | f32 radiusM | f32 slackM | u8 level | window
///   WORLD  := u64 cursor                                        every cell key below it delivered; 2⁶⁴ − 1 once the walk passed the last cell
///   REGION := u8 dims | varu vertexCount | (f64 x | f64 y [| f64 z if dims = 3])* | varu held | varu nearBudget | window
///   window := vari originX | vari originY | vari originZ | u8 W | row*
///             W rows, or W² if DEEP; each ⌈W / 8⌉ bytes little-endian, bit i = the cell at originX + i of the row's (y, z)
/// </code>
/// A sphere's radius is R′ as the session holds it (after any last-resort shrink), its slack the profile's half-band h; a region's <c>held</c> is the
/// near budget's estimate and <c>nearBudget</c> 0 when the profile has none. A region with no hull yet has no vertices.
/// </remarks>
public sealed class PushGeometry
{
    /// <summary>The window's most rows: 53 in a flat grid, 14² in a deep one (the window bound, 2 809 cells).</summary>
    public const int MaxRows = 196;

    /// <summary>The most vertices a region carries.</summary>
    public const int MaxVertices = 16;

    /// <summary>The largest payload: a sixteen-vertex deep region with the widest window.</summary>
    public const int MaxBytes = 2 + 1 + 5 + (MaxVertices * 24) + 10 + 15 + 1 + (MaxRows * 7);

    /// <summary>The shape.</summary>
    public PushShape Shape { get; init; }

    /// <summary>The flags.</summary>
    public PushGeometryFlags Flags { get; init; }

    /// <summary>A sphere's anchor x.</summary>
    public double AnchorX { get; init; }

    /// <summary>A sphere's anchor y.</summary>
    public double AnchorY { get; init; }

    /// <summary>A sphere's anchor z.</summary>
    public double AnchorZ { get; init; }

    /// <summary>A sphere's radius R′, as the session holds it.</summary>
    public double RadiusM { get; init; }

    /// <summary>A sphere profile's half-band h.</summary>
    public double SlackM { get; init; }

    /// <summary>A sphere's LOD level.</summary>
    public int Level { get; init; }

    /// <summary>A World session's cursor.</summary>
    public ulong Cursor { get; init; }

    /// <summary>A region's dimensions, 2 or 3.</summary>
    public int Dims { get; init; }

    /// <summary>A region's vertices, <see cref="Dims"/> numbers each.</summary>
    public double[] Vertices { get; init; } = [];

    /// <summary>A region's near-budget estimate.</summary>
    public int Held { get; init; }

    /// <summary>A region profile's near budget; 0 for none.</summary>
    public int NearBudget { get; init; }

    /// <summary>The window's lowest cell x.</summary>
    public int WindowX { get; init; }

    /// <summary>The window's lowest cell y.</summary>
    public int WindowY { get; init; }

    /// <summary>The window's lowest cell z.</summary>
    public int WindowZ { get; init; }

    /// <summary>The window's width per axis, in cells; 0 for a World session.</summary>
    public int Window { get; init; }

    /// <summary>The window's rows.</summary>
    public ulong[] Rows { get; init; } = [];

    /// <summary>Whether the session holds a cell: inside the window, with its bit set.</summary>
    /// <param name="cx">The cell's x.</param>
    /// <param name="cy">The cell's y.</param>
    /// <param name="cz">The cell's z; ignored in a flat grid.</param>
    /// <returns>Whether it is delivered.</returns>
    public bool Delivered(int cx, int cy, int cz)
    {
        var lx = cx - WindowX;
        var ly = cy - WindowY;
        var lz = (Flags & PushGeometryFlags.Deep) != 0 ? cz - WindowZ : 0;
        if ((uint)lx >= (uint)Window || (uint)ly >= (uint)Window || (uint)lz >= (uint)Window)
        {
            return false;
        }

        return ((Rows[(lz * Window) + ly] >> lx) & 1) != 0;
    }

    /// <summary>Writes a sphere's payload up to its window, which <see cref="WriteWindow"/> writes next.</summary>
    /// <param name="w">The writer.</param>
    /// <param name="flags">The flags.</param>
    /// <param name="anchorX">The anchor's x.</param>
    /// <param name="anchorY">The anchor's y.</param>
    /// <param name="anchorZ">The anchor's z.</param>
    /// <param name="radiusM">R′.</param>
    /// <param name="slackM">The profile's half-band.</param>
    /// <param name="level">The LOD level.</param>
    public static void WriteSphere(ref WireWriter w, PushGeometryFlags flags, double anchorX, double anchorY, double anchorZ, double radiusM, double slackM,
        int level)
    {
        w.WriteU8((byte)PushShape.Sphere);
        w.WriteU8((byte)flags);
        w.WriteF64(anchorX);
        w.WriteF64(anchorY);
        w.WriteF64(anchorZ);
        w.WriteF32(radiusM);
        w.WriteF32(slackM);
        w.WriteU8((byte)level);
    }

    /// <summary>Writes a World session's payload, whole.</summary>
    /// <param name="w">The writer.</param>
    /// <param name="flags">The flags.</param>
    /// <param name="cursor">The cursor.</param>
    public static void WriteWorld(ref WireWriter w, PushGeometryFlags flags, ulong cursor)
    {
        w.WriteU8((byte)PushShape.World);
        w.WriteU8((byte)flags);
        w.WriteU64(cursor);
    }

    /// <summary>Writes a region's payload up to its window, which <see cref="WriteWindow"/> writes next.</summary>
    /// <param name="w">The writer.</param>
    /// <param name="flags">The flags.</param>
    /// <param name="dims">2 or 3.</param>
    /// <param name="vertices">The vertices, three numbers each whatever <paramref name="dims"/>: a flat region's z is not written.</param>
    /// <param name="held">The near budget's estimate.</param>
    /// <param name="nearBudget">The profile's near budget; 0 for none.</param>
    public static void WriteRegion(ref WireWriter w, PushGeometryFlags flags, int dims, scoped ReadOnlySpan<double> vertices, int held, int nearBudget)
    {
        if (dims is not (2 or 3) || vertices.Length % 3 != 0 || vertices.Length / 3 > MaxVertices)
        {
            throw new ArgumentException("a region has 2 or 3 dimensions and at most 16 vertices of three numbers each");
        }

        w.WriteU8((byte)PushShape.Region);
        w.WriteU8((byte)flags);
        w.WriteU8((byte)dims);
        w.WriteVaru((uint)(vertices.Length / 3));
        for (var i = 0; i < vertices.Length; i += 3)
        {
            w.WriteF64(vertices[i]);
            w.WriteF64(vertices[i + 1]);
            if (dims == 3)
            {
                w.WriteF64(vertices[i + 2]);
            }
        }

        w.WriteVaru((uint)held);
        w.WriteVaru((uint)nearBudget);
    }

    /// <summary>Writes a window: its origin, its width, and its rows.</summary>
    /// <param name="w">The writer.</param>
    /// <param name="originX">The window's lowest cell x.</param>
    /// <param name="originY">The window's lowest cell y.</param>
    /// <param name="originZ">The window's lowest cell z.</param>
    /// <param name="window">Its width per axis, at most 64.</param>
    /// <param name="rows">Its rows: <paramref name="window"/>, or its square in a deep grid.</param>
    public static void WriteWindow(ref WireWriter w, int originX, int originY, int originZ, int window, scoped ReadOnlySpan<ulong> rows)
    {
        if ((uint)window > 64 || rows.Length > MaxRows)
        {
            throw new ArgumentException($"a window is at most 64 cells wide and {MaxRows} rows");
        }

        w.WriteVari(originX);
        w.WriteVari(originY);
        w.WriteVari(originZ);
        w.WriteU8((byte)window);
        var rowBytes = (window + 7) >> 3;
        Span<byte> row = stackalloc byte[8];
        foreach (var bits in rows)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(row, bits);
            w.WriteBytes(row[..rowBytes]);
        }
    }

    /// <summary>Reads a payload.</summary>
    /// <param name="payload">The sub-block's payload.</param>
    /// <returns>The geometry.</returns>
    /// <exception cref="WireFormatException">The payload is truncated, malformed or has bytes left over.</exception>
    public static PushGeometry Read(ReadOnlySpan<byte> payload)
    {
        var r = new WireReader(payload);
        var shape = r.ReadU8();
        var flags = (PushGeometryFlags)r.ReadU8();
        PushGeometry geometry;
        switch ((PushShape)shape)
        {
            case PushShape.World:
                geometry = new PushGeometry { Shape = PushShape.World, Flags = flags, Cursor = r.ReadU64() };
                break;
            case PushShape.Sphere:
            {
                var (x, y, z) = (r.ReadF64(), r.ReadF64(), r.ReadF64());
                var (radius, slack, level) = (r.ReadF32(), r.ReadF32(), r.ReadU8());
                geometry = ReadWindow(ref r, flags, new PushGeometry
                {
                    Shape = PushShape.Sphere, Flags = flags, AnchorX = x, AnchorY = y, AnchorZ = z, RadiusM = radius, SlackM = slack, Level = level,
                });
                break;
            }
            case PushShape.Region:
            {
                var dims = r.ReadU8();
                if (dims is not (2 or 3))
                {
                    throw WireFormatException.Malformed($"PUSH_GEOMETRY region of {dims} dimensions");
                }

                var vertices = new double[r.ReadVaruAtMost(MaxVertices, "PUSH_GEOMETRY vertex count") * dims];
                for (var i = 0; i < vertices.Length; i++)
                {
                    vertices[i] = r.ReadF64();
                }

                var held = r.ReadVaruAtMost(int.MaxValue, "PUSH_GEOMETRY held");
                var budget = r.ReadVaruAtMost(int.MaxValue, "PUSH_GEOMETRY near budget");
                geometry = ReadWindow(ref r, flags, new PushGeometry
                {
                    Shape = PushShape.Region, Flags = flags, Dims = dims, Vertices = vertices, Held = held, NearBudget = budget,
                });
                break;
            }
            default:
                throw WireFormatException.Malformed($"PUSH_GEOMETRY shape {shape} is unknown");
        }

        r.ExpectEnd("PUSH_GEOMETRY");
        return geometry;
    }

    private static PushGeometry ReadWindow(ref WireReader r, PushGeometryFlags flags, PushGeometry head)
    {
        var (x, y, z) = (r.ReadVari(), r.ReadVari(), r.ReadVari());
        var window = r.ReadU8();
        if (window > 64)
        {
            throw WireFormatException.Malformed($"PUSH_GEOMETRY window of {window} cells");
        }

        var rowCount = (flags & PushGeometryFlags.Deep) != 0 ? window * window : window;
        if (rowCount > MaxRows)
        {
            throw WireFormatException.Malformed($"PUSH_GEOMETRY window of {rowCount} rows");
        }

        var rowBytes = (window + 7) >> 3;
        var rows = new ulong[rowCount];
        for (var i = 0; i < rows.Length; i++)
        {
            var bytes = r.ReadBytes(rowBytes);
            ulong bits = 0;
            for (var b = 0; b < bytes.Length; b++)
            {
                bits |= (ulong)bytes[b] << (8 * b);
            }

            rows[i] = bits;
        }

        return new PushGeometry
        {
            Shape = head.Shape, Flags = head.Flags, AnchorX = head.AnchorX, AnchorY = head.AnchorY, AnchorZ = head.AnchorZ, RadiusM = head.RadiusM,
            SlackM = head.SlackM, Level = head.Level, Dims = head.Dims, Vertices = head.Vertices, Held = head.Held, NearBudget = head.NearBudget,
            WindowX = x, WindowY = y, WindowZ = z, Window = window, Rows = rows,
        };
    }
}
