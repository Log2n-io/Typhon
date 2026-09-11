using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// <c>AC-9.7</c> — cluster-level kNN, against a brute-force oracle, in both the linear and the promoted configuration.
/// </summary>
/// <remarks>
/// <para>The design singles kNN out as the one operation that must be REIMPLEMENTED rather than ported, because a cluster's distance is a lower bound rather
/// than a distance. The implementation is best-first over clusters with early termination, so the two things worth testing are that it returns the right
/// answer and that it does so without opening everything.</para>
/// <para><b>The oracle is a full scan, and it has to be.</b> Comparing kNN against another pruning implementation would share the pruning assumption, and a
/// wrong bound would agree with itself. Every entity is measured directly.</para>
/// </remarks>
[TestFixture]
class ClusterKnnTests : TestBase<ClusterKnnTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    /// <summary>Cell size for the linear-path tests: 10x10 cells over the world, so each holds about one cluster and nothing promotes.</summary>
    private const float SparseCellSize = 200f;

    /// <summary>
    /// Cell size for the promoted-path tests: 2x2 cells, so a few thousand entities put ~16 clusters in each and a low threshold actually fires.
    /// </summary>
    /// <remarks>
    /// Worth stating because the first version of this fixture used one cell size for both and the promoted test silently ran the LINEAR path — the
    /// population never crossed the threshold, and only the non-vacuity assertion on PromotedCellCount caught it.
    /// </remarks>
    private const float DenseCellSize = 1_000f;

    private const float WorldExtent = 2_000f;

    private static ClCohPos PointAt(float x, float y) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Mass = 1.0f };

    private DatabaseEngine Setup(IServiceScope scope, int promoteThreshold, float cellSize)
    {
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClCohPos>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(WorldExtent, WorldExtent), cellSize));
        dbe.ClusterCellTreePromoteThreshold = promoteThreshold;
        // Step 16: these fixtures exercise the TREE, not the gate that decides when a cell gets one — their clusters are scattered over the cell
        // on purpose, which is exactly the shape the tightness gate refuses. Count-only promotion keeps them testing what they were written for.
        dbe.ClusterCellTreePromoteTightness = 1f;
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static ArchetypeClusterState ClusterStateOf(DatabaseEngine dbe) =>
        dbe._archetypeStates[Archetype<ClCohUnit>.Metadata.ArchetypeId].ClusterState;

    private static ClCohPos BoxAt(float x, float y, float half) =>
        new() { Bounds = new AABB2F { MinX = x - half, MinY = y - half, MaxX = x + half, MaxY = y + half }, Mass = 1.0f };

    /// <summary>
    /// The nearest entity may be one whose CENTRE is in a further cell, because a box reaches outside the cell it is filed under. The ring search must not
    /// stop before it has covered that overhang.
    /// </summary>
    /// <remarks>
    /// <para><b>The geometry, chosen so the failure is arithmetic rather than luck.</b> 100-unit cells; the query sits at (150, 150), in cell (1, 1), whose
    /// faces are 50 units away on every side. <c>A</c> is a point at (190, 150) — same cell, 40 units away, so <c>40² = 1600</c>. <c>B</c> is a 50-wide box
    /// centred at (205, 150): its centre is in cell (2, 1), so it is filed one shell out, but its near edge is at x = 180 and it is only <b>30</b> units away,
    /// <c>30² = 900</c>. B is the true nearest neighbour and it is not in the first shell.</para>
    /// <para><b>What the bug did.</b> After ring 0 the search held A, and asked whether the region it had covered reached as far as A. Measuring to the cell
    /// FACE it answered 50, and <c>50² = 2500 ≥ 1600</c>, so it stopped one shell too early and returned A. The fix subtracts
    /// <c>ArchetypeClusterState.MaxClusterOverhang</c> — 20 here, since B's box starts 20 units inside cell (2, 1)'s lower face — giving
    /// <c>30² = 900 &lt; 1600</c>, which does not satisfy the stopping rule, so ring 1 is collected and B is found.</para>
    /// <para><b>Why the rest of this fixture cannot catch it.</b> Every other test spawns <see cref="PointAt"/> — zero-extent entities, whose overhang is
    /// zero, which is the one shape where measuring to the cell face happens to be right. A differential against a brute-force oracle is not enough on its
    /// own if the population never exercises the term that is wrong.</para>
    /// </remarks>
    [Test]
    [CancelAfter(60_000)]
    public void Knn_FindsANearerEntityWhoseCentreIsInAFurtherCell()
    {
        ServiceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClCohPos>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(1_000f, 1_000f), cellSize: 100f));
        dbe.InitializeArchetypes();

        EntityId near;
        EntityId far;
        using (var tx = dbe.CreateQuickTransaction())
        {
            far = tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(190f, 150f)));          // same cell, 40 away
            near = tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(BoxAt(205f, 150f, 25f)));      // next cell, edge 30 away
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        var cs = ClusterStateOf(dbe);

        // Non-vacuity: the two entities must land in DIFFERENT cells, or the overhang never matters and this test degenerates into "kNN works".
        int nearCell = dbe.SpatialGrid.WorldToCellKey(205f, 150f, 0f);
        int farCell = dbe.SpatialGrid.WorldToCellKey(190f, 150f, 0f);
        Assert.That(nearCell, Is.Not.EqualTo(farCell), "the nearer entity must be filed one cell out, or the ring search never has to reach for it");
        Assert.That(cs.MaxClusterOverhang, Is.GreaterThan(0f), "the overhang bound must have been observed, or the corrected stopping rule is a no-op");

        var buffer = new (long entityId, double distSq)[1];
        int n;
        using (var epoch = EpochGuard.Enter(dbe.EpochManager))
        {
            n = cs.QueryNearest(dbe.SpatialGrid, 150f, 150f, 0f, k: 1, buffer, categoryMask: 0);
        }

        TestContext.Out.WriteLine($"KNN overhang: n={n} distSq={(n > 0 ? buffer[0].distSq : -1f)} overhang={cs.MaxClusterOverhang}");

        Assert.Multiple(() =>
        {
            Assert.That(n, Is.EqualTo(1));
            Assert.That(buffer[0].distSq, Is.EqualTo(900f).Within(1f), "the box 30 units away is nearer than the point 40 units away");
        });
        _ = near;
        _ = far;
    }

    /// <summary>Brute force: every entity in the world, measured directly, sorted, truncated to k.</summary>
    private static List<(long entityId, double distSq)> Oracle(DatabaseEngine dbe, ArchetypeClusterState cs, float px, float py, int k)
    {
        var all = new List<(long entityId, double distSq)>();
        using (var epoch = EpochGuard.Enter(dbe.EpochManager))
        {
            foreach (var r in cs.QueryAabb(dbe.SpatialGrid, 0f, 0f, float.NegativeInfinity, WorldExtent, WorldExtent, float.PositiveInfinity))
            {
                // Double, matching the engine since #919 F2. It matters that this MIRRORS the production arithmetic rather than approximating it: the
                // oracle's job is to be an independent implementation of the same SEMANTICS, and a narrower one disagrees by more than the tolerance at
                // 4 000 entities — which reads as a kNN bug and is not one.
                double dx = Math.Max(Math.Max(r.MinX - px, 0d), px - r.MaxX);
                double dy = Math.Max(Math.Max(r.MinY - py, 0d), py - r.MaxY);
                all.Add((r.EntityId, (dx * dx) + (dy * dy)));
            }
        }

        all.Sort((a, b) => a.distSq.CompareTo(b.distSq));
        if (all.Count > k)
        {
            all.RemoveRange(k, all.Count - k);
        }
        return all;
    }

    private (List<(long entityId, double distSq)> knn, List<(long entityId, double distSq)> oracle, int promotedCells) Run(
        int promoteThreshold, int entityCount, float px, float py, int k, int seed, float cellSize)
    {
        // A fresh database per configuration — two engines under one fixture name otherwise share a file, and the second loads the first's population.
        ServiceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();

        using var scope = ServiceProvider.CreateScope();
        using var dbe = Setup(scope, promoteThreshold, cellSize);

        var rng = new Random(seed);
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < entityCount; i++)
            {
                tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt((float)rng.NextDouble() * WorldExtent, (float)rng.NextDouble() * WorldExtent)));
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        var cs = ClusterStateOf(dbe);
        var buffer = new (long entityId, double distSq)[k];
        int n;
        using (var epoch = EpochGuard.Enter(dbe.EpochManager))
        {
            n = cs.QueryNearest(dbe.SpatialGrid, px, py, 0f, k, buffer, categoryMask: 0);
        }

        var knn = new List<(long entityId, double distSq)>();
        for (int i = 0; i < n; i++)
        {
            knn.Add(buffer[i]);
        }

        // The oracle enumerates through QueryAabb — the SAME production path, in the same configuration. That is independent enough for the ray/kNN logic
        // itself, but NOT for the promotion layer underneath: an entity lost from a promoted tree vanishes from both sides and the comparison passes. Pinning
        // the total against the known spawn count is the check that does not share that blind spot.
        int reachable = 0;
        using (var epoch = EpochGuard.Enter(dbe.EpochManager))
        {
            foreach (var _ in cs.QueryAabb(dbe.SpatialGrid, 0f, 0f, float.NegativeInfinity, WorldExtent, WorldExtent, float.PositiveInfinity))
            {
                reachable++;
            }
        }
        Assert.That(reachable, Is.EqualTo(entityCount), "the index lost entities — every comparison below would share that blind spot");

        return (knn, Oracle(dbe, cs, px, py, k), cs.PromotedCellCount);
    }

    private static void AssertMatchesOracle(List<(long entityId, double distSq)> knn, List<(long entityId, double distSq)> oracle, string stage)
    {
        Assert.That(knn, Has.Count.EqualTo(oracle.Count), $"{stage}: wrong number of neighbours returned");

        // Distances first: they are what kNN promises, and they are unambiguous even when two entities tie. Comparing them before the ids means a tie reports
        // as "same distances, different id at position n" rather than as a bare set mismatch nobody can act on.
        for (int i = 0; i < knn.Count; i++)
        {
            Assert.That(knn[i].distSq, Is.EqualTo(oracle[i].distSq).Within(1e-4f),
                $"{stage}: neighbour {i} is at distSq {knn[i].distSq} but the nearest available is {oracle[i].distSq}");
        }

        // Distinct ids: distances alone would accept the same entity returned twice at a tied distance, which is the shape a heap bug produces.
        var ids = new HashSet<long>();
        foreach (var h in knn)
        {
            ids.Add(h.entityId);
        }
        Assert.That(ids, Has.Count.EqualTo(knn.Count), $"{stage}: the same entity was returned more than once");

        // Ascending order is part of the contract.
        for (int i = 1; i < knn.Count; i++)
        {
            Assert.That(knn[i].distSq, Is.GreaterThanOrEqualTo(knn[i - 1].distSq), $"{stage}: results are not in ascending distance order");
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public void Knn_MatchesBruteForce_OnTheLinearPath()
    {
        var r = Run(promoteThreshold: int.MaxValue, entityCount: 4_000, px: 913f, py: 471f, k: 20, seed: 12345, cellSize: SparseCellSize);
        TestContext.Out.WriteLine($"KNN linear   returned={r.knn.Count} nearest={Math.Sqrt(r.knn[0].distSq):F3} promotedCells={r.promotedCells}");

        Assert.That(r.promotedCells, Is.Zero, "this configuration must not promote, or it is not testing the linear path");
        AssertMatchesOracle(r.knn, r.oracle, "linear");
    }

    [Test]
    [CancelAfter(60_000)]
    public void Knn_MatchesBruteForce_OnPromotedCells()
    {
        var r = Run(promoteThreshold: 4, entityCount: 4_000, px: 913f, py: 471f, k: 20, seed: 12345, cellSize: DenseCellSize);
        TestContext.Out.WriteLine($"KNN promoted returned={r.knn.Count} nearest={Math.Sqrt(r.knn[0].distSq):F3} promotedCells={r.promotedCells}");

        Assert.That(r.promotedCells, Is.GreaterThan(0), "the population must cross the threshold, or the tree path never ran");
        AssertMatchesOracle(r.knn, r.oracle, "promoted");
    }

    /// <summary>
    /// The query point far outside the populated region, and k larger than the population — the two shapes where a ring search is easiest to get wrong.
    /// </summary>
    /// <remarks>
    /// A point outside every occupied cell means ring 0 finds nothing, so the loop must keep expanding rather than concluding the world is empty. And k above
    /// the population must return everything rather than spinning until the ring exceeds the grid — the termination test never fires when the result heap
    /// cannot fill.
    /// </remarks>
    [Test]
    [CancelAfter(60_000)]
    public void Knn_HandlesAFarPointAndAnOversizedK()
    {
        var far = Run(promoteThreshold: 4, entityCount: 500, px: WorldExtent - 1f, py: 1f, k: 10, seed: 777, cellSize: DenseCellSize);
        TestContext.Out.WriteLine($"KNN far      returned={far.knn.Count} nearest={Math.Sqrt(far.knn[0].distSq):F3}");
        AssertMatchesOracle(far.knn, far.oracle, "far point");

        var oversized = Run(promoteThreshold: 4, entityCount: 30, px: 1_000f, py: 1_000f, k: 100, seed: 99, cellSize: DenseCellSize);
        TestContext.Out.WriteLine($"KNN k>n      returned={oversized.knn.Count} of 30 entities");
        Assert.That(oversized.knn, Has.Count.EqualTo(30), "k above the population must return the whole population, not k entries and not zero");
        AssertMatchesOracle(oversized.knn, oversized.oracle, "k > n");
    }

    /// <summary>
    /// The point of the priority queue: a small k must not open every cluster in the world.
    /// </summary>
    /// <remarks>
    /// Measured by cluster opens rather than by time — a timing threshold would be a machine-specific guess, while "did it look at everything" is exactly the
    /// property the lower-bound formulation buys and is invariant across machines. The previous implementation scanned every entity in the world for any k,
    /// so this is the assertion that would have caught it.
    /// </remarks>
    [Test]
    [CancelAfter(60_000)]
    public void Knn_DoesNotOpenEveryCluster()
    {
        ServiceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        using var scope = ServiceProvider.CreateScope();
        using var dbe = Setup(scope, int.MaxValue, SparseCellSize);

        var rng = new Random(4242);
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 6_000; i++)
            {
                tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt((float)rng.NextDouble() * WorldExtent, (float)rng.NextDouble() * WorldExtent)));
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        var cs = ClusterStateOf(dbe);
        int totalClusters = cs.ActiveClusterCount;

        int scanned;
        var buffer = new (long entityId, double distSq)[5];
        using (var epoch = EpochGuard.Enter(dbe.EpochManager))
        {
            cs.QueryNearest(dbe.SpatialGrid, 1_000f, 1_000f, 0f, 5, buffer, out scanned, categoryMask: 0);
        }

        TestContext.Out.WriteLine($"KNN opened {scanned} of {totalClusters} clusters for k=5 over 6000 entities");

        Assert.Multiple(() =>
        {
            Assert.That(scanned, Is.GreaterThan(0), "it must have opened something, or the counter is not wired");
            Assert.That(scanned, Is.LessThan(totalClusters / 4),
                $"kNN opened {scanned} of {totalClusters} clusters for k=5 — the lower-bound bound is not pruning, which is the whole point of the "
                + "priority-queue formulation over the radius-expansion loop it replaced");
        });
    }
}
