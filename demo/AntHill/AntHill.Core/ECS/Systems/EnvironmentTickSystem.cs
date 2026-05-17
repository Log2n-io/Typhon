namespace AntHill.Core;

/// <summary>
/// Phase 6A — Daisyworld luminosity + sim-time day/night cycle. Pure callback — no entity access.
/// Advances <c>TyphonBridge._dayPhase</c> and computes <c>EnvironmentBrightness</c>, which the Godot
/// side polls each frame to drive the terrain + ant brightness uniforms and Godot's sun + ambient
/// energy. Runs in <see cref="Phase.Input"/> so downstream systems (if any ever consume the scalar)
/// see the current-tick value.
/// </summary>
internal sealed class EnvironmentTickSystem : CallbackSystem
{
    private readonly TyphonBridge _bridge;
    public EnvironmentTickSystem(TyphonBridge bridge) { _bridge = bridge; }

    protected override void Configure(SystemBuilder b) => b
        .Name("EnvironmentTick")
        .Phase(Phase.Input);

    protected override void Execute(TickContext ctx) => _bridge.EnvironmentTick(ctx);
}
