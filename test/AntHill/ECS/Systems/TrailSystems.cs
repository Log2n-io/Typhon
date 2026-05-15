using Typhon.Engine;

namespace AntHill;

/// <summary>
/// Linear evaporate over the entire pheromone grid. Pure callback — per-cell sweep, no entity iteration.
/// Sole W×W with <see cref="AntUpdateSystem"/> on the PheromoneGrid resource; ordered via cross-phase
/// rules (Simulation → Trail), so no explicit edge is needed.
///
/// Runs at 10 Hz (every 6 ticks). Decay factor in <see cref="TyphonBridge.PheromoneDecayTick"/>
/// is the 6-tick equivalent of the original 60 Hz factor, so long-term decay rate is unchanged.
/// Deposits stay at 60 Hz inside <see cref="AntUpdateSystem"/>; they accumulate in the grid
/// itself between decay passes — no staging buffer required.
/// </summary>
internal sealed class PheroDecaySystem : CallbackSystem
{
    private const int DecayPeriodTicks = 6;

    private readonly TyphonBridge _bridge;
    public PheroDecaySystem(TyphonBridge bridge) { _bridge = bridge; }

    protected override void Configure(SystemBuilder b) => b
        .Name("PheroDecay")
        .Phase(AntPhases.Trail)
        .WritesResource("PheromoneGrid");

    protected override void Execute(TickContext ctx)
    {
        if ((ctx.TickNumber % DecayPeriodTicks) != 0) return;
        _bridge.PheromoneDecayTick(ctx);
    }
}
