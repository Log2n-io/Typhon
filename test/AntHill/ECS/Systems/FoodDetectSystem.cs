using Typhon.Engine;

namespace AntHill;

/// <summary>
/// Food smell + pickup + nest drop. Reads bounds (positional query against the food grid +
/// nest cache) and velocity (re-orientation toward smelled food); writes velocity (heading)
/// and ant state (foraging ↔ returning). Atomically mutates the food + nest inventories;
/// emits the food-pickup / food-delivered events that <see cref="AntStatsAggregator"/>
/// consumes downstream.
/// </summary>
internal sealed class FoodDetectSystem : QuerySystem
{
    private readonly TyphonBridge _bridge;
    public FoodDetectSystem(TyphonBridge bridge) { _bridge = bridge; }

    protected override void Configure(SystemBuilder b) => b
        .Name("FoodDetect")
        .Phase(AntPhases.Sense)
        .Parallel()
        .Reads<WorldBounds>()
        .Writes<Velocity>()
        .Writes<AntState>()
        .ReadsSnapshot<Genetics>()
        .ReadsResource("FoodGrid")
        .WritesResource("FoodInventory")
        .WritesResource("NestInventory")
        .WritesEvents(_bridge._foodPickedUpQueue)
        .WritesEvents(_bridge._foodDeliveredQueue)
        .Input(() => _bridge._antView)
        .After("MoveAll");

    protected override void Execute(TickContext ctx) => _bridge.FoodDetectTick(ctx);
}
