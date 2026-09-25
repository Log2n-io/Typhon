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
        var offenders = typeof(ArchetypeClusterState)
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(f => perCell.Contains(f.FieldType))
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
        Assert.That(cs.DefaultRealmSpatial, Is.SameAs(cs.RealmSpatial[0]));
        Assert.That(cs.DefaultRealmSpatial.Realm, Is.EqualTo(RealmId.Default));
        Assert.That(cs.DefaultRealmSpatial.Grid, Is.SameAs(dbe.SpatialGrid));
        Assert.That(cs.CellClusterPool, Is.SameAs(cs.DefaultRealmSpatial.CellClusterPool));
    }
}
