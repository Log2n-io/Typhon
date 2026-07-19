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
// MVCC: Revision Chain Depth Impact on Read Latency
// ═══════════════════════════════════════════════════════════════════════

[Component("Typhon.Benchmark.RevComp", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct RevComp
{
    [Field]
    public int Value;

    [Field]
    public long Timestamp;
}

[Archetype(505)]
class RevArch : Archetype<RevArch>
{
    public static readonly Comp<RevComp> Rev = Register<RevComp>();
}

[SimpleJob(warmupCount: 2, iterationCount: 3)]
[MemoryDiagnoser]
[BenchmarkCategory("MVCC", "Regression")]
public class RevisionBenchmarks
{
    private ServiceProvider _serviceProvider;
    private DatabaseEngine _dbe;
    private string _databaseName;

    private EntityId _singleVersionEntity;
    private EntityId _tenVersionEntity;
    private EntityId _fiftyVersionEntity;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _databaseName = $"RevisionBench_{Environment.ProcessId}";

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
        _dbe.RegisterComponentFromAccessor<RevComp>();

        _dbe.InitializeArchetypes();

        // Create entity with 1 version
        using (var t = _dbe.CreateQuickTransaction())
        {
            var comp = new RevComp { Value = 1, Timestamp = 1 };
            _singleVersionEntity = t.Spawn<RevArch>(RevArch.Rev.Set(in comp));
            t.Commit();
        }

        // Create entity with 10 versions (1 create + 9 updates)
        using (var t = _dbe.CreateQuickTransaction())
        {
            var comp = new RevComp { Value = 1, Timestamp = 1 };
            _tenVersionEntity = t.Spawn<RevArch>(RevArch.Rev.Set(in comp));
            t.Commit();
        }
        for (int i = 2; i <= 10; i++)
        {
            using var t = _dbe.CreateQuickTransaction();
            var entity = t.OpenMut(_tenVersionEntity);
            ref var comp = ref entity.Write(RevArch.Rev);
            comp.Value = i;
            comp.Timestamp = i;
            t.Commit();
        }

        // Create entity with 50 versions (1 create + 49 updates)
        using (var t = _dbe.CreateQuickTransaction())
        {
            var comp = new RevComp { Value = 1, Timestamp = 1 };
            _fiftyVersionEntity = t.Spawn<RevArch>(RevArch.Rev.Set(in comp));
            t.Commit();
        }
        for (int i = 2; i <= 50; i++)
        {
            using var t = _dbe.CreateQuickTransaction();
            var entity = t.OpenMut(_fiftyVersionEntity);
            ref var comp = ref entity.Write(RevArch.Rev);
            comp.Value = i;
            comp.Timestamp = i;
            t.Commit();
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
    /// Read an entity with a single MVCC version. Baseline for version resolution.
    /// </summary>
    [Benchmark]
    public void Read_SingleVersion()
    {
        using var t = _dbe.CreateQuickTransaction();
        var entity = t.Open(_singleVersionEntity);
        _ = entity.Read(RevArch.Rev);
    }

    /// <summary>
    /// Read an entity with 10 MVCC versions. Measures chain walk overhead.
    /// </summary>
    [Benchmark]
    public void Read_10Versions()
    {
        using var t = _dbe.CreateQuickTransaction();
        var entity = t.Open(_tenVersionEntity);
        _ = entity.Read(RevArch.Rev);
    }

    /// <summary>
    /// Read an entity with 50 MVCC versions. Tests longer chain traversal cost.
    /// </summary>
    [Benchmark]
    public void Read_50Versions()
    {
        using var t = _dbe.CreateQuickTransaction();
        var entity = t.Open(_fiftyVersionEntity);
        _ = entity.Read(RevArch.Rev);
    }
}
