using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace Typhon.Benchmark;

// SingleVersion component for PTA write benchmarks (PTA cannot write Versioned components)
[Component("Typhon.Benchmark.PD.SvData", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct PdSvData
{
    [Field] public int Value;
    [Field] public long Timestamp;
}

[Archetype]
class PdSvArch : Archetype<PdSvArch>
{
    public static readonly Comp<PdSvData> Data = Register<PdSvData>();
}

// Transient component for storage mode scaling comparison
[Component("Typhon.Benchmark.PD.TransientData", 1, StorageMode = StorageMode.Transient)]
[StructLayout(LayoutKind.Sequential)]
struct PdTransientData
{
    [Field] public int Value;
    [Field] public long Timestamp;
}

[Archetype]
class PdTransientArch : Archetype<PdTransientArch>
{
    public static readonly Comp<PdTransientData> Data = Register<PdTransientData>();
}

/// <summary>
/// Validates the parallel dispatch optimization (#211):
///   1. Prepare phase: O(N) eliminated — compare PTA vs Transaction path at same entity counts
///   2. Per-chunk overhead: Transaction vs PTA
///   3. End-to-end parallel speedup at 1..28 workers with consistent entity count
/// </summary>
static class ParallelDispatchBenchmark
{
    private const int BenchTickRate = 100_000; // Max rate — processing time dominates

    public static void Run()
    {
        Console.WriteLine("═══════════════════════════════════════════════════════════════");
        Console.WriteLine("  Parallel Dispatch Benchmark (#211)");
        Console.WriteLine("═══════════════════════════════════════════════════════════════");
        Console.WriteLine($"  CPU: {Environment.ProcessorCount} logical cores");
        Console.WriteLine();

        // ── Benchmark 1: Prepare phase — PTA vs Transaction at various entity counts ──
        // Both paths do identical per-entity work (read-only). The delta = materialization cost saved.
        Console.WriteLine("── 1. Prepare Phase: PTA (O(1)) vs Transaction (O(N)) ──");
        Console.WriteLine("   Same read-only work per entity; difference = materialization overhead eliminated.");
        Console.WriteLine($"  {"Entities",10} {"PTA (us)",10} {"Tx (us)",10} {"Delta (us)",12} {"Delta/ent (ns)",15}");

        foreach (var n in new[] { 1_000, 5_000, 20_000, 50_000 })
        {
            var ptaUs = Measure(n, workers: 2, versioned: false, write: false, warmup: 30, measured: 80);
            var txUs = Measure(n, workers: 2, versioned: true, write: false, warmup: 30, measured: 80);
            var delta = txUs - ptaUs;
            var deltaNs = n > 0 ? delta * 1000.0 / n : 0;
            Console.WriteLine($"  {n,10:N0} {ptaUs,10:F1} {txUs,10:F1} {delta,12:F1} {deltaNs,15:F2}");
        }

        Console.WriteLine();

        // ── Benchmark 2: Per-chunk overhead — Transaction vs PTA with read workload ──
        Console.WriteLine("── 2. Per-Chunk Overhead (20K entities, 4 workers = 4 chunks) ──");

        var txChunk = Measure(20_000, workers: 4, versioned: true, write: false, warmup: 40, measured: 100);
        var ptaChunk = Measure(20_000, workers: 4, versioned: false, write: false, warmup: 40, measured: 100);
        Console.WriteLine($"  Transaction path:  {txChunk,8:F1} us/tick  ({txChunk / 4:F1} us/chunk)");
        Console.WriteLine($"  PTA path:          {ptaChunk,8:F1} us/tick  ({ptaChunk / 4:F1} us/chunk)");
        if (ptaChunk > 0.001)
        {
            Console.WriteLine($"  Improvement:       {txChunk / ptaChunk,8:F2}x");
        }

        Console.WriteLine();

        // ── Benchmark 3: Storage mode scaling — V, SV, T at 1/2/4/8/16 workers ──
        // Same CPU-bound work (Open + Read + xorshift compute). Isolates storage mode overhead:
        //   Versioned: EntityMap lookup + RevisionChainReader.WalkChain (MVCC chain walk)
        //   SingleVersion: EntityMap lookup + direct chunk read (no chain walk)
        //   Transient: EntityMap lookup + direct transient heap read (no persistent pages)
        const int scalingEntities = 100_000;
        Console.WriteLine($"── 3. Storage Mode Scaling ({scalingEntities:N0} entities, Read + compute) ──");
        Console.WriteLine();

        var maxWorkers = Math.Max(1, Math.Min(16, Environment.ProcessorCount - 2));
        int[] scalingWorkers = maxWorkers >= 16 ? [1, 2, 4, 8, 16] : [1, 2, 4, Math.Min(8, maxWorkers)];

        // Header
        Console.Write($"  {"Workers",8}");
        foreach (var mode in new[] { "Versioned", "SingleVersion", "Transient" })
        {
            Console.Write($" {(mode + " (us)"),16} {"Spd",5}");
        }

        Console.WriteLine();

        // Baselines per mode (1-worker tick time)
        double vBase = 0, svBase = 0;

        double tBase = 0;
        const int transientEntities = 40_000; // Transient Spawn NRE at 43K+ (separate bug)
        Console.WriteLine($"  (Transient uses {transientEntities:N0} entities — NRE at 43K+ during Spawn, separate bug)");
        foreach (var w in scalingWorkers)
        {
            var vUs = MeasureComputeMode(scalingEntities, w, StorageMode.Versioned);
            var svUs = MeasureComputeMode(scalingEntities, w, StorageMode.SingleVersion);
            double tUs;
            try { tUs = MeasureComputeMode(transientEntities, w, StorageMode.Transient); }
            catch { tUs = -1; }

            if (w == 1) { vBase = vUs; svBase = svUs; if (tUs > 0) tBase = tUs; }

            var vSpd = vUs > 0.001 ? vBase / vUs : 0;
            var svSpd = svUs > 0.001 ? svBase / svUs : 0;

            if (tUs > 0)
            {
                var tSpd = tUs > 0.001 ? tBase / tUs : 0;
                Console.WriteLine($"  {w,8} {vUs,16:F0} {vSpd,5:F2}x {svUs,16:F0} {svSpd,5:F2}x {tUs,16:F0} {tSpd,5:F2}x");
            }
            else
            {
                Console.WriteLine($"  {w,8} {vUs,16:F0} {vSpd,5:F2}x {svUs,16:F0} {svSpd,5:F2}x {"err",16}");
            }
        }

        // Per-entity cost summary at 1 worker
        Console.WriteLine();
        var tNote = tBase > 0 ? $"  T={tBase * 1000 / transientEntities:F0}ns (40K ent)" : "";
        Console.WriteLine($"  Per-entity @ 1 worker:  V={vBase * 1000 / scalingEntities:F0}ns  SV={svBase * 1000 / scalingEntities:F0}ns{tNote}");

        Console.WriteLine();

        // ── Benchmark 4: Write scaling (for reference — shows page contention ceiling) ──
        const int writeEntities = 50_000;
        Console.WriteLine($"── 4. Write Scaling ({writeEntities:N0} SV entities, Read+Write — page contention visible) ──");
        Console.WriteLine($"  {"Workers",8} {"Tick (us)",10} {"Speedup",10}");

        double writeBaselineUs = 0;

        var writeWorkers = BuildWorkerList(maxWorkers);
        foreach (var w in writeWorkers)
        {
            var us = Measure(writeEntities, workers: w, versioned: false, write: true, warmup: 40, measured: 120);
            if (w == 1)
            {
                writeBaselineUs = us;
            }

            var speedup = us > 0.001 ? writeBaselineUs / us : 0;
            Console.WriteLine($"  {w,8} {us,10:F1} {speedup,10:F2}x");
        }

        Console.WriteLine();

        // ── Benchmark 5: WalkChain fast-path vs full walk (direct micro-bench) ──
        Console.WriteLine("── 5. WalkChain: Fast-Path vs Full Walk (Versioned read, 50K entities) ──");
        MeasureWalkChain(50_000);

        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════════════════");
    }

    // ═══════════════════════════════════════════════════════════════
    // Benchmark 5: WalkChain micro-bench (no runtime, direct PTA.Open)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Measures PTA.Open on Versioned entities — isolates RevisionChainReader.WalkChain cost.
    /// Runs A/B: skipTimeout=true (fast-path: single-entry shortcut + no Deadline) vs
    /// skipTimeout=false (full walk: RevisionEnumerator + Deadline.FromTimeout per entity).
    /// </summary>
    private static void MeasureWalkChain(int entityCount)
    {
        using var env = BenchEnv.Create(entityCount); // BenchComp is Versioned by default
        var dbe = env.Dbe;

        // Collect all entity IDs
        EntityId[] ids;
        {
            using var tx = dbe.CreateQuickTransaction();
            var view = tx.Query<BenchArch>().ToView();
            ids = new EntityId[view.Count];
            var i = 0;
            foreach (var pk in view)
            {
                ids[i++] = EntityId.FromRaw(pk);
            }
        }

        const int warmup = 30;
        const int measured = 100;

        // ── Fast-path (skipTimeout=true): PTA with cached accessor ──
        double fastNs;
        {
            using var pta = PointInTimeAccessor.Create(dbe, 1);
            var acc = pta.GetWorkerAccessor(0);

            // Warmup
            for (var w = 0; w < warmup; w++)
            {
                for (var i = 0; i < ids.Length; i++)
                {
                    acc.Open(ids[i]);
                }

                acc.RefreshEpochScope();
            }

            var sw = Stopwatch.StartNew();
            for (var m = 0; m < measured; m++)
            {
                for (var i = 0; i < ids.Length; i++)
                {
                    acc.Open(ids[i]);
                }

                acc.RefreshEpochScope();
            }

            sw.Stop();
            fastNs = sw.Elapsed.TotalNanoseconds / (measured * (long)ids.Length);
        }

        // ── Full walk (skipTimeout=false): Transaction path (includes Deadline.FromTimeout per entity) ──
        double slowNs;
        {
            // Use a Transaction — its ResolveEntity calls WalkChain with skipTimeout=false
            using var tx = dbe.CreateQuickTransaction();

            // Warmup
            for (var w = 0; w < warmup; w++)
            {
                for (var i = 0; i < ids.Length; i++)
                {
                    tx.Open(ids[i]);
                }
            }

            var sw = Stopwatch.StartNew();
            for (var m = 0; m < measured; m++)
            {
                for (var i = 0; i < ids.Length; i++)
                {
                    tx.Open(ids[i]);
                }
            }

            sw.Stop();
            slowNs = sw.Elapsed.TotalNanoseconds / (measured * (long)ids.Length);
        }

        Console.WriteLine($"  PTA fast-path (skipTimeout=true):   {fastNs,8:F1} ns/entity");
        Console.WriteLine($"  Transaction full walk:              {slowNs,8:F1} ns/entity");
        Console.WriteLine($"  Savings:                            {slowNs - fastNs,8:F1} ns/entity ({(slowNs - fastNs) / slowNs * 100:F0}%)");
    }

    // ═══════════════════════════════════════════════════════════════

    private static long Dummy; // Anti-optimization sink

    /// <summary>
    /// Storage mode scaling: Open + Read + xorshift compute for a specific storage mode.
    /// Same CPU-bound work per entity — isolates the cost difference between V/SV/T access paths.
    /// </summary>
    private static double MeasureComputeMode(int entityCount, int workers, StorageMode mode)
    {
        using var env = mode switch
        {
            StorageMode.Versioned => BenchEnv.Create(entityCount),
            StorageMode.SingleVersion => BenchEnv.CreateSv(entityCount),
            StorageMode.Transient => BenchEnv.CreateTransient(entityCount),
            _ => throw new ArgumentException($"Unsupported mode: {mode}")
        };

        using var txView = env.Dbe.CreateQuickTransaction();
        ViewBase view = mode switch
        {
            StorageMode.Versioned => txView.Query<BenchArch>().ToView(),
            StorageMode.SingleVersion => txView.Query<PdSvArch>().ToView(),
            StorageMode.Transient => txView.Query<PdTransientArch>().ToView(),
            _ => throw new ArgumentException()
        };

        using var runtime = TyphonRuntime.Create(env.Dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").QuerySystem("B", ctx =>
            {
                long accumulator = 0;
                foreach (var id in ctx.Entities)
                {
                    var entity = ctx.Accessor.Open(id);

                    // Read the component (different Comp<T> per mode, but same data layout)
                    int value;
                    long timestamp;
                    switch (mode)
                    {
                        case StorageMode.Versioned:
                            var v = entity.Read(BenchArch.Data);
                            value = v.Value; timestamp = v.Timestamp;
                            break;
                        case StorageMode.SingleVersion:
                            var sv = entity.Read(PdSvArch.Data);
                            value = sv.Value; timestamp = sv.Timestamp;
                            break;
                        default:
                            var t = entity.Read(PdTransientArch.Data);
                            value = t.Value; timestamp = t.Timestamp;
                            break;
                    }

                    var h = (uint)(value ^ (int)timestamp);
                    for (var r = 0; r < 20; r++)
                    {
                        h ^= h << 13;
                        h ^= h >> 17;
                        h ^= h << 5;
                    }

                    accumulator += h;
                }

                Thread.VolatileWrite(ref Dummy, accumulator);
            }, input: () => view, parallel: true);
        }, new RuntimeOptions { WorkerCount = workers, BaseTickRate = BenchTickRate });

        runtime.Start();
        SpinWait.SpinUntil(() => runtime.CurrentTickNumber >= 30, TimeSpan.FromSeconds(30));

        var t0 = runtime.CurrentTickNumber;
        var sw = Stopwatch.StartNew();
        SpinWait.SpinUntil(() => runtime.CurrentTickNumber >= t0 + 80, TimeSpan.FromSeconds(30));
        sw.Stop();
        var ticks = runtime.CurrentTickNumber - t0;

        runtime.Shutdown();
        return ticks > 0 ? sw.Elapsed.TotalMicroseconds / ticks : 0;
    }

    private static double Measure(int entityCount, int workers, bool versioned, bool write, int warmup, int measured)
    {
        var useSv = write && !versioned;
        using var env = useSv ? BenchEnv.CreateSv(entityCount) : BenchEnv.Create(entityCount);
        using var txView = env.Dbe.CreateQuickTransaction();
        ViewBase view = useSv ? txView.Query<PdSvArch>().ToView() : txView.Query<BenchArch>().ToView();

        using var runtime = TyphonRuntime.Create(env.Dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            if (versioned)
            {
                dag.QuerySystem("B", ctx =>
                {
                    foreach (var id in ctx.Entities)
                    {
                        _ = ctx.Transaction.Open(id).Read(BenchArch.Data);
                    }
                }, input: () => view, parallel: true, writesVersioned: true);
            }
            else if (write)
            {
                dag.QuerySystem("B", ctx =>
                {
                    foreach (var id in ctx.Entities)
                    {
                        ctx.Accessor.OpenMut(id).Write(PdSvArch.Data).Value++;
                    }
                }, input: () => view, parallel: true);
            }
            else
            {
                dag.QuerySystem("B", ctx =>
                {
                    foreach (var id in ctx.Entities)
                    {
                        _ = ctx.Accessor.Open(id).Read(BenchArch.Data);
                    }
                }, input: () => view, parallel: true);
            }
        }, new RuntimeOptions { WorkerCount = workers, BaseTickRate = BenchTickRate });

        runtime.Start();
        SpinWait.SpinUntil(() => runtime.CurrentTickNumber >= warmup, TimeSpan.FromSeconds(30));

        var t0 = runtime.CurrentTickNumber;
        var sw = Stopwatch.StartNew();
        SpinWait.SpinUntil(() => runtime.CurrentTickNumber >= t0 + measured, TimeSpan.FromSeconds(30));
        sw.Stop();
        var ticks = runtime.CurrentTickNumber - t0;

        runtime.Shutdown();
        return ticks > 0 ? sw.Elapsed.TotalMicroseconds / ticks : 0;
    }

    private static SortedSet<int> BuildWorkerList(int max)
    {
        var set = new SortedSet<int> { 1, 2, 4 };
        if (max >= 8)
        {
            set.Add(8);
        }

        if (max >= 16)
        {
            set.Add(16);
        }

        if (max >= 28)
        {
            set.Add(28);
        }
        else if (max > 16)
        {
            set.Add(max);
        }

        return set;
    }

    // ═══════════════════════════════════════════════════════════════

    private sealed class BenchEnv : IDisposable
    {
        public DatabaseEngine Dbe { get; }
        private readonly ServiceProvider _sp;
        private static int _seq;

        private BenchEnv(ServiceProvider sp, DatabaseEngine dbe) { _sp = sp; Dbe = dbe; }

        public static BenchEnv Create(int n) => Build(n, StorageMode.Versioned);
        public static BenchEnv CreateSv(int n) => Build(n, StorageMode.SingleVersion);
        public static BenchEnv CreateTransient(int n) => Build(n, StorageMode.Transient);

        private static BenchEnv Build(int n, StorageMode mode)
        {
            var name = $"PD_{Environment.ProcessId}_{Interlocked.Increment(ref _seq)}";
            var sc = new ServiceCollection();
            sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
              .AddResourceRegistry().AddMemoryAllocator().AddEpochManager()
              .AddHighResolutionSharedTimer().AddDeadlineWatchdog()
              .AddScopedManagedPagedMemoryMappedFile(o =>
              {
                  o.DatabaseName = name;
                  o.DatabaseCacheSize = (ulong)(200 * 1024 * PagedMMF.PageSize);
                  o.PagesDebugPattern = false;
              })
              .AddScopedDatabaseEngine();

            var sp = sc.BuildServiceProvider();
            sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
            var dbe = sp.GetRequiredService<DatabaseEngine>();

            // Register ALL component types — static archetype registry may have all archetypes
            // from earlier benchmarks in this process, and InitializeArchetypes processes all of them.
            dbe.RegisterComponentFromAccessor<BenchComp>();
            dbe.RegisterComponentFromAccessor<PdSvData>();
            dbe.RegisterComponentFromAccessor<PdTransientData>();

            dbe.InitializeArchetypes();

            const int batch = 1000;
            for (var off = 0; off < n; off += batch)
            {
                var cnt = Math.Min(batch, n - off);
                using var tx = dbe.CreateQuickTransaction();
                for (var i = 0; i < cnt; i++)
                {
                    switch (mode)
                    {
                        case StorageMode.SingleVersion:
                            var sv = new PdSvData { Value = off + i, Timestamp = 12345 };
                            tx.Spawn<PdSvArch>(PdSvArch.Data.Set(in sv));
                            break;
                        case StorageMode.Transient:
                            var t = new PdTransientData { Value = off + i, Timestamp = 12345 };
                            tx.Spawn<PdTransientArch>(PdTransientArch.Data.Set(in t));
                            break;
                        default:
                            var v = new BenchComp { Value = off + i, Timestamp = 12345 };
                            tx.Spawn<BenchArch>(BenchArch.Data.Set(in v));
                            break;
                    }
                }

                tx.Commit();
            }

            return new BenchEnv(sp, dbe);
        }

        public void Dispose()
        {
            Dbe?.Dispose();
            _sp?.Dispose();
            try
            {
                foreach (var f in System.IO.Directory.GetFiles(".", $"PD_{Environment.ProcessId}_*.bin"))
                {
                    System.IO.File.Delete(f);
                }
            }
            catch { }
        }
    }
}
