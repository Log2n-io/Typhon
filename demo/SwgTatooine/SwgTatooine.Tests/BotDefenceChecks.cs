using System;
using NUnit.Framework;
using SwgTatooine.Bots.Brain;
using SwgTatooine.Navigation;

namespace SwgTatooine.Tests;

/// <summary>
/// A bot attacked fights back or runs (BotBrain.Defence.cs; Loïc, 2026-11-04: "I saw bots being attacked and not fight back or walk away, so just wait and
/// die"): one decision per engagement, on the odds — the time to kill against the time to die — and, inside the fight, the bars' own odds; a run that does
/// not shake it off ends in a fight.
/// </summary>
[TestFixture]
internal sealed class BotDefenceChecks
{
    private static BotBrain Brain(uint seed = 7u) => new(BotMap.Cities, seed);

    /// <summary>A body in the open with a creature on it: <paramref name="level"/>, 8 m east and 6 m north (10 m away), the nearest creature too.</summary>
    private static Perceived Bitten(int level = 1, float health = 1f, uint attacker = 42u, float theirs = 0f, int attackers = 1) => new()
    {
        HasBody = true,
        NearestCreature = attacker,
        NearestCreatureX = 8f,
        NearestCreatureZ = 6f,
        NearestCreatureM = 10f,
        NearestCreatureLevel = level,
        Attacker = attacker,
        AttackerX = 8f,
        AttackerZ = 6f,
        AttackerM = 10f,
        AttackerLevel = level,
        AttackerHealth = theirs,
        Attackers = attackers,
        Health = health,
    };

    private static bool RunsFrom(Intent intent) =>
        intent.Kind == IntentKind.Attack && intent.NetId == 0u && intent.SpeedClass == Routing.SpeedClassOf(TravelMode.Run)
        && MathF.Abs(intent.Heading - MathF.Atan2(-6f, -8f)) < 0.001f;

    /// <summary>
    /// The odds are the balance model's (content-levels.md): on level, 8 s to kill and 75 s to die; a stronger attacker takes longer to kill and kills
    /// sooner; both scale with the bars; a pack shares the time to die; 0 is "not sent" — taken as whole, one attacker, on level.
    /// </summary>
    [Test]
    public void TheOdds_AreTheBalanceModels()
    {
        var (kill, die) = BotBrain.Odds(10, 1f, 10, 1f);
        var (strongKill, strongDie) = BotBrain.Odds(10, 1f, 30, 1f);
        var (halfKill, halfDie) = BotBrain.Odds(10, 0.5f, 10, 0.25f);
        var (_, packDie) = BotBrain.Odds(10, 1f, 10, 1f, attackers: 3);
        Assert.Multiple(() =>
        {
            Assert.That(kill, Is.EqualTo(8d).Within(1e-9), "on level, an on-level beast falls in 8 s");
            Assert.That(die, Is.EqualTo(75d).Within(1e-9), "on level, an on-level beast kills in 75 s");
            Assert.That(strongKill, Is.GreaterThan(kill));
            Assert.That(strongDie, Is.LessThan(die));
            Assert.That(halfKill, Is.EqualTo(2d).Within(1e-9), "a quarter of its bar is a quarter of the time");
            Assert.That(halfDie, Is.EqualTo(37.5d).Within(1e-9), "half the bot's bar is half the time");
            Assert.That(packDie, Is.EqualTo(25d).Within(1e-9), "three on the bot kill it three times sooner");
            Assert.That(BotBrain.Odds(0, 0f, 0, 0f), Is.EqualTo((8d, 75d)), "nothing sent is one on-level beast at full health");
        });
    }

    /// <summary>
    /// The founding case: an idling bot bitten does something — it did nothing, its goal's <c>Intent.None</c> standing it there till it died. Every
    /// reaction is one of the two: the attacker named, or a run straight away from it.
    /// </summary>
    [Test]
    public void AnIdlingBot_Attacked_FightsBackOrRuns_NeverStands()
    {
        for (var seed = 1u; seed <= 50u; seed++)
        {
            var brain = Brain(seed);
            brain.Decide(new Perceived { HasBody = true }, 1);
            var intent = brain.Decide(Bitten(), 2);
            Assert.That(intent.Kind == IntentKind.Attack && (intent.NetId == 42u || RunsFrom(intent)), Is.True,
                $"seed {seed}: an attacked bot sent {intent.Kind} {intent.NetId}, neither fighting back nor running");
            Assert.That(brain.Attacked, Is.EqualTo(1), $"seed {seed}: the attacker was not rolled for once");
        }
    }

    /// <summary>
    /// Over a fleet of seeds, a fight the bot wins easily is always taken, a lost one never, and one in between by the bot's nerve: some take it, some do
    /// not. The level gap alone decides nothing; the run counted is the odds'.
    /// </summary>
    [Test]
    public void AWonFight_IsTaken_ALostOne_Never_AClose_OneByNerve()
    {
        int weakFought = 0, packFought = 0, closeFought = 0, oddsRuns = 0;
        const int Seeds = 400;
        for (var seed = 1u; seed <= Seeds; seed++)
        {
            var weak = Brain(seed);
            weak.CombatLevel = 10;
            weak.Decide(Bitten(level: 8), 1);
            weakFought += (int)weak.FoughtBack;

            // A CL-5 bot and three level-50 creatures on it: 21 s to kill one, 7 s to die.
            var pack = Brain(seed);
            pack.CombatLevel = 5;
            pack.Decide(Bitten(level: 50, attackers: 3), 1);
            packFought += (int)pack.FoughtBack;
            oddsRuns += (int)pack.RunsBy(BotBrain.RunCause.Odds);

            // A CL-1 bot and a level-40 creature: 19 s to kill, 22 s to die — a fight for the brave.
            var close = Brain(seed);
            close.Decide(Bitten(level: 40), 1);
            closeFought += (int)close.FoughtBack;
        }

        Assert.Multiple(() =>
        {
            Assert.That(weakFought, Is.EqualTo(Seeds), "a creature two levels under, alone, was not always fought");
            Assert.That(packFought, Is.Zero, "a lost fight was taken");
            Assert.That(oddsRuns, Is.EqualTo(Seeds), "the runs from a lost fight were not counted as the odds'");
            Assert.That(closeFought, Is.InRange(Seeds * 5 / 100, Seeds * 60 / 100), "a close fight was not taken by some and refused by the others");
        });
    }

    /// <summary>
    /// Hurt, a bot runs from a fight it loses and finishes one it wins: at 20 % a CL-40 bot still kills a level-1 rat (3 s against 50), a CL-10 bot runs
    /// from a level-30 beast (12 s against 8). A health of 0 is "not sent", not dying.
    /// </summary>
    [Test]
    public void Hurt_ItRunsFromALostFight_AndFinishesAWonOne()
    {
        for (var seed = 1u; seed <= 50u; seed++)
        {
            var strong = Brain(seed);
            strong.CombatLevel = 40;
            Assert.That(strong.Decide(Bitten(level: 1, health: 0.2f), 1).NetId, Is.EqualTo(42u), $"seed {seed}: a CL-40 bot at 20 % ran from a level-1 rat");

            var weak = Brain(seed);
            weak.CombatLevel = 10;
            Assert.That(RunsFrom(weak.Decide(Bitten(level: 30, health: 0.2f), 1)), Is.True, $"seed {seed}: a CL-10 bot at 20 % fought a level-30 beast");
            Assert.That(weak.RunsBy(BotBrain.RunCause.Hurt), Is.EqualTo(1), $"seed {seed}: the run was not counted as hurt");

            var unknown = Brain(seed);
            unknown.CombatLevel = 40;
            unknown.Decide(Bitten(level: 1, health: 0f), 1);
            Assert.That(unknown.FoughtBack, Is.EqualTo(1), $"seed {seed}: a health the server never sent was read as dying");
        }
    }

    /// <summary>A CL-10 bot fighting an on-level beast, the bars of the next ticks given by <paramref name="bars"/>, up to <paramref name="ticks"/>.</summary>
    private static BotBrain Fighting(Func<long, (float Own, float Theirs)> bars, long ticks, uint seed = 1u)
    {
        var brain = Brain(seed);
        brain.CombatLevel = 10;
        brain.Decide(Bitten(level: 10, theirs: 1f), 1);
        Assert.That(brain.FoughtBack, Is.EqualTo(1), "the premise: an on-level beast at full health is fought");
        for (var tick = 2L; tick <= ticks; tick++)
        {
            var (own, theirs) = bars(tick);
            brain.Decide(Bitten(level: 10, health: own, theirs: theirs), tick);
        }

        return brain;
    }

    /// <summary>
    /// Inside the fight, the bars decide: a bot losing 10 % a second and dealing 1 % runs at the first window (the fight turned); one dealing 0.2 % a
    /// second and losing 0.05 % fights on past the old 30 s limit.
    /// </summary>
    [Test]
    public void AFightThatTurns_IsBrokenOff_OneBeingWon_GoesOnPast30s()
    {
        const int PerSecond = 4;
        var window = (long)(BotBrain.OddsWindowSeconds * PerSecond) + 2;
        var losing = Fighting(t => (MathF.Max(0.05f, 1f - (0.1f * t / PerSecond)), MathF.Max(0.05f, 1f - (0.01f * t / PerSecond))), window);
        var winning = Fighting(t => (MathF.Max(0.05f, 1f - (0.0005f * t / PerSecond)), MathF.Max(0.02f, 1f - (0.002f * t / PerSecond))), 40 * PerSecond);
        Assert.Multiple(() =>
        {
            Assert.That(losing.RunsBy(BotBrain.RunCause.Losing), Is.EqualTo(1), "a fight lost on its bars was not broken off at the first window");
            Assert.That(winning.Fled, Is.Zero, "a fight being won slowly was broken off");
            Assert.That(winning.Goal, Is.EqualTo(Goal.Hunting), "the fight being won is not going on at 40 s");
        });
    }

    /// <summary>
    /// A fight whose attacker's bar does not move for <see cref="BotBrain.StallSeconds"/> is left: out of reach, behind a wall, not being won.
    /// </summary>
    [Test]
    public void AStalledFight_IsLeft()
    {
        const int PerSecond = 4;
        var stalled = Fighting(_ => (1f, 1f), (long)(BotBrain.StallSeconds * PerSecond) + 2);
        Assert.That(stalled.RunsBy(BotBrain.RunCause.Stalled), Is.EqualTo(1), "a fight in which the attacker was never hurt went on");
    }

    /// <summary>
    /// A pack's bars are read though its nearest biter changes every tick (review, 2026-11-05: each change reset the window, so a pack was never read):
    /// two biters alternating, each losing 0.4 % a second of its own bar, the bot losing 10 % — the fight turns at the first window; the same pack with the
    /// bot unhurt is not left as stalled, because the biters' bars do fall.
    /// </summary>
    [Test]
    public void APacksBars_AreRead_ThoughItsNearestBiterChangesEveryTick()
    {
        const int PerSecond = 4;
        BotBrain Pack(float ownPerSecond, long ticks)
        {
            var brain = Brain(1u);
            brain.CombatLevel = 10;
            brain.Decide(Bitten(level: 10, theirs: 1f, attackers: 2), 1);
            Assert.That(brain.FoughtBack, Is.EqualTo(1), "the premise: a pair of on-level beasts is fought");
            for (var tick = 2L; tick <= ticks; tick++)
            {
                var biter = tick % 2 == 0 ? 42u : 43u;
                var own = MathF.Max(0.05f, 1f - (ownPerSecond * tick / PerSecond));
                brain.Decide(Bitten(level: 10, health: own, attacker: biter, theirs: 1f - (0.004f * tick / PerSecond), attackers: 2), tick);
            }

            return brain;
        }

        var losing = Pack(0.1f, (long)(BotBrain.OddsWindowSeconds * PerSecond) + 2);
        var unhurt = Pack(0f, (long)(BotBrain.StallSeconds * PerSecond) + 8);
        Assert.Multiple(() =>
        {
            Assert.That(losing.RunsBy(BotBrain.RunCause.Losing), Is.EqualTo(1), "a pack fight lost on its bars was not broken off at the first window");
            Assert.That(unhurt.RunsBy(BotBrain.RunCause.Stalled), Is.Zero, "a pack whose bars fall was taken for a stalled fight");
        });
    }

    /// <summary>
    /// Hurt by an attacker whose bar does not move, the bot leaves at <see cref="BotBrain.StallSeconds"/> for the stall — not at the first window as a
    /// lost fight (review, 2026-11-05: a bar that did not move made the time to kill infinite).
    /// </summary>
    [Test]
    public void HurtByABarThatDoesNotMove_ItLeavesAsStalled_NotAsLosing()
    {
        const int PerSecond = 4;
        var brain = Fighting(t => (MathF.Max(0.05f, 1f - (0.01f * t / PerSecond)), 1f), (long)(BotBrain.StallSeconds * PerSecond) + 2);
        Assert.Multiple(() =>
        {
            Assert.That(brain.RunsBy(BotBrain.RunCause.Losing), Is.Zero, "an unhurt attacker's bar was read as a lost fight");
            Assert.That(brain.RunsBy(BotBrain.RunCause.Stalled), Is.EqualTo(1));
        });
    }

    /// <summary>
    /// Cornered, it fights on: a run that could not get away turns into a fight, and the fight is not broken off when its bars turn — that was a flip
    /// between running and fighting until it died (review, 2026-11-05).
    /// </summary>
    [Test]
    public void Cornered_ItFightsOn_WhenTheBarsTurn()
    {
        const int PerSecond = 4;
        BotBrain brain = null;
        for (var seed = 1u; seed <= 400u && brain == null; seed++)
        {
            var b = Brain(seed);
            b.Decide(Bitten(level: 30, theirs: 1f), 1);
            brain = b.Fled == 1 ? b : null;
        }

        Assert.That(brain, Is.Not.Null, "no seed ran from a level-30 attacker");
        for (var tick = 2L; tick <= 40L * PerSecond; tick++)
        {
            var own = MathF.Max(0.05f, 1f - (0.02f * tick / PerSecond));
            brain.Decide(Bitten(level: 30, health: own, theirs: MathF.Max(0.5f, 1f - (0.002f * tick / PerSecond))), tick);
        }

        Assert.Multiple(() =>
        {
            Assert.That(brain.FoughtBack, Is.EqualTo(1), "the cornered run did not turn into one fight");
            Assert.That(brain.Fled, Is.EqualTo(1), "cornered, the bot ran again when its bars turned");
        });
    }

    /// <summary>An engagement that ends with the last attacker's bar near empty, the bot standing, is a fight won.</summary>
    [Test]
    public void AFightEndingOnAnEmptyBar_IsWon()
    {
        const int PerSecond = 4;
        var brain = Fighting(t => (0.9f, MathF.Max(0.1f, 1f - (0.05f * t / PerSecond))), 20 * PerSecond);
        var clear = new Perceived { HasBody = true, Health = 0.9f };
        for (var tick = (20L * PerSecond) + 1; tick <= (20L * PerSecond) + (long)(BotBrain.ClearSeconds * PerSecond) + 2; tick++)
        {
            brain.Decide(in clear, tick);
        }

        Assert.Multiple(() =>
        {
            Assert.That(brain.Fled, Is.Zero, "the premise: the fight was not run from");
            Assert.That(brain.FightsWon, Is.EqualTo(1), "a fight that ended on an empty bar was not counted won");
        });
    }

    /// <summary>
    /// Fighting back names the attacker — not the nearest creature, and whatever its level: a CL-1 bot bitten by a level-40 viper, a harmless creature
    /// nearer, turns on the viper. The roll is found by seed: the case is what a fight-back does, not how often.
    /// </summary>
    [Test]
    public void FightingBack_NamesTheAttacker_PastItsLevel_NotTheNearest()
    {
        var world = Bitten(level: 40, attacker: 9u) with { NearestCreature = 5u, NearestCreatureX = 3f, NearestCreatureZ = 0f, NearestCreatureM = 3f, NearestCreatureLevel = 1 };
        for (var seed = 1u; seed <= 400u; seed++)
        {
            var brain = Brain(seed);
            var first = brain.Decide(in world, 1);
            if (brain.FoughtBack == 0)
            {
                continue;
            }

            var next = brain.Decide(in world, 2);
            Assert.Multiple(() =>
            {
                Assert.That(brain.Goal, Is.EqualTo(Goal.Hunting), "fighting back is not a hunt");
                Assert.That(first.NetId, Is.EqualTo(9u), "the bot fought the nearest creature, not the one biting it");
                Assert.That(next.NetId, Is.EqualTo(9u), "the hunt dropped an attacker past its level the tick after it turned on it");
                Assert.That(brain.Attacked, Is.EqualTo(1), "the same attacker was rolled for twice");
            });
            return;
        }

        Assert.Fail("no seed in 400 fought back a 5 % attacker: the roll is not reached");
    }

    /// <summary>A run that shakes the attacker off ends, and the bot decides again; one that does not, in <see cref="BotBrain.FleeSeconds"/>, turns and fights.</summary>
    [Test]
    public void ARunThatShakesItOff_Ends_OneThatDoesNot_EndsInAFight()
    {
        var world = Bitten(level: 30);
        BotBrain Fleeing()
        {
            for (var seed = 1u; seed <= 400u; seed++)
            {
                var b = Brain(seed);
                b.Decide(in world, 1);
                if (b.Fled == 1)
                {
                    return b;
                }
            }

            Assert.Fail("no seed ran from a level-30 attacker");
            return null;
        }

        var escaping = Fleeing();
        Assert.That(RunsFrom(escaping.Decide(in world, 2)), Is.True, "the run did not keep running while the attacker was still on it");
        var clear = new Perceived { HasBody = true };
        var gap = escaping.Decide(in clear, 3);
        Assert.That(escaping.Escaped, Is.Zero, "one tick with nothing in view ended the run");
        Assert.That(gap.SpeedClass, Is.EqualTo(Routing.SpeedClassOf(TravelMode.Run)), "the run stopped the moment its attacker left the view");
        var clearBy = 3 + (long)(BotBrain.ClearSeconds * 4) + 1;
        for (var tick = 4L; tick <= clearBy; tick++)
        {
            escaping.Decide(in clear, tick);
        }

        Assert.That(escaping.Escaped, Is.EqualTo(1), "a run with nothing on it for the clear time did not end as an escape");

        var cornered = Fleeing();
        var limit = (long)(BotBrain.FleeSeconds * 4) + 1;
        Intent last = default;
        for (var tick = 2L; tick <= limit + 1; tick++)
        {
            last = cornered.Decide(in world, tick);
        }

        Assert.Multiple(() =>
        {
            Assert.That(cornered.FoughtBack, Is.EqualTo(1), "a run that did not shake the attacker off never turned to fight");
            Assert.That(last.NetId, Is.EqualTo(42u), "cornered, the bot did not name its attacker");
            Assert.That(cornered.Attacked, Is.EqualTo(1), "the cornered fight was a second roll, not the run's end");
        });
    }

    /// <summary>
    /// A pack: the nearest biter changes from tick to tick, and the bot rolls once for the engagement — the first build rolled for each, flipped between
    /// fighting and running, and almost never got away (live, 2026-11-04: 33 135 rolls for 2 000 bots in five minutes, 640 escapes of 15 851 runs).
    /// </summary>
    [Test]
    public void APack_IsOneEngagement_OneRoll_WhicheverBiterIsNearest()
    {
        for (var seed = 1u; seed <= 50u; seed++)
        {
            var brain = Brain(seed);
            for (var tick = 1L; tick <= 40L; tick++)
            {
                var biter = tick % 2 == 0 ? 42u : 43u;
                brain.Decide(Bitten(level: 1, attacker: biter), tick);
            }

            Assert.That(brain.Attacked, Is.EqualTo(1), $"seed {seed}: a pack of two alternating as the nearest was rolled for {brain.Attacked} times");
            // One roll; at most the one turn a run makes when it gains nothing on the pack (cornered: CorneredSeconds), never a flip per biter.
            Assert.That(brain.Fled, Is.LessThanOrEqualTo(1), $"seed {seed}: the bot started more than one run inside one engagement");
            Assert.That(brain.FoughtBack, Is.LessThanOrEqualTo(1), $"seed {seed}: the bot turned to fight more than once inside one engagement");
        }
    }

    /// <summary>
    /// In a city, a creature near is a raider: a bot fights or flees before it bites — standing in a terminal's queue while the horde walks past was the
    /// complaint (Loïc, 2026-11-04). Out in the desert, a creature that is not on the bot leaves it to its hunt.
    /// </summary>
    [Test]
    public void InTown_ACreatureNear_IsFoughtOrFled_BeforeItBites()
    {
        var town = BotMap.Cities[0];
        Perceived Near(float x, float z) => new()
        {
            HasBody = true,
            X = x,
            Z = z,
            NearestCreature = 42u,
            NearestCreatureX = x + 8f,
            NearestCreatureZ = z + 6f,
            NearestCreatureM = 10f,
            NearestCreatureLevel = 40,
            NearestCreatureRaider = true,
        };

        var inTown = Brain(3);
        inTown.Decide(Near(town.X, town.Z), 1);
        var desert = Brain(3);
        desert.Decide(Near(town.X + town.Radius + 2000f, town.Z), 1);
        Assert.Multiple(() =>
        {
            Assert.That(inTown.Attacked, Is.EqualTo(1), "a level-40 creature 10 m away in town was not reacted to");
            Assert.That(inTown.FoughtBack + inTown.Fled, Is.EqualTo(1));
            Assert.That(desert.Attacked, Is.Zero, "a creature in the desert not on the bot was taken for an attacker");
        });
    }

    /// <summary>A bot already fighting the creature by its own choice — a hunt on the nearest, a mission — is not made to roll: it is in the fight.</summary>
    [Test]
    public void ABotAlreadyHuntingItsAttacker_DoesNotRoll()
    {
        var brain = Brain();
        brain.CombatLevel = 5;
        var quarry = Bitten(level: 3) with { Attacker = 0u };
        brain.Decide(in quarry, 1);
        Assert.That(brain.Goal, Is.EqualTo(Goal.Hunting), "the premise: a level-3 creature 10 m away is hunted");

        brain.Decide(Bitten(level: 3), 2);
        Assert.That(brain.Attacked, Is.Zero, "a bot hunting a creature rolled when that creature bit back");
    }
}
