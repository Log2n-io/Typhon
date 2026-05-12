using Typhon.Engine;

namespace AntHill;

/// <summary>
/// Merged per-ant simulation system. Walks each Ant cluster once per tick and performs all five logical
/// operations in registers, avoiding the redundant cluster walks the previous tier-split topology
/// (Metabolism×4 + Brain×4 + PheroDep×4 + MoveAll + FoodDetect = 14 systems) paid each tick:
/// <list type="number">
///   <item>Energy decay + respawn — gated by per-tier <c>AmortMetabolism</c> rate; emits AntDiedEvent.</item>
///   <item>Position integration + edge-bounce reflection.</item>
///   <item>Food/nest interaction — pickup reverses heading; smell-only hit steers toward food; drop at nest. Emits FoodPickedUp/Delivered.</item>
///   <item>Pheromone steering — gated by <c>AmortBrain</c> rate; skipped this tick on pickup/respawn (smell-override).</item>
///   <item>Pheromone deposit — gated by <c>AmortPheroDep</c> rate; <c>amortScale</c> preserves the time-integrated pheromone field.</item>
/// </list>
///
/// <para><b>Inter-cluster pheromone ordering</b> — within a single ant, step 4 (sense) reads the grid before step 5 (deposit) writes it.
/// Across clusters running on parallel workers, step 5 deposits may interleave with step 4 reads on another cluster's ants. This relaxes
/// the previous phase-barrier guarantee (Brain phase strictly before Trail phase). The race is bounded by the 0.995/tick decay factor and
/// is imperceptible in simulation output. Documented as a deliberate trade for cluster-walk cache locality.</para>
///
/// <para><b>Phase relationship</b> — runs in <see cref="AntPhases.Simulation"/> after <c>TierAssignment</c> (Phase.Input).
/// <c>PheroDecay</c> (Phase.Trail) runs after on the W×W on PheromoneGrid. Render pipeline reads
/// the ant component state from this tick's output.</para>
/// </summary>
internal sealed class AntUpdateSystem : QuerySystem
{
    private readonly TyphonBridge _bridge;
    public AntUpdateSystem(TyphonBridge bridge) { _bridge = bridge; }

    protected override void Configure(SystemBuilder b) => b
        .Name("AntUpdate")
        .Phase(AntPhases.Simulation)
        .Parallel()
        .ChunksPerWorker(2f)
        // Component access — single writer across (Bounds, Velocity, AntState); Genetics read-only post-spawn.
        .Reads<Genetics>()
        .Writes<WorldBounds>()
        .Writes<Velocity>()
        .Writes<AntState>()
        // Resource access — PheromoneGrid is both read (sensing) and written (deposit) within this system body.
        // PheroDecay (Phase.Trail) is the only other writer; ordered after via cross-phase phase order.
        .ReadsResource("PheromoneGrid")
        .WritesResource("PheromoneGrid")
        .WritesResource("FoodInventory")
        .WritesResource("NestInventory")
        // Event emissions — consumed by AntStatsAggregator (Phase.Render).
        .WritesEvents(_bridge._antDiedQueue)
        .WritesEvents(_bridge._foodPickedUpQueue)
        .WritesEvents(_bridge._foodDeliveredQueue)
        .Input(() => _bridge._antView)
        .After("TierAssignment");

    protected override void Execute(TickContext ctx) => _bridge.AntUpdateTick(ctx);
}
