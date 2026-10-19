using System;

namespace SwgTatooine.Bots.Brain;

/// <summary>How a bot gets from where it is to a city: on foot, on a speeder, or by shuttle.</summary>
/// <remarks>
/// <b>This is the brain's actual content.</b> Goal selection picks what to want and is a few lines; the interesting
/// decision is how to get there, and it is interesting because the three modes do not order the same way for every
/// journey. A shuttle is faster over a planet and absurd across a street; its wait is a distribution rather than a
/// constant, and the walk to the port at one end and from the port at the other is often most of the cost. A brain that
/// compares them on expected TIME rather than on distance will pick different modes for the same pair of cities
/// depending on where in the city it happens to be standing — which is what a person does without thinking about it.
/// </remarks>
public enum TravelMode
{
    /// <summary>On foot, at a run.</summary>
    Run = 0,

    /// <summary>On a speeder.</summary>
    Speeder,

    /// <summary>Walk to this city's port, wait, fly, walk from the destination's port.</summary>
    Shuttle,
}

/// <summary>What a journey needs to know about the world it is crossing.</summary>
/// <param name="FromX">Where the bot is, east.</param>
/// <param name="FromZ">Where the bot is, north.</param>
/// <param name="ToX">Where it wants to be, east.</param>
/// <param name="ToZ">Where it wants to be, north.</param>
/// <param name="FromCity">The city it is standing in, or -1 out in the desert.</param>
/// <param name="ToCity">The city it is going to.</param>
/// <param name="Map">What it has learned about the cities' facilities.</param>
public readonly record struct Journey(float FromX, float FromZ, float ToX, float ToZ, int FromCity, int ToCity, FacilityMap Map);

/// <summary>A chosen route.</summary>
/// <param name="Mode">How to travel.</param>
/// <param name="Seconds">What it is expected to cost, in seconds.</param>
/// <param name="PortX">The port to walk to first, east, for <see cref="TravelMode.Shuttle"/>.</param>
/// <param name="PortZ">The port to walk to first, north.</param>
public readonly record struct Route(TravelMode Mode, double Seconds, float PortX, float PortZ);

/// <summary>Chooses between walking, riding and flying on expected time.</summary>
public static class Routing
{
    /// <summary>A player's run speed, m/s — <c>TatooineData.PlayerRunSpeedMps</c>, the class the server grants by default.</summary>
    public const double RunMps = 5d;

    /// <summary>A speeder's speed, m/s — <c>TatooineData.PlayerSpeederSpeedMps</c>.</summary>
    public const double SpeederMps = 24d;

    /// <summary>
    /// What mounting costs, in seconds.
    /// </summary>
    /// <remarks>
    /// <b>Zero on this server, and named rather than omitted.</b> There are no vehicle items and no mount state here, so
    /// a speeder is a speed class the server grants on request and riding costs nothing to begin. In SWG it cost calling
    /// and mounting a vehicle, which is seconds — and the moment this server grows one, this constant is where the
    /// routing learns about it, instead of the comparison quietly staying wrong.
    /// </remarks>
    public const double MountSeconds = 0d;

    /// <summary>The shuttle's cadence, in seconds — <c>SimConfig.ShuttleIntervalS</c>'s default.</summary>
    /// <remarks>
    /// <b>A bot's assumption until it has watched a port, not a fact it is told.</b> The cadence is observable: a bot
    /// standing at a port sees departures and can time them. Starting from the server's default means a bot that has
    /// never waited still routes sensibly, and `brain.md` keeps learning the timetable as the thing this grows into.
    /// </remarks>
    public const double ShuttleIntervalSeconds = 120d;

    /// <summary>The flight itself, in seconds: the server teleports, so this is the boarding window's cost and not a distance.</summary>
    public const double FlightSeconds = 5d;

    /// <summary>`StructureKind.Shuttleport`, which is how a port is recognised in what the server sends.</summary>
    public const int ShuttleportKind = 2;

    /// <summary>Chooses a route.</summary>
    /// <param name="journey">The journey.</param>
    /// <returns>The cheapest mode by expected time, and what it is expected to cost.</returns>
    /// <remarks>
    /// <b>The shuttle is only considered when the bot knows both ports.</b> Not because flying needs the destination's
    /// port — the server puts the body there — but because the walk FROM it is part of the cost, and a bot that guessed
    /// it at the city centre would systematically under- or over-estimate by up to the city's radius, which at 456 m is
    /// a minute and a half on foot. A bot that has not seen the far port yet walks or rides, which is also what it would
    /// do if it had never heard of the shuttle. The first visit pays; the second does not.
    /// </remarks>
    public static Route Plan(in Journey journey)
    {
        var direct = Distance(journey.FromX, journey.FromZ, journey.ToX, journey.ToZ);
        var best = new Route(TravelMode.Run, direct / RunMps, 0f, 0f);

        var riding = (direct / SpeederMps) + MountSeconds;
        if (riding < best.Seconds)
        {
            best = new Route(TravelMode.Speeder, riding, 0f, 0f);
        }

        if (journey.FromCity < 0 || journey.Map == null)
        {
            return best;   // out in the desert there is no port to walk to, which is the ordinary travel case
        }

        if (!journey.Map.TryFind(journey.FromCity, ShuttleportKind, out var from)
            || !journey.Map.TryFind(journey.ToCity, ShuttleportKind, out var to))
        {
            return best;
        }

        // The wait is a distribution, and its mean is what an expectation wants: a bot arriving at an unobserved moment
        // in a cadence of T waits T/2 on average. Using T would make the shuttle look worse than it is by a minute, and
        // using 0 would make it look better than it ever is.
        var toPort = Distance(journey.FromX, journey.FromZ, from.X, from.Z) / RunMps;
        var fromPort = Distance(to.X, to.Z, journey.ToX, journey.ToZ) / RunMps;
        var flying = toPort + (ShuttleIntervalSeconds / 2d) + FlightSeconds + fromPort;
        return flying < best.Seconds ? new Route(TravelMode.Shuttle, flying, from.X, from.Z) : best;
    }

    /// <summary>The speed class a mode asks the server for.</summary>
    /// <param name="mode">The mode.</param>
    /// <returns>The class, as <c>SpeedClasses</c> numbers them.</returns>
    public static byte SpeedClassOf(TravelMode mode) => mode == TravelMode.Speeder ? (byte)4 : (byte)2;

    /// <summary>Plain distance.</summary>
    /// <param name="ax">First point, east.</param>
    /// <param name="az">First point, north.</param>
    /// <param name="bx">Second point, east.</param>
    /// <param name="bz">Second point, north.</param>
    /// <returns>Metres.</returns>
    public static double Distance(float ax, float az, float bx, float bz)
    {
        var dx = (double)bx - ax;
        var dz = (double)bz - az;
        return Math.Sqrt((dx * dx) + (dz * dz));
    }

    /// <summary>The heading from one point to another, in radians, as <c>MoveDir</c> wants it.</summary>
    /// <param name="fromX">Where the bot is, east.</param>
    /// <param name="fromZ">Where the bot is, north.</param>
    /// <param name="toX">Where it is going, east.</param>
    /// <param name="toZ">Where it is going, north.</param>
    /// <returns>Radians.</returns>
    /// <remarks>
    /// <b>The server's convention, read off the server: 0 along +X, measured toward +Z</b> (<c>MoveDir.Heading</c>, and
    /// <c>ApplyIntents</c> stepping the destination by <c>cos</c> on X and <c>sin</c> on Z). So the arguments to
    /// <c>Atan2</c> are (dz, dx) and not the (dx, dz) a reader expects, and getting them the wrong way round sends every
    /// bot on a heading mirrored about the diagonal — which looks like plausible wandering rather than like a bug.
    /// </remarks>
    public static float HeadingTo(float fromX, float fromZ, float toX, float toZ) => MathF.Atan2(toZ - fromZ, toX - fromX);
}
