using System;
using System.Threading;
using Typhon.Engine;
using Typhon.Engine.Profiler;

namespace AntHill.ProfileRunner;

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

        // Shared profiler activation path (same ordering as Godot's Main.cs).
        // Step 1 runs env-var + TelemetryConfig setup BEFORE bridge construction so the JIT gate
        // (TelemetryConfig.ProfilerActive) is open when the scheduler compiles.
        var profilerConfig = ProfilerLaunchConfig
            .FromEnvironment()
            .MergedWith(ProfilerLaunchConfig.FromArgs(args));
        ProfilerLauncher.EnableTelemetryGateIfActive(profilerConfig);

        if (profilerConfig.TraceFilePath != null)
        {
            Console.WriteLine($"Profiler: file mode → {profilerConfig.TraceFilePath}");
        }
        else if (profilerConfig.LivePort >= 0)
        {
            Console.WriteLine($"Profiler: live mode → TCP listener on port {profilerConfig.LivePort}");
        }

        Console.WriteLine($"AntHill ProfileRunner: {TyphonBridge.AntCount:N0} ants, {TyphonBridge.WorldSize:N0} world");
        Console.WriteLine($"Warming up {WarmupSeconds}s, measuring {durationSec}s...");

        var bridge = new TyphonBridge();
        bridge.Initialize();

        // Step 2: exporters + profiler start, now that DI has built registry.Profiler.
        // Must happen BEFORE bridge.Start() so the very first tick is captured. Dual-attach when both --trace and --live are passed.
        System.Collections.Generic.List<IProfilerExporter> exporters = null;
        if (profilerConfig.IsActive)
        {
            try
            {
                exporters = ProfilerLauncher.CreateExporters(profilerConfig, bridge.ProfilerParent);
                foreach (var exp in exporters) TyphonProfiler.AttachExporter(exp);
                var metadata = ProfilerSetup.BuildSessionMetadata(
                    bridge.Systems, workerCount: 16, baseTickRate: 60f,
                    phases: bridge.PhaseNames,
                    currentEngineTickProvider: () => bridge.CurrentTick,
                    engine: bridge.DatabaseEngine,
                    resourceGraphRoot: bridge.ResourceGraphRoot,
                    runtime: bridge.ActiveRuntime);
                if (profilerConfig.LiveWaitMs > 0 && profilerConfig.LivePort >= 0)
                {
                    Console.WriteLine($"Waiting up to {profilerConfig.LiveWaitMs} ms for the workbench to attach on :{profilerConfig.LivePort}…");
                }
                // Start runs each exporter's Initialize. For a TcpExporter with LiveWaitMs > 0, that call blocks
                // until the first viewer connects (or the timeout elapses), giving the operator time to attach.
                TyphonProfiler.Start(bridge.ProfilerParent, metadata);
            }
            catch (Exception ex)
            {
                // Port busy / firewall / disposal race / non-writable trace path. Continue without profiling rather than crashing.
                Console.Error.WriteLine($"Profiler startup FAILED — {ex.GetType().Name}: {ex.Message}");
                Console.Error.WriteLine($"  Likely cause: port {profilerConfig.LivePort} already in use, firewall blocking, or trace path not writable. Running without profiling.");
                exporters = null;
            }
        }

        bridge.Start();

        // Telemetry diagnostics — prints full config state, exporter types, and whether
        // TyphonProfiler's consumer thread is running. Run right after Start() so the
        // scheduler has had a chance to register with the profiler.
        ProfilerLauncher.PrintDiagnostics(Console.WriteLine, exporters);

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

        bridge.Dispose();

        // Stop the profiler AFTER the bridge so any final tick/shutdown events have been emitted.
        // TyphonProfiler.Stop flushes + disposes every attached exporter; DetachExporter clears the
        // static list so re-running in the same process starts from empty state.
        if (exporters != null && exporters.Count > 0)
        {
            TyphonProfiler.Stop();
            foreach (var exp in exporters)
            {
                TyphonProfiler.DetachExporter(exp);
            }
        }
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
