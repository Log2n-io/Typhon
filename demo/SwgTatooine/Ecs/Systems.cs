namespace SwgTatooine;

// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// Systems
//
// Each is a declaration plus one line. What the scheduler needs is in Configure — the phase, the parallelism, and the
// component access that lets the auto-DAG order two systems without either naming the other. The behaviour is in
// SimBridge.
//
// The access declarations are load-bearing rather than documentation: Think writes the motion and brain components and
// reads the placements; Move writes the placements and reads the motion. That inversion is what puts them in order, and
// what lets everything inside a phase run concurrently. Phases order nothing by themselves (cross-phase edges are
// conflict-driven), so a system whose spatial queries read another archetype's positions declares that placement too.
// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>Creature AI: mode transitions, wander destinations and the aggro query.</summary>
internal sealed class CreatureThinkSystem : QuerySystem
{
    private readonly SimBridge _bridge;

    public CreatureThinkSystem(SimBridge bridge) => _bridge = bridge;

    protected override void Configure(SystemBuilder b) => b
        .Name("CreatureThink")
        .Phase(SimPhases.Think)
        .Parallel()
        .ChunksPerWorker(2f)
        .Reads<CreaturePlacement>()
        .Reads<CreatureVitals>()

        // The aggro query reads player positions; declared, so Shuttle's and PlayerMove's writes are ordered around it (rule ED-05).
        .Reads<PlayerPlacement>()
        .Writes<CreatureBrain>()
        .Writes<CreatureMotion>()
        .Input(() => _bridge.CreatureView);

    protected override void Execute(TickContext ctx) => _bridge.CreatureThinkTick(ctx);
}

/// <summary>Player behaviour: the activity mix and where each player is heading.</summary>
internal sealed class PlayerThinkSystem : QuerySystem
{
    private readonly SimBridge _bridge;

    public PlayerThinkSystem(SimBridge bridge) => _bridge = bridge;

    protected override void Configure(SystemBuilder b) => b
        .Name("PlayerThink")
        .Phase(SimPhases.Think)
        .Parallel()
        .ChunksPerWorker(2f)
        .Reads<PlayerPlacement>()
        .Writes<PlayerState>()
        .Writes<PlayerMotion>()
        .Input(() => _bridge.PlayerView);

    protected override void Execute(TickContext ctx) => _bridge.PlayerThinkTick(ctx);
}

/// <summary>Creature position integration — the creature half of the only phase that moves anything.</summary>
internal sealed class CreatureMoveSystem : QuerySystem
{
    private readonly SimBridge _bridge;

    public CreatureMoveSystem(SimBridge bridge) => _bridge = bridge;

    protected override void Configure(SystemBuilder b) => b
        .Name("CreatureMove")
        .Phase(SimPhases.Move)
        .Parallel()
        .ChunksPerWorker(2f)
        .Reads<CreatureMotion>()
        .Reads<CreatureBrain>()
        .Writes<CreaturePlacement>()
        .Input(() => _bridge.CreatureView);

    protected override void Execute(TickContext ctx) => _bridge.CreatureMoveTick(ctx);
}

/// <summary>Player position integration.</summary>
internal sealed class PlayerMoveSystem : QuerySystem
{
    private readonly SimBridge _bridge;

    public PlayerMoveSystem(SimBridge bridge) => _bridge = bridge;

    protected override void Configure(SystemBuilder b) => b
        .Name("PlayerMove")
        .Phase(SimPhases.Move)
        .Parallel()
        .ChunksPerWorker(2f)
        .Reads<PlayerMotion>()
        .Writes<PlayerPlacement>()
        .Input(() => _bridge.PlayerView);

    protected override void Execute(TickContext ctx) => _bridge.PlayerMoveTick(ctx);
}

/// <summary>
/// City NPCs: the 12 % that shuffle, and the 88 % that are walked over to find them.
/// </summary>
internal sealed class NpcMoveSystem : QuerySystem
{
    private readonly SimBridge _bridge;

    public NpcMoveSystem(SimBridge bridge) => _bridge = bridge;

    protected override void Configure(SystemBuilder b) => b
        .Name("NpcMove")
        .Phase(SimPhases.Move)
        .Parallel()
        .ChunksPerWorker(2f)
        .Writes<NpcBrain>()
        .Writes<NpcMotion>()
        .Writes<NpcPlacement>()
        .Input(() => _bridge.NpcView);

    protected override void Execute(TickContext ctx) => _bridge.NpcMoveTick(ctx);
}

/// <summary>Realms G1c: starships fly between waypoints in the space realm — the deep grid's movers.</summary>
internal sealed class ShipMoveSystem : QuerySystem
{
    private readonly SimBridge _bridge;

    public ShipMoveSystem(SimBridge bridge) => _bridge = bridge;

    protected override void Configure(SystemBuilder b) => b
        .Name("ShipMove")
        .Phase(SimPhases.Move)
        .Parallel()
        .ChunksPerWorker(2f)
        .Writes<ShipPlacement>()
        .Writes<ShipMotion>()
        .Input(() => _bridge.ShipView);

    protected override void Execute(TickContext ctx) => _bridge.ShipMoveTick(ctx);
}

/// <summary>Realms G1c: each starship scans a sphere around it every <c>ShipScanPeriodTicks</c>, staggered — the deep grid's 3D queries.</summary>
internal sealed class ShipScanSystem : QuerySystem
{
    private readonly SimBridge _bridge;

    public ShipScanSystem(SimBridge bridge) => _bridge = bridge;

    protected override void Configure(SystemBuilder b) => b
        .Name("ShipScan")
        .Phase(SimPhases.Awareness)
        .Parallel()
        .ChunksPerWorker(2f)
        .Reads<ShipPlacement>()
        .Input(() => _bridge.ShipView);

    protected override void Execute(TickContext ctx) => _bridge.ShipScanTick(ctx);
}

/// <summary>
/// Interest management over this tick's positions: it declares every placement its queries read, which is what orders it after the systems that move them.
/// </summary>
internal sealed class AwarenessSystem : QuerySystem
{
    private readonly SimBridge _bridge;

    public AwarenessSystem(SimBridge bridge) => _bridge = bridge;

    protected override void Configure(SystemBuilder b)
    {
        b
            .Name("Awareness")
            .Phase(SimPhases.Awareness)
            .Parallel()
            .ChunksPerWorker(2f)

            // Interest management costs ~3.5 us per player against ~9 ns per creature in CreatureThink — a 375x spread in
            // per-entity work inside one DAG. The global 64-entity floor is sized for the cheap end, so with a few hundred
            // players this system gets ceil(320 / 64) = 5 chunks and ChunksPerWorker above cannot lift that. 0 leaves it on
            // the global floor.
            .MinChunkSize(_bridge.AwarenessMinChunk)
            .Reads<PlayerPlacement>()

            // The creature, NPC and structure queries read these. Declaring them is what makes Awareness wait for CreatureMove and NpcMove: cross-phase
            // edges are conflict-driven (rule ED-05), and without them it ran while those systems wrote the positions it queried. StructurePlacement has
            // no writer today; it is declared because the queries read it.
            .Reads<CreaturePlacement>()
            .Reads<NpcPlacement>()
            .Reads<StructurePlacement>()
            .WritesResource("AwarenessStats")
            .Input(() => _bridge.PlayerView);
    }

    protected override void Execute(TickContext ctx) => _bridge.AwarenessTick(ctx);
}

/// <summary>Harvesters and factories. Walks the largest archetype to find the few hundred entities with work.</summary>
internal sealed class EconomySystem : QuerySystem
{
    private readonly SimBridge _bridge;

    public EconomySystem(SimBridge bridge) => _bridge = bridge;

    protected override void Configure(SystemBuilder b) => b
        .Name("Economy")
        .Phase(SimPhases.Economy)
        .Parallel()
        .ChunksPerWorker(2f)
        .Writes<Structure>()
        .Input(() => _bridge.StructureView);

    protected override void Execute(TickContext ctx) => _bridge.EconomyTick(ctx);
}

/// <summary>
/// Creature-side combat: a creature standing in a player's line of fire takes damage, dies, and is revived by its lair.
/// </summary>
/// <remarks>
/// <para><b>Why the creature applies the damage to itself rather than the player applying it.</b> A system may only
/// write components of its OWN input archetype. Reaching across — opening a Creature from a system whose input is
/// Player, through <c>ctx.Transaction</c>, and writing its vitals — stalls the tick loop outright: the runtime reached
/// tick 1 and never advanced. That is filed against the engine; here the model is inverted instead, and the inversion is
/// not a distortion. The spatial query, the range test, the weapon cadence and the health arithmetic are identical; only
/// which side of the exchange runs the code has moved.</para>
/// <para><b>Death is pooled, not structural.</b> A killed creature goes to <see cref="AiMode.Dead"/> and is revived at
/// its lair after the respawn interval, rather than being destroyed and re-spawned. That is what SWG lairs did anyway —
/// a lair owns a fixed set of spawn slots — but it does mean this workload exercises cluster-occupancy churn only
/// through the world build, not per tick. The revive is a TELEPORT back to the lair, which is a large position jump and
/// does exercise migration and cluster-bound recomputation hard.</para>
/// </remarks>
internal sealed class CreatureCombatSystem : QuerySystem
{
    private readonly SimBridge _bridge;

    public CreatureCombatSystem(SimBridge bridge) => _bridge = bridge;

    protected override void Configure(SystemBuilder b) => b
        .Name("CreatureCombat")
        .Phase(SimPhases.Resolve)
        .Parallel()
        .ChunksPerWorker(2f)
        .Reads<CreaturePlacement>()

        // The line-of-fire query reads player positions (rule ED-05: undeclared, PlayerMove would be free to write them while it runs).
        .Reads<PlayerPlacement>()
        .Writes<CreatureVitals>()
        .Writes<CreatureBrain>()
        .Input(() => _bridge.CreatureView);

    protected override void Execute(TickContext ctx) => _bridge.CreatureCombatTick(ctx);
}

/// <summary>
/// Destroy missions: dormant lairs find a player and teleport to a fresh target point; destroyed ones return to the pool.
/// </summary>
/// <remarks>
/// Runs over <see cref="CreatureLair"/> because the whole loop — the seek query, the placement rejection and the
/// teleport — is a write to the lair's own archetype. Core3's algorithm, with its own constants; see
/// <c>SimBridge.Missions.cs</c>.
/// </remarks>
internal sealed class MissionSystem : QuerySystem
{
    private readonly SimBridge _bridge;

    public MissionSystem(SimBridge bridge) => _bridge = bridge;

    protected override void Configure(SystemBuilder b) => b
        .Name("Missions")
        .Phase(SimPhases.Spawn)
        .Parallel()
        .ChunksPerWorker(2f)
        .Writes<Lair>()
        .Writes<LairVitals>()
        .Writes<LairPlacement>()

        // The seek query reads player positions, which Shuttle writes in this same phase: Fresh orders it after Shuttle, so it never reads a position
        // mid-write (a plain Reads with a same-phase writer is a Build error, and Snapshot needs a Versioned component). A cell-walking query still finds
        // an arrival in its new cell only after this tick's fence.
        .ReadsFresh<PlayerPlacement>()
        .Input(() => _bridge.LairView);

    protected override void Execute(TickContext ctx) => _bridge.MissionTick(ctx);
}

/// <summary>
/// Shuttle travel (#910's workload): boards queued players while their port's shuttle is down and transports them to the destination port.
/// </summary>
/// <remarks>
/// In the Spawn phase, ahead of everything that reads <see cref="PlayerPlacement"/> — Think's queries, and <see cref="MissionSystem"/>, which reads it
/// fresh and so runs after it — and of Move, which writes it. A cell-walking query still finds an arrival in its new cell only after this tick's fence.
/// </remarks>
internal sealed class ShuttleSystem : QuerySystem
{
    private readonly SimBridge _bridge;

    public ShuttleSystem(SimBridge bridge) => _bridge = bridge;

    protected override void Configure(SystemBuilder b) => b
        .Name("Shuttle")
        .Phase(SimPhases.Spawn)
        .Parallel()
        .ChunksPerWorker(2f)
        .Writes<PlayerPlacement>()
        .Writes<PlayerState>()
        .Writes<PlayerMotion>()
        .Input(() => _bridge.PlayerView);

    protected override void Execute(TickContext ctx) => _bridge.ShuttleTick(ctx);
}

/// <summary>
/// Realms G1b: applies the queued realm changes — portal crossings PlayerThink queued last tick, and shuttle boardings bound for another planet that
/// Shuttle queued this tick — each a <c>Teleport</c>, in one side transaction with the Commit discipline, so a crossing is committed before the fence
/// moves the entity.
/// </summary>
/// <remarks>
/// Serial, in the Spawn phase after Shuttle (a deviation from 02 § G1's "after the move systems": Move already has a <see cref="PlayerPlacement"/>
/// writer, and a crossing player stands still, so a portal's tick of latency costs nothing).
/// </remarks>
internal sealed class TeleportSystem : CallbackSystem
{
    private readonly SimBridge _bridge;
    private readonly bool _afterShuttle;

    public TeleportSystem(SimBridge bridge, bool afterShuttle)
    {
        _bridge = bridge;
        _afterShuttle = afterShuttle;
    }

    protected override void Configure(SystemBuilder b)
    {
        b.Name("Teleport")
            .Phase(SimPhases.Spawn)
            .Writes<PlayerPlacement>()
            .Writes<PlayerRealm>();
        if (_afterShuttle)
        {
            b.After("Shuttle");
        }
    }

    protected override void Execute(TickContext ctx) => _bridge.TeleportTick(ctx);
}

/// <summary>Per-tick shuttle bookkeeping and, with <c>--probe</c>, the arrival-cell query probe. It declares the player positions its queries read, like
/// any system; a CallbackSystem that declares component access still runs (measured: it did here, every tick).</summary>
internal sealed class ShuttleProbeSystem : CallbackSystem
{
    private readonly SimBridge _bridge;

    public ShuttleProbeSystem(SimBridge bridge) => _bridge = bridge;

    protected override void Configure(SystemBuilder b) => b
        .Name("ShuttleProbe")
        .Phase(SimPhases.Report)
        .WritesResource("ShuttleProbe")

        // With --probe it queries player positions.
        .Reads<PlayerPlacement>();

    protected override void Execute(TickContext ctx) => _bridge.ShuttleProbeTick(ctx);
}

/// <summary>Folds the previous fence's per-archetype maintenance counters every tick, for the run's per-tick means.</summary>
internal sealed class SpatialTelemetrySystem : CallbackSystem
{
    private readonly SimBridge _bridge;

    public SpatialTelemetrySystem(SimBridge bridge) => _bridge = bridge;

    protected override void Configure(SystemBuilder b) => b
        .Name("SpatialTelemetry")
        .Phase(SimPhases.Report)
        .WritesResource("SpatialTelemetry");

    protected override void Execute(TickContext ctx) => _bridge.SpatialTelemetryTick(ctx);
}

/// <summary>Which archetype one split awareness system queries.</summary>
public enum AwarenessTarget
{
    Structures,
    Creatures,
    Npcs,
    Players,
}

/// <summary>
/// One quarter of interest management: the same per-player sweep, but querying a single archetype.
/// </summary>
/// <remarks>
/// <para>The measured problem this exists to test: the combined <see cref="AwarenessSystem"/> is over half the tick and
/// runs on FIVE workers, because chunk count is capped at <c>ceil(entityCount / ParallelQueryMinChunkSize)</c> and 320
/// players over a minimum chunk of 64 is five chunks. Raising <c>ChunksPerWorker</c> cannot help — that term is not the
/// binding one.</para>
/// <para>Four systems over the same 320 players each get their own five chunks, and they declare no shared write, so the
/// scheduler is free to run them at once. Same total work, four times the dispatch width, and no global setting
/// changed.</para>
/// </remarks>
internal sealed class AwarenessSplitSystem : QuerySystem
{
    private readonly SimBridge _bridge;
    private readonly AwarenessTarget _target;

    public AwarenessSplitSystem(SimBridge bridge, AwarenessTarget target)
    {
        _bridge = bridge;
        _target = target;
    }

    protected override void Configure(SystemBuilder b)
    {
        b
            .Name($"Awareness{_target}")
            .Phase(SimPhases.Awareness)
            .Parallel()
            .ChunksPerWorker(2f)
            .Reads<PlayerPlacement>()
            .WritesResource($"AwarenessStats{_target}")
            .Input(() => _bridge.PlayerView);

        // The placement this split's query reads, as AwarenessSystem declares it.
        switch (_target)
        {
            case AwarenessTarget.Structures:
                b.Reads<StructurePlacement>();
                break;
            case AwarenessTarget.Creatures:
                b.Reads<CreaturePlacement>();
                break;
            case AwarenessTarget.Npcs:
                b.Reads<NpcPlacement>();
                break;
        }
    }

    protected override void Execute(TickContext ctx) => _bridge.AwarenessTick(ctx, _target);
}
