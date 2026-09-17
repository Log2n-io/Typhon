using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Typhon.Protocol;

namespace Typhon.Engine.Internals;

/// <summary>One vertex of a client's ground footprint, in world metres.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct RegionVertex
{
    /// <summary>The first world axis.</summary>
    public double X;

    /// <summary>The second world axis.</summary>
    public double Z;
}

/// <summary>Storage for a footprint's vertices: the wire's ceiling, so the hull never needs a heap allocation.</summary>
[InlineArray(BuiltInCommands.MaxRegionVertices)]
internal struct RegionVertices
{
    private RegionVertex _first;
}

/// <summary>
/// A client's viewpoint footprint, as the engine keeps it: the convex hull of what the client sent, clamped to the profile's longest accepted edge.
/// </summary>
/// <remarks>
/// <para>
/// <b>The hull is taken on the server, and that is a correctness step rather than tidying.</b> The vertices arrive quantized over the world grid, and
/// quantization can push a convex polygon concave; a concave footprint turned into half-planes selects the wrong entities, silently. Taking the hull of what
/// arrived makes the half-plane query well defined whatever the client sent, and costs a monotone chain over at most sixteen points
/// (03-wire-protocol § 12 W28).
/// </para>
/// <para>
/// <b>Phase 1 stores it and nothing more.</b> The observer that turns it into a frustum query is Phase 2; what this slice owes is that a well-formed region
/// reaches the session and a malformed one is refused with <c>ACK REGION_INVALID</c> while the previous region stays in force — a client that sends one bad
/// frame does not go blind.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct ClientRegionCommand
{
    /// <summary>
    /// Bytes of a region record before its vertices: the count, the altitude, the budget and the padding that keeps the vertices eight-byte aligned.
    /// </summary>
    /// <remarks>
    /// Sixteen rather than the twelve the fields need, and the padding is explicit rather than left to the layout: a vertex holds doubles, so the runtime
    /// would insert the same four bytes anyway — and a record whose declared header disagreed with the real one would put every vertex four bytes off.
    /// </remarks>
    internal const int HeaderBytes = 16;

    /// <summary>Bytes one vertex occupies.</summary>
    internal const int VertexBytes = 16;

    /// <summary>The largest a region record can be: a full sixteen-vertex hull.</summary>
    internal const int MaxRecordBytes = HeaderBytes + (BuiltInCommands.MaxRegionVertices * VertexBytes);

    /// <summary>Vertices of the hull, in counter-clockwise order.</summary>
    public int VertexCount;

    /// <summary>The viewpoint's altitude in metres, as the client reported it.</summary>
    public float AltitudeM;

    /// <summary>The client's own byte budget for this region, in KiB per second.</summary>
    public ushort BudgetKiBps;

    /// <summary>Padding, so the vertices start eight-byte aligned and the record's layout is the same in the ring as in memory.</summary>
    public ushort Reserved;

    /// <summary>More of the same padding. Together with <see cref="Reserved"/> it makes the header exactly <see cref="HeaderBytes"/>.</summary>
    public int Reserved2;

    /// <summary>The hull's vertices; only the first <see cref="VertexCount"/> are meaningful.</summary>
    public RegionVertices Vertices;

    /// <summary>How many bytes this region occupies as a ring record — its header plus the hull it actually has.</summary>
    public readonly int RecordBytes => HeaderBytes + (VertexCount * VertexBytes);

    /// <summary>This region's bytes, exactly <see cref="RecordBytes"/> of them.</summary>
    /// <param name="scratch">A buffer of at least <see cref="MaxRecordBytes"/>.</param>
    /// <returns>The record.</returns>
    public readonly ReadOnlySpan<byte> Write(Span<byte> scratch)
    {
        var copy = this;
        MemoryMarshal.Write(scratch, in copy);
        return scratch[..RecordBytes];
    }

    /// <summary>Reads a region back out of a ring record.</summary>
    /// <param name="record">The record, as <see cref="Write"/> produced it.</param>
    /// <param name="region">The region.</param>
    /// <returns><see langword="false"/> when the record is not a well-formed region — a truncated or hostile ring payload.</returns>
    public static bool TryRead(ReadOnlySpan<byte> record, out ClientRegionCommand region)
    {
        region = default;
        if (record.Length < HeaderBytes)
        {
            return false;
        }

        var count = MemoryMarshal.Read<int>(record);
        if (count is < BuiltInCommands.MinRegionVertices or > BuiltInCommands.MaxRegionVertices || record.Length < HeaderBytes + (count * VertexBytes))
        {
            return false;
        }

        // Copied field by field rather than as one struct read: the record carries only the vertices it has, so a whole-struct read would run off its end.
        region.VertexCount = count;
        region.AltitudeM = MemoryMarshal.Read<float>(record[4..8]);
        region.BudgetKiBps = MemoryMarshal.Read<ushort>(record[8..10]);
        MemoryMarshal.Cast<byte, RegionVertex>(record.Slice(HeaderBytes, count * VertexBytes)).CopyTo(region.Vertices[..count]);
        return true;
    }

    /// <summary>
    /// Shrinks the hull about its centroid until neither side of its bounding box exceeds <paramref name="maxEdgeM"/>.
    /// </summary>
    /// <param name="maxEdgeM">The profile's ceiling in metres; zero or less leaves the hull untouched.</param>
    /// <remarks>
    /// <b>The extent is what is clamped, not each edge in isolation.</b> A profile's <c>maxEdgeM</c> bounds how much world one client may ask to see, and a
    /// per-edge clamp would not do that: sixteen short edges can enclose any area at all. Scaling about the centroid keeps the footprint's shape and its
    /// centre, which is what the client asked for; refusing it outright would blind a client for looking too far.
    /// </remarks>
    public void ClampToMaxEdge(double maxEdgeM)
    {
        if (maxEdgeM <= 0 || VertexCount == 0)
        {
            return;
        }

        var minX = double.MaxValue;
        var maxX = double.MinValue;
        var minZ = double.MaxValue;
        var maxZ = double.MinValue;
        double sumX = 0;
        double sumZ = 0;
        for (var i = 0; i < VertexCount; i++)
        {
            ref readonly var v = ref Vertices[i];
            minX = Math.Min(minX, v.X);
            maxX = Math.Max(maxX, v.X);
            minZ = Math.Min(minZ, v.Z);
            maxZ = Math.Max(maxZ, v.Z);
            sumX += v.X;
            sumZ += v.Z;
        }

        var extent = Math.Max(maxX - minX, maxZ - minZ);
        if (extent <= maxEdgeM)
        {
            return;
        }

        var scale = maxEdgeM / extent;
        var centreX = sumX / VertexCount;
        var centreZ = sumZ / VertexCount;
        for (var i = 0; i < VertexCount; i++)
        {
            ref var v = ref Vertices[i];
            v.X = centreX + ((v.X - centreX) * scale);
            v.Z = centreZ + ((v.Z - centreZ) * scale);
        }
    }

    /// <summary>Twice the signed area of the hull. Zero means the vertices are collinear, which is a region that selects nothing.</summary>
    public readonly double DoubledArea()
    {
        double total = 0;
        for (var i = 0; i < VertexCount; i++)
        {
            ref readonly var a = ref Vertices[i];
            ref readonly var b = ref Vertices[(i + 1) % VertexCount];
            total += (a.X * b.Z) - (b.X * a.Z);
        }

        return total;
    }
}

/// <summary>Why a <c>ClientRegion</c> was not taken.</summary>
internal enum ClientRegionOutcome
{
    /// <summary>The hull is convex, has at least three points and encloses area. It replaces the session's region.</summary>
    Accepted = 0,

    /// <summary>The hull has fewer than three points, or no area. The previous region is kept and the client is told (<c>ACK REGION_INVALID</c>).</summary>
    Invalid = 1,
}

/// <summary>
/// Turns the vertices a <c>ClientRegion</c> command carried into the convex hull the engine keeps.
/// </summary>
/// <remarks>
/// Andrew's monotone chain, over at most sixteen points, with both the sort and the working set on the stack. No allocation, and a bound small enough that the
/// O(k log k) sort is a handful of comparisons — the wire caps the polygon at sixteen vertices precisely so this stays a fixed, tiny cost per region
/// (03-wire-protocol § 12 W28).
/// </remarks>
internal static class ConvexHull
{
    /// <summary>
    /// Builds the convex hull of <paramref name="points"/> into <paramref name="region"/>.
    /// </summary>
    /// <param name="points">The vertices, as the wire carried them: at most <see cref="BuiltInCommands.MaxRegionVertices"/>.</param>
    /// <param name="altitudeM">The viewpoint's altitude.</param>
    /// <param name="budgetKiBps">The client's byte budget.</param>
    /// <param name="region">The hull.</param>
    /// <returns>Whether the hull is usable.</returns>
    public static ClientRegionOutcome Build(scoped ReadOnlySpan<RegionVertex> points, float altitudeM, ushort budgetKiBps, out ClientRegionCommand region)
    {
        region = default;
        region.AltitudeM = altitudeM;
        region.BudgetKiBps = budgetKiBps;

        if (points.Length < BuiltInCommands.MinRegionVertices || points.Length > BuiltInCommands.MaxRegionVertices)
        {
            return ClientRegionOutcome.Invalid;
        }

        Span<RegionVertex> sorted = stackalloc RegionVertex[BuiltInCommands.MaxRegionVertices];
        points.CopyTo(sorted);
        sorted = sorted[..points.Length];
        InsertionSort(sorted);

        // Duplicates would make the cross product zero and let a degenerate point into the hull; dropping them here is cheaper than special-casing it below.
        var unique = 0;
        for (var i = 0; i < sorted.Length; i++)
        {
            if (unique == 0 || sorted[i].X != sorted[unique - 1].X || sorted[i].Z != sorted[unique - 1].Z)
            {
                sorted[unique++] = sorted[i];
            }
        }

        sorted = sorted[..unique];
        if (unique < BuiltInCommands.MinRegionVertices)
        {
            return ClientRegionOutcome.Invalid;
        }

        Span<RegionVertex> hull = stackalloc RegionVertex[BuiltInCommands.MaxRegionVertices * 2];
        var k = 0;

        for (var i = 0; i < sorted.Length; i++)
        {
            while (k >= 2 && Cross(hull[k - 2], hull[k - 1], sorted[i]) <= 0)
            {
                k--;
            }

            hull[k++] = sorted[i];
        }

        var lower = k + 1;
        for (var i = sorted.Length - 2; i >= 0; i--)
        {
            while (k >= lower && Cross(hull[k - 2], hull[k - 1], sorted[i]) <= 0)
            {
                k--;
            }

            hull[k++] = sorted[i];
        }

        // The last point of the upper chain is the first of the lower one.
        k--;
        if (k < BuiltInCommands.MinRegionVertices)
        {
            return ClientRegionOutcome.Invalid;
        }

        region.VertexCount = k;
        hull[..k].CopyTo(region.Vertices[..k]);
        return region.DoubledArea() == 0 ? ClientRegionOutcome.Invalid : ClientRegionOutcome.Accepted;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double Cross(in RegionVertex o, in RegionVertex a, in RegionVertex b)
        => ((a.X - o.X) * (b.Z - o.Z)) - ((a.Z - o.Z) * (b.X - o.X));

    /// <summary>Sorts by X then Z. Insertion sort, because sixteen is the wire's ceiling and a comparison-delegate sort would allocate.</summary>
    private static void InsertionSort(Span<RegionVertex> points)
    {
        for (var i = 1; i < points.Length; i++)
        {
            var value = points[i];
            var j = i - 1;
            while (j >= 0 && (points[j].X > value.X || (points[j].X == value.X && points[j].Z > value.Z)))
            {
                points[j + 1] = points[j];
                j--;
            }

            points[j + 1] = value;
        }
    }
}
