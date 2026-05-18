using System;
using System.Diagnostics;
using System.Threading;
using AntHill.Core;
using Typhon.Engine;

namespace AntHill.Harness;

/// <summary>
/// Executes a single <see cref="ScenarioRun"/> against a fresh <see cref="TyphonBridge"/>:
/// drives it for the configured window under a progress watchdog, captures crashes, collects metrics, and evaluates the scenario's assertions.
/// </summary>
public static class ScenarioRunner
{
    /// <summary>A run making no tick progress for this long is treated as hung.</summary>
    public static readonly TimeSpan WatchdogTimeout = TimeSpan.FromSeconds(30);

    private const int PollIntervalMs = 200;

    /// <summary>Runs <paramref name="run"/> end to end and returns its outcome.</summary>
    public static RunResult Run(ScenarioRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        Console.WriteLine(
            $"-- Run: {run.ScenarioName} | {run.Config.AntCount:N0} ants | {run.WorkerCount} workers "
            + $"| tier={run.Config.TierMode} --");

        TyphonBridge bridge = null;
        try
        {
            bridge = new TyphonBridge(run.Config);
            if (run.Trace)
            {
                var tracePath = run.TracePath;
                bridge.Initialize(services => 
                    services.AddTyphonProfiler(fc => fc.MergedWith(ProfilerLaunchConfig.FromArgs(["--trace", tracePath]))));
            }
            else
            {
                bridge.Initialize();
            }
            bridge.Start();

            var healthy = DriveRun(run, bridge);
            var metrics = CollectMetrics(run, bridge, exceptionCount: 0);

            if (!healthy)
            {
                return Fail(run, metrics,
                    $"watchdog: no tick progress for {WatchdogTimeout.TotalSeconds:F0}s (hang)");
            }
            if (metrics.TicksExecuted <= 0)
            {
                return Fail(run, metrics, "no ticks were executed");
            }

            var assertionFailures = AssertionEvaluator.Evaluate(run.Assertions, metrics);
            return new RunResult
            {
                ScenarioName = run.ScenarioName,
                WorkerCount = run.WorkerCount,
                Passed = assertionFailures.Count == 0,
                FailureReason = assertionFailures.Count == 0 ? null : "assertion failure",
                AssertionFailures = assertionFailures,
                Metrics = metrics,
                TracePath = run.TracePath,
            };
        }
        catch (Exception ex)
        {
            // Any unhandled exception / engine error ends the run as a FAIL — the first-order signal.
            return Fail(run, CollectMetricsSafe(run, bridge, exceptionCount: 1), $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try
            {
                bridge?.Dispose();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  (teardown error: {ex.GetType().Name}: {ex.Message})");
            }
        }
    }

    /// <summary>Drives the run to its window. Returns false if the watchdog tripped (a hang).</summary>
    private static bool DriveRun(ScenarioRun run, TyphonBridge bridge)
    {
        var watchdog = new ProgressWatchdog(WatchdogTimeout, Environment.TickCount64);
        var sw = Stopwatch.StartNew();
        while (true)
        {
            Thread.Sleep(PollIntervalMs);
            var tick = bridge.CurrentTick;
            if (!watchdog.Observe(tick, Environment.TickCount64))
            {
                return false;
            }
            if (run.TickBudget.HasValue)
            {
                if (tick >= run.TickBudget.Value)
                {
                    return true;
                }
            }
            else if (sw.Elapsed.TotalSeconds >= (run.DurationSeconds ?? 0))
            {
                return true;
            }
        }
    }

    private static RunMetrics CollectMetrics(ScenarioRun run, TyphonBridge bridge, int exceptionCount)
    {
        var telemetry = bridge?.Telemetry;
        if (telemetry == null)
        {
            return new RunMetrics
            {
                ConfiguredAntCount = run.Config.AntCount,
                ExceptionCount = exceptionCount,
            };
        }

        var newest = telemetry.NewestTick;
        var from = Math.Max(telemetry.OldestAvailableTick, 1);
        var count = (int)(newest - from + 1);
        double p50 = 0, p99 = 0;
        if (count > 0)
        {
            var durations = new float[count];
            for (var i = 0; i < count; i++)
            {
                ref readonly var tick = ref telemetry.GetTick(from + i);
                durations[i] = tick.ActualDurationMs;
            }
            Array.Sort(durations);
            p50 = durations[count / 2];
            p99 = durations[Math.Min(count - 1, (int)(count * 0.99))];
        }

        long antCount = 0;
        var tiers = bridge.TierCounts;
        for (var i = 0; i < tiers.Length; i++)
        {
            antCount += tiers[i];
        }

        return new RunMetrics
        {
            TicksExecuted = newest,
            TickP50Ms = p50,
            TickP99Ms = p99,
            FinalAntCount = antCount,
            ConfiguredAntCount = run.Config.AntCount,
            ExceptionCount = exceptionCount,
        };
    }

    private static RunMetrics CollectMetricsSafe(ScenarioRun run, TyphonBridge bridge, int exceptionCount)
    {
        try
        {
            return CollectMetrics(run, bridge, exceptionCount);
        }
        catch
        {
            return new RunMetrics
            {
                ConfiguredAntCount = run.Config.AntCount,
                ExceptionCount = exceptionCount,
            };
        }
    }

    private static RunResult Fail(ScenarioRun run, RunMetrics metrics, string reason) => new()
    {
        ScenarioName = run.ScenarioName,
        WorkerCount = run.WorkerCount,
        Passed = false,
        FailureReason = reason,
        AssertionFailures = [],
        Metrics = metrics,
        TracePath = run.TracePath,
    };
}
