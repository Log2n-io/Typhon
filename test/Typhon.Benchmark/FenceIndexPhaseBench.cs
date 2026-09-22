using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Typhon.Schema.Definition;

namespace Typhon.Benchmark;

// ─── A cluster-eligible archetype with BOTH a spatial index (so entities migrate) and a B+Tree index (so migration stages value updates) ───
[Component("Typhon.Benchmark.FenceIdx.Pos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct FpBenchPos
{
    [Field]
    [SpatialIndex]
    public AABB2F Bounds;

    /// <summary>Unique per entity, so the B+Tree index is the ordinary unique kind rather than the buffer-backed one.</summary>
    [Field]
    [Index]
    public int Tag;
}

[Archetype]
partial class FpBenchUnit : Archetype<FpBenchUnit>
{
    public static readonly Comp<FpBenchPos> Pos = Register<FpBenchPos>();
}

/// <summary>
/// <c>AC-6.5</c> measured on the PHASE, driven by the real scheduler, rather than on the primitive under hand-rolled threads.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists alongside <see cref="FenceParallelBench"/>.</b> That harness spawns W threads around <c>BTree.UpdateValues</c> on a bare tree. It is a
/// fair measurement of the descent's scaling and a poor one of the phase, because the phase does not work that way: it is a
/// <c>ChunkedParallel</c> system on the fence DAG, and the number of chunks is chosen by <c>FenceWorkPlan.ComputeMaxChunks</c> from the cost model, not by
/// whoever is holding the stopwatch. Everything between "the planner decided" and "the descent ran" — bin-packing, dependency resolution, the per-chunk
/// ChangeSet rent/release, the EpochGuard, and the barriers either side of the phase — is invisible to the other harness and is charged here.
/// </para>
/// <para>
/// <b>What is varied is <c>RuntimeOptions.WorkerCount</c>, which is the only W that exists in production.</b> The phase's own per-chunk wall-time totals are
/// read back through <c>TyphonRuntime.LastIndexMassUpdateStats</c> — the same instrumentation <c>LiveFenceCostModel</c> calibrates from — so the number
/// reported is the phase's, not a stopwatch wrapped around something adjacent to it.
/// </para>
/// </remarks>
internal static class FenceIndexPhaseBench
{
    private const float WorldMax = 10_000f;
    private const float CellSize = 100f;

    public static void Run(int entityCount = 400_000, int migrantsPerTick = 100_000, float bulkMinPerBucket = 0f)
    {
        Console.WriteLine();
        Console.WriteLine($"#872 — IndexMassUpdate + EntityMapUpdate phases under the scheduler ({entityCount:N0} entities, "
            + $"{migrantsPerTick:N0} migrants/tick, bulkMin={bulkMinPerBucket})");
        Console.WriteLine();

        // Discarded: tiered compilation is process-wide, and without it the first W row measures the runtime warming up rather than the phase.
        Measure(entityCount, migrantsPerTick, workerCount: 4, ticks: 6, bulkMinPerBucket);

        Console.WriteLine($"{"W",3} {"chunks",7} {"applied",9} {"span us",10} {"cpu us",10} {"span x",8} {"eff",6} {"ns/update",10} "
            + $"{"map us",9} {"map cpu",9} {"mig cpu",9} {"em ns/e",9}");
        Console.WriteLine(new string('-', 100));

        double phaseAtOne = 0;
        foreach (var w in new[] { 1, 2, 4, 8, 16 })
        {
            var r = Measure(entityCount, migrantsPerTick, w, ticks: 12, bulkMinPerBucket);
            if (w == 1)
            {
                phaseAtOne = r.SpanUs;
            }

            var speedup = phaseAtOne / r.SpanUs;
            Console.WriteLine(
                $"{w,3} {r.Chunks,7} {r.Applied,9:N0} {r.SpanUs,10:F1} {r.CpuUs,10:F1} {speedup,7:F2}x {100.0 * speedup / w,5:F0}% "
                + $"{r.SpanUs * 1000.0 / Math.Max(1, r.Applied),10:F1} {r.MapSpanUs,9:F1} {r.MapCpuUs,9:F1} {r.MigCpuUs,9:F1} "
                + $"{(r.MapCpuUs + r.MigCpuUs) * 1000.0 / Math.Max(1, r.Applied),9:F1}");
        }

        Console.WriteLine();
        Console.WriteLine("  span us = WALL CLOCK from the start of the phase's Prepare (merge + sort + leaf-snap) to the last chunk finishing.");
        Console.WriteLine("  cpu us  = summed per-chunk wall time. Shown only to make the difference visible; it is CPU consumed, not time taken.");
        Console.WriteLine("  chunks  = what the planner chose from the cost model, NOT W: ceil(totalCost / 200us), capped at the item count.");
        Console.WriteLine("  map us  = the EntityMapUpdate phase's own span on the same tick (#872 step 7), Prepare included; map ns/e is per migrant.");
    }

    private readonly struct Result
    {
        public readonly int Chunks;
        public readonly long Applied;
        public readonly double SpanUs;
        public readonly double CpuUs;
        public readonly double MapSpanUs;
        public readonly double MapCpuUs;
        public readonly double MigCpuUs;
        public readonly long MapApplied;

        public Result(int chunks, long applied, double spanUs, double cpuUs, double mapSpanUs, double mapCpuUs, double migCpuUs, long mapApplied)
        {
            Chunks = chunks;
            Applied = applied;
            SpanUs = spanUs;
            CpuUs = cpuUs;
            MapSpanUs = mapSpanUs;
            MapCpuUs = mapCpuUs;
            MigCpuUs = migCpuUs;
            MapApplied = mapApplied;
        }
    }

    /// <summary>
    /// Spawns a world, then drives <paramref name="ticks"/> ticks in each of which a system moves <paramref name="migrants"/> entities across cell
    /// boundaries, and reports the best tick's phase cost.
    /// </summary>
    /// <remarks>
    /// Driven by the runtime's own timer (<c>Start</c> / <c>Shutdown</c>) because there is no single-tick entry point, and the per-tick phase numbers are
    /// sampled from inside the next tick rather than timed from outside. Best-of across ticks, so a tick the timer coalesced or that landed on a GC pause
    /// cannot set the reported figure.
    /// </remarks>
    private static Result Measure(int entityCount, int migrants, int workerCount, int ticks, float bulkMinPerBucket)
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
                o.DatabaseName = $"FenceIdxPhase_{Environment.ProcessId}_{workerCount}_{ticks}";
                o.DatabaseDirectory = Path.GetTempPath();
                o.DatabaseCacheSize = 2048UL * 1024 * 1024 / 2;
            })
            .AddScopedDatabaseEngine(o => { });

        using var provider = services.BuildServiceProvider();
        provider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        using var scope = provider.CreateScope();

        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<FpBenchPos>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(WorldMax, WorldMax), CellSize));
        dbe.InitializeArchetypes();

        var ids = new List<EntityId>(entityCount);
        var rnd = new Random(987654);
        var spawned = 0;
        while (spawned < entityCount)
        {
            var batch = Math.Min(8_192, entityCount - spawned);
            using var tx = dbe.CreateQuickTransaction();
            for (var i = 0; i < batch; i++)
            {
                var x = (float)rnd.NextDouble() * (WorldMax - 1);
                var y = (float)rnd.NextDouble() * (WorldMax - 1);
                ids.Add(tx.Spawn<FpBenchUnit>(FpBenchUnit.Pos.Set(new FpBenchPos
                {
                    Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y },
                    Tag = spawned + i,
                })));
            }

            tx.Commit();
            spawned += batch;
        }

        var cursor = 0;
        var samples = new List<(double SpanUs, double CpuUs, long Units, int Chunks, double MapSpanUs, double MapCpuUs, double MigCpuUs, long MapUnits)>();
        var ticksObserved = 0;

        TyphonRuntime runtime = null;
        runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Churn");

            // Ordered FIRST, and it reads the PREVIOUS tick's numbers. The fence is the scheduler's tick-end callback, so it has not run yet this tick and
            // no system can observe its own tick's fence — sampling here is the earliest a value can be read at all.
            dag.CallbackSystem("Sample", _ =>
            {
                var (mapSpanTicks, mapCpuTicks, mapUnits, _) = runtime.LastEntityMapUpdateStats;
                var (_, migCpuTicks, _, _) = runtime.LastMigrateStats;
                var (spanTicks, cpuTicks, units, chunks) = runtime.LastIndexMassUpdateStats;
                if (units > 0 && spanTicks > 0)
                {
                    var toUs = 1_000_000.0 / Stopwatch.Frequency;
                    samples.Add((spanTicks * toUs, cpuTicks * toUs, units, chunks, mapSpanTicks * toUs, mapCpuTicks * toUs, migCpuTicks * toUs, mapUnits));
                }

                Interlocked.Increment(ref ticksObserved);
            });

            // Moves `migrants` entities one whole cell to the right, wrapping. A cell is 100 units, so every one of them crosses a boundary and the fence's
            // Prep phase detects it as a migration.
            dag.CallbackSystem("Move", ctx =>
            {
                var tx = ctx.Transaction;
                for (var i = 0; i < migrants; i++)
                {
                    var id = ids[(cursor + i) % ids.Count];
                    ref var pos = ref tx.OpenMut(id).Write(FpBenchUnit.Pos);
                    var nx = pos.Bounds.MinX + CellSize;
                    if (nx >= WorldMax - 1)
                    {
                        nx -= WorldMax - 1;
                    }

                    pos.Bounds = new AABB2F { MinX = nx, MinY = pos.Bounds.MinY, MaxX = nx, MaxY = pos.Bounds.MaxY };
                }

                cursor += migrants;
            }, after: "Sample");
        }, new RuntimeOptions
        {
            WorkerCount = workerCount, BaseTickRate = 200, AdaptiveFenceCost = false,
            EntityMapBulkMinEntriesPerBucket = bulkMinPerBucket,
        });

        using var runtimeScope = runtime;
        runtime.Start();
        SpinWait.SpinUntil(() => Volatile.Read(ref ticksObserved) >= ticks, TimeSpan.FromSeconds(60));
        runtime.Shutdown();

        var bestSpan = double.MaxValue;
        var cpuUs = 0.0;
        var chunks = 0;
        long applied = 0;
        var mapSpanUs = 0.0;
        var mapCpuUs = 0.0;
        var migCpuUs = 0.0;
        long mapApplied = 0;

        // Best of the samples, and the first two are discarded: the first tick spawns nothing to migrate yet, and the second is the first to run the phase
        // at all, so it carries the page faults for every index chunk the batch touches.
        for (var i = 2; i < samples.Count; i++)
        {
            if (samples[i].SpanUs < bestSpan)
            {
                bestSpan = samples[i].SpanUs;
                cpuUs = samples[i].CpuUs;
                mapSpanUs = samples[i].MapSpanUs;
                mapCpuUs = samples[i].MapCpuUs;
                migCpuUs = samples[i].MigCpuUs;
                mapApplied = samples[i].MapUnits;
                chunks = samples[i].Chunks;
                applied = samples[i].Units;
            }
        }

        if (bestSpan == double.MaxValue)
        {
            throw new InvalidOperationException(
                "no tick staged an index update, so the phase never ran and the numbers would be about nothing. "
                + "The workload must actually move entities across cell boundaries on an archetype with an indexed field.");
        }

        return new Result(chunks, applied, bestSpan, cpuUs, mapSpanUs, mapCpuUs, migCpuUs, mapApplied);
    }
}
