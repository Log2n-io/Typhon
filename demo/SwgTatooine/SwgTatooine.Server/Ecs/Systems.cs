using System;

namespace SwgTatooine.Server;

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

        // Read for the aggro gate. It was written while the revive lived here; a dead creature is destroyed now, not restored (SimBridge.Lairs.cs).
        .Reads<CreatureVitals>()

        // The aggro query reads player positions, and the pursuit re-reads its quarry's; declared, so Shuttle's and PlayerMove's writes are ordered around it
        // (rule ED-05).
        .Reads<PlayerPlacement>()

        // And whether that player is down: a knocked-out or dead player is nobody's quarry.
        .Reads<PlayerVitals>()
        .Writes<CreatureBrain>()
        .Writes<CreatureMotion>()

        // The think, move and combat systems all write CreatureTimers and none of them used to say so. Phase order happens to serialize them, so the
        // omission never bit — but that is the DAG being lucky rather than informed, which is what rule ED-05 is about.
        .Writes<CreatureTimers>()
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

        // Declared, because the loop reads it to decide whether this player is the client's (SWG-01). Ordering against PlayerSessions, which writes it,
        // currently survives only through the unrelated PlayerMotion edge — which is rule ED-05's definition of the DAG being lucky rather than informed.
        .Reads<PlayerControl>()

        // Whether a player is down, so a downed body is left lying.
        .Reads<PlayerVitals>()

        // A fighter's objective: the lairs standing at a point of interest (lair-objectives.md).
        .Reads<Lair>()
        .Reads<LairPlacement>()
        .Reads<LairVitals>()
        .Writes<PlayerState>()
        .Writes<PlayerMotion>()

        // A scripted player's way round a city (SimBridge.Routes.cs): planned and followed here, read nowhere else.
        .Writes<PlayerRoute>()
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

/// <summary>
/// The lair director (<c>SimBridge.Lairs.cs</c>): destroys corpses, retires spent lairs with their creatures, and refills lairs and sites.
/// </summary>
/// <remarks>
/// Serial, in the Economy phase, through its own side transaction — the shape <see cref="DungeonSystem"/> spawns and destroys with. After Think, which hands
/// it the corpses, and after Apply, which breaks lairs, so both are handled on the tick they happen. The writes are declared for what it SPAWNS: a lair and
/// every component of the creatures it fills itself with.
/// </remarks>
internal sealed class LairLifeSystem : CallbackSystem
{
    private readonly SimBridge _bridge;

    public LairLifeSystem(SimBridge bridge) => _bridge = bridge;

    protected override void Configure(SystemBuilder b) => b
        .Name("LairLife")
        .Phase(SimPhases.Economy)

        // Read, not consumed: a consumer is skipped on a tick with no corpse, and the pass must run on those too (TatooineSim.BuildSchedule).
        .ReadsEvents(_bridge.CorpseQueue)

        // The census counts the players near each point of interest (lair-objectives.md L-1), and who drives them, where (GET /swg/stats).
        .Reads<PlayerPlacement>()
        .Reads<PlayerControl>()
        .Reads<PlayerLook>()
        .Writes<Lair>()
        .Writes<LairPlacement>()
        .Writes<LairVitals>()
        .Writes<LairRealm>()
        .Writes<CreaturePlacement>()
        .Writes<CreatureMotion>()
        .Writes<CreatureVitals>()
        .Writes<CreatureBrain>()
        .Writes<CreatureTimers>()
        .Writes<CreatureRealm>()
        .Writes<CreatureHome>();

    protected override void Execute(TickContext ctx) => _bridge.LairLifeTick(ctx);
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

        // Collision reads the structures around the step this system just produced (the static neighbourhood; see
        // SimBridge.Proximity.cs). Declared because rule ED-05 is about saying so rather than about whether anything
        // happens to write it: nothing writes a structure's placement during a tick, so the declaration orders the graph
        // without constraining it -- and if a later feature does place one, this edge is already right.
        .Reads<StructurePlacement>()

        // And WHAT that structure is: a body that has entered a footprint asks which asset stands there and which way it
        // faces, so it can be pushed out of the asset's own walls rather than out of its bounding box. Read on contacts
        // only (see StructureShapes in SimBridge.Proximity.cs), but declared here because ED-05 is about saying so.
        .Reads<Structure>()

        // Which bodies collide at all: a client's, never the scripted crowd's. PlayerThink already reads it for the
        // mirror-image reason -- it decides for the bodies this does not.
        .Reads<PlayerControl>()

        // The disc each body carries: where its last query was taken from, and how far it may walk from there.
        .Writes<PlayerProximity>()
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
/// The creature's side of a fight: one that has closed on its quarry and stopped attacks it, and pushes the damage as an event.
/// </summary>
/// <remarks>
/// <para><b>It runs no spatial query at all, which is what made SWG-02 cheaper rather than dearer.</b> The system it
/// replaces asked, once per living creature per weapon cycle, "is a player within 75 m?" — on the order of 850 radius
/// queries a tick at the faithful baseline — and then took damage from however many it found, whatever those players were
/// doing (gap G8). A creature that is fighting now knows WHAT it is fighting and reads that entity's position; finding
/// somebody to fight in the first place is <see cref="CreatureThinkSystem"/>'s 24 m aggro bubble, which is untouched and
/// is where the negative-case query load the study is about actually lives.</para>
/// <para><b>It writes nothing outside its own archetype.</b> The damage travels as a <c>CombatEvent</c> to the serial
/// system in <see cref="SimPhases.Apply"/>, which is the only place a Player is opened and written — so the
/// cross-archetype write that #907 is open about happens on one thread, by construction.</para>
/// <para><b>Death is pooled, not structural.</b> A killed creature goes to <see cref="AiMode.Dead"/> and is revived at
/// its lair after the respawn interval, rather than being destroyed and re-spawned. That is what SWG lairs did anyway —
/// a lair owns a fixed set of spawn slots — but it does mean this workload exercises cluster-occupancy churn only
/// through the world build, not per tick. The revive is a TELEPORT back to the lair, which is a large position jump and
/// does exercise migration and cluster-bound recomputation hard; it is <see cref="CreatureThinkSystem"/>'s now.</para>
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

        // The attack's damage, read and never written here — see CreatureThink, which owns the revive and so owns this component.
        .Reads<CreatureVitals>()

        // The quarry's position, read through its EntityId rather than through a query (rule ED-05: undeclared, PlayerMove would be free to write it while
        // this runs).
        .Reads<PlayerPlacement>()

        // A creature whose quarry has gone drops back to Wander here rather than standing frozen mid-swing until its next decision.
        .Writes<CreatureBrain>()

        // Cycles the weapon; see CreatureThink for why this was once missing.
        .Writes<CreatureTimers>()
        .Input(() => _bridge.CreatureView);

    protected override void Execute(TickContext ctx) => _bridge.CreatureCombatTick(ctx);
}

/// <summary>
/// The player's side of a fight: a player in <see cref="PlayerActivity.Combat"/> fires at its own target, and at nothing else.
/// </summary>
/// <remarks>
/// <para><b>This is the system that did not exist, and its absence is what gap G8 was.</b> Combat used to be resolved
/// entirely from the creature's side, so a player's activity, target and weapon were never consulted: standing near a
/// lair was indistinguishable from attacking it. Here a player that is travelling, idling or roaming does no damage, and
/// a player whose target has walked out of weapon range does no damage — both counted, because an acceptance criterion
/// about what does NOT happen cannot be checked against a silent zero.</para>
/// <para><b>It shares no component with <see cref="CreatureCombatSystem"/> in either direction</b>, which is why the two
/// run concurrently in one phase: this one writes the shooter's cooldown and target and READS the creature and lair
/// vitals and placements, and the other does the mirror image. Neither writes what the other reads.</para>
/// </remarks>
internal sealed class PlayerCombatSystem : QuerySystem
{
    private readonly SimBridge _bridge;

    public PlayerCombatSystem(SimBridge bridge) => _bridge = bridge;

    protected override void Configure(SystemBuilder b) => b
        .Name("PlayerCombat")
        .Phase(SimPhases.Resolve)
        .Parallel()
        .ChunksPerWorker(2f)
        .Reads<PlayerPlacement>()

        // Whether this player is fighting at all, and whether a client or the simulation decides for it — the latter settles who may drop a target that
        // walked away.
        .Reads<PlayerState>()
        .Reads<PlayerControl>()

        // The weapon's cooldown and the shooter's own target.
        .Writes<PlayerVitals>()
        .Writes<PlayerSession>()

        // What the acquisition query and the target check read: a candidate must be alive, a lair must still belong to a live mission, and both must be within
        // weapon range of the shooter.
        .Reads<CreaturePlacement>()
        .Reads<CreatureVitals>()
        .Reads<LairPlacement>()
        .Reads<LairVitals>()
        .Reads<Lair>()
        .Input(() => _bridge.PlayerView);

    protected override void Execute(TickContext ctx) => _bridge.PlayerCombatTick(ctx);
}

/// <summary>
/// Every effect this tick produced, applied to the entity it names: the demo's only event consumer, and its only writer of a <c>Versioned</c> component.
/// </summary>
/// <remarks>
/// <para><b>Serial, because <c>EventQueue.Drain</c> is single-consumer</b> — and that is the bargain rather than the
/// price. This is the one place in the tick that opens another archetype's entity and writes it, so a single thread is
/// what makes the #907 shape safe by construction. The volume is bounded by weapon cooldowns, so at the faithful baseline
/// it is single digits of events a tick.</para>
/// <para><b>In <see cref="SimPhases.Apply"/> rather than in <see cref="SimPhases.Resolve"/> with the producers</b>, for
/// the reason set out on that phase: it writes the very components the producers read, and the deriver's in-phase
/// resolutions would order the producers after it.</para>
/// <para><b>It writes through the tick's transaction, not a side transaction.</b> That is the whole of why this tick
/// carries a WAL record and waits for it: a side transaction would commit on its own and the tick would carry nothing.</para>
/// </remarks>
internal sealed class CombatResolveSystem : CallbackSystem
{
    private readonly SimBridge _bridge;

    public CombatResolveSystem(SimBridge bridge) => _bridge = bridge;

    protected override void Configure(SystemBuilder b) => b
        .Name("CombatResolve")
        .Phase(SimPhases.Apply)

        // The creature's half of an exchange: health down, a grudge against the shooter, the respawn countdown started.
        .Writes<CreatureVitals>()
        .Writes<CreatureBrain>()
        .Writes<CreatureTimers>()

        // The lair's, which is what lets a destroy mission finish at all — and its guards, one fewer at each occupant's death.
        .Writes<LairVitals>()
        .Writes<Lair>()
        .Reads<CreatureHome>()
        // A death's KillEvent carries where and in which realm it happened (character-level.md § 2).
        .Reads<CreaturePlacement>()
        .Reads<CreatureRealm>()
        .Reads<LairPlacement>()
        .Reads<LairRealm>()

        // The player's: health, the clone's teleport and the recovery it wakes up in, and the target that died with it.
        .Writes<PlayerVitals>()
        .Writes<PlayerState>()
        .Writes<PlayerMotion>()
        .Writes<PlayerSession>()
        .Writes<PlayerPlacement>()

        // A fight-back ends the lair objective the body had (lair-objectives.md).
        .Writes<PlayerRoute>()
        .Reads<PlayerRealm>()

        .Reads<PlayerControl>();

    protected override void Execute(TickContext ctx) => _bridge.CombatResolveTick(ctx);
}

/// <summary>
/// The mission desk (<c>SimBridge.Missions.cs</c>; missions.md § 3): terminals' offers, takes and cancels, held missions and the lairs drawn for them.
/// </summary>
/// <remarks>
/// <para><b>Serial</b>, because the race between two takers is settled by its order, and because it spawns and destroys — offers, held missions, lairs and
/// their creatures — through a side transaction, as the lair director does. <b>After the lair director</b>, in the same phase: both write lairs, and a
/// mission's lair broken this tick (<c>CombatResolve</c>, Apply) is then seen by the desk the same tick.</para>
/// <para><b>Its writes to a body go through the tick's transaction</b>: the journal's revision, the completed count and the wallet's revision. <b>A reward is
/// paid in its own transaction</b>, the one that ends the mission (<c>SimBridge.Economy.cs</c>, <c>Reward</c>), so it writes wallets and the ledger too —
/// before the <see cref="LedgerSystem"/>, by an edge.</para>
/// </remarks>
internal sealed class MissionDeskSystem : CallbackSystem
{
    private readonly SimBridge _bridge;

    public MissionDeskSystem(SimBridge bridge) => _bridge = bridge;

    protected override void Configure(SystemBuilder b) => b
        .Name("Missions")
        .Phase(SimPhases.Economy)
        .After("LairLife")

        .Writes<MissionOfferPlacement>()
        .Writes<MissionOfferInfo>()
        .Writes<MissionOfferRegion>()
        .Writes<MissionOfferRealm>()
        .Writes<HeldMission>()

        // A mission's lair and its occupants, spawned and destroyed like the lair director's.
        .Writes<Lair>()
        .Writes<LairPlacement>()
        .Writes<LairVitals>()
        .Writes<LairRealm>()
        .Writes<CreaturePlacement>()
        .Writes<CreatureMotion>()
        .Writes<CreatureVitals>()
        .Writes<CreatureBrain>()
        .Writes<CreatureTimers>()
        .Writes<CreatureRealm>()
        .Writes<CreatureHome>()

        // A completed mission's reward, paid with its end.
        .Writes<Wallet>()
        .Writes<LedgerEntry>()
        .Writes<LedgerHead>()

        // The body: whether it is down, where it stands; its journal and its completed count.
        .Reads<PlayerPlacement>()
        .Reads<PlayerVitals>()
        .Reads<PlayerRealm>()
        .Writes<PlayerState>()

        // Whether an offline holder's character still exists.
        .Reads<Character>();

    protected override void Execute(TickContext ctx) => _bridge.MissionDeskTick(ctx);
}

/// <summary>
/// The economy (<c>SimBridge.Economy.cs</c>; economy-foundation.md): every movement of credits, applied in order, each with its ledger row.
/// </summary>
/// <remarks>
/// <para><b>Serial, and one of the two writers of <see cref="Wallet"/>, <see cref="LedgerEntry"/> and <see cref="LedgerHead"/></b>, the mission desk being the
/// other: conservation comes from order — one code path, two serial systems ordered by an edge — never from concurrent transactions (economy-build § 1).
/// <b>After the mission desk</b>, whose rewards are committed by then.</para>
/// <para>The balances, the head and the rows go through a side transaction, committed once per tick; the wallet revision of each body whose wallet moved
/// through the tick's own, after that commit.</para>
/// </remarks>
internal sealed class LedgerSystem : CallbackSystem
{
    private readonly SimBridge _bridge;

    public LedgerSystem(SimBridge bridge) => _bridge = bridge;

    protected override void Configure(SystemBuilder b) => b
        .Name("Ledger")
        .Phase(SimPhases.Economy)
        .After("Missions")
        .Writes<Wallet>()
        .Writes<LedgerEntry>()
        .Writes<LedgerHead>()

        // A tip is given between two bodies standing together, and lands as a revision on each.
        .Reads<PlayerPlacement>()
        .Reads<PlayerRealm>()
        .Writes<PlayerState>()

        // A wallet is opened only for a character committed already.
        .Reads<Character>();

    protected override void Execute(TickContext ctx) => _bridge.LedgerTick(ctx);
}

/// <summary>
/// Shuttle travel (#910's workload): boards queued players while their port's shuttle is down and transports them to the destination port.
/// </summary>
/// <remarks>
/// In the Spawn phase, ahead of everything that reads <see cref="PlayerPlacement"/> — Think's queries — and of Move, which writes it. A cell-walking query
/// still finds an arrival in its new cell only after this tick's fence.
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

/// <summary>
/// Realms G2: dungeon instances — a realm registered at run time when a party enters, emptied and unregistered when it leaves.
/// </summary>
/// <remarks>Serial, in the Spawn phase after Teleport: it registers, spawns, teleports and destroys through its own side transactions, and every one of
/// those touches Player and CityNpc, so the scheduler orders it after the systems that write them in this phase.</remarks>
internal sealed class DungeonSystem : CallbackSystem
{
    private readonly SimBridge _bridge;
    private readonly bool _afterShuttle;

    public DungeonSystem(SimBridge bridge, bool afterShuttle)
    {
        _bridge = bridge;
        _afterShuttle = afterShuttle;
    }

    protected override void Configure(SystemBuilder b)
    {
        b.Name("Dungeon")
            .Phase(SimPhases.Spawn)
            .Writes<PlayerPlacement>()
            .Writes<PlayerRealm>()
            .Writes<PlayerState>()
            .Writes<PlayerMotion>()
            .Writes<NpcPlacement>();
        // Both write the player's placement (and Shuttle its state) in this phase. AfterAll, not two After calls: After holds one dependency.
        if (_afterShuttle)
        {
            b.AfterAll("Teleport", "Shuttle");
        }
        else
        {
            b.After("Teleport");
        }
    }

    protected override void Execute(TickContext ctx) => _bridge.DungeonTick(ctx);
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

/// <summary>
/// Everything the tick does about sessions: kicks, counts, profiles, possession and this tick's client intents (SWG-01).
/// </summary>
/// <remarks>
/// <para>
/// <b>Serial, and in this DAG rather than a replication DAG of its own, for two reasons that are both correctness.</b> Session requests append to an
/// unsynchronized per-worker segment that no caller can address by its own worker index (#1070), so segment 0 is safe only while one thread writes it — and
/// applying an intent writes <c>PlayerMotion</c>, which <c>PlayerThink</c> also writes, so the two have to be orderable, which across DAGs they are not
/// (<c>overview/13-runtime.md</c>: "a cross-DAG <c>.After()</c> edge is a configuration error"). <see cref="TatooineReplication.SessionTick"/>'s remarks
/// carry the full account.
/// </para>
/// <para>
/// <b>The <c>Input</c> phase, which exists for it</b> — see <c>SimPhases.Input</c>. <c>Shuttle</c> writes the same two player components in <c>Spawn</c>, and
/// the access deriver refuses two writers of one component in one phase without an explicit edge; a phase of its own says what an edge would only enforce.
/// </para>
/// </remarks>
internal sealed class PlayerSessionSystem : CallbackSystem
{
    protected override void Configure(SystemBuilder b) => b
        .Name("PlayerSessions")
        .Phase(SimPhases.Input)
        .Writes<PlayerControl>()
        .Writes<PlayerMotion>()
        .Writes<PlayerState>()

        // Since A5 this system SPAWNS and DESTROYS avatars, so it writes every component of the archetype and not only the three an
        // intent touches. `PlayerPlacement` was declared as a read, which was provably wrong the moment `SpawnAvatar` set
        // `Player.Bounds` — and `DV-01` throws on the tick for an undeclared write, so the whole set has to be here rather than
        // whichever members a reader notices.
        .Writes<PlayerPlacement>()
        .Writes<PlayerVitals>()
        .Writes<PlayerSession>()
        .Writes<PlayerRealm>()
        .Writes<PlayerLook>()

        // A6 saves the avatar's position onto its character, and a first login creates the character, both on this tick — dressed in the outfit its
        // account chose at sign-up.
        .Writes<Character>()
        .Reads<Account>();

    protected override void Execute(TickContext ctx) => TatooineReplication.SessionTick(ctx);
}

/// <summary>Applies the writes a thread outside the tick staged (<see cref="StagedWrites"/>): a check placing a player while the server runs.</summary>
/// <remarks>
/// <b>Every player component, for the reason <see cref="PlayerSessionSystem"/> gives</b>: <c>DV-01</c> throws on the tick for an undeclared write, and a
/// staged write may set any of them. After <c>PlayerSessions</c>, because the two share a phase and the deriver refuses two writers of one component in
/// one phase without an edge — and so that a staged placement is the last word on the tick it lands in, after that tick's intents. Not gated on the
/// pause: it is how a check stages the world, and a paused world is one it may want to stage.
/// </remarks>
internal sealed class StagedWriteSystem : CallbackSystem
{
    private readonly StagedWrites _staged;

    public StagedWriteSystem(StagedWrites staged) => _staged = staged;

    protected override void Configure(SystemBuilder b) => b
        .Name("StagedWrites")
        .Phase(SimPhases.Input)
        .After("PlayerSessions")
        .Writes<PlayerControl>()
        .Writes<PlayerMotion>()
        .Writes<PlayerState>()
        .Writes<PlayerPlacement>()
        .Writes<PlayerVitals>()
        .Writes<PlayerSession>()
        .Writes<PlayerRealm>()
        .Writes<PlayerProximity>();

    protected override void Execute(TickContext ctx) => _staged.Drain(ctx);
}

/// <summary>The periodic replication report: cumulative counters, every three hundred ticks.</summary>
/// <remarks>
/// In the report phase with every other diagnostic, and separate from <see cref="PlayerSessionSystem"/> because it writes nothing and must not be ordered
/// against anything. It was the same method until SWG-01, which is how a print ended up in the phase that decides what players do.
/// </remarks>
internal sealed class ReplicationReportSystem : CallbackSystem
{
    protected override void Configure(SystemBuilder b) => b
        .Name("ReplicationReport")
        .Phase(SimPhases.Report)

        // A declaration, because a phase alone does not order a system: AccessDagDeriver skips any pair where either side declares no access, so with none this
        // was a DAG ROOT — free to run at the head of the tick, concurrently with PlayerSessions and PlayerThink, doing blocking Console I/O on a pool worker.
        // Reading what the input system writes is what actually puts it last.
        .Reads<PlayerState>();

    protected override void Execute(TickContext ctx) => TatooineReplication.ReportTick(ctx);
}

/// <summary>
/// Counts what is standing in each realm, once every <see cref="SimConfig.RealmCensusHz"/> of a second, for the realm inventory the client polls.
/// </summary>
/// <remarks>
/// <para>
/// <b>In the DAG only when this process is serving</b> — added beside <see cref="PlayerSessionSystem"/>, under the same condition. A measurement run must
/// execute the same code it always did, and a serial walk of every cluster of five archetypes once a second is small enough to disappear into the noise of
/// one arm and not the other.
/// </para>
/// <para>
/// <b>It reads placements, and says so.</b> It opens no component and touches no field — the count is <c>PopCount</c> of a cluster's occupancy — but it walks
/// the same clusters the movers write, and a system that declares no access at all is a DAG root free to run at the head of the tick (see
/// <see cref="ReplicationReportSystem"/>, which learned that the hard way). Declaring the placements it walks is what puts it after them.
/// </para>
/// </remarks>
internal sealed class RealmCensusSystem : CallbackSystem
{
    private readonly RealmCensus _census;
    private readonly int _everyTicks;

    /// <summary>The census to fill, walked once every <paramref name="everyTicks"/> ticks.</summary>
    internal RealmCensusSystem(RealmCensus census, int everyTicks)
    {
        _census = census;
        _everyTicks = Math.Max(1, everyTicks);
    }

    protected override void Configure(SystemBuilder b) => b
        .Name("RealmCensus")
        .Phase(SimPhases.Report)
        .Reads<PlayerPlacement>()
        .Reads<NpcPlacement>()
        .Reads<CreaturePlacement>()

        // The scenery is walked too, so it is declared too. It never moves in this world — SetSpatialBarrierOnly says
        // so — which makes the omission latent rather than live, and latent by luck is exactly what rule ED-05 and the
        // note on ReplicationReportSystem above are about.
        .Reads<StructurePlacement>()
        .Reads<LairPlacement>();

    protected override void Execute(TickContext ctx)
    {
        // The cadence, and the only thing this system does on the other ticks. A viewer polls at 1 Hz; walking every
        // cluster at 50 Hz to answer it would be fifty times the work for the same document.
        //
        // The null check is not defensive noise: this runs unconditionally on the tick path, where a throw takes the
        // tick thread down, so the one argument it passes on is tested rather than asserted.
        if (ctx.TickNumber % _everyTicks != 0 || ctx.Transaction == null)
        {
            return;
        }

        _census.Take(ctx.Transaction, ctx.TickNumber);
    }
}
