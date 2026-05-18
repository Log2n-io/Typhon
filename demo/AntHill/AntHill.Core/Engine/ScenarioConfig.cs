using JetBrains.Annotations;

namespace AntHill.Core;

/// <summary>
/// How the simulation grid assigns <c>SimTier</c> values to cells each tick.
/// </summary>
public enum TierMode
{
    /// <summary>
    /// Tiers follow concentric distance rings around the camera AABB centre — the interactive
    /// default. Cells far from the camera are amortized; only the on-camera region runs at full
    /// per-tick fidelity.
    /// </summary>
    Camera,

    /// <summary>
    /// Every cell is forced to <c>Tier0</c> — the whole world runs at full per-tick fidelity.
    /// This is the worst-case stress configuration used by the validation harness; camera
    /// position has no effect.
    /// </summary>
    UniformT0,
}

/// <summary>
/// Runtime configuration consumed by <see cref="TyphonBridge"/>: the knobs a harness scenario
/// drives. The defaults reproduce the historical hard-coded behavior, so <c>AntHill.Demo</c> —
/// which constructs <see cref="TyphonBridge"/> without a config — is unaffected.
/// </summary>
[PublicAPI]
public sealed class ScenarioConfig
{
    /// <summary>
    /// RNG seed for entity spawning. Ant, spider, and food spawns use <c>Seed</c>, <c>Seed + 1</c>,
    /// and <c>Seed + 2</c> as independent streams.
    /// </summary>
    public int Seed { get; set; } = 42;

    /// <summary>Total ants spawned across all nests. Defaults to <see cref="TyphonBridge.AntCount"/>.</summary>
    public int AntCount { get; set; } = TyphonBridge.AntCount;

    /// <summary>Runtime worker-thread count. Defaults to <see cref="TyphonBridge.DefaultWorkerCount"/>.</summary>
    public int WorkerCount { get; set; } = TyphonBridge.DefaultWorkerCount;

    /// <summary>
    /// Grid tier-assignment mode. <see cref="TierMode.Camera"/> is the interactive default;
    /// <see cref="TierMode.UniformT0"/> is the harness full-fidelity stress mode.
    /// </summary>
    public TierMode TierMode { get; set; } = TierMode.Camera;
}
