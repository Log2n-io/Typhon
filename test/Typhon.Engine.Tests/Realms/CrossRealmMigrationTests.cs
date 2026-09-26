using System;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Realms;

/// <summary>
/// Realms C4: an entity changes realm by writing its <c>[RealmKey]</c> — through the write barrier, <c>OpenMut</c> or <c>Teleport</c>. The write is
/// validated at the call; the next fence moves the entity into the new realm's cell (a mandatory crossing, hysteresis or not); until then no realm's
/// query returns it from the wrong frame; an invalid key written through a raw path is reverted at the fence, never thrown there (D-2).
/// </summary>
[TestFixture]
class CrossRealmMigrationTests : TestBase<CrossRealmMigrationTests>
{
    private static readonly AABB2F Everywhere = new() { MinX = 0, MinY = 0, MaxX = 100, MaxY = 100 };

    private static SpatialGridConfig Grid(double cellSize) => SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(100, 100), cellSize);

    private static RealmPos At(float x, float y, ushort realm, int tag = 0) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Realm = realm, Tag = tag };

    private DatabaseEngine TwoRealms()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<RealmPos>();
        dbe.ConfigureRealms(3);
        dbe.ConfigureSpatialGrid(Grid(10));
        dbe.Realms.Register(new RealmId(2), RealmConfig.SimulatedAlways(Grid(25)));
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static ArchetypeClusterState StateOf(DatabaseEngine dbe) => dbe._archetypeStates[Archetype<RealmUnit>.Metadata.ArchetypeId].ClusterState;

    private static EntityId SpawnOne(DatabaseEngine dbe, RealmPos value)
    {
        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<RealmUnit>(RealmUnit.Pos.Set(value));
            tx.Commit();
        }

        dbe.WriteTickFence(1);
        return id;
    }

    /// <summary>The entity's slot through the cluster barrier — the path an application's system writes through.</summary>
    private static void WriteSpatialOf(DatabaseEngine dbe, EntityId id, RealmPos value)
    {
        using var tx = dbe.CreateQuickTransaction();
        var accessor = tx.For<RealmUnit>();
        try
        {
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
                {
                    var slot = BitOperations.TrailingZeroCount(bits);
                    if (cluster.GetEntityId(slot) == id)
                    {
                        cluster.WriteSpatial(RealmUnit.Pos, slot, value);
                        tx.Commit();
                        return;
                    }
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        Assert.Fail("entity not found");
    }

    private static int Count(DatabaseEngine dbe, ushort realm, AABB2F box)
    {
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        return dbe.ClusterSpatialQuery<RealmUnit>(new RealmId(realm)).AABB(box).Count();
    }

    private static int CellEntities(SpatialGrid grid)
    {
        var total = 0;
        for (var cell = 0; cell < grid.CellCount; cell++)
        {
            total += grid.GetCell(cell).EntityCount;
        }

        return total;
    }

    private static (ushort realm, int chunk) HomeOf(DatabaseEngine dbe, EntityId id)
    {
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var cs = StateOf(dbe);
        foreach (ushort r in new ushort[] { 0, 2 })
        {
            foreach (var hit in dbe.ClusterSpatialQuery<RealmUnit>(new RealmId(r)).AABB(Everywhere))
            {
                if (hit.Entity == id)
                {
                    return (cs.ClusterRealmMap[hit.ClusterChunkId], hit.ClusterChunkId);
                }
            }
        }

        return (ushort.MaxValue, -1);
    }

    [Test]
    [VerifiesRule("RM-03")]
    public void WriteSpatial_RealmChange_MigratesAtTheFence_AndIsLogged()
    {
        using var dbe = TwoRealms();
        var id = SpawnOne(dbe, At(5, 5, 0));
        var cs = StateOf(dbe);
        var grid0 = dbe.RealmTable.Get(0).Grid;
        var grid2 = dbe.RealmTable.Get(2).Grid;

        WriteSpatialOf(dbe, id, At(60, 60, 2));
        Assert.That(Count(dbe, 0, Everywhere), Is.Zero, "realm 0 must not answer with an entity whose key names realm 2 (RM-04)");
        Assert.That(Count(dbe, 2, Everywhere), Is.Zero, "realm 2 does not hold it before the fence moves it");

        dbe.WriteTickFence(2);
        Assert.That(HomeOf(dbe, id).realm, Is.EqualTo(2));
        Assert.That(Count(dbe, 2, new AABB2F { MinX = 55, MinY = 55, MaxX = 65, MaxY = 65 }), Is.EqualTo(1));
        Assert.That(CellEntities(grid0), Is.Zero, "realm 0's cell gave the entity back");
        Assert.That(CellEntities(grid2), Is.EqualTo(1), "realm 2's cell counts it");
        Assert.That(cs.LastTickRealmChanges, Is.EqualTo(1));
        Assert.That(cs.LastFenceRealmChanges, Has.Count.EqualTo(1));
        Assert.That(cs.LastFenceRealmChanges[0], Is.EqualTo(new ArchetypeClusterState.RealmChange(unchecked((long)id.RawValue), 0, 2)));
    }

    [Test]
    [VerifiesRule("RM-03")]
    public void RealmChange_InsideTheHysteresisBand_StillMigrates()
    {
        using var dbe = TwoRealms();
        var id = SpawnOne(dbe, At(5, 5, 0));
        WriteSpatialOf(dbe, id, At(5, 5, 2));   // same coordinates: no crossing in either grid's terms, only the realm changes
        dbe.WriteTickFence(2);
        Assert.That(HomeOf(dbe, id).realm, Is.EqualTo(2));
    }

    [Test]
    public void RealmChange_DoesNotGrowTheSourceClustersBox()
    {
        using var dbe = TwoRealms();
        var id = SpawnOne(dbe, At(5, 5, 0));
        var cs = StateOf(dbe);
        var chunk = HomeOf(dbe, id).chunk;
        var before = cs.ClusterAabbs[chunk];
        WriteSpatialOf(dbe, id, At(95, 95, 2));
        Assert.That(cs.ClusterAabbs[chunk], Is.EqualTo(before), "another realm's coordinates must not widen this realm's cluster (CA-01)");
    }

    [Test]
    [VerifiesRule("RM-04")]
    public void RealmChange_ViaOpenMut_IsFoundByTheDirtyScan()
    {
        using var dbe = TwoRealms();
        var id = SpawnOne(dbe, At(5, 5, 0));
        using (var tx = dbe.CreateQuickTransaction())
        {
            ref var pos = ref tx.OpenMut(id).Write(RealmUnit.Pos);
            pos = At(40, 40, 2);
            tx.Commit();
        }

        Assert.That(Count(dbe, 0, Everywhere), Is.Zero, "the narrowphase filter covers a raw write too");
        dbe.WriteTickFence(2);
        Assert.That(HomeOf(dbe, id).realm, Is.EqualTo(2));
    }

    [Test]
    public void RealmChangeAndBack_InOneTick_StaysHome()
    {
        using var dbe = TwoRealms();
        var id = SpawnOne(dbe, At(5, 5, 0));
        var cs = StateOf(dbe);
        WriteSpatialOf(dbe, id, At(60, 60, 2));
        WriteSpatialOf(dbe, id, At(6, 6, 0));
        dbe.WriteTickFence(2);
        Assert.That(HomeOf(dbe, id).realm, Is.EqualTo(0));
        Assert.That(Count(dbe, 0, new AABB2F { MinX = 5.5f, MinY = 5.5f, MaxX = 6.5f, MaxY = 6.5f }), Is.EqualTo(1),
            "back home: its cluster's bound covers where the undo put it (the undo grew it, the realm change had not)");
        Assert.That(cs.LastFenceRealmChanges, Is.Empty);
    }

    [Test]
    public void WriteSpatial_IntoAnUnregisteredRealm_Throws_AndStoresNothing()
    {
        using var dbe = TwoRealms();
        var id = SpawnOne(dbe, At(5, 5, 0));
        Assert.Throws<InvalidOperationException>(() => WriteSpatialOf(dbe, id, At(60, 60, 1)));
        using var tx = dbe.CreateQuickTransaction();
        Assert.That(tx.Open(id).Read(RealmUnit.Pos).Realm, Is.EqualTo(0));
    }

    [Test]
    [VerifiesRule("RM-05")]
    public void InvalidRealmThroughARawWrite_IsRevertedAtTheFence_NeverThrown()
    {
        using var dbe = TwoRealms();
        var id = SpawnOne(dbe, At(5, 5, 0));
        var cs = StateOf(dbe);
        using (var tx = dbe.CreateQuickTransaction())
        {
            ref var pos = ref tx.OpenMut(id).Write(RealmUnit.Pos);
            pos.Realm = 1;   // unregistered: no pre-store check exists on a ref write
            tx.Commit();
        }

        Assert.DoesNotThrow(() => dbe.WriteTickFence(2));
        Assert.That(cs.LastTickRealmKeyReverts, Is.EqualTo(1));
        using (var tx = dbe.CreateQuickTransaction())
        {
            Assert.That(tx.Open(id).Read(RealmUnit.Pos).Realm, Is.EqualTo(0), "the key is rewritten to the cluster's realm");
        }

        Assert.That(Count(dbe, 0, Everywhere), Is.EqualTo(1), "and the entity answers its realm's queries again");
    }

    [Test]
    public void Teleport_ValidatesUpFront_AndTheFenceMovesTheEntity()
    {
        using var dbe = TwoRealms();
        var id = SpawnOne(dbe, At(5, 5, 0, 7));
        using (var tx = dbe.CreateQuickTransaction())
        {
            Assert.Throws<InvalidOperationException>(() => tx.Teleport(id, RealmUnit.Pos, new RealmId(1), At(1, 1, 0)));
            tx.Teleport(id, RealmUnit.Pos, new RealmId(2), At(70, 70, 0, 7));   // the key is set by Teleport, whatever the value held
            tx.Commit();
        }

        dbe.WriteTickFence(2);
        Assert.That(HomeOf(dbe, id).realm, Is.EqualTo(2));
        using var rtx = dbe.CreateQuickTransaction();
        var v = rtx.Open(id).Read(RealmUnit.Pos);
        Assert.That((v.Realm, v.Tag, v.Bounds.MinX), Is.EqualTo(((ushort)2, 7, 70f)));
    }

    [Test]
    public void Teleport_MovesABarrierOnlyArchetype_WhoseFenceRunsNoDirtyScan()
    {
        using var dbe = TwoRealms();
        dbe.SetSpatialBarrierOnly<RealmUnit>();
        var id = SpawnOne(dbe, At(5, 5, 0));
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Teleport(id, RealmUnit.Pos, new RealmId(2), At(30, 30, 2));
            tx.Commit();
        }

        dbe.WriteTickFence(2);
        Assert.That(HomeOf(dbe, id).realm, Is.EqualTo(2));
    }

    [Test]
    public void ASecondWriteInTheNewRealm_StillDoesNotGrowTheSourceBox()
    {
        using var dbe = TwoRealms();
        var id = SpawnOne(dbe, At(5, 5, 0));
        var cs = StateOf(dbe);
        var chunk = HomeOf(dbe, id).chunk;
        var before = cs.ClusterAabbs[chunk];
        WriteSpatialOf(dbe, id, At(95, 95, 2));
        WriteSpatialOf(dbe, id, At(90, 90, 2));   // the key already reads 2: still another realm's frame for this cluster
        Assert.That(cs.ClusterAabbs[chunk], Is.EqualTo(before));
        dbe.WriteTickFence(2);
        Assert.That(HomeOf(dbe, id).realm, Is.EqualTo(2));
        Assert.That(Count(dbe, 2, new AABB2F { MinX = 89, MinY = 89, MaxX = 91, MaxY = 91 }), Is.EqualTo(1));
    }

    [Test]
    public void RealmChange_ToANonFinitePosition_Throws_AtTheCall()
    {
        using var dbe = TwoRealms();
        var id = SpawnOne(dbe, At(5, 5, 0));
        Assert.Throws<InvalidOperationException>(() => WriteSpatialOf(dbe, id, At(float.NaN, 5, 2)));
        using var tx = dbe.CreateQuickTransaction();
        Assert.Throws<InvalidOperationException>(() => tx.Teleport(id, RealmUnit.Pos, new RealmId(2), At(float.PositiveInfinity, 1, 2)));
    }

    [Test]
    public void Teleport_OfAnEntitySpawnedInTheSameTransaction_PlacesItInTheNewRealm()
    {
        using var dbe = TwoRealms();
        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<RealmUnit>(RealmUnit.Pos.Set(At(5, 5, 0)));
            tx.Teleport(id, RealmUnit.Pos, new RealmId(2), At(40, 40, 0));
            tx.Commit();
        }

        Assert.That(HomeOf(dbe, id).realm, Is.EqualTo(2), "placed in realm 2 at commit — no fence needed, no phantom flag on cluster 0");
    }

    [Test]
    public void AnInvalidKeyWrittenIntoAPendingSpawn_IsPlacedInTheValidatedRealm()
    {
        using var dbe = TwoRealms();
        var cs = StateOf(dbe);
        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<RealmUnit>(RealmUnit.Pos.Set(At(5, 5, 2)));
            ref var pos = ref tx.OpenMut(id).Write(RealmUnit.Pos);
            pos.Realm = 1;   // unregistered, written in place into the staged spawn
            Assert.DoesNotThrow(() => tx.Commit());
        }

        Assert.That(HomeOf(dbe, id).realm, Is.EqualTo(2));
        using var rtx = dbe.CreateQuickTransaction();
        Assert.That(rtx.Open(id).Read(RealmUnit.Pos).Realm, Is.EqualTo(2), "the key is corrected to the realm it was placed in");
    }
}
