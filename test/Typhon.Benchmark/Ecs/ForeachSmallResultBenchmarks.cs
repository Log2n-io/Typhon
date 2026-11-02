using System;
using System.IO;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Typhon.Schema.Definition;

namespace Typhon.Benchmark;

[Archetype]
partial class FsbOne : Archetype<FsbOne>
{
    public static readonly Comp<AaBenchPosition> Position = Register<AaBenchPosition>();
}

[Archetype]
partial class FsbSixteen : Archetype<FsbSixteen>
{
    public static readonly Comp<AaBenchPosition> Position = Register<AaBenchPosition>();
}

[Archetype]
partial class FsbTwoFiftySix : Archetype<FsbTwoFiftySix>
{
    public static readonly Comp<AaBenchPosition> Position = Register<AaBenchPosition>();
}

/// <summary>
/// <c>foreach</c> over a query, where its fixed cost shows: results of 1, 16 and 256 entities, and the first row of 10 000 (a <c>break</c>). The streaming
/// enumerator rents a batch per fill; the collecting one it replaced built a list of every match before the first row.
/// </summary>
[SimpleJob(warmupCount: 2, iterationCount: 5)]
[MemoryDiagnoser]
[BenchmarkCategory("Query", "Regression")]
public class ForeachSmallResultBenchmarks : IDisposable
{
    private const int LargeCount = 10_000;

    private ServiceProvider _serviceProvider;
    private DatabaseEngine _dbe;
    private Transaction _tx;

    [GlobalSetup]
    public void Setup()
    {
        var name = $"ForeachSmallBench_{Environment.ProcessId}";
        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
          .AddResourceRegistry()
          .AddMemoryAllocator()
          .AddEpochManager()
          .AddHighResolutionSharedTimer()
          .AddDeadlineWatchdog()
          .AddScopedManagedPagedMemoryMappedFile(o =>
          {
              o.DatabaseName = name;
              o.DatabaseCacheSize = (ulong)(200 * 1024 * PagedMMF.PageSize);
              o.PagesDebugPattern = false;
          })
          .AddInMemoryWalEngine();

        _serviceProvider = sc.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        _dbe = _serviceProvider.GetRequiredService<DatabaseEngine>();
        _dbe.RegisterComponentFromAccessor<AaBenchPosition>();
        _dbe.RegisterComponentFromAccessor<AaBenchMovement>();
        _dbe.InitializeArchetypes();

        using (var tx = _dbe.CreateQuickTransaction())
        {
            var pos = new AaBenchPosition(1, 2);
            tx.Spawn<FsbOne>(FsbOne.Position.Set(in pos));
            for (var i = 0; i < 16; i++)
            {
                tx.Spawn<FsbSixteen>(FsbSixteen.Position.Set(in pos));
            }

            for (var i = 0; i < 256; i++)
            {
                tx.Spawn<FsbTwoFiftySix>(FsbTwoFiftySix.Position.Set(in pos));
            }

            tx.Commit();
        }

        for (var first = 0; first < LargeCount; first += 1000)
        {
            using var tx = _dbe.CreateQuickTransaction();
            for (var i = 0; i < 1000; i++)
            {
                var pos = new AaBenchPosition(first + i, 0);
                var mov = new AaBenchMovement(1, 1);
                tx.Spawn<AaBenchAnt>(AaBenchAnt.Position.Set(in pos), AaBenchAnt.Movement.Set(in mov));
            }

            tx.Commit();
        }

        _tx = _dbe.CreateReadOnlyTransaction();
    }

    [Benchmark]
    public float Foreach_1()
    {
        var sum = 0f;
        foreach (var e in _tx.Query<FsbOne>())
        {
            sum += e.Read(FsbOne.Position).X;
        }

        return sum;
    }

    [Benchmark]
    public float Foreach_16()
    {
        var sum = 0f;
        foreach (var e in _tx.Query<FsbSixteen>())
        {
            sum += e.Read(FsbSixteen.Position).X;
        }

        return sum;
    }

    [Benchmark]
    public float Foreach_256()
    {
        var sum = 0f;
        foreach (var e in _tx.Query<FsbTwoFiftySix>())
        {
            sum += e.Read(FsbTwoFiftySix.Position).X;
        }

        return sum;
    }

    [Benchmark]
    public float Foreach_10k()
    {
        var sum = 0f;
        foreach (var e in _tx.Query<AaBenchAnt>())
        {
            sum += e.Read(AaBenchAnt.Position).X;
        }

        return sum;
    }

    [Benchmark]
    public float Foreach_10k_BreakAfterOne()
    {
        foreach (var e in _tx.Query<AaBenchAnt>())
        {
            return e.Read(AaBenchAnt.Position).X;
        }

        return 0;
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        _tx?.Dispose();
        _tx = null;
        _dbe?.Dispose();
        _serviceProvider?.Dispose();
        try { File.Delete($"ForeachSmallBench_{Environment.ProcessId}.bin"); } catch { }
        GC.SuppressFinalize(this);
    }
}
