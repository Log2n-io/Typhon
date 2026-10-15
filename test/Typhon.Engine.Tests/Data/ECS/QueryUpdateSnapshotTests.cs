using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

[Component("Typhon.Test.Vis12.Pos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct Vis12Pos
{
    [Field]
    [SpatialIndex]
    public AABB2F Bounds;
}

[Component("Typhon.Test.Vis12.Rank", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential)]
struct Vis12Rank
{
    [Index(AllowMultiple = true)]
    public int Tier;
    public int Score;
}

/// <summary>A spatial archetype with an indexed Versioned component: its entities migrate between clusters at the fence (VIS-12).</summary>
[Archetype]
partial class Vis12Unit : Archetype<Vis12Unit>
{
    public static readonly Comp<Vis12Pos> Pos = Register<Vis12Pos>();
    public static readonly Comp<Vis12Rank> Rank = Register<Vis12Rank>();
}

/// <summary>
/// The value half of snapshot isolation (#674, VIS-12): a reader older than an update sees the values of its snapshot, on every query path. The SoA scan
/// evaluated its predicates on the cluster's HEAD, the index paths read trees keyed by it, and both hold the newest committed values — so a value
/// committed after the snapshot changed the reader's answer. A cluster an update newer than the snapshot touched is now read at the snapshot.
/// </summary>
[TestFixture]
[NonParallelizable] // QueryPathProbe.Forced is a static every engine reads.
class QueryUpdateSnapshotTests : TestBase<QueryUpdateSnapshotTests>
{
    private static readonly (string Name, ClusterScanPath Path)[] Paths =
        [("the planner's path", ClusterScanPath.Planner), ("the index path", ClusterScanPath.Selective), ("the scan path", ClusterScanPath.FullScan)];

    private DatabaseEngine OpenEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<StatsRanked>();
        dbe.RegisterComponentFromAccessor<StatsTag>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static List<EntityId> Spawn(DatabaseEngine dbe, params int[] tiers)
    {
        var ids = new List<EntityId>();
        using var tx = dbe.CreateQuickTransaction();
        foreach (var tier in tiers)
        {
            ids.Add(tx.Spawn<StatsArch>(StatsArch.Ranked.Set(new StatsRanked(tier, tier * 10)), StatsArch.Tag.Set(new StatsTag(tier))));
        }

        Assert.That(tx.Commit(), Is.True);
        return ids;
    }

    private static void Update(DatabaseEngine dbe, EntityId id, int tier, int score)
    {
        using var tx = dbe.CreateQuickTransaction();
        tx.OpenMut(id).Set(StatsArch.Ranked, new StatsRanked(tier, score));
        Assert.That(tx.Commit(), Is.True, "premise: the update commits");
    }

    private static void OnEachPath(Action<string> check)
    {
        foreach (var (name, path) in Paths)
        {
            QueryPathProbe.Reset();
            QueryPathProbe.Forced = path;
            try
            {
                check(name);
            }
            finally
            {
                QueryPathProbe.Reset();
            }
        }
    }

    /// <summary>Count, Any, Execute and ExecuteOrdered of Tier == <paramref name="tier"/> return exactly <paramref name="expected"/>.</summary>
    private static void AssertTier(Transaction tx, int tier, IReadOnlyList<EntityId> expected, string at)
    {
        EcsQuery<StatsArch> Q() => tx.Query<StatsArch>().WhereField<StatsRanked>(x => x.Tier == tier);
        Assert.That(Q().Count(), Is.EqualTo(expected.Count), $"{at}: Count");
        Assert.That(Q().Any(), Is.EqualTo(expected.Count > 0), $"{at}: Any");
        Assert.That(Q().Execute(), Is.EquivalentTo(expected), $"{at}: Execute");
        Assert.That(Q().OrderByField<StatsRanked, int>(x => x.Tier).ExecuteOrdered(), Is.EquivalentTo(expected), $"{at}: ExecuteOrdered");
    }

    [Test]
    [VerifiesRule("VIS-12")]
    public void AReaderOlderThanUpdates_SeesItsSnapshotsValues_OnEveryPathAndTerminal()
    {
        using var dbe = OpenEngine();
        var ids = Spawn(dbe, Enumerable.Range(0, 150).Select(i => i % 3 == 0 ? 5 : 7).ToArray());
        var fives = ids.Where((_, i) => i % 3 == 0).ToList();
        var sevens = ids.Where((_, i) => i % 3 != 0).ToList();
        using var reader = dbe.CreateQuickTransaction();
        Update(dbe, fives[1], 7, 70);     // leaves tier 5 after the snapshot
        Update(dbe, sevens[2], 5, 50);    // joins it after the snapshot

        OnEachPath(name =>
        {
            AssertTier(reader, 5, fives, $"tier 5 at a snapshot older than the updates, on {name}");
            AssertTier(reader, 7, sevens, $"tier 7 at a snapshot older than the updates, on {name}");
        });

        using var fresh = dbe.CreateQuickTransaction();
        var newFives = fives.Where(id => id != fives[1]).Append(sevens[2]).ToList();
        OnEachPath(name => AssertTier(fresh, 5, newFives, $"a snapshot after the updates, on {name}"));
    }

    /// <summary>
    /// A predicate the index range does not enforce — on a second field — is evaluated on the value at the snapshot too: the index path verified it on the
    /// cluster's HEAD bytes.
    /// </summary>
    [Test]
    [VerifiesRule("VIS-12")]
    public void APredicateOnASecondField_IsEvaluatedAtTheSnapshot()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<QipTwoIndexed>();
        dbe.RegisterComponentFromAccessor<StatsTag>();
        dbe.InitializeArchetypes();
        var ids = new List<EntityId>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 60; i++)
            {
                ids.Add(tx.Spawn<QipArch>(QipArch.Ranked.Set(new QipTwoIndexed(i % 2 == 0 ? 5 : 7, 50)), QipArch.Tag.Set(new StatsTag(i))));
            }

            Assert.That(tx.Commit(), Is.True);
        }

        var fives = ids.Where((_, i) => i % 2 == 0).ToList();
        using var reader = dbe.CreateQuickTransaction();
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(fives[3]).Set(QipArch.Ranked, new QipTwoIndexed(5, 999));
            Assert.That(tx.Commit(), Is.True);
        }

        OnEachPath(name =>
        {
            EcsQuery<QipArch> Q() => reader.Query<QipArch>().WhereField<QipTwoIndexed>(x => x.Tier == 5 && x.Score == 50);
            Assert.That(Q().Execute(), Is.EquivalentTo(fives), $"Execute, on {name}");
            Assert.That(Q().Count(), Is.EqualTo(fives.Count), $"Count, on {name}");
        });
    }

    /// <summary>
    /// In a cluster another commit updated after the snapshot, the transaction's own unpublished write stays unseen by its field queries (QENABLE-01): the
    /// snapshot read takes the committed revision, not the transaction's own.
    /// </summary>
    [Test]
    [VerifiesRule("VIS-12")]
    public void ATransactionsOwnWrite_StaysUnseenByItsFieldQueries_InAClusterReadAtTheSnapshot()
    {
        using var dbe = OpenEngine();
        var ids = Spawn(dbe, 5, 5, 5, 5);
        using var tx = dbe.CreateQuickTransaction();
        Update(dbe, ids[0], 7, 70);   // another commit, after tx's snapshot: the cluster is read at the snapshot
        tx.OpenMut(ids[1]).Set(StatsArch.Ranked, new StatsRanked(7, 70));   // tx's own write, unpublished

        OnEachPath(name => AssertTier(tx, 5, ids, $"own write unseen, on {name}"));
    }

    [Test]
    [VerifiesRule("VIS-12")]
    public void AnEntityUpdatedThenDestroyedAfterTheSnapshot_IsFoundAtItsSnapshotValue()
    {
        using var dbe = OpenEngine();
        var ids = Spawn(dbe, 5, 5, 5);
        using var reader = dbe.CreateQuickTransaction();
        Update(dbe, ids[1], 7, 70);
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(ids[1]);
            Assert.That(tx.Commit(), Is.True);
        }

        OnEachPath(name => AssertTier(reader, 5, ids, $"updated then destroyed, on {name}"));
    }

    /// <summary>
    /// An update published after the ordered merge read the archetype's update count, before its streams read their trees: the merge reads the count again
    /// once done, finds the update in a cluster it did not read whole, and answers again — at the snapshot.
    /// </summary>
    [Test]
    [VerifiesRule("VIS-12")]
    public void AnUpdateAfterTheMergeReadTheUpdates_IsAnsweredAgain()
    {
        using var dbe = OpenEngine();
        var ids = Spawn(dbe, Enumerable.Range(1, 130).ToArray());   // tiers 1..130: more than one cluster
        using var reader = dbe.CreateQuickTransaction();
        Update(dbe, ids[0], 1, 11);   // the first cluster updated, so the merge reads that cluster whole at the snapshot

        QueryPathProbe.Reset();
        try
        {
            QueryPathProbe.OrderedAfterUpdateReadHook = () =>
            {
                QueryPathProbe.OrderedAfterUpdateReadHook = null;
                Update(dbe, ids[100], 1000, 0);   // a cluster the first read did not name: its tree key moves past every other
            };
            var ordered = reader.Query<StatsArch>().WhereField<StatsRanked>(x => x.Tier >= 1).OrderByField<StatsRanked, int>(x => x.Tier).ExecuteOrdered();
            Assert.That(QueryPathProbe.OrderedAfterUpdateReadHook, Is.Null, "premise: the update ran inside the query");
            Assert.That(ordered, Is.EqualTo(ids), "by the tiers the snapshot holds: 1 to 130");
        }
        finally
        {
            QueryPathProbe.Reset();
        }
    }

    /// <summary>
    /// An update published after the scan read a cluster's watermark, before it read the cluster's HEAD: the HEAD holds a value newer than the snapshot the
    /// watermark said it could not. The scan validates the watermark after the HEAD read and evaluates the cluster again at the snapshot. Both fields move,
    /// so whichever one the index path's range enforces, the other is verified on the bytes read.
    /// </summary>
    [Test]
    [VerifiesRule("VIS-12")]
    public void AnUpdateBetweenTheWatermarkAndTheHeadRead_IsReadAtTheSnapshot()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<QipTwoIndexed>();
        dbe.RegisterComponentFromAccessor<StatsTag>();
        dbe.InitializeArchetypes();
        var ids = new List<EntityId>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 8; i++)
            {
                ids.Add(tx.Spawn<QipArch>(QipArch.Ranked.Set(new QipTwoIndexed(5, 50)), QipArch.Tag.Set(new StatsTag(i))));
            }

            Assert.That(tx.Commit(), Is.True);
        }

        void Set(EntityId id, int tier, int score)
        {
            using var tx = dbe.CreateQuickTransaction();
            tx.OpenMut(id).Set(QipArch.Ranked, new QipTwoIndexed(tier, score));
            Assert.That(tx.Commit(), Is.True, "premise: the update commits");
        }

        foreach (var (name, path) in Paths)
        {
            using var reader = dbe.CreateQuickTransaction();
            QueryPathProbe.Reset();
            QueryPathProbe.Forced = path;
            try
            {
                var fired = false;
                QueryPathProbe.BeforeHeadEvaluateHook = () =>
                {
                    QueryPathProbe.BeforeHeadEvaluateHook = null;
                    fired = true;
                    Set(ids[2], 7, 999);   // leaves the predicate after the snapshot, in the cluster the scan is about to read
                };
                var found = reader.Query<QipArch>().WhereField<QipTwoIndexed>(x => x.Tier == 5 && x.Score == 50).Execute();
                Assert.That(fired, Is.True, $"premise: the update ran between the two reads, on {name}");
                Assert.That(found, Is.EquivalentTo(ids), $"the snapshot's matches, on {name}");
            }
            finally
            {
                QueryPathProbe.Reset();
            }

            Set(ids[2], 5, 50);   // back in the predicate for the next path
        }
    }

    private DatabaseEngine OpenSpatialEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<Vis12Pos>();
        dbe.RegisterComponentFromAccessor<Vis12Rank>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(worldMin: new Vector2(0, 0), worldMax: new Vector2(1000, 1000), cellSize: 100f));
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static Vis12Pos At(float x, float y) => new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y } };

    private static void AssertSpatialTier(Transaction reader, int tier, IReadOnlyList<EntityId> expected, string what)
    {
        OnEachPath(name =>
        {
            EcsQuery<Vis12Unit> Q() => reader.Query<Vis12Unit>().WhereField<Vis12Rank>(x => x.Tier == tier);
            Assert.That(Q().Execute(), Is.EquivalentTo(expected), $"{what}: Execute, on {name}");
            Assert.That(Q().Count(), Is.EqualTo(expected.Count), $"{what}: Count, on {name}");
            Assert.That(Q().OrderByField<Vis12Rank, int>(x => x.Tier).ExecuteOrdered(), Is.EquivalentTo(expected), $"{what}: ExecuteOrdered, on {name}");
        });
    }

    /// <summary>
    /// An entity updated after the snapshot, then migrated to another cluster at the fence: its HEAD and its tree entry move with it, so the destination
    /// carries the source's update watermark, and the archetype's count moves — the source's alone no longer names the cluster that holds the newer value.
    /// </summary>
    [Test]
    [VerifiesRule("VIS-12")]
    public void AnEntityUpdatedThenMigrated_IsReadAtTheSnapshot_InItsNewCluster()
    {
        using var dbe = OpenSpatialEngine();
        EntityId moved, stays;
        using (var tx = dbe.CreateQuickTransaction())
        {
            moved = tx.Spawn<Vis12Unit>(Vis12Unit.Pos.Set(At(50f, 50f)), Vis12Unit.Rank.Set(new Vis12Rank { Tier = 5, Score = 1 }));
            stays = tx.Spawn<Vis12Unit>(Vis12Unit.Pos.Set(At(60f, 50f)), Vis12Unit.Rank.Set(new Vis12Rank { Tier = 5, Score = 2 }));
            Assert.That(tx.Commit(), Is.True);
        }

        dbe.WriteTickFence(0);
        using var reader = dbe.CreateQuickTransaction();
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(moved).Set(Vis12Unit.Rank, new Vis12Rank { Tier = 7, Score = 1 });
            tx.OpenMut(moved).Set(Vis12Unit.Pos, At(550f, 750f));   // another cell: the fence migrates it
            Assert.That(tx.Commit(), Is.True);
        }

        dbe.WriteTickFence(1);
        using (var fresh = dbe.CreateQuickTransaction())
        {
            Assert.That(fresh.Query<Vis12Unit>().WhereField<Vis12Rank>(x => x.Tier == 7).Execute(), Is.EquivalentTo(new[] { moved }), "premise: the update");
        }

        AssertSpatialTier(reader, 5, [moved, stays], "updated then migrated");
        AssertSpatialTier(reader, 7, [], "updated then migrated");
    }

    /// <summary>
    /// The fence re-tightens a written cluster's zone maps from its HEAD: once every entity of a cluster left the tier after the snapshot, the cluster's map
    /// no longer covers the tier the snapshot holds. The scan does not prune a cluster an update newer than the snapshot rewrote.
    /// </summary>
    [Test]
    [VerifiesRule("VIS-12")]
    public void AClusterTheFenceReTightenedAfterAnUpdate_IsNotPrunedForAnOlderReader()
    {
        using var dbe = OpenSpatialEngine();
        var ids = new List<EntityId>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 4; i++)
            {
                ids.Add(tx.Spawn<Vis12Unit>(Vis12Unit.Pos.Set(At(50f + i, 50f)), Vis12Unit.Rank.Set(new Vis12Rank { Tier = 5, Score = i })));
            }

            Assert.That(tx.Commit(), Is.True);
        }

        dbe.WriteTickFence(0);
        using var reader = dbe.CreateQuickTransaction();

        // Every entity leaves tier 5, rewritten at each of 16 fences: one of them is the cluster's exact re-tighten pass (ZoneMapRetightenPeriod).
        for (var tick = 1; tick <= 16; tick++)
        {
            using (var tx = dbe.CreateQuickTransaction())
            {
                foreach (var id in ids)
                {
                    tx.OpenMut(id).Set(Vis12Unit.Rank, new Vis12Rank { Tier = 7, Score = tick });
                }

                Assert.That(tx.Commit(), Is.True);
            }

            dbe.WriteTickFence(tick);
        }

        AssertSpatialTier(reader, 5, ids, "re-tightened after the update");
    }

    /// <summary>
    /// An older commit can publish its update after a newer one: the archetype's newest stamp keeps the newer, the count moves for the late one too — the
    /// ordered merge's second read sees it — and the late cluster's watermark carries the older stamp, still above the snapshot of a reader older than both.
    /// </summary>
    [Test]
    [VerifiesRule("VIS-12")]
    public void AnOlderCommitPublishingAfterANewerOne_KeepsTheNewestStamp_AndMovesTheCount()
    {
        using var dbe = OpenEngine();
        var ids = Spawn(dbe, Enumerable.Range(1, 130).ToArray());   // more than one cluster
        var cs = dbe._archetypeStates[Archetype<StatsArch>.Metadata.ArchetypeId].ClusterState;
        using var reader = dbe.CreateQuickTransaction();
        using (dbe.CreateQuickTransaction())
        {
            // a later snapshot, so a stamp fits between the reader's and the update's
        }

        Update(dbe, ids[0], 1, 11);
        var newest = cs.MaxUpdateStamp;
        var count = cs.UpdateCount;
        var older = newest - 1;
        Assert.That(older, Is.GreaterThan(reader.TSN), "premise: the late stamp is above the reader's snapshot");

        long WatermarkOf(int clusterChunkId) => ArchetypeClusterState.MaxUpdatedOf(ref ArchetypeClusterState.SlotWords(cs.SlotWordTable, clusterChunkId));
        var active = cs.ReadActiveClusterList(out var activeCount);
        var late = -1;
        for (var c = 0; c < activeCount && late < 0; c++)
        {
            late = WatermarkOf(active[c]) < older ? active[c] : -1;
        }

        Assert.That(late, Is.GreaterThanOrEqualTo(0), "premise: a cluster the newer update did not touch");

        cs.NoteUpdatePublished(late, older);   // the older commit's publish, landing after the newer one's

        Assert.That(cs.MaxUpdateStamp, Is.EqualTo(newest), "the newest stamp is not lowered by an older one");
        Assert.That(cs.UpdateCount, Is.EqualTo(count + 1), "the count moves, so the merge's second read answers again");
        Assert.That(WatermarkOf(late), Is.EqualTo(older), "the late cluster carries its own stamp");
        Assert.That(WatermarkOf(late), Is.GreaterThan(reader.TSN), "the older reader's walk names the late cluster");
    }

    /// <summary>
    /// The index paths look at the cluster watermarks only when the archetype's newest update is newer than the snapshot: a reader begun after every update
    /// walks nothing, one older than an update walks them and answers at its snapshot.
    /// </summary>
    [Test]
    [VerifiesRule("VIS-12")]
    public void TheIndexPathsWalkTheWatermarks_OnlyForAReaderOlderThanAnUpdate()
    {
        using var dbe = OpenEngine();
        var ids = Spawn(dbe, 5, 5, 7);
        using var older = dbe.CreateQuickTransaction();
        Update(dbe, ids[0], 7, 70);
        using var newer = dbe.CreateQuickTransaction();

        foreach (var (name, path) in Paths.Where(p => p.Path == ClusterScanPath.Selective))   // the planner takes the scan path at this size
        {
            QueryPathProbe.Reset();
            QueryPathProbe.Forced = path;
            try
            {
                Assert.That(newer.Query<StatsArch>().WhereField<StatsRanked>(x => x.Tier == 5).Execute(), Is.EquivalentTo(new[] { ids[1] }), $"newer, {name}");
                Assert.That(QueryPathProbe.UpdateWatermarkWalks, Is.Zero, $"a reader newer than every update walks nothing, on {name}");
                Assert.That(older.Query<StatsArch>().WhereField<StatsRanked>(x => x.Tier == 5).Execute(), Is.EquivalentTo(new[] { ids[0], ids[1] }),
                    $"older, {name}");
                Assert.That(QueryPathProbe.UpdateWatermarkWalks, Is.GreaterThan(0), $"premise: the older reader walked the watermarks, on {name}");
            }
            finally
            {
                QueryPathProbe.Reset();
            }
        }
    }
}
