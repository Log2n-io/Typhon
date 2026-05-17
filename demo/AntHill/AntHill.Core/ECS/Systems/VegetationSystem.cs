namespace AntHill.Core;

/// <summary>
/// Phase 6C — drives the 100 k plant carpet. At 10 Hz (every 6th 60 Hz tick, matching the fire
/// CA cadence) scans every plant:
///   • Alive plant on a Burning cell → start the burn countdown (2 s @ 10 Hz), decrement the cell's
///     density so the next FireGrid tick spreads slower through the newly-empty pocket.
///   • Burning plant → decrement countdown; transition to Despawned when it reaches 0.
///
/// State transitions push the plant index onto <see cref="PlantGrid.DirtyIndices"/> so the next
/// <c>PrepareRender</c> hands the list to Godot for a `SetInstanceColor` update.
///
/// Lives in <see cref="AntPhases.Trail"/> alongside <c>FireTickSystem</c>. Declares
/// <c>ReadsResource("FireGrid")</c> + <c>WritesResource("PlantGrid")</c> so the auto-DAG orders us
/// after FireTickSystem this tick (FireGrid is the resource it just wrote).
/// </summary>
internal sealed class VegetationSystem : CallbackSystem
{
    private const int CaPeriodTicks = 6;                                    // 60 Hz / 10 Hz = 6 — same cadence as FireTickSystem

    private readonly TyphonBridge _bridge;
    public VegetationSystem(TyphonBridge bridge) { _bridge = bridge; }

    protected override void Configure(SystemBuilder b) => b
        .Name("VegetationTick")
        .Phase(AntPhases.Trail)
        .ReadsResource("FireGrid")
        .WritesResource("PlantGrid");

    protected override void Execute(TickContext ctx)
    {
        if ((ctx.TickNumber % CaPeriodTicks) != 0) return;

        var pg = _bridge.PlantGrid;
        if (pg == null) return;                                             // heightmap not wired yet — fire CA runs uniform until plants exist

        var fire = _bridge.FireState;
        var state = pg.State;
        var cell = pg.Cell;
        var density = pg.Density;
        var dirty = pg.DirtyIndices;
        var densityChanged = false;

        for (var i = 0; i < PlantGrid.Count; i++)
        {
            var s = state[i];
            if (s == PlantGrid.Despawned) continue;

            if (s == PlantGrid.Alive)
            {
                var cs = fire[cell[i]];
                if (cs >= FireGrid.BurnMin && cs <= FireGrid.BurnStart)
                {
                    state[i] = PlantGrid.BurnCountdownStart;
                    density[cell[i]]--;
                    densityChanged = true;
                    dirty.Enqueue(i);
                }
            }
            else                                                            // counting down from BurnCountdownStart..1
            {
                var n = (byte)(s - 1);
                state[i] = n == 0 ? PlantGrid.Despawned : n;
                if (n == 0) dirty.Enqueue(i);                               // alpha → 0 next render frame
            }
        }

        if (densityChanged) pg.RecomputeDensityFactor();
    }
}
