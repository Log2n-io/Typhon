using System;

namespace SwgTatooine.Bots.Brain;

/// <summary>What a bot is currently trying to do.</summary>
public enum Goal
{
    /// <summary>Deciding, which it does for exactly one tick.</summary>
    Choosing = 0,

    /// <summary>Going to a city.</summary>
    Travelling,

    /// <summary>Killing what is around a lair.</summary>
    Hunting,

    /// <summary>Standing somewhere plausible for a while.</summary>
    Idling,

    /// <summary>Waiting for a shuttle it has queued for.</summary>
    Waiting,
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
/// </remarks>
public sealed class BotBrain
{
    /// <summary>How close counts as arrived, in metres.</summary>
    /// <remarks>
    /// A city's radius would be too coarse — a bot would stop at the wall and call it arrival — and a metre would be too
    /// fine for a server that integrates positions per tick: a bot would orbit its destination forever. 24 m is about
    /// five seconds of running, and it is inside every city.
    /// </remarks>
    public const float ArrivedM = 24f;

    /// <summary>How close a creature has to be before a bot decides to go for it, in metres.</summary>
    /// <remarks>
    /// A player's sphere is 192 m, so anything in view is within that; this is the range at which a bot bothers. Smaller
    /// than the sphere on purpose: a bot that chased everything it could see would never travel anywhere, and a planet
    /// of bots all converging on whatever is closest is not a population, it is a crowd.
    /// </remarks>
    public const float NoticeM = 120f;

    /// <summary>How close a bot gets before attacking, in metres — inside the server's own weapon reach.</summary>
    public const float StrikeM = 20f;

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

    private readonly City[] _cities;
    private readonly long _idleTicks;
    private readonly long _patienceTicks;
    private uint _rng;

    /// <summary>A brain for one bot.</summary>
    /// <param name="cities">The map it ships with: where the cities are.</param>
    /// <param name="seed">Its own seed; two bots with the same seed behave identically.</param>
    /// <param name="ticksPerSecond">How often this bot is driven, which is what its timed goals are measured in.</param>
    public BotBrain(City[] cities, uint seed, double ticksPerSecond = 4d)
    {
        ArgumentNullException.ThrowIfNull(cities);
        _cities = cities;
        _rng = seed == 0 ? 0x9E3779B9u : seed;   // xorshift is dead at zero, and a caller numbering bots from 0 would hand it one
        var rate = ticksPerSecond > 0d ? ticksPerSecond : 1d;
        _idleTicks = Math.Max(1L, (long)Math.Round(IdleSeconds * rate));
        _patienceTicks = Math.Max(1L, (long)Math.Round(ShuttlePatienceSeconds * rate));
        Facilities = new FacilityMap();
    }

    /// <summary>What this bot has learned about the cities.</summary>
    public FacilityMap Facilities { get; }

    /// <summary>What it is doing.</summary>
    public Goal Goal { get; private set; } = Goal.Choosing;

    /// <summary>The city it is going to, or -1.</summary>
    public int TargetCity { get; private set; } = -1;

    /// <summary>How it decided to get there.</summary>
    public TravelMode Mode { get; private set; } = TravelMode.Run;

    /// <summary>The route's first leg when it is flying: the port to walk to.</summary>
    private float _portX;

    private float _portZ;

    /// <summary>The tick the current goal ends on, for the goals that are timed.</summary>
    private long _until;

    /// <summary>Whether it has already asked to board on this trip, so it asks once rather than every tick.</summary>
    private bool _asked;

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

        // Interiors are not navigated: the realm is one cell, the map's coordinates are the planet's, and a bot that
        // steered by them indoors would walk into a wall forever. Standing still is the honest behaviour until there is
        // a reason to leave, and leaving is a ViewRealm this brain does not yet send.
        if (world.Realm != 0)
        {
            return Intent.None;
        }

        switch (Goal)
        {
            case Goal.Hunting:
                return Hunt(in world, tick);
            case Goal.Travelling:
                return Travel(in world, tick);
            case Goal.Waiting:
                return Wait(in world, tick);
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
        if (world.NearestCreature != 0 && world.NearestCreatureM <= NoticeM)
        {
            Goal = Goal.Hunting;
            return Hunt(in world, tick);
        }

        var roll = Next() % 100u;
        if (roll < 20u)
        {
            Goal = Goal.Idling;
            _until = tick + _idleTicks;
            return Intent.Move(0f, 0);   // stop where it is: speed class 0 is the server's own "stand still"
        }

        var here = CityAt(world.X, world.Z);
        var destination = (int)(Next() % (uint)_cities.Length);
        if (destination == here)
        {
            destination = (destination + 1) % _cities.Length;
        }

        TargetCity = destination;
        var city = _cities[destination];
        var route = Routing.Plan(new Journey(world.X, world.Z, city.X, city.Z, here, destination, Facilities));
        Mode = route.Mode;
        _portX = route.PortX;
        _portZ = route.PortZ;
        _asked = false;
        Goal = Goal.Travelling;
        return Travel(in world, tick);
    }

    /// <summary>Attacks what is near the lair, and leaves when there is nothing left to attack.</summary>
    /// <param name="world">What the bot was sent.</param>
    /// <param name="tick">The fleet's tick.</param>
    /// <returns>The command.</returns>
    private Intent Hunt(in Perceived world, long tick)
    {
        if (world.NearestCreature == 0)
        {
            // Killed, or it wandered out of view. Nothing to hunt, so decide again.
            Goal = Goal.Choosing;
            return Choose(in world, tick);
        }

        if (world.NearestCreatureM > StrikeM)
        {
            return Intent.Move(
                Routing.HeadingTo(world.X, world.Z, world.NearestCreatureX, world.NearestCreatureZ),
                Routing.SpeedClassOf(TravelMode.Run));
        }

        // In range. Attacking is naming a target and letting the server's combat path resolve it, which is exactly what
        // a browser does — there is no attack command, and that is the point.
        return Intent.Attack(world.NearestCreature);
    }

    /// <summary>Goes to the target city by the mode the route chose.</summary>
    /// <param name="world">What the bot was sent.</param>
    /// <param name="tick">The fleet's tick.</param>
    /// <returns>The command.</returns>
    private Intent Travel(in Perceived world, long tick)
    {
        if (TargetCity < 0 || TargetCity >= _cities.Length)
        {
            Goal = Goal.Choosing;
            return Choose(in world, tick);
        }

        var city = _cities[TargetCity];
        if (Routing.Distance(world.X, world.Z, city.X, city.Z) <= city.Radius)
        {
            // Arrived. Idle a while rather than choosing again at once: a fleet that re-decided on arrival would travel
            // without pause, and a city nobody stands in is a city whose group set is never held.
            Goal = Goal.Idling;
            _until = tick + _idleTicks;
            TargetCity = -1;
            return Intent.Move(0f, 0);
        }

        if (Mode != TravelMode.Shuttle)
        {
            return Intent.Move(Routing.HeadingTo(world.X, world.Z, city.X, city.Z), Routing.SpeedClassOf(Mode));
        }

        // Flying: walk to this city's port, then ask. The ask is refused unless the body is at the port, so the walk is
        // not optional and the refusal is the feedback if the port has been misremembered.
        if (world.PortCity >= 0 && !_asked)
        {
            _asked = true;
            Goal = Goal.Waiting;
            _until = tick + _patienceTicks;
            return Intent.Board(TargetCity);
        }

        return Intent.Move(Routing.HeadingTo(world.X, world.Z, _portX, _portZ), Routing.SpeedClassOf(TravelMode.Run));
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
        if (TargetCity >= 0 && TargetCity < _cities.Length)
        {
            var city = _cities[TargetCity];
            if (Routing.Distance(world.X, world.Z, city.X, city.Z) <= city.Radius)
            {
                Goal = Goal.Idling;
                _until = tick + _idleTicks;
                TargetCity = -1;
                return Intent.Move(0f, 0);
            }
        }

        if (tick < _until)
        {
            return Intent.None;   // standing at the port, which is what waiting looks like on the wire
        }

        // Patience spent. Walk or ride instead, and do not ask again on this trip.
        Mode = TravelMode.Speeder;
        Goal = Goal.Travelling;
        return Travel(in world, tick);
    }

    /// <summary>The city whose disc contains a point, or -1.</summary>
    /// <param name="x">East.</param>
    /// <param name="z">North.</param>
    /// <returns>The city, or -1.</returns>
    public int CityAt(float x, float z)
    {
        for (var i = 0; i < _cities.Length; i++)
        {
            if (Routing.Distance(x, z, _cities[i].X, _cities[i].Z) <= _cities[i].Radius)
            {
                return i;
            }
        }

        return -1;
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
