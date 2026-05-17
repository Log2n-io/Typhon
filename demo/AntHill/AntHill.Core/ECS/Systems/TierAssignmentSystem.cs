namespace AntHill.Core;

/// <summary>
/// Resets the spatial-grid tier assignment from the camera AABB. Pure callback — no entity
/// access. Owns the only writer of <c>SpatialGrid</c> (tier flags) and the <c>TierMirror</c>
/// resource consumed by the renderer; reads <c>CameraAABB</c> from the UI thread.
/// </summary>
internal sealed class TierAssignmentSystem : CallbackSystem
{
    private readonly TyphonBridge _bridge;
    public TierAssignmentSystem(TyphonBridge bridge) { _bridge = bridge; }

    protected override void Configure(SystemBuilder b) => b
        .Name("TierAssignment")
        .Phase(Phase.Input)
        .Priority(SystemPriority.High)
        .ReadsResource("CameraAABB")
        .WritesResource("SpatialGrid")
        .WritesResource("TierMirror");

    protected override void Execute(TickContext ctx) => _bridge.TierAssignment(ctx);
}
