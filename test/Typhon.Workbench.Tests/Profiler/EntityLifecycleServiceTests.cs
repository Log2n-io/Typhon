using System.Linq;
using NUnit.Framework;
using Typhon.Profiler;
using Typhon.Workbench.Profiler;

namespace Typhon.Workbench.Tests.Profiler;

/// <summary>
/// Cohort paging and identity rules for the entity lens (#620, design §4.4).
/// </summary>
/// <remarks>
/// Two properties carry the weight. First, a run is expanded only at the page boundary — a 200,000-entity bulk load is one row, and asking for 50 of its
/// members must not materialize the other 199,950. Second, a cohort reports its archetype identity honestly: the durable routing id when every run agrees,
/// and <i>mixed</i> when they do not, rather than picking the first one it saw.
/// </remarks>
[TestFixture]
internal sealed class EntityLifecycleServiceTests
{
    private static EntityLifecycleRun Spawn(uint tick, long firstKey, uint count, ushort routing = 3, ushort catalog = 10) => new()
    {
        TickNumber = tick,
        ArchetypeId = catalog,
        RoutingId = routing,
        FirstEntityKey = firstKey,
        Count = count,
        Kind = (byte)EntityLifecycleKind.Spawn,
    };

    private static EntityLifecycleRun Destroy(uint tick, long key, ushort routing = 3) => new()
    {
        TickNumber = tick,
        ArchetypeId = EntityLifecycleRun.UnknownArchetypeId,
        RoutingId = routing,
        FirstEntityKey = key,
        Count = 1,
        Kind = (byte)EntityLifecycleKind.Destroy,
    };

    private static long Raw(long key, ushort routing) => EntityLifecycleService.RawIdOf(key, routing);

    // ═══════════════════════════════════════════════════════════════════════
    // Paging
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void ARunIsExpandedOnlyAcrossThePageWindow()
    {
        // One row describing 200,000 entities. Page 3 of 50 must cost 50 ids, not 200,000.
        var runs = new[] { Spawn(tick: 0, firstKey: 1_000, count: 200_000) };

        var page = EntityLifecycleService.GetCohort(runs, EntityLifecycleKind.Spawn, 0, 0, null, offset: 100, limit: 50);

        Assert.That(page.TotalEntities, Is.EqualTo(200_000));
        Assert.That(page.EntityIds, Has.Count.EqualTo(50));
        Assert.That(page.EntityIds[0], Is.EqualTo(Raw(1_100, 3).ToString()));
        Assert.That(page.EntityIds[49], Is.EqualTo(Raw(1_149, 3).ToString()));
        Assert.That(page.HasMore, Is.True);
    }

    [Test]
    public void APageStraddlingTwoRunsContinuesAcrossTheBoundary()
    {
        // The off-by-one that a single-run test would never catch: the page starts inside run A and finishes inside run B.
        var runs = new[]
        {
            Spawn(tick: 5, firstKey: 10, count: 3),   // keys 10,11,12
            Spawn(tick: 5, firstKey: 100, count: 3),  // keys 100,101,102
        };

        var page = EntityLifecycleService.GetCohort(runs, EntityLifecycleKind.Spawn, 0, 10, null, offset: 2, limit: 3);

        Assert.That(page.EntityIds, Is.EqualTo(new[] { Raw(12, 3).ToString(), Raw(100, 3).ToString(), Raw(101, 3).ToString() }));
        Assert.That(page.TotalEntities, Is.EqualTo(6));
        Assert.That(page.HasMore, Is.True);
    }

    [Test]
    public void TheLastPageReportsNoMore()
    {
        var runs = new[] { Spawn(tick: 1, firstKey: 1, count: 5) };

        var page = EntityLifecycleService.GetCohort(runs, EntityLifecycleKind.Spawn, 0, 10, null, offset: 3, limit: 10);

        Assert.That(page.EntityIds, Has.Count.EqualTo(2));
        Assert.That(page.HasMore, Is.False);
    }

    [Test]
    public void TotalCountsTheWholeRange_EvenWhenThePageIsFull()
    {
        // The total must not be "what I returned" — the readout says "1,240 spawned", and paging must not change that number.
        var runs = Enumerable.Range(0, 20).Select(i => Spawn(tick: 7, firstKey: i * 10, count: 10)).ToArray();

        var page = EntityLifecycleService.GetCohort(runs, EntityLifecycleKind.Spawn, 7, 7, null, offset: 0, limit: 5);

        Assert.That(page.TotalEntities, Is.EqualTo(200));
        Assert.That(page.EntityIds, Has.Count.EqualTo(5));
    }

    [Test]
    public void PageSizeIsClampedSoACohortCannotBeBulkExported()
    {
        var runs = new[] { Spawn(tick: 0, firstKey: 1, count: 10_000) };

        var page = EntityLifecycleService.GetCohort(runs, EntityLifecycleKind.Spawn, 0, 0, null, offset: 0, limit: 100_000);

        Assert.That(page.EntityIds, Has.Count.EqualTo(EntityLifecycleService.MaxPageSize));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Range + kind selection
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void OnlyRunsInsideTheTickRangeCount()
    {
        var runs = new[] { Spawn(1, 1, 1), Spawn(5, 2, 1), Spawn(9, 3, 1) };

        var page = EntityLifecycleService.GetCohort(runs, EntityLifecycleKind.Spawn, 5, 5, null, 0, 10);

        Assert.That(page.TotalEntities, Is.EqualTo(1));
        Assert.That(page.EntityIds.Single(), Is.EqualTo(Raw(2, 3).ToString()));
    }

    [Test]
    public void SpawnsAndDestroysDoNotBleedIntoEachOther()
    {
        var runs = new[] { Spawn(3, 1, 4), Destroy(3, 2) };

        Assert.That(EntityLifecycleService.GetCohort(runs, EntityLifecycleKind.Spawn, 0, 10, null, 0, 10).TotalEntities, Is.EqualTo(4));
        Assert.That(EntityLifecycleService.GetCohort(runs, EntityLifecycleKind.Destroy, 0, 10, null, 0, 10).TotalEntities, Is.EqualTo(1));
    }

    [Test]
    public void AnInvertedRangeIsEmptyRatherThanAnError()
    {
        var page = EntityLifecycleService.GetCohort([Spawn(1, 1, 1)], EntityLifecycleKind.Spawn, 10, 5, null, 0, 10);

        Assert.That(page.TotalEntities, Is.Zero);
        Assert.That(page.EntityIds, Is.Empty);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Identity — design §5.2 / §5.3
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void ACohortReportsBothArchetypeIdentifiers_AndTheyAreDifferentNumbers()
    {
        // The live SWG capture has exactly this shape: catalog id 10, routing id 1, same archetype. Both are ushorts and nothing distinguishes them, so
        // the cohort surfaces both and leaves the caller no room to guess which one it holds.
        var runs = new[] { Spawn(tick: 0, firstKey: 1_001, count: 500, routing: 1, catalog: 10) };

        var page = EntityLifecycleService.GetCohort(runs, EntityLifecycleKind.Spawn, 0, 0, null, 0, 1);

        Assert.That(page.RoutingId, Is.EqualTo(1));
        Assert.That(page.CatalogArchetypeId, Is.EqualTo(10));
        Assert.That(page.EntityIds.Single(), Is.EqualTo(Raw(1_001, 1).ToString()), "ids must be built from the routing id, never the catalog id");
    }

    [Test]
    public void ACohortSpanningTwoArchetypesReportsMixed_RatherThanTheFirstOneSeen()
    {
        // A tick range wide enough to catch two archetypes cannot be joined to one database archetype. Reporting the first routing id would produce a
        // confident, plausible, wrong answer for every entity from the second.
        var runs = new[] { Spawn(tick: 4, firstKey: 1, count: 2, routing: 1), Spawn(tick: 4, firstKey: 50, count: 2, routing: 7) };

        var page = EntityLifecycleService.GetCohort(runs, EntityLifecycleKind.Spawn, 0, 10, null, 0, 10);

        Assert.That(page.RoutingId, Is.EqualTo(EntityLifecycleService.MixedRoutingId));
        Assert.That(page.TotalEntities, Is.EqualTo(4));
    }

    [Test]
    public void MixedIsDecidedByTheWholeRange_NotByThePageThatHappensToBeAsked()
    {
        // Identity accumulates over every matching run, so a page that lands entirely inside one archetype still reports the cohort as mixed.
        var runs = new[] { Spawn(tick: 4, firstKey: 1, count: 2, routing: 1), Spawn(tick: 4, firstKey: 50, count: 2, routing: 7) };

        var firstPage = EntityLifecycleService.GetCohort(runs, EntityLifecycleKind.Spawn, 0, 10, null, offset: 0, limit: 2);

        Assert.That(firstPage.EntityIds, Has.Count.EqualTo(2));
        Assert.That(firstPage.RoutingId, Is.EqualTo(EntityLifecycleService.MixedRoutingId));
    }

    [Test]
    public void FilteringByRoutingIdNarrowsAMixedRangeBackToOneArchetype()
    {
        var runs = new[] { Spawn(tick: 4, firstKey: 1, count: 2, routing: 1), Spawn(tick: 4, firstKey: 50, count: 3, routing: 7) };

        var page = EntityLifecycleService.GetCohort(runs, EntityLifecycleKind.Spawn, 0, 10, routingIdFilter: 7, 0, 10);

        Assert.That(page.RoutingId, Is.EqualTo(7));
        Assert.That(page.TotalEntities, Is.EqualTo(3));
    }

    [Test]
    public void ADestroyCohortHasNoCatalogId_AndSaysSo()
    {
        // Destroy events carry only the entity id. Reporting a catalog id here would mean inventing one.
        var page = EntityLifecycleService.GetCohort([Destroy(2, 5)], EntityLifecycleKind.Destroy, 0, 10, null, 0, 10);

        Assert.That(page.CatalogArchetypeId, Is.EqualTo(-1));
        Assert.That(page.RoutingId, Is.EqualTo(3), "the routing id IS recoverable — it is the low 16 bits of the destroyed id");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Series
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void TheSeriesSumsEachTickAndCountsItsRuns()
    {
        var runs = new[] { Spawn(1, 1, 10), Spawn(1, 20, 5), Spawn(2, 40, 1) };

        var series = EntityLifecycleService.GetSeries(runs, EntityLifecycleKind.Spawn, 0, 10, null);

        Assert.That(series, Has.Count.EqualTo(2));
        Assert.That(series[0], Is.EqualTo(new EntityLifecycleService.LifecyclePoint(1, 15, 2)));
        Assert.That(series[1], Is.EqualTo(new EntityLifecycleService.LifecyclePoint(2, 1, 1)));
    }

    [Test]
    public void RunCountDistinguishesOneBulkLoadFromAStormOfIndividualSpawns()
    {
        // Same entity count, very different causes — and the whole reason RunCount is on the wire.
        var bulk = EntityLifecycleService.GetSeries([Spawn(1, 1, 1_000)], EntityLifecycleKind.Spawn, 0, 5, null);
        var storm = EntityLifecycleService.GetSeries(
            Enumerable.Range(0, 1_000).Select(i => Spawn(1, i, 1)).ToArray(), EntityLifecycleKind.Spawn, 0, 5, null);

        Assert.That(bulk[0].EntityCount, Is.EqualTo(storm[0].EntityCount));
        Assert.That(bulk[0].RunCount, Is.EqualTo(1));
        Assert.That(storm[0].RunCount, Is.EqualTo(1_000));
    }

    [Test]
    public void TheSeriesIsSparse_SoAQuietTickProducesNoPoint()
    {
        var series = EntityLifecycleService.GetSeries([Spawn(1, 1, 1), Spawn(9, 2, 1)], EntityLifecycleKind.Spawn, 0, 20, null);

        Assert.That(series.Select(p => p.TickNumber), Is.EqualTo(new uint[] { 1, 9 }));
    }
}
