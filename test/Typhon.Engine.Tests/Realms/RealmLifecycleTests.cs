using System;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Realms;

/// <summary>
/// Realms D5 (RT-7, RLM-06): realms registered and unregistered on a running engine. Register publishes a complete realm and writes its catalog row;
/// Unregister closes it — entries refused, removal at the first fence that finds it empty; its id is reusable only after an open retires it.
/// </summary>
[TestFixture]
[NonParallelizable]
class RealmLifecycleTests : TestBase<RealmLifecycleTests>
{
    /// <summary>Reopen needs WAL segments that outlive an engine dispose.</summary>
    protected override IWalFileIO CreateWalFileIO() => new WalFileIO();

    /// <summary>No periodic checkpoint: the crash test must depend on nothing but the catalog's own synchronous writes and the WAL.</summary>
    protected override void ConfigureEngineOptions(DatabaseEngineOptions o)
    {
        base.ConfigureEngineOptions(o);
        o.Resources.CheckpointIntervalMs = int.MaxValue;
    }

    private static SpatialGridConfig Grid() => SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(100, 100), 10);

    private static readonly AABB2F Everywhere = new() { MinX = 0, MinY = 0, MaxX = 100, MaxY = 100 };

    private static RealmPos At(float x, float y, ushort realm, int tag = 0) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Realm = realm, Tag = tag };

    private static DatabaseEngine Open(IServiceScope scope, bool configure = true)
    {
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<RealmPos>();
        if (configure)
        {
            dbe.ConfigureRealms(8);
            dbe.ConfigureSpatialGrid(Grid());
        }

        dbe.InitializeArchetypes();
        return dbe;
    }

    private static int Count(DatabaseEngine dbe, ushort realm)
    {
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        return dbe.ClusterSpatialQuery<RealmUnit>(new RealmId(realm)).AABB(Everywhere).Count();
    }

    private static void SpawnIn(DatabaseEngine dbe, ushort realm, int n)
    {
        using var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
        for (var i = 0; i < n; i++)
        {
            tx.Spawn<RealmUnit>(RealmUnit.Pos.Set(At(5 + (10 * i), 50, realm, i)));
        }

        tx.Commit();
    }

    private static void Empty(DatabaseEngine dbe, ushort realm)
    {
        using var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
        dbe.Realms.DestroyContents(new RealmId(realm), tx);
        tx.Commit();
    }

    [Test]
    [VerifiesRule("RLM-06")]
    public void Register_OnARunningEngine_ThenReopen_TheCatalogRebuildsIt()
    {
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = Open(scope);
            dbe.Realms.Register(new RealmId(5), RealmConfig.SimulatedAlways(Grid()));
            Assert.That(dbe.RealmTable.IsRegistered(5), Is.True);
            SpawnIn(dbe, 5, 3);
            dbe.WriteTickFence(1);
            Assert.That((Count(dbe, 5), Count(dbe, 0)), Is.EqualTo((3, 0)));
        }

        using var reopen = ServiceProvider.CreateScope();
        using var engine = Open(reopen, configure: false);
        Assert.That(engine.RealmTable.IsRegistered(5), Is.True, "the run-time registration's catalog row names it at the next open");
        Assert.That(Count(engine, 5), Is.EqualTo(3));
    }

    [Test]
    [VerifiesRule("RLM-06")]
    public void Unregister_NonEmpty_StaysClosing_AndRefusesEntries()
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = Open(scope);
        dbe.Realms.Register(new RealmId(3), RealmConfig.SimulatedAlways(Grid()));
        SpawnIn(dbe, 3, 2);
        EntityId outsider;
        using (var tx = dbe.CreateQuickTransaction())
        {
            outsider = tx.Spawn<RealmUnit>(RealmUnit.Pos.Set(At(50, 50, 0)));
            tx.Commit();
        }

        dbe.WriteTickFence(1);
        dbe.Realms.Unregister(new RealmId(3));
        Assert.That(dbe.Realms.StateOf(new RealmId(3)), Is.EqualTo(RealmRunState.Closing));

        using (var tx = dbe.CreateQuickTransaction())
        {
            Assert.Throws<InvalidOperationException>(() => tx.Spawn<RealmUnit>(RealmUnit.Pos.Set(At(1, 1, 3))), "no spawn into a closing realm");
            Assert.Throws<InvalidOperationException>(() => tx.Teleport(outsider, RealmUnit.Pos, new RealmId(3), At(1, 1, 3)), "no teleport into it");
        }

        for (var t = 2; t < 5; t++)
        {
            dbe.WriteTickFence(t);
        }

        Assert.That(dbe.RealmTable.IsRegistered(3), Is.True, "a closing realm with entities stays");
        Assert.That(Count(dbe, 3), Is.EqualTo(2), "its entities are still there, and still answer its queries");
    }

    [Test]
    [VerifiesRule("RLM-06")]
    public void Unregister_Empty_RemovedAtTheFence_TheIdQuarantinedUntilAnOpenRetiresIt()
    {
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = Open(scope);
            dbe.Realms.Register(new RealmId(4), RealmConfig.SimulatedAlways(Grid()));
            SpawnIn(dbe, 4, 3);
            dbe.WriteTickFence(1);
            var cs = dbe._archetypeStates[Archetype<RealmUnit>.Metadata.ArchetypeId].ClusterState;
            Assert.That(cs.RealmSpatial[4], Is.Not.Null);

            dbe.Realms.Unregister(new RealmId(4));
            Empty(dbe, 4);
            dbe.WriteTickFence(2);

            Assert.That(dbe.RealmTable.IsRegistered(4), Is.False, "emptied, it is removed at the fence");
            Assert.That(cs.RealmSpatial[4], Is.Null, "with its per-archetype state");
            Assert.Throws<InvalidOperationException>(() => dbe.Realms.Register(new RealmId(4), RealmConfig.SimulatedAlways(Grid())),
                "its id is not reusable in the session that unregistered it");
        }

        using var reopen = ServiceProvider.CreateScope();
        using var engine = Open(reopen);
        Assert.That(engine.RealmTable.IsRegistered(4), Is.False, "an open that found it empty retired its catalog row");
        engine.Realms.Register(new RealmId(4), RealmConfig.SimulatedAlways(SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(100, 100), 25)));
        Assert.That(engine.PersistedRealmCatalog[4].Row.Generation, Is.EqualTo(1), "the retired row is reused, next incarnation");
        Assert.That(engine.RealmTable.Get(4).GridConfig.CellSize, Is.EqualTo(25d), "a new identity: the id is free");
    }

    [Test]
    [CancelAfter(30_000)]
    [VerifiesRule("RLM-06")]
    public void CrashAfterUnregister_TheRealmIsNeverResurrected()
    {
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            dbe.Realms.Register(new RealmId(6), RealmConfig.SimulatedAlways(Grid()));
            SpawnIn(dbe, 6, 4);
            dbe.WriteTickFence(1);
            dbe.Realms.Unregister(new RealmId(6));
            Empty(dbe, 6);
            dbe.WriteTickFence(2);
            Assert.That(dbe.RealmTable.IsRegistered(6), Is.False);
            dbe.SimulateHardCrash();
        }

        // A generic opener: the catalog says Closing; the data pages may still hold the entities (no checkpoint ran), the WAL replays their destroys.
        using var reopen = ServiceProvider.CreateScope();
        using var engine = Open(reopen, configure: false);
        Assert.That(engine.RealmTable.IsRegistered(6), Is.False, "empty after recovery, the closing realm is retired, not resurrected");
        Assert.That(engine.PersistedRealmCatalog?.ContainsKey(6) ?? false, Is.False);
    }
}
