using System;
using System.Collections.Generic;

namespace AntHill.Harness;

/// <summary>
/// AntHill.Harness entry point. Two modes:
///   <c>--scenario &lt;file.yaml&gt;</c> runs a declarative scenario (the validation harness);
///   with no scenario it falls back to the ad-hoc profiler runner (see <see cref="AdHocRunner"/>).
/// </summary>
public static class Program
{
    /// <summary>Exit codes: 0 = all runs passed, 1 = a run failed, 2 = scenario load/validation error.</summary>
    public static int Main(string[] args)
    {
        if (HasFlag(args, "--help", "-h"))
        {
            PrintUsage();
            return 0;
        }

        var wantsScenario = HasFlag(args, "--scenario");
        var scenarioPath = GetOption(args, "--scenario");
        if (wantsScenario && scenarioPath == null)
        {
            Console.Error.WriteLine("Error: --scenario requires a file path.");
            return 2;
        }
        if (scenarioPath != null)
        {
            return RunScenario(scenarioPath);
        }

        AdHocRunner.Run(args);
        return 0;
    }

    private static int RunScenario(string scenarioPath)
    {
        Scenario scenario;
        try
        {
            scenario = ScenarioLoader.Load(scenarioPath);
        }
        catch (ScenarioException ex)
        {
            Console.Error.WriteLine($"Scenario error: {ex.Message}");
            return 2;
        }

        var runs = ScenarioExpander.Expand(scenario);
        Console.WriteLine($"Scenario '{scenario.Name}': {runs.Count} run(s) across the worker sweep.");

        var results = new List<RunResult>(runs.Count);
        foreach (var run in runs)
        {
            results.Add(ScenarioRunner.Run(run));
        }

        RunReporter.WriteHuman(results, Console.Out);
        var jsonPath = $"{scenario.Name}-report.json";
        RunReporter.WriteJson(results, jsonPath);
        Console.WriteLine($"JSON report: {jsonPath}");

        var anyFailed = false;
        foreach (var r in results)
        {
            if (!r.Passed)
            {
                anyFailed = true;
            }
        }
        return anyFailed ? 1 : 0;
    }

    private static bool HasFlag(string[] args, params string[] flags)
    {
        foreach (var a in args)
        {
            foreach (var f in flags)
            {
                if (a == f)
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static string GetOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name)
            {
                return args[i + 1];
            }
        }
        return null;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("AntHill.Harness — scriptable engine stress + validation harness.");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run -- --scenario <file.yaml>   Run a declarative scenario (validation harness)");
        Console.WriteLine("  dotnet run -- [options]                Ad-hoc profiler run (no scenario)");
        Console.WriteLine();
        Console.WriteLine("Scenario mode:");
        Console.WriteLine("  --scenario <path>   YAML scenario file; expands its worker sweep into one run each.");
        Console.WriteLine("                      Exit code: 0 all passed, 1 a run failed, 2 scenario error.");
        Console.WriteLine();
        Console.WriteLine("Ad-hoc mode options:");
        Console.WriteLine("  --duration <sec>    Measurement window in seconds (default: 10)");
        Console.WriteLine("  --trace <path>      Enable the runtime profiler, write a .typhon-trace file");
        Console.WriteLine("  --live [port]       Enable the runtime profiler, open a TCP listener");
        Console.WriteLine("  --help, -h          Show this message");
    }
}
