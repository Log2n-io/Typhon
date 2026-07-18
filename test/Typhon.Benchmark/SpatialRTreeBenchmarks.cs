using System;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Typhon.Engine;

namespace Typhon.Benchmark;

// ═══════════════════════════════════════════════════════════════════════
// 1. INSERT BENCHMARKS
// ═══════════════════════════════════════════════════════════════════════

[SimpleJob(warmupCount: 3, iterationCount: 10)]
[JsonExporterAttribute.Full]
[BenchmarkCategory("SpatialRTree", "Insert")]
public unsafe class SpatialInsertBenchmarks
{
    private ServiceCollection _sc;
    private ServiceProvider _sp;
    private ManagedPagedMMF _pmmf;
    private EpochManager _em;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _sc = new ServiceCollection();
        _sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
            .AddResourceRegistry().AddMemoryAllocator().AddEpochManager()
            .AddScopedManagedPagedMemoryMappedFile(o =>
            {
                o.DatabaseName = "SpatInsert";
                o.DatabaseCacheSize = (ulong)(128L * 1024 * PagedMMF.PageSize);
                o.TestMode = true;
            });
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _sp = _sc.BuildServiceProvider();
        _sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
        _pmmf = _sp.GetRequiredService<ManagedPagedMMF>();
        _em = _sp.GetRequiredService<EpochManager>();
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        _em?.Dispose(); _pmmf?.Dispose(); _sp?.Dispose();
    }

    private void RunInsert(int count, SpatialVariant variant, double worldSize, bool clustered)
    {
        using var guard = EpochGuard.Enter(_em);
        var desc = SpatialNodeDescriptor.ForVariant(variant);
        var seg = _pmmf.AllocateChunkBasedSegment(PageBlockType.None, 512, desc.Stride);
        var tree = new SpatialRTree<PersistentStore>(seg, variant);
        var rng = new Random(42);
        int halfCoord = desc.CoordCount / 2;
        var accessor = seg.CreateChunkAccessor();
        try
        {
            for (int i = 0; i < count; i++)
            {
                Span<double> coords = stackalloc double[desc.CoordCount];
                for (int d = 0; d < halfCoord; d++)
                {
                    double v = clustered ? 5000 + rng.NextDouble() * 100 : rng.NextDouble() * worldSize;
                    coords[d] = v;
                    coords[d + halfCoord] = v + rng.NextDouble() * 8 + 2;
                }
                tree.Insert(i + 1, coords, ref accessor);
            }
        }
        finally { accessor.Dispose(); }
    }

    [Benchmark] public void Insert_Sparse_2D_1K() => RunInsert(1_000, SpatialVariant.R2Df32, 10000, false);
    [Benchmark] public void Insert_Sparse_2D_10K() => RunInsert(10_000, SpatialVariant.R2Df32, 10000, false);
    [Benchmark] public void Insert_Sparse_2D_100K() => RunInsert(100_000, SpatialVariant.R2Df32, 10000, false);
    [Benchmark] public void Insert_Clustered_2D_10K() => RunInsert(10_000, SpatialVariant.R2Df32, 10000, true);
    [Benchmark] public void Insert_3D_10K() => RunInsert(10_000, SpatialVariant.R3Df32, 10000, false);
}

// ═══════════════════════════════════════════════════════════════════════
// 2. QUERY BENCHMARKS (AABB + Frustum)
// ═══════════════════════════════════════════════════════════════════════

[SimpleJob(warmupCount: 3, iterationCount: 10)]
[JsonExporterAttribute.Full]
[BenchmarkCategory("SpatialRTree", "Query")]
public unsafe class SpatialQueryBenchmarks
{
    private ServiceCollection _sc;
    private ServiceProvider _sp;
    private ManagedPagedMMF _pmmf;
    private EpochManager _em;
    private SpatialRTree<PersistentStore> _tree;
    private SpatialVariant _variant;

    [Params(1_000, 10_000, 100_000)]
    public int EntityCount;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _sc = new ServiceCollection();
        _sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
            .AddResourceRegistry().AddMemoryAllocator().AddEpochManager()
            .AddScopedManagedPagedMemoryMappedFile(o =>
            {
                o.DatabaseName = "SpatQuery";
                o.DatabaseCacheSize = (ulong)(128L * 1024 * PagedMMF.PageSize);
                o.TestMode = true;
            });
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _sp = _sc.BuildServiceProvider();
        _sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
        _pmmf = _sp.GetRequiredService<ManagedPagedMMF>();
        _em = _sp.GetRequiredService<EpochManager>();
        _variant = SpatialVariant.R2Df32;

        using var guard = EpochGuard.Enter(_em);
        var desc = SpatialNodeDescriptor.ForVariant(_variant);
        var seg = _pmmf.AllocateChunkBasedSegment(PageBlockType.None, 512, desc.Stride);
        _tree = new SpatialRTree<PersistentStore>(seg, _variant);
        var rng = new Random(42);
        var accessor = seg.CreateChunkAccessor();
        try
        {
            for (int i = 0; i < EntityCount; i++)
            {
                double x = rng.NextDouble() * 10000, y = rng.NextDouble() * 10000;
                _tree.Insert(i + 1, stackalloc double[] { x, y, x + 5, y + 5 }, ref accessor);
            }
        }
        finally { accessor.Dispose(); }
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        _tree = null;
        _em?.Dispose(); _pmmf?.Dispose(); _sp?.Dispose();
    }

    [Benchmark]
    public int Query_SmallBox_2D()
    {
        using var guard = EpochGuard.Enter(_em);
        int total = 0;
        for (int q = 0; q < 100; q++)
        {
            double cx = (q % 10) * 1000 + 450;
            foreach (var _ in _tree.QueryAABB(stackalloc double[] { cx, 4500, cx + 100, 5500 }))
            {
                total++;
            }
        }
        return total;
    }

    [Benchmark]
    public int Query_LargeBox_2D()
    {
        using var guard = EpochGuard.Enter(_em);
        int total = 0;
        for (int q = 0; q < 100; q++)
        {
            foreach (var _ in _tree.QueryAABB(stackalloc double[] { 0, 0, 5000, 5000 }))
            {
                total++;
            }
        }
        return total;
    }

    [Benchmark]
    public int Query_MissAll_2D()
    {
        using var guard = EpochGuard.Enter(_em);
        int total = 0;
        for (int q = 0; q < 100; q++)
        {
            foreach (var _ in _tree.QueryAABB(stackalloc double[] { 20000, 20000, 20100, 20100 }))
            {
                total++;
            }
        }
        return total;
    }

    [Benchmark]
    public int Frustum_Narrow_2D()
    {
        using var guard = EpochGuard.Enter(_em);
        int total = 0;
        Span<double> planes = stackalloc double[]
        {
            1, 0, -4900, -1, 0, 5100, 0, 1, 0, 0, -1, 10000,
        };
        for (int q = 0; q < 100; q++)
        {
            foreach (var _ in _tree.QueryFrustum(planes, 4))
            {
                total++;
            }
        }
        return total;
    }

    [Benchmark]
    public int Frustum_Wide_2D()
    {
        using var guard = EpochGuard.Enter(_em);
        int total = 0;
        Span<double> planes = stackalloc double[]
        {
            1, 0, 0, -1, 0, 5000, 0, 1, 0, 0, -1, 5000,
        };
        for (int q = 0; q < 100; q++)
        {
            foreach (var _ in _tree.QueryFrustum(planes, 4))
            {
                total++;
            }
        }
        return total;
    }
}

// ═══════════════════════════════════════════════════════════════════════
// 3. COMPOUND QUERY (two-pass) BENCHMARKS
// ═══════════════════════════════════════════════════════════════════════

/// <summary>
/// Measures post-filter waste: spatial query with/without category mask → simulated component read.
/// The two-pass pattern: (1) spatial+category query reduces candidates, (2) caller reads component data
/// via ComponentChunkId and applies a predicate. Category masks cut the post-filter set dramatically.
/// </summary>
[SimpleJob(warmupCount: 3, iterationCount: 10)]
[JsonExporterAttribute.Full]
[BenchmarkCategory("SpatialRTree", "Compound")]
public unsafe class SpatialCompoundQueryBenchmarks
{
    private ServiceCollection _sc;
    private ServiceProvider _sp;
    private ManagedPagedMMF _pmmf;
    private EpochManager _em;
    private SpatialRTree<PersistentStore> _tree;

    [Params(1_000, 10_000)]
    public int EntityCount;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _sc = new ServiceCollection();
        _sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
            .AddResourceRegistry().AddMemoryAllocator().AddEpochManager()
            .AddScopedManagedPagedMemoryMappedFile(o =>
            {
                o.DatabaseName = "SpatCompound";
                o.DatabaseCacheSize = (ulong)(128L * 1024 * PagedMMF.PageSize);
                o.TestMode = true;
            });
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _sp = _sc.BuildServiceProvider();
        _sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
        _pmmf = _sp.GetRequiredService<ManagedPagedMMF>();
        _em = _sp.GetRequiredService<EpochManager>();

        using var guard = EpochGuard.Enter(_em);
        var desc = SpatialNodeDescriptor.ForVariant(SpatialVariant.R2Df32);
        var seg = _pmmf.AllocateChunkBasedSegment(PageBlockType.None, 512, desc.Stride);
        _tree = new SpatialRTree<PersistentStore>(seg, SpatialVariant.R2Df32);
        var rng = new Random(42);
        var accessor = seg.CreateChunkAccessor();
        try
        {
            // 4 categories: 25% each (Enemy=0x01, Ally=0x02, Projectile=0x04, Terrain=0x08)
            uint[] masks = [0x01, 0x02, 0x04, 0x08];
            for (int i = 0; i < EntityCount; i++)
            {
                double x = rng.NextDouble() * 10000, y = rng.NextDouble() * 10000;
                _tree.Insert(i + 1, i + 1, stackalloc double[] { x, y, x + 5, y + 5 }, ref accessor,
                    categoryMask: masks[i % 4]);
            }
        }
        finally { accessor.Dispose(); }
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        _tree = null;
        _em?.Dispose(); _pmmf?.Dispose(); _sp?.Dispose();
    }

    // ── Baseline: full query (no mask), enumerate all, apply post-filter ──

    [Benchmark(Baseline = true)]
    public int NoMask_PostFilter()
    {
        using var guard = EpochGuard.Enter(_em);
        int accepted = 0;
        for (int q = 0; q < 100; q++)
        {
            foreach (var hit in _tree.QueryAABB(stackalloc double[] { 0, 0, 5000, 5000 }))
            {
                // Simulate: "is this an enemy?" post-filter without category mask
                // Uses ComponentChunkId to index into simulated component array
                if (hit.ComponentChunkId % 4 == 0) // ~25% pass rate
                {
                    accepted++;
                }
            }
        }
        return accepted;
    }

    // ── Two-pass: category mask pre-filters, then component post-filter ──

    [Benchmark]
    public int WithMask_PostFilter()
    {
        using var guard = EpochGuard.Enter(_em);
        int accepted = 0;
        for (int q = 0; q < 100; q++)
        {
            // Pass 1: tree does spatial + category filtering (only enemies: 0x01)
            foreach (var hit in _tree.QueryAABB(stackalloc double[] { 0, 0, 5000, 5000 }, categoryMask: 0x01))
            {
                // Pass 2: component-level predicate on the reduced set
                if (hit.ComponentChunkId % 2 == 0) // ~50% of enemies pass
                {
                    accepted++;
                }
            }
        }
        return accepted;
    }

    // ── Two-mask intersection: enemies AND alive (conjunctive) ──

    [Benchmark]
    public int ConjunctiveMask_PostFilter()
    {
        using var guard = EpochGuard.Enter(_em);
        int accepted = 0;
        for (int q = 0; q < 100; q++)
        {
            // Category mask 0x01 matches entities inserted with mask 0x01
            // Only the enemy-category entities pass the tree filter
            foreach (var hit in _tree.QueryAABB(stackalloc double[] { 0, 0, 5000, 5000 }, categoryMask: 0x01))
            {
                // Simulate reading health via ComponentChunkId and filtering
                if (hit.ComponentChunkId > 10)
                {
                    accepted++;
                }
            }
        }
        return accepted;
    }

    // ── Count-only: no materialization at all ──

    [Benchmark]
    public int CountOnly_NoPostFilter()
    {
        using var guard = EpochGuard.Enter(_em);
        int total = 0;
        for (int q = 0; q < 100; q++)
        {
            total += _tree.CountInAABB(stackalloc double[] { 0, 0, 5000, 5000 }, categoryMask: 0x01);
        }
        return total;
    }
}

// ═══════════════════════════════════════════════════════════════════════
// 4. COUNT vs FULL QUERY BENCHMARKS
// ═══════════════════════════════════════════════════════════════════════

/// <summary>
/// Compares CountInAABB vs full QueryAABB across different containment ratios.
/// The subtree counting shortcut fires when child MBRs are fully contained in the query box.
/// </summary>
[SimpleJob(warmupCount: 3, iterationCount: 10)]
[JsonExporterAttribute.Full]
[BenchmarkCategory("SpatialRTree", "Count")]
public unsafe class SpatialCountBenchmarks
{
    private ServiceCollection _sc;
    private ServiceProvider _sp;
    private ManagedPagedMMF _pmmf;
    private EpochManager _em;
    private SpatialRTree<PersistentStore> _tree;

    [Params(1_000, 10_000, 100_000)]
    public int EntityCount;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _sc = new ServiceCollection();
        _sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
            .AddResourceRegistry().AddMemoryAllocator().AddEpochManager()
            .AddScopedManagedPagedMemoryMappedFile(o =>
            {
                o.DatabaseName = "SpatCount";
                o.DatabaseCacheSize = (ulong)(128L * 1024 * PagedMMF.PageSize);
                o.TestMode = true;
            });
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _sp = _sc.BuildServiceProvider();
        _sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
        _pmmf = _sp.GetRequiredService<ManagedPagedMMF>();
        _em = _sp.GetRequiredService<EpochManager>();

        using var guard = EpochGuard.Enter(_em);
        var desc = SpatialNodeDescriptor.ForVariant(SpatialVariant.R2Df32);
        var seg = _pmmf.AllocateChunkBasedSegment(PageBlockType.None, 512, desc.Stride);
        _tree = new SpatialRTree<PersistentStore>(seg, SpatialVariant.R2Df32);
        var rng = new Random(42);
        var accessor = seg.CreateChunkAccessor();
        try
        {
            // Entities uniformly distributed in [0..10000, 0..10000], size ~5
            for (int i = 0; i < EntityCount; i++)
            {
                double x = rng.NextDouble() * 10000, y = rng.NextDouble() * 10000;
                // Alternate category masks: even=0x01, odd=0x02
                uint mask = (i % 2 == 0) ? 0x01u : 0x02u;
                _tree.Insert(i + 1, stackalloc double[] { x, y, x + 5, y + 5 }, ref accessor, categoryMask: mask);
            }
        }
        finally { accessor.Dispose(); }
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        _tree = null;
        _em?.Dispose(); _pmmf?.Dispose(); _sp?.Dispose();
    }

    // ── Small box (~1% of area): mostly partial overlaps, few fully-contained subtrees ──

    [Benchmark]
    public int Count_SmallBox()
    {
        using var guard = EpochGuard.Enter(_em);
        int total = 0;
        for (int q = 0; q < 100; q++)
        {
            double cx = (q % 10) * 1000 + 450;
            total += _tree.CountInAABB(stackalloc double[] { cx, 4500, cx + 100, 5500 });
        }
        return total;
    }

    [Benchmark]
    public int Query_SmallBox()
    {
        using var guard = EpochGuard.Enter(_em);
        int total = 0;
        for (int q = 0; q < 100; q++)
        {
            double cx = (q % 10) * 1000 + 450;
            foreach (var _ in _tree.QueryAABB(stackalloc double[] { cx, 4500, cx + 100, 5500 }))
            {
                total++;
            }
        }
        return total;
    }

    // ── Large box (~25% of area): many fully-contained subtrees ──

    [Benchmark]
    public int Count_LargeBox()
    {
        using var guard = EpochGuard.Enter(_em);
        int total = 0;
        for (int q = 0; q < 100; q++)
        {
            total += _tree.CountInAABB(stackalloc double[] { 0, 0, 5000, 5000 });
        }
        return total;
    }

    [Benchmark]
    public int Query_LargeBox()
    {
        using var guard = EpochGuard.Enter(_em);
        int total = 0;
        for (int q = 0; q < 100; q++)
        {
            foreach (var _ in _tree.QueryAABB(stackalloc double[] { 0, 0, 5000, 5000 }))
            {
                total++;
            }
        }
        return total;
    }

    // ── Whole world (100%): maximum subtree shortcut, every node fully contained ──

    [Benchmark]
    public int Count_WholeWorld()
    {
        using var guard = EpochGuard.Enter(_em);
        int total = 0;
        for (int q = 0; q < 100; q++)
        {
            total += _tree.CountInAABB(stackalloc double[] { -1, -1, 10001, 10001 });
        }
        return total;
    }

    [Benchmark]
    public int Query_WholeWorld()
    {
        using var guard = EpochGuard.Enter(_em);
        int total = 0;
        for (int q = 0; q < 100; q++)
        {
            foreach (var _ in _tree.QueryAABB(stackalloc double[] { -1, -1, 10001, 10001 }))
            {
                total++;
            }
        }
        return total;
    }

    // ── Count with category mask (~50% filter): subtree shortcut for geometry, still scan for category ──

    [Benchmark]
    public int Count_LargeBox_CategoryMask()
    {
        using var guard = EpochGuard.Enter(_em);
        int total = 0;
        for (int q = 0; q < 100; q++)
        {
            total += _tree.CountInAABB(stackalloc double[] { 0, 0, 5000, 5000 }, categoryMask: 0x01);
        }
        return total;
    }

    [Benchmark]
    public int Query_LargeBox_CategoryMask()
    {
        using var guard = EpochGuard.Enter(_em);
        int total = 0;
        for (int q = 0; q < 100; q++)
        {
            foreach (var _ in _tree.QueryAABB(stackalloc double[] { 0, 0, 5000, 5000 }, categoryMask: 0x01))
            {
                total++;
            }
        }
        return total;
    }

    // ── Miss all: disjoint query region, pure pruning ──

    [Benchmark]
    public int Count_MissAll()
    {
        using var guard = EpochGuard.Enter(_em);
        int total = 0;
        for (int q = 0; q < 100; q++)
        {
            total += _tree.CountInAABB(stackalloc double[] { 20000, 20000, 20100, 20100 });
        }
        return total;
    }
}

// ═══════════════════════════════════════════════════════════════════════
// 4. GAME TICK MIXED WORKLOAD
// ═══════════════════════════════════════════════════════════════════════

[SimpleJob(warmupCount: 2, iterationCount: 5)]
[JsonExporterAttribute.Full]
[BenchmarkCategory("SpatialRTree", "GameTick")]
public unsafe class SpatialGameTickBenchmarks
{
    private ServiceCollection _sc;
    private ServiceProvider _sp;
    private ManagedPagedMMF _pmmf;
    private EpochManager _em;
    private SpatialRTree<PersistentStore> _tree;
    private ChunkBasedSegment<PersistentStore> _segment;
    private double[] _entityCoords;
    private int _entityCount;

    [Params(10_000, 50_000, 100_000, 200_000)]
    public int EntityCount;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _sc = new ServiceCollection();
        _sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
            .AddResourceRegistry().AddMemoryAllocator().AddEpochManager()
            .AddScopedManagedPagedMemoryMappedFile(o =>
            {
                o.DatabaseName = "SpatTick";
                o.DatabaseCacheSize = (ulong)(128L * 1024 * PagedMMF.PageSize);
                o.TestMode = true;
            });
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _sp = _sc.BuildServiceProvider();
        _sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
        _pmmf = _sp.GetRequiredService<ManagedPagedMMF>();
        _em = _sp.GetRequiredService<EpochManager>();

        using var guard = EpochGuard.Enter(_em);
        var desc = SpatialNodeDescriptor.ForVariant(SpatialVariant.R2Df32);
        _segment = _pmmf.AllocateChunkBasedSegment(PageBlockType.None, 512, desc.Stride);
        _tree = new SpatialRTree<PersistentStore>(_segment, SpatialVariant.R2Df32);
        _entityCoords = new double[EntityCount * 4];
        var rng = new Random(42);
        var accessor = _segment.CreateChunkAccessor();
        try
        {
            for (int i = 0; i < EntityCount; i++)
            {
                double x = rng.NextDouble() * 10000, y = rng.NextDouble() * 10000;
                _entityCoords[i * 4] = x; _entityCoords[i * 4 + 1] = y;
                _entityCoords[i * 4 + 2] = x + 5; _entityCoords[i * 4 + 3] = y + 5;
                _tree.Insert(i + 1, stackalloc double[] { x, y, x + 5, y + 5 }, ref accessor);
            }
        }
        finally { accessor.Dispose(); }
        _entityCount = EntityCount;
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        _tree = null; _segment = null; _entityCoords = null;
        _em?.Dispose(); _pmmf?.Dispose(); _sp?.Dispose();
    }

    [Benchmark]
    public int GameTick()
    {
        using var guard = EpochGuard.Enter(_em);
        var rng = new Random(123);
        var accessor = _segment.CreateChunkAccessor();
        int ops = 0;
        try
        {
            // 5% spawn
            int spawnCount = _entityCount / 20;
            for (int i = 0; i < spawnCount; i++)
            {
                double x = rng.NextDouble() * 10000, y = rng.NextDouble() * 10000;
                _tree.Insert(_entityCount + i + 1, stackalloc double[] { x, y, x + 5, y + 5 }, ref accessor);
                ops++;
            }

            // 30% containment checks (2% escape rate)
            int moveCount = (int)(_entityCount * 0.3);
            Span<double> fat = stackalloc double[4];
            Span<double> tight = stackalloc double[4];
            for (int i = 0; i < moveCount; i++)
            {
                int idx = rng.Next(_entityCount);
                fat[0] = _entityCoords[idx * 4] - 5; fat[1] = _entityCoords[idx * 4 + 1] - 5;
                fat[2] = _entityCoords[idx * 4 + 2] + 5; fat[3] = _entityCoords[idx * 4 + 3] + 5;
                double move = (i % 50 == 0) ? 20.0 : 0.5;
                tight[0] = _entityCoords[idx * 4] + move; tight[1] = _entityCoords[idx * 4 + 1] + move;
                tight[2] = _entityCoords[idx * 4 + 2] + move; tight[3] = _entityCoords[idx * 4 + 3] + move;
                CoordsContained(fat, tight, 4);
                ops++;
            }

            // 10 queries
            for (int q = 0; q < 10; q++)
            {
                double cx = rng.NextDouble() * 9000, cy = rng.NextDouble() * 9000;
                foreach (var _ in _tree.QueryAABB(stackalloc double[] { cx, cy, cx + 500, cy + 500 }))
                {
                    ops++;
                }
            }
        }
        finally { accessor.Dispose(); }
        return ops;
    }

    private static bool CoordsContained(ReadOnlySpan<double> fat, ReadOnlySpan<double> tight, int coordCount)
    {
        int half = coordCount / 2;
        for (int i = 0; i < half; i++) if (fat[i] > tight[i]) return false;
        for (int i = half; i < coordCount; i++) if (fat[i] < tight[i]) return false;
        return true;
    }
}
