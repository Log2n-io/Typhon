using System;
using System.Collections.Concurrent;
using System.Linq;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;
using SwgTatooine.Server;
using SwgTatooine.Server.Accounts;
using SwgTatooine.Server.Terrain;
using Typhon.Engine;
using Typhon.Protocol;

namespace SwgTatooine.Tests;

/// <summary>
/// Character level and XP (character-level.md): the table from the balance model, kill and mission XP, the body's level and health, the damage factor, the kill
/// hook's events.
/// </summary>
/// <remarks>
/// The integration cases stage their fights the way <c>ClientIntentChecks</c> does: a lair spawned beside the avatar, its occupants taken away or one blow
/// from death, the avatar given a target and a stance. Levels are written onto the staged content, so a case states the levels it is about. Bodies are read
/// through the tick (<c>WriteInTick</c>); characters, which are Versioned and non-spatial, through quick transactions (<c>EW-02</c>).
/// </remarks>
[TestFixture]
[NonParallelizable]
internal sealed class ProgressionChecks : ServedChecks
{
    // ── The table and the formulas ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>CH-2: § 14.6's seven values, CL 50 at 1 691 050 XP, and 45 hours at the model's rates.</summary>
    [Test]
    public void TheXpTable_IsTheBalanceModels_AndCl50Takes45PlayHours()
    {
        var hours = 0d;
        for (var l = 1; l < Progression.MaxLevel; l++)
        {
            hours += Progression.XpToNext(l) / (1200d * l);
        }

        Assert.Multiple(() =>
        {
            Assert.That(Progression.XpToNext(1), Is.EqualTo(267));
            Assert.That(Progression.XpToNext(5), Is.EqualTo(2_030));
            Assert.That(Progression.XpToNext(10), Is.EqualTo(5_800));
            Assert.That(Progression.XpToNext(20), Is.EqualTo(18_561));
            Assert.That(Progression.XpToNext(30), Is.EqualTo(38_281));
            Assert.That(Progression.XpToNext(40), Is.EqualTo(64_962));
            Assert.That(Progression.XpToNext(49), Is.EqualTo(94_926));
            Assert.That(Progression.XpAt(5), Is.EqualTo(3_364));
            Assert.That(Progression.XpAt(10), Is.EqualTo(20_358));
            Assert.That(Progression.XpAt(20), Is.EqualTo(130_039));
            Assert.That(Progression.XpAt(30), Is.EqualTo(398_647));
            Assert.That(Progression.XpAt(40), Is.EqualTo(895_783));
            Assert.That(Progression.XpAt(49), Is.EqualTo(1_596_124));
            Assert.That(Progression.XpAt(50), Is.EqualTo(1_691_050));
            Assert.That(Progression.XpToNext(50), Is.Zero, "the cap has no next level");
            Assert.That(hours, Is.EqualTo(45d).Within(0.01d), "CL 50 is 45 play-hours at the model's rates");
        });

        // Exactly the closed form the design checked against balance_model.py, at every level.
        for (var l = 1; l < Progression.MaxLevel; l++)
        {
            Assert.That(Progression.XpToNext(l), Is.EqualTo(((10_800L * l * (20 + (3 * l))) + 465) / 931), $"level {l}");
        }
    }

    /// <summary>CH-3: on-level beast 10 L, foe 15 L, lair 40 L; ×1.2 five above, ×0.64 nine below, nothing ten below, nothing without a level; clamped.</summary>
    [Test]
    public void KillXp_FollowsTheLevelAndTheKind_AndAKillTenBelowGivesNone()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Progression.KillXp(KillVictimKind.Creature, 12, 12), Is.EqualTo(120));
            Assert.That(Progression.KillXp(KillVictimKind.Foe, 12, 12), Is.EqualTo(180));
            Assert.That(Progression.KillXp(KillVictimKind.Lair, 12, 12), Is.EqualTo(480));
            Assert.That(Progression.KillXp(KillVictimKind.Creature, 15, 10), Is.EqualTo(180), "five above: 10 × 15 × 1.2");
            Assert.That(Progression.KillXp(KillVictimKind.Creature, 11, 20), Is.EqualTo(70), "nine below: 10 × 11 × 0.64 = 70.4");
            Assert.That(Progression.KillXp(KillVictimKind.Creature, 10, 20), Is.Zero, "ten below is grey");
            Assert.That(Progression.KillXp(KillVictimKind.Creature, 1, 50), Is.Zero);
            Assert.That(Progression.KillXp(KillVictimKind.Creature, 0, 5), Is.Zero, "a victim without a level");
            Assert.That(Progression.KillXp(KillVictimKind.Creature, 5, 0), Is.Zero, "a killer without a character");
            Assert.That(Progression.KillXp(KillVictimKind.Creature, 30, 10), Is.EqualTo(450), "twenty above clamps at ×1.5");
            Assert.That(Progression.KillXp(KillVictimKind.Creature, 23, 10), Is.EqualTo(345), "thirteen above clamps at ×1.5");
            Assert.That(Progression.KillXp(KillVictimKind.Creature, 12, 12, ratePct: 200), Is.EqualTo(240), "--xp-rate scales it");
        });
    }

    /// <summary>CH-5's formula: 150 L on level, the factor, the ten-below cut.</summary>
    [Test]
    public void MissionXp_IsOneFiftyPerLevel_OnLevel()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Progression.MissionXp(3, 3), Is.EqualTo(450));
            Assert.That(Progression.MissionXp(5, 1), Is.EqualTo(870), "150 × 5 × 1.16");
            Assert.That(Progression.MissionXp(11, 20), Is.EqualTo(1_056), "150 × 11 × 0.64");
            Assert.That(Progression.MissionXp(10, 20), Is.Zero);
            Assert.That(Progression.MissionXp(0, 1), Is.Zero);
        });
    }

    /// <summary>The one <c>lvl_mod</c>: 4 % a level, clamped to 50-150 %.</summary>
    [Test]
    public void LevelPercent_IsFourPercentALevel_ClampedHalfToOneAndAHalf()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Progression.LevelPercent(0), Is.EqualTo(100));
            Assert.That(Progression.LevelPercent(5), Is.EqualTo(120));
            Assert.That(Progression.LevelPercent(-5), Is.EqualTo(80));
            Assert.That(Progression.LevelPercent(12), Is.EqualTo(148));
            Assert.That(Progression.LevelPercent(13), Is.EqualTo(150));
            Assert.That(Progression.LevelPercent(49), Is.EqualTo(150));
            Assert.That(Progression.LevelPercent(-12), Is.EqualTo(52));
            Assert.That(Progression.LevelPercent(-13), Is.EqualTo(50));
            Assert.That(Progression.LevelPercent(-49), Is.EqualTo(50));
        });
    }

    /// <summary>CH-1's health, the XP byte, CH-14's equipment gate.</summary>
    [Test]
    public void HealthFor_XpByte_AndCanEquip_AreTheirFormulas()
    {
        var halfway = new CharacterProgress { Level = 10, Xp = Progression.XpAt(10) + (Progression.XpToNext(10) / 2) };
        Assert.Multiple(() =>
        {
            Assert.That(Progression.HealthFor(1), Is.EqualTo(1_040));
            Assert.That(Progression.HealthFor(10), Is.EqualTo(1_400), "the crowd's health is CL 10's");
            Assert.That(Progression.HealthFor(50), Is.EqualTo(3_000));
            Assert.That(Progression.XpByte(in halfway), Is.EqualTo(127));
            Assert.That(Progression.XpByte(CharacterProgress.Start), Is.Zero);
            Assert.That(Progression.XpByte(new CharacterProgress { Level = 50, Xp = Progression.MaxXp }), Is.EqualTo(255), "full at the cap");
            Assert.That(Progression.CanEquip(1, 1), Is.True);
            Assert.That(Progression.CanEquip(10, 2), Is.False);
            Assert.That(Progression.CanEquip(11, 2), Is.True);
            Assert.That(Progression.CanEquip(40, 5), Is.False);
            Assert.That(Progression.CanEquip(41, 5), Is.True);
        });
    }

    /// <summary>CH-6's rule: several thresholds at once, the source totals, the clamp at the cap, never a level lost.</summary>
    [Test]
    public void AddXp_CrossesEveryThreshold_AndClampsAtTheCap()
    {
        var p = CharacterProgress.Start;
        var applied = Progression.AddXp(ref p, Progression.XpAt(3), mission: false);
        var q = new CharacterProgress { Level = 49, Xp = Progression.XpAt(49) };
        var toCap = Progression.AddXp(ref q, int.MaxValue / 2, mission: true);
        var past = Progression.AddXp(ref q, 500, mission: true);
        var retuned = new CharacterProgress { Level = 7, Xp = 10 };
        Progression.AddXp(ref retuned, 1, mission: false);
        Assert.Multiple(() =>
        {
            Assert.That(applied, Is.EqualTo(Progression.XpAt(3)), "the XP applied below the cap is the grant");
            Assert.That(p.Level, Is.EqualTo(3), "two thresholds crossed at once");
            Assert.That(toCap, Is.EqualTo(Progression.MaxXp - Progression.XpAt(49)), "at the cap only what fits is applied");
            Assert.That(past, Is.Zero, "nothing is applied past the cap");
            Assert.That(q.MissionXp, Is.EqualTo(toCap), "the source's total took more than was applied");
            Assert.That(p.KillXp, Is.EqualTo(Progression.XpAt(3)));
            Assert.That(p.MissionXp, Is.Zero);
            Assert.That(q.Level, Is.EqualTo(50));
            Assert.That(q.Xp, Is.EqualTo(Progression.MaxXp), "XP is clamped at the cap");
            Assert.That(retuned.Level, Is.EqualTo(7), "a level is never lost to a table that says less");
        });
    }

    /// <summary>CH-8: × clamp(1 + 0.04 × (attacker − target), 0.5, 1.5) both ways; none when a side has no level; at least 1.</summary>
    [Test]
    public void Damage_IsScaledByTheLevelDifference_BothWays_AndTheCrowdCountsAsItsLevel()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SimBridge.ScaleByLevel(100, 10, 15), Is.EqualTo(80), "CL 10 on L 15");
            Assert.That(SimBridge.ScaleByLevel(100, 15, 10), Is.EqualTo(120), "L 15 on CL 10");
            Assert.That(SimBridge.ScaleByLevel(100, 1, 21), Is.EqualTo(50), "twenty below clamps at ×0.5");
            Assert.That(SimBridge.ScaleByLevel(100, 21, 1), Is.EqualTo(150), "twenty above clamps at ×1.5");
            Assert.That(SimBridge.ScaleByLevel(95, 1, TatooineData.CrowdLevel), Is.EqualTo(61), "a CL 1 player hits the crowd for ×0.64");
            Assert.That(SimBridge.ScaleByLevel(95, TatooineData.CrowdLevel, 1), Is.EqualTo(129), "and takes ×1.36 from it");
            Assert.That(SimBridge.ScaleByLevel(95, 0, 30), Is.EqualTo(95), "an attacker without a level scales nothing");
            Assert.That(SimBridge.ScaleByLevel(95, 30, 0), Is.EqualTo(95), "nor does a target without one");
            Assert.That(SimBridge.ScaleByLevel(1, 1, 50), Is.EqualTo(1), "a blow that lands takes at least one");
        });
    }

    // ── A character's body ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>CH-1: a new character is CL 1, XP 0; its body carries level 1 and 1 040 health, max and current.</summary>
    [Test]
    [CancelAfter(90_000)]
    [Category("Integration")]
    public void ACharacter_StartsAtLevelOne_AndItsBodyCarriesItsLevelAndHealth()
    {
        using var sim = Start();
        var me = LogIn(sim, 0x8101);
        var progress = ProgressOf(sim, me.Character);
        var body = BodyOf(sim, me.Body);
        Assert.Multiple(() =>
        {
            Assert.That(progress.Level, Is.EqualTo(1));
            Assert.That(progress.Xp, Is.Zero);
            Assert.That(body.Look.Level, Is.EqualTo(1));
            Assert.That(body.Look.Xp, Is.Zero);
            Assert.That(body.Vitals.MaxHealth, Is.EqualTo(Progression.HealthFor(1)));
            Assert.That(body.Vitals.Health, Is.EqualTo(Progression.HealthFor(1)));
        });
    }

    /// <summary>CH-1: a scripted body has no character level and keeps the crowd's 1 400, which is CL 10's.</summary>
    [Test]
    [CancelAfter(90_000)]
    [Category("Integration")]
    public void TheCrowd_HasNoCharacterLevel_AndKeepsItsHealth()
    {
        using var sim = Start();
        Harness.Ticks(2);
        var scripted = 0;
        var levelled = 0;
        var otherHealth = 0;
        sim.WriteInTick(tx =>
        {
            foreach (var cluster in tx.For<Player>().GetClusterEnumerator())
            {
                var controls = cluster.GetReadOnlySpan(Player.Control);
                var looks = cluster.GetReadOnlySpan(Player.Look);
                var vitals = cluster.GetReadOnlySpan(Player.Vitals);
                for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
                {
                    var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    if (controls[slot].Kind != ControllerKind.Scripted)
                    {
                        continue;
                    }

                    scripted++;
                    levelled += looks[slot].Level != 0 ? 1 : 0;
                    otherHealth += vitals[slot].MaxHealth != Progression.HealthFor(TatooineData.CrowdLevel) ? 1 : 0;
                }
            }
        });

        Assert.Multiple(() =>
        {
            Assert.That(scripted, Is.GreaterThan(0), "the premise: the world has a crowd");
            Assert.That(levelled, Is.Zero, "a scripted body carries a character level");
            Assert.That(otherHealth, Is.Zero, "a scripted body's max health is not the crowd's 1 400");
        });
    }

    // ── The kill hook and kill XP ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// CH-4, CH-3: a beast, a camp foe and a wild lair killed by a person's body make one event each, every field what the drain saw; the character is paid
    /// each kill's XP, and its body's level and XP byte follow.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    [Category("Integration")]
    public void AKill_PushesOneKillEvent_IntoBothQueues_WithItsVictimAndKiller()
    {
        using var sim = Start();
        var me = LogIn(sim, 0x8111);
        var tap = new ConcurrentQueue<KillEvent>();
        sim.Bridge.KillTap = tap;

        var frogs = StageLair(sim, me, CreatureTemplates.Frog, level: 7, gapM: 10f, keepOccupants: true);
        var frog = Kill(sim, me, frogs, OccupantOf(sim, frogs, level: 7), tap);
        var ninjas = StageLair(sim, me, CreatureTemplates.Ninja, level: 7, gapM: 10f, keepOccupants: true, alongM: 12f);
        var ninja = Kill(sim, me, ninjas, OccupantOf(sim, ninjas, level: 7), tap);
        var wild = StageLair(sim, me, CreatureTemplates.Frog, level: 9, gapM: 10f, keepOccupants: false, alongM: -12f);
        SetLairHealth(sim, wild, 1);
        Kill(sim, me, wild, wild, tap);

        var events = tap.ToArray();
        var expected = Progression.KillXp(KillVictimKind.Creature, 7, 1) + Progression.KillXp(KillVictimKind.Foe, 7, 1)
            + Progression.KillXp(KillVictimKind.Lair, 9, 1);
        SessionHarness.Until(() => ProgressOf(sim, me.Character).Xp >= expected, "the three kills to be paid");
        Harness.Ticks(2);
        var progress = ProgressOf(sim, me.Character);
        var body = BodyOf(sim, me.Body);
        Assert.Multiple(() =>
        {
            Assert.That(events.Count(e => e.Victim == frog.Victim), Is.EqualTo(1), "a beast's death made other than one event");
            Assert.That(frog.Killer, Is.EqualTo(me.Body));
            Assert.That(frog.VictimKind, Is.EqualTo(KillVictimKind.Creature));
            Assert.That(frog.VictimLevel, Is.EqualTo(7));
            Assert.That(frog.Species, Is.EqualTo(CreatureTemplates.Frog));
            Assert.That(frog.Lair, Is.EqualTo(frogs));
            Assert.That(frog.Mission, Is.EqualTo(EntityId.Null));
            Assert.That(frog.Realm, Is.Zero);
            Assert.That(frog.Tick, Is.GreaterThan(0));
            Assert.That(MathF.Abs(frog.X - me.X) + MathF.Abs(frog.Z - me.Z), Is.LessThan(150f), "the event is not where the beast died");

            Assert.That(events.Count(e => e.Victim == ninja.Victim), Is.EqualTo(1), "a foe's death made other than one event");
            Assert.That(ninja.VictimKind, Is.EqualTo(KillVictimKind.Foe));
            Assert.That(ninja.Species, Is.EqualTo(CreatureTemplates.Ninja));
            Assert.That(ninja.Lair, Is.EqualTo(ninjas));

            var broken = events.Single(e => e.Victim == wild);
            Assert.That(broken.Killer, Is.EqualTo(me.Body));
            Assert.That(broken.VictimKind, Is.EqualTo(KillVictimKind.Lair));
            Assert.That(broken.VictimLevel, Is.EqualTo(9));
            Assert.That(broken.Species, Is.EqualTo(CreatureTemplates.Frog));
            Assert.That(broken.Lair, Is.EqualTo(wild));

            Assert.That(progress.Xp, Is.EqualTo(expected), "the character was not paid exactly its kills");
            Assert.That(progress.KillXp, Is.EqualTo(expected));
            Assert.That(progress.MissionXp, Is.Zero);
            Assert.That(progress.Level, Is.EqualTo(2), $"{expected} XP is CL 2");
            Assert.That(body.Look.Level, Is.EqualTo(progress.Level), "the body does not carry its character's level");
            Assert.That(body.Look.Xp, Is.EqualTo(Progression.XpByte(in progress)), "the body's XP byte is not its character's");
            Assert.That(sim.KillXpOverflow, Is.Zero);
            Assert.That(sim.KillLootOverflow, Is.Zero);
            Assert.That(sim.ProgressOverflow, Is.Zero);
            Assert.That(sim.Bridge.KillsGrey + sim.Bridge.KillsWorthless + sim.Bridge.KillsNoCharacter, Is.Zero, "a paid kill was counted as unpaid");
        });
    }

    /// <summary>CH-4: a wild lair broken is retired by the lair director that tick; its XP is granted from the event alone.</summary>
    [Test]
    [CancelAfter(120_000)]
    [Category("Integration")]
    public void AWildLairsKill_IsGrantedAfterLairLifeRetiredIt()
    {
        using var sim = Start();
        var me = LogIn(sim, 0x8121);
        var tap = new ConcurrentQueue<KillEvent>();
        sim.Bridge.KillTap = tap;
        var wild = StageLair(sim, me, CreatureTemplates.Frog, level: 4, gapM: 10f, keepOccupants: false);
        SetLairHealth(sim, wild, 1);
        Kill(sim, me, wild, wild, tap);
        SessionHarness.Until(() => ProgressOf(sim, me.Character).Xp > 0, "the lair's XP to be granted");

        // Retired on the lair director's next pass (2 Hz), once broken.
        SessionHarness.Until(() => LairHealth(sim, wild) == 0 && !Exists(sim, wild), "the lair director to retire the broken lair");
        Harness.Ticks(2);
        var standing = Exists(sim, wild);

        Assert.Multiple(() =>
        {
            Assert.That(standing, Is.False, "the premise: the lair director retired the broken lair");
            Assert.That(ProgressOf(sim, me.Character).Xp, Is.EqualTo(Progression.KillXp(KillVictimKind.Lair, 4, 1)));
        });
    }

    /// <summary>CH-6: a grant across two thresholds raises the body's level by two, its max health by 80 and its health by the same, on the tick.</summary>
    [Test]
    [CancelAfter(120_000)]
    [Category("Integration")]
    public void ALevelUp_RaisesTheBodysLevelAndMaxHealth_OnTheTick_AndHealsTheDifference()
    {
        using var sim = Start();
        var me = LogIn(sim, 0x8131);
        var tap = new ConcurrentQueue<KillEvent>();
        sim.Bridge.KillTap = tap;
        sim.WriteInTick(tx => tx.OpenMut(me.Character).Set(CharacterArch.Progress, new CharacterProgress { Level = 1, Xp = Progression.XpAt(3) - 10 }));
        var before = BodyOf(sim, me.Body);
        var wild = StageLair(sim, me, CreatureTemplates.Frog, level: 5, gapM: 10f, keepOccupants: false);
        SetLairHealth(sim, wild, 1);
        Kill(sim, me, wild, wild, tap);
        SessionHarness.Until(() => BodyOf(sim, me.Body).Look.Level == 3, "the body to reach CL 3");
        var after = BodyOf(sim, me.Body);
        Assert.Multiple(() =>
        {
            Assert.That(before.Look.Level, Is.EqualTo(1), "the premise: the body starts at CL 1");
            Assert.That(ProgressOf(sim, me.Character).Level, Is.EqualTo(3));
            Assert.That(after.Vitals.MaxHealth, Is.EqualTo(before.Vitals.MaxHealth + 80));
            Assert.That(after.Vitals.Health, Is.EqualTo(before.Vitals.Health + 80), "a level-up did not heal the max's increase");
            Assert.That(sim.Bridge.LevelUps, Is.EqualTo(2));
        });
    }

    /// <summary>CH-8: a CL 1 avatar's blows on a level 30 lair land halved — every blow 48 of its 95 — and the Attack carries what landed.</summary>
    [Test]
    [CancelAfter(120_000)]
    [Category("Integration")]
    public void TheStrike_CarriesTheScaledAmount()
    {
        using var sim = Start();
        var me = LogIn(sim, 0x8141);
        var lair = StageLair(sim, me, CreatureTemplates.Frog, level: 30, gapM: 10f, keepOccupants: false);
        var before = LairHealth(sim, lair);
        me.Link.SendCommand(nameof(SetTarget), ClientSays.SetTarget(SessionHarness.NetIdOf(me.Link.Session, lair)));
        me.Link.SendCommand(nameof(SetStance), ClientSays.SetStance(Stances.Pistol));
        SessionHarness.Until(() => LairHealth(sim, lair) < before, "a blow to land");
        Harness.Ticks(3 * Worlds.ServedHz);
        var taken = before - LairHealth(sim, lair);
        var blow = SimBridge.ScaleByLevel(95, 1, 30);
        Assert.Multiple(() =>
        {
            Assert.That(blow, Is.EqualTo(48));
            Assert.That(taken % blow, Is.Zero, $"{taken} taken is not a whole number of {blow}-point blows");
            Assert.That(taken % 95, Is.Not.Zero, "the blows landed unscaled");
        });
    }

    /// <summary>
    /// CH-7: the location save and a kill's XP grant on one tick both commit — two writers of one character, of two components, in two transactions.
    /// </summary>
    /// <remarks>The save cadence is one tick, so whatever tick pays the kill also saves the avatar's location.</remarks>
    [Test]
    [CancelAfter(120_000)]
    [Category("Integration")]
    public void ALocationSaveAndAnXpGrantOnOneTick_BothSurvive()
    {
        using var sim = Start(saveIntervalS: 1d / Worlds.ServedHz);
        var me = LogIn(sim, 0x8171);
        var tap = new ConcurrentQueue<KillEvent>();
        sim.Bridge.KillTap = tap;
        var wild = StageLair(sim, me, CreatureTemplates.Frog, level: 3, gapM: 10f, keepOccupants: false);
        SetLairHealth(sim, wild, 1);
        var savedBefore = SavedAt(sim, me.Character);
        Kill(sim, me, wild, wild, tap);
        SessionHarness.Until(() => ProgressOf(sim, me.Character).Xp > 0, "the kill to be paid");
        Harness.Ticks(3);
        Assert.Multiple(() =>
        {
            Assert.That(ProgressOf(sim, me.Character).Xp, Is.EqualTo(Progression.KillXp(KillVictimKind.Lair, 3, 1)), "the XP grant was lost");
            Assert.That(SavedAt(sim, me.Character), Is.GreaterThan(savedBefore), "the location saves stopped committing");
            Assert.That(sim.Bridge.ProgressCommitsFailed, Is.Zero, "an XP commit failed against the location save");
        });
    }

    private static long SavedAt(TatooineSim sim, EntityId character)
    {
        using var tx = sim.Dbe.CreateQuickTransaction();
        return tx.Open(character).Read(CharacterArch.Profile).LastSavedUtcTicks;
    }

    // ── Persistence and the endpoint ───────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// CH-7: progress survives a reopen after a crash — a kill's grant, its level-up included, then the plug pulled with no logout — and the next body
    /// carries it.
    /// </summary>
    /// <remarks>"Crash" as <c>AvatarLoginChecks.AKillWithNoLogoutCostsAtMostTheCadence</c> has it: no Close, no logout, the session dropped.</remarks>
    [Test]
    [CancelAfter(120_000)]
    [Category("Integration")]
    public void ProgressSurvivesAReopenAndACrash()
    {
        EntityId character;
        CharacterProgress saved;
        using (var first = Start(persist: true))
        {
            var me = LogIn(first, 0x8151);
            character = me.Character;
            var tap = new ConcurrentQueue<KillEvent>();
            first.Bridge.KillTap = tap;
            first.WriteInTick(tx => tx.OpenMut(character).Set(CharacterArch.Progress, new CharacterProgress { Level = 1, Xp = 200, KillXp = 200 }));
            var wild = StageLair(first, me, CreatureTemplates.Frog, level: 3, gapM: 10f, keepOccupants: false);
            SetLairHealth(first, wild, 1);
            Kill(first, me, wild, wild, tap);
            SessionHarness.Until(() => ProgressOf(first, character).Xp > 200, "the kill to be paid");
            Harness.Ticks(3);
            saved = ProgressOf(first, character);
            Assert.That(saved.Level, Is.EqualTo(2), "the premise: the kill took the character to CL 2");

            // The plug pulled: no Close, no logout.
            Harness.Release();
        }

        TatooineReplication.ResetSessionAccounting();
        TatooineReplication.ResetIntentAccounting();

        using var second = Start(persist: true);
        Assert.That(second.Reopened, Is.True, "the second run must reopen the first's database, or this proves nothing");
        var back = LogIn(second, 0x8151);
        var progress = ProgressOf(second, character);
        var body = BodyOf(second, back.Body);
        Assert.Multiple(() =>
        {
            Assert.That(back.Character, Is.EqualTo(character));
            Assert.That(progress.Level, Is.EqualTo(saved.Level));
            Assert.That(progress.Xp, Is.EqualTo(200 + Progression.KillXp(KillVictimKind.Lair, 3, 1)));
            Assert.That(progress.Xp, Is.EqualTo(saved.Xp));
            Assert.That(progress.KillXp, Is.EqualTo(saved.KillXp));
            Assert.That(body.Look.Level, Is.EqualTo(2), "the next body does not carry the level");
            Assert.That(body.Look.Xp, Is.EqualTo(Progression.XpByte(in progress)));
            Assert.That(body.Vitals.MaxHealth, Is.EqualTo(Progression.HealthFor(2)));
            Assert.That(body.Vitals.Health, Is.EqualTo(Progression.HealthFor(2)));
        });
    }

    /// <summary>
    /// CH-4: two shooters' lethal blows on one creature in one drain make one KillEvent, and one character is paid: the second blow finds a corpse.
    /// </summary>
    /// <remarks>Both avatars fight the frog, then on one tick it is put one blow from death and both weapons are re-armed, so both fire on the next.</remarks>
    [Test]
    [CancelAfter(120_000)]
    [Category("Integration")]
    public void TwoShootersOnOneCorpse_MakeOneKillEvent()
    {
        using var sim = Start();
        var a = LogIn(sim, 0x8181);
        var b = LogIn(sim, 0x8182);
        var tap = new ConcurrentQueue<KillEvent>();
        sim.Bridge.KillTap = tap;
        var frogs = StageLair(sim, a, CreatureTemplates.Frog, level: 5, gapM: 10f, keepOccupants: true);
        var frog = OccupantOf(sim, frogs, level: 5, health: 1_000_000);
        foreach (var me in new[] { a, b })
        {
            SessionHarness.Until(() => SessionHarness.NetIdOf(me.Link.Session, frog) != 0, "the frog to reach both views");
            me.Link.SendCommand(nameof(SetTarget), ClientSays.SetTarget(SessionHarness.NetIdOf(me.Link.Session, frog)));
            me.Link.SendCommand(nameof(SetStance), ClientSays.SetStance(Stances.Pistol));
        }

        SessionHarness.Until(() => TatooineReplication.Intents.TargetsSet >= 2, "both targets to be accepted");
        Harness.Ticks(Worlds.ServedHz);
        sim.WriteInTick(tx =>
        {
            var creature = tx.OpenMut(frog);
            var v = creature.Read(Creature.Vitals);
            v.Health = 1;
            creature.Set(Creature.Vitals, v);
            foreach (var body in new[] { a.Body, b.Body })
            {
                var player = tx.OpenMut(body);
                var pv = player.Read(Player.Vitals);
                pv.AttackCooldown = 0;
                player.Set(Player.Vitals, pv);
            }
        });
        SessionHarness.Until(() => tap.Any(e => e.Victim == frog), "the frog's death to be pushed");
        Harness.Ticks(Worlds.ServedHz);

        var paidA = ProgressOf(sim, a.Character).Xp;
        var paidB = ProgressOf(sim, b.Character).Xp;
        var death = tap.First(e => e.Victim == frog);
        Assert.Multiple(() =>
        {
            Assert.That(tap.Count(e => e.Victim == frog), Is.EqualTo(1), "one death made more than one KillEvent");
            Assert.That(death.Killer == a.Body || death.Killer == b.Body, Is.True, "the killer is neither shooter");
            Assert.That(paidA == 0 ^ paidB == 0, Is.True, $"paid A {paidA} and B {paidB}: exactly one shooter is the killer");
        });
    }

    /// <summary>CH-9: the bearer's level and XP as JSON; 401 without a token.</summary>
    [Test]
    [CancelAfter(90_000)]
    [Category("Integration")]
    public void TheCharacterEndpoint_AnswersLevelAndXp_ForTheBearer()
    {
        using var sim = Start();
        var me = LogIn(sim, 0x8161);
        var p = new CharacterProgress { Level = 12, Xp = Progression.XpAt(12) + 500, KillXp = 23_000, MissionXp = Progression.XpAt(12) + 500 - 23_000 };
        sim.WriteInTick(tx => tx.OpenMut(me.Character).Set(CharacterArch.Progress, p));
        var json = ProgressEndpoints.ProgressJson(sim.Dbe, me.Character);

        var anonymous = new DefaultHttpContext();
        var refused = ProgressEndpoints.GetCharacter(sim.Dbe, anonymous) as Microsoft.AspNetCore.Http.HttpResults.StatusCodeHttpResult;
        Assert.Multiple(() =>
        {
            Assert.That(json, Is.EqualTo(
                $"{{\"level\":12,\"xp\":{p.Xp},\"xpLevelStart\":{Progression.XpAt(12)},\"xpNextLevel\":{Progression.XpAt(13)},\"killXp\":23000,"
                + $"\"missionXp\":{p.MissionXp},\"maxHealth\":1480}}"));
            Assert.That(refused, Is.Not.Null);
            Assert.That(refused.StatusCode, Is.EqualTo(StatusCodes.Status401Unauthorized));
        });
    }

    // ── Staging ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private TatooineSim Start(bool persist = false, double saveIntervalS = TatooineReplication.DefaultSaveIntervalSeconds)
    {
        var config = Worlds.Served(Dir);
        config.WorkerCount = 1;
        config.Persist = persist;
        config.AvatarSaveIntervalS = saveIntervalS;

        // Pinned, so the lairs staged beside the avatar are in its view whatever the account id.
        config.SpawnSpread = 0;
        var sim = new TatooineSim(config);
        sim.Initialize();
        Harness = new SessionHarness(sim);
        return sim;
    }

    private readonly record struct Me(FakeLink Link, EntityId Body, EntityId Character, float X, float Z, float HalfExtent);

    private Me LogIn(TatooineSim sim, ulong account)
    {
        var bound = TatooineReplication.Intents.Possessions;
        var link = Harness.Connect(TatooineReplication.PlayerKind, EntityId.FromRawValue(account));
        SessionHarness.Until(() => TatooineReplication.Intents.Possessions > bound, "the avatar to be spawned");
        Harness.Ticks(2);
        var body = TatooineReplication.ControlledBy(sim.Dbe, link.Session);
        Assert.That(body, Is.Not.EqualTo(EntityId.Null), "the session controls no body");
        Assert.That(CharacterStore.TryFind(sim.Dbe, EntityId.FromRawValue(account), out var character, out _), Is.True, "the account has no character");
        PlayerPlacement place = default;
        sim.WriteInTick(tx => place = tx.Open(body).Read(Player.Bounds));
        return new Me(link, body, character, place.X, place.Z, place.HalfExtent);
    }

    private static CharacterProgress ProgressOf(TatooineSim sim, EntityId character)
    {
        using var tx = sim.Dbe.CreateQuickTransaction();
        return tx.Open(character).Read(CharacterArch.Progress);
    }

    private static (PlayerLook Look, PlayerVitals Vitals) BodyOf(TatooineSim sim, EntityId body)
    {
        PlayerLook look = default;
        PlayerVitals vitals = default;
        sim.WriteInTick(tx =>
        {
            var player = tx.Open(body);
            look = player.Read(Player.Look);
            vitals = player.Read(Player.Vitals);
        });
        return (look, vitals);
    }

    private static int LairHealth(TatooineSim sim, EntityId lair)
    {
        using var tx = sim.Dbe.CreateQuickTransaction();
        return tx.TryOpen(lair, out var e) ? e.Read(CreatureLair.Vitals).Health : 0;
    }

    private static bool Exists(TatooineSim sim, EntityId id)
    {
        using var tx = sim.Dbe.CreateQuickTransaction();
        return tx.TryOpen(id, out _);
    }

    private static void SetLairHealth(TatooineSim sim, EntityId lair, int health)
        => sim.WriteInTick(tx =>
        {
            var l = tx.OpenMut(lair);
            var v = l.Read(CreatureLair.Vitals);
            v.Health = health;
            l.Set(CreatureLair.Vitals, v);
        });

    /// <summary>
    /// A lair of <paramref name="template"/> at <paramref name="level"/>, <paramref name="gapM"/> beside the avatar's edge, in its view. Its occupants carry the
    /// level too; without <paramref name="keepOccupants"/> they are taken away and nothing guards it.
    /// </summary>
    private EntityId StageLair(TatooineSim sim, Me me, int template, int level, float gapM, bool keepOccupants, float alongM = 0f)
    {
        var config = sim.Config;
        var terrain = TerrainField.Shared(config.ContentScale);
        var lair = EntityId.Null;
        sim.WriteInTick((tx, _) =>
        {
            var rng = new Rng(7);
            lair = WorldBuilder.SpawnLair(tx, ref rng, template, level, me.X + me.HalfExtent + WorldBuilder.LairHalfM + gapM, me.Z + alongM, 1, expireTick: 0, config,
                terrain, 0);
        });
        SessionHarness.Until(() => !lair.IsNull, "the lair to be spawned");

        sim.WriteInTick((tx, _) =>
        {
            var open = tx.OpenMut(lair);
            var spawner = open.Read(CreatureLair.Spawner);
            spawner.Level = level;
            if (!keepOccupants)
            {
                spawner.Guards = 0;
                spawner.AliveCount = 0;
            }

            open.Set(CreatureLair.Spawner, spawner);
            foreach (var cluster in tx.For<Creature>().GetClusterEnumerator())
            {
                var homes = cluster.GetReadOnlySpan(Creature.Home);
                for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
                {
                    var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    if ((EntityId)homes[slot].Lair != lair)
                    {
                        continue;
                    }

                    var id = cluster.GetEntityId(slot);
                    if (!keepOccupants)
                    {
                        tx.Destroy(id);
                        continue;
                    }

                    var occupant = tx.OpenMut(id);
                    var brain = occupant.Read(Creature.Ai);
                    brain.Level = (byte)level;
                    occupant.Set(Creature.Ai, brain);
                }
            }
        });

        Harness.Ticks(2);
        SessionHarness.UntilNamed(me.Link.Session, lair, "the lair to reach the client's view");
        return lair;
    }

    /// <summary>One of a lair's occupants, at <paramref name="health"/>: by default one blow from death.</summary>
    private static EntityId OccupantOf(TatooineSim sim, EntityId lair, int level, int health = 1)
    {
        var found = EntityId.Null;
        var staged = 0;
        sim.WriteInTick(tx =>
        {
            foreach (var cluster in tx.For<Creature>().GetClusterEnumerator())
            {
                var homes = cluster.GetReadOnlySpan(Creature.Home);
                for (var bits = cluster.OccupancyBits; bits != 0 && found.IsNull; bits &= bits - 1)
                {
                    var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    if ((EntityId)homes[slot].Lair == lair)
                    {
                        found = cluster.GetEntityId(slot);
                    }
                }
            }

            if (found.IsNull)
            {
                return;
            }

            var body = tx.OpenMut(found);
            var vitals = body.Read(Creature.Vitals);
            vitals.Health = health;
            body.Set(Creature.Vitals, vitals);
            staged = body.Read(Creature.Ai).Level;
        });
        Assert.That(found.IsNull, Is.False, "the lair has no occupant");
        Assert.That(staged, Is.EqualTo(level), "the occupant was not staged at its lair's level");
        return found;
    }

    /// <summary>The avatar fights <paramref name="victim"/> until the kill hook names it; returns its event.</summary>
    private KillEvent Kill(TatooineSim sim, Me me, EntityId lair, EntityId victim, ConcurrentQueue<KillEvent> tap)
    {
        SessionHarness.Until(() => SessionHarness.NetIdOf(me.Link.Session, victim) != 0, "the victim to reach the client's view");
        me.Link.SendCommand(nameof(SetTarget), ClientSays.SetTarget(SessionHarness.NetIdOf(me.Link.Session, victim)));
        me.Link.SendCommand(nameof(SetStance), ClientSays.SetStance(Stances.Pistol));
        SessionHarness.Until(() => tap.Any(e => e.Victim == victim), $"the kill of {victim} (lair {lair}) to be pushed");
        Harness.Ticks(2);
        return tap.First(e => e.Victim == victim);
    }
}
