using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>
    /// #1081: the session following its entity through a door, and back, names that entity by the same netId on both sides — in its enters and in its SELF
    /// block. It used to be renamed at every crossing (5 runs out of 5 at demo scale): the migration released the identity and the arrival took a new one.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-09")]
    public void AFollowedEntityKeepsItsNetIdThroughARealmRoundTrip()
    {
        using var dbe = SetupEngine();
        using var harness = CreateHarness(dbe);
        var session = harness.OpenSessions(1, Follow)[0];
        var ids = Spawn(dbe, 0, 3);
        Assert.That(harness.Sessions.SetControlled(session, ids[0]), Is.True);
        Run(harness, session, 3);
        var before = harness.NetIdOf(ids[0]);
        Assert.That(before, Is.Not.Zero, "precondition: the entity has an identity");

        foreach (var (realm, to) in new[] { ((ushort)1, At(0, 0, 1, 0)), ((ushort)0, At(5, 25, 0, 0)) })
        {
            Cross(dbe, ids[0], realm, to);
            var logs = RunLogged(harness, session, 4);
            Assert.Multiple(() =>
            {
                Assert.That(logs.Any(l => (l.Flags & TickFlags.Reset) != 0), Is.True, $"precondition: crossing into realm {realm} switches the session");
                Assert.That(logs.SelectMany(l => l.Enters), Does.Contain(before), $"realm {realm}: the arrival names the entity by the identity it had");
                Assert.That(logs.SelectMany(l => l.Selves).Where(s => s.NetId != 0).Select(s => s.NetId), Has.All.EqualTo(before),
                    $"realm {realm}: SELF never names it by another");
                Assert.That(harness.Replica(session).NetIds(UnitIdx(harness)), Does.Contain(before), $"realm {realm}: and the client holds it under it");
            });
        }
    }

    /// <summary>
    /// An entity that crosses is a leave to the sessions of the realm it left and an arrival to those of the realm it entered — under ONE identity, not
    /// only for the entity a session follows (03 § 2). Both realms' occupancy follows it. Two paths: the destination cluster already has a replication block
    /// (another entity is there), so the entry is carried and re-initialized in place; or it has none, so only the identity is kept, for the first projection.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-09")]
    public void AnEntityThatCrossesLeavesItsRealmAndArrivesInTheOtherUnderTheSameNetId([Values] bool destinationWatched)
    {
        using var dbe = SetupEngine();
        using var harness = CreateHarness(dbe);
        var inZero = harness.OpenSessions(1, World)[0];
        var inOne = harness.OpenSessions(1, World)[0];
        var commands = harness.Subscriptions.Commands;
        Assert.That(commands.Enter(inZero, RealmId.Default), Is.True);
        Assert.That(commands.Enter(inOne, new RealmId(1)), Is.True);
        var ids = Spawn(dbe, 0, 3);
        if (destinationWatched)
        {
            // Already where the crossing lands, so its cluster — and that cluster's block — exist before the crossing.
            using var tx = dbe.CreateQuickTransaction();
            tx.Spawn<RealmUnit>(RealmUnit.Pos.Set(At(0, 0, 1, 9)));
            tx.Commit();
        }

        RunLogged(harness, inZero, inOne, 3);
        var netId = harness.NetIdOf(ids[1]);
        Assert.That(netId, Is.Not.Zero, "precondition: the entity has an identity");

        Cross(dbe, ids[1], 1, At(0, 0, 1, 1));
        var (left, arrived) = RunLogged(harness, inZero, inOne, 4);
        Assert.Multiple(() =>
        {
            Assert.That(left.SelectMany(l => l.Leaves), Does.Contain(netId), "the realm it left is told it left");
            Assert.That(arrived.SelectMany(l => l.Enters), Does.Contain(netId), "the realm it entered sees it arrive under the same identity");
            Assert.That(harness.Replica(inZero).NetIds(UnitIdx(harness)), Does.Not.Contain(netId));
            Assert.That(harness.Replica(inOne).NetIds(UnitIdx(harness)), Does.Contain(netId));
            Assert.That(harness.Replica(inOne).NetIds(UnitIdx(harness)), Has.Length.EqualTo(destinationWatched ? 2 : 1));
            foreach (var push in harness.Subscriptions.Hub.Active)
            {
                Assert.That(push.VerifyOccupancy(), Is.Zero, "every realm's occupancy agrees with a recount after the crossing");
            }
        });
    }

    private static int UnitIdx(FrameHarness harness) => harness.CatalogPlan.ArchetypeByName(nameof(RealmUnit)).Idx;

    private static void Cross(DatabaseEngine dbe, EntityId entity, ushort realm, RealmPos to)
    {
        using var tx = dbe.CreateQuickTransaction();
        tx.Teleport(entity, RealmUnit.Pos, new RealmId(realm), to);
        tx.Commit();
    }

    private static List<FrameLog> RunLogged(FrameHarness harness, SessionId session, int ticks)
    {
        var logs = new List<FrameLog>();
        for (var i = 0; i < ticks; i++)
        {
            harness.RunTick(harness.Tick + 1);
            logs.AddRange(harness.DeliverLogged(session));
        }

        return logs;
    }

    private static (List<FrameLog> A, List<FrameLog> B) RunLogged(FrameHarness harness, SessionId a, SessionId b, int ticks)
    {
        var (logsA, logsB) = (new List<FrameLog>(), new List<FrameLog>());
        for (var i = 0; i < ticks; i++)
        {
            harness.RunTick(harness.Tick + 1);
            logsA.AddRange(harness.DeliverLogged(a));
            logsB.AddRange(harness.DeliverLogged(b));
        }

        return (logsA, logsB);
    }

    /// <summary>
    /// The Try overloads answer an anchored session instead of raising at it, and still do not move it.
    /// </summary>
    /// <remarks>
    /// <b>SUB-29 is satisfied either way — what changes is whether an application can obey it.</b> Whether a session is anchored is decided by
    /// <c>ViewpointSource</c>, which is internal, and a profile requested on one tick is applied by the next tick's prologue: so an application that asks
    /// first and acts second is asking about a state that changes between the two, and the wrong answer is an exception on the tick thread. The test and the
    /// act have to be one call, which is what these are.
    /// </remarks>
    [Test]
    [VerifiesRule("SUB-29")]
    public void AnAnchoredSessionAnswersTheTryOverloadsRatherThanRaising()
    {
        using var dbe = SetupEngine();
        using var harness = CreateHarness(dbe);
        var commands = harness.Subscriptions.Commands;
        var session = harness.OpenSessions(1, Follow)[0];
        var ids = Spawn(dbe, 0, 3);
        Assert.That(harness.Sessions.SetControlled(session, ids[0]), Is.True);
        Run(harness, session, 3);

        Assert.Multiple(() =>
        {
            Assert.That(commands.IsAnchored(session), Is.True, "a profile declaring AroundControlled is what anchored means");
            Assert.That(commands.ControlledOf(session), Is.EqualTo(ids[0]), "the anchor is readable, so an application need not mirror it");

            // The whole point: the same condition, answered rather than thrown.
            Assert.Throws<InvalidOperationException>(() => commands.Enter(session, new RealmId(1)));
            Assert.That(commands.TryEnter(session, new RealmId(1)), Is.False, "TryEnter raised or moved an anchored session");
            Assert.That(commands.TryPlace(session, new RealmId(1), new Vector3D(1, 1, 0)), Is.False, "TryPlace raised or moved an anchored session");

            // SUB-29 itself: answering false is not a quiet move.
            Assert.That(commands.RealmOf(session), Is.EqualTo(RealmId.Default), "a refused Try moved the session anyway");
        });
    }

    /// <summary>
    /// A session nothing anchors takes the Try overloads exactly as it takes the raising ones.
    /// </summary>
    /// <remarks>
    /// The half that stops <c>TryEnter</c> being a no-op that always answers <see langword="false"/> — which would pass every assertion of the case above.
    /// </remarks>
    [Test]
    [VerifiesRule("SUB-29")]
    public void AnUnanchoredSessionIsMovedByTheTryOverloads()
    {
        using var dbe = SetupEngine();
        using var harness = CreateHarness(dbe);
        var commands = harness.Subscriptions.Commands;
        var session = harness.OpenSessions(1, World)[0];
        Spawn(dbe, 1, 2);

        Assert.That(commands.IsAnchored(session), Is.False);
        Assert.That(commands.ControlledOf(session), Is.EqualTo(EntityId.Null), "nothing controls it");
        Assert.That(commands.TryEnter(session, new RealmId(1)), Is.True, "an ordinary session was refused");
        Run(harness, session, 3);
        Assert.That(commands.RealmOf(session), Is.EqualTo(new RealmId(1)), "TryEnter answered true and did not move it");
    }

    /// <summary>
    /// <c>TryEnter</c> answers <see langword="false"/> for a realm that is gone, and still raises for a caller's own mistake.
    /// </summary>
    /// <remarks>
    /// <b>The line is what can change under the caller.</b> A realm being unregistered is a race — a dungeon closes at the first fence that finds it empty —
    /// so it is an answer. <see cref="RealmId.None"/> is not: it cannot become a realm, and answering false for it would turn a misuse of the API into a
    /// session that quietly never arrives.
    /// </remarks>
    [Test]
    [VerifiesRule("SUB-29")]
    public void TryEnterAnswersForARealmThatIsGoneAndRaisesForAMisuse()
    {
        using var dbe = SetupEngine();
        using var harness = CreateHarness(dbe);
        var commands = harness.Subscriptions.Commands;
        var session = harness.OpenSessions(1, World)[0];

        Assert.Multiple(() =>
        {
            Assert.That(commands.TryEnter(session, new RealmId(9)), Is.False, "a realm this engine never registered is an answer, not an exception");
            Assert.Throws<ArgumentException>(() => commands.TryEnter(session, RealmId.None), "RealmId.None is Leave(), and saying so is the point");
            Assert.Throws<InvalidOperationException>(() => commands.TryEnter(session, new RealmId(2)),
                "a realm declaring no replication is fixed when it is registered, so it is the application's bug and not a race");
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

    [Test]
    [VerifiesRule("SUB-30")]
    public void APositionDecodedBeforeASwitchAndDrainedAfterItIsRefused()
    {
        using var dbe = SetupEngine();
        using var harness = FrameHarness.Create(dbe, subs =>
        {
            subs.RealmKinds("interior");
            subs.Archetype<RealmUnit>(a => a.Motion(RealmUnit.Pos, m => m.Teleport(20)));
            subs.Profile(World, p => p.World().Of<RealmUnit>());
            subs.Command<RealmGoTo>(c => c.Rate(1_000, 1_000).Field(g => g.At, Codec.Pos3));
            subs.Command<RealmPing>(c => c.Rate(1_000, 1_000).Field(p => p.N, Codec.VarUInt));
        }, nameof(APositionDecodedBeforeASwitchAndDrainedAfterItIsRefused), replicationCellM: 10);
        harness.RunFence = true;
        harness.RunIngress = true;
        var session = harness.OpenSessions(1, World)[0];
        var commands = harness.Subscriptions.Commands;
        commands.Enter(session, RealmId.Default);
        Spawn(dbe, 0, 2);
        Run(harness, session, 3);
        var realm0 = harness.Replica(session).Store.Realm;

        // Decoded in realm 0, then the switch is published before any drain: the ring still holds the command when the session is in realm 1.
        harness.Subscriptions.Ingress.OnCommands(session,
            Encode(harness.CatalogPlan, (uint)harness.Tick, realm0, ("RealmGoTo", 20, GoTo(5, 6, 0)), ("RealmPing", 21, Ping(3))));
        harness.RunIngress = false;
        commands.Enter(session, new RealmId(1));
        Run(harness, session, 1);
        Assert.That(commands.RealmOf(session), Is.EqualTo(new RealmId(1)));

        harness.RunIngress = true;
        harness.RunTick(harness.Tick + 1);
        var log = harness.Read(session);
        Assert.Multiple(() =>
        {
            Assert.That(commands.Commands<RealmGoTo>().Count, Is.Zero, "realm 0's position never reaches the application once the session is in realm 1");
            Assert.That(log?.Acks, Does.Contain(((ushort)20, AckReasons.RealmChanged)));
            Assert.That(commands.Commands<RealmPing>().Count, Is.EqualTo(1), "a command with no position is not refused");
        });
    }

    [Test]
    [VerifiesRule("SUB-24")]
    public void AWokenRealmsOccupancyCountsItsOwnEntitiesOnly()
    {
        using var dbe = SetupEngine();
        using var harness = CreateHarness(dbe);
        var sessions = harness.OpenSessions(2, World);
        var commands = harness.Subscriptions.Commands;
        var hub = harness.Subscriptions.Hub;
        commands.Enter(sessions[0], RealmId.Default);
        commands.Enter(sessions[1], new RealmId(1));
        Spawn(dbe, 0, 3);
        var inOne = Spawn(dbe, 1, 2);

        void RunBoth(int ticks)
        {
            for (var i = 0; i < ticks; i++)
            {
                harness.RunTick(harness.Tick + 1);
                harness.Deliver(sessions[0]);
                harness.Deliver(sessions[1]);
            }
        }

        RunBoth(3);
        commands.Enter(sessions[1], RealmId.Default);
        RunBoth(70);
        Assert.That(hub.For(1), Is.Null, "realm 1 went dormant");

        // While it slept: one more entity, and one moved inside it.
        Spawn(dbe, 1, 1);
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Teleport(inOne[0], RealmUnit.Pos, new RealmId(1), At(-30, -30, 1, 0));
            tx.Commit();
        }

        RunBoth(2);
        commands.Enter(sessions[1], new RealmId(1));
        RunBoth(4);
        var one = hub.For(1);
        var zero = hub.For(0);
        Assert.Multiple(() =>
        {
            Assert.That(Total(one.Occupancy), Is.EqualTo(3), "realm 1's occupancy counts realm 1's entities, not realm 0's");
            Assert.That(one.VerifyOccupancy(), Is.Zero);
            Assert.That(Total(zero.Occupancy), Is.EqualTo(3));
            Assert.That(zero.VerifyOccupancy(), Is.Zero);
            Assert.That(Held(harness, sessions[1]), Is.EqualTo(3));
        });
    }

    private static int Total(ReplicationOccupancy occupancy)
    {
        var keys = occupancy.OrderedKeys(out var count);
        var total = 0;
        for (var i = 0; i < count; i++)
        {
            total += occupancy.Get(keys[i]);
        }

        return total;
    }

    [Test]
    public void WithOneRealmASessionTakenOutWithLeaveIsPlacedBackInRealm0()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<RealmPos>();
        dbe.ConfigureSpatialGrid(Realm0Grid());
        dbe.InitializeArchetypes();
        using (dbe)
        {
            using var harness = FrameHarness.Create(dbe, subs =>
            {
                subs.Archetype<RealmUnit>(a => a.Motion(RealmUnit.Pos, m => m.Teleport(20)));
                subs.Profile("near", p => p.Sphere(30).Of<RealmUnit>());
            }, nameof(WithOneRealmASessionTakenOutWithLeaveIsPlacedBackInRealm0), replicationCellM: 10);
            harness.RunFence = true;
            var session = harness.OpenSessions(1, "near")[0];
            var commands = harness.Subscriptions.Commands;
            Spawn(dbe, 0, 2);
            Assert.That(commands.Place(session, new Vector3D(5, 25, 0)), Is.True);
            Run(harness, session, 3);
            Assert.That(Held(harness, session), Is.EqualTo(2));

            commands.Leave(session);
            Run(harness, session, 2);
            Assert.That(commands.RealmOf(session), Is.EqualTo(RealmId.None));

            Assert.That(commands.Place(session, new Vector3D(5, 25, 0)), Is.True);
            Run(harness, session, 3);
            Assert.Multiple(() =>
            {
                Assert.That(commands.RealmOf(session), Is.EqualTo(RealmId.Default), "the one realm there is");
                Assert.That(Held(harness, session), Is.EqualTo(2));
            });
        }
    }

    [TestCase(true, true)]
    [TestCase(true, false)]
    [TestCase(false, false)]
    public void ARegionSentThroughTheTransportIsServed_WithRealmKindsDeclared(bool kinds, bool notIn)
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<RealmPos>();
        dbe.ConfigureSpatialGrid(Realm0Grid());
        dbe.InitializeArchetypes();
        using (dbe)
        {
            using var harness = FrameHarness.Create(dbe, subs =>
            {
                if (kinds)
                {
                    subs.RealmKinds("interior");
                }

                subs.Archetype<RealmUnit>(a => a.Motion(RealmUnit.Pos, m => m.Teleport(20)));
                subs.Profile("god", p =>
                {
                    p.ClientRegion(80).Of<RealmUnit>();
                    if (notIn)
                    {
                        p.NotIn("interior");
                    }
                });
            }, nameof(ARegionSentThroughTheTransportIsServed_WithRealmKindsDeclared), replicationCellM: 10);
            harness.RunFence = true;
            harness.RunIngress = true;
            var session = harness.OpenSessions(1, "god")[0];
            Spawn(dbe, 0, 3);
            Run(harness, session, 2);
            var frame = harness.Replica(session).Store.Realm;
            Assert.That(frame, Is.Not.Null, "the session was sent its realm before it had a region");

            var region = new RecordValues
            {
                [BuiltInCommands.RegionVerticesField] = FieldValue.Of(0d, 0d, 0d, 60d, 0d, 0d, 60d, 60d, 0d, 0d, 60d, 0d),
                [BuiltInCommands.RegionAltitudeField] = FieldValue.Of(50d),
                [BuiltInCommands.RegionBudgetField] = FieldValue.Of(256d),
            };
            harness.Subscriptions.Ingress.OnCommands(session,
                Encode(harness.CatalogPlan, (uint)harness.Tick, frame, (BuiltInCommands.ClientRegion, 1, region)));
            Run(harness, session, 4);
            Assert.That(Held(harness, session), Is.EqualTo(3), "the region's entities");
        }
    }

    [Test]
    public void ARadiusSetForTheProfileIsBoundedByTheVariantServingTheRealm()
    {
        using var dbe = SetupEngine();
        using var harness = CreateHarness(dbe, subs => subs.Profile("scoped", p =>
        {
            p.Sphere(10, max: 40).Of<RealmUnit>();
            p.In("interior", v => v.Sphere(5).Of<RealmUnit>());
        }));
        var session = harness.OpenSessions(1, "scoped")[0];
        var commands = harness.Subscriptions.Commands;
        Assert.That(commands.Place(session, RealmId.Default, new Vector3D(5, 25, 0)), Is.True);
        Assert.That(commands.SetRadius(session, 35), Is.True, "within the declared profile's bounds");
        Spawn(dbe, 1, 3);
        Run(harness, session, 2);

        // Realm 1's window is sized for the interior variant's 5 m: the session is gathered at 5 m there, not at the 35 m it asked for on the planet.
        Assert.That(commands.Place(session, new RealmId(1), new Vector3D(5, 25, 0)), Is.True);
        Run(harness, session, 3);
        Assert.That(Held(harness, session), Is.EqualTo(1), "only the entity within the variant's radius");
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
    [VerifiesRule("SUB-28")]
    public void ATeleportBetweenServedRealmsLeavesOneAndEntersTheOtherInItsFrame()
    {
        using var dbe = SetupEngine();
        using var harness = CreateHarness(dbe);
        var sessions = harness.OpenSessions(2, World);
        var commands = harness.Subscriptions.Commands;
        commands.Enter(sessions[0], RealmId.Default);
        commands.Enter(sessions[1], new RealmId(1));
        var ids = Spawn(dbe, 0, 3);
        Spawn(dbe, 1, 1);

        void Run3()
        {
            for (var i = 0; i < 3; i++)
            {
                harness.RunTick(harness.Tick + 1);
                harness.Deliver(sessions[0]);
                harness.Deliver(sessions[1]);
            }
        }

        Run3();
        var archetype = harness.CatalogPlan.ArchetypeByName(nameof(RealmUnit)).Idx;
        var before = harness.NetIdOf(ids[1]);
        Assert.Multiple(() =>
        {
            Assert.That(Held(harness, sessions[0]), Is.EqualTo(3));
            Assert.That(Held(harness, sessions[1]), Is.EqualTo(1));
        });

        // Realm 0 → realm 1, both served: a leave in realm 0, an enter in realm 1 at its place there — never realm 0's codes read in realm 1's frame.
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Teleport(ids[1], RealmUnit.Pos, new RealmId(1), At(-12.5f, 30, 1, 1));
            tx.Commit();
        }

        Run3();
        var after = harness.NetIdOf(ids[1]);
        Assert.Multiple(() =>
        {
            Assert.That(harness.Replica(sessions[0]).NetIds(archetype), Does.Not.Contain(before), "realm 0's session was told it left");
            Assert.That(Held(harness, sessions[0]), Is.EqualTo(2));
            Assert.That(after, Is.Not.Zero, "it has an identity in realm 1");
            Assert.That(harness.Replica(sessions[1]).NetIds(archetype), Does.Contain(after));
            Assert.That(Held(harness, sessions[1]), Is.EqualTo(2));
            var at = harness.Replica(sessions[1]).Position(archetype, after);
            Assert.That(at[0], Is.EqualTo(-12.5).Within(0.01));
            Assert.That(at[1], Is.EqualTo(30).Within(0.01));
        });

        // And back.
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Teleport(ids[1], RealmUnit.Pos, RealmId.Default, At(60, 60, 0, 1));
            tx.Commit();
        }

        Run3();
        Assert.Multiple(() =>
        {
            Assert.That(Held(harness, sessions[0]), Is.EqualTo(3));
            Assert.That(Held(harness, sessions[1]), Is.EqualTo(1));
            var at = harness.Replica(sessions[0]).Position(archetype, harness.NetIdOf(ids[1]));
            Assert.That(at[0], Is.EqualTo(60).Within(0.01));
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

    // ── SWG-10: SessionEvent.RealmClosed (12-realms § 1.6 Q7) ───────────────────────────────────────────────────

    /// <summary>
    /// A realm unregistered and then removed with a session still in it moves that session to none and says so once.
    /// </summary>
    /// <remarks>
    /// <b>This is the case a game meets on its first day and the engine had no way to report.</b> An instance, a dungeon, a house, a match — anything
    /// registered at run time — is unregistered when it is finished with, and the fence removes it once it holds nothing. A session watching it then points at
    /// a realm that does not exist, and before this it simply stopped being served with nothing said to the application: the demo sent an announcement of its
    /// own and hoped the client acted on it.
    /// <para>
    /// <b>Once</b> is asserted over many ticks rather than one, because the natural wrong implementation fires on every tick afterwards: the notice is produced
    /// by the frame prologue failing to resolve the session's realm, which it would go on failing to do for as long as the session kept pointing at it.
    /// </para>
    /// </remarks>
    [Test]
    [VerifiesRule("SUB-29")]
    public void ARealmRemovedUnderASessionMovesItToNoneAndTellsTheApplicationOnce()
    {
        using var dbe = SetupEngine();
        using var harness = CreateHarness(dbe);
        var commands = harness.Subscriptions.Commands;
        var session = harness.OpenSessions(1, World)[0];

        // A realm registered while the engine runs, which is the only kind that is ever removed.
        dbe.Realms.Register(new RealmId(3), new RealmConfig
        {
            Grid = Realm1Grid(),
            WhenUnobserved = RealmUnobserved.Simulate,
            UnobservedTickDivisor = 1,
            Replication = new RealmReplicationConfig { Kind = "interior", CellM = 8, AppTag = 7 },
        });

        Assert.That(commands.TryEnter(session, new RealmId(3)), Is.True);
        Run(harness, session, 3);
        Assert.That(commands.RealmOf(session), Is.EqualTo(new RealmId(3)), "precondition: the session is in the realm about to go");

        // Unregistered (Closing), then removed by the first fence that finds it empty — it never held anything, so that is the next one.
        var log = RemoveRealm(harness, dbe, new RealmId(3), session);
        Assert.Multiple(() =>
        {
            Assert.That(commands.RealmOf(session), Is.EqualTo(RealmId.None), "the session was left pointing at a realm that does not exist");
            Assert.That(log, Is.Not.Null, "a session whose realm went away is told, and the only way to tell a client is a frame");
            Assert.That(log.Flags & TickFlags.Reset, Is.EqualTo(TickFlags.Reset));
            Assert.That(log.Calls[1], Is.EqualTo("realm none"), "its client holds nothing now, and the REALM block is what says so (SUB-29)");
        });

        // Delivered by the next tick's Engine-Pre, as Opened and Closed are.
        harness.RunTick(harness.Tick + 1);
        var events = RealmClosures(harness);
        Assert.Multiple(() =>
        {
            Assert.That(events, Has.Count.EqualTo(1), "exactly one event");
            Assert.That(events[0].Kind, Is.EqualTo(SessionEventKind.RealmClosed));
            Assert.That(events[0].Session, Is.EqualTo(session));
            Assert.That(events[0].Realm, Is.EqualTo(new RealmId(3)), "the realm that went away — the key the application had it filed under");
            Assert.That(events[0].ToString(), Does.Contain("realm 3"), "and it says which, because a log line that did not would be no use");
        });

        for (var i = 0; i < 8; i++)
        {
            harness.RunTick(harness.Tick + 1);
            Assert.That(RealmClosures(harness), Is.Empty, $"tick {i} after the removal produced a second RealmClosed");
        }
    }

    /// <summary>
    /// The same, for a session anchored to an entity that was destroyed with the realm rather than placed in it by hand.
    /// </summary>
    /// <remarks>
    /// <b>A separate case because it is answered from a different place.</b> A placed session's realm is a field in the session table; an anchored one's is a
    /// cache of its entity's last known realm, kept so that the common tick costs no entity probe. Clearing one and not the other leaves this arm firing the
    /// event on every tick for ever, which is exactly what the first implementation did.
    /// </remarks>
    [Test]
    [VerifiesRule("SUB-29")]
    public void AnAnchoredSessionWhoseRealmIsRemovedIsAlsoMovedToNoneOnce()
    {
        using var dbe = SetupEngine();
        using var harness = CreateHarness(dbe);
        var commands = harness.Subscriptions.Commands;
        var session = harness.OpenSessions(1, Follow)[0];
        dbe.Realms.Register(new RealmId(3), new RealmConfig
        {
            Grid = Realm1Grid(),
            WhenUnobserved = RealmUnobserved.Simulate,
            UnobservedTickDivisor = 1,
            Replication = new RealmReplicationConfig { Kind = "interior", CellM = 8, AppTag = 7 },
        });

        var inside = Spawn(dbe, 3, 1);
        Assert.That(harness.Sessions.SetControlled(session, inside[0]), Is.True);
        Run(harness, session, 3);
        Assert.That(commands.RealmOf(session), Is.EqualTo(new RealmId(3)), "precondition: its realm is its entity's");

        // The realm is emptied the way an application empties one, then unregistered: the entity goes with it.
        using (var tx = dbe.CreateQuickTransaction())
        {
            Assert.That(dbe.Realms.DestroyContents(new RealmId(3), tx), Is.EqualTo(1));
            tx.Commit();
        }

        RemoveRealm(harness, dbe, new RealmId(3), session);
        harness.RunTick(harness.Tick + 1);
        var events = RealmClosures(harness);
        Assert.Multiple(() =>
        {
            Assert.That(commands.RealmOf(session), Is.EqualTo(RealmId.None));
            Assert.That(events, Has.Count.EqualTo(1));
            Assert.That(events[0].Realm, Is.EqualTo(new RealmId(3)));
        });

        for (var i = 0; i < 8; i++)
        {
            harness.RunTick(harness.Tick + 1);
            Assert.That(RealmClosures(harness), Is.Empty, $"tick {i} after the removal produced a second RealmClosed");
        }
    }

    // ── SWG-10: SessionRequest.Follow (12-realms § 2.2 Q5) ──────────────────────────────────────────────────────

    /// <summary>
    /// <c>Follow</c> centres the session on an entity it does not control, and takes it through that entity's realm changes.
    /// </summary>
    [Test]
    public void AFollowedSessionIsCentredAndRealmedOnItsEntityWithoutControllingIt()
    {
        using var dbe = SetupEngine();
        using var harness = CreateHarness(dbe);
        var commands = harness.Subscriptions.Commands;
        var session = harness.OpenSessions(1, Follow)[0];
        var ids = Spawn(dbe, 0, 3);

        Request(harness, session, r => r.Follow(ids[1]));
        Run(harness, session, 3);

        Assert.Multiple(() =>
        {
            Assert.That(harness.Sessions.FollowedOf(session), Is.EqualTo(ids[1]));
            Assert.That(harness.Sessions.ControlledOf(session), Is.EqualTo(EntityId.Null), "following is not controlling");
            Assert.That(commands.ControlledOf(session), Is.EqualTo(EntityId.Null), "and the engine says so when the application asks");
            Assert.That(harness.Assembler.TryGetFollowed(session, out var at), Is.True, "the viewpoint is the entity's");
            Assert.That(at.X, Is.EqualTo(15).Within(0.5), "entity 1 is at x = 15, which is where the session is looking from");
            Assert.That(Held(harness, session), Is.GreaterThan(0));
        });
    }

    /// <summary>
    /// A session that follows an entity is served the entity's position and NOT its owner data; one that controls it is served both.
    /// </summary>
    /// <remarks>
    /// <b>This is the reason the verb exists rather than the reason it is convenient.</b> An application that wanted a camera to ride a player had one way to
    /// do it — make the session control the player — and that also sends the session the player's <c>SELF</c> block and its owner fields, which are its
    /// private data. The two arms are asserted in one test on one archetype, because the claim is a difference and a test of the following arm alone would
    /// pass against an engine that sends nobody owner fields at all.
    /// </remarks>
    [Test]
    [VerifiesRule("SUB-29")]
    public void AFollowedSessionGetsThePositionButNotTheOwnerFieldsOfItsSubject()
    {
        using var dbe = SetupEngine();
        using var harness = FrameHarness.Create(dbe, subs =>
        {
            subs.RealmKinds("interior");
            subs.Archetype<RealmUnit>(a => a
                .Motion(RealmUnit.Pos, m => m.Teleport(20))
                .Owner(o => o.Field(RealmUnit.Pos, p => p.Tag, Codec.I32, "tag")));
            subs.Profile(Follow, p => p.Sphere(30).AroundControlled().Of<RealmUnit>());
        }, nameof(AFollowedSessionGetsThePositionButNotTheOwnerFieldsOfItsSubject), replicationCellM: 10);
        harness.RunFence = true;

        var sessions = harness.OpenSessions(2, Follow);
        var follower = sessions[0];
        var owner = sessions[1];
        var ids = Spawn(dbe, 0, 2);

        Request(harness, follower, r => r.Follow(ids[0]));
        Assert.That(harness.Sessions.SetControlled(owner, ids[0]), Is.True);

        // Both sessions' frames read on every tick, in one loop: a SELF is sent on the first frame after the control lands and then only when an owner group
        // changes, so reading one session for three ticks and the other for the three after that is reading past it.
        var selves = SelfBlocks(harness, 5, follower, owner);
        var followerSelves = selves[0];
        var ownerSelves = selves[1];
        Assert.Multiple(() =>
        {
            Assert.That(harness.Assembler.TryGetFollowed(follower, out _), Is.True, "the follower is looking from the entity, so it IS following it");
            Assert.That(ownerSelves, Is.GreaterThan(0), "precondition: a controlling session is sent the entity's SELF block, or this test proves nothing");
            Assert.That(followerSelves, Is.Zero, "a session that follows an entity is not that entity, and is sent no SELF block for it");
            Assert.That(Controllers(harness, ids[0]), Does.Not.Contain((int)follower.Slot),
                "the owner-routing map is keyed on control, and that is what keeps a follower out of the owner fields");
            Assert.That(Controllers(harness, ids[0]), Does.Contain((int)owner.Slot), "precondition: the controlling session IS in it");
        });
    }

    /// <summary>
    /// <c>Follow</c> outranks the profile's own anchor while it is set, and <see cref="EntityId.Null"/> gives the profile its anchor back.
    /// </summary>
    [Test]
    public void FollowOverridesTheProfilesOwnAnchorAndReleasingReturnsToIt()
    {
        using var dbe = SetupEngine();
        using var harness = CreateHarness(dbe);
        var session = harness.OpenSessions(1, Follow)[0];
        var ids = Spawn(dbe, 0, 3);

        // Controlling one entity and following another: the viewpoint is the followed one's.
        Assert.That(harness.Sessions.SetControlled(session, ids[0]), Is.True);
        Request(harness, session, r => r.Follow(ids[2]));
        Run(harness, session, 3);
        Assert.That(harness.Assembler.TryGetFollowed(session, out var followed), Is.True);
        Assert.That(followed.X, Is.EqualTo(25).Within(0.5), "entity 2's x, not entity 0's");

        // Released: the profile's declared AroundControlled takes over again, with no other call.
        Request(harness, session, r => r.Follow(EntityId.Null));
        Run(harness, session, 3);
        Assert.Multiple(() =>
        {
            Assert.That(harness.Sessions.FollowedOf(session), Is.EqualTo(EntityId.Null));
            Assert.That(harness.Assembler.TryGetFollowed(session, out var back), Is.True);
            Assert.That(back.X, Is.EqualTo(5).Within(0.5), "entity 0's x: the profile's own anchor is in force again");
        });
    }

    /// <summary>
    /// A followed entity crossing a realm takes its session with it in the same tick, as a controlled one does.
    /// </summary>
    /// <remarks>
    /// <b>Not the same code path as the controlled case, which is why it is its own test.</b> A controlled entity's crossing is noticed through the
    /// owner-routing map — the fence's realm changes are walked against the sessions that control them — and a followed entity is deliberately not in that map.
    /// The crossing has to be noticed the other way, through the set of entities the fence moved.
    /// </remarks>
    [Test]
    [VerifiesRule("SUB-29")]
    public void AFollowedEntityCrossingRealmsTakesItsSessionInTheSameTick()
    {
        using var dbe = SetupEngine();
        using var harness = CreateHarness(dbe);
        var commands = harness.Subscriptions.Commands;
        var session = harness.OpenSessions(1, Follow)[0];
        var ids = Spawn(dbe, 0, 3);

        Request(harness, session, r => r.Follow(ids[1]));
        Run(harness, session, 3);
        Assert.That(commands.RealmOf(session), Is.EqualTo(RealmId.Default), "precondition: it is in its subject's realm");

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Teleport(ids[1], RealmUnit.Pos, new RealmId(1), At(0, 0, 1, 1));
            tx.Commit();
        }

        harness.RunTick(harness.Tick + 1);
        var log = harness.Read(session);
        Assert.Multiple(() =>
        {
            Assert.That(log, Is.Not.Null);
            Assert.That(log.Flags & TickFlags.Reset, Is.EqualTo(TickFlags.Reset));
            Assert.That(log.Calls[1], Is.EqualTo("realm 1"), "one frame, one RESET, the new realm first (SUB-29)");
            Assert.That(commands.RealmOf(session), Is.EqualTo(new RealmId(1)));
        });
    }

    /// <summary>
    /// A followed entity that is destroyed leaves its session where it last saw it and counts a lost follow, as a bound one does (09 § 6).
    /// </summary>
    [Test]
    public void ASessionWhoseFollowedEntityDiesKeepsItsLastViewpoint()
    {
        using var dbe = SetupEngine();
        using var harness = CreateHarness(dbe);
        var session = harness.OpenSessions(1, Follow)[0];
        var ids = Spawn(dbe, 0, 3);

        Request(harness, session, r => r.Follow(ids[1]));
        Run(harness, session, 3);
        Assert.That(harness.Assembler.TryGetFollowed(session, out var before), Is.True);
        var lostBefore = harness.Assembler.BoundLost;

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(ids[1]);
            tx.Commit();
        }

        Run(harness, session, 3);
        Assert.Multiple(() =>
        {
            Assert.That(harness.Assembler.TryGetFollowed(session, out var after), Is.True, "it is still somewhere");
            Assert.That(after.X, Is.EqualTo(before.X).Within(0.001), "and it is where its subject last was, not nowhere and not the origin");
            Assert.That(harness.Assembler.BoundLost, Is.GreaterThan(lostBefore), "a follow the engine could not resolve is counted, not silent");
            Assert.That(harness.Subscriptions.Commands.RealmOf(session), Is.EqualTo(RealmId.Default), "and it keeps the realm it was in");
        });
    }

    /// <summary>
    /// A run-time <c>Follow</c> anchors a session whose profile declared no anchor, and releasing it gives the application its realm back.
    /// </summary>
    /// <remarks>
    /// <b>The condition has to be the engine's whole answer, not the profile's half of it.</b> While a session follows an entity the engine writes its realm
    /// from that entity every tick, so an application calling <c>Enter</c> as well would be the second writer of one field — the thing 12-realms § 1.3 forbids
    /// and the reason <c>Enter</c> throws for an anchored session at all. Reading only the declared profile would answer "not anchored" for a following
    /// session on a <c>World</c> profile and let the two fight, silently, with the engine winning on the next tick.
    /// </remarks>
    [Test]
    [VerifiesRule("SUB-29")]
    public void AFollowAnchorsASessionWhoseProfileDeclaredNoAnchor()
    {
        using var dbe = SetupEngine();
        using var harness = CreateHarness(dbe);
        var commands = harness.Subscriptions.Commands;
        var session = harness.OpenSessions(1, World)[0];
        var ids = Spawn(dbe, 0, 2);

        Assert.That(commands.IsAnchored(session), Is.False, "precondition: a World profile declares no anchor");
        Assert.That(commands.TryEnter(session, new RealmId(1)), Is.True, "precondition: so the application may move it");

        Request(harness, session, r => r.Follow(ids[0]));
        Run(harness, session, 2);
        Assert.Multiple(() =>
        {
            Assert.That(commands.IsAnchored(session), Is.True, "a followed session's realm is the engine's to move");
            Assert.That(commands.TryEnter(session, new RealmId(1)), Is.False, "and the application is answered rather than allowed to fight it");
            Assert.Throws<InvalidOperationException>(() => commands.Enter(session, new RealmId(1)));
            Assert.That(commands.RealmOf(session), Is.EqualTo(RealmId.Default), "the follow won, and the refused Enter moved nothing");
        });

        Request(harness, session, r => r.Follow(EntityId.Null));
        Run(harness, session, 2);
        Assert.Multiple(() =>
        {
            Assert.That(commands.IsAnchored(session), Is.False, "released");
            Assert.That(commands.TryEnter(session, new RealmId(1)), Is.True, "and the application has its realm back");
        });
    }

    /// <summary>
    /// The immediate <c>Follow</c> releases in the same tick, so stopping a follow and placing the session yourself is one tick's work.
    /// </summary>
    /// <remarks>
    /// <b>The staged form cannot express the release, and that is the whole reason the immediate one exists.</b> <c>Session(s).Follow(null)</c> applies at the
    /// next prologue, while <c>Leave</c>/<c>Enter</c>/<c>Place</c> apply now — so an application that asked to stop following and then placed the session in
    /// the same tick met its own follow, still in force, and took a throw on the tick thread. It is the two-phase trap <c>TryEnter</c> was added to close,
    /// met from the other side.
    /// </remarks>
    [Test]
    [VerifiesRule("SUB-29")]
    public void TheImmediateFollowReleasesInTheSameTickTheApplicationPlacesTheSession()
    {
        using var dbe = SetupEngine();
        using var harness = CreateHarness(dbe);
        var commands = harness.Subscriptions.Commands;
        var session = harness.OpenSessions(1, World)[0];
        var ids = Spawn(dbe, 0, 2);

        Assert.That(commands.Follow(session, ids[0]), Is.True, "the immediate Follow is refused");
        Assert.Multiple(() =>
        {
            Assert.That(commands.FollowedOf(session), Is.EqualTo(ids[0]), "it took effect at once, not at the next prologue");
            Assert.That(commands.IsAnchored(session), Is.True);

            // The message must send the reader to the lever that works: this session's profile declares no anchor, so blaming the profile would be a dead end.
            var raised = Assert.Throws<InvalidOperationException>(() => commands.Enter(session, new RealmId(1)));
            Assert.That(raised.Message, Does.Contain("Follow(EntityId.Null)"), "the refusal does not name the way out");
        });

        // Released and placed in one tick, which the staged form cannot do.
        Assert.That(commands.Follow(session, EntityId.Null), Is.True);
        Assert.That(commands.TryEnter(session, new RealmId(1)), Is.True, "released, so the application may move it — in this same tick");
        Run(harness, session, 3);
        Assert.That(commands.RealmOf(session), Is.EqualTo(new RealmId(1)));
    }

    // ── helpers for the two above ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Stages one session request and lets the next prologue apply it, as a system would.</summary>
    private static void Request(FrameHarness harness, SessionId session, Action<SessionRequest> ask)
    {
        ask(harness.Subscriptions.Ingress.Requests.Request(0, session));
        harness.Subscriptions.Ingress.Requests.Apply(harness.Sessions);
    }

    /// <summary>
    /// This tick's <see cref="SessionEventKind.RealmClosed"/> events, as a system reading <c>subs.SessionEvents</c> in Engine-Pre would see them.
    /// </summary>
    /// <remarks>
    /// It only reads. <c>RunTick</c> calls the table's <c>BeginTick</c> itself, exactly where Engine-Pre does, so the batch is already published by the time a
    /// test looks — and publishing it a second time here would swap it straight back out and show an empty batch, which is a test that can only ever pass by
    /// accident.
    /// </remarks>
    private static List<SessionEvent> RealmClosures(FrameHarness harness)
    {
        var events = new List<SessionEvent>();
        foreach (ref readonly var e in harness.Sessions.Events)
        {
            if (e.Kind == SessionEventKind.RealmClosed)
            {
                events.Add(e);
            }
        }

        return events;
    }

    /// <summary>
    /// Unregisters <paramref name="realm"/> and runs the tick whose fence removes it, returning that tick's frame for the session.
    /// </summary>
    /// <remarks>
    /// The removal is a fence, and the notice is the frame stage of the same tick finding the realm gone — so the caller's frame assertions are about this
    /// tick, and its event assertions are about the next one, where the batch is published.
    /// </remarks>
    private static FrameLog RemoveRealm(FrameHarness harness, DatabaseEngine dbe, RealmId realm, SessionId session)
    {
        dbe.Realms.Unregister(realm);
        harness.RunTick(harness.Tick + 1);
        Assert.That(dbe.Realms.IsRegistered(realm), Is.False, "precondition: the fence removed the realm rather than only marking it Closing");
        return harness.Read(session);
    }

    /// <summary>How many SELF blocks each of <paramref name="sessions"/> was sent over the next <paramref name="ticks"/> ticks.</summary>
    /// <remarks>
    /// Summed over several ticks rather than read off one: a SELF block is sent on the first frame after a control lands and then only when an owner group
    /// changes, so which tick carries it is not something a test should pin. Every session is read on every tick, because reading one for a few ticks and then
    /// the next for a few more reads the second one past its only SELF.
    /// </remarks>
    private static int[] SelfBlocks(FrameHarness harness, int ticks, params SessionId[] sessions)
    {
        var seen = new int[sessions.Length];
        for (var i = 0; i < ticks; i++)
        {
            harness.RunTick(harness.Tick + 1);
            for (var s = 0; s < sessions.Length; s++)
            {
                while (harness.Read(sessions[s]) is { } log)
                {
                    seen[s] += log.Selves.Count;
                }
            }
        }

        return seen;
    }

    /// <summary>The session slots the owner-routing map says control <paramref name="entity"/>.</summary>
    private static List<int> Controllers(FrameHarness harness, EntityId entity)
    {
        var self = harness.Subscriptions.Self;
        var slots = new List<int>();
        if (self == null)
        {
            return slots;
        }

        self.Refresh(harness.Sessions);
        for (var slot = self.FirstControlling((ulong)entity.RawValue); slot >= 0; slot = self.NextControlling(slot))
        {
            slots.Add(slot);
        }

        return slots;
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
