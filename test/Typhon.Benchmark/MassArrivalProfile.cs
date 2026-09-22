using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Typhon.Engine;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Benchmark;

[Component("Typhon.Bench.MassArrival.Pos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct MaPos
{
    [Field]
    [SpatialIndex]
    public AABB2F Bounds;
}

[Archetype]
partial class MaUnit : Archetype<MaUnit>
{
    public static readonly Comp<MaPos> Pos = Register<MaPos>();
}

/// <summary>
/// #910's worst case, measured rather than argued: N entities teleported in ONE tick into an empty cell, and what the engine's existing repair does
/// about it over the ticks that follow.
/// </summary>
/// <remarks>
/// <para><b>Pre-registered, before the first run.</b> Hypothesis: at the default repair budget the destination cell is back within 10 % of its settled
/// layout in a few fences (≤ 10), its queries cost at most ~2× settled inside that window, and the repair spent on it does not starve the rest of the
/// world — units refused and repair-queue depth stay inside the A/A band. Any of the three failing makes T1 (arrival ordering) worth building, with this
/// harness as its acceptance test.</para>
/// <para><b>Arms.</b> One fresh engine per run; every arm of a round starts from the same seeded world. <c>none</c> runs twice — the A/A band, because the
/// repair budget is priced from measured time and two identical runs are therefore not identical. <c>random</c>: arrival order unrelated to landing
/// position, first fit's worst case. <c>coherent</c>: arrival order equal to the landing points' Morton order — the order a fleet that kept its formation
/// would arrive in. The two teleport arms move the same entities to the same landing points; only which entity takes which point differs. The arm order
/// rotates each round.</para>
/// <para><b>World.</b> Flat, 16×16 cells of 100 m, a background that never enters the destination cell (8, 8), 10 % of it stepping up to 0.5 m a tick so
/// drift and repair have work elsewhere. Serial fence. Readings after every fence, on the fence's thread: the destination cell's layout, the work a query
/// box there does (clusters overlapping, entities tested — counted, from the cell index and occupancy), its time (one discarded pass, then the median of
/// three), and the engine's repair telemetry.</para>
/// <para><b>Churn</b> (<c>--churn</c>, default 32 a tick): background entities jumping to random points, so every cell keeps receiving arrivals that
/// loosen it and repair has standing work for the arrival to compete with. Without it the A/A arms repair nothing and starvation cannot show. A discarded
/// warm-up run precedes the rounds: the first repair in a process pays for its JIT.</para>
/// <para>Run: <c>dotnet run -c Release -- --mass-arrival [--arrivals 128,512,2048] [--rounds 3] [--budget-ms 1] [--repair-unit 8] [--background 16384]
/// [--dest-pop 0] [--churn 32] [--series]</c>.</para>
/// </remarks>
static class MassArrivalProfile
{
    private const float CellSize = 100f;
    private const int CellsPerSide = 16;
    private const float WorldSize = CellSize * CellsPerSide;
    private const float DestMin = CellSize * 8f;
    private const float DestMax = DestMin + CellSize;
    private const int WarmTicks = 30;
    private const int WindowTicks = 40;
    private const int BeforeTicks = 5;
    private const int SettledTicks = 5;
    private const int QueryBoxes = 64;
    private const float QueryEdge = CellSize * 0.1f;
    private const float Step = 0.5f;
    private const double MovingFraction = 0.1;
    private const int TimedPasses = 3;

    private enum ArmKind { None, Random, Coherent }

    private sealed class Reading
    {
        public double Clusters, Extent, ToBound, Occupancy, QueryNs, Overlapping, Tested, Hits, FenceMs, BackgroundToBound, BudgetUsedMs, NsPerEntity;
        public int Repaired, Units, Refused, QueueDepth, Largest, Jumps, Migrations;
    }

    private sealed class RunResult
    {
        public string Arm = "";
        public ArmKind Kind;
        public int N;
        public int Round;
        public readonly List<Reading> Series = [];

        // Reading index of the arrival fence (t = 0).
        public Reading At(int t) => Series[BeforeTicks + t];

        public IEnumerable<Reading> Window => Series.Skip(BeforeTicks);

        public IEnumerable<Reading> Settled => Series.Skip(Series.Count - SettledTicks);
    }

    internal static void Run(string[] args)
    {
        var arrivals = ArgList(args, "--arrivals", "128,512,2048").Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        var rounds = Math.Max(1, ArgInt(args, "--rounds", 3));
        var budgetMs = ArgFloat(args, "--budget-ms", 1f);
        var repairUnit = ArgInt(args, "--repair-unit", 8);
        var background = ArgInt(args, "--background", 16_384);
        var destPop = ArgInt(args, "--dest-pop", 0);
        var churn = ArgInt(args, "--churn", 32);
        var series = Array.IndexOf(args, "--series") >= 0;

        Console.WriteLine($"#910 worst case — N entities teleported in one tick into cell (8,8). Background {background:N0} on {CellsPerSide}x{CellsPerSide} "
            + $"cells of {CellSize:G} m, churn {churn} random teleports a tick, repair budget {budgetMs:G} ms, repair unit {repairUnit}, destination "
            + $"pre-population {destPop}, {rounds} rounds, {WindowTicks}-fence window, serial fence");

        var results = new List<RunResult>();
        var runIndex = 0;

        // Discarded: the first repair a process runs pays for its JIT, and it would land on whichever arm happened to repair first.
        RunOne(ArmKind.Random, arrivals.Min(), round: 99, background, destPop, churn, budgetMs, repairUnit, runIndex++);
        Console.WriteLine("  warm-up run done (discarded)");
        var arms = new (string Name, ArmKind Kind)[]
        {
            ("none-a", ArmKind.None), ("random", ArmKind.Random), ("none-b", ArmKind.None), ("coherent", ArmKind.Coherent),
        };
        for (var round = 0; round < rounds; round++)
        {
            foreach (var n in arrivals)
            {
                for (var k = 0; k < arms.Length; k++)
                {
                    var (name, kind) = arms[(k + round) % arms.Length];
                    var r = RunOne(kind, n, round, background, destPop, churn, budgetMs, repairUnit, runIndex++);
                    r.Arm = name;
                    results.Add(r);
                    PrintRow(r);
                }
            }
        }

        PrintSummary(results, arrivals);
        if (series)
        {
            foreach (var n in arrivals)
            {
                PrintSeries(results.First(r => r.Kind == ArmKind.Random && r.N == n && r.Round == 0));
            }
        }
    }

    private static RunResult RunOne(ArmKind kind, int n, int round, int background, int destPop, int churn, float budgetMs, int repairUnit, int runIndex)
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
              o.DatabaseName = $"MassArr_{Environment.ProcessId}_{runIndex}";
              o.DatabaseCacheSize = (ulong)(32L * 1024 * PagedMMF.PageSize);
              o.TestMode = true;
              o.PagesDebugPattern = false;
          })
          .AddInMemoryWalEngine();

        using var services = sc.BuildServiceProvider();
        services.EnsureFileDeleted<ManagedPagedMMFOptions>();
        var dbe = services.GetRequiredService<DatabaseEngine>();
        try
        {
            dbe.RegisterComponentFromAccessor<MaPos>();
            dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(WorldSize, WorldSize), CellSize, reclusterBudgetMs: budgetMs,
                repairWorstClustersPerUnit: repairUnit));
            dbe.InitializeArchetypes();
            var archetypeId = Archetype<MaUnit>.Metadata.ArchetypeId;
            var cs = dbe._archetypeStates[archetypeId].ClusterState;
            var grid = dbe.SpatialGrid;
            var destKey = grid.WorldToCellKey(DestMin + CellSize * 0.5f, DestMin + CellSize * 0.5f, 0f);

            // The round's world. Every seed below is a function of the round (and N), never of the arm.
            var worldRng = new Random(9_100 + round);
            var total = background + destPop;
            var xs = new float[total];
            var ys = new float[total];
            var ids = new EntityId[total];
            var moving = new bool[total];
            for (var i = 0; i < background; i++)
            {
                do
                {
                    xs[i] = (float)worldRng.NextDouble() * WorldSize;
                    ys[i] = (float)worldRng.NextDouble() * WorldSize;
                }
                while (InDest(xs[i], ys[i]));

                moving[i] = worldRng.NextDouble() < MovingFraction;
            }

            for (var i = background; i < total; i++)
            {
                xs[i] = DestMin + (float)worldRng.NextDouble() * CellSize;
                ys[i] = DestMin + (float)worldRng.NextDouble() * CellSize;
            }

            using (var tx = dbe.CreateQuickTransaction())
            {
                for (var i = 0; i < total; i++)
                {
                    ids[i] = tx.Spawn<MaUnit>(MaUnit.Pos.Set(new MaPos { Bounds = Point(xs[i], ys[i]) }));
                }

                tx.Commit();
            }

            // Who teleports and where they land: the same in both teleport arms of a round.
            var pickRng = new Random(9_100 * 31 + round * 7 + n);
            var pool = Enumerable.Range(0, background).ToArray();
            for (var j = 0; j < n; j++)
            {
                var swap = j + pickRng.Next(background - j);
                (pool[j], pool[swap]) = (pool[swap], pool[j]);
            }

            var picks = pool[..n];

            // The picks stand still in EVERY arm, the no-teleport arms included: otherwise the teleport arms would lose the picks' motion and churn
            // after t = 0 and the background they are compared against would carry less work than theirs.
            var isPick = new bool[total];
            foreach (var p in picks)
            {
                isPick[p] = true;
            }

            var points = new (float X, float Y)[n];
            for (var j = 0; j < n; j++)
            {
                points[j] = (DestMin + (float)pickRng.NextDouble() * CellSize, DestMin + (float)pickRng.NextDouble() * CellSize);
            }

            var boxRng = new Random(910);
            var boxes = new (float X, float Y)[QueryBoxes];
            for (var b = 0; b < QueryBoxes; b++)
            {
                boxes[b] = (DestMin + (float)boxRng.NextDouble() * (CellSize - QueryEdge), DestMin + (float)boxRng.NextDouble() * (CellSize - QueryEdge));
            }

            var result = new RunResult { Kind = kind, N = n, Round = round };
            var motionRng = new Random(9_100 + round * 1_009);
            var churnRng = new Random(9_100 + round * 2_003);
            var frozen = new bool[total];
            var tick = 1L;
            dbe.WriteTickFence(tick++);
            for (var t = -WarmTicks; t <= WindowTicks; t++)
            {
                // Where each arriving entity lands, decided before the tick's transaction: the coherent arm reads the current locations.
                var landing = t == 0 && kind != ArmKind.None ? Assign(kind, dbe, cs, ids, picks, points) : null;
                using (var tx = dbe.CreateQuickTransaction())
                {
                    for (var i = 0; i < background; i++)
                    {
                        if (!moving[i])
                        {
                            continue;
                        }

                        // Drawn for every mover in every arm, applied only to those still in the background, so the background's motion is the same
                        // sequence whichever arm runs.
                        var dx = ((float)motionRng.NextDouble() - 0.5f) * 2f * Step;
                        var dy = ((float)motionRng.NextDouble() - 0.5f) * 2f * Step;
                        if (isPick[i])
                        {
                            continue;
                        }

                        var nx = Math.Clamp(xs[i] + dx, 0f, WorldSize - 0.001f);
                        var ny = Math.Clamp(ys[i] + dy, 0f, WorldSize - 0.001f);
                        if (InDest(nx, ny))
                        {
                            continue;   // the destination cell belongs to the arrival alone
                        }

                        xs[i] = nx;
                        ys[i] = ny;
                        tx.OpenMut(ids[i]).Write(MaUnit.Pos) = new MaPos { Bounds = Point(nx, ny) };
                    }

                    // Churn: a few background entities a tick jump to a random point outside the destination — every cell keeps receiving arrivals
                    // that loosen its clusters, so repair has standing work elsewhere for the arrival's repair to compete with. Drawn before the frozen
                    // test for the same reason as the motion.
                    for (var c = 0; c < churn; c++)
                    {
                        var i = churnRng.Next(background);
                        float cx, cy;
                        do
                        {
                            cx = (float)churnRng.NextDouble() * WorldSize;
                            cy = (float)churnRng.NextDouble() * WorldSize;
                        }
                        while (InDest(cx, cy));

                        if (isPick[i])
                        {
                            continue;
                        }

                        xs[i] = cx;
                        ys[i] = cy;
                        tx.OpenMut(ids[i]).Write(MaUnit.Pos) = new MaPos { Bounds = Point(cx, cy) };
                    }

                    if (landing != null)
                    {
                        foreach (var (i, x, y) in landing)
                        {
                            xs[i] = x;
                            ys[i] = y;
                            frozen[i] = true;
                            tx.OpenMut(ids[i]).Write(MaUnit.Pos) = new MaPos { Bounds = Point(x, y) };
                        }
                    }

                    tx.Commit();
                }

                var t0 = Stopwatch.GetTimestamp();
                dbe.WriteTickFence(tick++);
                var fenceMs = (Stopwatch.GetTimestamp() - t0) * 1e3 / Stopwatch.Frequency;
                if (t >= -BeforeTicks)
                {
                    result.Series.Add(Read(dbe, cs, grid, destKey, archetypeId, boxes, fenceMs));
                }
            }

            return result;
        }
        finally
        {
            dbe.Dispose();
        }
    }

    /// <summary>
    /// The landing assignment. Random: the picks in their drawn order take the points in theirs — unrelated to either's position. Coherent: the picks in the
    /// order this tick's drain will execute them — ascending (cluster, slot), the order the dirty scan files them and the stable destination sort keeps —
    /// take the points in Morton order, so first fit fills the cell's clusters one compact run at a time.
    /// </summary>
    private static (int Index, float X, float Y)[] Assign(ArmKind kind, DatabaseEngine dbe, ArchetypeClusterState cs, EntityId[] ids, int[] picks,
        (float X, float Y)[] points)
    {
        var order = (int[])picks.Clone();
        var landing = ((float X, float Y)[])points.Clone();
        if (kind == ArmKind.Coherent)
        {
            var where = Locations(dbe, cs);
            Array.Sort(order, (a, b) =>
            {
                var la = where[(long)ids[a].RawValue];
                var lb = where[(long)ids[b].RawValue];
                return la.Chunk != lb.Chunk ? la.Chunk.CompareTo(lb.Chunk) : la.Slot.CompareTo(lb.Slot);
            });
            Array.Sort(landing, static (a, b) => Morton(a).CompareTo(Morton(b)));
        }

        var assignment = new (int, float, float)[order.Length];
        for (var j = 0; j < order.Length; j++)
        {
            assignment[j] = (order[j], landing[j].X, landing[j].Y);
        }

        return assignment;
    }

    private static unsafe Dictionary<long, (int Chunk, int Slot)> Locations(DatabaseEngine dbe, ArchetypeClusterState cs)
    {
        var map = new Dictionary<long, (int Chunk, int Slot)>();
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var accessor = cs.ClusterSegment.CreateChunkAccessor();
        try
        {
            for (var c = 0; c < cs.ActiveClusterCount; c++)
            {
                var chunkId = cs.ActiveClusterIds[c];
                var clusterBase = accessor.GetChunkAddress(chunkId);
                var occupancy = *(ulong*)clusterBase;
                while (occupancy != 0)
                {
                    var slot = BitOperations.TrailingZeroCount(occupancy);
                    occupancy &= occupancy - 1;
                    map[*(long*)(clusterBase + cs.Layout.EntityIdsOffset + slot * 8)] = (chunkId, slot);
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        return map;
    }

    private static unsafe Reading Read(DatabaseEngine dbe, ArchetypeClusterState cs, SpatialGrid grid, int destKey, ushort archetypeId,
        (float X, float Y)[] boxes, double fenceMs)
    {
        var tel = dbe.GetSpatialTelemetry(archetypeId);
        var reading = new Reading
        {
            FenceMs = fenceMs,
            Repaired = tel.RepairedEntityCount,
            Units = tel.RepairUnitCount,
            Refused = tel.RepairUnitsRefused,
            QueueDepth = tel.RepairQueueDepth,
            BudgetUsedMs = tel.ReclusterBudgetUsedMs,
            NsPerEntity = tel.MeasuredNsPerEntity,
            Largest = tel.LargestArrivalRun,
            Jumps = tel.JumpCrossings,
            Migrations = tel.MigrationCount,
        };

        var slots = BitOperations.PopCount(cs.Layout.FullMask);
        using (EpochGuard.Enter(dbe.EpochManager))
        {
            var accessor = cs.ClusterSegment.CreateChunkAccessor();
            try
            {
                (reading.Clusters, reading.Extent, reading.ToBound, reading.Occupancy) = CellLayout(cs, ref accessor, destKey, slots);

                // Every other cell, weighted by its clusters: the part of the world the arrival's repair could starve.
                double weighted = 0;
                double clusters = 0;
                var perCell = cs.PerCellIndex;
                for (var key = 0; perCell != null && key < perCell.Length; key++)
                {
                    if (key == destKey || perCell[key] == null)
                    {
                        continue;
                    }

                    var (c, _, toBound, _) = CellLayout(cs, ref accessor, key, slots);
                    weighted += c * toBound;
                    clusters += c;
                }

                reading.BackgroundToBound = clusters > 0 ? weighted / clusters : 0;

                // What a box in the destination makes the query do. Boxes lie inside the cell, so it is the only cell they open.
                var index = perCell != null && destKey < perCell.Length ? perCell[destKey]?.ReadIndex(isStatic: false) : null;
                if (index != null)
                {
                    grid.CellOrigin(destKey, out var ox, out var oy, out _);
                    long overlapping = 0;
                    long tested = 0;
                    foreach (var (x, y) in boxes)
                    {
                        var qMinX = ClusterSpatialAabb.ToCellRelativeMin(x, ox);
                        var qMinY = ClusterSpatialAabb.ToCellRelativeMin(y, oy);
                        var qMaxX = ClusterSpatialAabb.ToCellRelativeMax(x + QueryEdge, ox);
                        var qMaxY = ClusterSpatialAabb.ToCellRelativeMax(y + QueryEdge, oy);
                        for (var i = 0; i < index.ClusterCount; i++)
                        {
                            if (index.MaxX[i] < qMinX || index.MinX[i] > qMaxX || index.MaxY[i] < qMinY || index.MinY[i] > qMaxY)
                            {
                                continue;
                            }

                            overlapping++;
                            tested += BitOperations.PopCount(*(ulong*)accessor.GetChunkAddress(index.ClusterIds[i]));
                        }
                    }

                    reading.Overlapping = overlapping / (double)boxes.Length;
                    reading.Tested = tested / (double)boxes.Length;
                }
            }
            finally
            {
                accessor.Dispose();
            }
        }

        RunPass(dbe, cs, grid, boxes);   // discarded: the pass that pays for whatever the fence left in the caches
        var ns = new double[TimedPasses];
        long hits = 0;
        for (var p = 0; p < TimedPasses; p++)
        {
            var t0 = Stopwatch.GetTimestamp();
            hits = RunPass(dbe, cs, grid, boxes);
            ns[p] = (Stopwatch.GetTimestamp() - t0) * 1e9 / Stopwatch.Frequency / boxes.Length;
        }

        reading.QueryNs = Median(ns);
        reading.Hits = hits / (double)boxes.Length;
        return reading;
    }

    /// <summary>
    /// One cell's clusters of the archetype: count, mean extent as a fraction of the cell, extent ÷ the packing bound of its own population, occupancy.
    /// </summary>
    private static unsafe (double Clusters, double Extent, double ToBound, double Occupancy) CellLayout(ArchetypeClusterState cs,
        ref ChunkAccessor<PersistentStore> accessor, int cellKey, int slots)
    {
        var ids = cs.CellClusterPool.GetClusters(cellKey);
        if (ids.Length == 0)
        {
            return (0, 0, 0, 0);
        }

        double extent = 0;
        var counted = 0;
        long entities = 0;
        foreach (var id in ids)
        {
            entities += BitOperations.PopCount(*(ulong*)accessor.GetChunkAddress(id));
            ref var b = ref cs.ClusterAabbs[id];
            if (float.IsPositiveInfinity(b.MinX))
            {
                continue;
            }

            extent += Math.Max(b.MaxX - b.MinX, b.MaxY - b.MinY) / CellSize;
            counted++;
        }

        if (counted == 0 || entities == 0)
        {
            return (ids.Length, 0, 0, 0);
        }

        var mean = extent / counted;
        var bound = Math.Min(1d, Math.Sqrt((double)slots / entities));
        return (ids.Length, mean, mean / bound, entities / (double)((long)ids.Length * slots));
    }

    private static long RunPass(DatabaseEngine dbe, ArchetypeClusterState cs, SpatialGrid grid, (float X, float Y)[] boxes)
    {
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var found = 0L;
        foreach (var (x, y) in boxes)
        {
            foreach (var hit in cs.QueryAabb(grid, x, y, float.NegativeInfinity, x + QueryEdge, y + QueryEdge, float.PositiveInfinity))
            {
                found += hit.EntityId == 0 ? 0 : 1;
            }
        }

        return found;
    }

    // ── Reporting ───────────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed record Summary(double ToBound0, double ToBoundSettled, int Recover, double Query0Us, double QuerySettledUs, double QueryPeak,
        double QueryExcess, double Tested0, double TestedSettled, int Repaired, int Units, int Refused, double Queue, double FenceMs, double Background,
        int Largest, int Jumps);

    private static Summary Summarize(RunResult r)
    {
        var arrival = r.At(0);
        var settledTb = Median(r.Settled.Select(static x => x.ToBound));
        var settledQ = Median(r.Settled.Select(static x => x.QueryNs));
        var recover = -1;
        for (var t = 0; t <= WindowTicks; t++)
        {
            if (r.Kind == ArmKind.None || r.At(t).ToBound <= settledTb * 1.1)
            {
                recover = t;
                break;
            }
        }

        var window = r.Window.ToList();
        var peak = settledQ > 0 ? window.Max(x => x.QueryNs / settledQ) : 0;
        var excess = settledQ > 0 ? window.Sum(x => Math.Max(0, x.QueryNs / settledQ - 1)) : 0;
        return new Summary(arrival.ToBound, settledTb, recover, arrival.QueryNs / 1e3, settledQ / 1e3, peak, excess, arrival.Tested,
            Median(r.Settled.Select(static x => x.Tested)), window.Sum(static x => x.Repaired), window.Sum(static x => x.Units),
            window.Sum(static x => x.Refused), Median(window.Select(static x => (double)x.QueueDepth)), Median(window.Select(static x => x.FenceMs)),
            Median(window.Select(static x => x.BackgroundToBound)), arrival.Largest, arrival.Jumps);
    }

    private static void PrintRow(RunResult r)
    {
        var s = Summarize(r);
        var head = $"  r{r.Round} {r.Arm,-8} N={r.N,5} | fence p50 {s.FenceMs,5:F2} ms, repaired {s.Repaired,6:N0} in {s.Units,4} units, "
            + $"refused {s.Refused,4}, queue {s.Queue,4:F0}, background t/bound {s.Background,4:F2}";
        if (r.Kind == ArmKind.None)
        {
            Console.WriteLine(head);
            return;
        }

        var recover = s.Recover < 0 ? $">{WindowTicks}" : s.Recover.ToString(CultureInfo.InvariantCulture);
        Console.WriteLine(head + $" | arrival run {s.Largest,5}, jumps {s.Jumps,5} | dest t/bound {s.ToBound0,4:F2} -> {s.ToBoundSettled,4:F2}, "
            + $"within 10 % after {recover,3} fences | query {s.Query0Us,6:F1} -> {s.QuerySettledUs,6:F1} us (peak {s.QueryPeak,4:F2}x, excess "
            + $"{s.QueryExcess,5:F1} query-fences), tested {s.Tested0,6:F0} -> {s.TestedSettled,6:F0}");
    }

    private static void PrintSummary(List<RunResult> results, int[] arrivals)
    {
        Console.WriteLine();
        Console.WriteLine("  medians over rounds:");
        foreach (var n in arrivals)
        {
            foreach (var arm in new[] { "none-a", "none-b", "random", "coherent" })
            {
                var rows = results.Where(r => r.N == n && r.Arm == arm).Select(Summarize).ToList();
                if (rows.Count == 0)
                {
                    continue;
                }

                var line = $"  N={n,5} {arm,-8} fence {Median(rows.Select(static s => s.FenceMs)),5:F2} ms, refused "
                    + $"{Median(rows.Select(static s => (double)s.Refused)),5:F0}, queue {Median(rows.Select(static s => s.Queue)),4:F0}, background t/bound "
                    + $"{Median(rows.Select(static s => s.Background)),4:F2}";
                if (arm is "random" or "coherent")
                {
                    line += $" | dest t/bound {Median(rows.Select(static s => s.ToBound0)),4:F2} -> "
                        + $"{Median(rows.Select(static s => s.ToBoundSettled)),4:F2}, "
                        + $"recover {Median(rows.Select(static s => (double)(s.Recover < 0 ? 999 : s.Recover))),3:F0} fences, query peak "
                        + $"{Median(rows.Select(static s => s.QueryPeak)),4:F2}x, excess {Median(rows.Select(static s => s.QueryExcess)),5:F1} query-fences, "
                        + $"repaired {Median(rows.Select(static s => (double)s.Repaired)),6:F0}";
                }

                Console.WriteLine(line);
            }
        }
    }

    private static void PrintSeries(RunResult r)
    {
        Console.WriteLine();
        Console.WriteLine($"  per fence, {r.Arm} N={r.N} round {r.Round} (t = 0 is the arrival fence):");
        Console.WriteLine("      t  clusters  t/bound   occ   query us  overlap  tested  repaired  units  refused  queue  ns/entity  fence ms");
        for (var t = -BeforeTicks; t <= WindowTicks; t++)
        {
            var x = r.At(t);
            Console.WriteLine($"  {t,5}  {x.Clusters,8:F0}  {x.ToBound,7:F2}  {x.Occupancy,4:P0}  {x.QueryNs / 1e3,8:F1}  {x.Overlapping,7:F1}  "
                + $"{x.Tested,6:F0}  {x.Repaired,8}  {x.Units,5}  {x.Refused,7}  {x.QueueDepth,5}  {x.NsPerEntity,9:F0}  {x.FenceMs,8:F2}");
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static bool InDest(float x, float y) => x >= DestMin && x < DestMax && y >= DestMin && y < DestMax;

    private static AABB2F Point(float x, float y) => new() { MinX = x, MinY = y, MaxX = x, MaxY = y };

    private static ulong Morton((float X, float Y) p)
    {
        var x = (uint)Math.Clamp((p.X - DestMin) / CellSize * 65_535f, 0f, 65_535f);
        var y = (uint)Math.Clamp((p.Y - DestMin) / CellSize * 65_535f, 0f, 65_535f);
        return Spread(x) | (Spread(y) << 1);
    }

    private static ulong Spread(uint v)
    {
        ulong x = v;
        x = (x | (x << 16)) & 0x0000FFFF0000FFFFUL;
        x = (x | (x << 8)) & 0x00FF00FF00FF00FFUL;
        x = (x | (x << 4)) & 0x0F0F0F0F0F0F0F0FUL;
        x = (x | (x << 2)) & 0x3333333333333333UL;
        x = (x | (x << 1)) & 0x5555555555555555UL;
        return x;
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(static v => v).ToArray();
        return sorted.Length == 0 ? 0 : sorted[sorted.Length / 2];
    }

    private static int ArgInt(string[] args, string name, int fallback)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var v) ? v : fallback;
    }

    private static float ArgFloat(string[] args, string name, float fallback)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length && float.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    private static string[] ArgList(string[] args, string name, string fallback)
    {
        var i = Array.IndexOf(args, name);
        var raw = i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
