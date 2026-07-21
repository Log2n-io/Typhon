using System;
using System.IO;
using System.Numerics;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Typhon.Benchmark;

// ═══════════════════════════════════════════════════════════════════════
// Cluster storage regression benchmarks (BDN)
// Reuses archetypes from ArchetypeAccessorBenchmark.cs:
//   AaBenchAnt (510):          SV Position + SV Movement
//   AaBenchMixedCluster (516): SV Position + SV Movement + Versioned Health
//   AaBenchIdxUnit (512):      SV Position + Indexed IdxData
// ═══════════════════════════════════════════════════════════════════════

[SimpleJob(warmupCount: 2, iterationCount: 5)]
[MemoryDiagnoser]
[BenchmarkCategory("Cluster", "Regression")]
public class ClusterRegressionBenchmarks : IDisposable
{
    private const int EntityCount = 10_000;
    private const int RandomAccessCount = 100;

    private ServiceProvider _serviceProvider;
    private DatabaseEngine _dbe;

    // Entity ID arrays per archetype
    private EntityId[] _svIds;
    private EntityId[] _mixedIds;
    private EntityId[] _idxIds;

    // Pre-selected random-access subset (100 entries from _mixedIds)
    private EntityId[] _randomAccessIds;

    [GlobalSetup]
    public void Setup()
    {

        var name = $"ClusterRegBench_{Environment.ProcessId}";
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
        _dbe.RegisterComponentFromAccessor<AaBenchIdxData>();
        _dbe.InitializeArchetypes();

        var rng = new Random(42);

        // ── Spawn SV entities (AaBenchAnt) ──────────────────────────
        _svIds = new EntityId[EntityCount];
        SpawnInBatches(EntityCount, (tx, i) =>
        {
            var pos = new AaBenchPosition((float)(rng.NextDouble() * 10_000), (float)(rng.NextDouble() * 10_000));
            float angle = (float)(rng.NextDouble() * Math.PI * 2);
            float speed = 20f + (float)(rng.NextDouble() * 60);
            var mov = new AaBenchMovement(MathF.Cos(angle) * speed, MathF.Sin(angle) * speed);
            _svIds[i] = tx.Spawn<AaBenchAnt>(AaBenchAnt.Position.Set(in pos), AaBenchAnt.Movement.Set(in mov));
        });

        // ── Spawn Mixed SV+Versioned entities (AaBenchMixedCluster) ─
        _mixedIds = new EntityId[EntityCount];
        SpawnInBatches(EntityCount, (tx, i) =>
        {
            var pos = new AaBenchPosition((float)(rng.NextDouble() * 10_000), (float)(rng.NextDouble() * 10_000));
            var mov = new AaBenchMovement((float)(rng.NextDouble() * 100), (float)(rng.NextDouble() * 100));
            var health = new AaVcHealth { Current = 100, Max = 100 };
            _mixedIds[i] = tx.Spawn<AaBenchMixedCluster>(
                AaBenchMixedCluster.Position.Set(in pos),
                AaBenchMixedCluster.Movement.Set(in mov),
                AaBenchMixedCluster.Health.Set(in health));
        });

        // ── Spawn Indexed entities (AaBenchIdxUnit) ─────────────────
        _idxIds = new EntityId[EntityCount];
        SpawnInBatches(EntityCount, (tx, i) =>
        {
            var pos = new AaBenchPosition(i, 0);
            var data = new AaBenchIdxData(i, 0); // Sequential Score 0..9999
            _idxIds[i] = tx.Spawn<AaBenchIdxUnit>(AaBenchIdxUnit.Position.Set(in pos), AaBenchIdxUnit.Data.Set(in data));
        });

        // Tick fence to populate zone maps and cluster indexes
        _dbe.WriteTickFence(0);

        // Pre-select 100 entity IDs for random access benchmarks
        _randomAccessIds = new EntityId[RandomAccessCount];
        var stride = EntityCount / RandomAccessCount;
        for (int i = 0; i < RandomAccessCount; i++)
        {
            _randomAccessIds[i] = _mixedIds[i * stride];
        }
    }

    private void SpawnInBatches(int count, Action<Transaction, int> spawnOne)
    {
        int remaining = count;
        int offset = 0;
        while (remaining > 0)
        {
            int batch = Math.Min(1000, remaining);
            remaining -= batch;
            using var tx = _dbe.CreateQuickTransaction();
            for (int i = 0; i < batch; i++)
            {
                spawnOne(tx, offset + i);
            }
            tx.Commit();
            offset += batch;
        }
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        _dbe?.Dispose();
        _serviceProvider?.Dispose();
        try { File.Delete($"ClusterRegBench_{Environment.ProcessId}.bin"); } catch { }
        GC.SuppressFinalize(this);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 1. Cluster iteration — pure SV (Position + Movement)
    // ═══════════════════════════════════════════════════════════════════

    [Benchmark]
    public int ClusterIteration_SV()
    {
        int sum = 0;
        using var tx = _dbe.CreateQuickTransaction();
        var accessor = tx.For<AaBenchAnt>();
        foreach (var cluster in accessor.GetClusterEnumerator())
        {
            var positions = cluster.GetReadOnlySpan<AaBenchPosition>(AaBenchAnt.Position);
            ulong bits = cluster.OccupancyBits;
            while (bits != 0)
            {
                int idx = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                sum += (int)positions[idx].X;
            }
        }
        accessor.Dispose();
        return sum;
    }

    // ═══════════════════════════════════════════════════════════════════
    // 2. Cluster iteration — mixed SV + Versioned
    // ═══════════════════════════════════════════════════════════════════

    [Benchmark]
    public int ClusterIteration_MixedSvVersioned()
    {
        int sum = 0;
        using var tx = _dbe.CreateQuickTransaction();
        var accessor = tx.For<AaBenchMixedCluster>();
        foreach (var cluster in accessor.GetClusterEnumerator())
        {
            var positions = cluster.GetReadOnlySpan(AaBenchMixedCluster.Position);
            var healths = cluster.GetReadOnlySpan(AaBenchMixedCluster.Health);
            ulong bits = cluster.OccupancyBits;
            while (bits != 0)
            {
                int idx = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                sum += (int)positions[idx].X + healths[idx].Current;
            }
        }
        accessor.Dispose();
        return sum;
    }

    // ═══════════════════════════════════════════════════════════════════
    // 3. Random access — Open 100 mixed entities, read Versioned
    // ═══════════════════════════════════════════════════════════════════

    [Benchmark]
    public int ClusterRandomAccess_Mixed()
    {
        int sum = 0;
        using var tx = _dbe.CreateQuickTransaction();
        var accessor = tx.For<AaBenchMixedCluster>();
        for (int i = 0; i < _randomAccessIds.Length; i++)
        {
            var entity = accessor.Open(_randomAccessIds[i]);
            sum += entity.Read(AaBenchMixedCluster.Health).Current;
        }
        accessor.Dispose();
        return sum;
    }

    // ═══════════════════════════════════════════════════════════════════
    // 4. Indexed query — ~1% selectivity (Score >= 9900)
    // ═══════════════════════════════════════════════════════════════════

    [Benchmark]
    public int IndexedQuery_1Percent()
    {
        using var tx = _dbe.CreateQuickTransaction();
        var results = tx.Query<AaBenchIdxUnit>()
            .WhereField<AaBenchIdxData>(d => d.Score >= 9900)
            .Execute();
        return results.Count;
    }

    // ═══════════════════════════════════════════════════════════════════
    // 5. Ordered query — Take(100) over full index range
    // ═══════════════════════════════════════════════════════════════════

    [Benchmark]
    public int OrderedQuery_Take100()
    {
        using var tx = _dbe.CreateQuickTransaction();
        var results = tx.Query<AaBenchIdxUnit>()
            .WhereField<AaBenchIdxData>(d => d.Score >= 0)
            .OrderByField<AaBenchIdxData, int>(d => d.Score)
            .Take(100)
            .ExecuteOrdered();
        return results.Count;
    }

    // ═══════════════════════════════════════════════════════════════════
    // 6. Versioned write + commit — 100 entities via OpenMut
    // ═══════════════════════════════════════════════════════════════════

    [Benchmark]
    public int VersionedWriteCommit()
    {
        using var tx = _dbe.CreateQuickTransaction();
        var accessor = tx.For<AaBenchMixedCluster>();
        for (int i = 0; i < _randomAccessIds.Length; i++)
        {
            var entity = accessor.OpenMut(_randomAccessIds[i]);
            ref var h = ref entity.Write(AaBenchMixedCluster.Health);
            h.Current -= 1;
        }
        accessor.Dispose();
        tx.Commit();
        return _randomAccessIds.Length;
    }
}
