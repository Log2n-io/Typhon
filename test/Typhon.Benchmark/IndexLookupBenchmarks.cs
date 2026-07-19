using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Runtime.InteropServices;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace Typhon.Benchmark;

// ═══════════════════════════════════════════════════════════════════════
// Index: Primary Key Lookup Access Pattern Benchmarks
// ═══════════════════════════════════════════════════════════════════════

[Component("Typhon.Benchmark.IdxComp", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct IdxComp
{
    [Field]
    public int Value;

    [Field]
    public long Timestamp;
}

[Archetype(504)]
class IdxArch : Archetype<IdxArch>
{
    public static readonly Comp<IdxComp> Idx = Register<IdxComp>();
}

[SimpleJob(warmupCount: 2, iterationCount: 3)]
[MemoryDiagnoser]
[BenchmarkCategory("Index", "Regression")]
public class IndexLookupBenchmarks
{
    private ServiceProvider _serviceProvider;
    private DatabaseEngine _dbe;
    private EntityId[] _entityIds;
    private EntityId[] _randomOrder;
    private EntityId _deleteEntity;
    private string _databaseName;

    private const int PrePopulateCount = 10_000;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _databaseName = $"IndexLookupBench_{Environment.ProcessId}";

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
        _dbe.RegisterComponentFromAccessor<IdxComp>();

        _dbe.InitializeArchetypes();

        // Pre-populate 10K entities
        _entityIds = new EntityId[PrePopulateCount];
        using (var t = _dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < PrePopulateCount; i++)
            {
                var comp = new IdxComp { Value = i, Timestamp = DateTime.UtcNow.Ticks };
                _entityIds[i] = t.Spawn<IdxArch>(IdxArch.Idx.Set(in comp));
            }
            t.Commit();
        }

        // Separate entity for delete benchmark (avoids polluting lookup data)
        using (var t = _dbe.CreateQuickTransaction())
        {
            var comp = new IdxComp { Value = -1, Timestamp = 0 };
            _deleteEntity = t.Spawn<IdxArch>(IdxArch.Idx.Set(in comp));
            t.Commit();
        }

        // Random-order array for random access benchmark (fixed seed for reproducibility)
        _randomOrder = new EntityId[100];
        var rng = new Random(42);
        for (int i = 0; i < 100; i++)
        {
            _randomOrder[i] = _entityIds[rng.Next(PrePopulateCount)];
        }
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _dbe?.Dispose();
        _serviceProvider?.Dispose();

        try { File.Delete($"{_databaseName}.bin"); } catch { }
    }

    /// <summary>
    /// Read a single entity by primary key from a 10K entity table.
    /// Measures entity resolution through the full Transaction -> LinearHash stack.
    /// </summary>
    [Benchmark]
    public void PrimaryKey_PointLookup()
    {
        using var t = _dbe.CreateQuickTransaction();
        var entity = t.Open(_entityIds[5000]);
        _ = entity.Read(IdxArch.Idx);
    }

    /// <summary>
    /// Read 100 entities with sequential primary keys.
    /// Measures cache-friendly sequential access through entity resolution.
    /// </summary>
    [Benchmark]
    public void PrimaryKey_BatchSequential()
    {
        using var t = _dbe.CreateQuickTransaction();
        for (int i = 0; i < 100; i++)
        {
            var entity = t.Open(_entityIds[i]);
            _ = entity.Read(IdxArch.Idx);
        }
    }

    /// <summary>
    /// Read 100 entities in random order from a 10K entity table.
    /// Measures cache-unfriendly random access through entity resolution.
    /// </summary>
    [Benchmark]
    public void PrimaryKey_BatchRandom()
    {
        using var t = _dbe.CreateQuickTransaction();
        for (int i = 0; i < 100; i++)
        {
            var entity = t.Open(_randomOrder[i]);
            _ = entity.Read(IdxArch.Idx);
        }
    }

    /// <summary>
    /// Destroy a single entity and commit. Measures tombstone creation path.
    /// Entity is re-spawned to maintain steady state for future invocations.
    /// </summary>
    [Benchmark]
    public void DeleteEntity_SingleComponent()
    {
        using var t = _dbe.CreateQuickTransaction();
        t.Destroy(_deleteEntity);
        var comp = new IdxComp { Value = -1, Timestamp = 0 };
        _deleteEntity = t.Spawn<IdxArch>(IdxArch.Idx.Set(in comp));
        t.Commit();
    }
}
