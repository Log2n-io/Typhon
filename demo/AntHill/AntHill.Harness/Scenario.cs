using System.Collections.Generic;

namespace AntHill.Harness;

/// <summary>
/// A harness scenario as loaded from a YAML file. One scenario expands into one run per entry
/// in <see cref="Workers"/> (the worker-scaling sweep). See <see cref="ScenarioLoader"/>.
/// </summary>
public sealed class Scenario
{
    /// <summary>Scenario identifier — used in report output and trace file names.</summary>
    public string Name { get; set; }

    /// <summary>RNG seed for entity spawning (see <c>AntHill.Core.ScenarioConfig.Seed</c>).</summary>
    public int Seed { get; set; } = 42;

    /// <summary>World size. Fixed at 20000 in v1 — any other value is rejected at load.</summary>
    public int World { get; set; } = 20_000;

    /// <summary>Total ants spawned across all nests.</summary>
    public int Ants { get; set; }

    /// <summary>Worker counts to sweep — one run is executed per entry.</summary>
    public List<int> Workers { get; set; }

    /// <summary>Measurement window in seconds. Mutually exclusive with <see cref="Ticks"/>.</summary>
    public int? Duration { get; set; }

    /// <summary>Measurement window in ticks. Mutually exclusive with <see cref="Duration"/>.</summary>
    public int? Ticks { get; set; }

    /// <summary>Simulation settings (tier mode).</summary>
    public SimulationSettings Simulation { get; set; }

    /// <summary>When true, each run emits a <c>.typhon-trace</c> file.</summary>
    public bool Trace { get; set; }

    /// <summary>Per-run assertions evaluated after each run completes.</summary>
    public AssertionSettings Assertions { get; set; }
}

/// <summary>Simulation-level scenario settings.</summary>
public sealed class SimulationSettings
{
    /// <summary>Tier-assignment mode: <c>camera</c> or <c>uniform-t0</c>.</summary>
    public string TierMode { get; set; } = "camera";
}

/// <summary>Optional per-run assertions. A null member means "not asserted".</summary>
public sealed class AssertionSettings
{
    /// <summary>Bound on the number of exceptions / engine errors during the run.</summary>
    public BoundSpec Exceptions { get; set; }

    /// <summary>Bound / invariant on the final ant count.</summary>
    public BoundSpec AntCount { get; set; }

    /// <summary>Bound on the p99 tick duration in milliseconds (advisory).</summary>
    public BoundSpec TickP99Ms { get; set; }
}

/// <summary>A single assertion bound. <see cref="Max"/> / <see cref="Min"/> are inclusive limits.</summary>
public sealed class BoundSpec
{
    /// <summary>Inclusive upper bound, if set.</summary>
    public double? Max { get; set; }

    /// <summary>Inclusive lower bound, if set.</summary>
    public double? Min { get; set; }

    /// <summary>When true, the value is asserted to be a true invariant (documents intent).</summary>
    public bool Invariant { get; set; }
}
