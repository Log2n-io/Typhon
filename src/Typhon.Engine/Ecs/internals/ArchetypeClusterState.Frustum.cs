using System;

namespace Typhon.Engine.Internals;

/// <summary>
/// Cluster-level frustum query — <c>AC-9.6</c> of #872 step 9.
/// </summary>
internal sealed unsafe partial class ArchetypeClusterState
{
    /// <summary>Upper bound on a frustum query's plane count — the buffers it walks them in are stack-allocated.</summary>
    private const int MaxFrustumPlanes = 64;

    /// <summary>
    /// Entities whose tight AABB is not fully outside a set of half-space planes. Returns how many were written to <paramref name="results"/>.
    /// </summary>
    /// <param name="grid">The spatial grid whose cells are walked.</param>
    /// <param name="planes">
    /// Packed <c>(normalX, normalY, [normalZ,] distance)</c>, <c>dim + 1</c> doubles per plane. Inside is <c>dot(n, p) + d &gt;= 0</c>.
    /// </param>
    /// <param name="planeCount">How many planes <paramref name="planes"/> holds.</param>
    /// <param name="boundsMin">World-space minimum corner of a box containing the frustum. Required — see the remarks.</param>
    /// <param name="boundsMax">World-space maximum corner of that box.</param>
    /// <param name="results">Receives the matching entity ids; the query stops once it is full.</param>
    /// <param name="categoryMask">Category bitmask; <c>0</c> means "no filter".</param>
    /// <remarks>
    /// <para><b>The classification is <see cref="SpatialGeometry.ClassifyAABBAgainstPlanes"/> at every level</b> — cell, cluster and entity — which is the
    /// same routine the tree's own frustum traversal uses. §4.1 lists frustum queries under "reuse as-is", and reusing the predicate rather than restating it
    /// is what makes that true in practice: a second implementation of the positive/negative-vertex test would be free to disagree, and the disagreement
    /// would be a silent <c>SQ-01</c> false negative on whichever path was wrong.</para>
    /// <para><b>Why the caller supplies a bounding box.</b> A set of half-spaces need not be bounded at all, so there is no general way to derive the cell
    /// range from the planes — and walking every cell in the grid to find out would cost more than the query. A camera frustum's box falls out of its eight
    /// corners, which is the caller's to compute. Cells inside that box are then classified against the planes proper, so an over-generous box costs a few
    /// rejected cells rather than wrong results.</para>
    /// <para><b>Planes are translated per cell, not the geometry.</b> Cluster bounds are <c>C15</c> cell-relative, and shifting a plane between frames is
    /// <c>d' = d + dot(n, origin)</c> — one dot product per plane per cell, against re-expressing every cluster box in world space.</para>
    /// </remarks>
    public int QueryFrustum(SpatialGrid grid, ReadOnlySpan<double> planes, int planeCount, Vector3Like boundsMin, Vector3Like boundsMax,
        Span<long> results, uint categoryMask = uint.MaxValue)
    {
        if (results.Length == 0 || planeCount <= 0 || !SpatialSlot.HasSpatialIndex || PerCellIndex == null || ClusterSegment == null || ClusterAabbs == null)
        {
            return 0;
        }

        // The plane count drives a stackalloc below, and again per promoted cell. This method is reachable from
        // public API (EcsQuery.WhereFrustum), so the bound has to hold HERE and not only at the entry point: a stack
        // overflow kills the process rather than raising something the caller can catch. EcsQuery enforces the same
        // number so the message names the API the user called; this is the backstop for every other caller.
        if (planeCount > MaxFrustumPlanes)
        {
            ThrowHelper.ThrowInvalidOp($"A frustum query is limited to {MaxFrustumPlanes} planes; got {planeCount}.");
        }

        ref readonly var ss = ref SpatialSlot;
        bool is3D = ss.FieldInfo.FieldType == SpatialFieldType.AABB3F || ss.FieldInfo.FieldType == SpatialFieldType.BSphere3F;
        int dim = is3D ? 3 : 2;
        int stride = dim + 1;
        if (planes.Length < planeCount * stride)
        {
            ThrowHelper.ThrowInvalidOp($"Frustum query needs {planeCount * stride} doubles for {planeCount} planes in {dim}D, got {planes.Length}.");
        }

        grid.WorldToCellRange(boundsMin.X, boundsMin.Y, is3D ? boundsMin.Z : float.NegativeInfinity,
            boundsMax.X, boundsMax.Y, is3D ? boundsMax.Z : float.PositiveInfinity,
            out int cellMinX, out int cellMinY, out int cellMinZ, out int cellMaxX, out int cellMaxY, out int cellMaxZ);

        if (!is3D)
        {
            cellMinZ = grid.FlatPlaneZ;   // computed once at grid construction — #916's O2
            cellMaxZ = grid.FlatPlaneZ;
        }

        Span<double> shifted = stackalloc double[planeCount * stride];
        Span<double> box = stackalloc double[6];
        int count = 0;

        // ONE read of ClusterAabbs for the whole query — the rent below, every bounds check, and every index.
        // ArchetypeClusterState grows it with Array.Resize (a plain reference store), so re-reading the field per
        // cluster can see a LONGER array than the visit set was sized for: ids in [oldLen, newLen) would then pass the
        // bounds check while TryVisit reports them unvisited every time, silently turning dedup off for that range.
        var aabbs = ClusterAabbs;

        // One bit per cluster — see ClusterVisitSet. The frustum case is why it exists: its result sets are large enough for a per-entity rescan of the
        // whole buffer to dominate the query.
        var visited = ClusterVisitSet.Rent(aabbs.Length);
        var accessor = ClusterSegment.CreateChunkAccessor();
        try
        {
            for (int cz = cellMinZ; cz <= cellMaxZ && count < results.Length; cz++)
            {
                for (int cy = cellMinY; cy <= cellMaxY && count < results.Length; cy++)
                {
                    for (int cx = cellMinX; cx <= cellMaxX && count < results.Length; cx++)
                    {
                        if (!grid.TryGetCellKey(cx, cy, cz, out int cellKey) || cellKey >= PerCellIndex.Length)
                        {
                            continue;
                        }

                        var slot = PerCellIndex[cellKey];
                        if (slot == null)
                        {
                            continue;
                        }

                        grid.CellOrigin(cellKey, out float originX, out float originY, out float originZ);

                        // Reject the whole cell before touching its clusters. The cell's own box is exactly one classification, against up to a few thousand.
                        float cellSize = grid.Config.CellSize;
                        box[0] = originX;
                        box[1] = originY;
                        box[dim] = originX + cellSize;
                        box[dim + 1] = originY + cellSize;
                        if (is3D)
                        {
                            box[2] = originZ;
                            box[5] = originZ + cellSize;
                        }
                        if (SpatialGeometry.ClassifyAABBAgainstPlanes(box, planes, planeCount, dim) == SpatialGeometry.FrustumOutside)
                        {
                            continue;
                        }

                        ShiftPlanes(planes, shifted, planeCount, dim, originX, originY, is3D ? originZ : 0f);

                        FrustumScanHalf(slot, isStatic: false, ref accessor, shifted, planes, planeCount, dim, is3D, categoryMask, box, aabbs,
                            ref visited, results, ref count);
                        FrustumScanHalf(slot, isStatic: true, ref accessor, shifted, planes, planeCount, dim, is3D, categoryMask, box, aabbs,
                            ref visited, results, ref count);
                    }
                }
            }
        }
        finally
        {
            visited.Dispose();
            accessor.Dispose();
        }

        return count;
    }

    /// <summary>Re-express each plane in a cell's frame: the normal is unchanged by a translation, the distance shifts by its dot with the origin.</summary>
    private static void ShiftPlanes(ReadOnlySpan<double> planes, Span<double> shifted, int planeCount, int dim, float ox, float oy, float oz)
    {
        int stride = dim + 1;
        for (int p = 0; p < planeCount; p++)
        {
            int at = p * stride;
            double nx = planes[at];
            double ny = planes[at + 1];
            shifted[at] = nx;
            shifted[at + 1] = ny;

            double shift = (nx * ox) + (ny * oy);
            if (dim == 3)
            {
                double nz = planes[at + 2];
                shifted[at + 2] = nz;
                shift += nz * oz;
            }
            shifted[at + dim] = planes[at + dim] + shift;
        }
    }

    private void FrustumScanHalf(PerCellSpatialSlot slot, bool isStatic, ref ChunkAccessor<PersistentStore> accessor,
        ReadOnlySpan<double> cellPlanes, ReadOnlySpan<double> worldPlanes, int planeCount, int dim, bool is3D, uint categoryMask, Span<double> box,
        ClusterSpatialAabb[] aabbs, ref ClusterVisitSet visited, Span<long> results, ref int count)
    {
        var tree = slot.ReadTree(isStatic);   // acquire — see PerCellSpatialSlot.PublishDynamicTree
        if (tree != null)
        {
            // The tree stores 3D coordinates whatever the archetype's dimension, so its own traversal is always given 3D planes — a 2D query gets a Z pair
            // that accepts the flat slab and rejects nothing.
            Span<double> treePlanes = stackalloc double[planeCount * 4];
            To3DPlanes(cellPlanes, treePlanes, planeCount, dim);

            foreach (var hit in tree.Tree.QueryFrustum(treePlanes, planeCount, null, 0))
            {
                FrustumScanCluster((int)hit.PayloadId, ref accessor, worldPlanes, planeCount, dim, is3D, categoryMask, aabbs, ref visited, results,
                    ref count);
                if (count == results.Length)
                {
                    return;
                }
            }
            return;
        }

        var linear = slot.ReadIndex(isStatic);
        if (linear == null)
        {
            return;
        }

        for (int i = 0; i < linear.ClusterCount && count < results.Length; i++)
        {
            box[0] = linear.MinX[i];
            box[1] = linear.MinY[i];
            box[dim] = linear.MaxX[i];
            box[dim + 1] = linear.MaxY[i];
            if (is3D)
            {
                box[2] = linear.MinZ[i];
                box[5] = linear.MaxZ[i];
            }

            if (SpatialGeometry.ClassifyAABBAgainstPlanes(box, cellPlanes, planeCount, dim) == SpatialGeometry.FrustumOutside)
            {
                continue;
            }

            FrustumScanCluster(linear.ClusterIds[i], ref accessor, worldPlanes, planeCount, dim, is3D, categoryMask, aabbs, ref visited, results,
                ref count);
        }
    }

    /// <summary>Widen 2D planes to the tree's 3D layout, adding a Z pair that cannot reject the flat slab 2D clusters are stored on.</summary>
    private static void To3DPlanes(ReadOnlySpan<double> planes, Span<double> outPlanes, int planeCount, int dim)
    {
        if (dim == 3)
        {
            planes[..(planeCount * 4)].CopyTo(outPlanes);
            return;
        }

        for (int p = 0; p < planeCount; p++)
        {
            outPlanes[(p * 4) + 0] = planes[(p * 3) + 0];
            outPlanes[(p * 4) + 1] = planes[(p * 3) + 1];
            outPlanes[(p * 4) + 2] = 0d;                        // no Z component — the plane is a prism, exactly as the 2D query means it
            outPlanes[(p * 4) + 3] = planes[(p * 3) + 2];
        }
    }

    private void FrustumScanCluster(int clusterChunkId, ref ChunkAccessor<PersistentStore> accessor, ReadOnlySpan<double> worldPlanes,
        int planeCount, int dim, bool is3D, uint categoryMask, ClusterSpatialAabb[] aabbs, ref ClusterVisitSet visited, Span<long> results, ref int count)
    {
        if ((uint)clusterChunkId >= (uint)aabbs.Length)
        {
            return;
        }
        if (!visited.TryVisit(clusterChunkId))
        {
            return;
        }
        if (categoryMask != 0 && (aabbs[clusterChunkId].CategoryMask & categoryMask) == 0)
        {
            return;
        }


        ref readonly var ss = ref SpatialSlot;
        int compOffset = Layout.ComponentOffset(ss.Slot);
        int compSize = Layout.ComponentSize(ss.Slot);
        int fieldOffset = ss.FieldOffset;

        byte* clusterBase = accessor.GetChunkAddress(clusterChunkId);
        ulong occupancy = *(ulong*)clusterBase;

        Span<double> entityCoords = stackalloc double[6];
        Span<double> entityBox = stackalloc double[6];

        while (occupancy != 0UL && count < results.Length)
        {
            int slot = System.Numerics.BitOperations.TrailingZeroCount(occupancy);
            occupancy &= occupancy - 1;

            byte* fieldPtr = clusterBase + compOffset + (slot * compSize) + fieldOffset;
            if (!SpatialMaintainer.ReadAndValidateBoundsFromPtr(fieldPtr, ss.FieldInfo, entityCoords))
            {
                continue;
            }

            entityBox[0] = entityCoords[0];
            entityBox[1] = entityCoords[1];
            if (is3D)
            {
                entityBox[2] = entityCoords[2];
                entityBox[3] = entityCoords[3];
                entityBox[4] = entityCoords[4];
                entityBox[5] = entityCoords[5];
            }
            else
            {
                entityBox[2] = entityCoords[2];
                entityBox[3] = entityCoords[3];
            }

            // Entity bounds are WORLD space, so they are classified against the caller's original planes. Only the broadphase works in the cell frame,
            // because only cluster bounds are cell-relative — mixing the two up would reject entities by the cell's offset.
            if (SpatialGeometry.ClassifyAABBAgainstPlanes(entityBox, worldPlanes, planeCount, dim) == SpatialGeometry.FrustumOutside)
            {
                continue;
            }

            // No entity-level duplicate check: an entity lives in exactly one cluster slot, so the only way to report one twice is to scan its cluster
            // twice, and ClusterVisitSet has already refused that above.
            results[count++] = *(long*)(clusterBase + Layout.EntityIdsOffset + (slot * 8));
        }
    }

}

/// <summary>Minimal three-float point, so the frustum entry point does not force a dependency on a particular vector type.</summary>
internal readonly struct Vector3Like
{
    public readonly float X;
    public readonly float Y;
    public readonly float Z;

    public Vector3Like(float x, float y, float z)
    {
        X = x;
        Y = y;
        Z = z;
    }
}
