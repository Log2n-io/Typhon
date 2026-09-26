using System;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Engine.Tests.Runtime;
using Typhon.Protocol;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Realms;

/// <summary>
/// Realms R4.3a (12-realms § 1–§ 2.2): a session is in at most one realm — placed, entered or anchored to the entity it follows — and a realm switch is one
/// published <c>RESET</c> frame whose first block is the new realm's <c>REALM</c> (SUB-29). Replication serves realm 0 only until F2, so a session in realm 1
/// holds nothing positioned there yet: what these tests pin is the switch, not realm 1's content.
/// </summary>
[TestFixture]
[NonParallelizable]
class RealmSessionTests : TestBase<RealmSessionTests>
{
    private const string World = "world";
    private const string Follow = "follow";

    private static SpatialGridConfig Realm0Grid() => SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(100, 100), 10);

    private static SpatialGridConfig Realm1Grid() => SpatialGridConfig.Flat(new Vector2(-40, -40), new Vector2(40, 40), 10);

    private static RealmPos At(float x, float y, ushort realm, int tag = 0) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Realm = realm, Tag = tag };

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<RealmPos>();
        dbe.ConfigureRealms(4);
        dbe.ConfigureSpatialGrid(Realm0Grid());
        dbe.InitializeArchetypes();
        dbe.Realms.Register(new RealmId(1), RealmConfig.SimulatedAlways(Realm1Grid()));
        return dbe;
    }

    private static FrameHarness CreateHarness(DatabaseEngine dbe)
    {
        var harness = FrameHarness.Create(dbe, subs =>
        {
            subs.Archetype<RealmUnit>(a => a.Motion(RealmUnit.Pos, m => m.Teleport(20)));
            subs.Profile(World, p => p.World().Of<RealmUnit>());
            subs.Profile(Follow, p => p.Sphere(30).AroundControlled().Of<RealmUnit>());
        }, nameof(RealmSessionTests), replicationCellM: 10);
        harness.RunFence = true;
        return harness;
    }

    private static EntityId[] Spawn(DatabaseEngine dbe, ushort realm, int count)
    {
        var ids = new EntityId[count];
        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < count; i++)
        {
            ids[i] = tx.Spawn<RealmUnit>(RealmUnit.Pos.Set(At(5 + (10 * i), 25, realm, i)));
        }

        tx.Commit();
        return ids;
    }

    private static int Held(FrameHarness harness, SessionId session) =>
        harness.Replica(session).NetIds(harness.CatalogPlan.ArchetypeByName(nameof(RealmUnit)).Idx).Length;

    private static void Run(FrameHarness harness, SessionId session, int ticks)
    {
        for (var i = 0; i < ticks; i++)
        {
            harness.RunTick(harness.Tick + 1);
            harness.Deliver(session);
        }
    }

    [Test]
    public void AnUnplacedSessionIsInNoRealmWhenTheEngineHoldsSeveral()
    {
        using var dbe = SetupEngine();
        using var harness = CreateHarness(dbe);
        var session = harness.OpenSessions(1, World)[0];
        Spawn(dbe, 0, 3);
        Run(harness, session, 3);

        Assert.Multiple(() =>
        {
            Assert.That(Held(harness, session), Is.Zero, "a session nobody placed holds nothing (12-realms § 1.2)");
            Assert.That(harness.Subscriptions.Commands.RealmOf(session), Is.EqualTo(RealmId.None));
            Assert.Throws<InvalidOperationException>(() => harness.Subscriptions.Commands.Place(session, new Vector3D(1, 1, 0)),
                "a realm-less Place is refused once the engine holds several realms");
        });
    }

    [Test]
    [VerifiesRule("SUB-29")]
    public void PlacingIntoAnotherRealmIsOneResetRealmFrame()
    {
        using var dbe = SetupEngine();
        using var harness = CreateHarness(dbe);
        var session = harness.OpenSessions(1, World)[0];
        var commands = harness.Subscriptions.Commands;
        Assert.That(commands.Enter(session, RealmId.Default), Is.True);
        Spawn(dbe, 0, 3);
        Run(harness, session, 3);
        Assert.Multiple(() =>
        {
            Assert.That(Held(harness, session), Is.EqualTo(3));
            Assert.That(commands.RealmOf(session), Is.EqualTo(RealmId.Default));
        });

        // One tick, one frame: RESET whose first block is REALM(1), and nothing of realm 0 after it.
        Assert.That(commands.Enter(session, new RealmId(1)), Is.True);
        harness.RunTick(harness.Tick + 1);
        var log = harness.Read(session);
        Assert.Multiple(() =>
        {
            Assert.That(log, Is.Not.Null, "a switch away from a realm the client holds entities of is sent at once, with or without content");
            Assert.That(log.Flags & TickFlags.Reset, Is.EqualTo(TickFlags.Reset));
            Assert.That(log.Calls[1], Is.EqualTo("realm 1"));
            Assert.That(log.Enters, Is.Empty, "no record of realm 0 follows the REALM of realm 1");
            Assert.That(commands.RealmOf(session), Is.EqualTo(new RealmId(1)), "the committed realm moved with the published frame");
        });
    }

    [Test]
    [VerifiesRule("SUB-29")]
    public void ASwitchAndBackRefillsFromAResetAndLeavingIsAResetRealmNone()
    {
        using var dbe = SetupEngine();
        using var harness = CreateHarness(dbe);
        var session = harness.OpenSessions(1, World)[0];
        var commands = harness.Subscriptions.Commands;
        commands.Enter(session, RealmId.Default);
        Spawn(dbe, 0, 3);
        Run(harness, session, 3);

        commands.Enter(session, new RealmId(1));
        Run(harness, session, 2);
        Assert.Multiple(() =>
        {
            Assert.That(Held(harness, session), Is.Zero, "the client's store was cleared by the switch");
            Assert.That(harness.Replica(session).Store.Realm?.RealmId, Is.EqualTo((ushort)1));
        });

        commands.Enter(session, RealmId.Default);
        Run(harness, session, 3);
        Assert.That(Held(harness, session), Is.EqualTo(3), "back in realm 0, refilled from nothing");

        Assert.That(commands.Leave(session), Is.True);
        harness.RunTick(harness.Tick + 1);
        var log = harness.Read(session);
        Assert.Multiple(() =>
        {
            Assert.That(log, Is.Not.Null);
            Assert.That(log.Flags & TickFlags.Reset, Is.EqualTo(TickFlags.Reset));
            Assert.That(log.Calls[1], Is.EqualTo("realm none"));
            Assert.That(commands.RealmOf(session), Is.EqualTo(RealmId.None));
        });
    }

    [Test]
    [VerifiesRule("SUB-29")]
    public void ASkippedRealmSwitchIsRetriedAsAReset()
    {
        using var dbe = SetupEngine();
        using var harness = CreateHarness(dbe);
        var session = harness.OpenSessions(1, World)[0];
        var commands = harness.Subscriptions.Commands;
        commands.Enter(session, RealmId.Default);
        Spawn(dbe, 0, 3);
        Run(harness, session, 4);

        // One frame in four (tick % 4 == 0): the switch is asked for on a tick that is skipped.
        var state = harness.StateOf(session);
        state.DegradeLevel = SkipPolicy.MaxDegradeLevel;
        while ((harness.Tick + 1) % 4 == 0)
        {
            Run(harness, session, 1);
        }

        commands.Enter(session, new RealmId(1));
        harness.RunTick(harness.Tick + 1);
        Assert.Multiple(() =>
        {
            Assert.That(harness.HasFrame(session), Is.False, "the switch tick was skipped");
            Assert.That(commands.RealmOf(session), Is.EqualTo(RealmId.Default), "a switch not published has not happened (SUB-03)");
        });

        FrameLog first = null;
        for (var i = 0; i < 4 && first == null; i++)
        {
            harness.RunTick(harness.Tick + 1);
            first = harness.Read(session);
        }

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.Not.Null);
            Assert.That(first.Flags & TickFlags.Reset, Is.EqualTo(TickFlags.Reset), "the first frame published after the switch is its RESET");
            Assert.That(first.Calls[1], Is.EqualTo("realm 1"));
            Assert.That(commands.RealmOf(session), Is.EqualTo(new RealmId(1)));
        });
    }

    [Test]
    public void ABudgetedSessionKeepsItsLinkStateAcrossARealmSwitch()
    {
        using var dbe = SetupEngine();
        using var harness = CreateHarness(dbe);
        var session = harness.OpenSessions(1, World)[0];
        var commands = harness.Subscriptions.Commands;
        var push = harness.Subscriptions.Push;
        commands.Enter(session, RealmId.Default);
        Spawn(dbe, 0, 3);
        Run(harness, session, 3);

        // The budget loop's state is the link's (R4.3): a door does not reset it, though the session's realm-0 geometry is given back and taken anew.
        push.SetShrink(session, 3);
        commands.Enter(session, new RealmId(1));
        Run(harness, session, 2);
        commands.Enter(session, RealmId.Default);
        Run(harness, session, 2);
        Assert.Multiple(() =>
        {
            Assert.That(push.ShrinkOf(session), Is.EqualTo(3));
            Assert.That(Held(harness, session), Is.EqualTo(3));
        });
    }

    [Test]
    [VerifiesRule("SUB-29")]
    public void AControlledSessionFollowsItsEntityIntoAnotherRealmInTheSameTick()
    {
        using var dbe = SetupEngine();
        using var harness = CreateHarness(dbe);
        var session = harness.OpenSessions(1, Follow)[0];
        var ids = Spawn(dbe, 0, 3);
        Assert.That(harness.Sessions.SetControlled(session, ids[0]), Is.True);
        Run(harness, session, 3);
        Assert.Multiple(() =>
        {
            Assert.That(harness.Subscriptions.Commands.RealmOf(session), Is.EqualTo(RealmId.Default), "its realm is its entity's");
            Assert.That(Held(harness, session), Is.GreaterThan(0));
            Assert.Throws<InvalidOperationException>(() => harness.Subscriptions.Commands.Enter(session, new RealmId(1)),
                "an entity-anchored session is moved by its entity, never explicitly");
        });

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Teleport(ids[0], RealmUnit.Pos, new RealmId(1), At(0, 0, 1, 0));
            tx.Commit();
        }

        // The teleport lands in this tick's fence, and this tick's frame is the switch.
        harness.RunTick(harness.Tick + 1);
        var log = harness.Read(session);
        Assert.Multiple(() =>
        {
            Assert.That(log, Is.Not.Null);
            Assert.That(log.Flags & TickFlags.Reset, Is.EqualTo(TickFlags.Reset));
            Assert.That(log.Calls[1], Is.EqualTo("realm 1"));
            Assert.That(harness.Subscriptions.Commands.RealmOf(session), Is.EqualTo(new RealmId(1)));
        });
    }

    [Test]
    public void ARealmSessionSlotIsGivenBackAndTheRealmIsObservedWhileASessionIsInIt()
    {
        using var dbe = SetupEngine();
        using var harness = CreateHarness(dbe);
        var sessions = harness.OpenSessions(3, World);
        var commands = harness.Subscriptions.Commands;
        var push = harness.Subscriptions.Push;
        foreach (var session in sessions)
        {
            commands.Enter(session, RealmId.Default);
        }

        Spawn(dbe, 0, 2);
        harness.RunTick(harness.Tick + 1);
        Assert.That(push.SessionsHere, Is.EqualTo(3));

        commands.Enter(sessions[0], new RealmId(1));
        harness.RunTick(harness.Tick + 1);
        Assert.Multiple(() =>
        {
            Assert.That(push.SessionsHere, Is.EqualTo(2), "a session that left realm 0 gave its realm-local slot back at once");
            Assert.That(dbe.RealmTable.ObserverCount(1), Is.EqualTo(1), "a session in a realm observes it (RLM-03)");
            Assert.That(dbe.RealmTable.ObserverCount(0), Is.EqualTo(2));
        });

        commands.Leave(sessions[0]);
        harness.RunTick(harness.Tick + 1);
        Assert.That(dbe.RealmTable.ObserverCount(1), Is.Zero);
    }
}
