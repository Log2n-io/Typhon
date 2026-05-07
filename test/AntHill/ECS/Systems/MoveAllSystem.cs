using Typhon.Engine;

namespace AntHill;

/// <summary>
/// Integrates velocity into world bounds and reflects on world-edge collisions. Sole writer of
/// <c>WorldBounds</c> in <see cref="AntPhases.Movement"/>; also writes <c>Velocity</c> on
/// reflection (sign flip), so it owns the velocity slot for this phase too.
/// </summary>
internal sealed class MoveAllSystem : QuerySystem
{
    private readonly TyphonBridge _bridge;
    public MoveAllSystem(TyphonBridge bridge) { _bridge = bridge; }

    protected override void Configure(SystemBuilder b) => b
        .Name("MoveAll")
        .Phase(AntPhases.Movement)
        .Parallel()
        // No WritesVersioned — components are StorageMode.SingleVersion. Adding it would force
        // the per-chunk Transaction fallback path which does NOT populate ctx.Accessor, breaking
        // every cluster enumerator call in the body.
        .Writes<WorldBounds>()
        .Writes<Velocity>()
        .Input(() => _bridge._antView)
        .After("TierAssignment");

    protected override void Execute(TickContext ctx) => _bridge.MoveAllAnts(ctx);
}
