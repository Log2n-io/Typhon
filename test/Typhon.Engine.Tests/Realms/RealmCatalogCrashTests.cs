using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Realms;

/// <summary>
/// Realms C2: the catalog is written synchronously at registration, because the spatial rebuild at the next open runs BEFORE the WAL is replayed —
/// a realm known only to the WAL would be unknown exactly when its clusters are filed (02-runtime-lifecycle §3, "ordering trap").
/// </summary>
[TestFixture]
[NonParallelizable]
class RealmCatalogCrashTests : TestBase<RealmCatalogCrashTests>
{
    /// <summary>Reopen needs WAL segments that outlive an engine dispose.</summary>
    protected override IWalFileIO CreateWalFileIO() => new WalFileIO();

    /// <summary>No periodic checkpoint: one landing before the crash would persist the catalog page by another route and prove nothing.</summary>
    protected override void ConfigureEngineOptions(DatabaseEngineOptions o)
    {
        base.ConfigureEngineOptions(o);
        o.Resources.CheckpointIntervalMs = int.MaxValue;
    }

    [Test]
    [CancelAfter(30_000)]
    [VerifiesRule("RLM-01")]
    public void HardCrashBeforeAnyCheckpoint_TheCatalogStillNamesTheRealm()
    {
        const int population = 30;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<RealmPos>();
            dbe.ConfigureRealms(3);
            dbe.Realms.Register(new RealmId(2), RealmConfig.SimulatedAlways(SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(100, 100), 25)));
            dbe.InitializeArchetypes();
            using (var uow = dbe.CreateUnitOfWork())
            {
                using (var tx = uow.CreateTransaction(CommitDiscipline.Commit))
                {
                    for (var i = 0; i < population; i++)
                    {
                        tx.Spawn<RealmUnit>(RealmUnit.Pos.Set(new RealmPos
                        {
                            Bounds = new AABB2F { MinX = 3 * i + 1, MinY = 40, MaxX = 3 * i + 1, MaxY = 40 }, Realm = 2, Tag = i,
                        }));
                    }

                    Assert.That(tx.Commit(), Is.True);
                }

                uow.Flush();
            }

            dbe.SimulateHardCrash();
        }

        // A generic opener: realm 2 can only come from the catalog, and the replayed spawns only find a home if it did.
        using var reopen = ServiceProvider.CreateScope();
        using var engine = reopen.ServiceProvider.GetRequiredService<DatabaseEngine>();
        engine.RegisterComponentFromAccessor<RealmPos>();
        engine.InitializeArchetypes();
        Assert.That(engine.RealmTable.IsRegistered(2), Is.True, "the catalog entry must have survived the crash");
        using var epoch = EpochGuard.Enter(engine.EpochManager);
        Assert.That(engine.ClusterSpatialQuery<RealmUnit>(new RealmId(2)).AABB(new AABB2F { MinX = 0, MinY = 0, MaxX = 100, MaxY = 100 }).Count(),
            Is.EqualTo(population), "every replayed spawn must be filed in realm 2");
    }
}
