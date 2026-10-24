using SwgTatooine.Server.Accounts;
using System;
using System.Collections.Generic;
using System.Numerics;
using Typhon.Protocol;

namespace SwgTatooine.Server;

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

    /// <summary>
    /// The factor the class's speed is multiplied by: 1 to <see cref="SpeedClasses.MaxBoost"/>, the client's Shift+1..4. 0 means 1 — a client or a bot
    /// that never names a boost moves at the granted speed. Anything above the maximum is refused and counted, as a class is.
    /// </summary>
    public byte Boost;

    /// <summary>
    /// The share of the granted speed to move at, in 255ths: a swoop accelerating, coasting or braking under its rider (the client's <c>swoop-drive</c>).
    /// 0 means all of it — a key held on foot, and a bot, name no throttle.
    /// </summary>
    /// <remarks>
    /// <b>Not a speed, and it cannot be used as one:</b> it only ever scales DOWN what the class and the boost already grant, so the worst a lying client
    /// can do with it is move slower.
    /// </remarks>
    public byte Throttle;

    /// <summary>
    /// How far into the tick this intent CHANGED, in 255ths — the share of the boundary tick that still belongs to the velocity before it. 0 means "the
    /// whole tick is mine", which is what a re-send of an unchanged intent says and what any client that does not measure it sends.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is what makes the client's integration and the server's agree, and nothing else does.</b> A tick advances a player by one whole period of
    /// whatever intent it held at the boundary, while a client that predicts moves on the key event. Both travel at the same speed, for different
    /// DURATIONS: the two positions differ by <c>v x (startPhase - stopPhase)</c>, uniformly over a full tick of travel in either direction — 0.5 m at
    /// 5 m/s and 10 Hz, mean 0.167 m, which is the back-step a player sees every time they let go of a key. With the phase the boundary tick is split
    /// between the old velocity and the new one (<c>SteerTo</c>) and the two durations are equal exactly.
    /// </para>
    /// <para>
    /// <b>It cannot be used to move faster than this server allows, which is why a client is trusted with it at all.</b> The split is a convex
    /// combination of two velocities the server itself granted, so the step it produces is never longer than the longer of the two — a lying phase
    /// redistributes one tick of travel and cannot manufacture any. That is a different thing from a client sending a SPEED, which is the speed hack.
    /// </para>
    /// <para>255ths rather than milliseconds, so it means the same thing at every <c>--hz</c> and costs one byte.</para>
    /// </remarks>
    public byte ChangePhase;
}

/// <summary>
/// "Serve me this far." A player asking for a wider or narrower interest sphere than its profile's own.
/// </summary>
/// <remarks>
/// <b>Metres, and the one place in this file where a client sends a magnitude rather than a class.</b> A speed class is a
/// class because a speed from a client is the speed hack; a radius cannot be, because the whole point is a slider that
/// chooses an arbitrary distance. What makes it safe is that it is not a speed or a position: the server declares the
/// range its profile allows (<see cref="TatooineReplication.PlayerRadiusM"/>..<see cref="TatooineReplication.PlayerMaxRadiusM"/>),
/// refuses anything outside it, and the engine sizes the session's window for the maximum at start-up — so the worst a
/// client can do with this is ask for more of what it is already entitled to see, at its own bandwidth's expense.
/// </remarks>
public struct SetViewRadius
{
    /// <summary>The radius asked for, in metres. 0 returns the session to its profile's own.</summary>
    public ushort RadiusM;
}

/// <summary>
/// "Show me the whole planet." A player switching to the god camera, and back.
/// </summary>
/// <remarks>
/// <para>
/// <b>The camera alone could not do it.</b> A player session is served a sphere around its avatar, so a client that flew its camera up looked down on an empty
/// planet with one lit disc in it. On, the server serves the session <see cref="TatooineReplication.OverviewProfile"/>, the god camera's shape on the avatar's
/// realm; off, the player profile again, at the radius the player last asked for. The avatar stays bound and steered throughout.
/// </para>
/// <para>
/// <b>Planets only.</b> Refused when the avatar is inside a building, in space or in a dungeon, and ended by the server when the avatar goes into one: the god
/// camera is a view of a planet's map. Refused too while <c>--max-spectators</c> is full, because an overview costs what a god session does.
/// </para>
/// </remarks>
public struct Overview
{
    /// <summary>1 to switch to the god camera, 0 to come back.</summary>
    public byte On;
}

/// <summary>
/// "Fly me to that city." A player standing at its city's shuttleport asking to be flown to another.
/// </summary>
/// <remarks>
/// <para>
/// <b>A city index, not a position, and not a port entity.</b> The destination is one of the planet's cities, which the client already ships in its own map
/// data, so the whole of the wire payload is "which city" — there is nothing in it a client could inflate. The server decides which port that city is served
/// by, where that port is, and whether this player is standing at one.
/// </para>
/// <para>
/// <b>Why a command at all, rather than relaxing who the shuttle will carry.</b> The boarding loop admits only players the simulation is deciding for,
/// and that is right: a body flown away by a system its client cannot see is the worst kind of surprise, and with several planets it would change realm
/// too. A command inverts it — the flight happens because the client asked for it, on the tick it asked, and the refusals say why not.
/// </para>
/// </remarks>
public struct BoardShuttle
{
    /// <summary>The destination city's index, as the client's own map numbers them.</summary>
    public int DestCity;
}

/// <summary>The geometry a boarding ask is validated against: where the ports are, and how many cities there are.</summary>
/// <remarks>
/// <b>An interface rather than the index itself, because the index lives in the simulation and this file is the protocol.</b> The world's cities and the
/// ports drawn inside them belong to <c>WorldIndex</c>, which <c>SimBridge</c> owns; the command handler needs one question answered about them and has no
/// business walking them. <c>TatooineSim</c> hands the bridge over at <c>StartReplication</c>, and a server with shuttles off hands over nothing, which is
/// itself the refusal.
/// </remarks>
public interface IShuttleBoarding
{
    /// <summary>How many cities a destination may name.</summary>
    int CityCount { get; }

    /// <summary>Whether a point is close enough to its own city's shuttleport to board there.</summary>
    /// <param name="x">Where the player is, east.</param>
    /// <param name="z">Where the player is, north.</param>
    /// <param name="planet">The realm it is in, which decides whose ports these are.</param>
    /// <param name="withinM">How close counts as at the port, in metres.</param>
    /// <param name="city">The city whose port it is standing at.</param>
    /// <returns>Whether it is at one.</returns>
    bool TryPortAt(float x, float z, ushort planet, double withinM, out int city);

    /// <summary>Ticks between two landings at one port; 0 when nothing flies.</summary>
    int IntervalTicks { get; }

    /// <summary>How many ports the landings are staggered over — the divisor in the formula below; 0 when nothing flies.</summary>
    int PortCount { get; }

    /// <summary>
    /// Ticks a shuttle stays down; port <c>c</c> is down while <c>(tick + floor(c × IntervalTicks / Ports)) % IntervalTicks &lt; WindowTicks</c>.
    /// </summary>
    int WindowTicks { get; }
}

/// <summary>The speed classes a client may ask for, what each is worth, and how far a client may multiply them.</summary>
/// <remarks>
/// <para>
/// <b>A class rather than a speed, because a speed from a client is the speed hack.</b> The server maps the class onto a figure the player is entitled to,
/// and the mapping is the whole of the entitlement check: a client says "run", never "10 m/s", so there is no number on the wire for it to inflate. A
/// class this server does not grant is refused and counted rather than clamped, which is what makes an attempt visible instead of merely ineffective.
/// </para>
/// <para>
/// <b>Three ways to move, and a multiplier on all of them</b> (Loïc, 2026-10-24). A direction key walks, Shift runs, and the swoop rides; Shift+1..4 latches
/// a boost of ×1..×4 that applies to whichever of the three is in use — "more like a cheat code" than a gait, which is why it is a factor and not more
/// rungs. It replaced the ladder of burst run and two speeders, which let a pedestrian outrun a rider. The boost is bounded here
/// (<see cref="MaxBoost"/>), so the entitlement is still the server's: a client may ask for at most four times what it is granted.
/// </para>
/// </remarks>
public static class SpeedClasses
{
    /// <summary>Stand still.</summary>
    public const byte Stop = 0;

    /// <summary>Half of <see cref="Run"/> — SWG had no separate walk figure worth quoting, so this is a fraction rather than a source.</summary>
    public const byte Walk = 1;

    /// <summary>[WIKI] <c>TatooineData.PlayerRunSpeedMps</c> — 5 m/s, an unencumbered player on foot. Shift held with a direction.</summary>
    public const byte Run = 2;

    /// <summary>Riding a swoop: four times <see cref="Run"/>. Granted only to a player <c>SetMount</c> put on one (<c>PlayerState.Mounted</c>).</summary>
    public const byte Swoop = 3;

    /// <summary>The first class this server does not grant.</summary>
    public const byte Count = 4;

    /// <summary>The largest boost a client may ask for (<see cref="MoveDir.Boost"/>): Shift+4.</summary>
    public const byte MaxBoost = 4;
}

/// <summary>The combat stances a person puts a player in (<see cref="SetStance"/>), by what it fights with.</summary>
public static class Stances
{
    /// <summary>No stance: idle, and a player in it does not fight whatever it targets.</summary>
    public const byte None = 0;

    /// <summary>Fists, with a vibroknuckler: reaches 1 m.</summary>
    public const byte Unarmed = 1;

    /// <summary>A one-handed blade: reaches 3 m.</summary>
    public const byte OneHanded = 2;

    /// <summary>A pistol: reaches 30 m.</summary>
    public const byte Pistol = 3;

    /// <summary>The first stance this server does not know.</summary>
    public const byte Count = 4;
}

/// <summary>
/// "Fight with this", or stop (the client's action bar 2–4, each a toggle). <see cref="Stances"/>.
/// </summary>
/// <remarks>
/// <b>A state the server holds and replicates</b> (<c>PlayerState.Stance</c>), because it is what decides whether a possessed player's target is fought and
/// how far it reaches, and because everyone watching draws it. Taking one gets the player off its swoop: nobody fights from the saddle.
/// </remarks>
public struct SetStance
{
    /// <summary>The stance, or <see cref="Stances.None"/> to stop fighting.</summary>
    public byte Stance;
}

/// <summary>
/// "Put me on my swoop", or back on foot (the client's 1 key). The swoop is summoned where the player stands, at once: there is nothing to own or call yet.
/// </summary>
/// <remarks>
/// <b>A state the server holds, not a speed the client claims.</b> <c>PlayerState.Mounted</c> is the entitlement <c>TrySpeedFor</c> reads for
/// <see cref="SpeedClasses.Swoop"/>, and it is replicated so every session draws the rider on the swoop. Refused off a planet: a swoop does not fit a room.
/// </remarks>
public struct SetMount
{
    /// <summary>1 to ride, 0 to get off.</summary>
    public byte On;
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
public static partial class TatooineReplication
{
    /// <summary>The god camera's profile: the whole planet, every archetype, through a <c>World</c> observer.</summary>
    public const string GodProfile = "god-world";

    /// <summary>
    /// The god camera's profile under <c>--god-region</c>: the client's own hull (<c>ClientRegion</c>), a near budget, and an aggregate of the rest — the
    /// shape AC-3 measures (design/Subscriptions/09 § 17). Declared instead of <see cref="GodProfile"/>, never beside it.
    /// </summary>
    public const string GodRegionProfile = "god-region";

    /// <summary>
    /// A player session in the god camera (<see cref="Overview"/>): <see cref="GodProfile"/>'s whole world, anchored on the avatar so the session stays in its
    /// avatar's realm.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A profile of its own, not the god camera's.</b> A god session's realm is placed by the application (<c>ViewRealm</c>); a player's follows its body
    /// through doors and shuttles. <c>AroundControlled</c> on a <c>World</c> observer names the realm only (12-realms § 1.5), which is exactly that: the god
    /// camera's view of the planet the player stands on, with the avatar still bound and still steered.
    /// </para>
    /// <para>
    /// <b>No new visibility.</b> Anyone may open an anonymous god session and see all of this already, so a logged-in player is not given anything the
    /// server was hiding. What it costs is a god session's bandwidth, which is why it counts against <c>--max-spectators</c> while it is on.
    /// </para>
    /// </remarks>
    public const string OverviewProfile = "overview";

    /// <summary><see cref="OverviewProfile"/> under <c>--god-region</c>: <see cref="GodRegionProfile"/>'s shape, anchored on the avatar.</summary>
    public const string OverviewRegionProfile = "overview-region";

    /// <summary>The session kind a client names in <c>HELLO</c>.</summary>
    public const string GodKind = "god";

    /// <summary>
    /// A player's profile: a disc around the session's player, over the archetypes that move — players, city NPCs and creatures, without the scenery.
    /// </summary>
    public const string PlayerProfile = "player-lite";

    /// <summary>
    /// What a session with something to RENDER is served: <see cref="PlayerProfile"/>'s shape plus the buildings (A7).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A player who cannot see buildings is standing in an empty desert that happens to stop them walking.</b> The world's towns
    /// are <c>WorldObject</c>s, and <see cref="PlayerProfile"/> does not carry them — correctly, for what it was built for: its
    /// sessions were headless measurement clients that rendered nothing, and the comment on <see cref="SpectateProfile"/> explains
    /// at length why an extra archetype on a MOVING sphere is not free. So the buildings go on a profile of their own and the
    /// measurement profile keeps its numbers.
    /// </para>
    /// <para>
    /// <b>The cost is accepted and is written down rather than discovered later:</b> about 0.7 extra cell-queries per tick per
    /// moving session, because the crescent sweep queries every archetype in the set over every cell the sphere newly covers. For a
    /// human that is the right trade — they are one per person, not one per population.
    /// </para>
    /// <para>
    /// <b>Identical in content to <see cref="SpectateProfile"/>, and NOT merged with it.</b> They are the same shape for different
    /// reasons: a spectator rides somebody else's entity and a player drives its own, and <c>Spectate</c>'s own commentary turns on
    /// what <c>Control</c> leaks to a watcher. The day a <c>Player</c> declares an <c>[Owner]</c> field those two diverge — the
    /// spectator must stop seeing it and the player must not — and one shared name would make that a rename under pressure.
    /// </para>
    /// </remarks>
    public const string HumanProfile = "human";

    /// <summary>
    /// Which profile a <c>player</c> session is given: <see cref="HumanProfile"/> by default, <see cref="PlayerProfile"/> for a measurement run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It is one setting for every player session, and that is forced by what a bot is.</b> Phase B's bots connect as
    /// <c>player</c> and the server must not be able to tell them from a person, so it cannot serve them a cheaper profile — the
    /// moment it could, the thing that distinguishes them would be the profile they got. So either everybody pays for buildings or
    /// nobody does.
    /// </para>
    /// <para>
    /// <b>The default is to pay, because the browser is what the phase is for.</b> The switch exists for the measurement runs whose
    /// published CPU numbers were all taken on <see cref="PlayerProfile"/>: changing the profile under them would make every figure
    /// before this change incomparable with every figure after it, and that is a worse outcome than one flag. A run that sets it is
    /// stating that it wants the old shape, which is exactly what a comparison needs.
    /// </para>
    /// <para>
    /// The honest long answer is that detail level belongs to the CLIENT — a real client says what it renders, and a bot asking for
    /// no buildings is indistinguishable from a person on low settings. That needs a field in <c>HELLO</c>, which is A8's wire and
    /// not this.
    /// </para>
    /// </remarks>
    public static string PlayerProfileName { get; set; } = HumanProfile;

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
    public const double PlayerRadiusM = 192d;

    /// <summary>How far a session with a RENDERER is served, in metres: the radius a connecting player gets before touching the slider.</summary>
    /// <remarks>
    /// <para>
    /// <b>Separate from <see cref="PlayerRadiusM"/> because the two serve different things.</b> That one is a headless
    /// bot's sphere and the floor of what a session may ask for, and it is also what the demo's CPU figures are taken
    /// on — raising it would multiply every bot's replicated set by the square of the change (1 024 against 192 is
    /// 28x the area) and make every number before the change incomparable with every number after it. A renderer, on
    /// the other hand, is looking at a horizon: at 192 m a building three streets away does not exist, which reads as
    /// the city popping into being as you walk rather than as a view distance.
    /// </para>
    /// <para>
    /// Inside <see cref="PlayerMaxRadiusM"/>'s 1 280 m, so the window the engine sizes at <c>Start</c> is unchanged and
    /// this costs nothing it was not already paying: the profiles that carry it already declare the maximum, and the
    /// window is sized for the maximum rather than for the current radius.
    /// </para>
    /// </remarks>
    public const double HumanRadiusM = 1024d;

    /// <summary>The widest a player may set its own sphere to, in metres (<c>SetViewRadius</c>).</summary>
    /// <remarks>
    /// <para>
    /// <b>Five cells, because that is the most the engine's window bound allows and not because the number is a nice
    /// one.</b> A sphere session's gather window is <c>2⌈R / c⌉ + 5</c> cells per axis and may be at most 15 in a flat
    /// grid (<c>W² ≤ 2 809</c>, <c>ReplicationGrid.MaxWindowCells</c>), so <c>⌈R / c⌉ ≤ 5</c> — at
    /// <see cref="ReplicationCellM"/>'s 256 m, <b>1 280 m</b>. Declaring more throws at <c>Start</c> with a message
    /// naming the cell side it would need, which is the engine telling you to make this a deliberate decision rather
    /// than a typo. Written as an expression so the two cannot drift.
    /// </para>
    /// <para>
    /// <b>The ceiling is five of whatever <see cref="ReplicationCellM"/> is</b>, so it moved from 320 m to 1 280 m when
    /// that cell went from 64 m to 256 m to match the spatial grid. <b>Measured, the wider ceiling is nearly free</b>
    /// (<c>ReplicationWindowBench</c>, sweep F, 2026-10-14, same session served 192 m, only the declared maximum moving):
    /// a 320 m ceiling is 81 window cells and 14.3 µs per tick of gather, 640 m is 121 cells and 14.5 µs, 1 280 m is 225
    /// cells and 15.7 µs. The window's cells cost about 5 ns each; what costs is the cell SIZE, not how many.
    /// </para>
    /// <para>
    /// <b>Declaring a max at all still has a price, paid by every human session.</b> <c>Sphere(..., max:)</c> sizes the
    /// window at <c>Start</c> for the maximum, not for the current radius — 15 cells per axis instead of the 9 that
    /// 192 m needs at this cell, so 225 window cells instead of 81, which the figures above price at 1.4 µs per tick.
    /// It lands on <see cref="HumanProfile"/> and <see cref="SpectateProfile"/> only, never on
    /// <see cref="PlayerProfile"/>, which is the one the demo's CPU numbers are taken on: a bot renders nothing and has
    /// no slider.
    /// </para>
    /// </remarks>
    public static double PlayerMaxRadiusM => ReplicationCellM * 5d;

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
    /// <c>ACKS</c> reason: a <see cref="SetViewRadius"/> outside the range this profile allows.
    /// </summary>
    /// <remarks>
    /// Refused and told, never clamped — the same reasoning as a speed class this server does not grant. A clamp would
    /// let a slider sit at 2 km while the session was served 320 m, with the client drawing a near-radius ring at a
    /// figure nothing honours and no way to find out. The client reads the range from <c>/typhon/demo.json</c>, so a
    /// refusal means the two disagree about the server, which is worth hearing about.
    /// </remarks>
    public const byte ViewRadiusRefused = AckReasons.FirstApplicationReason + 2;

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

    /// <summary><c>ACKS</c> reason: a <see cref="BoardShuttle"/> from a player that is not standing at a shuttleport.</summary>
    /// <remarks>
    /// Refused rather than turned into a walk: a client that asked to fly and got a walk instead would have no way to tell the difference from a shuttle
    /// that is simply away, and the bot brain comparing routes needs to know which of the two happened.
    /// </remarks>
    public const byte ShuttleNotAtPort = AckReasons.FirstApplicationReason + 3;

    /// <summary><c>ACKS</c> reason: a <see cref="BoardShuttle"/> naming a city that does not exist, or the one it is standing in.</summary>
    public const byte ShuttleBadDestination = AckReasons.FirstApplicationReason + 4;

    /// <summary><c>ACKS</c> reason: an <see cref="Overview"/> from a player whose avatar is not on a planet, or that has no avatar yet.</summary>
    public const byte OverviewNotOnPlanet = AckReasons.FirstApplicationReason + 5;

    /// <summary><c>ACKS</c> reason: an <see cref="Overview"/> while <c>--max-spectators</c> is full.</summary>
    public const byte OverviewFull = AckReasons.FirstApplicationReason + 6;

    /// <summary><c>ACKS</c> reason: a <see cref="SetMount"/> from a player that is not on a planet — a room, a dungeon — or is waiting for a shuttle.</summary>
    public const byte MountNotHere = AckReasons.FirstApplicationReason + 7;

    /// <summary><c>ACKS</c> reason: a <see cref="SetStance"/> naming a stance this server does not know.</summary>
    public const byte StanceUnknown = AckReasons.FirstApplicationReason + 8;

    /// <summary><c>ACKS</c> reason: a move, a mount or a boarding from a player that is down — knocked out or dead — and cannot.</summary>
    public const byte PlayerDown = AckReasons.FirstApplicationReason + 9;

    /// <summary>How close to a shuttleport a player must stand to board, in metres.</summary>
    /// <remarks>
    /// Measured from the port's centre: its ring wall stands about 20 m out and its ticket terminal 21.4 m, so 30 m takes in the terminal and the gate
    /// just outside it (passengers land 27.2 m out), but not the dome's diagonal corners (32.7 m). The client reads it from demo.json.
    /// </remarks>
    public const double ShuttleBoardM = 30d;

    /// <summary>The simulation's ports and cities, for validating a boarding ask; <see langword="null"/> when the server runs without shuttles.</summary>
    public static IShuttleBoarding ShuttleBoarding { get; set; }

    // There is deliberately no "you are riding" refusal code. Asking for a realm while riding ENDS the ride and goes there — see the ViewRealm loop. The
    // first version refused it instead, which needed the application to know whether the engine considered the session anchored; it got that wrong once, in
    // the tick between asking for the god profile and the prologue applying it, and the wrong answer was a throw on the tick thread.

    /// <summary>The realm kind of a building's interior (Realms G3): a one-cell realm, served whole.</summary>
    public const string InteriorKind = "interior";

    /// <summary>The realm kind of space (Realms G3): a deep realm at its own cell.</summary>
    public const string SpaceKind = "space";

    /// <summary>
    /// The replication grid's cell side (<see cref="SubscriptionsOptions.ReplicationCellM"/>): the planet's own spatial cell, set from
    /// <c>SimConfig.ResolveCellSize</c> before the realms are registered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A deliberate deviation from the engine's documented rule of thumb</b> (09 § 4: <c>c ≈ R′/3</c> of the profile with the most sessions, which would
    /// be 64 m here), <b>because a replication cell smaller than the SPATIAL cell is paid for on every delivery.</b> Delivering a replication cell queries
    /// the spatial index over that cell's box, so when four replication cells sit inside one spatial cell, that spatial cell is opened four times to answer
    /// questions about a quarter of itself each.
    /// </para>
    /// <para>
    /// Measured (<c>ReplicationWindowBench</c>, sweep E, 2026-10-14, 7950X, Release, two runs each in its own process, this demo's own geometry — a session
    /// served 192 m with a 320 m ceiling, 113 entities held, 1.18 records per tick, identical in every row):
    /// </para>
    /// <list type="table">
    /// <item><term>64 m (4:1)</term><description>33.4 µs per tick of gather</description></item>
    /// <item><term>128 m (2:1)</term><description>20.8 µs</description></item>
    /// <item><term>192 m (1.33:1)</term><description>16.1 µs</description></item>
    /// <item><term>256 m (1:1)</term><description>13.8 µs</description></item>
    /// <item><term>320 m (0.8:1)</term><description>14.2 µs — coarser than the spatial cell stops helping</description></item>
    /// </list>
    /// <para>
    /// So the rule is <b>match the spatial cell</b>, not "as coarse as possible": past 1:1 one query straddles several spatial cells and the curve turns
    /// back up. The window's own cell count is nearly free by comparison — 5 ns per cell per tick, measured on an empty world — which is why the wider
    /// ceiling this buys (<see cref="PlayerMaxRadiusM"/>, five cells) costs 1.4 µs rather than the 2.4x the cell side itself is worth.
    /// </para>
    /// <para>
    /// <b>Not a const, because the spatial cell is not one:</b> <c>SimConfig.ResolveCellSize</c> scales with <c>--content-scale</c> and is overridden by
    /// <c>--cell</c>, which the sweeps move. Following it keeps the ratio at 1:1 in every configuration instead of only the default one.
    /// </para>
    /// </remarks>
    public static double ReplicationCellM = 256d;

    /// <summary>The teleport threshold, in metres per second: the fastest anything on Tatooine moves, plus headroom.</summary>
    /// <remarks>
    /// <para>
    /// It sizes the motion codec: the teleport threshold is what separates "it moved" from "it was put somewhere else", and the velocity width is derived from
    /// it together with the tick period. Declaring it too high wastes a bit per segment; too low turns a sprint into a teleport.
    /// </para>
    /// <para>
    /// <b>The 5 % headroom over the fastest gait dates from when declaring the true maximum was broken.</b> The engine's test compared
    /// <c>step²</c> against <c>(TeleportMaxSpeedMps × tickPeriod)²</c> over QUANTIZED positions with no margin, so an entity at exactly the declared maximum
    /// tipped over the boundary on about half its ticks; every tip was reported as a teleport, and a mounted player stood still for 112 frames of 180 and then
    /// jumped at 86 m/s. The engine now adds two position-quantum diagonals to the threshold (#1074), so the true maximum is safe; the headroom stays until a
    /// browser-client run confirms dropping it.
    /// </para>
    /// <para>
    /// <b>It is COMPUTED from the fastest thing a player can ask for, and that is the whole point of the expression.</b> It was 12.6, a hand-written 5 % over
    /// the mount's 12 — so the day a gait went past 12 m/s, every segment of a player using it would have read as a teleport and the symptom would have been
    /// the stutter above, in a part of the code that says nothing about speeds. The top is now the swoop at the largest boost, 160 m/s: 168 with the
    /// headroom, 2.2x the 75.6 m/s that measured 12 387 of a 16-bit velocity code's 32 767 at 50 Hz (<c>TatooineData.PlayerGaitScale</c>), so still one width.
    /// </para>
    /// <para>
    /// <b>What it costs is real and is the price of the fastest gait.</b> The threshold sizes the velocity codec
    /// (<c>ProjectionCompiler.VelocityCodec</c>: codes = maxSpeed × tickPeriod ÷ unit, then the next width up the ladder) and the teleport test's
    /// sensitivity. Doubling it doubles the codes a velocity component may need, which can cost a width step for EVERY moving entity in the world, and
    /// halves how sharp a jump has to be before the engine calls it a teleport rather than motion. A demo that wants to cross 16 km quickly pays that; a
    /// server that wanted the detector tight would cap the ladder instead.
    /// </para>
    /// </remarks>
    internal const double MaxSpeedMps = TatooineData.PlayerSwoopSpeedMps * TatooineData.PlayerGaitScale * SpeedClasses.MaxBoost * 1.05;

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

    /// <summary>
    /// The engine's session table size (<c>--max-sessions</c>), shared by both roles; 0 leaves the refusal to the engine.
    /// </summary>
    /// <remarks>
    /// <b>Checked here so that reaching it SAYS something.</b> The engine refuses admission past its table at connect time, which is
    /// correct and cheap, but it is silent: a fleet asking for 10 000 against the default 8 192 opened 8 192, stopped, and reported
    /// "8 191 of 8 191 opened" — no close reason, no server line, nothing naming the limit. Twenty minutes went into looking for a
    /// socket or a thread ceiling that was never there.
    /// <para>
    /// This guard refuses the same connect one step earlier, with <see cref="HouseFullCloseCode"/> and a message that names the
    /// table, and it moves <see cref="RefusedFull"/>, so the count appears in the periodic report. It can refuse ONE early relative
    /// to the engine, because the engine's table also holds pending resumes that this count does not see; that is the harmless
    /// direction, and it is why this is a diagnostic rather than the authority.
    /// </para>
    /// </remarks>
    public static int SessionTableSize { get; set; }

    /// <summary>The close code a session refused for a full house carries; the application range starts at 4100.</summary>
    public const ushort HouseFullCloseCode = 4101;

    /// <summary>The close code a session kicked by <see cref="RequestShutdown"/> carries.</summary>
    public const ushort ShutdownCloseCode = 4100;

    /// <summary>
    /// The close code a <c>player</c> session carrying no valid token is refused with (A3).
    /// </summary>
    /// <remarks>
    /// Its own code rather than reusing <see cref="HouseFullCloseCode"/>, because a client must be able to tell "come back
    /// later" from "log in again": the first is worth a retry on the same credentials and the second never is. A browser that
    /// cannot distinguish them either retries forever against a rejected token or gives up on a full server.
    /// </remarks>
    public const ushort NotAuthenticatedCloseCode = 4102;

    /// <summary>
    /// How many <c>player</c> admissions were refused for want of a valid token. Read by a test and by the stats page.
    /// </summary>
    public static long RefusedUnauthenticated => System.Threading.Interlocked.Read(ref _refusedUnauthenticated);

    private static long _refusedUnauthenticated;

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

    /// <summary>The refusal count the report has already announced, so a run sitting at the ceiling says so once rather than every report.</summary>
    private static long _refusedFullSaid;

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

    /// <summary>
    /// The <c>netId</c> a session's client knows an entity by, or <c>0</c> when it cannot see it.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="entity">The entity to name.</param>
    /// <returns>The identity, or <c>0</c>.</returns>
    /// <remarks>
    /// <para>
    /// <b>The inverse of what a command does, and the demo has wanted it since A11.</b> Everything else here goes the other way — a
    /// client names a <c>netId</c> and the server resolves it. Naming an entity TO a client is what a GM tool, a tutorial pointing at a
    /// quest giver, or a test driving a command at a specific entity all need.
    /// </para>
    /// <para>
    /// <b>Read through the cached commands surface, exactly as <see cref="SubjectOf"/> is.</b> Both read committed replication state —
    /// what the session's last published frame carried — which is the state a system reads and is stable between ticks. It answers 0
    /// before the first tick of a run, because there is no surface to ask yet.
    /// </para>
    /// </remarks>
    public static uint NetIdOf(SessionId session, EntityId entity)
        => _pushCommands != null && _pushCommands.TryNetIdOf(session, entity, out var netId) ? netId : 0u;

    /// <summary>The sessions whose subject died this tick, collected before any is released because releasing writes to <see cref="Spectators"/>.</summary>
    private static readonly List<SessionId> Dead = [];

    /// <summary>The profile a camera session gets: the region shape when one is configured, and the whole world otherwise.</summary>
    /// <remarks>
    /// One property because it is asked twice — when a session opens, and when a ride is released — and a session that came back on a different profile
    /// than it opened with would be a difference nobody would look for.
    /// </remarks>
    private static string GodProfileName => GodRegionMaxEdgeM > 0 ? GodRegionProfile : GodProfile;

    /// <summary>The profile a player in the god camera gets: the god camera's shape, kept on the avatar's realm.</summary>
    private static string OverviewProfileName => GodRegionMaxEdgeM > 0 ? OverviewRegionProfile : OverviewProfile;

    /// <summary>The player sessions in the god camera (<see cref="Overview"/>). Tick-thread only.</summary>
    private static readonly HashSet<SessionId> Overviewing = [];

    /// <summary>How many sessions are in <see cref="Overviewing"/>, published for the admission hook, which runs on the transport thread.</summary>
    private static int _liveOverviews;

    /// <summary>
    /// The radius each player session last asked for with <see cref="SetViewRadius"/>, so leaving the god camera gives it back.
    /// </summary>
    /// <remarks>
    /// <b>The engine forgets it on purpose:</b> a radius "holds until the session's profile changes, which returns it to the new profile's own radius"
    /// (<c>SetRadius</c>). An overview is two profile changes, so without this a player who widened its view to 1 280 m came back at 1 024. Tick-thread only.
    /// </remarks>
    private static readonly Dictionary<SessionId, double> AskedRadius = [];

    /// <summary>
    /// Sessions that left the god camera this tick and get their radius back next tick, once the prologue has applied the sphere profile again.
    /// </summary>
    /// <remarks>
    /// <c>SetRadius</c> is checked against the profile applied NOW and throws for one that is not a Sphere; on the tick thread a throw is a dead tick. So the
    /// radius waits one tick, as <see cref="Releasing"/> does and for the same reason. Tick-thread only.
    /// </remarks>
    private static readonly List<SessionId> RadiusToRestore = [];

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

        // Before the role caps, because the session table is shared by both roles and is the limit a crowd actually reaches: --max-clients
        // and --max-spectators are 0 (unlimited) in every run anybody makes, and the table is 8 192 whether or not anyone chose it.
        if (SessionTableSize > 0)
        {
            var live = System.Threading.Volatile.Read(ref _liveClients) + System.Threading.Volatile.Read(ref _liveSpectators);
            var pending = System.Threading.Volatile.Read(ref _pendingClients) + System.Threading.Volatile.Read(ref _pendingSpectators);
            if (live + pending >= SessionTableSize)
            {
                return Full("session table", SessionTableSize);
            }
        }

        var player = string.Equals(request.Kind, PlayerKind, StringComparison.Ordinal);
        if (player)
        {
            // A3: a player session IS its account, so the token is checked before the capacity reservation — refusing after reserving would leak a pending
            // slot on every unauthenticated attempt, which is a cap this hook cannot get back without a tick.
            if (!AuthToken.TryValidate(request.Token, out var account))
            {
                System.Threading.Interlocked.Increment(ref _refusedUnauthenticated);
                return Admission.Reject(NotAuthenticatedCloseCode, "log in at /auth/login and present the token on HELLO");
            }

            // The byte budget stays with BindOpenedSessions' SetBudget rather than moving into these limits. Both would work, and setting it in two places
            // would mean two numbers to keep equal — and that one is what the Phase 2 criteria measure with.
            //
            // The account rides through as AppData, which is what the session system reads on SessionEventKind.Opened to know WHICH avatar to bind (A5).
            // Carrying it here rather than looking the token up again later is the point: the hook is where the token was proved, and proving it twice would
            // be two places that could disagree.
            return TryReserve(ref _pendingClients, System.Threading.Volatile.Read(ref _liveClients), MaxClients)
                ? Admission.Accept(SessionRole.Player, null, new PlayerIdentity(account))
                : Full("player", MaxClients);
        }

        // A god session stays ANONYMOUS and carries no AppData. It is read-only by role — SessionRole.Spectator — and every game intent is Roles(Player), so
        // there is nothing for an identity to authorise. Requiring a token here would mean a login screen in front of the god camera, which is the one tool
        // that has to work on a box nobody has an account on.
        // Players in the god camera count too: they cost what a god session costs (see Overview).
        var spectating = System.Threading.Volatile.Read(ref _liveSpectators) + System.Threading.Volatile.Read(ref _liveOverviews);
        return TryReserve(ref _pendingSpectators, spectating, MaxSpectators)
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
        _refusedFullSaid = 0;
        System.Threading.Interlocked.Exchange(ref _refusedUnauthenticated, 0);
        ResetAvatarAccounting();
        BoundPlayer.Clear();
        Unbound.Clear();
        Seen.Clear();
        Retired.Clear();
        System.Threading.Interlocked.Exchange(ref _possessions, 0);
        System.Threading.Interlocked.Exchange(ref _releases, 0);
        System.Threading.Interlocked.Exchange(ref _kicksStaged, 0);
        Kicked.Clear();
        Spectators.Clear();
        Releasing.Clear();
        Dead.Clear();
        Overviewing.Clear();
        System.Threading.Volatile.Write(ref _liveOverviews, 0);
        System.Threading.Interlocked.Exchange(ref _overviewsRefused, 0);
        AskedRadius.Clear();
        RadiusToRestore.Clear();
        MaxClients = 0;
        MaxSpectators = 0;
        PlayerProfileName = HumanProfile;
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

    /// <summary>The group set naming a city's structures: its shuttleport, terminals, buildings and props.</summary>
    /// <remarks>
    /// <b>Why a set and not a wider sphere.</b> Mos Eisley's radius is 456 m against a player's 192 m sphere, so a player at one edge of the city cannot see
    /// the shuttleport at the other — and a bot's routing needs the port's position to decide between walking and flying. Widening the sphere to 456 m sends
    /// 2.8x the bytes for every session everywhere; the set delivers a city's facilities only to the sessions actually in one.
    /// </remarks>
    public const string CitySet = "city";

    /// <summary>How close to a city's edge a session must be to be served its structures, in metres.</summary>
    /// <remarks>
    /// Distance is measured to the circle's nearest point and is 0 inside it, so this is a margin outside the city wall rather than a radius. 64 m is about
    /// three seconds at a run, which is enough for the facilities to have arrived by the time a player is among the buildings.
    /// </remarks>
    public const double CityReachM = 64d;

    /// <summary>The hysteresis on <see cref="CitySet"/>: how much further a session must leave before the city is released, in metres.</summary>
    /// <remarks>
    /// Without it a session loitering on the boundary joins and leaves the set every few ticks, and each re-entry refills the whole city. 32 m on a 64 m
    /// reach is half again, which costs a session that leaves for good one extra city's worth of held state for the few seconds it takes to walk clear.
    /// </remarks>
    public const double CityLeaveM = 32d;

    /// <summary>How many group keys one session may hold at once (<c>SubscriptionsOptions.MaxGroupsPerSession</c>).</summary>
    /// <remarks>
    /// <b>Required once any set is declared, and it is a per-session allocation rather than a limit that is rarely reached:</b> a session holds this many keys
    /// and cursors per set whether it uses them or not. Four, because the cities are kilometres apart and a session within reach of two at once is already an
    /// edge case; the POI keys share the index space and can have regions later without changing this.
    /// </remarks>
    public const int MaxCityGroupsPerSession = 4;

    /// <summary>Declares everything a client can see.</summary>
    /// <param name="subs">The runtime's registry, before <c>Start</c>.</param>
    /// <param name="automatic">Whether the engine detects changes itself instead of relying on the simulation's <c>Replicate</c> calls (experimental).</param>
    public static void Declare(SubscriptionsRegistry subs, bool automatic = false)
    {
        ArgumentNullException.ThrowIfNull(subs);

        subs.Sessions.Kinds(GodKind, PlayerKind);

        // A city as a group: one CityRegion entity per city carries the key and the disc, and every structure of that city carries the same key in
        // StructureRegion. A session whose sphere comes within CityReachM of the disc is served the whole city and told so once it is complete; it keeps it
        // until it is CityLeaveM further out again. The members are positioned, so a session meets only the cities of the realm it is in.
        //
        // The key is region + 1 on both sides and 0 is no region, because the members' key field is a SPARSE index and a sparse index never files key 0.
        subs.Groups(CitySet, g => g
            .Region<CityRegion>(r => r.Key(CityRegion.Area, a => a.Key).Extent(CityRegion.Area, a => a.Circle))
            .Member<WorldObject>(m => m.Key(WorldObject.Region, s => s.Value))
            .Leave(CityLeaveM));

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

            // A player in the god camera: the same region, on its avatar's realm. Indoors and in space it is the player's own shape, for the tick between
            // the avatar crossing a door and the server ending the overview (Overview).
            subs.Profile(OverviewRegionProfile, p =>
            {
                p.Detection(detection)
                    .ClientRegion(GodRegionMaxEdgeM)
                    .AroundControlled()
                    .Near(GodNearBudget)
                    .Of<Creature>()
                    .Of<CityNpc>()
                    .Of<Player>()
                    .Of<CreatureLair>()
                    .Of<WorldObject>();
                p.Aggregate(GodAggregateTileM, rateHz: 1).Of<Creature>().Of<CityNpc>().Of<Player>();
                p.In(InteriorKind, v => v.World().AroundControlled().Of<Player>().Of<CityNpc>().Of<WorldObject>());
                p.In(SpaceKind, v => v.World().AroundControlled().Of<Player>());
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

            // A player in the god camera: the whole planet its avatar is on. AroundControlled on a World names the realm only (12-realms § 1.5), so the
            // session keeps following its body. Indoors and in space it is the player's own shape, for the tick between the avatar crossing a door and the
            // server ending the overview (Overview).
            subs.Profile(OverviewProfile, p =>
            {
                p.Detection(detection)
                    .World()
                    .AroundControlled()
                    .Of<Creature>()
                    .Of<CityNpc>()
                    .Of<Player>()
                    .Of<CreatureLair>()
                    .Of<WorldObject>();
                p.In(InteriorKind, v => v.World().AroundControlled().Of<Player>().Of<CityNpc>().Of<WorldObject>());
                p.In(SpaceKind, v => v.World().AroundControlled().Of<Player>());
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
                .Of<Creature>()

                // A bot routes on facilities it cannot see from one edge of a city, so it gets the city set even though it carries no WorldObject by
                // geometry: the set's members arrive whether or not the observer names their archetype.
                .Groups(CitySet, CityReachM);
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
                .Sphere(HumanRadiusM, leave: PlayerLeaveM, max: PlayerMaxRadiusM)
                .AroundControlled()
                .Of<Player>()
                .Of<CityNpc>()
                .Of<Creature>()
                .Of<WorldObject>()

                // The lairs, drawn: a creature with no nest beside it reads as a stray. Static, sent once on enter, so its cost is the crescent query the
                // note above describes, paid by watched riders only.
                .Of<CreatureLair>()
                .Groups(CitySet, CityReachM);

            // Free here, unlike on the sphere above: a World observer fills once per realm from a cursor and has no crescent to sweep.
            p.In(InteriorKind, v => v.World().AroundControlled().Of<Player>().Of<CityNpc>().Of<WorldObject>());
            p.In(SpaceKind, v => v.World().AroundControlled().Of<Player>());
        });

        // A7: what a session with something to RENDER is served — the player's own shape plus the buildings. Declared separately from
        // SpectateProfile despite being identical today; see HumanProfile for why they are kept apart, and for the sweep cost this
        // accepts. Declared unconditionally rather than only when PlayerProfileName selects it, because the catalog a client
        // negotiates against is compiled from these declarations: a profile that appeared only in some runs would change the catalog
        // hash between them, and that hash is what a client checks it is talking to the same server with.
        subs.Profile(HumanProfile, p =>
        {
            p.Detection(detection)
                .Sphere(HumanRadiusM, leave: PlayerLeaveM, max: PlayerMaxRadiusM)
                .AroundControlled()
                .Of<Player>()
                .Of<CityNpc>()
                .Of<Creature>()
                .Of<WorldObject>()

                // A lair is what a person walks up to and breaks, so a browser has to be able to see one — and target it, which the server refuses for
                // anything the session was never shown. Bots' PlayerProfile does not carry it: they render nothing and hunt creatures.
                .Of<CreatureLair>()

                // Parity: a browser is served the same city set a bot is, because every capability a bot has has to be reachable from the client.
                .Groups(CitySet, CityReachM);
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
            .Precheck(static (in m) => float.IsFinite(m.X) && float.IsFinite(m.Z))
            .Field(m => m.X, Codec.F32, "x")
            .Field(m => m.Z, Codec.F32, "z"));
        subs.Command<MoveDir>(c => c
            .Coalesce(CommandCoalesce.LatestPerSession)
            .Rate(30, 60)
            .Roles(SessionRole.Player)
            // Finiteness only. The speed class is deliberately NOT checked here: the engine's own contract for a pre-check is that it "rejects the impossible,
            // not the disallowed", and which classes this server grants is policy that lives with the entitlement in TrySpeedFor. Checking it here made the
            // system's refusal counter unreachable by any client — so the code claimed to count something it never could.
            .Precheck(static (in m) => float.IsFinite(m.Heading))
            .Field(m => m.Heading, Codec.F16, "heading")
            .Field(m => m.SpeedClass, Codec.U8, "speedClass")
            .Field(m => m.ChangePhase, Codec.U8, "changePhase")
            .Field(m => m.Boost, Codec.U8, "boost")
            .Field(m => m.Throttle, Codec.U8, "throttle"));
        subs.Command<SetTarget>(c => c
            .Rate(4, 8)
            .Roles(SessionRole.Player)
            .Field(t => t.NetId, Codec.VarUInt, "netId"));

        // How far this session wants to be served. Coalesced latest-per-session for the same reason MoveDir is: dragging a
        // slider produces a stream of values of which only the last is wanted, and coalescing is what makes a drag cost one
        // command instead of sixty. The rate is SetTarget's rather than MoveDir's — a radius is a decision, not a continuous
        // state, and a client changing it four times a second is already more than a person can mean.
        //
        // Metres, not a class, and the pre-check is finiteness only for MoveDir's reason: the range this server allows is
        // policy that lives with the profile declaration, and checking it on the transport thread would make the system's
        // refusal counter unreachable by any client.
        subs.Command<SetViewRadius>(c => c
            .Coalesce(CommandCoalesce.LatestPerSession)
            .Rate(4, 8)
            .Roles(SessionRole.Player)
            .Field(v => v.RadiusM, Codec.VarUInt, "radiusM"));

        // The god camera for a player. Each accepted ask is a profile change, so a whole RESET of the planet: ViewRealm's rate, once a second with a burst
        // of two, which a person toggling a button does not reach.
        subs.Command<Overview>(c => c
            .Rate(1, 2)
            .Roles(SessionRole.Player)
            .Field(v => v.On, Codec.U8, "on"));

        // Queued and slow: boarding is a decision, not a state, and a second ask before the first has been answered is a person pressing the key twice.
        // LatestPerSession would be wrong here — two asks naming two cities are two different intentions and the last one is not obviously the wanted one;
        // the rate limit refuses the second instead, which the client can show.
        subs.Command<BoardShuttle>(c => c
            .Rate(1, 2)
            .Roles(SessionRole.Player)
            .Field(v => v.DestCity, Codec.VarInt, "destCity"));

        // A toggle a person presses; two a second is more than a hand does, and a refused one is counted.
        subs.Command<SetMount>(c => c
            .Rate(2, 4)
            .Roles(SessionRole.Player)
            .Field(v => v.On, Codec.U8, "on"));

        // A toggle a person presses, and only the last one counts: latest per session, at the mount's rate.
        subs.Command<SetStance>(c => c
            .Coalesce(CommandCoalesce.LatestPerSession)
            .Rate(4, 8)
            .Roles(SessionRole.Player)
            .Field(v => v.Stance, Codec.U8, "stance"));

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
            // A5: the session's own avatar, spawned at its character's saved position, rather than the first free player out of the
            // pooled crowd. See TatooineReplication.Avatars.cs for why that distinction is the whole requirement.
            BindAvatars(tick);
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
            // A6's cadence bounds what a CRASH costs. A graceful stop should cost nothing, and it used to cost the same: this branch
            // returns before the avatar binding, so the last thing a Ctrl-C did was kick everybody and leave up to a whole save
            // interval of walking unwritten. Flushed once, on the first shutdown tick, while the avatars still exist — the kicks staged
            // just below are applied by the next tick's prologue, so this is the last tick on which there is anything to read.
            FlushAvatarsForShutdown(tick);

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

                // A5: the account Admit proved, taken from the event because AppData reaches the tick nowhere else. A god session
                // carries none and nothing is recorded, which is what keeps it out of the avatar binding entirely.
                NoteOpenedSession(e.Session, e.AppData);

                var request = subs.Session(e.Session).Profile(player ? PlayerProfileName : GodProfileName);
                if (player && PlayerBudgetBytesPerSecond > 0)
                {
                    request.SetBudget(PlayerBudgetBytesPerSecond);
                }

                // A13: this session resumed one whose link died, and that session's avatar is still standing in the world. Handing it straight back is the
                // whole feature — no character read, no spawn, no login transaction — and it happens here rather than in BindAvatars because a resumed
                // session must never reach the login path at all. A hold that is gone (the grace elapsed, or the account logged in fresh) falls through to
                // the ordinary login, which is the same answer the client would have had without resume.
                if (player && e.ResumedFrom.IsValid && TryRebindHeldAvatar(subs, tick.Transaction, e.Session, e.ResumedFrom))
                {
                    System.Threading.Interlocked.Increment(ref _resumesRebound);
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

                // The same bounded growth, and the overview's place under --max-spectators given back. A resumed session opens on the player profile; its
                // client asks for the god camera again if it is still in it.
                Overviewing.Remove(e.Session);
                AskedRadius.Remove(e.Session);

                // A13: the engine says this link died on its own and is keeping the session's resume record for the grace, so the avatar stays standing
                // instead of being retired by RetireAvatars this tick. The engine then delivers exactly one of Opened{ResumedFrom} or ResumeExpired
                // (SUB-12), and those two are the only things that end the hold — there is no deadline on this side.
                if (e.Resumable)
                {
                    HoldForResume(tick.Transaction, e.Session);
                }
            }
            else if (e.Kind == SessionEventKind.ResumeExpired)
            {
                // Nobody came back. Queued rather than retired here: the save needs the tick's player and character accessors, which BindAvatars rents once
                // for the whole tick (see RetireAvatars).
                //
                // Only when there IS a hold behind it: a god session's close is resumable too, and queueing its expiry would defeat BindAvatars' early-out
                // for a tick in order to run a no-op.
                if (TatooineReplication.HoldsResume(e.Session))
                {
                    ResumesToRetire.Add(e.Session.Value);
                }
                else
                {
                    // <b>Counted, where it used to be dropped on the floor.</b> The test is right — a god session's close is resumable too and queueing
                    // its expiry would defeat BindAvatars' early-out in order to run a no-op — but it made the god case and a lost hold the same silent
                    // non-event. The engine sends exactly one of Opened{ResumedFrom} or ResumeExpired per resumable close (SUB-12), so for a session that
                    // HAD a hold this arriving with none means something else dropped it and an avatar is standing for nobody. Which of the two it is,
                    // the number cannot say by itself; that it happens at all is what no observation on the box could previously establish.
                    TatooineReplication.NoteResumeExpiryWithoutHold(e.Session);
                }
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

        // A7: how far a session asks to be served. A player's control, unlike ViewRealm — a human watching its own avatar is the one who wants a wider view,
        // and the engine's SetRadius is built for exactly this ("a player boarding an aircraft").
        //
        // <b>Validated here and not by the engine's own throw, because this runs on the tick thread.</b> `SetRadius` raises
        // ArgumentOutOfRangeException for a radius outside the profile's range, and a throw out of a system is not a refusal — it is a dead tick. So the range
        // is tested first and a bad ask is refused with a reason the client can show.
        // A radius given back to a session that left the god camera on the tick before, now that the prologue has applied its sphere profile again. Before
        // this tick's own asks, which then apply on top of it.
        if (RadiusToRestore.Count > 0)
        {
            foreach (var session in RadiusToRestore)
            {
                if (!Overviewing.Contains(session) && AskedRadius.TryGetValue(session, out var radius) && radius != 0d)
                {
                    subs.SetRadius(session, radius);
                }
            }

            RadiusToRestore.Clear();
        }

        foreach (var command in subs.Commands<SetViewRadius>())
        {
            var asked = (double)command.Value.RadiusM;

            // 0 is "back to the profile's own", which the engine spells the same way. Anything else must land inside the declared range; the client is told
            // both ends in /typhon/demo.json, so an ask outside it is a client and a server that disagree about which server this is.
            if (asked != 0d && (asked < PlayerRadiusM || asked > PlayerMaxRadiusM))
            {
                subs.Reject(command, ViewRadiusRefused);
                System.Threading.Interlocked.Increment(ref _radiusRefused);
                continue;
            }

            // Kept even when it cannot be applied yet: a session in the god camera is served no sphere right now — SetRadius would throw — and gets this
            // radius when it leaves (see AskedRadius).
            AskedRadius[command.Session] = asked;
            if (Overviewing.Contains(command.Session))
            {
                System.Threading.Interlocked.Increment(ref _radiusApplied);
                continue;
            }

            // The session's own profile must be a Sphere, which a player's is and a god camera's is not — the role filter on the command already keeps
            // spectators out, and this is the second half of that: a session whose profile has not been applied yet would make SetRadius throw.
            if (!subs.SetRadius(command.Session, asked))
            {
                continue;   // closing or gone; nothing to answer to
            }

            System.Threading.Interlocked.Increment(ref _radiusApplied);
        }

        Overviews(subs);

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

    /// <summary>The god camera for players: overviews ended where the avatar left its planet, then this tick's <see cref="Overview"/> asks.</summary>
    /// <param name="subs">The tick's subscriptions surface.</param>
    private static void Overviews(SubscriptionsCommands subs)
    {
        // Ended by the server, before the asks: an avatar that went through a door, or flew to space, is no longer on a planet, and the god camera is a view of
        // a planet's map. RealmOf is the realm the session's last frame committed, which for an AroundControlled session is its avatar's.
        if (Overviewing.Count > 0)
        {
            OverviewsEnding.Clear();
            foreach (var session in Overviewing)
            {
                if (!OnPlanet(subs, session))
                {
                    OverviewsEnding.Add(session);
                }
            }

            foreach (var session in OverviewsEnding)
            {
                EndOverview(subs, session);
            }
        }

        foreach (var command in subs.Commands<Overview>())
        {
            var session = command.Session;
            var on = command.Value.On != 0;

            // Asking for the state the session is already in is answered by doing nothing: a client that reconnected, or toggled twice inside a tick, is
            // not refused for agreeing with the server.
            if (on == Overviewing.Contains(session))
            {
                continue;
            }

            if (!on)
            {
                EndOverview(subs, session);
                continue;
            }

            // No avatar yet means no realm to look at: the session's first frame has not committed one.
            if (!BoundPlayer.ContainsKey(session.Value) || !OnPlanet(subs, session))
            {
                subs.Reject(command, OverviewNotOnPlanet);
                System.Threading.Interlocked.Increment(ref _overviewsRefused);
                continue;
            }

            // Counted against --max-spectators with the god sessions and the reservations admission holds for them, which is the same sum Admit tests.
            var spectating = System.Threading.Volatile.Read(ref _liveSpectators) + System.Threading.Volatile.Read(ref _pendingSpectators) + Overviewing.Count;
            if (MaxSpectators > 0 && spectating >= MaxSpectators)
            {
                subs.Reject(command, OverviewFull);
                System.Threading.Interlocked.Increment(ref _overviewsRefused);
                continue;
            }

            subs.Session(session).Profile(OverviewProfileName);
            Overviewing.Add(session);
        }

        System.Threading.Volatile.Write(ref _liveOverviews, Overviewing.Count);
    }

    /// <summary>Puts a session in the god camera back on the player profile, and queues its radius for the next tick (see <see cref="RadiusToRestore"/>).</summary>
    /// <param name="subs">The tick's subscriptions surface.</param>
    /// <param name="session">A session in <see cref="Overviewing"/>.</param>
    private static void EndOverview(SubscriptionsCommands subs, SessionId session)
    {
        Overviewing.Remove(session);
        var request = subs.Session(session).Profile(PlayerProfileName);
        if (PlayerBudgetBytesPerSecond > 0)
        {
            request.SetBudget(PlayerBudgetBytesPerSecond);
        }

        RadiusToRestore.Add(session);
    }

    /// <summary>Whether a session's committed realm is a planet: realms <c>0 .. Planets - 1</c>, as <c>TatooineSim</c> registers them.</summary>
    /// <param name="subs">The tick's subscriptions surface.</param>
    /// <param name="session">The session.</param>
    /// <returns><see langword="true"/> on a planet.</returns>
    private static bool OnPlanet(SubscriptionsCommands subs, SessionId session)
    {
        var realm = subs.RealmOf(session);
        return !realm.IsNone && realm.Value < Planets;
    }

    /// <summary>The overviews <see cref="Overviews"/> ends this tick, collected first because ending one writes to <see cref="Overviewing"/>.</summary>
    private static readonly List<SessionId> OverviewsEnding = [];

    /// <summary>How many player sessions are in the god camera now, and how many <see cref="Overview"/> asks were refused.</summary>
    public static (int Live, long Refused) OverviewAsks =>
        (System.Threading.Volatile.Read(ref _liveOverviews), System.Threading.Interlocked.Read(ref _overviewsRefused));

    private static long _overviewsRefused;

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

            // <b>Said out loud, because a full table is indistinguishable from a client that stopped connecting.</b> A fleet that asked
            // for more sessions than the table holds gets refused at admission and reports only what it opened, so the ceiling reads as
            // "the ramp stopped" on both sides. Printed once per crossing rather than every report: at 300 ticks a run that sits at the
            // ceiling would otherwise bury everything else.
            var refused = RefusedFull;
            if (refused > _refusedFullSaid)
            {
                _refusedFullSaid = refused;
                Console.Error.WriteLine(
                    $"  !! {refused} session(s) REFUSED for a full house; the session table is {SessionTableSize} (--max-sessions), "
                    + "clients and god cameras share it");
            }

            // What the intent path did (SWG-01). These counters were built "for the report and for the checks beside the demo" — the checks read them, and the
            // report never did, so a served run printed nothing about the one thing a connected client changes. The refusals are printed beside the applications
            // deliberately: "only what the server allows is applied" is a claim about what does NOT happen, and a report that shows only successes cannot carry it.
            var it = Intents;
            Console.Error.WriteLine(
                $"  intents: {it.Applied} applied of {it.Owned} owned, {it.Unowned} unowned, {it.RefusedSpeed} refused for speed; "
                + $"targets {it.TargetsSet} set, {it.TargetsRefused} refused; possession {it.Possessions} taken, {it.Releases} released");

            // <b>The holds, because "taken" minus "released" is not a leak until you know how many are being HELD.</b> A link-dead session's avatar stays
            // standing for the resume grace (A13) and is counted by neither of those two, so a fleet whose sockets had dropped read as 3 001 possessions
            // taken and 0 released — which looks exactly like a leak and was, in part, a report that did not mention the state every one of them was in.
            // `standing` is the live population of holds; `orphan expiries` is zero unless the engine and this demo disagree about who holds what.
            Console.Error.WriteLine(
                $"  resume: {ResumesHeld} held, {ResumesRebound} rebound, {ResumeHoldsExpired} expired, {ResumeHoldsTakenOver} taken over; "
                + $"{ResumeHoldsStanding} standing now, {ResumeExpiriesWithoutHold} orphan expiries");
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

    // PossessPlayers lived here until A5. It handed each session the first FREE player out of the pooled crowd, which is why
    // logging out and back in landed on whatever body happened to be unclaimed — the one thing "log in and be where you were"
    // cannot tolerate. Its replacement spawns a body for the session instead: TatooineReplication.Avatars.cs, BindAvatars.
    //
    // What came with it and did NOT survive: the cluster walk looking for an unheld player, and BoundIds — the set that told a
    // free player from a taken one across that walk. BoundIds went with it. The comment here used to say it was "still written,
    // because ControlledBy's scan and the measurement checks read it", which was not true of either: ControlledBy scans clusters
    // for the possession mark and never touched the set, and no check referenced it. It was written in three places and read in
    // none, which is the kind of residue that reads as load-bearing to the next person.


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

    /// <summary>Boarding asks queued for the next shuttle.</summary>
    private static long _boardingsQueued;

    /// <summary>Boarding asks refused — not at a port, or a destination that does not exist.</summary>
    private static long _boardingsRefused;

    /// <summary>Queued boardings a client walked out of before the shuttle came.</summary>
    private static long _boardingsCancelled;

    /// <summary>What the boarding command did, cumulatively: asks queued, asks refused, and queues abandoned by walking away.</summary>
    public static (long Queued, long Refused, long Cancelled) Boardings
        => (System.Threading.Interlocked.Read(ref _boardingsQueued), System.Threading.Interlocked.Read(ref _boardingsRefused),
            System.Threading.Interlocked.Read(ref _boardingsCancelled));

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
            if (!TryOpenControlled(subs, accessor, command.Session, out var player) || RefusedDown(subs, command, in player))
            {
                continue;
            }

            var move = player.Read(Player.Move);
            move.DestX = Math.Clamp(command.Value.X, -half, half);
            move.DestZ = Math.Clamp(command.Value.Z, -half, half);

            // The run the server GRANTS, through the gait ladder like MoveDir's: the raw figure was half of it, so a click walked at the speed a held
            // key runs at half — and a bot walking a city by clicks crossed it at a jog.
            move.SpeedMps = SpeedFor(SpeedClasses.Run);
            CancelBoardingIfWaiting(ref player);

            // No phase: a click names a destination, and nothing about WHEN the pointer went down changes where the server is asked to walk to. The split
            // exists for a held key, whose duration is the whole of what it means.
            SteerTo(ref move, ref player, subs, 0f, tick.TickNumber);
            player.Set(Player.Move, move);
            System.Threading.Interlocked.Increment(ref _intentsApplied);
        }

        // Boarding. Everything here is re-validated against the state the ask has to agree with, as every other intent is: the wire carries a city index
        // and nothing else, and whether this player is standing at a port is a fact about the world rather than about the message.
        foreach (var command in subs.Commands<BoardShuttle>())
        {
            if (!TryOpenControlled(subs, accessor, command.Session, out var player) || RefusedDown(subs, command, in player))
            {
                continue;
            }

            var boarding = ShuttleBoarding;
            var dest = command.Value.DestCity;
            if (boarding == null || (uint)dest >= (uint)boarding.CityCount)
            {
                // No boarding geometry at all means the server is running without shuttles, which is a destination that does not exist rather than a
                // player in the wrong place.
                subs.Reject(command, ShuttleBadDestination);
                System.Threading.Interlocked.Increment(ref _boardingsRefused);
                continue;
            }

            var at = player.Read(Player.Bounds);
            if (!boarding.TryPortAt(at.X, at.Z, player.Read(Player.Realm).Value, ShuttleBoardM, out var from))
            {
                subs.Reject(command, ShuttleNotAtPort);
                System.Threading.Interlocked.Increment(ref _boardingsRefused);
                continue;
            }

            if (dest == from)
            {
                subs.Reject(command, ShuttleBadDestination);
                System.Threading.Interlocked.Increment(ref _boardingsRefused);
                continue;
            }

            // Queued where the simulation's own passengers queue, so the flight is the same code: ShuttleTick boards whoever is waiting when the shuttle is
            // down. Stopped moving first — a passenger still walking would drift out of the port between the ask and the landing.
            var state = player.Read(Player.State);
            state.Activity = PlayerActivity.AwaitingShuttle;
            state.ActivityTicks = 0;
            state.ShuttleFrom = from;
            state.ShuttleDest = dest;

            // Nobody boards with a swoop, or a drawn weapon: back on foot and idle, and the client draws it so the moment it queues.
            state.Mounted = 0;
            state.Stance = Stances.None;
            player.Set(Player.State, state);

            var waiting = player.Read(Player.Move);
            waiting.VelX = 0f;
            waiting.VelZ = 0f;
            waiting.SpeedMps = 0f;
            waiting.DestX = at.X;
            waiting.DestZ = at.Z;
            player.Set(Player.Move, waiting);
            Replicate(in player);
            System.Threading.Interlocked.Increment(ref _boardingsQueued);
        }

        foreach (var command in subs.Commands<SetMount>())
        {
            if (!TryOpenControlled(subs, accessor, command.Session, out var player) || RefusedDown(subs, command, in player))
            {
                continue;
            }

            var state = player.Read(Player.State);
            var on = command.Value.On != 0 ? 1 : 0;
            if (on == 1 && (player.Read(Player.Realm).Value >= Planets || state.Activity == PlayerActivity.AwaitingShuttle))
            {
                subs.Reject(command, MountNotHere);
                System.Threading.Interlocked.Increment(ref _mountsRefused);
                continue;
            }

            if (state.Mounted != on)
            {
                state.Mounted = on;

                // Riding is idle: the stance goes with the mount, as the client's 1 key drops it.
                if (on == 1)
                {
                    state.Stance = Stances.None;
                }

                player.Set(Player.State, state);
                if (on == 0)
                {
                    // Off the swoop at its speed: the body keeps the destination it had, at a run, rather than coasting at forty metres a second on foot.
                    var move = player.Read(Player.Move);
                    move.SpeedMps = MathF.Min(move.SpeedMps, SpeedFor(SpeedClasses.Run));
                    player.Set(Player.Move, move);
                }

                Replicate(in player);
                System.Threading.Interlocked.Increment(ref _mountsApplied);
            }
        }

        foreach (var command in subs.Commands<SetStance>())
        {
            if (!TryOpenControlled(subs, accessor, command.Session, out var player))
            {
                continue;
            }

            var stance = command.Value.Stance;
            if (stance >= Stances.Count)
            {
                subs.Reject(command, StanceUnknown);
                System.Threading.Interlocked.Increment(ref _stancesRefused);
                continue;
            }

            var state = player.Read(Player.State);
            if (state.Stance == stance)
            {
                continue;
            }

            state.Stance = stance;
            if (stance != Stances.None && state.Mounted != 0)
            {
                // Off the swoop to fight, at a run at most, as SetMount gets off it.
                state.Mounted = 0;
                var move = player.Read(Player.Move);
                move.SpeedMps = MathF.Min(move.SpeedMps, SpeedFor(SpeedClasses.Run));
                player.Set(Player.Move, move);
            }

            player.Set(Player.State, state);
            Replicate(in player);
            System.Threading.Interlocked.Increment(ref _stancesApplied);
        }

        foreach (var command in subs.Commands<MoveDir>())
        {
            if (!TryOpenControlled(subs, accessor, command.Session, out var player) || RefusedDown(subs, command, in player))
            {
                continue;
            }

            if (!TrySpeedFor(command.Value.SpeedClass, player.Read(Player.State).Mounted != 0, command.Value.Boost, out var speed))
            {
                System.Threading.Interlocked.Increment(ref _intentsRefusedSpeed);
                continue;
            }

            CancelBoardingIfWaiting(ref player);

            // A heading is turned into a destination one second of travel away rather than into a velocity, so that this and MoveTo leave the player in the
            // same state and PlayerMove needs to know nothing about which one sent it. A held key re-sends every frame, and each one renews the second.
            if (command.Value.Throttle != 0)
            {
                speed *= command.Value.Throttle / 255f;
            }

            var move = player.Read(Player.Move);
            var place = player.Read(Player.Bounds);
            move.SpeedMps = speed;
            var reach = MathF.Max(speed, 1f);
            move.DestX = Math.Clamp(place.X + (MathF.Cos(command.Value.Heading) * reach), -half, half);
            move.DestZ = Math.Clamp(place.Z + (MathF.Sin(command.Value.Heading) * reach), -half, half);

            // A14b. Clamped rather than refused: a phase is a measurement of the client's own clock against a grid it estimates, so a nonsensical one is a
            // bad estimate and not a protocol violation — and 255ths cannot leave the range anyway. See MoveDir.ChangePhase for why it is safe to trust.
            SteerTo(ref move, ref player, subs, command.Value.ChangePhase / 255f, tick.TickNumber);
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
            //
            // A11 adds the second half: resolvable is not the same as ALIVE. A netId stays resolvable for as long as the session's view holds it, so a client
            // that clicks the moment something dies — or replays the click — names a corpse, and the combat system would then carry a target it can never
            // resolve a shot against. Refused here, where the refusal is counted and the client is told, rather than silently dropped per shot downstream.
            if (subs.TryResolve(command.Session, command.Value.NetId, out var target) && IsAliveTarget(tx, target))
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

    /// <summary>Takes a player out of the shuttle queue when its client asks to move instead.</summary>
    /// <param name="player">The body, already opened for writing; by ref because <c>Set</c> updates the handle.</param>
    /// <remarks>
    /// <para>
    /// <b>Boarding and walking are the same client's two answers, and the later one wins.</b> The boarding handler zeroes the body's motion and the comment
    /// there says "stopped moving first", but the MoveDir loop is drained AFTER it in the same <c>ApplyIntents</c> and used to touch nothing but the motion:
    /// a client holding a direction key re-set its destination on the same tick, walked away from the port for a whole shuttle interval, and was still
    /// teleported to the destination when the shuttle came. The queue is the thing that has to go, not the movement — refusing the walk instead would freeze
    /// an avatar for up to two minutes with no way to say "never mind", which is the worse answer to give a person holding a key.
    /// </para>
    /// <para>
    /// <c>MoveTo</c> is drained before the boarding loop and <c>MoveDir</c> after it, so a click in the same tick as a board loses to the board and a held
    /// key beats it. That asymmetry is the engine's command order rather than a decision here, and it lands the right way round: an explicit boarding is the
    /// later intent in the first case and the abandoned one in the second.
    /// </para>
    /// </remarks>
    private static void CancelBoardingIfWaiting(ref EntityRefMut player)
    {
        var state = player.Read(Player.State);
        if (state.Activity != PlayerActivity.AwaitingShuttle)
        {
            return;
        }

        state.Activity = PlayerActivity.Idle;
        state.ActivityTicks = 0;
        state.ShuttleFrom = 0;
        state.ShuttleDest = 0;
        player.Set(Player.State, state);
        Replicate(in player);
        System.Threading.Interlocked.Increment(ref _boardingsCancelled);
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
    /// <summary>
    /// Refuses a command a downed player cannot carry out — it lies where it fell until it gets up or wakes as a clone — and counts it. True when refused.
    /// </summary>
    private static bool RefusedDown<T>(SubscriptionsCommands subs, in ClientCommand<T> command, in EntityRefMut player)
        where T : unmanaged
    {
        if (player.Read(Player.Vitals).Down == Downed.Up)
        {
            return false;
        }

        subs.Reject(command, PlayerDown);
        System.Threading.Interlocked.Increment(ref _refusedDown);
        return true;
    }

    /// <summary>How many moves, mounts and boardings a downed player sent and was refused.</summary>
    public static long RefusedWhileDown => System.Threading.Interlocked.Read(ref _refusedDown);

    private static long _refusedDown;

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
    /// <param name="changePhase">
    /// The share of this tick that still belongs to the velocity being replaced, in <c>[0, 1]</c> — <see cref="MoveDir.ChangePhase"/>. 0 gives the whole
    /// tick to the new velocity, which is what every intent did before A14b.
    /// </param>
    /// <param name="tick">This tick's number, which stamps the bias so the move phase can tell it from a stale one.</param>
    /// <remarks>
    /// <para>
    /// The same <c>Steer</c> the simulation uses, through the same <c>PlayerMove</c> integration: an intent is a destination and nothing else, and that is
    /// the whole of why a client cannot move faster than the server allows.
    /// </para>
    /// <para>
    /// <b>A14b: the tick an intent changes in is SPLIT, and the share owed to the old velocity travels as a one-tick bias rather than as a velocity.</b>
    /// Scaling the velocity itself would be wrong for every tick after this one: nothing re-steers a player between commands — a held key re-sends only
    /// every <c>REFRESH_MS</c>, and a stop is sent exactly once — so a scaled velocity would persist as a speed error for a third of a second, or for
    /// ever. The bias is stamped with the tick it is owed for and never cleared: the move phase applies it when the stamp matches and ignores it
    /// afterwards, which costs one comparison and keeps <c>PlayerMove</c> a reader of <c>PlayerMotion</c> rather than a third writer of it.
    /// </para>
    /// </remarks>
    private static void SteerTo(ref PlayerMotion move, ref EntityRefMut player, SubscriptionsCommands subs, float changePhase, long tick)
    {
        var place = player.Read(Player.Bounds);

        // Captured before Steer overwrites them: this is the velocity the player held for the first `changePhase` of this tick.
        var wasX = move.VelX;
        var wasZ = move.VelZ;

        // The simulation's own integration, not a copy of it: one function, so "an intent cannot move a player faster than the server does" is a property of
        // the code rather than of two implementations happening to agree.
        var moving = SimBridge.Steer(ref move.VelX, ref move.VelZ, move.SpeedMps, MetresPerTickForIntents, place.X, place.Z, move.DestX, move.DestZ);

        // The velocity carries the WHOLE tick, so the bias is what the split owes on top of it: `was x p + now x (1 - p)` less `now`.
        var phase = Math.Clamp(changePhase, 0f, 1f);
        if (phase > 0f)
        {
            move.BiasX = (wasX - move.VelX) * phase;
            move.BiasZ = (wasZ - move.VelZ) * phase;
            move.BiasTick = tick;
        }

        var state = player.Read(Player.State);

        // A player going nowhere is Idle, not Travelling. `Activity` is replicated, so getting this wrong tells every client watching that a standing player is
        // running — which is the one visible consequence in the whole intent path, and a stop is the commonest intent there is.
        state.Activity = moving ? PlayerActivity.Travelling : PlayerActivity.Idle;

        // Zero, because a possessed player has no server-side timer: see the remarks on ApplyIntents.
        state.ActivityTicks = 0;
        player.Set(Player.State, state);
        subs.Replicate(in player);
    }

    /// <summary>What a speed class is worth at a boost, or nothing when this server does not grant it.</summary>
    /// <param name="speedClass">The class the client asked for.</param>
    /// <param name="mounted">Whether the player is on its swoop.</param>
    /// <param name="boost">The multiplier asked for, 0 meaning 1.</param>
    /// <param name="speed">Metres per second.</param>
    /// <returns><see langword="false"/> for a class or a boost above what the player is entitled to.</returns>
    /// <remarks>
    /// Every class below <see cref="SpeedClasses.Count"/> is granted to any player but <see cref="SpeedClasses.Swoop"/>, which needs the player on its swoop
    /// (<paramref name="mounted"/>), and any boost up to <see cref="SpeedClasses.MaxBoost"/>. Anything above either is refused rather than clamped: a clamp
    /// would let a client ask for more every tick and never learn that it is not getting it, and the refusal is the number that says whether anything is
    /// trying.
    /// </remarks>
    internal static bool TrySpeedFor(byte speedClass, bool mounted, byte boost, out float speed)
    {
        if (speedClass >= SpeedClasses.Count || (speedClass == SpeedClasses.Swoop && !mounted) || boost > SpeedClasses.MaxBoost)
        {
            speed = 0f;
            return false;
        }

        speed = SpeedFor(speedClass) * Math.Max((byte)1, boost);
        return true;
    }

    /// <summary>What a class this server GRANTS is worth, in metres per second.</summary>
    /// <param name="speedClass">A class below <see cref="SpeedClasses.Count"/>.</param>
    /// <returns>Metres per second; 0 for a class this server does not grant.</returns>
    /// <remarks>
    /// The entitlement itself, so <see cref="TrySpeedFor"/> (the command path, which must refuse what it does not grant) and the avatar's replicated
    /// <c>walkMps</c>/<c>runMps</c> stats (A14, which tell the client what to predict with) cannot disagree about what running is worth. Two copies of this
    /// mapping would be two speeds, and the one the client integrated would be the one that drifts.
    /// </remarks>
    internal static float SpeedFor(byte speedClass) => speedClass switch
    {
        SpeedClasses.Stop => 0f,
        SpeedClasses.Walk => TatooineData.PlayerRunSpeedMps * 0.5f * TatooineData.PlayerGaitScale,
        SpeedClasses.Run => TatooineData.PlayerRunSpeedMps * TatooineData.PlayerGaitScale,
        SpeedClasses.Swoop => TatooineData.PlayerSwoopSpeedMps * TatooineData.PlayerGaitScale,
        _ => 0f,
    };

    /// <summary>How many <see cref="SetMount"/> asks changed a player's mount, and how many were refused (off a planet, waiting for a shuttle).</summary>
    public static (long Applied, long Refused) MountAsks =>
        (System.Threading.Interlocked.Read(ref _mountsApplied), System.Threading.Interlocked.Read(ref _mountsRefused));

    private static long _mountsApplied;
    private static long _mountsRefused;

    /// <summary>How many <see cref="SetStance"/> asks changed a player's stance, and how many named a stance this server does not know.</summary>
    public static (long Applied, long Refused) StanceAsks =>
        (System.Threading.Interlocked.Read(ref _stancesApplied), System.Threading.Interlocked.Read(ref _stancesRefused));

    private static long _stancesApplied;
    private static long _stancesRefused;

    /// <summary>How many <see cref="SetViewRadius"/> asks were applied, and how many were refused as out of range.</summary>
    /// <remarks>
    /// The refusal is the number that says whether a client and this server disagree about the range — the same role the speed class's refusal count plays.
    /// Both are reset by <see cref="ResetIntentAccounting"/>, so one check's numbers are its own.
    /// </remarks>
    public static (long Applied, long Refused) ViewRadiusAsks =>
        (System.Threading.Interlocked.Read(ref _radiusApplied), System.Threading.Interlocked.Read(ref _radiusRefused));

    private static long _radiusApplied;

    private static long _radiusRefused;

    /// <summary>Clears the intent counters, so one check's refusals do not decide the next one's.</summary>
    /// <remarks>Called from a <c>SetUp</c>, for the reason <see cref="ResetSessionAccounting"/> is. Never from the demo.</remarks>
    public static void ResetIntentAccounting()
    {
        System.Threading.Interlocked.Exchange(ref _intentsApplied, 0);
        System.Threading.Interlocked.Exchange(ref _intentsOwned, 0);
        System.Threading.Interlocked.Exchange(ref _intentsUnowned, 0);
        System.Threading.Interlocked.Exchange(ref _intentsRefusedSpeed, 0);
        System.Threading.Interlocked.Exchange(ref _targetsRefused, 0);
        System.Threading.Interlocked.Exchange(ref _radiusApplied, 0);
        System.Threading.Interlocked.Exchange(ref _radiusRefused, 0);
        System.Threading.Interlocked.Exchange(ref _targetsSet, 0);
        System.Threading.Interlocked.Exchange(ref _possessions, 0);
        System.Threading.Interlocked.Exchange(ref _releases, 0);
        System.Threading.Interlocked.Exchange(ref _boardingsQueued, 0);
        System.Threading.Interlocked.Exchange(ref _boardingsRefused, 0);
        System.Threading.Interlocked.Exchange(ref _boardingsCancelled, 0);
        System.Threading.Interlocked.Exchange(ref _mountsApplied, 0);
        System.Threading.Interlocked.Exchange(ref _mountsRefused, 0);
        System.Threading.Interlocked.Exchange(ref _stancesApplied, 0);
        System.Threading.Interlocked.Exchange(ref _stancesRefused, 0);
        BoundPlayer.Clear();

        // The holds go with it (A13), for the same reason BoundPlayer does: a fixture that resets only the intent accounting would otherwise inherit a hold
        // naming an entity from a disposed world, and the first resumable close of the next run would hand a client a body out of it.
        ResumingPlayer.Clear();
        ResumesToRetire.Clear();

        // The rest of the statics a runtime leaves behind. _pushCommands in particular holds the LAST runtime's SubscriptionsCommands, so Replicate() from a
        // system of the next one would mark a slot in an object belonging to a disposed engine; the others make a second run in one process inherit the
        // first one's configuration.
        _pushCommands = null;
        Scheduler = null;

        // The boarding geometry goes with them, and for the same reason: it is the LAST runtime's SimBridge, whose port
        // and planet indexes describe a world that has been disposed. A BoardShuttle in the next run would be validated
        // against it, and a fixture that happened to run second would accept or refuse on the first run's map.
        ShuttleBoarding = null;
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
                if (owners[slot].Controller == session.Value && control[slot].Kind != ControllerKind.Scripted)
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

    /// <summary>
    /// Whether an entity is something that can still be shot at: it exists, and whatever vitals it has are above zero (A11).
    /// </summary>
    /// <param name="tx">A transaction to read through — the tick's, in the one place this is used for real.</param>
    /// <param name="target">The entity a client named.</param>
    /// <returns><see langword="false"/> when the target is gone or dead.</returns>
    /// <remarks>
    /// <para>
    /// <b>By archetype, because health lives on a different component in each.</b> A player's is <c>PlayerVitals</c>, a creature's
    /// <c>CreatureVitals</c>, a lair's <c>LairVitals</c>; there is no shared interface to read, and inventing one for three call
    /// sites would be an abstraction over a switch.
    /// </para>
    /// <para>
    /// <b>Anything that is not one of the three answers TRUE, and that is deliberate.</b> A city NPC or a building has no vitals and
    /// is not dead — it is simply not shootable, which <see cref="ClassifyTarget"/> already describes and the combat system already
    /// refuses. Answering false here would conflate "you cannot attack a cantina" with "that thing is dead", and the client's two
    /// reactions to those are different.
    /// </para>
    /// </remarks>
    public static bool IsAliveTarget(Transaction tx, EntityId target)
    {
        var routing = target.ArchetypeId;

        if (routing == _playerRouting)
        {
            return tx.For<Player>().TryOpen(target, out var player) && player.Read(Player.Vitals).Health > 0;
        }

        if (routing == _creatureRouting)
        {
            return tx.For<Creature>().TryOpen(target, out var creature) && creature.Read(Creature.Vitals).Health > 0;
        }

        if (routing == _lairRouting)
        {
            return tx.For<CreatureLair>().TryOpen(target, out var lair)
                && SimBridge.IsStandingLair(lair.Read(CreatureLair.Spawner), lair.Read(CreatureLair.Vitals));
        }

        return true;
    }

    /// <summary>Which of the shootable archetypes an entity belongs to, for <c>PlayerSession.TargetKind</c> (SWG-02).</summary>
    /// <param name="target">The entity a client named.</param>
    /// <returns>
    /// The <c>CombatTargetKind</c> of <paramref name="target"/>. An entity of any other archetype — a city NPC, a building, a starship — answers
    /// <c>CombatTargetKind.Unshootable</c>: selected, and never fired at.
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

        if (routing == _lairRouting)
        {
            return CombatTargetKind.Lair;
        }

        return routing == _playerRouting ? CombatTargetKind.Player : CombatTargetKind.Unshootable;
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

    /// <summary>Scratch: the player sessions open this tick that hold no player yet.</summary>
    private static readonly List<SessionId> Unbound = [];

    /// <summary>Scratch: the player sessions seen open this tick, so the closed ones can give their players back.</summary>
    private static readonly HashSet<uint> Seen = [];

    /// <summary>Scratch: the bindings to drop, collected before the dictionary is written.</summary>
    private static readonly List<uint> Retired = [];
}
