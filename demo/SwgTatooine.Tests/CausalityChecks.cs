using System;
using System.Collections.Generic;
using NUnit.Framework;
using Typhon.Engine;

namespace SwgTatooine.Tests;

/// <summary>
/// Shared reads for the SWG-02 fixtures: the state a causality claim has to be checked against, not the state a report prints.
/// </summary>
internal static class Causality
{
    /// <summary>Every creature's identity, position, mode, target and health, through the realm's spatial index and then the entity.</summary>
    public static List<(EntityId Id, float X, float Z, int Mode, EntityId Target, int Health)> Creatures(TatooineSim sim)
    {
        var rows = new List<(EntityId, float, float, int, EntityId, int)>();
        using var tx = sim.Dbe.CreateQuickTransaction();
        Span<ClusterSpatialQueryResult> buffer = new ClusterSpatialQueryResult[64];
        var e = sim.Dbe.ClusterSpatialQuery<Creature>(RealmId.Default).AABB(in Worlds.Everywhere);
        try
        {
            int got;
            while ((got = e.Fill(buffer)) > 0)
            {
                for (var i = 0; i < got; i++)
                {
                    var entity = tx.Open(buffer[i].Entity);
                    var p = entity.Read(Creature.Bounds);
                    var ai = entity.Read(Creature.Ai);
                    rows.Add((buffer[i].Entity, p.X, p.Z, ai.Mode, ai.Target, entity.Read(Creature.Vitals).Health));
                }
            }
        }
        finally
        {
            e.Dispose();
        }

        return rows;
    }

    /// <summary>The credits every player is carrying, summed — the world's whole <see cref="Inventory"/> balance.</summary>
    public static long TotalCredits(TatooineSim sim)
    {
        using var tx = sim.Dbe.CreateQuickTransaction();
        var total = 0L;
        foreach (var id in sim.Index.Players)
        {
            total += tx.Open(id).Read(Player.Inventory).Credits;
        }

        return total;
    }

    /// <summary>Every live destroy mission's lair and the player it was built for.</summary>
    public static List<(EntityId Lair, EntityId Owner, int Health)> LiveMissions(TatooineSim sim)
    {
        var rows = new List<(EntityId, EntityId, int)>();
        using var tx = sim.Dbe.CreateQuickTransaction();
        foreach (var id in sim.Index.Lairs)
        {
            var entity = tx.Open(id);
            var lair = entity.Read(CreatureLair.Spawner);
            if (lair.MissionId != 0)
            {
                rows.Add((id, lair.Owner, entity.Read(CreatureLair.Vitals).Health));
            }
        }

        return rows;
    }

    /// <summary>A world long enough for the loop to close: the walk to a mission is one to two kilometres at 12 m/s, which is minutes of simulated time.</summary>
    /// <remarks>
    /// <b>Unpaced, and that is not a shortcut.</b> Every interval in the simulation is expressed in seconds and converted at the configured rate (S0-3), so an
    /// unpaced run is the same world sampled as fast as the box will go. Paced, these fixtures would take six minutes of wall-clock each to reach the first
    /// mission completion; unpaced they take about three seconds.
    /// </remarks>
    public static SimConfig LongEnough(string dir, int ticks)
    {
        var config = Worlds.Small(dir);
        config.WarmTicks = 0;
        config.MeasuredTicks = ticks;
        config.Unpaced = true;
        return config;
    }
}

/// <summary>
/// AC-1, AC-2 and AC-4's aggregate half: the causality loop actually closes, and every effect it produced is accounted for.
/// </summary>
/// <remarks>
/// One world, run once, asserted many times — the run is the expensive part, and the criteria are claims about the same window rather than about different
/// worlds. What each test must not do is re-derive its expectation from the same counter it is checking, which is why the durability assertion is an identity
/// between two independently produced numbers (the WAL's own watermark, and the count of things that wrote to it).
/// </remarks>
[TestFixture]
[NonParallelizable]
public sealed class CausalityLoopTests
{
    private string _dir;
    private TatooineSim _sim;
    private RunResult _result;
    private long _creditsBefore;

    [OneTimeSetUp]
    public void RunWorld()
    {
        _dir = Worlds.NewDirectory();
        _sim = new TatooineSim(Causality.LongEnough(_dir, 4000));
        _sim.Initialize();
        _creditsBefore = Causality.TotalCredits(_sim);
        _result = _sim.Run();
    }

    [OneTimeTearDown]
    public void Dispose()
    {
        _sim?.Dispose();
        Worlds.Delete(_dir);
    }

    [Test]
    public void AC1_ALairDiesFromPlayerFire_AndTheMissionCompletes()
    {
        var s = _sim.LastStats;

        // The chain, each link asserted separately so a break says which link broke: a lair is built for a player, the player is told where it is, the player's
        // fire lands on it, and MissionTick's completion branch — unreachable before SWG-02, because nothing wrote LairVitals.Health downward — fires.
        Assert.Multiple(() =>
        {
            Assert.That(s.MissionsIssued, Is.GreaterThan(0), "no mission was issued: the pool or the seek query is broken, not the combat loop");
            Assert.That(s.MissionsAssigned, Is.EqualTo(s.MissionsIssued), "every mission built for a simulated player must reach it");
            Assert.That(s.LairHits, Is.GreaterThan(0), "no player fire reached a lair");
            Assert.That(s.MissionsCompleted, Is.GreaterThan(0), "lairs were hit but no mission completed");
        });
    }

    [Test]
    public void AC2_EveryTickThatWroteInventory_AdvancedTheDurabilityWatermark()
    {
        var s = _sim.LastStats;
        var writes = s.CreaturesKilled + s.MissionRewards;

        // The identity is the assertion. Each kill pays loot and each completion pays a reward, and both go through the tick's own transaction — so the number
        // of ticks on which the WAL's durable LSN moved must equal the number of ticks that wrote Inventory. The two numbers are produced by different things:
        // one by the simulation's counters, the other by the WAL writer thread.
        //
        // It is also the proof that ClusterDurability.Checkpoint does what it declares. Four thousand ticks of creatures, players and NPCs moving produce no WAL
        // record at all; only the Versioned half of Player does.
        Assert.Multiple(() =>
        {
            Assert.That(writes, Is.GreaterThan(0), "precondition: something was looted or rewarded");
            Assert.That(_sim.WalAdvances, Is.EqualTo(writes), "the durable LSN moved on a different number of ticks than wrote Inventory");
            Assert.That(_sim.WalLsnGained, Is.EqualTo(_sim.WalAdvances), "one WAL record per writing tick: more would mean an unexpected second writer");
            Assert.That(Causality.TotalCredits(_sim), Is.GreaterThan(_creditsBefore), "credits did not grow, so nothing was actually written");
        });
    }

    [Test]
    public void AC2_TheDurabilityWaitIsReportedBesideCompute_AndIsNotTheWholeStory()
    {
        // Not a threshold on the wait — that is AC-6's, and at this population it would be measuring the box. What is asserted is that the split EXISTS and is
        // non-negative, because the figure it replaced was a hard zero for every tick of every run and nothing would have noticed if it still were.
        Assert.Multiple(() =>
        {
            Assert.That(_result.DurabilityMedianMs, Is.GreaterThanOrEqualTo(0f));
            Assert.That(_result.DurabilityP99Ms, Is.GreaterThanOrEqualTo(_result.DurabilityMedianMs));
            Assert.That(_result.DurabilityMaxMs, Is.GreaterThanOrEqualTo(_result.DurabilityP99Ms));
            Assert.That(_result.DurabilityMedianMs, Is.LessThan(_result.TickMedianMs), "the wait cannot exceed the tick that contains it");
        });
    }

    [Test]
    public void AC4_BothRefusalsAreCounted_AndNotOneEffectWasLost()
    {
        var s = _sim.LastStats;
        Assert.Multiple(() =>
        {
            // A player that is not fighting does no damage. Counted rather than assumed: with the activity mix at 22 % combat, the overwhelming majority of
            // player-ticks must land here, and a zero would mean the activity gate was removed.
            Assert.That(s.ShotsSkippedNotFighting, Is.GreaterThan(s.PlayerShots), "most player-ticks are not fighting; a zero means the gate is gone");
            Assert.That(s.PlayerShots, Is.GreaterThan(0), "nobody fired at all");

            // Every shot pushed an event, every event was applied to something that was still there.
            Assert.That(s.DamageApplied, Is.EqualTo(s.PlayerShots + s.CreatureAttacks), "an effect was produced and not applied");
            Assert.That(s.EventsStale, Is.Zero, "an event named a target that had gone: something now destroys what a shot can name");
            Assert.That(_sim.CombatQueueOverflow, Is.Zero, "the queue dropped an effect: raise its per-tick budget");
        });
    }

    [Test]
    public void AC4_TheQueueIsNowhereNearItsBudget()
    {
        // The 1 024 figure is an order of magnitude above the worst case it was sized for, and this is the check that says so rather than assuming it. If a
        // future population change brings the peak near the budget, this fails before an overflow silently drops an effect.
        Assert.That(_sim.CombatQueuePeak, Is.LessThan(256), $"the combat queue peaked at {_sim.CombatQueuePeak} against a 1 024 budget");
    }

    [Test]
    public void AC3_NoCreatureIsLeftFightingNothing()
    {
        var s = _sim.LastStats;
        var rows = Causality.Creatures(_sim);
        var orphans = 0;
        var engaged = 0;
        foreach (var row in rows)
        {
            if (row.Mode is not (AiMode.Pursue or AiMode.Fighting))
            {
                continue;
            }

            engaged++;
            if (row.Target.IsNull)
            {
                orphans++;
            }
        }

        Assert.Multiple(() =>
        {
            // The defect this guards is gap G6's end state: Pursue was sticky and targetless, so pursuers accumulated for the life of the process. Every
            // creature in a combat mode must now name somebody.
            Assert.That(orphans, Is.Zero, $"{orphans} of {engaged} engaged creatures are pursuing nobody");

            // And the leash fires, which is the half of AC-3 that a snapshot of the end state cannot show.
            Assert.That(s.ChaseGivenUp, Is.GreaterThan(0), "no chase was ever abandoned past MaxChaseRangeM: the leash is not being reached");
        });
    }
}

/// <summary>
/// AC-4's falsifiable half: drive-by combat is gone. A player standing next to a creature and not fighting it does nothing to it.
/// </summary>
/// <remarks>
/// <b>This is the test the old model could not pass, and the only one that states gap G8 directly.</b> Before SWG-02 combat ran from the creature's side over a
/// 75 m radius with no reference to the player's activity or target, so "standing near" and "attacking" were the same thing. The world is built, one player is
/// placed beside one creature in an activity that is not Combat, and the creature's health is read afterwards.
/// </remarks>
[TestFixture]
[NonParallelizable]
public sealed class DriveByCombatIsGoneTests
{
    private string _dir;
    private TatooineSim _sim;
    private EntityId _creature;
    private int _healthBefore;

    [OneTimeSetUp]
    public void RunWorld()
    {
        _dir = Worlds.NewDirectory();
        var config = Causality.LongEnough(_dir, 40);
        config.WorkerCount = 1;
        _sim = new TatooineSim(config);
        _sim.Initialize();

        var rows = Causality.Creatures(_sim);
        Assert.That(rows, Is.Not.Empty, "precondition: the world has creatures");

        // A PASSIVE creature, so its own aggro query cannot start a fight and turn this into a test of something else. Banthas and rontos never attack on sight.
        var chosen = default((EntityId Id, float X, float Z, int Mode, EntityId Target, int Health));
        using (var tx = _sim.Dbe.CreateQuickTransaction())
        {
            foreach (var row in rows)
            {
                if (tx.Open(row.Id).Read(Creature.Ai).AggroRadius <= 0f && row.Mode != AiMode.Dead)
                {
                    chosen = row;
                    break;
                }
            }
        }

        Assert.That(chosen.Id.IsNull, Is.False, "precondition: the world has a living passive creature");
        _creature = chosen.Id;
        _healthBefore = chosen.Health;

        using (var tx = _sim.Dbe.CreateQuickTransaction())
        {
            var id = _sim.Index.Players[0];
            var player = tx.OpenMut(id);

            // Roaming, parked, and with a timer long enough that the activity mix never re-rolls it into Combat during the window.
            ref var state = ref player.Write(Player.State);
            state.Activity = PlayerActivity.Roaming;
            state.ActivityTicks = int.MaxValue / 2;
            ref var move = ref player.Write(Player.Move);
            move.VelX = 0f;
            move.VelZ = 0f;

            // Standing on the creature's toes: two metres, well inside both the 75 m weapon and the 6 m melee reach.
            var at = default(PlayerPlacement);
            at.SetAt(chosen.X + 2f, chosen.Z, 0f, player.Read(Player.Bounds).HalfExtent);
            tx.Teleport(id, Player.Bounds, RealmId.Default, in at);
            tx.Commit();
        }

        _sim.Run();
    }

    [OneTimeTearDown]
    public void Dispose()
    {
        _sim?.Dispose();
        Worlds.Delete(_dir);
    }

    [Test]
    public void APlayerNotInCombat_DoesNoDamageToTheCreatureItIsStandingOn()
    {
        using var tx = _sim.Dbe.CreateQuickTransaction();
        var after = tx.Open(_creature).Read(Creature.Vitals).Health;
        Assert.That(after, Is.EqualTo(_healthBefore),
            "a roaming player damaged a creature it was standing next to: the activity gate is gone and combat is drive-by again");
    }
}

/// <summary>
/// AC-5: a player reduced to zero health is incapacitated and cloned at the nearest city — one teleport, full health.
/// </summary>
/// <remarks>
/// <b>Driven through the real systems, not by calling the resolver.</b> The world is engineered so that the exchange happens on the first tick — a creature in
/// <see cref="AiMode.Fighting"/> whose target is a player standing next to it, with the weapon off cooldown and the player on its last few hit points — and then
/// the tick runs. The producer pushes the damage, the consumer applies it, and the clone is the consumer's. Nothing here reaches past a public entry point.
/// <para>
/// It cannot be observed in a long run instead: at 1 400 hit points against creature damage of 12 to 45 a hit, a player survives everything a 4 000-tick window
/// throws at it. That is a property of the world's numbers rather than of the code, which is exactly why this is its own fixture.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
public sealed class PlayerCloneTests
{
    private string _dir;
    private TatooineSim _sim;
    private EntityId _player;
    private (float X, float Z) _diedAt;
    private int _maxHealth;

    [OneTimeSetUp]
    public void RunWorld()
    {
        _dir = Worlds.NewDirectory();
        var config = Causality.LongEnough(_dir, 10);
        config.WorkerCount = 1;
        _sim = new TatooineSim(config);
        _sim.Initialize();

        var attacker = EntityId.Null;
        foreach (var row in Causality.Creatures(_sim))
        {
            // INSIDE the world by a margin, and this is not a formality — it is what the fixture was flaky on for four runs in thirty. A spawn region may
            // place a creature past the planet's f32 extent (the Southern Wastes disc reaches z = -9 100 against a half-extent of 8 192), and teleporting the
            // player onto such a creature has the position clamped back inside by about a hundred metres — putting the player out of melee reach before the
            // first tick and leaving the creature swinging at nothing. Filed separately; here it is simply avoided.
            var margin = (_sim.Config.WorldEdgeM * 0.5f) - 500f;
            if (row.Mode != AiMode.Dead && row.Health > 0 && MathF.Abs(row.X) < margin && MathF.Abs(row.Z) < margin)
            {
                attacker = row.Id;
                break;
            }
        }

        Assert.That(attacker.IsNull, Is.False, "precondition: the world has a living creature inside its own bounds");
        _player = _sim.Index.Players[0];

        using (var tx = _sim.Dbe.CreateQuickTransaction())
        {
            var player = tx.OpenMut(_player);
            ref var vitals = ref player.Write(Player.Vitals);
            _maxHealth = vitals.MaxHealth;

            // Five hit points: less than the weakest creature's 12, so one hit is fatal whichever creature was picked.
            vitals.Health = 5;
            ref var state = ref player.Write(Player.State);
            state.Activity = PlayerActivity.Roaming;
            state.ActivityTicks = int.MaxValue / 2;
            ref var move = ref player.Write(Player.Move);
            move.VelX = 0f;
            move.VelZ = 0f;

            var creature = tx.OpenMut(attacker);

            var at = creature.Read(Creature.Bounds);
            _diedAt = (at.X + 2f, at.Z);

            // The creature is already in the fight: Fighting, naming this player, weapon ready. ThinkCooldown is parked high so CreatureThink does not re-decide
            // inside the window and leash it home instead — the test is about the exchange, not about how a creature gets into one.
            ref var ai = ref creature.Write(Creature.Ai);
            ai.Mode = AiMode.Fighting;
            ai.Target = _player;
            ref var timers = ref creature.Write(Creature.Timers);
            timers.AttackCooldown = 0;
            timers.ThinkCooldown = int.MaxValue / 2;

            // A fighting creature stands still; without this it keeps the velocity its last wander leg left and the move system walks it away from the fight.
            ref var cmove = ref creature.Write(Creature.Move);
            cmove.VelX = 0f;
            cmove.VelZ = 0f;

            // Destination = where it is about to stand, so PlayerThink's arrival test succeeds and it never steers anywhere: a stale roam destination would
            // have it walking out of the creature's reach while the fight was still being set up.
            move.DestX = _diedAt.X;
            move.DestZ = _diedAt.Z;

            var here = default(PlayerPlacement);
            here.SetAt(_diedAt.X, _diedAt.Z, 0f, player.Read(Player.Bounds).HalfExtent);
            tx.Teleport(_player, Player.Bounds, RealmId.Default, in here);
            tx.Commit();
        }

        _sim.Run();
    }

    [OneTimeTearDown]
    public void Dispose()
    {
        _sim?.Dispose();
        Worlds.Delete(_dir);
    }

    [Test]
    public void APlayerAtZeroHealth_IsClonedAtTheNearestCity_WithFullHealth()
    {
        // Read out first: EntityRef is a ref struct and cannot be captured by Assert.Multiple's lambda (CS8175).
        float health, distance;
        EntityId target;
        int activity;
        using (var tx = _sim.Dbe.CreateQuickTransaction())
        {
            var player = tx.Open(_player);
            var at = player.Read(Player.Bounds);
            var nearest = NearestCity(_diedAt.X, _diedAt.Z);
            var dx = at.X - nearest.X;
            var dz = at.Z - nearest.Z;
            distance = MathF.Sqrt((dx * dx) + (dz * dz));
            health = player.Read(Player.Vitals).Health;
            target = player.Read(Player.Session).Target;
            activity = player.Read(Player.State).Activity;
        }

        Assert.Multiple(() =>
        {
            Assert.That(_sim.LastStats.PlayersIncapacitated, Is.EqualTo(1), "the player was not brought down at all");
            Assert.That(health, Is.EqualTo(_maxHealth), "a cloned player wakes with full health: no wounds, no insurance");
            Assert.That(distance, Is.LessThan(1f), "the clone is not at the city nearest where the player went down");
            Assert.That(target, Is.EqualTo(EntityId.Null), "a cloned player keeps a target on the far side of the planet");
            Assert.That(activity, Is.EqualTo(PlayerActivity.Idle), "a cloned player wakes up recovering, not mid-activity");
        });
    }

    /// <summary>The city the resolver should have chosen — computed here from the map rather than read back from the code under test.</summary>
    private (float X, float Z) NearestCity(float x, float z)
    {
        var bestSq = float.MaxValue;
        var best = (X: x, Z: z);
        foreach (var c in _sim.Index.Cities)
        {
            var dx = c.X - x;
            var dz = c.Z - z;
            var d2 = (dx * dx) + (dz * dz);
            if (d2 < bestSq)
            {
                bestSq = d2;
                best = (c.X, c.Z);
            }
        }

        return best;
    }
}
