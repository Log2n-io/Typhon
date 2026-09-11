using System;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Cluster-level k-nearest-neighbour search — <c>AC-9.7</c> of #872 step 9.
/// </summary>
internal sealed unsafe partial class ArchetypeClusterState
{
    /// <summary>
    /// Per-call scratch for the candidate min-heap. <b>Deliberately NOT instance state.</b>
    /// </summary>
    /// <remarks>
    /// It was instance state, which was wrong: <see cref="ArchetypeClusterState"/> is shared by every system touching the archetype, so two concurrent kNN
    /// queries would interleave pushes and pops on one heap and each would get the other's neighbours, silently. <see cref="AabbClusterEnumerator"/> is a
    /// <c>ref struct</c> for exactly this reason and kNN had abandoned that. Reusing a buffer across calls is worth nothing next to a query that returns the
    /// wrong answer under a load nobody reproduces on demand.
    /// </remarks>
    private ref struct KnnCandidateHeap
    {
        internal int[] Chunk;
        internal double[] Dist;
        internal int Count;
    }

    /// <summary>
    /// The <paramref name="k"/> entities closest to a point, written to <paramref name="results"/> in ascending distance order. Returns how many were written.
    /// </summary>
    /// <remarks>
    /// <para><b>Best-first over clusters, not a scan.</b> The design (§4.1) singles kNN out as the one tree operation that must be reimplemented rather than
    /// ported: <i>a cluster's distance is a lower bound, not a distance</i>. That is what makes early termination sound — once k entities are held and the
    /// nearest unopened cluster's box is further than the worst of them, no unopened cluster can improve the answer, whatever it contains.</para>
    /// <para><b>What it replaces.</b> The previous implementation issued a radius query with <see cref="float.MaxValue"/>, which reaches every cell in the
    /// world, collected every entity into a <c>List</c> allocated per call, and partial-selection-sorted it — <c>O(k·n)</c> over the whole population for any
    /// k, on a path whose whole purpose is to look at as little as possible. Its own doc called it "simple" and pointed at a follow-up; this is that follow-up.
    /// </para>
    /// <para><b>Rings, so the search does not have to start by knowing where to look.</b> Cells are taken in shells around the query point, and the loop stops
    /// only when the k-th distance is inside the region already covered — otherwise a closer entity could still be sitting in the next shell. That test is the
    /// correctness condition; the priority queue is what makes it cheap to reach.</para>
    /// <para><b>Category filtering is per cluster.</b> A category is a property of the archetype, so every entity in a cluster shares its mask and the
    /// broadphase filter is exact — no per-entity re-check, matching <see cref="AabbClusterEnumerator"/>'s convention where a zero mask means "no filter".
    /// </para>
    /// </remarks>
    public int QueryNearest(SpatialGrid grid, double centerX, double centerY, double centerZ, int k, Span<(long entityId, double distSq)> results,
        uint categoryMask = uint.MaxValue) =>
        QueryNearest(grid, centerX, centerY, centerZ, k, results, out _, categoryMask);

    /// <summary>As above, additionally reporting how many clusters the search opened.</summary>
    /// <remarks>
    /// Whether the lower bound is actually pruning is a property of the search, not of the machine it ran on, so this is what a test asserts on rather than a
    /// wall-clock threshold that would redden on a busy box.
    /// </remarks>
    public int QueryNearest(SpatialGrid grid, double centerX, double centerY, double centerZ, int k, Span<(long entityId, double distSq)> results,
        out int clustersOpened, uint categoryMask = uint.MaxValue)
    {
        clustersOpened = 0;
        if (k <= 0 || results.Length == 0 || !SpatialSlot.HasSpatialIndex || PerCellIndex == null || ClusterSegment == null || ClusterAabbs == null)
        {
            return 0;
        }

        int target = Math.Min(k, results.Length);
        ref readonly var ss = ref SpatialSlot;
        bool is3D = ss.FieldInfo.FieldType.Is3D();

        // A 2D archetype's entities all lie in the plane containing world Z = 0, so the query point is projected onto it rather than searching Z shells that
        // are empty by construction — the same reasoning AabbClusterEnumerator applies to its cell range.
        double queryZ = is3D ? centerZ : 0d;

        grid.WorldToCellCoords(centerX, centerY, queryZ, out int originCellX, out int originCellY, out int originCellZ);

        ref readonly var config = ref grid.Config;
        int maxRing = Math.Max(Math.Max(config.GridWidth, config.GridHeight), is3D ? config.GridDepth : 1);

        int resultCount = 0;
        var heap = default(KnnCandidateHeap);

        var accessor = ClusterSegment.CreateChunkAccessor();
        try
        {
            for (int ring = 0; ring <= maxRing; ring++)
            {
                CollectRingCandidates(grid, ring, originCellX, originCellY, originCellZ, is3D,
                    centerX, centerY, queryZ, categoryMask, ref heap);

                // Drain every candidate that could still beat the current k-th best. A candidate whose LOWER BOUND is already worse cannot contain anything
                // better, so it stays on the heap — and if the ring test below ends the search, it is never opened at all.
                while (heap.Count > 0)
                {
                    double bestBound = heap.Dist[0];
                    if (resultCount == target && bestBound >= results[0].distSq)
                    {
                        break;
                    }

                    int clusterChunkId = heap.Chunk[0];
                    PopMinCandidate(ref heap);
                    ScanClusterForNearest(clusterChunkId, ref accessor, centerX, centerY, queryZ, is3D, target, results, ref resultCount, ref clustersOpened);
                }

                if (resultCount == target && CoveredRadiusSq(grid, ring, originCellX, originCellY, originCellZ, is3D, centerX, centerY, queryZ)
                    >= results[0].distSq)
                {
                    break;
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        // The result heap is max-at-root; the caller is promised ascending order, so drain it.
        for (int i = resultCount - 1; i > 0; i--)
        {
            (results[0], results[i]) = (results[i], results[0]);
            SiftDownMax(results[..i]);
        }

        return resultCount;
    }

    /// <summary>Push every cluster in the cells of one shell onto the candidate heap, keyed by the lower bound its box implies.</summary>
    private void CollectRingCandidates(SpatialGrid grid, int ring, int originCellX, int originCellY, int originCellZ, bool is3D,
        double px, double py, double pz, uint categoryMask, ref KnnCandidateHeap heap)
    {
        int zLo = is3D ? originCellZ - ring : originCellZ;
        int zHi = is3D ? originCellZ + ring : originCellZ;

        for (int cz = zLo; cz <= zHi; cz++)
        {
            for (int cy = originCellY - ring; cy <= originCellY + ring; cy++)
            {
                for (int cx = originCellX - ring; cx <= originCellX + ring; cx++)
                {
                    // Only the shell: everything strictly inside was pushed by an earlier ring, and pushing it again would open clusters twice.
                    int chebyshev = Math.Max(Math.Abs(cx - originCellX), Math.Abs(cy - originCellY));
                    if (is3D)
                    {
                        chebyshev = Math.Max(chebyshev, Math.Abs(cz - originCellZ));
                    }
                    if (chebyshev != ring)
                    {
                        continue;
                    }

                    // TryGetCellKey, never ComputeCellKey — a kNN sweep crosses mostly empty space, and resolving-with-create would materialise a cell for
                    // every coordinate it touches.
                    if (!grid.TryGetCellKey(cx, cy, cz, out int cellKey) || cellKey >= PerCellIndex.Length)
                    {
                        continue;
                    }

                    var slot = PerCellIndex[cellKey];
                    if (slot == null)
                    {
                        continue;
                    }

                    grid.CellOrigin(cellKey, out double originX, out double originY, out double originZ);
                    PushCellClusters(slot, isStatic: false, originX, originY, originZ, px, py, pz, categoryMask, ref heap);
                    PushCellClusters(slot, isStatic: true, originX, originY, originZ, px, py, pz, categoryMask, ref heap);
                }
            }
        }

        return;
    }

    /// <summary>Push one half of a cell — whichever structure serves it — onto the candidate heap.</summary>
    private void PushCellClusters(PerCellSpatialSlot slot, bool isStatic, double originX, double originY, double originZ,
        double px, double py, double pz, uint categoryMask, ref KnnCandidateHeap heap)
    {
        var tree = slot.ReadTree(isStatic);   // acquire — see PerCellSpatialSlot.PublishDynamicTree
        if (tree != null)
        {
            foreach (int clusterChunkId in tree.EnumerateClusterIds())
            {
                PushCandidate(clusterChunkId, originX, originY, originZ, px, py, pz, categoryMask, ref heap);
            }
            return;
        }

        var linear = slot.ReadIndex(isStatic);
        if (linear == null)
        {
            return;
        }

        for (int i = 0; i < linear.ClusterCount; i++)
        {
            PushCandidate(linear.ClusterIds[i], originX, originY, originZ, px, py, pz, categoryMask, ref heap);
        }
        return;
    }

    private void PushCandidate(int clusterChunkId, double originX, double originY, double originZ, double px, double py, double pz,
        uint categoryMask, ref KnnCandidateHeap heap)
    {
        if ((uint)clusterChunkId >= (uint)ClusterAabbs.Length)
        {
            return;
        }

        ref readonly var aabb = ref ClusterAabbs[clusterChunkId];
        if (categoryMask != 0 && (aabb.CategoryMask & categoryMask) == 0)
        {
            return;
        }
        if (float.IsPositiveInfinity(aabb.MinX))
        {
            return; // Empty sentinel — a cluster with no cell, hence no frame and no position.
        }

        // Cell-relative to world (C15), then the squared distance from the point to the box. Zero when the point is inside, which correctly sorts such a
        // cluster first.
        //
        // ToWorldExact since #919 F2, where the directed ToWorldMin/Max pair used to be. The direction mattered because those RETURN an f32 and the
        // narrowing could round this box inward — OVERSTATING the lower bound, which lets the early-termination test prune a cluster holding a closer
        // entity. In double the residual is at most half a double ULP (~4e-6 at 2^36), against a stored bound already rounded outward by up to one f32 ULP
        // in the cell frame (~6e-5 across a 1 000-unit cell). The bound therefore still UNDER-states the distance, which is the direction that keeps the
        // pruning sound.
        double minX = ClusterSpatialAabb.ToWorldExact(aabb.MinX, originX);
        double minY = ClusterSpatialAabb.ToWorldExact(aabb.MinY, originY);
        double maxX = ClusterSpatialAabb.ToWorldExact(aabb.MaxX, originX);
        double maxY = ClusterSpatialAabb.ToWorldExact(aabb.MaxY, originY);

        double dx = Math.Max(Math.Max(minX - px, 0d), px - maxX);
        double dy = Math.Max(Math.Max(minY - py, 0d), py - maxY);
        double bound = (dx * dx) + (dy * dy);

        // A 2D cluster carries the ±Infinity Z sentinel, which contributes nothing to a planar distance.
        if (!float.IsPositiveInfinity(aabb.MinZ) && !float.IsNegativeInfinity(aabb.MaxZ))
        {
            double minZ = ClusterSpatialAabb.ToWorldExact(aabb.MinZ, originZ);
            double maxZ = ClusterSpatialAabb.ToWorldExact(aabb.MaxZ, originZ);
            double dz = Math.Max(Math.Max(minZ - pz, 0d), pz - maxZ);
            bound += dz * dz;
        }

        if (heap.Chunk == null)
        {
            heap.Chunk = new int[64];
            heap.Dist = new double[64];
        }
        if (heap.Count == heap.Chunk.Length)
        {
            Array.Resize(ref heap.Chunk, heap.Count * 2);
            Array.Resize(ref heap.Dist, heap.Count * 2);
        }

        int i = heap.Count++;
        heap.Chunk[i] = clusterChunkId;
        heap.Dist[i] = bound;
        while (i > 0)
        {
            int parent = (i - 1) / 2;
            if (heap.Dist[parent] <= heap.Dist[i])
            {
                break;
            }
            (heap.Dist[parent], heap.Dist[i]) = (heap.Dist[i], heap.Dist[parent]);
            (heap.Chunk[parent], heap.Chunk[i]) = (heap.Chunk[i], heap.Chunk[parent]);
            i = parent;
        }
        return;
    }

    private static void PopMinCandidate(ref KnnCandidateHeap heap)
    {
        int last = heap.Count - 1;
        heap.Dist[0] = heap.Dist[last];
        heap.Chunk[0] = heap.Chunk[last];

        int i = 0;
        while (true)
        {
            int l = (2 * i) + 1;
            int r = l + 1;
            int smallest = i;
            if (l < last && heap.Dist[l] < heap.Dist[smallest])
            {
                smallest = l;
            }
            if (r < last && heap.Dist[r] < heap.Dist[smallest])
            {
                smallest = r;
            }
            if (smallest == i)
            {
                break;
            }
            (heap.Dist[smallest], heap.Dist[i]) = (heap.Dist[i], heap.Dist[smallest]);
            (heap.Chunk[smallest], heap.Chunk[i]) = (heap.Chunk[i], heap.Chunk[smallest]);
            i = smallest;
        }
        heap.Count = last;
    }

    /// <summary>Read every occupied entity of one cluster and offer it to the result heap.</summary>
    private void ScanClusterForNearest(int clusterChunkId, ref ChunkAccessor<PersistentStore> accessor, double px, double py, double pz, bool is3D,
        int target, Span<(long entityId, double distSq)> results, ref int resultCount, ref int clustersOpened)
    {
        ref readonly var ss = ref SpatialSlot;
        int compOffset = Layout.ComponentOffset(ss.Slot);
        int compSize = Layout.ComponentSize(ss.Slot);
        int fieldOffset = ss.FieldOffset;

        clustersOpened++;
        byte* clusterBase = accessor.GetChunkAddress(clusterChunkId);
        ulong occupancy = *(ulong*)clusterBase;

        Span<double> entityCoords = stackalloc double[6];
        while (occupancy != 0UL)
        {
            int slot = System.Numerics.BitOperations.TrailingZeroCount(occupancy);
            occupancy &= occupancy - 1;

            byte* fieldPtr = clusterBase + compOffset + (slot * compSize) + fieldOffset;
            if (!SpatialMaintainer.ReadAndValidateBoundsFromPtr(fieldPtr, ss.FieldInfo, entityCoords))
            {
                continue;
            }

            double eMinX = entityCoords[0];
            double eMinY = entityCoords[1];
            double eMaxX = is3D ? entityCoords[3] : entityCoords[2];
            double eMaxY = is3D ? entityCoords[4] : entityCoords[3];

            double dx = Math.Max(Math.Max(eMinX - px, 0d), px - eMaxX);
            double dy = Math.Max(Math.Max(eMinY - py, 0d), py - eMaxY);
            double distSq = (dx * dx) + (dy * dy);

            if (is3D)
            {
                double eMinZ = entityCoords[2];
                double eMaxZ = entityCoords[5];
                double dz = Math.Max(Math.Max(eMinZ - pz, 0d), pz - eMaxZ);
                distSq += dz * dz;
            }

            if (resultCount == target && distSq >= results[0].distSq)
            {
                continue;
            }

            long entityId = *(long*)(clusterBase + Layout.EntityIdsOffset + (slot * 8));
            PushResult(results, ref resultCount, target, entityId, distSq);
        }
    }

    /// <summary>Offer one entity to the bounded max-heap of results, evicting the current worst once it is full.</summary>
    private static void PushResult(Span<(long entityId, double distSq)> results, ref int resultCount, int target, long entityId, double distSq)
    {
        if (resultCount < target)
        {
            int i = resultCount++;
            results[i] = (entityId, distSq);
            while (i > 0)
            {
                int parent = (i - 1) / 2;
                if (results[parent].distSq >= results[i].distSq)
                {
                    break;
                }
                (results[parent], results[i]) = (results[i], results[parent]);
                i = parent;
            }
            return;
        }

        results[0] = (entityId, distSq);
        SiftDownMax(results[..resultCount]);
    }

    private static void SiftDownMax(Span<(long entityId, double distSq)> heap)
    {
        int i = 0;
        while (true)
        {
            int l = (2 * i) + 1;
            int r = l + 1;
            int largest = i;
            if (l < heap.Length && heap[l].distSq > heap[largest].distSq)
            {
                largest = l;
            }
            if (r < heap.Length && heap[r].distSq > heap[largest].distSq)
            {
                largest = r;
            }
            if (largest == i)
            {
                break;
            }
            (heap[largest], heap[i]) = (heap[i], heap[largest]);
            i = largest;
        }
    }

    /// <summary>
    /// The squared radius around the query point that rings <c>0..ring</c> are guaranteed to have covered.
    /// </summary>
    /// <remarks>
    /// <para>This is the search's correctness condition, not an optimisation. An entity closer than the current k-th best could still be sitting one shell
    /// out, so the loop may only stop once the k-th distance fits inside the covered region. A face that coincides with the world bound is treated as
    /// unbounded — there is nothing beyond it to find.</para>
    /// <para><b>The shell of cells is NOT the region those cells cover.</b> A cluster is filed by its entities' CENTRES, so its box reaches up to
    /// <see cref="MaxClusterOverhang"/> outside its own cell — and a cluster one shell out can therefore hold an entity nearer than the face distance. Taking
    /// the face distance as covered is what dropped true nearest neighbours: with 100-unit cells, a query at (150,150), a point at (190,150) and a 50-wide box
    /// centred at (205,150) reaching x=180, ring 0 declared 50 units covered, 50^2 beat the point's 40^2, and the nearer box was never opened. Subtracting the
    /// overhang is what makes the stopping rule true again; it costs breadth, and only where extended entities actually exist.</para>
    /// </remarks>
    private double CoveredRadiusSq(SpatialGrid grid, int ring, int originCellX, int originCellY, int originCellZ, bool is3D,
        double px, double py, double pz)
    {
        ref readonly var config = ref grid.Config;
        double cell = config.CellSize;

        double safe = AxisSlack(px, config.WorldMin.X, originCellX, ring, cell, config.GridWidth);
        safe = Math.Min(safe, AxisSlack(py, config.WorldMin.Y, originCellY, ring, cell, config.GridHeight));
        if (is3D)
        {
            safe = Math.Min(safe, AxisSlack(pz, config.WorldMin.Z, originCellZ, ring, cell, config.GridDepth));
        }

        if (double.IsPositiveInfinity(safe))
        {
            return double.PositiveInfinity;
        }

        safe -= Volatile.Read(ref MaxClusterOverhang);
        return safe <= 0d ? 0d : safe * safe;
    }

    private static double AxisSlack(double p, double worldMin, int originCell, int ring, double cell, int gridExtent)
    {
        int lo = originCell - ring;
        int hi = originCell + ring;

        // Both faces at the world bound: this axis can hide nothing further out.
        //
        // The slack is a distance of at most a few cells, so f32 would hold it exactly where a world coordinate would not — which is why it used to be
        // narrowed here. It is kept in double since #919 F2 for a different reason than magnitude: the SUBTRACTION is what has to be exact, and its two
        // operands are world coordinates. Narrowing the difference after computing it in double was already right; narrowing it is simply no longer
        // needed now that the consumer compares against an f64 distSq.
        double loSlack = lo <= 0 ? double.PositiveInfinity : p - (worldMin + (lo * cell));
        double hiSlack = hi >= gridExtent - 1 ? double.PositiveInfinity : (worldMin + ((hi + 1) * cell)) - p;
        return Math.Max(0d, Math.Min(loSlack, hiSlack));
    }
}
