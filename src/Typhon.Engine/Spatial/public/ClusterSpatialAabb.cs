using System.Runtime.InteropServices;

namespace Typhon.Engine;

/// <summary>
/// Per-cluster tight AABB plus category mask, used by the per-cell cluster spatial index (issue #230).
/// One instance per spatially-active cluster, indexed by clusterChunkId. Stored in-memory only on
/// <see cref="ArchetypeClusterState"/> and rebuilt at startup via <c>RebuildClusterAabbs</c> from
/// entity positions (Q2/Q6 transient-state decision).
/// </summary>
/// <remarks>
/// <para>
/// <b>Storage shape.</b> 28 bytes: six f32 bounds components (XYZ min/max) plus a 4-byte category mask.
/// 2D archetypes leave <see cref="MinZ"/>/<see cref="MaxZ"/> at the <see cref="Empty"/> sentinel (+inf/-inf);
/// 2D queries use an infinite Z range which trivially passes the Z overlap test, so 2D clusters match
/// correctly. 3D archetypes populate all six bounds. The unified 3D storage adds ~8 bytes per cluster
/// versus a 2D-only design, in exchange for a single cluster-index code path that handles both tiers.
/// f64 variants (AABB2D/AABB3D) are deferred to a follow-up sub-issue of #228.
/// </para>
/// <para>
/// The <see cref="CategoryMask"/> is the OR of all entity category masks in the cluster — it lets the
/// per-cell broadphase skip entire clusters when the query's category mask does not intersect. Maintained
/// incrementally on spawn; tightened on the next full recompute pass at the tick fence.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
[JetBrains.Annotations.PublicAPI]
public struct ClusterSpatialAabb
{
    public float MinX;
    public float MinY;
    public float MinZ;
    public float MaxX;
    public float MaxY;
    public float MaxZ;
    public uint CategoryMask;

    /// <summary>Static empty sentinel for ref-returning properties when no spatial data exists.</summary>
    internal static ClusterSpatialAabb s_empty = new()
    {
        MinX = float.PositiveInfinity, MinY = float.PositiveInfinity, MinZ = float.PositiveInfinity,
        MaxX = float.NegativeInfinity, MaxY = float.NegativeInfinity, MaxZ = float.NegativeInfinity,
        CategoryMask = 0u,
    };

    /// <summary>Create an empty AABB suitable as the seed for incremental unions (min = +inf, max = -inf on all axes).</summary>
    public static ClusterSpatialAabb Empty => new()
    {
        MinX = float.PositiveInfinity,
        MinY = float.PositiveInfinity,
        MinZ = float.PositiveInfinity,
        MaxX = float.NegativeInfinity,
        MaxY = float.NegativeInfinity,
        MaxZ = float.NegativeInfinity,
        CategoryMask = 0u,
    };

    /// <summary>
    /// Union a 2D entity's tight AABB + category mask into this cluster AABB in place. Leaves <see cref="MinZ"/>/<see cref="MaxZ"/> at their initial
    /// values; 2D cluster archetypes never populate Z bounds, and 2D queries against those clusters use an infinite Z range that trivially passes the Z
    /// overlap test regardless of the stored Z values.
    /// </summary>
    public void Union2F(float entityMinX, float entityMinY, float entityMaxX, float entityMaxY, uint entityCategoryMask)
    {
        if (entityMinX < MinX) MinX = entityMinX;
        if (entityMinY < MinY) MinY = entityMinY;
        if (entityMaxX > MaxX) MaxX = entityMaxX;
        if (entityMaxY > MaxY) MaxY = entityMaxY;
        CategoryMask |= entityCategoryMask;
    }

    /// <summary>
    /// Union a 3D entity's tight AABB + category mask into this cluster AABB in place. Updates all six bounds components.
    /// </summary>
    public void Union3F(float entityMinX, float entityMinY, float entityMinZ, float entityMaxX, float entityMaxY, float entityMaxZ, uint entityCategoryMask)
    {
        if (entityMinX < MinX) MinX = entityMinX;
        if (entityMinY < MinY) MinY = entityMinY;
        if (entityMinZ < MinZ) MinZ = entityMinZ;
        if (entityMaxX > MaxX) MaxX = entityMaxX;
        if (entityMaxY > MaxY) MaxY = entityMaxY;
        if (entityMaxZ > MaxZ) MaxZ = entityMaxZ;
        CategoryMask |= entityCategoryMask;
    }
}
