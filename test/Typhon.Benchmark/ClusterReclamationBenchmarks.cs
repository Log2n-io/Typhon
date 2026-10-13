using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Typhon.Schema.Definition;

namespace Typhon.Benchmark;

[Component("Typhon.Benchmark.Reclaim.Data", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct ReclaimBenchData
{
    [Field] public int Value;
    [Field] public int Pad;
}

[Archetype]
partial class ReclaimBenchArch : Archetype<ReclaimBenchArch>
{
    public static readonly Comp<ReclaimBenchData> Data = Register<ReclaimBenchData>();
}

/// <summary>
/// #582: the cost of a destroy that empties its cluster, and of the fence that reclaims it. Every cluster holds one entity, so each destroy drains one;
/// <c>Clusters</c> is the active-list length, <c>Drains</c> the destroys per invocation. Before #582 the commit finalized inline — an O(active) removal per
/// drain — and the fence had nothing to do for it; after, the commit records the drain and the fence reclaims all of them in one pass.
/// </summary>
[SimpleJob(warmupCount: 2, iterationCount: 10, invocationCount: 1)]
[BenchmarkCategory("ECS", "Regression")]
public class ClusterReclamationBenchmarks : IDisposable
{
    public sealed record Shape(int Clusters, int Drains)
    {
        public override string ToString() => $"N={Clusters},D={Drains}";
    }

    public static IEnumerable<Shape> Shapes() => [new(500, 100), new(4500, 100), new(4500, 1000)];

    [ParamsSource(nameof(Shapes))]
    public Shape Case;

    private ServiceProvider _serviceProvider;
    private DatabaseEngine _dbe;
    private readonly List<EntityId> _singletons = [];
    private int _clusterSize;
    private long _tick = 1;

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
              options.DatabaseName = $"ReclaimBench_{Environment.ProcessId}";
              options.DatabaseCacheSize = (ulong)(64 * 1024 * PagedMMF.PageSize);
              options.PagesDebugPattern = false;
          })
          .AddScopedDatabaseEngine();

        _serviceProvider = sc.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        _dbe = _serviceProvider.GetRequiredService<DatabaseEngine>();
        _dbe.RegisterComponentFromAccessor<ReclaimBenchData>();
        _dbe.InitializeArchetypes();
        _clusterSize = _dbe._archetypeStates[Archetype<ReclaimBenchArch>.Metadata.ArchetypeId].ClusterState.Layout.ClusterSize;
        RefillTo(Case.Clusters);
    }

    /// <summary>
    /// Brings the archetype back to <paramref name="clusters"/> clusters of one entity each: spawns full clusters, keeps one new entity per cluster made only
    /// of new ones, destroys the rest (spawns that land beside an existing singleton are destroyed again), then fences.
    /// </summary>
    private void RefillTo(int clusters)
    {
        while (_singletons.Count < clusters)
        {
            var missing = clusters - _singletons.Count;
            var spawned = new HashSet<EntityId>();
            using (var tx = _dbe.CreateQuickTransaction())
            {
                for (var i = 0; i < missing * _clusterSize; i++)
                {
                    var d = new ReclaimBenchData { Value = i };
                    spawned.Add(tx.Spawn<ReclaimBenchArch>(ReclaimBenchArch.Data.Set(in d)));
                }

                tx.Commit();
            }

            var existing = new HashSet<EntityId>(_singletons);
            var keep = new List<EntityId>();
            using (var tx = _dbe.CreateQuickTransaction())
            {
                foreach (var cluster in tx.GetClusterEnumerator<ReclaimBenchArch>())
                {
                    var bits = cluster.OccupancyBits;
                    var holdsExisting = false;
                    var firstNew = default(EntityId);
                    var hasNew = false;
                    while (bits != 0)
                    {
                        var e = cluster.GetEntityId(BitOperations.TrailingZeroCount(bits));
                        bits &= bits - 1;
                        holdsExisting |= existing.Contains(e);
                        if (!hasNew && spawned.Contains(e))
                        {
                            firstNew = e;
                            hasNew = true;
                        }
                    }

                    if (hasNew && !holdsExisting && keep.Count < missing)
                    {
                        keep.Add(firstNew);
                    }
                }
            }

            spawned.ExceptWith(keep);
            using (var tx = _dbe.CreateQuickTransaction())
            {
                foreach (var id in spawned)
                {
                    tx.Destroy(id);
                }

                tx.Commit();
            }

            _singletons.AddRange(keep);
            _dbe.WriteTickFence(_tick++);
        }
    }

    [IterationSetup(Target = nameof(DrainingDestroyCommit))]
    public void SetupCommits()
    {
        RefillTo(Case.Clusters);
    }

    [IterationCleanup(Target = nameof(DrainingDestroyCommit))]
    public void CleanupCommits() => _dbe.WriteTickFence(_tick++);

    /// <summary><c>Drains</c> destroy commits, each draining its cluster (the total, not per commit). The fence that reclaims them is not measured here.</summary>
    [Benchmark(OperationsPerInvoke = 1)]
    public void DrainingDestroyCommit()
    {
        for (var i = 0; i < Case.Drains; i++)
        {
            var id = _singletons[^1];
            _singletons.RemoveAt(_singletons.Count - 1);
            using var tx = _dbe.CreateQuickTransaction();
            tx.Destroy(id);
            tx.Commit();
        }
    }

    [IterationSetup(Target = nameof(FenceAfterDrains))]
    public void SetupFence()
    {
        RefillTo(Case.Clusters);

        for (var i = 0; i < Case.Drains; i++)
        {
            var id = _singletons[^1];
            _singletons.RemoveAt(_singletons.Count - 1);
            using var tx = _dbe.CreateQuickTransaction();
            tx.Destroy(id);
            tx.Commit();
        }
    }

    /// <summary>The fence after <c>Drains</c> draining destroys: where #582 reclaims them.</summary>
    [Benchmark]
    public void FenceAfterDrains() => _dbe.WriteTickFence(_tick++);

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        _dbe?.Dispose();
        _serviceProvider?.Dispose();
        GC.SuppressFinalize(this);
    }
}
