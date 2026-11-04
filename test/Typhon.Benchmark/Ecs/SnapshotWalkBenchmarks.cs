using System;
using System.IO;
using System.Numerics;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Typhon.Benchmark;

/// <summary>
/// <c>tx.Walk</c> against the cluster enumerator it is built beside, over 10 000 entities: a SingleVersion archetype (zero-copy columns) and a mixed one
/// whose Versioned column the walk reads at the snapshot.
/// </summary>
[SimpleJob(warmupCount: 2, iterationCount: 5)]
[MemoryDiagnoser]
[BenchmarkCategory("Query", "Regression")]
public class SnapshotWalkBenchmarks : IDisposable
{
    private const int EntityCount = 10_000;

    private ServiceProvider _serviceProvider;
    private DatabaseEngine _dbe;
    private Transaction _tx;

    [GlobalSetup]
    public void Setup()
    {
        var name = $"SnapshotWalkBench_{Environment.ProcessId}";
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
        _dbe.RegisterComponentFromAccessor<AaVcHealth>();
        _dbe.InitializeArchetypes();

        for (var first = 0; first < EntityCount; first += 1000)
        {
            using var tx = _dbe.CreateQuickTransaction();
            for (var i = 0; i < 1000; i++)
            {
                var pos = new AaBenchPosition(first + i, 0);
                var mov = new AaBenchMovement(1, 1);
                var health = new AaVcHealth { Current = first + i, Max = 100 };
                tx.Spawn<AaBenchAnt>(AaBenchAnt.Position.Set(in pos), AaBenchAnt.Movement.Set(in mov));
                tx.Spawn<AaBenchMixedCluster>(AaBenchMixedCluster.Position.Set(in pos), AaBenchMixedCluster.Movement.Set(in mov),
                    AaBenchMixedCluster.Health.Set(in health));
            }

            tx.Commit();
        }

        _tx = _dbe.CreateReadOnlyTransaction();
    }

    [Benchmark(Baseline = true)]
    public float ClusterEnumerator_Sv()
    {
        var sum = 0f;
        foreach (var cluster in _tx.GetClusterEnumerator<AaBenchAnt>())
        {
            var span = cluster.GetReadOnlySpan(AaBenchAnt.Position);
            for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
            {
                sum += span[BitOperations.TrailingZeroCount(bits)].X;
            }
        }

        return sum;
    }

    [Benchmark]
    public float TxWalk_Sv()
    {
        var sum = 0f;
        foreach (var batch in _tx.Walk<AaBenchAnt>())
        {
            var span = batch.GetReadOnlySpan(AaBenchAnt.Position);
            for (var bits = batch.Rows; bits != 0; bits &= bits - 1)
            {
                sum += span[BitOperations.TrailingZeroCount(bits)].X;
            }
        }

        return sum;
    }

    [Benchmark]
    public long TxWalk_MixedVersionedColumn()
    {
        var sum = 0L;
        foreach (var batch in _tx.Walk<AaBenchMixedCluster>())
        {
            var span = batch.GetReadOnlySpan(AaBenchMixedCluster.Health);
            for (var bits = batch.Rows; bits != 0; bits &= bits - 1)
            {
                sum += span[BitOperations.TrailingZeroCount(bits)].Current;
            }
        }

        return sum;
    }

    [Benchmark]
    public float Foreach_Sv()
    {
        var sum = 0f;
        foreach (var e in _tx.Query<AaBenchAnt>())
        {
            sum += e.Read(AaBenchAnt.Position).X;
        }

        return sum;
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        _tx?.Dispose();
        _tx = null;
        _dbe?.Dispose();
        _serviceProvider?.Dispose();
        try { File.Delete($"SnapshotWalkBench_{Environment.ProcessId}.bin"); } catch { }
        GC.SuppressFinalize(this);
    }
}
