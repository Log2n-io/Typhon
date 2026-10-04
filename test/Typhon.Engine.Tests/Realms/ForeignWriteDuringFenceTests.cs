using System.Numerics;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Engine.Tests.Runtime;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Realms;

/// <summary>
/// A spatial write committed by a transaction on another thread WHILE a fence runs must be processed by a later fence, never discarded by this one. The SWG
/// demo's realm crossings were lost this way under load: the player's realm key said the interior, the engine still filed it on the planet, and the session
/// following it was never switched.
/// </summary>
[TestFixture]
[NonParallelizable]
class ForeignWriteDuringFenceTests : TestBase<ForeignWriteDuringFenceTests>
{
    private static SpatialGridConfig Realm0Grid() => SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(100, 100), 10);

    private static SpatialGridConfig Realm1Grid() => SpatialGridConfig.Flat(new Vector2(-40, -40), new Vector2(40, 40), 10);

    private static RealmPos At(float x, float y, ushort realm) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Realm = realm };

    private DatabaseEngine SetupEngine(bool barrierOnly)
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<RealmPos>();
        dbe.ConfigureRealms(4);
        dbe.ConfigureSpatialGrid(Realm0Grid());
        dbe.InitializeArchetypes();
        dbe.Realms.Register(new RealmId(1), RealmConfig.SimulatedAlways(Realm1Grid()));
        if (barrierOnly)
        {
            // As the SWG demo declares its movers: only flagged writes are seen by the fence, no dirty-slot scan.
            dbe.SetSpatialBarrierOnly<RealmUnit>();
        }

        return dbe;
    }

    private static ushort RealmOf(DatabaseEngine dbe, EntityId entity)
    {
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var probe = new BoundViewpoint(dbe);
        try
        {
            Assert.That(probe.TryRealm(entity, out var realm), Is.True, "the entity is filed somewhere");
            return realm;
        }
        finally
        {
            probe.Dispose();
        }
    }

    /// <summary>
    /// The SWG demo's own pattern: a transaction whose writes land at COMMIT (<see cref="CommitDiscipline.Commit"/>) calls <c>Teleport</c>, and a fence runs
    /// before it commits. The flag Teleport raised is consumed by that fence against the old position; the commit must still be seen by a later one.
    /// </summary>
    [Test]
    [VerifiesRule("CC-02")]
    public void ATeleportWhoseCommitStraddlesAFence_IsStillFiledInItsNewRealm([Values] bool barrierOnly)
    {
        using var dbe = SetupEngine(barrierOnly);
        EntityId entity;
        using (var tx = dbe.CreateQuickTransaction())
        {
            entity = tx.Spawn<RealmUnit>(RealmUnit.Pos.Set(At(5, 25, 0)));
            tx.Commit();
        }

        dbe.WriteTickFence(1);

        // A transaction is thread-affine, so it lives on one thread from open to commit; the fence runs between its Teleport and its Commit.
        using var teleported = new System.Threading.ManualResetEventSlim();
        using var fenced = new System.Threading.ManualResetEventSlim();
        var writer = Task.Run(() =>
        {
            using var tx = dbe.CreateQuickTransaction(DurabilityMode.GroupCommit, CommitDiscipline.Commit);
            tx.Teleport(entity, RealmUnit.Pos, new RealmId(1), At(0, 0, 1));
            teleported.Set();
            fenced.Wait();
            Assert.That(tx.Commit(), Is.True);
        });

        teleported.Wait();
        dbe.WriteTickFence(2);
        fenced.Set();
        writer.Wait();

        dbe.WriteTickFence(3);
        dbe.WriteTickFence(4);
        Assert.That(RealmOf(dbe, entity), Is.EqualTo(1), "the committed teleport was processed: the entity is filed in realm 1");
    }

    private static void Teleport(DatabaseEngine dbe, EntityId entity, ushort realm, RealmPos to)
    {
        using var tx = dbe.CreateQuickTransaction();
        tx.Teleport(entity, RealmUnit.Pos, new RealmId(realm), to);
        Assert.That(tx.Commit(), Is.True);
    }

    [Test]
    [VerifiesRule("CC-02")]
    public void ATeleportIsNeverLost([Values] bool duringFence, [Values] bool barrierOnly)
    {
        using var dbe = SetupEngine(barrierOnly);
        EntityId entity;
        using (var tx = dbe.CreateQuickTransaction())
        {
            entity = tx.Spawn<RealmUnit>(RealmUnit.Pos.Set(At(5, 25, 0)));
            tx.Commit();
        }

        dbe.WriteTickFence(1);
        Assert.That(RealmOf(dbe, entity), Is.Zero, "precondition: filed in realm 0");

        var clusters = dbe._archetypeStates[Archetype<RealmUnit>.Metadata.ArchetypeId].ClusterState;
        if (duringFence)
        {
            // From another thread, as an application's would be, and while this fence is between consuming the tick's flags and clearing them.
            var fired = false;
            clusters.BeforeBookkeepingClearProbe = () =>
            {
                if (fired)
                {
                    return;
                }

                fired = true;
                Task.Run(() => Teleport(dbe, entity, 1, At(0, 0, 1))).Wait();
            };
            dbe.WriteTickFence(2);
            clusters.BeforeBookkeepingClearProbe = null;
            Assert.That(fired, Is.True, "precondition: the write landed inside the fence");
        }
        else
        {
            Teleport(dbe, entity, 1, At(0, 0, 1));
            dbe.WriteTickFence(2);
        }

        dbe.WriteTickFence(3);
        dbe.WriteTickFence(4);
        Assert.That(RealmOf(dbe, entity), Is.EqualTo(1), "the teleport was processed: the entity is filed in realm 1");
    }
}
