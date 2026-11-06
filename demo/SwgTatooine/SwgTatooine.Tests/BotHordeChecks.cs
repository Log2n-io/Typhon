using System.Linq;
using NUnit.Framework;
using SwgTatooine.Bots.Brain;

namespace SwgTatooine.Tests;

/// <summary>
/// A bot and a horde's call (horde.md § 6): it answers by a roll that falls with the help already found and with the distance, never when enough have;
/// with a rally point it travels to its city, walks to the point, stands there, and fights a raider that comes near whatever its level; the event over,
/// it goes back to its day.
/// </summary>
[TestFixture]
internal sealed class BotHordeChecks
{
    private const ulong Event = 77;

    private static BotBrain Brain(uint seed) => new(BotMap.Cities, seed);

    private static City Bestine => BotMap.Cities.First(c => c.Name == "Whitestone");

    private static Perceived At(float x, float z) => new() { HasBody = true, X = x, Z = z };

    private static int Answers(int help, float distance)
    {
        var target = Bestine;
        var n = 0;
        for (var seed = 1u; seed <= 2000u; seed++)
        {
            n += Brain(seed).ShouldAnswer(Event, 1, help, target.X, target.Z, target.X + distance, target.Z, 1) ? 1 : 0;
        }

        return n;
    }

    /// <summary>The roll: none when enough have answered or the event is over; fewer when most help is found, fewer from far away; once per event.</summary>
    [Test]
    public void TheAnswer_FallsWithTheHelpFoundAndTheDistance_AndNoneWhenEnough()
    {
        var target = Bestine;
        var brain = Brain(3);
        var first = Enumerable.Range(0, 200).Count(i => brain.ShouldAnswer(Event, 1, 0, target.X, target.Z, target.X, target.Z, i));
        Assert.Multiple(() =>
        {
            Assert.That(Answers(255, 0f), Is.Zero, "enough: nobody answers");
            Assert.That(Enumerable.Range(1, 500).Count(s => Brain((uint)s).ShouldAnswer(Event, 3, 0, target.X, target.Z, target.X, target.Z, 1)), Is.Zero,
                "won: nobody answers");
            Assert.That(Answers(0, 0f), Is.GreaterThan(Answers(192, 0f)), "more answer when no help has come");
            Assert.That(Answers(0, 0f), Is.GreaterThan(Answers(0, 6000f)), "more answer from near than from far");
            Assert.That(Answers(0, 0f), Is.InRange(2000 * 3 / 100, 2000 * 12 / 100), "about 10 % of the 70 % willing, per status");
            Assert.That(first, Is.LessThanOrEqualTo(1), "a bot answers once, until its retry");
        });
    }

    /// <summary>Told its rally point from another city, a bot travels to that city; there it walks to the point and stands; the event over, it stops.</summary>
    [Test]
    public void ARallyPoint_IsTravelledTo_WalkedTo_AndLeftWhenTheEventEnds()
    {
        var target = Bestine;
        var brain = Brain(11);
        var far = BotMap.Cities.First(c => c.Name == "Port Sable");
        brain.Decide(At(far.X, far.Z), 1);
        brain.HearRally(Event, target.X, target.Z + 30f, 1, 2);
        brain.Decide(At(far.X, far.Z), 3);
        Assert.That(brain.Goal, Is.AnyOf(Goal.Travelling, Goal.Waiting), "a rally in another city: it travels");
        Assert.That(brain.TargetRegion, Is.EqualTo(System.Array.IndexOf(BotMap.Cities, target) + 1), "to the rally's city");

        var there = Brain(11);
        there.HearRally(Event, target.X, target.Z + 30f, 1, 1);
        there.Decide(At(target.X, target.Z - 20f), 2);
        Assert.That(there.Goal, Is.EqualTo(Goal.Defending), "in the city: it walks to the point");

        var standing = there.Decide(At(target.X, target.Z + 29f), 3);
        Assert.Multiple(() =>
        {
            Assert.That(standing.Kind, Is.EqualTo(IntentKind.None), "at the point it stands");
            Assert.That(there.RalliesArrived, Is.EqualTo(1));
        });

        there.HearRally(Event, target.X, target.Z + 30f, 0, 4);
        there.Decide(At(target.X, target.Z + 29f), 5);
        Assert.Multiple(() =>
        {
            Assert.That(there.Goal, Is.Not.EqualTo(Goal.Defending), "the event over, it goes back to its day");
            Assert.That(there.RalliesEnded, Is.EqualTo(1));
        });
    }

    /// <summary>
    /// A gather point out in the desert, past the city's edge (a fortress 800 m out): a defender between the city and the point walks to the point — it is
    /// not sent back to the city as it crosses the edge (the third 4K raid, 2026-11-05: 2 000 defenders milling at the edge, one at the gather point).
    /// </summary>
    [Test]
    public void ADefenderPastTheCitysEdge_WalksOnToAGatherPointInTheDesert()
    {
        var target = Bestine;
        var rally = (X: target.X + target.Radius + 450f, Z: target.Z);
        var brain = Brain(11);
        brain.HearRally(Event, rally.X, rally.Z, 1, 1);
        brain.Decide(At(target.X + target.Radius + 40f, target.Z), 2);
        Assert.That(brain.Goal, Is.EqualTo(Goal.Defending), "walking on to the point, not back to the city");
    }

    /// <summary>
    /// The event over ends the call though the stand-down never came, and the radar's last blips with it (Loïc, 2026-11-05: 571 bots stood beside the town
    /// hall after the 4K battle, committed by a rally told after the stand-down, walking to where the raiders had been).
    /// </summary>
    [Test]
    public void TheEventOver_EndsTheCall_AndItsRadar_ThoughNoStandDownCame()
    {
        var target = Bestine;
        var hall = (X: target.X, Z: target.Z + 54f);
        var city = System.Array.IndexOf(BotMap.Cities, target);
        BotBrain Rallied()
        {
            var brain = Brain(11);
            brain.HearRally(Event, target.X + target.Radius + 600f, target.Z, 1, 1);
            brain.HearHorde(Event, 2, city, hall.X, hall.Z, 1000, 1000, 1);
            brain.HearRadar([hall.X + 5f], [hall.Z], [12]);
            return brain;
        }

        var holding = Rallied();
        holding.Decide(At(hall.X - 40f, hall.Z), 2);
        Assert.That(holding.Goal, Is.EqualTo(Goal.Defending), "the premise: in the town, it seeks the radar's blip");

        var over = Rallied();
        over.Decide(At(hall.X - 40f, hall.Z), 2);
        over.HearHorde(Event, 4, city, hall.X, hall.Z, 0, 1000, 3);
        for (var tick = 4; tick < 40; tick++)
        {
            over.Decide(At(hall.X + 5f, hall.Z), tick);
        }

        Assert.Multiple(() =>
        {
            Assert.That(over.Goal, Is.Not.EqualTo(Goal.Defending), "the event lost, it still defended");
            Assert.That(over.RalliesEnded, Is.EqualTo(1));
        });
    }

    /// <summary>
    /// A defender arriving on its swoop gets off it and fights (Loïc, 2026-11-04: they sat on their swoops doing nothing): it asks to dismount at once, and
    /// on foot it attacks the raider beside it.
    /// </summary>
    [Test]
    public void ADefender_OnItsSwoop_GetsOffAndFights()
    {
        var target = Bestine;
        var brain = Brain(5);

        // The rally point out in the desert, 500 m from the town, and the bot riding to it from far away: its journey wishes the swoop on.
        var (rx, rz) = (target.X + target.Radius + 500f, target.Z);
        brain.HearRally(Event, rx, rz, 1, 1);
        var far = BotMap.Cities.First(c => c.Name == "Port Sable");
        for (var tick = 2; tick < 40; tick++)
        {
            brain.Decide(At(far.X + far.Radius + 200f, far.Z) with { Mounted = tick > 4 }, tick);
        }

        var raider = At(rx + 5f, rz) with
        {
            NearestCreature = 9u,
            NearestCreatureX = rx + 25f,
            NearestCreatureZ = rz,
            NearestCreatureM = 20f,
            NearestCreatureLevel = 28,
        };

        var asked = false;
        for (var tick = 50; tick < 60 && !asked; tick++)
        {
            var intent = brain.Decide(raider with { Mounted = true }, tick);
            asked = intent.Kind == IntentKind.Mount && !intent.On;
            Assert.That(intent.Kind, Is.Not.EqualTo(IntentKind.Attack), "an attack from the swoop");
        }

        var onFoot = brain.Decide(in raider, 70);
        Assert.Multiple(() =>
        {
            Assert.That(asked, Is.True, "it never asked to get off its swoop");
            Assert.That((onFoot.Kind, onFoot.NetId), Is.EqualTo((IntentKind.Attack, 9u)), "on foot, it fights the raider");
        });
    }

    /// <summary>
    /// A defender on its swoop with the fight still far rides there (Loïc, 2026-11-06: "they all take their swoop a few metres, then stand down, and try
    /// again"): it never asks to get off while the radar's cluster is a ride away and no raider is in view, and it gets off once one is.
    /// </summary>
    [Test]
    public void ADefender_OnItsSwoop_RidesToAFarFight_AndGetsOffInView()
    {
        var target = Bestine;
        var brain = Brain(50);
        var (rx, rz) = (target.X + target.Radius + 300f, target.Z);
        brain.HearRally(Event, rx, rz, 1, 1);
        brain.HearRadar([rx + 700f], [rz], [20]);
        var offs = 0;
        var x = rx + 20f;
        for (var tick = 2; tick < 80; tick++)
        {
            // On its swoop from its second decision: the first, a brain just made, has no wish yet.
            var intent = brain.Decide(At(x, rz) with { Mounted = tick > 2 }, tick);
            offs += tick > 2 && intent.Kind == IntentKind.Mount && !intent.On ? 1 : 0;
            x += 2f;
        }

        var inView = At(x, rz) with
        {
            Mounted = true,
            NearestCreature = 9u,
            NearestCreatureX = x + 100f,
            NearestCreatureZ = rz,
            NearestCreatureM = 100f,
            NearestCreatureRaider = true,
            NearestCreatureLevel = 28,
        };
        var asked = false;
        for (var tick = 80; tick < 90 && !asked; tick++)
        {
            var intent = brain.Decide(in inView, tick);
            asked = intent.Kind == IntentKind.Mount && !intent.On;
        }

        Assert.Multiple(() =>
        {
            Assert.That(offs, Is.Zero, "it got off its swoop with the fight 700 m away and nothing in view");
            Assert.That(asked, Is.True, "a raider in view: on foot");
        });
    }

    /// <summary>
    /// An assault bot (horde.md § 10) goes for the fortress during the raid, past the raiders in reach, and shoots it from its stand-off; before the
    /// waves, it does not.
    /// </summary>
    [Test]
    public void AnAssaultBot_ShootsTheFortress_DuringTheRaid()
    {
        var target = Bestine;
        var brain = Brain(5);   // 5 % 100 < AssaultPercent: one of the assault
        var (fx, fz) = (target.X + target.Radius + 400f, target.Z);
        brain.HearRally(Event, fx - 120f, fz, 1, 1);
        brain.HearHorde(Event, 2, System.Array.IndexOf(BotMap.Cities, target), target.X, target.Z + 30f, 255, 255, 1, fx, fz);
        var near = At(fx - 30f, fz) with
        {
            Fortress = 77u,
            FortressX = fx,
            FortressZ = fz,
            NearestCreature = 9u,
            NearestCreatureX = fx - 20f,
            NearestCreatureZ = fz,
            NearestCreatureM = 10f,
            NearestCreatureLevel = 28,
            NearestCreatureRaider = true,
        };
        var intent = brain.Decide(in near, 2);
        Assert.That((intent.Kind, intent.NetId), Is.EqualTo((IntentKind.Attack, 77u)), $"the assault bot did not shoot the fortress: {intent.Kind} {brain.Goal}");

        // Drawn into a fight by a raider on it, then free again: back to the fortress, not on in a hunt that never ends.
        brain.Decide(near with { Attacker = 9u, AttackerX = fx - 20f, AttackerZ = fz, AttackerM = 10f, AttackerLevel = 28, AttackerRaider = true }, 3);
        var free = brain.Decide(in near, 3 + 40);
        Assert.That((free.Kind, free.NetId), Is.EqualTo((IntentKind.Attack, 77u)), $"the assault bot stayed in its fight: {free.Kind} {brain.Goal}");

        // Down to its fist, no assault: a fist reaches no wall from the stand-off (the garrison siege, 2026-11-04).
        var fist = Brain(5);
        fist.HearRally(Event, fx - 120f, fz, 1, 1);
        fist.HearHorde(Event, 2, System.Array.IndexOf(BotMap.Cities, target), target.X, target.Z + 30f, 255, 255, 1, fx, fz);
        Assert.That(fist.Decide(near with { Unarmed = true }, 2).NetId, Is.Not.EqualTo(77u), "a fist assaulted the fortress");

        var before = Brain(5);
        before.HearRally(Event, fx - 120f, fz, 1, 1);
        before.HearHorde(Event, 1, System.Array.IndexOf(BotMap.Cities, target), target.X, target.Z + 30f, 255, 255, 1, fx, fz);
        Assert.That(before.Decide(in near, 2).NetId, Is.Not.EqualTo(77u), "the fortress was shot before the waves");
    }

    /// <summary>Defending, a bot fights a raider that comes near even far past its level, which it would leave alone on any other day.</summary>
    [Test]
    public void ADefender_FightsARaiderPastItsLevel()
    {
        var target = Bestine;
        var brain = Brain(5);
        brain.CombatLevel = 1;
        brain.HearRally(Event, target.X, target.Z + 30f, 1, 1);
        var raider = At(target.X, target.Z + 29f) with
        {
            NearestCreature = 9u,
            NearestCreatureX = target.X + 20f,
            NearestCreatureZ = target.Z + 29f,
            NearestCreatureM = 20f,
            NearestCreatureLevel = 40,
        };
        var intent = brain.Decide(in raider, 2);
        Assert.Multiple(() =>
        {
            Assert.That(brain.Goal, Is.EqualTo(Goal.Hunting), "a level-40 raider at the rally point is fought by a CL-1 defender");
            Assert.That((intent.Kind, intent.NetId), Is.EqualTo((IntentKind.Attack, 9u)));
        });
    }
}
