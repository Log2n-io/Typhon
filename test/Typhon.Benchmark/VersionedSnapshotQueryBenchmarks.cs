using System;
using System.IO;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Typhon.Engine.Internals;

namespace Typhon.Benchmark;

// ═══════════════════════════════════════════════════════════════════════
// #674 A/B: field queries over an indexed VERSIONED archetype, read by a reader older than some updates (VIS-12), and the update
// commit that now folds a cluster watermark and appends to the update log. Not a regression benchmark: an A/B fixture.
// ═══════════════════════════════════════════════════════════════════════

[SimpleJob(warmupCount: 3, iterationCount: 10)]
[MemoryDiagnoser]
[BenchmarkCategory("Snapshot674")]
public class VersionedSnapshotQueryBenchmarks : IDisposable
{
    private const int EntityCount = 10_000;

    private ServiceProvider _serviceProvider;
    private DatabaseEngine _dbe;
    private Transaction _reader;

    /// <summary>Updates committed after the reader's snapshot, each in a different cluster: 0 = no cluster read at the snapshot.</summary>
    [Params(0, 16)]
    public int UpdatedAfterSnapshot;

    /// <summary>The index path, or the scan path (QueryPathProbe.Forced: the enum is internal, a BDN param must be public).</summary>
    [Params(true, false)]
    public bool Selective;

    private ClusterScanPath Path => Selective ? ClusterScanPath.Selective : ClusterScanPath.FullScan;

    [GlobalSetup]
    public void Setup()
    {
        (_serviceProvider, _dbe) = VersionedBench.Open("VsnapQ");
        var ids = VersionedBench.Spawn(_dbe, EntityCount);
        _reader = _dbe.CreateQuickTransaction();
        for (var k = 0; k < UpdatedAfterSnapshot; k++)
        {
            using var tx = _dbe.CreateQuickTransaction();
            var id = ids[k * (EntityCount / UpdatedAfterSnapshot)];
            var r = tx.Open(id).Read(AaBenchIdxVersionedCluster.Ranked);
            r.Tier = (r.Tier + 1) & 7;
            tx.OpenMut(id).Set(AaBenchIdxVersionedCluster.Ranked, r);
            tx.Commit();
        }
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        _reader?.Dispose();
        _reader = null;
        VersionedBench.Close(_serviceProvider, _dbe, "VsnapQ");
        _dbe = null;
        _serviceProvider = null;
        GC.SuppressFinalize(this);
    }

    [Benchmark]
    public int Count_Tier3()
    {
        QueryPathProbe.Forced = Path;
        return _reader.Query<AaBenchIdxVersionedCluster>().WhereField<AaVcRanked>(x => x.Tier == 3).Count();
    }

    [Benchmark]
    public int Execute_Tier3()
    {
        QueryPathProbe.Forced = Path;
        return _reader.Query<AaBenchIdxVersionedCluster>().WhereField<AaVcRanked>(x => x.Tier == 3).Execute().Count;
    }

    /// <summary>A broad count with no predicate: the occupancy popcount over every cluster (TryCountViaOccupancy), yielding every 64 clusters.</summary>
    [Benchmark]
    public int Count_All()
    {
        QueryPathProbe.Forced = Path;
        return _reader.Query<AaBenchIdxVersionedCluster>().Count();
    }

    [Benchmark]
    public int ExecuteOrdered_Take50()
    {
        QueryPathProbe.Forced = Path;
        return _reader.Query<AaBenchIdxVersionedCluster>().WhereField<AaVcRanked>(x => x.Tier >= 2).OrderByField<AaVcRanked, int>(x => x.Tier).Take(50)
            .ExecuteOrdered().Count;
    }
}

/// <summary>
/// The index paths at scale (VIS-12): 200 k entities, about 3 100 clusters. A reader older than an update walks every cluster's update watermark — the
/// cost of finding the clusters to read at the snapshot without a log of them.
/// </summary>
[SimpleJob(warmupCount: 3, iterationCount: 10)]
[MemoryDiagnoser]
[BenchmarkCategory("Snapshot674")]
public class VersionedSnapshotScaleBenchmarks : IDisposable
{
    private const int EntityCount = 200_000;

    private ServiceProvider _serviceProvider;
    private DatabaseEngine _dbe;
    private Transaction _reader;

    [Params(0, 16)]
    public int UpdatedAfterSnapshot;

    [GlobalSetup]
    public void Setup()
    {
        (_serviceProvider, _dbe) = VersionedBench.Open("VsnapS");
        var ids = VersionedBench.Spawn(_dbe, EntityCount);
        _reader = _dbe.CreateQuickTransaction();
        for (var k = 0; k < UpdatedAfterSnapshot; k++)
        {
            using var tx = _dbe.CreateQuickTransaction();
            var id = ids[k * (EntityCount / UpdatedAfterSnapshot)];
            var r = tx.Open(id).Read(AaBenchIdxVersionedCluster.Ranked);
            r.Score++;
            tx.OpenMut(id).Set(AaBenchIdxVersionedCluster.Ranked, r);
            tx.Commit();
        }
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        _reader?.Dispose();
        _reader = null;
        VersionedBench.Close(_serviceProvider, _dbe, "VsnapS");
        _dbe = null;
        _serviceProvider = null;
        GC.SuppressFinalize(this);
    }

    /// <summary>A point-like selective query: one tier of eight, read through the index.</summary>
    [Benchmark]
    public int Selective_Count_Tier3()
    {
        QueryPathProbe.Forced = ClusterScanPath.Selective;
        return _reader.Query<AaBenchIdxVersionedCluster>().WhereField<AaVcRanked>(x => x.Tier == 3).Count();
    }

    [Benchmark]
    public int ExecuteOrdered_Take50()
    {
        QueryPathProbe.Forced = ClusterScanPath.Planner;
        return _reader.Query<AaBenchIdxVersionedCluster>().WhereField<AaVcRanked>(x => x.Tier >= 2).OrderByField<AaVcRanked, int>(x => x.Tier).Take(50)
            .ExecuteOrdered().Count;
    }
}

[SimpleJob(warmupCount: 3, iterationCount: 10)]
[MemoryDiagnoser]
[BenchmarkCategory("Snapshot674")]
public class VersionedUpdateCommitBenchmarks
{
    private const int EntityCount = 10_000;
    private const int Writes = 100;

    private ServiceProvider _serviceProvider;
    private DatabaseEngine _dbe;
    private EntityId[] _targets;

    [GlobalSetup]
    public void Setup()
    {
        (_serviceProvider, _dbe) = VersionedBench.Open("VsnapW");
        var ids = VersionedBench.Spawn(_dbe, EntityCount);
        _targets = new EntityId[Writes];
        for (var i = 0; i < Writes; i++)
        {
            _targets[i] = ids[i * (EntityCount / Writes)];
        }
    }

    [GlobalCleanup]
    public void Cleanup() => VersionedBench.Close(_serviceProvider, _dbe, "VsnapW");

    /// <summary>One update per transaction: the per-commit cost — the tail's dispose, its drain, its epoch refresh — is most of it.</summary>
    [Benchmark(OperationsPerInvoke = 100)]
    public int Commit1_KeyUnchanged_x100()
    {
        for (var i = 0; i < _targets.Length; i++)
        {
            using var tx = _dbe.CreateQuickTransaction();
            var e = tx.OpenMut(_targets[i]);
            var r = e.Read(AaBenchIdxVersionedCluster.Ranked);
            r.Score++;
            e.Set(AaBenchIdxVersionedCluster.Ranked, r);
            tx.Commit();
        }

        return _targets.Length;
    }

    [Benchmark]
    public int Commit100_KeyUnchanged() => Commit(false);

    [Benchmark]
    public int Commit100_KeyChanges() => Commit(true);

    private int Commit(bool moveKey)
    {
        using var tx = _dbe.CreateQuickTransaction();
        for (var i = 0; i < _targets.Length; i++)
        {
            var e = tx.OpenMut(_targets[i]);
            var r = e.Read(AaBenchIdxVersionedCluster.Ranked);
            if (moveKey)
            {
                r.Tier = (r.Tier + 1) & 7;
            }

            r.Score++;
            e.Set(AaBenchIdxVersionedCluster.Ranked, r);
        }

        tx.Commit();
        return _targets.Length;
    }
}

internal static class VersionedBench
{
    internal static (ServiceProvider, DatabaseEngine) Open(string prefix)
    {
        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
          .AddResourceRegistry()
          .AddMemoryAllocator()
          .AddEpochManager()
          .AddHighResolutionSharedTimer()
          .AddDeadlineWatchdog()
          .AddScopedManagedPagedMemoryMappedFile(o =>
          {
              o.DatabaseName = $"{prefix}_{Environment.ProcessId}";
              o.DatabaseCacheSize = (ulong)(200 * 1024 * PagedMMF.PageSize);
              o.PagesDebugPattern = false;
          })
          .AddInMemoryWalEngine();
        var sp = sc.BuildServiceProvider();
        sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
        var dbe = sp.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<AaBenchPosition>();
        dbe.RegisterComponentFromAccessor<AaVcRanked>();
        dbe.InitializeArchetypes();
        return (sp, dbe);
    }

    internal static EntityId[] Spawn(DatabaseEngine dbe, int count)
    {
        var ids = new EntityId[count];
        for (var offset = 0; offset < count; offset += 1000)
        {
            using var tx = dbe.CreateQuickTransaction();
            for (var i = offset; i < Math.Min(count, offset + 1000); i++)
            {
                var pos = new AaBenchPosition(i, 0);
                var ranked = new AaVcRanked(i % 8, i);
                ids[i] = tx.Spawn<AaBenchIdxVersionedCluster>(AaBenchIdxVersionedCluster.Position.Set(in pos),
                    AaBenchIdxVersionedCluster.Ranked.Set(in ranked));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(0);
        return ids;
    }

    internal static void Close(ServiceProvider sp, DatabaseEngine dbe, string prefix)
    {
        dbe?.Dispose();
        sp?.Dispose();
        try { File.Delete($"{prefix}_{Environment.ProcessId}.bin"); } catch { }
    }
}
