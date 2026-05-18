using System;
using System.Threading;
using AntHill.Core;
using Typhon.Engine;

namespace AntHill.Harness;

/// <summary>
/// The ad-hoc profiler runner: drives the default-configured simulation for a fixed window and prints per-system timings. This is the pre-#353 behavior,
/// preserved for quick profiling and the live-Workbench-attach workflow. Scenario-driven runs go through <see cref="ScenarioRunner"/>.
/// </summary>
public static class AdHocRunner
{
    private const int RunSeconds = 10;
    private const int WarmupSeconds = 2;

    /// <summary>Runs the default-configured simulation, honoring <c>--duration</c> / profiler flags.</summary>
    public static void Run(string[] args)
    {
        var durationSec = RunSeconds;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--duration" && i + 1 < args.Length)
            {
                durationSec = int.Parse(args[++i]);
            }
        }

        Console.WriteLine($"AntHill ad-hoc run: {TyphonBridge.AntCount:N0} ants, {TyphonBridge.WorldSize:N0} world");
        Console.WriteLine($"Warming up {WarmupSeconds}s, measuring {durationSec}s...");

        // Profiler (issue #332): enabled via typhon.telemetry.json (Typhon:Profiler:Enabled, or any Trace/Live key). This runner's --trace/--live flags are
        // parsed here and injected through the AddTyphonProfiler DI hook — the host owns its CLI parsing.
        var bridge = new TyphonBridge();
        bridge.Initialize(services => services.AddTyphonProfiler(fileConfig => fileConfig.MergedWith(ProfilerLaunchConfig.FromArgs(args))));

        bridge.Start();

        Thread.Sleep(WarmupSeconds * 1000);

        var telemetry = bridge.Telemetry;
        if (telemetry == null)
        {
            Console.WriteLine("ERROR: No telemetry available");
            bridge.Dispose();
            return;
        }

        var startTick = telemetry.NewestTick;
        Thread.Sleep(durationSec * 1000);
        var endTick = telemetry.NewestTick;

        if (endTick <= startTick)
        {
            Console.WriteLine("ERROR: No ticks recorded");
            bridge.Dispose();
            return;
        }

        var sysDefs = bridge.Systems;
        var oldest = telemetry.OldestAvailableTick;
        var from = Math.Max(startTick + 1, oldest);
        var tickCount = (int)(endTick - from + 1);

        var tickDurations = new float[tickCount];
        var systemDurations = new float[sysDefs.Length][];
        for (var s = 0; s < sysDefs.Length; s++)
        {
            systemDurations[s] = new float[tickCount];
        }

        for (var i = 0; i < tickCount; i++)
        {
            var t = from + i;
            ref readonly var tick = ref telemetry.GetTick(t);
            tickDurations[i] = tick.ActualDurationMs;
            var systems = telemetry.GetSystemMetrics(t);
            for (var s = 0; s < sysDefs.Length && s < systems.Length; s++)
            {
                systemDurations[s][i] = systems[s].DurationUs;
            }
        }

        Array.Sort(tickDurations);
        var tickP50 = tickDurations[tickCount / 2];
        var tickP99 = tickDurations[(int)(tickCount * 0.99)];

        Console.WriteLine();
        Console.WriteLine($"-- Results ({tickCount} ticks) --------------------------------");
        Console.WriteLine($"  Tick p50: {tickP50:F2}ms  p99: {tickP99:F2}ms");
        Console.WriteLine();
        Console.WriteLine($"  {"System",-24} {"p50 (us)",10} {"p99 (us)",10}");

        for (var s = 0; s < sysDefs.Length; s++)
        {
            var dur = systemDurations[s];
            Array.Sort(dur);
            var p50 = dur[tickCount / 2];
            var p99 = dur[(int)(tickCount * 0.99)];
            if (p50 > 0.1f)
            {
                Console.WriteLine($"  {sysDefs[s].Name,-24} {p50,10:F0} {p99,10:F0}");
            }
        }

        // The profiler tears itself down inside bridge.Dispose() → TyphonRuntime.Shutdown/Dispose (#332).
        bridge.Dispose();
    }
}
