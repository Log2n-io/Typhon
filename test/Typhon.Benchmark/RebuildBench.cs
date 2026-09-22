using System;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Typhon.Schema.Definition;

namespace Typhon.Benchmark;

// ═══════════════════════════════════════════════════════════════════════════════════
// #872 step 2 — startup spatial rebuild: two-pass vs merged, serial vs parallel.
//
// Not a BDN benchmark: each measured call MUTATES the grid and the cluster pool and
// needs both reset between runs, which BDN's iteration model cannot express without
// putting the reset inside the measurement. A plain timed loop with an explicit reset
// measures the thing itself.
//
// Reuses ClQBenchUnit (see ClusterSpatialQueryBenchmarks.cs) — 2D point bounds over a
// 10 000 × 10 000 world at cell size 100.
// ═══════════════════════════════════════════════════════════════════════════════════
internal static class RebuildBench
{
    private const float WorldMax = 10_000f;
    private const int SpawnBatchSize = 10_000;

    public static void Run(int entityCount = 200_000, int rounds = 4, float cellSize = 100f, bool sweep = true)
    {
        var services = new ServiceCollection();
        services
            .AddLogging(c => c.SetMinimumLevel(LogLevel.Error))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddHighResolutionSharedTimer()
            .AddDeadlineWatchdog()
            .AddScopedManagedPagedMemoryMappedFile(o =>
            {
                o.DatabaseName = $"RebuildBench_{Environment.ProcessId}";
                o.DatabaseDirectory = Path.GetTempPath();
                o.DatabaseCacheSize = 1024UL * 1024 * 1024;
            })
            .AddScopedDatabaseEngine(o => { });

        using var provider = services.BuildServiceProvider();
        provider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        using var scope = provider.CreateScope();

        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClQBenchPos>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(
            worldMin: new Vector2(0, 0),
            worldMax: new Vector2(WorldMax, WorldMax),
            cellSize: cellSize));
        dbe.InitializeArchetypes();

        var rnd = new Random(1234);
        var spawned = 0;
        while (spawned < entityCount)
        {
            var batch = Math.Min(SpawnBatchSize, entityCount - spawned);
            using var tx = dbe.CreateQuickTransaction();
            for (var i = 0; i < batch; i++)
            {
                var x = (float)rnd.NextDouble() * (WorldMax - 1);
                var y = (float)rnd.NextDouble() * (WorldMax - 1);
                tx.Spawn<ClQBenchUnit>(ClQBenchUnit.Pos.Set(
                    new ClQBenchPos { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y } }));
            }
            tx.Commit();
            spawned += batch;
        }

        var cs = dbe._archetypeStates[Archetype<ClQBenchUnit>.Metadata.ArchetypeId].ClusterState;
        Console.WriteLine();
        Console.WriteLine("  Startup spatial rebuild — two-pass vs merged");
        Console.WriteLine($"    entities {entityCount:N0}   clusters {cs.ActiveClusterCount:N0}   cores {Environment.ProcessorCount}");
        Console.WriteLine();

        void Reset()
        {
            dbe.SpatialGrid.ResetCellState();
            cs.CellClusterPool = new CellClusterPool(dbe.SpatialGrid.CellCount);
        }

        static double TimeMs(Action a)
        {
            var sw = Stopwatch.StartNew();
            a();
            return sw.Elapsed.TotalMilliseconds;
        }

        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        for (var round = 0; round < rounds; round++)
        {
            // Rotate the order each round. Run in a fixed sequence, whichever variant goes first absorbs that round's cold-page and GC cost every time, which
            // biases the comparison in favour of the two that follow it — small once warm, but free to remove.
            var order = new (string Name, Action Run)[]
            {
                ("twoPass", () => { cs.RebuildCellState(dbe.SpatialGrid); cs.RebuildClusterAabbs(dbe.SpatialGrid); }),
                ("serial", () => cs.RebuildSpatialStateFromData(dbe.SpatialGrid, dbe.EpochManager, 1)),
                ("parallel", () => cs.RebuildSpatialStateFromData(dbe.SpatialGrid, dbe.EpochManager, 0)),
            };

            double twoPass = 0, mergedSerial = 0, mergedParallel = 0;
            for (var k = 0; k < order.Length; k++)
            {
                var (name, run) = order[(round + k) % order.Length];
                Reset();
                var ms = TimeMs(run);
                switch (name)
                {
                    case "twoPass": twoPass = ms; break;
                    case "serial": mergedSerial = ms; break;
                    default: mergedParallel = ms; break;
                }
            }

            var note = round == 0 ? "   (warmup — ignore)" : string.Empty;
            Console.WriteLine($"    two-pass {twoPass,8:F2} ms    merged-serial {mergedSerial,8:F2} ms    merged-parallel {mergedParallel,8:F2} ms{note}");
        }

        if (!sweep)
        {
            Console.WriteLine();
            return;
        }

        Console.WriteLine("    W-sweep (merged):");
        foreach (var w in new[] { 1, 2, 4, 8, 16, 32 })
        {
            var best = double.MaxValue;
            for (var r = 0; r < 5; r++)
            {
                Reset();
                var ms = TimeMs(() => cs.RebuildSpatialStateFromData(dbe.SpatialGrid, dbe.EpochManager, w));
                if (ms < best) { best = ms; }
            }
            Console.WriteLine($"      W={w,2}   {best,7:F2} ms");
        }

        var bestDispatch = double.MaxValue;
        for (var r = 0; r < 50; r++)
        {
            var ms = TimeMs(() => Parallel.For(0, Environment.ProcessorCount,
                new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, _ => { }));
            if (ms < bestDispatch) { bestDispatch = ms; }
        }
        Console.WriteLine($"    bare Parallel.For dispatch ({Environment.ProcessorCount} partitions, empty body): {bestDispatch * 1000:F1} us");
        Console.WriteLine();
    }

    /// <summary>
    /// Scaling matrix. Answers the question the single-shape run cannot: does the parallel rebuild scale with the SIZE of the database, or with the DENSITY
    /// of its clusters? The map is <c>O(entities)</c> and the serial reduce is <c>O(clusters)</c>, so the speedup ceiling is set by entities-per-cluster —
    /// which grows with density, not with total size.
    /// </summary>
    public static void RunMatrix()
    {
        Console.WriteLine();
        Console.WriteLine("  Rebuild scaling — size vs density");
        Console.WriteLine("  (cellSize fixes how many entities share a cell, and therefore how full each 64-slot cluster is)");
        Console.WriteLine();

        // Constant density, growing size: entities and cells scale together, so entities-per-cluster is held roughly fixed.
        Console.WriteLine("  ── constant density (~20 entities/cell), growing size ──");
        Run(100_000, rounds: 3, cellSize: 100f, sweep: false);
        Run(200_000, rounds: 3, cellSize: 141f, sweep: false);
        Run(400_000, rounds: 3, cellSize: 200f, sweep: false);

        // Constant size, growing density: same entity count packed into progressively fewer, larger cells.
        Console.WriteLine("  ── constant size (200 K entities), growing density ──");
        Run(200_000, rounds: 3, cellSize: 100f, sweep: false);
        Run(200_000, rounds: 3, cellSize: 250f, sweep: false);
        Run(200_000, rounds: 3, cellSize: 500f, sweep: false);
        Run(200_000, rounds: 3, cellSize: 1000f, sweep: false);
    }
}