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
    public static readonly Phase Movement  = new("Movement");
    public static readonly Phase Lifecycle = new("Lifecycle");
    public static readonly Phase Sense     = new("Sense");
    public static readonly Phase Brain     = new("Brain");
    public static readonly Phase Trail     = new("Trail");
    public static readonly Phase Render    = new("Render");
}
