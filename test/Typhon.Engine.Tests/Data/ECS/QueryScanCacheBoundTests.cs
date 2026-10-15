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
        EntityAccessor.ScanYieldProbe = null;
        EntityAccessor.ScanEpochRefreshInterval = System.TimeSpan.FromMilliseconds(1);
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

    public enum WalkKind
    {
        MapCount,
        SoaScan,
        OccupancyCount,
    }

    /// <summary>
    /// #1364: a broad walk moves its transaction's epoch at its yield points. A writer commits on another thread while the walk is under way; by the walk's
    /// next yields, what the writer tagged is no longer protected. Held for the whole walk, the reader's epoch kept the minimum where the walk began, so
    /// every page another thread tagged meanwhile stayed unevictable until the walk ended — at 12M entities, a count stalled every writer with the whole
    /// cache epoch-protected and nothing dirty.
    /// </summary>
    [TestCase(WalkKind.MapCount)]
    [TestCase(WalkKind.SoaScan)]
    [TestCase(WalkKind.OccupancyCount)]
    [CancelAfter(60_000)]
    [NonParallelizable] // the yield probe and interval are process-wide, and other threads' epoch scopes would hold the writer's pages too
    [VerifiesRule("EP-02")]
    public void PagesAWriterTagsDuringALongWalk_AreNotHeldByTheWalksEpoch(WalkKind walk)
    {
        var dbe = Build();
        var victims = new System.Collections.Generic.List<EntityId>();
        using (var tx = dbe.CreateReadOnlyTransaction())
        {
            foreach (var id in tx.Query<QsFatArch>().Execute())
            {
                if (victims.Count < 8_000)
                {
                    victims.Add(id);
                }
            }
        }

        EntityAccessor.ScanEpochRefreshInterval = System.TimeSpan.Zero;
        var yields = 0;
        var heldAfterTheWriter = -1;
        var walker = System.Environment.CurrentManagedThreadId;
        EntityAccessor.ScanYieldProbe = () =>
        {
            if (System.Environment.CurrentManagedThreadId != walker)
            {
                return;
            }

            yields++;
            if (yields == 2)
            {
                // Another thread's commit, whole, while this walk is in the middle: it tags the clusters and revision pages it writes.
                var writer = new System.Threading.Thread(() =>
                {
                    using var w = dbe.CreateQuickTransaction();
                    for (var i = 0; i < victims.Count; i++)
                    {
                        w.OpenMut(victims[i]).Set(QsFatArch.Fat, new QsFat { Key = -(i + 1) });   // the key is unique
                    }

                    w.Commit();
                });
                writer.Start();
                writer.Join();
            }
            else if (yields == 4)
            {
                heldAfterTheWriter = EpochProtectedPages(dbe);
            }
        };

        using (var reader = dbe.CreateReadOnlyTransaction())
        {
            switch (walk)
            {
                case WalkKind.SoaScan:
                    QueryPathProbe.Forced = ClusterScanPath.FullScan;
                    reader.Query<QsFatArch>().WhereField<QsFat>(x => x.Key >= 0).Count();
                    break;
                case WalkKind.MapCount:
                    QueryPathProbe.ForcedCount = ClusterCountPath.MapProbe;
                    reader.Query<QsFatArch>().Count();
                    break;
                default:
                    QueryPathProbe.OccupancyCounts = 0;
                    var longWalk = EntityAccessor.LongWalkClusters;
                    EntityAccessor.LongWalkClusters = 0;   // this archetype's walk is short of the threshold: arm the yield anyway
                    try
                    {
                        reader.Query<QsFatArch>().Count();
                    }
                    finally
                    {
                        EntityAccessor.LongWalkClusters = longWalk;
                    }

                    Assert.That(QueryPathProbe.OccupancyCounts, Is.GreaterThan(0), "premise: the count summed the occupancy words");
                    break;
            }
        }

        Assert.That(yields, Is.GreaterThanOrEqualTo(4), "premise: the walk reached four yield points");
        Assert.That(heldAfterTheWriter, Is.LessThan(BoundedPages), "the walk's epoch kept the writer's pages protected");
    }

    /// <summary>
    /// #1230: a walk that waits on back-pressure moves its epoch while it waits. Its yield refresh is off here, so the wait is all that can: a writer fills
    /// the cache with pages it tags after the walk's epoch, the walk misses, and at the minimum its epoch protects every one of them — nothing could be
    /// evicted, and the walk and the writer both waited out the back-pressure timeout and failed.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    [NonParallelizable] // the yield probe and interval are process-wide
    [VerifiesRule("EP-02")]
    public void AWalkWaitingOnBackpressure_DoesNotHoldTheCacheWithItsEpoch()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<QsFat>();
        dbe.InitializeArchetypes();
        var ids = new System.Collections.Generic.List<EntityId>();
        for (var b = 0; b < 80; b++)
        {
            using var tx = dbe.CreateQuickTransaction();
            for (var i = 0; i < Batch; i++)
            {
                var fat = new QsFat { Key = b * Batch + i };
                ids.Add(tx.Spawn<QsFatArch>(QsFatArch.Fat.Set(in fat)));
            }

            tx.Commit();
        }

        EntityAccessor.ScanEpochRefreshInterval = System.TimeSpan.MaxValue;   // the walk's yield refresh off: only the wait can move its epoch
        var refreshedBefore = System.Threading.Interlocked.Read(ref PagedMMF.RefreshedWaits);
        System.Exception writerFault = null;
        System.Threading.Thread writer = null;
        long written = 0;
        var walker = System.Environment.CurrentManagedThreadId;
        EntityAccessor.ScanYieldProbe = () =>
        {
            if (writer != null || System.Environment.CurrentManagedThreadId != walker)
            {
                return;
            }

            // Another thread rewrites every entity, tagging page after page after this walk's epoch, until the cache is held.
            writer = new System.Threading.Thread(() =>
            {
                try
                {
                    for (var b = 0; b < ids.Count; b += Batch)
                    {
                        using var w = dbe.CreateQuickTransaction();
                        for (var i = b; i < b + Batch; i++)
                        {
                            w.OpenMut(ids[i]).Set(QsFatArch.Fat, new QsFat { Key = -(i + 1) });
                            System.Threading.Interlocked.Increment(ref written);
                        }

                        w.Commit();
                    }
                }
                catch (System.Exception e)
                {
                    writerFault = e;
                }
            });
            writer.Start();
            // Until the writer stalls: the cache is held, by this walk's epoch, and its next miss must wait.
            var until = System.Diagnostics.Stopwatch.StartNew();
            var lastWritten = -1L;
            var still = System.Diagnostics.Stopwatch.StartNew();
            while (writer.IsAlive && until.ElapsedMilliseconds < 3_000)
            {
                var now = System.Threading.Interlocked.Read(ref written);
                if (now != lastWritten)
                {
                    lastWritten = now;
                    still.Restart();
                }
                else if (still.ElapsedMilliseconds >= 150)
                {
                    break;
                }

                System.Threading.Thread.Yield();
            }

            TestContext.Out.WriteLine($"the writer stalled: epoch-protected {EpochProtectedPages(dbe)}, written {lastWritten}, " +
                                      $"writer alive {writer.IsAlive}, " +
                                      $"{until.ElapsedMilliseconds} ms");
        };

        using (var reader = dbe.CreateReadOnlyTransaction())
        {
            QueryPathProbe.ForcedCount = ClusterCountPath.MapProbe;
            reader.Query<QsFatArch>().Count();
        }

        writer?.Join();
        Assert.That(writer, Is.Not.Null, "premise: the walk reached a yield point");
        Assert.That(writerFault, Is.Null, $"the writer failed: {writerFault}");
        Assert.That(System.Threading.Interlocked.Read(ref PagedMMF.RefreshedWaits), Is.GreaterThan(refreshedBefore),
            "premise: the walk waited on back-pressure");
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
