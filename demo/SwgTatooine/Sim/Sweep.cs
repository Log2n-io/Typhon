using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace SwgTatooine;

/// <summary>
/// The partitioning study: run the same world under a matrix of settings and report what each cost.
/// </summary>
/// <remarks>
/// <para><b>Three axes, and they are deliberately separable.</b> World size changes how sparse the grid is without
/// changing how much work the systems do; population changes how dense the occupied cells are without changing how many
/// exist; cell size changes only the partitioning. A sweep that moved two at once could not attribute anything.</para>
/// <para>Every point rebuilds the world from the same seed, so two arms differ only in the setting under test.</para>
/// </remarks>
public static class Sweep
{
    /// <summary>Run the matrix and write a markdown report beside the executable.</summary>
    public static int Run(SimConfig template, string[] args)
    {
        var worlds = Floats(args, "--sweep-worlds", [TatooineData.PlanetEdgeM / 1000f, 64f, 128f]);
        var pops = Floats(args, "--sweep-pops", [1f, 4f, 16f]);
        var cells = Floats(args, "--sweep-cells", [64f, 128f, 256f, 512f, 1024f]);
        var rows = new List<SweepRow>();

        Console.WriteLine($"── Sweep: {worlds.Length} worlds x {pops.Length} populations x {cells.Length} cell sizes "
            + $"= {worlds.Length * pops.Length * cells.Length} points ────────");
        Console.WriteLine();

        foreach (var world in worlds)
        {
            foreach (var pop in pops)
            {
                foreach (var cell in cells)
                {
                    var config = Clone(template);
                    config.WorldEdgeKm = world;
                    config.PopulationScale = pop;

                    // Cell sizes are quoted at the REAL planet's scale and scaled with the world, so "256 m" means the
                    // same fraction of the map at every world size. Quoting them absolutely would make the cell axis and
                    // the world axis the same axis wearing two hats.
                    config.CellSizeM = cell * config.ContentScale;

                    RunResult result = null;
                    WorldCensus census = null;
                    var failure = "";
                    try
                    {
                        using var sim = new TatooineSim(config);
                        sim.Initialize();
                        census = sim.Census;
                        result = sim.Run();
                        var stats = sim.LastStats;
                        rows.Add(new SweepRow
                        {
                            WorldKm = world,
                            Pop = pop,
                            CellM = cell,
                            Entities = census.Total,
                            Mobile = census.Mobile,
                            MedianMs = result.TickMedianMs,
                            P99Ms = result.TickP99Ms,
                            BudgetPct = result.BudgetPct,
                            AwarenessHits = stats.HitsPerAwarenessQuery,
                            Ticks = result.TicksMeasured,
                            AwarenessUs = SystemUs(result, "Awareness"),
                            FenceUs = FenceUs(result),
                        });
                    }
                    catch (Exception ex)
                    {
                        failure = $"{ex.GetType().Name}: {ex.Message}";
                        rows.Add(new SweepRow { WorldKm = world, Pop = pop, CellM = cell, Failure = failure });
                    }

                    Console.WriteLine(failure.Length > 0
                        ? $"  {world,5:N0} km  x{pop,-5:N1} cell {cell,5:N0} m  FAILED: {failure}"
                        : $"  {world,5:N0} km  x{pop,-5:N1} cell {cell,5:N0} m  {census.Total,9:N0} entities  "
                          + $"{result.TickMedianMs,7:F2} ms ({result.BudgetPct,5:F1} % of budget)  p99 {result.TickP99Ms,7:F2}");
                }
            }
        }

        var path = WriteReport(rows, template);
        Console.WriteLine();
        Console.WriteLine($"  report -> {path}");
        return 0;
    }

    private static string WriteReport(List<SweepRow> rows, SimConfig template)
    {
        var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "claude", "scratch"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"swg-tatooine-sweep-{DateTime.Now:yyyy-MM-dd}.md");
        var w = new StringBuilder();

        w.AppendLine("# Typhon under a Star Wars Galaxies server workload — partitioning sweep");
        w.AppendLine();
        w.AppendLine(CultureInfo.InvariantCulture, $"Generated {DateTime.Now:yyyy-MM-dd HH:mm} by `SwgTatooine --sweep`.");
        w.AppendLine("Source: `demo/SwgTatooine/`. Not a CI test — an instrument.");
        w.AppendLine();
        w.AppendLine("## What is being measured");
        w.AppendLine();
        w.AppendLine(CultureInfo.InvariantCulture,
            $"A reconstruction of SWG's planet Tatooine driven through `TyphonRuntime` at {template.TickRateHz} Hz: five");
        w.AppendLine("archetypes, nine systems across seven phases, a real on-disk database with a real WAL, and every archetype but the players'");
        w.AppendLine("inventory declaring `ClusterDurability.Checkpoint`.");
        w.AppendLine();
        w.AppendLine("**tick ms** is the whole tick — every system plus the fence — measured by the runtime's own telemetry ring, "
            + "median over the measured window.");
        w.AppendLine(CultureInfo.InvariantCulture,
            $"**% of budget** is that median against the {1000f / template.TickRateHz:F0} ms a {template.TickRateHz} Hz server has. "
            + $"Above 100 % it cannot keep up.");
        w.AppendLine("**hits/query** is the mean object count one interest query returns — the number that says how much work "
            + "awareness is actually doing.");
        w.AppendLine();
        w.AppendLine("**Awareness us** and **fence us** are the two columns that make the total attributable. The tick median can rank");
        w.AppendLine("two cell sizes but cannot say where the difference landed; interest management is 51-83 % of the tick depending on");
        w.AppendLine("population, and the fence is where migration and bound recomputation are paid for. A cell size that makes queries");
        w.AppendLine("cheap by making the fence expensive is visible here and invisible in the total alone.");
        w.AppendLine();
        w.AppendLine("Cell sizes are quoted at the real planet's scale and scaled with the world, so a given row means the same "
            + "fraction of the map at every world size.");
        w.AppendLine();

        var worlds = new List<float>();
        foreach (var r in rows)
        {
            if (!worlds.Contains(r.WorldKm))
            {
                worlds.Add(r.WorldKm);
            }
        }

        foreach (var world in worlds)
        {
            w.AppendLine(CultureInfo.InvariantCulture, $"## {world:N0} km world");
            w.AppendLine();
            w.AppendLine("| population | entities | mobile | cell | tick ms | p99 | % of budget | hits/query | Awareness us | fence us |");
            w.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
            foreach (var r in rows)
            {
                if (Math.Abs(r.WorldKm - world) > 0.01f)
                {
                    continue;
                }

                if (r.Failure.Length > 0)
                {
                    w.AppendLine(CultureInfo.InvariantCulture, $"| x{r.Pop:N1} | ❌ {r.Failure} | | | | | | | | |");
                    continue;
                }

                w.AppendLine(CultureInfo.InvariantCulture,
                    $"| x{r.Pop:N1} | {r.Entities:N0} | {r.Mobile:N0} | {r.CellM:N0} m | {r.MedianMs:F2} | {r.P99Ms:F2} | {r.BudgetPct:F1} % | "
                    + $"{r.AwarenessHits:F1} | {r.AwarenessUs:N0} | {r.FenceUs:N0} |");
            }

            w.AppendLine();
        }

        File.WriteAllText(path, w.ToString());
        return path;
    }

    /// <summary>Median microseconds one named system cost, or 0 if it did not run.</summary>
    private static float SystemUs(RunResult r, string name)
    {
        foreach (var s in r.Systems)
        {
            if (string.Equals(s.Name, name, StringComparison.Ordinal))
            {
                return s.MedianUs;
            }
        }

        return 0f;
    }

    /// <summary>
    /// Everything the ENGINE declared, summed: the tick fence's own systems.
    /// </summary>
    /// <remarks>
    /// Identified by name prefix rather than by phase, because the fence's systems are declared on the engine's own
    /// track and land in the default phase — this workload's phase names do not reach them.
    /// </remarks>
    private static float FenceUs(RunResult r)
    {
        var total = 0f;
        foreach (var s in r.Systems)
        {
            if (s.Name != null && s.Name.StartsWith("Fence", StringComparison.Ordinal))
            {
                total += s.MedianUs;
            }
        }

        return total;
    }

    private static SimConfig Clone(SimConfig c) => new()
    {
        WorldEdgeKm = c.WorldEdgeKm,
        ScaleContentWithWorld = c.ScaleContentWithWorld,
        PopulationScale = c.PopulationScale,
        CellSizeM = c.CellSizeM,
        CellTreePromoteThreshold = c.CellTreePromoteThreshold,
        CellTreePromoteTightness = c.CellTreePromoteTightness,
        ReclusterBudgetMs = c.ReclusterBudgetMs,
        ClusterTargetPackingSlack = c.ClusterTargetPackingSlack,
        ClusterTargetExtentRatio = c.ClusterTargetExtentRatio,
        ClusterRepairExtentRatio = c.ClusterRepairExtentRatio,
        ClusterRepairCriticalExtentRatio = c.ClusterRepairCriticalExtentRatio,
        RepairWorstClustersPerUnit = c.RepairWorstClustersPerUnit,
        RepairCooldownTicks = c.RepairCooldownTicks,
        Shuttles = c.Shuttles,
        ShuttleShare = c.ShuttleShare,
        ShuttleIntervalS = c.ShuttleIntervalS,
        BoardingWindowS = c.BoardingWindowS,
        ShuttleBurst = c.ShuttleBurst,
        Probe = c.Probe,
        BatchSpawnSortThreshold = c.BatchSpawnSortThreshold,
        TickRateHz = c.TickRateHz,
        WorkerCount = c.WorkerCount,
        WarmTicks = c.WarmTicks,
        MeasuredTicks = c.MeasuredTicks,
        PageCacheMiB = c.PageCacheMiB,
        DatabaseDirectory = c.DatabaseDirectory,
        Seed = c.Seed,
        ParallelFence = c.ParallelFence,
        // Not part of the workload, so not part of the label either (SimConfig.Label) — but an A/B arm set on the command line must reach every sweep arm.
        AwarenessApi = c.AwarenessApi,
        CombatApi = c.CombatApi,
        SimdNarrowphase = c.SimdNarrowphase,
    };

    private static float[] Floats(string[] args, string name, float[] fallback)
    {
        var i = Array.IndexOf(args, name);
        if (i < 0 || i + 1 >= args.Length)
        {
            return fallback;
        }

        var parts = args[i + 1].Split(',', StringSplitOptions.RemoveEmptyEntries);
        var result = new float[parts.Length];
        for (var k = 0; k < parts.Length; k++)
        {
            if (!float.TryParse(parts[k], NumberStyles.Float, CultureInfo.InvariantCulture, out result[k]))
            {
                return fallback;
            }
        }

        return result;
    }
}

/// <summary>One point of the sweep.</summary>
public sealed class SweepRow
{
    public float WorldKm;
    public float Pop;
    public float CellM;
    public int Entities;
    public int Mobile;
    public float MedianMs;
    public float P99Ms;
    public float BudgetPct;
    public double AwarenessHits;
    public int Ticks;

    /// <summary>Median microseconds the interest-management system cost — the dominant term, and what the cell size moves.</summary>
    public float AwarenessUs;

    /// <summary>Median microseconds summed across the engine's own fence systems.</summary>
    public float FenceUs;

    public string Failure = "";
}
