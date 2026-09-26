using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Tests.Runtime;

namespace Typhon.Engine.Tests.Realms;

/// <summary>SP-2 (Realms): an archetype's cell-keyed spatial state lives in its per-realm <see cref="RealmArchetypeSpatial"/>.</summary>
[TestFixture]
class RealmArchetypeSpatialTests : TestBase<RealmArchetypeSpatialTests>
{
    /// <summary>A cell-keyed structure left as a FIELD on the archetype state would be shared by every realm the archetype is in: a cross-realm leak.</summary>
    [Test]
    public void NoPerCellStructureLivesOnArchetypeClusterState()
    {
        Type[] perCell = [typeof(CellClusterPool), typeof(PerCellSpatialSlot[]), typeof(EscapedClusterSet), typeof(SpatialGrid)];
        // By name too: a float reach or an int promoted count has a type too common to test by, and a field of that name on the archetype would be
        // realm-blind all the same.
        string[] perRealmNames = ["ClusterReach", "EscapedClusters", "PromotedCellCount", "PromotedCells", "TightnessBlockedCells", "PerCellIndex"];
        var offenders = typeof(ArchetypeClusterState)
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(f => perCell.Contains(f.FieldType) || perRealmNames.Any(n => f.Name.TrimStart('_').Equals(n, StringComparison.OrdinalIgnoreCase)))
            .Select(f => f.Name)
            .ToArray();

        Assert.That(offenders, Is.Empty, "cell-keyed state must live in RealmArchetypeSpatial, one per realm");
    }

    [Test]
    public void SpatialArchetype_HasRealm0State_OverTheEngineGrid()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<TierPos>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(100, 100), 10f));
        dbe.InitializeArchetypes();

        var cs = dbe._archetypeStates[Archetype<TierUnit>.Metadata.ArchetypeId].ClusterState;
        Assert.That(cs.RealmSpatial, Has.Length.EqualTo(1));
        Assert.That(cs.Realm0Spatial, Is.SameAs(cs.RealmSpatial[0]));
        Assert.That(cs.Realm0Spatial.Realm, Is.EqualTo(RealmId.Default));
        Assert.That(cs.Realm0Spatial.Grid, Is.SameAs(dbe.Realm0Grid));
        Assert.That(cs.Realm0Spatial.CellClusterPool, Is.SameAs(cs.Realm0Spatial.CellClusterPool));
    }

    [Test]
    public void SpatialOf_ResolvesTheGridsRealm_AndNoneOtherwise()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<TierPos>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(100, 100), 10f));
        dbe.InitializeArchetypes();
        var cs = dbe._archetypeStates[Archetype<TierUnit>.Metadata.ArchetypeId].ClusterState;

        Assert.That(cs.SpatialOf(dbe.Realm0Grid), Is.SameAs(cs.Realm0Spatial));
        Assert.That(cs.SpatialOf(null), Is.SameAs(RealmArchetypeSpatial.None));

        // A grid of a realm this archetype has no state in answers None, never an out-of-range throw.
        var table = new RealmTable(8);
        var elsewhere = new SpatialGrid(SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(10, 10), 10f));
        table.Register(new RealmId(5), elsewhere);
        Assert.That(cs.SpatialOf(elsewhere), Is.SameAs(RealmArchetypeSpatial.None));
    }

    /// <summary><see cref="RealmArchetypeSpatial.None"/> is shared by every non-spatial archetype: nothing the suite does may have written into it.</summary>
    [OneTimeTearDown]
    public void NoneStayedEmpty()
    {
        var none = RealmArchetypeSpatial.None;
        Assert.That(none.PerCellIndex, Is.Null);
        Assert.That(none.CellClusterPool, Is.Null);
        Assert.That(none.ClusterReach, Is.Zero);
        Assert.That(none.EscapedClusters.Count, Is.Zero);
        Assert.That(none.PromotedCellCount, Is.Zero);
        Assert.That(none.PromotedCells, Is.Null);
        Assert.That(none.TightnessBlockedCells, Is.Null);
    }
}
