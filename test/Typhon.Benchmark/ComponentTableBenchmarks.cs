using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace Typhon.Benchmark;

// ═══════════════════════════════════════════════════════════════════════
// Data: ComponentTable / Entity ECS Microbenchmarks
// ═══════════════════════════════════════════════════════════════════════

[Component("Typhon.Benchmark.DataBenchComp", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct DataBenchComp
{
    [Field]
    public int Value;

    [Field]
    public long Timestamp;
}

[Archetype]
class DataBenchArch : Archetype<DataBenchArch>
{
    public static readonly Comp<DataBenchComp> Data = Register<DataBenchComp>();
}

[SimpleJob(warmupCount: 2, iterationCount: 3)]
[MemoryDiagnoser]
[BenchmarkCategory("Data", "Regression")]
public class ComponentTableBenchmarks
{
    private ServiceProvider _serviceProvider;
    private DatabaseEngine _dbe;
    private EntityId[] _entityIds;
    private string _databaseName;

    private const int PrePopulateCount = 500;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _databaseName = $"CompTableBench_{Environment.ProcessId}";

        var dcs = 200 * 1024;
        dcs *= PagedMMF.PageSize;

        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
          .AddResourceRegistry()
          .AddMemoryAllocator()
          .AddEpochManager()
          .AddHighResolutionSharedTimer()
          .AddDeadlineWatchdog()
          .AddScopedManagedPagedMemoryMappedFile(options =>
          {
              options.DatabaseName = _databaseName;
              options.DatabaseCacheSize = (ulong)dcs;
              options.PagesDebugPattern = false;
          })
          .AddScopedDatabaseEngine();

        _serviceProvider = sc.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        _dbe = _serviceProvider.GetRequiredService<DatabaseEngine>();
        _dbe.RegisterComponentFromAccessor<DataBenchComp>();

        _dbe.InitializeArchetypes();

        // Pre-populate entities for read/update benchmarks
        _entityIds = new EntityId[PrePopulateCount];
        using var t = _dbe.CreateQuickTransaction();
        for (int i = 0; i < PrePopulateCount; i++)
        {
            var comp = new DataBenchComp { Value = i, Timestamp = DateTime.UtcNow.Ticks };
            _entityIds[i] = t.Spawn<DataBenchArch>(DataBenchArch.Data.Set(in comp));
        }
        t.Commit();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _dbe?.Dispose();
        _serviceProvider?.Dispose();

        try { File.Delete($"{_databaseName}.bin"); } catch { }
    }

    /// <summary>
    /// Spawn an entity with a single component in a transaction and commit.
    /// Measures: transaction creation + entity allocation + B+Tree insert + MVCC version + commit.
    /// </summary>
    [Benchmark]
    public void CreateEntity_SingleComponent()
    {
        using var t = _dbe.CreateQuickTransaction();
        var comp = new DataBenchComp { Value = 42, Timestamp = 12345 };
        t.Spawn<DataBenchArch>(DataBenchArch.Data.Set(in comp));
        t.Commit();
    }

    /// <summary>
    /// Read a component by entity ID in a snapshot transaction.
    /// Measures: transaction creation + entity resolution + MVCC version resolution.
    /// </summary>
    [Benchmark]
    public void ReadComponent_ById()
    {
        using var t = _dbe.CreateQuickTransaction();
        var entity = t.Open(_entityIds[0]);
        _ = entity.Read(DataBenchArch.Data);
    }

    /// <summary>
    /// Update a single component field in a transaction and commit.
    /// Measures: transaction creation + read-for-update + MVCC new version + commit.
    /// </summary>
    [Benchmark]
    public void UpdateComponent_SingleField()
    {
        using var t = _dbe.CreateQuickTransaction();
        var entity = t.OpenMut(_entityIds[0]);
        ref var comp = ref entity.Write(DataBenchArch.Data);
        comp.Value = 9999;
        comp.Timestamp = DateTime.UtcNow.Ticks;
        t.Commit();
    }
}
