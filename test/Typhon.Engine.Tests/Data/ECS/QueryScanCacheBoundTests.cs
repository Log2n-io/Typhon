using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>A component as large as a spawn carries: 64 to a cluster, a cluster to a page.</summary>
[Component("Typhon.Test.QueryScan.Fat", 1)]
unsafe struct QsFat
{
    [Index]
    public int Key;

    public fixed byte Payload[108];   // 112 bytes in all: the most a spawned component may carry
}

[Archetype]
class QsFatArch : Archetype<QsFatArch>
{
    public static readonly Comp<QsFat> Fat = Register<QsFat>();
}

/// <summary>
/// A query over a whole archetype holds a bounded set of the page cache, however large the archetype (EP-02). Its scans read through a scan accessor, which
/// pins the 32 pages of its window and never tags them with the transaction's epoch. They used to tag every page they touched, and a transaction holds its
/// epoch until it is disposed, so counting an archetype larger than the cache waited for evictions its own tags forbade: MarketHardeningTests' count of
/// 300 000 audit entries stalled with 12 600 pages epoch-protected and none dirty.
/// </summary>
/// <remarks>
/// The claim is the bound, so the bound is what is asserted: each test reads an archetype of about 625 pages inside ONE read-only transaction, then counts
/// the pages still epoch-protected while that transaction is open. An epoch-tagged scan leaves all it read; a scan accessor leaves none. The archetype
/// does not need to outgrow the cache for that, which keeps the build — 40 000 spawns — inside the default 8 MiB cache.
/// </remarks>
[TestFixture]
class QueryScanCacheBoundTests : TestBase<QueryScanCacheBoundTests>
{
    private const int Entities = 40_000;
    private const int Batch = 1000;

    /// <summary>
    /// A scan accessor's window pins by slot reference, never by epoch, so what stays epoch-protected is only what the query reads outside its scans:
    /// 3 to 5 pages measured. Epoch-tagged, the scans leave 619 (occupancy count), 467 (SoA scan) and 41 (the index's leaves alone).
    /// </summary>
    private const int BoundedPages = 16;

    [TearDown]
    public void ResetProbes()
    {
        QueryPathProbe.Forced = ClusterScanPath.Planner;
        QueryPathProbe.ForcedCount = ClusterCountPath.Planner;
    }

    private DatabaseEngine Build()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<QsFat>();
        dbe.InitializeArchetypes();
        for (var b = 0; b < Entities / Batch; b++)
        {
            using var tx = dbe.CreateQuickTransaction();
            for (var i = 0; i < Batch; i++)
            {
                var fat = new QsFat { Key = b * Batch + i };
                tx.Spawn<QsFatArch>(QsFatArch.Fat.Set(in fat));
            }

            tx.Commit();
        }

        var clusterPages = dbe._archetypeStates[Archetype<QsFatArch>.Metadata.ArchetypeId].ClusterState.ClusterSegment.Length;
        Assert.That(clusterPages, Is.GreaterThan(32 * BoundedPages), "premise: the archetype's clusters span many times the bound");
        Assert.That(EpochProtectedPages(dbe), Is.LessThan(BoundedPages), "premise: the build left nothing pinned");
        return dbe;
    }

    private static int EpochProtectedPages(DatabaseEngine dbe)
    {
        dbe.MMF.GetMetrics().GetMemPageExtraInfo(out var census);
        return census.EpochProtectedPageCount;
    }

    private static int SlotRefPages(DatabaseEngine dbe)
    {
        dbe.MMF.GetMetrics().GetMemPageExtraInfo(out var census);
        return census.SlotRefPageCount;
    }

    /// <summary>
    /// A scan accessor pins by slot reference, which nothing releases for it: a window left undisposed, or a page released twice, shows here as a count
    /// that differs from the one before the scan — and, unlike an epoch tag, never comes back. A background reader (the statistics worker) may hold a
    /// few for a moment, so the count is given two seconds to return; a leak does not return at all.
    /// </summary>
    private static void AssertSlotReferencesReturned(DatabaseEngine dbe, int atRest, string scan)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        var now = SlotRefPages(dbe);
        while (now != atRest && deadline.ElapsedMilliseconds < 2_000)
        {
            System.Threading.Thread.Sleep(1);
            now = SlotRefPages(dbe);
        }

        Assert.That(now, Is.EqualTo(atRest), $"{scan} left pages held by slot reference");
    }

    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("EP-02")]
    public void CountingAWholeArchetype_HoldsABoundedSetOfPages()
    {
        var dbe = Build();
        var atRest = SlotRefPages(dbe);
        using var tx = dbe.CreateReadOnlyTransaction();

        var occupancyBefore = QueryPathProbe.OccupancyCounts;
        Assert.That(tx.Query<QsFatArch>().Count(), Is.EqualTo(Entities));
        Assert.That(QueryPathProbe.OccupancyCounts, Is.GreaterThan(occupancyBefore), "premise: the count took the occupancy path");
        Assert.That(EpochProtectedPages(dbe), Is.LessThan(BoundedPages), "the occupancy count kept the pages it read pinned for the transaction");
        AssertSlotReferencesReturned(dbe, atRest, "the occupancy count");

        QueryPathProbe.ForcedCount = ClusterCountPath.MapProbe;
        Assert.That(tx.Query<QsFatArch>().Count(), Is.EqualTo(Entities), "the entity-map probe");
        AssertSlotReferencesReturned(dbe, atRest, "the entity-map count");
        Assert.That(tx.Query<QsFatArch>().Any(), Is.True);
        AssertSlotReferencesReturned(dbe, atRest, "the entity-map existence check");
        Assert.That(EpochProtectedPages(dbe), Is.LessThan(BoundedPages), "the entity-map scan kept the pages it read pinned for the transaction");
    }

    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("EP-02")]
    public void FilteringAWholeArchetype_HoldsABoundedSetOfPages()
    {
        var dbe = Build();
        var atRest = SlotRefPages(dbe);
        using var tx = dbe.CreateReadOnlyTransaction();

        QueryPathProbe.Forced = ClusterScanPath.FullScan;
        Assert.That(tx.Query<QsFatArch>().WhereField<QsFat>(x => x.Key >= 10_000).Count(), Is.EqualTo(Entities - 10_000), "the SoA scan");
        Assert.That(EpochProtectedPages(dbe), Is.LessThan(BoundedPages), "the SoA scan kept the pages it read pinned for the transaction");
        AssertSlotReferencesReturned(dbe, atRest, "the SoA scan");

        QueryPathProbe.Forced = ClusterScanPath.Selective;
        Assert.That(tx.Query<QsFatArch>().WhereField<QsFat>(x => x.Key >= 10_000).Execute().Count, Is.EqualTo(Entities - 10_000),
            "the index range scan");
        Assert.That(EpochProtectedPages(dbe), Is.LessThan(BoundedPages), "the index range scan kept the pages it read pinned for the transaction");
        AssertSlotReferencesReturned(dbe, atRest, "the index range scan");

        QueryPathProbe.Forced = ClusterScanPath.Planner;
        Assert.That(tx.Query<QsFatArch>().Execute().Count, Is.EqualTo(Entities), "the unfiltered collect");
        Assert.That(EpochProtectedPages(dbe), Is.LessThan(BoundedPages), "the collect kept the pages it read pinned for the transaction");
        AssertSlotReferencesReturned(dbe, atRest, "the unfiltered collect");
    }
}
