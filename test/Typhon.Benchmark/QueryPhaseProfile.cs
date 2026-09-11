using System;
using System.Diagnostics;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Typhon.Engine;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Benchmark;

/// <summary>
/// Splits a cluster spatial query into its four cost terms by CONTROLLED SWEEP rather than by instrumenting the enumerator.
/// </summary>
/// <remarks>
/// <para><b>Why a sweep and not a stopwatch inside <c>MoveNext</c>.</b> The terms in question are single-digit to low-tens of
/// nanoseconds each, inside a <c>ref struct</c> state machine whose whole point is that it does not allocate and does not call out.
/// Bracketing them would cost more than they cost and would suppress the inlining that makes them what they are. Sweeping the ONE
/// variable each term scales with, and reading the slope, measures them without touching the code under test.</para>
/// <para><b>The four terms</b>, in the order a query pays them:</para>
/// <list type="table">
///   <item><term>S0</term><description><c>WorldToCellRange</c> — once per query, independent of everything.</description></item>
///   <item><term>S1a</term><description>the cell walk, per cell the box COVERS, whether or not it holds anything.</description></item>
///   <item><term>S1b+S2</term><description>frame conversion + broadphase, per cell that holds clusters.</description></item>
///   <item><term>S3</term><description>narrowphase, per occupied slot of every cluster the broadphase admits.</description></item>
/// </list>
/// <para><b>Three arms, each a straight line in a different variable:</b></para>
/// <list type="bullet">
///   <item><b>Absent</b> — k x k cells over a region nothing was ever spawned into. <c>cost = S0 + k^2 * S1a</c>. The INTERCEPT of
///   this line is S0, which is the only way to see it: no query pays S0 without also paying at least one cell walk.</item>
///   <item><b>Occupied</b> — k x k cells holding one entity each. <c>cost = S0 + k^2 * (S1a + S1b + S2 + S3)</c>. Subtracting the
///   Absent line at the same k isolates what an OCCUPIED cell adds over an empty one.</item>
///   <item><b>Density</b> — one cell, e entities. <c>cost = const + e * S3 + ceil(e/64) * S2</c>. The slope in e is the narrowphase.</item>
/// </list>
/// <para><b>Arms are interleaved, not batched.</b> Round-robin across arms within each round, medians reported across rounds — a
/// batch-vs-batch layout drifts with whatever else the box is doing, which on a shared machine is a 2x effect.</para>
/// <para>Run: <c>dotnet run -c Release -- --query-phases</c>.</para>
/// </remarks>
static class QueryPhaseProfile
{
    private const float WorldSize = 10_000f;
    private const float CellSize = 100f;

    /// <summary>Cell coordinate of the region that stays empty — the Absent arm queries here.</summary>
    private const int AbsentOriginCell = 60;

    /// <summary>Cell coordinate of the region seeded one entity per cell — the Occupied arm queries here.</summary>
    private const int OccupiedOriginCell = 10;

    /// <summary>First of the Density arms' cells; arm <c>i</c> uses <c>DensityCell + i</c>, seeded with <c>Densities[i]</c> entities.</summary>
    private const int DensityCell = 50;

    /// <summary>Widest k of the two k x k sweeps. 32 x 32 = 1 024 cells, which is where the per-cell term is far above the noise floor.</summary>
    private const int MaxK = 32;

    private static readonly int[] Ks = [1, 2, 4, 8, 16, 32];
    private static readonly int[] Densities = [1, 8, 64, 256, 1024];

    internal static void Run(string[] args)
    {
        var rounds = ArgInt(args, "--rounds", 5);
        var iters = ArgInt(args, "--iters", 20_000);

        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
          .AddResourceRegistry()
          .AddMemoryAllocator()
          .AddEpochManager()
          .AddHighResolutionSharedTimer()
          .AddDeadlineWatchdog()
          .AddScopedManagedPagedMemoryMappedFile(o =>
          {
              o.DatabaseName = $"QueryPhases_{Environment.ProcessId}";
              o.DatabaseCacheSize = (ulong)(64L * 1024 * PagedMMF.PageSize);
              o.TestMode = true;
              o.PagesDebugPattern = false;
          })
          .AddInMemoryWalEngine();

        using var sp = sc.BuildServiceProvider();
        sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
        var dbe = sp.GetRequiredService<DatabaseEngine>();

        dbe.RegisterComponentFromAccessor<ClQBenchPos>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(
            worldMin: new Vector2(0, 0),
            worldMax: new Vector2(WorldSize, WorldSize),
            cellSize: CellSize));
        dbe.InitializeArchetypes();

        long tick = 1;
        Seed(dbe, ref tick);

        Console.WriteLine($"QueryPhaseProfile — world {WorldSize}, cell {CellSize}, rounds {rounds}, iters {iters}");
        Console.WriteLine();

        // Per-arm accumulated per-round nanoseconds. Index layout: [round][armIndex].
        var kCount = Ks.Length;
        var dCount = Densities.Length;
        // Two micro-arms after the sweeps: WorldToCellRange alone, then construct-and-dispose.
        var armCount = kCount * 2 + dCount + 2;
        var samples = new double[rounds][];
        var hits = new long[armCount];

        // One untimed pass so every arm's code is jitted and every page it touches is resident before the first measured round.
        using (EpochGuard.Enter(dbe.EpochManager))
        {
            for (var a = 0; a < armCount; a++)
            {
                RunArm(dbe, a, 200, out _);
            }
        }

        for (var r = 0; r < rounds; r++)
        {
            samples[r] = new double[armCount];
            // Round-robin, so a drift in machine load lands on every arm rather than on whichever ran last.
            for (var a = 0; a < armCount; a++)
            {
                using (EpochGuard.Enter(dbe.EpochManager))
                {
                    var start = Stopwatch.GetTimestamp();
                    RunArm(dbe, a, iters, out var hit);
                    var elapsed = Stopwatch.GetTimestamp() - start;
                    samples[r][a] = elapsed * 1_000_000_000d / Stopwatch.Frequency / iters;
                    hits[a] = hit;
                }
            }
        }

        var med = new double[armCount];
        var scratch = new double[rounds];
        for (var a = 0; a < armCount; a++)
        {
            for (var r = 0; r < rounds; r++)
            {
                scratch[r] = samples[r][a];
            }

            Array.Sort(scratch);
            med[a] = scratch[rounds / 2];
        }

        // ── Absent sweep ────────────────────────────────────────────────────────────────────────────────────────────
        Console.WriteLine("ABSENT — k x k cells, none of which exist. cost = S0 + k^2 * S1a");
        Console.WriteLine("   k   cells        ns/query      ns/cell   hits");
        for (var i = 0; i < kCount; i++)
        {
            var cells = (double)Ks[i] * Ks[i];
            Console.WriteLine($"{Ks[i],4} {cells,7:F0} {med[i],15:F1} {med[i] / cells,12:F2} {hits[i],6}");
        }

        // Slope from the two WIDEST rows only. A least-squares over every row weights k = 32 by k^4 against k = 1, so its
        // intercept is a fitting artefact rather than a measurement — the first run reported the Occupied arm's as -98 ns.
        var s1a = SlopeTop2(Ks, med, 0, kCount);
        var fixedAbsent = med[0] - s1a;
        Console.WriteLine($"   slope (k=16..32): S1a = {s1a:F2} ns/cell      fixed per query (k=1 row minus one walk) = {fixedAbsent:F1} ns");
        Console.WriteLine();

        // ── Occupied sweep ──────────────────────────────────────────────────────────────────────────────────────────
        Console.WriteLine("OCCUPIED — k x k cells, one entity each. cost = S0 + k^2 * (S1a + S1b + S2 + S3)");
        Console.WriteLine("   k   cells        ns/query      ns/cell   hits");
        for (var i = 0; i < kCount; i++)
        {
            var cells = (double)Ks[i] * Ks[i];
            var a = kCount + i;
            Console.WriteLine($"{Ks[i],4} {cells,7:F0} {med[a],15:F1} {med[a] / cells,12:F2} {hits[a],6}");
        }

        var perOccupied = SlopeTop2(Ks, med, kCount, kCount);
        Console.WriteLine($"   slope (k=16..32): per occupied cell = {perOccupied:F2} ns");
        Console.WriteLine($"   => S1b + S2 + S3(1 entity) = {perOccupied - s1a:F2} ns/cell over an absent one");
        Console.WriteLine();

        // ── Density sweep ───────────────────────────────────────────────────────────────────────────────────────────
        Console.WriteLine("DENSITY — ONE cell, e entities, query matches all. slope in e is the narrowphase");
        Console.WriteLine("   e        ns/query    ns/entity   hits");
        for (var i = 0; i < dCount; i++)
        {
            var e = Densities[i];
            var a = kCount * 2 + i;
            Console.WriteLine($"{e,4} {med[a],15:F1} {med[a] / e,12:F2} {hits[a],6}");
        }

        var lo = dCount - 2;
        var slopeS3 = (med[kCount * 2 + dCount - 1] - med[kCount * 2 + lo])
                      / (Densities[dCount - 1] - Densities[lo]);
        Console.WriteLine($"   slope over the top two rows: S3 = {slopeS3:F2} ns/entity (narrowphase + its share of broadphase)");
        Console.WriteLine();

        var s0 = med[kCount * 2 + dCount];
        var setup = med[kCount * 2 + dCount + 1];
        Console.WriteLine("FIXED COST, decomposed");
        Console.WriteLine($"   WorldToCellRange alone                  {s0,9:F2} ns   <- the node the walkthrough calls query-box-to-world-space");
        Console.WriteLine($"   construct query + enumerator + dispose  {setup,9:F2} ns   (includes the line above)");
        Console.WriteLine($"   fixed term read off the k=1 absent row  {fixedAbsent,9:F2} ns");
        Console.WriteLine();

        Console.WriteLine("SHARE OF A TYPICAL QUERY — 3x3 cells, one entity each");
        var typicalCells = 9d;
        var frameBroad = perOccupied - s1a - slopeS3;
        var typical = setup + typicalCells * perOccupied;
        Console.WriteLine($"   total {typical:F1} ns");
        Console.WriteLine($"     WorldToCellRange   {s0,9:F2} ns  {100 * s0 / typical,5:F1} %");
        Console.WriteLine($"     rest of the setup  {setup - s0,9:F2} ns  {100 * (setup - s0) / typical,5:F1} %");
        Console.WriteLine($"     cell walk  (S1a)   {typicalCells * s1a,9:F2} ns  {100 * typicalCells * s1a / typical,5:F1} %");
        Console.WriteLine($"     frame + broadphase {typicalCells * frameBroad,9:F2} ns  {100 * typicalCells * frameBroad / typical,5:F1} %");
        Console.WriteLine($"     narrowphase (S3)   {typicalCells * slopeS3,9:F2} ns  {100 * typicalCells * slopeS3 / typical,5:F1} %");

        dbe.Dispose();
    }

    /// <summary>
    /// Per-cell slope from the two WIDEST rows of a sweep — the only place the fixed per-query term is negligible.
    /// </summary>
    /// <remarks>
    /// A least-squares over every row weights <c>k = 32</c> by <c>k^4</c> against <c>k = 1</c>, so its intercept is a fitting
    /// artefact rather than a measurement: the first run of this profile reported the Occupied arm's as <b>-98 ns</b>. The fixed
    /// term is read from the <c>k = 1</c> row instead, where it is most of the cost, and the two micro-arms decompose it.
    /// </remarks>
    private static double SlopeTop2(int[] ks, double[] med, int offset, int count)
    {
        var xHi = (double)ks[count - 1] * ks[count - 1];
        var xLo = (double)ks[count - 2] * ks[count - 2];
        return (med[offset + count - 1] - med[offset + count - 2]) / (xHi - xLo);
    }

    /// <summary>Runs one arm's query <paramref name="iters"/> times, returning the last hit count so nothing is dead code.</summary>
    private static void RunArm(DatabaseEngine dbe, int armIndex, int iters, out long hits)
    {
        var kCount = Ks.Length;
        AABB2F box;
        if (armIndex < kCount)
        {
            box = CellBox(AbsentOriginCell, Ks[armIndex]);
        }
        else if (armIndex < kCount * 2)
        {
            box = CellBox(OccupiedOriginCell, Ks[armIndex - kCount]);
        }
        else if (armIndex < kCount * 2 + Densities.Length)
        {
            // One cell per density, so the arms differ in the variable being swept rather than all reading the fullest cell.
            box = CellBox(DensityCell + (armIndex - kCount * 2), 1);
        }
        else
        {
            box = CellBox(AbsentOriginCell, 1);
        }

        // Micro-arm: WorldToCellRange alone — the node the walkthrough labels "query box, world space".
        if (armIndex == kCount * 2 + Densities.Length)
        {
            var grid = dbe.SpatialGrid;
            var sink = 0;
            for (var it = 0; it < iters; it++)
            {
                grid.WorldToCellRange(box.MinX, box.MinY, float.NegativeInfinity, box.MaxX, box.MaxY, float.PositiveInfinity,
                    out var a0, out var a1, out var a2, out var a3, out var a4, out var a5);
                sink += a0 + a1 + a2 + a3 + a4 + a5;
            }

            hits = sink == int.MinValue ? 1 : 0;
            return;
        }

        // Micro-arm: construct the query and the enumerator, then dispose — no cell is walked.
        if (armIndex == kCount * 2 + Densities.Length + 1)
        {
            var sink = 0;
            for (var it = 0; it < iters; it++)
            {
                var e = dbe.ClusterSpatialQuery<ClQBenchUnit>().AABB<AABB2F>(in box);
                sink++;
                e.Dispose();
            }

            hits = sink == int.MinValue ? 1 : 0;
            return;
        }

        long total = 0;
        for (var it = 0; it < iters; it++)
        {
            var n = 0;
            foreach (var _ in dbe.ClusterSpatialQuery<ClQBenchUnit>().AABB<AABB2F>(in box))
            {
                n++;
            }

            total += n;
        }

        hits = iters == 0 ? 0 : total / iters;
    }

    /// <summary>World box covering cells <c>[origin, origin + k)</c> on both axes, inset so it cannot graze the neighbouring cell.</summary>
    private static AABB2F CellBox(int originCell, int k) => new()
    {
        MinX = originCell * CellSize + 1f,
        MinY = originCell * CellSize + 1f,
        MaxX = (originCell + k) * CellSize - 1f,
        MaxY = (originCell + k) * CellSize - 1f,
    };

    private static void Seed(DatabaseEngine dbe, ref long tick)
    {
        // Occupied region: one entity per cell over MaxK x MaxK, each at its cell's centre so every query box covering the cell hits it.
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var cy = OccupiedOriginCell; cy < OccupiedOriginCell + MaxK; cy++)
            {
                for (var cx = OccupiedOriginCell; cx < OccupiedOriginCell + MaxK; cx++)
                {
                    var x = cx * CellSize + CellSize * 0.5f;
                    var y = cy * CellSize + CellSize * 0.5f;
                    tx.Spawn<ClQBenchUnit>(ClQBenchUnit.Pos.Set(new ClQBenchPos
                    {
                        Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y },
                    }));
                }
            }

            tx.Commit();
        }

        dbe.WriteTickFence(tick++);

        // Density region: one cell per arm, cell DensityCell + i holding Densities[i] entities spread over its interior.
        var rng = new Random(1234);
        for (var d = 0; d < Densities.Length; d++)
        {
            var cell = DensityCell + d;
            var target = Densities[d];
            var spawned = 0;
            while (spawned < target)
            {
                var batch = Math.Min(512, target - spawned);
                using (var tx = dbe.CreateQuickTransaction())
                {
                    for (var i = 0; i < batch; i++)
                    {
                        var x = cell * CellSize + 5f + (float)rng.NextDouble() * (CellSize - 10f);
                        var y = cell * CellSize + 5f + (float)rng.NextDouble() * (CellSize - 10f);
                        tx.Spawn<ClQBenchUnit>(ClQBenchUnit.Pos.Set(new ClQBenchPos
                        {
                            Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y },
                        }));
                    }

                    tx.Commit();
                }

                dbe.WriteTickFence(tick++);
                spawned += batch;
            }
        }

        dbe.WriteTickFence(tick++);
    }

    private static int ArgInt(string[] args, string name, int fallback)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var v) ? v : fallback;
    }
}
