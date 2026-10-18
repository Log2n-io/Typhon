using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime.Subscriptions.Oracle;

/// <summary>
/// An event routed to the sessions that know an entity (09 § 11) reaches the sessions holding it through a group (14 § 3): a member beyond the Sphere, a
/// positionless one, one that left its town or died in the event's tick — and, under the enter budget, exactly the members the fill has reached.
/// </summary>
[TestFixture]
[NonParallelizable]
class PushGroupEventTests : TestBase<PushGroupEventTests>
{
    private const double Radius = 120d;
    private const double Reach = 80d;
    private const double Margin = 20d;
    private const int TownKey = 7;
    private const int OtherKey = 9;

    private static SubscriptionsOptions Options(int enterBudget) => new()
    {
        IngressBytesPerSecond = TestIngress.Budget,
        MaxSessions = 16,
        ReplicationCellM = ProjectionTestSchema.ReplicationCellFor(Radius),
        MaxGroupsPerSession = 8,
        EnterBudgetPerFrame = enterBudget,
        PushShadow = true,
        FrameBytes = 8 * 1024,
        ReliableEventBytes = 1 << 20,
    };

    private static void Declare(SubscriptionsRegistry subs)
    {
        GroupTestSchema.Declare(subs, Radius, Reach, Margin, shards: true);
        subs.Event<ProjDuel>(e => e.RouteToKnown(d => d.A, d => d.B));
        subs.Event<RelDuel>(e => e.RouteToKnown(d => d.A).Reliable());
    }

    private FrameHarness Harness(DatabaseEngine dbe, string name, int enterBudget = 500)
    {
        var harness = FrameHarness.Create(dbe, Declare, name, Options(enterBudget));
        harness.RunFence = true;
        harness.DrainNetIds = true;
        return harness;
    }

    private static void Towns(DatabaseEngine dbe)
    {
        using var tx = dbe.CreateQuickTransaction();
        tx.Spawn<GroupTown>(GroupTown.Zone.Set(new GroupZone { Key = TownKey, Extent = GroupTestSchema.Square(0f, 0f, 60f) }));
        tx.Spawn<GroupTown>(GroupTown.Zone.Set(new GroupZone { Key = OtherKey, Extent = GroupTestSchema.Square(5000f, 0f, 60f) }));
        tx.Commit();
    }

    // Far beyond the Sphere: held through the town alone.
    private static List<EntityId> Buildings(DatabaseEngine dbe, int count, float x = 600f)
    {
        var ids = new List<EntityId>();
        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < count; i++)
        {
            ids.Add(tx.Spawn<GroupBuilding>(GroupBuilding.Bounds.Set(GroupTestSchema.At(x + (i * 6f), 300f)), GroupBuilding.Ai.Set(new ProjAi { Template = 1 }),
                GroupBuilding.Key.Set(new GroupKey { Town = TownKey })));
        }

        tx.Commit();
        return ids;
    }

    /// <summary>A holder of the town at its centre, and a session 3 km away holding nothing.</summary>
    private static SessionId[] Open(FrameHarness harness)
    {
        var sessions = harness.OpenSessions(2, GroupTestSchema.Profile);
        Assert.That(harness.Sessions.SetViewpoint(sessions[0], new Vector3D(0d, 0d, 0d)), Is.True);
        Assert.That(harness.Sessions.SetViewpoint(sessions[1], new Vector3D(3000d, 3000d, 0d)), Is.True);
        return sessions;
    }

    private static void Run(FrameHarness harness, ref long tick, int ticks, params SessionId[] sessions)
    {
        for (var t = 0; t < ticks; t++)
        {
            tick++;
            harness.RunTick(tick);
            foreach (var session in sessions)
            {
                harness.Deliver(session);
            }
        }
    }

    private static int[] Heard(FrameHarness harness, SessionId session) =>
        harness.Replica(session).Events.Received.Select(e => (int)e.Fields["Seq"]).ToArray();

    private static void Clean(FrameHarness harness, params SessionId[] sessions) => Assert.Multiple(() =>
    {
        foreach (var session in sessions)
        {
            Assert.That(harness.Replica(session).Store.Anomalies, Is.Zero);
        }

        Assert.That(harness.Subscriptions.Events.Rejected, Is.Zero);
        Assert.That(harness.Subscriptions.Push.ShadowIllegal, Is.Zero);
    });

    /// <summary>A building 600 m away, held through the town, and a positionless shard of it: their events reach the town's holder, and no one else.</summary>
    [Test]
    [VerifiesRule("SUB-21")]
    public void AnEventNamingAGroupMemberReachesTheSessionsHoldingItsKey()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        Towns(dbe);
        var building = Buildings(dbe, 1)[0];
        EntityId shard;
        using (var tx = dbe.CreateQuickTransaction())
        {
            shard = tx.Spawn<GroupPositionless>(GroupPositionless.Ai.Set(new ProjAi { Template = 2 }),
                GroupPositionless.Key.Set(new GroupKey { Town = TownKey }));
            tx.Commit();
        }

        using var harness = Harness(dbe, nameof(AnEventNamingAGroupMemberReachesTheSessionsHoldingItsKey));
        var sessions = Open(harness);
        var tick = 0L;
        Run(harness, ref tick, 4, sessions);
        Assert.That(harness.Replica(sessions[0]).NetIds(harness.PlanIndex(nameof(GroupBuilding))), Does.Contain(harness.NetIdOf(building)));

        var commands = harness.Subscriptions.Commands;
        commands.Emit(new ProjDuel { A = building, B = building, Seq = 1 });
        commands.Emit(new ProjDuel { A = shard, B = shard, Seq = 2 });
        Run(harness, ref tick, 1, sessions);

        var heard = harness.Replica(sessions[0]).Events.Received;
        Assert.Multiple(() =>
        {
            Assert.That(heard.Select(e => (int)e.Fields["Seq"]), Is.EqualTo(new[] { 1, 2 }), "the building beyond the Sphere and the shard with no position");
            Assert.That(heard[0].Fields["A"], Is.EqualTo(harness.NetIdOf(building)));
            Assert.That(heard[1].Fields["A"], Is.EqualTo(harness.NetIdOf(shard)));
            Assert.That(Heard(harness, sessions[1]), Is.Empty, "the far session holds neither");
        });
        Clean(harness, sessions);
    }

    /// <summary>
    /// Was ∨ is, as for the cells: a member moved to a town nobody holds, and a member destroyed, in the event's own tick, are still heard of by the session
    /// that held them — the frame that tells it they left.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-21")]
    public void AMemberThatLeftItsTownOrDiedInTheEventsTickIsStillHeardOf()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        Towns(dbe);
        var buildings = Buildings(dbe, 2);
        using var harness = Harness(dbe, nameof(AMemberThatLeftItsTownOrDiedInTheEventsTickIsStillHeardOf));
        var sessions = Open(harness);
        var tick = 0L;
        Run(harness, ref tick, 4, sessions);
        var plan = harness.PlanIndex(nameof(GroupBuilding));
        Assert.That(harness.Replica(sessions[0]).NetIds(plan), Has.Length.EqualTo(2));

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(buildings[0]).Set(GroupBuilding.Key, new GroupKey { Town = OtherKey });
            tx.Destroy(buildings[1]);
            tx.Commit();
        }

        var commands = harness.Subscriptions.Commands;
        commands.Emit(new ProjDuel { A = buildings[0], B = buildings[0], Seq = 1 });
        commands.Emit(new ProjDuel { A = buildings[1], B = buildings[1], Seq = 2 });
        Run(harness, ref tick, 1, sessions);

        Assert.Multiple(() =>
        {
            Assert.That(Heard(harness, sessions[0]), Is.EqualTo(new[] { 1, 2 }), "the moved one and the dead one");
            Assert.That(harness.Replica(sessions[0]).NetIds(plan), Is.Empty, "both left in the same frame");
            Assert.That(Heard(harness, sessions[1]), Is.Empty);
        });
        Clean(harness, sessions);
    }

    /// <summary>
    /// The other side of was ∨ is: the town itself released in the event's tick — its region destroyed — takes its members with it, and the event naming
    /// one of them reaches the session that held them until this frame.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-21")]
    public void AMemberOfATownReleasedInTheEventsTickIsStillHeardOf()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        EntityId town;
        using (var tx = dbe.CreateQuickTransaction())
        {
            town = tx.Spawn<GroupTown>(GroupTown.Zone.Set(new GroupZone { Key = TownKey, Extent = GroupTestSchema.Square(0f, 0f, 60f) }));
            tx.Commit();
        }

        var building = Buildings(dbe, 1)[0];
        using var harness = Harness(dbe, nameof(AMemberOfATownReleasedInTheEventsTickIsStillHeardOf));
        var sessions = Open(harness);
        var tick = 0L;
        Run(harness, ref tick, 4, sessions);
        var plan = harness.PlanIndex(nameof(GroupBuilding));
        Assert.That(harness.Replica(sessions[0]).NetIds(plan), Has.Length.EqualTo(1));

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(town);
            tx.Commit();
        }

        harness.Subscriptions.Commands.Emit(new ProjDuel { A = building, B = building, Seq = 3 });
        Run(harness, ref tick, 1, sessions);
        Assert.Multiple(() =>
        {
            Assert.That(harness.Replica(sessions[0]).NetIds(plan), Is.Empty, "released with its town");
            Assert.That(Heard(harness, sessions[0]), Is.EqualTo(new[] { 3 }), "held until this frame");
        });
        Clean(harness, sessions);
    }

    /// <summary>
    /// An event naming a building held through the town and a citizen inside the Sphere — both reaching the session, by key and by cell — is heard once.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-21")]
    public void AnEventReachingASessionByKeyAndByCellIsHeardOnce()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        Towns(dbe);
        var building = Buildings(dbe, 1)[0];
        EntityId citizen;
        using (var tx = dbe.CreateQuickTransaction())
        {
            citizen = tx.Spawn<GroupCitizen>(GroupCitizen.Bounds.Set(GroupTestSchema.At(10f, 0f)), GroupCitizen.Ai.Set(new ProjAi { Template = 1 }),
                GroupCitizen.Key.Set(new GroupKey { Town = 0 }));
            tx.Commit();
        }

        using var harness = Harness(dbe, nameof(AnEventReachingASessionByKeyAndByCellIsHeardOnce));
        var sessions = Open(harness);
        var tick = 0L;
        Run(harness, ref tick, 4, sessions);
        harness.Subscriptions.Commands.Emit(new ProjDuel { A = building, B = citizen, Seq = 4 });
        Run(harness, ref tick, 1, sessions);
        Assert.That(Heard(harness, sessions[0]), Is.EqualTo(new[] { 4 }));
    }

    /// <summary>
    /// The tick a session comes to hold a town is the tick it may hear of its members: the event of that tick names a member its frame enters, and is heard.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-21")]
    public void AnEventOfTheTickATownIsMetIsHeardWithTheEnter()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        Towns(dbe);
        var building = Buildings(dbe, 1)[0];
        using var harness = Harness(dbe, nameof(AnEventOfTheTickATownIsMetIsHeardWithTheEnter));
        var sessions = Open(harness);
        var tick = 0L;
        Run(harness, ref tick, 3, sessions);
        Assert.That(harness.Replica(sessions[1]).NetIds(harness.PlanIndex(nameof(GroupBuilding))), Is.Empty);

        // The far session walks in — a teleport, which resets it into the town — and the event of the same tick names the building.
        Assert.That(harness.Sessions.SetViewpoint(sessions[1], new Vector3D(0d, 0d, 0d)), Is.True);
        harness.Subscriptions.Commands.Emit(new ProjDuel { A = building, B = building, Seq = 5 });
        Run(harness, ref tick, 1, sessions);
        Assert.Multiple(() =>
        {
            Assert.That(harness.Replica(sessions[1]).NetIds(harness.PlanIndex(nameof(GroupBuilding))), Does.Contain(harness.NetIdOf(building)));
            Assert.That(Heard(harness, sessions[1]), Is.EqualTo(new[] { 5 }), "held in the frame that carries the event");
        });
    }

    /// <summary>
    /// A reliable event is judged once (SUB-35): a burst naming a building held through the town, larger than a frame, is delivered whole and in order even
    /// though the session leaves the town while the backlog drains — the events were routed to it when they were emitted.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-35")]
    public void AReliableBacklogNamingAMemberIsDeliveredAfterTheTownIsLeft()
    {
        const int Burst = 1500;
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        Towns(dbe);
        var building = Buildings(dbe, 1)[0];
        using var harness = Harness(dbe, nameof(AReliableBacklogNamingAMemberIsDeliveredAfterTheTownIsLeft));
        var sessions = Open(harness);
        var tick = 0L;
        Run(harness, ref tick, 4, sessions);

        var commands = harness.Subscriptions.Commands;
        for (var i = 0; i < Burst; i++)
        {
            Assert.That(commands.Emit(new RelDuel { A = building, Seq = i }), Is.True);
        }

        Run(harness, ref tick, 1, sessions);
        var first = Heard(harness, sessions[0]).Length;
        Assert.That(first, Is.InRange(1, Burst - 1), "the burst does not fit one frame");

        // Gone 3 km away: the town is released, and with it the building.
        Assert.That(harness.Sessions.SetViewpoint(sessions[0], new Vector3D(3000d, -3000d, 0d)), Is.True);
        Run(harness, ref tick, 20, sessions);
        Assert.Multiple(() =>
        {
            Assert.That(harness.Replica(sessions[0]).NetIds(harness.PlanIndex(nameof(GroupBuilding))), Is.Empty, "the town was left");
            Assert.That(Heard(harness, sessions[0]), Is.EqualTo(Enumerable.Range(0, Burst)), "every one, once, in order");
            Assert.That(Heard(harness, sessions[1]), Is.Empty, "the far session held nothing when they were emitted");
        });
    }

    /// <summary>
    /// A town filling under the enter budget: an event naming each of its members reaches the session for exactly the members its frame holds — the fill's
    /// cursor, not the key alone, says who is known.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-21")]
    public void DuringAFillOnlyTheMembersAlreadyEnteredAreHeardOf()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        Towns(dbe);
        var buildings = Buildings(dbe, 30);
        using var harness = Harness(dbe, nameof(DuringAFillOnlyTheMembersAlreadyEnteredAreHeardOf), enterBudget: 7);
        var sessions = Open(harness);
        var tick = 0L;
        Run(harness, ref tick, 1, sessions);
        var plan = harness.PlanIndex(nameof(GroupBuilding));
        Assert.That(harness.Replica(sessions[0]).NetIds(plan).Length, Is.InRange(1, 29), "the town is filling");

        var commands = harness.Subscriptions.Commands;
        for (var i = 0; i < buildings.Count; i++)
        {
            commands.Emit(new ProjDuel { A = buildings[i], B = buildings[i], Seq = i });
        }

        Run(harness, ref tick, 1, sessions);
        var held = harness.Replica(sessions[0]).NetIds(plan);
        var named = harness.Replica(sessions[0]).Events.Received.Select(e => (uint)e.Fields["A"]).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(held.Length, Is.InRange(1, 29), "still filling after the events' frame");
            Assert.That(named, Is.EquivalentTo(held), "every member the frame holds, and none it does not");
        });
        Clean(harness, sessions);
    }
}
