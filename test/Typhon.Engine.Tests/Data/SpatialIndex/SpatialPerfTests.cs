using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// Microbenchmarks for spatial indexing hot paths. Uses Stopwatch with warmup.
/// Not for CI — run manually to measure optimization impact.
/// </summary>
[TestFixture]
[Explicit("Performance benchmarks — run manually")]
public class SpatialPerfTests
{
    private IServiceProvider _serviceProvider;
    private string _testDatabaseDir;

    [SetUp]
    public void Setup()
    {
        _testDatabaseDir = Path.Combine(Path.GetTempPath(), "Typhon.Tests", "SpatialPerfTests");
        Directory.CreateDirectory(_testDatabaseDir);

        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddScopedManagedPagedMemoryMappedFile(o =>
            {
                o.DatabaseName = $"SPT{TestContext.CurrentContext.Test.Name}"[..Math.Min(30, $"SPT{TestContext.CurrentContext.Test.Name}".Length)];
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
        try { foreach (var f in Directory.GetFiles(_testDatabaseDir)) File.Delete(f); } catch { }
    }

    private (SpatialRTree<PersistentStore> tree, ChunkBasedSegment<PersistentStore> segment) CreatePopulatedTree(
        ManagedPagedMMF pmmf, int entityCount, SpatialVariant variant = SpatialVariant.R2Df32)
    {
        var desc = SpatialNodeDescriptor.ForVariant(variant);
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 128, desc.Stride);
        var tree = new SpatialRTree<PersistentStore>(segment, variant);
        var rng = new Random(42);

        var accessor = segment.CreateChunkAccessor();
        try
        {
            for (int i = 0; i < entityCount; i++)
            {
                int halfCoord = desc.CoordCount / 2;
                Span<double> coords = stackalloc double[desc.CoordCount];
                for (int d = 0; d < halfCoord; d++)
                {
                    double v = rng.NextDouble() * 10000;
                    coords[d] = v;
                    coords[d + halfCoord] = v + rng.NextDouble() * 10 + 1;
                }
                tree.Insert(i + 1, coords, ref accessor);
            }
        }
        finally
        {
            accessor.Dispose();
        }

        return (tree, segment);
    }

    // ── AABB Query Benchmark ─────────────────────────────────────────────

    [Test]
    public void Bench_AABBQuery_2Df32_1000Entities()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var em = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(em);

        var (tree, _) = CreatePopulatedTree(pmmf, 1000, SpatialVariant.R2Df32);

        // Warmup
        for (int w = 0; w < 100; w++)
        {
            int count = 0;
            foreach (var _ in tree.QueryAABB(stackalloc double[] { 4000, 4000, 6000, 6000 }))
            {
                count++;
            }
        }

        // Benchmark: 10000 queries
        const int iterations = 10000;
        var sw = Stopwatch.StartNew();
        int totalHits = 0;
        for (int i = 0; i < iterations; i++)
        {
            // Vary query position slightly to avoid branch prediction artifacts
            double offset = (i % 100) * 10.0;
            foreach (var hit in tree.QueryAABB(stackalloc double[] { 4000 + offset, 4000, 5000 + offset, 5000 }))
            {
                totalHits++;
            }
        }
        sw.Stop();

        double nsPerQuery = (double)sw.ElapsedTicks / Stopwatch.Frequency * 1e9 / iterations;
        TestContext.Out.WriteLine($"AABB Query (2D-f32, 1000 entities): {nsPerQuery:F0} ns/query, {totalHits} total hits");

        guard.Dispose();
    }

    [Test]
    public void Bench_AABBQuery_3Df32_1000Entities()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var em = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(em);

        var (tree, _) = CreatePopulatedTree(pmmf, 1000, SpatialVariant.R3Df32);

        for (int w = 0; w < 100; w++)
        {
            foreach (var _ in tree.QueryAABB(stackalloc double[] { 4000, 4000, 4000, 6000, 6000, 6000 })) { }
        }

        const int iterations = 10000;
        var sw = Stopwatch.StartNew();
        int totalHits = 0;
        for (int i = 0; i < iterations; i++)
        {
            double offset = (i % 100) * 10.0;
            foreach (var hit in tree.QueryAABB(stackalloc double[] { 4000 + offset, 4000, 4000, 5000 + offset, 5000, 5000 }))
            {
                totalHits++;
            }
        }
        sw.Stop();

        double nsPerQuery = (double)sw.ElapsedTicks / Stopwatch.Frequency * 1e9 / iterations;
        TestContext.Out.WriteLine($"AABB Query (3D-f32, 1000 entities): {nsPerQuery:F0} ns/query, {totalHits} total hits");

        guard.Dispose();
    }

    // ── Insert Benchmark ─────────────────────────────────────────────────

    [Test]
    public void Bench_Insert_2Df32_10000Entities()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var em = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(em);

        var desc = SpatialNodeDescriptor.ForVariant(SpatialVariant.R2Df32);
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 256, desc.Stride);
        var tree = new SpatialRTree<PersistentStore>(segment, SpatialVariant.R2Df32);
        var rng = new Random(42);

        // Warmup: insert 1000
        var accessor = segment.CreateChunkAccessor();
        for (int i = 0; i < 1000; i++)
        {
            double x = rng.NextDouble() * 10000;
            double y = rng.NextDouble() * 10000;
            tree.Insert(i + 1, stackalloc double[] { x, y, x + 5, y + 5 }, ref accessor);
        }

        // Benchmark: insert 10000 more
        const int insertCount = 10000;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < insertCount; i++)
        {
            double x = rng.NextDouble() * 10000;
            double y = rng.NextDouble() * 10000;
            tree.Insert(1001 + i, stackalloc double[] { x, y, x + 5, y + 5 }, ref accessor);
        }
        sw.Stop();
        accessor.Dispose();

        double nsPerInsert = (double)sw.ElapsedTicks / Stopwatch.Frequency * 1e9 / insertCount;
        TestContext.Out.WriteLine($"Insert (2D-f32): {nsPerInsert:F0} ns/insert, tree depth={tree.Depth}, nodes={tree.NodeCount}");

        guard.Dispose();
    }

    // ── Containment Check Benchmark ──────────────────────────────────────

    [Test]
    public void Bench_ContainmentCheck_2Df32()
    {
        // Simulate the fat AABB containment check (fast path of UpdateSpatial)
        // This is called 10K+ times per tick fence
        const int iterations = 1_000_000;

        Span<double> fat = stackalloc double[] { 95, 95, 105, 105 }; // fat AABB: margin=5
        Span<double> tight = stackalloc double[] { 99, 99, 101, 101 }; // tight: within margin

        // Warmup
        for (int w = 0; w < 10000; w++)
        {
            CoordsContainedPublic(fat, tight, 4);
        }

        var sw = Stopwatch.StartNew();
        int contained = 0;
        for (int i = 0; i < iterations; i++)
        {
            // Vary slightly to prevent constant folding
            tight[0] = 99 + (i & 1) * 0.001;
            if (CoordsContainedPublic(fat, tight, 4))
            {
                contained++;
            }
        }
        sw.Stop();

        double nsPerCheck = (double)sw.ElapsedTicks / Stopwatch.Frequency * 1e9 / iterations;
        TestContext.Out.WriteLine($"Containment check (2D): {nsPerCheck:F1} ns/check, contained={contained}/{iterations}");
    }

    // Expose the private method for benchmarking
    private static bool CoordsContainedPublic(ReadOnlySpan<double> fat, ReadOnlySpan<double> tight, int coordCount)
    {
        int half = coordCount / 2;
        for (int i = 0; i < half; i++)
        {
            if (fat[i] > tight[i])
            {
                return false;
            }
        }
        for (int i = half; i < coordCount; i++)
        {
            if (fat[i] < tight[i])
            {
                return false;
            }
        }
        return true;
    }

    // ── Geometry: RayAABBIntersect Benchmark ─────────────────────────────

    [Test]
    public void Bench_RayAABBIntersect()
    {
        const int iterations = 1_000_000;

        Span<double> origin = stackalloc double[] { 0, 5 };
        Span<double> invDir = stackalloc double[] { 1.0, double.MaxValue };
        Span<double> aabb = stackalloc double[] { 10, 0, 20, 10 };

        // Warmup
        for (int w = 0; w < 10000; w++)
        {
            SpatialGeometry.RayAABBIntersect(origin, invDir, aabb, 4);
        }

        var sw = Stopwatch.StartNew();
        int hits = 0;
        for (int i = 0; i < iterations; i++)
        {
            aabb[0] = 10 + (i & 3) * 0.001; // Prevent constant folding
            var (hit, _) = SpatialGeometry.RayAABBIntersect(origin, invDir, aabb, 4);
            if (hit) hits++;
        }
        sw.Stop();

        double nsPerTest = (double)sw.ElapsedTicks / Stopwatch.Frequency * 1e9 / iterations;
        TestContext.Out.WriteLine($"RayAABBIntersect (2D): {nsPerTest:F1} ns/test, hits={hits}/{iterations}");
    }
}
