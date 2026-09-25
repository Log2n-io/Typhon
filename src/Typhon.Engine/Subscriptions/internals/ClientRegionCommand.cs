using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Typhon.Protocol;

namespace Typhon.Engine.Internals;

/// <summary>One vertex of a client's region, in world metres, on the world's own axes (10 § 6: a flat region lies in the XY plane, z = 0).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct RegionVertex
{
    public double X;

    public double Y;

    /// <summary>The third axis; 0 in a flat grid.</summary>
    public double Z;
}

/// <summary>Storage for a region's vertices: the wire's ceiling, so the hull never needs a heap allocation.</summary>
[InlineArray(BuiltInCommands.MaxRegionVertices)]
internal struct RegionVertices
{
    private RegionVertex _first;
}

/// <summary>One bounding half-space of a region: the points <c>p</c> with <c>n · p ≤ d</c>, <c>n</c> of unit length and pointing out.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct RegionPlane
{
    public float Nx;
    public float Ny;
    public float Nz;
    public float D;
}

/// <summary>A region's half-spaces: at most 2n − 4 = 28 faces for a hull of sixteen points (10 § 6).</summary>
[InlineArray(MaxPlanes)]
internal struct RegionPlanes
{
    /// <summary>The most faces a hull of <see cref="BuiltInCommands.MaxRegionVertices"/> points has.</summary>
    public const int MaxPlanes = (2 * BuiltInCommands.MaxRegionVertices) - 4;

    private RegionPlane _first;
}

/// <summary>How a box of space lies against a region.</summary>
internal enum RegionOverlap : byte
{
    /// <summary>Wholly outside: one of the region's planes separates them.</summary>
    Outside = 0,

    /// <summary>Wholly inside: every corner is inside every plane.</summary>
    Inside = 1,

    /// <summary>Neither proven: the per-entity test decides.</summary>
    Straddling = 2,
}

/// <summary>
/// A client's region as the engine keeps it (10 § 6, L4): a convex set of half-spaces — a polygon's in a flat grid, a polyhedron's in a deep one, one
/// type — built from the convex hull of what the client sent and clamped to the profile's longest accepted extent.
/// </summary>
/// <remarks>
/// <para>
/// <b>The hull is taken on the server, and that is a correctness step rather than tidying.</b> The vertices arrive quantized over the world grid, and
/// quantization can push a convex shape concave; a concave region turned into half-spaces selects the wrong entities, silently. Taking the hull of what
/// arrived makes the half-space query well defined whatever the client sent, and costs a monotone chain, or an incremental 3D hull, over at most sixteen
/// points (03-wire-protocol § 12 W28).
/// </para>
/// <para>
/// <b>Vertices travel, planes are derived.</b> The ingress ring carries the hull's vertices (≤ 400 bytes); the tick side clamps them, then derives the
/// planes (<see cref="BuildPlanes"/>). A frustum's eight corners give six planes once coplanar faces are merged.
/// </para>
/// <para>
/// <b>1.5 stores it and classifies with it; it serves nothing yet.</b> The observer that turns a region into a known-set is Phase 2's step 2.6. A
/// malformed region is refused with <c>ACK REGION_INVALID</c> while the previous one stays in force — a client that sends one bad frame does not go blind.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct ClientRegionCommand
{
    /// <summary>Bytes of a region record before its vertices: the count, the altitude, the budget, the dimensions, and padding to eight bytes.</summary>
    internal const int HeaderBytes = 16;

    /// <summary>Bytes one vertex occupies.</summary>
    internal const int VertexBytes = 24;

    /// <summary>The largest a region record can be: a full sixteen-vertex hull.</summary>
    internal const int MaxRecordBytes = HeaderBytes + (BuiltInCommands.MaxRegionVertices * VertexBytes);

    /// <summary>Vertices of the hull: counter-clockwise in a flat region, in no particular order in a deep one.</summary>
    public int VertexCount;

    /// <summary>The viewpoint's altitude in metres, as the client reported it.</summary>
    public float AltitudeM;

    /// <summary>The client's own byte budget for this region, in KiB per second.</summary>
    public ushort BudgetKiBps;

    /// <summary>2 for a flat region (a polygon in the XY plane), 3 for a deep one.</summary>
    public byte Dims;

    /// <summary>Padding, so the header is exactly <see cref="HeaderBytes"/> and the vertices start eight-byte aligned.</summary>
    public byte Reserved;

    /// <summary>The planes <see cref="BuildPlanes"/> derived; zero until it runs. Not part of the ring record.</summary>
    public int PlaneCount;

    /// <summary>The hull's vertices; only the first <see cref="VertexCount"/> are meaningful.</summary>
    public RegionVertices Vertices;

    /// <summary>The region's half-spaces; only the first <see cref="PlaneCount"/> are meaningful.</summary>
    public RegionPlanes Planes;

    /// <summary>How many bytes this region occupies as a ring record — its header plus the hull it actually has.</summary>
    public readonly int RecordBytes => HeaderBytes + (VertexCount * VertexBytes);

    /// <summary>This region's ring record, exactly <see cref="RecordBytes"/> bytes: the header and the vertices.</summary>
    /// <param name="scratch">A buffer of at least <see cref="MaxRecordBytes"/>.</param>
    /// <returns>The record.</returns>
    public readonly ReadOnlySpan<byte> Write(Span<byte> scratch)
    {
        MemoryMarshal.Write(scratch, in VertexCount);
        MemoryMarshal.Write(scratch[4..], in AltitudeM);
        MemoryMarshal.Write(scratch[8..], in BudgetKiBps);
        scratch[10] = Dims;
        scratch[11] = 0;
        MemoryMarshal.Write(scratch[12..], 0);
        MemoryMarshal.AsBytes(((ReadOnlySpan<RegionVertex>)Vertices)[..VertexCount]).CopyTo(scratch[HeaderBytes..]);
        return scratch[..RecordBytes];
    }

    /// <summary>Reads a region back out of a ring record. Its planes are not in it: <see cref="BuildPlanes"/> derives them.</summary>
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
        var dims = record[10];
        if (dims is not (2 or 3) || count < BuiltInCommands.MinVertices(dims) || count > BuiltInCommands.MaxRegionVertices
            || record.Length < HeaderBytes + (count * VertexBytes))
        {
            return false;
        }

        region.VertexCount = count;
        region.Dims = dims;
        region.AltitudeM = MemoryMarshal.Read<float>(record[4..8]);
        region.BudgetKiBps = MemoryMarshal.Read<ushort>(record[8..10]);
        MemoryMarshal.Cast<byte, RegionVertex>(record.Slice(HeaderBytes, count * VertexBytes)).CopyTo(region.Vertices[..count]);
        return true;
    }

    /// <summary>
    /// Shrinks the hull about its centroid until no side of its bounding box exceeds <paramref name="maxEdgeM"/> — on every axis the region has.
    /// </summary>
    /// <param name="maxEdgeM">The profile's ceiling in metres; zero or less leaves the hull untouched.</param>
    /// <remarks>
    /// <b>The extent is what is clamped, not each edge in isolation.</b> A profile's <c>maxEdgeM</c> bounds how much world one client may ask to see, and a
    /// per-edge clamp would not do that: sixteen short edges can enclose any area at all. Scaling about the centroid keeps the region's shape and its
    /// centre, which is what the client asked for; refusing it outright would blind a client for looking too far.
    /// </remarks>
    public void ClampToMaxEdge(double maxEdgeM)
    {
        if (maxEdgeM <= 0 || VertexCount == 0)
        {
            return;
        }

        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
        double minZ = double.MaxValue, maxZ = double.MinValue;
        double sumX = 0, sumY = 0, sumZ = 0;
        for (var i = 0; i < VertexCount; i++)
        {
            ref readonly var v = ref Vertices[i];
            minX = Math.Min(minX, v.X);
            maxX = Math.Max(maxX, v.X);
            minY = Math.Min(minY, v.Y);
            maxY = Math.Max(maxY, v.Y);
            minZ = Math.Min(minZ, v.Z);
            maxZ = Math.Max(maxZ, v.Z);
            sumX += v.X;
            sumY += v.Y;
            sumZ += v.Z;
        }

        var extent = Math.Max(Math.Max(maxX - minX, maxY - minY), maxZ - minZ);
        if (extent <= maxEdgeM)
        {
            return;
        }

        var scale = maxEdgeM / extent;
        var centreX = sumX / VertexCount;
        var centreY = sumY / VertexCount;
        var centreZ = sumZ / VertexCount;
        for (var i = 0; i < VertexCount; i++)
        {
            ref var v = ref Vertices[i];
            v.X = centreX + ((v.X - centreX) * scale);
            v.Y = centreY + ((v.Y - centreY) * scale);
            v.Z = centreZ + ((v.Z - centreZ) * scale);
        }
    }

    /// <summary>Twice the signed area of a flat hull. Zero means the vertices are collinear, which is a region that selects nothing.</summary>
    public readonly double DoubledArea()
    {
        double total = 0;
        for (var i = 0; i < VertexCount; i++)
        {
            ref readonly var a = ref Vertices[i];
            ref readonly var b = ref Vertices[(i + 1) % VertexCount];
            total += (a.X * b.Y) - (b.X * a.Y);
        }

        return total;
    }

    /// <summary>
    /// Derives the region's half-spaces from its hull: the outward edge normals of a flat polygon (their z is 0, so the region is its footprint and a flat
    /// grid's z = 0 is inside every one), or a deep hull's faces with coplanar ones merged.
    /// </summary>
    /// <returns><see langword="false"/> when the vertices no longer make a hull — which the ingress check rules out for a region it accepted.</returns>
    public bool BuildPlanes()
    {
        PlaneCount = 0;
        if (Dims == 3)
        {
            return ConvexHull.Planes3(((ReadOnlySpan<RegionVertex>)Vertices)[..VertexCount], ref this);
        }

        for (var i = 0; i < VertexCount; i++)
        {
            ref readonly var a = ref Vertices[i];
            ref readonly var b = ref Vertices[(i + 1) % VertexCount];

            // Counter-clockwise, so the inside is on the left of a → b and the outward normal is the edge turned clockwise.
            var nx = b.Y - a.Y;
            var ny = a.X - b.X;
            var length = Math.Sqrt((nx * nx) + (ny * ny));
            if (length == 0)
            {
                continue;
            }

            nx /= length;
            ny /= length;
            Planes[PlaneCount++] = new RegionPlane { Nx = (float)nx, Ny = (float)ny, Nz = 0f, D = (float)((nx * a.X) + (ny * a.Y)) };
        }

        return PlaneCount >= 3;
    }

    /// <summary>Whether a point lies in the region: inside every half-space.</summary>
    public readonly bool Contains(double x, double y, double z)
    {
        for (var i = 0; i < PlaneCount; i++)
        {
            ref readonly var p = ref Planes[i];
            if ((p.Nx * x) + (p.Ny * y) + (p.Nz * z) > p.D)
            {
                return false;
            }
        }

        return PlaneCount > 0;
    }

    /// <summary>
    /// How an axis-aligned box lies against the region (10 § 6): per plane, the corner farthest along −n (the n-vertex) decides "outside", the corner
    /// farthest along n (the p-vertex) decides "inside" — 2k dot products, whether the box has four corners or eight. A flat grid's cell passes z = 0 for
    /// both bounds.
    /// </summary>
    public readonly RegionOverlap Classify(double minX, double minY, double minZ, double maxX, double maxY, double maxZ)
    {
        if (PlaneCount == 0)
        {
            return RegionOverlap.Outside;
        }

        var inside = true;
        for (var i = 0; i < PlaneCount; i++)
        {
            ref readonly var p = ref Planes[i];
            double nx = p.Nx, ny = p.Ny, nz = p.Nz;
            var near = (nx * (nx >= 0 ? minX : maxX)) + (ny * (ny >= 0 ? minY : maxY)) + (nz * (nz >= 0 ? minZ : maxZ));
            if (near > p.D)
            {
                return RegionOverlap.Outside;
            }

            var far = (nx * (nx >= 0 ? maxX : minX)) + (ny * (ny >= 0 ? maxY : minY)) + (nz * (nz >= 0 ? maxZ : minZ));
            inside &= far <= p.D;
        }

        return inside ? RegionOverlap.Inside : RegionOverlap.Straddling;
    }
}

/// <summary>Why a <c>ClientRegion</c> was not taken.</summary>
internal enum ClientRegionOutcome
{
    /// <summary>The hull is convex and encloses area (flat) or volume (deep). It replaces the session's region.</summary>
    Accepted = 0,

    /// <summary>The hull has too few points, no area, or no volume. The previous region is kept and the client is told (<c>ACK REGION_INVALID</c>).</summary>
    Invalid = 1,
}

/// <summary>
/// Turns the vertices a <c>ClientRegion</c> command carried into the convex hull the engine keeps: a polygon in a flat grid, a polyhedron in a deep one.
/// </summary>
/// <remarks>
/// Both over at most sixteen points with every working set on the stack, so neither allocates. Flat: Andrew's monotone chain. Deep: an incremental hull —
/// a tetrahedron from four extreme points, then each point that sees a face replaces the faces it sees with a fan from their horizon — whose faces are
/// at most 2n − 4 = 28. Degenerate input (all points on a line in 2D, on a plane in 3D) has no hull and is refused.
/// </remarks>
internal static class ConvexHull
{
    /// <summary>Builds the hull of <paramref name="points"/> into <paramref name="region"/>, flat or deep by <paramref name="dims"/>.</summary>
    /// <param name="points">The vertices, as the wire carried them: at most <see cref="BuiltInCommands.MaxRegionVertices"/>.</param>
    /// <param name="dims">2 for a flat grid's region (the Z of every point ignored), 3 for a deep one's.</param>
    /// <param name="altitudeM">The viewpoint's altitude.</param>
    /// <param name="budgetKiBps">The client's byte budget.</param>
    /// <param name="region">The hull.</param>
    /// <returns>Whether the hull is usable.</returns>
    public static ClientRegionOutcome Build(scoped ReadOnlySpan<RegionVertex> points, int dims, float altitudeM, ushort budgetKiBps,
        out ClientRegionCommand region)
    {
        region = default;
        region.AltitudeM = altitudeM;
        region.BudgetKiBps = budgetKiBps;
        region.Dims = (byte)dims;
        if (points.Length < BuiltInCommands.MinVertices(dims) || points.Length > BuiltInCommands.MaxRegionVertices)
        {
            return ClientRegionOutcome.Invalid;
        }

        return dims == 3 ? Build3(points, ref region) : Build2(points, ref region);
    }

    private static ClientRegionOutcome Build2(scoped ReadOnlySpan<RegionVertex> points, ref ClientRegionCommand region)
    {
        Span<RegionVertex> sorted = stackalloc RegionVertex[BuiltInCommands.MaxRegionVertices];
        points.CopyTo(sorted);
        sorted = sorted[..points.Length];
        for (var i = 0; i < sorted.Length; i++)
        {
            sorted[i].Z = 0;
        }

        InsertionSort(sorted);

        // Duplicates would make the cross product zero and let a degenerate point into the hull; dropping them here is cheaper than special-casing it below.
        var unique = 0;
        for (var i = 0; i < sorted.Length; i++)
        {
            if (unique == 0 || sorted[i].X != sorted[unique - 1].X || sorted[i].Y != sorted[unique - 1].Y)
            {
                sorted[unique++] = sorted[i];
            }
        }

        sorted = sorted[..unique];
        if (unique < 3)
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
        if (k < 3)
        {
            return ClientRegionOutcome.Invalid;
        }

        region.VertexCount = k;
        hull[..k].CopyTo(region.Vertices[..k]);
        return region.DoubledArea() == 0 ? ClientRegionOutcome.Invalid : ClientRegionOutcome.Accepted;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double Cross(in RegionVertex o, in RegionVertex a, in RegionVertex b)
        => ((a.X - o.X) * (b.Y - o.Y)) - ((a.Y - o.Y) * (b.X - o.X));

    /// <summary>Sorts by X then Y. Insertion sort, because sixteen is the wire's ceiling and a comparison-delegate sort would allocate.</summary>
    private static void InsertionSort(Span<RegionVertex> points)
    {
        for (var i = 1; i < points.Length; i++)
        {
            var value = points[i];
            var j = i - 1;
            while (j >= 0 && (points[j].X > value.X || (points[j].X == value.X && points[j].Y > value.Y)))
            {
                points[j + 1] = points[j];
                j--;
            }

            points[j + 1] = value;
        }
    }

    // ── The deep hull ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A triangle of the hull under construction: three point indices, counter-clockwise seen from outside, and its plane.</summary>
    private struct Face
    {
        public byte A;
        public byte B;
        public byte C;
        public bool Alive;
        public double Nx;
        public double Ny;
        public double Nz;
        public double D;
    }

    // Faces ever created: 4, then at most 2 × horizon per point, each horizon at most 2n — far fewer in practice; the working set is compacted as it goes.
    private const int MaxFaces = 128;

    private static ClientRegionOutcome Build3(scoped ReadOnlySpan<RegionVertex> points, ref ClientRegionCommand region)
    {
        Span<RegionVertex> unique = stackalloc RegionVertex[BuiltInCommands.MaxRegionVertices];
        var n = Dedupe(points, unique);
        Span<Face> faces = stackalloc Face[MaxFaces];
        var count = Hull3(unique[..n], faces, out var epsilon);
        if (count == 0)
        {
            return ClientRegionOutcome.Invalid;
        }

        // The hull's vertices: every point some live face names.
        Span<bool> used = stackalloc bool[BuiltInCommands.MaxRegionVertices];
        for (var f = 0; f < count; f++)
        {
            ref readonly var face = ref faces[f];
            if (face.Alive)
            {
                used[face.A] = used[face.B] = used[face.C] = true;
            }
        }

        var k = 0;
        for (var i = 0; i < n; i++)
        {
            if (used[i])
            {
                region.Vertices[k++] = unique[i];
            }
        }

        region.VertexCount = k;
        return k >= 4 ? ClientRegionOutcome.Accepted : ClientRegionOutcome.Invalid;
    }

    /// <summary>The planes of the hull of <paramref name="points"/>, coplanar faces merged, into <paramref name="region"/>.</summary>
    internal static bool Planes3(scoped ReadOnlySpan<RegionVertex> points, ref ClientRegionCommand region)
    {
        Span<RegionVertex> unique = stackalloc RegionVertex[BuiltInCommands.MaxRegionVertices];
        var n = Dedupe(points, unique);
        Span<Face> faces = stackalloc Face[MaxFaces];
        var count = Hull3(unique[..n], faces, out var epsilon);
        region.PlaneCount = 0;
        for (var f = 0; f < count; f++)
        {
            ref readonly var face = ref faces[f];
            if (!face.Alive)
            {
                continue;
            }

            // Two triangles of one flat side share a plane: the same unit normal, the same offset, to the hull's own precision.
            var merged = false;
            for (var p = 0; p < region.PlaneCount && !merged; p++)
            {
                ref readonly var plane = ref region.Planes[p];
                merged = Math.Abs(plane.Nx - face.Nx) < 1e-6 && Math.Abs(plane.Ny - face.Ny) < 1e-6 && Math.Abs(plane.Nz - face.Nz) < 1e-6
                         && Math.Abs(plane.D - face.D) <= Math.Max(epsilon, Math.Abs(face.D) * 1e-6);
            }

            if (!merged && region.PlaneCount < RegionPlanes.MaxPlanes)
            {
                region.Planes[region.PlaneCount++] = new RegionPlane { Nx = (float)face.Nx, Ny = (float)face.Ny, Nz = (float)face.Nz, D = (float)face.D };
            }
        }

        return region.PlaneCount >= 4;
    }

    private static int Dedupe(scoped ReadOnlySpan<RegionVertex> points, Span<RegionVertex> into)
    {
        var n = 0;
        for (var i = 0; i < points.Length && n < into.Length; i++)
        {
            var duplicate = false;
            for (var j = 0; j < n && !duplicate; j++)
            {
                duplicate = into[j].X == points[i].X && into[j].Y == points[i].Y && into[j].Z == points[i].Z;
            }

            if (!duplicate)
            {
                into[n++] = points[i];
            }
        }

        return n;
    }

    /// <summary>
    /// The incremental hull: the number of faces written (live and dead), or 0 when the points span no volume. <paramref name="epsilon"/> is the distance
    /// below which a point counts as on a plane, scaled to the points' extent.
    /// </summary>
    private static int Hull3(scoped ReadOnlySpan<RegionVertex> p, Span<Face> faces, out double epsilon)
    {
        epsilon = 0;
        var n = p.Length;
        if (n < 4)
        {
            return 0;
        }

        double extent = 0;
        for (var i = 1; i < n; i++)
        {
            extent = Math.Max(extent, Math.Max(Math.Abs(p[i].X - p[0].X), Math.Max(Math.Abs(p[i].Y - p[0].Y), Math.Abs(p[i].Z - p[0].Z))));
        }

        epsilon = Math.Max(extent, 1.0) * 1e-9;

        // Four extreme points: the farthest from p0, the farthest from that line, the farthest from that plane.
        var i1 = 0;
        var best = 0.0;
        for (var i = 1; i < n; i++)
        {
            var d = Dist2(p[0], p[i]);
            if (d > best)
            {
                best = d;
                i1 = i;
            }
        }

        if (Math.Sqrt(best) <= epsilon)
        {
            return 0;
        }

        var i2 = 0;
        best = 0;
        for (var i = 1; i < n; i++)
        {
            var c = CrossLength(p[0], p[i1], p[i]);
            if (c > best)
            {
                best = c;
                i2 = i;
            }
        }

        if (best / Math.Sqrt(Dist2(p[0], p[i1])) <= epsilon)
        {
            return 0;
        }

        var i3 = 0;
        best = 0;
        var basePlane = PlaneOf(p[0], p[i1], p[i2]);
        for (var i = 1; i < n; i++)
        {
            var d = Math.Abs(Distance(basePlane, p[i]));
            if (d > best)
            {
                best = d;
                i3 = i;
            }
        }

        if (best <= epsilon)
        {
            return 0;
        }

        // The tetrahedron, every face oriented away from its centroid.
        var cx = (p[0].X + p[i1].X + p[i2].X + p[i3].X) / 4;
        var cy = (p[0].Y + p[i1].Y + p[i2].Y + p[i3].Y) / 4;
        var cz = (p[0].Z + p[i1].Z + p[i2].Z + p[i3].Z) / 4;
        var interior = new RegionVertex { X = cx, Y = cy, Z = cz };
        var count = 0;
        AddFace(faces, ref count, p, 0, i1, i2, interior);
        AddFace(faces, ref count, p, 0, i1, i3, interior);
        AddFace(faces, ref count, p, 0, i2, i3, interior);
        AddFace(faces, ref count, p, i1, i2, i3, interior);

        Span<int> visible = stackalloc int[MaxFaces];
        Span<(byte A, byte B)> horizon = stackalloc (byte, byte)[MaxFaces];
        for (var i = 0; i < n; i++)
        {
            if (i == 0 || i == i1 || i == i2 || i == i3)
            {
                continue;
            }

            var seen = 0;
            for (var f = 0; f < count; f++)
            {
                if (faces[f].Alive && Distance(faces[f], p[i]) > epsilon)
                {
                    visible[seen++] = f;
                }
            }

            if (seen == 0)
            {
                continue;
            }

            // The horizon: the edges of the visible faces that no other visible face shares.
            var edges = 0;
            for (var v = 0; v < seen; v++)
            {
                ref readonly var face = ref faces[visible[v]];
                AddHorizonEdge(faces, visible[..seen], face.A, face.B, horizon, ref edges);
                AddHorizonEdge(faces, visible[..seen], face.B, face.C, horizon, ref edges);
                AddHorizonEdge(faces, visible[..seen], face.C, face.A, horizon, ref edges);
            }

            for (var v = 0; v < seen; v++)
            {
                faces[visible[v]].Alive = false;
            }

            count = Compact(faces, count);
            if (count + edges > MaxFaces)
            {
                return 0;
            }

            for (var e = 0; e < edges; e++)
            {
                AddFace(faces, ref count, p, horizon[e].A, horizon[e].B, i, interior);
            }
        }

        return count;
    }

    // An edge a → b of a visible face is on the horizon when no other visible face has it (as b → a, its neighbour's winding).
    private static void AddHorizonEdge(Span<Face> faces, ReadOnlySpan<int> visible, byte a, byte b, Span<(byte A, byte B)> horizon, ref int edges)
    {
        foreach (var f in visible)
        {
            ref readonly var face = ref faces[f];
            if ((face.A == b && face.B == a) || (face.B == b && face.C == a) || (face.C == b && face.A == a))
            {
                return;
            }
        }

        horizon[edges++] = (a, b);
    }

    private static int Compact(Span<Face> faces, int count)
    {
        var live = 0;
        for (var f = 0; f < count; f++)
        {
            if (faces[f].Alive)
            {
                faces[live++] = faces[f];
            }
        }

        return live;
    }

    // A face through three points, wound so that its normal points away from the interior point.
    private static void AddFace(Span<Face> faces, ref int count, ReadOnlySpan<RegionVertex> p, int a, int b, int c, in RegionVertex interior)
    {
        var plane = PlaneOf(p[a], p[b], p[c]);
        if (Distance(plane, interior) > 0)
        {
            (b, c) = (c, b);
            plane = PlaneOf(p[a], p[b], p[c]);
        }

        faces[count++] = new Face { A = (byte)a, B = (byte)b, C = (byte)c, Alive = true, Nx = plane.Nx, Ny = plane.Ny, Nz = plane.Nz, D = plane.D };
    }

    private static Face PlaneOf(in RegionVertex a, in RegionVertex b, in RegionVertex c)
    {
        double ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z;
        double vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;
        var nx = (uy * vz) - (uz * vy);
        var ny = (uz * vx) - (ux * vz);
        var nz = (ux * vy) - (uy * vx);
        var length = Math.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
        if (length > 0)
        {
            nx /= length;
            ny /= length;
            nz /= length;
        }

        return new Face { Nx = nx, Ny = ny, Nz = nz, D = (nx * a.X) + (ny * a.Y) + (nz * a.Z) };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double Distance(in Face plane, in RegionVertex p) => (plane.Nx * p.X) + (plane.Ny * p.Y) + (plane.Nz * p.Z) - plane.D;

    private static double Dist2(in RegionVertex a, in RegionVertex b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z;
        return (dx * dx) + (dy * dy) + (dz * dz);
    }

    // |(b − a) × (c − a)|: twice the triangle's area, which over |b − a| is c's distance from the line.
    private static double CrossLength(in RegionVertex a, in RegionVertex b, in RegionVertex c)
    {
        double ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z;
        double vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;
        var x = (uy * vz) - (uz * vy);
        var y = (uz * vx) - (ux * vz);
        var z = (ux * vy) - (uy * vx);
        return Math.Sqrt((x * x) + (y * y) + (z * z));
    }
}
