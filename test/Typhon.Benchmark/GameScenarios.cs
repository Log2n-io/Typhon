using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Typhon.Engine;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Benchmark;

[Component("Typhon.Benchmark.Game.Pos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct GamePos
{
    [Field]
    [SpatialIndex]
    public AABB3F Bounds;

    /// <summary>Indexed and non-unique, because a real archetype pays index staging on every migration.</summary>
    [Field]
    [Index(AllowMultiple = true)]
    public int Tag;
}

[Archetype]
partial class GameEntity : Archetype<GameEntity>
{
    public static readonly Comp<GamePos> Pos = Register<GamePos>();
}

/// <summary>
/// What the spatial layer does under the shapes real games actually have: real world extents in metres, real entity
/// sizes, real speeds, real query radii, at three populations each.
/// </summary>
/// <remarks>
/// <para><b>Not a CI test and not a unit test.</b> It runs minutes, it reports numbers rather than asserting them, and
/// its whole value is in the absolute figures. Run it deliberately:</para>
/// <code>cd test/Typhon.Benchmark &amp;&amp; dotnet run -c Release -- --game</code>
/// <para><b>The fence column is the SERIAL fence</b> (<c>WriteTickFence</c>), not the parallel DAG a host drives through
/// <c>TyphonRuntime</c>. That is stated on every table rather than quietly converted: the parallel fence measured 1.2–4×
/// faster depending on worker count in the step-14 campaign, so these fence numbers are an upper bound on a real host's.
/// Everything else here — query time, layout, cluster counts, migration counts — is unaffected by fence parallelism.</para>
/// </remarks>
internal static class GameScenarios
{
    /// <summary>One world, in the units its game actually uses.</summary>
    internal sealed class Scenario
    {
        public string Name = "";

        /// <summary>What this is modelling, and where the numbers came from.</summary>
        public string Provenance = "";

        /// <summary>Side of the simulated cube, in metres.</summary>
        public double WorldExtentM;

        /// <summary>Grid cell edge, in metres.</summary>
        public double CellSizeM;

        /// <summary>Half-extent of an entity's bounding box, in metres.</summary>
        public double EntityHalfExtentM;

        /// <summary>Typical speed of a MOVING entity, metres per second.</summary>
        public double SpeedMPerS;

        /// <summary>Simulation rate, ticks per second — what converts a speed into a per-tick displacement.</summary>
        public double TickHz;

        /// <summary>Fraction of the population that moves at all; the rest is scenery.</summary>
        public double MovingFraction;

        /// <summary>Radius of the game's own interest query, in metres.</summary>
        public double QueryRadiusM;

        /// <summary>Populations to sweep. A swarm world may leave it empty and sweep <see cref="SwarmPopulations"/> instead.</summary>
        public int[] Populations = [];

        /// <summary>
        /// (swarm count, ships per swarm) points. When non-empty the world is built as SWARMS — tight balls of entities
        /// separated by emptiness — instead of a uniform fill, and the total population of a point is the product.
        /// </summary>
        /// <remarks>
        /// A uniform fill is the wrong model for a space game and it hides the thing the cluster layer exists to do. Fill a
        /// cell uniformly and its clusters MUST tile it: C clusters cannot cover a cell with boxes shorter than
        /// <c>1/C^(1/d)</c> of the edge, so a cell holding one cluster reports ~100 % extent no matter how good the packer
        /// is, and "tight %" degenerates into a restatement of the density. Swarms break that: the occupied volume is a
        /// fraction of a percent of the cell, the clusters tile the SWARM rather than the cell, and the column finally
        /// measures packing instead of geometry.
        /// </remarks>
        public (int Swarms, int Ships)[] SwarmPopulations = [];

        /// <summary>
        /// Fraction of swarms that are BRAWLING — centre held, every ship moving inside the ball — against the rest, which
        /// travel as a rigid formation. The two are opposite workloads for the fence and both are real.
        /// </summary>
        /// <remarks>
        /// A formation in transit translates its clusters without disturbing which ship sits in which: every bound moves by
        /// the same vector, intra-cluster locality is preserved exactly, and the only migrations are the ones a cell
        /// boundary crossing forces. A brawl scrambles locality every tick inside a fixed volume and files no cell crossings
        /// at all. Running only one of them would flatter or libel the fence depending on which.
        /// </remarks>
        public double CombatFraction;

        /// <summary>
        /// Ship-to-ship spacing inside a swarm, in metres. The swarm's radius is derived — <c>spacing * ships^(1/3) / 2</c> —
        /// so a bigger fleet occupies a bigger volume at constant density rather than compressing into the same ball.
        /// </summary>
        public double SwarmSpacingM;

        /// <summary>
        /// Ask the interest query around a random ENTITY rather than a random point in the world.
        /// </summary>
        /// <remarks>
        /// Mandatory once the world is sparse. A uniformly drawn point in a mostly empty volume misses every swarm essentially
        /// always, so a random-point query would measure the cost of returning nothing and report zero hits — which says
        /// nothing about either structure. "What is around this ship" is also the query the game actually asks.
        /// </remarks>
        public bool QueryAroundEntity;

        /// <summary>Flat worlds (a city, a battle-royale island) put every entity in one Z plane; space does not.</summary>
        public bool Flat;
    }

    internal sealed class Row
    {
        public string Scenario = "";
        public string Arm = "";
        public int Entities;

        /// <summary>For a swarm world, " (20 x 100)" — the factorisation behind <see cref="Entities"/>. Empty otherwise.</summary>
        /// <remarks>Part of a point's identity: a swarm world can put two points at the same total (200 x 100 against 20 x 1 000).</remarks>
        public string Layout = "";
        public double SpawnMs;
        public double FirstFenceMs;
        public double FenceMedianMs;
        public double FenceP99Ms;
        public double QueryUs;
        public double QueryHits;

        /// <summary>Lowest and highest per-sweep <see cref="QueryHits"/> behind the median. Hits are a function of the seeded world and the fixed boxes
        /// alone, so every sweep of every arm must land on one number — and a median would hide the sweep that did not.</summary>
        public double QueryHitsMin;

        /// <inheritdoc cref="QueryHitsMin"/>
        public double QueryHitsMax;
        public double MigrationsPerTick;

        /// <summary>Median per-tick WHOLE migration cost in ms — the migrant loop plus the bulk index descent and the bulk EntityMap patch.</summary>
        /// <remarks>
        /// <b>Not <c>MigrationExecuteMs</c>, which brackets the loop alone.</b> Since #872 step 6 the loop only STAGES the index update and the EntityMap
        /// patch; both are applied in later phases, and the secondary index alone was measured at ~48 % of a migration's cost. A per-entity figure derived
        /// from the loop bracket under-reports by roughly half, which is the mistake this column exists not to make.
        /// </remarks>
        public double MigrationMedianMs;

        /// <summary>Whole migration cost per migrated entity, in nanoseconds — <see cref="MigrationMedianMs"/> over the migrations that tick.</summary>
        /// <remarks>
        /// Summed across workers on a parallel fence, so it is CPU and not latency; these scenarios drive the SERIAL fence, where the two coincide. Zero on
        /// a scenario whose steady state migrates nothing, which is a real answer and not a missing measurement.
        /// </remarks>
        public double MigrationNsPerEntity;

        /// <summary>Worker count the parallel arm ran at; 0 for a serial row.</summary>
        public int Workers;

        /// <summary>Median fence STALL in ms — the whole fence call on the tick thread, from <c>DatabaseEngine.LastFenceStallMs</c>.</summary>
        /// <remarks>
        /// <para><b>This is the interruption, and it is what the parallel sweep reports.</b> No user system runs inside it at any worker count. It is
        /// deliberately NOT <c>LastFenceSpanMs</c>: that one starts at Prep's <c>Prepare</c> and so omits the serial prep the fence runs first on the tick
        /// thread — the context reset, the dormancy drain and <c>ProcessTableFence</c> over every component table. That prep is single-threaded by
        /// construction, so a sweep across worker counts reading only the span claims a speed-up on a fraction of the stall. <see cref="FenceDagSpanMs"/>
        /// carries the span beside it precisely so the fraction is visible rather than assumed away.</para>
        /// <para><b>A span, not a sum.</b> <see cref="MigrationMedianMs"/> beside it is CPU summed across workers, so W workers each busy for 1 ms report W
        /// there and 1 here. Confusing them is how an 8 ms budget once bought one repair unit.</para>
        /// </remarks>
        public double FenceSpanMs;

        /// <summary>Median parallel-DAG span in ms — the six phases plus the scheduler's gaps, from <c>DatabaseEngine.LastFenceSpanMs</c>.</summary>
        /// <remarks>
        /// Reported only so <see cref="FenceSpanMs"/> minus this one names the serial prep: the part of the stall no worker count shrinks, which is the
        /// fence's Amdahl fraction. On its own it flatters the parallel arm.
        /// </remarks>
        public double FenceDagSpanMs;

        /// <summary>CPU over span for the three migration phases — how many workers' worth of CPU one unit of span bought.</summary>
        public double MigrationParallelism;

        /// <summary>True when the median fence span exceeded the pacing interval, so ticks overlapped and the row describes an overrunning engine.</summary>
        public bool Overran;
        public int Clusters;
        public int LiveCells;
        public double EntitiesPerCell;
        public double ClustersPerCell;
        public double TightnessPct;
        public double SlotOccupancyPct;
        public int PromotedCells;
        public string Failure = "";
    }

    /// <summary>
    /// The cluster count at which a cell promotes to a per-cell R-Tree, for the arm being run. <see cref="int.MaxValue"/>
    /// never promotes; <c>1</c> promotes every cell that holds anything.
    /// </summary>
    private static int PromoteThreshold = SpatialOptions.DefaultCellTreePromoteThreshold;

    /// <summary>
    /// The two arms: never promote (the engine's default), and promote every cell — the second answers "what does the tree do HERE", not "should this
    /// cell have a tree".
    /// </summary>
    private static readonly (string Name, int Threshold)[] Arms =
    [
        ("scan", int.MaxValue),
        ("tree", 1),
    ];


    private const int WarmTicks = 5;

    private const int MeasuredTicks = 30;

    private const int QueriesPerRound = 64;

    /// <summary>
    /// Independent sweeps of each (scenario, population, arm) point, reported as the per-field MEDIAN.
    /// </summary>
    /// <remarks>
    /// <b>A single-shot figure from this harness cannot be compared against another run.</b> Three sweeps of the same point were measured spreading up to
    /// 106 % on the fence and 54 % on the query on this desktop, so a report meant to be read against an earlier one has to take the median or it reports
    /// the machine's mood. Anything under about 1.3x between two runs is noise. Settable with <c>--repeats</c>; 1 turns it off for a quick look.
    /// </remarks>
    private static int Repeats = 3;

    /// <summary>
    /// Runs one point <see cref="Repeats"/> times and returns a row whose every numeric field is the median of the sweeps.
    /// </summary>
    /// <remarks>
    /// <b>Per FIELD, not per sweep.</b> Picking the sweep whose fence was median and reporting all of its other columns would let one noisy query time ride
    /// in on an unrelated field's ranking. The layout columns (clusters, cells, tightness) are deterministic for a fixed seed, so their median is just their
    /// value; taking it anyway costs nothing and means no column is special-cased.
    /// </remarks>
    private static Row MedianOfRepeats(Scenario s, int entities, int swarms, int ships, int workerCount = 0)
    {
        var sweeps = new List<Row>(Repeats);
        for (var i = 0; i < Math.Max(1, Repeats); i++)
        {
            var r = workerCount == 0 ? RunScenario(s, entities, swarms, ships) : RunParallelScenario(s, entities, workerCount, swarms, ships);
            if (r.Failure.Length > 0)
            {
                return r;   // a failure is not a number to take the median of
            }

            sweeps.Add(r);
        }

        var first = sweeps[0];
        var median = new Row
        {
            Scenario = first.Scenario,
            Entities = first.Entities,
            PromotedCells = first.PromotedCells,
            Workers = first.Workers,
            // NOT first.Overran. That is sweep 0's verdict on sweep 0's own stall, while the stall printed beside it is the MEDIAN across sweeps — so the
            // flag and the number it annotates came from different runs, and a median row could print an unflagged figure over budget or a flagged one under
            // it. Recomputed from the median below, after the medians exist.
            Overran = false,
            FenceSpanMs = Med(sweeps, static r => r.FenceSpanMs),
            FenceDagSpanMs = Med(sweeps, static r => r.FenceDagSpanMs),
            MigrationParallelism = Med(sweeps, static r => r.MigrationParallelism),
            LiveCells = first.LiveCells,
            Clusters = first.Clusters,
            SpawnMs = Med(sweeps, static r => r.SpawnMs),
            FirstFenceMs = Med(sweeps, static r => r.FirstFenceMs),
            FenceMedianMs = Med(sweeps, static r => r.FenceMedianMs),
            FenceP99Ms = Med(sweeps, static r => r.FenceP99Ms),
            QueryUs = Med(sweeps, static r => r.QueryUs),
            QueryHits = Med(sweeps, static r => r.QueryHits),
            QueryHitsMin = Lowest(sweeps, static r => r.QueryHits),
            QueryHitsMax = Highest(sweeps, static r => r.QueryHits),
            MigrationsPerTick = Med(sweeps, static r => r.MigrationsPerTick),
            MigrationMedianMs = Med(sweeps, static r => r.MigrationMedianMs),
            MigrationNsPerEntity = Med(sweeps, static r => r.MigrationNsPerEntity),
            EntitiesPerCell = Med(sweeps, static r => r.EntitiesPerCell),
            ClustersPerCell = Med(sweeps, static r => r.ClustersPerCell),
            TightnessPct = Med(sweeps, static r => r.TightnessPct),
            SlotOccupancyPct = Med(sweeps, static r => r.SlotOccupancyPct),
        };

        // Recomputed here, from the median, so the flag describes the row it is printed on. The parallel arm is the only one paced, so the serial arm keeps
        // the `false` the initialiser gave it.
        median.Overran = median.Workers != 0 && median.FenceSpanMs > 1000d / ParallelPacingHz;
        return median;
    }

    /// <summary>Worker counts the parallel arm sweeps. 16 is this desktop's physical core count.</summary>
    private static readonly int[] WorkerCounts = [1, 2, 4, 8, 16];

    /// <summary>
    /// Pacing rate for the parallel arm, in Hz — deliberately NOT the scenario's own tick rate.
    /// </summary>
    /// <remarks>
    /// <para><b>The scenario's rate would make this arm take hours and measure the wrong thing.</b> EVE ticks at 1 Hz, so thirty measured ticks is thirty
    /// SECONDS per point, times five worker counts times three populations. And pacing does not affect what is being measured: the fence span is however long
    /// the fence takes, and the tick rate only decides how long the engine idles between fences.</para>
    /// <para><b>What the rate must not do is let ticks overlap.</b> A rate the fence cannot keep up with makes the figures describe an overrunning engine
    /// rather than the work. 50 Hz gives a 20 ms budget, comfortably above the heaviest serial fence measured here (12.5 ms), and
    /// <see cref="Row.Overran"/> flags any row where the median span exceeded it anyway rather than letting it pass silently.</para>
    /// </remarks>
    private const int ParallelPacingHz = 50;

    /// <summary>
    /// The same world under the PARALLEL fence, at one worker count.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this arm has to exist.</b> Every other row in this report comes from <c>WriteTickFence</c>, which is the SERIAL fence — the report has
    /// always said so and called itself an upper bound, carrying a "1.2–4x faster in parallel" figure inherited from a different campaign on a different
    /// workload. An inherited range that wide cannot be checked against anything. This measures it.</para>
    /// <para><b>Motion runs inside a callback system</b>, not on the harness thread, because the parallel fence is dispatched by the runtime at the end of
    /// the tick the callback belongs to. Driving the world from outside would race the fence it is supposed to feed.</para>
    /// </remarks>
    private static Row RunParallelScenario(Scenario s, int entities, int workerCount, int swarms, int ships)
    {
        var row = RunScenarioCore(s, entities, workerCount, swarms, ships);
        row.Workers = workerCount;
        return row;
    }

    /// <summary>
    /// Drives <see cref="WarmTicks"/> + <see cref="MeasuredTicks"/> ticks of the PARALLEL fence and samples each measured one.
    /// </summary>
    /// <remarks>
    /// <para><b>The motion runs inside the tick, not around it.</b> A callback system on the public track moves the world, and the runtime dispatches the
    /// fence DAG at the end of that same tick. Stepping the world from the harness thread instead would race the fence it is meant to feed, and the numbers
    /// would describe the race.</para>
    /// <para><b>The span comes from the engine, not from a stopwatch out here.</b> <c>DatabaseEngine.LastFenceSpanMs</c> is published by the runtime after
    /// each parallel fence — Prep's start to the last phase that dispatched, plus the scheduler's gaps. There is nothing to time from outside: the fence
    /// runs on the tick thread's own schedule, and a stopwatch around the callback would measure the callback.</para>
    /// <para><b>Sampled one tick late, deliberately.</b> The callback runs BEFORE the fence its tick will dispatch, so what it reads is the previous tick's
    /// completed fence — which is the same ordering the serial arm gets for free by reading after <c>WriteTickFence</c> returns.</para>
    /// </remarks>
    private static void RunParallelTicks(DatabaseEngine dbe, Scenario s, int workerCount, Row row, Action stepWorld, Action<double> sample)
    {
        var ticks = 0;
        var parallelisms = new List<double>(MeasuredTicks);
        var dagSpans = new List<double>(MeasuredTicks);
        Exception unhandled = null;

        using (var runtime = TyphonRuntime.Create(dbe, schedule =>
               {
                   schedule.PublicTrack.DeclareDag("Game").CallbackSystem("Move", _ =>
                   {
                       var n = Interlocked.Increment(ref ticks);

                       // Stop doing work the moment the sweep has what it needs. SpinUntil below releases as soon as the counter passes the target, and the
                       // runtime then shuts down while a tick may still be in flight — a callback that opens a transaction into a tearing-down engine throws
                       // ObjectDisposedException on the scheduler's own wait handle, which is a harness bug that reads exactly like an engine defect.
                       if (n > WarmTicks + MeasuredTicks + 1)
                       {
                           return;
                       }

                       if (n > WarmTicks + 1)
                       {
                           lock (parallelisms)
                           {
                               // The STALL, not the span: the sweep's question is how long user code was stopped, and the span starts only after the
                               // fence's serial prep has already run on this thread. The span is collected beside it so the report can show the
                               // difference, which is the part of the stall no worker count removes.
                               sample(dbe.LastFenceStallMs);
                               dagSpans.Add(dbe.LastFenceSpanMs);
                               parallelisms.Add(dbe.LastFenceMigrationParallelism);
                           }
                       }

                       stepWorld();
                   });
               }, new RuntimeOptions
               {
                   WorkerCount = workerCount,
                   BaseTickRate = ParallelPacingHz,
                   EnableParallelFence = true,
               }))
        {
            // ObjectDisposedException is excluded rather than reported: it is what a tick still in flight sees when Shutdown races it, and recording it
            // would fail the row for the harness's own teardown. Every other exception is a real finding and fails the row loudly.
            runtime.Scheduler.UnhandledExceptionCallback = (_, _, ex) =>
            {
                if (ex is not ObjectDisposedException)
                {
                    Interlocked.CompareExchange(ref unhandled, ex, null);
                }
            };
            runtime.Start();
            SpinWait.SpinUntil(() => Volatile.Read(ref ticks) >= WarmTicks + MeasuredTicks + 1, TimeSpan.FromSeconds(120));
            runtime.Shutdown();
        }

        if (unhandled != null)
        {
            row.Failure = $"parallel fence threw: {unhandled.GetType().Name}: {unhandled.Message}";
            return;
        }

        lock (parallelisms)
        {
            var sum = 0d;
            foreach (var p in parallelisms)
            {
                sum += p;
            }

            row.MigrationParallelism = parallelisms.Count == 0 ? 0 : sum / parallelisms.Count;

            dagSpans.Sort();
            row.FenceDagSpanMs = dagSpans.Count == 0 ? 0 : dagSpans[dagSpans.Count / 2];
        }
    }

    private static double Lowest(List<Row> rows, Func<Row, double> select)
    {
        var lowest = double.PositiveInfinity;
        foreach (var r in rows)
        {
            lowest = Math.Min(lowest, select(r));
        }

        return lowest;
    }

    private static double Highest(List<Row> rows, Func<Row, double> select)
    {
        var highest = double.NegativeInfinity;
        foreach (var r in rows)
        {
            highest = Math.Max(highest, select(r));
        }

        return highest;
    }

    private static double Med(List<Row> rows, Func<Row, double> select)
    {
        var vals = new double[rows.Count];
        for (var i = 0; i < rows.Count; i++)
        {
            vals[i] = select(rows[i]);
        }

        Array.Sort(vals);
        return vals[vals.Length / 2];
    }

    internal static void Run(string[] args)
    {
        var only = ArgString(args, "--scenario", "");
        var scale = ArgFloat(args, "--scale", 1f);
        Repeats = Math.Max(1, (int)ArgFloat(args, "--repeats", Repeats));
        var scenarios = Build();
        var rows = new List<Row>();
        var parallelRows = new List<Row>();
        var skipParallel = Array.IndexOf(args, "--no-parallel") >= 0;

        Console.WriteLine("── Game scenarios ──────────────────────────────────────────────────────");
        var sw = Stopwatch.StartNew();
        foreach (var s in scenarios)
        {
            if (only.Length > 0 && !s.Name.Contains(only, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Console.WriteLine();
            Console.WriteLine($"  {s.Name} — world {s.WorldExtentM:N0} m, cell {s.CellSizeM:N0} m, entity {2 * s.EntityHalfExtentM:N1} m, "
                + $"{s.SpeedMPerS:N0} m/s at {s.TickHz:N0} Hz, query r={s.QueryRadiusM:N0} m"
                + (s.SwarmPopulations.Length > 0 ? $", swarms at {s.SwarmSpacingM:N0} m spacing, {100 * s.CombatFraction:N0} % brawling" : ""));

            // A point is (total, swarms, ships per swarm). A uniform world leaves the last two at zero; a swarm world can put two points at the SAME total
            // with different factorisations (200 x 100 against 20 x 1 000) — the comparison that separates "more occupied cells" from "denser occupied
            // cells" — so the label, not the total, identifies a point from here on.
            var points = new List<(int Total, int Swarms, int Ships, string Label)>();
            foreach (var (swarmCount, ships) in s.SwarmPopulations)
            {
                var scaledSwarms = Math.Max(1, (int)(swarmCount * scale));
                points.Add((scaledSwarms * ships, scaledSwarms, ships, $" ({scaledSwarms} x {ships:N0})"));
            }

            foreach (var n in s.Populations)
            {
                points.Add((Math.Max(16, (int)(n * scale)), 0, 0, ""));
            }

            foreach (var (scaled, swarms, shipsPerSwarm, label) in points)
            {
                foreach (var (armName, threshold) in Arms)
                {
                    PromoteThreshold = threshold;
                    var row = MedianOfRepeats(s, scaled, swarms, shipsPerSwarm);
                    row.Arm = armName;
                    row.Layout = label;
                    rows.Add(row);
                    Console.WriteLine(row.Failure.Length > 0
                        ? $"    n={scaled,-8}{label} {armName,-6} FAILED: {row.Failure}"
                        : $"    n={scaled,-8}{label} {armName,-6} fence {row.FenceMedianMs,7:F2} ms  query {row.QueryUs,8:F1} us ({row.QueryHits,6:F0} hits)  "
                          + $"clusters {row.Clusters,6} ({row.ClustersPerCell,6:F0}/cell)  cells {row.LiveCells,6}  tight {row.TightnessPct,5:F2}%  "
                          + $"promoted {row.PromotedCells,5}");
                }

                if (!skipParallel)
                {
                    // Scan arm only. The promotion arm answers "what does the tree do here", which is orthogonal to how the fence spreads across workers,
                    // and running both would double a sweep that is already the longest thing in this harness.
                    PromoteThreshold = Arms[0].Threshold;
                    foreach (var w in WorkerCounts)
                    {
                        var prow = MedianOfRepeats(s, scaled, swarms, shipsPerSwarm, w);
                        prow.Arm = $"W={w}";
                        prow.Layout = label;
                        parallelRows.Add(prow);
                        Console.WriteLine(prow.Failure.Length > 0
                            ? $"    n={scaled,-8}{label} W={w,-4} FAILED: {prow.Failure}"
                            : $"    n={scaled,-8}{label} W={w,-4} stall {prow.FenceSpanMs,7:F2} ms (dag {prow.FenceDagSpanMs,6:F2})"
                              + $"{(prow.Overran ? " !OVERRAN" : "        ")}  "
                              + $"migCPU {prow.MigrationMedianMs,7:F3} ms  {prow.MigrationNsPerEntity,7:F0} ns/ent  par {prow.MigrationParallelism,5:F2}x");
                    }
                }

                // Every promoted arm against the scan, every sweep: a structure may change what an answer costs, never the answer (SQ-01).
                var scan = rows.FindLast(r => r.Entities == scaled && r.Layout == label && r.Arm == "scan" && r.Scenario == s.Name);
                foreach (var (armName, _) in Arms)
                {
                    var other = armName == "scan"
                        ? null
                        : rows.FindLast(r => r.Entities == scaled && r.Layout == label && r.Arm == armName && r.Scenario == s.Name);
                    if (scan == null || other == null || scan.Failure.Length > 0 || other.Failure.Length > 0)
                    {
                        continue;
                    }

                    var verdict = !HitsAgree(scan, other)
                        ? $"!! HIT MISMATCH scan {HitRange(scan)} vs {armName} {HitRange(other)} — the two structures disagree (SQ-01)"
                        : $"   {armName}/scan query {(other.QueryUs > 0 ? scan.QueryUs / other.QueryUs : 0):F2}x, "
                          + $"fence {(other.FenceMedianMs > 0 ? scan.FenceMedianMs / other.FenceMedianMs : 0):F2}x";
                    Console.WriteLine($"    {"",-8} {"",-6} {verdict}");
                }
            }
        }

        sw.Stop();
        Console.WriteLine();
        Console.WriteLine($"  {rows.Count} runs in {sw.Elapsed.TotalSeconds:F1}s");
        var path = WriteReport(scenarios, rows, parallelRows, sw.Elapsed);
        Console.WriteLine($"  report -> {path}");
    }

    /// <summary>
    /// The four worlds. Every number is in metres and metres per second, and every one of them is a real game's, so a
    /// row below can be read as "this is what the engine does for that game" rather than as an abstract population sweep.
    /// </summary>
    private static List<Scenario> Build() =>
    [
        new Scenario
        {
            Name = "EVE Online fleet engagements in system space",
            Provenance = "**The world here is EMPTY and the population is CLUMPED, which is the shape a space game actually "
                + "has and the previous version of this scenario did not.** That version filled a 200 km cube uniformly at "
                + "40 km cells: every cell held ~16 evenly scattered ships, one cluster covered each cell by necessity, and "
                + "the tightness column read 98 % everywhere — a restatement of the uniform fill, not a measurement of the "
                + "packer. Sourced numbers: server tick is 1 Hz, stretched to 10x by time dilation under load "
                + "(imperium.news/understanding-eve-online-server-tick); CCP's grid was a 250 km cube before 2016 and is "
                + "~8 000 km from centre on each axis since (eveonline.com/news/view/grid-sizes-you); real engagements are "
                + "B-R5RB 2014, ~7 500 players and 75 titans over 21 h, and M2-XFE 2020, 5 000+ players and 257 titans (both "
                + "eveonline.com news); sub-warp speeds of 300-400 m/s are a community figure, not a CCP spec; titans run "
                + "~15.75-18 km long (Imperium News, community estimate), so the 500 m hull here is battleship class, the "
                + "fleet-fight median. MINE, and larger than CCP's grid on purpose: the 500 000 km world, the 1 000 km cell, "
                + "the 100 km query radius, the 2 km ship-to-ship spacing, and the swarm counts. The volume is system-scale "
                + "rather than grid-scale so that the grid is genuinely sparse — 500^3 = 125 million cells of which a few "
                + "dozen are ever touched — which is the regime the cell layer was built for and the one the old numbers "
                + "never entered.",
            WorldExtentM = 500_000_000,      // 500 000 km — system-scale, so the grid is mostly void
            CellSizeM = 1_000_000,           // 1 000 km cells: 500^3 = 125 M possible, a few dozen live
            EntityHalfExtentM = 250,         // a 500 m hull — battleship class, the fleet-fight median
            SpeedMPerS = 300,                // sub-warp capital speed
            TickHz = 1,                      // EVE's server tick
            MovingFraction = 1,              // every ship in an engagement is under way; CombatFraction splits HOW
            QueryRadiusM = 100_000,          // 100 km — targeting/overview range, a tenth of a cell
            SwarmPopulations = [(20, 100), (200, 100), (20, 1_000), (100, 2_500)],
            CombatFraction = 0.5,            // half the fleets brawling in place, half under way in formation
            SwarmSpacingM = 2_000,           // four hull-lengths between ships; sets the swarm radius from its size
            QueryAroundEntity = true,        // a random point in this volume hits nothing, ever
            Flat = false,                    // space is not flat
        },
        new Scenario
        {
            Name = "Open-world city",
            Provenance = "GTA V's map is 75.84 km² total, 48.15 km² of it land; the 9 km square here is 81 km², the same "
                + "order. Rockstar publishes NO pedestrian, vehicle or building counts and no simulation radius — density is "
                + "driven by popcycle.dat multipliers with no public numbers (gtamods.com/wiki/Popcycle.dat) — so the "
                + "populations swept here are MINE, not the game's. On-foot speed is community-measured at 6.1-6.7 m/s; "
                + "cars reach 62-77 m/s; the 12 m/s used is an urban-traffic average and is my choice. GTA Online is "
                + "peer-to-peer with no published tick rate; 30 Hz is a community estimate. The 300 m interest radius is "
                + "mine — no title publishes one.",
            WorldExtentM = 9_000,
            CellSizeM = 250,
            EntityHalfExtentM = 1.0,         // a person or a car, ~2 m box
            SpeedMPerS = 12,                 // traffic; pedestrians are the static-ish remainder
            TickHz = 30,
            MovingFraction = 0.35,           // the rest is scenery and parked vehicles
            QueryRadiusM = 300,              // streaming / interest radius
            Populations = [20_000, 80_000, 250_000],
            Flat = true,
        },
        new Scenario
        {
            Name = "Battle royale island",
            Provenance = "PUBG's Erangel and Miramar are 8 x 8 km (ggrecon.com); 100 players is the official cap. Sprint is "
                + "6.3 m/s from the official wiki (pubg.wiki.gg/wiki/Movement_Speed), which is what the 6 m/s here rounds. "
                + "PUBG's server tick is 60 Hz — the highest of the genre, against Fortnite's 30 and Warzone's 20 — so this "
                + "has the TIGHTEST per-tick budget of the five worlds at 16.7 ms. The play zone shrinks from 3 994 m "
                + "diameter to nothing over nine phases (pubg.wiki.gg/wiki/The_Playzone); that is not modelled here, and it "
                + "would concentrate the population far past what these rows show. The 250 m replication radius is mine — "
                + "no title publishes one. Populations above 100 are ground loot and are my figures.",
            WorldExtentM = 8_000,
            CellSizeM = 300,
            EntityHalfExtentM = 0.5,         // a player or a loot item
            SpeedMPerS = 6,                  // sprint; vehicles are faster but rare
            TickHz = 60,                     // PUBG's official server tick — the tightest budget of the five worlds
            MovingFraction = 0.05,           // 100 players and some vehicles against thousands of static items
            QueryRadiusM = 250,
            Populations = [5_000, 20_000, 60_000],
            Flat = true,
        },
        new Scenario
        {
            Name = "City, few big cells",
            Provenance = "The same city population, partitioned into a handful of very large cells instead of a fine grid. "
                + "This is the shape the per-cell R-Tree was built for — tens of thousands of entities in ONE cell, so the "
                + "cell holds enough clusters to cross the promotion threshold — and it is the direct test of whether the "
                + "tree pays there. It is NOT a recommended configuration; it is the configuration that reaches the tree.",
            WorldExtentM = 9_000,
            CellSizeM = 4_500,               // four cells, so ~60 000 entities and ~1 200 clusters in each
            EntityHalfExtentM = 1.0,
            SpeedMPerS = 12,
            TickHz = 30,
            MovingFraction = 0.35,
            QueryRadiusM = 300,
            Populations = [20_000, 80_000, 250_000],
            Flat = true,
        },
        new Scenario
        {
            Name = "Large-scale RTS battle",
            Provenance = "Supreme Commander: Forged Alliance — 20 x 20 km standard maps, a fixed 10 Hz simulation tick "
                + "(FAForever forums). Chosen because it is the closest analogue to Typhon's own fixed-tick ECS loop and "
                + "because nearly EVERY entity moves every tick, the hardest shape for a spatial index to maintain. The unit "
                + "cap is 500 by default and 1 500 in FAForever, with community reports of 5 000+ at reduced sim speed — so "
                + "the 10 000 to 150 000 swept here is deliberately one to two orders ABOVE what the genre ships, which is "
                + "the volumetry question rather than the fidelity one. Unit sizes, speeds and weapon ranges are not "
                + "published anywhere I could find; the 6 m box, 5 m/s and 120 m query radius are mine.",
            WorldExtentM = 20_000,
            CellSizeM = 500,
            EntityHalfExtentM = 3.0,         // a tank-sized unit
            SpeedMPerS = 5,
            TickHz = 10,
            MovingFraction = 0.90,           // an RTS army is nearly all in motion
            QueryRadiusM = 120,              // weapon / vision range
            Populations = [10_000, 50_000, 150_000],
            Flat = true,
        },
    ];

    private static Row RunScenario(Scenario s, int entities, int swarms, int ships) => RunScenarioCore(s, entities, 0, swarms, ships);

    /// <param name="workerCount">0 drives the SERIAL fence through <c>WriteTickFence</c>; anything else builds a <c>TyphonRuntime</c> and drives the
    /// parallel DAG at that width.</param>
    /// <param name="swarms">Swarm count for a swarm world; 0 builds the uniform fill.</param>
    /// <param name="shipsPerSwarm">Ships in each swarm; 0 builds the uniform fill.</param>
    /// <inheritdoc cref="RunScenario"/>
    private static Row RunScenarioCore(Scenario s, int entities, int workerCount, int swarms, int shipsPerSwarm)
    {
        var row = new Row { Scenario = s.Name, Entities = entities };
        try
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
                  o.DatabaseName = $"Game_{Environment.ProcessId}";
                  // 1 GiB of pages: the largest scenario here is a quarter of a million entities with a secondary index,
                  // and the page-cache back-pressure timeout is what a too-small cache looks like from the outside.
                  o.DatabaseCacheSize = (ulong)(128L * 1024 * PagedMMF.PageSize);
                  o.TestMode = true;
                  o.PagesDebugPattern = false;
              })
              .AddInMemoryWalEngine();
            using var sp = sc.BuildServiceProvider();
            sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
            var dbe = sp.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<GamePos>();

            var extent = (float)s.WorldExtentM;
            var cell = (float)s.CellSizeM;
            dbe.ConfigureSpatialGrid(new SpatialGridConfig(
                new Vector3(0, 0, 0),
                new Vector3(extent, extent, s.Flat ? cell : extent),
                cell));
            dbe.ClusterCellTreePromoteThreshold = PromoteThreshold;
            if (PromoteThreshold == 1)
            {
                // Forcing promotion means promoting cells the tightness gate would refuse — which is the point of the arm:
                // it answers "what does the tree do HERE", not "should this cell have a tree".
                dbe.ClusterCellTreePromoteTightness = 1f;
            }

            dbe.InitializeArchetypes();

            var rng = new Random(20260906);
            var half = (float)s.EntityHalfExtentM;
            var xs = new float[entities];
            var ys = new float[entities];
            var zs = new float[entities];
            var vx = new float[entities];
            var vy = new float[entities];
            var vz = new float[entities];
            var step = (float)(s.SpeedMPerS / s.TickHz);

            // Swarm state. Empty for a uniform world; for a swarm world, one entry per swarm plus a per-ship owner index.
            var isSwarmWorld = swarms > 0 && shipsPerSwarm > 0;
            var swarmOf = isSwarmWorld ? new int[entities] : [];
            var scx = new float[Math.Max(1, swarms)];
            var scy = new float[Math.Max(1, swarms)];
            var scz = new float[Math.Max(1, swarms)];
            var svx = new float[Math.Max(1, swarms)];
            var svy = new float[Math.Max(1, swarms)];
            var svz = new float[Math.Max(1, swarms)];
            var swarmRadius = 0f;
            var brawling = new bool[Math.Max(1, swarms)];

            if (isSwarmWorld)
            {
                // Constant ship-to-ship spacing, so a bigger fleet takes a bigger volume instead of compressing: a 2 500-ship fleet that occupied the same
                // ball as a 100-ship gang would be reporting a density no game produces.
                swarmRadius = (float)(s.SwarmSpacingM * Math.Cbrt(shipsPerSwarm) / 2d);
                var margin = swarmRadius + half;
                var i = 0;
                for (var g = 0; g < swarms; g++)
                {
                    scx[g] = (float)(rng.NextDouble() * (extent - (2 * margin))) + margin;
                    scy[g] = (float)(rng.NextDouble() * (extent - (2 * margin))) + margin;
                    scz[g] = s.Flat ? cell * 0.5f : (float)(rng.NextDouble() * (extent - (2 * margin))) + margin;
                    brawling[g] = rng.NextDouble() < s.CombatFraction;
                    if (!brawling[g])
                    {
                        // A formation in transit: ONE heading for the whole swarm, applied rigidly to every ship in it.
                        var a0 = rng.NextDouble() * Math.PI * 2;
                        var e0 = s.Flat ? 0d : (rng.NextDouble() - 0.5) * Math.PI;
                        svx[g] = (float)(Math.Cos(a0) * Math.Cos(e0) * step);
                        svy[g] = (float)(Math.Sin(a0) * Math.Cos(e0) * step);
                        svz[g] = s.Flat ? 0f : (float)(Math.Sin(e0) * step);
                    }

                    for (var k = 0; k < shipsPerSwarm && i < entities; k++, i++)
                    {
                        // Rejection-sampled into the BALL rather than the cube: a cube of ships has corners, and the corners are what a cluster bound would
                        // report as extent the fleet does not actually occupy.
                        double dx, dy, dz;
                        do
                        {
                            dx = (rng.NextDouble() * 2) - 1;
                            dy = (rng.NextDouble() * 2) - 1;
                            dz = s.Flat ? 0d : (rng.NextDouble() * 2) - 1;
                        }
                        while ((dx * dx) + (dy * dy) + (dz * dz) > 1d);

                        swarmOf[i] = g;
                        xs[i] = scx[g] + (float)(dx * swarmRadius);
                        ys[i] = scy[g] + (float)(dy * swarmRadius);
                        zs[i] = scz[g] + (float)(dz * swarmRadius);

                        if (brawling[g])
                        {
                            // Manoeuvring inside the ball. The swarm holds station; which ship is next to which does not.
                            var a = rng.NextDouble() * Math.PI * 2;
                            var e = s.Flat ? 0d : (rng.NextDouble() - 0.5) * Math.PI;
                            vx[i] = (float)(Math.Cos(a) * Math.Cos(e) * step);
                            vy[i] = (float)(Math.Sin(a) * Math.Cos(e) * step);
                            vz[i] = s.Flat ? 0f : (float)(Math.Sin(e) * step);
                        }
                        else
                        {
                            vx[i] = svx[g];
                            vy[i] = svy[g];
                            vz[i] = svz[g];
                        }
                    }
                }
            }
            else
            {
                for (var i = 0; i < entities; i++)
                {
                    xs[i] = (float)(rng.NextDouble() * (extent - (2 * half))) + half;
                    ys[i] = (float)(rng.NextDouble() * (extent - (2 * half))) + half;
                    zs[i] = s.Flat ? cell * 0.5f : (float)(rng.NextDouble() * (extent - (2 * half))) + half;

                    if (rng.NextDouble() < s.MovingFraction)
                    {
                        // A persistent heading, so an entity crosses cells the way a moving thing does rather than jittering in place.
                        var a = rng.NextDouble() * Math.PI * 2;
                        var e = s.Flat ? 0d : (rng.NextDouble() - 0.5) * Math.PI;
                        vx[i] = (float)(Math.Cos(a) * Math.Cos(e) * step);
                        vy[i] = (float)(Math.Sin(a) * Math.Cos(e) * step);
                        vz[i] = s.Flat ? 0f : (float)(Math.Sin(e) * step);
                    }
                }
            }

            var ids = new EntityId[entities];
            var sw = Stopwatch.StartNew();
            using (var tx = dbe.CreateQuickTransaction())
            {
                for (var i = 0; i < entities; i++)
                {
                    var p = default(GamePos);
                    Write(ref p, xs[i], ys[i], zs[i], half, i);
                    ids[i] = tx.Spawn<GameEntity>(GameEntity.Pos.Set(in p));
                }

                tx.Commit();
            }

            sw.Stop();
            row.SpawnMs = sw.Elapsed.TotalMilliseconds;

            sw.Restart();
            dbe.WriteTickFence(1);
            sw.Stop();
            row.FirstFenceMs = sw.Elapsed.TotalMilliseconds;

            var fenceMs = new List<double>(MeasuredTicks);
            var migrationMs = new List<double>(MeasuredTicks);
            var migrationCount = new List<double>(MeasuredTicks);

            // Hoisted out of the tick loop so the serial and parallel arms move the world with the SAME code. Two copies of a bounce-and-integrate would
            // diverge silently, and a difference between the arms would then be a difference in the workload rather than in the fence.
            void StepWorld()
            {
                using var tx = dbe.CreateQuickTransaction();
                {
                    // Written through OpenMut rather than the ClusterRef spatial barrier: the barrier is AABB2F-only, and
                    // three of these four worlds are flat but one is not. OpenMut also routes Prep down the DIRTY-bitmap
                    // branch, which is the fuller of the two — shadow drain, zone-map recompute and dormancy sweep all run,
                    // so a fence number here is not flattered by the barrier's shortcuts.
                    // Swarm centres move first, so a transit formation's ships all read the SAME already-bounced heading and translate rigidly. Bouncing
                    // each ship against the world box independently would tear a formation apart the moment it reached an edge.
                    if (isSwarmWorld)
                    {
                        for (var g = 0; g < swarms; g++)
                        {
                            if (brawling[g])
                            {
                                continue;
                            }

                            var m = swarmRadius + half;
                            if (scx[g] + svx[g] < m || scx[g] + svx[g] > extent - m) { svx[g] = -svx[g]; }
                            if (scy[g] + svy[g] < m || scy[g] + svy[g] > extent - m) { svy[g] = -svy[g]; }
                            if (!s.Flat && (scz[g] + svz[g] < m || scz[g] + svz[g] > extent - m)) { svz[g] = -svz[g]; }
                            scx[g] += svx[g];
                            scy[g] += svy[g];
                            scz[g] += svz[g];
                        }
                    }

                    for (var i = 0; i < entities; i++)
                    {
                        if (vx[i] == 0f && vy[i] == 0f && vz[i] == 0f)
                        {
                            continue;
                        }

                        if (isSwarmWorld)
                        {
                            var g = swarmOf[i];
                            if (brawling[g])
                            {
                                // Reflect off the INSIDE of the swarm ball, about the radial normal, so the brawl stays a brawl: speed is preserved and the
                                // fleet neither disperses nor collapses onto its centre.
                                var nx = (xs[i] + vx[i]) - scx[g];
                                var ny = (ys[i] + vy[i]) - scy[g];
                                var nz = s.Flat ? 0f : (zs[i] + vz[i]) - scz[g];
                                var d2 = (nx * nx) + (ny * ny) + (nz * nz);
                                if (d2 > swarmRadius * swarmRadius && d2 > 0f)
                                {
                                    var inv = 1f / MathF.Sqrt(d2);
                                    nx *= inv;
                                    ny *= inv;
                                    nz *= inv;
                                    var dot = 2f * ((vx[i] * nx) + (vy[i] * ny) + (vz[i] * nz));
                                    vx[i] -= dot * nx;
                                    vy[i] -= dot * ny;
                                    vz[i] -= dot * nz;
                                }
                            }
                            else
                            {
                                // Rigid: the swarm's heading is the ship's heading, re-read each tick so a world-box bounce of the centre turns the whole
                                // formation at once.
                                vx[i] = svx[g];
                                vy[i] = svy[g];
                                vz[i] = svz[g];
                            }
                        }
                        else
                        {
                            // Bounce off the world box rather than wrapping: a wrap is a teleport, and a teleport is a different
                            // workload (a migration storm) from the steady motion these scenarios are about.
                            if (xs[i] + vx[i] < half || xs[i] + vx[i] > extent - half) { vx[i] = -vx[i]; }
                            if (ys[i] + vy[i] < half || ys[i] + vy[i] > extent - half) { vy[i] = -vy[i]; }
                            if (!s.Flat && (zs[i] + vz[i] < half || zs[i] + vz[i] > extent - half)) { vz[i] = -vz[i]; }
                        }

                        xs[i] += vx[i];
                        ys[i] += vy[i];
                        zs[i] += vz[i];
                        ref var p = ref tx.OpenMut(ids[i]).Write(GameEntity.Pos);
                        Write(ref p, xs[i], ys[i], zs[i], half, i);
                    }

                    tx.Commit();
                }
            }

            // Read after a fence, never during one: every counter on the snapshot is per-tick and reset at the top of the next fence, so a sample taken
            // once at the end describes one arbitrary tick out of thirty.
            void SampleTick(double spanMs)
            {
                fenceMs.Add(spanMs);
                var t = dbe.GetSpatialTelemetry(Archetype<GameEntity>.Metadata.ArchetypeId);
                migrationMs.Add(t.MigrationTotalMs);
                migrationCount.Add(t.MigrationCount);
            }

            if (workerCount == 0)
            {
                for (var tick = 0; tick < WarmTicks + MeasuredTicks; tick++)
                {
                    StepWorld();
                    sw.Restart();
                    dbe.WriteTickFence(2 + tick);
                    sw.Stop();
                    if (tick >= WarmTicks)
                    {
                        SampleTick(sw.Elapsed.TotalMilliseconds);
                    }
                }
            }
            else
            {
                RunParallelTicks(dbe, s, workerCount, row, StepWorld, SampleTick);
            }

            fenceMs.Sort();
            row.FenceMedianMs = fenceMs.Count == 0 ? 0 : fenceMs[fenceMs.Count / 2];
            row.FenceP99Ms = fenceMs.Count == 0 ? 0 : fenceMs[Math.Min(fenceMs.Count - 1, (int)(fenceMs.Count * 0.99))];

            // Summed then divided, rather than a median of per-tick quotients: a tick that migrated two entities would otherwise weigh as much as one that
            // migrated two thousand, and the quiet ticks are the majority in most of these worlds.
            var totalMigrations = 0d;
            var totalMigrationMs = 0d;
            for (var i = 0; i < migrationMs.Count; i++)
            {
                totalMigrationMs += migrationMs[i];
                totalMigrations += migrationCount[i];
            }

            migrationMs.Sort();
            row.MigrationMedianMs = migrationMs.Count == 0 ? 0 : migrationMs[migrationMs.Count / 2];
            row.MigrationNsPerEntity = totalMigrations > 0 ? totalMigrationMs * 1e6 / totalMigrations : 0;

            var telemetry = dbe.GetSpatialTelemetry(Archetype<GameEntity>.Metadata.ArchetypeId);
            row.MigrationsPerTick = migrationCount.Count == 0
                ? telemetry.MigrationCount
                : totalMigrations / migrationCount.Count;

            if (workerCount != 0)
            {
                // The fence column IS the stall on this arm — there is no outer stopwatch — so the two are the same number under two names, and the second
                // exists so the report can print it beside the pacing budget it has to fit inside.
                row.FenceSpanMs = row.FenceMedianMs;
                row.Overran = row.FenceSpanMs > 1000d / ParallelPacingHz;
            }

            // A FRESH generator with a fixed seed, not the one the world was built from: both arms must ask the same
            // boxes, or a hit-count difference between them says nothing about the structures.
            MeasureQuery(dbe, s, new Random(90_210), xs, ys, zs, out var qUs, out var qHits);
            row.QueryUs = qUs;
            row.QueryHits = qHits;
            row.QueryHitsMin = qHits;
            row.QueryHitsMax = qHits;

            Snapshot(dbe, s, row);
            return row;
        }
        catch (Exception ex)
        {
            row.Failure = $"{ex.GetType().Name}: {ex.Message}";
            return row;
        }
    }

    /// <summary>
    /// The game's own query: a box of the scenario's interest radius around a random entity, which is what an interest
    /// manager, a targeting scan or an AI perception pass actually asks.
    /// </summary>
    /// <remarks>
    /// Warmed by WALL TIME rather than by iteration count. Tiered compilation promotes on a background thread after a
    /// delay, and a fixed iteration warm-up returns before that lands — which in an earlier measurement made a cold arm
    /// look 17× faster than a warm one.
    /// </remarks>
    private static void MeasureQuery(DatabaseEngine dbe, Scenario s, Random rng, float[] xs, float[] ys, float[] zs, out double us, out double hits)
    {
        var cs = dbe._archetypeStates[Archetype<GameEntity>.Metadata.ArchetypeId].ClusterState;
        var r = (float)s.QueryRadiusM;
        var extent = (float)s.WorldExtentM;

        // A FIXED set of boxes, generated once from the caller's seeded generator and reused by every arm.
        //
        // The earlier shape drew a fresh box per iteration and warmed for a fixed WALL TIME, so an arm that queried faster
        // completed more warm-up iterations, left the generator at a different position, and then measured a different
        // NUMBER of different boxes. Comparing hit counts across arms that way reported "the two structures disagree" for
        // three of fifteen points — in both directions, which is the tell — when the structures agreed and the harness did
        // not. Hits must be a function of the world and the boxes alone.
        var boxes = new (float X, float Y, float Z)[QueriesPerRound];
        for (var i = 0; i < boxes.Length; i++)
        {
            if (s.QueryAroundEntity)
            {
                // Centred on a real entity. In a world this empty a uniformly drawn point returns nothing essentially always, and "how fast do we return
                // nothing" is not the question either structure is being asked.
                var k = rng.Next(xs.Length);
                boxes[i] = (xs[k], ys[k], zs[k]);
            }
            else
            {
                boxes[i] = ((float)(rng.NextDouble() * extent), (float)(rng.NextDouble() * extent), s.Flat ? 0f : (float)(rng.NextDouble() * extent));
            }
        }

        // Warmed by WALL TIME rather than by iteration count: tiered compilation promotes on a background thread after a
        // delay, and a fixed iteration warm-up returns before that lands — which in an earlier measurement made a cold arm
        // look 17x faster than a warm one. The warm-up walks the same boxes, so it moves no shared state.
        var sw = Stopwatch.StartNew();
        var spins = 0;
        while (sw.ElapsedMilliseconds < 200)
        {
            spins += OneQuery(dbe, cs, boxes[spins % boxes.Length], extent, r, s.Flat);
            spins++;
        }

        long found = 0;
        sw.Restart();
        for (var i = 0; i < boxes.Length; i++)
        {
            found += OneQuery(dbe, cs, boxes[i], extent, r, s.Flat);
        }

        sw.Stop();
        us = sw.Elapsed.TotalMilliseconds * 1000d / boxes.Length;
        hits = (double)found / boxes.Length;
    }

    private static int OneQuery(DatabaseEngine dbe, ArchetypeClusterState cs, (float X, float Y, float Z) at, float extent, float radius, bool flat)
    {
        // A flat world asks a 2D question, which reaches a 3D-capable index as an infinite Z band — the case #905 fixed, kept on purpose.
        var zLo = flat ? float.NegativeInfinity : at.Z - radius;
        var zHi = flat ? float.PositiveInfinity : at.Z + radius;
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var n = 0;
        foreach (var hit in cs.QueryAabb(dbe.SpatialGrid, at.X - radius, at.Y - radius, zLo, at.X + radius, at.Y + radius, zHi))
        {
            n += hit.EntityId == 0 ? 0 : 1;
        }

        return n;
    }

    private static void Snapshot(DatabaseEngine dbe, Scenario s, Row row)
    {
        var cs = dbe._archetypeStates[Archetype<GameEntity>.Metadata.ArchetypeId].ClusterState;
        row.Clusters = cs.ActiveClusterCount;
        row.PromotedCells = cs.PromotedCellCount;

        var live = 0;
        for (var key = 0; key < dbe.SpatialGrid.CellCount; key++)
        {
            if (dbe.SpatialGrid.GetCell(key).EntityCount > 0)
            {
                live++;
            }
        }

        row.LiveCells = live;
        row.EntitiesPerCell = live > 0 ? (double)row.Entities / live : 0d;
        row.ClustersPerCell = live > 0 ? (double)row.Clusters / live : 0d;

        var cell = (float)s.CellSizeM;
        var total = 0d;
        var counted = 0;
        for (var i = 0; i < cs.ActiveClusterCount; i++)
        {
            var id = cs.ActiveClusterIds[i];
            if ((uint)id >= (uint)cs.ClusterAabbs.Length)
            {
                continue;
            }

            ref var b = ref cs.ClusterAabbs[id];
            if (float.IsPositiveInfinity(b.MinX))
            {
                continue;
            }

            var e = Math.Max(b.MaxX - b.MinX, b.MaxY - b.MinY);
            if (!s.Flat)
            {
                e = Math.Max(e, b.MaxZ - b.MinZ);
            }

            if (float.IsFinite(e) && e >= 0f)
            {
                total += e;
                counted++;
            }
        }

        row.TightnessPct = counted == 0 ? 0d : 100d * total / counted / cell;
        var slots = System.Numerics.BitOperations.PopCount(cs.Layout.FullMask);
        row.SlotOccupancyPct = row.Clusters > 0 ? 100d * row.Entities / (row.Clusters * (double)slots) : 0d;
    }

    /// <summary>
    /// The parallel-fence sweep: what a real host sees, against the serial figure every other table in this report carries.
    /// </summary>
    /// <remarks>
    /// <b>This section exists because the rest of the report could not answer the question it kept raising.</b> Every other table here is the SERIAL fence,
    /// captioned "treat these as an upper bound" and carrying an inherited "1.2-4x faster in parallel" from a different campaign on a different workload. A
    /// range that wide, measured elsewhere, is not a number a reader can use.
    /// </remarks>

    /// <summary>Emits one (world, population, width) row of the sweep table.</summary>
    /// <remarks>
    /// <paramref name="parallelism"/> is passed as 1 for the serial arm rather than read off the row: one thread means summed CPU and elapsed time are the
    /// same number, and dividing by a parallelism the serial path never published would silently rescale it.
    /// </remarks>
    private static void WriteSweepRow(TextWriter w, string scenario, int entities, string width, double stallMs, double migrationsPerTick,
        double migCpuNs, double parallelism, bool overran)
    {
        var nsPerEntity = entities > 0 ? stallMs * 1e6 / entities : 0d;
        var migWallNs = parallelism > 0 ? migCpuNs / parallelism : migCpuNs;
        var stall = $"{stallMs:N2}{(overran ? " ⚠" : "")}";
        var mig = migrationsPerTick > 0 ? $"{migCpuNs:N0} | {migWallNs:N0}" : "— | —";

        // Two decimals under ten, none above. A world that migrates 0.4 entities a tick was rounding to "0" while still printing a per-migration cost beside
        // it, which reads as a division by zero rather than as the thin denominator it is — and a per-migration figure off a fraction of an event is noise
        // the reader has to be able to SEE is noise.
        var migrations = migrationsPerTick > 0 && migrationsPerTick < 10 ? $"{migrationsPerTick:N2}" : $"{migrationsPerTick:N0}";
        w.WriteLine($"| {scenario} | {entities:N0} | {width} | **{stall}** | {nsPerEntity:N0} | {migrations} | {mig} | {parallelism:N2}x |");
    }

    /// <summary>Did every sweep of both arms return one and the same hit count?</summary>
    /// <remarks>Hits are a function of the seeded world and the fixed boxes alone, so two sweeps of one arm disagreeing is as much a defect as two arms
    /// disagreeing — and a median would hide either.</remarks>
    private static bool HitsAgree(Row scan, Row other) =>
        SameHits(scan.QueryHitsMin, scan.QueryHitsMax) && SameHits(other.QueryHitsMin, other.QueryHitsMax) && SameHits(scan.QueryHitsMin, other.QueryHitsMin);

    /// <summary>Two per-query hit means over the same box set are equal to within half a hit in total — which is to say exactly.</summary>
    private static bool SameHits(double a, double b) => Math.Abs(a - b) * QueriesPerRound < 0.5d;

    /// <summary>A row's hits as totals over the box set: one number, or the lowest and highest sweep when they differ.</summary>
    private static string HitRange(Row r)
    {
        var lo = r.QueryHitsMin * QueriesPerRound;
        var hi = r.QueryHitsMax * QueriesPerRound;
        return SameHits(r.QueryHitsMin, r.QueryHitsMax) ? $"{lo:N0}" : $"{lo:N0}–{hi:N0}";
    }

    /// <summary>
    /// Compares every promoted arm's query hits with the scan's, point by point and sweep by sweep, and reports a finding only when it measures one.
    /// </summary>
    /// <remarks>
    /// <para>The <c>scan</c> and <c>tree</c> arms ask the same boxes — one fixed-seed generator per arm — of the same seeded world, so their
    /// hit counts must be identical in every sweep. Any difference is a correctness defect in a query path (rule SQ-01), not noise, and it is compared
    /// exactly: half a hit over the whole box set is the tolerance.</para>
    /// <para><b>This replaces a section that was written as fixed prose.</b> It described #905 — a promoted cell returning nothing for a query with an
    /// infinite axis bound — and kept printing that finding into every report after #905 was fixed, because it was added to the harness in the same commit
    /// that fixed it. A finding a harness asserts without measuring is a claim with a date on it; this one outlived the bug it described. Every sentence this
    /// section prints is computed from the rows.</para>
    /// </remarks>
    private static void WriteTreeScanAgreement(TextWriter w, List<Scenario> scenarios, List<Row> rows)
    {
        var points = 0;
        var flatPoints = 0;
        var mismatches = new List<string>();
        foreach (var s in scenarios)
        {
            foreach (var scan in rows)
            {
                if (scan.Scenario != s.Name || scan.Arm != "scan" || scan.Failure.Length > 0)
                {
                    continue;
                }

                foreach (var other in rows)
                {
                    if (other.Scenario != s.Name || other.Entities != scan.Entities || other.Layout != scan.Layout || other.Arm != "tree"
                        || other.Failure.Length > 0)
                    {
                        continue;
                    }

                    points++;
                    flatPoints += s.Flat ? 1 : 0;
                    if (!HitsAgree(scan, other))
                    {
                        mismatches.Add($"| {s.Name} | {scan.Entities:N0}{scan.Layout} | {other.Arm} | {HitRange(scan)} | **{HitRange(other)}** |");
                    }
                }
            }
        }

        if (points == 0)
        {
            w.WriteLine("## Tree against scan (SQ-01) — not measured");
            w.WriteLine();
            w.WriteLine("No population point ran a promoted arm beside the scan, so this report says nothing about whether the promoted tree and the");
            w.WriteLine("linear scan agree.");
            w.WriteLine();
            return;
        }

        if (mismatches.Count == 0)
        {
            w.WriteLine("## Tree against scan (SQ-01) — agree");
            w.WriteLine();
            w.WriteLine($"The forced tree asked the same query boxes of the same world as `scan` and returned **identical hit counts in every sweep, at all "
                + $"{points} population points**.");
            if (flatPoints > 0)
            {
                w.WriteLine($"{flatPoints} of them are flat worlds, whose 2D question reaches the index as an infinite Z band — the case #905 fixed.");
            }

            w.WriteLine();
            w.WriteLine("Query time is not compared here. The two arms run on two engines, and repair admission is priced from wall-clock costs, so their "
                + "layouts drift apart and a time ratio between them measures the layouts as much as the structures; `--cell-tree-crossover` times both on "
                + "one engine.");
            w.WriteLine();
            return;
        }

        w.WriteLine($"## Finding: a promoted arm and the scan DISAGREE at {mismatches.Count} of {points} comparisons (SQ-01)");
        w.WriteLine();
        w.WriteLine("Every arm asks the same boxes of the same seeded world, so every sweep's hit count must be equal. A query path is returning a");
        w.WriteLine("wrong answer, or one arm's sweeps disagree among themselves; the scan is the reference, because it has no pruning to get wrong.");
        w.WriteLine();
        w.WriteLine("| scenario | entities | arm | scan hits | arm hits |");
        w.WriteLine("|---|---|---|---|---|");
        foreach (var m in mismatches)
        {
            w.WriteLine(m);
        }

        w.WriteLine();
        w.WriteLine($"Hits are totals over the {QueriesPerRound} boxes; a range is the lowest and highest sweep.");
        w.WriteLine();
    }

    private static void WriteParallelSection(TextWriter w, List<Scenario> scenarios, List<Row> rows, List<Row> parallelRows)
    {
        if (parallelRows.Count == 0)
        {
            return;
        }

        w.WriteLine("## The parallel fence, measured (W = 1/2/4/8/16)");
        w.WriteLine();
        w.WriteLine("Every other table in this report is the **serial** fence, and has always said so and called itself an upper bound while carrying an");
        w.WriteLine("inherited \"1.2-4x faster in parallel\" from a different campaign on a different workload. This section measures it instead.");
        w.WriteLine();
        w.WriteLine("**One row per (world, population, worker count)**, because that is the shape of the question: at this size and this width, how long is");
        w.WriteLine("the engine in the way, and what does one migration cost? A wide table with a column per W could not carry the second number.");
        w.WriteLine();
        w.WriteLine("### What each column is");
        w.WriteLine();
        w.WriteLine("| column | meaning |");
        w.WriteLine("|---|---|");
        w.WriteLine("| `W` | fence worker count. `serial` is the separate single-threaded path — `WriteTickFence` driven straight from the host with no scheduler and no DAG, so it has no worker count to vary. `W=1` is NOT the same thing: it is the parallel fence narrowed to one worker, still paying DAG dispatch, epoch scopes and per-chunk change sets. |");
        w.WriteLine("| **`stall ms`** | **how long the engine stopped user code.** The whole fence call timed on the tick thread (`LastFenceStallMs`): the epoch fence window opens and no user system runs until it closes. This is the number to hold against a frame budget. |");
        w.WriteLine("| `ns/ent` | `stall ms` divided by the population — what one entity PRESENT costs per tick. Multiply by a planned population to size a world. Not per migrant: the fence walks everything every tick and only a fraction moves. |");
        w.WriteLine("| `migr/tick` | migrations that actually happened per tick, the denominator of the next two columns. Zero is a real answer — a settled world migrates nothing and the fence still costs what it costs. |");
        w.WriteLine("| **`mig ns CPU`** | **average execution time of one migration, in CPU nanoseconds** — `MigrationTotalMs` over `migr/tick`. Summed across workers, so W workers each busy for one nanosecond report W. It is what a migration COSTS, and it is the figure that inflates with W. |");
        w.WriteLine("| `mig ns wall` | the same migration in elapsed nanoseconds — `mig ns CPU` divided by `par`. This is what a migration adds to the stall, and it is the one that should fall as W rises. |");
        w.WriteLine("| `par` | CPU over span across the three migration phases: how many workers’ worth of CPU one unit of span bought. |");
        w.WriteLine();
        w.WriteLine($"**Pacing is {ParallelPacingHz} Hz for every world here, NOT the game's own tick rate.** EVE ticks at 1 Hz, so its own rate would make");
        w.WriteLine("this sweep take an hour and measure idling. Pacing does not change the stall; it only decides how long the engine waits between fences.");
        w.WriteLine($"A row whose stall exceeded the {1000d / ParallelPacingHz:N0} ms pacing budget is flagged ⚠ — its ticks overlapped, so it describes an");
        w.WriteLine("overrunning engine rather than the work.");
        w.WriteLine();
        w.WriteLine("The serial row's `mig ns CPU` and `mig ns wall` are the same number by construction: one thread, so summed CPU IS elapsed.");
        w.WriteLine();
        w.WriteLine("| scenario | entities | W | stall ms | ns/ent | migr/tick | mig ns CPU | mig ns wall | par |");
        w.WriteLine("|---|---|---|---|---|---|---|---|---|");

        foreach (var s in scenarios)
        {
            var pops = new List<(int Entities, string Layout)>();
            foreach (var r in parallelRows)
            {
                if (r.Scenario == s.Name && !pops.Contains((r.Entities, r.Layout)))
                {
                    pops.Add((r.Entities, r.Layout));
                }
            }

            foreach (var (n, layout) in pops)
            {
                // The serial baseline leads each block rather than sitting in its own table: the comparison a reader wants is one line away, and the row
                // labels itself `serial` so it cannot be mistaken for a width.
                var serial = rows.Find(r => r.Scenario == s.Name && r.Entities == n && r.Layout == layout && r.Arm == "scan");
                if (serial != null)
                {
                    WriteSweepRow(w, s.Name + layout, n, "serial", serial.FenceMedianMs, serial.MigrationsPerTick, serial.MigrationNsPerEntity, 1d, false);
                }

                for (var i = 0; i < WorkerCounts.Length; i++)
                {
                    var r = parallelRows.Find(x => x.Scenario == s.Name && x.Entities == n && x.Layout == layout && x.Workers == WorkerCounts[i]);
                    if (r == null || r.Failure.Length > 0)
                    {
                        w.WriteLine($"| {s.Name}{layout} | {n:N0} | {WorkerCounts[i]} | — | — | — | — | — | — |");
                        continue;
                    }

                    WriteSweepRow(w, s.Name + layout, n, WorkerCounts[i].ToString(), r.FenceSpanMs, r.MigrationsPerTick, r.MigrationNsPerEntity,
                        r.MigrationParallelism, r.Overran);
                }
            }
        }

        w.WriteLine();
        w.WriteLine("⚠ = the span exceeded the pacing budget, so that row's ticks overlapped.");
        w.WriteLine();
    }

    private static string WriteReport(List<Scenario> scenarios, List<Row> rows, List<Row> parallelRows, TimeSpan elapsed)
    {
        var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "claude", "scratch"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"game-scenarios-{DateTime.Now:yyyy-MM-dd}.md");
        using var w = new StreamWriter(path, false);

        w.WriteLine("# Typhon spatial layer under real game workloads");
        w.WriteLine();
        w.WriteLine($"Generated {DateTime.Now:yyyy-MM-dd HH:mm} by `dotnet run -c Release -- --game` ({elapsed.TotalSeconds:F0} s).");
        w.WriteLine("Source: `test/Typhon.Benchmark/GameScenarios.cs`. Not a CI test — an instrument.");
        w.WriteLine();
        w.WriteLine($"Every row is the **median of {Repeats} independent sweeps** of that point, per field. Three sweeps of the same point were");
        w.WriteLine("measured spreading up to 106 % on the fence and 54 % on the query on this desktop, so a single-shot figure cannot be");
        w.WriteLine("compared against an earlier report — anything under about 1.3x between two runs here is noise, not a change.");
        w.WriteLine();
        w.WriteLine("## How to read this");
        w.WriteLine();
        w.WriteLine("Every world is in **metres** and **metres per second**, at the game's own tick rate, so a per-tick");
        w.WriteLine("displacement here is the one that game actually applies. Entities carry a real bounding box, not a point.");
        w.WriteLine();
        w.WriteLine("- **fence** is the per-tick spatial maintenance: drift detection, relocation, repair, migration, AABB refresh.");
        w.WriteLine("  It is the **serial** fence (`WriteTickFence`). A host drives the parallel DAG through `TyphonRuntime`, which");
        w.WriteLine("  measured 1.2-4x faster depending on worker count, so treat these as an upper bound.");
        w.WriteLine("- **query** is one interest query at the scenario's own radius, against the cluster index, warmed by wall time.");
        w.WriteLine("- **mig ms** is the WHOLE migration for one tick — the migrant loop plus the bulk index descent and the bulk EntityMap");
        w.WriteLine("  patch — as a median over the measured ticks. It reads 0.000 wherever fewer than half the ticks migrate anything, which");
        w.WriteLine("  is a true statement about the median and a useless one about the cost.");
        w.WriteLine("- **mig ns/ent** is that cost divided by the migrations that produced it, SUMMED over the measured ticks rather than");
        w.WriteLine("  taken as a median, so a busy tick is not outvoted by the quiet ones. This is the column to read where migration is rare.");
        w.WriteLine("- **tight** is the mean cluster bound as a percentage of the cell edge — the selectivity proxy. Lower prunes better.");
        w.WriteLine("- **occ** is slot occupancy over the archetype's real slots per cluster.");
        w.WriteLine("- **budget**: at the scenario's tick rate, one tick is " + "`1000 / Hz` ms — the fence has to fit inside it alongside everything else the game does.");
        w.WriteLine();

        foreach (var s in scenarios)
        {
            var mine = rows.FindAll(r => r.Scenario == s.Name);
            if (mine.Count == 0)
            {
                continue;
            }

            var tickBudgetMs = 1000d / s.TickHz;
            w.WriteLine($"## {s.Name}");
            w.WriteLine();
            w.WriteLine(s.Provenance);
            w.WriteLine();
            w.WriteLine($"| | |");
            w.WriteLine($"|---|---|");
            w.WriteLine($"| World | {s.WorldExtentM:N0} m {(s.Flat ? "square (flat)" : "cube")} |");
            w.WriteLine($"| Cell edge | {s.CellSizeM:N0} m |");
            w.WriteLine($"| Entity box | {2 * s.EntityHalfExtentM:N1} m |");
            w.WriteLine($"| Speed | {s.SpeedMPerS:N0} m/s ({s.MovingFraction:P0} of entities move) |");
            w.WriteLine($"| Tick | {s.TickHz:N0} Hz — {tickBudgetMs:N1} ms per tick |");
            w.WriteLine($"| Displacement per tick | {s.SpeedMPerS / s.TickHz:N2} m ({100d * s.SpeedMPerS / s.TickHz / s.CellSizeM:N2} % of a cell) |");
            w.WriteLine($"| Query radius | {s.QueryRadiusM:N0} m ({100d * s.QueryRadiusM / s.CellSizeM:N1} % of a cell), asked around "
                + $"{(s.QueryAroundEntity ? "a random ENTITY" : "a random point")} |");
            if (s.SwarmPopulations.Length > 0)
            {
                w.WriteLine($"| Grid | {100d * s.CellSizeM / s.WorldExtentM:N3} % of the world per cell edge — "
                    + $"{Math.Pow(s.WorldExtentM / s.CellSizeM, s.Flat ? 2 : 3):N0} cells possible |");
                w.WriteLine($"| Swarm | ships {s.SwarmSpacingM:N0} m apart; radius = spacing x ships^(1/3) / 2 |");
                w.WriteLine($"| Swarm motion | {100 * s.CombatFraction:N0} % brawling in place, "
                    + $"{100 * (1 - s.CombatFraction):N0} % under way as a rigid formation |");
            }
            w.WriteLine();
            w.WriteLine("| entities | arm | spawn ms | fence ms | fence p99 | % of tick | mig ms | mig ns/ent | query us | hits | live cells | ent/cell "
                + "| clusters | tight % | occ % | mig/tick | promoted |");
            w.WriteLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
            foreach (var r in mine)
            {
                if (r.Failure.Length > 0)
                {
                    w.WriteLine($"| {r.Entities:N0}{r.Layout} | {r.Arm} | ❌ {r.Failure} | | | | | | | | | | | | | | |");
                    continue;
                }

                w.WriteLine($"| {r.Entities:N0}{r.Layout} | {r.Arm} | {r.SpawnMs:N0} | {r.FenceMedianMs:N2} | {r.FenceP99Ms:N2} | "
                    + $"{100d * r.FenceMedianMs / tickBudgetMs:N1} % | "
                    + $"{r.MigrationMedianMs:N3} | {r.MigrationNsPerEntity:N0} | "
                    + $"{r.QueryUs:N1} | {r.QueryHits:N0} | {r.LiveCells:N0} | {r.EntitiesPerCell:N1} | {r.Clusters:N0} | {r.TightnessPct:N1} | "
                    + $"{r.SlotOccupancyPct:N1} | {r.MigrationsPerTick:N0} | {r.PromotedCells} |");
            }

            w.WriteLine();
        }

        WriteParallelSection(w, scenarios, rows, parallelRows);

        WriteTreeScanAgreement(w, scenarios, rows);

        w.WriteLine("## Caveats");
        w.WriteLine();
        w.WriteLine("- Serial fence, as above. The parallel figure is the one a host sees.");
        w.WriteLine("- One archetype per world. A real game splits entities across several, and the fence is per-archetype,");
        w.WriteLine("  so a world of the same size split four ways does less work per archetype and more scheduling.");
        w.WriteLine("- Motion is a persistent heading with a bounce off the world box. Real movement is more correlated");
        w.WriteLine("  (roads, orbits, fleet manoeuvres), which makes clusters TIGHTER than this, not looser.");
        w.WriteLine("- The static fraction never moves at all, which is what the scenery in a city or the ground loot on a");
        w.WriteLine("  battle-royale island actually does.");
        w.WriteLine("- Cell sizes were chosen to land near the measured optimum of tens of entities per cell at the middle");
        w.WriteLine("  population, not tuned per row.");
        w.WriteLine("- **Provenance is per scenario above.** EVE and PUBG numbers are official or wiki-sourced. GTA V is the");
        w.WriteLine("  weakest: Rockstar publishes no agent counts, no simulation radius and no tick rate, so those are mine.");
        w.WriteLine("  Supreme Commander tick and map size are community-sourced and mutually consistent; its unit sizes and");
        w.WriteLine("  query radii are not published anywhere, so those are mine too. Where a number is mine it says so.");
        w.WriteLine("- The RTS populations are one to two orders above the genre's real unit caps (500 default, 1 500 in");
        w.WriteLine("  FAForever). That is deliberate: the question asked here is how the engine reacts to VOLUMETRY, not");
        w.WriteLine("  whether it can run Supreme Commander.");
        return path;
    }

    private static void Write(ref GamePos p, float x, float y, float z, float half, int tag)
    {
        p.Bounds = new AABB3F { MinX = x - half, MinY = y - half, MinZ = z - half, MaxX = x + half, MaxY = y + half, MaxZ = z + half };
        p.Tag = tag;
    }

    private static string ArgString(string[] args, string name, string fallback)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
    }

    private static float ArgFloat(string[] args, string name, float fallback)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length && float.TryParse(args[i + 1], out var v) ? v : fallback;
    }
}
