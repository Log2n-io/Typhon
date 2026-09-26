using System;
using System.Linq;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Tests.Runtime;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Realms;

#pragma warning disable CS0649
/// <summary>A point event in one realm.</summary>
public struct RealmBoom
{
    public float X;
    public float Y;
    public ushort R;
    public int Seq;
}

/// <summary>An announcement to a realm, and to the realms below it.</summary>
public struct RealmAnnounce
{
    public ushort R;
    public int Seq;
}

/// <summary>An announcement to one realm only.</summary>
public struct RealmNotice
{
    public ushort R;
    public int Seq;
}

/// <summary>An event about an entity, heard where it is known.</summary>
public struct RealmDuel
{
    public EntityId A;
    public int Seq;
}
#pragma warning restore CS0649

/// <summary>
/// Realms R4.7 (12-realms § 3): events are realm-scoped by route. A <c>Near</c> point and a <c>ToKnown</c> entity are filed in their realm, by its cells,
/// and match only that realm's sessions — at identical local coordinates in another realm, nothing; <c>ToRealm</c> reaches a realm, or its subtree in the
/// parent tree, which routes and grants no visibility.
/// </summary>
[TestFixture]
[NonParallelizable]
class RealmEventTests : TestBase<RealmEventTests>
{
    private const string World = "world";

    private static SpatialGridConfig Grid0() => SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(100, 100), 10);

    private static SpatialGridConfig Grid1() => SpatialGridConfig.Flat(new Vector2(-40, -40), new Vector2(40, 40), 10);

    private static RealmConfig Replicated(RealmId parent = default, bool hasParent = false) => new()
    {
        Grid = Grid1(),
        WhenUnobserved = RealmUnobserved.Simulate,
        UnobservedTickDivisor = 1,
        Parent = hasParent ? parent : RealmId.None,
        Replication = new RealmReplicationConfig { CellM = 10 },
    };

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<RealmPos>();
        dbe.ConfigureRealms(4);
        dbe.ConfigureSpatialGrid(Grid0());
        dbe.InitializeArchetypes();
        dbe.Realms.Register(new RealmId(1), Replicated());

        // Realm 2 is below realm 1 in the parent tree: an interior of a planet.
        dbe.Realms.Register(new RealmId(2), Replicated(new RealmId(1), hasParent: true));
        return dbe;
    }

    private static FrameHarness CreateHarness(DatabaseEngine dbe)
    {
        var harness = FrameHarness.Create(dbe, subs =>
        {
            subs.Archetype<RealmUnit>(a => a.Motion(RealmUnit.Pos, m => m.Teleport(20)));
            subs.Profile(World, p => p.World().Of<RealmUnit>());
            subs.Event<RealmBoom>(e => e.RouteNear(b => new Vector3D(b.X, b.Y, 0d), b => new RealmId(b.R)));
            subs.Event<RealmAnnounce>(e => e.RouteToRealm(a => new RealmId(a.R), subtree: true));
            subs.Event<RealmNotice>(e => e.RouteToRealm(n => new RealmId(n.R)));
            subs.Event<RealmDuel>(e => e.RouteToKnown(d => d.A));
        }, nameof(RealmEventTests), replicationCellM: 10);
        harness.RunFence = true;
        return harness;
    }

    private static EntityId Spawn(DatabaseEngine dbe, ushort realm, float x, float y)
    {
        using var tx = dbe.CreateQuickTransaction();
        var id = tx.Spawn<RealmUnit>(RealmUnit.Pos.Set(new RealmPos
        {
            Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Realm = realm,
        }));
        tx.Commit();
        return id;
    }

    private static int[] Seqs(FrameHarness harness, SessionId session, string name) =>
        harness.Replica(session).Events.Received.Where(e => e.Name == name).Select(e => (int)e.Fields["Seq"]).ToArray();

    private static SessionId[] OpenIn(FrameHarness harness, params ushort[] realms)
    {
        var sessions = harness.OpenSessions(realms.Length, World);
        for (var i = 0; i < realms.Length; i++)
        {
            Assert.That(harness.Subscriptions.Commands.Enter(sessions[i], new RealmId(realms[i])), Is.True);
        }

        return sessions;
    }

    private static void Run(FrameHarness harness, SessionId[] sessions, int ticks)
    {
        for (var t = 0; t < ticks; t++)
        {
            harness.RunTick(harness.Tick + 1);
            foreach (var session in sessions)
            {
                harness.Deliver(session);
            }
        }
    }

    [Test]
    [VerifiesRule("SUB-28")]
    public void ANearEventIsHeardInItsRealmOnly_AtIdenticalLocalCoordinates()
    {
        using var dbe = SetupEngine();
        using var harness = CreateHarness(dbe);
        var sessions = OpenIn(harness, 0, 1);
        Spawn(dbe, 0, 25, 25);
        Spawn(dbe, 1, 25, 25);
        Run(harness, sessions, 3);

        // The same point, (25, 25), in realm 1: only realm 1's session is near it.
        harness.Subscriptions.Commands.Emit(new RealmBoom { X = 25, Y = 25, R = 1, Seq = 11 });
        Run(harness, sessions, 1);
        harness.Subscriptions.Commands.Emit(new RealmBoom { X = 25, Y = 25, R = 0, Seq = 12 });
        Run(harness, sessions, 1);

        Assert.Multiple(() =>
        {
            Assert.That(Seqs(harness, sessions[0], nameof(RealmBoom)), Is.EqualTo(new[] { 12 }));
            Assert.That(Seqs(harness, sessions[1], nameof(RealmBoom)), Is.EqualTo(new[] { 11 }));
        });
    }

    [Test]
    [VerifiesRule("SUB-28")]
    public void AToKnownEventIsFiledInItsEntitysRealm()
    {
        using var dbe = SetupEngine();
        using var harness = CreateHarness(dbe);
        var sessions = OpenIn(harness, 0, 1);
        Spawn(dbe, 0, 25, 25);
        var inOne = Spawn(dbe, 1, 25, 25);
        Run(harness, sessions, 3);

        harness.Subscriptions.Commands.Emit(new RealmDuel { A = inOne, Seq = 21 });
        Run(harness, sessions, 1);

        Assert.Multiple(() =>
        {
            Assert.That(Seqs(harness, sessions[0], nameof(RealmDuel)), Is.Empty, "realm 0's session does not know an entity of realm 1");
            Assert.That(Seqs(harness, sessions[1], nameof(RealmDuel)), Is.EqualTo(new[] { 21 }));
        });
    }

    [Test]
    public void ARealmAnnouncementReachesTheRealm_AndWithItsSubtreeTheRealmsBelow()
    {
        using var dbe = SetupEngine();
        using var harness = CreateHarness(dbe);
        var sessions = OpenIn(harness, 0, 1, 2);
        Spawn(dbe, 1, 5, 5);
        Run(harness, sessions, 3);

        harness.Subscriptions.Commands.Emit(new RealmAnnounce { R = 1, Seq = 31 });
        harness.Subscriptions.Commands.Emit(new RealmNotice { R = 1, Seq = 32 });
        Run(harness, sessions, 1);

        Assert.Multiple(() =>
        {
            Assert.That(Seqs(harness, sessions[0], nameof(RealmAnnounce)), Is.Empty, "realm 0 is not below realm 1");
            Assert.That(Seqs(harness, sessions[1], nameof(RealmAnnounce)), Is.EqualTo(new[] { 31 }));
            Assert.That(Seqs(harness, sessions[2], nameof(RealmAnnounce)), Is.EqualTo(new[] { 31 }), "realm 2's parent is realm 1");
            Assert.That(Seqs(harness, sessions[1], nameof(RealmNotice)), Is.EqualTo(new[] { 32 }));
            Assert.That(Seqs(harness, sessions[2], nameof(RealmNotice)), Is.Empty, "without its subtree, the realm alone");
        });
    }

    [Test]
    public void ARealmLessNearRouteIsRefusedWithSeveralRealms()
    {
        using var dbe = SetupEngine();
        Assert.That(() => FrameHarness.Create(dbe, subs =>
            {
                subs.Archetype<RealmUnit>(a => a.Motion(RealmUnit.Pos, m => m.Teleport(20)));
                subs.Profile(World, p => p.World().Of<RealmUnit>());
                subs.Event<RealmBoom>(e => e.RouteNear(b => new Vector3D(b.X, b.Y, 0d)));
            }, nameof(ARealmLessNearRouteIsRefusedWithSeveralRealms), replicationCellM: 10),
            Throws.InstanceOf<NotSupportedException>().With.Message.Contains("RouteNear(point, realm)"));
    }
}
