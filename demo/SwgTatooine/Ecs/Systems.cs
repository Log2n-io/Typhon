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
/// <para><b>Why the creature applies the damage to itself rather than the player applying it.</b> A player-side system that opens the creature through
/// <c>ctx.Transaction</c> and writes its vitals stalls the tick loop at tick 1 — engine bug #907, not a rule. With <c>--combat-model push</c> the range
/// test runs on the player side instead (<see cref="PlayerFireSystem"/>, <see cref="CombatDrainSystem"/>), and this system only applies the shooter counts
/// they produced.</para>
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

    protected override void Configure(SystemBuilder b)
    {
        b
            .Name("CreatureCombat")
            .Phase(SimPhases.Resolve)
            .Parallel()
            .ChunksPerWorker(2f)
            .Reads<CreaturePlacement>()
            .Writes<CreatureVitals>()
            .Writes<CreatureBrain>()
            .Input(() => _bridge.CreatureView);

        if (_bridge.CombatModel == CombatModel.Pull)
        {
            // The line-of-fire query reads player positions (rule ED-05: undeclared, PlayerMove would be free to write them while it runs).
            b.Reads<PlayerPlacement>();
        }
        else
        {
            // The shooter counts the drain folded this tick: the resource edge (rule ED-04) runs this system after it.
            b.ReadsResource(SimBridge.ShooterLaneResource);
            if (_bridge.CombatVerify)
            {
                // --combat-verify replays pull's query.
                b.Reads<PlayerPlacement>();
            }
        }
    }

    protected override void Execute(TickContext ctx) => _bridge.CreatureCombatTick(ctx);
}

/// <summary>
/// Push combat, player side: each player queries the creatures in weapon range and publishes a hit event per creature.
/// </summary>
/// <remarks>
/// Writes nothing but events, so its chunks share no write with each other or with the creature side, and run on the whole pool. Only with
/// <c>--combat-model push</c>.
/// </remarks>
internal sealed class PlayerFireSystem : QuerySystem
{
    private readonly SimBridge _bridge;

    public PlayerFireSystem(SimBridge bridge) => _bridge = bridge;

    protected override void Configure(SystemBuilder b) => b
        .Name("PlayerFire")
        .Phase(SimPhases.Resolve)
        .Parallel()
        .ChunksPerWorker(2f)

        // Per-player work is a radius query, as in Awareness; the same chunk floor.
        .MinChunkSize(_bridge.AwarenessMinChunk)
        .Reads<PlayerPlacement>()

        // The query reads creature positions (rule ED-05).
        .Reads<CreaturePlacement>()
        .WritesEvents(_bridge.CombatQueue)
        .Input(() => _bridge.PlayerView);

    protected override void Execute(TickContext ctx) => _bridge.PlayerFireTick(ctx);
}

/// <summary>
/// Push combat, the serial part: drains the tick's hit events into per-creature shooter counts. Only with <c>--combat-model push</c>.
/// </summary>
/// <remarks>A parallel system cannot consume an event queue (the drain is single-consumer), so the fold sits between the producer and the parallel
/// creature side. Declaring the queue orders it after <see cref="PlayerFireSystem"/> (rule ED-03); the lane resource orders
/// <see cref="CreatureCombatSystem"/> after it (ED-04).</remarks>
internal sealed class CombatDrainSystem : CallbackSystem
{
    private readonly SimBridge _bridge;

    public CombatDrainSystem(SimBridge bridge) => _bridge = bridge;

    protected override void Configure(SystemBuilder b) => b
        .Name("CombatDrain")
        .Phase(SimPhases.Resolve)
        .ReadsEvents(_bridge.CombatQueue)
        .WritesResource(SimBridge.ShooterLaneResource);

    protected override void Execute(TickContext ctx) => _bridge.CombatDrainTick(ctx);
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
