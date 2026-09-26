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
        dbe.Realms.Register(new RealmId(1), new RealmConfig
        {
            Grid = Realm1Grid(),
            WhenUnobserved = RealmUnobserved.Simulate,
            UnobservedTickDivisor = 1,
            Replication = new RealmReplicationConfig { Kind = "interior", CellM = 8, AppTag = 42 },
        });

        // A realm no session may be in: no replication declared.
        dbe.Realms.Register(new RealmId(2), RealmConfig.SimulatedAlways(Realm1Grid()));
        return dbe;
    }

    private static FrameHarness CreateHarness(DatabaseEngine dbe, Action<SubscriptionsRegistry> more = null)
    {
        var harness = FrameHarness.Create(dbe, subs =>
        {
            subs.RealmKinds("interior");
            more?.Invoke(subs);
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
    [VerifiesRule("SUB-30")]
    public void ACommandIsFramedByTheRealmItWasBuiltIn_AndAPositionFromALeftRealmIsRefused()
    {
        using var dbe = SetupEngine();
        using var harness = FrameHarness.Create(dbe, subs =>
        {
            subs.RealmKinds("interior");
            subs.Archetype<RealmUnit>(a => a.Motion(RealmUnit.Pos, m => m.Teleport(20)));
            subs.Profile(World, p => p.World().Of<RealmUnit>());
            subs.Command<RealmGoTo>(c => c.Rate(1_000, 1_000).Field(g => g.At, Codec.Pos3));
            subs.Command<RealmPing>(c => c.Rate(1_000, 1_000).Field(p => p.N, Codec.VarUInt));
        }, nameof(ACommandIsFramedByTheRealmItWasBuiltIn_AndAPositionFromALeftRealmIsRefused), replicationCellM: 10);
        harness.RunFence = true;
        harness.RunIngress = true;
        var session = harness.OpenSessions(1, World)[0];
        var commands = harness.Subscriptions.Commands;
        var ingress = harness.Subscriptions.Ingress;
        commands.Enter(session, RealmId.Default);
        Spawn(dbe, 0, 2);
        Run(harness, session, 3);
        var realm0 = harness.Replica(session).Store.Realm;

        commands.Enter(session, new RealmId(1));
        harness.RunTick(harness.Tick + 1);
        var switchTick = (uint)harness.Tick;
        harness.Deliver(session);
        var realm1 = harness.Replica(session).Store.Realm;
        Assert.That(realm1.RealmId, Is.EqualTo((ushort)1));

        // Built before the switch, in realm 0: the position is refused (REALM_CHANGED), the command without one arrives, framed by the realm it was built in.
        ingress.OnCommands(session, Encode(harness.CatalogPlan, switchTick - 1, realm0, ("RealmGoTo", 10, GoTo(5, 6, 0)), ("RealmPing", 11, Ping(7))));
        harness.RunTick(harness.Tick + 1);
        var log = harness.Read(session);
        Assert.Multiple(() =>
        {
            Assert.That(commands.Commands<RealmGoTo>().Count, Is.Zero, "a position built in a realm the session left never reaches the application");
            Assert.That(log?.Acks, Does.Contain(((ushort)10, AckReasons.RealmChanged)));
            Assert.That(commands.Commands<RealmPing>().Count, Is.EqualTo(1));
            foreach (ref readonly var ping in commands.Commands<RealmPing>())
            {
                Assert.That(ping.Realm, Is.EqualTo(RealmId.Default), "a command arrives with the realm it was built in");
            }
        });

        // Built in the new realm: decoded over its frame, and says so.
        ingress.OnCommands(session, Encode(harness.CatalogPlan, switchTick, realm1, ("RealmGoTo", 12, GoTo(-30.5, 12.25, 0))));
        harness.RunTick(harness.Tick + 1);
        Assert.That(commands.Commands<RealmGoTo>().Count, Is.EqualTo(1));
        foreach (var goTo in commands.Commands<RealmGoTo>())
        {
            Assert.Multiple(() =>
            {
                Assert.That(goTo.Realm, Is.EqualTo(new RealmId(1)));
                Assert.That(goTo.Value.At.X, Is.EqualTo(-30.5).Within(realm1.ForCommands.Step[0]));
                Assert.That(goTo.Value.At.Y, Is.EqualTo(12.25).Within(realm1.ForCommands.Step[1]));
            });
        }
    }

    private static byte[] Encode(CatalogPlan plan, uint clientTick, RealmFrame frame, params (string Name, ushort Seq, RecordValues Values)[] batch)
    {
        var list = new System.Collections.Generic.List<(MessagePlan, ushort, RecordValues)>();
        foreach (var (name, seq, values) in batch)
        {
            list.Add((plan.CommandByName(name), seq, values));
        }

        var buffer = new byte[1024];
        var writer = new WireWriter(buffer);
        CommandsMessage.Write(ref writer, clientTick, list, frame);
        return writer.Written.ToArray();
    }

    private static RecordValues GoTo(double x, double y, double z) => new() { ["At"] = FieldValue.Of(x, y, z) };

    private static RecordValues Ping(uint n) => new() { ["N"] = FieldValue.Of((double)n) };

    [Test]
    public void ARealmsFrameCarriesItsKindTagCellAndWidth_AndARealmWithNoReplicationCannotBeEntered()
    {
        using var dbe = SetupEngine();
        using var harness = CreateHarness(dbe);
        var session = harness.OpenSessions(1, World)[0];
        var commands = harness.Subscriptions.Commands;
        Assert.That(() => commands.Enter(session, new RealmId(2)), Throws.InvalidOperationException, "realm 2 declares no replication");

        commands.Enter(session, RealmId.Default);
        Spawn(dbe, 0, 1);
        Run(harness, session, 2);
        commands.Enter(session, new RealmId(1));
        Run(harness, session, 1);
        var store = harness.Replica(session).Store;
        Assert.Multiple(() =>
        {
            Assert.That(store.Realm?.RealmId, Is.EqualTo((ushort)1));
            Assert.That(store.RealmKind, Is.EqualTo("interior"));
            Assert.That(store.Realm?.AppTag, Is.EqualTo(42u));
            Assert.That(store.Realm?.CellM, Is.EqualTo(8d));
            Assert.That(store.Realm?.PositionBits, Is.EqualTo(24));
        });
    }

    [Test]
    public void AProfilesVariantServesTheRealmsOfItsKind_AndAnExcludedKindServesNothing()
    {
        using var dbe = SetupEngine();

        // In realm 0 (kind ""), the base's unplaced Sphere would see nothing: its "" variant is a World, and serves everything.
        using var harness = CreateHarness(dbe, subs =>
        {
            subs.Profile("scaled", p =>
            {
                p.Sphere(30).Of<RealmUnit>();
                p.In("", v => v.World().Of<RealmUnit>());
            });
            subs.Profile("elsewhere", p =>
            {
                p.World().Of<RealmUnit>();
                p.NotIn("");
            });
        });
        var scaled = harness.OpenSessions(1, "scaled")[0];
        var elsewhere = harness.OpenSessions(1, "elsewhere")[0];
        harness.Subscriptions.Commands.Enter(scaled, RealmId.Default);
        harness.Subscriptions.Commands.Enter(elsewhere, RealmId.Default);
        Spawn(dbe, 0, 3);
        for (var i = 0; i < 3; i++)
        {
            harness.RunTick(harness.Tick + 1);
            harness.Deliver(scaled);
            harness.Deliver(elsewhere);
        }

        Assert.Multiple(() =>
        {
            Assert.That(Held(harness, scaled), Is.EqualTo(3), "the realm's kind picked the World variant");
            Assert.That(Held(harness, elsewhere), Is.Zero, "a kind the profile excludes serves it nothing");
            Assert.That(harness.Assembler.RealmsUnserved, Is.GreaterThan(0), "and is counted");
        });
    }

    [Test]
    public void AVariantForAKindNobodyDeclaredIsRefusedAtStart()
    {
        using var dbe = SetupEngine();
        Assert.That(() => CreateHarness(dbe, subs => subs.Profile("cave", p =>
        {
            p.World().Of<RealmUnit>();
            p.In("cave", v => v.World().Of<RealmUnit>());
        })), Throws.InvalidOperationException.With.Message.Contains("does not declare"));
    }

    [Test]
    [VerifiesRule("SUB-28")]
    public void EachRealmsSessionsHoldThatRealmsEntitiesOnly_AtIdenticalLocalCoordinates()
    {
        using var dbe = SetupEngine();
        using var harness = CreateHarness(dbe);
        var sessions = harness.OpenSessions(2, World);
        var commands = harness.Subscriptions.Commands;
        commands.Enter(sessions[0], RealmId.Default);
        commands.Enter(sessions[1], new RealmId(1));

        // Same local coordinates in both realms: only which realm's structures an entry lives in can keep them apart (SUB-28).
        var inZero = Spawn(dbe, 0, 3);
        var inOne = Spawn(dbe, 1, 3);
        for (var i = 0; i < 4; i++)
        {
            harness.RunTick(harness.Tick + 1);
            harness.Deliver(sessions[0]);
            harness.Deliver(sessions[1]);
        }

        var archetype = harness.CatalogPlan.ArchetypeByName(nameof(RealmUnit)).Idx;
        Assert.Multiple(() =>
        {
            Assert.That(harness.Subscriptions.Hub.Active.Length, Is.EqualTo(2), $"realm 1 is served for its session ({harness.Subscriptions.LastUnservableRealm})");
            Assert.That(harness.Replica(sessions[0]).NetIds(archetype), Is.EquivalentTo(Array.ConvertAll(inZero, harness.NetIdOf)));
            Assert.That(harness.Replica(sessions[1]).NetIds(archetype), Is.EquivalentTo(Array.ConvertAll(inOne, harness.NetIdOf)));
            for (var i = 0; i < inOne.Length; i++)
            {
                // Decoded over realm 1's frame (bounds −40…40): the place in realm 1, not a code of realm 0's.
                var at = harness.Replica(sessions[1]).Position(archetype, harness.NetIdOf(inOne[i]));
                Assert.That(at[0], Is.EqualTo(5 + (10 * i)).Within(0.01), $"entity {i} x");
                Assert.That(at[1], Is.EqualTo(25).Within(0.01), $"entity {i} y");
            }
        });
    }

    [Test]
    public void ARealmNoSessionIsInStopsBeingServed_AndIsRefilledWhenOneReturns()
    {
        using var dbe = SetupEngine();
        using var harness = CreateHarness(dbe);
        var session = harness.OpenSessions(1, World)[0];
        var commands = harness.Subscriptions.Commands;
        var hub = harness.Subscriptions.Hub;
        commands.Enter(session, new RealmId(1));
        Spawn(dbe, 1, 2);
        Run(harness, session, 3);
        Assert.That(Held(harness, session), Is.EqualTo(2));

        // Out of realm 1, and the next sweep (every 64 ticks) stops serving it: no mark, projection or index for it after.
        commands.Enter(session, RealmId.Default);
        Run(harness, session, 70);
        Assert.Multiple(() =>
        {
            Assert.That(hub.RealmsDeactivated, Is.EqualTo(1));
            Assert.That(hub.For(1), Is.Null);
        });

        // Changed while nobody watched: an entity more. Back in, the realm is served again and its whole population re-pushed.
        var later = Spawn(dbe, 1, 1);
        Run(harness, session, 3);
        commands.Enter(session, new RealmId(1));
        Run(harness, session, 4);
        Assert.Multiple(() =>
        {
            Assert.That(hub.RealmsActivated, Is.EqualTo(2), "built once, woken once");
            Assert.That(Held(harness, session), Is.EqualTo(3), "refilled with what changed while it was dormant");
            Assert.That(harness.NetIdOf(later[0]), Is.Not.Zero);
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

#pragma warning disable CS0649
/// <summary>A command with a realm-framed field: where to go, in the session's realm.</summary>
struct RealmGoTo
{
    public Vector3D At;
}

/// <summary>A command with none.</summary>
struct RealmPing
{
    public uint N;
}
#pragma warning restore CS0649
