using System.Collections.Generic;

namespace AntHill.Harness;

/// <summary>Metrics collected from a completed run — the input to the assertion evaluator.</summary>
public sealed class RunMetrics
{
    /// <summary>Highest tick number the engine reached.</summary>
    public long TicksExecuted { get; init; }

    /// <summary>Median per-tick wall-clock duration, milliseconds.</summary>
    public double TickP50Ms { get; init; }

    /// <summary>99th-percentile per-tick wall-clock duration, milliseconds.</summary>
    public double TickP99Ms { get; init; }

    /// <summary>Live ant count at the end of the run (sum of per-tier counts).</summary>
    public long FinalAntCount { get; init; }

    /// <summary>Configured ant count — the spawn cap the live count must never exceed.</summary>
    public long ConfiguredAntCount { get; init; }

    /// <summary>Number of unhandled exceptions / engine errors during the run (0 or 1).</summary>
    public int ExceptionCount { get; init; }
}

/// <summary>Outcome of a single <see cref="ScenarioRun"/>.</summary>
public sealed class RunResult
{
    /// <summary>Originating scenario name.</summary>
    public string ScenarioName { get; init; }

    /// <summary>Worker count for this run.</summary>
    public int WorkerCount { get; init; }

    /// <summary>True when the run completed and every assertion held.</summary>
    public bool Passed { get; init; }

    /// <summary>Non-null failure summary when the run did not pass (crash, hang, or assertion).</summary>
    public string FailureReason { get; init; }

    /// <summary>Individual assertion failures — empty when none failed.</summary>
    public IReadOnlyList<string> AssertionFailures { get; init; }

    /// <summary>Metrics collected from the run.</summary>
    public RunMetrics Metrics { get; init; }

    /// <summary>Trace file path, when the run emitted one.</summary>
    public string TracePath { get; init; }
}
