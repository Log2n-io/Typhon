using System;
using System.Collections.Generic;
using System.Globalization;
using AntHill.Core;

namespace AntHill.Harness;

/// <summary>
/// Expands a validated <see cref="Scenario"/> into one <see cref="ScenarioRun"/> per worker count — the worker-scaling sweep. Pure logic, independently testable.
/// </summary>
public static class ScenarioExpander
{
    /// <summary>Expands <paramref name="scenario"/> into its concrete runs (one per worker count).</summary>
    public static IReadOnlyList<ScenarioRun> Expand(Scenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ScenarioLoader.TryParseTierMode(scenario.Simulation?.TierMode ?? "camera", out var tierMode);

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var runs = new List<ScenarioRun>(scenario.Workers.Count);
        foreach (var workers in scenario.Workers)
        {
            var config = new ScenarioConfig
            {
                Seed = scenario.Seed,
                AntCount = scenario.Ants,
                WorkerCount = workers,
                TierMode = tierMode,
            };
            runs.Add(new ScenarioRun
            {
                ScenarioName = scenario.Name,
                WorkerCount = workers,
                Config = config,
                DurationSeconds = scenario.Duration,
                TickBudget = scenario.Ticks,
                Trace = scenario.Trace,
                TracePath = scenario.Trace ? $"{scenario.Name}-w{workers}-{stamp}.typhon-trace" : null,
                Assertions = scenario.Assertions,
            });
        }
        return runs;
    }
}
