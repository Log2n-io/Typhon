using Typhon.Engine;

namespace AntHill;

/// <summary>
/// Pheromone deposit (food trail when returning, faint home trail when foraging). All four
/// tiered deposit systems share <c>WritesResource("PheromoneGrid")</c> with PheroDecay → the
/// AccessDagDeriver requires direct pairwise edges between every writer (no transitive closure),
/// so every PheroDep_Tn declares <c>.AfterAll</c> over every prior tier and PheroDecay runs
/// after all four.
/// </summary>
internal abstract class PheroDepSystemBase : QuerySystem
{
    protected readonly TyphonBridge Bridge;
    protected PheroDepSystemBase(TyphonBridge bridge) { Bridge = bridge; }

    protected void ConfigureCommon(SystemBuilder b, string name, SimTier tier, int cellAmortize, string[] after) => b
        .Name(name)
        .Phase(AntPhases.Trail)
        .Tier(tier)
        .CellAmortize(cellAmortize)
        .Parallel()
        .ReadsSnapshot<WorldBounds>()
        .ReadsSnapshot<AntState>()
        .WritesResource("PheromoneGrid")
        .Input(() => Bridge._antView)
        .AfterAll(after);

    protected override void Execute(TickContext ctx) => Bridge.PheromoneDepositTick(ctx);
}

internal sealed class PheroDepT0System : PheroDepSystemBase
{
    public PheroDepT0System(TyphonBridge bridge) : base(bridge) { }
    protected override void Configure(SystemBuilder b) => ConfigureCommon(b, "PheroDep_T0", SimTier.Tier0, 1, ["Brain_T0", "Brain_T1", "Brain_T2", "Brain_T3"]);
}

internal sealed class PheroDepT1System : PheroDepSystemBase
{
    public PheroDepT1System(TyphonBridge bridge) : base(bridge) { }
    protected override void Configure(SystemBuilder b) => ConfigureCommon(b, "PheroDep_T1", SimTier.Tier1, 2, ["PheroDep_T0"]);
}

internal sealed class PheroDepT2System : PheroDepSystemBase
{
    public PheroDepT2System(TyphonBridge bridge) : base(bridge) { }
    protected override void Configure(SystemBuilder b) => ConfigureCommon(b, "PheroDep_T2", SimTier.Tier2, 4, ["PheroDep_T0", "PheroDep_T1"]);
}

internal sealed class PheroDepT3System : PheroDepSystemBase
{
    public PheroDepT3System(TyphonBridge bridge) : base(bridge) { }
    protected override void Configure(SystemBuilder b) => ConfigureCommon(b, "PheroDep_T3", SimTier.Tier3, 8, ["PheroDep_T0", "PheroDep_T1", "PheroDep_T2"]);
}

/// <summary>
/// Linear evaporate over the entire pheromone grid. Pure callback. Caps the deposit chain.
/// </summary>
internal sealed class PheroDecaySystem : CallbackSystem
{
    private readonly TyphonBridge _bridge;
    public PheroDecaySystem(TyphonBridge bridge) { _bridge = bridge; }

    protected override void Configure(SystemBuilder b) => b
        .Name("PheroDecay")
        .Phase(AntPhases.Trail)
        .WritesResource("PheromoneGrid")
        .AfterAll("PheroDep_T0", "PheroDep_T1", "PheroDep_T2", "PheroDep_T3");

    protected override void Execute(TickContext ctx) => _bridge.PheromoneDecayTick(ctx);
}
