using Typhon.Engine;

namespace AntHill;

/// <summary>
/// Pheromone-driven steering + wander. Reads bounds (sensor positions) + velocity (current
/// heading) + state (foraging vs returning) + genetics (home nest), reads the pheromone grid
/// resource, writes velocity (new heading). Tier-amortized like Metabolism — chained via
/// <c>.After</c> within <see cref="AntPhases.Brain"/> for W×W disambiguation on velocity.
/// </summary>
internal abstract class BrainSystemBase : QuerySystem
{
    protected readonly TyphonBridge Bridge;
    protected BrainSystemBase(TyphonBridge bridge) { Bridge = bridge; }

    protected void ConfigureCommon(SystemBuilder b, string name, SimTier tier, int cellAmortize, string[] after) => b
        .Name(name)
        .Phase(AntPhases.Brain)
        .Tier(tier)
        .CellAmortize(cellAmortize)
        .Parallel()
        .Reads<WorldBounds>()
        .Writes<Velocity>()
        .ReadsSnapshot<AntState>()
        .ReadsSnapshot<Genetics>()
        .ReadsResource("PheromoneGrid")
        .Input(() => Bridge._antView)
        .AfterAll(after);

    protected override void Execute(TickContext ctx) => Bridge.AntBrainTick(ctx);
}

// All-pairs ordering on every Brain_T* — the four systems all `Writes<Velocity>`, so the
// AccessDagDeriver requires a direct edge between every pair (it doesn't transitively close a
// chain). The Metabolism_T* refs are leftover from the original DAG (Brain reads what
// Metabolism wrote on respawn, but Metabolism is in an earlier phase so phase ordering already
// covers that — keeping them here doesn't hurt though).
internal sealed class BrainT0System : BrainSystemBase
{
    public BrainT0System(TyphonBridge bridge) : base(bridge) { }
    protected override void Configure(SystemBuilder b)
        => ConfigureCommon(b, "Brain_T0", SimTier.Tier0, 1, ["FoodDetect"]);
}

internal sealed class BrainT1System : BrainSystemBase
{
    public BrainT1System(TyphonBridge bridge) : base(bridge) { }
    protected override void Configure(SystemBuilder b)
        => ConfigureCommon(b, "Brain_T1", SimTier.Tier1, 8, ["Brain_T0"]);
}

internal sealed class BrainT2System : BrainSystemBase
{
    public BrainT2System(TyphonBridge bridge) : base(bridge) { }
    protected override void Configure(SystemBuilder b)
        => ConfigureCommon(b, "Brain_T2", SimTier.Tier2, 30, ["Brain_T0", "Brain_T1"]);
}

internal sealed class BrainT3System : BrainSystemBase
{
    public BrainT3System(TyphonBridge bridge) : base(bridge) { }
    protected override void Configure(SystemBuilder b)
        => ConfigureCommon(b, "Brain_T3", SimTier.Tier3, 60, ["Brain_T0", "Brain_T1", "Brain_T2"]);
}
