using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// <c>AC-9.9</c> — the crossover sweep. Measures the per-cell R-Tree against the linear SoA scan it is proposed to replace, over cluster count and query
/// selectivity, for both the query side and the update side (#872 step 9).
/// </summary>
/// <remarks>
/// <para><b>Why this runs before the tree is wired in, not after.</b> Wiring <see cref="CellClusterTree"/> into production is a 47-site change across seven
/// files, and nothing yet shows the tree wins at the cluster counts this engine actually runs. <see cref="CellSpatialIndex"/>'s own class doc puts AntHill's
/// dense zones at <b>&#8804;80 clusters per cell</b>, while §4.1's 2-9x prediction is at <b>C = 1563</b>. If the crossover sits between those two numbers, the
/// honest answer is a hybrid keyed on cluster count, not a wholesale replacement — and that is a conclusion best reached before the churn, not after.</para>
/// <para><b>Both sides of the ledger, because they move in opposite directions.</b> The tree prunes, so queries get cheaper as C grows. But maintenance gets
/// DEARER: the linear index's <c>UpdateAt</c> is six float stores into SoA, while the tree must unpack a handle, resolve a chunk address, check leaf-ness and
/// payload identity, test containment on six axes, take a latch — and on the changes that escape their leaf, remove and reinsert instead. A query-only
/// benchmark would therefore report a win that a real tick does not see. The crossover is a surface over (C, selectivity, query:update ratio), and this
/// fixture measures the first two directly so the third is arithmetic.</para>
/// <para><b>Cluster size scales with 1/sqrt(C) on purpose.</b> A cluster holds a roughly fixed number of entities, so a cell with more clusters has SMALLER
/// ones — the clusters tile the cell rather than growing to fill it. Holding cluster extent constant while raising C would instead model every cluster
/// overlapping every query, which is the one regime where a tree cannot prune, and would rig the result against it.</para>
/// <para><b><see cref="ExplicitAttribute"/> — this is an instrument, not a gate.</b> It reports numbers and asserts only that the two structures agree on what
/// they found; a timing threshold in the suite would be a machine-specific guess that reddens on a busy box. Run it deliberately:</para>
/// <code>
/// dotnet test test/Typhon.Engine.Tests/Typhon.Engine.Tests.csproj -c Release --filter "FullyQualifiedName~BroadphaseCrossoverSweepTests"
/// </code>
/// </remarks>
[TestFixture]
[Explicit("Measurement instrument for AC-9.9 — run deliberately in Release, not as part of the suite.")]
// Manual rather than Nightly, deliberately. What this fixture produces is wall-clock timings whose whole purpose is to be COMPARED, and a shared CI runner
// contends for cache and cores in ways that move them by more than the effects being measured — a nightly series of numbers nobody can trust is worse than no
// series, because it invites conclusions. It also takes ~2 minutes, and its only assertions are that the two structures agree, which the differential fixture
// already gates on every run. Re-run it when the decision it informs is revisited: a change to the tree's fan-out, split heuristic, or update path.
[Category("Manual")]
unsafe class BroadphaseCrossoverSweepTests
{
    private IServiceProvider _serviceProvider;
    private string _dir;

    /// <summary>Cell edge in cell-relative units. Arbitrary but fixed — every other extent in the sweep is expressed as a fraction of it.</summary>
    private const float CellExtent = 100f;

    [SetUp]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "Typhon.Tests", "BroadphaseCrossover");
        Directory.CreateDirectory(_dir);

        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddScopedManagedPagedMemoryMappedFile(o =>
            {
                o.DatabaseName = "BPXover";
                o.DatabaseDirectory = _dir;
                o.DatabaseCacheSize = 8192UL * PagedMMF.PageSize;
                o.TestMode = true;
            });
        _serviceProvider = sc.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
    }

    [TearDown]
    public void TearDown() => (_serviceProvider as IDisposable)?.Dispose();

    private ChunkBasedSegment<TransientStore> CreateSegment(int startingPages)
    {
        var em = _serviceProvider.GetRequiredService<EpochManager>();
        var allocator = _serviceProvider.GetRequiredService<IMemoryAllocator>();
        var registry = _serviceProvider.GetRequiredService<IResourceRegistry>();
        var desc = SpatialNodeDescriptor.ForVariant(CellClusterTree.Variant);

        var store = new TransientStore(new TransientOptions(), allocator, em, registry.Root);
        Span<int> pages = stackalloc int[startingPages];
        store.AllocatePages(ref pages, 0, null);
        var segment = new ChunkBasedSegment<TransientStore>(em, store, desc.Stride);
        segment.Create(PageBlockType.None, StorageSegmentKind.Cluster, pages, false);
        return segment;
    }

    private static ClusterSpatialAabb Box(float minX, float minY, float maxX, float maxY) => new()
    {
        MinX = minX,
        MinY = minY,
        MinZ = float.PositiveInfinity,
        MaxX = maxX,
        MaxY = maxY,
        MaxZ = float.NegativeInfinity,
        CategoryMask = uint.MaxValue,
    };

    /// <summary>
    /// The enumerator's broadphase loop, lifted verbatim minus the narrowphase. Category filtering is skipped on both sides (mask 0 = no filter), so what is
    /// compared is exactly the AABB overlap work.
    /// </summary>
    private static int ScanLinear(CellSpatialIndex idx, float minX, float minY, float maxX, float maxY)
    {
        int checksum = 0;
        int count = idx.ClusterCount;
        var aMinX = idx.MinX;
        var aMinY = idx.MinY;
        var aMaxX = idx.MaxX;
        var aMaxY = idx.MaxY;
        var ids = idx.ClusterIds;
        for (int i = 0; i < count; i++)
        {
            if (aMaxX[i] < minX || aMinX[i] > maxX) { continue; }
            if (aMaxY[i] < minY || aMinY[i] > maxY) { continue; }
            checksum += ids[i];
        }
        return checksum;
    }

    private static int ScanTree(CellClusterTree tree, float minX, float minY, float maxX, float maxY)
    {
        Span<double> coords = stackalloc double[6];
        CellClusterTree.QueryToCoords(minX, minY, float.NegativeInfinity, maxX, maxY, float.PositiveInfinity, coords);
        int checksum = 0;
        foreach (var r in tree.Query(coords))
        {
            checksum += (int)r.PayloadId;
        }
        return checksum;
    }

    /// <summary>Run <paramref name="body"/> enough times to fill roughly <paramref name="targetMs"/>, after a warmup, and return nanoseconds per call.</summary>
    private static double TimeNs(Func<int> body, int targetMs, out int checksum)
    {
        checksum = 0;
        for (int i = 0; i < 32; i++) { checksum ^= body(); }

        // Calibrate: grow the rep count until one batch is long enough to dwarf timer granularity.
        int reps = 64;
        while (true)
        {
            long t0 = Stopwatch.GetTimestamp();
            for (int i = 0; i < reps; i++) { checksum ^= body(); }
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            if (ms >= targetMs || reps >= (1 << 22)) { break; }
            reps *= 4;
        }

        // Best of five: the minimum is the least contaminated by the scheduler, which is what is wanted when comparing two structures against each other
        // rather than modelling a production tick.
        double bestNs = double.MaxValue;
        for (int pass = 0; pass < 5; pass++)
        {
            long t0 = Stopwatch.GetTimestamp();
            for (int i = 0; i < reps; i++) { checksum ^= body(); }
            double ns = (Stopwatch.GetTimestamp() - t0) * 1_000_000_000.0 / Stopwatch.Frequency / reps;
            if (ns < bestNs) { bestNs = ns; }
        }
        return bestNs;
    }

    [Test]
    [CancelAfter(1_800_000)]
    public void Crossover_QueryAndUpdate_AcrossClusterCountAndSelectivity()
    {
        int[] clusterCounts = [8, 32, 80, 200, 512, 1563, 6250, 15625];
        float[] selectivities = [0.02f, 0.10f, 0.30f, 1.00f];

        var em = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(em);
        try
        {
            TestContext.Out.WriteLine("XOVER-QUERY  C=clusters/cell  sel=query edge as fraction of cell edge  hits=clusters returned");
            TestContext.Out.WriteLine($"XOVER-QUERY  {"C",6} {"sel",6} {"hits",7} {"linear ns",11} {"tree ns",11} {"speedup",9}  verdict");

            foreach (int c in clusterCounts)
            {
                // Cluster edge shrinks as 1/sqrt(C): a cluster holds a fixed entity count, so a denser cell has smaller clusters, not bigger ones.
                float clusterEdge = CellExtent / MathF.Sqrt(c) * 1.5f;

                var oracle = new CellSpatialIndex(Math.Max(16, c));
                var segment = CreateSegment(c >= 6250 ? 128 : 16);
                var backPointers = new int[c + 2];
                Array.Fill(backPointers, SpatialRTree<TransientStore>.NullHandle);
                var tree = new CellClusterTree(segment, backPointers);

                var rng = new Random(4242);
                var cx = new float[c];
                var cy = new float[c];
                for (int i = 0; i < c; i++)
                {
                    cx[i] = (float)rng.NextDouble() * (CellExtent - clusterEdge);
                    cy[i] = (float)rng.NextDouble() * (CellExtent - clusterEdge);
                    var box = Box(cx[i], cy[i], cx[i] + clusterEdge, cy[i] + clusterEdge);
                    oracle.Add(i + 1, in box);
                    tree.Add(i + 1, in box);
                }

                foreach (float sel in selectivities)
                {
                    float qEdge = CellExtent * sel;

                    // One fixed query per (C, sel) point, centred, so linear and tree answer exactly the same question and the checksums are comparable. A
                    // moving query would fold query-generation noise into the measurement.
                    float qMin = (CellExtent - qEdge) * 0.5f;
                    float qMax = qMin + qEdge;

                    // Agreement is checked ONCE, on id SETS, outside the timing loop.
                    //
                    // It used to be a checksum XORed inside TimeNs, and that assertion was 0 == 0 — always, on both sides. Every phase of TimeNs runs an EVEN
                    // number of calls (32 warmup, 64/256/1024… calibration, 5 x reps), the scans are pure, so XOR-ing one constant an even number of times
                    // cancels to zero whatever the scans return. Making ScanTree return nothing at all would have passed, and reported the tree as infinitely
                    // fast. Two further reasons it could not have worked: the two sides calibrate to different rep counts, so even the parities were not
                    // guaranteed to match, and the per-call value was a SUM of ids, which does not identify a set.
                    //
                    // This matters more than a normal test bug: these timings are what decided a 47-site design question, and the fixture header cites this
                    // assertion as the thing keeping them honest.
                    var linHits = CollectLinear(oracle, qMin, qMin, qMax, qMax);
                    var treeHits = CollectTree(tree, qMin, qMin, qMax, qMax);
                    Assert.That(treeHits, Is.EquivalentTo(linHits),
                        $"tree and linear scan disagreed at C={c} sel={sel} — the timings below would be comparing different work");

                    double linNs = TimeNs(() => ScanLinear(oracle, qMin, qMin, qMax, qMax), 120, out _);
                    double treeNs = TimeNs(() => ScanTree(tree, qMin, qMin, qMax, qMax), 120, out _);

                    int hits = linHits.Count;
                    double speedup = linNs / treeNs;
                    string verdict = speedup >= 1.15 ? "TREE" : speedup <= 0.87 ? "linear" : "wash";
                    TestContext.Out.WriteLine($"XOVER-QUERY  {c,6} {sel,6:F2} {hits,7} {linNs,11:F1} {treeNs,11:F1} {speedup,8:F2}x  {verdict}");
                }

                MeasureUpdates(c, clusterEdge, oracle, tree, cx, cy);
                foreach (float margin in (float[])[0.25f, 0.50f, 1.00f])
                {
                    MeasureFatUpdates(c, clusterEdge, tree, cx, cy, margin);
                }
                TestContext.Out.WriteLine("");
                segment.Dispose();
            }
        }
        finally
        {
            guard.Dispose();
        }
    }

    /// <summary>The cluster ids the linear scan returns — the set the tree must match, collected once and outside the timed region.</summary>
    private static HashSet<int> CollectLinear(CellSpatialIndex idx, float minX, float minY, float maxX, float maxY)
    {
        var hits = new HashSet<int>();
        for (int i = 0; i < idx.ClusterCount; i++)
        {
            if (idx.MaxX[i] < minX || idx.MinX[i] > maxX) { continue; }
            if (idx.MaxY[i] < minY || idx.MinY[i] > maxY) { continue; }
            hits.Add(idx.ClusterIds[i]);
        }
        return hits;
    }

    /// <inheritdoc cref="CollectLinear"/>
    private static HashSet<int> CollectTree(CellClusterTree tree, float minX, float minY, float maxX, float maxY)
    {
        Span<double> coords = stackalloc double[6];
        CellClusterTree.QueryToCoords(minX, minY, float.NegativeInfinity, maxX, maxY, float.PositiveInfinity, coords);
        var hits = new HashSet<int>();
        foreach (var r in tree.Query(coords))
        {
            hits.Add((int)r.PayloadId);
        }
        return hits;
    }

    private static int ScanCount(CellSpatialIndex idx, float minX, float minY, float maxX, float maxY)
    {
        int n = 0;
        for (int i = 0; i < idx.ClusterCount; i++)
        {
            if (idx.MaxX[i] < minX || idx.MinX[i] > maxX) { continue; }
            if (idx.MaxY[i] < minY || idx.MinY[i] > maxY) { continue; }
            n++;
        }
        return n;
    }

    /// <summary>
    /// The same churn, but with the stored bound FATTENED and the tree left alone while the true box stays inside it — the classic broadphase hysteresis, and
    /// the one lever that can move the update side of the ledger.
    /// </summary>
    /// <remarks>
    /// <para>A fat bound stays a valid conservative bound, so <c>CA-01</c> is untouched: the stored box always contains the cluster. What it buys is that a
    /// cluster which merely drifts produces NO tree call at all — not a cheap one, none — and what it costs is broadphase false positives, which the
    /// narrowphase already filters because a cluster bound is only ever a hint. The measurement reports the skip rate and the per-move cost amortised over
    /// every move, including the skipped ones, because that is the number a tick actually pays.</para>
    /// <para>Reported for the tree only. The linear index writes six floats in ~21 ns, so hysteresis can save it almost nothing — which is precisely why this
    /// lever changes the comparison rather than shifting both sides equally.</para>
    /// </remarks>
    private static void MeasureFatUpdates(int c, float clusterEdge, CellClusterTree tree, float[] cx, float[] cy, float margin)
    {
        int moved = Math.Max(1, c * 30 / 100);
        float step = clusterEdge * 0.25f;
        float fat = clusterEdge * margin;

        // Seed every cluster's stored bound with its fattened current box, so the first round is not an artificial burst of escapes.
        var sMinX = new float[c];
        var sMinY = new float[c];
        var sMaxX = new float[c];
        var sMaxY = new float[c];
        for (int i = 0; i < c; i++)
        {
            sMinX[i] = cx[i] - fat;
            sMinY[i] = cy[i] - fat;
            sMaxX[i] = cx[i] + clusterEdge + fat;
            sMaxY[i] = cy[i] + clusterEdge + fat;
            tree.UpdateAt(i + 1, Box(sMinX[i], sMinY[i], sMaxX[i], sMaxY[i]), out _);
        }

        long skipped = 0;
        long escapes = 0;
        long total = 0;
        var rng = new Random(777);
        double ns = TimeNs(() =>
        {
            for (int k = 0; k < moved; k++)
            {
                int i = rng.Next(c);
                float nx = Math.Clamp(cx[i] + (((float)rng.NextDouble() - 0.5f) * step), 0f, CellExtent - clusterEdge);
                float ny = Math.Clamp(cy[i] + (((float)rng.NextDouble() - 0.5f) * step), 0f, CellExtent - clusterEdge);
                cx[i] = nx;
                cy[i] = ny;
                total++;

                // The true box still inside the stored fat one: the tree already holds a bound that contains this cluster, so there is nothing to do.
                if (nx >= sMinX[i] && ny >= sMinY[i] && nx + clusterEdge <= sMaxX[i] && ny + clusterEdge <= sMaxY[i])
                {
                    skipped++;
                    continue;
                }

                sMinX[i] = nx - fat;
                sMinY[i] = ny - fat;
                sMaxX[i] = nx + clusterEdge + fat;
                sMaxY[i] = ny + clusterEdge + fat;
                tree.UpdateAt(i + 1, Box(sMinX[i], sMinY[i], sMaxX[i], sMaxY[i]), out bool escaped);
                if (escaped) { escapes++; }
            }
            return moved;
        }, 120, out _) / moved;

        double skipRate = total == 0 ? 0 : 100.0 * skipped / total;
        double escRate = total == skipped ? 0 : 100.0 * escapes / (total - skipped);
        TestContext.Out.WriteLine($"XOVER-FAT    C={c,6}  margin {margin,4:F2}  tree {ns,8:F1} ns/move (amortised)   "
            + $"skipped {skipRate,5:F1}%   escapes-of-remainder {escRate,5:F1}%");
    }

    /// <summary>
    /// The other half of the ledger: what one tick of AABB churn costs each structure. A round moves 30% of the clusters by a small random step, which is
    /// <c>Q2</c>'s stated workload shape, and the escape count is reported alongside so the timing can be read against the rate that produced it.
    /// </summary>
    /// <remarks>
    /// <b>The walk accumulates.</b> Jittering each cluster around a FIXED anchor is the tempting shape and it silently rigs the result: total displacement is
    /// then bounded by half a step, so any hysteresis margin wider than that can never be escaped and the fat-bound measurement reports 100% skipped no matter
    /// what the tree does. A clamped random walk is also stationary — its steady-state distribution over the cell is the uniform one it started from — so the
    /// timings stay comparable across however many reps the calibration settles on.
    /// </remarks>
    private static void MeasureUpdates(int c, float clusterEdge, CellSpatialIndex oracle, CellClusterTree tree, float[] cx, float[] cy)
    {
        int moved = Math.Max(1, c * 30 / 100);
        float step = clusterEdge * 0.25f;

        var rngL = new Random(777);
        double linNs = TimeNs(() =>
        {
            for (int k = 0; k < moved; k++)
            {
                int i = rngL.Next(c);
                float nx = Math.Clamp(cx[i] + (((float)rngL.NextDouble() - 0.5f) * step), 0f, CellExtent - clusterEdge);
                float ny = Math.Clamp(cy[i] + (((float)rngL.NextDouble() - 0.5f) * step), 0f, CellExtent - clusterEdge);
                cx[i] = nx;
                cy[i] = ny;
                var box = Box(nx, ny, nx + clusterEdge, ny + clusterEdge);
                oracle.UpdateAt(i, in box);
            }
            return moved;
        }, 120, out _) / moved;

        long escapes = 0;
        long total = 0;
        var rngT = new Random(777);
        double treeNs = TimeNs(() =>
        {
            for (int k = 0; k < moved; k++)
            {
                int i = rngT.Next(c);
                float nx = Math.Clamp(cx[i] + (((float)rngT.NextDouble() - 0.5f) * step), 0f, CellExtent - clusterEdge);
                float ny = Math.Clamp(cy[i] + (((float)rngT.NextDouble() - 0.5f) * step), 0f, CellExtent - clusterEdge);
                cx[i] = nx;
                cy[i] = ny;
                var box = Box(nx, ny, nx + clusterEdge, ny + clusterEdge);
                tree.UpdateAt(i + 1, in box, out bool escaped);
                total++;
                if (escaped) { escapes++; }
            }
            return moved;
        }, 120, out _) / moved;

        double escapeRate = total == 0 ? 0 : 100.0 * escapes / total;
        TestContext.Out.WriteLine($"XOVER-UPDATE C={c,6}  linear {linNs,8:F1} ns/update   tree {treeNs,8:F1} ns/update   "
            + $"ratio {treeNs / linNs,6:F1}x dearer   escapes {escapeRate,5:F1}%");
    }
}
