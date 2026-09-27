using System;
using System.Collections.Generic;
using System.Numerics;

namespace SwgTatooine;

/// <summary>A god camera asks to look at another planet (Realms G3): the planet's realm id.</summary>
public struct ViewRealm
{
    /// <summary>The planet, 0 to <c>--planets</c> − 1.</summary>
    public uint Realm;
}

/// <summary>
/// "Walk to this point." The one movement intent a client needs and the only one it gets (SWG-01).
/// </summary>
/// <remarks>
/// <para>
/// <b>A destination, never a position.</b> The server integrates it through the same <c>Steer</c> every simulated player uses, so the client's message
/// cannot move anything: it can only change where a player is heading, at whatever speed the server says that player is entitled to. Speed hacks are
/// impossible by construction rather than by check — there is no code path from a client message to a placement
/// (<c>design/SwgTatooine/06-gameplay.md § 2</c>).
/// </para>
/// <para>
/// Coalesced <see cref="CommandCoalesce.LatestPerSession"/>: for a continuous intent an older destination is already wrong, so a client that sends ten in a
/// tick costs the tick one.
/// </para>
/// </remarks>
public struct MoveTo
{
    /// <summary>Where to walk, in world metres. Clamped to the world when applied.</summary>
    public float X;

    /// <summary>Where to walk, in world metres.</summary>
    public float Z;
}

/// <summary>
/// "Walk this way, at this speed class." The held-key form of <see cref="MoveTo"/>, and — at speed class 0 — the stop.
/// </summary>
/// <remarks>
/// <b>Stop is a speed class rather than a fourth command type, which is a stated deviation from
/// <c>design/SwgTatooine/04-protocol.md § 4</c>.</b> That table lists <c>MoveDir</c> and <c>Stop</c> as separate rows, with <c>Stop</c> carrying no fields.
/// A field-less command is a wire struct of one padding byte whose only content is its type, and with <c>LatestPerSession</c> coalescing a client that sent
/// <c>MoveDir</c> and <c>Stop</c> in one tick would have both applied in an order the protocol never stated. Speed class 0 says the same thing
/// unambiguously and one type more cheaply. Nothing is lost: a client has a stop, and a bot has the same one.
/// </remarks>
public struct MoveDir
{
    /// <summary>Heading in radians, 0 along +X, measured toward +Z.</summary>
    public float Heading;

    /// <summary>See <see cref="SpeedClasses"/>. Anything higher is refused and counted.</summary>
    public byte SpeedClass;
}

/// <summary>The speed classes a client may ask for, and what each is worth.</summary>
/// <remarks>
/// <b>A class rather than a speed, because a speed from a client is the speed hack.</b> The server maps the class onto a figure the player is entitled to,
/// and the mapping is the whole of the entitlement check. There is no mount state in this demo yet, so there is no class above <see cref="Run"/>: a client
/// asking for one is refused rather than given 12 m/s, and the refusal is counted. When mounts exist, the class stays and the mapping learns about them.
/// </remarks>
public static class SpeedClasses
{
    /// <summary>Stand still.</summary>
    public const byte Stop = 0;

    /// <summary>Half of <see cref="Run"/> — SWG had no separate walk figure worth quoting, so this is a fraction rather than a source.</summary>
    public const byte Walk = 1;

    /// <summary>[CORE3] <c>TatooineData.PlayerRunSpeedMps</c>, the fastest anything on foot moves.</summary>
    public const byte Run = 2;

    /// <summary>The first class this server does not grant.</summary>
    public const byte Count = 3;
}

/// <summary>
/// "This is what I am aiming at." The target a later tick's combat reads (SWG-02).
/// </summary>
/// <remarks>
/// <b>The reference is a <c>netId</c>, and it is resolved through <c>SubscriptionsCommands.TryResolve</c> — which refuses an entity the session was never
/// shown</b> (SUB-26). That is the check that makes a target a target rather than a world-wide entity picker: a client cannot aim at something it cannot
/// see, and it cannot discover an entity's identity by guessing one. A refusal is not an error — an entity may have left since the client sent this — so it
/// clears the target and is counted.
/// </remarks>
public struct SetTarget
{
    /// <summary>The target's network identity, or 0 to stop targeting.</summary>
    public uint NetId;
}

/// <summary>
/// News of a realm, heard by every session in it and in the realms under it (Realms G3: <c>RouteToRealm</c> over the parent tree) — a planet's news
/// reaches the players in its buildings and dungeons.
/// </summary>
public struct RealmNews
{
    /// <summary>A dungeon opened: <see cref="Subject"/> is its realm, <see cref="Count"/> its party.</summary>
    public const ushort DungeonOpened = 1;

    /// <summary>A dungeon closed: its party is sent home.</summary>
    public const ushort DungeonClosed = 2;

    /// <summary>The realm the news is about, and whose subtree hears it.</summary>
    public ushort Realm;

    /// <summary>What happened.</summary>
    public ushort What;

    /// <summary>The realm it happened in.</summary>
    public ushort Subject;

    /// <summary>How many took part.</summary>
    public ushort Count;
}

/// <summary>
/// What a connected client sees of Tatooine, declared through the public replication API and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>This file is the blueprint claim.</b> It contains no framing, no codec arithmetic, no varint, no socket: an application says which archetypes are
/// replicated, which of their fields travel, how position is quantized and who may look at what — and the engine does the rest. If anything below starts to
/// look like protocol code, the API has failed rather than the demo.
/// </para>
/// <para>
/// <b>Five archetypes, three shapes.</b> Creatures, city NPCs and players move, so they carry motion and change groups; lairs and world objects never move,
/// so they are sent once on enter and never updated — which costs a client nothing per tick and is the case that most easily goes unnoticed if the projection
/// compiler treats "no change group" as an error rather than as a shape.
/// </para>
/// </remarks>
public static class TatooineReplication
{
    /// <summary>The god camera's profile: the whole planet, every archetype, through a <c>World</c> observer.</summary>
    public const string GodProfile = "god-world";

    /// <summary>
    /// The god camera's profile under <c>--god-region</c>: the client's own hull (<c>ClientRegion</c>), a near budget, and an aggregate of the rest — the
    /// shape AC-3 measures (design/Subscriptions/09 § 17). Declared instead of <see cref="GodProfile"/>, never beside it.
    /// </summary>
    public const string GodRegionProfile = "god-region";

    /// <summary>The session kind a client names in <c>HELLO</c>.</summary>
    public const string GodKind = "god";

    /// <summary>
    /// A player's profile: a disc around the session's player, over the archetypes that move — players, city NPCs and creatures, without the scenery.
    /// </summary>
    public const string PlayerProfile = "player-lite";

    /// <summary>The session kind a small-view client names in <c>HELLO</c>.</summary>
    public const string PlayerKind = "player";

    /// <summary>How far a player sees, in metres.</summary>
    /// <remarks>
    /// Chosen as a plausible awareness range for a ground game at this world scale, not measured from anything: what it is here for is that a player's view
    /// is a DISC rather than the world, and the exact figure only moves the constant. Tatooine's cells are 256 m, so a disc of this size spans a handful of
    /// them and the cluster index has something to reject.
    /// </remarks>
    private const double PlayerRadiusM = 192d;

    /// <summary>The realm kind of a building's interior (Realms G3): a one-cell realm, served whole.</summary>
    public const string InteriorKind = "interior";

    /// <summary>The realm kind of space (Realms G3): a deep realm at its own cell.</summary>
    public const string SpaceKind = "space";

    /// <summary>The replication grid's cell side (<see cref="SubscriptionsOptions.ReplicationCellM"/>): a third of <see cref="PlayerRadiusM"/>.</summary>
    public const double ReplicationCellM = PlayerRadiusM / 3d;

    /// <summary>The fastest anything on Tatooine moves, in metres per second — a mounted player.</summary>
    /// <remarks>
    /// It sizes the motion codec: the teleport threshold is what separates "it moved" from "it was put somewhere else", and the velocity width is derived from
    /// it together with the tick period. Declaring it too high wastes a bit per segment; too low turns a sprint into a teleport.
    /// </remarks>
    internal const double MaxSpeedMps = 12.0;

    private static SubscriptionsCommands _pushCommands;

    /// <summary>
    /// The simulation's "this entity changed something a client sees" (ADR-067): a system that writes a replicated value calls it for the slot it wrote.
    /// </summary>
    /// <param name="cluster">The cluster being iterated.</param>
    /// <param name="slot">The entity's slot.</param>
    public static void Replicate<T>(in ClusterRef<T> cluster, int slot) where T : class => _pushCommands?.Replicate(in cluster, slot);

    /// <summary><see cref="Replicate{T}(in ClusterRef{T}, int)"/> for a set of slots.</summary>
    /// <param name="cluster">The cluster being iterated.</param>
    /// <param name="slots">The slots.</param>
    public static void Replicate<T>(in ClusterRef<T> cluster, ulong slots) where T : class => _pushCommands?.Replicate(in cluster, slots);

    /// <summary>Each player session's outbound byte budget, bytes per second; 0 for none (<c>--session-budget</c>).</summary>
    public static int PlayerBudgetBytesPerSecond { get; set; }

    /// <summary>The players' leave radius, metres (<c>--player-leave</c>); 0 for none, the default. AC-2 and AC-3 run at 192/208 m.</summary>
    public static double PlayerLeaveM { get; set; }

    /// <summary>The god region's largest edge, metres (<c>--god-region</c>); 0 keeps the <c>World</c> god camera, the default.</summary>
    public static double GodRegionMaxEdgeM { get; set; }

    /// <summary>The god region's near budget, entities (<c>--god-near</c>); 10 000 by default, AC-3's.</summary>
    public static int GodNearBudget { get; set; } = 10_000;

    /// <summary>How many planets a god camera may look at with <see cref="ViewRealm"/> (<c>--planets</c>); planet p is realm p.</summary>
    public static int Planets { get; set; } = 1;

    // ── Admission and shutdown (SWG-07) ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Player sessions admitted at once (<c>--max-clients</c>); 0 is unlimited.</summary>
    public static int MaxClients { get; set; }

    /// <summary>God-camera sessions admitted at once (<c>--max-spectators</c>); 0 is unlimited.</summary>
    public static int MaxSpectators { get; set; }

    /// <summary>The close code a session refused for a full house carries; the application range starts at 4100.</summary>
    public const ushort HouseFullCloseCode = 4101;

    /// <summary>The close code a session kicked by <see cref="RequestShutdown"/> carries.</summary>
    public const ushort ShutdownCloseCode = 4100;

    // Live sessions of each role, as the last tick counted them from the session table, and admissions granted since that count. The hook runs on the
    // transport thread and the count runs in the tick, so neither alone is the truth: the hook admits against live + pending, and the tick zeroes pending
    // BEFORE it walks. That ordering is what makes the cap safe — a session admitted during the walk is either seen by the walk or still in pending, so it
    // is counted at least once and possibly twice. Double-counting refuses one client a tick early; under-counting would let the cap be exceeded, and only
    // one of those is a correctness failure.
    private static int _liveClients;
    private static int _liveSpectators;
    private static int _pendingClients;
    private static int _pendingSpectators;
    private static long _refusedFull;

    /// <summary>Sessions refused because their role's cap was reached, for the shutdown report.</summary>
    public static long RefusedFull => System.Threading.Interlocked.Read(ref _refusedFull);

    /// <summary>Live sessions of each role as the last tick counted them.</summary>
    public static (int Clients, int Spectators) LiveSessions
        => (System.Threading.Volatile.Read(ref _liveClients), System.Threading.Volatile.Read(ref _liveSpectators));

    // Set from any thread when the process is going away; read by the tick, which kicks every open session once. A string rather than a bool so that the
    // reason reaching the client is the operator's, and so that "not shutting down" is unambiguously null.
    private static volatile string _shutdownReason;
    private static long _kicksStaged;

    // Sessions already kicked. Tick-thread only, and never cleared: the process is on its way out, and a set bounded by the connections made during a
    // shutdown window is not a leak worth a lifetime for.
    private static readonly HashSet<SessionId> Kicked = [];

    /// <summary>
    /// Asks the next tick to send every open session a <c>KICK</c> carrying <paramref name="reason"/>, then close it. Callable from any thread.
    /// </summary>
    /// <param name="reason">Why, as the client will see it.</param>
    /// <remarks>
    /// <para>
    /// <b>Why a client is told rather than dropped (SWG-07).</b> The host used to stop Kestrel and let every socket die, which a client cannot distinguish
    /// from its own network failing: an SDK's reconnect policy then backs off and retries a server that is deliberately gone. A <c>KICK</c> with an
    /// application close code says "this was on purpose", and the code is in the application range because a protocol code would tell the SDK something
    /// different about whether to come back.
    /// </para>
    /// <para>
    /// The kick is staged, not sent: <c>SessionRequest</c> is applied by the next tick's prologue, and the send pump writes the frame and then closes the
    /// link. So the caller must let the runtime tick — see <see cref="KicksStaged"/>, which is what the host waits on.
    /// </para>
    /// </remarks>
    public static void RequestShutdown(string reason) => _shutdownReason = string.IsNullOrEmpty(reason) ? "server shutting down" : reason;

    /// <summary>How many sessions have been staged a shutdown <c>KICK</c>.</summary>
    public static long KicksStaged => System.Threading.Interlocked.Read(ref _kicksStaged);

    /// <summary>Whether a shutdown has been asked for.</summary>
    public static bool ShuttingDown => _shutdownReason != null;

    /// <summary>
    /// The application's admission hook: a role by session kind, and a refusal when that role's house is full.
    /// </summary>
    /// <param name="request">What the client presented.</param>
    /// <returns>An acceptance carrying the role, or a refusal.</returns>
    /// <remarks>
    /// <para>
    /// <b>Roles are not cosmetic here.</b> A <c>god</c> session is a <see cref="SessionRole.Spectator"/> and gets the tooling limits, because the god camera
    /// is allowed to see how the server is arranged; a <c>player</c> session is a <see cref="SessionRole.Player"/> on the operator's defaults. Counting them
    /// against separate caps is deliberate: a full house of spectators must not lock players out of their own world, which one shared cap would allow.
    /// </para>
    /// <para>
    /// A kind the registry never declared does not reach here at all — the engine refuses it with
    /// <see cref="Typhon.Protocol.CloseCodes.AuthenticationRejected"/> before the hook — so this only ever sees <c>god</c> or <c>player</c>.
    /// </para>
    /// </remarks>
    public static Admission Admit(in AdmissionRequest request)
    {
        if (_shutdownReason != null)
        {
            return Admission.Reject(ShutdownCloseCode, _shutdownReason);
        }

        var player = string.Equals(request.Kind, PlayerKind, StringComparison.Ordinal);
        if (player)
        {
            // The byte budget stays with BindOpenedSessions' SetBudget rather than moving into these limits. Both would work, and setting it in two places
            // would mean two numbers to keep equal — and that one is what the Phase 2 criteria measure with.
            return TryReserve(ref _pendingClients, System.Threading.Volatile.Read(ref _liveClients), MaxClients)
                ? Admission.Accept(SessionRole.Player)
                : Full("player", MaxClients);
        }

        return TryReserve(ref _pendingSpectators, System.Threading.Volatile.Read(ref _liveSpectators), MaxSpectators)
            ? Admission.Accept(SessionRole.Spectator, SessionLimits.God)
            : Full("spectator", MaxSpectators);
    }

    /// <summary>
    /// Clears the session accounting and the shutdown request, so one check's caps and kicks do not decide the next one's.
    /// </summary>
    /// <remarks>
    /// This class is static because the declarations it makes are process-wide — the schedule holds delegates to its methods — so its counters are too, and
    /// a fixture that ran second would otherwise inherit the first one's full house. Called from a <c>SetUp</c>; never from the demo itself.
    /// </remarks>
    public static void ResetSessionAccounting()
    {
        _shutdownReason = null;
        System.Threading.Volatile.Write(ref _liveClients, 0);
        System.Threading.Volatile.Write(ref _liveSpectators, 0);
        System.Threading.Volatile.Write(ref _pendingClients, 0);
        System.Threading.Volatile.Write(ref _pendingSpectators, 0);
        System.Threading.Interlocked.Exchange(ref _refusedFull, 0);
        System.Threading.Interlocked.Exchange(ref _kicksStaged, 0);
        Kicked.Clear();
        MaxClients = 0;
        MaxSpectators = 0;
    }

    /// <summary>A refusal for a full house, counted.</summary>
    /// <param name="role">The role whose cap was reached, for the message.</param>
    /// <param name="cap">The cap.</param>
    /// <returns>The refusal.</returns>
    private static Admission Full(string role, int cap)
    {
        System.Threading.Interlocked.Increment(ref _refusedFull);
        return Admission.Reject(HouseFullCloseCode, $"the {role} house is full ({cap})");
    }

    /// <summary>Claims a place under a cap, atomically against every other transport thread.</summary>
    /// <param name="pending">The role's pending counter.</param>
    /// <param name="live">The role's live count, as the last tick saw it.</param>
    /// <param name="cap">The cap; 0 or less is unlimited.</param>
    /// <returns>Whether a place was claimed.</returns>
    private static bool TryReserve(ref int pending, int live, int cap)
    {
        if (cap <= 0)
        {
            System.Threading.Interlocked.Increment(ref pending);
            return true;
        }

        if (live + System.Threading.Interlocked.Increment(ref pending) <= cap)
        {
            return true;
        }

        System.Threading.Interlocked.Decrement(ref pending);
        return false;
    }

    // Whether the declarations were made: a measurement run has no replication, and an announcement there has nobody to reach.
    private static bool _declared;

    // Announcements emitted, for the periodic report.
    private static long _announced;

    /// <summary>Announces <paramref name="news"/> to its realm's subtree, when this process serves clients. From a serial system.</summary>
    /// <param name="tick">The tick context of the system this is called from.</param>
    /// <param name="news">The news.</param>
    public static void Announce(TickContext tick, in RealmNews news)
    {
        if (_declared && tick.Subscriptions != null)
        {
            tick.Subscriptions.Emit(in news);
            System.Threading.Interlocked.Increment(ref _announced);
        }
    }

    /// <summary>The god region's aggregate tile, metres; its counts refresh once a second.</summary>
    private const double GodAggregateTileM = 256d;

    /// <summary>Declares everything a client can see.</summary>
    /// <param name="subs">The runtime's registry, before <c>Start</c>.</param>
    /// <param name="automatic">Whether the engine detects changes itself instead of relying on the simulation's <c>Replicate</c> calls (experimental).</param>
    public static void Declare(SubscriptionsRegistry subs, bool automatic = false)
    {
        ArgumentNullException.ThrowIfNull(subs);

        subs.Sessions.Kinds(GodKind, PlayerKind);

        // The admission hook. Without one the engine accepts every connection as a Spectator, so the demo had no role distinction, no cap, and no refusal
        // path — and neither the engine's admission surface nor an SDK's handling of a refusal was exercised by anything (SWG-07).
        subs.Sessions.Admit = Admit;

        // What each archetype replicates is declared on the data — [Replicated] on the archetype, [Motion] / [Position] on its placement, [Replicate],
        // [OnEnter], [Fraction] and [Owner] on its components' fields (Ecs/Archetypes.cs, Ecs/Components.cs; design/Subscriptions/11 § 5). Everyone sees a
        // player's health as an 8-bit bar; the player alone sees the exact number and its mission waypoint, in SELF (11 § 2) — SWG's own HAM display and
        // quest marker. A builder call, subs.Archetype<T>(a => …), would replace an archetype's attributes for a deployment that wants otherwise.
        // Planets are the default kind; interiors and space are served differently (12-realms § 1.4) — one profile name for every scale.
        subs.RealmKinds(InteriorKind, SpaceKind);

        subs.Archetype<Creature>();
        subs.Archetype<CityNpc>();
        subs.Archetype<Player>();
        subs.Archetype<CreatureLair>();
        subs.Archetype<WorldObject>();

        // The god camera through a World observer, the players through a disc with no band: the anchor's slack is its hysteresis for observer motion
        // (push-model.md § 4.5).
        var detection = automatic ? PushDetection.Automatic : PushDetection.Explicit;
        if (GodRegionMaxEdgeM > 0)
        {
            subs.Profile(GodRegionProfile, p =>
            {
                p.Detection(detection)
                    .ClientRegion(GodRegionMaxEdgeM)
                    .Near(GodNearBudget)
                    .Of<Creature>()
                    .Of<CityNpc>()
                    .Of<Player>()
                    .Of<CreatureLair>()
                    .Of<WorldObject>();
                p.Aggregate(GodAggregateTileM, rateHz: 1).Of<Creature>().Of<CityNpc>().Of<Player>();

                // A camera over a planet has nothing to show inside a building: the god camera is served nothing in an interior.
                p.NotIn(InteriorKind);
            });
        }
        else
        {
            subs.Profile(GodProfile, p =>
            {
                p.Detection(detection)
                    .World()
                    .Of<Creature>()
                    .Of<CityNpc>()
                    .Of<Player>()
                    .Of<CreatureLair>()
                    .Of<WorldObject>();
                p.NotIn(InteriorKind);
            });
        }

        // Centred on the player the session controls, at its post-fence position (09 § 6): no per-tick Place. The session follows its player through
        // doors and shuttles (12-realms § 1.3): inside a building everything in it, one cell; in space a World of the players there.
        subs.Profile(PlayerProfile, p =>
        {
            p.Detection(detection)
                .Sphere(PlayerRadiusM, leave: PlayerLeaveM)
                .AroundControlled()
                .Of<Player>()
                .Of<CityNpc>()
                .Of<Creature>();
            p.In(InteriorKind, v => v.World().AroundControlled().Of<Player>().Of<CityNpc>());
            p.In(SpaceKind, v => v.World().AroundControlled().Of<Player>());
        });

        // Realms G3: a planet's news reaches its subtree, and a god camera moves between planets with a command.
        subs.Event<RealmNews>(e => e.RouteToRealm(n => new RealmId(n.Realm), subtree: true));

        // Every accepted ask is a RESET of a whole planet, the dearest frame there is: once a second, a burst of two. Spectators only — a possessed player
        // moves between planets by taking a shuttle like everyone else, and now that admission assigns roles the engine can say so instead of this being a
        // string comparison in BindOpenedSessions (which stays, as the check that the god camera is not a player's).
        subs.Command<ViewRealm>(c => c.Rate(1, 2).Roles(SessionRole.Spectator).Field(v => v.Realm, Codec.VarUInt));

        // The movement and targeting intents (SWG-01). Players and bots only: a spectator has no entity to move, so the engine refuses the message rather
        // than the system dropping it after the wire has already been paid for.
        //
        // The rates are 04-protocol § 4's. MoveTo and MoveDir are LatestPerSession because an older destination is already wrong, and generous per second
        // because a client sending one per frame is normal — the coalescing is what makes that cheap, not the rate. SetTarget is queued at 4/s: each one is a
        // decision rather than a continuous state, and a client that spams them is picking targets faster than a person can.
        //
        // Every field is given an explicit codec. A field with none still travels, at its natural width, so leaving one out is a silent 4 bytes rather than
        // an error — and a heading in 16 bits is a quarter of a degree, which is finer than a mouse can aim.
        subs.Command<MoveTo>(c => c
            .Coalesce(CommandCoalesce.LatestPerSession)
            .Rate(30, 60)
            .Roles(SessionRole.Player)
            .Precheck(static (in MoveTo m) => float.IsFinite(m.X) && float.IsFinite(m.Z))
            .Field(m => m.X, Codec.F32, "x")
            .Field(m => m.Z, Codec.F32, "z"));
        subs.Command<MoveDir>(c => c
            .Coalesce(CommandCoalesce.LatestPerSession)
            .Rate(30, 60)
            .Roles(SessionRole.Player)
            .Precheck(static (in MoveDir m) => float.IsFinite(m.Heading) && m.SpeedClass < SpeedClasses.Count)
            .Field(m => m.Heading, Codec.F16, "heading")
            .Field(m => m.SpeedClass, Codec.U8, "speedClass"));
        subs.Command<SetTarget>(c => c
            .Rate(4, 8)
            .Roles(SessionRole.Player)
            .Field(t => t.NetId, Codec.VarUInt, "netId"));

        _declared = true;
    }

    /// <summary>
    /// Everything the tick does about sessions, in one serial system: kicks, counts, profiles, possession and the clients' intents.
    /// </summary>
    /// <param name="tick">The tick context.</param>
    /// <remarks>
    /// <para>
    /// <b>One system, and that is a correctness requirement rather than tidiness (SWG-01).</b> Two things forced it.
    /// </para>
    /// <para>
    /// First, session requests. <c>SubscriptionsCommands.Session(session, worker)</c> appends to a per-worker segment whose append is deliberately
    /// unsynchronized — its own remarks say two threads sharing one "loses records, duplicates them, or throws out of an <c>Array.Resize</c>" — and the
    /// worker index cannot be passed <c>TickContext.WorkerId</c>, because that ranges over <c>[0, WorkerCount]</c> while the log is sized
    /// <c>WorkerCount</c> (filed as #1070). So every caller uses segment 0, and segment 0 is safe only if one thread writes it. This used to be two callback
    /// systems declared with no edge between them in one DAG phase — <c>ExecuteInline</c> runs a callback system on whichever worker reaches it, so they
    /// could and did run concurrently, one staging <c>Profile</c> and <c>Enter</c> while the other staged <c>Control</c>.
    /// </para>
    /// <para>
    /// Second, ECS writes. Applying an intent writes <c>PlayerMotion</c> and <c>PlayerControl</c>, and <c>PlayerThink</c> writes <c>PlayerMotion</c> too.
    /// Ordering them needs them in the SAME DAG — "a cross-DAG <c>.After()</c> edge is a configuration error … access-edge derivation runs per-DAG"
    /// (<c>overview/13-runtime.md</c>) — and DAGs within a track have no barrier between them. So this lives in the simulation's own DAG, in the
    /// <c>Spawn</c> phase, ahead of every phase that reads what it wrote.
    /// </para>
    /// </remarks>
    public static void SessionTick(TickContext tick)
    {
        if (BindOpenedSessions(tick))
        {
            PossessPlayers(tick);
            ApplyIntents(tick);
        }
    }

    /// <summary>
    /// Binds every session that opens to its profile, and answers a god camera's request to look at another planet.
    /// </summary>
    /// <param name="tick">The tick context of the system this is called from.</param>
    /// <returns><see langword="false"/> when there is nothing further to do this tick — no replication, or the server is shutting down.</returns>
    /// <remarks>
    /// A session with no profile receives nothing, so this is not optional wiring — it is the moment a connection becomes a viewer. The request is staged
    /// and applied by the next tick's prologue.
    /// </remarks>
    private static bool BindOpenedSessions(TickContext tick)
    {
        var subs = tick.Subscriptions;
        if (subs == null)
        {
            return false;
        }

        _pushCommands = subs;

        // Once a shutdown has been asked for, every open session is told and nothing else about this tick matters: binding a session that is about to be
        // kicked would give it one frame of world it cannot use.
        var shutdown = _shutdownReason;
        if (shutdown != null)
        {
            foreach (var session in subs.OpenSessions)
            {
                if (Kicked.Add(session))
                {
                    subs.Session(session).Kick(ShutdownCloseCode, shutdown);
                    System.Threading.Interlocked.Increment(ref _kicksStaged);
                }
            }

            return false;
        }

        RecountSessions(subs);

        foreach (ref readonly var e in subs.SessionEvents)
        {
            if (e.Kind == SessionEventKind.Opened)
            {
                // By kind, so one run can carry both shapes and a measurement can say which it measured.
                var player = e.SessionKind == PlayerKind;
                var request = subs.Session(e.Session).Profile(player ? PlayerProfile : GodRegionMaxEdgeM > 0 ? GodRegionProfile : GodProfile);
                if (player && PlayerBudgetBytesPerSecond > 0)
                {
                    request.SetBudget(PlayerBudgetBytesPerSecond);
                }

                // A player's session is in its player's realm (AroundControlled). A god camera has no entity to follow: with several realms it is in none
                // until placed, so it starts on planet 0 (12-realms § 1.3).
                if (!player)
                {
                    subs.Enter(e.Session, RealmId.Default);
                }
            }
        }

        // A god camera's move to another planet: its next frame is a RESET carrying the planet's REALM (SUB-29). A player's session follows its player,
        // and a realm that is not a planet is not the god camera's to enter.
        foreach (var command in subs.Commands<ViewRealm>())
        {
            if (command.Value.Realm < (uint)Planets && !string.Equals(subs.SessionKindOf(command.Session), PlayerKind, StringComparison.Ordinal))
            {
                subs.Enter(command.Session, new RealmId((ushort)command.Value.Realm));
            }
        }

        return true;
    }

    /// <summary>
    /// Recounts the live sessions of each role from the session table, so that the admission hook has a number it did not derive from its own increments.
    /// </summary>
    /// <param name="subs">This tick's replication surface.</param>
    /// <remarks>
    /// <para>
    /// <b>Why the truth is recomputed rather than maintained.</b> A counter incremented at admission and decremented on <c>Closed</c> is exact only if every
    /// acceptance produces exactly one of each, and it does not: <c>SessionTable.TryAdmit</c> can still fail to lease a slot, or throw while opening, after
    /// the hook has said yes. Each such case leaks a place under the cap permanently, and a server whose cap has silently shrunk to zero stops accepting for
    /// a reason nothing reports. Walking the open sessions is O(sessions) once a tick against a table the tick is already touching.
    /// </para>
    /// <para>
    /// The pending counters are cleared BEFORE the walk, not after — see their declaration for why that direction is the safe one.
    /// </para>
    /// </remarks>
    private static void RecountSessions(SubscriptionsCommands subs)
    {
        System.Threading.Interlocked.Exchange(ref _pendingClients, 0);
        System.Threading.Interlocked.Exchange(ref _pendingSpectators, 0);

        var players = 0;
        var spectators = 0;
        foreach (var session in subs.OpenSessions)
        {
            if (string.Equals(subs.SessionKindOf(session), PlayerKind, StringComparison.Ordinal))
            {
                players++;
            }
            else
            {
                spectators++;
            }
        }

        System.Threading.Volatile.Write(ref _liveClients, players);
        System.Threading.Volatile.Write(ref _liveSpectators, spectators);
    }

    /// <summary>
    /// Places every player session's disc for this tick, spreading the sessions over the world's players.
    /// </summary>
    /// <param name="tick">The tick context of the system this is called from.</param>
    /// <param name="viewpoints">Where the world's players are, this tick.</param>
    /// <remarks>
    /// <para>
    /// <b>Sessions are spread across DIFFERENT players on purpose.</b> Placing them all at one point would give every session the same disc, and a
    /// measurement taken that way would report a per-session cost that no real population has. Spreading them is what makes the numbers mean something.
    /// </para>
    /// <para>
    /// A session with no viewpoint sees nothing at all, so this runs every tick for every open session rather than once at admission: the players move, and
    /// a disc left where a player was is a view of somewhere they have left.
    /// </para>
    /// </remarks>
    private static long _placeTicks;

    /// <summary>The runtime's scheduler, for the periodic worker-idle line.</summary>
    internal static DagScheduler Scheduler;
    private static (double InDispatchMs, double IdleMs, double ParkedMs, long Parks, long Backstops, long Spells, long Wakes) IdleFrom;
    private static long IdleTickFrom;
    private static (double ActiveMs, double TickWallMs, long Ticks, int Workers) _utilFrom;

    /// <summary>
    /// Per system over the report window, from the scheduler's telemetry ring: its span per tick, the worker time summed over its chunks, and how much of
    /// the pool that span left unused (span × pool − work). A span with idle workers can still be overlapped by a DAG sibling, so the unused figure is an
    /// upper bound on what the system wastes, not a measure of it.
    /// </summary>
    private static void ReportSystemEfficiency()
    {
        var ring = Scheduler.Telemetry;
        var systems = Scheduler.Systems;
        var pool = Math.Max(1, Scheduler.WorkerCount);
        var span = new double[systems.Length];
        var work = new double[systems.Length];
        var chunks = new long[systems.Length];
        var ran = new int[systems.Length];
        var transition = new double[systems.Length];
        var first = Math.Max(IdleTickFrom, ring.OldestAvailableTick);
        var ticks = 0;
        var wall = 0d;
        for (var t = first; t <= ring.NewestTick; t++)
        {
            ref readonly var tick = ref ring.GetTick(t);
            if (tick.ActualDurationMs <= 0f)
            {
                continue;
            }

            ticks++;
            wall += tick.ActualDurationMs;
            var metrics = ring.GetSystemMetrics(t);
            for (var i = 0; i < metrics.Length && i < systems.Length; i++)
            {
                if (metrics[i].WasSkipped)
                {
                    continue;
                }

                span[i] += metrics[i].DurationUs;
                work[i] += metrics[i].WorkUs;
                transition[i] += metrics[i].TransitionLatencyUs;
                chunks[i] += metrics[i].WorkersTouched;
                ran[i]++;
            }
        }

        if (ticks == 0)
        {
            return;
        }

        var order = new int[systems.Length];
        for (var i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        Array.Sort(order, (a, b) => span[b].CompareTo(span[a]));
        var line = new System.Text.StringBuilder();
        line.Append($"  system efficiency over {ticks} ticks ({wall / ticks:F2} ms/tick, pool {pool}): name span/work ms per tick, chunks, eff, unused worker-ms");
        for (var k = 0; k < Math.Min(14, order.Length); k++)
        {
            var i = order[k];
            if (ran[i] == 0)
            {
                continue;
            }

            var s = span[i] / 1000d / ticks;
            var w = work[i] / 1000d / ticks;
            var c = (double)chunks[i] / ran[i];
            var eff = s <= 0 ? 0 : w * 100d / (s * pool);
            line.Append($"\n    {systems[i].Name,-34} span {s,6:F3} work {w,7:F3} chunks {c,5:F1} eff {eff,5:F1} % unused {(s * pool) - w,7:F2}");
        }

        // The replication track in DAG order, whatever its cost: an empty stage still costs its hand-off.
        for (var i = 0; i < systems.Length; i++)
        {
            if (!systems[i].Name.StartsWith("Subscriptions", StringComparison.Ordinal))
            {
                continue;
            }

            var runs = Math.Max(1, ran[i]);
            line.AppendLine().Append($"    track {systems[i].Name,-28} ran {ran[i],4} span {span[i] / runs,7:F1} us transition {transition[i] / runs,6:F1} us ")
                .Append($"chunks {(double)chunks[i] / runs,5:F1}");
        }

        Console.Error.WriteLine(line.ToString());
        ReportTail(ring, systems, first);
    }

    /// <summary>
    /// Where the window's slow ticks go: for the ticks at or above the window's 95th percentile, each system's mean span beyond its own median over the
    /// window. A tail that a mean hides shows up here by name.
    /// </summary>
    private static void ReportTail(TickTelemetryRing ring, SystemDefinition[] systems, long first)
    {
        var durations = new List<float>();
        var rows = new List<float[]>();
        for (var t = first; t <= ring.NewestTick; t++)
        {
            ref readonly var tick = ref ring.GetTick(t);
            if (tick.ActualDurationMs <= 0f)
            {
                continue;
            }

            var metrics = ring.GetSystemMetrics(t);
            var row = new float[systems.Length];
            for (var i = 0; i < metrics.Length && i < systems.Length; i++)
            {
                row[i] = metrics[i].WasSkipped ? 0f : metrics[i].DurationUs;
            }

            durations.Add(tick.ActualDurationMs);
            rows.Add(row);
        }

        if (durations.Count < 20)
        {
            return;
        }

        var sorted = durations.ToArray();
        Array.Sort(sorted);
        var p95 = sorted[(int)(sorted.Length * 0.95)];
        var median = new float[systems.Length];
        var column = new float[rows.Count];
        for (var i = 0; i < systems.Length; i++)
        {
            for (var r = 0; r < rows.Count; r++)
            {
                column[r] = rows[r][i];
            }

            Array.Sort(column);
            median[i] = column[column.Length / 2];
        }

        var excess = new double[systems.Length];
        var slow = 0;
        for (var r = 0; r < rows.Count; r++)
        {
            if (durations[r] < p95)
            {
                continue;
            }

            slow++;
            for (var i = 0; i < systems.Length; i++)
            {
                excess[i] += rows[r][i] - median[i];
            }
        }

        var order = new int[systems.Length];
        for (var i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        Array.Sort(order, (x, y) => excess[y].CompareTo(excess[x]));
        var line = new System.Text.StringBuilder(
            $"  tail: {slow} ticks >= p95 {p95:F2} ms (median {sorted[sorted.Length / 2]:F2}); mean excess over each system's median, us:");
        for (var k = 0; k < Math.Min(8, order.Length); k++)
        {
            line.Append($" {systems[order[k]].Name}={excess[order[k]] / slow:F0}");
        }

        Console.Error.WriteLine(line.ToString());
    }

    private static long SendWindowFrom, SendFramesFrom, SendBytesFrom, SendAllocFrom, SendItemsFrom;
    private static double SendCpuFrom;
    private static int SendGen0From;

    /// <summary>
    /// The periodic replication report: what the track did, cumulatively, every three hundred ticks.
    /// </summary>
    /// <param name="tick">The tick context.</param>
    /// <remarks>
    /// Split out of the possession pass (SWG-01) because the two have nothing to do with each other and only one of them may write ECS state. This reads
    /// counters and prints; it stages nothing and touches no entity, so it is free to sit in the report phase where every other diagnostic is.
    /// </remarks>
    public static void ReportTick(TickContext tick)
    {
        var subs = tick.Subscriptions;
        if (subs == null)
        {
            return;
        }

        // Error, not Out: a redirected stdout is block-buffered and this process is stopped rather than asked to exit, so the buffer is never flushed and the
        // diagnostic is lost exactly when it is being collected.
        if (++_placeTicks % 300 == 0)
        {
            var (projected, dormant) = subs.ProjectionBlocks;
            var ef = subs.EnterFlow;
            Console.Error.WriteLine(
                $"  replication (cumulative): blocks {projected} projected, {dormant} dormant, sleeping {subs.SleepingClusters}; "
                + $"records {ef.Entered} entered, {ef.Left} left");
            var idf = subs.IdentityFlow;
            Console.Error.WriteLine(
                $"  identities: {idf.Minted} minted, {idf.Released} released, {idf.Reused} reused; "
                + $"{subs.EntriesMigrated} entries relocated between clusters; {System.Threading.Volatile.Read(ref _announced)} realm news announced");
            var sendPath = subs.SendPath;
            var st = subs.SendTotals;
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            var cpu = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds;
            var alloc = GC.GetTotalAllocatedBytes();
            var items = System.Threading.ThreadPool.CompletedWorkItemCount;
            var gen0 = GC.CollectionCount(0);
            if (SendWindowFrom != 0)
            {
                var wallMs = (now - SendWindowFrom) * 1000d / System.Diagnostics.Stopwatch.Frequency;
                Console.Error.WriteLine(
                    $"  send path: wake {sendPath.WakeMsPerPublish:F3} ms/publish on the driver ({sendPath.WokenPerPublish:F0} woken), pool delay {sendPath.QueueDelayUs:F0} us, "
                    + $"send {sendPath.SendUs:F1} us ({sendPath.SendsSync} sync, {sendPath.SendsAsync} async); window: {(st.Frames - SendFramesFrom) * 1000d / wallMs:F0} frames/s, "
                    + $"{(st.Bytes - SendBytesFrom) / wallMs / 1000d:F1} MB/s, process CPU {(cpu - SendCpuFrom) / wallMs:F2} cores, "
                    + $"alloc {(alloc - SendAllocFrom) / wallMs / 1000d:F2} MB/s, pool items {(items - SendItemsFrom) * 1000d / wallMs:F0}/s, "
                    + $"pool threads {System.Threading.ThreadPool.ThreadCount}, gen0 {gen0 - SendGen0From}");
            }

            if (Scheduler != null && SendWindowFrom != 0)
            {
                var wi = Scheduler.WorkerIdle;
                var ticks = Math.Max(1, Scheduler.CurrentTickNumber - IdleTickFrom);
                var inDispatch = wi.InDispatchMs - IdleFrom.InDispatchMs;
                var idle = wi.IdleMs - IdleFrom.IdleMs;
                var parked = wi.ParkedMs - IdleFrom.ParkedMs;
                Console.Error.WriteLine(
                    $"  scheduler: {inDispatch / ticks:F2} ms worker time in dispatches per tick, idle {(inDispatch <= 0 ? 0 : idle * 100d / inDispatch):F1} % "
                    + $"(parked {(inDispatch <= 0 ? 0 : parked * 100d / inDispatch):F1} %, spinning {(inDispatch <= 0 ? 0 : (idle - parked) * 100d / inDispatch):F1} %); "
                    + $"per tick: {(double)(wi.Parks - IdleFrom.Parks) / ticks:F1} parks, {(double)(wi.Wakes - IdleFrom.Wakes) / ticks:F1} wakes, "
                    + $"{(double)(wi.Backstops - IdleFrom.Backstops) / ticks:F2} backstops, {(double)(wi.Spells - IdleFrom.Spells) / ticks:F1} idle spells");
            }

            if (Scheduler != null && SendWindowFrom != 0)
            {
                var wu = Scheduler.WorkerUtilization;
                var uTicks = wu.Ticks - _utilFrom.Ticks;
                if (uTicks > 0)
                {
                    var active = wu.ActiveMs - _utilFrom.ActiveMs;
                    var wall = wu.TickWallMs - _utilFrom.TickWallMs;
                    Console.Error.WriteLine(
                        $"  worker utilization: {active / uTicks:F2} ms active per tick over {wall / uTicks:F2} ms tick wall, "
                        + $"{(wall <= 0 ? 0 : active * 100d / (wall * wu.Workers)):F1} % of {wu.Workers} workers");
                }
            }

            if (Scheduler != null && SendWindowFrom != 0)
            {
                ReportSystemEfficiency();
            }

            if (Scheduler != null)
            {
                _utilFrom = Scheduler.WorkerUtilization;
                IdleFrom = Scheduler.WorkerIdle;
                IdleTickFrom = Scheduler.CurrentTickNumber;
            }

            SendWindowFrom = now;
            SendFramesFrom = st.Frames;
            SendBytesFrom = st.Bytes;
            SendCpuFrom = cpu;
            SendAllocFrom = alloc;
            SendItemsFrom = items;
            SendGen0From = gen0;
            var ee = subs.EpochEnter;
            Console.Error.WriteLine($"  epoch enter: {ee.MeanUs:F1} us mean over {ee.Chunks} chunks");
            var fs = subs.FrameSpan;
            Console.Error.WriteLine($"  frame span: {fs.SpanMs:F2} ms wall, {fs.BusyMs:F2} ms busy, {fs.Concurrency:F1} concurrent, start spread {fs.StartSpreadMs:F2} ms");
            var pm = subs.FramePrologueMs;
            Console.Error.WriteLine($"  frame prologue: {pm.Prologue:F2} ms/tick serial (sweep {pm.Sweep:F2}, prepare {pm.Prepare:F2})");
            var pp = subs.ProjectPrologueMs;
            Console.Error.WriteLine(
                $"  project: serial {pp.Prepare + pp.Drain + pp.Mark:F2} ms/tick (prepare {pp.Prepare:F2}, drain {pp.Drain:F2}, mark {pp.Mark:F2}), "
                + $"parallel busy {pp.Busy:F2} ms/tick");
            var fb = subs.FrameBalance;
            Console.Error.WriteLine($"  frame balance: {fb.Effective:F1} effective workers, {fb.Efficiency * 100d:F0} % efficiency over {fb.Ticks} ticks");
            var ph = subs.FramePhases;
            if (ph.Gather + ph.Encode > 0d)
            {
                Console.Error.WriteLine(
                    $"  frame phases (ms CPU, cumulative): gather {ph.Gather:F0}, sort {ph.Sort:F0}, encode {ph.Encode:F0}, publish {ph.Publish:F0}");
            }
        }
    }

    /// <summary>
    /// Hands each player session a player of its own to control, takes it back when the session goes, and marks both on the entity.
    /// </summary>
    /// <param name="tick">The tick context.</param>
    /// <remarks>
    /// <para>
    /// <b>The claim is made once per session, not per tick.</b> The engine centres the session's sphere on the entity from then on
    /// (<c>AroundControlled</c>), so there is no per-tick walk of every player and no <c>Place</c>. See <see cref="BoundPlayer"/> for what re-picking per tick
    /// cost when this method used to do it.
    /// </para>
    /// <para>
    /// <b>What SWG-01 added is the mark on the ENTITY.</b> The claim table alone tells the session which player it drives; it does not tell the simulation to
    /// stop driving that player. <c>PlayerControl.Kind</c> is that, and it is written here — the one place that knows both when a claim is made and when it is
    /// given back — rather than inferred anywhere from the table, which only this system may read.
    /// </para>
    /// </remarks>
    private static void PossessPlayers(TickContext tick)
    {
        var subs = tick.Subscriptions;
        var tx = tick.Transaction;
        if (subs == null || tx == null)
        {
            return;
        }

        // NOT disposed: the accessor comes from the TICK's transaction, which owns it and releases it. Disposing one taken from a transaction this
        // method did not create tears down the cached EntityMap and chunk accessors mid-tick, which stops later systems reading.
        var accessor = tx.For<Player>();

        // Which sessions still need a player. Collected first so the walk below can hand one out the moment it meets a player nobody holds.
        Unbound.Clear();
        Seen.Clear();
        foreach (var session in subs.OpenSessions)
        {
            if (!string.Equals(subs.SessionKindOf(session), PlayerKind, StringComparison.Ordinal))
            {
                continue;
            }

            Seen.Add(session.Value);
            if (!BoundPlayer.ContainsKey(session.Value))
            {
                Unbound.Add(session);
            }
        }

        // A player for every session that does not hold one yet, handed over once with Control: the engine centres the session's sphere on it from then
        // on (AroundControlled), so there is no per-tick walk of every player and no Place.
        var cursor = 0;
        if (Unbound.Count > 0)
        {
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                var occupancy = cluster.OccupancyBits;
                while (occupancy != 0 && cursor < Unbound.Count)
                {
                    var slot = BitOperations.TrailingZeroCount(occupancy);
                    occupancy &= occupancy - 1;

                    // The EntityId rather than the raw long the span carries, because the claim has to be opened for writing on RELEASE as well as on claim
                    // and an EntityId cannot be reconstructed from a raw value outside the engine.
                    var entity = cluster.GetEntityId(slot);
                    if (BoundIds.Contains(entity))
                    {
                        continue;
                    }

                    var session = Unbound[cursor++];
                    BoundPlayer[session.Value] = entity;
                    BoundIds.Add(entity);
                    subs.Session(session).Control(entity);

                    // The mark that stops PlayerThink driving it. Written through OpenMut rather than into this cluster's span because the span is read-only
                    // here and, more to the point, a possession is one entity: opening it says so, and sets the dirty bit the fence and the projection read.
                    if (accessor.TryOpenMut(entity, out var possessed))
                    {
                        ref var control = ref possessed.Write(Player.Control);
                        control.Kind = ControllerKind.Human;
                        control.Controller = session.Value;
                        control.Target = EntityId.Null;
                        System.Threading.Interlocked.Increment(ref _possessions);
                    }
                }

                if (cursor >= Unbound.Count)
                {
                    break;
                }
            }
        }

        // A closed session gives its player back, or the maps grow for the life of the process and every player eventually reads as held — at which
        // point a new session is bound to nothing and sees nothing.
        Retired.Clear();
        foreach (var (sessionValue, entity) in BoundPlayer)
        {
            if (!Seen.Contains(sessionValue))
            {
                Retired.Add(sessionValue);
                BoundIds.Remove(entity);

                // Back to the simulation. Without this a player whose client left would stand still for ever: possessed, so PlayerThink skips it, and with
                // nobody left to send it an intent. Zero throughout is the resting state, which is also what a freshly spawned player has.
                if (accessor.TryOpenMut(entity, out var released))
                {
                    ref var control = ref released.Write(Player.Control);
                    control.Kind = ControllerKind.InProcess;
                    control.Controller = 0u;
                    control.Target = EntityId.Null;
                    System.Threading.Interlocked.Increment(ref _releases);
                }
            }
        }

        for (var i = 0; i < Retired.Count; i++)
        {
            BoundPlayer.Remove(Retired[i]);
        }
    }

    // ── Intents (SWG-01) ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Movement intents applied.</summary>
    private static long _intentsApplied;

    /// <summary>Intents whose session controls no player — a command that arrived before the claim, or after the player was given back.</summary>
    private static long _intentsUnowned;

    /// <summary>Intents asking for a speed this server does not grant.</summary>
    private static long _intentsRefusedSpeed;

    /// <summary>Targets refused because the session was never shown the entity it named, or it has gone.</summary>
    private static long _targetsRefused;

    /// <summary>Targets set.</summary>
    private static long _targetsSet;

    /// <summary>Possessions granted and given back.</summary>
    private static long _possessions;

    /// <summary>Players handed back to the simulation.</summary>
    private static long _releases;

    /// <summary>What the intent path did, cumulatively. For the report and for the checks beside the demo.</summary>
    public static (long Applied, long Unowned, long RefusedSpeed, long TargetsSet, long TargetsRefused, long Possessions, long Releases) Intents
        => (System.Threading.Interlocked.Read(ref _intentsApplied), System.Threading.Interlocked.Read(ref _intentsUnowned),
            System.Threading.Interlocked.Read(ref _intentsRefusedSpeed), System.Threading.Interlocked.Read(ref _targetsSet),
            System.Threading.Interlocked.Read(ref _targetsRefused), System.Threading.Interlocked.Read(ref _possessions),
            System.Threading.Interlocked.Read(ref _releases));

    /// <summary>
    /// Applies this tick's client intents: where each possessed player is heading, how fast, and what it is aiming at.
    /// </summary>
    /// <param name="tick">The tick context.</param>
    /// <remarks>
    /// <para>
    /// <b>Command-first, not player-first, which is a stated deviation from <c>design/SwgTatooine/03-server.md § 6</c>.</b> That section has a parallel
    /// <c>PlayerInput</c> system reading <c>Commands&lt;MoveTo&gt;().TryGetLatest(session, out cmd)</c>, which is a scan of the command buffer per player per
    /// chunk — O(players × commands) for a buffer whose length is bounded by the number of CONNECTED clients. Walking the commands and opening the one entity
    /// each names is O(commands), and it is what makes this affordable serially, which is what the segment-0 and <c>PlayerMotion</c> hazards described on
    /// <see cref="SessionTick"/> require. Nothing about the validation or the effect changes.
    /// </para>
    /// <para>
    /// <b>Every intent is re-validated here, where the state it must agree with is.</b> The wire check is syntactic (a finite coordinate, a declared speed
    /// class) and the engine's rate limiter is arithmetic; neither knows whether this session still controls that player, whether the destination is in the
    /// world, or whether the target is something this client was ever shown. A speed is never taken from a client at all — only a class, which is mapped to
    /// what the player is entitled to — so there is no code path from a message to a position and a speed hack is impossible by construction rather than by
    /// check (<c>06-gameplay.md § 2</c>).
    /// </para>
    /// <para>
    /// <b><c>ActivityTicks</c> is set to zero, deliberately.</b> A possessed player has no server-side activity timer: nothing counts down for it because
    /// nothing is deciding for it. That also makes the possession mark load-bearing rather than decorative — with <c>ActivityTicks</c> at zero,
    /// <c>PlayerThink</c> would re-decide this player's activity on the very next tick and overwrite the destination the client just sent.
    /// </para>
    /// </remarks>
    private static void ApplyIntents(TickContext tick)
    {
        var subs = tick.Subscriptions;
        var tx = tick.Transaction;
        if (subs == null || tx == null || BoundPlayer.Count == 0)
        {
            return;
        }

        var accessor = tx.For<Player>();
        var half = (float)(WorldEdgeM * 0.5);

        foreach (var command in subs.Commands<MoveTo>())
        {
            if (!TryOpenControlled(subs, accessor, command.Session, out var player))
            {
                continue;
            }

            ref var move = ref player.Write(Player.Move);
            move.DestX = Math.Clamp(command.Value.X, -half, half);
            move.DestZ = Math.Clamp(command.Value.Z, -half, half);
            move.SpeedMps = TatooineData.PlayerRunSpeedMps;
            SteerTo(ref move, ref player, subs);
        }

        foreach (var command in subs.Commands<MoveDir>())
        {
            if (!TryOpenControlled(subs, accessor, command.Session, out var player))
            {
                continue;
            }

            if (!TrySpeedFor(command.Value.SpeedClass, out var speed))
            {
                System.Threading.Interlocked.Increment(ref _intentsRefusedSpeed);
                continue;
            }

            // A heading is turned into a destination one second of travel away rather than into a velocity, so that this and MoveTo leave the player in the
            // same state and PlayerMove needs to know nothing about which one sent it. A held key re-sends every frame, and each one renews the second.
            ref var move = ref player.Write(Player.Move);
            var place = player.Read(Player.Bounds);
            move.SpeedMps = speed;
            var reach = MathF.Max(speed, 1f);
            move.DestX = Math.Clamp(place.X + (MathF.Cos(command.Value.Heading) * reach), -half, half);
            move.DestZ = Math.Clamp(place.Z + (MathF.Sin(command.Value.Heading) * reach), -half, half);
            SteerTo(ref move, ref player, subs);
        }

        foreach (var command in subs.Commands<SetTarget>())
        {
            if (!TryOpenControlled(subs, accessor, command.Session, out var player))
            {
                continue;
            }

            ref var control = ref player.Write(Player.Control);
            if (command.Value.NetId == 0u)
            {
                control.Target = EntityId.Null;
                continue;
            }

            // TryResolve, not TryResolveAny: a client may only name what it was shown (SUB-26). A refusal is not an error — the entity may have left since the
            // command was sent — so the target is cleared and counted rather than the session being closed.
            if (subs.TryResolve(command.Session, command.Value.NetId, out var target))
            {
                control.Target = target;
                System.Threading.Interlocked.Increment(ref _targetsSet);
            }
            else
            {
                control.Target = EntityId.Null;
                System.Threading.Interlocked.Increment(ref _targetsRefused);
            }
        }
    }

    /// <summary>The player a session controls, opened for writing.</summary>
    /// <param name="subs">This tick's replication surface.</param>
    /// <param name="accessor">The tick transaction's player accessor.</param>
    /// <param name="session">The session that sent the command.</param>
    /// <param name="player">The entity.</param>
    /// <returns><see langword="false"/> when the session controls nothing, or what it controls has gone.</returns>
    /// <remarks>
    /// <b>Both failures are ordinary rather than exceptional</b> and are counted as one: a command can arrive on the tick a session opened, before the claim
    /// has been made, and a command can be in flight when the player it names is destroyed. Neither is the client's fault and neither is worth a kick.
    /// </remarks>
    private static bool TryOpenControlled(SubscriptionsCommands subs, ArchetypeAccessor<Player> accessor, SessionId session, out EntityRefMut player)
    {
        if (!BoundPlayer.TryGetValue(session.Value, out var entity) || !accessor.TryOpenMut(entity, out player))
        {
            System.Threading.Interlocked.Increment(ref _intentsUnowned);
            player = default;
            return false;
        }

        System.Threading.Interlocked.Increment(ref _intentsApplied);
        return true;
    }

    /// <summary>Points a player's velocity at the destination its intent just set, and tells replication its activity changed.</summary>
    /// <param name="move">The player's motion, already carrying the destination and the speed.</param>
    /// <param name="player">The entity.</param>
    /// <param name="subs">This tick's replication surface, for the push mark.</param>
    /// <remarks>
    /// The same <c>Steer</c> the simulation uses, through the same <c>PlayerMove</c> integration: an intent is a destination and nothing else, and that is
    /// the whole of why a client cannot move faster than the server allows.
    /// </remarks>
    private static void SteerTo(ref PlayerMotion move, ref EntityRefMut player, SubscriptionsCommands subs)
    {
        ref var state = ref player.Write(Player.State);
        state.Activity = PlayerActivity.Travelling;

        // Zero, because a possessed player has no server-side timer: see the remarks on ApplyIntents.
        state.ActivityTicks = 0;

        var place = player.Read(Player.Bounds);
        var dx = move.DestX - place.X;
        var dz = move.DestZ - place.Z;
        var len = MathF.Sqrt((dx * dx) + (dz * dz));
        if (len < 0.001f)
        {
            move.VelX = 0f;
            move.VelZ = 0f;
        }
        else
        {
            var step = MathF.Min(move.SpeedMps * MetresPerTickForIntents, len);
            move.VelX = dx / len * step;
            move.VelZ = dz / len * step;
        }

        subs.Replicate(in player);
    }

    /// <summary>What a speed class is worth, or nothing when this server does not grant it.</summary>
    /// <param name="speedClass">The class the client asked for.</param>
    /// <param name="speed">Metres per second.</param>
    /// <returns><see langword="false"/> for a class above what the player is entitled to.</returns>
    /// <remarks>
    /// There is no mount state in this demo, so <c>PlayerRunSpeedMps</c> is the ceiling and a client asking for more is refused rather than clamped: a clamp
    /// would let a client ask for 12 m/s every tick and never learn that it is not getting it, and the refusal is the number that says whether anything is
    /// trying.
    /// </remarks>
    private static bool TrySpeedFor(byte speedClass, out float speed)
    {
        switch (speedClass)
        {
            case SpeedClasses.Stop: speed = 0f; return true;
            case SpeedClasses.Walk: speed = TatooineData.PlayerRunSpeedMps * 0.5f; return true;
            case SpeedClasses.Run: speed = TatooineData.PlayerRunSpeedMps; return true;
            default: speed = 0f; return false;
        }
    }

    /// <summary>Clears the intent counters, so one check's refusals do not decide the next one's.</summary>
    /// <remarks>Called from a <c>SetUp</c>, for the reason <see cref="ResetSessionAccounting"/> is. Never from the demo.</remarks>
    public static void ResetIntentAccounting()
    {
        System.Threading.Interlocked.Exchange(ref _intentsApplied, 0);
        System.Threading.Interlocked.Exchange(ref _intentsUnowned, 0);
        System.Threading.Interlocked.Exchange(ref _intentsRefusedSpeed, 0);
        System.Threading.Interlocked.Exchange(ref _targetsRefused, 0);
        System.Threading.Interlocked.Exchange(ref _targetsSet, 0);
        System.Threading.Interlocked.Exchange(ref _possessions, 0);
        System.Threading.Interlocked.Exchange(ref _releases, 0);
        BoundPlayer.Clear();
        BoundIds.Clear();
    }

    /// <summary>The player a session controls, or <c>EntityId.Null</c>.</summary>
    /// <param name="session">The session.</param>
    /// <returns>The entity.</returns>
    /// <remarks>
    /// The claim table is this system's alone at run time — nothing else may read it, because nothing else is on its thread. A check reads it between ticks,
    /// which is a different situation and the only one in which this is safe.
    /// </remarks>
    public static EntityId ControlledForTest(SessionId session)
        => BoundPlayer.TryGetValue(session.Value, out var entity) ? entity : EntityId.Null;

    /// <summary>The world's edge in metres, so an intent's destination can be clamped to it.</summary>
    /// <remarks>Set from the configuration before <c>Start</c>, like every other value here that the simulation owns and replication reads.</remarks>
    public static double WorldEdgeM { get; set; } = 16_384d;

    /// <summary>One tick's share of a second, for turning a speed into a step. Set before <c>Start</c>.</summary>
    public static float MetresPerTickForIntents { get; set; } = 0.1f;

    /// <summary>The player each session watches, for the life of the session.</summary>
    /// <remarks>
    /// <para>
    /// <b>A session is an observer, and an observer has to be somewhere in particular.</b> This used to re-pick the player per session per tick by walking
    /// the Player clusters and taking the n-th one — so a session was bound to an ORDINAL IN AN ITERATION ORDER rather than to a character. That order is
    /// not stable: players migrate between clusters, repair redistributes them, and clusters are created and released, so session n watched a different
    /// character on almost every tick and its viewpoint jumped to wherever that character happened to stand.
    /// </para>
    /// <para>
    /// <b>Measured, it moved the viewpoints 118.8 m per tick</b> against the 0.1 m a player running at <c>PlayerRunSpeedMps</c> covers at 50 Hz — about
    /// twelve hundred times too fast, and the distance between two arbitrary characters rather than a distance anybody travelled. Every session therefore
    /// entered and left most of a disc every tick: 322 enters and 321 leaves per frame with 6 600 enters permanently owed, a backlog that could never
    /// drain because the next tick moved the disc again. That is where "the enter backlog" and the 1.05 enter-to-leave ratio came from, and both are
    /// artefacts of this method rather than anything the subscriptions track did.
    /// </para>
    /// <para>
    /// <b>Spreading the sessions across DIFFERENT players is still the point</b> and is why the binding walks for a player nobody holds: a measurement taken
    /// with every session on one spot reports a per-session cost no real population has. Doing it ONCE is what makes each session an observer instead of a
    /// teleport.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<uint, EntityId> BoundPlayer = [];

    /// <summary>The players held by some session, so the walk can tell a free one from a taken one without searching.</summary>
    private static readonly HashSet<EntityId> BoundIds = [];

    /// <summary>Scratch: the player sessions open this tick that hold no player yet.</summary>
    private static readonly List<SessionId> Unbound = [];

    /// <summary>Scratch: the player sessions seen open this tick, so the closed ones can give their players back.</summary>
    private static readonly HashSet<uint> Seen = [];

    /// <summary>Scratch: the bindings to drop, collected before the dictionary is written.</summary>
    private static readonly List<uint> Retired = [];
}
