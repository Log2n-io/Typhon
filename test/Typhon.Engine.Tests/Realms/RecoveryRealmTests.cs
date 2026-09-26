using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Realms;

/// <summary>
/// Realms C5: the rebuild checks every slot's realm, not only the first. A crash-recovery replay claims slots cell- and realm-agnostically, and a realm
/// change committed but never fenced is in the file with its old cluster: the reopen files each cluster in its realm, leaves other-realm slots out of
/// its box, hands them to the first fence, and rewrites an invalid key to the cluster's realm (D-2).
/// </summary>
[TestFixture]
[NonParallelizable]
class RecoveryRealmTests : TestBase<RecoveryRealmTests>
{
    /// <summary>Reopen needs WAL segments that outlive an engine dispose.</summary>
    protected override IWalFileIO CreateWalFileIO() => new WalFileIO();

    /// <summary>No periodic checkpoint: one landing before the crash would empty the replay window.</summary>
    protected override void ConfigureEngineOptions(DatabaseEngineOptions o)
    {
        base.ConfigureEngineOptions(o);
        o.Resources.CheckpointIntervalMs = int.MaxValue;
    }

    private static readonly AABB2F Everywhere = new() { MinX = 0, MinY = 0, MaxX = 100, MaxY = 100 };

    private static SpatialGridConfig Grid(double cellSize) => SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(100, 100), cellSize);

    private static RealmPos At(float x, float y, ushort realm, int tag) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Realm = realm, Tag = tag };

    private static void Configure(DatabaseEngine dbe)
    {
        dbe.RegisterComponentFromAccessor<RealmPos>();
        dbe.ConfigureRealms(3);
        dbe.ConfigureSpatialGrid(Grid(10));
        dbe.Realms.Register(new RealmId(2), RealmConfig.SimulatedAlways(Grid(25)));
        dbe.InitializeArchetypes();
    }

    private static ArchetypeClusterState StateOf(DatabaseEngine dbe) => dbe._archetypeStates[Archetype<RealmUnit>.Metadata.ArchetypeId].ClusterState;

    /// <summary>Runs <paramref name="body"/> in a Commit-discipline transaction (durable through its own WAL record), flushes, then crashes.</summary>
    private void SessionThenCrash(Action<DatabaseEngine, Transaction> body, bool fenceFirst = false, Action<DatabaseEngine> before = null)
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        Configure(dbe);
        before?.Invoke(dbe);
        if (fenceFirst)
        {
            dbe.WriteTickFence(1);
        }

        using (var uow = dbe.CreateUnitOfWork())
        {
            using (var tx = uow.CreateTransaction(CommitDiscipline.Commit))
            {
                body(dbe, tx);
                Assert.That(tx.Commit(), Is.True);
            }

            uow.Flush();
        }

        dbe.SimulateHardCrash();
    }

    private static HashSet<int> TagsIn(DatabaseEngine dbe, ushort realm)
    {
        var ids = new List<EntityId>();
        using (EpochGuard.Enter(dbe.EpochManager))
        {
            foreach (var hit in dbe.ClusterSpatialQuery<RealmUnit>(new RealmId(realm)).AABB(Everywhere))
            {
                ids.Add(hit.Entity);
            }
        }

        var tags = new HashSet<int>();
        using var tx = dbe.CreateQuickTransaction();
        foreach (var id in ids)
        {
            var v = tx.Open(id).Read(RealmUnit.Pos);
            Assert.That(v.Realm, Is.EqualTo(realm), $"realm {realm}'s query returned an entity of realm {v.Realm}");
            tags.Add(v.Tag);
        }

        return tags;
    }

    [Test]
    [CancelAfter(30_000)]
    [VerifiesRule("RM-06")]
    public void RecoveredSpawnsInterleavedAcrossRealms_AreSplitAtTheFirstFence()
    {
        const int n = 60;
        SessionThenCrash((_, tx) =>
        {
            for (var i = 0; i < n; i++)
            {
                tx.Spawn<RealmUnit>(RealmUnit.Pos.Set(At(5 + i % 10, 5 + i / 10, (ushort)(i % 2 == 0 ? 0 : 2), i)));
            }
        });

        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        Configure(dbe);
        var cs = StateOf(dbe);
        Assert.That(cs.LastRebuildForeignRealmSlots, Is.GreaterThan(0),
            "the replay did not mix realms in a cluster, so this fixture would pass with the per-slot realm check deleted");

        var even = new HashSet<int>();
        var odd = new HashSet<int>();
        for (var i = 0; i < n; i++)
        {
            (i % 2 == 0 ? even : odd).Add(i);
        }

        // Before the fence: realm 0 answers only its own (the narrowphase drops the other realm's slots its clusters still hold).
        Assert.That(TagsIn(dbe, 0), Is.SubsetOf(even));

        dbe.WriteTickFence(1);
        Assert.That(TagsIn(dbe, 0), Is.EquivalentTo(even), "after the first fence every realm-0 entity answers realm 0");
        Assert.That(TagsIn(dbe, 2), Is.EquivalentTo(odd), "and every realm-2 entity has moved into realm 2");
    }

    [Test]
    [CancelAfter(30_000)]
    [VerifiesRule("RM-06")]
    public void CommittedTeleport_NeverFenced_IsMovedAtTheFirstFenceAfterReopen()
    {
        EntityId id = default;
        SessionThenCrash((_, tx) => tx.Teleport(id, RealmUnit.Pos, new RealmId(2), At(80, 80, 2, 1)),
            fenceFirst: true,
            before: dbe =>
            {
                using var t = dbe.CreateUnitOfWork();
                using (var tx = t.CreateTransaction(CommitDiscipline.Commit))
                {
                    id = tx.Spawn<RealmUnit>(RealmUnit.Pos.Set(At(5, 5, 0, 1)));
                    tx.Commit();
                }

                t.Flush();
            });

        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        Configure(dbe);
        Assert.That(TagsIn(dbe, 0), Is.Empty, "the teleported entity must not answer its old realm");
        dbe.WriteTickFence(1);
        Assert.That(TagsIn(dbe, 2), Is.EquivalentTo(new[] { 1 }));
        Assert.That(TagsIn(dbe, 0), Is.Empty);
    }

    [Test]
    [CancelAfter(30_000)]
    [VerifiesRule("RM-05")]
    public void InvalidKeyInTheFile_IsRewrittenToItsClustersRealmAtRebuild()
    {
        EntityId id = default;
        SessionThenCrash((_, tx) =>
            {
                ref var pos = ref tx.OpenMut(id).Write(RealmUnit.Pos);
                pos.Realm = 1;   // unregistered: a raw write no validation sees
            },
            fenceFirst: true,
            before: dbe =>
            {
                using var t = dbe.CreateUnitOfWork();
                using (var tx = t.CreateTransaction(CommitDiscipline.Commit))
                {
                    id = tx.Spawn<RealmUnit>(RealmUnit.Pos.Set(At(5, 5, 0, 3)));
                    tx.Spawn<RealmUnit>(RealmUnit.Pos.Set(At(6, 6, 0, 4)));
                    tx.Commit();
                }

                t.Flush();
            });

        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        Assert.DoesNotThrow(() => Configure(dbe), "one bad key must not brick the database");
        Assert.That(StateOf(dbe).LastRebuildRealmKeyReverts, Is.EqualTo(1));
        Assert.That(TagsIn(dbe, 0), Is.EquivalentTo(new[] { 3, 4 }));
    }
}
