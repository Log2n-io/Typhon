using System;
using System.Runtime.CompilerServices;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Internals;

/// <summary>
/// Geometry helper methods for spatial index operations. All methods are aggressively inlined for hot-path performance.
/// v1 ships with scalar implementations; SOA layout enables future drop-in SIMD replacements.
/// </summary>
internal static class SpatialGeometry
{
    // ── AABB2F ──────────────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Overlaps(AABB2F a, AABB2F b) => a.MinX <= b.MaxX && a.MaxX >= b.MinX && a.MinY <= b.MaxY && a.MaxY >= b.MinY;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Contains(AABB2F outer, AABB2F inner) => 
        outer.MinX <= inner.MinX && outer.MaxX >= inner.MaxX && outer.MinY <= inner.MinY && outer.MaxY >= inner.MaxY;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static AABB2F Enlarge(AABB2F box, float margin) => new()
    {
        MinX = box.MinX - margin,
        MinY = box.MinY - margin,
        MaxX = box.MaxX + margin,
        MaxY = box.MaxY + margin,
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static AABB2F Union(AABB2F a, AABB2F b) => new()
    {
        MinX = Math.Min(a.MinX, b.MinX),
        MinY = Math.Min(a.MinY, b.MinY),
        MaxX = Math.Max(a.MaxX, b.MaxX),
        MaxY = Math.Max(a.MaxY, b.MaxY),
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Area(AABB2F box) => (box.MaxX - box.MinX) * (box.MaxY - box.MinY);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static AABB2F Enclosing(BSphere2F s) => new()
    {
        MinX = s.CenterX - s.Radius,
        MinY = s.CenterY - s.Radius,
        MaxX = s.CenterX + s.Radius,
        MaxY = s.CenterY + s.Radius,
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsDegenerate(AABB2F box) => 
        float.IsNaN(box.MinX) || float.IsNaN(box.MinY) || float.IsNaN(box.MaxX) || float.IsNaN(box.MaxY) || box.MinX > box.MaxX || box.MinY > box.MaxY;

    // ── AABB3F ──────────────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Overlaps(AABB3F a, AABB3F b) => 
        a.MinX <= b.MaxX && a.MaxX >= b.MinX && a.MinY <= b.MaxY && a.MaxY >= b.MinY && a.MinZ <= b.MaxZ && a.MaxZ >= b.MinZ;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Contains(AABB3F outer, AABB3F inner) =>
        outer.MinX <= inner.MinX && outer.MaxX >= inner.MaxX && outer.MinY <= inner.MinY && outer.MaxY >= inner.MaxY && outer.MinZ <= inner.MinZ && outer.MaxZ >= inner.MaxZ;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static AABB3F Enlarge(AABB3F box, float margin) => new()
    {
        MinX = box.MinX - margin,
        MinY = box.MinY - margin,
        MinZ = box.MinZ - margin,
        MaxX = box.MaxX + margin,
        MaxY = box.MaxY + margin,
        MaxZ = box.MaxZ + margin,
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static AABB3F Union(AABB3F a, AABB3F b) => new()
    {
        MinX = Math.Min(a.MinX, b.MinX),
        MinY = Math.Min(a.MinY, b.MinY),
        MinZ = Math.Min(a.MinZ, b.MinZ),
        MaxX = Math.Max(a.MaxX, b.MaxX),
        MaxY = Math.Max(a.MaxY, b.MaxY),
        MaxZ = Math.Max(a.MaxZ, b.MaxZ),
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Volume(AABB3F box) =>
        (box.MaxX - box.MinX) * (box.MaxY - box.MinY) * (box.MaxZ - box.MinZ);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static AABB3F Enclosing(BSphere3F s) => new()
    {
        MinX = s.CenterX - s.Radius,
        MinY = s.CenterY - s.Radius,
        MinZ = s.CenterZ - s.Radius,
        MaxX = s.CenterX + s.Radius,
        MaxY = s.CenterY + s.Radius,
        MaxZ = s.CenterZ + s.Radius,
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsDegenerate(AABB3F box) =>
        float.IsNaN(box.MinX) || float.IsNaN(box.MinY) || float.IsNaN(box.MinZ) ||
        float.IsNaN(box.MaxX) || float.IsNaN(box.MaxY) || float.IsNaN(box.MaxZ) ||
        box.MinX > box.MaxX || box.MinY > box.MaxY || box.MinZ > box.MaxZ;

    // ── AABB2D ──────────────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Overlaps(AABB2D a, AABB2D b) =>
        a.MinX <= b.MaxX && a.MaxX >= b.MinX && a.MinY <= b.MaxY && a.MaxY >= b.MinY;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Contains(AABB2D outer, AABB2D inner) =>
        outer.MinX <= inner.MinX && outer.MaxX >= inner.MaxX && outer.MinY <= inner.MinY && outer.MaxY >= inner.MaxY;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static AABB2D Enlarge(AABB2D box, double margin) => new()
    {
        MinX = box.MinX - margin,
        MinY = box.MinY - margin,
        MaxX = box.MaxX + margin,
        MaxY = box.MaxY + margin,
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static AABB2D Union(AABB2D a, AABB2D b) => new()
    {
        MinX = Math.Min(a.MinX, b.MinX),
        MinY = Math.Min(a.MinY, b.MinY),
        MaxX = Math.Max(a.MaxX, b.MaxX),
        MaxY = Math.Max(a.MaxY, b.MaxY),
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Area(AABB2D box) => (box.MaxX - box.MinX) * (box.MaxY - box.MinY);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static AABB2D Enclosing(BSphere2D s) => new()
    {
        MinX = s.CenterX - s.Radius,
        MinY = s.CenterY - s.Radius,
        MaxX = s.CenterX + s.Radius,
        MaxY = s.CenterY + s.Radius,
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsDegenerate(AABB2D box) =>
        double.IsNaN(box.MinX) || double.IsNaN(box.MinY) || double.IsNaN(box.MaxX) || double.IsNaN(box.MaxY) || box.MinX > box.MaxX || box.MinY > box.MaxY;

    // ── AABB3D ──────────────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Overlaps(AABB3D a, AABB3D b) =>
        a.MinX <= b.MaxX && a.MaxX >= b.MinX && a.MinY <= b.MaxY && a.MaxY >= b.MinY && a.MinZ <= b.MaxZ && a.MaxZ >= b.MinZ;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Contains(AABB3D outer, AABB3D inner) =>
        outer.MinX <= inner.MinX && outer.MaxX >= inner.MaxX && outer.MinY <= inner.MinY && outer.MaxY >= inner.MaxY && outer.MinZ <= inner.MinZ && outer.MaxZ >= inner.MaxZ;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static AABB3D Enlarge(AABB3D box, double margin) => new()
    {
        MinX = box.MinX - margin,
        MinY = box.MinY - margin,
        MinZ = box.MinZ - margin,
        MaxX = box.MaxX + margin,
        MaxY = box.MaxY + margin,
        MaxZ = box.MaxZ + margin,
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static AABB3D Union(AABB3D a, AABB3D b) => new()
    {
        MinX = Math.Min(a.MinX, b.MinX),
        MinY = Math.Min(a.MinY, b.MinY),
        MinZ = Math.Min(a.MinZ, b.MinZ),
        MaxX = Math.Max(a.MaxX, b.MaxX),
        MaxY = Math.Max(a.MaxY, b.MaxY),
        MaxZ = Math.Max(a.MaxZ, b.MaxZ),
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Volume(AABB3D box) =>
        (box.MaxX - box.MinX) * (box.MaxY - box.MinY) * (box.MaxZ - box.MinZ);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static AABB3D Enclosing(BSphere3D s) => new()
    {
        MinX = s.CenterX - s.Radius,
        MinY = s.CenterY - s.Radius,
        MinZ = s.CenterZ - s.Radius,
        MaxX = s.CenterX + s.Radius,
        MaxY = s.CenterY + s.Radius,
        MaxZ = s.CenterZ + s.Radius,
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsDegenerate(AABB3D box) =>
        double.IsNaN(box.MinX) || double.IsNaN(box.MinY) || double.IsNaN(box.MinZ) ||
        double.IsNaN(box.MaxX) || double.IsNaN(box.MaxY) || double.IsNaN(box.MaxZ) ||
        box.MinX > box.MaxX || box.MinY > box.MaxY || box.MinZ > box.MaxZ;

    // ── Generic coordinate-based helpers (variant-agnostic) ──────────────

    /// <summary>
    /// Ray-AABB slab intersection test. Returns (hit, tEntry) for front-to-back ordering.
    /// Coords ordered [min0, min1, ..., max0, max1, ...]. ~6-8 float ops per AABB.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (bool hit, double tEntry) RayAABBIntersect(ReadOnlySpan<double> origin, ReadOnlySpan<double> invDir, ReadOnlySpan<double> aabbCoords, 
        int coordCount)
    {
        int halfCoord = coordCount / 2;
        double tNear = double.MinValue;
        double tFar = double.MaxValue;

        for (int d = 0; d < halfCoord; d++)
        {
            double t1 = (aabbCoords[d] - origin[d]) * invDir[d];
            double t2 = (aabbCoords[d + halfCoord] - origin[d]) * invDir[d];

            if (t1 > t2)
            {
                (t1, t2) = (t2, t1);
            }

            tNear = Math.Max(tNear, t1);
            tFar = Math.Min(tFar, t2);

            if (tNear > tFar)
            {
                return (false, 0);
            }
        }

        // tNear < 0 means ray starts inside the box — still a hit at t=0
        return (tFar >= 0, Math.Max(tNear, 0));
    }

    /// <summary>
    /// Frustum classification constants.
    /// </summary>
    internal const int FrustumInside = 0;
    internal const int FrustumIntersecting = 1;
    internal const int FrustumOutside = 2;

    /// <summary>
    /// Classify an AABB against frustum planes using the positive/negative vertex method.
    /// Returns INSIDE (all planes pass), OUTSIDE (any plane fully rejects), or INTERSECTING.
    /// Planes are packed as (normalX, normalY, [normalZ,] distance) — dimCount+1 doubles per plane.
    /// </summary>
    public static int ClassifyAABBAgainstPlanes(ReadOnlySpan<double> aabbCoords, ReadOnlySpan<double> planes, int planeCount, int dimCount)
    {
        int halfCoord = dimCount; // aabbCoords: [min0..minN, max0..maxN]
        bool allInside = true;

        int planeStride = dimCount + 1; // normal(dimCount) + distance(1)
        for (int p = 0; p < planeCount; p++)
        {
            int planeOffset = p * planeStride;

            // Positive vertex: for each dimension, pick max if normal is positive, min if negative
            double pDot = 0;
            double nDot = 0;
            for (int d = 0; d < dimCount; d++)
            {
                double normal = planes[planeOffset + d];
                double minVal = aabbCoords[d];
                double maxVal = aabbCoords[d + halfCoord];
                if (normal >= 0)
                {
                    pDot += normal * maxVal;
                    nDot += normal * minVal;
                }
                else
                {
                    pDot += normal * minVal;
                    nDot += normal * maxVal;
                }
            }
            double dist = planes[planeOffset + dimCount];

            // If positive vertex is behind plane → entire AABB is outside
            if (pDot + dist < 0)
            {
                return FrustumOutside;
            }

            // If negative vertex is behind plane → AABB straddles this plane
            if (nDot + dist < 0)
            {
                allInside = false;
            }
        }

        return allInside ? FrustumInside : FrustumIntersecting;
    }

    /// <summary>
    /// Squared distance from a point to the center of an AABB. Coords ordered [min0..., max0...].
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double SquaredDistanceToCenter(ReadOnlySpan<double> point, ReadOnlySpan<double> aabbCoords, int coordCount)
    {
        int halfCoord = coordCount / 2;
        double distSq = 0;
        for (int d = 0; d < halfCoord; d++)
        {
            double center = (aabbCoords[d] + aabbCoords[d + halfCoord]) * 0.5;
            double diff = point[d] - center;
            distSq += diff * diff;
        }
        return distSq;
    }
}
