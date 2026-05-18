using System;
using System.Threading;
using AntHill.Core;
using Typhon.Engine;

namespace AntHill.Harness;

public static class Program
{
    const int RunSeconds = 10;
    const int WarmupSeconds = 2;

    public static void Main(string[] args)
    {
        int durationSec = RunSeconds;

        // First pass: handle --duration and --help here (profile-runner-specific).
        // Profiler flags (--trace, --live) are parsed by the shared helper below.
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--duration" when i + 1 < args.Length:
                    durationSec = int.Parse(args[++i]);
                    break;
                case "--help" or "-h":
                    PrintUsage();
                    return;
            }
        }

        Console.WriteLine($"AntHill ProfileRunner: {TyphonBridge.AntCount:N0} ants, {TyphonBridge.WorldSize:N0} world");
        Console.WriteLine($"Warming up {WarmupSeconds}s, measuring {durationSec}s...");

        // Profiler (issue #332): enabled via typhon.telemetry.json (Typhon:Profiler:Enabled, or any Trace/Live key).
        // This runner's --trace/--live flags are parsed here and injected through the AddTyphonProfiler DI hook —
        // the host owns its CLI parsing; the engine never reads the command line itself.
        var bridge = new TyphonBridge();
        bridge.Initialize(services =>
            services.AddTyphonProfiler(fileConfig => fileConfig.MergedWith(ProfilerLaunchConfig.FromArgs(args))));

        bridge.Start();

        // Warm up
        Thread.Sleep(WarmupSeconds * 1000);

        var telemetry = bridge.Telemetry;
        if (telemetry == null)
        {
            Console.WriteLine("ERROR: No telemetry available");
            bridge.Dispose();
            return;
        }

        long startTick = telemetry.NewestTick;
        Thread.Sleep(durationSec * 1000);
        long endTick = telemetry.NewestTick;

        if (endTick <= startTick)
        {
            Console.WriteLine("ERROR: No ticks recorded");
            bridge.Dispose();
            return;
        }

        // Collect metrics
        var sysDefs = bridge.Systems;
        long oldest = telemetry.OldestAvailableTick;
        long from = Math.Max(startTick + 1, oldest);
        int tickCount = (int)(endTick - from + 1);

        var tickDurations = new float[tickCount];
        var systemDurations = new float[sysDefs.Length][];
        for (int s = 0; s < sysDefs.Length; s++)
        {
            systemDurations[s] = new float[tickCount];
        }

        for (int i = 0; i < tickCount; i++)
        {
            long t = from + i;
            ref readonly var tick = ref telemetry.GetTick(t);
            tickDurations[i] = tick.ActualDurationMs;
            var systems = telemetry.GetSystemMetrics(t);
            for (int s = 0; s < sysDefs.Length && s < systems.Length; s++)
            {
                systemDurations[s][i] = systems[s].DurationUs;
            }
        }

        // Print results
        Array.Sort(tickDurations);
        float tickP50 = tickDurations[tickCount / 2];
        float tickP99 = tickDurations[(int)(tickCount * 0.99)];

        Console.WriteLine();
        Console.WriteLine($"── Results ({tickCount} ticks) ──────────────────────────────────");
        Console.WriteLine($"  Tick p50: {tickP50:F2}ms  p99: {tickP99:F2}ms");
        Console.WriteLine();
        Console.WriteLine($"  {"System",-24} {"p50 (us)",10} {"p99 (us)",10}");
        Console.WriteLine($"  {"─",-24} {"─",10} {"─",10}");

        for (int s = 0; s < sysDefs.Length; s++)
        {
            var dur = systemDurations[s];
            Array.Sort(dur);
            float p50 = dur[tickCount / 2];
            float p99 = dur[(int)(tickCount * 0.99)];
            if (p50 > 0.1f)
            {
                Console.WriteLine($"  {sysDefs[s].Name,-24} {p50,10:F0} {p99,10:F0}");
            }
        }

        Console.WriteLine($"──────────────────────────────────────────────────");

        // The profiler tears itself down inside bridge.Dispose() → TyphonRuntime.Shutdown (begins the async CPU-sampler
        // stop) → TyphonRuntime.Dispose (finishes it, stops the profiler, detaches exporters). Issue #332.
        bridge.Dispose();
    }

    private static void PrintUsage()
    {
        Console.WriteLine("AntHill ProfileRunner — captures per-system timings and optionally a runtime trace.");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run -- [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --duration <seconds>   Measurement window in seconds (default: 10)");
        Console.WriteLine("  --trace <path>         Enable runtime profiler, write .typhon-trace file to <path>");
        Console.WriteLine("  --live [port]          Enable runtime profiler, open TCP listener on <port>");
        Console.WriteLine($"                         (default port: {ProfilerLaunchConfig.DefaultLivePort})");
        Console.WriteLine("  --live-wait <ms>       Block startup up to <ms> milliseconds waiting for the first viewer to attach");
        Console.WriteLine("  --help, -h             Show this message");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  dotnet run -- --duration 30");
        Console.WriteLine("  dotnet run -- --trace anthill.typhon-trace");
        Console.WriteLine("  dotnet run -- --live 9001");
        Console.WriteLine();
        Console.WriteLine("Note: --trace and --live are mutually exclusive; if both are given, --trace wins.");
    }
}
