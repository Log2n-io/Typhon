using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// Own archetype: ArchetypeRegistry is process-global and unsynchronised across parallel fixtures (#720).

[Component("Typhon.Test.QTally.Pos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct QTallyPos
{
    [Field]
    [SpatialIndex]
    public AABB2F Bounds;
}

[Archetype]
partial class QTallyUnit : Archetype<QTallyUnit>
{
    public static readonly Comp<QTallyPos> Pos = Register<QTallyPos>();
}

/// <summary>
/// The query-efficiency tally (SO-02): every range query adds, once, the clusters it opened, the entities it tested and its matches, on the thread it ran
/// on; the fence publishes what ran since the previous fence.
/// </summary>
/// <remarks>
/// One cell holds <see cref="Population"/> point entities in one cluster, on a line at y = 50: x = 10, 14, 18, … 86. A query can then be answered by hand —
/// one cluster, twenty candidates, and the entities its box or sphere covers.
/// </remarks>
[TestFixture]
class SpatialQueryTallyTests : TestBase<SpatialQueryTallyTests>
{
    private const int Population = 20;

    /// <summary>The start of the verifier's message when the tally is not the query's own count; the mutant looks for it.</summary>
    private const string TalliedWrong = "the tally is not what the query tested";

    // x in [9, 31] covers x = 10, 14, 18, 22, 26, 30.
    private static readonly AABB2F SixOfThem = new() { MinX = 9f, MinY = 40f, MaxX = 31f, MaxY = 60f };
    private static readonly BSphere2F SixByRadius = new() { CenterX = 20f, CenterY = 50f, Radius = 10.5f };
    private static readonly AABB2F AllOfThem = new() { MinX = 0f, MinY = 0f, MaxX = 99f, MaxY = 99f };

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<QTallyPos>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(1_000f, 1_000f), 100f));
        dbe.InitializeArchetypes();

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < Population; i++)
            {
                float x = 10f + (4f * i);
                tx.Spawn<QTallyUnit>(QTallyUnit.Pos.Set(new QTallyPos { Bounds = new AABB2F { MinX = x, MinY = 50f, MaxX = x, MaxY = 50f } }));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);
        Assert.That(StateOf(dbe).ActiveClusterCount, Is.EqualTo(1), "precondition: the whole population in one cluster");
        return dbe;
    }

    private static ArchetypeClusterState StateOf(DatabaseEngine dbe) => dbe._archetypeStates[Archetype<QTallyUnit>.Metadata.ArchetypeId].ClusterState;

    private static (long Clusters, long Candidates, long Hits) TallyOf(ArchetypeClusterState cs)
    {
        var tally = cs.QueryTally;
        if (tally == null)
        {
            return (0, 0, 0);
        }

        tally.Read(out var clusters, out var candidates, out var hits);
        return (clusters, candidates, hits);
    }

    /// <summary>What <paramref name="queries"/> added to the tally, against what it should have.</summary>
    private static void AssertTallied(DatabaseEngine dbe, (long Clusters, long Candidates, long Hits) expected, string what, Action queries)
    {
        var cs = StateOf(dbe);
        var before = TallyOf(cs);
        using (EpochGuard.Enter(dbe.EpochManager))
        {
            queries();
        }

        var after = TallyOf(cs);
        var added = (after.Clusters - before.Clusters, after.Candidates - before.Candidates, after.Hits - before.Hits);
        Assert.That(added, Is.EqualTo(expected), $"{what}: {TalliedWrong} (clusters, candidates, hits)");
    }

    private static int CountBox(DatabaseEngine dbe, in AABB2F box)
    {
        var e = dbe.ClusterSpatialQuery<QTallyUnit>().AABB(in box);
        try
        {
            return e.Count();
        }
        finally
        {
            e.Dispose();
        }
    }

    // ── Each drain tallies the query it ran ───────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Count, MoveNext and Fill, and a radius query, each add one cluster, its twenty entities and the matches — whatever the drain, and whether the query
    /// is disposed or only drained.
    /// </summary>
    [Test]
    [VerifiesRule("SO-02")]
    public void EachDrain_TalliesTheClusterItOpened_TheEntitiesItTested_AndItsMatches()
    {
        using var dbe = SetupEngine();
        var six = (1L, (long)Population, 6L);

        AssertTallied(dbe, six, "Count", () => Assert.That(CountBox(dbe, in SixOfThem), Is.EqualTo(6)));

        AssertTallied(dbe, six, "MoveNext", () =>
        {
            var hits = 0;
            foreach (var _ in dbe.ClusterSpatialQuery<QTallyUnit>().AABB(in SixOfThem))
            {
                hits++;
            }

            Assert.That(hits, Is.EqualTo(6));
        });

        AssertTallied(dbe, six, "Fill, four at a time", () =>
        {
            Span<ClusterSpatialQueryResult> buffer = stackalloc ClusterSpatialQueryResult[4];
            var e = dbe.ClusterSpatialQuery<QTallyUnit>().AABB(in SixOfThem);
            var hits = 0;
            for (var n = e.Fill(buffer); n > 0; n = e.Fill(buffer))
            {
                hits += n;
            }

            Assert.That(hits, Is.EqualTo(6));
        });

        AssertTallied(dbe, six, "Radius, drained without a Dispose", () =>
            Assert.That(dbe.ClusterSpatialQuery<QTallyUnit>().Radius(in SixByRadius).Count(), Is.EqualTo(6)));

        AssertTallied(dbe, (3L, 3L * Population, 18L), "three queries", () =>
        {
            for (var i = 0; i < 3; i++)
            {
                CountBox(dbe, in SixOfThem);
            }
        });

        // A box that opens the cluster and matches none of it (x in [11, 13]): twenty candidates, no hit.
        var none = new AABB2F { MinX = 11f, MinY = 40f, MaxX = 13f, MaxY = 60f };
        AssertTallied(dbe, (1L, (long)Population, 0L), "a query that matches nothing in the cluster it opens", () =>
            Assert.That(CountBox(dbe, in none), Is.Zero));

        // A box over empty space opens nothing: it tested nothing, and says so.
        var empty = new AABB2F { MinX = 500f, MinY = 500f, MaxX = 600f, MaxY = 600f };
        AssertTallied(dbe, (0L, 0L, 0L), "a query that opens no cluster", () => Assert.That(CountBox(dbe, in empty), Is.Zero));
    }

    /// <summary>
    /// A caller that stops early has still opened its cluster, and the tally counts all of it: every entity of the cluster, and only the matches taken.
    /// </summary>
    /// <remarks>
    /// Counting only the entities the drain had reached made the tally depend on the narrowphase: the block kernel decides sixteen slots at once, so the
    /// same stopped query had tested one entity without it and fifteen with it (<see cref="TheTally_IsTheSameWithTheBlockKernelAndWithout"/>).
    /// </remarks>
    [Test]
    [VerifiesRule("SO-02")]
    public void AStoppedQuery_CountsTheWholeClusterItOpened()
    {
        using var dbe = SetupEngine();

        AssertTallied(dbe, (1L, (long)Population, 1L), "MoveNext once, then Dispose", () =>
        {
            var e = dbe.ClusterSpatialQuery<QTallyUnit>().AABB(in AllOfThem);
            try
            {
                Assert.That(e.MoveNext(), Is.True);
            }
            finally
            {
                e.Dispose();
            }
        });

        AssertTallied(dbe, (1L, (long)Population, 5L), "one Fill of five, then Dispose", () =>
        {
            Span<ClusterSpatialQueryResult> buffer = stackalloc ClusterSpatialQueryResult[5];
            var e = dbe.ClusterSpatialQuery<QTallyUnit>().AABB(in AllOfThem);
            try
            {
                Assert.That(e.Fill(buffer), Is.EqualTo(5));
            }
            finally
            {
                e.Dispose();
            }
        });

        AssertTallied(dbe, (1L, (long)Population, (long)Population), "the same query drained", () =>
            Assert.That(CountBox(dbe, in AllOfThem), Is.EqualTo(Population)));
    }

    /// <summary>
    /// The tally does not depend on the narrowphase. A query stopped at its first match, where the block kernel has rejected most of the slots the loop has
    /// not reached, tallies what the loop alone tallies.
    /// </summary>
    [Test]
    [NonParallelizable] // flips the process-wide narrowphase switch
    [VerifiesRule("SO-02")]
    public void TheTally_IsTheSameWithTheBlockKernelAndWithout([Values(true, false)] bool kernel)
    {
        if (kernel)
        {
            Assume.That(NarrowphaseAabb2F.Best, Is.Not.EqualTo(NarrowphaseAabb2F.Kernel.None), "no block kernel on this machine");
        }

        var before = SpatialQueryTuning.SimdNarrowphase;
        SpatialQueryTuning.SimdNarrowphase = kernel;
        try
        {
            using var dbe = SetupEngine();
            Assert.That(dbe.ClusterSpatialQuery<QTallyUnit>().AABB(in SixOfThem).UsesAabb2FBlocks, Is.EqualTo(kernel),
                "precondition: the narrowphase this case asks for");

            AssertTallied(dbe, (1L, (long)Population, 1L), "one MoveNext of six matches, then Dispose", () =>
            {
                var e = dbe.ClusterSpatialQuery<QTallyUnit>().AABB(in SixOfThem);
                try
                {
                    Assert.That(e.MoveNext(), Is.True);
                }
                finally
                {
                    e.Dispose();
                }
            });

            AssertTallied(dbe, (1L, (long)Population, 6L), "Count", () => Assert.That(CountBox(dbe, in SixOfThem), Is.EqualTo(6)));
        }
        finally
        {
            SpatialQueryTuning.SimdNarrowphase = before;
        }
    }

    /// <summary>
    /// A copy that carries a rent and hands it back adds the tally of everything counted before the two split; the original's return, now stale, adds
    /// nothing. Counted twice, the query would read two clusters.
    /// </summary>
    [Test]
    [VerifiesRule("SO-02")]
    public void ACopyThatHandsTheWindowBack_TalliesTheQueryOnce()
    {
        using var dbe = SetupEngine();

        AssertTallied(dbe, (1L, (long)Population, (long)Population), "one hit, then a copy drained, then the original disposed", () =>
        {
            var e = dbe.ClusterSpatialQuery<QTallyUnit>().AABB(in AllOfThem);
            Assert.That(e.MoveNext(), Is.True);
            var copy = e.GetEnumerator();
            Assert.That(copy.Count(), Is.EqualTo(Population - 1));
            e.Dispose();
        });
    }

    /// <summary>
    /// A tally the query path adds twice is caught: the verifiers compare the tally with the query's own count exactly. The second add is the one a stale
    /// copy's return would make if the token did not stop it — the query's own cluster, entities and matches again.
    /// </summary>
    [Test]
    [RuleMutant("SO-02")]
    public void AQueryTalliedTwice_IsCaught()
    {
        using var dbe = SetupEngine();

        RuleMutants.AssertDetects("SO-02", TalliedWrong, () => AssertTallied(dbe, (1L, (long)Population, 6L), "Count, tallied twice", () =>
        {
            CountBox(dbe, in SixOfThem);
            StateOf(dbe).RecordQueryTally(Environment.CurrentManagedThreadId, 1, Population, 6);
        }));
    }

    // ── Per thread, and nothing lost ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Eight threads querying at once, each into its own slot: every query is counted, none twice.</summary>
    [Test]
    [VerifiesRule("SO-02")]
    public void ConcurrentQueries_OnEightThreads_AreEachCountedOnce()
    {
        const int Threads = 8;
        const int QueriesEach = 500;

        using var dbe = SetupEngine();
        var cs = StateOf(dbe);
        var before = TallyOf(cs);

        using var start = new Barrier(Threads);
        var failures = 0;
        var workers = new Thread[Threads];
        for (var t = 0; t < Threads; t++)
        {
            workers[t] = new Thread(() =>
            {
                // Before anything that can throw, so a worker that fails cannot leave the others waiting at the barrier.
                start.SignalAndWait();
                try
                {
                    using var epoch = EpochGuard.Enter(dbe.EpochManager);
                    for (var i = 0; i < QueriesEach; i++)
                    {
                        if (CountBox(dbe, in SixOfThem) != 6)
                        {
                            Interlocked.Increment(ref failures);
                        }
                    }
                }
                catch
                {
                    Interlocked.Increment(ref failures);
                }
            });
            workers[t].Start();
        }

        foreach (var worker in workers)
        {
            Assert.That(worker.Join(TimeSpan.FromSeconds(30)), Is.True, "a worker did not finish");
        }

        var after = TallyOf(cs);
        const long Queries = Threads * QueriesEach;
        Assert.Multiple(() =>
        {
            Assert.That(failures, Is.Zero, "every query must answer six");
            Assert.That(after.Clusters - before.Clusters, Is.EqualTo(Queries));
            Assert.That(after.Candidates - before.Candidates, Is.EqualTo(Queries * Population));
            Assert.That(after.Hits - before.Hits, Is.EqualTo(Queries * 6));
        });
    }

    /// <summary>
    /// Every thread id up to the engine's bound owns a slot, in chunks made as their threads arrive — four of them at once here — and an id past the bound
    /// shares one slot through interlocked adds. Nothing is lost either way, whichever chunk an id falls in.
    /// </summary>
    [Test]
    [VerifiesRule("SO-02")]
    public void EveryThreadIdUpToTheBound_OwnsASlot_AndNothingIsLost()
    {
        const int Threads = 4;
        const int AddsEach = 20_000;
        var tally = new SpatialQueryTally();

        // The first chunk, one far out, and the last owned id.
        tally.Add(3, 1, 10, 2);
        tally.Add(5_000, 1, 10, 2);
        tally.Add(SpatialQueryTally.MaxOwnedThreadId, 1, 10, 2);

        using var start = new Barrier(Threads);
        var workers = new Thread[Threads];
        for (var t = 0; t < Threads; t++)
        {
            // Each worker its own id, in its own chunk, so the four chunks are made concurrently; and all four share the slot past the bound.
            var owned = 1_000 + (t * SpatialQueryTally.ChunkSlots);
            workers[t] = new Thread(() =>
            {
                start.SignalAndWait();
                for (var i = 0; i < AddsEach; i++)
                {
                    tally.Add(owned, 1, 2, 3);
                    tally.Add(SpatialQueryTally.MaxOwnedThreadId + 7, 1, 2, 3);
                }
            });
            workers[t].Start();
        }

        foreach (var worker in workers)
        {
            Assert.That(worker.Join(TimeSpan.FromSeconds(30)), Is.True, "a worker did not finish");
        }

        tally.Read(out var clusters, out var candidates, out var hits);
        const long Adds = 2L * Threads * AddsEach;
        Assert.Multiple(() =>
        {
            Assert.That(clusters, Is.EqualTo(Adds + 3));
            Assert.That(candidates, Is.EqualTo((Adds * 2) + 30));
            Assert.That(hits, Is.EqualTo((Adds * 3) + 6));
        });
    }

    // ── Published per tick ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The fence publishes the queries that ran since the previous fence, once: a tick without queries reads zero, and its ratio zero rather than a
    /// division by nothing.
    /// </summary>
    [Test]
    [VerifiesRule("SO-01")]
    [VerifiesRule("SO-02")]
    public void TheFencePublishesTheTicksQueries_AndZeroOnATickWithNone()
    {
        using var dbe = SetupEngine();
        var archetypeId = Archetype<QTallyUnit>.Metadata.ArchetypeId;

        using (EpochGuard.Enter(dbe.EpochManager))
        {
            for (var i = 0; i < 3; i++)
            {
                CountBox(dbe, in SixOfThem);
            }
        }

        dbe.WriteTickFence(2);
        var t = dbe.GetSpatialTelemetry(archetypeId);
        Assert.Multiple(() =>
        {
            Assert.That(t.QueryClustersOpened, Is.EqualTo(3));
            Assert.That(t.QueryCandidates, Is.EqualTo(3 * Population));
            Assert.That(t.QueryHits, Is.EqualTo(18));
            Assert.That(t.QueryCandidatesPerHit, Is.EqualTo(3d * Population / 18d).Within(1e-12));
        });

        dbe.WriteTickFence(3);
        var quiet = dbe.GetSpatialTelemetry(archetypeId);
        Assert.Multiple(() =>
        {
            Assert.That(quiet.QueryClustersOpened, Is.Zero, "a tick whose systems ran no query");
            Assert.That(quiet.QueryCandidates, Is.Zero);
            Assert.That(quiet.QueryHits, Is.Zero);
            Assert.That(quiet.QueryCandidatesPerHit, Is.Zero);
        });
    }
}
