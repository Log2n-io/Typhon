using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// Tree-level query tests for all spatial query types. Compares R-Tree against BruteForceSpatialIndex oracle.
/// </summary>
[TestFixture]
public class SpatialQueryTests
{
    private IServiceProvider _serviceProvider;
    private string _testDatabaseDir;

    private static string CurrentDatabaseName
    {
        get
        {
            var testName = TestContext.CurrentContext.Test.Name;
            foreach (var c in new[] { '(', ')', ',', '"', '.', '<', '>', '+', ' ' })
            {
                testName = testName.Replace(c, '_');
            }
            if (testName.Length > 30)
            {
                testName = testName[^30..];
            }
            return $"SQT{testName}";
        }
    }

    [SetUp]
    public void Setup()
    {
        _testDatabaseDir = Path.Combine(Path.GetTempPath(), "Typhon.Tests", "SpatialQueryTests");
        Directory.CreateDirectory(_testDatabaseDir);

        var sc = new ServiceCollection();
        sc.AddLogging(b =>
            {
                b.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "mm:ss.fff "; });
                b.SetMinimumLevel(LogLevel.Warning);
            })
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddScopedManagedPagedMemoryMappedFile(o =>
            {
                o.DatabaseName = CurrentDatabaseName;
                o.DatabaseDirectory = _testDatabaseDir;
                o.DatabaseCacheSize = (ulong)(PagedMMF.MinimumMemPageCount * PagedMMF.PageSize);
                o.TestMode = true;
            });
        _serviceProvider = sc.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
    }

    [TearDown]
    public void TearDown()
    {
        (_serviceProvider as IDisposable)?.Dispose();
        if (_testDatabaseDir != null)
        {
            try
            {
                foreach (var file in Directory.GetFiles(_testDatabaseDir))
                {
                    File.Delete(file);
                }
            }
            catch { /* ignore cleanup errors */ }
        }
    }

    private (SpatialRTree<PersistentStore> tree, BruteForceSpatialIndex oracle, ChunkBasedSegment<PersistentStore> segment)
        CreateTreeAndOracle(ManagedPagedMMF pmmf, int entityCount, SpatialVariant variant = SpatialVariant.R2Df32, int seed = 42)
    {
        var desc = SpatialNodeDescriptor.ForVariant(variant);
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 64, desc.Stride);
        var tree = new SpatialRTree<PersistentStore>(segment, variant);
        int coordCount = desc.CoordCount;
        var oracle = new BruteForceSpatialIndex(coordCount);
        var rng = new Random(seed);

        var accessor = segment.CreateChunkAccessor();
        try
        {
            for (int i = 0; i < entityCount; i++)
            {
                int halfCoord = coordCount / 2;
                Span<double> coords = stackalloc double[coordCount];
                for (int d = 0; d < halfCoord; d++)
                {
                    double v = rng.NextDouble() * 1000;
                    double size = rng.NextDouble() * 10 + 1;
                    coords[d] = v;
                    coords[d + halfCoord] = v + size;
                }
                tree.Insert(i + 1, coords, ref accessor);
                oracle.Insert(i + 1, coords);
            }
        }
        finally
        {
            accessor.Dispose();
        }

        return (tree, oracle, segment);
    }

    // ── Radius Query ─────────────────────────────────────────────────────

    [Test]
    [CancelAfter(5000)]
    public void QueryRadius_MatchesBruteForce()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var em = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(em);

        var (tree, oracle, _) = CreateTreeAndOracle(pmmf, 200);

        Span<double> center = stackalloc double[] { 500, 500 };
        double radius = 100;

        var treeResults = new HashSet<long>();
        foreach (var hit in tree.QueryRadius(center, radius))
        {
            treeResults.Add(hit.EntityId);
        }

        var oracleResults = oracle.QueryRadius(center, radius);
        Assert.That(treeResults, Is.EquivalentTo(new HashSet<long>(oracleResults)));
        guard.Dispose();
    }

    [Test]
    [CancelAfter(5000)]
    public void QueryRadius_EmptyRegion_ReturnsEmpty()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var em = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(em);

        var (tree, _, _) = CreateTreeAndOracle(pmmf, 50);

        Span<double> center = stackalloc double[] { 5000, 5000 };
        int count = 0;
        foreach (var _ in tree.QueryRadius(center, 10))
        {
            count++;
        }
        Assert.That(count, Is.EqualTo(0));
        guard.Dispose();
    }

    // ── Ray Query ────────────────────────────────────────────────────────

    [Test]
    [CancelAfter(5000)]
    public void QueryRay_MatchesBruteForce()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var em = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(em);

        var (tree, oracle, _) = CreateTreeAndOracle(pmmf, 200);

        Span<double> origin = stackalloc double[] { 0, 500 };
        Span<double> direction = stackalloc double[] { 1, 0 };
        double maxDist = 1500;

        var treeResults = new HashSet<long>();
        foreach (var hit in tree.QueryRay(origin, direction, maxDist))
        {
            treeResults.Add(hit.EntityId);
        }

        var oracleResults = oracle.QueryRay(origin, direction, maxDist);
        var oracleIds = new HashSet<long>();
        foreach (var (id, _) in oracleResults)
        {
            oracleIds.Add(id);
        }

        Assert.That(treeResults, Is.EquivalentTo(oracleIds));
        guard.Dispose();
    }

    [Test]
    [CancelAfter(5000)]
    public void QueryRay_MissAll_ReturnsEmpty()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var em = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(em);

        var (tree, _, _) = CreateTreeAndOracle(pmmf, 50);

        Span<double> origin = stackalloc double[] { 5000, 0 };
        Span<double> direction = stackalloc double[] { 0, 1 };

        int count = 0;
        foreach (var _ in tree.QueryRay(origin, direction, 1000))
        {
            count++;
        }
        Assert.That(count, Is.EqualTo(0));
        guard.Dispose();
    }

    // ── Frustum Query ────────────────────────────────────────────────────

    [Test]
    [CancelAfter(5000)]
    public void QueryFrustum_MatchesBruteForce()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var em = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(em);

        var (tree, oracle, _) = CreateTreeAndOracle(pmmf, 200);

        // Box frustum [200..800, 200..800] — 4 planes, 3 doubles each (2D: normalX, normalY, distance)
        Span<double> planes = stackalloc double[]
        {
            1, 0, -200,    // x >= 200
            -1, 0, 800,    // x <= 800
            0, 1, -200,    // y >= 200
            0, -1, 800,    // y <= 800
        };

        var treeResults = new HashSet<long>();
        foreach (var hit in tree.QueryFrustum(planes, 4))
        {
            treeResults.Add(hit.EntityId);
        }

        var oracleResults = oracle.QueryFrustum(planes, 4);
        Assert.That(treeResults, Is.EquivalentTo(new HashSet<long>(oracleResults)));
        guard.Dispose();
    }

    // ── kNN Query ────────────────────────────────────────────────────────

    [Test]
    [CancelAfter(5000)]
    public void QueryKNN_ExactK()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var em = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(em);

        var (tree, _, _) = CreateTreeAndOracle(pmmf, 100);

        Span<double> center = stackalloc double[] { 500, 500 };
        Span<(long entityId, double distSq)> results = stackalloc (long, double)[5];

        int count = tree.QueryKNN(center, 5, results);
        Assert.That(count, Is.EqualTo(5));
        guard.Dispose();
    }

    [Test]
    [CancelAfter(5000)]
    public void QueryKNN_FewerThanK_ReturnsAll()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var em = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(em);

        var (tree, _, _) = CreateTreeAndOracle(pmmf, 10);

        Span<double> center = stackalloc double[] { 500, 500 };
        Span<(long entityId, double distSq)> results = stackalloc (long, double)[100];

        int count = tree.QueryKNN(center, 100, results);
        Assert.That(count, Is.EqualTo(10));
        guard.Dispose();
    }

    // ── Geometry helper tests ────────────────────────────────────────────

    [Test]
    public void RayAABBIntersect_HitFromOutside()
    {
        Span<double> origin = stackalloc double[] { 0, 5 };
        Span<double> invDir = stackalloc double[] { 1.0, 0 }; // horizontal ray (invDir.Y = 0 → not infinity for this test since no Y component)
        Span<double> aabb = stackalloc double[] { 10, 0, 20, 10 };

        // Need proper invDir: 1/dirX=1, 1/dirY=MaxValue (direction.Y=0)
        invDir[1] = double.MaxValue;

        var (hit, tEntry) = SpatialGeometry.RayAABBIntersect(origin, invDir, aabb, 4);
        Assert.That(hit, Is.True);
        Assert.That(tEntry, Is.EqualTo(10.0).Within(0.001));
    }

    [Test]
    public void RayAABBIntersect_Miss()
    {
        Span<double> origin = stackalloc double[] { 0, 15 };
        Span<double> invDir = stackalloc double[] { 1.0, double.MaxValue };
        Span<double> aabb = stackalloc double[] { 10, 0, 20, 10 };

        var (hit, _) = SpatialGeometry.RayAABBIntersect(origin, invDir, aabb, 4);
        Assert.That(hit, Is.False);
    }

    [Test]
    public void ClassifyAABB_Inside()
    {
        Span<double> planes = stackalloc double[]
        {
            1, 0, 0,
            -1, 0, 100,
            0, 1, 0,
            0, -1, 100,
        };
        Span<double> aabb = stackalloc double[] { 20, 20, 80, 80 };
        int cls = SpatialGeometry.ClassifyAABBAgainstPlanes(aabb, planes, 4, 2);
        Assert.That(cls, Is.EqualTo(SpatialGeometry.FrustumInside));
    }

    [Test]
    public void ClassifyAABB_Outside()
    {
        Span<double> planes = stackalloc double[]
        {
            1, 0, 0,
            -1, 0, 100,
            0, 1, 0,
            0, -1, 100,
        };
        Span<double> aabb = stackalloc double[] { 200, 200, 300, 300 };
        int cls = SpatialGeometry.ClassifyAABBAgainstPlanes(aabb, planes, 4, 2);
        Assert.That(cls, Is.EqualTo(SpatialGeometry.FrustumOutside));
    }

    [Test]
    public void ClassifyAABB_Intersecting()
    {
        Span<double> planes = stackalloc double[]
        {
            1, 0, 0,
            -1, 0, 100,
            0, 1, 0,
            0, -1, 100,
        };
        Span<double> aabb = stackalloc double[] { 50, 50, 150, 150 };
        int cls = SpatialGeometry.ClassifyAABBAgainstPlanes(aabb, planes, 4, 2);
        Assert.That(cls, Is.EqualTo(SpatialGeometry.FrustumIntersecting));
    }

    [Test]
    public void SquaredDistanceToCenter_Correct()
    {
        Span<double> point = stackalloc double[] { 0, 0 };
        Span<double> aabb = stackalloc double[] { 10, 20, 30, 40 };
        double distSq = SpatialGeometry.SquaredDistanceToCenter(point, aabb, 4);
        Assert.That(distSq, Is.EqualTo(1300.0).Within(0.001));
    }

    // ── Count Queries ────────────────────────────────────────────────────

    [Test]
    [CancelAfter(5000)]
    public void CountInAABB_MatchesBruteForce()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var em = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(em);

        var (tree, oracle, _) = CreateTreeAndOracle(pmmf, 200);

        Span<double> queryCoords = stackalloc double[] { 200, 200, 800, 800 };
        int treeCount = tree.CountInAABB(queryCoords);
        int oracleCount = oracle.CountInAABB(queryCoords);

        Assert.That(treeCount, Is.EqualTo(oracleCount));
        Assert.That(treeCount, Is.GreaterThan(0), "Query should match some entities");
        guard.Dispose();
    }

    [Test]
    [CancelAfter(5000)]
    public void CountInAABB_EmptyRegion_ReturnsZero()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var em = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(em);

        var (tree, _, _) = CreateTreeAndOracle(pmmf, 50);

        Span<double> queryCoords = stackalloc double[] { 5000, 5000, 6000, 6000 };
        Assert.That(tree.CountInAABB(queryCoords), Is.EqualTo(0));
        guard.Dispose();
    }

    [Test]
    [CancelAfter(5000)]
    public void CountInAABB_WholeWorld_ReturnsEntityCount()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var em = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(em);

        var (tree, _, _) = CreateTreeAndOracle(pmmf, 200);

        // Region encompassing all entities (data is in 0..1010 range)
        Span<double> queryCoords = stackalloc double[] { -1, -1, 2000, 2000 };
        Assert.That(tree.CountInAABB(queryCoords), Is.EqualTo(200));
        guard.Dispose();
    }

    [Test]
    [CancelAfter(5000)]
    public void CountInRadius_MatchesBruteForce()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var em = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(em);

        var (tree, oracle, _) = CreateTreeAndOracle(pmmf, 200);

        Span<double> center = stackalloc double[] { 500, 500 };
        double radius = 200;

        int treeCount = tree.CountInRadius(center, radius);
        int oracleCount = oracle.CountInRadius(center, radius);

        Assert.That(treeCount, Is.EqualTo(oracleCount));
        Assert.That(treeCount, Is.GreaterThan(0), "Query should match some entities");
        guard.Dispose();
    }

    [Test]
    [CancelAfter(5000)]
    public void CountInRadius_EmptyRegion_ReturnsZero()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var em = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(em);

        var (tree, _, _) = CreateTreeAndOracle(pmmf, 50);

        Span<double> center = stackalloc double[] { 5000, 5000 };
        Assert.That(tree.CountInRadius(center, 10), Is.EqualTo(0));
        guard.Dispose();
    }

    [Test]
    [CancelAfter(5000)]
    public void CountInAABB_WithCategoryMask_ReducesCount()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var em = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(em);

        var desc = SpatialNodeDescriptor.ForVariant(SpatialVariant.R2Df32);
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 64, desc.Stride);
        var tree = new SpatialRTree<PersistentStore>(segment, SpatialVariant.R2Df32);
        var oracle = new BruteForceSpatialIndex(desc.CoordCount);
        var rng = new Random(42);

        var accessor = segment.CreateChunkAccessor();
        try
        {
            uint maskA = 0x01;
            uint maskB = 0x02;

            for (int i = 0; i < 200; i++)
            {
                double v = rng.NextDouble() * 1000;
                double v2 = rng.NextDouble() * 1000;
                double size = rng.NextDouble() * 10 + 1;
                Span<double> coords = stackalloc double[] { v, v2, v + size, v2 + size };

                // Alternate category masks: even = A, odd = B
                uint mask = (i % 2 == 0) ? maskA : maskB;
                tree.Insert(i + 1, coords, ref accessor, categoryMask: mask);
                oracle.Insert(i + 1, coords, mask);
            }
        }
        finally
        {
            accessor.Dispose();
        }

        Span<double> queryCoords = stackalloc double[] { 200, 200, 800, 800 };

        int totalCount = tree.CountInAABB(queryCoords);
        int countA = tree.CountInAABB(queryCoords, categoryMask: 0x01);
        int countB = tree.CountInAABB(queryCoords, categoryMask: 0x02);
        int oracleCountA = oracle.CountInAABB(queryCoords, 0x01);
        int oracleCountB = oracle.CountInAABB(queryCoords, 0x02);

        Assert.That(countA, Is.EqualTo(oracleCountA), "Category A count should match oracle");
        Assert.That(countB, Is.EqualTo(oracleCountB), "Category B count should match oracle");
        Assert.That(countA + countB, Is.EqualTo(totalCount), "Disjoint masks should sum to total");
        Assert.That(countA, Is.LessThan(totalCount), "Category filter should reduce count");
        guard.Dispose();
    }

    [Test]
    [CancelAfter(5000)]
    public void CountInAABB_MatchesQueryResultLength()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var em = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(em);

        var (tree, _, _) = CreateTreeAndOracle(pmmf, 200);

        Span<double> queryCoords = stackalloc double[] { 200, 200, 800, 800 };

        // Count via CountInAABB
        int countResult = tree.CountInAABB(queryCoords);

        // Count via iterating QueryAABB
        int queryResult = 0;
        foreach (var _ in tree.QueryAABB(queryCoords))
        {
            queryResult++;
        }

        Assert.That(countResult, Is.EqualTo(queryResult), "CountInAABB must equal QueryAABB iteration count");
        guard.Dispose();
    }

    // ── Compound Queries (two-pass pattern) ──────────────────────────────

    [Test]
    [CancelAfter(5000)]
    public void QueryResult_ExposesComponentChunkId()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var em = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(em);

        var desc = SpatialNodeDescriptor.ForVariant(SpatialVariant.R2Df32);
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 64, desc.Stride);
        var tree = new SpatialRTree<PersistentStore>(segment, SpatialVariant.R2Df32);

        // Insert entities with known componentChunkIds
        var accessor = segment.CreateChunkAccessor();
        try
        {
            tree.Insert(entityId: 100, componentChunkId: 42, stackalloc double[] { 10, 10, 20, 20 }, ref accessor);
            tree.Insert(entityId: 200, componentChunkId: 99, stackalloc double[] { 50, 50, 60, 60 }, ref accessor);
        }
        finally { accessor.Dispose(); }

        // Query should return both EntityId and ComponentChunkId
        var results = new List<(long entityId, int compChunkId)>();
        foreach (var hit in tree.QueryAABB(stackalloc double[] { 0, 0, 100, 100 }))
        {
            results.Add((hit.EntityId, hit.ComponentChunkId));
        }

        Assert.That(results, Has.Count.EqualTo(2));
        Assert.That(results, Has.Some.Matches<(long entityId, int compChunkId)>(r => r.entityId == 100 && r.compChunkId == 42));
        Assert.That(results, Has.Some.Matches<(long entityId, int compChunkId)>(r => r.entityId == 200 && r.compChunkId == 99));
        guard.Dispose();
    }

    [Test]
    [CancelAfter(5000)]
    public void CompoundQuery_TwoPassPattern_CategoryMaskThenComponentFilter()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var em = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(em);

        var desc = SpatialNodeDescriptor.ForVariant(SpatialVariant.R2Df32);
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 64, desc.Stride);
        var tree = new SpatialRTree<PersistentStore>(segment, SpatialVariant.R2Df32);

        // Simulate game entities: category 0x01 = enemies, 0x02 = allies
        // "Health" encoded as entityId % 100 (simulated component data, no real CBS needed)
        var accessor = segment.CreateChunkAccessor();
        var rng = new Random(42);
        int entityCount = 200;
        try
        {
            for (int i = 0; i < entityCount; i++)
            {
                double x = rng.NextDouble() * 1000, y = rng.NextDouble() * 1000;
                uint mask = (i % 3 == 0) ? 0x01u : 0x02u; // 1/3 enemies, 2/3 allies
                tree.Insert(i + 1, i + 1, stackalloc double[] { x, y, x + 5, y + 5 }, ref accessor, categoryMask: mask);
            }
        }
        finally { accessor.Dispose(); }

        // Two-pass compound query: "enemies near center with health > 50"
        // Pass 1: spatial + category mask (done by the tree)
        // Pass 2: component predicate on the reduced set (done by caller using ComponentChunkId)
        Span<double> queryCoords = stackalloc double[] { 200, 200, 800, 800 };

        int spatialHits = 0;
        int postFilterHits = 0;

        foreach (var hit in tree.QueryAABB(queryCoords, categoryMask: 0x01))
        {
            spatialHits++;

            // Pass 2: simulate reading component data via ComponentChunkId
            // In real code: byte* compData = componentCBS.GetChunkAddress(hit.ComponentChunkId);
            // Here we simulate: "health > 50" as entityId % 100 > 50
            Assert.That(hit.ComponentChunkId, Is.GreaterThan(0), "ComponentChunkId should be set");
            int simulatedHealth = (int)(hit.EntityId % 100);
            if (simulatedHealth > 50)
            {
                postFilterHits++;
            }
        }

        // Verify the two-pass pattern filters progressively
        int totalEnemies = tree.CountInAABB(queryCoords, categoryMask: 0x01);
        Assert.That(spatialHits, Is.EqualTo(totalEnemies), "Pass 1 should match CountInAABB");
        Assert.That(postFilterHits, Is.LessThanOrEqualTo(spatialHits), "Pass 2 should reduce or equal pass 1");
        Assert.That(spatialHits, Is.GreaterThan(0), "Should have some spatial hits");
        guard.Dispose();
    }
}
