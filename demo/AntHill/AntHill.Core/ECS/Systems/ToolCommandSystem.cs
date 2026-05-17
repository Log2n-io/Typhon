namespace AntHill.Core;

/// <summary>
/// Drains the god-game tool command queue (filled by Godot's input handlers) and applies
/// sim-side mutations under a single transaction. Runs in <see cref="Phase.Input"/> before
/// <c>TierAssignmentSystem</c> + <c>AntUpdateSystem</c>, so newly-placed food / rocks and
/// culled ants are visible in the same tick's simulation.
///
/// All sim-state arrays (<c>_foodCache</c>, <c>_rockPositions</c>, etc.) are mutated here on a
/// single worker thread, satisfying Typhon's per-tick transaction affinity (see CLAUDE.md).
/// The phase barrier ensures any subsequent system in <see cref="AntPhases.Simulation"/>
/// captures the new array references at the top of its body.
/// </summary>
internal sealed class ToolCommandSystem : CallbackSystem
{
    private readonly TyphonBridge _bridge;
    public ToolCommandSystem(TyphonBridge bridge) { _bridge = bridge; }

    protected override void Configure(SystemBuilder b) => b
        .Name("ToolCommand")
        .Phase(Phase.Input)
        .WritesResource("ToolCommands")
        .WritesResource("FoodInventory")
        .WritesResource("EventLog");

    protected override void Execute(TickContext ctx)
    {
        var queue = _bridge.ToolCommands;
        if (queue.IsEmpty) return;

        var foodAdded = false;
        var t = _bridge.SimTimeSec;
        var simToWorld = 100f / TyphonBridge.WorldSize;   // mirrors AntRenderer.SimToWorld

        using var tx = _bridge.DBE.CreateQuickTransaction();
        while (queue.TryDequeue(out var cmd))
        {
            switch (cmd.Kind)
            {
                case ToolCommandKind.PlaceFood:
                {
                    _bridge.RuntimeSpawnFood(tx, cmd.X, cmd.Y, cmd.Amount);
                    foodAdded = true;
                    _bridge.EventLog.Enqueue(new LogEntry(
                        t,
                        $"Food placed at ({cmd.X * simToWorld:F1}, {cmd.Y * simToWorld:F1})",
                        cmd.X * simToWorld, cmd.Y * simToWorld, LogSeverity.Tool));
                    break;
                }
                case ToolCommandKind.PlaceRock:
                {
                    _bridge.RuntimeSpawnRock(tx, cmd.X, cmd.Y);
                    _bridge.EventLog.Enqueue(new LogEntry(
                        t,
                        $"Rock placed at ({cmd.X * simToWorld:F1}, {cmd.Y * simToWorld:F1})",
                        cmd.X * simToWorld, cmd.Y * simToWorld, LogSeverity.Tool));
                    break;
                }
                case ToolCommandKind.Cull:
                {
                    var killed = _bridge.RuntimeCullAnts(tx, cmd.X, cmd.Y, cmd.Radius);
                    _bridge.EventLog.Enqueue(new LogEntry(
                        t,
                        $"{killed} ants culled at ({cmd.X * simToWorld:F1}, {cmd.Y * simToWorld:F1})",
                        cmd.X * simToWorld, cmd.Y * simToWorld, LogSeverity.Action));
                    break;
                }
                case ToolCommandKind.Ignite:
                {
                    // Phase 6B — IgniteAt flips Fuel cells within a 6-cell radius (= 13 cells across,
                    // ~3.25 m worldspace at 400×400 / 0.25 m per cell) to Burning. Doubled from 3 when
                    // FireGrid resolution jumped 200→400 to keep the same worldspace ignition area.
                    _bridge.IgniteAt(cmd.X, cmd.Y, radius: 6);
                    break;
                }
            }
        }
        tx.Commit();

        if (foodAdded) _bridge.RebuildFoodGridPublic();
    }
}
