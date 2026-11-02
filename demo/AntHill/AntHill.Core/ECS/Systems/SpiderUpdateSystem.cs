namespace AntHill.Core;

/// <summary>
/// Phase 5 predator update. Sequential callback over the (8) flat spider arrays on TyphonBridge.
/// Does NOT participate in the Ant archetype DAG — spider state lives outside ECS to keep this
/// system simple and avoid cross-archetype access patterns. Hunting uses a transaction-scoped
/// <see cref="EcsQuery{T}.WhereNearby{TComp}"/> for proximity → destroy.
///
/// Runs in <see cref="AntPhases.Simulation"/> after AntUpdate so the spatial index reflects
/// this-tick's ant positions.
/// </summary>
internal sealed class SpiderUpdateSystem : CallbackSystem
{
    private readonly TyphonBridge _bridge;
    public SpiderUpdateSystem(TyphonBridge bridge) { _bridge = bridge; }

    protected override void Configure(SystemBuilder b) => b
        .Name("SpiderUpdate")
        .Phase(AntPhases.Simulation)
        .After("AntUpdate");

    protected override void Execute(TickContext ctx) => _bridge.SpiderUpdateTick(ctx);
}
