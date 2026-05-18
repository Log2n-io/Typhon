using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace AntHill.Harness;

/// <summary>Writes the human-readable summary and the machine-readable JSON report for a batch of runs.</summary>
public static class RunReporter
{
    /// <summary>Writes the human-readable summary table to <paramref name="output"/>.</summary>
    public static void WriteHuman(IReadOnlyList<RunResult> results, TextWriter output)
    {
        output.WriteLine();
        output.WriteLine("== Scenario report =========================================");
        output.WriteLine($"  {"workers",8} {"result",8} {"ticks",10} {"p50 ms",9} {"p99 ms",9} {"ants",12}");
        foreach (var r in results)
        {
            var m = r.Metrics;
            output.WriteLine(
                $"  {r.WorkerCount,8} {(r.Passed ? "PASS" : "FAIL"),8} {m.TicksExecuted,10} "
                + $"{m.TickP50Ms,9:F2} {m.TickP99Ms,9:F2} {m.FinalAntCount,12:N0}");
            if (!r.Passed)
            {
                if (r.FailureReason != null)
                {
                    output.WriteLine($"           reason: {r.FailureReason}");
                }
                foreach (var f in r.AssertionFailures)
                {
                    output.WriteLine($"           assert: {f}");
                }
            }
        }
        output.WriteLine("============================================================");
    }

    /// <summary>Writes the machine-readable JSON report to <paramref name="path"/>.</summary>
    public static void WriteJson(IReadOnlyList<RunResult> results, string path)
    {
        var runs = new List<object>(results.Count);
        foreach (var r in results)
        {
            runs.Add(new
            {
                scenario = r.ScenarioName,
                workers = r.WorkerCount,
                passed = r.Passed,
                failureReason = r.FailureReason,
                assertionFailures = r.AssertionFailures,
                ticksExecuted = r.Metrics.TicksExecuted,
                tickP50Ms = r.Metrics.TickP50Ms,
                tickP99Ms = r.Metrics.TickP99Ms,
                finalAntCount = r.Metrics.FinalAntCount,
                exceptionCount = r.Metrics.ExceptionCount,
                tracePath = r.TracePath,
            });
        }

        var payload = new { generatedUtc = DateTime.UtcNow, runs };
        File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }
}
