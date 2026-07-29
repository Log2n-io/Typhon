using System;
using System.Numerics;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Typhon.Schema.Definition;

namespace Typhon.Benchmark;

// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// ACCESS-PATH COMPARISON — the same work, reached three different ways.
//
// Typhon can reach entity data by three structurally different routes, and they differ by ~100x. Published per-op numbers
// are meaningless unless they name which route they came from, so this class measures all three ON THE SAME FIXTURE, IN
// THE SAME RUN — the only condition under which the ratios are trustworthy:
//
//   1. POINT ACCESS      — hand the engine one EntityId; it probes the EntityMap, checks visibility, resolves the cluster
//                          slot. One hash probe PER ENTITY. This is all a key-value store can do.
//   2. CLUSTER SWEEP     — walk the archetype's clusters in storage order, take one SoA span per component, step the
//                          occupancy bitmask. NO lookup at all: the data is read where it physically sits.
//   3. CELL-SCOPED SWEEP — same as (2), but cluster-by-cluster within one spatial CELL at a time. This is the topology a
//                          game tick runs on (AntHill): every entity in a cluster is guaranteed to be in the same cell,
//                          so "process everything near here" becomes "walk this cell's cluster list" with no per-entity
//                          position test. The runtime normally hands a system a pre-filtered cluster-id range via
//                          TickContext.ClusterIds; here we build the same list from the per-cell index directly.
//
// WHAT IS DELIBERATELY ABSENT: "bulk". Typhon's batch surface (SpawnBatch / SpawnBatchAllocate / SpawnBatchWriteAll /
// DestroyBatch / BeginBulkLoad) is entirely LIFECYCLE + INGEST. There is no batch read or batch update over existing
// entities, so "bulk" is not a fourth route to the data measured here and no honest row for it exists. The loop-vs-batch
// comparison that DOES exist lives in SpawnBatchBenchmarks (spawn) and EcsLifecycleBenchmarks.DestroyBatch_Sv (destroy).
//
// COMPARABILITY: Point_ByEntityId, ClusterSweep_WholeArchetype and ClusterSweep_CellByCell each touch EXACTLY
// EntityCount entities and perform the identical per-entity arithmetic, so their means are directly comparable and BDN's
// Ratio column is the answer. ClusterSweep_OneCellNeighbourhood is the odd one out ON PURPOSE — it touches only a 3x3
// cell block, so its mean is NOT comparable per-invocation; it is here to show SELECTIVITY, which is the actual reason
// to use cells. See its own note.
//
// REPORTING: OperationsPerInvoke must be a compile-time constant and therefore cannot track [Params], so the means below
// are PER FULL PASS (all EntityCount entities), not per entity. Divide by EntityCount for ns/entity. The ratios between
// rows are unaffected by that divisor, which is what this class is for.
//
// NOT Regression-tracked: two [Params] values x four routes, with a 100K spatial spawn in setup, would cost more suite
// wall time than the guard is worth. The per-entity sweep and point costs ARE guarded, by StorageModeProofBenchmarks.
//
// Run: dotnet run -c Release -- --filter '*EcsAccessPath*'
// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════

[Component("Typhon.Benchmark.Ap.Pos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct ApBenchPos
{
    [Field]
    [SpatialIndex(5.0f)]   // margin = fat-AABB movement hysteresis; nothing moves here, so it never triggers
    public AABB2F Bounds;
}

[Component("Typhon.Benchmark.Ap.Payload", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct ApBenchPayload
{
    [Field] public float Value;
}

[Archetype]
partial class ApBenchUnit : Archetype<ApBenchUnit>
{
    public static readonly Comp<ApBenchPos> Pos = Register<ApBenchPos>();
    public static readonly Comp<ApBenchPayload> Payload = Register<ApBenchPayload>();
}

[SimpleJob(warmupCount: 3, iterationCount: 5)]
[MemoryDiagnoser]
[BenchmarkCategory("AccessPath")]
public class EcsAccessPathBenchmarks : IDisposable
{
    private const float WorldSize = 10_000f;
    private const float CellSize = 100f;

    /// <summary>Occupied cells form a CellsPerSide x CellsPerSide block, so clusters stay densely packed instead of one entity per cell.</summary>
    private const int CellsPerSide = 16;
    private const int CellCount = CellsPerSide * CellsPerSide;   // 256

    private const int SpawnBatchSize = 1_000;

    /// <summary>Scratch buffer for one cell's cluster-id list. A cell holds EntityCount/256 entities in clusters of 8-64 slots, so this is ample.</summary>
    private const int MaxClustersPerCell = 8192;

    [Params(10_000, 100_000)]
    public int EntityCount;

    private ServiceProvider _sp;
    private DatabaseEngine _dbe;
    private EntityId[] _ids;
    private int[] _cellKeys;              // the 256 occupied cell keys, in spawn order
    private int[] _clusterScratch;        // reused across cells; never resized during a measurement
    private ArchetypeClusterState _clusterState;

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
              o.DatabaseName = $"ApBench_{EntityCount}_{Environment.ProcessId}";
              // 200K pages x 8 KiB = 1.6 GiB. Do NOT raise past ~256K pages: the byte size overflows Int32 and the engine
              // throws "Size must be positive" (learned in ClusterScanScalingBenchmarks).
              o.DatabaseCacheSize = (ulong)(200L * 1024 * PagedMMF.PageSize);
              o.TestMode = true;
              o.PagesDebugPattern = false;
          })
          .AddInMemoryWalEngine();

        _sp = sc.BuildServiceProvider();
        _sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
        _dbe = _sp.GetRequiredService<DatabaseEngine>();

        _dbe.RegisterComponentFromAccessor<ApBenchPos>();
        _dbe.RegisterComponentFromAccessor<ApBenchPayload>();
        _dbe.ConfigureSpatialGrid(new SpatialGridConfig(
            worldMin: new Vector2(0, 0),
            worldMax: new Vector2(WorldSize, WorldSize),
            cellSize: CellSize));
        _dbe.InitializeArchetypes();

        _ids = new EntityId[EntityCount];
        _cellKeys = new int[CellCount];
        _clusterScratch = new int[MaxClustersPerCell];

        // Spawn GROUPED BY CELL. Grouping matters: the cluster-cell coherence invariant means a cluster only ever holds
        // entities of one cell, so spawning cell-by-cell fills each cluster densely. Interleaving cells would instead
        // leave every cell's clusters partly empty, which would flatter the point path and penalise both sweeps.
        int basePer = EntityCount / CellCount;
        int remainder = EntityCount % CellCount;
        int spawned = 0;
        long tickNum = 1;

        for (int c = 0; c < CellCount; c++)
        {
            int cx = c % CellsPerSide;
            int cy = c / CellsPerSide;
            // Inset from the cell edge so no entity sits within the migration hysteresis band of a boundary.
            float baseX = cx * CellSize + CellSize * 0.5f;
            float baseY = cy * CellSize + CellSize * 0.5f;
            _cellKeys[c] = 0; // filled after spawn, from the grid

            int inThisCell = basePer + (c < remainder ? 1 : 0);
            int placed = 0;
            while (placed < inThisCell)
            {
                int batch = Math.Min(SpawnBatchSize, inThisCell - placed);
                using (var tx = _dbe.CreateQuickTransaction())
                {
                    for (int i = 0; i < batch; i++)
                    {
                        // Deterministic sub-cell jitter, bounded well inside the cell (+-20 of a 100-wide cell).
                        int n = placed + i;
                        float x = baseX + (n % 41 - 20);
                        float y = baseY + (n / 41 % 41 - 20);
                        var pos = new ApBenchPos { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y } };
                        var payload = new ApBenchPayload { Value = n };
                        _ids[spawned + i] = tx.Spawn<ApBenchUnit>(ApBenchUnit.Pos.Set(in pos), ApBenchUnit.Payload.Set(in payload));
                    }
                    tx.Commit();
                }
                // Fence between batches so the page cache drains dirty pages; without it a 100K spawn hits
                // PageCacheBackpressureTimeoutException.
                _dbe.WriteTickFence(tickNum++);
                placed += batch;
                spawned += batch;
            }
        }

        // Final fence: settles the per-cell cluster index and cluster AABBs before any measurement.
        _dbe.WriteTickFence(tickNum);

        // Resolve the internals needed for the cell-scoped route. Enumeration itself is public
        // (EntityAccessor.GetClusterEnumerator<T>(int[], int, int)); only the cell -> cluster-id list is internal,
        // reachable via the existing InternalsVisibleTo("Typhon.Benchmark") in Typhon.Engine's AssemblyInfo.
        var meta = ArchetypeRegistry.GetMetadata<ApBenchUnit>();
        _clusterState = _dbe._archetypeStates[meta.ArchetypeId].ClusterState;

        var grid = _clusterState.Grid;
        for (int c = 0; c < CellCount; c++)
        {
            float baseX = c % CellsPerSide * CellSize + CellSize * 0.5f;
            float baseY = c / CellsPerSide * CellSize + CellSize * 0.5f;
            _cellKeys[c] = grid.WorldToCellKey(baseX, baseY);
        }

        ValidateRoutesAgree();
    }

    /// <summary>
    /// Assert that the three full-set routes visit the SAME entities, by comparing their checksums.
    /// <para>
    /// This is not optional hygiene — it is what makes the ratios meaningful. The routes are only comparable if they do
    /// equal work, and every failure mode that would silently break that (a cell missing from the per-cell index, a
    /// cluster dropped by the scoped enumerator, a partially-drained migration leaving entities unreachable by cell)
    /// shows up as a throughput WIN for the broken route. A benchmark that accumulates a checksum purely to defeat
    /// dead-code elimination, and never checks it, cannot tell "fast" from "skipped a third of the data".
    /// </para>
    /// Runs once per <see cref="EntityCount"/> in GlobalSetup, so it costs nothing measurable.
    /// </summary>
    private void ValidateRoutesAgree()
    {
        float point = Point_ByEntityId();
        float sweep = ClusterSweep_WholeArchetype();
        float cells = ClusterSweep_CellByCell();

        // Exact equality is the right test despite float: all three sum the SAME values, and the cluster routes sum them
        // in the same storage order. A mismatch means a different entity SET, not a rounding difference.
        if (point != sweep || point != cells)
        {
            throw new InvalidOperationException(
                $"Access routes disagree at EntityCount={EntityCount} — the benchmark is not comparing equal work. " +
                $"point={point}, wholeArchetypeSweep={sweep}, cellByCell={cells}.");
        }
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // The three routes. Identical per-entity work (accumulate Payload.Value); identical entity count (EntityCount).
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// ROUTE 1 — point access. One EntityMap probe + visibility check + slot resolve PER ENTITY. The baseline, because it
    /// is the only route a key-value store has and therefore the one competitive numbers are quoted at.
    /// </summary>
    [Benchmark(Baseline = true)]
    public float Point_ByEntityId()
    {
        using var tx = _dbe.CreateQuickTransaction();
        float sum = 0;
        for (int i = 0; i < EntityCount; i++)
        {
            sum += tx.Open(_ids[i]).Read(ApBenchUnit.Payload).Value;
        }
        return sum;
    }

    /// <summary>
    /// ROUTE 2 — flat cluster sweep over the whole archetype. No lookup of any kind: one SoA span per cluster, stepped by
    /// the occupancy bitmask. This is the ~100x row.
    /// </summary>
    [Benchmark]
    public float ClusterSweep_WholeArchetype()
    {
        using var tx = _dbe.CreateQuickTransaction();
        var e = tx.GetClusterEnumerator<ApBenchUnit>();
        float sum = 0;
        foreach (var cluster in e)
        {
            var payloads = cluster.GetReadOnlySpan(ApBenchUnit.Payload);
            ulong bits = cluster.OccupancyBits;
            while (bits != 0)
            {
                int idx = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                sum += payloads[idx].Value;
            }
        }
        e.Dispose();
        return sum;
    }

    /// <summary>
    /// ROUTE 3 — the same total work, but reached CELL BY CELL: for each cell, look up its cluster list and sweep it.
    /// Touches exactly the same EntityCount entities as route 2, so the delta against it is the pure cost of the spatial
    /// topology — per-cell index lookup, cluster-list copy, enumerator setup — amortised over the cell's entities.
    /// <para>
    /// This is what a game tick does. The runtime normally supplies the cluster-id range through TickContext.ClusterIds;
    /// building it here from the per-cell index measures the same enumeration against a per-cell list.
    /// </para>
    /// </summary>
    [Benchmark]
    public float ClusterSweep_CellByCell()
    {
        using var tx = _dbe.CreateQuickTransaction();
        float sum = 0;
        for (int c = 0; c < CellCount; c++)
        {
            var clusters = _clusterState.CellClusterPool.GetClusters(_cellKeys[c]);
            if (clusters.Length == 0)
            {
                continue;
            }

            // GetClusters hands back a span over the pool's storage; the enumerator takes int[]. Copy into a reused
            // buffer — a handful of ints per cell, and it is counted in the measurement rather than hidden in setup.
            clusters.CopyTo(_clusterScratch);
            var e = tx.GetClusterEnumerator<ApBenchUnit>(_clusterScratch, 0, clusters.Length);
            foreach (var cluster in e)
            {
                var payloads = cluster.GetReadOnlySpan(ApBenchUnit.Payload);
                ulong bits = cluster.OccupancyBits;
                while (bits != 0)
                {
                    int idx = BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    sum += payloads[idx].Value;
                }
            }
            e.Dispose();
        }
        return sum;
    }

    /// <summary>
    /// SELECTIVITY, not throughput — the reason cells exist. Sweeps a 3x3 cell neighbourhood ("everything near me"),
    /// i.e. 9/256 of the world, so it touches roughly <c>EntityCount * 9 / 256</c> entities.
    /// <para>
    /// <b>Its mean is NOT comparable with the three rows above</b>, which each process the full EntityCount — it is a
    /// different quantity of work. What it shows is that scoping to a neighbourhood costs proportionally, with no
    /// per-entity position test and no index probe: the cell list IS the filter. Divide by the touched-entity count
    /// (returned via the checksum's magnitude, or computed as above) for a ns/entity figure comparable to route 3.
    /// </para>
    /// </summary>
    [Benchmark]
    public float ClusterSweep_OneCellNeighbourhood()
    {
        using var tx = _dbe.CreateQuickTransaction();
        float sum = 0;
        const int CenterX = CellsPerSide / 2;
        const int CenterY = CellsPerSide / 2;
        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                int c = (CenterY + dy) * CellsPerSide + (CenterX + dx);
                var clusters = _clusterState.CellClusterPool.GetClusters(_cellKeys[c]);
                if (clusters.Length == 0)
                {
                    continue;
                }

                clusters.CopyTo(_clusterScratch);
                var e = tx.GetClusterEnumerator<ApBenchUnit>(_clusterScratch, 0, clusters.Length);
                foreach (var cluster in e)
                {
                    var payloads = cluster.GetReadOnlySpan(ApBenchUnit.Payload);
                    ulong bits = cluster.OccupancyBits;
                    while (bits != 0)
                    {
                        int idx = BitOperations.TrailingZeroCount(bits);
                        bits &= bits - 1;
                        sum += payloads[idx].Value;
                    }
                }
                e.Dispose();
            }
        }
        return sum;
    }

    public void Dispose()
    {
        _dbe?.Dispose();
        _sp?.Dispose();
        _dbe = null;
        _sp = null;
        GC.SuppressFinalize(this);
    }
}
