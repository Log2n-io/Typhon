using System;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Realms;

/// <summary>
/// Realms C1c-2: what the runtime reads per cell — the tier index, trigger regions — is the cell of the entity's OWN realm. Realm 0 (10 m cells) and
/// realm 2 (25 m cells) share cell keys, so a reader keyed by cell alone would take one realm's tier or occupants for the other's.
/// </summary>
[TestFixture]
class RealmRuntimeTests : TestBase<RealmRuntimeTests>
{
    private static SpatialGridConfig Grid(double cellSize) => SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(100, 100), cellSize);

    private static RealmPos At(float x, float y, ushort realm, int tag) =>
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

    [Test]
    public void TierIndex_ReadsEachClustersTier_InItsOwnRealm_AndAnyRealmsTierChangeInvalidatesIt()
    {
        using var dbe = TwoRealms();
        var grid0 = dbe.RealmTable.Get(0).Grid;
        var grid2 = dbe.RealmTable.Get(2).Grid;

        // Realm 0 at (5, 5) → its cell (0, 0) = key 0. Realm 2 at (55, 55) → its cell (2, 2), key 12; realm 0's key 12 is a different place.
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<RealmUnit>(RealmUnit.Pos.Set(At(5, 5, 0, 0)));
            tx.Spawn<RealmUnit>(RealmUnit.Pos.Set(At(55, 55, 2, 1)));
            tx.Commit();
        }

        var cs = StateOf(dbe);
        var key0 = grid0.WorldToCellKey(5, 5, 0);
        var key2 = grid2.WorldToCellKey(55, 55, 0);
        grid0.SetCellTier(key0, SimTier.Tier3);
        grid2.SetCellTier(key2, SimTier.Tier0);
        // The same KEY in the other realm's grid, set to the opposite tier: read by key alone, realm 2's cluster would land in Tier3.
        grid0.SetCellTier(key2, SimTier.Tier3);

        var index = new TierClusterIndex();
        index.RebuildIfStale(cs, dbe.RealmTable.TierVersion);
        var tier0 = index.GetClusters(SimTier.Tier0).ToArray();
        var tier3 = index.GetClusters(SimTier.Tier3).ToArray();
        Assert.That(tier0, Has.Length.EqualTo(1));
        Assert.That(cs.ClusterRealmMap[tier0[0]], Is.EqualTo(2), "Tier0 must hold realm 2's cluster, whose cell realm 2 set to Tier0");
        Assert.That(tier3, Has.Length.EqualTo(1));
        Assert.That(cs.ClusterRealmMap[tier3[0]], Is.EqualTo(0));

        // A tier change in realm 2 ONLY must still invalidate the index: the version is engine-wide.
        var rebuilds = index.RebuildCount;
        var before = dbe.RealmTable.TierVersion;
        grid2.SetCellTier(key2, SimTier.Tier1);
        Assert.That(dbe.RealmTable.TierVersion, Is.Not.EqualTo(before), "realm 2's grid must move the engine-wide version");
        index.RebuildIfStale(cs, dbe.RealmTable.TierVersion);
        Assert.That(index.RebuildCount, Is.EqualTo(rebuilds + 1));
        Assert.That(index.GetClusters(SimTier.Tier1).ToArray(), Has.Length.EqualTo(1));
        Assert.That(index.GetClusters(SimTier.Tier0).ToArray(), Is.Empty);
    }

    [Test]
    public void TriggerRegion_SeesOnlyItsRealm_AndAnUnregisteredRealmIsRefused()
    {
        using var dbe = TwoRealms();
        EntityId in0, in2;
        using (var tx = dbe.CreateQuickTransaction())
        {
            in0 = tx.Spawn<RealmUnit>(RealmUnit.Pos.Set(At(50, 50, 0, 0)));
            in2 = tx.Spawn<RealmUnit>(RealmUnit.Pos.Set(At(50, 50, 2, 1)));
            tx.Commit();
        }

        dbe.WriteTickFence(1);
        var triggers = dbe.SpatialTriggers<RealmPos>();
        var region0 = triggers.CreateRegion(new double[] { 40, 40, 60, 60 });
        var region2 = triggers.CreateRegion(new double[] { 40, 40, 60, 60 }, realm: new RealmId(2));

        var r0 = triggers.EvaluateRegion(region0, 2).Entered.ToArray();
        Assert.That(r0, Is.EqualTo(new[] { unchecked((long)in0.RawValue) }), "a realm-0 region reports realm 0's entity only");
        var r2 = triggers.EvaluateRegion(region2, 2).Entered.ToArray();
        Assert.That(r2, Is.EqualTo(new[] { unchecked((long)in2.RawValue) }), "a realm-2 region reports realm 2's entity only");

        Assert.Throws<InvalidOperationException>(() => triggers.CreateRegion(new double[] { 0, 0, 1, 1 }, realm: new RealmId(1)));
    }
}
