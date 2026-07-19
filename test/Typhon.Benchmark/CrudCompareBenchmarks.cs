using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using Typhon.Engine;

namespace Typhon.Benchmark;

// Uses BenchComp + BenchArch from EpochBenchmarks.cs

class InProcConfig : ManualConfig
{
    public InProcConfig()
    {
        AddJob(Job.MediumRun.WithToolchain(InProcessEmitToolchain.Instance).WithWarmupCount(3).WithIterationCount(10));
    }
}

[Config(typeof(InProcConfig))]
[MemoryDiagnoser]
public class CrudCompareBenchmarks
{
    private ServiceProvider _sp;
    private DatabaseEngine _dbe;
    private EntityId[] _prePopIds;

    private const int PrePopCount = 500;

    [GlobalSetup]
    public void Setup()
    {
        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
          .AddResourceRegistry()
          .AddMemoryAllocator()
          .AddEpochManager()
          .AddHighResolutionSharedTimer()
          .AddDeadlineWatchdog()
          .AddScopedManagedPagedMemoryMappedFile(options =>
          {
              options.DatabaseName = $"CrudCompare_{Environment.ProcessId}";
              options.DatabaseCacheSize = (ulong)(16 * 1024 * PagedMMF.PageSize);
              options.PagesDebugPattern = false;
          })
          .AddScopedDatabaseEngine();

        _sp = sc.BuildServiceProvider();
        _sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
        _dbe = _sp.GetRequiredService<DatabaseEngine>();
        _dbe.RegisterComponentFromAccessor<BenchComp>();
        _dbe.InitializeArchetypes();

        // Pre-populate for read/update benchmarks
        _prePopIds = new EntityId[PrePopCount];
        using var t = _dbe.CreateQuickTransaction();
        for (int i = 0; i < PrePopCount; i++)
        {
            var comp = new BenchComp { Value = i, Timestamp = i * 100L };
            _prePopIds[i] = t.Spawn<BenchArch>(BenchArch.Data.Set(in comp));
        }
        t.Commit();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _dbe?.Dispose();
        _sp?.Dispose();
    }

    /// <summary>Scenario 1: Create 100 entities, commit. Pure write throughput.</summary>
    [Benchmark]
    public void Create100_Commit()
    {
        using var t = _dbe.CreateQuickTransaction();
        for (int i = 0; i < 100; i++)
        {
            var comp = new BenchComp { Value = i, Timestamp = 12345 };
            t.Spawn<BenchArch>(BenchArch.Data.Set(in comp));
        }
        t.Commit();
    }

    /// <summary>Scenario 2: Create 1 entity, read it, write it, commit. Single-entity lifecycle.</summary>
    [Benchmark]
    public void SingleCRUD_Commit()
    {
        using var t = _dbe.CreateQuickTransaction();
        var comp = new BenchComp { Value = 42, Timestamp = 99 };
        var id = t.Spawn<BenchArch>(BenchArch.Data.Set(in comp));

        var entity = t.Open(id);
        _ = entity.Read(BenchArch.Data);

        var mut = t.OpenMut(id);
        mut.Write(BenchArch.Data).Value = 999;

        t.Commit();
    }

    /// <summary>Scenario 3: Read 100 pre-populated entities. Read-only, no commit.</summary>
    [Benchmark]
    public void Read100()
    {
        using var t = _dbe.CreateQuickTransaction();
        int sum = 0;
        for (int i = 0; i < 100; i++)
        {
            var entity = t.Open(_prePopIds[i]);
            sum += entity.Read(BenchArch.Data).Value;
        }
    }

    /// <summary>Scenario 4: Update 100 pre-populated entities, commit. Write amplification.</summary>
    [Benchmark]
    public void Update100_Commit()
    {
        using var t = _dbe.CreateQuickTransaction();
        for (int i = 0; i < 100; i++)
        {
            var mut = t.OpenMut(_prePopIds[i]);
            mut.Write(BenchArch.Data).Value = i + 10000;
        }
        t.Commit();
    }

    /// <summary>Scenario 5: Create 50, destroy 25, commit. Mixed lifecycle.</summary>
    [Benchmark]
    public void Create50_Destroy25_Commit()
    {
        using var t = _dbe.CreateQuickTransaction();
        Span<EntityId> ids = stackalloc EntityId[50];
        for (int i = 0; i < 50; i++)
        {
            var comp = new BenchComp { Value = i, Timestamp = 12345 };
            ids[i] = t.Spawn<BenchArch>(BenchArch.Data.Set(in comp));
        }
        for (int i = 0; i < 25; i++)
        {
            t.Destroy(ids[i]);
        }
        t.Commit();
    }
}
