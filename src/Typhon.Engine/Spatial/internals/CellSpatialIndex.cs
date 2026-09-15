using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Threading;

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
/// cluster-index layout rather than maintaining two parallel index types. The f64 tiers landed in #914 and did NOT change this: cluster bounds are stored
/// CELL-RELATIVE (C15), so f32 spans one cell at ~6 x 10^-5 resolution however far out the cell sits, and widening this SoA would have halved the batch
/// width for nothing. An f64 archetype's precision lives in its component field, its query box and its result — never here.
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
    /// Below this many clusters in a cell, the scalar loop wins and <see cref="MatchBatch"/> should not be used.
    /// </summary>
    /// <remarks>
    /// Not a hedge — measured. A batch costs a call, a count clamp, six array loads and a set-bit loop before any comparison
    /// happens, and under 8 clusters not one vector iteration runs, so the whole apparatus wraps a scalar tail: at 4 clusters
    /// the batched form measured 9.2 ns against the scalar loop's 6.3 ns, a 46% REGRESSION, and that is the density measured
    /// game worlds actually sit at. Two full vector iterations is where it turns over.
    /// </remarks>
    public const int SimdScanMinClusters = 16;

    /// <summary>
    /// Test up to 64 clusters starting at <paramref name="start"/> against a query box, returning a bit per overlapping slot.
    /// </summary>
    /// <remarks>
    /// <para><b>This is the broadphase the engine almost always runs.</b> A cell promotes to a per-cell R-Tree only above
    /// <c>CellTreePromoteThreshold</c> clusters, which measured game worlds essentially never reach, so this linear scan —
    /// not the tree — is what a spatial query spends its time in. It was a scalar loop over six <see cref="float"/> arrays
    /// while the R-Tree's leaf scan had already been vectorised, which had the optimisation effort pointed at the path that
    /// does not run.</para>
    /// <para><b>Batched rather than whole-index, because the caller is an enumerator.</b> The broadphase yields one cluster
    /// at a time and the narrowphase runs between yields, so a whole-index match would need somewhere to put an unbounded
    /// result set. Sixty-four slots at a time fits a <see cref="ulong"/> the caller can carry as one field, refilled when it
    /// empties — bounded, allocation-free, and it keeps the vector work on contiguous runs.</para>
    /// <para>The Z test is skipped for a 2D archetype, whose <see cref="MinZ"/>/<see cref="MaxZ"/> hold the +∞/−∞ sentinel:
    /// comparing those against a finite query bound is false on both sides, so including the axis would reject everything.
    /// The caller passes ±∞ for a 2D query, and ±∞ compared against the sentinel is likewise false — hence the explicit
    /// skip rather than relying on the comparison.</para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public ulong MatchBatch(int start, float minX, float minY, float minZ, float maxX, float maxY, float maxZ, bool testZ)
    {
        int count = Math.Min(64, ClusterCount - start);
        if (count <= 0)
        {
            return 0UL;
        }

        var aMinX = MinX;
        var aMinY = MinY;
        var aMaxX = MaxX;
        var aMaxY = MaxY;
        var aMinZ = MinZ;
        var aMaxZ = MaxZ;

        ulong mask = 0UL;
        int i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            var qMinX = Vector256.Create(minX);
            var qMinY = Vector256.Create(minY);
            var qMaxX = Vector256.Create(maxX);
            var qMaxY = Vector256.Create(maxY);
            var qMinZ = Vector256.Create(minZ);
            var qMaxZ = Vector256.Create(maxZ);
            for (; i + 8 <= count; i += 8)
            {
                int b = start + i;
                var m = Vector256.GreaterThanOrEqual(Vector256.Create(aMaxX, b), qMinX)
                    & Vector256.LessThanOrEqual(Vector256.Create(aMinX, b), qMaxX)
                    & Vector256.GreaterThanOrEqual(Vector256.Create(aMaxY, b), qMinY)
                    & Vector256.LessThanOrEqual(Vector256.Create(aMinY, b), qMaxY);
                if (testZ)
                {
                    m &= Vector256.GreaterThanOrEqual(Vector256.Create(aMaxZ, b), qMinZ)
                        & Vector256.LessThanOrEqual(Vector256.Create(aMinZ, b), qMaxZ);
                }

                mask |= (ulong)m.ExtractMostSignificantBits() << i;
            }
        }

        for (; i < count; i++)
        {
            int b = start + i;
            if (aMaxX[b] < minX || aMinX[b] > maxX || aMaxY[b] < minY || aMinY[b] > maxY)
            {
                continue;
            }

            if (testZ && (aMaxZ[b] < minZ || aMinZ[b] > maxZ))
            {
                continue;
            }

            mask |= 1UL << i;
        }

        return mask;
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
    /// Odd while <see cref="Grow"/> copies the arrays, even otherwise: bumped once before the copy and once after the publish. <see cref="WidenAt"/> keeps
    /// a write only if the even stamp it started from has not moved. The protocol, and the argument for it, are
    /// <c>ArchetypeClusterState._clusterAabbsGrowth</c>'s: both sides fence between their two accesses, so either the copy reads the widen or the widen
    /// sees the bump. The grow's fence is its first bump. The widen's is its closing <c>Interlocked.Or</c>, which always runs: a CAS loop that finds its
    /// axis already wide enough writes, and fences, nothing.
    /// </summary>
    private int _growth;

    /// <summary>Test seam: runs inside <see cref="Grow"/> after the copy and before the publish, with <see cref="_growth"/> odd. Null outside tests.</summary>
    internal Action GrowCopiedProbe;

    /// <summary>Test seam: runs when <see cref="WidenAt"/> finds a grow in flight, before it waits the grow out. Null outside tests.</summary>
    internal Action WidenWaitProbe;

    /// <summary>Test seam: runs in <see cref="WidenAt"/> once it holds an even stamp, before each attempt's writes. Null outside tests.</summary>
    internal Action WidenStampedProbe;

    /// <summary>
    /// Widen the AABB at the given slot — every axis a CAS that only moves a min down or a max up — from a thread that holds no latch. The spawn path's
    /// funnel (<c>ArchetypeClusterState.WidenClusterInPerCellIndex</c>): two spawns into one cluster widen the same slot at once, and a spawn opening a
    /// cluster in the same cell may be inside a latched <see cref="Add"/> → <see cref="Grow"/> that replaces every array. A plain store there lands in
    /// the abandoned array — a bound the index never sees (CA-02 → CA-01 false negative, step 15 review). The writes therefore run under
    /// <see cref="_growth"/>: from an even stamp, kept if it has not moved, redone in the live arrays if it has. Idempotent by construction, so a redo
    /// costs nothing but the CAS loops.
    /// </summary>
    /// <remarks>
    /// The first form re-checked <see cref="ClusterIds"/>, which <see cref="Grow"/> publishes last, as its witness. That missed a widen into an array the
    /// grow had copied but not yet published whenever the re-check ran before the grow published <see cref="ClusterIds"/>: the witness had not moved, so
    /// the write stayed in the abandoned array (<c>CellSpatialIndexTests.AWidenDuringAGrowsCopy_LandsInTheGrownArrays</c>). A widen stamped before a grow
    /// and written after its copy is the redo's case (<c>AWidenStampedBeforeAGrow_IsRedoneInTheGrownArrays</c>).
    /// </remarks>
    public void WidenAt(int slot, in ClusterSpatialAabb aabb)
    {
        while (true)
        {
            var stamp = Volatile.Read(ref _growth);
            if ((stamp & 1) != 0)
            {
                stamp = WaitOutGrow();
            }

            WidenStampedProbe?.Invoke();
            ClusterSpatialAabb.CasMin(ref Volatile.Read(ref MinX)[slot], aabb.MinX);
            ClusterSpatialAabb.CasMin(ref Volatile.Read(ref MinY)[slot], aabb.MinY);
            ClusterSpatialAabb.CasMin(ref Volatile.Read(ref MinZ)[slot], aabb.MinZ);
            ClusterSpatialAabb.CasMax(ref Volatile.Read(ref MaxX)[slot], aabb.MaxX);
            ClusterSpatialAabb.CasMax(ref Volatile.Read(ref MaxY)[slot], aabb.MaxY);
            ClusterSpatialAabb.CasMax(ref Volatile.Read(ref MaxZ)[slot], aabb.MaxZ);

            // Always a write, so always a full fence: the protocol's fence on this side (see _growth). Do not skip it when the bits are already set.
            Interlocked.Or(ref Volatile.Read(ref CategoryMasks)[slot], aabb.CategoryMask);
            if (Volatile.Read(ref _growth) == stamp)
            {
                return;
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private int WaitOutGrow()
    {
        WidenWaitProbe?.Invoke();
        var spin = new SpinWait();
        int stamp;
        while (((stamp = Volatile.Read(ref _growth)) & 1) != 0)
        {
            spin.SpinOnce();
        }

        return stamp;
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
        // Odd for the whole copy: a WidenAt whose CAS the copy may have missed sees the stamp move and redoes it in the new arrays.
        Interlocked.Increment(ref _growth);
        try
        {
            var minX = Grown(MinX, newCapacity);
            var minY = Grown(MinY, newCapacity);
            var minZ = Grown(MinZ, newCapacity);
            var maxX = Grown(MaxX, newCapacity);
            var maxY = Grown(MaxY, newCapacity);
            var maxZ = Grown(MaxZ, newCapacity);
            var categoryMasks = Grown(CategoryMasks, newCapacity);
            var clusterIds = Grown(ClusterIds, newCapacity);
            GrowCopiedProbe?.Invoke();

            // Release stores, the bound arrays before ClusterIds.
            Volatile.Write(ref MinX, minX);
            Volatile.Write(ref MinY, minY);
            Volatile.Write(ref MinZ, minZ);
            Volatile.Write(ref MaxX, maxX);
            Volatile.Write(ref MaxY, maxY);
            Volatile.Write(ref MaxZ, maxZ);
            Volatile.Write(ref CategoryMasks, categoryMasks);
            Volatile.Write(ref ClusterIds, clusterIds);
        }
        finally
        {
            Interlocked.Increment(ref _growth);
        }
    }

    private static T[] Grown<T>(T[] source, int newCapacity)
    {
        var grown = new T[newCapacity];
        Array.Copy(source, grown, source.Length);
        return grown;
    }
}
