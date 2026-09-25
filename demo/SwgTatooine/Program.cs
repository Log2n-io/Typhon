using System;
using System.Diagnostics;

namespace SwgTatooine;

internal static class Program
{
    /// <summary>The port <c>--serve</c> names, or 0 when it is absent.</summary>
    /// <param name="args">The command line.</param>
    /// <returns>The port.</returns>
    /// <remarks>Read here rather than in <see cref="CommandLine"/> because it selects a mode rather than configuring the simulation.</remarks>
    private static int PortArgument(string[] args)
    {
        var at = Array.IndexOf(args, "--serve");
        if (at < 0)
        {
            return 0;
        }

        return at + 1 < args.Length && int.TryParse(args[at + 1], out var port) && port is > 0 and <= 65535 ? port : 8080;
    }

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

        // After the build, before any tick: what the world and its realms hold (Realms G1d compares runs with and without --interiors against README § 11).
        // The page cache is a fixed native block of --cache-mib, the same in every run, so it cancels in a difference.
        Console.WriteLine($"  memory after build: managed {GC.GetTotalMemory(true) / 1048576.0:F1} MB, private "
            + $"{Process.GetCurrentProcess().PrivateMemorySize64 / 1048576.0:F1} MB");

        // `--serve <port>` turns the benchmark into a server: the same world and the same systems, ticking forever behind a WebSocket, with the browser
        // client served beside it. It returns from here rather than falling through to the measurement report, which has nothing to say about a run with no
        // end.
        var servePort = PortArgument(args);
        if (servePort > 0)
        {
            sim.ServeAsync(servePort, TatooineSim.DefaultClientRoot(AppContext.BaseDirectory)).GetAwaiter().GetResult();
            return 0;
        }
        Console.WriteLine($"  {sim.Census}");
        // Live cell count is only reachable through TickContext.SpatialGrid, so it is reported by the telemetry system
        // once the runtime is up rather than here.
        var perAxis = (int)(config.WorldEdgeM / config.ResolveCellSize());
        Console.WriteLine($"  grid {config.WorldEdgeM:N0} m at {config.ResolveCellSize():N0} m cells ({perAxis}^2 = {(long)perAxis * perAxis:N0} possible)");

        var result = sim.Run();
        var s = sim.LastStats;
        Console.WriteLine($"  tick {result.TickMedianMs:F2} ms median, {result.TickP99Ms:F2} p99, {result.TickMaxMs:F2} max "
            + $"of a {result.BudgetMs:F0} ms budget = {result.BudgetPct:F1} % ({result.TicksMeasured} ticks)");
        Console.WriteLine($"  tail: p90 {result.TickP90Ms:F2}, p99.9 {result.TickP999Ms:F2} ms; ticks over 1.25x / 1.5x / 2x the median: "
            + $"{result.TicksOver125} / {result.TicksOver150} / {result.TicksOver200}");
        var sp = result.Spikes;
        if (sp.Ticks > 0)
        {
            var top = string.Join(", ", sp.Systems.GetRange(0, Math.Min(4, sp.Systems.Count))
                .ConvertAll(x => $"{x.Name} {x.ExcessMs:F1} ms ({100 * x.ExcessMs / sp.TickExcessMs:F0} %)"));
            Console.WriteLine($"  spike excess ({sp.Ticks} ticks over 1.25x, {sp.TickExcessMs:F1} ms over the median in all): {top}");
        }
        Console.WriteLine($"  per tick: {s.AwarenessQueries / (double)Math.Max(1, result.TicksMeasured):F0} awareness queries "
            + $"({s.HitsPerAwarenessQuery:F1} hits each), {s.AggroQueries / (double)Math.Max(1, result.TicksMeasured):F0} aggro queries, "
            + $"{s.EconomyTicks / (double)Math.Max(1, result.TicksMeasured):F1} economy updates");
        Console.WriteLine($"  combat: {s.PlayersEngaged} attacks, {s.CreaturesKilled} creatures killed, "
            + $"{s.CreaturesRespawned} revived over {result.TicksMeasured} ticks");
        Console.WriteLine($"  missions: {s.MissionsIssued} issued, {s.MissionsCompleted} completed");
        var gc = sim.LastGc;
        Console.WriteLine($"  gc while ticking: {gc.Gen0} gen0, {gc.Gen1} gen1, {gc.Gen2} gen2 collections, {gc.PauseMs:F1} ms paused "
            + $"({100 * gc.PauseMs / Math.Max(1d, gc.ElapsedMs):F2} % of {gc.ElapsedMs / 1000:F1} s), {gc.AllocatedBytes / 1048576.0:F1} MB allocated");

        Console.WriteLine();
        Console.WriteLine($"  {"system",-16} {"phase",-10} {"median us",10} {"share",7} {"entities",10} {"workers",8} {"work us",9} {"wait p50",9} {"p99",7}");
        foreach (var sys in result.Systems)
        {
            var share = result.TickMedianMs <= 0f ? 0f : 100f * sys.MedianUs / (result.TickMedianMs * 1000f);
            var workUs = float.IsNaN(sys.WorkMedianUs) ? "-" : sys.WorkMedianUs.ToString("F0");
            var p50 = float.IsNaN(sys.WaitP50Us) ? "-" : sys.WaitP50Us.ToString("F0");
            var p99 = float.IsNaN(sys.WaitP99Us) ? "-" : sys.WaitP99Us.ToString("F0");
            Console.WriteLine($"  {sys.Name,-16} {sys.Phase,-10} {sys.MedianUs,10:F1} {share,6:F1} % {sys.EntitiesPerTick,10:N0} {sys.WorkersPerTick,8:F1} "
                + $"{workUs,9} {p50,9} {p99,7}");
        }

        var residualShare = result.TickMedianMs <= 0f ? 0f : 100f * result.ResidualUs / (result.TickMedianMs * 1000f);
        Console.WriteLine($"  {"<fence+dispatch>",-16} {"",-10} {result.ResidualUs,10:F1} {residualShare,6:F1} %");
        Console.WriteLine($"  {"= tick",-16} {"",-10} {result.TickMedianMs * 1000f,10:F1}");

        sim.PrintShuttleReport();
        sim.PrintPortalReport();
        sim.PrintSpaceReport();
        sim.PrintSpatialTelemetry();
        sim.PrintWorkProbe();
        sim.PrintChunkStats();
        SpatialCensus.Print(sim.Dbe);
        return 0;
    }
}
