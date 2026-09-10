using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Typhon.Engine;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Benchmark;

[Component("Typhon.Bench.CellTreeCrossover.Pos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct CtxPos
{
    [Field]
    [SpatialIndex]
    public AABB2F Bounds;
}

[Archetype]
partial class CtxUnit : Archetype<CtxUnit>
{
    public static readonly Comp<CtxPos> Pos = Register<CtxPos>();
}

/// <summary>
/// The per-cell tree against the linear scan (#917), on a cell layout the ENGINE produced and maintains, on the engine's own query path.
/// </summary>
/// <remarks>
/// <para><b>The layout.</b> One dense cell, batch-spawned uniformly at random — the per-cell Morton sort (D4) packs it — moved a Drift-sized step per fence
/// with repair running, then quiet fences until the partition holds still. The repair budget is lifted past what the cell can use, so no admission
/// decision depends on a clock and the layout is a function of the seed.</para>
/// <para><b>Both structures over ONE set of clusters.</b> The cell's half is rebuilt as a tree or as a linear index in place
/// (<c>ArchetypeClusterState.ForceCellHalfStructure</c>) between batches, which alternate with the order flipped each round — the same-binary,
/// interleaved-pairs discipline. An earlier version timed two engines, one per structure, and they did not hold one world: repair admission is priced from
/// measured wall-clock costs, so the engine whose fences cost more admitted less repair.</para>
/// <para><b>What it reports.</b> Per cluster count and query selectivity, the whole query (<c>QueryAabb</c>, narrowphase included) on each structure; with
/// <c>--broadphase</c>, also the two broadphases ALONE — the SIMD linear scan against the tree descent, no narrowphase — and what the clusters a query
/// opens really hold, read from their occupancy bitmaps.</para>
/// <para><b>A stand-in.</b> A uniform dense cell, not a game; <c>demo/SwgTatooine</c> (#906) is the representative workload.</para>
/// <para>Run: <c>dotnet run -c Release -- --cell-tree-crossover [--broadphase] [--clusters 64,256,1024,4096,16384] [--sels 0.01,0.02,0.05,0.1,0.2]
/// [--rounds 7] [--budget-ms 1000]</c>.</para>
/// </remarks>
static class CellTreeCrossoverProfile
{
    private const float CellSize = 1_000f;
    private const int WarmFences = 20;
    private const int QueryBoxes = 64;
    private const float Step = CellSize * 0.005f;

    /// <summary>
    /// Entities per transaction when spawning or moving the cell. A million-entity cell in ONE commit overflows the WAL's 32 MiB claim buffer; 262 144 is
    /// the largest population a single commit held before this cap existed, so every cell size up to 4 096 clusters builds exactly as it always did.
    /// </summary>
    private const int TxChunk = 262_144;

    /// <summary>A repair budget the one cell here cannot exhaust, so no admission decision depends on a clock.</summary>
    private const float NonBindingBudgetMs = 1_000f;

    /// <summary>
    /// The category mask <c>QueryAabb</c> carries by default. A live filter, not a no-op: the enumerator reads every candidate's mask against it, on the
    /// linear path from the index's own contiguous array and on the tree path from <c>ClusterAabbs</c>, one scattered read per candidate.
    /// </summary>
    private const uint QueryCategory = uint.MaxValue;

    /// <summary>Quiet fences in a row with an unchanged partition that count as settled, and the most <see cref="Settle"/> will run.</summary>
    private const int SettleStableFences = 4;

    /// <inheritdoc cref="SettleStableFences"/>
    private const int SettleFenceCap = 200;

    /// <summary>One engine, and the one thread every call into it runs on.</summary>
    private sealed class Arm : IDisposable
    {
        private readonly BlockingCollection<(Action Work, ManualResetEventSlim Done)> _queue = new();
        private readonly Thread _thread;
        private Exception _failure;

        internal ServiceProvider Services;
        internal DatabaseEngine Engine;
        internal ArchetypeClusterState State;
        internal EntityId[] Ids;
        internal int CellKey;
        internal long Tick = 1;

        internal Arm(string name)
        {
            _thread = new Thread(() =>
            {
                foreach (var (work, done) in _queue.GetConsumingEnumerable())
                {
                    try
                    {
                        work();
                    }
                    catch (Exception ex)
                    {
                        _failure = ex;
                    }
                    finally
                    {
                        done.Set();
                    }
                }
            }) { IsBackground = true, Name = name };
            _thread.Start();
        }

        internal void Do(Action work)
        {
            using var done = new ManualResetEventSlim();
            _queue.Add((work, done));
            done.Wait();
            if (_failure != null)
            {
                var failure = _failure;
                _failure = null;
                throw new InvalidOperationException($"{_thread.Name} failed", failure);
            }
        }

        internal T Do<T>(Func<T> work)
        {
            T result = default;
            Do(() => { result = work(); });   // a block body, so this binds to the Action overload rather than recursing into this one
            return result;
        }

        public void Dispose()
        {
            Do(() =>
            {
                Engine?.Dispose();
                Services?.Dispose();
            });
            _queue.CompleteAdding();
            _thread.Join();
        }
    }

    internal static void Run(string[] args)
    {
        var broadphase = Array.IndexOf(args, "--broadphase") >= 0;
        var clusterCounts = ArgList(args, "--clusters", broadphase ? "64,256,1024,4096,16384" : "64,128,256,512,1024,2048").Select(int.Parse).ToArray();
        var sels = ArgList(args, "--sels", "0.01,0.02,0.05,0.1,0.2").Select(s => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var rounds = Math.Max(1, ArgInt(args, "--rounds", 7));
        var budgetMs = ArgFloat(args, "--budget-ms", NonBindingBudgetMs);

        Console.WriteLine($"#917 — cell tree vs linear scan on an engine-maintained layout; rounds {rounds}, {QueryBoxes} boxes per pass, "
            + $"repair budget {budgetMs:G} ms{(broadphase ? ", broadphase alone and whole query" : "")}");
        foreach (var clusters in clusterCounts)
        {
            // One engine per cell size: the half is switched between the two structures in place, so both answer over the same clusters.
            using var arm = new Arm("cell");
            arm.Do(() => Build(arm, clusters, budgetMs));
            var slots = BitOperations.PopCount(arm.State.Layout.FullMask);
            var spawned = clusters * slots;
            if (broadphase)
            {
                MeasureBroadphase(arm, sels, rounds, spawned);
            }

            MeasureQueries(arm, sels, rounds, spawned, slots);
            Console.WriteLine();
        }
    }

    /// <summary>
    /// The whole query, on one engine: each box set timed on the linear index and on the tree over the same clusters, interleaved.
    /// </summary>
    private static void MeasureQueries(Arm arm, float[] sels, int rounds, int count, int capacity)
    {
        if (!arm.Do(() => Settle(arm)))
        {
            Console.WriteLine($"  !! the layout did not settle in {SettleFenceCap} quiet fences — a row may time the two structures over different clusters");
        }

        var (extent, toBound, actualClusters) = arm.Do(() => ReadLayout(arm, count, capacity));
        Console.WriteLine("  whole query (QueryAabb, narrowphase included):");
        Console.WriteLine("      C  sel   extent  t/bound   hits/q  opened/q    linear ns      tree ns     gain ns  speed-up");
        var boxRng = new Random(917);   // the same sequence MeasureBroadphase draws, so the two tables are over the same boxes
        foreach (var sel in sels)
        {
            var edge = CellSize * sel;
            var boxes = Boxes(boxRng, edge);
            var layoutBefore = arm.Do(() => Fingerprint(arm));

            // One untimed pass on each structure: the answers must agree (SQ-01), and the linear index says how many clusters a query opens.
            var (linHits, opened) = arm.Do(() =>
            {
                Switch(arm, tree: false);
                return (RunPass(arm, boxes, edge), CandidatesPerQuery(arm, boxes, edge));
            });
            var treeHits = arm.Do(() =>
            {
                Switch(arm, tree: true);
                return RunPass(arm, boxes, edge);
            });
            if (linHits != treeHits)
            {
                Console.WriteLine($"  !! C={actualClusters} sel={sel}: linear found {linHits}, tree {treeHits} — structures disagree (SQ-01), row skipped");
                continue;
            }

            if (linHits == 0)
            {
                Console.WriteLine($"  !! C={actualClusters} sel={sel}: no hits — the row would compare nothing, skipped");
                continue;
            }

            // Warm each structure by wall time, then size a batch to ~1/30 of it.
            var passes = 0;
            foreach (var onTree in new[] { false, true })
            {
                arm.Do(() => Switch(arm, onTree));
                var warm = Stopwatch.StartNew();
                while (warm.ElapsedMilliseconds < 150)
                {
                    arm.Do(() => RunPass(arm, boxes, edge));
                    passes++;
                }
            }

            var batch = Math.Max(4, passes / 30);
            var linNs = new double[rounds];
            var treeNs = new double[rounds];
            for (var r = 0; r < rounds; r++)
            {
                for (var half = 0; half < 2; half++)
                {
                    var onTree = ((r & 1) == 0) == (half == 1);
                    (onTree ? treeNs : linNs)[r] = arm.Do(() =>
                    {
                        Switch(arm, onTree);   // a rebuild, outside the clock
                        var t0 = Stopwatch.GetTimestamp();
                        for (var p = 0; p < batch; p++)
                        {
                            RunPass(arm, boxes, edge);
                        }

                        var ns = (Stopwatch.GetTimestamp() - t0) * 1e9 / Stopwatch.Frequency / (batch * (double)QueryBoxes);
                        if (IsTree(arm) != onTree)
                        {
                            throw new InvalidOperationException("the half's structure changed under the batch — the row would time the wrong structure");
                        }

                        return ns;
                    });
                }
            }

            var lin = Median(linNs);
            var tr = Median(treeNs);
            var moved = arm.Do(() => Fingerprint(arm)) != layoutBefore;
            Console.WriteLine($"  {actualClusters,5} {sel,4:F2}  {extent,6:F3}  {toBound,7:F2}  {linHits / (double)QueryBoxes,7:F0}  "
                + $"{opened,8:F1}  {lin,11:F1}  {tr,11:F1}  {lin - tr,10:F1}  {lin / tr,7:F2}x"
                + (moved ? "   !! the layout changed under this row" : ""));
        }

        arm.Do(() => Switch(arm, tree: false));
    }

    /// <summary>
    /// The broadphase alone, over one set of clusters: the engine's SIMD linear scan (<see cref="CellSpatialIndex.MatchBatch"/>, driven as the query
    /// enumerator drives it) against the cell tree's descent (<see cref="CellClusterTree.Query"/>), same boxes, no narrowphase — and what the clusters a
    /// query opens actually hold, read from their occupancy bitmaps rather than assumed from the slot capacity.
    /// </summary>
    /// <remarks>
    /// Both structures are alive at once: the linear index is taken, the half is promoted from it, and the old index — no longer served, and never
    /// written again because no fence runs in between — is timed beside the tree. The two therefore answer over bit-identical bounds, and every box's
    /// candidates are compared between them before anything is timed. Both timed passes do the per-candidate work the enumerator does on each path: pop the
    /// cluster id and test its category mask against <see cref="QueryCategory"/> — contiguous from the linear index, scattered from <c>ClusterAabbs</c> on
    /// the tree path, the dearer of the two. Opening the cluster and its narrowphase are common to both and left out of both.
    /// </remarks>
    private static void MeasureBroadphase(Arm arm, float[] sels, int rounds, int spawned)
    {
        if (!arm.Do(() => Settle(arm)))
        {
            Console.WriteLine($"  !! the layout did not settle in {SettleFenceCap} quiet fences");
        }

        var clusters = arm.Do(() => arm.State.CellClusterPool.GetClusters(arm.CellKey).Length);
        var slots = BitOperations.PopCount(arm.State.Layout.FullMask);
        Console.WriteLine($"  broadphase alone: {clusters:N0} clusters holding {spawned:N0} entities — "
            + $"{spawned / (double)Math.Max(1, clusters):F1} per cluster against {slots} slots");
        Console.WriteLine("      C  sel   cand/q   entities/q  per cand   hits/q    scan ns/q    tree ns/q  scan/tree  scan ns/cluster");
        var boxRng = new Random(917);   // the same sequence MeasureQueries draws, so its whole-query rows are over these boxes
        foreach (var sel in sels)
        {
            var edge = CellSize * sel;
            var boxes = Boxes(boxRng, edge);
            var r = arm.Do(() => BroadphaseRow(arm, boxes, edge, rounds));
            if (r.Failure != null)
            {
                Console.WriteLine($"  !! C={clusters} sel={sel}: {r.Failure} — row skipped");
                continue;
            }

            Console.WriteLine($"  {clusters,5} {sel,4:F2}  {r.Candidates,7:F1}  {r.Entities,11:F1}  {r.Entities / Math.Max(1d, r.Candidates),8:F1}  "
                + $"{r.Hits,7:F1}  {r.ScanNs,11:F1}  {r.TreeNs,11:F1}  {r.ScanNs / r.TreeNs,8:F2}x  {r.ScanNs / clusters,15:F3}");
        }

        arm.Do(() => Switch(arm, tree: false));
    }

    private static unsafe (string Failure, double Candidates, double Entities, double Hits, double ScanNs, double TreeNs) BroadphaseRow(
        Arm arm, (float X, float Y)[] boxes, float edge, int rounds)
    {
        Switch(arm, tree: false);
        var hits = RunPass(arm, boxes, edge);
        var slot = arm.State.PerCellIndex[arm.CellKey];
        var linear = slot.ReadIndex(isStatic: false);
        Switch(arm, tree: true);
        var tree = slot.ReadTree(isStatic: false);
        if (linear == null || tree == null || linear.ClusterCount != tree.ClusterCount)
        {
            return ("the half did not hold the same clusters in both structures", 0, 0, 0, 0, 0);
        }

        var frame = CellFrame(arm, boxes, edge);
        var aabbs = arm.State.ClusterAabbs;
        var coords = new double[boxes.Length * 6];
        for (var b = 0; b < boxes.Length; b++)
        {
            CellClusterTree.QueryToCoords(frame[b * 4], frame[b * 4 + 1], float.NegativeInfinity, frame[b * 4 + 2], frame[b * 4 + 3], float.PositiveInfinity,
                coords.AsSpan(b * 6, 6));
        }

        using var epoch = EpochGuard.Enter(arm.Engine.EpochManager);

        // Once, before any timing: per box both structures must name the same clusters, and the clusters named hold what their occupancy bitmaps say.
        long candidates = 0, entities = 0;
        using (var accessor = arm.State.ClusterSegment.CreateChunkAccessor())
        {
            for (var b = 0; b < boxes.Length; b++)
            {
                long scanIds = 0, treeIds = 0;
                var fromScan = 0;
                for (var start = 0; start < linear.ClusterCount; start += 64)
                {
                    var mask = linear.MatchBatch(start, frame[b * 4], frame[b * 4 + 1], float.NegativeInfinity, frame[b * 4 + 2], frame[b * 4 + 3],
                        float.PositiveInfinity, testZ: true);
                    while (mask != 0)
                    {
                        var idx = start + BitOperations.TrailingZeroCount(mask);
                        mask &= mask - 1;
                        if ((linear.CategoryMasks[idx] & QueryCategory) == 0)
                        {
                            continue;
                        }

                        var id = linear.ClusterIds[idx];
                        fromScan++;
                        scanIds += id;
                        entities += BitOperations.PopCount(*(ulong*)accessor.GetChunkAddress(id));
                    }
                }

                var fromTree = 0;
                var e = tree.Query(coords.AsSpan(b * 6, 6), 0);
                try
                {
                    while (e.MoveNext())
                    {
                        var id = e.Current.PayloadId;
                        if ((aabbs[id].CategoryMask & QueryCategory) == 0)
                        {
                            continue;
                        }

                        fromTree++;
                        treeIds += id;
                    }
                }
                finally
                {
                    e.Dispose();
                }

                if (fromScan != fromTree || scanIds != treeIds)
                {
                    return ($"box {b}: the scan names {fromScan} clusters, the tree {fromTree}", 0, 0, 0, 0, 0);
                }

                candidates += fromScan;
            }
        }

        // Warm each by wall time, then size a batch to ~1/30 of the warm-up and time interleaved rounds, the order flipped each round.
        var passes = 0;
        var warm = Stopwatch.StartNew();
        while (warm.ElapsedMilliseconds < 300)
        {
            ScanPass(linear, frame);
            TreePass(tree, coords, aabbs);
            passes++;
        }

        var batch = Math.Max(4, passes / 30);
        var scanNs = new double[rounds];
        var treeNs = new double[rounds];
        var sink = 0L;
        for (var r = 0; r < rounds; r++)
        {
            for (var half = 0; half < 2; half++)
            {
                var timeTree = ((r & 1) == 0) == (half == 1);
                var t0 = Stopwatch.GetTimestamp();
                for (var p = 0; p < batch; p++)
                {
                    sink += timeTree ? TreePass(tree, coords, aabbs) : ScanPass(linear, frame);
                }

                var ns = (Stopwatch.GetTimestamp() - t0) * 1e9 / Stopwatch.Frequency / (batch * (double)boxes.Length);
                (timeTree ? treeNs : scanNs)[r] = ns;
            }
        }

        GC.KeepAlive(sink);
        return (null, candidates / (double)boxes.Length, entities / (double)boxes.Length, hits / (double)boxes.Length, Median(scanNs), Median(treeNs));
    }

    /// <summary>
    /// One pass of the linear broadphase over every box: the SIMD batches, and per candidate what the enumerator does — its category mask tested from the
    /// index's own array, its cluster id popped.
    /// </summary>
    private static long ScanPass(CellSpatialIndex linear, float[] frame)
    {
        var sum = 0L;
        for (var b = 0; b < frame.Length / 4; b++)
        {
            for (var start = 0; start < linear.ClusterCount; start += 64)
            {
                var mask = linear.MatchBatch(start, frame[b * 4], frame[b * 4 + 1], float.NegativeInfinity, frame[b * 4 + 2], frame[b * 4 + 3],
                    float.PositiveInfinity, testZ: true);
                while (mask != 0)
                {
                    var idx = start + BitOperations.TrailingZeroCount(mask);
                    mask &= mask - 1;
                    if ((linear.CategoryMasks[idx] & QueryCategory) != 0)
                    {
                        sum += linear.ClusterIds[idx];
                    }
                }
            }
        }

        return sum;
    }

    /// <summary>
    /// One pass of the tree broadphase over every box: the descent, and per candidate what the enumerator does — its cluster id taken off the tree, its
    /// category mask read from <c>ClusterAabbs</c>.
    /// </summary>
    private static long TreePass(CellClusterTree tree, double[] coords, ClusterSpatialAabb[] aabbs)
    {
        var sum = 0L;
        for (var b = 0; b < coords.Length / 6; b++)
        {
            var e = tree.Query(coords.AsSpan(b * 6, 6), 0);
            try
            {
                while (e.MoveNext())
                {
                    var id = e.Current.PayloadId;
                    if ((aabbs[id].CategoryMask & QueryCategory) != 0)
                    {
                        sum += id;
                    }
                }
            }
            finally
            {
                e.Dispose();
            }
        }

        return sum;
    }

    /// <summary>Clusters the linear broadphase names per query over the box set — what a query opens, which is the same on either structure.</summary>
    /// <remarks>Call with the half on the linear index.</remarks>
    private static double CandidatesPerQuery(Arm arm, (float X, float Y)[] boxes, float edge)
    {
        var linear = arm.State.PerCellIndex[arm.CellKey].ReadIndex(isStatic: false);
        var frame = CellFrame(arm, boxes, edge);
        long total = 0;
        for (var b = 0; b < boxes.Length; b++)
        {
            for (var start = 0; start < linear.ClusterCount; start += 64)
            {
                total += BitOperations.PopCount(linear.MatchBatch(start, frame[b * 4], frame[b * 4 + 1], float.NegativeInfinity, frame[b * 4 + 2],
                    frame[b * 4 + 3], float.PositiveInfinity, testZ: true));
            }
        }

        return total / (double)boxes.Length;
    }

    /// <summary>The boxes in the cell's frame, rounded outward exactly as the query enumerator's <c>SetCellQueryFrame</c> does: four floats per box.</summary>
    private static float[] CellFrame(Arm arm, (float X, float Y)[] boxes, float edge)
    {
        arm.Engine.SpatialGrid.CellOrigin(arm.CellKey, out var originX, out var originY, out _);
        var frame = new float[boxes.Length * 4];
        for (var b = 0; b < boxes.Length; b++)
        {
            var (x, y) = boxes[b];
            frame[b * 4] = ClusterSpatialAabb.ToCellRelativeMin(x, originX);
            frame[b * 4 + 1] = ClusterSpatialAabb.ToCellRelativeMin(y, originY);
            frame[b * 4 + 2] = ClusterSpatialAabb.ToCellRelativeMax(x + edge, originX);
            frame[b * 4 + 3] = ClusterSpatialAabb.ToCellRelativeMax(y + edge, originY);
        }

        return frame;
    }

    private static void Build(Arm arm, int clusters, float budgetMs)
    {
        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
          .AddResourceRegistry()
          .AddMemoryAllocator()
          .AddEpochManager()
          .AddHighResolutionSharedTimer()
          .AddDeadlineWatchdog()
          .AddScopedManagedPagedMemoryMappedFile(o =>
          {
              o.DatabaseName = $"CtxOver_{Environment.ProcessId}";
              o.DatabaseCacheSize = (ulong)(32L * 1024 * PagedMMF.PageSize);
              o.TestMode = true;
              o.PagesDebugPattern = false;
          })
          .AddInMemoryWalEngine();

        arm.Services = sc.BuildServiceProvider();
        arm.Services.EnsureFileDeleted<ManagedPagedMMFOptions>();
        var dbe = arm.Services.GetRequiredService<DatabaseEngine>();
        arm.Engine = dbe;
        dbe.RegisterComponentFromAccessor<CtxPos>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(CellSize, CellSize), CellSize, reclusterBudgetMs: budgetMs));
        dbe.ClusterCellTreePromoteThreshold = int.MaxValue;   // the half is switched by hand, never by the gate
        dbe.InitializeArchetypes();
        arm.State = dbe._archetypeStates[Archetype<CtxUnit>.Metadata.ArchetypeId].ClusterState;
        arm.CellKey = dbe.SpatialGrid.WorldToCellKey(CellSize * 0.5f, CellSize * 0.5f, 0f);

        var capacity = BitOperations.PopCount(arm.State.Layout.FullMask);
        var count = clusters * capacity;
        var rng = new Random(20260910);
        var xs = new float[count];
        var ys = new float[count];
        arm.Ids = new EntityId[count];
        for (var start = 0; start < count; start += TxChunk)
        {
            using var tx = dbe.CreateQuickTransaction();
            for (var i = start; i < Math.Min(count, start + TxChunk); i++)
            {
                xs[i] = (float)rng.NextDouble() * CellSize;
                ys[i] = (float)rng.NextDouble() * CellSize;
                arm.Ids[i] = tx.Spawn<CtxUnit>(CtxUnit.Pos.Set(new CtxPos { Bounds = Point(xs[i], ys[i]) }));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(arm.Tick++);
        for (var f = 0; f < WarmFences; f++)
        {
            Displace(xs, ys, rng);
            Write(arm, xs, ys);
            dbe.WriteTickFence(arm.Tick++);
        }
    }

    /// <summary>
    /// Quiet fences until the cell's partition holds still for <see cref="SettleStableFences"/> in a row. Repair goes on working a still world for a few
    /// fences after motion stops, and a query row must time both structures over one set of clusters.
    /// </summary>
    private static bool Settle(Arm arm)
    {
        var last = Fingerprint(arm);
        var stable = 0;
        for (var f = 0; f < SettleFenceCap && stable < SettleStableFences; f++)
        {
            arm.Engine.WriteTickFence(arm.Tick++);
            var now = Fingerprint(arm);
            stable = now == last ? stable + 1 : 0;
            last = now;
        }

        return stable >= SettleStableFences;
    }

    /// <summary>Put the cell's half on the given structure, and fail loudly if the engine will not.</summary>
    private static void Switch(Arm arm, bool tree)
    {
        using var epoch = EpochGuard.Enter(arm.Engine.EpochManager);
        if (!arm.State.ForceCellHalfStructure(arm.CellKey, tree) || IsTree(arm) != tree)
        {
            throw new InvalidOperationException($"could not put cell {arm.CellKey} on the {(tree ? "tree" : "linear index")}");
        }
    }

    private static bool IsTree(Arm arm)
    {
        var slot = arm.State.PerCellIndex[arm.CellKey];
        return slot != null && (slot.ReadTree(isStatic: false) != null || slot.ReadTree(isStatic: true) != null);
    }

    /// <summary>The cell's clusters and their bounds, hashed: two readings with equal fingerprints saw the same partition.</summary>
    private static (int Clusters, int Hash) Fingerprint(Arm arm)
    {
        var ids = arm.State.CellClusterPool.GetClusters(arm.CellKey);
        var hash = new HashCode();
        foreach (var id in ids)
        {
            ref var b = ref arm.State.ClusterAabbs[id];
            hash.Add(id);
            hash.Add(b.MinX);
            hash.Add(b.MinY);
            hash.Add(b.MaxX);
            hash.Add(b.MaxY);
        }

        return (ids.Length, hash.ToHashCode());
    }

    private static (float X, float Y)[] Boxes(Random rng, float edge)
    {
        var boxes = new (float X, float Y)[QueryBoxes];
        for (var b = 0; b < QueryBoxes; b++)
        {
            boxes[b] = ((float)rng.NextDouble() * (CellSize - edge), (float)rng.NextDouble() * (CellSize - edge));
        }

        return boxes;
    }

    private static AABB2F Point(float x, float y) => new() { MinX = x, MinY = y, MaxX = x, MaxY = y };

    private static void Displace(float[] xs, float[] ys, Random rng)
    {
        for (var i = 0; i < xs.Length; i++)
        {
            xs[i] = Math.Clamp(xs[i] + ((float)rng.NextDouble() - 0.5f) * 2f * Step, 0f, CellSize - 0.001f);
            ys[i] = Math.Clamp(ys[i] + ((float)rng.NextDouble() - 0.5f) * 2f * Step, 0f, CellSize - 0.001f);
        }
    }

    private static void Write(Arm arm, float[] xs, float[] ys)
    {
        for (var start = 0; start < arm.Ids.Length; start += TxChunk)
        {
            using var tx = arm.Engine.CreateQuickTransaction();
            for (var i = start; i < Math.Min(arm.Ids.Length, start + TxChunk); i++)
            {
                tx.OpenMut(arm.Ids[i]).Write(CtxUnit.Pos) = new CtxPos { Bounds = Point(xs[i], ys[i]) };
            }

            tx.Commit();
        }
    }

    private static long RunPass(Arm arm, (float X, float Y)[] boxes, float edge)
    {
        using var epoch = EpochGuard.Enter(arm.Engine.EpochManager);
        var found = 0L;
        for (var b = 0; b < boxes.Length; b++)
        {
            var (x, y) = boxes[b];
            foreach (var hit in arm.State.QueryAabb(arm.Engine.SpatialGrid, x, y, float.NegativeInfinity, x + edge, y + edge, float.PositiveInfinity))
            {
                found += hit.EntityId == 0 ? 0 : 1;
            }
        }

        return found;
    }

    private static (double Extent, double ToBound, int Clusters) ReadLayout(Arm arm, int count, int capacity)
    {
        var ids = arm.State.CellClusterPool.GetClusters(arm.CellKey);
        var total = 0d;
        var counted = 0;
        foreach (var id in ids)
        {
            ref var b = ref arm.State.ClusterAabbs[id];
            if (float.IsPositiveInfinity(b.MinX))
            {
                continue;
            }

            total += Math.Max(b.MaxX - b.MinX, b.MaxY - b.MinY);
            counted++;
        }

        var extent = counted == 0 ? 0d : total / counted / CellSize;
        return (extent, extent / Math.Sqrt((double)capacity / count), ids.Length);
    }

    private static double Median(double[] values)
    {
        var copy = (double[])values.Clone();
        Array.Sort(copy);
        return copy[copy.Length / 2];
    }

    private static int ArgInt(string[] args, string name, int fallback)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var v) ? v : fallback;
    }

    private static float ArgFloat(string[] args, string name, float fallback)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length
            && float.TryParse(args[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? v
            : fallback;
    }

    private static string[] ArgList(string[] args, string name, string fallback)
    {
        var i = Array.IndexOf(args, name);
        var raw = i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
