using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// The one path in the streaming ordered-query cursor that nothing else reaches.
//
// ArchetypeSortedStream buffers a page of entries ahead of its B+Tree cursor, starting at 64. On an AllowMultiple index
// a single key owns a whole list of values, and the fill emits a key whole or not at all — parking inside a value
// buffer would mean remembering a position in a structure a writer may reallocate. So a key with more values than the
// page cannot be served at all, and FillOrderedPage answers with a NEGATIVE count meaning "grow to n and ask again".
//
// Every other fixture uses a handful of entities per key, so that path never runs. Probing it with a throw confirmed
// it: 0 of 4 296 tests reached it. These two tests are the only coverage it has.
//
// Reuses ClQUnit from ClusterQueryTests: ClQStats.Score is [Index(AllowMultiple = true)].
// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════

[TestFixture]
class OrderedStreamPageGrowthTests : TestBase<OrderedStreamPageGrowthTests>
{
    /// <summary>Page capacity the stream starts at. A key must exceed this to force the grow-and-retry path.</summary>
    private const int InitialPage = 64;

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClQStats>();
        dbe.RegisterComponentFromAccessor<ClQTag>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>
    /// One key holding more values than an empty page can take. The fill must demand a bigger page and be retried until
    /// the key fits, rather than returning a short result or looping.
    /// </summary>
    /// <remarks>
    /// 200 values against a 64-entry page forces the growth to run twice (64 → 128 → 256), so it also covers the case
    /// where one round of doubling is still not enough — an off-by-one that stopped after a single grow would hang here
    /// rather than return a wrong answer.
    /// </remarks>
    [Test]
    public void SingleKeyLargerThanThePage_IsReturnedWhole()
    {
        using var dbe = SetupEngine();
        const int count = 200;
        Assert.That(count, Is.GreaterThan(InitialPage * 2), "the fixture must force MORE than one round of page growth, or it tests half the path");

        Spawn(dbe, count, _ => 42);       // every entity shares one indexed key

        using var tx = dbe.CreateQuickTransaction();
        var result = tx.Query<ClQUnit>()
            .WhereField<ClQStats>(s => s.Score >= 0)
            .OrderByField<ClQStats, int>(s => s.Score)
            .ExecuteOrdered();
        tx.Commit();

        Assert.That(result, Has.Count.EqualTo(count), "a key whose value list exceeds the page must still come back complete");
        Assert.That(result, Is.Unique, "growing and retrying the page must not re-emit the values already copied");
    }

    /// <summary>
    /// A small key followed by an oversized one. The oversized key must be backed out of the page it does not fit in —
    /// leaving the small key's values intact — and returned whole by the next fill.
    /// </summary>
    /// <remarks>
    /// This is the rewind-with-entries-already-on-the-page case. Getting it wrong is silent in both directions: rewind
    /// too far and the small key's rows vanish; rewind too little and the partial key's values are emitted twice, then
    /// again when the key is re-read whole.
    /// </remarks>
    [Test]
    public void SmallKeyThenOversizedKey_KeepsBothWholeAndInOrder()
    {
        using var dbe = SetupEngine();
        const int smallCount = 10;
        const int largeCount = 150;

        // Score 1 for the first few, Score 2 for the rest: the page takes key 1 whole, then cannot take key 2.
        Spawn(dbe, smallCount + largeCount, i => i < smallCount ? 1 : 2);

        using var tx = dbe.CreateQuickTransaction();
        var result = tx.Query<ClQUnit>()
            .WhereField<ClQStats>(s => s.Score >= 0)
            .OrderByField<ClQStats, int>(s => s.Score)
            .ExecuteOrdered();
        tx.Commit();

        Assert.That(result, Has.Count.EqualTo(smallCount + largeCount), "rewinding the oversized key must not drop the key that preceded it");
        Assert.That(result, Is.Unique, "the backed-out values must not also be emitted by the fill that re-reads the key whole");
    }

    /// <summary>Take(N) across the boundary: the page grows underneath the merge without changing what the query returns.</summary>
    [Test]
    public void TakeAcrossAnOversizedKey_ReturnsExactlyN()
    {
        using var dbe = SetupEngine();
        Spawn(dbe, 300, i => i < 5 ? 1 : 2);

        using var tx = dbe.CreateQuickTransaction();
        var result = tx.Query<ClQUnit>()
            .WhereField<ClQStats>(s => s.Score >= 0)
            .OrderByField<ClQStats, int>(s => s.Score)
            .Take(100)
            .ExecuteOrdered();
        tx.Commit();

        Assert.That(result, Has.Count.EqualTo(100));
        Assert.That(result, Is.Unique);
    }

    private static void Spawn(DatabaseEngine dbe, int count, System.Func<int, int> scoreFunc)
    {
        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < count; i++)
        {
            var stats = new ClQStats(scoreFunc(i), i);
            var tag = new ClQTag(i);
            tx.Spawn<ClQUnit>(ClQUnit.Stats.Set(in stats), ClQUnit.Tag.Set(in tag));
        }

        tx.Commit();
    }
}
