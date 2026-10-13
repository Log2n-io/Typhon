using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// Two indexed fields: a predicate on the second sends an ordered query on the first to the scan-then-sort fallback.
[Component("Typhon.Test.QueryIndexPathSnapshot.TwoIndexed", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential)]
struct QipTwoIndexed
{
    [Index(AllowMultiple = true)] public int Tier;
    [Index(AllowMultiple = true)] public int Score;

    public QipTwoIndexed(int tier, int score)
    {
        Tier = tier;
        Score = score;
    }
}

[Archetype]
class QipArch : Archetype<QipArch>
{
    public static readonly Comp<QipTwoIndexed> Ranked = Register<QipTwoIndexed>();
    public static readonly Comp<StatsTag> Tag = Register<StatsTag>();
}

/// <summary>
/// The index paths at an older snapshot (#1347, VIS-09): a destroy removes its entity's B+Tree entries at its publish while a snapshot older than it still
/// sees the entity, kept in its hidden slot. The selective scan and the ordered merge read the trees, so they lost it — and the ordered merge had no born
/// gate either, so it returned an entity spawned after the snapshot. The SoA scan answered right, so the planner's choice of path changed the answer.
/// </summary>
[TestFixture]
[NonParallelizable] // QueryPathProbe.Forced is a static every engine reads.
class QueryIndexPathSnapshotTests : TestBase<QueryIndexPathSnapshotTests>
{
    private static readonly (string Name, ClusterScanPath Path)[] Paths =
        [("the planner's path", ClusterScanPath.Planner), ("the index path", ClusterScanPath.Selective), ("the scan path", ClusterScanPath.FullScan)];

    private DatabaseEngine OpenEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<StatsRanked>();
        dbe.RegisterComponentFromAccessor<StatsTag>();
        dbe.RegisterComponentFromAccessor<QipTwoIndexed>();
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

    private static void Destroy(DatabaseEngine dbe, params EntityId[] ids)
    {
        using var tx = dbe.CreateQuickTransaction();
        foreach (var id in ids)
        {
            tx.Destroy(id);
        }

        Assert.That(tx.Commit(), Is.True, "premise: the destroy commits");
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

    /// <summary>Count, Any, Execute and ExecuteOrdered of Tier == <paramref name="tier"/> through <paramref name="tx"/> all return exactly <paramref name="expected"/>.</summary>
    private static void AssertTier(Transaction tx, int tier, IReadOnlyCollection<EntityId> expected, string at)
    {
        EcsQuery<StatsArch> Q() => tx.Query<StatsArch>().WhereField<StatsRanked>(x => x.Tier == tier);
        Assert.That(Q().Count(), Is.EqualTo(expected.Count), $"{at}: Count");
        Assert.That(Q().Any(), Is.EqualTo(expected.Count > 0), $"{at}: Any");
        Assert.That(Q().Execute(), Is.EquivalentTo(expected), $"{at}: Execute");
        Assert.That(Q().OrderByField<StatsRanked, int>(x => x.Tier).ExecuteOrdered(), Is.EquivalentTo(expected), $"{at}: ExecuteOrdered");
    }

    [Test]
    [VerifiesRule("VIS-09")]
    public void AReaderOlderThanADestroy_FindsTheEntityOnEveryPathAndTerminal()
    {
        using var dbe = OpenEngine();
        var ids = Spawn(dbe, Enumerable.Range(0, 150).Select(i => i % 3 == 0 ? 5 : 7).ToArray());
        var fives = ids.Where((_, i) => i % 3 == 0).ToList();
        using var reader = dbe.CreateQuickTransaction();
        Destroy(dbe, fives[1], fives[30]);

        OnEachPath(name => AssertTier(reader, 5, fives, $"a snapshot older than the destroy, on {name}"));

        using var fresh = dbe.CreateQuickTransaction();
        var alive = fives.Where(id => id != fives[1] && id != fives[30]).ToList();
        OnEachPath(name => AssertTier(fresh, 5, alive, $"a snapshot after the destroy, on {name}"));
    }

    [Test]
    [VerifiesRule("VIS-09")]
    public void AReaderOlderThanASpawn_NoTerminalReturnsIt()
    {
        using var dbe = OpenEngine();
        var old = Spawn(dbe, 5);
        using var reader = dbe.CreateQuickTransaction();
        Spawn(dbe, 5, 5);

        OnEachPath(name => AssertTier(reader, 5, old, $"a snapshot older than the spawn, on {name}"));
    }

    [TestCase(false)]
    [TestCase(true)]
    [VerifiesRule("VIS-09")]
    public void AnOrderedQueryOfAnOlderReader_PlacesADestroyedEntityByItsKey(bool sortFallback)
    {
        using var dbe = OpenEngine();
        int[] tiers = [4, 1, 6, 3, 2, 5];
        var ids = new List<EntityId>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            foreach (var tier in tiers)
            {
                ids.Add(tx.Spawn<QipArch>(QipArch.Ranked.Set(new QipTwoIndexed(tier, tier * 10)), QipArch.Tag.Set(new StatsTag(tier))));
            }

            Assert.That(tx.Commit(), Is.True);
        }

        var byTier = ids.Zip(tiers).OrderBy(p => p.Second).Select(p => p.First).ToList();
        using var reader = dbe.CreateQuickTransaction();
        Destroy(dbe, byTier[2], byTier[5]);   // tiers 3 and 6

        // A predicate on the second indexed field sends the query to the scan-then-sort fallback.
        EcsQuery<QipArch> Q() => sortFallback
            ? reader.Query<QipArch>().WhereField<QipTwoIndexed>(x => x.Tier >= 1 && x.Score >= 0)
            : reader.Query<QipArch>().WhereField<QipTwoIndexed>(x => x.Tier >= 1);

        OnEachPath(name =>
        {
            Assert.That(Q().OrderByField<QipTwoIndexed, int>(x => x.Tier).ExecuteOrdered(), Is.EqualTo(byTier), $"ascending, on {name}");
            Assert.That(Q().OrderByFieldDescending<QipTwoIndexed, int>(x => x.Tier).ExecuteOrdered(), Is.EqualTo(Enumerable.Reverse(byTier)),
                $"descending, on {name}");
            Assert.That(Q().OrderByField<QipTwoIndexed, int>(x => x.Tier).Skip(2).Take(3).ExecuteOrdered(), Is.EqualTo(byTier.GetRange(2, 3)),
                $"a page, on {name}");
        });
    }

    /// <summary>
    /// The destroy publishes between the selective scan's tree read and its cluster reads, after it read the destroy stamp: the tree named the slot, the
    /// slot is hidden by the time it is read, and its record decides it.
    /// </summary>
    [Test]
    [VerifiesRule("VIS-09")]
    public void ADestroyBetweenTheSelectiveScansTreeReadAndItsClusterRead_DoesNotLoseTheEntity()
    {
        using var dbe = OpenEngine();
        var ids = Spawn(dbe, 5, 5, 5, 7);
        using var reader = dbe.CreateQuickTransaction();

        QueryPathProbe.Reset();
        QueryPathProbe.Forced = ClusterScanPath.Selective;
        try
        {
            QueryPathProbe.SelectiveAfterTreeScanHook = () =>
            {
                QueryPathProbe.SelectiveAfterTreeScanHook = null;
                Destroy(dbe, ids[1]);
            };
            var found = reader.Query<StatsArch>().WhereField<StatsRanked>(x => x.Tier == 5).Execute();
            Assert.That(QueryPathProbe.SelectiveAfterTreeScanHook, Is.Null, "premise: the destroy ran inside the scan");
            Assert.That(found, Is.EquivalentTo(ids.GetRange(0, 3)), "the index path lost an entity destroyed after its tree read");
        }
        finally
        {
            QueryPathProbe.Reset();
        }
    }

    /// <summary>
    /// The destroy publishes after the ordered query read the destroy stamp and before its stream read the tree: the tree no longer holds the entity, the
    /// stamp moved, and the merge is answered again.
    /// </summary>
    [Test]
    [VerifiesRule("VIS-09")]
    public void ADestroyBeforeTheOrderedStreamsReadTheirTree_DoesNotLoseTheEntity()
    {
        using var dbe = OpenEngine();
        var ids = Spawn(dbe, 1, 2, 3);
        using var reader = dbe.CreateQuickTransaction();

        QueryPathProbe.Reset();
        try
        {
            QueryPathProbe.OrderedBeforeStreamsHook = () =>
            {
                QueryPathProbe.OrderedBeforeStreamsHook = null;
                Destroy(dbe, ids[1]);
            };
            var ordered = reader.Query<StatsArch>().WhereField<StatsRanked>(x => x.Tier >= 1).OrderByField<StatsRanked, int>(x => x.Tier).ExecuteOrdered();
            Assert.That(QueryPathProbe.OrderedBeforeStreamsHook, Is.Null, "premise: the destroy ran inside the query");
            Assert.That(ordered, Is.EqualTo(ids), "the ordered merge lost an entity destroyed before its stream read the tree");
        }
        finally
        {
            QueryPathProbe.Reset();
        }
    }
}
