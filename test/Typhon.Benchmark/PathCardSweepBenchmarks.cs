using System;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Typhon.Schema.Definition;

namespace Typhon.Benchmark;

// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// Path A (selective B+Tree) vs Path B (zone-map prune + SoA scan) across KEY CARDINALITY x LAYOUT x SELECTIVITY.
//
// The prior sweep varied selectivity and found Path A winning on exactly one shape: LowCard (50 distinct keys). After
// the range-enforced-predicate skip (9d053866) that win grew to 1.37-1.59x at EVERY selectivity, which says the axis
// that matters is not selectivity at all but FAN-OUT — entities per distinct key.
//
// Physical model: Path A pays per KEY in range (descend + leaf walk + one buffer per key); Path B pays per CLUSTER
// scanned (zone check + a 64-slot SIMD pass). High fan-out means few keys cover many entities, so Path A's per-key
// cost amortizes. Fan-out 1 (a unique index) is Path A's worst case: one tree entry per entity.
//
//   DistinctKeys   10     50     250    1250   10000
//   fan-out        1000   200    40     8      1
//
// Layout decides whether Path B's zone maps can prune:
//   Clustered  score = i / fanout   equal keys adjacent -> one key lives in one cluster -> Path B prunes perfectly
//   Strided    score = i % keys     equal keys spread   -> every cluster holds every key -> Path B prunes nothing
//
// Selectivity is expressed as a fraction of the KEY space, so with uniform fan-out it is also the fraction of entities:
// MatchPct 10 matches ceil(keys/10) keys = 10% of entities. At DistinctKeys=10 the 1% cell cannot resolve below one key
// so it clamps to 10% — it becomes a duplicate of the 10% cell, which is a free self-consistency check rather than a
// data point.
// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════

[Component("Typhon.Benchmark.PathCard.Data", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct PathCardData
{
    [Index(AllowMultiple = true)] public int Score;
    public int Payload;

    public PathCardData(int score, int payload)
    {
        Score = score;
        Payload = payload;
    }
}

[Archetype]
class PathCardUnit : Archetype<PathCardUnit>
{
    public static readonly Comp<PathCardData> Data = Register<PathCardData>();
}

public enum KeyLayout
{
    Clustered,
    Strided
}

[SimpleJob(warmupCount: 3, iterationCount: 8)]
[BenchmarkCategory("PathCardSweep")]
public class PathCardSweepBenchmarks : IDisposable
{
    private const int EntityCount = 10_000;

    [Params(KeyLayout.Clustered, KeyLayout.Strided)]
    public KeyLayout Layout;

    /// <summary>
    /// Distinct values of Score. Fan-out is EntityCount / DistinctKeys: 1000, 200, 125, 80, 40, 8, 1 — the decisive-loss band, the marginal band and the
    /// win band, in one grid. <c>EcsQuery.MinFanOutClustersForSelectiveScan</c> is derived from exactly this sweep; re-run it before changing that constant.
    /// </summary>
    [Params(10, 50, 80, 125, 250, 1250, 10_000)]
    public int DistinctKeys;

    /// <summary>Percentage of the key space (hence of entities) the range matches.</summary>
    [Params(1, 10)]
    public int MatchPct;

    private ServiceProvider _serviceProvider;
    private DatabaseEngine _dbe;
    private int _threshold;
    private int _expectedMatches;

    [GlobalSetup]
    public void Setup()
    {
        var name = $"PathCard_{Environment.ProcessId}_{Layout}_{DistinctKeys}_{MatchPct}";
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

        _dbe.RegisterComponentFromAccessor<PathCardData>();
        _dbe.InitializeArchetypes();

        var fanout = EntityCount / DistinctKeys;

        var remaining = EntityCount;
        var offset = 0;
        while (remaining > 0)
        {
            var batch = Math.Min(1000, remaining);
            remaining -= batch;
            using var tx = _dbe.CreateQuickTransaction();
            for (var i = 0; i < batch; i++)
            {
                var idx = offset + i;
                var score = Layout == KeyLayout.Clustered ? idx / fanout : idx % DistinctKeys;
                var d = new PathCardData(score, idx);
                tx.Spawn<PathCardUnit>(PathCardUnit.Data.Set(in d));
            }
            tx.Commit();
            offset += batch;
        }

        _dbe.WriteTickFence(0);

        // Match the top `MatchPct` percent of the key space. Keys are 0..DistinctKeys-1 with exactly `fanout` entities
        // each, so the realised match count is exact arithmetic rather than something to be discovered at runtime.
        var keysMatched = Math.Max(1, (int)Math.Ceiling(DistinctKeys * (MatchPct / 100.0)));
        _threshold = DistinctKeys - keysMatched;
        _expectedMatches = keysMatched * fanout;

        // The whole premise is that EntryCount reports distinct keys for an AllowMultiple index — the signal the planner
        // will read. Assert it here rather than trusting the docstring, and assert the query really matches what the
        // arithmetic says, so a mis-shaped cell fails loudly instead of quietly measuring the wrong thing.
        var meta = ArchetypeRegistry.GetMetadata<PathCardUnit>();
        var clusterState = _dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        var entryCount = clusterState.IndexSlots[0].Fields[0].Index.EntryCount;
        if (entryCount != DistinctKeys)
        {
            throw new InvalidOperationException($"EntryCount {entryCount} != DistinctKeys {DistinctKeys} — the cardinality signal is not what it claims.");
        }

        var actual = RunQuery();
        if (actual != _expectedMatches)
        {
            throw new InvalidOperationException($"Query matched {actual}, expected {_expectedMatches} (keys={keysMatched}, fanout={fanout}).");
        }
    }

    private int RunQuery()
    {
        using var tx = _dbe.CreateQuickTransaction();
        var results = tx.Query<PathCardUnit>()
            .WhereField<PathCardData>(d => d.Score >= _threshold)
            .Execute();
        return results.Count;
    }

    [Benchmark(Baseline = true)]
    public int PathA()
    {
        QueryPathProbe.Forced = ClusterScanPath.Selective;
        var n = RunQuery();
        QueryPathProbe.Forced = ClusterScanPath.Planner;
        return n;
    }

    [Benchmark]
    public int PathB()
    {
        QueryPathProbe.Forced = ClusterScanPath.FullScan;
        var n = RunQuery();
        QueryPathProbe.Forced = ClusterScanPath.Planner;
        return n;
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        _serviceProvider?.Dispose();
        _serviceProvider = null;
        GC.SuppressFinalize(this);
    }
}
