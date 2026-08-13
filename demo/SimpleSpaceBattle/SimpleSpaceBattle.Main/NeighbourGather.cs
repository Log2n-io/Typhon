using System.Runtime.CompilerServices;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace SimpleSpaceBattle;

/// <summary>
/// A per-worker SoA scratch buffer holding the neighbourhood of one cluster, <b>bucketed into a uniform bin grid</b>
/// so each ship sweeps only the bins its own sphere touches.
///
/// <para><b>Why gather.</b> The obvious implementation runs one spatial query per ship — 50 000 per tick. Measured,
/// that cost ~60 ms, because the engine's narrowphase is not free per candidate: every hit goes through
/// <c>SpatialMaintainer.ReadAndValidateBoundsFromPtr</c> into a <c>double[6]</c> scratch, then back to float, inside
/// an enumerator state machine. One query per <i>cluster</i> drops that to ~1 200 queries, a ~27× reduction in the
/// expensive part.</para>
///
/// <para><b>Why bin.</b> Sharing one query across a cluster has a cost: the gather box is the cluster's AABB expanded
/// by the scan range, and because ships are assigned to clusters by slot availability rather than position, that AABB
/// spans essentially the whole cell. At <c>cellSize 50</c>, <c>worldZ 200</c>, <c>scanRange 50</c> the box is
/// 150×150×300 and holds ~1 687 candidates — while any single ship needs the ~131 inside its own 50-unit sphere. A
/// flat sweep therefore threw away ~92 % of its work.</para>
///
/// <para>Binning at <c>b = 25</c> reduces the examined volume from the full box to <c>(2R + b)³</c> — 488 candidates
/// instead of 1 687, a <b>3.5×</b> cut. The floor is 250 (the axis-aligned box circumscribing the sphere), so this
/// recovers most of what is available; the residual ~1.9× is the box-vs-sphere ratio and is not removable with
/// axis-aligned tests.</para>
///
/// <para>One instance per worker, reused across clusters and ticks — no allocation in the tick loop after warm-up.</para>
/// </summary>
internal sealed class NeighbourGather
{
    /// <summary>
    /// Bin edge length. Examined volume is <c>(2R + b)³</c>, so smaller is strictly better on candidate count and
    /// strictly worse on per-ship bin bookkeeping. 25 sits near the knee at <c>R = 50</c>: 488 candidates across 125
    /// bins visited as 25 contiguous runs.
    /// </summary>
    private const float BinSize = 25f;

    /// <summary>Cap per axis so a pathologically large cluster AABB cannot explode the bin array.</summary>
    private const int MaxBinsPerAxis = 64;

    // Raw gather staging, in query order.
    private float[] _rawX = new float[2048];
    private float[] _rawY = new float[2048];
    private float[] _rawZ = new float[2048];
    private long[] _rawId = new long[2048];
    private long[] _rawTarget = new long[2048];
    private int[] _rawBin = new int[2048];

    // Bin-ordered output — what the sweep reads.
    private float[] _x = new float[2048];
    private float[] _y = new float[2048];
    private float[] _z = new float[2048];
    private long[] _id = new long[2048];
    private long[] _target = new long[2048];

    private int[] _binStart = new int[1024];
    private int[] _cursor = new int[1024];

    public int Count { get; private set; }

    public float[] X => _x;

    public float[] Y => _y;

    public float[] Z => _z;

    public long[] Id => _id;

    /// <summary>Each candidate's published target, resolved from the lane at gather time (see <see cref="TargetLane"/>).</summary>
    public long[] Target => _target;

    /// <summary>Prefix offsets, length <c>BinCount + 1</c>. Bin <c>i</c> owns <c>[BinStart[i], BinStart[i+1])</c>.</summary>
    public int[] BinStart => _binStart;

    public float OriginX { get; private set; }

    public float OriginY { get; private set; }

    public float OriginZ { get; private set; }

    public int BinsX { get; private set; }

    public int BinsY { get; private set; }

    public int BinsZ { get; private set; }

    public float InverseBinSize { get; private set; }

    /// <summary>
    /// Fill from one spatial query covering <paramref name="bounds"/> expanded by <paramref name="scanRange"/>, then
    /// counting-sort the hits into the bin grid. Every ship in the cluster lies inside <paramref name="bounds"/>, so
    /// the expanded box is a superset of each of their individual neighbourhoods — which is what makes one query
    /// serve all of them.
    /// </summary>
    public void Fill(
        DatabaseEngine dbe,
        in ClusterSpatialAabb bounds,
        float scanRange,
        long[] laneBacking,
        int clusterSize)
        => FillBox(dbe, bounds.MinX, bounds.MinY, bounds.MinZ, bounds.MaxX, bounds.MaxY, bounds.MaxZ,
            scanRange, laneBacking, clusterSize);

    /// <summary>
    /// Fill from an explicit box expanded by <paramref name="scanRange"/>.
    ///
    /// <para>Callers pass the <b>cell's</b> extent rather than one cluster's AABB. A cell holds ~3 clusters whose
    /// AABBs are all approximately the cell (ships are assigned to clusters by slot availability, not position —
    /// DESIGN.md §3.2), so gathering per cluster ran the same query ~3× over. Keying on the cell collapses that.</para>
    ///
    /// <para>Correctness: every ship of every cluster in the cell lies inside the cell extent — including ships up to
    /// <c>MigrationHysteresisRatio × cellSize</c> outside it, which the ±<paramref name="scanRange"/> expansion
    /// covers many times over (1.25 units against a 50-unit scan).</para>
    /// </summary>
    public void FillBox(
        DatabaseEngine dbe,
        float boxMinX, float boxMinY, float boxMinZ,
        float boxMaxX, float boxMaxY, float boxMaxZ,
        float scanRange,
        long[] laneBacking,
        int clusterSize)
    {
        float minX = boxMinX - scanRange;
        float minY = boxMinY - scanRange;
        float minZ = boxMinZ - scanRange;
        float maxX = boxMaxX + scanRange;
        float maxY = boxMaxY + scanRange;
        float maxZ = boxMaxZ + scanRange;

        var box = new AABB3F { MinX = minX, MinY = minY, MinZ = minZ, MaxX = maxX, MaxY = maxY, MaxZ = maxZ };

        // ── 1. Gather, in query order ──────────────────────────────────────
        int laneLength = laneBacking.Length;
        int n = 0;

        foreach (ClusterSpatialQueryResult hit in dbe.ClusterSpatialQuery<Ship>().AABB(in box))
        {
            if (n == _rawX.Length)
            {
                GrowRaw();
            }

            _rawX[n] = hit.MinX;
            _rawY[n] = hit.MinY;
            _rawZ[n] = hit.MinZ;
            _rawId[n] = hit.EntityId;

            int laneIndex = hit.ClusterChunkId * clusterSize + hit.SlotIndex;
            _rawTarget[n] = (uint)laneIndex < (uint)laneLength ? laneBacking[laneIndex] : TargetingComponent.Unlocked;

            n++;
        }

        Count = n;
        if (n == 0)
        {
            BinsX = BinsY = BinsZ = 0;
            return;
        }

        // ── 2. Grid geometry ───────────────────────────────────────────────
        OriginX = minX;
        OriginY = minY;
        OriginZ = minZ;
        BinsX = AxisBins(maxX - minX);
        BinsY = AxisBins(maxY - minY);
        BinsZ = AxisBins(maxZ - minZ);
        InverseBinSize = 1f / BinSize;

        int binCount = BinsX * BinsY * BinsZ;
        if (_binStart.Length < binCount + 1)
        {
            _binStart = new int[binCount + 1];
            _cursor = new int[binCount + 1];
        }

        Array.Clear(_cursor, 0, binCount + 1);

        // ── 3. Count per bin ───────────────────────────────────────────────
        for (int i = 0; i < n; i++)
        {
            int bin = BinOf(_rawX[i], _rawY[i], _rawZ[i]);
            _rawBin[i] = bin;
            _cursor[bin]++;
        }

        // ── 4. Prefix sum ──────────────────────────────────────────────────
        int running = 0;
        for (int b = 0; b < binCount; b++)
        {
            _binStart[b] = running;
            running += _cursor[b];
            _cursor[b] = _binStart[b];
        }

        _binStart[binCount] = running;

        // ── 5. Scatter into bin order ──────────────────────────────────────
        if (_x.Length < n)
        {
            GrowSorted(n);
        }

        for (int i = 0; i < n; i++)
        {
            int dst = _cursor[_rawBin[i]]++;
            _x[dst] = _rawX[i];
            _y[dst] = _rawY[i];
            _z[dst] = _rawZ[i];
            _id[dst] = _rawId[i];
            _target[dst] = _rawTarget[i];
        }
    }

    /// <summary>
    /// The inclusive bin window covering a sphere of <paramref name="radius"/> at the given point. The caller sweeps
    /// <c>[x0,x1]</c> as one contiguous candidate run per <c>(z,y)</c> pair, because bins are x-major.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Window(
        float x, float y, float z, float radius,
        out int x0, out int x1, out int y0, out int y1, out int z0, out int z1)
    {
        x0 = ClampBin((x - radius - OriginX) * InverseBinSize, BinsX);
        x1 = ClampBin((x + radius - OriginX) * InverseBinSize, BinsX);
        y0 = ClampBin((y - radius - OriginY) * InverseBinSize, BinsY);
        y1 = ClampBin((y + radius - OriginY) * InverseBinSize, BinsY);
        z0 = ClampBin((z - radius - OriginZ) * InverseBinSize, BinsZ);
        z1 = ClampBin((z + radius - OriginZ) * InverseBinSize, BinsZ);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int BinOf(float x, float y, float z)
    {
        int bx = ClampBin((x - OriginX) * InverseBinSize, BinsX);
        int by = ClampBin((y - OriginY) * InverseBinSize, BinsY);
        int bz = ClampBin((z - OriginZ) * InverseBinSize, BinsZ);
        return (bz * BinsY + by) * BinsX + bx;
    }

    /// <summary>
    /// Truncating cast plus clamp. The inputs are non-negative by construction — every ship lies inside the cluster
    /// AABB, so <c>value - radius</c> is at or above the box minimum — but a hit sitting exactly on the upper face
    /// rounds to <c>bins</c>, so the clamp is load-bearing rather than defensive.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ClampBin(float scaled, int bins)
    {
        int v = (int)scaled;
        if (v < 0)
        {
            return 0;
        }

        return v >= bins ? bins - 1 : v;
    }

    private static int AxisBins(float extent)
    {
        int bins = (int)(extent / BinSize) + 1;
        return bins < 1 ? 1 : bins > MaxBinsPerAxis ? MaxBinsPerAxis : bins;
    }

    private void GrowRaw()
    {
        int next = _rawX.Length * 2;
        Array.Resize(ref _rawX, next);
        Array.Resize(ref _rawY, next);
        Array.Resize(ref _rawZ, next);
        Array.Resize(ref _rawId, next);
        Array.Resize(ref _rawTarget, next);
        Array.Resize(ref _rawBin, next);
    }

    private void GrowSorted(int required)
    {
        int next = _x.Length;
        while (next < required)
        {
            next *= 2;
        }

        _x = new float[next];
        _y = new float[next];
        _z = new float[next];
        _id = new long[next];
        _target = new long[next];
    }
}
