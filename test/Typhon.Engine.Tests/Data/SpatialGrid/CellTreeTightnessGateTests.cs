using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// Step 16 of the VDB cell-grid design (§5.8.4, decision D3): a cell half promotes to a per-cell R-Tree only when it holds enough clusters AND those
/// clusters are tight enough for the tree to prune between them.
/// </summary>
/// <remarks>
/// <para><b>What the measurement said.</b> The sweep that calibrated the shipped count threshold laid its clusters at 1.5× perfect tiling — 3.8 % of the
/// cell — while the engine runs at 63–103 %. Re-run against the engine's own tightness the tree wins 1.47× at 0.038 of the cell, breaks even at 0.10, and
/// loses at every count and selectivity from 0.25 upward (0.08× at 0.90). A cell promoted at the engine's tightness pays the 20–50× per-move update tax
/// for pruning it never gets, which is why the count alone was the wrong gate.</para>
/// <para>These tests are the gate's own, so unlike every other cell-tree fixture they leave <c>ClusterCellTreePromoteTightness</c> at its shipped value.</para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class CellTreeTightnessGateTests : TestBase<CellTreeTightnessGateTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    private const float CellSize = 1_000f;

    /// <summary>Low enough that a few hundred entities cross it, so a test is a handful of milliseconds rather than sixty thousand spawns.</summary>
    private const int PromoteAt = 8;

    private static ClCohPos PointAt(float x, float y) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Mass = 1.0f };

    private DatabaseEngine SetupEngine(IServiceScope scope, float tightness = SpatialOptions.DefaultCellTreePromoteTightness)
    {
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClCohPos>();
        // Repair off: every bound these tests read is the one placement produced, so the gate is measured against a layout the test controls.
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(
            new Vector2(0, 0), new Vector2(4_000f, 4_000f), CellSize, reclusterBudgetMs: 0f, batchSpawnSortThreshold: 0));
        dbe.ClusterCellTreePromoteThreshold = PromoteAt;
        dbe.ClusterCellTreePromoteTightness = tightness;
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static ArchetypeClusterState ClusterStateOf(DatabaseEngine dbe) =>
        dbe._archetypeStates[Archetype<ClCohUnit>.Metadata.ArchetypeId].ClusterState;

    /// <summary>Slots per cluster for this archetype — the layout's, not the 64-bit ceiling.</summary>
    private static int SlotsPerCluster(DatabaseEngine dbe) => System.Numerics.BitOperations.PopCount(ClusterStateOf(dbe).Layout.FullMask);

    /// <summary>
    /// Fill cell (0,0) with <paramref name="clusters"/> clusters' worth of entities. <paramref name="spread"/> is the fraction of the cell each cluster's
    /// entities are scattered over: the spawn path is first fit in arrival order, so consecutive spawns share a cluster and the spread IS the cluster's
    /// eventual bound.
    /// </summary>
    private static List<EntityId> FillCell(DatabaseEngine dbe, int clusters, float spread)
    {
        var slots = SlotsPerCluster(dbe);
        var ids = new List<EntityId>(clusters * slots);
        var rng = new Random(20260906);
        using var tx = dbe.CreateQuickTransaction();
        for (var c = 0; c < clusters; c++)
        {
            // Each cluster's entities live in their own box of `spread` of the cell, placed so the boxes do not all sit on top of each other.
            var originX = 10f + ((c * 37f) % Math.Max(1f, (CellSize * (1f - spread)) - 20f));
            var originY = 10f + ((c * 61f) % Math.Max(1f, (CellSize * (1f - spread)) - 20f));
            for (var s = 0; s < slots; s++)
            {
                var x = originX + ((float)rng.NextDouble() * spread * CellSize);
                var y = originY + ((float)rng.NextDouble() * spread * CellSize);
                ids.Add(tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(x, y))));
            }
        }

        tx.Commit();
        return ids;
    }

    /// <summary>The mean largest-axis extent of the cell's clusters, as a fraction of the cell — the quantity the gate reads.</summary>
    private static double MeanExtentFraction(DatabaseEngine dbe)
    {
        var cs = ClusterStateOf(dbe);
        var cellKey = dbe.SpatialGrid.WorldToCellKey(50f, 50f, 0f);
        var clusters = cs.CellClusterPool.GetClusters(cellKey);
        var total = 0d;
        var counted = 0;
        for (var i = 0; i < clusters.Length; i++)
        {
            ref var b = ref cs.ClusterAabbs[clusters[i]];
            if (float.IsPositiveInfinity(b.MinX))
            {
                continue;
            }

            total += Math.Max(b.MaxX - b.MinX, b.MaxY - b.MinY);
            counted++;
        }

        return counted == 0 ? 0d : total / counted / CellSize;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // AC-16.1 — count is not enough: the cell must also be tight
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A cell holding well over the count threshold, whose clusters each span most of the cell, stays on the linear scan; the same population packed into
    /// tight clusters promotes. The count is identical in both arms, so the count gate cannot be what separates them.
    /// </summary>
    [Test]
    public void ALooseCellDoesNotPromoteAndATightOneDoes([Values(true, false)] bool tight)
    {
        ServiceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        using var scope = ServiceProvider.CreateScope();
        using var dbe = SetupEngine(scope);

        FillCell(dbe, PromoteAt + 4, spread: tight ? 0.02f : 0.90f);
        dbe.WriteTickFence(1);

        var cs = ClusterStateOf(dbe);
        var cellKey = dbe.SpatialGrid.WorldToCellKey(50f, 50f, 0f);
        var clusterCount = cs.CellClusterPool.GetClusters(cellKey).Length;
        var mean = MeanExtentFraction(dbe);

        Assert.Multiple(() =>
        {
            Assert.That(clusterCount, Is.GreaterThanOrEqualTo(PromoteAt), "precondition: the cell is over the COUNT threshold in both arms");
            if (tight)
            {
                Assert.That(mean, Is.LessThanOrEqualTo(SpatialOptions.DefaultCellTreePromoteTightness), $"precondition: packed (mean {mean:F3})");
                Assert.That(cs.PromotedCellCount, Is.GreaterThan(0), "a cell that is both full and packed is what the tree is for");
            }
            else
            {
                Assert.That(mean, Is.GreaterThan(SpatialOptions.DefaultCellTreePromoteTightness), $"precondition: loose (mean {mean:F3})");
                Assert.That(cs.PromotedCellCount, Is.Zero, "a tree over clusters that each span the cell prunes nothing and pays the update tax");
            }
        });
    }

    /// <summary>
    /// The gate is re-read at the FENCE, not only when a cluster joins the cell: a cell that was too loose when it filled, and is then packed without any
    /// arrival, promotes on the tick its bounds tighten. Without that the cell would wait for an unrelated spawn to notice.
    /// </summary>
    [Test]
    public void ACellPackedWithoutAnArrivalPromotesAtTheNextFence()
    {
        ServiceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        using var scope = ServiceProvider.CreateScope();
        using var dbe = SetupEngine(scope);

        var ids = FillCell(dbe, PromoteAt + 4, spread: 0.90f);
        dbe.WriteTickFence(1);
        var cs = ClusterStateOf(dbe);
        Assert.That(cs.PromotedCellCount, Is.Zero, "precondition: born loose, so not promoted");

        // Pull every entity into its cluster's own corner — the shape a repair's re-pack produces — without spawning or destroying anything.
        var slots = SlotsPerCluster(dbe);
        using (var tx = dbe.CreateQuickTransaction())
        {
            var accessor = tx.For<ClCohUnit>();
            try
            {
                var c = 0;
                foreach (var cluster in accessor.GetClusterEnumerator())
                {
                    var originX = 10f + (c * 5f);
                    var originY = 10f + (c * 7f);
                    c++;
                    var bits = cluster.OccupancyBits;
                    while (bits != 0)
                    {
                        var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                        bits &= bits - 1;
                        cluster.WriteSpatial(ClCohUnit.Pos, slot, PointAt(originX + (slot % 4), originY + (slot / 4)));
                    }
                }
            }
            finally
            {
                accessor.Dispose();
            }

            tx.Commit();
        }

        dbe.WriteTickFence(2);

        Assert.Multiple(() =>
        {
            Assert.That(MeanExtentFraction(dbe), Is.LessThanOrEqualTo(SpatialOptions.DefaultCellTreePromoteTightness));
            Assert.That(cs.PromotedCellCount, Is.GreaterThan(0), "the fence re-read the gate after the bounds tightened");
            Assert.That(cs.LastTickCellTreePromotions, Is.GreaterThan(0));
            Assert.That(ids, Is.Not.Empty);
        });
    }

    /// <summary>
    /// A cell promoted BY THE FENCE answers exactly what it answered on the linear scan the moment before. Promotion at
    /// cluster-add time is covered by <c>CellTreePromotionTests</c>; this is the fence-time route step 16 added, and it
    /// moves a populated cell onto a tree in one step rather than growing it there.
    /// </summary>
    /// <remarks>
    /// Found by the game-scenario harness, not by this fixture: a city partitioned into four very large cells promoted all
    /// four at 250 000 entities and its interest query then returned NOTHING, where the same population on a fine grid
    /// returned 1 083. <c>SQ-01</c> is "no false negatives", and a promoted cell that answers zero is the loudest possible
    /// violation of it — invisible here because the fixture asserted that promotion HAPPENED and never that the cell could
    /// still be queried afterwards.
    /// </remarks>
    [Test]
    [VerifiesRule("SQ-01")]
    public void ACellPromotedAtTheFenceAnswersWhatItAnsweredBefore()
    {
        ServiceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        using var scope = ServiceProvider.CreateScope();
        using var dbe = SetupEngine(scope);

        FillCell(dbe, PromoteAt + 4, spread: 0.90f);
        dbe.WriteTickFence(1);
        var cs = ClusterStateOf(dbe);
        Assert.That(cs.PromotedCellCount, Is.Zero, "precondition: born loose, so still on the linear scan");

        var before = QueryAll(dbe, cs);
        Assert.That(before, Is.Not.Empty, "precondition: the query has to find something before promotion to mean anything after it");

        // Pack the cell without adding a cluster to it — the fence-time promotion route.
        var slots = SlotsPerCluster(dbe);
        using (var tx = dbe.CreateQuickTransaction())
        {
            var accessor = tx.For<ClCohUnit>();
            try
            {
                var c = 0;
                foreach (var cluster in accessor.GetClusterEnumerator())
                {
                    var originX = 10f + (c * 5f);
                    var originY = 10f + (c * 7f);
                    c++;
                    var bits = cluster.OccupancyBits;
                    while (bits != 0)
                    {
                        var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                        bits &= bits - 1;
                        cluster.WriteSpatial(ClCohUnit.Pos, slot, PointAt(originX + (slot % 4), originY + (slot / 4)));
                    }
                }
            }
            finally
            {
                accessor.Dispose();
            }

            tx.Commit();
        }

        dbe.WriteTickFence(2);
        Assert.That(cs.PromotedCellCount, Is.GreaterThan(0), "precondition: the fence promoted the cell");

        var after = QueryAll(dbe, cs);
        Assert.That(after, Is.EquivalentTo(before), "the promoted cell answers a different set from the linear scan it replaced (SQ-01)");
    }

    /// <summary>Every entity the cell-cluster index reports for a box covering the whole cell.</summary>
    private static HashSet<long> QueryAll(DatabaseEngine dbe, ArchetypeClusterState cs)
    {
        var found = new HashSet<long>();
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        foreach (var r in cs.QueryAabb(dbe.SpatialGrid, 0f, 0f, float.NegativeInfinity, CellSize, CellSize, float.PositiveInfinity))
        {
            found.Add(r.EntityId);
        }

        return found;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // AC-16.2 — hysteresis
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A promoted cell whose clusters widen past twice the promote gate falls back to the linear scan, and a cell oscillating just around the PROMOTE gate
    /// does not rebuild itself every tick — the gap between promote and demote is what buys that.
    /// </summary>
    [Test]
    public void ThePromoteAndDemoteGatesLeaveAGapNoOscillationCanCross()
    {
        ServiceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        using var scope = ServiceProvider.CreateScope();
        using var dbe = SetupEngine(scope);

        FillCell(dbe, PromoteAt + 4, spread: 0.02f);
        dbe.WriteTickFence(1);
        var cs = ClusterStateOf(dbe);
        Assert.That(cs.PromotedCellCount, Is.GreaterThan(0), "precondition: promoted");

        // Twenty ticks oscillating by ±0.02 of the cell around the PROMOTE gate — every tick crosses 0.10, none reaches the 0.20 fall-back, so a cell
        // that is already promoted must stay promoted. The gap is the whole subject: with promote and demote at the same value this loop would rebuild
        // the cell every tick. A demotion re-arms promotion (see EvaluateCellTreeTightnessTransitions), so the round trip is reachable and `rebuilds`
        // is not bounded by construction.
        var rebuilds = 0;
        for (var tick = 0; tick < 20; tick++)
        {
            var spread = ((tick & 1) == 0 ? 0.08f : 0.12f) * CellSize;
            using (var tx = dbe.CreateQuickTransaction())
            {
                var accessor = tx.For<ClCohUnit>();
                try
                {
                    var c = 0;
                    foreach (var cluster in accessor.GetClusterEnumerator())
                    {
                        var originX = 10f + (c * 3f);
                        var originY = 10f + (c * 5f);
                        c++;
                        var bits = cluster.OccupancyBits;
                        var i = 0;
                        while (bits != 0)
                        {
                            var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                            bits &= bits - 1;
                            var t = i++ / (float)Math.Max(1, SlotsPerCluster(dbe) - 1);
                            cluster.WriteSpatial(ClCohUnit.Pos, slot, PointAt(originX + (t * spread), originY + (t * spread)));
                        }
                    }
                }
                finally
                {
                    accessor.Dispose();
                }

                tx.Commit();
            }

            cs.LastTickCellTreePromotions = 0;
            cs.LastTickCellTreeDemotions = 0;
            dbe.WriteTickFence(2 + tick);
            rebuilds += cs.LastTickCellTreePromotions + cs.LastTickCellTreeDemotions;
        }

        Assert.That(rebuilds, Is.LessThanOrEqualTo(1), $"an oscillation inside the gap rebuilt the cell {rebuilds} times in 20 ticks");
        Assert.That(cs.PromotedCellCount, Is.GreaterThan(0), "the cell fell off the tree without ever reaching the fall-back gate");
    }

    /// <summary>A promoted cell whose clusters are pulled apart past the fall-back gate returns to the linear scan, and still answers.</summary>
    [Test]
    public void ACellPulledPastTheFallBackGateDemotesAndKeepsAnswering()
    {
        ServiceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        using var scope = ServiceProvider.CreateScope();
        using var dbe = SetupEngine(scope);

        FillCell(dbe, PromoteAt + 4, spread: 0.02f);
        dbe.WriteTickFence(1);
        var cs = ClusterStateOf(dbe);
        Assert.That(cs.PromotedCellCount, Is.GreaterThan(0), "precondition: promoted");

        var expected = new HashSet<long>();
        using (var epoch = EpochGuard.Enter(dbe.EpochManager))
        {
            foreach (var r in cs.QueryAabb(dbe.SpatialGrid, 0f, 0f, float.NegativeInfinity, CellSize, CellSize, float.PositiveInfinity))
            {
                expected.Add(r.EntityId);
            }
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            var accessor = tx.For<ClCohUnit>();
            try
            {
                foreach (var cluster in accessor.GetClusterEnumerator())
                {
                    var bits = cluster.OccupancyBits;
                    var i = 0;
                    while (bits != 0)
                    {
                        var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                        bits &= bits - 1;
                        // Spread each cluster over half the cell — past the 0.20 fall-back with room to spare.
                        var t = i++ / (float)Math.Max(1, SlotsPerCluster(dbe) - 1);
                        cluster.WriteSpatial(ClCohUnit.Pos, slot, PointAt(10f + (t * 0.5f * CellSize), 10f + (t * 0.5f * CellSize)));
                    }
                }
            }
            finally
            {
                accessor.Dispose();
            }

            tx.Commit();
        }

        dbe.WriteTickFence(2);

        var after = new HashSet<long>();
        using (var epoch = EpochGuard.Enter(dbe.EpochManager))
        {
            foreach (var r in cs.QueryAabb(dbe.SpatialGrid, 0f, 0f, float.NegativeInfinity, CellSize, CellSize, float.PositiveInfinity))
            {
                after.Add(r.EntityId);
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(cs.LastTickCellTreeDemotions, Is.GreaterThan(0), "the cell was pulled past the fall-back gate");
            Assert.That(cs.PromotedCellCount, Is.Zero);
            Assert.That(after, Is.EquivalentTo(expected), "the fall-back lost or duplicated entities");
        });
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // AC-16.1 (measurement) — what the promotion buys, on the engine's own query path
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The same packed cell, queried through <c>QueryAabb</c>, promoted and not — the gate's whole justification, measured where the engine will collect
    /// it rather than on the synthetic sweep. Reports the ratio; asserts only that both arms return the same entities.
    /// </summary>
    /// <remarks>
    /// An instrument, not a gate: a timing threshold in the suite is a machine-specific guess that reddens on a busy box, which is the same reason
    /// <c>BroadphaseCrossoverSweepTests</c> is explicit. Run it in Release:
    /// <code>dotnet test -c Release --filter "FullyQualifiedName~MeasureThePromotedCellQueryWin"</code>
    /// </remarks>
    [Test]
    [Explicit("Measurement instrument for AC-16.1 — run deliberately in Release.")]
    [Category("Manual")]
    public void MeasureThePromotedCellQueryWin([Values(100, 400, 700)] int clusters)
    {
        var linear = MeasureQuery(clusters, promote: false, out var linearHits);
        var tree = MeasureQuery(clusters, promote: true, out var treeHits);

        TestContext.Out.WriteLine($"C={clusters}: linear {linear:F1} us, tree {tree:F1} us, speed-up {linear / tree:F2}x, hits {linearHits}");
        Assert.That(treeHits, Is.EqualTo(linearHits), "the two structures disagree on what the query found, so the timing means nothing");
    }

    private double MeasureQuery(int clusters, bool promote, out int hits)
    {
        ServiceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        using var scope = ServiceProvider.CreateScope();
        // Tightness 0 never promotes (no cell is tighter than nothing); the shipped 0.10 promotes this packed cell.
        using var dbe = SetupEngine(scope, tightness: promote ? SpatialOptions.DefaultCellTreePromoteTightness : 0f);

        FillCell(dbe, clusters, spread: 0.02f);
        dbe.WriteTickFence(1);

        var cs = ClusterStateOf(dbe);
        Assert.That(cs.PromotedCellCount, promote ? Is.GreaterThan(0) : Is.Zero, "the arm did not get the structure it was asked for");

        // A medium query: a tenth of the cell on each axis, which is the selectivity §5.8.4 reports as the boundary.
        const float QMin = 200f;
        const float QMax = 300f;
        var found = 0;
        using (var epoch = EpochGuard.Enter(dbe.EpochManager))
        {
            foreach (var r in cs.QueryAabb(dbe.SpatialGrid, QMin, QMin, float.NegativeInfinity, QMax, QMax, float.PositiveInfinity))
            {
                found += r.EntityId == 0 ? 0 : 1;
            }
        }

        hits = found;

        // Warm by WALL TIME, not by iteration count: tiered compilation promotes on a background thread after ~30 calls plus a delay, and a 50-iteration
        // warm-up over a cheap query returns before that lands — which read as a 17x "win" for a 100-cluster cell whose linear arm was timing tier-0 code.
        var sink = 0;
        _stopwatch.Restart();
        var warmed = 0;
        while (_stopwatch.ElapsedMilliseconds < 300)
        {
            sink += RunQuery(dbe, cs, QMin, QMax);
            warmed++;
        }

        var iterations = Math.Max(200, warmed);
        _stopwatch.Restart();
        for (var i = 0; i < iterations; i++)
        {
            sink += RunQuery(dbe, cs, QMin, QMax);
        }

        _stopwatch.Stop();
        if (sink < 0)
        {
            throw new InvalidOperationException();   // keeps the loop from being optimised away
        }

        return _stopwatch.Elapsed.TotalMilliseconds * 1000d / iterations;
    }

    private static int RunQuery(DatabaseEngine dbe, ArchetypeClusterState cs, float qMin, float qMax)
    {
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var found = 0;
        foreach (var r in cs.QueryAabb(dbe.SpatialGrid, qMin, qMin, float.NegativeInfinity, qMax, qMax, float.PositiveInfinity))
        {
            found += r.EntityId == 0 ? 0 : 1;
        }

        return found;
    }

    private readonly System.Diagnostics.Stopwatch _stopwatch = new();

    /// <summary>Tightness <c>1</c> restores count-only promotion, which is what every fixture testing the tree itself relies on.</summary>
    [Test]
    public void TightnessOneRestoresCountOnlyPromotion()
    {
        ServiceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        using var scope = ServiceProvider.CreateScope();
        using var dbe = SetupEngine(scope, tightness: 1f);

        FillCell(dbe, PromoteAt + 4, spread: 0.90f);
        dbe.WriteTickFence(1);

        Assert.That(ClusterStateOf(dbe).PromotedCellCount, Is.GreaterThan(0), "with the tightness gate off the count alone must still promote");
    }
}
