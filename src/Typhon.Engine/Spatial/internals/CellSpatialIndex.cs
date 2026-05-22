using System;

namespace Typhon.Engine.Internals;

/// <summary>
/// Per-cell cluster index for one spatial archetype. Holds a compact SoA of cluster AABBs plus per-cluster back-references (clusterChunkId) and category masks.
/// Used by the broadphase stage of cluster-spatial queries — a linear scan over these arrays identifies which clusters in the cell overlap the query AABB
/// before the narrowphase scans each cluster's entities (issue #230).
/// </summary>
/// <remarks>
/// <para>
/// <b>Storage shape.</b> One allocation per cell that contains at least one cluster of this archetype. Each backing array has the same
/// length (<see cref="Capacity"/>) and the first <see cref="ClusterCount"/> entries are valid. Grown by doubling when <see cref="Add"/> would exceed capacity.
/// Removal is swap-with-last (the last entry fills the removed slot), which requires the caller to fix up the swapped cluster's back-pointer stored in
/// <c>ArchetypeClusterState.ClusterSpatialIndexSlot</c>.
/// </para>
/// <para>
/// <b>Tier support.</b> Stores 6 f32 axis-aligned bounds (XYZ min/max) per cluster. 2D archetypes leave <see cref="MinZ"/>/<see cref="MaxZ"/> at +inf/-inf
/// sentinels and are queried with an infinite Z range; 3D archetypes populate all six. Issue #230 Phase 3 unified the 2D and 3D paths into a single
/// cluster-index layout rather than maintaining two parallel index types. f64 variants are deferred to a follow-up.
/// </para>
/// <para>
/// <b>Phase 1 deviation from the design doc.</b> Design doc <c>02-cluster-rtree.md</c> proposes a fixed inline capacity (~24 clusters via <c>fixed float[]</c>
/// struct fields) with overflow to a real <see cref="SpatialRTree{TStore}"/>. Phase 1 uses plain managed arrays for simplicity and testability; the linear
/// broadphase scan is fine for typical cell populations (≤80 clusters in AntHill's dense zones). Phase 2 can reintroduce the inline-vs-overflow split once
/// profiling identifies hotspots.
/// </para>
/// <para>
/// <b>Not thread-safe.</b> All mutations happen inside the single-threaded tick fence / spawn / destroy paths. Queries also run single-threaded for now.
/// </para>
/// </remarks>
internal sealed class CellSpatialIndex
{
    internal const int DefaultInitialCapacity = 16;

    /// <summary>Number of valid entries in the index (first <c>ClusterCount</c> slots of each backing array).</summary>
    public int ClusterCount;

    /// <summary>Back-reference: clusterChunkId for each slot. Index within this array is the cluster's "index slot."</summary>
    public int[] ClusterIds;

    /// <summary>SoA AABB min-X components.</summary>
    public float[] MinX;

    /// <summary>SoA AABB min-Y components.</summary>
    public float[] MinY;

    /// <summary>SoA AABB min-Z components. Set to <see cref="float.PositiveInfinity"/> for 2D archetype clusters — see <see cref="ClusterSpatialAabb"/>.</summary>
    public float[] MinZ;

    /// <summary>SoA AABB max-X components.</summary>
    public float[] MaxX;

    /// <summary>SoA AABB max-Y components.</summary>
    public float[] MaxY;

    /// <summary>SoA AABB max-Z components. Set to <see cref="float.NegativeInfinity"/> for 2D archetype clusters — see <see cref="ClusterSpatialAabb"/>.</summary>
    public float[] MaxZ;

    /// <summary>Per-cluster category mask (OR of entity masks in that cluster).</summary>
    public uint[] CategoryMasks;

    /// <summary>Current backing-array capacity. All arrays are the same length.</summary>
    public int Capacity => ClusterIds.Length;

    public CellSpatialIndex(int initialCapacity = DefaultInitialCapacity)
    {
        if (initialCapacity < 1)
        {
            initialCapacity = 1;
        }
        ClusterCount = 0;
        ClusterIds = new int[initialCapacity];
        MinX = new float[initialCapacity];
        MinY = new float[initialCapacity];
        MinZ = new float[initialCapacity];
        MaxX = new float[initialCapacity];
        MaxY = new float[initialCapacity];
        MaxZ = new float[initialCapacity];
        CategoryMasks = new uint[initialCapacity];
    }

    /// <summary>
    /// Append a cluster to the index and return its slot (position in the SoA arrays). Grows the backing
    /// arrays by doubling when capacity is exhausted. The returned slot should be stored as the cluster's
    /// back-pointer so subsequent <see cref="UpdateAt"/> / <see cref="RemoveAt"/> calls can locate it in O(1).
    /// </summary>
    public int Add(int clusterChunkId, in ClusterSpatialAabb aabb)
    {
        if (ClusterCount == ClusterIds.Length)
        {
            Grow(ClusterCount * 2);
        }

        int slot = ClusterCount;
        ClusterIds[slot] = clusterChunkId;
        MinX[slot] = aabb.MinX;
        MinY[slot] = aabb.MinY;
        MinZ[slot] = aabb.MinZ;
        MaxX[slot] = aabb.MaxX;
        MaxY[slot] = aabb.MaxY;
        MaxZ[slot] = aabb.MaxZ;
        CategoryMasks[slot] = aabb.CategoryMask;
        ClusterCount++;
        return slot;
    }

    /// <summary>
    /// Overwrite the AABB at the given slot. Used when a cluster's AABB changes (entity movement, migration).
    /// </summary>
    public void UpdateAt(int slot, in ClusterSpatialAabb aabb)
    {
        MinX[slot] = aabb.MinX;
        MinY[slot] = aabb.MinY;
        MinZ[slot] = aabb.MinZ;
        MaxX[slot] = aabb.MaxX;
        MaxY[slot] = aabb.MaxY;
        MaxZ[slot] = aabb.MaxZ;
        CategoryMasks[slot] = aabb.CategoryMask;
    }

    /// <summary>
    /// Remove the cluster at the given slot via swap-with-last. Returns the clusterChunkId of the cluster
    /// that was MOVED into this slot (so the caller can fix up its back-pointer), or <c>-1</c> if no swap
    /// occurred (the removed slot was the last).
    /// </summary>
    public int RemoveAt(int slot)
    {
        int last = ClusterCount - 1;
        int swappedClusterId = -1;
        if (slot != last)
        {
            ClusterIds[slot] = ClusterIds[last];
            MinX[slot] = MinX[last];
            MinY[slot] = MinY[last];
            MinZ[slot] = MinZ[last];
            MaxX[slot] = MaxX[last];
            MaxY[slot] = MaxY[last];
            MaxZ[slot] = MaxZ[last];
            CategoryMasks[slot] = CategoryMasks[last];
            swappedClusterId = ClusterIds[slot];
        }
        // Clear the vacated tail entry (helps catch stray reads in Debug).
        ClusterIds[last] = 0;
        MinX[last] = 0f;
        MinY[last] = 0f;
        MinZ[last] = 0f;
        MaxX[last] = 0f;
        MaxY[last] = 0f;
        MaxZ[last] = 0f;
        CategoryMasks[last] = 0u;
        ClusterCount--;
        return swappedClusterId;
    }

    private void Grow(int newCapacity)
    {
        if (newCapacity <= ClusterIds.Length)
        {
            newCapacity = ClusterIds.Length + 1;
        }
        Array.Resize(ref ClusterIds, newCapacity);
        Array.Resize(ref MinX, newCapacity);
        Array.Resize(ref MinY, newCapacity);
        Array.Resize(ref MinZ, newCapacity);
        Array.Resize(ref MaxX, newCapacity);
        Array.Resize(ref MaxY, newCapacity);
        Array.Resize(ref MaxZ, newCapacity);
        Array.Resize(ref CategoryMasks, newCapacity);
    }
}
