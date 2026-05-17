namespace AntHill.Core;

/// <summary>
/// AntHill custom phases — reuses the engine-shipped <see cref="Phase.Input"/> token and adds three
/// of its own. The four phases form the DAG-local total order of the "AntHill" DAG (declared via
/// <see cref="Dag.Phases"/> in <c>TyphonBridge.BuildSchedule</c>); every system in phase N completes
/// before any system in phase N+1.
///
/// Pipeline (top → bottom):
/// <list type="bullet">
///   <item><see cref="Phase.Input"/> — ToolCommand / EnvironmentTick / TierAssignment</item>
///   <item><see cref="Simulation"/> — AntUpdate (merged per-ant simulation) + SpiderUpdate</item>
///   <item><see cref="Trail"/> — pheromone decay, fire CA, vegetation</item>
///   <item><see cref="Render"/> — Prepare/Fill/Publish render buffers + stats aggregation + heatmaps</item>
/// </list>
/// </summary>
public static class AntPhases
{
    /// <summary>
    /// Single per-ant simulation phase. <c>AntUpdateSystem</c> performs energy decay + respawn, food/nest
    /// interaction, pheromone steering, position integration, and pheromone deposit in one cluster walk per
    /// tick. Tier amortization (formerly four separate systems per phase: Metabolism/Brain/PheroDep × T0..T3)
    /// is now per-cluster gating inside the system body; per-step <c>amortScale</c> multipliers preserve the
    /// time-integrated semantics of each step.
    /// </summary>
    public static readonly Phase Simulation = new("Simulation");

    /// <summary>Pheromone grid evaporation sweep. Runs after <c>AntUpdate</c> on the W×W on PheromoneGrid.</summary>
    public static readonly Phase Trail     = new("Trail");

    /// <summary>Render-frame assembly pipeline: AntStats → PrepareRenderBuffer → FillRenderBuffer → PublishRenderFrame.</summary>
    public static readonly Phase Render    = new("Render");
}
