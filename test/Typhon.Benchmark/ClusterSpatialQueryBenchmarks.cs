using System;
using System.Numerics;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace Typhon.Benchmark;

// ═══════════════════════════════════════════════════════════════════════════════════
// Issue #230 criterion 11 — Phase 3 regression benchmark for the per-cell cluster
// spatial query path.
//
// Original criterion 11 was "broadphase+narrowphase vs old per-entity R-Tree" but the
// Option B purge deleted the legacy per-entity tree, so the A/B premise is gone. This
// benchmark is the single-path reframing: absolute query cost for the surviving per-cell
// cluster index path across realistic entity counts and query box sizes.
//
// The smoke test previously scaffolded in PerCellRTreeTests.SmokeBench_ClusterQueryPath
// couldn't run at 10K+ entities because the default test-config page cache hits
// PageCacheBackpressureTimeoutException. This benchmark fixes that by sizing the cache
// generously (256K pages) and batching spawns with tick-fences in between.
// ═══════════════════════════════════════════════════════════════════════════════════

// ─── Dedicated component + archetype for the cluster query benchmark ───────────────
// Kept separate from the AaBenchSpatialUnit (3D, AABB3F) in ArchetypeAccessorBenchmark.cs
// because the query benchmark is simpler to reason about in 2D and doesn't need the
// archetype to also carry movement / meta components.

[Component("Typhon.Benchmark.ClQ.Pos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct ClQBenchPos
{
    [Field]
    [SpatialIndex(1.0f)]
    public AABB2F Bounds;
}

[Archetype(550)]
partial class ClQBenchUnit : Archetype<ClQBenchUnit>
{
    public static readonly Comp<ClQBenchPos> Pos = Register<ClQBenchPos>();
}

// ═══════════════════════════════════════════════════════════════════════════════════
// 1. AABB query cost across entity counts and query box sizes.
//
// World: 10_000 × 10_000, cell size 100 (→ 100 × 100 = 10_000 cells).
// Entities: uniform-random positions. Point bounds (min==max) to keep narrowphase
// deterministic.
//
// Query box variants:
//   Small   — 100×100  (~1 cell, ~1% of entities)
//   Medium  — 1000×1000 (~10×10 cells, ~1% of entities × 100 = ~100 hits at 10K)
//   Large   — 5000×5000 (~50×50 cells, ~25% of entities)
//   MissAll — outside the world, 0 hits (measures broadphase-only cost)
// ═══════════════════════════════════════════════════════════════════════════════════

[InProcess(true)]
[MemoryDiagnoser]
[JsonExporterAttribute.Full]
[BenchmarkCategory("ClusterSpatial", "Query")]
public class ClusterAabbQueryBenchmarks : IDisposable
{
    [Params(1_000, 10_000, 100_000)]
    public int EntityCount;

    private ServiceProvider _sp;
    private DatabaseEngine _dbe;

    private const float WorldSize = 10_000f;
    private const float CellSize = 100f;
    private const int SpawnBatchSize = 1_000;

    [GlobalSetup]
    public void Setup()
    {
        Archetype<ClQBenchUnit>.Touch();

        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
          .AddResourceRegistry()
          .AddMemoryAllocator()
          .AddEpochManager()
          .AddHighResolutionSharedTimer()
          .AddDeadlineWatchdog()
          .AddScopedManagedPagedMemoryMappedFile(o =>
          {
              // Sized for the 100K case. 200K pages × 8 KiB = 1.6 GiB — plenty of headroom while staying under the Int32 byte-size overflow
              // at 256K pages (2 GiB + 1 byte). Only a small fraction is actually dirty at a time thanks to tick-fence draining.
              o.DatabaseName = $"ClQBench_{EntityCount}_{Environment.ProcessId}";
              o.DatabaseCacheSize = (ulong)(200L * 1024 * PagedMMF.PageSize);
              o.TestMode = true;
              o.PagesDebugPattern = false;
          })
          .AddInMemoryWalEngine();

        _sp = sc.BuildServiceProvider();
        _sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
        _dbe = _sp.GetRequiredService<DatabaseEngine>();

        _dbe.RegisterComponentFromAccessor<ClQBenchPos>();

        // Issue #230 Option B: ConfigureSpatialGrid is required for cluster spatial archetypes.
        _dbe.ConfigureSpatialGrid(new SpatialGridConfig(
            worldMin: new Vector2(0, 0),
            worldMax: new Vector2(WorldSize, WorldSize),
            cellSize: CellSize));

        _dbe.InitializeArchetypes();

        // Spawn in batches with a tick fence between each to let the page cache drain dirty pages. Skipping the tick fence
        // would accumulate thousands of dirty pages at 10K+ entity counts and hit PageCacheBackpressureTimeoutException.
        var rng = new Random(42);
        int spawned = 0;
        long tickNum = 1;
        while (spawned < EntityCount)
        {
            int batch = Math.Min(SpawnBatchSize, EntityCount - spawned);
            using (var tx = _dbe.CreateQuickTransaction())
            {
                for (int i = 0; i < batch; i++)
                {
                    float x = (float)(rng.NextDouble() * WorldSize);
                    float y = (float)(rng.NextDouble() * WorldSize);
                    var pos = new ClQBenchPos { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y } };
                    tx.Spawn<ClQBenchUnit>(ClQBenchUnit.Pos.Set(in pos));
                }
                tx.Commit();
            }
            _dbe.WriteTickFence(tickNum++);
            spawned += batch;
        }

        // Final fence so RecomputeDirtyClusterAabbs runs on the last batch and the per-cell index is in steady state before benchmarking.
        _dbe.WriteTickFence(tickNum);
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        _dbe?.Dispose();
        _sp?.Dispose();
        _dbe = null;
        _sp = null;
        GC.SuppressFinalize(this);
    }

    // ─── Query benchmarks ──────────────────────────────────────────────
    // Each returns the hit count so BDN can't dead-code-eliminate the enumeration loop.

    [Benchmark]
    public int AABB_Small_2D()
    {
        // 100×100 centered at (5000, 5000) — overlaps a single cell.
        var box = new AABB2F { MinX = 4950, MinY = 4950, MaxX = 5050, MaxY = 5050 };
        int count = 0;
        using (EpochGuard.Enter(_dbe.EpochManager))
        {
            foreach (var _ in _dbe.ClusterSpatialQuery<ClQBenchUnit>().AABB<AABB2F>(in box))
            {
                count++;
            }
        }
        return count;
    }

    [Benchmark]
    public int AABB_Medium_2D()
    {
        // 1000×1000 centered at (5000, 5000) — overlaps ~10×10 cells.
        var box = new AABB2F { MinX = 4500, MinY = 4500, MaxX = 5500, MaxY = 5500 };
        int count = 0;
        using (EpochGuard.Enter(_dbe.EpochManager))
        {
            foreach (var _ in _dbe.ClusterSpatialQuery<ClQBenchUnit>().AABB<AABB2F>(in box))
            {
                count++;
            }
        }
        return count;
    }

    [Benchmark]
    public int AABB_Large_2D()
    {
        // 5000×5000 centered at (5000, 5000) — overlaps ~50×50 cells (quarter of the world).
        var box = new AABB2F { MinX = 2500, MinY = 2500, MaxX = 7500, MaxY = 7500 };
        int count = 0;
        using (EpochGuard.Enter(_dbe.EpochManager))
        {
            foreach (var _ in _dbe.ClusterSpatialQuery<ClQBenchUnit>().AABB<AABB2F>(in box))
            {
                count++;
            }
        }
        return count;
    }

    [Benchmark]
    public int AABB_MissAll_2D()
    {
        // Query box outside the world — tests broadphase-only cost (no cluster matches).
        var box = new AABB2F { MinX = 20_000, MinY = 20_000, MaxX = 20_100, MaxY = 20_100 };
        int count = 0;
        using (EpochGuard.Enter(_dbe.EpochManager))
        {
            foreach (var _ in _dbe.ClusterSpatialQuery<ClQBenchUnit>().AABB<AABB2F>(in box))
            {
                count++;
            }
        }
        return count;
    }
}

// ═══════════════════════════════════════════════════════════════════════════════════
// 2. Radius query cost across entity counts.
//
// Radius queries exercise both the broadphase (AABB-enclosed sphere overlap) and the
// narrowphase radius filter (closest-point-on-AABB distance check).
// ═══════════════════════════════════════════════════════════════════════════════════

[InProcess(true)]
[MemoryDiagnoser]
[JsonExporterAttribute.Full]
[BenchmarkCategory("ClusterSpatial", "Radius")]
public class ClusterRadiusQueryBenchmarks : IDisposable
{
    [Params(1_000, 10_000, 100_000)]
    public int EntityCount;

    private ServiceProvider _sp;
    private DatabaseEngine _dbe;

    private const float WorldSize = 10_000f;
    private const float CellSize = 100f;
    private const int SpawnBatchSize = 1_000;

    [GlobalSetup]
    public void Setup()
    {
        Archetype<ClQBenchUnit>.Touch();

        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
          .AddResourceRegistry()
          .AddMemoryAllocator()
          .AddEpochManager()
          .AddHighResolutionSharedTimer()
          .AddDeadlineWatchdog()
          .AddScopedManagedPagedMemoryMappedFile(o =>
          {
              o.DatabaseName = $"ClRBench_{EntityCount}_{Environment.ProcessId}";
              o.DatabaseCacheSize = (ulong)(200L * 1024 * PagedMMF.PageSize);
              o.TestMode = true;
              o.PagesDebugPattern = false;
          })
          .AddInMemoryWalEngine();

        _sp = sc.BuildServiceProvider();
        _sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
        _dbe = _sp.GetRequiredService<DatabaseEngine>();

        _dbe.RegisterComponentFromAccessor<ClQBenchPos>();
        _dbe.ConfigureSpatialGrid(new SpatialGridConfig(
            worldMin: new Vector2(0, 0),
            worldMax: new Vector2(WorldSize, WorldSize),
            cellSize: CellSize));
        _dbe.InitializeArchetypes();

        var rng = new Random(42);
        int spawned = 0;
        long tickNum = 1;
        while (spawned < EntityCount)
        {
            int batch = Math.Min(SpawnBatchSize, EntityCount - spawned);
            using (var tx = _dbe.CreateQuickTransaction())
            {
                for (int i = 0; i < batch; i++)
                {
                    float x = (float)(rng.NextDouble() * WorldSize);
                    float y = (float)(rng.NextDouble() * WorldSize);
                    var pos = new ClQBenchPos { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y } };
                    tx.Spawn<ClQBenchUnit>(ClQBenchUnit.Pos.Set(in pos));
                }
                tx.Commit();
            }
            _dbe.WriteTickFence(tickNum++);
            spawned += batch;
        }
        _dbe.WriteTickFence(tickNum);
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        _dbe?.Dispose();
        _sp?.Dispose();
        _dbe = null;
        _sp = null;
        GC.SuppressFinalize(this);
    }

    // Radius queries route through EcsQuery.WhereNearby<T> which internally calls ArchetypeClusterState.QueryRadius.

    [Benchmark]
    public int Radius_Small_2D()
    {
        // radius=50 at (5000, 5000) — single cell footprint.
        int count = 0;
        using (var tx = _dbe.CreateQuickTransaction())
        {
            var results = tx.Query<ClQBenchUnit>().WhereNearby<ClQBenchPos>(5000, 5000, 0, 50).Execute();
            count = results.Count;
        }
        return count;
    }

    [Benchmark]
    public int Radius_Medium_2D()
    {
        // radius=500 at (5000, 5000) — ~10×10 cell footprint.
        int count = 0;
        using (var tx = _dbe.CreateQuickTransaction())
        {
            var results = tx.Query<ClQBenchUnit>().WhereNearby<ClQBenchPos>(5000, 5000, 0, 500).Execute();
            count = results.Count;
        }
        return count;
    }

    [Benchmark]
    public int Radius_Large_2D()
    {
        // radius=2500 at (5000, 5000) — quarter of the world.
        int count = 0;
        using (var tx = _dbe.CreateQuickTransaction())
        {
            var results = tx.Query<ClQBenchUnit>().WhereNearby<ClQBenchPos>(5000, 5000, 0, 2500).Execute();
            count = results.Count;
        }
        return count;
    }
}
