using System;
using System.Diagnostics;

namespace SwgTatooine;

internal static class Program
{
    /// <summary>
    /// Parses the command line, then runs whichever of the three modes it named.
    /// </summary>
    /// <param name="args">The command line.</param>
    /// <returns>0 on success, 2 when the command line is wrong.</returns>
    /// <remarks>
    /// <b>A wrong command line exits 2 and names the token (SWG-07).</b> It used to fall back to the default for anything it could not read, so a typo in a
    /// sweep script produced a complete, plausible report of the wrong configuration. Exiting non-zero is what lets a script notice; naming the token is what
    /// lets a person fix it. Only <see cref="ArgumentException"/> is caught, and only around the parse: a failure inside the simulation is a crash and should
    /// look like one.
    /// </remarks>
    private static int Main(string[] args)
    {
        SimConfig config;
        try
        {
            config = CommandLine.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"SwgTatooine: {ex.Message}");
            return 2;
        }

        if (config.HelpText != null)
        {
            Console.Write(config.HelpText);
            return 0;
        }

        if (config.RunSweep)
        {
            return Sweep.Run(config);
        }

        Console.WriteLine($"── SWG Tatooine — {config.Label} ──────────────");

        var sw = Stopwatch.StartNew();
        using var sim = new TatooineSim(config);
        sim.Initialize();
        sw.Stop();

        Console.WriteLine($"  world built in {sw.Elapsed.TotalSeconds:F1}s");

        // After the build, before any tick: what the world and its realms hold (Realms G1d compares runs with and without --interiors against README § 11).
        // The page cache is a fixed native block of --cache-mib, the same in every run, so it cancels in a difference.
        // Only in a realms run: the forced collection would otherwise give every default run a heap main's does not have (review #4, A/B fairness).
        if (config.Interiors || config.Space || config.Planets > 1 || config.Dungeons > 0)
        {
            Console.WriteLine($"  memory after build: managed {GC.GetTotalMemory(true) / 1048576.0:F1} MB, private "
                + $"{Process.GetCurrentProcess().PrivateMemorySize64 / 1048576.0:F1} MB");
        }

        // `--serve <port>` turns the benchmark into a server: the same world and the same systems, ticking forever behind a WebSocket, with the browser
        // client served beside it. It returns from here rather than falling through to the measurement report, which has nothing to say about a run with no
        // end.
        if (config.ServePort > 0)
        {
            sim.ServeAsync(config.ServePort, TatooineSim.DefaultClientRoot(AppContext.BaseDirectory)).GetAwaiter().GetResult();
            return 0;
        }
        Console.WriteLine($"  {sim.Census}");

        // The composition, not just the size: two runs with the same creature count and different template mixes are not the same workload (S0-5).
        var composition = sim.Census.Composition();
        if (composition.Length > 0)
        {
            Console.WriteLine($"  creatures: {composition}");
        }

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
        Console.WriteLine($"  combat over {result.TicksMeasured} ticks: {s.PlayerShots} player shots, {s.CreatureAttacks} creature attacks, "
            + $"{s.DamageApplied} hits applied, {s.CreaturesKilled} creatures killed, {s.CreaturesRespawned} revived, "
            + $"{s.PlayersIncapacitated} players cloned");

        // Every one of these is a refusal, and each is printed because the claim it supports is about what does NOT happen: a player that is not fighting does
        // no damage, a shot beyond weapon range does no damage, a chase past 75 m is abandoned. A silent zero cannot tell those apart from a system that never
        // ran. `stale` is the exception — it is expected to be zero, and a non-zero value means an assumption has stopped holding.
        Console.WriteLine($"  combat refusals: {s.ShotsSkippedNotFighting} player-ticks not fighting, {s.ShotsRefusedRange} shots out of range, "
            + $"{s.ShotsWithoutTarget} with nothing in range, {s.CreaturesLostTarget} chases dropped, {s.ChaseGivenUp} chases given up past "
            + $"{TatooineData.MaxChaseRangeM * config.ContentScale:F0} m, {s.EventsStale} stale events");
        Console.WriteLine($"  missions: {s.MissionsIssued} issued, {s.MissionsAssigned} assigned, {s.MissionsCompleted} completed, {s.MissionRewards} paid; {s.LairHits} lair hits");
        Console.WriteLine($"  durability wait: {result.DurabilityMedianMs:F3} ms median, {result.DurabilityP99Ms:F3} p99, {result.DurabilityMaxMs:F3} max; "
            + $"{result.TicksWithDurabilityWait} of {result.TicksMeasured} ticks waited at all "
            + $"({result.DurabilityShareOfMedianPct:F1} % of the median tick)");

        // The watermark, which is the evidence that the loot and the rewards reached the WAL rather than only a component: UowFlushMs above is a few
        // microseconds even on a tick that persisted nothing, because it times the flush call.
        Console.WriteLine($"  wal watermark: advanced on {sim.WalAdvances} of {result.TicksMeasured} measured ticks, {sim.WalLsnGained} LSNs gained");
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
        sim.PrintDungeonReport();
        var counts = sim.Dbe.Realms.Counts;
        if (counts.Active + counts.Simulated + counts.Dormant + counts.Closing > 1)
        {
            Console.WriteLine($"  realms at the end: {counts.Active} active, {counts.Simulated} simulated, {counts.Dormant} dormant, {counts.Closing} closing; "
                + $"{counts.Divided} divided; policy epoch {counts.PolicyEpoch}");
        }
        sim.PrintSpatialTelemetry();
        sim.PrintWorkProbe();
        sim.PrintChunkStats();
        SpatialCensus.Print(sim.Dbe);
        return 0;
    }
}
