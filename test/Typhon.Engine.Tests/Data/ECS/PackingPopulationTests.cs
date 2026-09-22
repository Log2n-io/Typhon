using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// CR-03: the packing bound is computed from the ARCHETYPE's own population in a cell, never the grid-wide sum (#927).
/// </summary>
/// <remarks>
/// <para><b>The defect.</b> <c>CellState.EntityCount</c> totals every archetype sharing a cell, and both consumers of the bound —
/// <c>CellTargetResolver.Resolve</c>, which feeds the drift gate, the repair gate and the tightness telemetry, and <c>ExceedsGrowthCap</c> — read it. A
/// minority archetype is therefore judged against the tiling of entities it does not own: measured on SWG Tatooine x16 at 1 024 m, the engine's bound was
/// 0.29x the Player archetype's own and 0.85x Creature's, and the drift gate kept firing on player clusters no packing could satisfy.</para>
/// <para><b>Why cluster count is the substitute.</b> The bound is a ratio, <c>slotsPerCluster / E</c>, so any per-archetype quantity proportional to E
/// serves. <see cref="CellClusterPool"/> already maintains this archetype's cluster count per cell at O(1), at the very sites that bump
/// <c>EntityCount</c> — so <c>clusters x slotsPerCluster</c> costs no new counter, no new maintenance site and no extra cache line. The alternative, a
/// per-archetype entity population maintained at every claim and release, is what measured 2-7 % slower on the tick.</para>
/// <para><b>These are unit tests of the substitution itself</b>, not of the gates that consume it: the bound's own arithmetic is already pinned by
/// <c>ClusterDensityTargetTests</c>, and what #927 changes is which population is fed in.</para>
/// </remarks>
[TestFixture]
class PackingPopulationTests
{
    private const int SlotsPerCluster = 64;

    private static CellClusterPool PoolWith(int cellKey, int clusters)
    {
        var pool = new CellClusterPool();
        for (var i = 0; i < clusters; i++)
        {
            pool.AddCluster(cellKey, i);
        }

        return pool;
    }

    /// <summary>
    /// The grid is only dereferenced on the A/B fallback path, so the own-population path is testable without one — which is itself the point: the reading
    /// no longer depends on grid-wide state.
    /// </summary>
    [Test]
    [VerifiesRule("CR-03")]
    public void ThePopulationIsTheArchetypesOwnClusters_NotTheGridWideSum()
    {
        var saved = ArchetypeClusterState.GridWidePackingBound;
        ArchetypeClusterState.GridWidePackingBound = false;
        try
        {
            var pool = PoolWith(cellKey: 7, clusters: 3);
            Assert.That(ArchetypeClusterState.PackingPopulationInCell(pool, null, 7, SlotsPerCluster), Is.EqualTo(3 * SlotsPerCluster),
                "the population must come from this archetype's own clusters in the cell");
        }
        finally
        {
            ArchetypeClusterState.GridWidePackingBound = saved;
        }
    }

    /// <summary>Two archetypes sharing one cell each read their OWN population — the whole defect, stated as two pools over the same cell key.</summary>
    [Test]
    [VerifiesRule("CR-03")]
    public void TwoArchetypesSharingACell_EachReadTheirOwnPopulation()
    {
        var saved = ArchetypeClusterState.GridWidePackingBound;
        ArchetypeClusterState.GridWidePackingBound = false;
        try
        {
            var majority = PoolWith(cellKey: 3, clusters: 20);
            var minority = PoolWith(cellKey: 3, clusters: 2);

            var majorityPop = ArchetypeClusterState.PackingPopulationInCell(majority, null, 3, SlotsPerCluster);
            var minorityPop = ArchetypeClusterState.PackingPopulationInCell(minority, null, 3, SlotsPerCluster);

            Assert.Multiple(() =>
            {
                Assert.That(majorityPop, Is.EqualTo(20 * SlotsPerCluster));
                Assert.That(minorityPop, Is.EqualTo(2 * SlotsPerCluster));

                // And therefore a LOOSER bound for the minority: a cell it barely occupies cannot demand the tiling of the archetype that fills it.
                var majorityBound = ArchetypeClusterState.PackingBoundRatio(majorityPop, SlotsPerCluster, flat: true);
                var minorityBound = ArchetypeClusterState.PackingBoundRatio(minorityPop, SlotsPerCluster, flat: true);
                Assert.That(minorityBound, Is.GreaterThan(majorityBound),
                    "the minority archetype was held to a tightness derived from a population it does not own");
            });
        }
        finally
        {
            ArchetypeClusterState.GridWidePackingBound = saved;
        }
    }

    /// <summary>An empty cell has no population, and the bound's own "fits one cluster" basin then switches maintenance off — as it did before.</summary>
    [Test]
    [VerifiesRule("CR-03")]
    public void AnEmptyCellHasNoPopulation_AndTheBoundIsTheWholeCell()
    {
        var saved = ArchetypeClusterState.GridWidePackingBound;
        ArchetypeClusterState.GridWidePackingBound = false;
        try
        {
            var pool = new CellClusterPool();
            Assert.That(ArchetypeClusterState.PackingPopulationInCell(pool, null, 11, SlotsPerCluster), Is.Zero);
            Assert.That(ArchetypeClusterState.PackingBoundRatio(0, SlotsPerCluster, flat: true), Is.EqualTo(1f),
                "a cell holding nothing of this archetype is bounded at the cell, which is where intra-cell maintenance switches itself off");
        }
        finally
        {
            ArchetypeClusterState.GridWidePackingBound = saved;
        }
    }

    /// <summary>A single cluster is the pre-existing "one cluster IS the cell" basin, and must stay there.</summary>
    [Test]
    [VerifiesRule("CR-03")]
    public void OneClusterInTheCell_LeavesTheBoundAtTheWholeCell()
    {
        var saved = ArchetypeClusterState.GridWidePackingBound;
        ArchetypeClusterState.GridWidePackingBound = false;
        try
        {
            var pool = PoolWith(cellKey: 1, clusters: 1);
            var pop = ArchetypeClusterState.PackingPopulationInCell(pool, null, 1, SlotsPerCluster);
            Assert.That(pop, Is.EqualTo(SlotsPerCluster));
            Assert.That(ArchetypeClusterState.PackingBoundRatio(pop, SlotsPerCluster, flat: true), Is.EqualTo(1f),
                "one cluster's worth of entities is bounded at the cell — the basin PackingBoundRatio already documents");
        }
        finally
        {
            ArchetypeClusterState.GridWidePackingBound = saved;
        }
    }
}
