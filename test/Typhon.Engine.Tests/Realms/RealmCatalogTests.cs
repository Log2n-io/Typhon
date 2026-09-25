using System;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Realms;

/// <summary>
/// Realms C2 (decision D-1): the engine persists every named realm's identity, so a later open rebuilds each realm's spatial layer before the
/// application has said anything, refuses a registration that would re-map a realm's entities, and refuses a realm count below what the file holds.
/// </summary>
[TestFixture]
[NonParallelizable]
class RealmCatalogTests : TestBase<RealmCatalogTests>
{
    private const float World = 100f;
    private const int PerRealm = 40;

    private static SpatialGridConfig Grid(double cellSize) => SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(World, World), cellSize);

    private static RealmConfig Realm(double cellSize) => RealmConfig.SimulatedAlways(Grid(cellSize));

    /// <summary>Session 1: realm 0 (10 m), realm 1 (10 m), realm 2 (25 m), a population in each.</summary>
    private void CreateThreeRealms()
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<RealmPos>();
        dbe.ConfigureRealms(3);
        dbe.ConfigureSpatialGrid(Grid(10));
        dbe.Realms.Register(new RealmId(1), Realm(10));
        dbe.Realms.Register(new RealmId(2), Realm(25));
        dbe.InitializeArchetypes();
        Assert.That(dbe.PersistedRealmCatalog, Has.Count.EqualTo(2), "realms 1 and 2 are catalogued; realm 0 keeps its own record");

        using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
        {
            for (var tag = 0; tag < PerRealm; tag++)
            {
                for (ushort r = 0; r < 3; r++)
                {
                    tx.Spawn<RealmUnit>(RealmUnit.Pos.Set(new RealmPos
                    {
                        Bounds = new AABB2F { MinX = 1 + tag * 2, MinY = 50, MaxX = 1 + tag * 2, MaxY = 50 }, Realm = r, Tag = tag,
                    }));
                }
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);
    }

    private static int CountIn(DatabaseEngine dbe, ushort realm)
    {
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        return dbe.ClusterSpatialQuery<RealmUnit>(new RealmId(realm)).AABB(new AABB2F { MinX = 0, MinY = 0, MaxX = World, MaxY = World }).Count();
    }

    [Test]
    [CancelAfter(30_000)]
    [VerifiesRule("RLM-01")]
    public void GenericOpener_ReconstructsEveryRealmFromTheCatalog()
    {
        CreateThreeRealms();

        // No ConfigureRealms, no Register, no ConfigureSpatialGrid: what a Workbench or `typhon check` open does.
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<RealmPos>();
        Assert.DoesNotThrow(() => dbe.InitializeArchetypes());

        Assert.That(dbe.RealmTable.MaxRealms, Is.EqualTo(3), "the realm count is the file's own");
        Assert.That(dbe.RealmTable.Get(2).GridConfig.CellSize, Is.EqualTo(25d), "realm 2's identity comes back from the catalog");
        for (ushort r = 0; r < 3; r++)
        {
            Assert.That(CountIn(dbe, r), Is.EqualTo(PerRealm), $"realm {r}");
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public void Reopen_WithTheSameRegistrations_WritesNothingNew()
    {
        CreateThreeRealms();

        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<RealmPos>();
        dbe.ConfigureRealms(3);
        dbe.ConfigureSpatialGrid(Grid(10));
        dbe.Realms.Register(new RealmId(1), Realm(10));
        dbe.Realms.Register(new RealmId(2), Realm(25));
        dbe.InitializeArchetypes();
        Assert.That(dbe.PersistedRealmCatalog, Has.Count.EqualTo(2));
        Assert.That(CountIn(dbe, 2), Is.EqualTo(PerRealm));
    }

    [Test]
    [CancelAfter(30_000)]
    public void Register_IdentityDifferingFromTheCatalog_IsRefused()
    {
        CreateThreeRealms();

        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<RealmPos>();
        dbe.ConfigureRealms(3);
        dbe.Realms.Register(new RealmId(2), Realm(20));
        var ex = Assert.Throws<InvalidOperationException>(() => dbe.InitializeArchetypes());
        Assert.That(ex.Message, Does.Contain("Realm 2").And.Contain("differs"));
    }

    [Test]
    [CancelAfter(30_000)]
    [VerifiesRule("RLM-02")]
    public void ConfigureRealms_BelowTheCatalogsHighestId_IsRefused_NeverClamped()
    {
        CreateThreeRealms();

        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<RealmPos>();
        dbe.ConfigureRealms(2);
        var ex = Assert.Throws<InvalidOperationException>(() => dbe.InitializeArchetypes());
        Assert.That(ex.Message, Does.Contain("ConfigureRealms(2)").And.Contain("never clamped"));
    }

    [Test]
    [CancelAfter(30_000)]
    [VerifiesRule("RLM-01")]
    public void Open_ClusterInARealmTheCatalogLost_IsRefused()
    {
        CreateThreeRealms();

        // Corrupt the catalog: realm 2's entry now names realm 5 — realm 2 is gone from it while its clusters remain.
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<RealmPos>();
            dbe.InitializeArchetypes();
            dbe.RenumberRealmCatalogEntryForTest(2, 5);
        }

        using var reopen = ServiceProvider.CreateScope();
        using var engine = reopen.ServiceProvider.GetRequiredService<DatabaseEngine>();
        engine.RegisterComponentFromAccessor<RealmPos>();
        var ex = Assert.Throws<InvalidOperationException>(() => engine.InitializeArchetypes());
        Assert.That(ex.Message, Does.Contain("realm 2").And.Contain("not registered"));
    }

    [Test]
    [CancelAfter(30_000)]
    public void CatalogWithADuplicateRealmId_IsRefusedAsCorrupt()
    {
        CreateThreeRealms();
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<RealmPos>();
            dbe.InitializeArchetypes();
            dbe.RenumberRealmCatalogEntryForTest(2, 1);
        }

        // Refused as the system tables load — at engine construction, before anything could file a cluster with the wrong identity.
        using var reopen = ServiceProvider.CreateScope();
        var ex = Assert.Catch<Exception>(() => reopen.ServiceProvider.GetRequiredService<DatabaseEngine>());
        Assert.That(ex.ToString(), Does.Contain("catalog is corrupt"), "realm 1 must not silently take realm 2's identity");
    }

    [Test]
    [CancelAfter(30_000)]
    public void AnOpenThatFails_LeavesNoCatalogIdentityBehind()
    {
        // Session 1 fails its open (an unkeyed spatial archetype without realm 0), after registering realm 2 with a wrong cell size.
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<RealmPos>();
            dbe.RegisterComponentFromAccessor<Typhon.Engine.Tests.Runtime.TierPos>();
            dbe.ConfigureRealms(3);
            dbe.Realms.Register(new RealmId(2), Realm(20));
            Assert.Catch<Exception>(() => dbe.InitializeArchetypes());
        }

        // Session 2 corrects it: nothing from the failed open refuses the new identity.
        using var reopen = ServiceProvider.CreateScope();
        using var engine = reopen.ServiceProvider.GetRequiredService<DatabaseEngine>();
        engine.RegisterComponentFromAccessor<RealmPos>();
        engine.ConfigureRealms(3);
        engine.Realms.Register(new RealmId(2), Realm(25));
        Assert.DoesNotThrow(() => engine.InitializeArchetypes());
        Assert.That(engine.RealmTable.Get(2).GridConfig.CellSize, Is.EqualTo(25d));
    }

    [Test]
    public void RealmConfig_WithADefaultGrid_IsRefused()
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.ConfigureRealms(2);
        Assert.Throws<ArgumentException>(() => dbe.Realms.Register(new RealmId(1), new RealmConfig
        {
            Grid = default, WhenUnobserved = RealmUnobserved.Simulate, UnobservedTickDivisor = 1,
        }));
    }
}
