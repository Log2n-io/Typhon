using System;
using System.Collections.Generic;
using NUnit.Framework;
using SwgTatooine.Bots.Brain;
using SwgTatooine.Server;

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
        // ASYMMETRIC on purpose: 30 m east and 40 m north. Both coordinates on one axis — the creature due north of the
        // bot — makes dx zero, and a zero term cannot show a sign error: atan2(100, -0) is the same quarter turn as
        // atan2(100, +0), so negating the X term passed. A 3-4-5 triangle pins the angle to one value no mirroring or
        // sign flip reproduces.
        var far = Empty(0f, 0f) with { NearestCreature = 42u, NearestCreatureX = 30f, NearestCreatureZ = 40f, NearestCreatureM = 50f };
        var approach = brain.Decide(in far, 1);

        Assert.Multiple(() =>
        {
            Assert.That(brain.Goal, Is.EqualTo(Goal.Hunting), "a creature in view did not become a hunt");
            Assert.That(approach.Kind, Is.EqualTo(IntentKind.Move), "a creature 50 m away was attacked rather than approached");

            // The server's convention: 0 along +X, measured toward +Z, so the arguments to Atan2 are (dz, dx).
            Assert.That(approach.Heading, Is.EqualTo(MathF.Atan2(40f, 30f)).Within(0.001f), "the heading does not point at the creature");
            Assert.That(approach.Heading, Is.EqualTo(0.9273f).Within(0.001f), "the heading is not the angle a 3-4-5 triangle makes");
        });

        var close = Empty(0f, 90f) with { NearestCreature = 99u, NearestCreatureX = 0f, NearestCreatureZ = 100f, NearestCreatureM = 10f };
        var attack = brain.Decide(in close, 2);

        Assert.Multiple(() =>
        {
            Assert.That(attack.Kind, Is.EqualTo(IntentKind.Attack), "a creature within reach was not attacked");
            Assert.That(attack.NetId, Is.EqualTo(99u), "the wrong entity was named");
        });
    }

    /// <summary>A fight that does not end is given up, and that creature is not hunted again.</summary>
    /// <remarks>The first live run: two bots named one target from one spot for the whole three minutes — something they could not hurt.</remarks>
    [Test]
    public void AHuntThatDoesNotEndIsGivenUp()
    {
        var brain = Brain();
        var quarry = Empty(0f, 0f) with { NearestCreature = 42u, NearestCreatureZ = 10f, NearestCreatureM = 10f };
        brain.Decide(in quarry, 1);
        Assert.That(brain.Goal, Is.EqualTo(Goal.Hunting), "the premise: a creature at 10 m is hunted");

        var limit = (long)(BotBrain.HuntSeconds * 4) + 2;
        for (var tick = 2L; tick <= limit; tick++)
        {
            brain.Decide(in quarry, tick);
        }

        Assert.That(brain.Goal, Is.Not.EqualTo(Goal.Hunting), "a hunt that never ended was never given up");
        for (var tick = limit + 1; tick < limit + 200; tick++)
        {
            brain.Decide(in quarry, tick);
            Assert.That(brain.Goal, Is.Not.EqualTo(Goal.Hunting), "the creature it gave up on was hunted again");
        }
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
            brain.Facilities.Observe(i + 1, Routing.ShuttleportKind, BotMap.Cities[i].X, BotMap.Cities[i].Z);
        }

        var here = BotMap.Cities[0];
        var asked = Intent.None;
        for (var tick = 1; tick < 400 && asked.Kind != IntentKind.Board; tick++)
        {
            var world = Empty(here.X, here.Z) with { PortRegion = 1 };
            asked = brain.Decide(in world, tick);
        }

        Assert.Multiple(() =>
        {
            Assert.That(asked.Kind, Is.EqualTo(IntentKind.Board), "a bot standing at a port, knowing every port, never asked to fly");

            // Against the region it is STANDING in, not against 0: `Is.Not.Zero` was unfalsifiable here, because the
            // only way to reach a Board is through the arrival test, and arriving at the city it is in ends the journey
            // before any ask. The claim is "it did not ask to fly to where it already is", so that is the assertion.
            Assert.That(asked.Region, Is.Not.EqualTo(1), "it asked to fly to the city it is standing in");
            Assert.That(asked.Region, Is.InRange(2, BotMap.Cities.Length), "the destination is not a region on the map");
            Assert.That(brain.Goal, Is.EqualTo(Goal.Waiting), "asking to board did not put the bot into waiting");
        });
    }

    /// <summary>A refused boarding is noticed at once and travelled around, instead of standing at the port.</summary>
    /// <remarks>
    /// The server answers a boarding it will not accept with an <c>ACKS</c> reason, and the brain's patience is two
    /// shuttle intervals — four minutes. Without reading the refusal a bot stood there for all of it, because the only
    /// thing it watched for was a position change that was never coming. This is the test that fails if the refusal
    /// path is removed.
    /// </remarks>
    [Test]
    public void ARefusedBoardingTravelsInsteadOfWaiting()
    {
        var brain = Brain();
        for (var i = 0; i < BotMap.Cities.Length; i++)
        {
            brain.Facilities.Observe(i + 1, Routing.ShuttleportKind, BotMap.Cities[i].X, BotMap.Cities[i].Z);
        }

        var here = BotMap.Cities[0];
        var tick = 1L;
        var asked = Intent.None;
        for (; tick < 400 && asked.Kind != IntentKind.Board; tick++)
        {
            asked = brain.Decide(Empty(here.X, here.Z) with { PortRegion = 1 }, tick);
        }

        Assert.That(brain.Goal, Is.EqualTo(Goal.Waiting), "the premise failed: the bot never got as far as waiting for a shuttle");

        var refused = Empty(here.X, here.Z) with { PortRegion = 1, BoardRefused = true };
        var next = brain.Decide(in refused, tick);

        Assert.Multiple(() =>
        {
            Assert.That(brain.Goal, Is.EqualTo(Goal.Travelling), "a refused boarding left the bot waiting for a shuttle it will never be given");
            Assert.That(next.Kind, Is.EqualTo(IntentKind.Move), "a refused boarding sent no movement");
            Assert.That(next.SpeedClass, Is.Not.Zero, "it stood still after being refused");
        });
    }

    /// <summary>A quarry that retreats out of notice range is given up on, even while it is still in view.</summary>
    /// <remarks>
    /// The sphere is 192 m and <see cref="BotBrain.NoticeM"/> is 120: between the two, a creature is visible and not
    /// worth chasing. The first version's only exit from a hunt was an EMPTY sphere, so a bot in a creature-dense place
    /// re-targeted whatever was nearest every tick and never travelled again — a goal with no end rather than a long
    /// one. A test that only moved the creature out of view entirely could not see the difference.
    /// </remarks>
    [Test]
    public void AQuarryBeyondNoticeRangeIsGivenUpOn()
    {
        var brain = Brain();
        var near = Empty(0f, 0f) with { NearestCreature = 42u, NearestCreatureZ = 100f, NearestCreatureM = 100f };
        brain.Decide(in near, 1);
        Assert.That(brain.Goal, Is.EqualTo(Goal.Hunting), "the premise failed: a creature at 100 m did not become a hunt");

        var retreated = Empty(0f, 0f) with { NearestCreature = 42u, NearestCreatureZ = 170f, NearestCreatureM = 170f };
        brain.Decide(in retreated, 2);

        Assert.That(brain.Goal, Is.Not.EqualTo(Goal.Hunting), "the bot chased a creature past the range it had decided to bother at");
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

        var city = BotMap.Cities[brain.TargetRegion - 1];

        // THE CITY'S EDGE IS NOT ARRIVAL, and this is the assertion the first version of this test was missing. It put
        // the body on the city's exact centre, where every candidate threshold agrees, so it passed just as well
        // against the radius — which is what the code actually compared, leaving a bot bound for a 456 m city standing
        // 456 m short of it, outside its own 192 m sphere, never seeing the shuttleport it had come for.
        // A city kilometres away is ridden, so a tick may be spent asking for the swoop; the movement is the tick after.
        var atTheWall = Empty(city.X + city.Radius - 1f, city.Z);
        var keepGoing = brain.Decide(in atTheWall, 100);
        if (keepGoing.Kind == IntentKind.Mount)
        {
            keepGoing = brain.Decide(in atTheWall, 100);
        }

        Assert.Multiple(() =>
        {
            Assert.That(brain.Goal, Is.EqualTo(Goal.Travelling), "the bot stopped at the city wall and called it arrival");
            Assert.That(keepGoing.Kind, Is.EqualTo(IntentKind.Move), "a bot short of its destination sent no movement");
            Assert.That(keepGoing.SpeedClass, Is.Not.Zero, "a bot short of its destination stopped moving");
        });

        var arrived = Empty(city.X + BotBrain.ArrivedM - 1f, city.Z);
        var stop = brain.Decide(in arrived, 101);

        Assert.Multiple(() =>
        {
            Assert.That(brain.Goal, Is.EqualTo(Goal.Idling), "arriving did not end the journey");
            Assert.That(stop.Kind, Is.EqualTo(IntentKind.Move), "arrival sent no command");
            Assert.That(stop.SpeedClass, Is.Zero, "arriving must stop the body, which is speed class 0");
            Assert.That(brain.TargetRegion, Is.Zero, "arriving left the destination set, so 0 no longer means 'not travelling'");
        });
    }

    /// <summary>A flight's arrival is being inside the city asked for, not within walking tolerance of its centre.</summary>
    /// <remarks>
    /// The server puts a passenger down at the DESTINATION's port, which can be most of a radius from the middle, so a
    /// shuttle that worked would otherwise look to the brain like a flight that never happened — and the bot would
    /// stand at its new port until its patience ran out.
    /// </remarks>
    [Test]
    public void LandingAnywhereInTheTargetCityEndsTheFlight()
    {
        var brain = Brain();
        for (var i = 0; i < BotMap.Cities.Length; i++)
        {
            brain.Facilities.Observe(i + 1, Routing.ShuttleportKind, BotMap.Cities[i].X, BotMap.Cities[i].Z);
        }

        var here = BotMap.Cities[0];
        var tick = 1L;
        var asked = Intent.None;
        for (; tick < 400 && asked.Kind != IntentKind.Board; tick++)
        {
            asked = brain.Decide(Empty(here.X, here.Z) with { PortRegion = 1 }, tick);
        }

        Assert.That(brain.Goal, Is.EqualTo(Goal.Waiting), "the premise failed: the bot never asked to board");

        // Put it down near the edge of the city it asked for — a port's distance from the centre, not an arrival's.
        var destination = BotMap.Cities[asked.Region - 1];
        var landed = Empty(destination.X + destination.Radius - 2f, destination.Z);
        var stop = brain.Decide(in landed, tick + 1);

        Assert.Multiple(() =>
        {
            Assert.That(brain.Goal, Is.EqualTo(Goal.Idling), "landing inside the city it flew to was not recognised as arrival");
            Assert.That(stop.SpeedClass, Is.Zero, "a bot that has arrived kept moving");
        });
    }

    /// <summary>The city a bot stands in is excluded by rolling over the others, so no neighbour is twice as likely.</summary>
    /// <remarks>
    /// <b>A distribution, because the defect was a distribution.</b> Excluding the current city by nudging a
    /// self-selection to the next index gave that one neighbour two of the seven rolls: a fleet that visited one place
    /// twice as often as the rest, reported as uniform. The seeds are fixed, so this is a property of a known sequence
    /// and not a sample — it either holds or it does not, on every run.
    /// </remarks>
    [Test]
    public void TheCityItStandsInIsExcludedWithoutFavouringItsNeighbour()
    {
        const int Brains = 700;
        var chosen = new int[BotMap.Cities.Length + 1];
        var here = BotMap.Cities[0];
        var travels = 0;
        for (var seed = 1u; seed <= Brains; seed++)
        {
            var brain = new BotBrain(BotMap.Cities, seed);
            for (var tick = 1L; tick <= 20 && brain.Goal != Goal.Travelling; tick++)
            {
                brain.Decide(Empty(here.X, here.Z), tick * 100);   // × 100 so an idle has always expired by the next look
            }

            if (brain.Goal != Goal.Travelling)
            {
                continue;
            }

            chosen[brain.TargetRegion]++;
            travels++;
        }

        Assert.That(travels, Is.GreaterThan(Brains / 2), "the premise failed: most brains never chose to travel");
        Assert.That(chosen[1], Is.Zero, "a bot chose to travel to the city it was already standing in");

        var share = travels / (double)(BotMap.Cities.Length - 1);
        for (var region = 2; region <= BotMap.Cities.Length; region++)
        {
            Assert.That(
                chosen[region],
                Is.InRange(share * 0.6, share * 1.5),
                $"region {region} took {chosen[region]} of {travels} travels against an even share of {share:F0}");
        }
    }

    /// <summary>Two brains with the same seed make the same decisions, which is what makes a fleet reproducible.</summary>
    [Test]
    public void TheSameSeedDecidesTheSameWay()
    {
        var a = Brain(12345u);
        var b = Brain(12345u);
        // THE TICKS ARE SPACED, and that is what makes the comparison mean anything. An idle lasts 80 driver ticks, so
        // sixty consecutive ticks are one roll followed by fifty-nine refusals to act: two brains that both happened to
        // idle first produced identical sequences whatever their seeds, and the negative control below failed on
        // exactly that before the spacing was added. Spaced ticks let every goal run to its end and the next begin.
        var travelled = 0;
        for (var tick = 1; tick <= 60; tick++)
        {
            var left = a.Decide(Empty(10f, 20f), tick * 100);
            Assert.That(left, Is.EqualTo(b.Decide(Empty(10f, 20f), tick * 100)), $"two bots with one seed diverged at tick {tick}");
            if (left.SpeedClass != 0)
            {
                travelled++;
            }
        }

        // Without these two, a brain that ignored its seed entirely — or one that only ever stood still — would pass:
        // equality of two sequences says nothing unless the sequences contain decisions and differ for different seeds.
        Assert.That(travelled, Is.GreaterThan(0), "the compared sequences never moved, so equality proved nothing");

        // The negative control is on the DESTINATIONS and not on the intent stream, because a bot in a world that never
        // changes never arrives: it chooses once and re-sends the same heading for every tick after, so the whole
        // sequence is one decision and two seeds that pick the same of seven cities produce identical streams. Seeds
        // 999 and 12345 do exactly that, which is how this control failed while the generator was working perfectly.
        var destinations = new HashSet<int>();
        for (var seed = 1u; seed <= 8u; seed++)
        {
            var brain = new BotBrain(BotMap.Cities, seed);
            for (var tick = 1L; tick <= 20 && brain.Goal != Goal.Travelling; tick++)
            {
                brain.Decide(Empty(10f, 20f), tick * 100);
            }

            if (brain.Goal == Goal.Travelling)
            {
                destinations.Add(brain.TargetRegion);
            }
        }

        Assert.That(destinations, Has.Count.GreaterThan(1), "eight differently seeded bots all chose the same city, so the seed is not being read");
    }

    /// <summary>A bot numbered 0 still decides: xorshift is dead at zero and the constructor must not hand it one.</summary>
    /// <remarks>
    /// <b>The assertion is that the bot TRAVELS, not that it does something.</b> A dead xorshift returns 0 for every
    /// roll, and a roll of 0 is under the idle threshold — so the brain idles, and an idle's first command is
    /// <c>Move(0, 0)</c>, whose <c>Kind</c> is not <c>None</c>. The old assertion therefore passed with the exact
    /// generator it was named after: remove the constructor's guard and it stayed green. A speed class that is not zero
    /// cannot come out of an endless idle.
    /// </remarks>
    [Test]
    public void ASeedOfZeroStillDecides()
    {
        var brain = Brain(0u);
        var travelled = false;
        for (var tick = 1; tick <= 40 && !travelled; tick++)
        {
            travelled = brain.Decide(Empty(0f, 0f), tick * 100).SpeedClass != 0;
        }

        Assert.That(travelled, Is.True, "a bot seeded 0 only ever stood still, which is what a dead xorshift looks like");
    }

    /// <summary>A brain with no cities is refused at construction rather than dividing by zero mid-run.</summary>
    [Test]
    public void ABrainWithNoCitiesIsRefused()
        => Assert.That(() => new BotBrain([], 7u), Throws.TypeOf<ArgumentException>(), "an empty map was accepted and would divide by zero on the first roll");

    // ── Missions (lair-objectives.md B-3, B-4) ──────────────────────────────────────────────────────────────────────

    private static Perceived Holding(float x, float z, float lairX, float lairZ) => Empty(x, z) with
    {
        MissionsHeld = 1,
        HasMissionLair = true,
        MissionLairX = lairX,
        MissionLairZ = lairZ,
    };

    /// <summary>
    /// Content levels (review 1, CL-13): a mission's defenders are fought whatever their level — a CL-1 bot at a lair of level 30 attacks the defender in
    /// reach, where it would not HUNT that creature met in the open. Filtering them out of sight left a bot idle at its shielded lair for ten minutes.
    /// </summary>
    [Test]
    public void AMissionsDefenders_AreFought_AboveTheBotsLevel()
    {
        var brain = new BotBrain(BotMap.Cities, 7u) { CombatLevel = 1 };
        var shielded = Holding(580f, 790f, 600f, 800f) with
        {
            MissionLair = 77u,
            MissionLairGuards = 2,
            NearestCreature = 31u,
            NearestCreatureX = 584f,
            NearestCreatureZ = 793f,
            NearestCreatureM = 5f,
            NearestCreatureLevel = 30,
        };
        var atDefender = brain.Decide(in shielded, 10);
        Assert.Multiple(() =>
        {
            Assert.That(brain.Goal, Is.EqualTo(Goal.Questing));
            Assert.That(atDefender.Kind, Is.EqualTo(IntentKind.Attack), "a defender above the bot's level was not attacked");
            Assert.That(atDefender.NetId, Is.EqualTo(31u));
        });

        // The same creature met with no mission held is not hunted.
        var wild = new BotBrain(BotMap.Cities, 7u) { CombatLevel = 1 };
        var open = Empty(580f, 790f) with
        {
            NearestCreature = 31u,
            NearestCreatureX = 584f,
            NearestCreatureZ = 793f,
            NearestCreatureM = 5f,
            NearestCreatureLevel = 30,
        };
        wild.Decide(in open, 10);
        Assert.That(wild.Goal, Is.Not.EqualTo(Goal.Hunting), "a CL-1 bot hunts a level-30 creature");
    }

    /// <summary>B-3: a mission held is ridden out to — on the swoop, straight at its lair — then fought: its defenders first, the lair once they are down.</summary>
    [Test]
    public void AMissionsLairIsRiddenTo_ItsDefendersKilled_ThenTheLairAttacked()
    {
        var brain = Brain();

        // 600 m east and 800 m north: a 3-4-5 triangle, so the heading is one angle only.
        var far = Holding(0f, 0f, 600f, 800f);
        var first = brain.Decide(in far, 1);
        var mount = brain.Decide(in far, 2);
        Assert.Multiple(() =>
        {
            Assert.That(brain.Goal, Is.EqualTo(Goal.Questing), "a mission held with its lair placed did not become the goal");
            Assert.That(first.Kind, Is.EqualTo(IntentKind.Move));
            Assert.That(first.Heading, Is.EqualTo(MathF.Atan2(800f, 600f)).Within(0.001f), "it does not head for the lair");
            Assert.That(mount.Kind, Is.EqualTo(IntentKind.Mount), "a kilometre's ride was not ridden on the swoop");
            Assert.That(mount.On, Is.True);
        });

        // There — still on the swoop it rode in on, which it leaves on the next tick — a defender 15 m away and 20 m from the lair: it is shot, the lair is
        // not yet (its guards stand).
        var shielded = Holding(580f, 790f, 600f, 800f) with
        {
            Mounted = true,
            MissionLair = 77u,
            MissionLairGuards = 2,
            NearestCreature = 31u,
            NearestCreatureX = 590f,
            NearestCreatureZ = 801f,
            NearestCreatureM = 15f,
        };
        var atDefender = brain.Decide(in shielded, 10);

        // The guards down: the lair.
        var open = Holding(590f, 795f, 600f, 800f) with { MissionLair = 77u, MissionLairGuards = 0 };
        var atLair = brain.Decide(in open, 11);
        Assert.Multiple(() =>
        {
            Assert.That(atDefender.Kind, Is.EqualTo(IntentKind.Attack), "a defender in reach was not attacked");
            Assert.That(atDefender.NetId, Is.EqualTo(31u), "it fired at the shielded lair, not its defender");
            Assert.That(atLair.Kind, Is.EqualTo(IntentKind.Attack));
            Assert.That(atLair.NetId, Is.EqualTo(77u), "the lair, open now, was not attacked");
        });

        // Paid: the journal lists nothing, and the mission is over.
        brain.Decide(Empty(590f, 795f), 12);
        Assert.That(brain.Goal, Is.Not.EqualTo(Goal.Questing), "a mission the journal no longer lists is still being done");
    }

    /// <summary>B-4: a mission not done in <see cref="BotBrain.QuestSeconds"/> is cancelled, once.</summary>
    [Test]
    public void AMissionNotDoneInTenMinutesIsCancelled()
    {
        var brain = Brain();
        var stuck = Holding(0f, 0f, 30f, 0f) with { MissionLair = 5u, MissionLairGuards = 3 };
        var limit = (long)(BotBrain.QuestSeconds * 4) + 2;
        var cancels = 0;
        for (var tick = 1L; tick <= limit + 8; tick++)
        {
            cancels += brain.Decide(in stuck, tick).Kind == IntentKind.AbandonMission ? 1 : 0;
        }

        Assert.Multiple(() =>
        {
            Assert.That(cancels, Is.EqualTo(1), "a mission past its time was not cancelled exactly once");
            Assert.That(brain.MissionsGivenUp, Is.EqualTo(1));
        });
    }

    /// <summary>
    /// B-2: the journal's answer read — the first mission and its lair (null while drawn), its id for a cancel — and the first read only learns what was
    /// already there, so a run's counts are its own.
    /// </summary>
    [Test]
    public void TheJournal_IsRead_AndItsFirstReadCountsNothing()
    {
        var seen = new HashSet<string>();
        var done = new HashSet<string>();
        const string First = "{\"missions\":[{\"id\":\"9007199254740993\",\"lair\":{\"x\":12.5,\"z\":-40}}],\"completed\":[{\"id\":\"5\"}]}";
        var first = SwgTatooine.Bots.MissionView.Parse(First, seen, done, count: false, out var taken0, out var completed0);
        const string Second = "{\"missions\":[{\"id\":\"9007199254740993\",\"lair\":null},{\"id\":\"8\",\"lair\":null}],"
            + "\"completed\":[{\"id\":\"5\"},{\"id\":\"6\"}]}";
        var second = SwgTatooine.Bots.MissionView.Parse(Second, seen, done, count: true, out var taken1, out var completed1);
        var none = SwgTatooine.Bots.MissionView.Parse("{\"missions\":[],\"completed\":[]}", seen, done, count: true, out _, out _);
        Assert.Multiple(() =>
        {
            Assert.That(first, Is.EqualTo(new SwgTatooine.Bots.MissionView(1, true, 12.5f, -40f, 9007199254740993UL)), "a 64-bit id lost precision, or the lair");
            Assert.That((taken0, completed0), Is.EqualTo((0, 0)), "the first read counted what was already there");
            Assert.That(second.Held, Is.EqualTo(2));
            Assert.That(second.LairKnown, Is.False, "a lair being drawn was given a position");
            Assert.That((taken1, completed1), Is.EqualTo((1, 1)), "only the new mission and the new completion count");
            Assert.That(none, Is.EqualTo(SwgTatooine.Bots.MissionView.None));
        });
    }

    /// <summary>A bot that lives in a city and stands outside it goes back to it.</summary>
    [Test]
    public void AHomeBotAwayFromHome_GoesHome()
    {
        var home = BotMap.Cities[0];
        var away = BotMap.Cities[1];
        var brain = new BotBrain(BotMap.Cities, 7u, 4d, null, homeRegion: 1);
        brain.Decide(Empty(away.X, away.Z), 1);
        Assert.Multiple(() =>
        {
            Assert.That(brain.Goal, Is.EqualTo(Goal.Travelling), "a bot away from home did not set off");
            Assert.That(brain.TargetRegion, Is.EqualTo(1), $"it set off for region {brain.TargetRegion}, not {home.Name}");
        });
    }

    /// <summary>A bot at home stays: over thousands of decisions it never sets off for another city, by any mode.</summary>
    [Test]
    public void AHomeBotAtHome_NeverLeavesForAnotherCity()
    {
        var home = BotMap.Cities[0];
        foreach (var seed in new[] { 1u, 7u, 99u, 12345u })
        {
            var brain = new BotBrain(BotMap.Cities, seed, 4d, null, homeRegion: 1);
            for (var tick = 1L; tick < 20_000; tick++)
            {
                brain.Decide(Empty(home.X + 20f, home.Z - 30f), tick);
                Assert.That(brain.TargetRegion is 0 or 1, Is.True, $"seed {seed}, tick {tick}: it set off for region {brain.TargetRegion}");
                Assert.That(brain.Goal, Is.Not.EqualTo(Goal.Waiting), $"seed {seed}: it queued for a shuttle");
            }
        }
    }

    /// <summary>Half the bots live in the home city, never more than its cap; none when the city is unknown.</summary>
    [TestCase(500, 250)]
    [TestCase(8000, 500)]
    [TestCase(30, 15)]
    [TestCase(1, 0)]
    public void HalfTheBots_LiveInPortSable_UpToFiveHundred(int count, int expected)
    {
        var options = new SwgTatooine.Bots.BotSwarmOptions { Count = count };
        Assert.Multiple(() =>
        {
            Assert.That(options.HomeRegion, Is.EqualTo(1), "Port Sable is not the default home");
            Assert.That(options.HomeBots, Is.EqualTo(expected));
            Assert.That(new SwgTatooine.Bots.BotSwarmOptions { Count = count, HomeCity = "none" }.HomeBots, Is.Zero);
        });
    }

    /// <summary>A lair being drawn or moved — held, no position yet — is waited for, not ridden at a stale point.</summary>
    [Test]
    public void ALairBeingMovedIsWaitedFor()
    {
        var brain = Brain();
        brain.Decide(Holding(0f, 0f, 500f, 0f), 1);
        Assert.That(brain.Goal, Is.EqualTo(Goal.Questing), "the premise");
        var moving = Empty(0f, 0f) with { MissionsHeld = 1, HasMissionLair = false, Mounted = true };
        var wait = brain.Decide(in moving, 2);
        Assert.Multiple(() =>
        {
            Assert.That(wait.Kind, Is.EqualTo(IntentKind.Move));
            Assert.That(wait.SpeedClass, Is.Zero, "it rode on toward where the lair used to be");
        });
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
        map.Observe(1, Port, 0f, 0f);
        map.Observe(2, Port, 100f, 0f);

        var route = Routing.Plan(new Journey(0f, 0f, 80f, 0f, 1, 2, map));
        Assert.That(route.Mode, Is.Not.EqualTo(TravelMode.Shuttle), "an 80 m hop was flown, which costs a minute of waiting to save three seconds");
    }

    /// <summary>Crossing the planet is flown, once both ports are known, and the route names the near port.</summary>
    /// <remarks>
    /// <b>The near port is deliberately NOT where the bot is standing.</b> It used to be, and that made the assertion on
    /// <c>PortX</c> a tautology: returning the journey's own origin instead of the port's position passed, and
    /// <c>PortZ</c> was never asserted at all. 300 m of offset on both axes is what makes the two distinguishable.
    /// </remarks>
    [Test]
    public void CrossingThePlanetIsFlown()
    {
        var map = new FacilityMap();
        map.Observe(1, Port, 3760f, -4468f);
        map.Observe(3, Port, -2890f, 2198f);

        var route = Routing.Plan(new Journey(3460f, -4768f, -2890f, 2198f, 1, 3, map));
        Assert.Multiple(() =>
        {
            Assert.That(route.Mode, Is.EqualTo(TravelMode.Shuttle), "a 10 km crossing was walked or ridden");
            Assert.That(route.PortX, Is.EqualTo(3760f), "the route must name the port to walk to first, not where the bot is standing");
            Assert.That(route.PortZ, Is.EqualTo(-4468f), "the route's port is on the wrong axis");
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
        map.Observe(1, Port, 3460f, -4768f);

        var route = Routing.Plan(new Journey(3460f, -4768f, -2890f, 2198f, 1, 3, map));
        Assert.That(route.Mode, Is.Not.EqualTo(TravelMode.Shuttle), "the bot flew to a port it has never seen");

        map.Observe(3, Port, -2890f, 2198f);
        var informed = Routing.Plan(new Journey(3460f, -4768f, -2890f, 2198f, 1, 3, map));
        Assert.That(informed.Mode, Is.EqualTo(TravelMode.Shuttle), "having learned the far port changed nothing, so the map is not being read");
    }

    /// <summary>Out in the desert there is no port to walk to, so the shuttle is not an option at all.</summary>
    [Test]
    public void FromTheDesertThereIsNoShuttle()
    {
        var map = new FacilityMap();
        map.Observe(1, Port, 3460f, -4768f);
        map.Observe(3, Port, -2890f, 2198f);

        var route = Routing.Plan(new Journey(0f, 0f, -2890f, 2198f, 0, 3, map));
        Assert.That(route.Mode, Is.Not.EqualTo(TravelMode.Shuttle), "a bot in the open desert boarded a shuttle from nowhere");
    }

    /// <summary>A journey too short to fly is RIDDEN on the swoop rather than run, and the speed classes are the server's.</summary>
    /// <remarks>
    /// <b>Nothing asserted that riding was ever chosen.</b> Every other case here is "(not) the shuttle", so deleting
    /// the riding branch outright left them all green — and the speed class a mode asks for, which is the only part of
    /// a route that reaches the wire, had no test of its own at all.
    /// </remarks>
    [Test]
    public void AJourneyTooShortToFlyIsRidden()
    {
        var map = new FacilityMap();
        map.Observe(1, Port, 0f, 0f);
        map.Observe(2, Port, 900f, 0f);

        var route = Routing.Plan(new Journey(0f, 0f, 900f, 0f, 1, 2, map));
        Assert.Multiple(() =>
        {
            Assert.That(route.Mode, Is.EqualTo(TravelMode.Swoop), "a 900 m ride was run on foot or flown");
            Assert.That(route.Seconds, Is.EqualTo((900d / Routing.SwoopMps) + Routing.MountSeconds).Within(0.001), "the ride was not costed at the swoop");
            // Against the server's own constants: the bot keeps copies because it may not reference the server, and a copy pinned to a literal here
            // stayed green when the server renumbered its classes.
            Assert.That(
                Routing.SpeedClassOf(TravelMode.Swoop), Is.EqualTo(SpeedClasses.Swoop), "a swoop must ask for the class the server grants a mounted player");
            Assert.That(Routing.SpeedClassOf(TravelMode.Run), Is.EqualTo(SpeedClasses.Run), "running must ask for the run class");
            Assert.That(Routing.SpeedClassOf(TravelMode.Shuttle), Is.EqualTo(SpeedClasses.Run), "walking to a port is done at a run");
        });
    }

    /// <summary>Thirty seconds on foot is the line: under it the bot runs, over it it rides — at the server's real run, 10 m/s.</summary>
    /// <remarks>
    /// Both sides of the line, because the run speed the line is drawn in was wrong for as long as the gait scale existed: at the stale 5 m/s, a 250 m
    /// hop would be fifty "seconds" and ridden.
    /// </remarks>
    [TestCase(250f, TravelMode.Run)]
    [TestCase(310f, TravelMode.Swoop)]
    public void ThirtySecondsOnFootIsWhereRidingStarts(float metres, TravelMode expected) =>
        Assert.That(Routing.Plan(new Journey(0f, 0f, metres, 0f, 0, 0, null)).Mode, Is.EqualTo(expected));

    /// <summary>The learned map answers by region and kind, and forgets nothing it has seen.</summary>
    [Test]
    public void TheMapRemembersWhatItWasShown()
    {
        var map = new FacilityMap();
        Assert.That(map.TryFind(4, Port, out _), Is.False, "an unseen region answered");

        map.Observe(4, Port, 10f, 20f);
        map.Observe(4, 1, 30f, 40f);
        map.Observe(0, Port, 50f, 60f);

        Assert.Multiple(() =>
        {
            Assert.That(map.TryFind(4, Port, out var port), Is.True);
            Assert.That(port, Is.EqualTo((10f, 20f)));
            Assert.That(map.TryFind(4, 1, out var terminal), Is.True, "two kinds in one city must not overwrite each other");
            Assert.That(terminal, Is.EqualTo((30f, 40f)));
            Assert.That(map.Count, Is.EqualTo(2), "a facility in no region was remembered under something");
            Assert.That(map.TryFind(0, Port, out _), Is.False, "region 0 means 'no region', so nothing can be filed under it");
        });
    }

    /// <summary>A second sighting does not move a facility, and the first one stands.</summary>
    /// <remarks>
    /// These structures never move, so this is about the table rather than the world: the scan files what it sees every
    /// tick, and the first-sighting rule is what makes that a no-op instead of a hashed write per structure per tick.
    /// </remarks>
    [Test]
    public void ASecondSightingKeepsTheFirst()
    {
        var map = new FacilityMap();
        map.Observe(2, Port, 10f, 20f);
        map.Observe(2, Port, 999f, 999f);

        Assert.That(map.TryFind(2, Port, out var port), Is.True);
        Assert.That(port, Is.EqualTo((10f, 20f)), "a second sighting of the same facility replaced the first");
    }
}
