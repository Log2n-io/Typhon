using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Realms;

[Component("Typhon.Test.Realm.PlainPos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct PlainPos
{
    [Field]
    [SpatialIndex]
    public AABB2F Bounds;
}

/// <summary>A spatial archetype with no realm key: it lives in realm 0.</summary>
[Archetype]
partial class PlainUnit : Archetype<PlainUnit>
{
    public static readonly Comp<PlainPos> Pos = Register<PlainPos>();
}

/// <summary>
/// Realms: one realm's clusters of an archetype, walked alone — <c>GetClusterEnumerator(realm)</c> reads that realm's own list, O(clusters there), and
/// follows spawns, cross-realm moves and drains; an unkeyed archetype is wholly in realm 0. <c>EcsQuery.InRealm</c> without a spatial predicate is refused
/// rather than silently answering every realm.
/// </summary>
[TestFixture]
class RealmClusterListTests : TestBase<RealmClusterListTests>
{
    private static SpatialGridConfig Grid(double cellSize) => SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(100, 100), cellSize);

    private static RealmPos At(float x, float y, ushort realm) => new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Realm = realm };

    private long _tick;

    private DatabaseEngine ThreeRealms()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<RealmPos>();
        dbe.RegisterComponentFromAccessor<PlainPos>();
        dbe.ConfigureRealms(4);
        dbe.ConfigureSpatialGrid(Grid(10));
        dbe.Realms.Register(new RealmId(1), RealmConfig.SimulatedAlways(Grid(25)));
        dbe.Realms.Register(new RealmId(2), RealmConfig.SimulatedAlways(Grid(50)));
        dbe.InitializeArchetypes();
        return dbe;
    }

    private List<EntityId> Spawn(DatabaseEngine dbe, ushort realm, int count)
    {
        var ids = new List<EntityId>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < count; i++)
            {
                // Spread over cells, so the realm holds several clusters.
                ids.Add(tx.Spawn<RealmUnit>(RealmUnit.Pos.Set(At(1 + (i * 97 % 98), 1 + (i * 31 % 98), realm))));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(++_tick);
        return ids;
    }

    // Every entity the realm's walk yields, and how many clusters it walked.
    private static (HashSet<EntityId> Ids, int Clusters) Walk<TArch>(DatabaseEngine dbe, RealmId realm) where TArch : Archetype<TArch>, new()
    {
        var ids = new HashSet<EntityId>();
        var clusters = 0;
        using var tx = dbe.CreateQuickTransaction();
        using var accessor = tx.For<TArch>();
        foreach (var cluster in accessor.GetClusterEnumerator(realm))
        {
            clusters++;
            for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
            {
                ids.Add(cluster.GetEntityId(System.Numerics.BitOperations.TrailingZeroCount(bits)));
            }
        }

        Assert.That(clusters, Is.EqualTo(accessor.ClusterCountIn(realm)), "the enumerator and the count read the same list");
        return (ids, clusters);
    }

    [Test]
    [VerifiesRule("RM-07")]
    public void ARealmsClusterWalkYieldsThatRealmsEntitiesOnly_AndFollowsMovesAndDrains()
    {
        using var dbe = ThreeRealms();
        var inZero = Spawn(dbe, 0, 300);
        var inOne = Spawn(dbe, 1, 200);

        var (zero, zeroClusters) = Walk<RealmUnit>(dbe, RealmId.Default);
        var (one, oneClusters) = Walk<RealmUnit>(dbe, new RealmId(1));
        Assert.Multiple(() =>
        {
            Assert.That(zero, Is.EquivalentTo(inZero));
            Assert.That(one, Is.EquivalentTo(inOne));
            Assert.That(zeroClusters + oneClusters, Is.EqualTo(dbe._archetypeStates[Archetype<RealmUnit>.Metadata.ArchetypeId].ClusterState.ActiveClusterCount),
                "every active cluster is in exactly one realm's list");
            Assert.That(Walk<RealmUnit>(dbe, new RealmId(2)).Ids, Is.Empty, "a registered realm with nothing in it");
        });

        // Ten entities of realm 0 move to realm 2; every entity of realm 1 is destroyed, so its clusters drain.
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 10; i++)
            {
                tx.Teleport(inZero[i], RealmUnit.Pos, new RealmId(2), At(5 + i, 5, 2));
            }

            foreach (var id in inOne)
            {
                tx.Destroy(id);
            }

            tx.Commit();
        }

        dbe.WriteTickFence(++_tick);
        dbe.WriteTickFence(++_tick);
        Assert.Multiple(() =>
        {
            Assert.That(Walk<RealmUnit>(dbe, new RealmId(2)).Ids, Is.EquivalentTo(inZero.GetRange(0, 10)));
            Assert.That(Walk<RealmUnit>(dbe, RealmId.Default).Ids, Is.EquivalentTo(inZero.GetRange(10, inZero.Count - 10)));
            Assert.That(Walk<RealmUnit>(dbe, new RealmId(1)).Clusters, Is.Zero, "a realm's drained clusters leave its list");
        });
    }

    [Test]
    public void AnUnkeyedArchetypeIsWhollyInRealm0()
    {
        using var dbe = ThreeRealms();
        var ids = new List<EntityId>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 100; i++)
            {
                var box = new AABB2F { MinX = 1 + i % 98, MinY = 1 + (i * 7 % 98), MaxX = 1 + i % 98, MaxY = 1 + (i * 7 % 98) };
                ids.Add(tx.Spawn<PlainUnit>(PlainUnit.Pos.Set(new PlainPos { Bounds = box })));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(++_tick);
        Assert.Multiple(() =>
        {
            Assert.That(Walk<PlainUnit>(dbe, RealmId.Default).Ids, Is.EquivalentTo(ids));
            Assert.That(Walk<PlainUnit>(dbe, new RealmId(1)).Clusters, Is.Zero);
        });
    }

    [Test]
    public void AnUnregisteredRealmIsRefused()
    {
        using var dbe = ThreeRealms();
        using var tx = dbe.CreateQuickTransaction();
        using (var accessor = tx.For<RealmUnit>())
        {
            var refused = false;
            try
            {
                accessor.ClusterCountIn(new RealmId(3));
            }
            catch (InvalidOperationException e) when (e.Message.Contains("not registered"))
            {
                refused = true;
            }

            Assert.That(refused, Is.True, "the count refuses an unregistered realm");
        }

        Assert.That(() => tx.GetClusterEnumerator<RealmUnit>(new RealmId(3)), Throws.InvalidOperationException);
    }

    [Test]
    [VerifiesRule("RM-07")]
    public void InRealmWithoutASpatialPredicateIsRefused_AndWithOneIsScoped()
    {
        using var dbe = ThreeRealms();
        Spawn(dbe, 0, 5);
        var inOne = Spawn(dbe, 1, 5);
        using var tx = dbe.CreateQuickTransaction();
        Assert.Multiple(() =>
        {
            Assert.That(() => tx.Query<RealmUnit>().InRealm(new RealmId(1)).Execute(),
                Throws.InvalidOperationException.With.Message.Contains("GetClusterEnumerator(realm)"));
            Assert.That(() => tx.Query<RealmUnit>().InRealm(RealmId.Default).Count(), Throws.InvalidOperationException, "naming realm 0 explicitly too");
            Assert.That(tx.Query<RealmUnit>().WhereInAABB<RealmPos>(0, 0, 0, 100, 100, 0).InRealm(new RealmId(1)).Execute(), Is.EquivalentTo(inOne));
        });
    }
}
