namespace AntHill.Core;

/// <summary>
/// Phase 6B — runs the Drossel-Schwabl forest fire CA at 10 Hz (every 6th 60 Hz tick).
/// Pure callback — no entity access. Mirrors <see cref="PheroDecaySystem"/>'s shape: lives in
/// <see cref="AntPhases.Trail"/> alongside pheromone evaporation, since both are "ambient
/// environment" sweeps that don't read/write entities.
/// </summary>
internal sealed class FireTickSystem : CallbackSystem
{
    private const int CaPeriodTicks = 6;   // 60 Hz / 10 Hz = 6

    private readonly TyphonBridge _bridge;
    public FireTickSystem(TyphonBridge bridge) { _bridge = bridge; }

    protected override void Configure(SystemBuilder b) => b
        .Name("FireTick")
        .Phase(AntPhases.Trail)
        .WritesResource("FireGrid");

    protected override void Execute(TickContext ctx)
    {
        if ((ctx.TickNumber % CaPeriodTicks) != 0) return;
        _bridge.FireTick(ctx);
    }
}
