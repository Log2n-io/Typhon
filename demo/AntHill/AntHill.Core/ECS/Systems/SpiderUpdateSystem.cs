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
        // Spider state lives outside ECS. Keep it as a logical resource declaration so tooling records the mutation
        // and a future reader/writer of the same state can derive an edge. It is NOT required to keep this callback in
        // the DAG: registration controls membership, and After("AntUpdate") supplies the ordering used today.
        .WritesResource("Spiders")
        .After("AntUpdate");

    protected override void Execute(TickContext ctx) => _bridge.SpiderUpdateTick(ctx);
}
