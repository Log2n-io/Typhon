using System;
using NUnit.Framework;
using SwgTatooine.Bots.Brain;
using SwgTatooine.Navigation;

namespace SwgTatooine.Tests;

/// <summary>
/// A bot attacked fights back or runs (BotBrain.Defence.cs; Loïc, 2026-11-04: "I saw bots being attacked and not fight back or walk away, so just wait and
/// die"): one roll per attacker, weighted by the attacker's level against the bot's, a run always under <see cref="BotBrain.FleeHealth"/>, a run that does
/// not shake it off ends in a fight.
/// </summary>
[TestFixture]
internal sealed class BotDefenceChecks
{
    private static BotBrain Brain(uint seed = 7u) => new(BotMap.Cities, seed);

    /// <summary>A body in the open with a creature on it: <paramref name="level"/>, 8 m east and 6 m north (10 m away), the nearest creature too.</summary>
    private static Perceived Bitten(int level = 1, float health = 1f, uint attacker = 42u) => new()
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
        Health = health,
    };

    private static bool RunsFrom(Intent intent) =>
        intent.Kind == IntentKind.Attack && intent.NetId == 0u && intent.SpeedClass == Routing.SpeedClassOf(TravelMode.Run)
        && MathF.Abs(intent.Heading - MathF.Atan2(-6f, -8f)) < 0.001f;

    /// <summary>The chance falls with the attacker's level above the bot's: 90 % at or under it, 15 points a level above, 5 % at the floor.</summary>
    [Test]
    public void TheFightChance_FallsWithTheAttackersLead()
    {
        Assert.Multiple(() =>
        {
            Assert.That(BotBrain.FightChancePercent(-10), Is.EqualTo(90));
            Assert.That(BotBrain.FightChancePercent(0), Is.EqualTo(90));
            Assert.That(BotBrain.FightChancePercent(2), Is.EqualTo(60));
            Assert.That(BotBrain.FightChancePercent(4), Is.EqualTo(30));
            Assert.That(BotBrain.FightChancePercent(6), Is.EqualTo(5));
            Assert.That(BotBrain.FightChancePercent(40), Is.EqualTo(5));
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
    /// Over a fleet of seeds, a weak attacker is fought back most of the time and a strong one run from most of the time — the roll weighs the lead.
    /// </summary>
    [Test]
    public void AWeakAttacker_IsMostlyFought_AStrongOne_MostlyRunFrom()
    {
        int weakFought = 0, strongFought = 0;
        const int Seeds = 400;
        for (var seed = 1u; seed <= Seeds; seed++)
        {
            var weak = Brain(seed);
            weak.CombatLevel = 10;
            weak.Decide(Bitten(level: 8), 1);
            weakFought += (int)weak.FoughtBack;

            var strong = Brain(seed);
            strong.CombatLevel = 10;
            strong.Decide(Bitten(level: 30), 1);
            strongFought += (int)strong.FoughtBack;
        }

        Assert.Multiple(() =>
        {
            Assert.That(weakFought, Is.InRange(Seeds * 80 / 100, Seeds * 98 / 100), "an attacker two levels under was not fought back about 90 % of the time");
            Assert.That(strongFought, Is.InRange(0, Seeds * 12 / 100), "an attacker twenty levels over was fought back far more than 5 % of the time");
        });
    }

    /// <summary>Under <see cref="BotBrain.FleeHealth"/> it always runs, even from a creature far under its level; a health of 0 is "not sent", not dying.</summary>
    [Test]
    public void UnderTheFleeHealth_ItAlwaysRuns_AndNoHealthSentIsNotLow()
    {
        for (var seed = 1u; seed <= 50u; seed++)
        {
            var hurt = Brain(seed);
            hurt.CombatLevel = 40;
            Assert.That(RunsFrom(hurt.Decide(Bitten(level: 1, health: 0.2f), 1)), Is.True, $"seed {seed}: a bot at 20 % health did not run from a level-1 rat");
        }

        var fought = 0;
        for (var seed = 1u; seed <= 50u; seed++)
        {
            var unknown = Brain(seed);
            unknown.CombatLevel = 40;
            unknown.Decide(Bitten(level: 1, health: 0f), 1);
            fought += (int)unknown.FoughtBack;
        }

        Assert.That(fought, Is.GreaterThan(35), "a health the server never sent was read as dying: every bot ran");
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
        escaping.Decide(new Perceived { HasBody = true }, 3);
        Assert.That(escaping.Escaped, Is.EqualTo(1), "a run whose attacker was gone did not end as an escape");

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
