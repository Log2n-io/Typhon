using System;

namespace Typhon.Engine.Internals;

/// <summary>
/// Zero-allocation spatial query handle for hot-loop usage (physics, AI, tick-frequency queries).
/// Obtained from <c>tx.SpatialQuery&lt;T&gt;()</c>. All methods return <c>ref struct</c> enumerators.
/// Delegates to <see cref="SpatialIndexState.ActiveTree"/> (exactly one tree is non-null per component type).
/// </summary>
internal readonly ref struct SpatialQuery<T> where T : unmanaged
{
    private readonly SpatialRTree<PersistentStore> _tree;

    internal SpatialQuery(SpatialIndexState state) => _tree = state.ActiveTree;

    /// <summary>AABB overlap query. Coords: [min0, min1, ..., max0, max1, ...]. categoryMask=0 means no filtering.</summary>
    public SpatialRTree<PersistentStore>.AABBQueryEnumerator AABB(ReadOnlySpan<double> coords, uint categoryMask = 0)
        => _tree.QueryAABB(coords, categoryMask: categoryMask);

    /// <summary>Radius (sphere) query. Returns entities whose fat AABB overlaps the bounding box of the sphere. Caller post-filters by distance.</summary>
    public SpatialRTree<PersistentStore>.RadiusEnumerator Radius(ReadOnlySpan<double> center, double radius, uint categoryMask = 0)
        => _tree.QueryRadius(center, radius, categoryMask: categoryMask);

    /// <summary>Ray query with front-to-back ordering. Origin + direction + maxDist.</summary>
    public SpatialRTree<PersistentStore>.RayEnumerator Ray(ReadOnlySpan<double> origin, ReadOnlySpan<double> direction, double maxDist,
        uint categoryMask = 0) =>
        _tree.QueryRay(origin, direction, maxDist, categoryMask: categoryMask);

    /// <summary>Frustum query. Planes packed as (normalX, normalY, [normalZ,] distance), dimCount+1 doubles per plane.</summary>
    public SpatialRTree<PersistentStore>.FrustumEnumerator Frustum(ReadOnlySpan<double> planes, int planeCount, uint categoryMask = 0) =>
        _tree.QueryFrustum(planes, planeCount, categoryMask: categoryMask);

    /// <summary>k-nearest-neighbor candidates via iterative radius expansion. The <c>distSq</c> field is 0 — callers must
    /// recompute actual distances from component data (the tree stores fat AABBs, not tight bounds) and sort if needed.</summary>
    public int Nearest(ReadOnlySpan<double> center, int k, Span<(long entityId, double distSq)> results, uint categoryMask = 0)
        => _tree.QueryKNN(center, k, results, categoryMask: categoryMask);

    /// <summary>Count entities whose fat AABB overlaps the query box. Uses subtree counting shortcut for fully-contained regions.</summary>
    public int CountInAABB(ReadOnlySpan<double> coords, uint categoryMask = 0)
        => _tree.CountInAABB(coords, categoryMask: categoryMask);

    /// <summary>Count entities whose fat AABB overlaps the bounding box of the sphere.</summary>
    public int CountInRadius(ReadOnlySpan<double> center, double radius, uint categoryMask = 0)
        => _tree.CountInRadius(center, radius, categoryMask: categoryMask);
}
