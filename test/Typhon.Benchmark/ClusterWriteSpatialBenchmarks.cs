using System;
using System.Numerics;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Typhon.Schema.Definition;

namespace Typhon.Benchmark;

[Component("Typhon.Benchmark.ClWs.Pos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct ClWsBenchPos
{
    [Field]
    [SpatialIndex]
    public AABB2F Bounds;
}

[Archetype]
partial class ClWsBenchUnit : Archetype<ClWsBenchUnit>
{
    public static readonly Comp<ClWsBenchPos> Pos = Register<ClWsBenchPos>();
}

/// <summary>
/// The write barrier's per-entity cost: every entity moved by <c>ClusterRef.WriteSpatial</c>, and by <c>EntityRefMut.Set</c> on the same barrier-only
/// archetype. Each move stays inside its cell, alternating ±0.25 so positions never drift: the steady state of a moving population, no crossing. No fence
/// runs between operations — the fence is not what is measured.
/// </summary>
[InProcess(true)]
[MemoryDiagnoser]
[BenchmarkCategory("ClusterSpatial", "WriteSpatial")]
public class ClusterWriteSpatialBenchmarks : IDisposable
{
    private const int EntityCount = 10_000;
    private const float WorldSize = 10_000f;
    private const float CellSize = 100f;

    private ServiceProvider _sp;
    private DatabaseEngine _dbe;
    private EntityId[] _ids;
    private EntityId[] _idsInClusterOrder;
    private float _sign = 1f;

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
          .AddScopedManagedPagedMemoryMappedFile(o =>
          {
              o.DatabaseName = $"ClWsBench_{Environment.ProcessId}";
              o.DatabaseCacheSize = (ulong)(64L * 1024 * PagedMMF.PageSize);
              o.TestMode = true;
              o.PagesDebugPattern = false;
          })
          .AddInMemoryWalEngine();

        _sp = sc.BuildServiceProvider();
        _sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
        _dbe = _sp.GetRequiredService<DatabaseEngine>();
        _dbe.RegisterComponentFromAccessor<ClWsBenchPos>();
        _dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(worldMin: new Vector2(0, 0), worldMax: new Vector2(WorldSize, WorldSize), cellSize: CellSize));
        _dbe.InitializeArchetypes();
        _dbe.SetSpatialBarrierOnly<ClWsBenchUnit>();

        // Each entity at the centre of a cell-local spot, far enough from every edge that ±0.25 never leaves the cell.
        var rng = new Random(42);
        _ids = new EntityId[EntityCount];
        var tick = 1L;
        for (var spawned = 0; spawned < EntityCount;)
        {
            var batch = Math.Min(1_000, EntityCount - spawned);
            using (var tx = _dbe.CreateQuickTransaction())
            {
                for (var i = 0; i < batch; i++)
                {
                    var x = (float)(rng.Next(0, 100) * CellSize + 10 + rng.NextDouble() * 80);
                    var y = (float)(rng.Next(0, 100) * CellSize + 10 + rng.NextDouble() * 80);
                    var pos = new ClWsBenchPos { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y } };
                    _ids[spawned + i] = tx.Spawn<ClWsBenchUnit>(ClWsBenchUnit.Pos.Set(in pos));
                }
                tx.Commit();
            }
            spawned += batch;
            _dbe.WriteTickFence(tick++);
        }

        // The same entities in the order a cluster walk visits them: what Set costs when the caller's order follows the storage.
        _idsInClusterOrder = new EntityId[EntityCount];
        var n = 0;
        using (var tx = _dbe.CreateQuickTransaction())
        {
            foreach (var cluster in tx.For<ClWsBenchUnit>().GetClusterEnumerator())
            {
                for (var live = cluster.OccupancyBits; live != 0; live &= live - 1)
                {
                    _idsInClusterOrder[n++] = cluster.GetEntityId(BitOperations.TrailingZeroCount(live));
                }
            }
        }
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        _dbe?.Dispose();
        _sp?.Dispose();
        _dbe = null;
        _sp = null;
    }

    /// <summary>Every entity, through the barrier: one cluster walk, a write per live slot.</summary>
    [Benchmark(OperationsPerInvoke = EntityCount)]
    public void WriteSpatial()
    {
        var d = 0.25f * (_sign = -_sign);
        using var tx = _dbe.CreateQuickTransaction();
        foreach (var cluster in tx.For<ClWsBenchUnit>().GetClusterEnumerator())
        {
            for (var live = cluster.OccupancyBits; live != 0; live &= live - 1)
            {
                var slot = BitOperations.TrailingZeroCount(live);
                var p = cluster.GetReadOnly(ClWsBenchUnit.Pos, slot);
                p.Bounds = new AABB2F { MinX = p.Bounds.MinX + d, MinY = p.Bounds.MinY + d, MaxX = p.Bounds.MaxX + d, MaxY = p.Bounds.MaxY + d };
                cluster.WriteSpatial(ClWsBenchUnit.Pos, slot, in p);
            }
        }
        tx.Commit();
    }

    /// <summary>Every entity, through <c>OpenMut</c> and <c>Set</c>, in spawn order: the per-entity path a barrier-only archetype now accepts.</summary>
    [Benchmark(OperationsPerInvoke = EntityCount)]
    public void Set() => SetAll(_ids);

    /// <summary><see cref="Set"/> over the same entities in cluster order: the share of its cost that is the order of the caller's ids.</summary>
    [Benchmark(OperationsPerInvoke = EntityCount)]
    public void SetInClusterOrder() => SetAll(_idsInClusterOrder);

    private void SetAll(EntityId[] ids)
    {
        var d = 0.25f * (_sign = -_sign);
        using var tx = _dbe.CreateQuickTransaction();
        for (var i = 0; i < ids.Length; i++)
        {
            var e = tx.OpenMut(ids[i]);
            var p = e.Read(ClWsBenchUnit.Pos);
            p.Bounds = new AABB2F { MinX = p.Bounds.MinX + d, MinY = p.Bounds.MinY + d, MaxX = p.Bounds.MaxX + d, MaxY = p.Bounds.MaxY + d };
            e.Set(ClWsBenchUnit.Pos, p);
        }
        tx.Commit();
    }
}
