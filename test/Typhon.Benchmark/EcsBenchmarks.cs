using System;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace Typhon.Benchmark;

// ═══════════════════════════════════════════════════════════════════════
// ECS benchmark types — cascade, polymorphic query, enable/disable
// ═══════════════════════════════════════════════════════════════════════

[Component("Typhon.Benchmark.ECS.ParentData", 1)]
[StructLayout(LayoutKind.Sequential)]
struct BenchParentData
{
    [Field] public int Category;
    [Field] public int Value;
}

[Component("Typhon.Benchmark.ECS.ChildData", 1)]
[StructLayout(LayoutKind.Sequential)]
struct BenchChildData
{
    [Index(AllowMultiple = true, OnParentDelete = CascadeAction.Delete)]
    public EntityLink<BenchParentArch> Owner;
    [Field] public int Weight;
}

[Component("Typhon.Benchmark.ECS.VehicleData", 1)]
[StructLayout(LayoutKind.Sequential)]
struct BenchVehicleData
{
    [Index(AllowMultiple = true)]
    public int Speed;
    [Field] public int Fuel;
}

[Component("Typhon.Benchmark.ECS.CarData", 1)]
[StructLayout(LayoutKind.Sequential)]
struct BenchCarData
{
    [Field] public int Doors;
    [Field] public int _pad;
}

[Archetype(510)]
class BenchParentArch : Archetype<BenchParentArch>
{
    public static readonly Comp<BenchParentData> Parent = Register<BenchParentData>();
}

[Archetype(511)]
class BenchChildArch : Archetype<BenchChildArch>
{
    public static readonly Comp<BenchChildData> Child = Register<BenchChildData>();
}

[Archetype(512)]
partial class BenchVehicleArch : Archetype<BenchVehicleArch>
{
    public static readonly Comp<BenchVehicleData> Vehicle = Register<BenchVehicleData>();
}

[Archetype(513)]
class BenchCarArch : Archetype<BenchCarArch, BenchVehicleArch>
{
    public static readonly Comp<BenchCarData> Car = Register<BenchCarData>();
}

// ═══════════════════════════════════════════════════════════════════════
// B2. Cascade delete at scale
// ═══════════════════════════════════════════════════════════════════════

[SimpleJob(warmupCount: 2, iterationCount: 5)]
[MemoryDiagnoser]
[BenchmarkCategory("ECS", "Regression")]
public class CascadeDeleteBenchmarks : IDisposable
{
    private ServiceProvider _serviceProvider;
    private DatabaseEngine _dbe;

    [Params(10, 100)]
    public int ParentCount;

    [Params(10, 100)]
    public int ChildrenPerParent;

    private EntityId[] _parentIds;

    [GlobalSetup]
    public void Setup()
    {
        Archetype<BenchParentArch>.Touch();
        Archetype<BenchChildArch>.Touch();

        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
          .AddResourceRegistry()
          .AddMemoryAllocator()
          .AddEpochManager()
          .AddHighResolutionSharedTimer()
          .AddDeadlineWatchdog()
          .AddScopedManagedPagedMemoryMappedFile(options =>
          {
              options.DatabaseName = $"CascadeBench_{Environment.ProcessId}";
              options.DatabaseCacheSize = (ulong)(64 * 1024 * PagedMMF.PageSize);
              options.PagesDebugPattern = false;
          })
          .AddScopedDatabaseEngine();

        _serviceProvider = sc.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        _dbe = _serviceProvider.GetRequiredService<DatabaseEngine>();
        _dbe.RegisterComponentFromAccessor<BenchParentData>();
        _dbe.RegisterComponentFromAccessor<BenchChildData>();
        _dbe.InitializeArchetypes();

        // Populate parents + children
        _parentIds = new EntityId[ParentCount];
        using var t = _dbe.CreateQuickTransaction();
        for (int p = 0; p < ParentCount; p++)
        {
            var pd = new BenchParentData { Category = p, Value = p * 100 };
            _parentIds[p] = t.Spawn<BenchParentArch>(BenchParentArch.Parent.Set(in pd));

            for (int c = 0; c < ChildrenPerParent; c++)
            {
                var cd = new BenchChildData { Owner = _parentIds[p], Weight = c };
                t.Spawn<BenchChildArch>(BenchChildArch.Child.Set(in cd));
            }
        }
        t.Commit();
    }

    [Benchmark]
    public void CascadeDeleteAll()
    {
        using var t = _dbe.CreateQuickTransaction();
        for (int i = 0; i < _parentIds.Length; i++)
        {
            if (t.IsAlive(_parentIds[i]))
            {
                t.Destroy(_parentIds[i]);
            }
        }
        t.Commit();
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        _dbe?.Dispose();
        _serviceProvider?.Dispose();
        GC.SuppressFinalize(this);
    }
}

// ═══════════════════════════════════════════════════════════════════════
// B3. Polymorphic query + B4. Count/Any + B5. Enable/Disable
// ═══════════════════════════════════════════════════════════════════════

[SimpleJob(warmupCount: 3, iterationCount: 5)]
[MemoryDiagnoser]
[BenchmarkCategory("ECS", "Regression")]
public class EcsQueryBenchmarks : IDisposable
{
    private ServiceProvider _serviceProvider;
    private DatabaseEngine _dbe;

    private const int VehicleCount = 5_000;
    private const int CarCount = 5_000;
    private EntityId[] _allIds;

    [GlobalSetup]
    public void Setup()
    {
        Archetype<BenchVehicleArch>.Touch();
        Archetype<BenchCarArch>.Touch();

        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
          .AddResourceRegistry()
          .AddMemoryAllocator()
          .AddEpochManager()
          .AddHighResolutionSharedTimer()
          .AddDeadlineWatchdog()
          .AddScopedManagedPagedMemoryMappedFile(options =>
          {
              options.DatabaseName = $"EcsQueryBench_{Environment.ProcessId}";
              options.DatabaseCacheSize = (ulong)(64 * 1024 * PagedMMF.PageSize);
              options.PagesDebugPattern = false;
          })
          .AddScopedDatabaseEngine();

        _serviceProvider = sc.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        _dbe = _serviceProvider.GetRequiredService<DatabaseEngine>();
        _dbe.RegisterComponentFromAccessor<BenchVehicleData>();
        _dbe.RegisterComponentFromAccessor<BenchCarData>();
        _dbe.InitializeArchetypes();

        _allIds = new EntityId[VehicleCount + CarCount];
        using var t = _dbe.CreateQuickTransaction();

        for (int i = 0; i < VehicleCount; i++)
        {
            var v = new BenchVehicleData { Speed = i % 200, Fuel = 100 };
            _allIds[i] = t.Spawn<BenchVehicleArch>(BenchVehicleArch.Vehicle.Set(in v));
        }

        for (int i = 0; i < CarCount; i++)
        {
            var v = new BenchVehicleData { Speed = 100 + i % 200, Fuel = 80 };
            var c = new BenchCarData { Doors = 4 };
            _allIds[VehicleCount + i] = t.Spawn<BenchCarArch>(BenchVehicleArch.Vehicle.Set(in v), BenchCarArch.Car.Set(in c));
        }
        t.Commit();

        // Disable Vehicle on first 1000 entities for Enable/Disable benchmarks
        using var t2 = _dbe.CreateQuickTransaction();
        for (int i = 0; i < 1000; i++)
        {
            var entity = t2.OpenMut(_allIds[i]);
            entity.Disable(BenchVehicleArch.Vehicle);
        }
        t2.Commit();
    }

    // B3: Polymorphic query scan
    [Benchmark]
    public int PolymorphicQuery_Count()
    {
        using var t = _dbe.CreateQuickTransaction();
        return t.Query<BenchVehicleArch>().Count();
    }

    [Benchmark]
    public int ExactQuery_Count()
    {
        using var t = _dbe.CreateQuickTransaction();
        return t.QueryExact<BenchVehicleArch>().Count();
    }

    // B4: Count/Any short-circuit
    [Benchmark]
    public bool Query_Any()
    {
        using var t = _dbe.CreateQuickTransaction();
        return t.Query<BenchVehicleArch>().Any();
    }

    [Benchmark]
    public int WhereField_Count()
    {
        using var t = _dbe.CreateQuickTransaction();
        return t.Query<BenchVehicleArch>().WhereField<BenchVehicleData>(v => v.Speed > 150).Count();
    }

    // B5: Enable/Disable throughput
    [Benchmark]
    public int Enabled_Query_Count()
    {
        using var t = _dbe.CreateQuickTransaction();
        return t.Query<BenchVehicleArch>().Enabled<BenchVehicleData>().Count();
    }

    [Benchmark]
    public void EnableDisable_1000()
    {
        using var t = _dbe.CreateQuickTransaction();
        for (int i = 0; i < 1000; i++)
        {
            var entity = t.OpenMut(_allIds[i]);
            entity.Enable(BenchVehicleArch.Vehicle);
        }
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        _dbe?.Dispose();
        _serviceProvider?.Dispose();
        GC.SuppressFinalize(this);
    }
}

// ═══════════════════════════════════════════════════════════════════════
// B1. SpawnBatch: loop vs shared vs SOA
// ═══════════════════════════════════════════════════════════════════════

// iterationCount normalized 10→5 to match the rest of the Regression suite (was an anomalous outlier and the
// 2nd-slowest group). 5 iterations remain statistically ample here. (#422 benchmark-runtime tuning.)
[SimpleJob(warmupCount: 3, iterationCount: 5)]
[MemoryDiagnoser]
[BenchmarkCategory("ECS", "Regression")]
public class SpawnBatchBenchmarks : IDisposable
{
    private ServiceProvider _serviceProvider;
    private DatabaseEngine _dbe;

    [Params(100, 1000)]
    public int EntityCount;

    [GlobalSetup]
    public void Setup()
    {
        Archetype<BenchVehicleArch>.Touch();

        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
          .AddResourceRegistry()
          .AddMemoryAllocator()
          .AddEpochManager()
          .AddHighResolutionSharedTimer()
          .AddDeadlineWatchdog()
          .AddScopedManagedPagedMemoryMappedFile(options =>
          {
              options.DatabaseName = $"SpawnBatchBench_{Environment.ProcessId}";
              // Large cache: spawn benchmarks allocate chunks on every invocation
              // without checkpoint to reclaim dirty pages — 128K pages (1 GB) prevents back-pressure.
              options.DatabaseCacheSize = (ulong)(128 * 1024 * PagedMMF.PageSize);
              options.PagesDebugPattern = false;
          })
          .AddScopedDatabaseEngine();

        _serviceProvider = sc.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        _dbe = _serviceProvider.GetRequiredService<DatabaseEngine>();
        _dbe.RegisterComponentFromAccessor<BenchVehicleData>();
        _dbe.InitializeArchetypes();
    }

    [Benchmark(Baseline = true)]
    public void SingleSpawnLoop()
    {
        using var t = _dbe.CreateQuickTransaction();
        for (int i = 0; i < EntityCount; i++)
        {
            var v = new BenchVehicleData { Speed = i % 200, Fuel = 100 };
            t.Spawn<BenchVehicleArch>(BenchVehicleArch.Vehicle.Set(in v));
        }
    }

    [Benchmark]
    public void SpawnBatch_SharedValues()
    {
        using var t = _dbe.CreateQuickTransaction();
        var v = new BenchVehicleData { Speed = 100, Fuel = 100 };
        var ids = new EntityId[EntityCount];
        t.SpawnBatch<BenchVehicleArch>(ids, BenchVehicleArch.Vehicle.Set(in v));
    }

    [Benchmark]
    public void SpawnBatch_SOA()
    {
        using var t = _dbe.CreateQuickTransaction();
        var vehicles = new BenchVehicleData[EntityCount];
        for (int i = 0; i < EntityCount; i++)
        {
            vehicles[i] = new BenchVehicleData { Speed = i % 200, Fuel = 100 };
        }
        BenchVehicleArch.SpawnBatch(t, vehicles);
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        _dbe?.Dispose();
        _serviceProvider?.Dispose();
        GC.SuppressFinalize(this);
    }
}
