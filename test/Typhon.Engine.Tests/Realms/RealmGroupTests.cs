using System.Linq;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Typhon.Engine.Internals;
using NUnit.Framework;
using Typhon.Engine.Tests.Runtime;
using Typhon.Engine.Tests.Runtime.Subscriptions.Oracle;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Realms;

/// <summary>A region with a place in a realm: a town, its key and extent, in the realm its position's key names.</summary>
[Archetype]
partial class RealmTown : Archetype<RealmTown>
{
    public static readonly Comp<RealmPos> Pos = Register<RealmPos>();
    public static readonly Comp<GroupZone> Zone = Register<GroupZone>();
}

/// <summary>A positioned group member with a place in a realm.</summary>
[Archetype]
partial class RealmResident : Archetype<RealmResident>
{
    public static readonly Comp<RealmPos> Pos = Register<RealmPos>();
    public static readonly Comp<GroupKey> Key = Register<GroupKey>();
    public static readonly Comp<GroupTag> Tag = Register<GroupTag>();
}

/// <summary>
/// Positionless members and realms (14 § 5): a shard is in no realm — it has no position, so no cluster realm map — and is held by every session that holds
/// its town, whichever realm that session and the town's region are in (SUB-28 for the regions, SUB-33 for the members).
/// </summary>
[TestFixture]
[NonParallelizable]
class RealmGroupTests : TestBase<RealmGroupTests>
{
    private const string Profile = "p";

    private static SpatialGridConfig Realm0Grid() => SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(100, 100), 10);

    private static SpatialGridConfig Realm1Grid() => SpatialGridConfig.Flat(new Vector2(-40, -40), new Vector2(40, 40), 10);

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<RealmPos>();
        dbe.RegisterComponentFromAccessor<GroupZone>();
        dbe.RegisterComponentFromAccessor<GroupKey>();
        dbe.RegisterComponentFromAccessor<GroupListKey>();
        dbe.RegisterComponentFromAccessor<GroupTag>();
        dbe.RegisterComponentFromAccessor<ProjAi>();
        dbe.ConfigureRealms(4);
        dbe.ConfigureSpatialGrid(Realm0Grid());
        dbe.InitializeArchetypes();
        dbe.Realms.Register(new RealmId(1), new RealmConfig
        {
            Grid = Realm1Grid(), WhenUnobserved = RealmUnobserved.Simulate, UnobservedTickDivisor = 1,
            Replication = new RealmReplicationConfig { CellM = 10 },
        });
        return dbe;
    }

    private static void Town(DatabaseEngine dbe, ushort realm, int key, float x, float y)
    {
        using var tx = dbe.CreateQuickTransaction();
        tx.Spawn<RealmTown>(RealmTown.Pos.Set(new RealmPos { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Realm = realm }),
            RealmTown.Zone.Set(new GroupZone { Key = key, Extent = GroupTestSchema.Square(x, y, 10f) }));
        tx.Commit();
    }

    // One transaction, interleaved: both towns' shards share a cluster, so a block attached for one town describes the other's too.
    private static EntityId[] Shards(DatabaseEngine dbe)
    {
        var ids = new EntityId[7];
        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < 7; i++)
        {
            var key = i % 2 == 0 ? 7 : 8;
            ids[i] = tx.Spawn<GroupPositionless>(GroupPositionless.Ai.Set(new ProjAi { Template = (byte)(1 + i) }),
                GroupPositionless.Key.Set(new GroupKey { Town = key }));
        }

        tx.Commit();
        return ids;
    }

    /// <summary>
    /// A realm arrival's entry is carried over from the realm it left, mask included: a hidden member made public in the tick it crosses is stored public
    /// in its new realm, where a session holding its town now holds it (14 § 6) — not kept hidden by the carried mask.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-34")]
    [VerifiesRule("SUB-28")]
    public void AMaskClearedAsAMemberCrossesRealmsIsTheOneItsNewRealmJudges()
    {
        using var dbe = SetupEngine();
        Town(dbe, 0, 8, 50f, 50f);
        EntityId resident;
        using (var tx = dbe.CreateQuickTransaction())
        {
            resident = tx.Spawn<RealmResident>(
                RealmResident.Pos.Set(new RealmPos { Bounds = new AABB2F { MinX = 0, MinY = 0, MaxX = 0, MaxY = 0 }, Realm = 1 }),
                RealmResident.Key.Set(new GroupKey { Town = 8 }), RealmResident.Tag.Set(new GroupTag { Mask = 4 }));

            // A public neighbour where it will land: its cluster has a block in realm 0, so the arrival's entry is carried there, not made anew.
            tx.Spawn<RealmResident>(RealmResident.Pos.Set(new RealmPos { Bounds = new AABB2F { MinX = 90, MinY = 90, MaxX = 90, MaxY = 90 }, Realm = 0 }),
                RealmResident.Key.Set(new GroupKey { Town = 8 }), RealmResident.Tag.Set(new GroupTag { Mask = 0 }));
            tx.Commit();
        }

        using var harness = FrameHarness.Create(dbe, subs =>
        {
            subs.Archetype<RealmResident>(a => a.Motion(RealmResident.Pos, m => m.Teleport(200)).Visibility(RealmResident.Tag, t => t.Mask));
            subs.Groups("town", g => g.Region<RealmTown>(r => r.Key(RealmTown.Zone, z => z.Key).Extent(RealmTown.Zone, z => z.Extent))
                .Member<RealmResident>(m => m.Key(RealmResident.Key, k => k.Town)));
            subs.Profile(Profile, p => p.Sphere(30).Groups("town", 5));
        }, nameof(AMaskClearedAsAMemberCrossesRealmsIsTheOneItsNewRealmJudges), new SubscriptionsOptions
        {
            IngressBytesPerSecond = TestIngress.Budget,
            MaxGroupsPerSession = 4,
            ReplicationCellM = 10,
        });
        harness.RunFence = true;

        // A realm 1 session keeps realm 1 served, so the resident has an entry there to carry over.
        var sessions = harness.OpenSessions(2, Profile);
        var commands = harness.Subscriptions.Commands;
        Assert.That(commands.Enter(sessions[0], RealmId.Default), Is.True);
        Assert.That(commands.Enter(sessions[1], new RealmId(1)), Is.True);
        harness.Sessions.SetViewpoint(sessions[0], new Vector3D(50d, 50d, 0d));
        harness.Sessions.SetViewpoint(sessions[1], new Vector3D(0d, 0d, 0d));
        for (var t = 0; t < 6; t++)
        {
            harness.RunTick(harness.Tick + 1);
            harness.Deliver(sessions[0]);
            harness.Deliver(sessions[1]);
        }

        var plan = harness.PlanIndex(nameof(RealmResident));
        Assert.That(harness.NetIdOf(resident), Is.Not.Zero, "it has an entry in realm 1");

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Teleport(resident, RealmResident.Pos, RealmId.Default, new RealmPos { Bounds = new AABB2F { MinX = 90, MinY = 90, MaxX = 90, MaxY = 90 } });
            var e = tx.OpenMut(resident);
            e.Set(RealmResident.Tag, new GroupTag { Mask = 0 });
            commands.Replicate(in e);
            tx.Commit();
        }

        for (var t = 0; t < 4; t++)
        {
            harness.RunTick(harness.Tick + 1);
            harness.Deliver(sessions[0]);
            harness.Deliver(sessions[1]);
        }

        Assert.Multiple(() =>
        {
            Assert.That(harness.Replica(sessions[0]).NetIds(plan), Does.Contain(harness.NetIdOf(resident)), "public now, a member of town 8: held");
            Assert.That(harness.Replica(sessions[0]).NetIds(plan), Has.Length.EqualTo(2));
            Assert.That(harness.Replica(sessions[0]).Store.Anomalies + harness.Replica(sessions[1]).Store.Anomalies, Is.Zero);
        });
    }

    /// <summary>
    /// A member that crosses into another realm in the event's tick is still heard of by the sessions of the realm it left, which held it before the frame:
    /// its old key is filed in that realm, where the crossing's leave recorded it (SUB-21's was ∨ is, SUB-28).
    /// </summary>
    [Test]
    [VerifiesRule("SUB-21")]
    [VerifiesRule("SUB-28")]
    public void AMemberCrossingRealmsInTheEventsTickIsHeardInTheRealmItLeft()
    {
        using var dbe = SetupEngine();
        Town(dbe, 1, 7, 0f, 0f);
        Town(dbe, 0, 8, 50f, 50f);
        EntityId resident;
        using (var tx = dbe.CreateQuickTransaction())
        {
            resident = tx.Spawn<RealmResident>(
                RealmResident.Pos.Set(new RealmPos { Bounds = new AABB2F { MinX = 35, MinY = -35, MaxX = 35, MaxY = -35 }, Realm = 1 }),
                RealmResident.Key.Set(new GroupKey { Town = 7 }));
            tx.Commit();
        }

        using var harness = FrameHarness.Create(dbe, subs =>
        {
            subs.Archetype<RealmResident>(a => a.Motion(RealmResident.Pos, m => m.Teleport(200)));
            subs.Groups("town", g => g.Region<RealmTown>(r => r.Key(RealmTown.Zone, z => z.Key).Extent(RealmTown.Zone, z => z.Extent))
                .Member<RealmResident>(m => m.Key(RealmResident.Key, k => k.Town)));
            subs.Profile(Profile, p => p.Sphere(30).Groups("town", 5));
            subs.Event<ProjDuel>(e => e.RouteToKnown(d => d.A, d => d.B));
        }, nameof(AMemberCrossingRealmsInTheEventsTickIsHeardInTheRealmItLeft), new SubscriptionsOptions
        {
            IngressBytesPerSecond = TestIngress.Budget,
            MaxGroupsPerSession = 4,
            ReplicationCellM = 10,
        });
        harness.RunFence = true;

        var sessions = harness.OpenSessions(2, Profile);
        var commands = harness.Subscriptions.Commands;
        Assert.That(commands.Enter(sessions[0], new RealmId(1)), Is.True);
        Assert.That(commands.Enter(sessions[1], RealmId.Default), Is.True);
        harness.Sessions.SetViewpoint(sessions[0], new Vector3D(0d, 0d, 0d));
        harness.Sessions.SetViewpoint(sessions[1], new Vector3D(50d, 50d, 0d));
        for (var t = 0; t < 6; t++)
        {
            harness.RunTick(harness.Tick + 1);
            harness.Deliver(sessions[0]);
            harness.Deliver(sessions[1]);
        }

        var plan = harness.PlanIndex(nameof(RealmResident));
        var inRealm1 = harness.Replica(sessions[0]);
        Assert.That(inRealm1.NetIds(plan), Has.Length.EqualTo(1), "held through town 7, beyond the Sphere");

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Teleport(resident, RealmResident.Pos, RealmId.Default, new RealmPos { Bounds = new AABB2F { MinX = 80, MinY = 80, MaxX = 80, MaxY = 80 } });
            tx.Commit();
        }

        commands.Emit(new ProjDuel { A = resident, B = resident, Seq = 9 });
        harness.RunTick(harness.Tick + 1);
        harness.Deliver(sessions[0]);
        harness.Deliver(sessions[1]);
        Assert.Multiple(() =>
        {
            Assert.That(inRealm1.Events.Received.Select(e => e.Fields["Seq"]), Is.EqualTo(new[] { 9d }), "realm 1's session held it until this frame");
            Assert.That(inRealm1.NetIds(plan), Is.Empty, "and is told it left");
            Assert.That(harness.Replica(sessions[1]).Events.Received, Is.Empty, "realm 0's session holds no town 7");
        });
    }

    /// <summary>
    /// A POSITIONED member's key is realm-scoped, unlike a positionless one's: two realms each with a town 8 (one map, spawned once per planet) are two
    /// groups. A session holds, completes and hears of only its own realm's residents — the other realm's are neither held nor waited for, so its town
    /// completes — though one index lists both realms' residents under the key.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-33")]
    [VerifiesRule("SUB-28")]
    public void APositionedMemberIsHeldOnlyInItsRealmUnderAKeyBothRealmsUse()
    {
        using var dbe = SetupEngine();
        Town(dbe, 0, 8, 50f, 50f);
        Town(dbe, 1, 8, 0f, 0f);
        var inZero = new EntityId[3];
        var inOne = new EntityId[2];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < inZero.Length; i++)
            {
                inZero[i] = tx.Spawn<RealmResident>(
                    RealmResident.Pos.Set(new RealmPos { Bounds = new AABB2F { MinX = 48 + i, MinY = 50, MaxX = 48 + i, MaxY = 50 }, Realm = 0 }),
                    RealmResident.Key.Set(new GroupKey { Town = 8 }), RealmResident.Tag.Set(new GroupTag()));
            }

            for (var i = 0; i < inOne.Length; i++)
            {
                inOne[i] = tx.Spawn<RealmResident>(
                    RealmResident.Pos.Set(new RealmPos { Bounds = new AABB2F { MinX = i, MinY = 0, MaxX = i, MaxY = 0 }, Realm = 1 }),
                    RealmResident.Key.Set(new GroupKey { Town = 8 }), RealmResident.Tag.Set(new GroupTag()));
            }

            tx.Commit();
        }

        using var harness = FrameHarness.Create(dbe, subs =>
        {
            subs.Archetype<RealmResident>(a => a.Motion(RealmResident.Pos, m => m.Teleport(200)));
            subs.Groups("town", g => g.Region<RealmTown>(r => r.Key(RealmTown.Zone, z => z.Key).Extent(RealmTown.Zone, z => z.Extent))
                .Member<RealmResident>(m => m.Key(RealmResident.Key, k => k.Town)));
            // Groups only: no resident is held by geometry, so whatever a session holds, its town gave it.
            subs.Profile(Profile, p => p.Sphere(30).Groups("town", 5));
            subs.Event<ProjDuel>(e => e.RouteToKnown(d => d.A, d => d.B));
        }, nameof(APositionedMemberIsHeldOnlyInItsRealmUnderAKeyBothRealmsUse), new SubscriptionsOptions
        {
            IngressBytesPerSecond = TestIngress.Budget,
            MaxGroupsPerSession = 4,
            ReplicationCellM = 10,
        });
        harness.RunFence = true;

        // Both realms served first, each session out of its town's reach: every resident gets its block and identity before either town is met, so the
        // fills below meet the other realm's residents described — with a block and a netId — and must still pass them by.
        var sessions = harness.OpenSessions(2, Profile);
        var commands = harness.Subscriptions.Commands;
        Assert.That(commands.Enter(sessions[0], RealmId.Default), Is.True);
        Assert.That(commands.Enter(sessions[1], new RealmId(1)), Is.True);
        harness.Sessions.SetViewpoint(sessions[0], new Vector3D(95d, 5d, 0d));
        harness.Sessions.SetViewpoint(sessions[1], new Vector3D(-35d, 35d, 0d));
        for (var t = 0; t < 4; t++)
        {
            harness.RunTick(harness.Tick + 1);
            harness.Deliver(sessions[0]);
            harness.Deliver(sessions[1]);
        }

        Assert.That(inZero.Concat(inOne).Select(harness.NetIdOf), Has.All.Not.Zero, "every resident is described before a town is met");
        harness.Sessions.SetViewpoint(sessions[0], new Vector3D(50d, 50d, 0d));
        harness.Sessions.SetViewpoint(sessions[1], new Vector3D(0d, 0d, 0d));
        for (var t = 0; t < 8; t++)
        {
            harness.RunTick(harness.Tick + 1);
            harness.Deliver(sessions[0]);
            harness.Deliver(sessions[1]);
        }

        var plan = harness.PlanIndex(nameof(RealmResident));
        var zero = harness.Replica(sessions[0]);
        var one = harness.Replica(sessions[1]);
        Assert.Multiple(() =>
        {
            Assert.That(zero.Store.IsGroupComplete(0, 8), Is.True, "realm 0's town 8 completes: realm 1's residents are not waited for");
            Assert.That(zero.NetIds(plan), Is.EquivalentTo(inZero.Select(harness.NetIdOf)), "realm 0's residents, and only them");
            Assert.That(one.Store.IsGroupComplete(0, 8), Is.True);
            Assert.That(one.NetIds(plan), Is.EquivalentTo(inOne.Select(harness.NetIdOf)), "realm 1's residents, and only them");
            Assert.That(zero.Store.Anomalies + one.Store.Anomalies, Is.Zero);
        });

        // An event naming a resident reaches its own realm's holders only, though both realms hold "town 8".
        commands.Emit(new ProjDuel { A = inOne[0], B = inOne[0], Seq = 5 });
        harness.RunTick(harness.Tick + 1);
        harness.Deliver(sessions[0]);
        harness.Deliver(sessions[1]);
        Assert.Multiple(() =>
        {
            Assert.That(one.Events.Received.Select(e => e.Fields["Seq"]), Is.EqualTo(new[] { 5d }), "realm 1 holds it");
            Assert.That(zero.Events.Received, Is.Empty, "realm 0's town 8 is another group");
        });
    }

    /// <summary>
    /// A realm no session serves never describes its residents, so another realm's fill meets them in the index with no block at all: they are not this
    /// realm's to wait for, and its town completes without them (a positionless member with no block is pending; a positioned one of another realm is not).
    /// </summary>
    [Test]
    [VerifiesRule("SUB-33")]
    [VerifiesRule("SUB-28")]
    public void AnUnservedRealmsResidentsDoNotHoldAnotherRealmsTownBack()
    {
        using var dbe = SetupEngine();
        Town(dbe, 0, 8, 50f, 50f);
        Town(dbe, 1, 8, 0f, 0f);
        var inZero = new EntityId[2];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < inZero.Length; i++)
            {
                inZero[i] = tx.Spawn<RealmResident>(
                    RealmResident.Pos.Set(new RealmPos { Bounds = new AABB2F { MinX = 48 + i, MinY = 50, MaxX = 48 + i, MaxY = 50 }, Realm = 0 }),
                    RealmResident.Key.Set(new GroupKey { Town = 8 }), RealmResident.Tag.Set(new GroupTag()));
            }

            for (var i = 0; i < 2; i++)
            {
                tx.Spawn<RealmResident>(RealmResident.Pos.Set(new RealmPos { Bounds = new AABB2F { MinX = i, MinY = 0, MaxX = i, MaxY = 0 }, Realm = 1 }),
                    RealmResident.Key.Set(new GroupKey { Town = 8 }), RealmResident.Tag.Set(new GroupTag()));
            }

            tx.Commit();
        }

        using var harness = FrameHarness.Create(dbe, subs =>
        {
            subs.Archetype<RealmResident>(a => a.Motion(RealmResident.Pos, m => m.Teleport(200)));
            subs.Groups("town", g => g.Region<RealmTown>(r => r.Key(RealmTown.Zone, z => z.Key).Extent(RealmTown.Zone, z => z.Extent))
                .Member<RealmResident>(m => m.Key(RealmResident.Key, k => k.Town)));
            subs.Profile(Profile, p => p.Sphere(30).Groups("town", 5));
        }, nameof(AnUnservedRealmsResidentsDoNotHoldAnotherRealmsTownBack), new SubscriptionsOptions
        {
            IngressBytesPerSecond = TestIngress.Budget,
            MaxGroupsPerSession = 4,
            ReplicationCellM = 10,
        });
        harness.RunFence = true;

        var session = harness.OpenSessions(1, Profile)[0];
        Assert.That(harness.Subscriptions.Commands.Enter(session, RealmId.Default), Is.True);
        harness.Sessions.SetViewpoint(session, new Vector3D(50d, 50d, 0d));
        for (var t = 0; t < 8; t++)
        {
            harness.RunTick(harness.Tick + 1);
            harness.Deliver(session);
        }

        var replica = harness.Replica(session);
        Assert.Multiple(() =>
        {
            Assert.That(replica.Store.IsGroupComplete(0, 8), Is.True, "realm 1's undescribed residents are not waited for");
            Assert.That(replica.NetIds(harness.PlanIndex(nameof(RealmResident))), Is.EquivalentTo(inZero.Select(harness.NetIdOf)));
        });
    }

    [Test]
    [VerifiesRule("SUB-33")]
    [VerifiesRule("SUB-28")]
    public void AShardIsHeldBySessionsOfTheRealmItsTownIsIn()
    {
        using var dbe = SetupEngine();
        Town(dbe, 1, 7, 0f, 0f);
        Town(dbe, 0, 8, 50f, 50f);
        var shards = Shards(dbe);   // 0, 2, 4, 6 in town 7; 1, 3, 5 in town 8

        using var harness = FrameHarness.Create(dbe, subs =>
        {
            subs.Archetype<GroupPositionless>(a => a.Field(GroupPositionless.Ai, x => x.Template, Codec.U8, name: "kind"));
            subs.Groups("town", g => g.Region<RealmTown>(r => r.Key(RealmTown.Zone, z => z.Key).Extent(RealmTown.Zone, z => z.Extent))
                .Member<GroupPositionless>(m => m.Key(GroupPositionless.Key, k => k.Town)));
            subs.Profile(Profile, p => p.Sphere(30).Groups("town", 5));
            subs.Event<ProjDuel>(e => e.RouteToKnown(d => d.A, d => d.B));
        }, nameof(AShardIsHeldBySessionsOfTheRealmItsTownIsIn), new SubscriptionsOptions
        {
            IngressBytesPerSecond = TestIngress.Budget,
            MaxGroupsPerSession = 4,
            ReplicationCellM = 10,
        });
        harness.RunFence = true;

        // Realm 0's session first: town 8 attaches the shards' cluster, and town 7's shards are identified with no event left to carry them — the realm 1
        // session that meets town 7 later reads them through the fill, from a block of no realm of its own.
        var sessions = harness.OpenSessions(2, Profile);
        var commands = harness.Subscriptions.Commands;
        Assert.That(commands.Enter(sessions[1], RealmId.Default), Is.True);
        harness.Sessions.SetViewpoint(sessions[1], new Vector3D(50d, 50d, 0d));
        for (var t = 0; t < 4 + PushReplication.LogDepth; t++)
        {
            harness.RunTick(harness.Tick + 1);
            harness.Deliver(sessions[1]);
        }

        Assert.That(commands.Enter(sessions[0], new RealmId(1)), Is.True);
        harness.Sessions.SetViewpoint(sessions[0], new Vector3D(0d, 0d, 0d));
        for (var t = 0; t < 6; t++)
        {
            harness.RunTick(harness.Tick + 1);
            harness.Deliver(sessions[0]);
            harness.Deliver(sessions[1]);
        }

        var plan = harness.PlanIndex(nameof(GroupPositionless));
        var inRealm1 = harness.Replica(sessions[0]);
        var inRealm0 = harness.Replica(sessions[1]);
        Assert.Multiple(() =>
        {
            Assert.That(inRealm1.Store.IsGroupComplete(0, 7), Is.True, "realm 1's town, met in realm 1");
            Assert.That(inRealm1.Store.IsGroupComplete(0, 8), Is.False, "realm 0's town is not met from realm 1, whatever the coordinates");
            Assert.That(inRealm1.NetIds(plan), Has.Length.EqualTo(4), "its shards, though no cluster of theirs is in realm 1");
            Assert.That(inRealm0.Store.IsGroupComplete(0, 8), Is.True);
            Assert.That(inRealm0.NetIds(plan), Has.Length.EqualTo(3));
            Assert.That(inRealm1.Store.Anomalies + inRealm0.Store.Anomalies, Is.Zero);
        });

        // An event naming a shard reaches the sessions holding its town, in whichever realm — the block the shard is described in is realm 0's.
        commands.Emit(new ProjDuel { A = shards[0], B = shards[0], Seq = 7 });
        commands.Emit(new ProjDuel { A = shards[1], B = shards[1], Seq = 8 });
        harness.RunTick(harness.Tick + 1);
        harness.Deliver(sessions[0]);
        harness.Deliver(sessions[1]);
        Assert.Multiple(() =>
        {
            Assert.That(inRealm1.Events.Received.Select(e => e.Fields["Seq"]), Is.EqualTo(new[] { 7d }), "town 7's shard, held from realm 1");
            Assert.That(inRealm0.Events.Received.Select(e => e.Fields["Seq"]), Is.EqualTo(new[] { 8d }), "town 8's, held from realm 0");
            Assert.That(inRealm1.NetIds(plan), Does.Contain((uint)inRealm1.Events.Received[0].Fields["A"]), "named by the netId realm 1's session holds");
            Assert.That(inRealm0.NetIds(plan), Does.Contain((uint)inRealm0.Events.Received[0].Fields["A"]));
        });
    }

    /// <summary>
    /// A realm-local slot a session gave back by leaving the realm, taken by another session of the same generation number: the newcomer starts from no
    /// key — it fills and completes the town it meets — never from the keys the slot's last session held (#1407).
    /// </summary>
    [Test]
    [VerifiesRule("SUB-33")]
    [VerifiesRule("SUB-28")]
    public void ASessionTakingTheRealmSlotAnotherLeftHoldsNoneOfItsKeys()
    {
        using var dbe = SetupEngine();
        Town(dbe, 0, 8, 50f, 50f);
        Town(dbe, 1, 7, 0f, 0f);
        using (var tx = dbe.CreateQuickTransaction())
        {
            // Beyond a Sphere at the town's centre: held through the town alone.
            tx.Spawn<RealmResident>(RealmResident.Pos.Set(new RealmPos { Bounds = new AABB2F { MinX = 90, MinY = 90, MaxX = 90, MaxY = 90 }, Realm = 0 }),
                RealmResident.Key.Set(new GroupKey { Town = 8 }));
            tx.Commit();
        }

        using var harness = FrameHarness.Create(dbe, subs =>
        {
            subs.Archetype<RealmResident>(a => a.Motion(RealmResident.Pos, m => m.Teleport(200)));
            subs.Groups("town", g => g.Region<RealmTown>(r => r.Key(RealmTown.Zone, z => z.Key).Extent(RealmTown.Zone, z => z.Extent))
                .Member<RealmResident>(m => m.Key(RealmResident.Key, k => k.Town)));
            subs.Profile(Profile, p => p.Sphere(30).Groups("town", 5));
        }, nameof(ASessionTakingTheRealmSlotAnotherLeftHoldsNoneOfItsKeys), new SubscriptionsOptions
        {
            IngressBytesPerSecond = TestIngress.Budget,
            MaxGroupsPerSession = 4,
            ReplicationCellM = 10,
        });
        harness.RunFence = true;
        var commands = harness.Subscriptions.Commands;
        var plan = harness.PlanIndex(nameof(RealmResident));

        void Run(SessionId session, int ticks)
        {
            for (var t = 0; t < ticks; t++)
            {
                harness.RunTick(harness.Tick + 1);
                harness.Deliver(session);
            }
        }

        var first = harness.OpenSessions(1, Profile)[0];
        Assert.That(commands.Enter(first, RealmId.Default), Is.True);
        harness.Sessions.SetViewpoint(first, new Vector3D(50d, 50d, 0d));
        Run(first, 4);
        Assert.That(harness.Replica(first).NetIds(plan), Has.Length.EqualTo(1), "precondition: town 8 held in realm 0");
        var slot = harness.Subscriptions.Hub.LocalSlot(first, 0);

        // To realm 1: its realm-0 slot goes back, its group state still in it.
        Assert.That(commands.Enter(first, new RealmId(1)), Is.True);
        harness.Sessions.SetViewpoint(first, new Vector3D(0d, 0d, 0d));
        Run(first, 2);

        var second = harness.OpenSessions(1, Profile)[0];
        Assert.That(second.Generation, Is.EqualTo(first.Generation), "precondition: a generation the slot's state could mistake for its own");
        Assert.That(commands.Enter(second, RealmId.Default), Is.True);
        harness.Sessions.SetViewpoint(second, new Vector3D(50d, 50d, 0d));
        Run(second, 4);
        Assert.That(harness.Subscriptions.Hub.LocalSlot(second, 0), Is.EqualTo(slot), "precondition: the newcomer took the slot the first gave back");
        Assert.Multiple(() =>
        {
            Assert.That(harness.Replica(second).NetIds(plan), Has.Length.EqualTo(1), "town 8's resident entered for the newcomer");
            Assert.That(harness.Replica(second).Store.IsGroupComplete(0, 8), Is.True, "and town 8 completed for it");
            Assert.That(harness.Replica(second).Store.Anomalies, Is.Zero);
        });
    }
}
