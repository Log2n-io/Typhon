using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

#region Schema

/// <summary>
/// <c>Versioned</c> on purpose: only a Versioned archetype promises snapshot isolation, so only it can show the failure the occupancy count exists to avoid —
/// counting an entity that was born after the reader's snapshot.
/// </summary>
[Component("Typhon.Test.QCount.Body", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential)]
struct QCountBody
{
    public int V;

    /// <summary>A chunk-based segment requires a stride of at least 8 bytes, and the size is derived from PUBLIC fields only.</summary>
    public int Pad;

    public QCountBody(int v)
    {
        V = v;
        Pad = 0;
    }
}

[Component("Typhon.Test.QCount.Tag", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct QCountTag
{
    public int T;
    public int Pad;

    public QCountTag(int t)
    {
        T = t;
        Pad = 0;
    }
}

[Component("Typhon.Test.QCount.Extra", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct QCountExtra
{
    public int E;
    public int Pad;

    public QCountExtra(int e)
    {
        E = e;
        Pad = 0;
    }
}

[Archetype]
class QCountUnit : Archetype<QCountUnit>
{
    public static readonly Comp<QCountBody> Body = Register<QCountBody>();
    public static readonly Comp<QCountTag> Tag = Register<QCountTag>();
}

/// <summary>Subtype, so a polymorphic count spans two archetypes and therefore two independent cluster sets.</summary>
[Archetype]
class QCountDerived : Archetype<QCountDerived, QCountUnit>
{
    public static readonly Comp<QCountExtra> Extra = Register<QCountExtra>();
}

/// <summary>Never spawned into — an archetype whose cluster set is empty must still count zero on both paths.</summary>
[Archetype]
class QCountEmpty : Archetype<QCountEmpty>
{
    public static readonly Comp<QCountExtra> Extra = Register<QCountExtra>();
}

#endregion

/// <summary>
/// An unfiltered <c>Count()</c> may be answered by summing each cluster's occupancy popcount instead of walking the EntityMap per entity. This fixture is the
/// evidence that the two answer identically — including on every shape where the popcount alone would be WRONG and the engine must fall back.
/// </summary>
/// <remarks>
/// <para>
/// Forcing the path is what makes the comparison mean anything. Whether the fast path runs is a property of the DATA (has anything died? was everything born
/// before this snapshot?), not of the query, so a test that merely counts and hopes would silently become a second map-probe test the first time a tombstone
/// appeared — green, and proving nothing. Every case below therefore runs both paths explicitly AND asserts which one the planner actually took.
/// </para>
/// <para>
/// The bail cases matter more than the happy one: the map probe is the reference implementation, so agreeing with it is only interesting where the fast path
/// had to decline. <see cref="ReaderPredatingACommit_DoesNotCountTheNewEntities"/> is the case that would produce a wrong number rather than a slow one.
/// </para>
/// </remarks>
[TestFixture]
class QueryCountEquivalenceTests : TestBase<QueryCountEquivalenceTests>
{
    private const int BaseCount = 150;      // spans 3 clusters at ClusterSize 64, so cluster-boundary handling is exercised rather than assumed
    private const int DerivedCount = 70;

    // ── Machinery ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<QCountBody>();
        dbe.RegisterComponentFromAccessor<QCountTag>();
        dbe.RegisterComponentFromAccessor<QCountExtra>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static EntityId[] SpawnBase(DatabaseEngine dbe, int count)
    {
        var ids = new EntityId[count];
        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < count; i++)
        {
            ids[i] = tx.Spawn<QCountUnit>(QCountUnit.Body.Set(new QCountBody(i)), QCountUnit.Tag.Set(new QCountTag(i)));
        }

        tx.Commit();
        return ids;
    }

    private static void SpawnDerived(DatabaseEngine dbe, int count)
    {
        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < count; i++)
        {
            tx.Spawn<QCountDerived>(
                QCountUnit.Body.Set(new QCountBody(i)),
                QCountUnit.Tag.Set(new QCountTag(i)),
                QCountDerived.Extra.Set(new QCountExtra(i)));
        }

        tx.Commit();
    }

    /// <summary>Run <paramref name="count"/> on a forced path and report which strategy each archetype actually took.</summary>
    private static int CountOn(ClusterCountPath path, System.Func<int> count, out int occupancy, out int mapProbes)
    {
        QueryPathProbe.Reset();
        QueryPathProbe.ForcedCount = path;
        try
        {
            var n = count();
            occupancy = QueryPathProbe.OccupancyCounts;
            mapProbes = QueryPathProbe.MapProbeCounts;
            return n;
        }
        finally
        {
            QueryPathProbe.Reset();
        }
    }

    /// <summary>Both strategies, one assertion: they agree, and the fast path's participation is asserted rather than hoped for.</summary>
    private static void AssertAgree(System.Func<int> count, int expected, bool expectFastPath, string because)
    {
        var reference = CountOn(ClusterCountPath.MapProbe, count, out var refOccupancy, out var refProbes);
        var planned = CountOn(ClusterCountPath.Planner, count, out var planOccupancy, out _);

        Assert.Multiple(() =>
        {
            Assert.That(reference, Is.EqualTo(expected), $"{because}: the map probe is the reference implementation and must match the spawned population");
            Assert.That(planned, Is.EqualTo(reference), $"{because}: the two count strategies must not disagree");
            Assert.That(refOccupancy, Is.Zero, "forcing MapProbe must actually disable the occupancy count, or the comparison is against itself");
            Assert.That(refProbes, Is.GreaterThan(0), "forcing MapProbe must actually walk the map, or the reference is vacuous");

            if (expectFastPath)
            {
                Assert.That(planOccupancy, Is.GreaterThan(0), $"{because}: this shape qualifies, so the occupancy count must be what answered it");
            }
            else
            {
                Assert.That(planOccupancy, Is.Zero, $"{because}: this shape does NOT qualify, so the engine must have fallen back to the map probe");
            }
        });
    }

    // ── The happy path ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Nothing has died and everything predates the reader: the occupancy popcount is the whole answer.</summary>
    [Test]
    public void FreshlySpawned_IsCountedByOccupancy_AndAgreesWithTheProbe()
    {
        using var dbe = SetupEngine();
        SpawnBase(dbe, BaseCount);

        using var tx = dbe.CreateQuickTransaction();
        AssertAgree(() => tx.QueryExact<QCountUnit>().Count(), BaseCount, expectFastPath: true, "a clean archetype");
    }

    /// <summary>A polymorphic count spans two archetypes, each with its own clusters; both must be summed.</summary>
    [Test]
    public void PolymorphicCount_SumsTheWholeSubtree()
    {
        using var dbe = SetupEngine();
        SpawnBase(dbe, BaseCount);
        SpawnDerived(dbe, DerivedCount);

        using var tx = dbe.CreateQuickTransaction();
        AssertAgree(() => tx.Query<QCountUnit>().Count(), BaseCount + DerivedCount, expectFastPath: true, "a subtree of two clean archetypes");
    }

    /// <summary>An archetype nobody spawned into has no active clusters — zero, not a bail and not a throw.</summary>
    [Test]
    public void EmptyArchetype_CountsZero()
    {
        using var dbe = SetupEngine();
        SpawnBase(dbe, BaseCount);

        using var tx = dbe.CreateQuickTransaction();

        // No map probe runs for an archetype with no entities either, so this one asserts the counts directly rather than through AssertAgree.
        var reference = CountOn(ClusterCountPath.MapProbe, () => tx.QueryExact<QCountEmpty>().Count(), out _, out _);
        var planned = CountOn(ClusterCountPath.Planner, () => tx.QueryExact<QCountEmpty>().Count(), out _, out _);

        Assert.Multiple(() =>
        {
            Assert.That(reference, Is.Zero);
            Assert.That(planned, Is.EqualTo(reference), "an empty archetype must count zero on both strategies");
        });
    }

    // ── The bails ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A destroy sets the cluster's died bit permanently, and the occupancy word cannot tell a reader whose snapshot predates that destroy that the entity was
    /// still alive for it. The archetype must fall back.
    /// </summary>
    [Test]
    public void AfterADestroy_AReaderThatFollowsItStaysOnTheFastPath()
    {
        using var dbe = SetupEngine();
        var ids = SpawnBase(dbe, BaseCount);

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(ids[0]);
            tx.Destroy(ids[1]);
            tx.Commit();
        }

        using var reader = dbe.CreateQuickTransaction();
        AssertAgree(() => reader.QueryExact<QCountUnit>().Count(), BaseCount - 2, expectFastPath: true,
            "an archetype whose only deaths predate the reader");
    }

    /// <summary>
    /// The recovery the sticky flag could not do (#722). Two destroys in different commits, and a reader created after each: the second reader must be back on
    /// the fast path too, not permanently exiled by the fact that the cluster has ever seen a death.
    /// </summary>
    /// <remarks>
    /// This is the case that decides whether an unfiltered View's per-tick refresh can use the occupancy delta at all. A simulation destroys entities
    /// continuously, so under the old <c>ClusterAnyDied</c> flag every cluster carried it within a few ticks and the gate never opened again — the fast path
    /// existed but was unreachable in the workload that needed it. Fails on the sticky flag; passes on the watermark.
    /// </remarks>
    [Test]
    public void RepeatedDestroys_DoNotPermanentlyExileTheArchetype()
    {
        using var dbe = SetupEngine();
        var ids = SpawnBase(dbe, BaseCount);

        for (var round = 0; round < 3; round++)
        {
            using (var tx = dbe.CreateQuickTransaction())
            {
                tx.Destroy(ids[round]);
                tx.Commit();
            }

            using var reader = dbe.CreateQuickTransaction();
            AssertAgree(() => reader.QueryExact<QCountUnit>().Count(), BaseCount - (round + 1), expectFastPath: true,
                $"round {round}: churn must not latch the archetype off the fast path");
        }
    }

    /// <summary>
    /// The destroy-side case that would be WRONG rather than slow, and the mirror of <see cref="ReaderPredatingACommit_DoesNotCountTheNewEntities"/>.
    /// </summary>
    /// <remarks>
    /// <c>ReleaseSlot</c> clears the occupancy bit at destroy commit while the tombstone lives on the EntityMap record, so a reader whose snapshot predates the
    /// death must still see the entity — but the occupancy word has already dropped it. Without the died watermark in the gate, the popcount UNDER-counts.
    /// Ablation: delete the <c>died[clusterChunkId] &lt;= txTsn</c> term from <c>IsClusterFullyVisibleAt</c> and this returns the wrong number.
    /// </remarks>
    [Test]
    public void ReaderPredatingADestroy_StillCountsTheDoomedEntities()
    {
        using var dbe = SetupEngine();
        var ids = SpawnBase(dbe, BaseCount);

        // Snapshot fixed here, before the destroy commits.
        using var reader = dbe.CreateQuickTransaction();
        var before = CountOn(ClusterCountPath.Planner, () => reader.QueryExact<QCountUnit>().Count(), out _, out _);

        using (var writer = dbe.CreateQuickTransaction())
        {
            writer.Destroy(ids[0]);
            writer.Destroy(ids[1]);
            writer.Destroy(ids[2]);
            writer.Commit();
        }

        var afterPlanned = CountOn(ClusterCountPath.Planner, () => reader.QueryExact<QCountUnit>().Count(), out var occupancy, out _);
        var afterProbe = CountOn(ClusterCountPath.MapProbe, () => reader.QueryExact<QCountUnit>().Count(), out _, out _);

        Assert.Multiple(() =>
        {
            Assert.That(before, Is.EqualTo(BaseCount), "sanity: the reader sees the seeded population before the writer runs");
            Assert.That(afterPlanned, Is.EqualTo(BaseCount),
                "the reader's snapshot predates the destroy, so those 3 entities must remain visible to it — the occupancy word has already dropped them, so "
                + "a popcount without the died-TSN gate would under-count");
            Assert.That(afterProbe, Is.EqualTo(afterPlanned), "and the two strategies must still agree");
            Assert.That(occupancy, Is.Zero, "a reader older than the death cannot use occupancy at all — the gate must have sent it to the probe");
        });
    }

    /// <summary>Occupancy is liveness, not enabled bits — any enabled/disabled requirement disqualifies the shortcut.</summary>
    [Test]
    public void AnEnabledPredicate_FallsBackToTheProbe()
    {
        using var dbe = SetupEngine();
        SpawnBase(dbe, BaseCount);

        using var tx = dbe.CreateQuickTransaction();
        AssertAgree(() => tx.QueryExact<QCountUnit>().Enabled<QCountTag>().Count(), BaseCount, expectFastPath: false, "a query with a T2 requirement");
    }

    /// <summary>An entity pending destroy still holds its occupancy bit, so the shortcut would over-count by exactly the pending set.</summary>
    [Test]
    public void APendingDestroy_IsExcluded_AndFallsBackToTheProbe()
    {
        using var dbe = SetupEngine();
        var ids = SpawnBase(dbe, BaseCount);

        using var tx = dbe.CreateQuickTransaction();
        tx.Destroy(ids[0]);
        tx.Destroy(ids[1]);
        tx.Destroy(ids[2]);

        AssertAgree(() => tx.QueryExact<QCountUnit>().Count(), BaseCount - 3, expectFastPath: false, "a transaction holding pending destroys");
    }

    /// <summary>
    /// Read-your-own-writes: spawns still pending in this transaction own no cluster slot, so they must be added exactly once by the caller's pending pass and
    /// must not also appear in the popcount.
    /// </summary>
    [Test]
    public void PendingSpawns_AreCountedExactlyOnce()
    {
        using var dbe = SetupEngine();
        SpawnBase(dbe, BaseCount);

        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < 5; i++)
        {
            tx.Spawn<QCountUnit>(QCountUnit.Body.Set(new QCountBody(1000 + i)), QCountUnit.Tag.Set(new QCountTag(i)));
        }

        AssertAgree(() => tx.QueryExact<QCountUnit>().Count(), BaseCount + 5, expectFastPath: true, "a transaction holding pending spawns");
    }

    /// <summary>
    /// The case that would be WRONG rather than slow. The reader's snapshot is fixed before a second commit; those entities own occupancy bits by the time it
    /// counts, so a popcount taken without the born-TSN gate would include them.
    /// </summary>
    [Test]
    public void ReaderPredatingACommit_DoesNotCountTheNewEntities()
    {
        using var dbe = SetupEngine();
        SpawnBase(dbe, BaseCount);

        // Snapshot fixed here, before the writer commits.
        using var reader = dbe.CreateQuickTransaction();
        var before = CountOn(ClusterCountPath.Planner, () => reader.QueryExact<QCountUnit>().Count(), out _, out _);

        using (var writer = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 40; i++)
            {
                writer.Spawn<QCountUnit>(QCountUnit.Body.Set(new QCountBody(2000 + i)), QCountUnit.Tag.Set(new QCountTag(i)));
            }

            writer.Commit();
        }

        var afterPlanned = CountOn(ClusterCountPath.Planner, () => reader.QueryExact<QCountUnit>().Count(), out _, out _);
        var afterProbe = CountOn(ClusterCountPath.MapProbe, () => reader.QueryExact<QCountUnit>().Count(), out _, out _);

        Assert.Multiple(() =>
        {
            Assert.That(before, Is.EqualTo(BaseCount), "sanity: the reader sees the seeded population before the writer runs");
            Assert.That(afterPlanned, Is.EqualTo(BaseCount),
                "the reader's snapshot predates the second commit, so those 40 entities must stay invisible — a popcount without the born-TSN gate would include them");
            Assert.That(afterProbe, Is.EqualTo(afterPlanned), "and the two strategies must still agree once the writer has committed");
        });
    }
}
