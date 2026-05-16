using Typhon.Engine;

namespace AntHill;

/// <summary>
/// AntHill custom phases — extends the engine-shipped <see cref="Phase.Input"/>, etc., with the
/// six tick stages that drive ant simulation. Phases form a total order (per RFC 07 / Q3); the
/// ordered list is wired into <c>RuntimeOptions.Phases</c> at engine bootstrap.
///
/// Pipeline (top → bottom):
/// <list type="bullet">
///   <item><see cref="Phase.Input"/> — TierAssignment (camera → spatial-grid tiers)</item>
///   <item><see cref="Movement"/> — MoveAll (apply velocity to bounds)</item>
///   <item><see cref="Lifecycle"/> — Metabolism (energy decay, death/respawn) per tier</item>
///   <item><see cref="Sense"/> — FoodDetect (food smell + pickup + nest drop)</item>
///   <item><see cref="Brain"/> — pheromone steering + wander, per tier</item>
///   <item><see cref="Trail"/> — pheromone deposit + decay</item>
///   <item><see cref="Render"/> — Prepare/Fill/Publish render buffers + stats aggregation</item>
/// </list>
///
/// Splitting Sense / Brain / Trail rather than collapsing into one "Simulation" phase is
/// deliberate — each phase boundary becomes a visible stripe in the Workbench System DAG and
/// Critical-Path views, which is the showcase value of the migration.
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
