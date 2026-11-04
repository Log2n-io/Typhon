using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SwgTatooine.Server;
using Typhon.Engine;
using Typhon.Protocol;

namespace SwgTatooine.Tests;

/// <summary>
/// Creatures join a fight a player starts on one of them (Loïc, 2026-11-03: "they should attack you all the time"): within
/// <see cref="TatooineData.PackRadiusM"/>, at the blow that wakes the one hit and at the blow that kills it — and a creature further off does not.
/// </summary>
[Category("Integration")]
[TestFixture]
[NonParallelizable]
internal sealed class PackChecks : ServedChecks
{
    private TatooineSim Start()
    {
        var config = Worlds.Served(Dir);
        config.WorkerCount = 1;
        var sim = new TatooineSim(config);
        sim.Initialize();
        Harness = new SessionHarness(sim);
        return sim;
    }

    private (FakeLink Link, EntityId Body) LogIn(TatooineSim sim, float x, float z)
    {
        var link = Harness.Connect(TatooineReplication.PlayerKind, TestAccounts.For(sim.Dbe, 0x7101));
        SessionHarness.Until(() => TatooineReplication.Intents.Possessions >= 1, "the avatar to be spawned");
        Harness.Ticks(2);
        return (link, Worlds.Park(sim, link.Session, x, z));
    }

    /// <summary>Living creatures of the planet, as many as asked, none of them in a fight.</summary>
    private static List<EntityId> Calm(TatooineSim sim, int n)
    {
        var picked = Causality.Creatures(sim)
            .Where(c => c.Health > 0 && c.Mode is AiMode.Wander or AiMode.Idle)
            .Select(c => c.Id)
            .Take(n)
            .ToList();
        Assert.That(picked, Has.Count.EqualTo(n), "the premise: enough calm creatures on the planet");
        return picked;
    }

    /// <summary>A creature moved to a point and made at home there, idle, its health as given (0: as it was).</summary>
    private static void Move(TatooineSim sim, EntityId id, float x, float z, int health = 0)
    {
        sim.WriteInTick(tx =>
        {
            var c = tx.OpenMut(id);
            var b = c.Read(Creature.Bounds);
            var half = (b.Bounds.MaxX - b.Bounds.MinX) * 0.5f;
            b.SetAt(x, z, b.Y, half);
            c.Set(Creature.Bounds, b);
            var m = c.Read(Creature.Move);
            m.VelX = m.VelZ = 0f;
            m.DestX = x;
            m.DestZ = z;
            c.Set(Creature.Move, m);
            var ai = c.Read(Creature.Ai);
            ai.Mode = AiMode.Idle;
            ai.Target = EntityId.Null;
            ai.HomeX = x;
            ai.HomeZ = z;
            ai.AggroRadius = 0f;   // none of them would have come on its own: what turns them is the fight
            c.Set(Creature.Ai, ai);
            var t = c.Read(Creature.Timers);
            t.ThinkCooldown = 1_000_000;   // and none decides to wander off inside the window
            c.Set(Creature.Timers, t);
            if (health > 0)
            {
                var v = c.Read(Creature.Vitals);
                v.Health = health;
                c.Set(Creature.Vitals, v);
            }
        });
    }

    private static (int Mode, EntityId Target) Of(TatooineSim sim, EntityId id)
    {
        (int, EntityId) seen = default;
        sim.WriteInTick(tx =>
        {
            var ai = tx.Open(id).Read(Creature.Ai);
            seen = (ai.Mode, ai.Target);
        });
        return seen;
    }

    private static void Attack(TatooineSim sim, FakeLink link, EntityId quarry)
    {
        Worlds.Arm(sim, link);
        link.SendCommand(nameof(SetTarget), ClientSays.SetTarget(SessionHarness.NetIdOf(link.Session, quarry)));
        link.SendCommand(nameof(SetStance), ClientSays.SetStance(Stances.Pistol));
    }

    /// <summary>The planet's far south-west, inside the world: nothing else near.</summary>
    private static (float X, float Z) OpenGround(TatooineSim sim)
    {
        var reach = sim.Config.WorldEdgeM * 0.5f * WorldBuilder.InsideEdge * 0.9f;
        return (-reach, -reach);
    }

    [TestCase(false, TestName = "TheCreaturesRoundOneAttacked_JoinIn")]
    [TestCase(true, TestName = "TheCreaturesRoundOneKilledAtOneBlow_JoinIn")]
    [CancelAfter(120_000)]
    public void TheCreaturesRoundOneAPlayerAttacks_TurnOnThePlayer(bool oneBlow)
    {
        using var sim = Start();
        var (x, z) = OpenGround(sim);
        var (link, body) = LogIn(sim, x, z);
        var four = Calm(sim, 4);
        var (quarry, near, other, far) = (four[0], four[1], four[2], four[3]);
        Move(sim, quarry, x + 12f, z, oneBlow ? 1 : 0);
        Move(sim, near, x + 16f, z + 5f);
        Move(sim, other, x + 18f, z - 6f);
        Move(sim, far, x + 12f, z + TatooineData.PackRadiusM + 15f);
        Harness.Ticks(3);   // into the spatial index, which the pack is found through
        SessionHarness.Until(() => SessionHarness.NetIdOf(link.Session, quarry) != 0, "the quarry in the attacker's view");

        var before = sim.Bridge.PackJoined;
        Attack(sim, link, quarry);
        SessionHarness.Until(() => sim.Bridge.PackJoined - before >= 2, "the creatures round the quarry to join in");
        Assert.Multiple(() =>
        {
            Assert.That(sim.Bridge.PackJoined - before, Is.EqualTo(2), "only the two within reach joined");
            Assert.That(Of(sim, near), Is.EqualTo((AiMode.Pursue, body)));
            Assert.That(Of(sim, other), Is.EqualTo((AiMode.Pursue, body)));
            Assert.That(Of(sim, far).Target.IsNull, Is.True, "a creature past the radius joined in");
            if (oneBlow)
            {
                Assert.That(Of(sim, quarry).Mode, Is.EqualTo(AiMode.Dead), "the premise: killed at the first blow");
            }
        });
    }
}
