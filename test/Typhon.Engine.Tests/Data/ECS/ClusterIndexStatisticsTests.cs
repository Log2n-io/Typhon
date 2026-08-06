using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// Versioned, so its writes go through the COMMIT-time reconcile (ReconcileClusterIndexAndViews) rather than the tick-fence drain — the path #665 step 1
// added the unchanged-field guard to.
[Component("Typhon.Test.ECS.Stats.Ranked", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential)]
struct StatsRanked
{
    // AllowMultiple: a low-cardinality classification is the shape the unchanged-field guard exists for — index the tier, write the score that churns.
    [Index(AllowMultiple = true)] public int Tier;
    public int Score;

    public StatsRanked(int tier, int score)
    {
        Tier = tier;
        Score = score;
    }
}

// SingleVersion sibling with no indexed field — its only job is to make the archetype cluster-eligible, which is what moves StatsRanked's index off the
// ComponentTable and onto the archetype.
[Component("Typhon.Test.ECS.Stats.Tag", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct StatsTag
{
    public int Marker;
    public StatsTag(int marker) => Marker = marker;
}

[Archetype]
class StatsArch : Archetype<StatsArch>
{
    public static readonly Comp<StatsRanked> Ranked = Register<StatsRanked>();
    public static readonly Comp<StatsTag> Tag = Register<StatsTag>();
}

// Pure-Versioned, and cluster-backed like everything else since #629. It still earns its place here for the reason it was added: it shares StatsRanked's
// ComponentTable with StatsArch, so the two archetypes must be planned from separate statistics for the same component type.
[Archetype]
class StatsFlatArch : Archetype<StatsFlatArch>
{
    public static readonly Comp<StatsRanked> Ranked = Register<StatsRanked>();
}

/// <summary>
/// Per-archetype index statistics and the write-path guards that feed them — issue #665.
/// </summary>
/// <remarks>
/// <para>
/// Two defects, one fixture, because they are two ends of the same wire. Selectivity statistics were maintained only by <c>IndexMaintainer</c>, which a
/// cluster-backed archetype never reaches, so <c>MutationsSinceRebuild</c> never moved and <c>StatisticsWorker</c> never crossed its threshold — the
/// estimates froze at whatever the first rebuild produced. Pointing the worker at the ComponentTable instead would have been worse: a cluster archetype's
/// entities are not in <c>ComponentSegment</c> at all, so the scan samples nothing and publishes statistics built from an empty scan.
/// </para>
/// <para>
/// The mutation counter is also the only direct observable for the unchanged-field guard. It is incremented past the guard, so "an unrelated write does no
/// index work" is a thing a test can assert rather than a claim about page counts.
/// </para>
/// </remarks>
[TestFixture]
class ClusterIndexStatisticsTests : TestBase<ClusterIndexStatisticsTests>
{
    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<StatsRanked>();
        dbe.RegisterComponentFromAccessor<StatsTag>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static ArchetypeClusterState ClusterState(DatabaseEngine dbe)
        => dbe._archetypeStates[Archetype<StatsArch>.Metadata.ArchetypeId].ClusterState;

    /// <summary>The shared per-ComponentTable home for <see cref="StatsRanked"/> — the array the planner used to estimate from.</summary>
    private static ComponentTable RankedTable(DatabaseEngine dbe)
    {
        var clusterState = ClusterState(dbe);
        var engineState = dbe._archetypeStates[Archetype<StatsArch>.Metadata.ArchetypeId];
        return engineState.SlotToComponentTable[clusterState.IndexSlots[0].Slot];
    }

    private static EntityId Spawn(Transaction tx, int tier, int score)
        => tx.Spawn<StatsArch>(StatsArch.Ranked.Set(new StatsRanked(tier, score)), StatsArch.Tag.Set(new StatsTag(score)));

    private static void SpawnMany(DatabaseEngine dbe, int count, System.Func<int, int> tierOf)
    {
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < count; i++)
            {
                Spawn(tx, tierOf(i), i);
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);
    }

    /// <summary>
    /// AC: the archetype owns a statistics array parallel to its index fields, and the ComponentTable's array describes NOTHING for it — which is the
    /// degraded-plan hypothesis, confirmed rather than assumed.
    /// </summary>
    /// <remarks>
    /// The planner used to estimate every cluster query from <c>ComponentTable.IndexStats</c>. That array wraps the per-ComponentTable B+Tree, which for a
    /// cluster-backed archetype holds no entries at all — so <c>EntryCount</c> is 0, every estimator returns 0, and the plan is built from a number that
    /// describes no population. Not a subtle skew: the input was empty.
    /// </remarks>
    [Test]
    public void PerArchetypeStats_DescribeTheEntities_WhereTheComponentTableArrayIsEmpty()
    {
        using var dbe = SetupEngine();
        SpawnMany(dbe, 40, i => i % 4);

        var clusterState = ClusterState(dbe);
        var archetypeStats = clusterState.IndexSlots[0].Stats;

        Assert.Multiple(() =>
        {
            Assert.That(archetypeStats, Is.Not.Null.And.Length.EqualTo(clusterState.IndexSlots[0].Fields.Length),
                "the archetype's statistics array must be parallel to its index fields");
            Assert.That(archetypeStats[0].EntryCount, Is.EqualTo(4), "the per-archetype tree holds the four distinct tiers");

            // The companion assertion — that the ComponentTable's array read 0 — is gone with the array itself (#629). It existed to show the planner had been
            // estimating from nothing; there is now no second array to estimate from, which is the stronger version of the same statement.
        });
    }

    /// <summary>
    /// AC: the mutation counter moves for real index work and stays put for an update that leaves every indexed field alone — the unchanged-field guard,
    /// made observable.
    /// </summary>
    [Test]
    public void MutationCounter_MovesOnlyForRealIndexWork()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = Spawn(tx, tier: 3, score: 100);
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        var clusterState = ClusterState(dbe);
        Assert.That(clusterState.MutationsSinceRebuild, Is.GreaterThan(0), "premise: spawning an indexed entity is index work");

        // An update that changes Score but NOT the indexed Tier: the guard must skip the whole field, so the counter must not move.
        clusterState.MutationsSinceRebuild = 0;
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(id).Write(StatsArch.Ranked) = new StatsRanked(3, 999);
            tx.Commit();
        }
        dbe.WriteTickFence(2);
        Assert.That(clusterState.MutationsSinceRebuild, Is.Zero,
            "an update that leaves every indexed field alone must do no tree work — two descents, a leaf write-lock and a dirtied page, all to move a "
            + "key onto itself");

        // Same write shape, but the indexed field genuinely changes.
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(id).Write(StatsArch.Ranked) = new StatsRanked(7, 999);
            tx.Commit();
        }
        dbe.WriteTickFence(3);
        Assert.That(clusterState.MutationsSinceRebuild, Is.GreaterThan(0), "and a real key move must still be counted");
    }

    /// <summary>
    /// The counter is accumulated in a local and written to the shared field once per commit rather than once per indexed field (review M4). That is only a
    /// safe change if the TOTAL is identical, so this pins the exact number rather than "greater than zero" — an off-by-one in the hoist, or an early
    /// <c>continue</c> that skips the local but not the field, would leave every existing assertion in this fixture green.
    /// </summary>
    [Test]
    public void MutationCounter_CountsExactlyOncePerChangedIndexedField()
    {
        using var dbe = SetupEngine();
        var clusterState = ClusterState(dbe);

        // One indexed field on this archetype (StatsRanked.Tier), so a spawn is exactly one unit of index work.
        EntityId id;
        clusterState.MutationsSinceRebuild = 0;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = Spawn(tx, tier: 1, score: 10);
            tx.Commit();
        }
        dbe.WriteTickFence(1);
        Assert.That(clusterState.MutationsSinceRebuild, Is.EqualTo(1), "one spawn x one indexed field = one count");

        clusterState.MutationsSinceRebuild = 0;
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(id).Write(StatsArch.Ranked) = new StatsRanked(2, 10);
            tx.Commit();
        }
        dbe.WriteTickFence(2);
        Assert.That(clusterState.MutationsSinceRebuild, Is.EqualTo(1), "one key move x one indexed field = one count");

        // Three spawns in one commit: the hoisted local must survive the whole commit, not reset per entity.
        clusterState.MutationsSinceRebuild = 0;
        using (var tx = dbe.CreateQuickTransaction())
        {
            Spawn(tx, tier: 4, score: 1);
            Spawn(tx, tier: 5, score: 2);
            Spawn(tx, tier: 6, score: 3);
            tx.Commit();
        }
        dbe.WriteTickFence(3);
        Assert.That(clusterState.MutationsSinceRebuild, Is.EqualTo(3), "three spawns x one indexed field = three counts");
    }


    /// <summary>AC: the cluster rebuilder populates the distribution structures the estimators read, by scanning the archetype's clusters.</summary>
    [Test]
    public void RebuildClusterAll_PopulatesTheDistribution()
    {
        using var dbe = SetupEngine();
        // Skewed on purpose: tier 0 gets half the entities, so a correct MCV has something to find.
        SpawnMany(dbe, 60, i => i % 2 == 0 ? 0 : 1 + (i % 3));

        var clusterState = ClusterState(dbe);
        var stats = clusterState.IndexSlots[0].Stats;
        Assert.That(stats[0].Histogram, Is.Null, "premise: nothing has rebuilt yet");

        StatisticsRebuilder.RebuildClusterAll(clusterState, dbe.EpochManager);

        Assert.Multiple(() =>
        {
            Assert.That(stats[0].Histogram, Is.Not.Null, "the histogram must be published");
            Assert.That(stats[0].Histogram.TotalCount, Is.EqualTo(60),
                "and must account for every live entity — the scan walks clusters, not ComponentSegment pages");
            Assert.That(stats[0].MostCommonValues, Is.Not.Null);
            Assert.That(stats[0].HyperLogLog, Is.Not.Null);
        });
    }

    /// <summary>
    /// AC: the selectivity estimator, given the archetype's array, returns a cardinality that tracks the real distribution — where the ComponentTable's
    /// array yields 0 for every predicate.
    /// </summary>
    [Test]
    public void Estimator_OnPerArchetypeStats_TracksTheRealDistribution()
    {
        using var dbe = SetupEngine();
        SpawnMany(dbe, 60, i => i % 2 == 0 ? 0 : 1 + (i % 3));

        var clusterState = ClusterState(dbe);
        StatisticsRebuilder.RebuildClusterAll(clusterState, dbe.EpochManager);

        var estimator = AdvancedSelectivityEstimator.Instance;
        var archetypeStats = clusterState.IndexSlots[0].Stats;

        var hot = estimator.EstimateCardinality(archetypeStats, 0, CompareOp.Equal, 0);
        var cold = estimator.EstimateCardinality(archetypeStats, 0, CompareOp.Equal, 2);

        Assert.Multiple(() =>
        {
            Assert.That(hot, Is.GreaterThan(cold), "tier 0 holds half the entities; a rarer tier must estimate lower");
            Assert.That(hot, Is.GreaterThan(0));
        });
    }

    /// <summary>
    /// AC: the query resolves the planner's statistics to the per-archetype array for a cluster-backed archetype, and back to the ComponentTable's for one
    /// that is not.
    /// </summary>
    /// <remarks>
    /// Asserted at this seam rather than through a query result on purpose. The planner's choice is a PERFORMANCE decision — Path A and Path B return the
    /// same entities — so no observable behaviour distinguishes a good plan from a bad one, and a test that went through <c>Execute()</c> would pass either
    /// way. What the estimate feeds is <c>EstimateClusterSelectivity</c>, which reads a 0 count as "unknown" and takes Path B every time.
    /// </remarks>
    /// <remarks>
    /// The second assertion used to check the FALLBACK — <c>StatsFlatArch</c> was pure-Versioned, so it had no cluster state and the planner had to keep
    /// reading the shared array. Since #629 it is cluster-backed too, and the property that matters is stronger: two archetypes sharing one ComponentTable
    /// must each be planned from THEIR OWN statistics. Estimating either from the other is what makes the planner pick a scan sized for the wrong population,
    /// and the shared array — which no longer receives any entries — would read as 0, i.e. "unknown", for both.
    /// </remarks>
    [Test]
    public void PlannerStats_PicksEachArchetypesOwnArray()
    {
        using var dbe = SetupEngine();
        SpawnMany(dbe, 20, i => i % 4);

        var clusterState = ClusterState(dbe);
        var flatState = dbe._archetypeStates[Archetype<StatsFlatArch>.Metadata.ArchetypeId].ClusterState;
        var ct = RankedTable(dbe);

        using var tx = dbe.CreateQuickTransaction();
        var clusterQuery = tx.Query<StatsArch>().WhereField<StatsRanked>(r => r.Tier == 0);
        var flatQuery = tx.Query<StatsFlatArch>().WhereField<StatsRanked>(r => r.Tier == 0);

        Assert.Multiple(() =>
        {
            Assert.That(clusterQuery.PlannerStats(ct), Is.SameAs(clusterState.IndexSlots[0].Stats),
                "a query resolving to one cluster-backed archetype must be planned from that archetype's own statistics");
            Assert.That(flatQuery.PlannerStats(ct), Is.SameAs(flatState.IndexSlots[0].Stats),
                "the second archetype sharing this ComponentTable must be planned from ITS statistics, not the first one's and not the empty shared array");
            Assert.That(flatState.IndexSlots[0].Stats, Is.Not.SameAs(clusterState.IndexSlots[0].Stats),
                "premise: the two archetypes really do own separate statistics arrays");
        });
    }
}
