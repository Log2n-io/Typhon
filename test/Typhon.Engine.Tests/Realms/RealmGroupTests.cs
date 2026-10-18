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
    private static void Shards(DatabaseEngine dbe)
    {
        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < 7; i++)
        {
            var key = i % 2 == 0 ? 7 : 8;
            tx.Spawn<GroupPositionless>(GroupPositionless.Ai.Set(new ProjAi { Template = (byte)(1 + i) }),
                GroupPositionless.Key.Set(new GroupKey { Town = key }));
        }

        tx.Commit();
    }

    [Test]
    [VerifiesRule("SUB-33")]
    [VerifiesRule("SUB-28")]
    public void AShardIsHeldBySessionsOfTheRealmItsTownIsIn()
    {
        using var dbe = SetupEngine();
        Town(dbe, 1, 7, 0f, 0f);
        Town(dbe, 0, 8, 50f, 50f);
        Shards(dbe);   // 0, 2, 4, 6 in town 7; 1, 3, 5 in town 8

        using var harness = FrameHarness.Create(dbe, subs =>
        {
            subs.Archetype<GroupPositionless>(a => a.Field(GroupPositionless.Ai, x => x.Template, Codec.U8, name: "kind"));
            subs.Groups("town", g => g.Region<RealmTown>(r => r.Key(RealmTown.Zone, z => z.Key).Extent(RealmTown.Zone, z => z.Extent))
                .Member<GroupPositionless>(m => m.Key(GroupPositionless.Key, k => k.Town)));
            subs.Profile(Profile, p => p.Sphere(30).Groups("town", 5));
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
    }
}
