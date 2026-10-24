using System;
using NUnit.Framework;
using SwgTatooine.Server;
using Typhon.Engine;

namespace SwgTatooine.Tests;

/// <summary>
/// AC-5, as Loïc rewrote it (2026-10-24): a player at no health goes DOWN where it stands — knocked out or dead, even odds — and is nobody's target until it
/// is up again: a knocked-out one gets up where it lies with a fifth of its health, a dead one wakes whole as a clone outside the nearest city's shuttleport.
/// </summary>
/// <remarks>
/// <b>Driven through the real systems, not by calling the resolver.</b> The world is engineered so that the exchange happens on the first tick — a creature in
/// <see cref="AiMode.Fighting"/> whose target is a player standing next to it, with the weapon off cooldown and the player on its last few hit points — and then
/// the tick runs. It cannot be observed in a long run instead: at 1 400 hit points against creature damage of 12 to 45 a hit, a player survives everything a
/// 4 000-tick window throws at it.
/// </remarks>
[Category("Integration")]
[TestFixture]
[NonParallelizable]
public sealed class PlayerDownTests
{
    private string _dir;
    private TatooineSim _sim;
    private EntityId _player;
    private (float X, float Z) _diedAt;

    [OneTimeSetUp]
    public void RunWorld()
    {
        _dir = Worlds.NewDirectory();
        var config = Causality.LongEnough(_dir, 10);
        config.WorkerCount = 1;
        _sim = new TatooineSim(config);
        _sim.Initialize();

        var attacker = DownStaging.LivingCreatureInside(_sim);
        _player = _sim.Index.Players[0];
        using (var tx = _sim.Dbe.CreateQuickTransaction())
        {
            var player = tx.OpenMut(_player);

            // Five hit points: less than the weakest creature's 12, so one hit is fatal whichever creature was picked.
            var vitals = player.Read(Player.Vitals);
            vitals.Health = 5;
            player.Set(Player.Vitals, vitals);
            var state = player.Read(Player.State);
            state.Activity = PlayerActivity.Roaming;
            state.ActivityTicks = int.MaxValue / 2;
            player.Set(Player.State, state);

            var at = tx.Open(attacker).Read(Creature.Bounds);
            _diedAt = (at.X + 2f, at.Z);
            DownStaging.Stage(tx, _player, _diedAt.X, _diedAt.Z);
            DownStaging.Engage(tx, attacker, _player);
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
    public void APlayerAtZeroHealth_GoesDownWhereItStands_AndIsUpAgainOnlyLater()
    {
        // Read out first: EntityRef is a ref struct and cannot be captured by Assert.Multiple's lambda (CS8175).
        PlayerVitals vitals;
        PlayerPlacement at;
        PlayerMotion move;
        EntityId target;
        using (var tx = _sim.Dbe.CreateQuickTransaction())
        {
            var player = tx.Open(_player);
            vitals = player.Read(Player.Vitals);
            at = player.Read(Player.Bounds);
            move = player.Read(Player.Move);
            target = player.Read(Player.Session).Target;
        }

        // Ten ticks at 10 Hz: down well before either way back up is due.
        var lies = vitals.Down == Downed.Dead ? TatooineData.CloneDelaySec : TatooineData.KnockOutSec;
        Assert.Multiple(() =>
        {
            Assert.That(_sim.LastStats.PlayersIncapacitated, Is.EqualTo(1), "the player was not brought down at all");
            Assert.That(vitals.Down, Is.AnyOf(Downed.KnockedOut, Downed.Dead), "a player at no health is not down");
            Assert.That(vitals.Health, Is.Zero, "a downed player was healed or cloned at once");
            Assert.That(MathF.Abs(at.X - _diedAt.X) + MathF.Abs(at.Z - _diedAt.Z), Is.LessThan(0.5f), "a downed player is not lying where it fell");
            Assert.That(move.VelX == 0f && move.VelZ == 0f, Is.True, "a downed player is still moving");
            Assert.That(target, Is.EqualTo(EntityId.Null), "a downed player kept its target");
            Assert.That(vitals.UpTick, Is.GreaterThan(lies * 10 - 10).And.LessThanOrEqualTo(lies * 10 + 1), "it gets up at the wrong time for how it went down");
        });
    }
}

/// <summary>How a downed player gets up, and what leaves it alone while it lies there. One small world per case, the state staged before the run.</summary>
[Category("Integration")]
[TestFixture]
[NonParallelizable]
public sealed class DownedRecoveryChecks
{
    private string _dir;
    private TatooineSim _sim;

    [TearDown]
    public void Dispose()
    {
        _sim?.Dispose();
        _sim = null;
        Worlds.Delete(_dir);
    }

    private TatooineSim World(int ticks)
    {
        _dir = Worlds.NewDirectory();
        var config = Causality.LongEnough(_dir, ticks);
        config.WorkerCount = 1;
        _sim = new TatooineSim(config);
        _sim.Initialize();
        return _sim;
    }

    [Test]
    public void AKnockedOutPlayer_GetsUpWhereItLies_WithAFifthOfItsHealth()
    {
        var sim = World(10);
        var id = sim.Index.Players[0];
        var spot = DownStaging.QuietSpot(sim);
        int max;
        using (var tx = sim.Dbe.CreateQuickTransaction())
        {
            DownStaging.Stage(tx, id, spot.X, spot.Z);
            max = DownStaging.KnockDown(tx, id, Downed.KnockedOut, upTick: 3);
            tx.Commit();
        }

        sim.Run();
        using var read = sim.Dbe.CreateQuickTransaction();
        var player = read.Open(id);
        var vitals = player.Read(Player.Vitals);
        var at = player.Read(Player.Bounds);
        Assert.Multiple(() =>
        {
            Assert.That(vitals.Down, Is.EqualTo(Downed.Up), "the knocked-out player never got up");
            // A fifth, and then healing out of a fight like anyone: up to one heal in the second it has been standing.
            var fifth = (int)(max * TatooineData.KnockOutRecoveryFraction);
            Assert.That(vitals.Health, Is.InRange(fifth, fifth + (int)(max * TatooineData.RegenPerSecFraction)), "it got up with the wrong health");
            Assert.That(MathF.Abs(at.X - spot.X) + MathF.Abs(at.Z - spot.Z), Is.LessThan(30f), "a knocked-out player was moved to get up");
            Assert.That(sim.LastStats.PlayersRevived, Is.EqualTo(1));
        });
    }

    [Test]
    public void ADeadPlayer_WakesAsACloneOnTheNearestCitysGateLane_WithFullHealth()
    {
        var sim = World(10);
        var id = sim.Index.Players[0];
        var attacker = DownStaging.LivingCreatureInside(sim);
        (float X, float Z) diedAt;
        int max;
        using (var tx = sim.Dbe.CreateQuickTransaction())
        {
            var c = tx.Open(attacker).Read(Creature.Bounds);
            diedAt = (c.X + 2f, c.Z);
            DownStaging.Stage(tx, id, diedAt.X, diedAt.Z);
            max = DownStaging.KnockDown(tx, id, Downed.Dead, upTick: 3);
            var state = tx.OpenMut(id);
            var s = state.Read(Player.State);
            s.Stance = Stances.Pistol;
            state.Set(Player.State, s);
            tx.Commit();
        }

        sim.Run();
        using var read = sim.Dbe.CreateQuickTransaction();
        var player = read.Open(id);
        var vitals = player.Read(Player.Vitals);
        var at = player.Read(Player.Bounds);

        // On the lane outside the nearest city's shuttleport gate — not the city's centre, which is the middle of the port's landing pad and where every
        // clone used to be stuck (2026-10-24). The lane is the rectangle OutsideGate spans.
        var port = sim.Index.Shuttleports[DownStaging.NearestCityIndex(sim, diedAt.X, diedAt.Z)];
        var (ax, az) = WorldBuilder.OutsideGate(port.X, port.Z, 0f, 0f);
        var (bx, bz) = WorldBuilder.OutsideGate(port.X, port.Z, 1f, 1f);
        var onLane = at.X >= ax - 0.01f && at.X <= bx + 0.01f && at.Z >= az - 0.01f && at.Z <= bz + 0.01f;
        var state2 = player.Read(Player.State);
        Assert.Multiple(() =>
        {
            Assert.That(vitals.Down, Is.EqualTo(Downed.Up), "the dead player never woke");
            Assert.That(vitals.Health, Is.EqualTo(max), "a clone wakes whole: no wounds, no insurance");
            Assert.That(onLane, Is.True, "the clone is not on the gate lane of the city nearest where the player died");
            Assert.That(state2.Activity, Is.EqualTo(PlayerActivity.Idle), "a clone wakes recovering, not mid-activity");
            Assert.That(state2.Stance, Is.EqualTo((int)Stances.None), "a clone wakes with its weapon still drawn");
        });
    }

    /// <summary>A creature fighting a player that is knocked out lets it lie — and its aggro does not pick it straight back up.</summary>
    [Test]
    public void ACreatureLetsADownedPlayerLie()
    {
        var sim = World(30);
        var id = sim.Index.Players[0];
        var attacker = DownStaging.LivingCreatureInside(sim);
        using (var tx = sim.Dbe.CreateQuickTransaction())
        {
            var c = tx.Open(attacker).Read(Creature.Bounds);
            DownStaging.Stage(tx, id, c.X + 2f, c.Z);
            DownStaging.KnockDown(tx, id, Downed.KnockedOut, upTick: int.MaxValue / 2);
            DownStaging.Engage(tx, attacker, id);

            // Thinking at once, so the decision this is about happens inside the window.
            var creature = tx.OpenMut(attacker);
            var timers = creature.Read(Creature.Timers);
            timers.ThinkCooldown = 0;
            creature.Set(Creature.Timers, timers);
            tx.Commit();
        }

        sim.Run();
        using var read = sim.Dbe.CreateQuickTransaction();
        var ai = read.Open(attacker).Read(Creature.Ai);
        var health = read.Open(id).Read(Player.Vitals).Health;
        Assert.Multiple(() =>
        {
            Assert.That((EntityId)ai.Target, Is.Not.EqualTo(id), "a creature is still fighting a knocked-out player");
            Assert.That(health, Is.Zero, "a knocked-out player took damage, or got up early");
        });
    }

    /// <summary>An aggressive creature wandering past a downed player leaves it lying: its aggro takes no downed quarry.</summary>
    [Test]
    public void AnAggressiveCreatureDoesNotPickUpADownedPlayer()
    {
        // Six ticks: its first decision, which with a healthy player there is an aggro (measured: by tick 6, not by tick 3). A creature that did pick the
        // downed player up drops it again at its NEXT decision, 0.4 to 1 s later, so a longer window would hide the pick-up this is about.
        var sim = World(6);
        var id = sim.Index.Players[0];
        var hunter = DownStaging.LivingCreatureInside(sim, aggressive: true);
        using (var tx = sim.Dbe.CreateQuickTransaction())
        {
            var c = tx.Open(hunter).Read(Creature.Bounds);
            DownStaging.Stage(tx, id, c.X + 3f, c.Z);
            DownStaging.KnockDown(tx, id, Downed.KnockedOut, upTick: int.MaxValue / 2);

            // Wandering, thinking at once, standing still: its next decision runs the aggro bubble with the player three metres away.
            var creature = tx.OpenMut(hunter);
            var ai = creature.Read(Creature.Ai);
            ai.Mode = AiMode.Wander;
            ai.Target = EntityId.Null;
            creature.Set(Creature.Ai, ai);
            var timers = creature.Read(Creature.Timers);
            timers.ThinkCooldown = 0;
            creature.Set(Creature.Timers, timers);
            tx.Commit();
        }

        sim.Run();
        using var read = sim.Dbe.CreateQuickTransaction();
        Assert.That((EntityId)read.Open(hunter).Read(Creature.Ai).Target, Is.Not.EqualTo(id), "an aggressive creature picked up a knocked-out player");
    }

    /// <summary>A player out of a fight heals a hundredth of its health a second.</summary>
    [Test]
    public void APlayerOutOfAFight_HealsAHundredthASecond()
    {
        var sim = World(30);
        var id = sim.Index.Players[0];
        var spot = DownStaging.QuietSpot(sim);
        int max;
        using (var tx = sim.Dbe.CreateQuickTransaction())
        {
            DownStaging.Stage(tx, id, spot.X, spot.Z);
            var player = tx.OpenMut(id);
            var vitals = player.Read(Player.Vitals);
            max = vitals.MaxHealth;
            vitals.Health = max / 2;
            player.Set(Player.Vitals, vitals);
            tx.Commit();
        }

        sim.Run();
        using var read = sim.Dbe.CreateQuickTransaction();
        var health = read.Open(id).Read(Player.Vitals).Health;

        // Thirty ticks at 10 Hz is three seconds: two to four of the once-a-second heals, depending on where the player's own beat falls.
        var step = (int)(max * TatooineData.RegenPerSecFraction);
        Assert.That(health - (max / 2), Is.InRange(2 * step, 4 * step), "a player out of a fight did not heal at a hundredth a second");
    }

    /// <summary>A player something is still hitting heals nothing, stance or no stance: it heals only once it has gone unhit a while (Loïc, 2026-10-24).</summary>
    [Test]
    public void APlayerStillBeingHit_DoesNotHeal()
    {
        var sim = World(30);
        var id = sim.Index.Players[0];
        var spot = DownStaging.QuietSpot(sim);
        int half;
        using (var tx = sim.Dbe.CreateQuickTransaction())
        {
            DownStaging.Stage(tx, id, spot.X, spot.Z);
            var player = tx.OpenMut(id);
            var vitals = player.Read(Player.Vitals);
            half = vitals.MaxHealth / 2;
            vitals.Health = half;

            // Hit "now" for the whole window: a blow stamped later than any tick the run reaches.
            vitals.LastHitTick = int.MaxValue / 2;
            player.Set(Player.Vitals, vitals);
            tx.Commit();
        }

        sim.Run();
        using var read = sim.Dbe.CreateQuickTransaction();
        Assert.That(read.Open(id).Read(Player.Vitals).Health, Is.EqualTo(half), "a player being hit healed");
    }
}

/// <summary>The staging the downed cases share.</summary>
internal static class DownStaging
{
    /// <summary>A living creature inside the world's bounds by a margin; see <c>PlayerDownTests</c> for why the margin is not a formality.</summary>
    public static EntityId LivingCreatureInside(TatooineSim sim, bool aggressive = false)
    {
        var margin = (sim.Config.WorldEdgeM * 0.5f) - 500f;
        foreach (var row in Causality.Creatures(sim))
        {
            if (row.Mode != AiMode.Dead && row.Health > 0 && MathF.Abs(row.X) < margin && MathF.Abs(row.Z) < margin
                && (!aggressive || Aggro(sim, row.Id) > 0f))
            {
                return row.Id;
            }
        }

        Assert.Fail("precondition: the world has a living creature inside its own bounds");
        return EntityId.Null;
    }

    private static float Aggro(TatooineSim sim, EntityId id)
    {
        using var tx = sim.Dbe.CreateQuickTransaction();
        return tx.Open(id).Read(Creature.Ai).AggroRadius;
    }

    /// <summary>Somewhere no creature comes: forty metres out of the first city's centre, where nothing spawns.</summary>
    public static (float X, float Z) QuietSpot(TatooineSim sim) => (sim.Index.Cities[0].X + 40f, sim.Index.Cities[0].Z);

    /// <summary>Puts a player at a point, standing still, roaming for good — the crowd's activity that neither fights nor decides inside the window.</summary>
    public static void Stage(Transaction tx, EntityId id, float x, float z)
    {
        var player = tx.OpenMut(id);
        var state = player.Read(Player.State);
        state.Activity = PlayerActivity.Roaming;
        state.ActivityTicks = int.MaxValue / 2;
        player.Set(Player.State, state);
        var move = player.Read(Player.Move);
        move.VelX = 0f;
        move.VelZ = 0f;
        move.DestX = x;
        move.DestZ = z;
        player.Set(Player.Move, move);
        var here = default(PlayerPlacement);
        here.SetAt(x, z, 0f, player.Read(Player.Bounds).HalfExtent);
        tx.Teleport(id, Player.Bounds, RealmId.Default, in here);
    }

    /// <summary>Puts a player down, as a lethal blow would, to get up at <paramref name="upTick"/>. Returns its maximum health.</summary>
    public static int KnockDown(Transaction tx, EntityId id, int how, int upTick)
    {
        var player = tx.OpenMut(id);
        var vitals = player.Read(Player.Vitals);
        vitals.Health = 0;
        vitals.Down = how;
        vitals.UpTick = upTick;
        player.Set(Player.Vitals, vitals);
        return vitals.MaxHealth;
    }

    /// <summary>A creature already in the fight: Fighting, naming the player, weapon ready, standing still.</summary>
    public static void Engage(Transaction tx, EntityId creatureId, EntityId playerId)
    {
        var creature = tx.OpenMut(creatureId);
        var ai = creature.Read(Creature.Ai);
        ai.Mode = AiMode.Fighting;
        ai.Target = playerId;
        creature.Set(Creature.Ai, ai);
        var timers = creature.Read(Creature.Timers);
        timers.AttackCooldown = 0;
        timers.ThinkCooldown = int.MaxValue / 2;
        creature.Set(Creature.Timers, timers);
        var move = creature.Read(Creature.Move);
        move.VelX = 0f;
        move.VelZ = 0f;
        creature.Set(Creature.Move, move);
    }

    /// <summary>The city the resolver should have chosen — computed here from the map rather than read back from the code under test.</summary>
    public static int NearestCityIndex(TatooineSim sim, float x, float z)
    {
        var bestSq = float.MaxValue;
        var best = -1;
        for (var i = 0; i < sim.Index.Cities.Count; i++)
        {
            var c = sim.Index.Cities[i];
            var dx = c.X - x;
            var dz = c.Z - z;
            var d2 = (dx * dx) + (dz * dz);
            if (d2 < bestSq)
            {
                bestSq = d2;
                best = i;
            }
        }

        return best;
    }
}
