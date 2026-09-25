using NUnit.Framework;
using System;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime.Subscriptions;

/// <summary>
/// A heading (09 § 15): an angle sent only when it turns past its tolerance from the value the client holds, measured in code space so the wrap at ±π is
/// no special case.
/// </summary>
[TestFixture]
[NonParallelizable]
sealed class HeadingTests : TestBase<HeadingTests>
{
    private const double ToleranceDeg = 2;

    private long _tick;

    private static double Rad(double degrees) => degrees * Math.PI / 180d;

    private FrameHarness Harness(DatabaseEngine dbe, string name)
    {
        var harness = FrameHarness.Create(dbe, subs =>
        {
            subs.Archetype<ProjCreature>(a => a
                .Motion(ProjCreature.Bounds, m => m.Tolerance(0.05).Teleport(ProjectionTestSchema.MaxSpeedMps))
                .Heading(ProjCreature.Bounds, x => x.Speed, bits: 16, toleranceDeg: ToleranceDeg, name: "yaw"));
            subs.Profile("world", p => p.Detection(PushDetection.Automatic).World().Of<ProjCreature>());
        }, name, new SubscriptionsOptions
        {
            MaxSessions = 4, ReplicationCellM = ProjectionTestSchema.ReplicationCellFor(0), AllowAutomaticPushDetection = true,
        });
        harness.RunFence = true;
        _tick = 0;
        return harness;
    }

    private static EntityId Spawn(DatabaseEngine dbe, double yaw)
    {
        using var tx = dbe.CreateQuickTransaction();
        var bounds = new ProjBounds { Bounds = new AABB2F { MinX = 10, MinY = 10, MaxX = 10, MaxY = 10 }, Speed = (float)yaw };
        var id = tx.Spawn<ProjCreature>(ProjCreature.Bounds.Set(in bounds));
        tx.Commit();
        return id;
    }

    private static void Turn(DatabaseEngine dbe, EntityId entity, double yaw)
    {
        using var tx = dbe.CreateQuickTransaction();
        tx.OpenMut(entity).Write(ProjCreature.Bounds).Speed = (float)yaw;
        tx.Commit();
    }

    // One tick, and the state records the session's frame carried for the entity; every frame is drained.
    private int StatesAfterTick(FrameHarness harness, SessionId session, uint netId)
    {
        harness.RunTick(++_tick);
        var states = 0;
        while (harness.Read(session) is { } log)
        {
            foreach (var (id, _) in log.States)
            {
                states += id == netId ? 1 : 0;
            }
        }

        return states;
    }

    /// <summary>
    /// A 1° turn under a 2° tolerance sends nothing; a turn to 3° from the held value sends one record, and the client then holds it. Across the wrap at
    /// ±π, a 0.9° turn sends nothing either.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-10")]
    public void AHeadingIsSentOnlyWhenItTurnsPastItsTolerance()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        var entity = Spawn(dbe, Rad(10));
        using var harness = Harness(dbe, nameof(AHeadingIsSentOnlyWhenItTurnsPastItsTolerance));
        var session = harness.OpenSessions(1, "world")[0];
        for (var i = 0; i < 4; i++)
        {
            harness.RunTick(++_tick);
            harness.Deliver(session);
        }

        var plan = harness.CatalogPlan.ArchetypeByName(nameof(ProjCreature)).Idx;
        var netIds = harness.Replica(session).NetIds(plan);
        Assert.That(netIds, Has.Length.EqualTo(1));
        var netId = netIds[0];
        Assert.That(harness.Replica(session).Value(plan, netId, "yaw"), Is.EqualTo(Rad(10)).Within(1e-3), "the enter carried the heading");

        Turn(dbe, entity, Rad(11));
        var small = StatesAfterTick(harness, session, netId);

        Turn(dbe, entity, Rad(13));
        var large = StatesAfterTick(harness, session, netId);

        // Held at 13°; the wrap: to 179.5°, then across ±π to −179.6° (0.9°).
        Turn(dbe, entity, Rad(179.5));
        StatesAfterTick(harness, session, netId);
        Turn(dbe, entity, Rad(-179.6));
        var wrap = StatesAfterTick(harness, session, netId);

        Assert.Multiple(() =>
        {
            Assert.That(small, Is.Zero, "a 1° turn under a 2° tolerance sent a record");
            Assert.That(large, Is.EqualTo(1), "a turn to 3° from the held heading sent no record, or more than one");
            Assert.That(wrap, Is.Zero, "a 0.9° turn across ±π sent a record");
        });
    }
}
