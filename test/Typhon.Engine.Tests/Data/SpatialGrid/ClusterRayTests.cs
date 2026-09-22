using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// <c>AC-9.6</c> — cluster-level ray queries against a brute-force oracle, on both the linear and the promoted path.
/// </summary>
/// <remarks>
/// <para>The tree's own ray traversal is reused rather than reimplemented (§4.1 lists it under "reuse as-is"), so what these tests actually exercise is the
/// part that is new: the cell walk, the <c>C15</c> frame conversion into each cell, and the merge of per-cell results into one front-to-back order.</para>
/// <para><b>The oracle tests every entity with an independent slab implementation.</b> Reusing the production predicate would make the test agree with itself
/// about a wrong intersection; the oracle here computes entry and exit parameters the long way and checks the interval overlaps the segment.</para>
/// </remarks>
[TestFixture]
class ClusterRayTests : TestBase<ClusterRayTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    private const float WorldExtent = 2_000f;

    private static ClCohPos BoxAt(float x, float y, float half) =>
        new() { Bounds = new AABB2F { MinX = x - half, MinY = y - half, MaxX = x + half, MaxY = y + half }, Mass = 1.0f };

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

    /// <summary>Independent slab test, written the long way so it shares no code with the implementation it checks.</summary>
    private static bool OracleRayHit(float ox, float oy, float dx, float dy, float maxDist, float minX, float minY, float maxX, float maxY, out float t)
    {
        float tMin = 0f;
        float tMax = maxDist;
        t = 0f;

        for (int axis = 0; axis < 2; axis++)
        {
            float o = axis == 0 ? ox : oy;
            float d = axis == 0 ? dx : dy;
            float lo = axis == 0 ? minX : minY;
            float hi = axis == 0 ? maxX : maxY;

            if (Math.Abs(d) < 1e-12f)
            {
                if (o < lo || o > hi)
                {
                    return false;
                }
                continue;
            }

            float t1 = (lo - o) / d;
            float t2 = (hi - o) / d;
            if (t1 > t2)
            {
                (t1, t2) = (t2, t1);
            }
            tMin = Math.Max(tMin, t1);
            tMax = Math.Min(tMax, t2);
            if (tMin > tMax)
            {
                return false;
            }
        }

        t = tMin;
        return true;
    }

    private (List<(long id, float t)> hits, List<(long id, float t)> oracle, int promotedCells) Run(
        int promoteThreshold, float cellSize, int entityCount, float ox, float oy, float dx, float dy, float maxDist, int seed)
    {
        ServiceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        using var scope = ServiceProvider.CreateScope();
        using var dbe = Setup(scope, promoteThreshold, cellSize);

        var rng = new Random(seed);
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < entityCount; i++)
            {
                // Extents rather than points: a zero-size box is the degenerate case for a slab test, and a ray hitting one exactly is a coin flip in f32.
                tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(BoxAt(
                    (float)rng.NextDouble() * WorldExtent,
                    (float)rng.NextDouble() * WorldExtent,
                    1f + ((float)rng.NextDouble() * 6f))));
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        var cs = ClusterStateOf(dbe);

        var buffer = new (long entityId, double distance)[512];
        int n;
        using (var epoch = EpochGuard.Enter(dbe.EpochManager))
        {
            n = cs.QueryRay(dbe.SpatialGrid, ox, oy, 0f, dx, dy, 0f, maxDist, buffer, categoryMask: 0);
        }

        var hits = new List<(long id, float t)>();
        for (int i = 0; i < n; i++)
        {
            hits.Add((buffer[i].entityId, (float)buffer[i].distance));
        }

        // Oracle: every entity in the world, tested directly.
        float len = MathF.Sqrt((dx * dx) + (dy * dy));
        float ndx = dx / len;
        float ndy = dy / len;
        var oracle = new List<(long id, float t)>();
        using (var epoch = EpochGuard.Enter(dbe.EpochManager))
        {
            foreach (var r in cs.QueryAabb(dbe.SpatialGrid, 0f, 0f, float.NegativeInfinity, WorldExtent, WorldExtent, float.PositiveInfinity))
            {
                if (OracleRayHit(ox, oy, ndx, ndy, maxDist, (float)r.MinX, (float)r.MinY, (float)r.MaxX, (float)r.MaxY, out float t))
                {
                    oracle.Add((r.EntityId, t));
                }
            }
        }
        oracle.Sort((a, b) => a.t.CompareTo(b.t));

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

return (hits, oracle, cs.PromotedCellCount);
    }

    private static void AssertMatches(List<(long id, float t)> hits, List<(long id, float t)> oracle, string stage)
    {
        var hitIds = new HashSet<long>();
        foreach (var h in hits)
        {
            hitIds.Add(h.id);
        }
        var oracleIds = new HashSet<long>();
        foreach (var o in oracle)
        {
            oracleIds.Add(o.id);
        }

        Assert.Multiple(() =>
        {
            Assert.That(oracleIds, Is.Not.Empty, $"{stage}: the ray must hit something, or the comparison is between two empty sets");
            Assert.That(hitIds, Is.EquivalentTo(oracleIds), $"{stage}: the ray query and the brute-force scan disagree on which entities the ray enters");

            // Front-to-back is part of the contract.
            for (int i = 1; i < hits.Count; i++)
            {
                Assert.That(hits[i].t, Is.GreaterThanOrEqualTo(hits[i - 1].t), $"{stage}: results are not in front-to-back order");
            }
        });
    }

    [Test]
    [CancelAfter(60_000)]
    public void Ray_MatchesBruteForce_OnTheLinearPath()
    {
        var r = Run(int.MaxValue, cellSize: 200f, entityCount: 4_000, ox: 10f, oy: 20f, dx: 1f, dy: 0.7f, maxDist: 3_000f, seed: 4242);
        TestContext.Out.WriteLine($"RAY linear   hits={r.hits.Count} oracle={r.oracle.Count} promotedCells={r.promotedCells}");

        Assert.That(r.promotedCells, Is.Zero, "this configuration must not promote, or it is not testing the linear path");
        AssertMatches(r.hits, r.oracle, "linear");
    }

    [Test]
    [CancelAfter(60_000)]
    public void Ray_MatchesBruteForce_OnPromotedCells()
    {
        var r = Run(promoteThreshold: 4, cellSize: 1_000f, entityCount: 4_000, ox: 10f, oy: 20f, dx: 1f, dy: 0.7f, maxDist: 3_000f, seed: 4242);
        TestContext.Out.WriteLine($"RAY promoted hits={r.hits.Count} oracle={r.oracle.Count} promotedCells={r.promotedCells}");

        Assert.That(r.promotedCells, Is.GreaterThan(0), "the population must cross the threshold, or the tree's ray traversal never ran");
        AssertMatches(r.hits, r.oracle, "promoted");
    }

    /// <summary>Axis-aligned rays, where one slab has a zero direction component — the case the slab test special-cases.</summary>
    [Test]
    [CancelAfter(60_000)]
    public void Ray_HandlesAxisAlignedDirections()
    {
        var horizontal = Run(promoteThreshold: 4, cellSize: 1_000f, entityCount: 2_000, ox: 0f, oy: 733f, dx: 1f, dy: 0f, maxDist: 3_000f, seed: 31337);
        TestContext.Out.WriteLine($"RAY horizontal hits={horizontal.hits.Count} oracle={horizontal.oracle.Count}");

        // The same non-vacuity guard the two differential tests carry: a silent fallback to the linear path would otherwise make this pass while testing
        // nothing about the tree.
        Assert.That(horizontal.promotedCells, Is.GreaterThan(0), "the population must cross the threshold, or the axis-aligned cases never reach the tree");
        AssertMatches(horizontal.hits, horizontal.oracle, "horizontal");

        var vertical = Run(promoteThreshold: 4, cellSize: 1_000f, entityCount: 2_000, ox: 917f, oy: 0f, dx: 0f, dy: 1f, maxDist: 3_000f, seed: 31337);
        TestContext.Out.WriteLine($"RAY vertical   hits={vertical.hits.Count} oracle={vertical.oracle.Count}");
        AssertMatches(vertical.hits, vertical.oracle, "vertical");
    }

    /// <summary>A short ray must stop at its own length rather than running to the world edge.</summary>
    [Test]
    [CancelAfter(60_000)]
    public void Ray_RespectsMaxDistance()
    {
        var shortRay = Run(promoteThreshold: 4, cellSize: 1_000f, entityCount: 2_000, ox: 10f, oy: 20f, dx: 1f, dy: 0.7f, maxDist: 200f, seed: 4242);
        var longRay = Run(promoteThreshold: 4, cellSize: 1_000f, entityCount: 2_000, ox: 10f, oy: 20f, dx: 1f, dy: 0.7f, maxDist: 3_000f, seed: 4242);

        TestContext.Out.WriteLine($"RAY maxDist short={shortRay.hits.Count} long={longRay.hits.Count}");

        AssertMatches(shortRay.hits, shortRay.oracle, "short ray");
        Assert.That(shortRay.hits, Has.Count.LessThan(longRay.hits.Count), "a 200-unit ray must hit fewer entities than a 3000-unit one along the same line");
        foreach (var h in shortRay.hits)
        {
            Assert.That(h.t, Is.LessThanOrEqualTo(200f), "a hit beyond maxDistance was returned");
        }
    }
}
