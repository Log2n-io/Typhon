using System;
using NUnit.Framework;
using SwgTatooine.Bots.Brain;

namespace SwgTatooine.Tests;

/// <summary>
/// The bot's decision loop and its routing, tested as what they are: pure functions of what a bot was sent.
/// </summary>
/// <remarks>
/// <b>No server, no socket, no store — and that is the design's dividend rather than a convenience.</b> The brain reads
/// a <see cref="Perceived"/> struct and returns an <see cref="Intent"/>, so every case below is a world written down and
/// a decision read off. The one part that cannot be tested this way is the scan that fills the struct, which is why
/// that scan is the only place in the brain that touches the SDK.
/// </remarks>
[TestFixture]
public sealed class BotBrainChecks
{
    private static BotBrain Brain(uint seed = 7u) => new(BotMap.Cities, seed);

    /// <summary>A body standing in the open, with nothing in view.</summary>
    private static Perceived Empty(float x, float z) => new()
    {
        HasBody = true,
        X = x,
        Z = z,
        Realm = 0,
        HealthFraction = 1f,
        PortCity = -1,
    };

    /// <summary>A camera has no body, so it has nothing to decide and must send nothing.</summary>
    [Test]
    public void ASessionWithNoBodyDecidesNothing()
    {
        var brain = Brain();
        Assert.That(brain.Decide(default, 1).Kind, Is.EqualTo(IntentKind.None), "a camera was given a command to drive a body it does not have");
    }

    /// <summary>
    /// Indoors it stands still, because the map it steers by is the planet's.
    /// </summary>
    /// <remarks>
    /// An interior is one cell with its own coordinate frame, so a heading computed from a city's planet coordinates
    /// walks a bot into a wall for as long as it is inside. Standing still is the honest behaviour until the brain can
    /// ask to leave, which is a <c>ViewRealm</c> it does not yet send — and a test that let it steer indoors would be
    /// asserting a bug.
    /// </remarks>
    [Test]
    public void IndoorsItSendsNothing()
    {
        var brain = Brain();
        var inside = Empty(0f, 0f) with { Realm = 12 };
        Assert.That(brain.Decide(in inside, 1).Kind, Is.EqualTo(IntentKind.None), "a bot steered by planet coordinates while inside a building");
    }

    /// <summary>A creature in view is hunted: walked to, then named as a target.</summary>
    [Test]
    public void ACreatureInViewIsWalkedToAndThenAttacked()
    {
        var brain = Brain();
        var far = Empty(0f, 0f) with { NearestCreature = 42u, NearestCreatureX = 0f, NearestCreatureZ = 100f, NearestCreatureM = 100f };
        var approach = brain.Decide(in far, 1);

        Assert.Multiple(() =>
        {
            Assert.That(brain.Goal, Is.EqualTo(Goal.Hunting), "a creature in view did not become a hunt");
            Assert.That(approach.Kind, Is.EqualTo(IntentKind.Move), "a creature 100 m away was attacked rather than approached");

            // The server's convention: 0 along +X, measured toward +Z. Due north is therefore a quarter turn.
            Assert.That(approach.Heading, Is.EqualTo(MathF.PI / 2f).Within(0.01f), "the heading does not point at the creature");
        });

        var close = Empty(0f, 90f) with { NearestCreature = 99u, NearestCreatureX = 0f, NearestCreatureZ = 100f, NearestCreatureM = 10f };
        var attack = brain.Decide(in close, 2);

        Assert.Multiple(() =>
        {
            Assert.That(attack.Kind, Is.EqualTo(IntentKind.Attack), "a creature within reach was not attacked");
            Assert.That(attack.NetId, Is.EqualTo(99u), "the wrong entity was named");
        });
    }

    /// <summary>
    /// A creature on the far side of the sphere is ignored, so a bot still travels.
    /// </summary>
    /// <remarks>
    /// Without this a bot chases whatever is in its 192 m sphere forever, and on a planet with ten thousand creatures
    /// that is every bot, always — a population that never goes anywhere, which is the load shape this fleet is NOT
    /// for.
    /// </remarks>
    [Test]
    public void ACreatureBeyondNoticeIsIgnored()
    {
        var brain = Brain();
        var distant = Empty(0f, 0f) with { NearestCreature = 7u, NearestCreatureZ = 180f, NearestCreatureM = 180f };
        brain.Decide(in distant, 1);
        Assert.That(brain.Goal, Is.Not.EqualTo(Goal.Hunting), "a creature 180 m away started a hunt, so nothing would ever travel");
    }

    /// <summary>Standing at a port with both ends known, it asks to board rather than walking.</summary>
    [Test]
    public void AtAPortWithBothEndsKnownItAsksToBoard()
    {
        var brain = Brain();

        // Every port learned, as a bot that has toured the planet would have them. Whichever city it then chooses, the
        // journey is kilometres and the shuttle wins — so this asserts the decision rather than a particular roll.
        for (var i = 0; i < BotMap.Cities.Length; i++)
        {
            brain.Facilities.Observe(i, Routing.ShuttleportKind, BotMap.Cities[i].X, BotMap.Cities[i].Z);
        }

        var here = BotMap.Cities[0];
        var asked = Intent.None;
        for (var tick = 1; tick < 400 && asked.Kind != IntentKind.Board; tick++)
        {
            var world = Empty(here.X, here.Z) with { PortCity = 0 };
            asked = brain.Decide(in world, tick);
        }

        Assert.Multiple(() =>
        {
            Assert.That(asked.Kind, Is.EqualTo(IntentKind.Board), "a bot standing at a port, knowing every port, never asked to fly");
            Assert.That(asked.City, Is.Not.Zero, "it asked to fly to the city it is standing in");
            Assert.That(brain.Goal, Is.EqualTo(Goal.Waiting), "asking to board did not put the bot into waiting");
        });
    }

    /// <summary>A hunt whose quarry leaves view decides again rather than standing there.</summary>
    [Test]
    public void AHuntWhoseQuarryLeavesViewChoosesAgain()
    {
        var brain = Brain();
        var seen = Empty(0f, 0f) with { NearestCreature = 42u, NearestCreatureZ = 100f, NearestCreatureM = 100f };
        brain.Decide(in seen, 1);
        Assert.That(brain.Goal, Is.EqualTo(Goal.Hunting));

        brain.Decide(Empty(0f, 0f), 2);
        Assert.That(brain.Goal, Is.Not.EqualTo(Goal.Hunting), "the bot kept hunting something that is no longer in view");
    }

    /// <summary>Arriving at the target city ends the journey and idles instead of re-deciding at once.</summary>
    [Test]
    public void ArrivingAtACityStopsAndIdles()
    {
        var brain = Brain();

        // Walk the brain into travelling, then put the body at the city it chose.
        Intent last = default;
        for (var tick = 1; tick < 40 && brain.Goal != Goal.Travelling; tick++)
        {
            last = brain.Decide(Empty(0f, 0f), tick);
        }

        Assert.That(brain.Goal, Is.EqualTo(Goal.Travelling), "the brain never chose to travel; the goal roll must have changed");
        Assert.That(last.Kind, Is.EqualTo(IntentKind.Move), "a travelling bot sent no movement");

        var city = BotMap.Cities[brain.TargetCity];
        var arrived = Empty(city.X, city.Z);
        var stop = brain.Decide(in arrived, 100);

        Assert.Multiple(() =>
        {
            Assert.That(brain.Goal, Is.EqualTo(Goal.Idling), "arriving did not end the journey");
            Assert.That(stop.Kind, Is.EqualTo(IntentKind.Move), "arrival sent no command");
            Assert.That(stop.SpeedClass, Is.Zero, "arriving must stop the body, which is speed class 0");
        });
    }

    /// <summary>Two brains with the same seed make the same decisions, which is what makes a fleet reproducible.</summary>
    [Test]
    public void TheSameSeedDecidesTheSameWay()
    {
        var a = Brain(12345u);
        var b = Brain(12345u);
        for (var tick = 1; tick <= 60; tick++)
        {
            Assert.That(a.Decide(Empty(10f, 20f), tick), Is.EqualTo(b.Decide(Empty(10f, 20f), tick)), $"two bots with one seed diverged at tick {tick}");
        }
    }

    /// <summary>A bot numbered 0 still decides: xorshift is dead at zero and the constructor must not hand it one.</summary>
    [Test]
    public void ASeedOfZeroStillDecides()
    {
        var brain = Brain(0u);
        var moved = false;
        for (var tick = 1; tick <= 40 && !moved; tick++)
        {
            moved = brain.Decide(Empty(0f, 0f), tick).Kind != IntentKind.None;
        }

        Assert.That(moved, Is.True, "a bot seeded 0 never did anything, which is what a dead xorshift looks like");
    }
}

/// <summary>
/// Choosing between walking, riding and flying.
/// </summary>
/// <remarks>
/// <b>The three modes do not order the same way for every journey, which is the whole reason this is a decision.</b> The
/// cases below are the orderings that must hold: a short hop is never worth a shuttle, a planet crossing is, and a
/// shuttle is not considered at all until the bot knows both ends of it.
/// </remarks>
[TestFixture]
public sealed class RoutingChecks
{
    private const int Port = Routing.ShuttleportKind;

    /// <summary>A few metres across a city is walked or ridden, never flown.</summary>
    [Test]
    public void AShortHopIsNeverWorthAShuttle()
    {
        var map = new FacilityMap();
        map.Observe(0, Port, 0f, 0f);
        map.Observe(1, Port, 100f, 0f);

        var route = Routing.Plan(new Journey(0f, 0f, 80f, 0f, 0, 1, map));
        Assert.That(route.Mode, Is.Not.EqualTo(TravelMode.Shuttle), "an 80 m hop was flown, which costs a minute of waiting to save three seconds");
    }

    /// <summary>Crossing the planet is flown, once both ports are known.</summary>
    [Test]
    public void CrossingThePlanetIsFlown()
    {
        var map = new FacilityMap();
        map.Observe(0, Port, 3460f, -4768f);
        map.Observe(2, Port, -2890f, 2198f);

        var route = Routing.Plan(new Journey(3460f, -4768f, -2890f, 2198f, 0, 2, map));
        Assert.Multiple(() =>
        {
            Assert.That(route.Mode, Is.EqualTo(TravelMode.Shuttle), "a 10 km crossing was walked or ridden");
            Assert.That(route.PortX, Is.EqualTo(3460f), "the route must name the port to walk to first");
        });
    }

    /// <summary>
    /// An unknown far port is not flown to, however long the journey.
    /// </summary>
    /// <remarks>
    /// Not because flying needs it — the server puts the body at the destination port — but because the walk FROM it is
    /// part of what the route is being compared on, and a bot that guessed the centre would misjudge by up to the
    /// city's radius. A first visit walks; a second, having seen the port, flies.
    /// </remarks>
    [Test]
    public void AnUnknownFarPortIsNotFlownTo()
    {
        var map = new FacilityMap();
        map.Observe(0, Port, 3460f, -4768f);

        var route = Routing.Plan(new Journey(3460f, -4768f, -2890f, 2198f, 0, 2, map));
        Assert.That(route.Mode, Is.Not.EqualTo(TravelMode.Shuttle), "the bot flew to a port it has never seen");

        map.Observe(2, Port, -2890f, 2198f);
        var informed = Routing.Plan(new Journey(3460f, -4768f, -2890f, 2198f, 0, 2, map));
        Assert.That(informed.Mode, Is.EqualTo(TravelMode.Shuttle), "having learned the far port changed nothing, so the map is not being read");
    }

    /// <summary>Out in the desert there is no port to walk to, so the shuttle is not an option at all.</summary>
    [Test]
    public void FromTheDesertThereIsNoShuttle()
    {
        var map = new FacilityMap();
        map.Observe(0, Port, 3460f, -4768f);
        map.Observe(2, Port, -2890f, 2198f);

        var route = Routing.Plan(new Journey(0f, 0f, -2890f, 2198f, -1, 2, map));
        Assert.That(route.Mode, Is.Not.EqualTo(TravelMode.Shuttle), "a bot in the open desert boarded a shuttle from nowhere");
    }

    /// <summary>The learned map answers by city and kind, and forgets nothing it has seen.</summary>
    [Test]
    public void TheMapRemembersWhatItWasShown()
    {
        var map = new FacilityMap();
        Assert.That(map.TryFind(3, Port, out _), Is.False, "an unseen city answered");

        map.Observe(3, Port, 10f, 20f);
        map.Observe(3, 1, 30f, 40f);
        map.Observe(-1, Port, 50f, 60f);

        Assert.Multiple(() =>
        {
            Assert.That(map.TryFind(3, Port, out var port), Is.True);
            Assert.That(port, Is.EqualTo((10f, 20f)));
            Assert.That(map.TryFind(3, 1, out var terminal), Is.True, "two kinds in one city must not overwrite each other");
            Assert.That(terminal, Is.EqualTo((30f, 40f)));
            Assert.That(map.Count, Is.EqualTo(2), "a facility in no city was remembered under something");
        });
    }
}
