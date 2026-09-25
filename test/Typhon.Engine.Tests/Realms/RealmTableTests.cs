using System;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests.Realms;

/// <summary>SP-1 (Realms): the engine's grid lives in a <see cref="RealmTable"/>; <c>ConfigureSpatialGrid</c> registers realm 0.</summary>
[TestFixture]
class RealmTableTests : TestBase<RealmTableTests>
{
    private static SpatialGrid Grid() => new(SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(100, 100), 10f));

    [Test]
    public void Register_PublishesTheRealmAtItsIndex()
    {
        var table = new RealmTable(4);
        var realm = table.Register(new RealmId(2), Grid());

        Assert.That(table.IsRegistered(2), Is.True);
        Assert.That(table.IsRegistered(0), Is.False);
        Assert.That(table.Get(2), Is.SameAs(realm));
        Assert.That(table.TryGet(1), Is.Null);
        Assert.That(table.Default, Is.Null, "realm 0 was never registered");
        Assert.That(table.Registered.ToArray(), Is.EqualTo((Realm[])[realm]));
    }

    [Test]
    public void Register_OutOfRangeOrDuplicate_Refused()
    {
        var table = new RealmTable(2);
        table.Register(RealmId.Default, Grid());

        var refused = Grid();
        Assert.Throws<ArgumentOutOfRangeException>(() => table.Register(new RealmId(2), refused));
        Assert.Throws<InvalidOperationException>(() => table.Register(RealmId.Default, refused));
        Assert.That(refused.IsRegistered, Is.False, "a refused registration binds nothing");
        Assert.That(table.Registered.Length, Is.EqualTo(1));
        Assert.Throws<InvalidOperationException>(() => table.Get(1));
        Assert.That(table.TryGet(5), Is.Null, "out of range reads as absent, never throws");
    }

    [TestCase(0)]
    [TestCase(RealmId.MaxCount + 1)]
    public void RealmCount_OutOfRange_Refused(int maxRealms) => Assert.Throws<ArgumentOutOfRangeException>(() => _ = new RealmTable(maxRealms));

    [Test]
    public void RealmIdNone_IsReservedAndNotAValidIndex()
    {
        Assert.That(RealmId.None.IsNone, Is.True);
        Assert.That(RealmId.Default.Value, Is.Zero);
        var table = new RealmTable(RealmId.MaxCount);
        Assert.That(table.IsRegistered(RealmId.NoneValue), Is.False, "0xFFFF is past the last index even at the maximum count");
    }

    [Test]
    public void ConfigureSpatialGrid_RegistersDefaultRealm()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(100, 100), 10f));
        dbe.InitializeArchetypes();

        Assert.That(dbe.Realms, Is.Not.Null);
        Assert.That(dbe.Realms.Default, Is.Not.Null);
        Assert.That(dbe.SpatialGrid, Is.SameAs(dbe.Realms.Default.Grid), "the single-world grid is realm 0's");
        Assert.That(dbe.Realms.Registered.Length, Is.EqualTo(1));
    }

    [Test]
    public void NoGridConfigured_NoRealms()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.InitializeArchetypes();

        Assert.That(dbe.SpatialGrid, Is.Null);
    }

    /// <summary>A grid belongs to one realm: registering it twice would make its realm id name the wrong realm's state (SpatialOf).</summary>
    [Test]
    public void SameGrid_RegisteredTwice_Refused()
    {
        var table = new RealmTable(2);
        var grid = Grid();
        table.Register(RealmId.Default, grid);
        Assert.Throws<InvalidOperationException>(() => table.Register(new RealmId(1), grid));
        Assert.That(grid.Realm, Is.EqualTo(RealmId.Default));
        Assert.That(table.IsRegistered(1), Is.False);
    }

    [Test]
    public void Registered_KeepsRegistrationOrder_AcrossGrowth()
    {
        var table = new RealmTable(40);
        var order = new ushort[] { 7, 3, 39, 0, 12, 5, 21, 1, 30, 2 };
        foreach (var id in order)
        {
            table.Register(new RealmId(id), Grid());
        }

        var registered = table.Registered.ToArray();
        Assert.That(registered, Has.Length.EqualTo(order.Length));
        for (var i = 0; i < order.Length; i++)
        {
            Assert.That(registered[i].Id.Value, Is.EqualTo(order[i]));
        }
    }
}
