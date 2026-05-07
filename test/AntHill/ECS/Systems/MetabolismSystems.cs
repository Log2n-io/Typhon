using Typhon.Engine;

namespace AntHill;

/// <summary>
/// Energy decay + death/respawn. Tier-discriminated: T0 every tick, T1 every 8, T2 every 30,
/// T3 every 60. All four systems share the same execute body and the same write set
/// (<c>AntState</c>, <c>WorldBounds</c>, <c>Velocity</c> on respawn) — RFC 07 / Q5 forces a
/// W×W chain via <c>.After</c> within <see cref="AntPhases.Lifecycle"/>. The chain is logically
/// safe because tier filters ensure disjoint clusters per tick, but the scheduler can't prove
/// it; the chain is the cost of the showcase.
/// </summary>
internal abstract class MetabolismSystemBase : QuerySystem
{
    protected readonly TyphonBridge Bridge;
    protected MetabolismSystemBase(TyphonBridge bridge) { Bridge = bridge; }

    // RFC 07 / Q5 W×W disambiguation requires DIRECT pairwise edges between every pair of writers
    // — a linear chain T0→T1→T2→T3 isn't enough, the deriver doesn't compute transitive closure.
    // Each tier declares .AfterAll over every prior tier so the all-pairs constraint is satisfied.
    protected void ConfigureCommon(SystemBuilder b, string name, SimTier tier, int cellAmortize, string[] after) => b
        .Name(name)
        .Phase(AntPhases.Lifecycle)
        .Tier(tier)
        .CellAmortize(cellAmortize)
        .Parallel()
        .ReadsSnapshot<Genetics>()
        .Writes<AntState>()
        .Writes<WorldBounds>()
        .Writes<Velocity>()
        .WritesResource("NestInventory")
        .WritesEvents(Bridge._antDiedQueue)
        .Input(() => Bridge._antView)
        .AfterAll(after);

    protected override void Execute(TickContext ctx) => Bridge.MetabolismTick(ctx);
}

internal sealed class MetabolismT0System : MetabolismSystemBase
{
    public MetabolismT0System(TyphonBridge bridge) : base(bridge) { }
    protected override void Configure(SystemBuilder b) => ConfigureCommon(b, "Metabolism_T0", SimTier.Tier0, 1, ["TierAssignment"]);
}

internal sealed class MetabolismT1System : MetabolismSystemBase
{
    public MetabolismT1System(TyphonBridge bridge) : base(bridge) { }
    protected override void Configure(SystemBuilder b) => ConfigureCommon(b, "Metabolism_T1", SimTier.Tier1, 8, ["Metabolism_T0"]);
}

internal sealed class MetabolismT2System : MetabolismSystemBase
{
    public MetabolismT2System(TyphonBridge bridge) : base(bridge) { }
    protected override void Configure(SystemBuilder b) => ConfigureCommon(b, "Metabolism_T2", SimTier.Tier2, 30, ["Metabolism_T0", "Metabolism_T1"]);
}

internal sealed class MetabolismT3System : MetabolismSystemBase
{
    public MetabolismT3System(TyphonBridge bridge) : base(bridge) { }
    protected override void Configure(SystemBuilder b) => ConfigureCommon(b, "Metabolism_T3", SimTier.Tier3, 60, ["Metabolism_T0", "Metabolism_T1", "Metabolism_T2"]);
}
