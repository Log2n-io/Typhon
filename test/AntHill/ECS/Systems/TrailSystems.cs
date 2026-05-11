using Typhon.Engine;

namespace AntHill;

/// <summary>
/// Linear evaporate over the entire pheromone grid. Pure callback — per-cell sweep, no entity iteration.
/// Sole W×W with <see cref="AntUpdateSystem"/> on the PheromoneGrid resource; ordered via cross-phase
/// rules (Simulation → Trail), so no explicit edge is needed.
/// </summary>
internal sealed class PheroDecaySystem : CallbackSystem
{
    private readonly TyphonBridge _bridge;
    public PheroDecaySystem(TyphonBridge bridge) { _bridge = bridge; }

    protected override void Configure(SystemBuilder b) => b
        .Name("PheroDecay")
        .Phase(AntPhases.Trail)
        .WritesResource("PheromoneGrid");

    protected override void Execute(TickContext ctx) => _bridge.PheromoneDecayTick(ctx);
}
