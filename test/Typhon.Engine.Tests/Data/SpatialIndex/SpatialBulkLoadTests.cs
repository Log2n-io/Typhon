using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

[TestFixture]
[NonParallelizable]
public class SpatialBulkLoadTests
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
            return $"BL{testName}";
        }
    }

    [SetUp]
    public void Setup()
    {
        _testDatabaseDir = Path.Combine(Path.GetTempPath(), "Typhon.Tests", "SpatialBulkLoadTests");
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
                // NOTE: 256 pages (MinimumMemPageCount) is a deliberate cache-stress size used elsewhere, but the
                // SpatialRTree.BulkLoad primitive holds a live working set larger than that and cannot run in it —
                // it allocates without a ChangeSet/UoW, so the dirty pages never become evictable and a checkpoint
                // cannot relieve the pressure (see the ci-merge-gate dig). Size the cache to fit the bulk workload.
                o.DatabaseCacheSize = (ulong)(8192 * PagedMMF.PageSize);
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
                    try { File.Delete(file); }
                    catch { /* ignore cleanup failures */ }
                }
            }
            catch { /* ignore */ }
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static void GenerateRandomEntries(int count, SpatialVariant variant, Random rng, out long[] entityIds, out int[] compChunkIds,
        out double[] coords, out uint[] categoryMasks)
    {
        var desc = SpatialNodeDescriptor.ForVariant(variant);
        int coordCount = desc.CoordCount;
        int halfCoord = coordCount / 2;

        entityIds = new long[count];
        compChunkIds = new int[count];
        coords = new double[count * coordCount];
        categoryMasks = new uint[count];

        for (int i = 0; i < count; i++)
        {
            entityIds[i] = i + 1;
            compChunkIds[i] = 0; // standalone test, no back-pointers
            categoryMasks[i] = uint.MaxValue;

            double cx = rng.NextDouble() * 1000;
            double cy = rng.NextDouble() * 1000;
            double size = rng.NextDouble() * 5 + 1;
            int baseIdx = i * coordCount;
            coords[baseIdx + 0] = cx;
            coords[baseIdx + 1] = cy;
            if (halfCoord == 3) coords[baseIdx + 2] = rng.NextDouble() * 1000;
            coords[baseIdx + halfCoord] = cx + size;
            coords[baseIdx + halfCoord + 1] = cy + size;
            if (halfCoord == 3) coords[baseIdx + halfCoord + 2] = coords[baseIdx + 2] + size;
        }
    }

    // ── Tests ───────────────────────────────────────────────────────────

    [Test]
    [TestCase(SpatialVariant.R2Df32)]
    [TestCase(SpatialVariant.R3Df32)]
    [TestCase(SpatialVariant.R2Df64)]
    [TestCase(SpatialVariant.R3Df64)]
    [CancelAfter(5000)]
    public void BulkLoad_EmptyInput_ProducesEmptyTree(SpatialVariant variant)
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var em = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(em);

        var desc = SpatialNodeDescriptor.ForVariant(variant);
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, desc.Stride);

        var tree = SpatialRTree<PersistentStore>.BulkLoad(segment, variant,
            ReadOnlySpan<long>.Empty, ReadOnlySpan<int>.Empty, ReadOnlySpan<double>.Empty, ReadOnlySpan<uint>.Empty);

        Assert.That(tree.EntityCount, Is.EqualTo(0));
        TreeValidator.Validate(tree);
        guard.Dispose();
    }

    [Test]
    [TestCase(SpatialVariant.R2Df32, 1)]
    [TestCase(SpatialVariant.R2Df32, 5)]
    [TestCase(SpatialVariant.R2Df32, 100)]
    [TestCase(SpatialVariant.R2Df32, 1000)]
    [TestCase(SpatialVariant.R3Df32, 100)]
    [TestCase(SpatialVariant.R3Df32, 1000)]
    [TestCase(SpatialVariant.R2Df64, 100)]
    [TestCase(SpatialVariant.R3Df64, 100)]
    [CancelAfter(5000)]
    public void BulkLoad_ProducesValidTree(SpatialVariant variant, int entityCount)
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var em = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(em);

        var desc = SpatialNodeDescriptor.ForVariant(variant);
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 500, desc.Stride);

        var rng = new Random(42);
        GenerateRandomEntries(entityCount, variant, rng, out var entityIds, out var compChunkIds, out var coords, out var categoryMasks);

        var tree = SpatialRTree<PersistentStore>.BulkLoad(segment, variant, entityIds, compChunkIds, coords, categoryMasks);

        Assert.That(tree.EntityCount, Is.EqualTo(entityCount));
        TreeValidator.Validate(tree);
        guard.Dispose();
    }

    [Test]
    [TestCase(SpatialVariant.R2Df32, 200)]
    [TestCase(SpatialVariant.R3Df32, 200)]
    [CancelAfter(5000)]
    public void BulkLoad_QueryMatchesBruteForce(SpatialVariant variant, int entityCount)
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var em = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(em);

        var desc = SpatialNodeDescriptor.ForVariant(variant);
        int coordCount = desc.CoordCount;
        int halfCoord = coordCount / 2;
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 500, desc.Stride);

        var rng = new Random(123);
        GenerateRandomEntries(entityCount, variant, rng, out var entityIds, out var compChunkIds, out var coords, out var categoryMasks);

        var tree = SpatialRTree<PersistentStore>.BulkLoad(segment, variant, entityIds, compChunkIds, coords, categoryMasks);

        // Build brute-force oracle
        var oracle = new BruteForceSpatialIndex(coordCount);
        for (int i = 0; i < entityCount; i++)
        {
            oracle.Insert(entityIds[i], coords.AsSpan(i * coordCount, coordCount));
        }

        // Run 20 random AABB queries and compare results
        for (int q = 0; q < 20; q++)
        {
            double qx = rng.NextDouble() * 900;
            double qy = rng.NextDouble() * 900;
            double qSize = rng.NextDouble() * 100 + 10;

            double[] queryCoords = new double[coordCount];
            queryCoords[0] = qx;
            queryCoords[1] = qy;
            if (halfCoord == 3) queryCoords[2] = rng.NextDouble() * 900;
            queryCoords[halfCoord] = qx + qSize;
            queryCoords[halfCoord + 1] = qy + qSize;
            if (halfCoord == 3) queryCoords[halfCoord + 2] = queryCoords[2] + qSize;

            var treeResults = new HashSet<long>();
            foreach (var hit in tree.QueryAABB(queryCoords))
            {
                treeResults.Add(hit.EntityId);
            }

            var oracleResults = new HashSet<long>(oracle.QueryAABB(queryCoords));

            Assert.That(treeResults, Is.EquivalentTo(oracleResults), $"Query {q}: results mismatch");
        }

        guard.Dispose();
    }

    [Test]
    [TestCase(SpatialVariant.R2Df32, 500)]
    [CancelAfter(5000)]
    public void BulkLoad_CategoryMask_Filtering(SpatialVariant variant, int entityCount)
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var em = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(em);

        var desc = SpatialNodeDescriptor.ForVariant(variant);
        int coordCount = desc.CoordCount;
        int halfCoord = coordCount / 2;
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 500, desc.Stride);

        var rng = new Random(99);
        GenerateRandomEntries(entityCount, variant, rng, out var entityIds, out var compChunkIds, out var coords, out var categoryMasks);

        // Set half the entities to category 1, half to category 2
        for (int i = 0; i < entityCount; i++)
        {
            categoryMasks[i] = (uint)(i % 2 == 0 ? 1 : 2);
        }

        var tree = SpatialRTree<PersistentStore>.BulkLoad(segment, variant, entityIds, compChunkIds, coords, categoryMasks);
        TreeValidator.Validate(tree);

        // Query with mask=1 should only return even-indexed entities
        double[] bigQuery = new double[coordCount];
        for (int d = 0; d < halfCoord; d++) bigQuery[d] = -10000;
        for (int d = halfCoord; d < coordCount; d++) bigQuery[d] = 10000;

        var mask1Results = new List<long>();
        foreach (var hit in tree.QueryAABB(bigQuery, categoryMask: 1))
        {
            mask1Results.Add(hit.EntityId);
        }

        // All results should be even-indexed entity IDs (1, 3, 5, ... based on our generation)
        foreach (long id in mask1Results)
        {
            int idx = (int)(id - 1);
            Assert.That(idx % 2, Is.EqualTo(0), $"Entity {id} should not be in mask=1 results");
        }

        Assert.That(mask1Results.Count, Is.EqualTo(entityCount / 2));
        guard.Dispose();
    }
}
