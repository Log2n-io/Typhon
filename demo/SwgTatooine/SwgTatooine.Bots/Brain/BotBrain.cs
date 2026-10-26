using System;
using System.Collections.Generic;
using SwgTatooine.Navigation;

namespace SwgTatooine.Bots.Brain;

/// <summary>What a bot is currently trying to do.</summary>
public enum Goal
{
    /// <summary>Deciding, which it does for exactly one tick.</summary>
    Choosing = 0,

    /// <summary>Going to a city.</summary>
    Travelling,

    /// <summary>Killing a creature it has noticed.</summary>
    Hunting,

    /// <summary>Standing somewhere plausible for a while.</summary>
    Idling,

    /// <summary>Waiting for a shuttle it has queued for.</summary>
    Waiting,

    /// <summary>Going to the nearest mission terminal or bazaar of the city it is in, and using it for a while.</summary>
    Visiting,

    /// <summary>Going into a building, standing about inside, and coming back out.</summary>
    Indoors,

    /// <summary>Doing the mission it holds: out to its lair, its defenders, then the lair (lair-objectives.md B-3).</summary>
    Questing,
}

/// <summary>
/// One bot's decision loop: what it wants, how it is getting there, and the command that follows.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own code, not the server's.</b> <c>PlayerThink</c> decides from global tables and this decides from what the
/// bot was sent. The requirement was never shared code — it is that the server cannot tell a bot from a browser, which
/// is a property of the protocol: every intent below is one a browser can send, and the server has no branch on
/// bot-ness. See `claude/design/SwgTatooine/design/brain.md`.
/// </para>
/// <para>
/// <b>Deterministic from a seed, so a run can be reproduced.</b> Every choice comes from an xorshift the bot owns, which
/// means a fleet of a thousand is a thousand different behaviours that replay identically — the only kind of load that
/// can be compared between two builds.
/// </para>
/// <para>
/// <b>Goals are a few lines; routing is the content.</b> That split is deliberate and it is the design's claim: deciding
/// to go to Dry Wells is trivial, and deciding whether to walk, ride or fly there is the thing a player does well and a
/// scripted bot does badly.
/// </para>
/// <para>
/// <b>Inside a city it walks the way a person clicking would</b> — round the buildings, on the browser's own grid search (<see cref="CityPath"/>) —
/// and through a door on the path the building publishes for it. Both need the public content (<see cref="BotAssets"/>); a brain built without it
/// keeps the behaviour it always had: straight lines, and no doors.
/// </para>
/// </remarks>
public sealed partial class BotBrain
{
    /// <summary>How close counts as arrived, in metres.</summary>
    /// <remarks>
    /// <para>
    /// A city's radius would be too coarse — a bot would stop at the wall and call it arrival — and a metre would be too
    /// fine for a server that integrates positions per tick: a bot would orbit its destination forever. 24 m is about
    /// five seconds of running, and it is inside every city.
    /// </para>
    /// <para>
    /// <b>This constant was written, argued for, and then not used, and the cost was the whole feature.</b> Arrival
    /// compared against the city's radius instead, so a bot bound for Port Sable stopped 456 m from its centre — more
    /// than twice a player's 192 m sphere. The city's structures therefore never entered its store, no shuttleport was
    /// ever learned, and <see cref="Routing.Plan"/> could not return <see cref="TravelMode.Shuttle"/>, which needs both
    /// ports. A fleet that never boarded anything was read as a run too short to board in. The test that should have
    /// caught it placed the body on the city's exact centre, where both thresholds agree.
    /// </para>
    /// </remarks>
    public const float ArrivedM = 24f;

    /// <summary>How close a creature has to be before a bot decides to go for it, in metres.</summary>
    /// <remarks>
    /// A player's sphere is 192 m, so anything in view is within that; this is the range at which a bot bothers. Smaller
    /// than the sphere on purpose: a bot that chased everything it could see would never travel anywhere, and a planet
    /// of bots all converging on whatever is closest is not a population, it is a crowd.
    /// </remarks>
    public const float NoticeM = 120f;

    /// <summary>How close a bot gets before attacking, in metres — inside the server's own weapon reach (the pistol's 30 m, edge to edge).</summary>
    public const float StrikeM = 20f;

    /// <summary>
    /// How near a creature may come before the bot backs off, in metres: its bite reaches 6 m and it closes at 2-4 m/s, so this leaves two seconds of
    /// shooting before it could strike. A bot runs at 10 m/s, so it always opens the gap again: it kites (Loïc, 2026-10-26).
    /// </summary>
    public const float KiteM = 12f;

    /// <summary>How long an idle lasts, in SECONDS, before the bot decides again.</summary>
    /// <remarks>
    /// <b>Seconds, converted by the brain, because the tick it counts in is the fleet driver's and not the server's.</b>
    /// The driver runs at a few hertz and the server at tens, and the first version of this held 200 "ticks" sized as if
    /// they were the server's — so an idling bot stood still for fifty seconds and a 45-second run sent two intents per
    /// session while every counter said it was healthy. A duration that means a duration cannot be read at the wrong
    /// rate.
    /// </remarks>
    public const double IdleSeconds = 20d;

    /// <summary>How long a bot waits for a shuttle before giving up and travelling the ordinary way, in seconds.</summary>
    /// <remarks>
    /// Two of the server's default intervals. A bot that waited forever would be a session that stopped generating load,
    /// and one that gave up after a single interval would give up on the shuttle it was promised: the wait is a
    /// distribution and one interval is only its mean.
    /// </remarks>
    public const double ShuttlePatienceSeconds = 2d * Routing.ShuttleIntervalSeconds;

    /// <summary>How long a hunt may last before the bot gives that creature up, in seconds.</summary>
    public const double HuntSeconds = 30d;

    /// <summary>A terminal is used from within this of its stand point, metres: inside every published interaction's <c>radius</c> (1.2–1.5 m).</summary>
    /// <remarks>
    /// Not the 0.3 m a door's last point needs. A stand point is a body's width from the terminal's collider and the street is crowded, so a body is
    /// pushed off it by a few tenths as often as not — and a walk that demanded the exact point circled it until the watchdog gave up.
    /// </remarks>
    public const double UseReachM = 1d;

    /// <summary>
    /// A terminal whose stand point a walk stuck short of is used from here if the body is within this of the terminal, metres: inside the server's own
    /// reach for a take (<c>TatooineData.MissionTerminalReachM</c>, 6 m) with room to spare.
    /// </summary>
    /// <remarks>
    /// A terminal is a queue. Ringed by other bodies the stand point is out of reach for good, and the walk stuck "0 m to go", failed the errand, and
    /// tried the same terminal again — the live telemetry's oscillating bots, and a third of all errands failed (2026-10-27). A person in that crowd
    /// reads the list over the shoulders in front.
    /// </remarks>
    public const float TerminalUseFromM = 4f;

    /// <summary>
    /// A walk's first point within this of the body, and behind it on the way to the next, is passed at once, metres: the open cell a search starts
    /// from when the body stands in a wall's margin, which can lie behind it.
    /// </summary>
    /// <remarks>Walked to, every walk planned from against a wall began by stepping back — and every re-plan from there did it again (live, 2026-10-27).</remarks>
    public const double StepOutM = 1.5d;

    /// <summary>A searched path's point counts as reached this close, metres.</summary>
    /// <remarks>
    /// A path's points are its corners, and every leg between two of them is clear only from corner to corner: called reached from 2.5 m, the walk
    /// cut each corner by that much and the third live test's rider steered across a house's corner into its wall. <c>MoveTo</c> puts the body on a
    /// point, so the reach only has to absorb the server's last step. On foot a corner is passed earlier, but only by sight: see <see cref="Follow"/>.
    /// </remarks>
    public const double CornerReachM = 0.75d;

    /// <summary>A walk's exact part — a door's, a terminal's published points — that has not closed by half a metre in this long is stuck, seconds.</summary>
    /// <remarks>Long, because a stand point is in a crowd: bodies at a terminal shove each other, and a bot that gave up on being shoved used none.</remarks>
    public const double StuckSeconds = 4d;

    /// <summary>
    /// A walk's open part, or a straight line, that has not closed by half a metre in this long is stuck, in seconds — then searched again from where the
    /// body stands.
    /// </summary>
    /// <remarks>
    /// Five decisions at the 4 Hz driver: a body running at 10 m/s covers 12 m in it, so half a metre is no progress at all. It was four seconds, and a bot
    /// leaving town pushed against a house's wall for all four, sliding along it, before it looked for a way round (Loïc, 2026-10-26).
    /// </remarks>
    public const double OpenStuckSeconds = 1.25d;

    /// <summary>
    /// What a step cut across a corner keeps clear of the corner's wall beyond the body's own half, metres: a heading is renewed a few times a second, not
    /// continuously, and the server's step does not land on the line to the hundredth.
    /// </summary>
    public const float CutClearanceM = 0.1f;

    /// <summary>A building further than this is not one a bot in the street decides to walk into, metres.</summary>
    public const float DoorReachM = 120f;

    /// <summary>A facility further than this is not an errand, it is a journey, metres.</summary>
    public const float ErrandReachM = 1500f;

    /// <summary>A mission not done this long after the bot set out on it is cancelled (lair-objectives.md B-4).</summary>
    public const double QuestSeconds = 600d;

    /// <summary>How far round its lair a creature is taken for one of its defenders.</summary>
    public const float DefenderM = 60f;

    /// <summary>A walk is searched on the grid only within this of its goal; further, it is ridden or run straight to there first, metres.</summary>
    /// <remarks>
    /// A search across a whole city is a million cells; one across 200 m is a fortieth of that and covers every street a door or a terminal stands on.
    /// The straight part crosses open ground or the city's edge, and the stuck watchdog re-plans it if a building is in the way after all.
    /// </remarks>
    public const float SearchReachM = 200f;

    /// <summary>A search rectangle no wider than this is searched on half-metre cells, metres: 440 × 440 cells at most.</summary>
    public const float FineSearchM = CityPath.FineSearchM;

    /// <summary>A search that has expanded this many cells without reaching its goal gives up: a walled-off goal must not cost the whole grid.</summary>
    public const int MaxExpansions = 120_000;

    /// <summary>A walk across a city is searched this far ahead at a time, metres, and so is a stuck traveller's longest detour.</summary>
    /// <remarks>
    /// Short enough that the search's rectangle (its 30 m margin each side included) stays on half-metre cells. Metre cells pull a walk straight across a
    /// wall's corner: searched 200 m out of a shuttleport's plaza, 5 % of the walks clipped a wall the server stops a body at (measured 2026-10-26, the real
    /// port's walls, 1 152 starts and headings).
    /// </remarks>
    public const float CityLegM = 150f;

    /// <summary>How often a bot in a city checks whether it stands inside a shuttleport's walls, in seconds.</summary>
    public const double PortCheckSeconds = 1d;

    /// <summary>Where a stuck traveller searches a way to, in order: how far along its course, and how far off it (radians).</summary>
    private static readonly (float Metres, float Turn)[] Detours =
    [
        (CityLegM, 0f), (SearchReachM * 0.6f, 0f), (SearchReachM * 0.3f, 0f),
        (SearchReachM * 0.3f, MathF.PI / 3f), (SearchReachM * 0.3f, -MathF.PI / 3f),
        (SearchReachM * 0.15f, MathF.PI / 2f), (SearchReachM * 0.15f, -MathF.PI / 2f),
    ];

    private readonly City[] _cities;
    private readonly BotAssets _assets;
    private readonly long _idleTicks;
    private readonly long _patienceTicks;
    private readonly double _rate;
    private readonly long _stuckTicks;
    private readonly long _openStuckTicks;
    private readonly List<SeenStructure> _seen = [];
    private readonly Leg _leg = new();
    private uint _rng;

    /// <summary>A brain for one bot.</summary>
    /// <param name="cities">The map it ships with: where the cities are.</param>
    /// <param name="seed">Its own seed; two bots with the same seed behave identically.</param>
    /// <param name="ticksPerSecond">How often this bot is driven, which is what its timed goals are measured in.</param>
    /// <param name="assets">The public content (walls, doors, stand points), or null for a brain that only travels and hunts.</param>
    /// <param name="homeRegion">
    /// The city this bot lives in, as a region (a city plus one), or 0 for none. A bot with a home goes back to it, and stays: its errands, doors, hunts
    /// and missions start and end there, and it never travels to another city.
    /// </param>
    public BotBrain(City[] cities, uint seed, double ticksPerSecond = 4d, BotAssets assets = null, int homeRegion = 0)
    {
        ArgumentNullException.ThrowIfNull(cities);
        if (cities.Length == 0)
        {
            // Refused at construction rather than at the first roll, where it was a DivideByZeroException from
            // `Next() % _cities.Length` several frames into a run.
            throw new ArgumentException("a brain needs at least one city to travel to", nameof(cities));
        }

        _cities = cities;
        _assets = assets;
        _rng = seed == 0 ? 0x9E3779B9u : seed;   // xorshift is dead at zero, and a caller numbering bots from 0 would hand it one
        _rate = ticksPerSecond > 0d ? ticksPerSecond : 1d;
        _idleTicks = Ticks(IdleSeconds);
        _patienceTicks = Ticks(ShuttlePatienceSeconds);
        _stuckTicks = Ticks(StuckSeconds);
        _openStuckTicks = Ticks(OpenStuckSeconds);
        Facilities = new FacilityMap();
        HomeRegion = homeRegion > 0 && homeRegion <= cities.Length ? homeRegion : 0;
    }

    /// <summary>The city this bot lives in, as a region (a city plus one); 0 for a bot that roams the planet.</summary>
    public int HomeRegion { get; }

    /// <summary>What this bot has learned about the cities.</summary>
    public FacilityMap Facilities { get; }

    /// <summary>What it is doing.</summary>
    public Goal Goal { get; private set; } = Goal.Choosing;

    /// <summary>How far above its own combat level a bot hunts (content-levels.md § 7): a creature past it is left alone, and an offer past it is never
    /// taken — the desk would refuse it (missions.md § 8).</summary>
    public const int LevelReach = 3;

    /// <summary>
    /// The bot's own combat level: 1 — where a new character belongs, the city fringes — until the character's CL is replicated and set here
    /// (character-level.md).
    /// </summary>
    public int CombatLevel { get; set; } = 1;

    /// <summary>The highest level this bot hunts, and prefers an offer at: its <see cref="CombatLevel"/> plus <see cref="LevelReach"/>.</summary>
    public int MaxLevel => CombatLevel + LevelReach;

    /// <summary>
    /// Whether the nearest creature is one to HUNT: at or below <see cref="MaxLevel"/>, or of no stated level. A level-45 viper in view does not pull a
    /// CL-1 bot into a fight it cannot win. A mission's defenders are not asked: they are fought at any level, since the lair cannot be broken past them.
    /// </summary>
    private bool Huntable(in Perceived world) => world.NearestCreatureLevel <= MaxLevel;

    /// <summary>The REGION it is going to — a city plus one — or 0 when it is not travelling.</summary>
    /// <remarks>
    /// A region rather than a city index, so that "no destination" is 0; see <see cref="Perceived.PortRegion"/> for why
    /// the brain carries the wire's own encoding rather than a sentinel of its own.
    /// </remarks>
    public int TargetRegion { get; private set; }

    /// <summary>How it decided to get there.</summary>
    public TravelMode Mode { get; private set; } = TravelMode.Run;

    /// <summary>How many times it asked to get on or off its swoop.</summary>
    public long MountsAsked { get; private set; }

    /// <summary>How many doors it went through, either way.</summary>
    public long DoorsCrossed { get; private set; }

    /// <summary>How many terminals it reached and used.</summary>
    public long FacilitiesUsed { get; private set; }

    /// <summary>How many walks it searched round the buildings.</summary>
    public long PathsPlanned { get; private set; }

    /// <summary>Missions it asked to take at a terminal, and missions it gave up on.</summary>
    public long MissionsAsked { get; private set; }

    public long MissionsGivenUp { get; private set; }

    /// <summary>How many times it found itself inside a shuttleport's walls with somewhere else to be, and walked out by the gate.</summary>
    public long PortsLeft { get; private set; }

    /// <summary>Whether its last check found it inside a shuttleport's walls with no flight to take there: what a trapped bot looks like.</summary>
    public bool InPort { get; private set; }

    /// <summary>The walk in progress, for a trace line: next point, count, where the exact part starts, ridden, how it ended.</summary>
    public string LegState =>
        $"leg {_leg.Next}/{_leg.Points.Count} exact {_leg.Precise} ride {(_leg.Ride ? 1 : 0)}{(_leg.Dismounted ? "-off" : string.Empty)} end {_leg.Ended}"
        + (_leg.Active ? $" next ({_leg.Points[_leg.Next].X:F1}, {_leg.Points[_leg.Next].Z:F1})" : string.Empty);

    /// <summary>The route's first leg when it is flying: the port to walk to.</summary>
    private float _portX;

    private float _portZ;

    /// <summary>The tick the current goal ends on, for the goals that are timed.</summary>
    private long _until;

    /// <summary>Whether it has already asked to board on this trip, so it asks once rather than every tick.</summary>
    /// <remarks>
    /// Once is enough now that a refusal comes back — see <see cref="Perceived.BoardRefused"/>. Asking repeatedly would
    /// also inflate the server's queued-boarding counter, which is one of the numbers this fleet exists to produce.
    /// </remarks>
    private bool _asked;

    /// <summary>Whether it wants to be on its swoop; the server's <see cref="Perceived.Mounted"/> is what it is.</summary>
    private bool _wantMounted;

    private long _mountAskedAt = long.MinValue / 2;

    /// <summary>When the current hunt is given up.</summary>
    private long _huntUntil;

    /// <summary>The creature it last gave up on, which it does not hunt again.</summary>
    private uint _givenUpOn;
    private int _mountAsks;

    /// <summary>The realm it was in last tick, so a door crossed is noticed as the change it is.</summary>
    private int _lastRealm;

    private bool _realmKnown;

    /// <summary>Where a travel's straight line last made progress, for the stuck watchdog.</summary>
    private double _travelBest = double.MaxValue;

    private long _travelBestAt;

    /// <summary>Inside a building: whether it has had its look round and is leaving.</summary>
    private bool _leaving;

    /// <summary>Visiting: whether it has arrived and is using the terminal.</summary>
    private bool _using;

    /// <summary>After a visit, the shuttle is the next thing it does.</summary>
    private bool _flyNext;

    /// <summary>The kind of facility the errand under way goes to, and where it stands.</summary>
    private int _visitKind;

    private (float X, float Z) _visitAt;

    /// <summary>When it set out on the mission it holds, or -1 while it holds none it has set out on.</summary>
    private long _questSince = -1;

    /// <summary>Whether it has reached its mission's lair, so a lair drawn again far away starts a new ride.</summary>
    private bool _questNear;

    /// <summary>Walking out of a shuttleport's plaza by its gate; the goal is chosen again once out.</summary>
    private bool _leavingPort;

    /// <summary>When it next checks whether it stands inside a shuttleport's walls.</summary>
    private long _portCheckAt;


    /// <summary>When a walk across the city may be searched again, after one found no way or stuck.</summary>
    private long _cityWalkAt;

    /// <summary>Decides what to do this tick.</summary>
    /// <param name="world">What the bot was sent.</param>
    /// <param name="tick">The fleet's tick, which is what the timed goals count in.</param>
    /// <returns>The command to send, or <see cref="Intent.None"/>.</returns>
    public Intent Decide(in Perceived world, long tick)
    {
        if (!world.HasBody)
        {
            return Intent.None;   // a camera has nothing to decide, and no body to decide for
        }

        var arrived = !_realmKnown || world.Realm != _lastRealm;
        if (arrived)
        {
            // The first realm seen is where the bot was put, not a door it went through.
            DoorsCrossed += _realmKnown ? 1 : 0;
            _realmKnown = true;
            _lastRealm = world.Realm;
            _leg.Clear();
            _room = null;
            _roomSearchAt = 0;
        }

        // Indoors the map is the room's: the planet's coordinates mean nothing there. Whatever the bot was doing, being inside is now the goal, and its
        // end is the way out — which a bot can only find with the room's published graph.
        if (world.Realm != 0)
        {
            // Every arrival starts its stay now — the walk in left whatever timer the street had, and a stale one turned the bot round in the doorway.
            if (Goal != Goal.Indoors || arrived)
            {
                Become(Goal.Indoors, GoalWhy.EnteredBuilding, tick, (uint)world.Realm);
                _leaving = false;
                _until = tick + Ticks(4 + (Next() % 8u));
            }

            return Inside(in world, tick);
        }

        if (Goal == Goal.Indoors && _leaving)
        {
            // Out: the crossing has put it back on the planet. Stand a moment where the door left it, as anyone does.
            Become(Goal.Idling, GoalWhy.LeftBuilding, tick);
            _until = tick + Ticks(2);
        }

        // The swoop, before the goal: getting on or off is a command of its own, and the goal's movement waits a tick for it.
        if (MountIntent(in world, tick) is { } mount)
        {
            // Getting on or off stands the body still for a tick or two, which is not being stuck.
            _travelBestAt = tick;
            _leg.BestAt = tick;
            return mount;
        }

        // Inside a shuttleport's walls with somewhere else to be: out by the gate first, whatever the goal. Nothing stops a body walking in through
        // the gate, and a straight line on from there ends against the far wall (Loïc, 2026-10-26: bots stood trapped in Port Sable's).
        if (LeavePort(in world, tick) is { } leave)
        {
            return leave;
        }

        switch (Goal)
        {
            case Goal.Hunting:
                return Hunt(in world, tick);
            case Goal.Travelling:
                return Travel(in world, tick);
            case Goal.Waiting:
                return Wait(in world, tick);
            case Goal.Visiting:
                return Visit(in world, tick);
            case Goal.Indoors:
                return Enter(in world, tick);
            case Goal.Questing:
                return Quest(in world, tick);
            case Goal.Idling:
                if (tick < _until)
                {
                    return Intent.None;
                }

                break;
        }

        return Choose(in world, tick);
    }

    /// <summary>Picks the next goal.</summary>
    /// <param name="world">What the bot was sent.</param>
    /// <param name="tick">The fleet's tick.</param>
    /// <returns>The first command of the new goal.</returns>
    /// <remarks>
    /// <b>Something in view wins, because it is the only goal whose subject the bot can actually see.</b> Everything else
    /// is chosen from the map, and walking off to another city while a womp rat is standing in front of you is what makes
    /// a scripted bot read as scripted.
    /// </remarks>
    private Intent Choose(in Perceived world, long tick)
    {
        ChoosingFromHere();
        _wantMounted = false;
        _leg.Clear();

        // A mission held, its lair placed: that is what it is doing until it is done or given up (lair-objectives.md B-3).
        if (world.MissionsHeld > 0 && world.HasMissionLair)
        {
            Become(Goal.Questing, GoalWhy.QuestHeld, tick);
            return Quest(in world, tick);
        }

        if (world.MissionsHeld == 0)
        {
            _questSince = -1;
        }

        if (world.NearestCreature != 0 && world.NearestCreatureM <= NoticeM && world.NearestCreature != _givenUpOn && Huntable(in world))
        {
            Become(Goal.Hunting, GoalWhy.CreatureNoticed, tick, world.NearestCreature, world.NearestCreatureM);
            _huntUntil = tick + Ticks(HuntSeconds);
            return Hunt(in world, tick);
        }

        var here = RegionAt(world.X, world.Z);

        // A bot with a home that is not there — it was spawned elsewhere, or a hunt or a mission took it out — goes back.
        if (HomeRegion != 0 && here != HomeRegion)
        {
            return GoTo(in world, tick, HomeRegion - 1, here);
        }

        // At home: its town's errands and doors, and standing about — never a trip to another city.
        if (HomeRegion != 0)
        {
            _flyNext = false;
            var pick = Next() % 100u;
            if (_assets != null && pick < 45u && StartVisit(in world, tick))
            {
                return Visit(in world, tick);
            }

            if (_assets != null && pick < 75u && StartEntering(in world, tick))
            {
                return Enter(in world, tick);
            }

            Become(Goal.Idling, GoalWhy.IdleChosen, tick);
            _until = tick + _idleTicks;
            return Intent.Move(0f, 0);
        }

        // In a city, with the content to walk it: an errand, a door, or — after an errand — the shuttle. Rolled from the same stream, after the
        // decision above and only when the content exists, so a brain without it draws exactly the sequence it always drew.
        if (_assets != null && here != 0)
        {
            var errand = Next() % 100u;
            if (_flyNext || errand < 25u)
            {
                if (!_flyNext && StartVisit(in world, tick))
                {
                    return Visit(in world, tick);
                }

                _flyNext = false;
                if (StartFlight(in world, here, tick))
                {
                    return Travel(in world, tick);
                }
            }
            else if (errand < 45u && StartEntering(in world, tick))
            {
                return Enter(in world, tick);
            }
        }

        var roll = Next() % 100u;
        if (roll < 20u)
        {
            Become(Goal.Idling, GoalWhy.IdleChosen, tick);
            _until = tick + _idleTicks;
            return Intent.Move(0f, 0);   // stop where it is: speed class 0 is the server's own "stand still"
        }

        // The city it is standing in is excluded by rolling over the OTHERS, not by nudging a self-selection to the
        // next index: that nudge made one neighbour twice as likely as every other city, and a fleet that visits one
        // place twice as often is a load distribution reported as uniform.
        return GoTo(in world, tick, OtherCity(here), here);
    }

    /// <summary>Sets off for a city — by the mode its route chose — and takes the first step.</summary>
    private Intent GoTo(in Perceived world, long tick, int destination, int here)
    {
        TargetRegion = destination + 1;
        var city = _cities[destination];
        var route = Routing.Plan(new Journey(world.X, world.Z, city.X, city.Z, here, TargetRegion, Facilities));
        Mode = route.Mode;
        _portX = route.PortX;
        _portZ = route.PortZ;
        _asked = false;
        _travelBest = double.MaxValue;
        _travelBestAt = tick;
        var why = TargetRegion == HomeRegion ? GoalWhy.GoingHome : GoalWhy.TravelChosen;
        Become(Goal.Travelling, why, tick, (uint)TargetRegion, (float)Routing.Distance(world.X, world.Z, city.X, city.Z));
        return Travel(in world, tick);
    }

    private int OtherCity(int here)
    {
        if (here != 0 && _cities.Length > 1)
        {
            var excluded = here - 1;
            var pick = (int)(Next() % (uint)(_cities.Length - 1));
            return pick < excluded ? pick : pick + 1;
        }

        return (int)(Next() % (uint)_cities.Length);
    }

    /// <summary>Closes on the creature it noticed and attacks it, and gives up when it is gone or too far.</summary>
    /// <param name="world">What the bot was sent.</param>
    /// <param name="tick">The fleet's tick.</param>
    /// <returns>The command.</returns>
    private Intent Hunt(in Perceived world, long tick)
    {
        // A fight that has not ended in this long will not: something it cannot hurt, or cannot reach. Leave that one be and get on with the day —
        // the first live run had two bots naming the same target from the same spot for all three minutes.
        if (world.NearestCreature != 0 && tick >= _huntUntil)
        {
            _givenUpOn = world.NearestCreature;
            Become(Goal.Choosing, GoalWhy.HuntTimeout, tick);
            return Choose(in world, tick);
        }

        if (world.NearestCreature == 0 || world.NearestCreature == _givenUpOn || !Huntable(in world))
        {
            // Killed, or it wandered out of view, or what is nearest now is past this bot's level. Nothing to hunt, so decide again.
            var why = world.NearestCreature == 0 ? GoalWhy.HuntTargetGone
                : world.NearestCreature == _givenUpOn ? GoalWhy.HuntGivenUpTarget
                : GoalWhy.HuntNotHuntable;
            Become(Goal.Choosing, why, tick);
            return Choose(in world, tick);
        }

        // Out of the range it bothers at: give up and decide again. Without this the only exit was an empty sphere, so
        // a bot in a creature-dense place re-targeted whatever was nearest every tick, for the whole run, and never
        // travelled anywhere — the goal had no end, which is not the same as a goal that takes a long time.
        if (world.NearestCreatureM > NoticeM)
        {
            Become(Goal.Choosing, GoalWhy.HuntOutOfRange, tick);
            return Choose(in world, tick);
        }

        // Attacking is naming a target and letting the server's combat path resolve it, which is exactly what a browser
        // does — there is no attack command, and that is the point. Where to stand while it does is Engage's.
        return Engage(world.X, world.Z, world.NearestCreature, world.NearestCreatureX, world.NearestCreatureZ, world.NearestCreatureM, mobile: true);
    }

    /// <summary>
    /// How a bot fights a target at <paramref name="distanceM"/>: closes in past <see cref="StrikeM"/>, stands and shoots between the two, and — a
    /// creature, which bites — runs from it inside <see cref="KiteM"/>, still shooting. A lair does not move or bite: the bot stands at its range.
    /// </summary>
    /// <param name="x">The bot, east.</param>
    /// <param name="z">The bot, north.</param>
    /// <param name="target">The target's netId.</param>
    /// <param name="tx">The target, east.</param>
    /// <param name="tz">The target, north.</param>
    /// <param name="distanceM">Between the two.</param>
    /// <param name="mobile">Whether the target chases and bites (a creature), rather than stands (a lair).</param>
    /// <returns>An attack carrying its motion: towards, none, or away.</returns>
    public static Intent Engage(float x, float z, uint target, float tx, float tz, float distanceM, bool mobile)
    {
        var run = Routing.SpeedClassOf(TravelMode.Run);
        if (distanceM > StrikeM)
        {
            return Intent.Attack(target, Routing.HeadingTo(x, z, tx, tz), run);
        }

        if (mobile && distanceM < KiteM)
        {
            return Intent.Attack(target, Routing.HeadingTo(tx, tz, x, z), run);
        }

        return Intent.Attack(target);
    }

    /// <summary>Goes to the target city by the mode the route chose.</summary>
    /// <param name="world">What the bot was sent.</param>
    /// <param name="tick">The fleet's tick.</param>
    /// <returns>The command.</returns>
    private Intent Travel(in Perceived world, long tick)
    {
        if (TargetRegion <= 0 || TargetRegion > _cities.Length)
        {
            Become(Goal.Choosing, GoalWhy.NoDestination, tick);
            return Choose(in world, tick);
        }

        var city = _cities[TargetRegion - 1];
        var toCentre = Routing.Distance(world.X, world.Z, city.X, city.Z);
        if (toCentre <= ArrivedM)
        {
            // Arrived. Idle a while rather than choosing again at once: a fleet that re-decided on arrival would travel
            // without pause, and a city nobody stands in is a city whose group set is never held.
            Become(Goal.Idling, GoalWhy.Arrived, tick);
            _until = tick + _idleTicks;
            TargetRegion = 0;
            _wantMounted = false;
            _leg.Clear();
            return Intent.Move(0f, 0);
        }

        if (Mode == TravelMode.Shuttle)
        {
            return ToTheShuttle(in world, tick);
        }

        return GoToward(in world, tick, city.X, city.Z, toCentre, Mode == TravelMode.Swoop);
    }

    /// <summary>
    /// Toward a point: on the swoop when <paramref name="ride"/> and still far, the straight line, and round whatever stops it — a travel to a city's
    /// centre, or a ride out to a mission's lair.
    /// </summary>
    private Intent GoToward(in Perceived world, long tick, float tx, float tz, double distance, bool ride)
    {
        _wantMounted = ride && distance > 2 * ArrivedM;
        var inCity = RegionAt(world.X, world.Z) != 0;

        // Out of the city on its walk out: the rest is open desert, ridden straight from its edge. Walked on to the stretch's end, the last of the
        // stretch was a hundred metres on foot past the edge, and getting on mid-walk rode the walk's corners at the swoop's speed.
        if (_leg.Active && _leg.CityWalk && !inCity)
        {
            NoteLeg(LegWhy.CityEdge, tick);
            _leg.Clear();
            _travelBest = double.MaxValue;
            _travelBestAt = tick;
        }

        // A walk round a building that stood in the straight line, once the watchdog found it; then the straight line again.
        if (_leg.Active)
        {
            var step = Follow(in world, tick, out var end);
            if (end == LegEnd.Going)
            {
                return step;
            }

            if (end == LegEnd.Stuck)
            {
                // A searched walk that stuck after its own replans: straight on, and the watchdog's detours, for a while before searching again.
                _cityWalkAt = tick + (2 * _stuckTicks);
            }

            _leg.Clear();
            _travelBest = double.MaxValue;
            _travelBestAt = tick;
        }

        // In a city, on foot: searched round its buildings before it is walked, a stretch at a time, as a person clicking would. A straight line until
        // stuck walked bots in through a shuttleport's gate and against its far wall.
        if (_assets != null && world.Structures != null && tick >= _cityWalkAt && inCity)
        {
            var reach = (float)Math.Min(distance, CityLegM);
            var heading = Routing.HeadingTo(world.X, world.Z, tx, tz);
            var goal = (world.X + (MathF.Cos(heading) * reach), world.Z + (MathF.Sin(heading) * reach));
            // A walk that ends where the body already stands — its goal walled in, snapped back to here — is no walk: searched again every tick, it
            // would be a search a tick that never moves the bot.
            if (PlanWalk(in world, goal, [], tick, exactGoal: false)
                && Routing.Distance(world.X, world.Z, _leg.Points[^1].X, _leg.Points[^1].Z) > 2d * CornerReachM)
            {
                _leg.Ride = ride;
                _leg.KeepRiding = true;
                _leg.CityWalk = true;
                return Follow(in world, tick, out _);
            }

            _leg.Clear();
            _cityWalkAt = tick + (2 * _stuckTicks);
        }

        if (distance < _travelBest - 1d)
        {
            _travelBest = distance;
            _travelBestAt = tick;
        }
        else if (tick - _travelBestAt > _openStuckTicks && _assets != null && world.Structures != null)
        {
            // Stuck against something: search a way round it toward the city — as far as a search reaches, then nearer, then off to either side. A
            // single goal straight ahead can stand inside the next building, and a search that cannot reach its goal finds nothing at all: the
            // second live run's bot pushed against one wall in Dry Wells for four minutes with every search failing.
            NoteStuck(_travelBestAt, tick);
            NoteLeg(LegWhy.TravelStuck, tick);
            _travelBest = double.MaxValue;
            _travelBestAt = tick;
            var heading = Routing.HeadingTo(world.X, world.Z, tx, tz);
            foreach (var (metres, turn) in Detours)
            {
                var reach = (float)Math.Min(distance, metres);
                var goal = (world.X + (MathF.Cos(heading + turn) * reach), world.Z + (MathF.Sin(heading + turn) * reach));
                if (PlanWalk(in world, goal, [], tick, exactGoal: false))
                {
                    // Round the obstacle on whatever it was travelling on: a rider does not get off to go round a house.
                    _leg.Ride = ride;
                    _leg.KeepRiding = true;
                    NoteLeg(LegWhy.Detour, tick);
                    return Follow(in world, tick, out _);
                }
            }
        }

        var speed = world.Mounted ? Routing.SwoopClass : Routing.SpeedClassOf(TravelMode.Run);
        return Intent.Move(Routing.HeadingTo(world.X, world.Z, tx, tz), speed);
    }

    /// <summary>
    /// The mission held (lair-objectives.md B-3, B-4): ride out to its lair, kill the defenders round it, then break the lair — with a browser's commands
    /// only. Done when the journal no longer lists it; cancelled after <see cref="QuestSeconds"/>.
    /// </summary>
    private Intent Quest(in Perceived world, long tick)
    {
        if (world.MissionsHeld == 0)
        {
            // Done — paid, the journal says it no longer holds it — or cancelled by somebody else's hand: decide again.
            _questSince = -1;
            _wantMounted = false;
            Become(Goal.Choosing, GoalWhy.QuestDone, tick);
            return Choose(in world, tick);
        }

        if (_questSince < 0)
        {
            _questSince = tick;
            _questNear = false;
            _flyNext = false;
            _travelBest = double.MaxValue;
            _travelBestAt = tick;
            _leg.Clear();
        }

        if (tick - _questSince > Ticks(QuestSeconds))
        {
            // Not done in ten minutes: something it cannot reach or cannot kill. Give it back and get on with the day.
            MissionsGivenUp++;
            _questSince = -1;
            _wantMounted = false;
            Become(Goal.Idling, GoalWhy.QuestTimeout, tick);
            _until = tick + Ticks(2);
            return Intent.AbandonMission;
        }

        if (!world.HasMissionLair)
        {
            // Being drawn, or moved after somebody else broke it: wait where it is for the journal to say where.
            _wantMounted = false;
            return Intent.Move(0f, 0);
        }

        var toLair = Routing.Distance(world.X, world.Z, world.MissionLairX, world.MissionLairZ);
        if (toLair > NoticeM)
        {
            if (_questNear)
            {
                // Its lair was drawn again somewhere else: a new ride, whose progress is measured from here, not from the fight.
                _questNear = false;
                _travelBest = double.MaxValue;
                _travelBestAt = tick;
            }

            return GoToward(in world, tick, world.MissionLairX, world.MissionLairZ, toLair, ride: true);
        }

        // There: on foot, and the fight. Its defenders first — the lair cannot be hurt while any stands — then the lair.
        _questNear = true;
        _wantMounted = false;
        _leg.Clear();
        if (world.MissionLair != 0 && world.MissionLairGuards == 0)
        {
            return Engage(world.X, world.Z, world.MissionLair, world.MissionLairX, world.MissionLairZ, (float)toLair, mobile: false);
        }

        if (world.NearestCreature != 0
            && Routing.Distance(world.NearestCreatureX, world.NearestCreatureZ, world.MissionLairX, world.MissionLairZ) <= DefenderM)
        {
            return Engage(world.X, world.Z, world.NearestCreature, world.NearestCreatureX, world.NearestCreatureZ, world.NearestCreatureM, mobile: true);
        }

        // Nothing to shoot in view yet: close on the lair.
        return toLair > StrikeM
            ? Intent.Move(Routing.HeadingTo(world.X, world.Z, world.MissionLairX, world.MissionLairZ), Routing.SpeedClassOf(TravelMode.Run))
            : Intent.Move(0f, 0);
    }

    /// <summary>Flying: to this city's ticket terminal, then the ask.</summary>
    /// <remarks>
    /// <b>The ticket terminal when the content says where it is, as a person buys a ticket there</b> — the walk ends on the terminal's stand point,
    /// inside the boarding radius — and the port itself otherwise, as this brain always did: the ask is refused unless the body is at the port, so the
    /// walk is not optional and the refusal is the feedback if the port has been misremembered.
    /// </remarks>
    private Intent ToTheShuttle(in Perceived world, long tick)
    {
        _wantMounted = false;
        if (_assets != null && !_asked)
        {
            if (!_leg.Active && _leg.Ended == LegEnd.Going && TerminalPath(world.X, world.Z) is { } use)
            {
                if (!PlanWalk(in world, use[0], use[1..], tick))
                {
                    // Abandoned, not cleared: a cleared leg reads as "not yet planned", and the search ran again on every tick until the port was in
                    // range — a whole-grid A* per tick per bot (review 1).
                    _leg.Abandon();
                    NoteLeg(LegWhy.Abandoned, tick);
                }
                else
                {
                    _leg.FinalReach = UseReachM;
                }
            }

            if (_leg.Active || _leg.Ended == LegEnd.Done)
            {
                var step = Follow(in world, tick, out var end);
                if (end == LegEnd.Going)
                {
                    return step;
                }

                if (end == LegEnd.Done && world.PortRegion != 0)
                {
                    return Ask(tick);
                }

                // Stuck on the way to the terminal, or at it and out of range: ask where it stands if that is at the port, else walk there plainly.
                _leg.Abandon();
                NoteLeg(LegWhy.Abandoned, tick);
            }
        }

        if (world.PortRegion != 0 && !_asked)
        {
            return Ask(tick);
        }

        return Intent.Move(Routing.HeadingTo(world.X, world.Z, _portX, _portZ), Routing.SpeedClassOf(TravelMode.Run));
    }

    private Intent Ask(long tick)
    {
        _asked = true;
        _leg.Clear();
        Become(Goal.Waiting, GoalWhy.BoardAsked, tick, (uint)TargetRegion);
        _until = tick + _patienceTicks;
        return Intent.Board(TargetRegion);
    }

    /// <summary>The walk to the ticket terminal of the port it means to fly from, in world metres, ending on the stand point; null when unknown.</summary>
    private (float X, float Z)[] TerminalPath(float x, float z)
    {
        if (!Facilities.TryNearest(Routing.ShuttleportKind, x, z, out var port, out _)
            || Routing.Distance(port.X, port.Z, _portX, _portZ) > 1f
            || _assets.At(port.Asset) is not { UsePath.Length: > 0 } plan)
        {
            return null;
        }

        return Placed(port, plan.UsePath);
    }

    /// <summary>Stands at the port until the shuttle comes, or gives up and travels the ordinary way.</summary>
    /// <param name="world">What the bot was sent.</param>
    /// <param name="tick">The fleet's tick.</param>
    /// <returns>The command.</returns>
    /// <remarks>
    /// <b>Arrival is noticed by position, not by an answer.</b> The server says nothing when a shuttle carries a body:
    /// the body is simply somewhere else, which is what a browser sees too. So the bot watches for having arrived in the
    /// city it asked for, exactly as a person watches the screen.
    /// </remarks>
    private Intent Wait(in Perceived world, long tick)
    {
        // Arrival after a flight is being INSIDE the city asked for, not within ArrivedM of its centre: the server puts
        // the body down at the destination's port, which can be most of a radius from the middle. Walking there is then
        // an ordinary arrival, and the two questions are deliberately different ones.
        if (TargetRegion > 0 && RegionAt(world.X, world.Z) == TargetRegion)
        {
            Become(Goal.Idling, GoalWhy.Landed, tick);
            _until = tick + _idleTicks;
            TargetRegion = 0;
            return Intent.Move(0f, 0);
        }

        // The ask was refused, so there is nothing to wait for. Without this the bot stood at the port for the full
        // patience — four minutes — because the only thing it watched for was a position change that was never coming.
        if (world.BoardRefused)
        {
            Mode = TravelMode.Swoop;
            Become(Goal.Travelling, GoalWhy.BoardRefused, tick);
            return Travel(in world, tick);
        }

        if (tick < _until)
        {
            return Intent.None;   // standing at the port, which is what waiting looks like on the wire
        }

        // Patience spent. Ride instead, and do not ask again on this trip.
        Mode = TravelMode.Swoop;
        Become(Goal.Travelling, GoalWhy.ShuttlePatienceOut, tick);
        return Travel(in world, tick);
    }

    /// <summary>Starts an errand to the nearest mission terminal or bazaar it knows, if there is one within reach.</summary>
    private bool StartVisit(in Perceived world, long tick)
    {
        var kind = Next() % 100u < 60u ? Routing.MissionTerminalKind : Routing.BazaarKind;
        _visitKind = kind;
        if (!Facilities.TryNearest(kind, world.X, world.Z, out var found, out _)
            || Routing.Distance(world.X, world.Z, found.X, found.Z) > ErrandReachM
            || _assets.At(found.Asset) is not { } plan)
        {
            return false;
        }

        var use = plan.UsePath.Length > 0 ? Placed(found, plan.UsePath) : [(found.X, found.Z)];
        if (!PlanWalk(in world, use[0], use[1..], tick))
        {
            return false;
        }

        _leg.FinalReach = UseReachM;
        _visitAt = (found.X, found.Z);

        Become(Goal.Visiting, GoalWhy.ErrandStarted, tick, (uint)kind, (float)Routing.Distance(world.X, world.Z, found.X, found.Z));
        _using = false;
        return true;
    }

    /// <summary>The errand: walk (or ride) there, use it for a while, then usually fly on.</summary>
    private Intent Visit(in Perceived world, long tick)
    {
        if (_using)
        {
            if (tick < _until)
            {
                return Intent.None;   // at the terminal: what using one looks like on the wire
            }

            // Loïc's errand: a mission terminal, then the shuttleport to go somewhere else. Most of the time, not every time.
            _flyNext = Next() % 100u < 60u;
            Become(Goal.Choosing, GoalWhy.VisitDone, tick);
            return Choose(in world, tick);
        }

        var step = Follow(in world, tick, out var end);
        if (end == LegEnd.Stuck && Routing.Distance(world.X, world.Z, _visitAt.X, _visitAt.Z) <= TerminalUseFromM)
        {
            // Stuck short of the stand point, in the crowd round the terminal: used from here.
            end = LegEnd.Done;
        }

        switch (end)
        {
            case LegEnd.Going:
                return step;
            case LegEnd.Done:
                FacilitiesUsed++;
                _using = true;
                _wantMounted = false;
                _until = tick + Ticks(5 + (Next() % 6u));

                // At a mission terminal, holding none: take one, as a person does at the list (lair-objectives.md B-1). The swarm names the offer.
                if (_visitKind == Routing.MissionTerminalKind && world.MissionsHeld == 0)
                {
                    MissionsAsked++;
                    return Intent.TakeMission;
                }

                return Intent.None;
            default:
                Become(Goal.Idling, GoalWhy.VisitFailed, tick);
                _until = tick + Ticks(2);
                return Intent.Move(0f, 0);
        }
    }

    /// <summary>Starts a flight from the city it is in: the nearest known port's terminal, to any other city.</summary>
    private bool StartFlight(in Perceived world, int here, long tick)
    {
        if (!Facilities.TryFind(here, Routing.ShuttleportKind, out var port))
        {
            return false;
        }

        TargetRegion = OtherCity(here) + 1;
        Mode = TravelMode.Shuttle;
        _portX = port.X;
        _portZ = port.Z;
        _asked = false;
        _leg.Clear();
        Become(Goal.Travelling, GoalWhy.FlightChosen, tick, (uint)TargetRegion);
        return true;
    }

    /// <summary>Picks one of the nearest buildings with a door and starts the walk to it.</summary>
    private bool StartEntering(in Perceived world, long tick)
    {
        if (world.Structures == null)
        {
            return false;
        }

        _seen.Clear();
        world.Structures.CopyTo(_seen);
        // One of the five nearest, so a street's bots do not all queue at the same door. Chosen once a goal, so a sort is not a cost.
        var doors = new List<(double D, int I)>();
        for (var i = 0; i < _seen.Count; i++)
        {
            var s = _seen[i];
            var d = Routing.Distance(world.X, world.Z, s.X, s.Z);
            if (d <= DoorReachM && _assets.At(s.Asset)?.Door != null)
            {
                doors.Add((d, i));
            }
        }

        if (doors.Count == 0)
        {
            return false;
        }

        doors.Sort();
        var building = _seen[doors[(int)(Next() % (uint)Math.Min(5, doors.Count))].I];
        var door = _assets.At(building.Asset).Door;
        var path = Placed(Facility.Of(building.X, building.Z, building.Asset, building.Yaw), door.Approach);
        if (!PlanWalk(in world, path[0], path[1..], tick))
        {
            return false;
        }

        Become(Goal.Indoors, GoalWhy.DoorChosen, tick);
        _leaving = false;
        return true;
    }

    /// <summary>On the planet, going in: the walk to the door and through it. The crossing ends it — the realm changes under it.</summary>
    private Intent Enter(in Perceived world, long tick)
    {
        var step = Follow(in world, tick, out var end);
        if (end == LegEnd.Going)
        {
            return step;
        }

        // The path ran out without a crossing, or stuck short of the door: give up on this one. Told how (the subject: 1 walked to its end, 2 stuck) and
        // how far short of the door's last point, which is what says whether a door fails by its crowd or by its geometry.
        var left = _leg.Points.Count > 0 ? Routing.Distance(world.X, world.Z, _leg.Points[^1].X, _leg.Points[^1].Z) : 0d;
        Become(Goal.Idling, GoalWhy.EnterFailed, tick, (uint)end, (float)left);
        _until = tick + Ticks(2);
        return Intent.Move(0f, 0);
    }

    /// <summary>Inside a room: a look round on the room's graph, a while standing, then out by the published way.</summary>
    private Intent Inside(in Perceived world, long tick)
    {
        if (RoomOf(in world, tick) is not { } room)
        {
            return Intent.None;   // a room this content does not know: stand, as this brain always did indoors
        }

        if (_leg.Active)
        {
            var step = Follow(in world, tick, out var end);
            if (end == LegEnd.Going)
            {
                return step;
            }

            _leg.Clear();
            if (_leaving)
            {
                // Walked the way out, or stuck on it, and still inside: try again after a pause rather than planning it again every tick (review 4).
                _leaving = false;
                _until = tick + Ticks(2);
            }
        }

        if (tick < _until)
        {
            return Intent.None;
        }

        var (plan, at) = room;
        var here = BotAssets.Unplace(at.X, at.Z, at.Yaw, (world.X, world.Z));
        if (!_leaving && _until > 0 && plan.Nav.Nodes.Count > 0 && Next() % 100u < 50u)
        {
            // A look round: the room's graph to one of its nodes, then another pause.
            var node = plan.Nav.Nodes[(int)(Next() % (uint)plan.Nav.Nodes.Count)];
            if (plan.Nav.Route(here.X, here.Z, node.X, node.Z) is { } wander)
            {
                _leg.Start(Shift(wander, at), 0, tick);
                _until = tick + Ticks(3 + (Next() % 5u));
                return Follow(in world, tick, out _);
            }
        }

        // Out: the graph to the way out's first point, then the way out itself, whose last point is past the door.
        var exit = plan.Door.Approach;
        var route = plan.Nav.Route(here.X, here.Z, exit[0].X, exit[0].Z) ?? [exit[0]];
        for (var i = 1; i < exit.Length; i++)
        {
            route.Add(exit[i]);
        }

        _leaving = true;
        _until = 0;
        _leg.Start(Shift(route, at), 0, tick);
        return Follow(in world, tick, out _);
    }

    /// <summary>The room it is standing in: the structure in view whose asset publishes a floor graph and a way out — found once per stay.</summary>
    /// <remarks>
    /// Kept for the stay and dropped at the next crossing: the realm only changes at a door, and copying and scanning every structure in view on every
    /// tick indoors found the same room each time (review). A room not found yet is looked for again every two seconds, not every tick.
    /// </remarks>
    private (AssetPlan Plan, Facility At)? RoomOf(in Perceived world, long tick)
    {
        if (_room is { } known)
        {
            return known;
        }

        if (_assets == null || world.Structures == null || tick < _roomSearchAt)
        {
            return null;
        }

        _roomSearchAt = tick + Ticks(2);
        _seen.Clear();
        world.Structures.CopyTo(_seen);
        foreach (var s in _seen)
        {
            if (_assets.At(s.Asset) is { Nav: not null, Door: not null } plan)
            {
                _room = (plan, Facility.Of(s.X, s.Z, s.Asset, s.Yaw));
                return _room;
            }
        }

        return null;
    }

    /// <summary>The room this stay is in, once found; see <see cref="RoomOf"/>.</summary>
    private (AssetPlan Plan, Facility At)? _room;

    private long _roomSearchAt;

    /// <summary>A room's points (in its own frame) moved to where it stands.</summary>
    private static List<(float X, float Z)> Shift(List<(float X, float Z)> points, Facility at)
    {
        for (var i = 0; i < points.Count; i++)
        {
            points[i] = BotAssets.Place(at.X, at.Z, at.Yaw, points[i]);
        }

        return points;
    }

    /// <summary>Plans a walk to a point and on along a published path, round the buildings; false when there is no way.</summary>
    /// <param name="world">What the bot was sent.</param>
    /// <param name="goal">Where the open-ground part ends: a door's or a terminal's first published point.</param>
    /// <param name="tail">The published points after it, walked exactly.</param>
    /// <param name="tick">The fleet's tick.</param>
    /// <param name="exactGoal">
    /// Whether the goal itself must be reached (a door's or a terminal's first point) even when the grid has it inside a wall, or only somewhere open
    /// near it (a detour's point, which is only a direction).
    /// </param>
    private bool PlanWalk(in Perceived world, (float X, float Z) goal, ReadOnlySpan<(float X, float Z)> tail, long tick, bool exactGoal = true)
    {
        var points = new List<(float X, float Z)>();
        var far = Routing.Distance(world.X, world.Z, goal.X, goal.Z);
        var searching = world.Structures != null && _assets != null;
        if (searching)
        {
            _seen.Clear();
            world.Structures.CopyTo(_seen);
        }

        var from = (X: world.X, Z: world.Z);
        if (searching && far > CityLegM)
        {
            // Too far to see the way: the next stretch searched now, and the rest from where that stretch ends — with the buildings in view from there.
            // An errand used to be searched out of its street and into its goal's, and walked straight between: across a city that line went in
            // through a shuttleport's gate, and the errand bots stood trapped in its plaza (live, 2026-10-26).
            var heading = Routing.HeadingTo(world.X, world.Z, goal.X, goal.Z);
            if (SearchToward(from, heading, CityLegM) is not { } stretch)
            {
                return false;
            }

            _leg.Start(stretch, stretch.Count, tick);
            _leg.Ride = far / Routing.RunMps > Routing.RideOverSeconds;
            _leg.Rest = new LegRest(goal, tail.ToArray(), exactGoal);
            Sight(from);
            NoteLeg(LegWhy.Planned, tick);
            return true;
        }

        if (searching)
        {
            // A point that is only a direction (a detour's, a stretch of a crossing) is searched short of any shuttleport's plaza it lands in.
            var into = exactGoal
                ? Search(from, goal)
                : SearchToward(from, Routing.HeadingTo(from.X, from.Z, goal.X, goal.Z), (float)far);
            if (into == null)
            {
                return false;
            }

            points.AddRange(into);
            if (exactGoal && points[^1] != goal)
            {
                points.Add(goal);
            }
        }
        else
        {
            points.Add(goal);
        }

        // A searched point that is only a direction is not an exact one: the open part runs to the end, and the end is passed rather than stood on.
        var precise = searching && !exactGoal ? points.Count : points.Count - 1;
        foreach (var p in tail)
        {
            points.Add(p);
        }

        _leg.Start(points, precise, tick);
        _leg.Ride = far / Routing.RunMps > Routing.RideOverSeconds;
        if (searching)
        {
            Sight(from);
        }

        NoteLeg(LegWhy.Planned, tick);
        return true;
    }

    /// <summary>How far round a searched walk its walls are kept, metres: a body pushed off the walk, or cutting a corner, stays within this of it.</summary>
    private const float SightMarginM = 12f;

    /// <summary>Keeps the walls round the walk just planned, grown by a body and <see cref="CutClearanceM"/>: what a corner is cut against.</summary>
    /// <param name="from">Where the body stands, which is where the walk starts.</param>
    private void Sight((float X, float Z) from)
    {
        var leg = _leg;
        var (minX, minZ, maxX, maxZ) = (from.X, from.Z, from.X, from.Z);
        foreach (var p in leg.Points)
        {
            (minX, maxX) = (MathF.Min(minX, p.X), MathF.Max(maxX, p.X));
            (minZ, maxZ) = (MathF.Min(minZ, p.Z), MathF.Max(maxZ, p.Z));
        }

        CityPath.CollectWalls(
            _seen, _assets, minX - SightMarginM, minZ - SightMarginM, maxX + SightMarginM, maxZ + SightMarginM, leg.Walls, CityPath.BodyHalfM + CutClearanceM);
        leg.HasWalls = true;
        leg.Origin = from;
    }

    /// <summary>
    /// A searched walk toward a direction, as far as <paramref name="metres"/> along it — or nearer when the walk would end inside a shuttleport's walls
    /// or where it started; null when no length finds one.
    /// </summary>
    /// <remarks>
    /// A point ahead on a port's raised pad is walled in, and the search moves an end it cannot reach to the nearest open ground — the plaza, through
    /// the gate. The end is what is checked, not the point asked for, because the snap is what goes wrong.
    /// </remarks>
    private List<(float X, float Z)> SearchToward((float X, float Z) from, float heading, float metres)
    {
        foreach (var share in StretchShares)
        {
            var reach = metres * share;
            var to = (from.X + (MathF.Cos(heading) * reach), from.Z + (MathF.Sin(heading) * reach));
            if (Search(from, to) is { } walk
                && Routing.Distance(from.X, from.Z, walk[^1].X, walk[^1].Z) > 2d * CornerReachM
                && !InAPortsWalls(walk[^1]))
            {
                return walk;
            }
        }

        return null;
    }

    /// <summary>How much of a stretch is tried, in order, when the whole of it ends inside a port's walls.</summary>
    private static readonly float[] StretchShares = [1f, 0.75f, 0.5f, 0.3f];

    /// <summary>Whether a point is inside one of the shuttleports in view, or in its gate.</summary>
    private bool InAPortsWalls((float X, float Z) p)
    {
        foreach (var s in _seen)
        {
            if (s.Kind == Routing.ShuttleportKind && _assets.At(s.Asset) is { HasEnclosure: true } plan
                && Routing.Distance(p.X, p.Z, s.X, s.Z) < 30d && BotAssets.Unplace(s.X, s.Z, s.Yaw, p) is var local && plan.Within(local.X, local.Z))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A searched walk between two points over the structures last copied into <see cref="_seen"/>; null when walled off.</summary>
    private List<(float X, float Z)> Search((float X, float Z) from, (float X, float Z) to)
    {
        // The window, the cell size and the porch rule are CityPath.Route's, which the server's scripted crowd walks with too.
        PathsPlanned++;
        return CityPath.Route(_seen, _assets, from, to, MaxExpansions);
    }

    /// <summary>Follows the leg: the next point, how fast, and whether it has ended.</summary>
    /// <param name="world">What the bot was sent.</param>
    /// <param name="tick">The fleet's tick.</param>
    /// <param name="end">Whether the leg is still going, was walked to its end, or stuck.</param>
    /// <returns>The command.</returns>
    /// <remarks>
    /// <para>
    /// <b>On foot, the open part is RUN: <c>MoveDir</c> at the next point, every decision, and a corner run past rather than stood on.</b> A
    /// <c>MoveTo</c> is "go there and stop": sent for every corner, with a corner called reached from 0.75 m and a decision every 2.5 m of running, the
    /// server stopped the body at most corners before the next point was sent — a bot walking a city stopped and started at every one (Loïc,
    /// 2026-10-26). A corner nearer than one decision's run is run at when the run carries the body past it in the clear — a straight step that enters
    /// none of the walk's walls grown by a body and <see cref="CutClearanceM"/> (<see cref="CityPath.SegmentClear"/>) — and passed once the body is
    /// beyond it; walked onto with <c>MoveTo</c>, the one stop left, only when even that step would touch a wall. A point is also passed when the one
    /// after it is in sight (<see cref="Passes"/>).
    /// </para>
    /// <para>
    /// <b><c>MoveTo</c> for the exact part, because the server walks to it and stops on it.</b> A heading renewed a few times a second overshoots a door
    /// by a body's length at a run; a destination does not. It is a destination, so it is sent when the point changes and refreshed every two seconds in
    /// case one was dropped — not every tick. So is every point of a walk with no walls in hand (a room's graph, a port's approach walked backwards).
    /// </para>
    /// <para>
    /// <b>Ridden in the open, walked at the end.</b> A ridden leg is <c>MoveDir</c> at the swoop's class while the point is more than fifteen metres off,
    /// and gets off for the published points — a door, a terminal — as a person would.
    /// </para>
    /// </remarks>
    private Intent Follow(in Perceived world, long tick, out LegEnd end)
    {
        var leg = _leg;
        if (!leg.Active)
        {
            // No leg is not a finished leg: a cleared one reported Done, and a caller counting arrivals would have counted one it never made
            // (review 7). A leg that ended says how.
            end = leg.Ended == LegEnd.Going ? LegEnd.Stuck : leg.Ended;
            return Intent.None;
        }

        // Run by sight: on foot, with the walk's walls in hand. A rider steers as it always did (review 8).
        var running = leg.HasWalls && !world.Mounted;
        var stride = Routing.RunMps / _rate;
        var p = leg.Points[leg.Next];
        var d = Routing.Distance(world.X, world.Z, p.X, p.Z);
        while (true)
        {
            // The last point closest of all: a door's is past its plane, and calling it reached a little short would be calling a crossing that
            // never fired. The server stops a MoveTo on its point, so a body gets there.
            var exact = leg.Next >= leg.Precise;
            var last = leg.Next == leg.Points.Count - 1;
            // Every path point to a body's length, ridden or not: a rider who called a corner reached from eight metres off cut through the house the
            // corner went round — the first live run's stuck bots, all of them pressed against a wall between two waypoints. Run, the end of the open
            // part (a stretch's end, a direction's) is passed within a stride: it is where the next search starts, not a place to stand.
            var reach = exact ? (last && leg.Rest == null ? leg.FinalReach : 0.6d)
                : running && last ? stride
                : last && leg.Rest == null ? leg.FinalReach : CornerReachM;
            if (d > reach && !(running && !exact && !last && Passes(in world, d, stride)))
            {
                break;
            }

            leg.Next++;
            NoteLeg(LegWhy.Waypoint, tick);
            leg.Best = double.MaxValue;
            leg.BestAt = tick;
            if (!leg.Active && leg.Rest != null)
            {
                // A stretch walked: the next one, searched from here.
                return GoOn(in world, tick, out end, replans: 0);
            }

            if (!leg.Active)
            {
                leg.Ended = LegEnd.Done;
                NoteLeg(LegWhy.Done, tick);
                end = LegEnd.Done;

                // Off at the end of a walk — except a detour's, which hands back to a ride still under way.
                _wantMounted = leg.KeepRiding && leg.Ride;
                return Intent.None;
            }

            p = leg.Points[leg.Next];
            d = Routing.Distance(world.X, world.Z, p.X, p.Z);
        }

        // Progress is the walk left — to the next point and on along the rest — so a corner cut, which moves the body off the next point's bearing, counts.
        var left = d + leg.LengthTo(leg.Next, leg.Points.Count - 1);
        if (left < leg.Best - 0.5d)
        {
            leg.Best = left;
            leg.BestAt = tick;
        }
        else if (tick - leg.BestAt > (leg.Next < leg.Precise ? _openStuckTicks : _stuckTicks))
        {
            NoteStuck(leg.BestAt, tick);
            // Stuck on the open-ground part: search again from where it stands, twice, before the goal gives up. The published points are walked
            // exactly and are not searched — stuck there is a door or a terminal in the way, which a new search would not change.
            if (leg.Next < leg.Precise && leg.Replans < 2 && leg.Rest == null)
            {
                // To the exact part's first point, or — a walk whose end is only a direction — to that end, as loosely as it was asked.
                var exactGoal = leg.Precise < leg.Points.Count;
                var goal = leg.Points[exactGoal ? leg.Precise : leg.Points.Count - 1];
                var tail = exactGoal ? leg.Points.GetRange(leg.Precise + 1, leg.Points.Count - leg.Precise - 1).ToArray() : [];
                var replans = leg.Replans + 1;
                var carried = Carry();
                if (PlanWalk(in world, goal, tail, tick, exactGoal))
                {
                    Restore(carried);
                    _leg.Replans = replans;
                    NoteLeg(LegWhy.Replanned, tick);
                    return Follow(in world, tick, out end);
                }
            }
            else if (leg.Rest != null && leg.Replans < 2)
            {
                // Stuck on a stretch of a long walk: the rest of the way searched again from where it stands.
                return GoOn(in world, tick, out end, replans: leg.Replans + 1);
            }

            NoteLeg(LegWhy.Stuck, tick);
            leg.Ended = LegEnd.Stuck;
            leg.Next = leg.Points.Count;
            end = LegEnd.Stuck;
            _wantMounted = leg.KeepRiding && leg.Ride;
            return Intent.Move(0f, 0);
        }

        end = LegEnd.Going;
        // Off once, for good, forty metres before the exact part — by distance along the walk, not by which point is next: deciding per waypoint got on
        // and off at every corner of a street, and getting off when the next point was the exact one walked the last 150 m of a long straight at a run.
        // Not for a detour, whose end is only a direction: getting off before it and back on after cost two SetMounts a detour (review 9).
        if (leg.Ride && !leg.KeepRiding && !leg.Dismounted && leg.Rest == null && d + leg.LengthTo(leg.Next, leg.Precise) < 40d)
        {
            leg.Dismounted = true;
        }

        // Steered at the swoop's class only while the point is further than a decision and a half at its speed: inside that, a heading renewed at
        // the driver's rate overshoots the corner (review 8). Fifteen metres at the 4 Hz driver.
        _wantMounted = leg.Ride && !leg.Dismounted;
        if (world.Mounted && !leg.Dismounted && d > Math.Max(15d, 1.5d * Routing.SwoopMps / _rate))
        {
            return Intent.Move(Routing.HeadingTo(world.X, world.Z, p.X, p.Z), Routing.SwoopClass);
        }

        if (running && leg.Next < leg.Precise)
        {
            // Run at it — renewed every decision, as a held key is — unless it is a corner nearer than a stride that cannot be cut yet and the stride
            // past it would touch a wall: then walked onto, and the next decision runs on from there.
            var heading = Routing.HeadingTo(world.X, world.Z, p.X, p.Z);
            var ahead = (X: world.X + (MathF.Cos(heading) * (float)stride), Z: world.Z + (MathF.Sin(heading) * (float)stride));
            if (d > stride || CityPath.SegmentClear(leg.Walls, world.X, world.Z, ahead.X, ahead.Z))
            {
                leg.Sent = (float.NaN, float.NaN);
                return Intent.Move(heading, Routing.SpeedClassOf(TravelMode.Run));
            }
        }

        if (leg.Sent != p || tick - leg.SentAt >= Ticks(2))
        {
            leg.Sent = p;
            leg.SentAt = tick;
            return Intent.MoveTo(p.X, p.Z);
        }

        return Intent.None;
    }

    /// <summary>
    /// Whether the open point the body is heading for is passed already, on foot: the body is within a stride of it and past it along the walk's line to
    /// it, or the point after it is in sight (<see cref="CityPath.SegmentClear"/>).
    /// </summary>
    /// <remarks>
    /// Running past is how a corner is passed: a searched walk is pulled taut, so its corners stand at the walls and the step to the point after one
    /// clears its wall only from a few tenths of a metre off. Sight is for a body off its walk — shoved by the crowd past a corner, or a walk whose point
    /// was only its way out of a wall's margin — which otherwise ran back to a point it no longer needed.
    /// </remarks>
    private bool Passes(in Perceived world, double d, double stride)
    {
        var leg = _leg;
        var p = leg.Points[leg.Next];
        var after = leg.Points[leg.Next + 1];
        var from = leg.Next > 0 ? leg.Points[leg.Next - 1] : leg.Origin;
        if (d <= stride && (((world.X - p.X) * (p.X - from.X)) + ((world.Z - p.Z) * (p.Z - from.Z))) > 0f)
        {
            return true;
        }

        // A walk's first point behind the body on the way to the next: the open cell its search started from, not somewhere to go back to (StepOutM).
        if (leg.Next == 0 && d <= StepOutM && (((world.X - p.X) * (after.X - p.X)) + ((world.Z - p.Z) * (after.Z - p.Z))) > 0f)
        {
            return true;
        }

        return CityPath.SegmentClear(leg.Walls, world.X, world.Z, after.X, after.Z);
    }

    /// <summary>What a walk searched again carries over from the one it replaces: ridden, kept riding, got off, a city's, and its last reach.</summary>
    private (bool Ride, bool KeepRiding, bool Dismounted, bool CityWalk, double FinalReach) Carry() =>
        (_leg.Ride, _leg.KeepRiding, _leg.Dismounted, _leg.CityWalk, _leg.FinalReach);

    private void Restore((bool Ride, bool KeepRiding, bool Dismounted, bool CityWalk, double FinalReach) carried) =>
        (_leg.Ride, _leg.KeepRiding, _leg.Dismounted, _leg.CityWalk, _leg.FinalReach) = carried;

    /// <summary>
    /// The rest of a walk planned a stretch at a time, searched from where the body stands: what the walk was riding, keeping and reaching for carries
    /// over, so the last stretch ends as the walk was asked to.
    /// </summary>
    private Intent GoOn(in Perceived world, long tick, out LegEnd end, int replans)
    {
        var rest = _leg.Rest;
        var carried = Carry();
        if (!PlanWalk(in world, rest.Goal, rest.Tail, tick, rest.ExactGoal))
        {
            _leg.Abandon();
            NoteLeg(LegWhy.Abandoned, tick);
            end = LegEnd.Stuck;
            _wantMounted = carried.KeepRiding && carried.Ride;
            return Intent.Move(0f, 0);
        }

        _leg.Replans = replans;
        Restore(carried);
        NoteLeg(replans > 0 ? LegWhy.Replanned : LegWhy.Stretch, tick);
        return Follow(in world, tick, out end);
    }

    /// <summary>Gets on or off the swoop when the goal's wish and the server's state disagree — a few asks, then it stops asking.</summary>
    /// <remarks>
    /// <b>Bounded, because the server may refuse</b> (in a room, waiting for a shuttle): three asks two seconds apart, and the goal moves at a run
    /// meanwhile. The count resets when the wish changes.
    /// </remarks>
    private Intent? MountIntent(in Perceived world, long tick)
    {
        // In a city, on foot — always (Loïc, 2026-10-26: a rider cannot navigate its streets). The swoop is for the open desert: a ride into a city
        // ends at its edge, and one out of it starts there. The goals keep wishing as they did; this is the one place a wish reaches the wire.
        if (_wantMounted && world.Realm == 0 && RegionAt(world.X, world.Z) != 0)
        {
            _wantMounted = false;
        }

        if (_wantMounted == world.Mounted)
        {
            _mountAsks = 0;
            return null;
        }

        if (_mountAsks >= 3 || tick - _mountAskedAt < Ticks(2))
        {
            return null;
        }

        _mountAsks++;
        _mountAskedAt = tick;
        MountsAsked++;
        return Intent.Mount(_wantMounted);
    }

    /// <summary>
    /// Out of a shuttleport's plaza by its gate: a searched walk to where the port's published approach starts, outside it, then the goal is chosen again.
    /// Null when it is not inside one, or is there to fly — walking to the ticket terminal, or queued.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Its own leg, then a fresh choice.</b> The goal under way had a leg of its own, which this one replaced; carrying on with that goal would read
    /// the exit's end as its leg's, and an errand would use a terminal it never reached.
    /// </para>
    /// <para>
    /// <b>Searched, with the approach walked backwards as the fallback</b> — the published way in, so a way out that exists whatever the search makes of
    /// the plaza. A bot that sticks even so stands where it is for ten seconds before trying again, so a bot that cannot get out costs a search every
    /// ten seconds rather than one a tick.
    /// </para>
    /// </remarks>
    private Intent? LeavePort(in Perceived world, long tick)
    {
        if (_leavingPort)
        {
            var step = Follow(in world, tick, out var end);
            if (end == LegEnd.Going)
            {
                return step;
            }

            // Out, or stuck on the way: the goal is chosen again from here either way, and the next check says whether it is still inside.
            _leavingPort = false;
            _leg.Clear();
            Become(Goal.Choosing, GoalWhy.PortExited, tick);
            if (end == LegEnd.Stuck)
            {
                _portCheckAt = tick + Ticks(10);
            }

            return null;
        }

        if (_assets == null || tick < _portCheckAt)
        {
            return null;
        }

        _portCheckAt = tick + Ticks(PortCheckSeconds);
        if (Goal == Goal.Waiting || (Goal == Goal.Travelling && Mode == TravelMode.Shuttle && !_asked)
            || !Facilities.TryNearest(Routing.ShuttleportKind, world.X, world.Z, out var port, out _)
            || _assets.At(port.Asset) is not { HasEnclosure: true, UsePath.Length: > 0 } plan
            || BotAssets.Unplace(port.X, port.Z, port.Yaw, (world.X, world.Z)) is var local && !plan.Encloses(local.X, local.Z))
        {
            InPort = false;
            return null;
        }

        InPort = true;

        var approach = Placed(port, plan.UsePath);
        if (!PlanWalk(in world, approach[0], [], tick))
        {
            var back = new List<(float X, float Z)>(approach);
            back.Reverse();
            _leg.Start(back, 0, tick);
        }

        _leavingPort = true;
        PortsLeft++;
        return Follow(in world, tick, out _);
    }

    /// <summary>A published path placed where its structure stands.</summary>
    private static (float X, float Z)[] Placed(Facility at, (float X, float Z)[] local)
    {
        var placed = new (float X, float Z)[local.Length];
        for (var i = 0; i < local.Length; i++)
        {
            placed[i] = BotAssets.Place(at.X, at.Z, at.Yaw, local[i]);
        }

        return placed;
    }

    private long Ticks(double seconds) => Math.Max(1L, (long)Math.Round(seconds * _rate));

    /// <summary>The REGION whose city disc contains a point — a city plus one — or 0 out in the desert.</summary>
    /// <param name="x">East.</param>
    /// <param name="z">North.</param>
    /// <returns>The region, or 0.</returns>
    /// <remarks>
    /// The disc and not <see cref="ArrivedM"/>, because this answers "which city am I in", which is what a route's
    /// first leg and a flight's arrival both need. Arrival on foot is a different question and uses the tighter one.
    /// </remarks>
    public int RegionAt(float x, float z)
    {
        for (var i = 0; i < _cities.Length; i++)
        {
            if (Routing.Distance(x, z, _cities[i].X, _cities[i].Z) <= _cities[i].Radius)
            {
                return i + 1;
            }
        }

        return 0;
    }

    /// <summary>xorshift32: deterministic, allocation-free, and owned by this bot alone.</summary>
    /// <returns>The next value.</returns>
    private uint Next()
    {
        _rng ^= _rng << 13;
        _rng ^= _rng >> 17;
        _rng ^= _rng << 5;
        return _rng;
    }

    /// <summary>How a leg ended.</summary>
    private enum LegEnd
    {
        Going = 0,
        Done,
        Stuck,
    }

    /// <summary>The walk in progress: its points, the next one, where the exact part starts, and the watchdog's best.</summary>
    private sealed class Leg
    {
        public readonly List<(float X, float Z)> Points = [];
        public int Next;
        public int Precise;
        public bool Ride;
        public bool Dismounted;

        /// <summary>A detour's: ridden to its end, which is only a direction, and not got off before it.</summary>
        public bool KeepRiding;

        /// <summary>A walk across a city toward somewhere beyond it: dropped at the city's edge, where the open desert starts.</summary>
        public bool CityWalk;

        /// <summary>
        /// The walls round the walk, grown by a body and <see cref="CutClearanceM"/>, when it was searched (<see cref="HasWalls"/>): what a corner is cut
        /// against. None for a walk on a published graph — a room's, a port's approach walked backwards — which is walked point by point.
        /// </summary>
        public readonly List<PlanBox> Walls = [];

        public bool HasWalls;

        /// <summary>Where the walk started: the line to its first point starts here.</summary>
        public (float X, float Z) Origin;

        /// <summary>The walk from point <paramref name="from"/> to point <paramref name="to"/> along the leg, metres.</summary>
        public double LengthTo(int from, int to)
        {
            var sum = 0d;
            for (var i = Math.Max(0, from); i < Math.Min(to, Points.Count - 1); i++)
            {
                sum += Routing.Distance(Points[i].X, Points[i].Z, Points[i + 1].X, Points[i + 1].Z);
            }

            return sum;
        }
        public double Best = double.MaxValue;
        public long BestAt;
        public (float X, float Z) Sent = (float.NaN, float.NaN);
        public long SentAt;
        public LegEnd Ended;
        public int Replans;
        public double FinalReach = 0.3d;

        /// <summary>Where the walk goes on to once these points are walked, when it was too long to search in one go; null for a walk searched whole.</summary>
        public LegRest Rest;

        public bool Active => Next < Points.Count;

        public void Start(List<(float X, float Z)> points, int precise, long tick)
        {
            Points.Clear();
            Points.AddRange(points);
            Next = 0;
            Precise = precise;
            Ride = false;
            Dismounted = false;
            KeepRiding = false;
            CityWalk = false;
            Walls.Clear();
            HasWalls = false;
            Best = double.MaxValue;
            BestAt = tick;
            Sent = (float.NaN, float.NaN);
            Ended = LegEnd.Going;
            Replans = 0;
            FinalReach = 0.3d;
            Rest = null;
        }

        public void Clear()
        {
            Points.Clear();
            Next = 0;
            Ride = false;
            Dismounted = false;
            KeepRiding = false;
            CityWalk = false;
            Ended = LegEnd.Going;
            Rest = null;
        }

        /// <summary>Over, for good: nothing more is followed and nothing restarts it.</summary>
        public void Abandon()
        {
            Points.Clear();
            Next = 0;
            Ended = LegEnd.Stuck;
            Rest = null;
        }
    }

    /// <summary>The rest of a walk planned a stretch at a time: its goal, the published points after it, and whether the goal must be reached exactly.</summary>
    private sealed record LegRest((float X, float Z) Goal, (float X, float Z)[] Tail, bool ExactGoal);
}

/// <summary>A city on the bot's own map: where it is and how big it is.</summary>
/// <param name="Name">What it is called, for a log line.</param>
/// <param name="X">Centre, east.</param>
/// <param name="Z">Centre, north.</param>
/// <param name="Radius">Its disc, metres.</param>
/// <remarks>
/// <b>Static content the bot ships with, exactly as a client ships the planet map.</b> A player knows where Mos Eisley
/// is without standing in it, and so does a bot; what it does NOT ship is what is inside a city, which it has to be sent
/// or walk up to. See <see cref="FacilityMap"/>.
/// </remarks>
public readonly record struct City(string Name, float X, float Z, float Radius);
