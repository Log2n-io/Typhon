using System;
using System.Diagnostics;

namespace SwgTatooine;

internal static class Program
{
    private static int Main(string[] args)
    {
        var config = CommandLine.Parse(args);
        if (Array.IndexOf(args, "--sweep") >= 0)
        {
            return Sweep.Run(config, args);
        }

        Console.WriteLine($"── SWG Tatooine — {config.Label} ──────────────");

        var sw = Stopwatch.StartNew();
        using var sim = new TatooineSim(config);
        sim.Initialize();
        sw.Stop();

        Console.WriteLine($"  world built in {sw.Elapsed.TotalSeconds:F1}s");
        Console.WriteLine($"  {sim.Census}");
        // Live cell count is only reachable through TickContext.SpatialGrid, so it is reported by the telemetry system
        // once the runtime is up rather than here.
        var perAxis = (int)(config.WorldEdgeM / config.ResolveCellSize());
        Console.WriteLine($"  grid {config.WorldEdgeM:N0} m at {config.ResolveCellSize():N0} m cells ({perAxis}^2 = {(long)perAxis * perAxis:N0} possible)");

        var result = sim.Run();
        var s = sim.LastStats;
        Console.WriteLine($"  tick {result.TickMedianMs:F2} ms median, {result.TickP99Ms:F2} p99, {result.TickMaxMs:F2} max "
            + $"of a {result.BudgetMs:F0} ms budget = {result.BudgetPct:F1} % ({result.TicksMeasured} ticks)");
        Console.WriteLine($"  per tick: {s.AwarenessQueries / (double)Math.Max(1, result.TicksMeasured):F0} awareness queries "
            + $"({s.HitsPerAwarenessQuery:F1} hits each), {s.AggroQueries / (double)Math.Max(1, result.TicksMeasured):F0} aggro queries, "
            + $"{s.EconomyTicks / (double)Math.Max(1, result.TicksMeasured):F1} economy updates");
        Console.WriteLine($"  combat: {s.PlayersEngaged} attacks, {s.CreaturesKilled} creatures killed, "
            + $"{s.CreaturesRespawned} revived over {result.TicksMeasured} ticks");
        Console.WriteLine($"  missions: {s.MissionsIssued} issued, {s.MissionsCompleted} completed");

        Console.WriteLine();
        Console.WriteLine($"  {"system",-16} {"phase",-10} {"median us",10} {"share",7} {"entities",10} {"workers",8}");
        foreach (var sys in result.Systems)
        {
            var share = result.TickMedianMs <= 0f ? 0f : 100f * sys.MedianUs / (result.TickMedianMs * 1000f);
            Console.WriteLine($"  {sys.Name,-16} {sys.Phase,-10} {sys.MedianUs,10:F1} {share,6:F1} % {sys.EntitiesPerTick,10:N0} {sys.WorkersPerTick,8:F1}");
        }

        var residualShare = result.TickMedianMs <= 0f ? 0f : 100f * result.ResidualUs / (result.TickMedianMs * 1000f);
        Console.WriteLine($"  {"<fence+dispatch>",-16} {"",-10} {result.ResidualUs,10:F1} {residualShare,6:F1} %");
        Console.WriteLine($"  {"= tick",-16} {"",-10} {result.TickMedianMs * 1000f,10:F1}");

        sim.PrintShuttleReport();
        sim.PrintSpatialTelemetry();
        SpatialCensus.Print(sim.Dbe);
        return 0;
    }
}
