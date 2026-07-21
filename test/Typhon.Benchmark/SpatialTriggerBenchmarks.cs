using System;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Typhon.Schema.Definition;

namespace Typhon.Benchmark;

// ═══════════════════════════════════════════════════════════════════════
// Trigger Volume benchmark types
// ═══════════════════════════════════════════════════════════════════════

static class TriggerBenchHelper
{
    /// <summary>Spawn entities in batches of 500 to avoid CBS segment overflow in a single transaction.</summary>
    internal static EntityId[] SpawnShips(DatabaseEngine dbe, int count, Func<int, (float x, float y)> positionFunc)
    {
        var ids = new EntityId[count];
        const int batchSize = 500;
        for (int batch = 0; batch < count; batch += batchSize)
        {
            int end = Math.Min(batch + batchSize, count);
            using var t = dbe.CreateQuickTransaction();
            for (int i = batch; i < end; i++)
            {
                var (x, y) = positionFunc(i);
                var ship = new TrigShip { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x + 2, MaxY = y + 2 } };
                ids[i] = t.Spawn<TrigShipArch>(TrigShipArch.Ship.Set(in ship));
            }
            t.Commit();
        }
        return ids;
    }

    internal static void SpawnTerrain(DatabaseEngine dbe, int count, Random rng)
    {
        const int batchSize = 500;
        for (int batch = 0; batch < count; batch += batchSize)
        {
            int end = Math.Min(batch + batchSize, count);
            using var t = dbe.CreateQuickTransaction();
            for (int i = batch; i < end; i++)
            {
                float x = (float)(rng.NextDouble() * 10000);
                float y = (float)(rng.NextDouble() * 10000);
                var terrain = new TrigTerrain { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x + 10, MaxY = y + 10 } };
                t.Spawn<TrigTerrainArch>(TrigTerrainArch.Terrain.Set(in terrain));
            }
            t.Commit();
        }
    }
}

[Component("Typhon.Benchmark.TrigVol.Ship", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct TrigShip
{
    [Field] [SpatialIndex(5.0f)]
    public AABB2F Bounds;
}

[Component("Typhon.Benchmark.TrigVol.Terrain", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct TrigTerrain
{
    [Field] [SpatialIndex(0.0f, Mode = SpatialMode.Static)]
    public AABB2F Bounds;
}

[Archetype]
partial class TrigShipArch : Archetype<TrigShipArch>
{
    public static readonly Comp<TrigShip> Ship = Register<TrigShip>();
}

[Archetype]
partial class TrigTerrainArch : Archetype<TrigTerrainArch>
{
    public static readonly Comp<TrigTerrain> Terrain = Register<TrigTerrain>();
}

// ═══════════════════════════════════════════════════════════════════════
// 1. EvaluateRegion — varying occupant density
//    Fixed 10K entities in tree, one region, vary how many are inside.
// ═══════════════════════════════════════════════════════════════════════

[InProcess(true)]
[MemoryDiagnoser]
[JsonExporterAttribute.Full]
[BenchmarkCategory("SpatialTrigger", "OccupantDensity")]
public class TriggerOccupantDensityBenchmarks : IDisposable
{
    private ServiceProvider _sp;
    private DatabaseEngine _dbe;
    private SpatialTriggerSystem _ts;
    private SpatialRegionHandle _handle;
    private int _tick;

    [Params(10, 50, 200, 1000)]
    public int OccupantCount;

    private const int TotalEntities = 10_000;
    private const double WorldSize = 10000.0;

    [GlobalSetup]
    public void Setup()
    {
        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
            .AddResourceRegistry().AddMemoryAllocator().AddEpochManager()
            .AddHighResolutionSharedTimer().AddDeadlineWatchdog()
            .AddScopedManagedPagedMemoryMappedFile(o =>
            {
                o.DatabaseName = $"TrigDensity{OccupantCount}";
                o.DatabaseCacheSize = (ulong)(128L * 1024 * PagedMMF.PageSize);
                o.TestMode = true;
                o.PagesDebugPattern = false;
            })
            .AddScopedDatabaseEngine();
        _sp = sc.BuildServiceProvider();
        _sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
        _dbe = _sp.GetRequiredService<DatabaseEngine>();
        _dbe.RegisterComponentFromAccessor<TrigShip>();
        _dbe.InitializeArchetypes();

        // Spawn entities: first OccupantCount in region [0,0 → 100,100], rest scattered
        var rng = new Random(42);
        var occCount = OccupantCount;
        TriggerBenchHelper.SpawnShips(_dbe, TotalEntities, i =>
        {
            if (i < occCount)
                return ((float)(rng.NextDouble() * 90 + 1), (float)(rng.NextDouble() * 90 + 1));
            return ((float)(200 + rng.NextDouble() * (WorldSize - 200)), (float)(200 + rng.NextDouble() * (WorldSize - 200)));
        });

        var table = _dbe.GetComponentTable<TrigShip>();
        _ts = table.SpatialIndex.GetOrCreateTriggerSystem(table);
        _handle = _ts.CreateRegion(stackalloc double[] { -5, -5, 105, 105 });

        // Prime: first evaluation populates previous state
        _ts.EvaluateRegion(_handle, 0);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _ts = null;
        _dbe?.Dispose(); _sp?.Dispose();
    }

    [Benchmark]
    public SpatialTriggerResult Evaluate_VaryingOccupants()
    {
        // Steady-state eval (entities haven't moved → all stays, zero enter/leave)
        return _ts.EvaluateRegion(_handle, _tick++);
    }

    public void Dispose() { _dbe?.Dispose(); _sp?.Dispose(); }
}

// ═══════════════════════════════════════════════════════════════════════
// 2. Steady state — no changes (the common case)
// ═══════════════════════════════════════════════════════════════════════

[InProcess(true)]
[MemoryDiagnoser]
[JsonExporterAttribute.Full]
[BenchmarkCategory("SpatialTrigger", "SteadyState")]
public class TriggerSteadyStateBenchmarks : IDisposable
{
    private ServiceProvider _sp;
    private DatabaseEngine _dbe;
    private SpatialTriggerSystem _ts;
    private SpatialRegionHandle _handle;
    private int _tick;

    [GlobalSetup]
    public void Setup()
    {
        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
            .AddResourceRegistry().AddMemoryAllocator().AddEpochManager()
            .AddHighResolutionSharedTimer().AddDeadlineWatchdog()
            .AddScopedManagedPagedMemoryMappedFile(o =>
            {
                o.DatabaseName = "TrigSteady";
                o.DatabaseCacheSize = (ulong)(128L * 1024 * PagedMMF.PageSize);
                o.TestMode = true;
                o.PagesDebugPattern = false;
            })
            .AddScopedDatabaseEngine();
        _sp = sc.BuildServiceProvider();
        _sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
        _dbe = _sp.GetRequiredService<DatabaseEngine>();
        _dbe.RegisterComponentFromAccessor<TrigShip>();
        _dbe.InitializeArchetypes();

        var rng = new Random(42);
        TriggerBenchHelper.SpawnShips(_dbe, 10_000, _ =>
            ((float)(rng.NextDouble() * 10000), (float)(rng.NextDouble() * 10000)));

        var table = _dbe.GetComponentTable<TrigShip>();
        _ts = table.SpatialIndex.GetOrCreateTriggerSystem(table);
        _handle = _ts.CreateRegion(stackalloc double[] { 4500, 4500, 5500, 5500 }); // ~100 occupants in center
        _ts.EvaluateRegion(_handle, 0); // prime
        _tick = 1;
    }

    [GlobalCleanup]
    public void Cleanup() { _ts = null; _dbe?.Dispose(); _sp?.Dispose(); }

    [Benchmark]
    public SpatialTriggerResult SteadyState_NoChanges()
    {
        return _ts.EvaluateRegion(_handle, _tick++);
    }

    public void Dispose() { _dbe?.Dispose(); _sp?.Dispose(); }
}

// ═══════════════════════════════════════════════════════════════════════
// 3. Scaling with tree size
//    ~50 occupants, vary total tree size (1K / 10K / 50K / 100K)
// ═══════════════════════════════════════════════════════════════════════

[InProcess(true)]
[MemoryDiagnoser]
[JsonExporterAttribute.Full]
[BenchmarkCategory("SpatialTrigger", "TreeScale")]
public class TriggerTreeScaleBenchmarks : IDisposable
{
    private ServiceProvider _sp;
    private DatabaseEngine _dbe;
    private SpatialTriggerSystem _ts;
    private SpatialRegionHandle _handle;
    private int _tick;

    [Params(1_000, 10_000, 50_000)]
    public int TotalEntities;

    private const int TargetOccupants = 50;

    [GlobalSetup]
    public void Setup()
    {
        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
            .AddResourceRegistry().AddMemoryAllocator().AddEpochManager()
            .AddHighResolutionSharedTimer().AddDeadlineWatchdog()
            .AddScopedManagedPagedMemoryMappedFile(o =>
            {
                o.DatabaseName = $"TrigScale{TotalEntities}";
                o.DatabaseCacheSize = (ulong)(128L * 1024 * PagedMMF.PageSize);
                o.TestMode = true;
                o.PagesDebugPattern = false;
            })
            .AddScopedDatabaseEngine();
        _sp = sc.BuildServiceProvider();
        _sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
        _dbe = _sp.GetRequiredService<DatabaseEngine>();
        _dbe.RegisterComponentFromAccessor<TrigShip>();
        _dbe.InitializeArchetypes();

        var rng = new Random(42);
        TriggerBenchHelper.SpawnShips(_dbe, TotalEntities, i =>
        {
            if (i < TargetOccupants)
                return ((float)(rng.NextDouble() * 90 + 5), (float)(rng.NextDouble() * 90 + 5));
            return ((float)(200 + rng.NextDouble() * 9800), (float)(200 + rng.NextDouble() * 9800));
        });

        var table = _dbe.GetComponentTable<TrigShip>();
        _ts = table.SpatialIndex.GetOrCreateTriggerSystem(table);
        _handle = _ts.CreateRegion(stackalloc double[] { -5, -5, 105, 105 });
        _ts.EvaluateRegion(_handle, 0);
    }

    [GlobalCleanup]
    public void Cleanup() { _ts = null; _dbe?.Dispose(); _sp?.Dispose(); }

    [Benchmark]
    public SpatialTriggerResult Evaluate_TreeScale() => _ts.EvaluateRegion(_handle, _tick++);

    public void Dispose() { _dbe?.Dispose(); _sp?.Dispose(); }
}

// ═══════════════════════════════════════════════════════════════════════
// 4. Multi-region batch evaluation
//    10 / 50 / 100 regions, each with ~50 occupants
// ═══════════════════════════════════════════════════════════════════════

[InProcess(true)]
[MemoryDiagnoser]
[JsonExporterAttribute.Full]
[BenchmarkCategory("SpatialTrigger", "MultiRegion")]
public class TriggerMultiRegionBenchmarks : IDisposable
{
    private ServiceProvider _sp;
    private DatabaseEngine _dbe;
    private SpatialTriggerSystem _ts;
    private SpatialRegionHandle[] _handles;
    private int _tick;

    [Params(10, 50, 100)]
    public int RegionCount;

    [GlobalSetup]
    public void Setup()
    {
        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
            .AddResourceRegistry().AddMemoryAllocator().AddEpochManager()
            .AddHighResolutionSharedTimer().AddDeadlineWatchdog()
            .AddScopedManagedPagedMemoryMappedFile(o =>
            {
                o.DatabaseName = $"TrigMulti{RegionCount}";
                o.DatabaseCacheSize = (ulong)(128L * 1024 * PagedMMF.PageSize);
                o.TestMode = true;
                o.PagesDebugPattern = false;
            })
            .AddScopedDatabaseEngine();
        _sp = sc.BuildServiceProvider();
        _sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
        _dbe = _sp.GetRequiredService<DatabaseEngine>();
        _dbe.RegisterComponentFromAccessor<TrigShip>();
        _dbe.InitializeArchetypes();

        // Spawn 10K entities spread across a grid
        var rng = new Random(42);
        TriggerBenchHelper.SpawnShips(_dbe, 10_000, _ =>
            ((float)(rng.NextDouble() * 10000), (float)(rng.NextDouble() * 10000)));

        var table = _dbe.GetComponentTable<TrigShip>();
        _ts = table.SpatialIndex.GetOrCreateTriggerSystem(table);

        // Create regions in a grid pattern (each ~100×100 covers ~1% of world → ~100 entities)
        _handles = new SpatialRegionHandle[RegionCount];
        int cols = (int)Math.Ceiling(Math.Sqrt(RegionCount));
        double regionSize = 10000.0 / cols;
        for (int i = 0; i < RegionCount; i++)
        {
            int row = i / cols, col = i % cols;
            double x = col * regionSize, y = row * regionSize;
            _handles[i] = _ts.CreateRegion(stackalloc double[] { x, y, x + regionSize, y + regionSize });
        }

        // Prime all regions
        for (int i = 0; i < RegionCount; i++)
        {
            _ts.EvaluateRegion(_handles[i], 0);
        }
    }

    [GlobalCleanup]
    public void Cleanup() { _ts = null; _dbe?.Dispose(); _sp?.Dispose(); }

    [Benchmark]
    public int EvaluateAll_Batch()
    {
        int tick = _tick++;
        int totalStay = 0;
        for (int i = 0; i < _handles.Length; i++)
        {
            var r = _ts.EvaluateRegion(_handles[i], tick);
            totalStay += r.StayCount;
        }
        return totalStay;
    }

    public void Dispose() { _dbe?.Dispose(); _sp?.Dispose(); }
}

// ═══════════════════════════════════════════════════════════════════════
// 5. Static cache hit vs miss
//    Region with TargetTree=Both, 1K static entities
// ═══════════════════════════════════════════════════════════════════════

[InProcess(true)]
[MemoryDiagnoser]
[JsonExporterAttribute.Full]
[BenchmarkCategory("SpatialTrigger", "StaticCache")]
public class TriggerStaticCacheBenchmarks : IDisposable
{
    private ServiceProvider _sp;
    private DatabaseEngine _dbe;
    private SpatialTriggerSystem _ts;
    private SpatialRegionHandle _handle;
    private int _tick;

    [GlobalSetup]
    public void Setup()
    {
        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
            .AddResourceRegistry().AddMemoryAllocator().AddEpochManager()
            .AddHighResolutionSharedTimer().AddDeadlineWatchdog()
            .AddScopedManagedPagedMemoryMappedFile(o =>
            {
                o.DatabaseName = "TrigStatic";
                o.DatabaseCacheSize = (ulong)(128L * 1024 * PagedMMF.PageSize);
                o.TestMode = true;
                o.PagesDebugPattern = false;
            })
            .AddScopedDatabaseEngine();
        _sp = sc.BuildServiceProvider();
        _sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
        _dbe = _sp.GetRequiredService<DatabaseEngine>();
        _dbe.RegisterComponentFromAccessor<TrigTerrain>();
        _dbe.InitializeArchetypes();

        var rng = new Random(42);
        TriggerBenchHelper.SpawnTerrain(_dbe, 1_000, rng);

        var table = _dbe.GetComponentTable<TrigTerrain>();
        _ts = table.SpatialIndex.GetOrCreateTriggerSystem(table);
        _handle = _ts.CreateRegion(stackalloc double[] { 4000, 4000, 6000, 6000 }, targetTree: TargetTreeMode.Both);
        _tick = 0;
    }

    [GlobalCleanup]
    public void Cleanup() { _ts = null; _dbe?.Dispose(); _sp?.Dispose(); }

    [Benchmark(Baseline = true)]
    public SpatialTriggerResult CacheMiss_FirstEval()
    {
        // Cache miss: must query static tree
        return _ts.EvaluateRegion(_handle, _tick++);
    }

    [Benchmark]
    public SpatialTriggerResult CacheHit_SecondEval()
    {
        // Prime (cache miss)
        _ts.EvaluateRegion(_handle, _tick++);
        // Now measure cache hit
        return _ts.EvaluateRegion(_handle, _tick++);
    }

    public void Dispose() { _dbe?.Dispose(); _sp?.Dispose(); }
}

// ═══════════════════════════════════════════════════════════════════════
// 6. Enter/Leave storm — 20% entity churn per tick
// ═══════════════════════════════════════════════════════════════════════

[InProcess(true)]
[MemoryDiagnoser]
[JsonExporterAttribute.Full]
[BenchmarkCategory("SpatialTrigger", "EnterLeaveStorm")]
public class TriggerEnterLeaveStormBenchmarks : IDisposable
{
    private ServiceProvider _sp;
    private DatabaseEngine _dbe;
    private SpatialTriggerSystem _ts;
    private SpatialRegionHandle _handle;
    private EntityId[] _entityIds;
    private Random _rng;

    private const int TotalEntities = 5_000;
    private const int ChurnPercent = 20;

    [GlobalSetup]
    public void Setup()
    {
        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
            .AddResourceRegistry().AddMemoryAllocator().AddEpochManager()
            .AddHighResolutionSharedTimer().AddDeadlineWatchdog()
            .AddScopedManagedPagedMemoryMappedFile(o =>
            {
                o.DatabaseName = "TrigStorm";
                o.DatabaseCacheSize = (ulong)(128L * 1024 * PagedMMF.PageSize);
                o.TestMode = true;
                o.PagesDebugPattern = false;
            })
            .AddScopedDatabaseEngine();
        _sp = sc.BuildServiceProvider();
        _sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
        _dbe = _sp.GetRequiredService<DatabaseEngine>();
        _dbe.RegisterComponentFromAccessor<TrigShip>();
        _dbe.InitializeArchetypes();

        // Region covers center 40% of world
        // Spawn all entities randomly — ~40% will be inside
        _rng = new Random(42);
        _entityIds = TriggerBenchHelper.SpawnShips(_dbe, TotalEntities, _ =>
            ((float)(_rng.NextDouble() * 10000), (float)(_rng.NextDouble() * 10000)));

        var table = _dbe.GetComponentTable<TrigShip>();
        _ts = table.SpatialIndex.GetOrCreateTriggerSystem(table);
        _handle = _ts.CreateRegion(stackalloc double[] { 3000, 3000, 7000, 7000 });
        _ts.EvaluateRegion(_handle, 0); // prime
    }

    [GlobalCleanup]
    public void Cleanup() { _ts = null; _dbe?.Dispose(); _sp?.Dispose(); }

    [Benchmark]
    public SpatialTriggerResult EvaluateAfterChurn()
    {
        // Simulate churn: destroy 20% and respawn at new positions
        int churnCount = TotalEntities * ChurnPercent / 100;
        using (var t = _dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < churnCount; i++)
            {
                int idx = _rng.Next(TotalEntities);
                if (!_entityIds[idx].IsNull)
                {
                    t.Destroy(_entityIds[idx]);
                    float x = (float)(_rng.NextDouble() * 10000);
                    float y = (float)(_rng.NextDouble() * 10000);
                    var ship = new TrigShip { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x + 2, MaxY = y + 2 } };
                    _entityIds[idx] = t.Spawn<TrigShipArch>(TrigShipArch.Ship.Set(in ship));
                }
            }
            t.Commit();
        }

        return _ts.EvaluateRegion(_handle, 1);
    }

    public void Dispose() { _dbe?.Dispose(); _sp?.Dispose(); }
}

// ═══════════════════════════════════════════════════════════════════════
// 7. Frequency gating skip — 100 regions, non-matching tick
// ═══════════════════════════════════════════════════════════════════════

[SimpleJob(warmupCount: 5, iterationCount: 20)]
[MemoryDiagnoser]
[JsonExporterAttribute.Full]
[BenchmarkCategory("SpatialTrigger", "FrequencySkip")]
public class TriggerFrequencySkipBenchmarks : IDisposable
{
    private ServiceProvider _sp;
    private DatabaseEngine _dbe;
    private SpatialTriggerSystem _ts;
    private SpatialRegionHandle[] _handles;

    private const int RegionCount = 100;

    [GlobalSetup]
    public void Setup()
    {
        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
            .AddResourceRegistry().AddMemoryAllocator().AddEpochManager()
            .AddHighResolutionSharedTimer().AddDeadlineWatchdog()
            .AddScopedManagedPagedMemoryMappedFile(o =>
            {
                o.DatabaseName = "TrigFreq";
                o.DatabaseCacheSize = (ulong)(128L * 1024 * PagedMMF.PageSize);
                o.TestMode = true;
                o.PagesDebugPattern = false;
            })
            .AddScopedDatabaseEngine();
        _sp = sc.BuildServiceProvider();
        _sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
        _dbe = _sp.GetRequiredService<DatabaseEngine>();
        _dbe.RegisterComponentFromAccessor<TrigShip>();
        _dbe.InitializeArchetypes();

        // Spawn a few entities (not relevant — we're measuring skip cost)
        TriggerBenchHelper.SpawnShips(_dbe, 100, i =>
            (i * 10f, 0f));

        var table = _dbe.GetComponentTable<TrigShip>();
        _ts = table.SpatialIndex.GetOrCreateTriggerSystem(table);

        _handles = new SpatialRegionHandle[RegionCount];
        for (int i = 0; i < RegionCount; i++)
        {
            _handles[i] = _ts.CreateRegion(stackalloc double[] { 0, 0, 10000, 10000 }, evaluationFrequency: 10);
        }

        // Prime all at tick 0
        for (int i = 0; i < RegionCount; i++)
        {
            _ts.EvaluateRegion(_handles[i], 0);
        }
    }

    [GlobalCleanup]
    public void Cleanup() { _ts = null; _dbe?.Dispose(); _sp?.Dispose(); }

    [Benchmark]
    public int Skip_100Regions_NonMatchingTick()
    {
        int skipped = 0;
        // Tick 1 — only 1 tick after last eval (0), frequency=10 → all skip
        for (int i = 0; i < RegionCount; i++)
        {
            var r = _ts.EvaluateRegion(_handles[i], 1);
            if (!r.WasEvaluated)
            {
                skipped++;
            }
        }
        return skipped;
    }

    public void Dispose() { _dbe?.Dispose(); _sp?.Dispose(); }
}
