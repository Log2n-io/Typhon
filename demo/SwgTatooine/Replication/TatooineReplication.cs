using System;
using System.Collections.Generic;
using System.Numerics;
using Typhon.Protocol;

namespace SwgTatooine;

/// <summary>A god camera asks to look at another planet (Realms G3): the planet's realm id.</summary>
public struct ViewRealm
{
    /// <summary>The planet, 0 to <c>--planets</c> − 1.</summary>
    public uint Realm;
}

/// <summary>
/// A god camera asks to ride an entity: the session itself follows it, wherever it goes (CLI3D-10 rung 2).
/// </summary>
/// <remarks>
/// <para>
/// <b>The session, not the camera.</b> The client could already point its eye camera at any entity it held, but the SESSION stayed a region around a point
/// the camera chose: the subject could walk out of it, and a subject that walked through a door simply left the store. This makes the subject the anchor —
/// the profile becomes the player's <c>AroundControlled</c> sphere and the engine takes both the realm and the centre from the entity, carrying the session
/// through portals and shuttles in the same tick the entity crosses (12-realms § 1.3).
/// </para>
/// <para>
/// <b>It names a netId, never an entity.</b> The resolution is <c>TryResolve</c>, which admits only what this session was shown (SUB-26), so the command
/// cannot be used to look at something the viewer was never sent.
/// </para>
/// </remarks>
public struct Spectate
{
    /// <summary>The subject's network identity, or 0 to stop and go back to a camera of one's own.</summary>
    public uint NetId;
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
/// What a client says out loud (SWG-09). Spatial <c>/say</c>, which SWG carried 50 m.
/// </summary>
/// <remarks>
/// <b>Queued rather than coalesced, at one a second.</b> Every utterance matters and they matter in order — coalescing
/// would keep the newest and drop the sentence before it, which is the one thing chat must not do. The rate is the
/// protocol's (04-protocol § 4); a client that exceeds it has its extra messages dropped and counted by the engine before
/// any system sees them.
/// </remarks>
public struct Say
{
    /// <summary>What was said. Capped at 256 UTF-8 bytes by the wire and by the type alike.</summary>
    public Utf8Text256 Text;
}

/// <summary>
/// Something said, heard by everyone near enough — and only in the speaker's own realm (SWG-09).
/// </summary>
/// <remarks>
/// <para>
/// <b>The realm is the half that is easy to get wrong.</b> Two players standing at the same local coordinates in two
/// different cantinas are 0 m apart by arithmetic and must not hear each other; <c>RouteNear(point, realm, radius)</c> is
/// what makes that true, and it is the engine's to enforce rather than the demo's to remember.
/// </para>
/// <para>
/// <see cref="X"/>, <see cref="Z"/> and <see cref="Realm"/> are routing only and are kept off the wire: a listener has
/// the speaker's position from the entity it already holds, and sending it again would be a second copy that can
/// disagree with the first.
/// </para>
/// </remarks>
public struct Chat
{
    /// <summary>Who spoke.</summary>
    public EntityId Speaker;

    /// <summary>What they said.</summary>
    public Utf8Text256 Text;

    /// <summary>Where they were, for the routing.</summary>
    public float X;

    /// <summary>Where they were, for the routing.</summary>
    public float Z;

    /// <summary>Which realm they were in, for the routing.</summary>
    public ushort Realm;
}

/// <summary>
/// Stop and start the whole simulation. <b>A demo control, and a temporary one.</b>
/// </summary>
/// <remarks>
/// <para>
/// <b>It pauses the world for EVERYONE, and any spectator may send it.</b> That is indefensible for a server with more
/// than one viewer, and it is deliberate: the browser client had a Pause button that could only ever pause the mock, and
/// a control that does nothing is worse than none. Pausing for real is what makes the world inspectable — you cannot
/// click a creature that is moving at 12 m/s across your screen.
/// </para>
/// <para>
/// <b>What has to happen before this is anything but a demo control:</b> it needs an entitlement (an operator role, not
/// <see cref="SessionRole.Spectator"/>), or it needs to become a per-session view freeze that stops the client's clock
/// instead of the server's tick. The second is the better answer for a real server and costs nothing to anyone else.
/// </para>
/// </remarks>
public struct SetPaused
{
    /// <summary>Non-zero to pause, zero to resume.</summary>
    public byte Paused;
}

/// <summary>
/// One blow landed: what a client draws an attack line for (SWG-09, 04-protocol § 3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Routed to the sessions that know either end</b> — a spectator watching the fight, and the player at one end of it.
/// A session that has never been shown either entity has nothing to draw and is not billed for the bytes.
/// </para>
/// <para>
/// <b>It does not carry the target's health, which 04-protocol § 3 lists.</b> A hit changes the target's replicated
/// <c>hp</c>, and events are delivered inside the frame that already carries this tick's updates — so the health after
/// the blow is in the store by the time the client's handler runs, and sending it again would be a second copy of a value
/// the client has. The client reads it from the store (<c>data/source.ts</c>, <c>healthOf</c>). Recorded as a deviation
/// rather than silently narrowed.
/// </para>
/// </remarks>
public struct Attack
{
    /// <summary>Who struck. Every blow in this demo has one; the field is nullable because the wire's is.</summary>
    /// <remarks>
    /// A session that was never shown the attacker receives this as netId 0 — the engine cannot name an entity a client
    /// has no identity for — so a client must treat either end as possibly unknown whatever the server intends.
    /// </remarks>
    public EntityId Attacker;

    /// <summary>What was struck.</summary>
    public EntityId Target;

    /// <summary>
    /// Health taken off, in the simulation's own absolute health units — NOT in the fraction the target's replicated
    /// <c>hp</c> carries.
    /// </summary>
    /// <remarks>
    /// The two are deliberately different scales and a client cannot convert between them: <c>hp</c> travels as an 8-bit
    /// fraction of a maximum that is not replicated at all (04-protocol § 2). So this is a magnitude for a hit marker, not
    /// a number to subtract from a health bar — the bar's new value arrives as state in the same frame.
    /// </remarks>
    public ushort Amount;
}

/// <summary>
/// A GM tells a realm and everything under it that something happened: the human half of <see cref="RealmNews"/>.
/// </summary>
/// <remarks>
/// <b>The announcement half of realm routing had no producer a person could reach.</b> Dungeons emit <see cref="RealmNews"/> when they open and close, and
/// nothing else ever did — so "a planet's news reaches the players in its buildings and not another planet's" was a claim the demo could make only as a
/// side effect of its own timing. This is a GM control: it names a realm, and every session in that realm or in a realm under it hears it (Realms G3,
/// <c>RouteToRealm(subtree: true)</c>).
/// </remarks>
public struct GmAnnounce
{
    /// <summary>The realm to tell, and whose subtree hears it.</summary>
    public uint Realm;
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

    /// <summary>A GM said something to a realm and everything under it (<see cref="GmAnnounce"/>).</summary>
    public const ushort GmNotice = 3;

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

    /// <summary>The profile a camera riding an entity is served: <see cref="PlayerProfile"/>'s shape plus the buildings. See its declaration for why it is
    /// a separate profile and not a flag on the player's.</summary>
    public const string SpectateProfile = "spectate";

    /// <summary>The session kind a small-view client names in <c>HELLO</c>.</summary>
    public const string PlayerKind = "player";

    /// <summary>How far a player sees, in metres.</summary>
    /// <remarks>
    /// Chosen as a plausible awareness range for a ground game at this world scale, not measured from anything: what it is here for is that a player's view
    /// is a DISC rather than the world, and the exact figure only moves the constant. Tatooine's cells are 256 m, so a disc of this size spans a handful of
    /// them and the cluster index has something to reject.
    /// </remarks>
    private const double PlayerRadiusM = 192d;

    /// <summary>
    /// <c>ACKS</c> reason: a possessed player's session asked to look at another realm.
    /// </summary>
    /// <remarks>
    /// Application reason codes start at <see cref="AckReasons.FirstApplicationReason"/>; these are the demo's first two. A client that fades between
    /// realms needs to hear a refusal, because the alternative is a fade it never comes out of.
    /// </remarks>
    public const byte ViewRealmRefusedPlayer = AckReasons.FirstApplicationReason;

    /// <summary><c>ACKS</c> reason: the realm asked for is not registered — never was, or is a dungeon nobody is inside.</summary>
    public const byte NoSuchRealm = AckReasons.FirstApplicationReason + 1;

    /// <summary>
    /// The reason a <see cref="ViewRealm"/> naming an unviewable realm is refused: <see cref="NoSuchRealm"/>, under the name its own callers read.
    /// </summary>
    /// <remarks>
    /// One code for both commands, because the code's meaning is the REALM and not the verb. A client's reason table rendering "cannot view that realm" for a
    /// refused announcement would be describing a command nobody sent.
    /// </remarks>
    public const byte ViewRealmNoSuchRealm = NoSuchRealm;

    /// <summary>
    /// <c>ACKS</c> reason: the netId names nothing this session was shown — it left the view between the click and the tick.
    /// </summary>
    /// <remarks>
    /// <b>There is deliberately no "a player may not spectate" code beside it.</b> <see cref="Spectate"/> declares <c>Roles(SessionRole.Spectator)</c>, and
    /// the engine answers a command a role may not send with <see cref="AckReasons.Forbidden"/> before any system sees it — so an application check for the
    /// same thing is unreachable, and a reason code for it would be a constant nothing can ever send. Measured, not assumed: a case that asserted the
    /// application's own refusal timed out, and the same case asserting <c>Forbidden</c> passes.
    /// </remarks>
    public const byte SpectateNoSuchEntity = AckReasons.FirstApplicationReason + 2;

    // There is deliberately no "you are riding" refusal code. Asking for a realm while riding ENDS the ride and goes there — see the ViewRealm loop. The
    // first version refused it instead, which needed the application to know whether the engine considered the session anchored; it got that wrong once, in
    // the tick between asking for the god profile and the prologue applying it, and the wrong answer was a throw on the tick thread.

    /// <summary>The realm kind of a building's interior (Realms G3): a one-cell realm, served whole.</summary>
    public const string InteriorKind = "interior";

    /// <summary>The realm kind of space (Realms G3): a deep realm at its own cell.</summary>
    public const string SpaceKind = "space";

    /// <summary>The replication grid's cell side (<see cref="SubscriptionsOptions.ReplicationCellM"/>): a third of <see cref="PlayerRadiusM"/>.</summary>
    public const double ReplicationCellM = PlayerRadiusM / 3d;

    /// <summary>The teleport threshold, in metres per second: the fastest anything on Tatooine moves, plus headroom.</summary>
    /// <remarks>
    /// <para>
    /// It sizes the motion codec: the teleport threshold is what separates "it moved" from "it was put somewhere else", and the velocity width is derived from
    /// it together with the tick period. Declaring it too high wastes a bit per segment; too low turns a sprint into a teleport.
    /// </para>
    /// <para>
    /// <b>The 5 % headroom over <c>PlayerMountSpeedMps</c> (12 m/s) is not padding, and declaring the true maximum here is broken.</b> The engine's test is
    /// <c>step² &gt; (TeleportMaxSpeedMps × tickPeriod)²</c> over QUANTIZED positions, with no margin — so an entity travelling at exactly the declared
    /// maximum sits on the boundary and the ~1 mm position quantum tips it over on about half its ticks. Every tip is reported to clients as a teleport,
    /// which forbids interpolation, so a mounted player stood still for 112 frames of 180 and then jumped at 86 m/s. Measured in the browser client, which is
    /// how it was found. At 12.6 every mover is smooth (11 of 11, no stalls). The engine should carry the margin itself; until it does, it lives here.
    /// </para>
    /// </remarks>
    internal const double MaxSpeedMps = 12.6;

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

    /// <summary>
    /// Marks an entity opened through a transaction as changed for replication (ADR-067). The form the combat resolver needs: it writes entities it opened by
    /// id rather than cluster slots it walked.
    /// </summary>
    /// <param name="entity">The entity, already written.</param>
    public static void Replicate(in EntityRefMut entity) => _pushCommands?.Replicate(in entity);

    /// <summary>Each player session's outbound byte budget, bytes per second; 0 for none (<c>--session-budget</c>).</summary>
    public static int PlayerBudgetBytesPerSecond { get; set; }

    /// <summary>The players' leave radius, metres (<c>--player-leave</c>); 0 for none, the default. AC-2 and AC-3 run at 192/208 m.</summary>
    public static double PlayerLeaveM { get; set; }

    /// <summary>The god region's largest edge, metres (<c>--god-region</c>); 0 keeps the <c>World</c> god camera, the default.</summary>
    public static double GodRegionMaxEdgeM { get; set; }

    /// <summary>The god region's near budget, entities (<c>--god-near</c>); 10 000 by default, AC-3's.</summary>
    public static int GodNearBudget { get; set; } = 10_000;

    /// <summary>How many planets there are (<c>--planets</c>); planet p is realm p.</summary>
    public static int Planets { get; set; } = 1;

    /// <summary>
    /// How many realms a god camera may ask to look at with <see cref="ViewRealm"/>: realms <c>0 .. ViewableRealms - 1</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The permanent realms, and deliberately not the dungeons.</b> Planets, interiors and space are registered at start-up and never unregistered, so a
    /// range check against this number is exactly a registration check and cannot be wrong. Dungeon realms are registered as parties form and unregistered
    /// when they disperse, so an id inside their range is registered or not depending on the second it is asked about — and <c>Enter</c> into an unregistered
    /// realm THROWS at the call site, which on the tick path is not a thing this may risk. They are refused here and absent from the realm directory; a
    /// dungeon watcher is its own feature, with its own way of knowing which ones are live.
    /// </para>
    /// <para>
    /// Set by <c>TatooineSim</c> when it registers the realms, so it cannot drift from what was registered.
    /// </para>
    /// </remarks>
    public static int ViewableRealms { get; set; } = 1;

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
    /// The sessions riding an entity: this application's INDEX of them, not its copy of the anchor.
    /// </summary>
    /// <remarks>
    /// <b>It answers "which sessions are riding", which the engine has no API for; it no longer answers "is this session anchored", which the engine
    /// does.</b> That second question used to be asked of this map, and getting it wrong cost a throw on the tick thread — the engine applies a requested
    /// profile at the NEXT tick's prologue, so between asking for the god profile and it landing, this map said one thing and the engine said another. The
    /// realm command now asks <c>TryEnter</c>, which tests and acts in one call; what is left here is a set to sweep in
    /// <see cref="DropDeadSubjects"/>, where a stale entry costs one wasted liveness check and nothing else.
    /// <para>Tick-thread only, entered when a <see cref="Spectate"/> is accepted and removed when it is released or its session closes.</para>
    /// </remarks>
    private static readonly Dictionary<SessionId, EntityId> Spectators = [];

    /// <summary>
    /// Sessions whose god profile has been asked for and which still need putting somewhere, with the realm to put them in.
    /// </summary>
    /// <remarks>
    /// <b>Releasing takes two ticks, and it is the engine's shape rather than a workaround.</b> A profile requested through <c>Session</c> is applied by the
    /// NEXT tick's prologue (<c>SubscriptionsCommands.SetRadius</c> remarks), and <c>Enter</c> is refused while the profile applied NOW is entity-anchored.
    /// So the release asks for the god profile on one tick and enters the realm on the next, which is the "two resets, one tick apart" 12-realms § 1.4 names
    /// when it rejects app-driven profile switches as the mechanism for realm variants.
    /// </remarks>
    private static readonly Dictionary<SessionId, RealmId> Releasing = [];

    /// <summary>
    /// The entity <paramref name="session"/> is riding, or <see cref="EntityId.Null"/>.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <returns>The subject.</returns>
    /// <remarks>
    /// <b>The engine's answer, not this application's.</b> It forwards to <c>Commands.ControlledOf</c>, which reads the session row the engine itself
    /// writes — so it cannot drift from the anchor the way reading the local index did, and it is right during the tick between a release being asked for
    /// and the prologue applying it, which the index is not.
    /// <para>
    /// <b>Tick thread only</b>, as everything on <c>SubscriptionsCommands</c> is. A test may call it between ticks to stage something. It goes through
    /// <c>_pushCommands</c> — the live runtime's, as <see cref="Replicate{T}(in ClusterRef{T}, int)"/> does — and answers null before one is running.
    /// </para>
    /// <para>
    /// <b>Not the same question as <see cref="ControlledBy"/>, which is why both exist.</b> That one reads
    /// <c>Player.Session.Controller</c> — the demo's own possession record, written when a client is given a player to play. This is the ENGINE's control,
    /// set by <c>Session(s).Control(e)</c>, which writes nothing on the entity: a ridden bot is not possessed and keeps deciding for itself, which is the
    /// whole difference between spectating and playing.
    /// </para>
    /// </remarks>
    public static EntityId SubjectOf(SessionId session) => _pushCommands?.ControlledOf(session) ?? EntityId.Null;

    /// <summary>The sessions whose subject died this tick, collected before any is released because releasing writes to <see cref="Spectators"/>.</summary>
    private static readonly List<SessionId> Dead = [];

    /// <summary>The profile a camera session gets: the region shape when one is configured, and the whole world otherwise.</summary>
    /// <remarks>
    /// One property because it is asked twice — when a session opens, and when a ride is released — and a session that came back on a different profile
    /// than it opened with would be a difference nobody would look for.
    /// </remarks>
    private static string GodProfileName => GodRegionMaxEdgeM > 0 ? GodRegionProfile : GodProfile;

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
        Spectators.Clear();
        Releasing.Clear();
        Dead.Clear();
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

    /// <summary>Gives back a reservation, never below zero.</summary>
    /// <param name="pending">The role's pending counter.</param>
    /// <remarks>
    /// A compare-and-swap loop rather than a decrement, because a decrement that ran once too often would make <c>live + pending</c> smaller than the truth,
    /// and that is the direction in which the cap is exceeded. Reading one too many refuses a client a moment early; reading one too few admits one too many.
    /// </remarks>
    private static void Release(ref int pending)
    {
        int seen;
        while ((seen = System.Threading.Volatile.Read(ref pending)) > 0)
        {
            if (System.Threading.Interlocked.CompareExchange(ref pending, seen - 1, seen) == seen)
            {
                return;
            }
        }
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

    // Attacks put on the wire, for the periodic report.
    private static long _strikes;

    // Things said that reached the wire, and things said that did not, for the periodic report.
    private static long _chatHeard;
    private static long _chatRefusedEmpty;
    private static long _chatRefusedNoSpeaker;

    /// <summary>What has been said, what was refused for having no speaker, and what was refused for being empty.</summary>
    public static (long Heard, long NoSpeaker, long Empty) Chatter => (
        System.Threading.Volatile.Read(ref _chatHeard),
        System.Threading.Volatile.Read(ref _chatRefusedNoSpeaker),
        System.Threading.Volatile.Read(ref _chatRefusedEmpty));

    // Whether the simulation is stopped, as an int because Volatile has no bool overload worth the cast.
    private static int _simulationPaused;

    /// <summary>
    /// Whether the simulation's own systems should do nothing this tick (<see cref="SetPaused"/>).
    /// </summary>
    /// <remarks>
    /// <b>Volatile because it crosses threads within a tick.</b> It is written by the serial session system in an early
    /// phase and read by the parallel movement, behaviour and combat systems in later ones; the phase barrier between
    /// them orders the write before the reads, and the volatile pair is what makes that ordering hold on arm64 as well
    /// as on x64.
    /// </remarks>
    public static bool SimulationPaused => System.Threading.Volatile.Read(ref _simulationPaused) != 0;

    /// <summary>
    /// Puts one landed blow on the wire, to the sessions that know either end. From a serial system, after the damage has
    /// been written — the frame carries this tick's state first and its events after (03-wire-protocol § 5), so a client's
    /// handler sees the target's new health.
    /// </summary>
    /// <param name="tick">The tick context of the system this is called from.</param>
    /// <param name="attack">The blow.</param>
    public static void Strike(TickContext tick, in Attack attack)
    {
        if (_declared && tick.Subscriptions != null)
        {
            tick.Subscriptions.Emit(in attack);
            System.Threading.Interlocked.Increment(ref _strikes);
        }
    }

    /// <summary>
    /// Puts one utterance on the wire, to whoever is near enough in the speaker's own realm.
    /// </summary>
    /// <param name="tick">The tick context of the system this is called from.</param>
    /// <param name="speaker">Who spoke.</param>
    /// <param name="text">What they said; empty is refused rather than sent.</param>
    /// <param name="x">The speaker's position, for the routing.</param>
    /// <param name="z">The speaker's position, for the routing.</param>
    /// <param name="realm">The speaker's realm, for the routing.</param>
    /// <returns>Whether it was emitted.</returns>
    /// <remarks>
    /// Empty text and a null speaker are refused HERE and counted, rather than reaching the wire as a bubble with nothing
    /// in it — the engine would carry either perfectly happily, and a client would have to decide what to do with them.
    /// </remarks>
    public static bool Speak(TickContext tick, EntityId speaker, in Utf8Text256 text, float x, float z, ushort realm)
    {
        if (speaker.IsNull)
        {
            System.Threading.Interlocked.Increment(ref _chatRefusedNoSpeaker);
            return false;
        }

        if (text.IsEmpty)
        {
            System.Threading.Interlocked.Increment(ref _chatRefusedEmpty);
            return false;
        }

        if (!_declared || tick.Subscriptions == null)
        {
            return false;
        }

        tick.Subscriptions.Emit(new Chat { Speaker = speaker, Text = text, X = x, Z = z, Realm = realm });
        System.Threading.Interlocked.Increment(ref _chatHeard);
        return true;
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

                // Inside a building, the whole realm: an interior is ONE 64 m cell, so a World observer is the room and a hull or a near budget would be
                // describing a camera footprint larger than the world it is in. No aggregate either — a far tier over a single cell is one tile.
                //
                // This used to be `NotIn(InteriorKind)`, which served a god camera nothing indoors. That was right while nothing could render a room and
                // wrong the moment something could: a viewer who picked a building got an empty realm and no way to tell it apart from an empty building.
                p.In(InteriorKind, v => v.World().Of<Player>().Of<CityNpc>().Of<Creature>().Of<WorldObject>());
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

                // As above: the whole room, because the room is one cell. This profile is already a World over the planet, so indoors it is the same shape
                // at a different scale — which is exactly what 12-realms § 1.4's variants are for.
                p.In(InteriorKind, v => v.World().Of<Player>().Of<CityNpc>().Of<Creature>().Of<WorldObject>());
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

        // What a human WATCHING a bot is served: the player's own shape plus the buildings, because a town a rider walks through has to be there.
        //
        // <b>A profile of its own rather than WorldObject added to the player's, and the reason is a measurement.</b> Adding an archetype to a Sphere is not
        // free for a sphere that MOVES: the crescent sweep queries every archetype in the set over every cell the sphere newly covers, so one more archetype
        // is a real spatial query per crescent cell on every tick the anchor moves further than its slack — about 0.7 extra cell-queries per tick per session
        // at walking pace. A first version put it on PlayerProfile with a comment claiming it cost "nothing per tick", which was read off the GOD profiles,
        // where it is true: a World observer's geometry never moves, so it fills once from a monotone cursor and never sweeps.
        //
        // Paying that on PlayerProfile would have put it on every simulated player, watched or not — and that is the profile the demo's CPU numbers are
        // taken on, so it would also have made every measurement across this change incomparable. Riders are one per human; players are the population.
        //
        // The player profile is not deficient for lacking it: its sessions are headless bots that render nothing.
        subs.Profile(SpectateProfile, p =>
        {
            p.Detection(detection)
                .Sphere(PlayerRadiusM, leave: PlayerLeaveM)
                .AroundControlled()
                .Of<Player>()
                .Of<CityNpc>()
                .Of<Creature>()
                .Of<WorldObject>();

            // Free here, unlike on the sphere above: a World observer fills once per realm from a cursor and has no crescent to sweep.
            p.In(InteriorKind, v => v.World().AroundControlled().Of<Player>().Of<CityNpc>().Of<WorldObject>());
            p.In(SpaceKind, v => v.World().AroundControlled().Of<Player>());
        });

        // Realms G3: a planet's news reaches its subtree, and a god camera moves between planets with a command.
        // SWG-09's first event: the blow a client draws a line for. Routed to whoever knows either end rather than by
        // position, because that is exactly the set of sessions with something to draw it between.
        subs.Event<Attack>(e => e
            .RouteToKnown(a => a.Attacker, a => a.Target)
            .Entity(a => a.Attacker, "attacker")
            .Entity(a => a.Target, "target")
            .Field(a => a.Amount, Codec.U16, "amount"));

        subs.Event<RealmNews>(e => e.RouteToRealm(n => new RealmId(n.Realm), subtree: true));

        // Every accepted ask is a RESET of a whole planet, the dearest frame there is: once a second, a burst of two. Spectators only — a possessed player
        // moves between planets by taking a shuttle like everyone else, and now that admission assigns roles the engine can say so instead of this being a
        // string comparison in BindOpenedSessions (which stays, as the check that the god camera is not a player's).
        //
        // The field is NAMED, like every other command's. It was the one that was not, and so it travelled as "Realm" — the C# member — where the rest of
        // this file sends "x", "z", "paused". Nothing had ever sent it, so nothing had ever noticed; the first client to try got a catalog lookup failure
        // at the point of sending. Named here rather than worked around in the client, because the client was right.
        subs.Command<ViewRealm>(c => c.Rate(1, 2).Roles(SessionRole.Spectator).Field(v => v.Realm, Codec.VarUInt, "realm"));

        // A GM announcement: one realm's subtree, told once. Spectators only, and at the SAME rate as the other god controls rather than twice it — an
        // announcement is one event fanned out to every session in a planet and in all of its buildings, which is the widest thing one message can ask for in
        // this demo, so it has no business being the most permissive of them.
        subs.Command<GmAnnounce>(c => c.Rate(1, 2).Roles(SessionRole.Spectator).Field(a => a.Realm, Codec.VarUInt, "realm"));

        // Spectators only, and at ViewRealm's rate for ViewRealm's reason: an accepted ask changes the session's profile, and a profile change is a whole
        // RESET. It is a camera's control, not a player's — a possessed player already follows itself.
        subs.Command<Spectate>(c => c.Rate(1, 2).Roles(SessionRole.Spectator).Field(s => s.NetId, Codec.VarUInt, "netId"));

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
            // Finiteness only. The speed class is deliberately NOT checked here: the engine's own contract for a pre-check is that it "rejects the impossible,
            // not the disallowed", and which classes this server grants is policy that lives with the entitlement in TrySpeedFor. Checking it here made the
            // system's refusal counter unreachable by any client — so the code claimed to count something it never could.
            .Precheck(static (in MoveDir m) => float.IsFinite(m.Heading))
            .Field(m => m.Heading, Codec.F16, "heading")
            .Field(m => m.SpeedClass, Codec.U8, "speedClass"));
        subs.Command<SetTarget>(c => c
            .Rate(4, 8)
            .Roles(SessionRole.Player)
            .Field(t => t.NetId, Codec.VarUInt, "netId"));

        // SWG-09. Queued, because each utterance matters and their order is the conversation; one a second, which is the
        // rate 04-protocol § 4 gives it and roughly what a person types.
        subs.Command<Say>(c => c
            .Rate(1, 2)
            .Roles(SessionRole.Player)
            .Field(s => s.Text, Codec.Str(Utf8Text256.Capacity), "text"));

        // Heard within 50 m of the speaker AND in the speaker's realm. The realm clause is what makes two players at the
        // same coordinates in two different cantinas unable to hear each other, and it is the engine's to enforce.
        subs.Event<Chat>(e => e
            .RouteNear(c => new Vector3D(c.X, c.Z, 0d), c => new RealmId(c.Realm), TatooineData.SayRangeM)
            .Entity(c => c.Speaker, "speaker")
            .Field(c => c.Text, Codec.Str(Utf8Text256.Capacity), "text")
            .Ignore(c => c.X)
            .Ignore(c => c.Z)
            .Ignore(c => c.Realm));

        // Stop and start the world (see SetPaused). Spectators, because the god camera is one and a possessed player has
        // no business stopping everyone else's — which is the narrowest this can be while the button still works, and is
        // not narrow enough for anything but a demo.
        subs.Command<SetPaused>(c => c
            .Coalesce(CommandCoalesce.LatestPerSession)
            .Rate(2, 4)
            .Roles(SessionRole.Spectator)
            .Field(p => p.Paused, Codec.U8, "paused"));

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

            // This branch returns before the session-event walk below, so a kicked spectator never reaches the Closed case that would drop its entry. The
            // rides are over either way; clearing here is what keeps the maps from being the one thing a shutdown leaks.
            Spectators.Clear();
            Releasing.Clear();
            return false;
        }

        WalkSessions(subs);

        foreach (ref readonly var e in subs.SessionEvents)
        {
            if (e.Kind == SessionEventKind.Opened)
            {
                // By kind, so one run can carry both shapes and a measurement can say which it measured.
                var player = e.SessionKind == PlayerKind;

                // The reservation this session's admission made is now accounted for by RecountSessions, which can see it in OpenSessions from this tick on.
                // Released here rather than by zeroing the counters before the walk, because between the hook and this event the session is in neither — see
                // RecountSessions. Clamped at zero: a reservation whose session never opened leaks one place until the next admission, which is the safe
                // direction, and a decrement that ran twice would be the unsafe one.
                Release(ref player ? ref _pendingClients : ref _pendingSpectators);
                var request = subs.Session(e.Session).Profile(player ? PlayerProfile : GodProfileName);
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
            else if (e.Kind == SessionEventKind.Closed)
            {
                // A spectator that closed mid-ride leaves nothing behind. NOT because a stale entry could be mistaken for the next session on that slot —
                // a SessionId carries its generation and compares on both, so a recycled slot is a different key and never collides. It is bounded growth:
                // these maps are process-lifetime statics, and a long run that admits and drops spectators would otherwise accumulate one entry per ride.
                Spectators.Remove(e.Session);
                Releasing.Remove(e.Session);
            }
            else if (e.Kind == SessionEventKind.RealmClosed)
            {
                // A dungeon was torn down with this session still in it, so the engine has put it in no realm and its client's store is empty. The demo used
                // to announce a dungeon's closing on planet 0's subtree and hope the client acted on it — which a session inside the dungeon does not even
                // hear, because the announcement goes to the planet it has left. This is the engine telling the application directly (12-realms § 1.6 Q7).
                //
                // Sent home rather than closed: a viewer whose instance ended has done nothing wrong, and planet 0 is somewhere it can certainly be. The god
                // profile and the release of the follow go with it, because the subject it was riding went down with the realm — and the Enter itself waits a
                // tick for the same reason every other release does (see Releasing).
                Console.WriteLine($"  !! session {e.Session.Value} was in realm {e.Realm.Value}, which closed under it — sending it home to planet 0");
                Spectators.Remove(e.Session);
                subs.Session(e.Session).Profile(GodProfileName).Control(EntityId.Null).Follow(EntityId.Null);
                Releasing[e.Session] = RealmId.Default;
            }
        }

        // The second half of a release, one tick after the god profile was asked for; see Releasing. Drained before the commands below so that a viewer who
        // stops spectating and immediately picks a realm is not refused by an anchor that is already gone.
        if (Releasing.Count > 0)
        {
            ReleaseSpectators(subs);
        }

        if (Spectators.Count > 0)
        {
            DropDeadSubjects(subs, tick.Transaction);
        }

        // A possessed player's session never reaches here: Spectate declares Roles(SessionRole.Spectator) and the engine answers a player's with
        // AckReasons.Forbidden before any system is offered it. See SpectateNoSuchEntity for why there is no application check for the same thing.
        foreach (var command in subs.Commands<Spectate>())
        {
            if (command.Value.NetId == 0u)
            {
                StopSpectating(subs, command.Session, RealmId.None);
                continue;
            }

            // TryResolve, not TryResolveAny: a client may only name what it was shown (SUB-26). A refusal is not an error — the subject may have left the
            // view between the click and this tick — so it is answered rather than the session being closed.
            if (!subs.TryResolve(command.Session, command.Value.NetId, out var subject))
            {
                subs.Reject(command, SpectateNoSuchEntity);
                continue;
            }

            // The profile carries the anchor and Control names the entity; together they make the session's realm and centre the subject's, which is what
            // takes the viewer through a door without a command (12-realms § 1.3). Control is set even when the session is already spectating something
            // else, so switching subjects is one ask rather than a release and a re-ask.
            //
            // <b>Control rather than the newer Follow, and that is a decision rather than an oversight.</b> Follow (12-realms § 2.2 Q5) gives a session an
            // entity's realm and centre WITHOUT its owner data, which is the right verb for a watcher — but "without its owner data" also means without the
            // SELF block, and SELF is the only thing that tells this client WHICH netId it ended up riding. Anchoring re-sends the whole view and netIds are
            // dense per view, so the id the viewer clicked is not the id it is now looking at; SpectateChecks says so at length. This demo declares no owner
            // fields at all, so the SELF it gets is identity and nothing else and Control leaks nothing here — the day a Player declares one, this needs
            // Follow plus a way to name the subject, and that is a wire question rather than a substitution.
            subs.Session(command.Session).Profile(SpectateProfile).Control(subject);
            Spectators[command.Session] = subject;
            Releasing.Remove(command.Session);
        }

        // A GM announcement. The realm is validated the same way ViewRealm's is and for the same reason: anything at or above ViewableRealms is a realm that
        // may have gone between the client reading a directory and the click arriving, and RouteToRealm over an unregistered realm reaches nobody anyway.
        foreach (var command in subs.Commands<GmAnnounce>())
        {
            if (command.Value.Realm >= (uint)ViewableRealms)
            {
                subs.Reject(command, NoSuchRealm);
                continue;
            }

            // Subject stays 0: for the dungeon kinds it names a realm OTHER than the one hearing the news, so repeating the announced realm here would make the
            // two indistinguishable — a client could not tell "about realm 7" from "about the realm you are in".
            Announce(tick, new RealmNews
            {
                Realm = (ushort)command.Value.Realm,
                What = RealmNews.GmNotice,
                Subject = 0,
                Count = 0,
            });
        }

        // Stop or start the world. Logged on every change and never folded into a counter: a server that stopped
        // simulating must say so where an operator reading the console will see it.
        foreach (var command in subs.Commands<SetPaused>())
        {
            var wanted = command.Value.Paused != 0 ? 1 : 0;
            if (System.Threading.Interlocked.Exchange(ref _simulationPaused, wanted) != wanted)
            {
                Console.WriteLine($"  !! simulation {(wanted != 0 ? "PAUSED" : "RESUMED")} by session {command.Session.Value}");
            }
        }

        // A god camera's move to another realm: its next frame is a RESET carrying that realm's REALM block (SUB-29). A player's session follows its player
        // through doors and shuttles instead, so this is a spectator's control and the role check says so.
        //
        // It used to accept PLANETS only, and to drop anything else in silence. Both halves were wrong once the client could draw an interior: a viewer
        // asking to look inside a building got no realm and no answer, which on a client that fades between realms is a black screen over a live world
        // rather than a refusal. So the range is every registered realm, and every path out of here either enters or SAYS SOMETHING.
        foreach (var command in subs.Commands<ViewRealm>())
        {
            if (string.Equals(subs.SessionKindOf(command.Session), PlayerKind, StringComparison.Ordinal))
            {
                // A possessed player moves by walking and taking shuttles. Its session is bound to its player's realm, so entering here would fight the
                // follow and the two would disagree about where it is.
                subs.Reject(command, ViewRealmRefusedPlayer);
                continue;
            }

            // Anything outside the permanent realms is the same answer to a client: there is nothing there to look at. See ViewableRealms for why this is a
            // range check and not a registry lookup — the realms it admits are the ones that exist for the whole run, so the two cannot differ. Checked
            // before the ride is ended, so a bad id costs the viewer nothing.
            if (command.Value.Realm >= (uint)ViewableRealms)
            {
                subs.Reject(command, ViewRealmNoSuchRealm);
                continue;
            }

            var wanted = new RealmId((ushort)command.Value.Realm);

            // <b>A rider asking for a realm stops riding and goes there, and the engine is what says which case this is.</b> Enter THROWS on an
            // entity-anchored session (12-realms § 1.3) and the dropdown is on screen throughout a ride, so the ask and the anchor test have to be one
            // call: TryEnter answers instead of raising, and the answer cannot go stale between asking and acting.
            //
            // Two earlier designs got this wrong in the same place. The first REFUSED the ask while a mirror said the session was riding; the second kept
            // the mirror and deferred instead of refusing. Both needed the application to predict the engine's two-phase profile apply — a profile asked
            // for on one tick is applied by the next tick's prologue — and the tick in between was a window where the mirror said "not anchored" and the
            // engine still said it was. The wrong answer there was a throw on the tick thread.
            //
            // A release that has been asked for but not yet applied is still anchored as far as the engine is concerned, so it lands here too and simply
            // updates where the drain will put the session.
            if (!subs.TryEnter(command.Session, wanted))
            {
                StopSpectating(subs, command.Session, wanted);
            }
        }

        // Reclaim reservations nobody ever claimed. A reservation is released where its session's `Opened` event is seen, and a session that was accepted but
        // then failed to open — `SessionTable.TryAdmit` can still fail to lease a slot, or throw, after the hook has said yes — produces no such event, so its
        // place under the cap would be held for the life of the process. Two consecutive ticks with no session event at all means nothing is in flight: an
        // admission that happened before the first of them has had a whole tick for its event to be drained. One tick would not be enough, because an
        // admission concurrent with this check has not reached the event buffer yet, and discarding THAT reservation is the direction in which the cap is
        // exceeded rather than the direction in which a client waits.
        if (subs.SessionEvents.Length == 0)
        {
            if (++_quietTicks >= 2)
            {
                System.Threading.Interlocked.Exchange(ref _pendingClients, 0);
                System.Threading.Interlocked.Exchange(ref _pendingSpectators, 0);
            }
        }
        else
        {
            _quietTicks = 0;
        }

        return true;
    }

    /// <summary>
    /// Consecutive ticks with no session event, after which an unclaimed reservation is known to be a leak rather than an admission in flight.
    /// </summary>
    private static int _quietTicks;

    /// <summary>
    /// Ends every ride whose subject has been destroyed, so the session is not left anchored to nothing.
    /// </summary>
    /// <param name="subs">This tick's replication surface.</param>
    /// <param name="tx">The tick's transaction, for the liveness test.</param>
    /// <remarks>
    /// <para>
    /// <b>The engine will not do this.</b> A destroyed subject leaves the session holding its realm and last point —
    /// <c>BoundLost</c>, 12-realms § 1.3 — until the application re-anchors it, and the only thing the client is told is a <c>SELF</c> of 0. Without this the
    /// server goes on treating that session as a rider for the rest of its life — every realm it picks costs it a pointless extra tick, and nothing ever
    /// clears the anchor, because the only other thing that does is a release the client has no particular reason to think it needs. A demo with combat in
    /// it reaches that.
    /// </para>
    /// <para>
    /// One <c>IsAlive</c> per RIDING session per tick, and rides are counted on one hand: this is not a walk of the sessions, it is a walk of the map.
    /// </para>
    /// </remarks>
    private static void DropDeadSubjects(SubscriptionsCommands subs, Transaction tx)
    {
        if (tx == null)
        {
            return;
        }

        Dead.Clear();
        foreach (var (session, subject) in Spectators)
        {
            if (!tx.IsAlive(subject))
            {
                Dead.Add(session);
            }
        }

        foreach (var session in Dead)
        {
            // Collected first and released after: StopSpectating writes to Spectators, and a dictionary may not be
            // modified while it is being enumerated.
            StopSpectating(subs, session, RealmId.None);
        }
    }

    /// <summary>
    /// Starts giving a spectating session its own camera back: asks for the god profile and notes where to put it.
    /// </summary>
    /// <param name="subs">This tick's replication surface.</param>
    /// <param name="session">The session that asked to stop.</param>
    /// <remarks>
    /// <b>The realm is read BEFORE the anchor is dropped</b>, because it is the subject's and there is nowhere else to get it. A viewer who rode a bot into
    /// a cantina and then stopped should be standing in that cantina, not thrown back to planet 0 — the realm they are looking at is the one they were last
    /// shown, and any other answer is a teleport they did not ask for. The <c>Enter</c> itself waits a tick; see <see cref="Releasing"/>.
    /// <para>
    /// <paramref name="target"/> overrides that: a viewer who picks a realm while riding is saying where they want to be, so the ride ends and the drain
    /// puts them there instead of back where the subject was.
    /// </para>
    /// <para>
    /// <c>RealmOf</c> is the last realm PUBLISHED to this session, not the subject's this instant, so a release in the tick after the subject crossed a
    /// portal leaves the viewer one realm behind. Accepted deliberately: it is the realm they were looking at when they pressed stop, which is a better
    /// answer to "where am I now" than one they were never shown.
    /// </para>
    /// </remarks>
    private static void StopSpectating(SubscriptionsCommands subs, SessionId session, RealmId target)
    {
        // RealmId.None is the "no opinion" sentinel and default(RealmId) cannot be: it is realm 0, which is planet 0 and a place a viewer may really want.
        var wanted = target;

        // Removing from the index is bookkeeping; whether there is a ride to END is the engine's answer, because a release already asked for and not yet
        // applied leaves the session anchored and out of the index at the same time.
        Spectators.Remove(session);
        if (!subs.IsAnchored(session) || Releasing.ContainsKey(session))
        {
            // Not riding. A release is accepted rather than refused — a client asking for a state it is already in is not an error — but a realm asked for
            // while a release is already pending must still land, so the pending target is updated rather than dropped.
            if (wanted.IsNone || !Releasing.ContainsKey(session))
            {
                return;
            }

            Releasing[session] = wanted;
            return;
        }

        var realm = wanted.IsNone ? subs.RealmOf(session) : wanted;
        subs.Session(session).Profile(GodProfileName).Control(EntityId.Null);
        Releasing[session] = realm.IsNone ? RealmId.Default : realm;
    }

    /// <summary>
    /// Finishes every release whose god profile has had a tick to apply: puts the session in the realm it was last shown.
    /// </summary>
    /// <param name="subs">This tick's replication surface.</param>
    /// <remarks>
    /// <para>
    /// Drained whole, once a tick. The anchor <c>Enter</c> would throw on is gone by construction — the profile was requested a tick ago and the prologue
    /// between then and now applied it — and a session that closed in the meantime is answered with <see langword="false"/> rather than a throw.
    /// </para>
    /// <para>
    /// <b>The realm is asked for rather than clamped to the permanent ones.</b> It used to be: the realm here was read a tick ago, <c>Enter</c> raises for a
    /// realm that is unregistered or closing, and a dungeon unregistered in between would have taken the tick down — so anything at or above
    /// <see cref="ViewableRealms"/> was replaced by planet 0 whether or not it was still there. <c>TryEnter</c> answers that race instead of raising on it, so
    /// a viewer released inside a dungeon that is still open stays in it, and one whose dungeon has gone falls back to the planet because it really has to.
    /// </para>
    /// </remarks>
    private static void ReleaseSpectators(SubscriptionsCommands subs)
    {
        foreach (var (session, realm) in Releasing)
        {
            if (!subs.TryEnter(session, realm) && realm != RealmId.Default)
            {
                subs.TryEnter(session, RealmId.Default);
            }
        }

        Releasing.Clear();
    }

    /// <summary>
    /// Walks the open sessions once: publishes the live count per role for the admission hook, and collects which sessions need a player and which
    /// are still here.
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
    /// <b>It does not clear the pending counters, and an earlier version that did could be made to exceed the cap.</b> A reservation has to stay in
    /// <c>pending</c> until the session it reserved for appears in <c>OpenSessions</c>, and that is not the next tick: <c>SessionTable.TryAdmit</c> runs the
    /// hook, leases a slot and appends an <c>Opened</c> event, and the session only enters the open set when <c>BeginTick</c> drains that event. Between the
    /// hook and that drain the session is in the table but not in the walk — so zeroing pending before walking counted it in neither, published a live count
    /// that did not include it, and let the next admission accept over the cap. Each reservation is now released where its <c>Opened</c> event is observed,
    /// which is the one place both facts are in hand.
    /// </para>
    /// </remarks>
    private static void WalkSessions(SubscriptionsCommands subs)
    {
        Unbound.Clear();
        Seen.Clear();
        var players = 0;
        var spectators = 0;

        foreach (var session in subs.OpenSessions)
        {
            if (!string.Equals(subs.SessionKindOf(session), PlayerKind, StringComparison.Ordinal))
            {
                spectators++;
                continue;
            }

            players++;
            Seen.Add(session.Value);
            if (!BoundPlayer.ContainsKey(session.Value))
            {
                Unbound.Add(session);
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
                + $"{subs.EntriesMigrated} entries relocated between clusters; {System.Threading.Volatile.Read(ref _announced)} realm news announced, "
                + $"{System.Threading.Volatile.Read(ref _strikes)} attacks sent, "
                + $"{System.Threading.Volatile.Read(ref _chatHeard)} said ({System.Threading.Volatile.Read(ref _chatRefusedNoSpeaker)} no speaker, "
                + $"{System.Threading.Volatile.Read(ref _chatRefusedEmpty)} empty)");

            // What the intent path did (SWG-01). These counters were built "for the report and for the checks beside the demo" — the checks read them, and the
            // report never did, so a served run printed nothing about the one thing a connected client changes. The refusals are printed beside the applications
            // deliberately: "only what the server allows is applied" is a claim about what does NOT happen, and a report that shows only successes cannot carry it.
            var it = Intents;
            Console.Error.WriteLine(
                $"  intents: {it.Applied} applied of {it.Owned} owned, {it.Unowned} unowned, {it.RefusedSpeed} refused for speed; "
                + $"targets {it.TargetsSet} set, {it.TargetsRefused} refused; possession {it.Possessions} taken, {it.Releases} released");
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
                + $"parallel busy {pp.Busy:F2} ms/tick, blocks {subs.ProjectBlocksMs:F3} ms/tick, of which column walk {subs.ProjectWalkMs:F3} ms/tick "
                + "(summed over workers)");
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

        // `Unbound` and `Seen` were filled by WalkSessions, one walk of the open sessions for this tick rather than one per consumer. There were two, each
        // with an ordinal string compare per session per tick, to produce facts a single pass already has in hand.
        //
        // Nothing to claim and nothing to release is the common case by far — a server spends almost every tick with a stable set of clients — and it is worth
        // detecting, because `tx.For<Player>()` is not free: it creates an EntityMap accessor and pre-warms the component table.
        if (Unbound.Count == 0 && BoundPlayer.Count == Seen.Count)
        {
            return;
        }

        // NOT disposed: the accessor comes from the TICK's transaction, which owns it and releases it. Disposing one taken from a transaction this
        // method did not create tears down the cached EntityMap and chunk accessors mid-tick, which stops later systems reading.
        var accessor = tx.For<Player>();

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
                        var control = possessed.Read(Player.Control);
                        control.Kind = ControllerKind.Human;
                        possessed.Set(Player.Control, control);
                        var owner = possessed.Read(Player.Session);
                        owner.Controller = session.Value;
                        owner.Target = EntityId.Null;
                        possessed.Set(Player.Session, owner);
                        Normalise(ref possessed);
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
                    var controlCopy = released.Read(Player.Control);
                    controlCopy.Kind = ControllerKind.InProcess;
                    released.Set(Player.Control, controlCopy);
                    var owner = released.Read(Player.Session);
                    owner.Controller = 0u;
                    owner.Target = EntityId.Null;
                    released.Set(Player.Session, owner);
                    Normalise(ref released);
                    System.Threading.Interlocked.Increment(ref _releases);
                }
            }
        }

        for (var i = 0; i < Retired.Count; i++)
        {
            BoundPlayer.Remove(Retired[i]);
        }
    }

    /// <summary>
    /// Brings a player to a standstill with no half-finished activity, at both ends of a possession.
    /// </summary>
    /// <param name="player">The player, already opened for writing.</param>
    /// <remarks>
    /// <para>
    /// <b>On CLAIM, because a mid-activity player would otherwise be frozen in it.</b> <c>PlayerThink</c> is what advances an activity and it now skips a
    /// possessed player, so one claimed while <c>ToShuttle</c>, <c>AwaitingShuttle</c>, <c>ToPortal</c> or <c>Inside</c> would sit in that state for as long as
    /// the client held it — and the systems that act on those states would act on it from outside the client's view. They now skip a possessed player too, but
    /// leaving the state set would be leaving a trap for the next system that reads it.
    /// </para>
    /// <para>
    /// <b>On RELEASE, because a stale activity outlives the session.</b> A player given back mid-intent would return to the simulation as <c>Travelling</c>
    /// towards wherever its last client pointed it, with a timer of zero — which <c>PlayerThink</c> reads as "arrived, decide again", so this is belt to that
    /// brace rather than load-bearing. What it does buy is that a released player is indistinguishable from one that was never possessed, which is the property
    /// the measurement checks assert.
    /// </para>
    /// </remarks>
    private static void Normalise(ref EntityRefMut player)
    {
        var state = player.Read(Player.State);
        state.Activity = PlayerActivity.Idle;
        state.ActivityTicks = 0;
        state.ShuttleFrom = -1;
        state.ShuttleDest = -1;
        player.Set(Player.State, state);

        var move = player.Read(Player.Move);
        move.VelX = 0f;
        move.VelZ = 0f;
        player.Set(Player.Move, move);
    }

    // ── Intents (SWG-01) ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Movement intents that reached a player and passed validation.</summary>
    private static long _intentsApplied;

    /// <summary>
    /// Commands of any kind whose session controlled a player — what used to be miscounted as "applied".
    /// </summary>
    /// <remarks>
    /// Separate because the two answer different questions. This one says whether a client's commands are reaching the tick at all, which is the first thing to
    /// look at when nothing moves; <see cref="_intentsApplied"/> says whether they survived validation. Counting them as one made a refused <c>MoveDir</c> both
    /// applied and refused, and made every <c>SetTarget</c> look like a movement intent.
    /// </remarks>
    private static long _intentsOwned;

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
    public static (long Owned, long Applied, long Unowned, long RefusedSpeed, long TargetsSet, long TargetsRefused, long Possessions, long Releases) Intents
        => (System.Threading.Interlocked.Read(ref _intentsOwned), System.Threading.Interlocked.Read(ref _intentsApplied),
            System.Threading.Interlocked.Read(ref _intentsUnowned),
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

            var move = player.Read(Player.Move);
            move.DestX = Math.Clamp(command.Value.X, -half, half);
            move.DestZ = Math.Clamp(command.Value.Z, -half, half);
            move.SpeedMps = TatooineData.PlayerRunSpeedMps;
            SteerTo(ref move, ref player, subs);
            player.Set(Player.Move, move);
            System.Threading.Interlocked.Increment(ref _intentsApplied);
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
            var move = player.Read(Player.Move);
            var place = player.Read(Player.Bounds);
            move.SpeedMps = speed;
            var reach = MathF.Max(speed, 1f);
            move.DestX = Math.Clamp(place.X + (MathF.Cos(command.Value.Heading) * reach), -half, half);
            move.DestZ = Math.Clamp(place.Z + (MathF.Sin(command.Value.Heading) * reach), -half, half);
            SteerTo(ref move, ref player, subs);
            player.Set(Player.Move, move);
            System.Threading.Interlocked.Increment(ref _intentsApplied);
        }

        foreach (var command in subs.Commands<SetTarget>())
        {
            if (!TryOpenControlled(subs, accessor, command.Session, out var player))
            {
                continue;
            }

            var owner = player.Read(Player.Session);
            if (command.Value.NetId == 0u)
            {
                owner.Target = EntityId.Null;
                player.Set(Player.Session, owner);
                continue;
            }

            // TryResolve, not TryResolveAny: a client may only name what it was shown (SUB-26). A refusal is not an error — the entity may have left since the
            // command was sent — so the target is cleared and counted rather than the session being closed.
            if (subs.TryResolve(command.Session, command.Value.NetId, out var target))
            {
                owner.Target = target;

                // Classified here, once, rather than on every shot: EntityId.ArchetypeId is comparable only against DatabaseEngine.ArchetypeIdOf<T>(), and the
                // system that fires needs to know which components its target has. An id that is none of the three shootable archetypes — a city NPC, a
                // building — resolves to a target the combat system will refuse, which is the right answer for "attack that cantina".
                owner.TargetKind = ClassifyTarget(target);
                System.Threading.Interlocked.Increment(ref _targetsSet);
            }
            else
            {
                owner.Target = EntityId.Null;
                System.Threading.Interlocked.Increment(ref _targetsRefused);
            }
            player.Set(Player.Session, owner);
        }

        // SWG-09. What a client says becomes a Chat heard by whoever is near enough IN THE SPEAKER'S REALM — the speaker's
        // place and realm are read here, from the player it controls, rather than taken from the client: a client that
        // could name where its voice comes from could be heard anywhere.
        foreach (var command in subs.Commands<Say>())
        {
            if (!TryOpenControlled(subs, accessor, command.Session, out var player))
            {
                continue;
            }

            var place = player.Read(Player.Bounds);
            Speak(tick, BoundPlayer[command.Session.Value], command.Value.Text, place.X, place.Z, player.Realm.Value);
        }
    }

    /// <summary>The player a session controls, opened for writing.</summary>
    /// <param name="subs">This tick's replication surface.</param>
    /// <param name="accessor">The tick transaction's player accessor.</param>
    /// <param name="session">The session that sent the command.</param>
    /// <param name="player">The entity.</param>
    /// <returns><see langword="false"/> when the session controls nothing, or what it controls has gone.</returns>
    /// <remarks>
    /// <para>
    /// <b>Both failures are ordinary rather than exceptional</b> and are counted as one: a command can arrive on the tick a session opened, before the claim
    /// has been made, and a command can be in flight when the player it names is destroyed. Neither is the client's fault and neither is worth a kick.
    /// </para>
    /// <para>
    /// <b>A claim whose entity has gone is dropped, not kept.</b> Nothing in this demo destroys a <c>Player</c>, so this is latent — but keeping a dead id in
    /// the claim table would strand the session for ever (it holds a player, so it is never handed another) and would permanently exclude whichever live player
    /// inherited the recycled <c>EntityId</c> from being possessed by anyone. Dropping it lets the next tick's walk re-bind the session, which is the behaviour
    /// this method's remarks used to claim without the code doing it.
    /// </para>
    /// <para>
    /// It counts an OWNED command, not an applied one. The movement paths increment <see cref="_intentsApplied"/> themselves, after their own validation has
    /// passed — counting here made a refused <c>MoveDir</c> both applied and refused, and made every <c>SetTarget</c> a movement intent.
    /// </para>
    /// </remarks>
    private static bool TryOpenControlled(SubscriptionsCommands subs, ArchetypeAccessor<Player> accessor, SessionId session, out EntityRefMut player)
    {
        if (!BoundPlayer.TryGetValue(session.Value, out var entity))
        {
            System.Threading.Interlocked.Increment(ref _intentsUnowned);
            player = default;
            return false;
        }

        if (!accessor.TryOpenMut(entity, out player))
        {
            BoundPlayer.Remove(session.Value);
            BoundIds.Remove(entity);
            System.Threading.Interlocked.Increment(ref _intentsUnowned);
            player = default;
            return false;
        }

        System.Threading.Interlocked.Increment(ref _intentsOwned);
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
        var place = player.Read(Player.Bounds);

        // The simulation's own integration, not a copy of it: one function, so "an intent cannot move a player faster than the server does" is a property of
        // the code rather than of two implementations happening to agree.
        var moving = SimBridge.Steer(ref move.VelX, ref move.VelZ, move.SpeedMps, MetresPerTickForIntents, place.X, place.Z, move.DestX, move.DestZ);

        var state = player.Read(Player.State);

        // A player going nowhere is Idle, not Travelling. `Activity` is replicated, so getting this wrong tells every client watching that a standing player is
        // running — which is the one visible consequence in the whole intent path, and a stop is the commonest intent there is.
        state.Activity = moving ? PlayerActivity.Travelling : PlayerActivity.Idle;

        // Zero, because a possessed player has no server-side timer: see the remarks on ApplyIntents.
        state.ActivityTicks = 0;
        player.Set(Player.State, state);
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
        System.Threading.Interlocked.Exchange(ref _intentsOwned, 0);
        System.Threading.Interlocked.Exchange(ref _intentsUnowned, 0);
        System.Threading.Interlocked.Exchange(ref _intentsRefusedSpeed, 0);
        System.Threading.Interlocked.Exchange(ref _targetsRefused, 0);
        System.Threading.Interlocked.Exchange(ref _targetsSet, 0);
        System.Threading.Interlocked.Exchange(ref _possessions, 0);
        System.Threading.Interlocked.Exchange(ref _releases, 0);
        BoundPlayer.Clear();
        BoundIds.Clear();

        // The rest of the statics a runtime leaves behind. _pushCommands in particular holds the LAST runtime's SubscriptionsCommands, so Replicate() from a
        // system of the next one would mark a slot in an object belonging to a disposed engine; the others make a second run in one process inherit the
        // first one's configuration.
        _pushCommands = null;
        Scheduler = null;
        _declared = false;
        System.Threading.Interlocked.Exchange(ref _announced, 0);
        System.Threading.Interlocked.Exchange(ref _strikes, 0);
        System.Threading.Interlocked.Exchange(ref _simulationPaused, 0);
        System.Threading.Interlocked.Exchange(ref _chatHeard, 0);
        System.Threading.Interlocked.Exchange(ref _chatRefusedEmpty, 0);
        System.Threading.Interlocked.Exchange(ref _chatRefusedNoSpeaker, 0);
        _placeTicks = 0;
        _quietTicks = 0;
        WorldEdgeM = 0d;
        MetresPerTickForIntents = 0f;
        PlayerBudgetBytesPerSecond = 0;
        PlayerLeaveM = 0d;
        GodRegionMaxEdgeM = 0d;
        GodNearBudget = 10_000;
        Planets = 1;
    }

    /// <summary>
    /// The player a session controls, found by asking the WORLD rather than the claim table.
    /// </summary>
    /// <param name="dbe">The engine, for a short read transaction of its own.</param>
    /// <param name="session">The session.</param>
    /// <returns>The entity, or <c>EntityId.Null</c> when the session controls nothing.</returns>
    /// <remarks>
    /// <b>It scans for the mark instead of reading <see cref="BoundPlayer"/>, because that dictionary is written by a live tick.</b> A
    /// <c>Dictionary&lt;,&gt;</c> read concurrent with an insert can throw or spin indefinitely, and a caller that is spinning on a condition — which is what
    /// every check that needs this is doing — runs exactly then. A "call it between ticks" contract in a comment cannot be honoured by a spin loop, so the
    /// contract is removed rather than documented. The scan is over the players of one realm-less query and costs microseconds, which is free at the only
    /// place it is called from.
    /// </remarks>
    public static EntityId ControlledBy(DatabaseEngine dbe, SessionId session)
    {
        ArgumentNullException.ThrowIfNull(dbe);
        using var tx = dbe.CreateQuickTransaction();
        foreach (var cluster in tx.For<Player>().GetClusterEnumerator())
        {
            var owners = cluster.GetReadOnlySpan(Player.Session);
            var control = cluster.GetReadOnlySpan(Player.Control);
            for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
            {
                var slot = BitOperations.TrailingZeroCount(bits);
                if (owners[slot].Controller == session.Value && control[slot].Kind != ControllerKind.InProcess)
                {
                    return cluster.GetEntityId(slot);
                }
            }
        }

        return EntityId.Null;
    }

    /// <summary>The world's edge in metres, so an intent's destination can be clamped to it. Zero until <see cref="ConfigureIntents"/> is called.</summary>
    public static double WorldEdgeM { get; private set; }

    /// <summary>One tick's share of a second, for turning a speed into a step. Zero until <see cref="ConfigureIntents"/> is called.</summary>
    public static float MetresPerTickForIntents { get; private set; }

    // Routing ids of the three shootable archetypes, cached by ConfigureIntents. See ClassifyTarget.
    private static ushort _creatureRouting;
    private static ushort _lairRouting;
    private static ushort _playerRouting;

    /// <summary>Which of the shootable archetypes an entity belongs to, for <c>PlayerSession.TargetKind</c> (SWG-02).</summary>
    /// <param name="target">The entity a client named.</param>
    /// <returns>
    /// The <c>CombatTargetKind</c> of <paramref name="target"/>. An entity of any other archetype — a city NPC, a building, a starship — answers
    /// <c>CombatTargetKind.Player</c>, which the combat system then refuses on the first shot because the entity carries no <c>PlayerVitals</c>.
    /// </returns>
    /// <remarks>
    /// <b>Refused rather than validated at the command.</b> Rejecting a targetable-but-unshootable entity here would mean the client could not select a
    /// building to inspect it, which is what <c>SetTarget</c> is also for; the combat path is the place that cares whether the target can be damaged, and it is
    /// the place that counts the refusal.
    /// </remarks>
    private static byte ClassifyTarget(EntityId target)
    {
        var routing = target.ArchetypeId;
        if (routing == _creatureRouting)
        {
            return CombatTargetKind.Creature;
        }

        return routing == _lairRouting ? CombatTargetKind.Lair : CombatTargetKind.Player;
    }

    /// <summary>
    /// States the two figures every intent is validated against: the world it must stay inside, and the tick it gets one step of.
    /// </summary>
    /// <param name="worldEdgeM">The world's edge, metres.</param>
    /// <param name="tickRateHz">The tick rate.</param>
    /// <exception cref="ArgumentOutOfRangeException">Either is not positive.</exception>
    /// <remarks>
    /// <b>Required and explicit, with no default, because both are load-bearing and a wrong one is silent.</b> They began as defaulted properties —
    /// 16 384 m and 0.1 s, the latter being 10 Hz rather than whatever the run is at — so a path that set the configuration but forgot these clamped every
    /// destination to the wrong world and moved a player five times too far per tick at 50 Hz, with nothing to see. The repository's rule for a parameter of
    /// that kind is that it is required and explicit, refused at start rather than silently clamped, which is what this is.
    /// </remarks>
    public static void ConfigureIntents(DatabaseEngine dbe, double worldEdgeM, int tickRateHz)
    {
        ArgumentNullException.ThrowIfNull(dbe);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(worldEdgeM);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tickRateHz);

        // The three archetypes a client may name as a target, by the routing id its EntityIds carry in THIS database. Resolved once here rather than per
        // command: ArchetypeIdOf is a lookup, but a per-command classification would also have to hold the engine, and the engine is what this method already
        // has. Ids are per database and assigned in registration order, so they are cached for a database rather than for a process — which is why this takes
        // the engine and not a static.
        _creatureRouting = dbe.ArchetypeIdOf<Creature>();
        _lairRouting = dbe.ArchetypeIdOf<CreatureLair>();
        _playerRouting = dbe.ArchetypeIdOf<Player>();
        WorldEdgeM = worldEdgeM;
        MetresPerTickForIntents = 1f / tickRateHz;
    }

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
