using System;
using System.Diagnostics;
using System.Numerics;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Typhon.Engine;
using Typhon.Engine.Internals;

namespace Typhon.Benchmark;

/// <summary>
/// A profiling target for the per-cell broadphase: one cell holding <c>C</c> clusters, queried in a tight loop through either the
/// R-Tree (<see cref="CellClusterTree"/>) or the linear SoA scan (<see cref="CellSpatialIndex"/>) it competes with.
/// </summary>
/// <remarks>
/// <para><b>Why a bespoke target rather than the existing query benchmark.</b> <c>ClusterSpatialQueryBenchmarks</c> measures the whole
/// engine-level query — cell walk, broadphase, narrowphase, entity reads — where the broadphase is a small slice of a much larger call
/// tree. Under dotTrace Tracing every one of those frames is instrumented, so the thing under study would be buried. This target holds
/// exactly the two structures and the query, so the trace's self-time ranking IS the broadphase's cost breakdown.</para>
/// <para><b>Tracing, not sampling.</b> A whole tree query is ~200 ns and the per-entry costs inside it are single-digit nanoseconds —
/// far under a sampling profiler's resolution. Tracing instruments every call, so the CALL COUNTS are exact and the ranking is real,
/// at the price of absolute times inflated by the instrumentation (and by the inlining it suppresses: <c>ReadLeafCoord</c> and
/// <c>GetChunkAddress</c> are <c>AggressiveInlining</c> in a normal build and only appear as frames because tracing kept them).
/// Read the counts as fact and the nanoseconds as a ranking, not as a budget.</para>
/// </remarks>
static class BroadphaseQueryProfile
{
    /// <summary>Cell edge in cell-relative units. Every other extent is a fraction of it, matching <c>BroadphaseCrossoverSweepTests</c>.</summary>
    private const float CellExtent = 100f;

    internal static void Run(string[] args)
    {
        if (Array.IndexOf(args, "--matrix") >= 0)
        {
            RunMatrix(args);
            return;
        }

        RunSingle(args);
    }

    /// <summary>One arm of the campaign: a name and the switch settings that define it.</summary>
    private readonly struct Arm
    {
        internal readonly string Name;
        internal readonly bool Hoist;
        internal readonly bool Gate;
        internal readonly bool Contained;
        internal readonly bool Simd;
        internal readonly bool SimdInternal;
        internal readonly bool FloatBox;

        internal Arm(string name, bool hoist, bool gate, bool contained, bool simd, bool simdInternal, bool floatBox)
        {
            Name = name;
            Hoist = hoist;
            Gate = gate;
            Contained = contained;
            Simd = simd;
            SimdInternal = simdInternal;
            FloatBox = floatBox;
        }

        internal void Apply()
        {
            SpatialQueryTuning.HoistLeafBase = Hoist;
            SpatialQueryTuning.GateQuerySpan = Gate;
            SpatialQueryTuning.FullyContained = Contained;
            SpatialQueryTuning.SimdLeafScan = Simd;
            SpatialQueryTuning.SimdInternalScan = SimdInternal;
            SpatialQueryTuning.DirectFloatBox = FloatBox;
        }
    }

    /// <summary>
    /// The whole campaign in ONE process: every arm against every (C, selectivity) point, arms INTERLEAVED inside each
    /// round rather than batched.
    /// </summary>
    /// <remarks>
    /// <para>Batching an arm's runs together and comparing batch against batch drifts with whatever else the box is doing —
    /// the repo's A/B doctrine, and the reason a four-point three-round sweep used to be twelve <c>dotnet run</c> launches
    /// at ~35 s each. Interleaved in one process, JIT is paid once and the whole matrix lands in seconds.</para>
    /// <para>Every arm answers the same fixed query box and its hit count is asserted equal to the baseline's, because a
    /// timing that comes from doing less work is not a speed-up (SQ-01).</para>
    /// </remarks>
    private static void RunMatrix(string[] args)
    {
        int[] clusterCounts = ParseInts(ArgString(args, "--clusters", "4,16,64,512,2048"));
        float[] sels = ParseFloats(ArgString(args, "--sels", "0.02,1.00"));
        int rounds = ArgInt(args, "--rounds", 3);

        Arm[] arms =
        [
            // "orig" is the path before ANY of this work; "prev" is where the previous round left it. The three new
            // levers are read marginally against "prev", which is the only comparison that says what each is worth now.
            new("orig",     hoist: false, gate: false, contained: false, simd: false, simdInternal: false, floatBox: false),
            new("prev",     hoist: true,  gate: true,  contained: true,  simd: true,  simdInternal: false, floatBox: false),
            new("+simdInt", hoist: true,  gate: true,  contained: true,  simd: true,  simdInternal: true,  floatBox: false),
            // simdInternal MUST be on here: the float box is only authoritative when BOTH vector paths are active, so with
            // it off the constructor still fills the f64 array and this arm would measure the extra branch and nothing else.
            new("+fltBox",  hoist: true,  gate: true,  contained: true,  simd: true,  simdInternal: true,  floatBox: true),
            new("new-all",  hoist: true,  gate: true,  contained: true,  simd: true,  simdInternal: true,  floatBox: true),
        ];

        var sp = BuildServices();
        var em = sp.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(em);
        var sw = Stopwatch.StartNew();
        try
        {
            Console.WriteLine($"{"C",6} {"sel",5} {"hits",6} {"scan",8}{"scanSimd",8}{"gain",5} | " + string.Join(" ", Array.ConvertAll(arms, a => $"{a.Name,10}"))
                + $" {"new+brw",10}   best vs scan");
            foreach (var c in clusterCounts)
            {
                var world = BuildWorld(sp, c);
                foreach (var sel in sels)
                {
                    float qEdge = CellExtent * sel;
                    float qMin = (CellExtent - qEdge) * 0.5f;
                    float qMax = qMin + qEdge;

                    int baseline = -1;
                    var ns = new double[arms.Length];
                    for (int a = 0; a < arms.Length; a++)
                    {
                        arms[a].Apply();
                        int hits = ScanTree(world.Tree, qMin, qMin, qMax, qMax);
                        if (baseline < 0)
                        {
                            baseline = hits;
                        }
                        else if (hits != baseline)
                        {
                            throw new InvalidOperationException($"arm '{arms[a].Name}' answered {hits} where base answered {baseline} at C={c} sel={sel} — SQ-01");
                        }

                        ns[a] = double.MaxValue;
                    }

                    // Interleaved rounds: every arm is measured once per round, so a drift in box load hits them all alike.
                    for (int round = 0; round < rounds; round++)
                    {
                        for (int a = 0; a < arms.Length; a++)
                        {
                            arms[a].Apply();
                            double t = TimeNs(() => ScanTree(world.Tree, qMin, qMin, qMax, qMax));
                            if (t < ns[a])
                            {
                                ns[a] = t;
                            }
                        }
                    }

                    // The two borrowed-accessor arms: switches at base, and switches all on.
                    double borrowBaseNs = double.MaxValue;
                    double borrowAllNs = double.MaxValue;
                    for (int round = 0; round < rounds; round++)
                    {
                        arms[0].Apply();
                        borrowBaseNs = Math.Min(borrowBaseNs, TimeNsBorrowed(world.Tree, qMin, qMax));
                        arms[^1].Apply();
                        borrowAllNs = Math.Min(borrowAllNs, TimeNsBorrowed(world.Tree, qMin, qMax));
                    }

                    arms[^1].Apply();
                    if (ScanTreeBorrowedCheck(world.Tree, qMin, qMax) != baseline)
                    {
                        throw new InvalidOperationException($"borrowed-accessor arm disagreed with base at C={c} sel={sel} — SQ-01");
                    }

                    double scanNs = double.MaxValue;
                    double scanSimdNs = double.MaxValue;
                    for (int round = 0; round < rounds; round++)
                    {
                        SpatialQueryTuning.SimdLinearScan = false;
                        scanNs = Math.Min(scanNs, TimeNs(() => ScanLinear(world.Index, qMin, qMin, qMax, qMax)));
                        SpatialQueryTuning.SimdLinearScan = true;
                        scanSimdNs = Math.Min(scanSimdNs, TimeNs(() => ScanLinear(world.Index, qMin, qMin, qMax, qMax)));
                    }

                    SpatialQueryTuning.SimdLinearScan = false;
                    int scanScalarHits = ScanLinear(world.Index, qMin, qMin, qMax, qMax);
                    SpatialQueryTuning.SimdLinearScan = true;
                    if (ScanLinear(world.Index, qMin, qMin, qMax, qMax) != scanScalarHits)
                    {
                        throw new InvalidOperationException($"SIMD linear scan disagreed with the scalar one at C={c} sel={sel} — SQ-01");
                    }

                    var cells = new string[arms.Length];
                    double best = double.MaxValue;
                    for (int a = 0; a < arms.Length; a++)
                    {
                        best = Math.Min(best, ns[a]);
                        cells[a] = a <= 1 ? $"{ns[a],10:F0}" : $"{ns[a],6:F0}{(ns[1] - ns[a]) / ns[1] * 100.0,4:F0}%";
                    }

                    best = Math.Min(best, borrowAllNs);
                    int treeHits = CollectTreeCount(world.Tree, qMin, qMin, qMax, qMax);
                    Console.WriteLine($"{c,6} {sel,5:F2} {treeHits,6} {scanNs,8:F1}{scanSimdNs,8:F1}{(scanNs - scanSimdNs) / scanNs * 100.0,4:F0}% | "
                        + string.Join(" ", cells)
                        + $" {borrowAllNs,6:F0}{(ns[1] - borrowAllNs) / ns[1] * 100.0,4:F0}%"
                        + $"   {scanNs / best,6:F2}x");
                }

                world.Segment.Dispose();
            }

            Console.WriteLine($"\nmatrix of {clusterCounts.Length * sels.Length * arms.Length} arm-points in {sw.Elapsed.TotalSeconds:F1}s "
                + "(ns = best of {rounds} interleaved rounds; % = gain vs base)");
        }
        finally
        {
            new Arm("reset", true, true, true, true, true, true).Apply();
            guard.Dispose();
            (sp as IDisposable)?.Dispose();
        }
    }

    private readonly struct World
    {
        internal readonly CellClusterTree Tree;
        internal readonly CellSpatialIndex Index;
        internal readonly ChunkBasedSegment<TransientStore> Segment;

        internal World(CellClusterTree tree, CellSpatialIndex index, ChunkBasedSegment<TransientStore> segment)
        {
            Tree = tree;
            Index = index;
            Segment = segment;
        }
    }

    private static World BuildWorld(IServiceProvider sp, int clusters)
    {
        float clusterEdge = CellExtent / MathF.Sqrt(clusters) * 1.5f;
        var index = new CellSpatialIndex(Math.Max(16, clusters));
        var segment = CreateSegment(sp, clusters >= 2048 ? 128 : 16);
        var backPointers = new int[clusters + 2];
        Array.Fill(backPointers, SpatialRTree<TransientStore>.NullHandle);
        var tree = new CellClusterTree(segment, backPointers);

        var rng = new Random(4242);
        for (int i = 0; i < clusters; i++)
        {
            float x = (float)rng.NextDouble() * (CellExtent - clusterEdge);
            float y = (float)rng.NextDouble() * (CellExtent - clusterEdge);
            var box = Box(x, y, x + clusterEdge, y + clusterEdge);
            index.Add(i + 1, in box);
            tree.Add(i + 1, in box);
        }

        return new World(tree, index, segment);
    }

    /// <summary>
    /// The borrowed-accessor arm: one accessor created before the loop and reused by every query, which is the shape a real
    /// cell walk has — many small questions against one segment.
    /// </summary>
    /// <remarks>
    /// Timed with its own loop rather than through <see cref="TimeNs"/> because the whole point is that the accessor lives
    /// OUTSIDE the per-query body; passing a delegate that creates one would measure exactly the thing being removed.
    /// </remarks>
    private static double TimeNsBorrowed(CellClusterTree tree, float qMin, float qMax)
    {
        var acc = tree.CreateAccessor();
        try
        {
            int sink = 0;
            for (int i = 0; i < 64; i++)
            {
                sink ^= ScanTreeBorrowed(tree, ref acc, qMin, qMin, qMax, qMax);
            }

            int reps = 256;
            while (true)
            {
                long t0 = Stopwatch.GetTimestamp();
                for (int i = 0; i < reps; i++)
                {
                    sink ^= ScanTreeBorrowed(tree, ref acc, qMin, qMin, qMax, qMax);
                }

                if ((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency >= 20 || reps >= (1 << 20))
                {
                    break;
                }

                reps *= 4;
            }

            long s = Stopwatch.GetTimestamp();
            for (int i = 0; i < reps; i++)
            {
                sink ^= ScanTreeBorrowed(tree, ref acc, qMin, qMin, qMax, qMax);
            }

            GC.KeepAlive(sink);
            return (Stopwatch.GetTimestamp() - s) * 1_000_000_000.0 / Stopwatch.Frequency / reps;
        }
        finally
        {
            acc.Dispose();
        }
    }

    /// <inheritdoc cref="ScanTree"/>
    private static int ScanTreeBorrowed(CellClusterTree tree, ref ChunkAccessor<TransientStore> accessor, float minX, float minY, float maxX, float maxY)
    {
        Span<double> coords = stackalloc double[6];
        CellClusterTree.QueryToCoords(minX, minY, float.NegativeInfinity, maxX, maxY, float.PositiveInfinity, coords);
        int checksum = 0;
        foreach (var r in tree.QueryWith(coords, ref accessor))
        {
            checksum += (int)r.PayloadId;
        }

        return checksum;
    }

    /// <summary>Best-of-N inner timing: calibrate a rep count once, then take the minimum, which is the least scheduler-contaminated.</summary>
    private static double TimeNs(Func<int> body)
    {
        int sink = 0;
        for (int i = 0; i < 64; i++)
        {
            sink ^= body();
        }

        int reps = 256;
        while (true)
        {
            long t0 = Stopwatch.GetTimestamp();
            for (int i = 0; i < reps; i++)
            {
                sink ^= body();
            }

            if ((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency >= 20 || reps >= (1 << 20))
            {
                break;
            }

            reps *= 4;
        }

        long s = Stopwatch.GetTimestamp();
        for (int i = 0; i < reps; i++)
        {
            sink ^= body();
        }

        GC.KeepAlive(sink);
        return (Stopwatch.GetTimestamp() - s) * 1_000_000_000.0 / Stopwatch.Frequency / reps;
    }

    /// <summary>Hit count through the borrowed-accessor path, for the SQ-01 equality check.</summary>
    private static int ScanTreeBorrowedCheck(CellClusterTree tree, float qMin, float qMax)
    {
        var acc = tree.CreateAccessor();
        try
        {
            Span<double> coords = stackalloc double[6];
            CellClusterTree.QueryToCoords(qMin, qMin, float.NegativeInfinity, qMax, qMax, float.PositiveInfinity, coords);
            int checksum = 0;
            foreach (var r in tree.QueryWith(coords, ref acc))
            {
                checksum += (int)r.PayloadId;
            }

            return checksum;
        }
        finally
        {
            acc.Dispose();
        }
    }

    private static int CollectTreeCount(CellClusterTree tree, float minX, float minY, float maxX, float maxY)
    {
        Span<double> coords = stackalloc double[6];
        CellClusterTree.QueryToCoords(minX, minY, float.NegativeInfinity, maxX, maxY, float.PositiveInfinity, coords);
        int n = 0;
        foreach (var _ in tree.Query(coords))
        {
            n++;
        }

        return n;
    }

    private static int[] ParseInts(string csv)
    {
        var parts = csv.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var v = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            v[i] = int.Parse(parts[i], System.Globalization.CultureInfo.InvariantCulture);
        }

        return v;
    }

    private static float[] ParseFloats(string csv)
    {
        var parts = csv.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var v = new float[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            v[i] = float.Parse(parts[i], System.Globalization.CultureInfo.InvariantCulture);
        }

        return v;
    }

    private static IServiceProvider BuildServices()
    {
        var dir = Path.Combine(Path.GetTempPath(), "Typhon.Bench", "BroadphaseProfile");
        Directory.CreateDirectory(dir);
        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddScopedManagedPagedMemoryMappedFile(o =>
            {
                o.DatabaseName = "BPProfile";
                o.DatabaseDirectory = dir;
                o.DatabaseCacheSize = 8192UL * PagedMMF.PageSize;
                o.TestMode = true;
            });
        var sp = sc.BuildServiceProvider();
        sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
        return sp;
    }

    static void RunSingle(string[] args)
    {
        int clusters = ArgInt(args, "--clusters", 512);
        float sel = ArgFloat(args, "--sel", 0.02f);
        int reps = ArgInt(args, "--reps", 200_000);
        string arm = ArgString(args, "--arm", "both");

        var sp = BuildServices();
        var em = sp.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(em);
        try
        {
            float clusterEdge = CellExtent / MathF.Sqrt(clusters) * 1.5f;
            var oracle = new CellSpatialIndex(Math.Max(16, clusters));
            var segment = CreateSegment(sp, clusters >= 2048 ? 128 : 16);
            var backPointers = new int[clusters + 2];
            Array.Fill(backPointers, SpatialRTree<TransientStore>.NullHandle);
            var tree = new CellClusterTree(segment, backPointers);

            var rng = new Random(4242);
            for (int i = 0; i < clusters; i++)
            {
                float x = (float)rng.NextDouble() * (CellExtent - clusterEdge);
                float y = (float)rng.NextDouble() * (CellExtent - clusterEdge);
                var box = Box(x, y, x + clusterEdge, y + clusterEdge);
                oracle.Add(i + 1, in box);
                tree.Add(i + 1, in box);
            }

            float qEdge = CellExtent * sel;
            float qMin = (CellExtent - qEdge) * 0.5f;
            float qMax = qMin + qEdge;

            Console.WriteLine($"broadphase profile: C={clusters} sel={sel:F2} reps={reps} arm={arm} clusterEdge={clusterEdge:F2}");
            if (ArgInt(args, "--warm-ms", 300) > 0)
            {
                Console.WriteLine($"  hits: tree {ScanTree(tree, qMin, qMin, qMax, qMax)}  linear {ScanLinear(oracle, qMin, qMin, qMax, qMax)} (checksums, must match)");
            }

            // Warm both arms by WALL TIME, not iteration count: tiered JIT promotes on a background thread, so a fixed iteration
            // count can finish before the tier-1 body is installed and leave the measured region running tier-0 code.
            // `--warm-ms 0` for a TRACING run: the warm-up calls the very methods being counted, so its calls would land in the
            // same per-method totals and destroy the calls-per-query arithmetic the trace is being taken for. Tiered JIT is moot
            // under tracing anyway — instrumentation dominates whatever tier the body reached.
            int warmMs = ArgInt(args, "--warm-ms", 300);
            if (warmMs > 0)
            {
                Warm(() => ScanTree(tree, qMin, qMin, qMax, qMax), warmMs);
                Warm(() => ScanLinear(oracle, qMin, qMin, qMax, qMax), warmMs);
            }

            long checksum = 0;
            var sw = Stopwatch.StartNew();
            if (arm is "tree" or "both")
            {
                for (int i = 0; i < reps; i++)
                {
                    checksum += ScanTree(tree, qMin, qMin, qMax, qMax);
                }
            }

            long treeMs = sw.ElapsedMilliseconds;
            if (arm is "scan" or "both")
            {
                for (int i = 0; i < reps; i++)
                {
                    checksum += ScanLinear(oracle, qMin, qMin, qMax, qMax);
                }
            }

            sw.Stop();
            Console.WriteLine($"  tree {treeMs} ms, scan {sw.ElapsedMilliseconds - treeMs} ms over {reps} reps  (checksum {checksum})");
            segment.Dispose();
        }
        finally
        {
            guard.Dispose();
            (sp as IDisposable)?.Dispose();
        }
    }

    private static void Warm(Func<int> body, int ms)
    {
        var sw = Stopwatch.StartNew();
        int sink = 0;
        while (sw.ElapsedMilliseconds < ms)
        {
            for (int i = 0; i < 64; i++)
            {
                sink ^= body();
            }
        }

        GC.KeepAlive(sink);
    }

    /// <summary>The tree half of the broadphase: build the query box in tree coordinates and drain the overlap enumerator.</summary>
    private static int ScanTree(CellClusterTree tree, float minX, float minY, float maxX, float maxY)
    {
        int checksum = 0;
        if (SpatialQueryTuning.DirectFloatBox)
        {
            // The f32 entry point: no QueryToCoords call at all, which is half of what this lever is worth.
            foreach (var r in tree.QueryF32(minX, minY, float.NegativeInfinity, maxX, maxY, float.PositiveInfinity))
            {
                checksum += (int)r.PayloadId;
            }

            return checksum;
        }

        Span<double> coords = stackalloc double[6];
        CellClusterTree.QueryToCoords(minX, minY, float.NegativeInfinity, maxX, maxY, float.PositiveInfinity, coords);
        foreach (var r in tree.Query(coords))
        {
            checksum += (int)r.PayloadId;
        }

        return checksum;
    }

    /// <summary>The linear half, lifted from <c>AabbClusterEnumerator</c>'s broadphase loop minus the narrowphase.</summary>
    private static int ScanLinear(CellSpatialIndex idx, float minX, float minY, float maxX, float maxY)
    {
        if (SpatialQueryTuning.SimdLinearScan && idx.ClusterCount >= CellSpatialIndex.SimdScanMinClusters)
        {
            int sum = 0;
            var batchIds = idx.ClusterIds;
            for (int start = 0; start < idx.ClusterCount; start += 64)
            {
                // 2D clusters: Z holds the ±infinity sentinel, so the axis is skipped rather than compared.
                ulong m = idx.MatchBatch(start, minX, minY, float.NegativeInfinity, maxX, maxY, float.PositiveInfinity, testZ: false);
                while (m != 0)
                {
                    sum += batchIds[start + BitOperations.TrailingZeroCount(m)];
                    m &= m - 1;
                }
            }

            return sum;
        }

        int checksum = 0;
        int count = idx.ClusterCount;
        var aMinX = idx.MinX;
        var aMinY = idx.MinY;
        var aMaxX = idx.MaxX;
        var aMaxY = idx.MaxY;
        var ids = idx.ClusterIds;
        for (int i = 0; i < count; i++)
        {
            if (aMaxX[i] < minX || aMinX[i] > maxX)
            {
                continue;
            }

            if (aMaxY[i] < minY || aMinY[i] > maxY)
            {
                continue;
            }

            checksum += ids[i];
        }

        return checksum;
    }

    private static ChunkBasedSegment<TransientStore> CreateSegment(IServiceProvider sp, int startingPages)
    {
        var em = sp.GetRequiredService<EpochManager>();
        var allocator = sp.GetRequiredService<IMemoryAllocator>();
        var registry = sp.GetRequiredService<IResourceRegistry>();
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

    private static int ArgInt(string[] args, string name, int fallback)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var v) ? v : fallback;
    }

    private static float ArgFloat(string[] args, string name, float fallback)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length && float.TryParse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    private static string ArgString(string[] args, string name, string fallback)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
    }
}
