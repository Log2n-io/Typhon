using System;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Tests.Runtime;

namespace Typhon.Engine.Tests.Realms;

/// <summary>
/// Realms C1 (data model): <c>ConfigureRealms</c> sizes the realm table, <c>Realms.Register</c> adds realms at open, and an archetype's per-realm state
/// is created in a realm the first time it claims a cluster there, with the cluster's realm recorded beside its cell.
/// </summary>
[TestFixture]
class RealmRegistrationTests : TestBase<RealmRegistrationTests>
{
    private static SpatialGridConfig Planet() => SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(100, 100), 10f);
    private static SpatialGridConfig Interior() => SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(20, 20), 32f);

    private static RealmConfig Sleeping(SpatialGridConfig grid) =>
        new() { Grid = grid, WhenUnobserved = RealmUnobserved.Sleep, UnobservedTickDivisor = 1, SleepAfterTicks = 30 };

    private DatabaseEngine Engine() => ServiceProvider.GetRequiredService<DatabaseEngine>();

    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(RealmId.MaxCount + 1)]
    public void ConfigureRealms_OutOfRange_Refused(int maxRealms) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Engine().ConfigureRealms(maxRealms));

    [Test]
    public void Register_IdBeyondConfiguredCount_Refused()
    {
        var dbe = Engine();
        Assert.Throws<ArgumentOutOfRangeException>(() => dbe.Realms.Register(new RealmId(1), Sleeping(Interior())), "one realm without ConfigureRealms");
        dbe.ConfigureRealms(4);
        Assert.Throws<ArgumentOutOfRangeException>(() => dbe.Realms.Register(new RealmId(4), Sleeping(Interior())));
        Assert.DoesNotThrow(() => dbe.Realms.Register(new RealmId(3), Sleeping(Interior())));
        Assert.Throws<InvalidOperationException>(() => dbe.Realms.Register(new RealmId(3), Sleeping(Interior())), "duplicate");
    }

    [Test]
    public void Register_InvalidPolicy_Refused_NeverClamped()
    {
        var dbe = Engine();
        dbe.ConfigureRealms(2);
        Assert.Throws<ArgumentOutOfRangeException>(() => dbe.Realms.Register(new RealmId(1),
            new RealmConfig { Grid = Interior(), WhenUnobserved = RealmUnobserved.Simulate, UnobservedTickDivisor = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => dbe.Realms.Register(new RealmId(1),
            new RealmConfig { Grid = Interior(), WhenUnobserved = RealmUnobserved.Sleep, UnobservedTickDivisor = 1 }), "Sleep needs SleepAfterTicks");
        Assert.Throws<ArgumentException>(() => dbe.Realms.Register(new RealmId(1), new RealmConfig
        {
            Grid = Interior(), WhenUnobserved = RealmUnobserved.Simulate, UnobservedTickDivisor = 1, Parent = new RealmId(1)
        }));
        Assert.That(dbe.Realms.IsRegistered(new RealmId(1)), Is.False);
    }

    [Test]
    public void ConfigureSpatialGrid_And_RegisterRealm0_AreExclusive()
    {
        var dbe = Engine();
        dbe.ConfigureSpatialGrid(Planet());
        Assert.Throws<InvalidOperationException>(() => dbe.Realms.Register(RealmId.Default, RealmConfig.SimulatedAlways(Planet())));
    }

    [Test]
    public void RegisteredRealms_BuildTheTable_EachWithItsOwnGrid()
    {
        var dbe = Engine();
        dbe.RegisterComponentFromAccessor<TierPos>();
        dbe.ConfigureRealms(3);
        dbe.ConfigureSpatialGrid(Planet());
        dbe.Realms.Register(new RealmId(2), Sleeping(Interior()));
        dbe.InitializeArchetypes();

        var table = dbe.RealmTable;
        Assert.That(table.MaxRealms, Is.EqualTo(3));
        Assert.That(table.Registered.Length, Is.EqualTo(2));
        Assert.That(table.Get(0).Grid, Is.SameAs(dbe.Realm0Grid));
        Assert.That(table.Get(2).Grid, Is.Not.SameAs(table.Get(0).Grid));
        Assert.That(table.Get(2).Grid.Realm, Is.EqualTo(new RealmId(2)));
        Assert.That(table.Get(2).Config.WhenUnobserved, Is.EqualTo(RealmUnobserved.Sleep));
        Assert.That(table.IsRegistered(1), Is.False);
        Assert.Throws<InvalidOperationException>(() => dbe.Realms.Register(new RealmId(1), Sleeping(Interior())), "after InitializeArchetypes");
    }

    [Test]
    public void AClaimInAnotherRealm_CreatesThatRealmsState_AndRecordsTheClustersRealm()
    {
        var dbe = Engine();
        dbe.RegisterComponentFromAccessor<TierPos>();
        dbe.ConfigureRealms(4);
        dbe.ConfigureSpatialGrid(Planet());
        dbe.Realms.Register(new RealmId(3), Sleeping(Interior()));
        dbe.InitializeArchetypes();

        var cs = dbe._archetypeStates[Archetype<TierUnit>.Metadata.ArchetypeId].ClusterState;
        Assert.That(cs.PresentRealmSpatial.Length, Is.EqualTo(1), "realm 0 only, eagerly");
        Assert.That(cs.RealmSpatial[3], Is.Null, "realm 3's state does not exist before the archetype has a cluster there");

        var interior = dbe.RealmTable.Get(3).Grid;
        int chunk;
        using (EpochGuard.Enter(dbe.EpochManager))
        {
            var accessor = cs.ClusterSegment.CreateChunkAccessor();
            var changeSet = dbe.MMF.CreateChangeSet();
            try
            {
                var cellKey = interior.WorldToCellKey(5, 5, 0);
                (chunk, _) = cs.ClaimSlotInCell(cellKey, 5f, 5f, 0f, ref accessor, changeSet, interior, 1);
            }
            finally
            {
                accessor.Dispose();
                changeSet.SaveChanges();
            }
        }

        var rs3 = cs.RealmSpatial[3];
        Assert.That(rs3, Is.Not.Null);
        Assert.That(rs3.Grid, Is.SameAs(interior));
        Assert.That(cs.PresentRealmSpatial.Length, Is.EqualTo(2));
        Assert.That(cs.ClusterRealmMap[chunk], Is.EqualTo(3));
        Assert.That(cs.SpatialOfCluster(chunk), Is.SameAs(rs3));
        Assert.That(cs.SpatialOf(interior), Is.SameAs(rs3));
        Assert.That(rs3.CellClusterPool.GetClusters(cs.ClusterCellMap[chunk]).ToArray(), Does.Contain(chunk));
        Assert.That(cs.Realm0Spatial.CellClusterPool.GetClusters(cs.ClusterCellMap[chunk]).ToArray(), Does.Not.Contain(chunk),
            "the same cell key in realm 0 must not list another realm's cluster");
    }
}
