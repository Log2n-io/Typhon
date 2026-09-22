using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// <c>AC-9.6</c> — cluster-level frustum queries against a brute-force oracle, on both the linear and the promoted path.
/// </summary>
/// <remarks>
/// <para>What is new here is not the plane test — that is <see cref="SpatialGeometry.ClassifyAABBAgainstPlanes"/>, shared with the tree's own traversal — but
/// the cell walk and the <c>C15</c> frame shift, <c>d' = d + dot(n, origin)</c>, applied once per cell. A sign error in that shift is invisible on a grid whose
/// cells all start at the origin, so the fixtures below deliberately use a world of several cells with the geometry away from the corner.</para>
/// <para><b>The oracle evaluates the half-spaces directly on each entity's four corners.</b> Calling the production classifier would make the test agree with
/// itself about a wrong shift; testing corners is the definition rather than an implementation of it.</para>
/// </remarks>
[TestFixture]
class ClusterFrustumTests : TestBase<ClusterFrustumTests>
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

    /// <summary>
    /// An axis-aligned window as four half-spaces, in the engine's convention: inside is <c>dot(n, p) + d &gt;= 0</c>.
    /// </summary>
    private static double[] WindowPlanes(float minX, float minY, float maxX, float maxY) =>
    [
        1d, 0d, -minX,    // x >= minX
        -1d, 0d, maxX,    // x <= maxX
        0d, 1d, -minY,    // y >= minY
        0d, -1d, maxY,    // y <= maxY
    ];

    /// <summary>True when the box is NOT fully outside any single plane — the same acceptance the query promises.</summary>
    private static bool OracleAccepts(double[] planes, int planeCount, float minX, float minY, float maxX, float maxY)
    {
        for (int p = 0; p < planeCount; p++)
        {
            double nx = planes[(p * 3) + 0];
            double ny = planes[(p * 3) + 1];
            double d = planes[(p * 3) + 2];

            // The corner furthest along the normal. If even that one is behind the plane, the whole box is.
            double best = (nx * (nx >= 0 ? maxX : minX)) + (ny * (ny >= 0 ? maxY : minY)) + d;
            if (best < 0)
            {
                return false;
            }
        }
        return true;
    }

    private (HashSet<long> hits, HashSet<long> oracle, int promotedCells) Run(
        int promoteThreshold, float cellSize, int entityCount, float wMinX, float wMinY, float wMaxX, float wMaxY, int seed)
    {
        ServiceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        using var scope = ServiceProvider.CreateScope();
        using var dbe = Setup(scope, promoteThreshold, cellSize);

        var rng = new Random(seed);
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < entityCount; i++)
            {
                tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(BoxAt(
                    (float)rng.NextDouble() * WorldExtent,
                    (float)rng.NextDouble() * WorldExtent,
                    1f + ((float)rng.NextDouble() * 6f))));
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        var cs = ClusterStateOf(dbe);
        var planes = WindowPlanes(wMinX, wMinY, wMaxX, wMaxY);

        var buffer = new long[4096];
        int n;
        using (var epoch = EpochGuard.Enter(dbe.EpochManager))
        {
            n = cs.QueryFrustum(dbe.SpatialGrid, planes, 4,
                new Vector3Like(wMinX, wMinY, 0f), new Vector3Like(wMaxX, wMaxY, 0f), buffer, categoryMask: 0);
        }

        var hits = new HashSet<long>();
        for (int i = 0; i < n; i++)
        {
            hits.Add(buffer[i]);
        }

        var oracle = new HashSet<long>();
        using (var epoch = EpochGuard.Enter(dbe.EpochManager))
        {
            foreach (var r in cs.QueryAabb(dbe.SpatialGrid, 0f, 0f, float.NegativeInfinity, WorldExtent, WorldExtent, float.PositiveInfinity))
            {
                if (OracleAccepts(planes, 4, (float)r.MinX, (float)r.MinY, (float)r.MaxX, (float)r.MaxY))
                {
                    oracle.Add(r.EntityId);
                }
            }
        }

        return (hits, oracle, cs.PromotedCellCount);
    }

    [Test]
    [CancelAfter(60_000)]
    public void Frustum_MatchesBruteForce_OnTheLinearPath()
    {
        var r = Run(int.MaxValue, cellSize: 200f, entityCount: 4_000, wMinX: 617f, wMinY: 823f, wMaxX: 1_180f, wMaxY: 1_402f, seed: 2468);
        TestContext.Out.WriteLine($"FRUSTUM linear   hits={r.hits.Count} oracle={r.oracle.Count} promotedCells={r.promotedCells}");

        Assert.Multiple(() =>
        {
            Assert.That(r.promotedCells, Is.Zero, "this configuration must not promote, or it is not testing the linear path");
            Assert.That(r.oracle, Is.Not.Empty, "the window must contain something, or the comparison is between two empty sets");
            Assert.That(r.hits, Is.EquivalentTo(r.oracle), "the frustum query and the brute-force scan disagree");
        });
    }

    [Test]
    [CancelAfter(60_000)]
    public void Frustum_MatchesBruteForce_OnPromotedCells()
    {
        var r = Run(promoteThreshold: 4, cellSize: 1_000f, entityCount: 4_000, wMinX: 617f, wMinY: 823f, wMaxX: 1_180f, wMaxY: 1_402f, seed: 2468);
        TestContext.Out.WriteLine($"FRUSTUM promoted hits={r.hits.Count} oracle={r.oracle.Count} promotedCells={r.promotedCells}");

        Assert.Multiple(() =>
        {
            Assert.That(r.promotedCells, Is.GreaterThan(0), "the population must cross the threshold, or the tree's frustum traversal never ran");
            Assert.That(r.oracle, Is.Not.Empty, "the window must contain something");
            Assert.That(r.hits, Is.EquivalentTo(r.oracle), "the frustum query and the brute-force scan disagree on a promoted cell");
        });
    }

    /// <summary>
    /// A window entirely inside one cell far from the origin — where a frame-shift sign error hides.
    /// </summary>
    /// <remarks>
    /// With <c>d' = d + dot(n, origin)</c> the sign only matters once the origin is non-zero, so a window in cell (0,0) would pass whichever way round the
    /// shift went. This one sits in the far cell of a 2x2 grid, so getting it backwards moves the window by twice the cell size and returns nothing.
    /// </remarks>
    [Test]
    [CancelAfter(60_000)]
    public void Frustum_IsCorrectInACellFarFromTheOrigin()
    {
        var r = Run(promoteThreshold: 4, cellSize: 1_000f, entityCount: 4_000, wMinX: 1_400f, wMinY: 1_500f, wMaxX: 1_700f, wMaxY: 1_850f, seed: 13579);
        TestContext.Out.WriteLine($"FRUSTUM far cell hits={r.hits.Count} oracle={r.oracle.Count} promotedCells={r.promotedCells}");

        Assert.Multiple(() =>
        {
            Assert.That(r.oracle, Is.Not.Empty, "the far window must contain something, or a shift error would pass unnoticed");
            Assert.That(r.hits, Is.EquivalentTo(r.oracle), "the cell-frame plane shift is wrong for a cell away from the world origin");
        });
    }

    /// <summary>A window covering the whole world must return every entity — the control that proves nothing is being over-rejected.</summary>
    [Test]
    [CancelAfter(60_000)]
    public void Frustum_CoveringTheWorldReturnsEverything()
    {
        var r = Run(promoteThreshold: 4, cellSize: 1_000f, entityCount: 500, wMinX: -10f, wMinY: -10f, wMaxX: WorldExtent + 10f, wMaxY: WorldExtent + 10f,
            seed: 8642);
        TestContext.Out.WriteLine($"FRUSTUM whole world hits={r.hits.Count} oracle={r.oracle.Count}");

        Assert.Multiple(() =>
        {
            Assert.That(r.oracle, Has.Count.EqualTo(500), "the oracle must accept the whole population for a world-covering window");
            Assert.That(r.hits, Is.EquivalentTo(r.oracle), "a window covering the world rejected entities");
        });
    }
}
