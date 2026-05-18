using AntHill.Core;

namespace AntHill.Harness;

/// <summary>
/// One concrete run produced by expanding a <see cref="Scenario"/> over its worker sweep —
/// a single worker count, paired with the engine config and the run parameters.
/// </summary>
public sealed class ScenarioRun
{
    /// <summary>Originating scenario name.</summary>
    public string ScenarioName { get; init; }

    /// <summary>Worker count for this run (the swept dimension).</summary>
    public int WorkerCount { get; init; }

    /// <summary>Engine configuration handed to <c>TyphonBridge</c>.</summary>
    public ScenarioConfig Config { get; init; }

    /// <summary>Measurement window in seconds. Null when <see cref="TickBudget"/> is used.</summary>
    public int? DurationSeconds { get; init; }

    /// <summary>Measurement window in ticks. Null when <see cref="DurationSeconds"/> is used.</summary>
    public int? TickBudget { get; init; }

    /// <summary>When true, the run emits a <c>.typhon-trace</c> to <see cref="TracePath"/>.</summary>
    public bool Trace { get; init; }

    /// <summary>Trace file path for this run — null when <see cref="Trace"/> is false.</summary>
    public string TracePath { get; init; }

    /// <summary>Assertions evaluated once the run completes.</summary>
    public AssertionSettings Assertions { get; init; }
}
