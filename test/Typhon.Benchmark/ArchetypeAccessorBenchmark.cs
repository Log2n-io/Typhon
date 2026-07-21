using System;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Typhon.Schema.Definition;

namespace Typhon.Benchmark;

// ── Components matching AntHill pattern (SV) ────────────────────────────
[Component("Typhon.Benchmark.AA.Position", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AaBenchPosition
{
    public float X, Y;
    public AaBenchPosition(float x, float y) { X = x; Y = y; }
}

[Component("Typhon.Benchmark.AA.Movement", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AaBenchMovement
{
    public float VX, VY;
    public AaBenchMovement(float vx, float vy) { VX = vx; VY = vy; }
}

[Archetype]
partial class AaBenchAnt : Archetype<AaBenchAnt>
{
    public static readonly Comp<AaBenchPosition> Position = Register<AaBenchPosition>();
    public static readonly Comp<AaBenchMovement> Movement = Register<AaBenchMovement>();
}

// ── Spatial SV component for Phase 3b benchmarks ─────────────────────
[Component("Typhon.Benchmark.AA.SpatialPos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AaBenchSpatialPos
{
    [Field]
    [SpatialIndex(5.0f)]
    public AABB3F Bounds;
    [Field]
    public float Speed;
}

[Component("Typhon.Benchmark.AA.SpatialMeta", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AaBenchSpatialMeta
{
    [Field]
    public long Tag;
}

[Archetype]
partial class AaBenchSpatialUnit : Archetype<AaBenchSpatialUnit>
{
    public static readonly Comp<AaBenchSpatialPos> Pos = Register<AaBenchSpatialPos>();
    public static readonly Comp<AaBenchSpatialMeta> Meta = Register<AaBenchSpatialMeta>();
}

// ── Indexed SV component for Phase 3a benchmarks ──────────────────────
[Component("Typhon.Benchmark.AA.IdxData", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AaBenchIdxData
{
    [Index]
    public int Score;
    public int Flags;
    public AaBenchIdxData(int score, int flags) { Score = score; Flags = flags; }
}

[Archetype]
partial class AaBenchIdxUnit : Archetype<AaBenchIdxUnit>
{
    public static readonly Comp<AaBenchPosition> Position = Register<AaBenchPosition>();
    public static readonly Comp<AaBenchIdxData> Data = Register<AaBenchIdxData>();
}

// ── Mixed SV+Versioned cluster benchmark archetype (Phase 5) ──────────
[Component("Typhon.Bench.AA.VcHealth", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential)]
struct AaVcHealth
{
    public int Current, Max;
}

[Archetype]
partial class AaBenchMixedCluster : Archetype<AaBenchMixedCluster>
{
    public static readonly Comp<AaBenchPosition> Position = Register<AaBenchPosition>();  // SV
    public static readonly Comp<AaBenchMovement> Movement = Register<AaBenchMovement>();  // SV
    public static readonly Comp<AaVcHealth> Health = Register<AaVcHealth>();              // Versioned
}

// ── Additional indexed archetypes for ordered query benchmark ─────────
[Archetype]
partial class AaBenchIdxUnit2 : Archetype<AaBenchIdxUnit2>
{
    public static readonly Comp<AaBenchPosition> Position = Register<AaBenchPosition>();
    public static readonly Comp<AaBenchIdxData> Data = Register<AaBenchIdxData>();
}

[Archetype]
partial class AaBenchIdxUnit3 : Archetype<AaBenchIdxUnit3>
{
    public static readonly Comp<AaBenchPosition> Position = Register<AaBenchPosition>();
    public static readonly Comp<AaBenchIdxData> Data = Register<AaBenchIdxData>();
}

/// <summary>
/// Compares per-entity access cost: standard EntityAccessor.Open vs ArchetypeAccessor.Open.
/// Mimics the AntHill movement system: Read(Movement) + Write(Position) per entity.
/// </summary>
static class ArchetypeAccessorBenchmark
{
    const float WorldSize = 10_000f;

    public static void Run(int entityCount = 50_000, int iterations = 500)
    {
        Console.WriteLine("═══════════════════════════════════════════════════════");
        Console.WriteLine($"  ArchetypeAccessor Benchmark — {entityCount:N0} entities, {iterations} iterations");
        Console.WriteLine("═══════════════════════════════════════════════════════");

        using var env = CreateEnv(entityCount);
        var dbe = env.Dbe;
        var entityIds = env.EntityIds;

        // Warm up both paths
        RunStandard(dbe, entityIds, 10);
        RunArchetypeAccessor(dbe, entityIds, 10);

        // ── Benchmark: Standard path ────────────────────────────────
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        var sw = Stopwatch.StartNew();
        RunStandard(dbe, entityIds, iterations);
        sw.Stop();
        double standardUs = sw.Elapsed.TotalMicroseconds;
        double standardPerEntity = standardUs / (iterations * entityCount) * 1000; // ns

        // ── Benchmark: ArchetypeAccessor path ───────────────────────
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        sw.Restart();
        RunArchetypeAccessor(dbe, entityIds, iterations);
        sw.Stop();
        double accessorUs = sw.Elapsed.TotalMicroseconds;
        double accessorPerEntity = accessorUs / (iterations * entityCount) * 1000; // ns

        Console.WriteLine();
        Console.WriteLine($"  Standard EntityAccessor:    {standardUs / iterations,8:F0} µs/iter  ({standardPerEntity:F1} ns/entity)");
        Console.WriteLine($"  ArchetypeAccessor:          {accessorUs / iterations,8:F0} µs/iter  ({accessorPerEntity:F1} ns/entity)");
        Console.WriteLine($"  Speedup:                    {standardUs / accessorUs:F2}x");
        Console.WriteLine();
    }

    /// <summary>Run with standard EntityAccessor.Open/OpenMut path (for profiling).</summary>
    public static void ProfileStandard(int entityCount = 50_000, int iterations = 500)
    {
        using var env = CreateEnv(entityCount);
        RunStandard(env.Dbe, env.EntityIds, 10); // warmup
        RunStandard(env.Dbe, env.EntityIds, iterations);
    }

    /// <summary>Run with ArchetypeAccessor path (for profiling).</summary>
    public static void ProfileAccessor(int entityCount = 50_000, int iterations = 500)
    {
        using var env = CreateEnv(entityCount);
        RunArchetypeAccessor(env.Dbe, env.EntityIds, 10); // warmup
        RunArchetypeAccessor(env.Dbe, env.EntityIds, iterations);
    }

    /// <summary>Run both runtime paths at increasing entity counts to find saturation.</summary>
    public static void ScaleTest()
    {
        int[] counts = [50_000, 100_000, 200_000, 500_000];
        Console.WriteLine("═══════════════════════════════════════════════════════════════════════════");
        Console.WriteLine("  Runtime Scale Test — Movement system only, 4 workers, 60Hz target");
        Console.WriteLine("═══════════════════════════════════════════════════════════════════════════");
        Console.WriteLine($"  {"Entities",10} | {"Std tick",10} | {"AA tick",10} | {"Speedup",8} | {"Std ns/e",10} | {"AA ns/e",10}");
        Console.WriteLine(new string('-', 75));

        foreach (var n in counts)
        {
            var (stdMs, aaMs) = MeasureRuntimePair(n);
            double stdNs = stdMs * 1_000_000 / n;
            double aaNs = aaMs * 1_000_000 / n;
            Console.WriteLine($"  {n,10:N0} | {stdMs,8:F2}ms | {aaMs,8:F2}ms | {stdMs / aaMs,7:F2}x | {stdNs,8:F1}ns | {aaNs,8:F1}ns");
        }
        Console.WriteLine();
    }

    static (double stdMs, double aaMs) MeasureRuntimePair(int entityCount)
    {
        double stdMs = MeasureRuntimeTick(entityCount, useAccessor: false);
        double aaMs = MeasureRuntimeTick(entityCount, useAccessor: true);
        return (stdMs, aaMs);
    }

    static double MeasureRuntimeTick(int entityCount, bool useAccessor)
    {
        using var env = CreateEnv(entityCount);
        using var txView = env.Dbe.CreateQuickTransaction();
        var view = txView.Query<AaBenchAnt>().ToView();

        using var runtime = TyphonRuntime.Create(env.Dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").QuerySystem("Movement", ctx =>
            {
                if (useAccessor)
                {
                    var ants = ctx.Accessor.For<AaBenchAnt>();
                    foreach (var id in ctx.Entities)
                    {
                        var entity = ants.OpenMut(id);
                        ref var pos = ref entity.Write(AaBenchAnt.Position);
                        ref readonly var mov = ref entity.Read(AaBenchAnt.Movement);
                        pos.X += mov.VX * ctx.DeltaTime;
                        pos.Y += mov.VY * ctx.DeltaTime;
                        if (pos.X < 0f) pos.X += WorldSize;
                        else if (pos.X >= WorldSize) pos.X -= WorldSize;
                        if (pos.Y < 0f) pos.Y += WorldSize;
                        else if (pos.Y >= WorldSize) pos.Y -= WorldSize;
                    }
                    ants.Dispose();
                }
                else
                {
                    foreach (var id in ctx.Entities)
                    {
                        var entity = ctx.Accessor.OpenMut(id);
                        ref var pos = ref entity.Write(AaBenchAnt.Position);
                        ref readonly var mov = ref entity.Read(AaBenchAnt.Movement);
                        pos.X += mov.VX * ctx.DeltaTime;
                        pos.Y += mov.VY * ctx.DeltaTime;
                        if (pos.X < 0f) pos.X += WorldSize;
                        else if (pos.X >= WorldSize) pos.X -= WorldSize;
                        if (pos.Y < 0f) pos.Y += WorldSize;
                        else if (pos.Y >= WorldSize) pos.Y -= WorldSize;
                    }
                }
            }, input: () => view, parallel: true);
        }, new RuntimeOptions { BaseTickRate = 60, WorkerCount = 4 });

        runtime.Start();
        // Let it stabilize for 2s, then sample for 3s
        System.Threading.Thread.Sleep(2000);
        var telemetry = runtime.Telemetry;
        long startTick = telemetry.NewestTick;
        System.Threading.Thread.Sleep(3000);
        long endTick = telemetry.NewestTick;

        // Average system duration across sampled ticks
        double totalSystemUs = 0;
        int samples = 0;
        for (long t = startTick + 1; t <= endTick && t >= telemetry.OldestAvailableTick; t++)
        {
            var systems = telemetry.GetSystemMetrics(t);
            if (systems.Length > 0 && !systems[0].WasSkipped)
            {
                totalSystemUs += systems[0].DurationUs;
                samples++;
            }
        }

        runtime.Shutdown();
        view.Dispose();

        return samples > 0 ? totalSystemUs / samples / 1000.0 : 0; // ms
    }

    /// <summary>Profile the STANDARD path through TyphonRuntime (parallel QuerySystem + PTA), same as AntHill.</summary>
    public static void ProfileRuntimeStandard(int entityCount = 50_000, int runSeconds = 10)
    {
        Console.WriteLine($"ProfileRuntimeStandard: {entityCount:N0} entities, {runSeconds}s via TyphonRuntime");
        using var env = CreateEnv(entityCount);
        using var txView = env.Dbe.CreateQuickTransaction();
        var view = txView.Query<AaBenchAnt>().ToView();

        using var runtime = TyphonRuntime.Create(env.Dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").QuerySystem("Movement", ctx =>
            {
                foreach (var id in ctx.Entities)
                {
                    var entity = ctx.Accessor.OpenMut(id);
                    ref var pos = ref entity.Write(AaBenchAnt.Position);
                    ref readonly var mov = ref entity.Read(AaBenchAnt.Movement);
                    pos.X += mov.VX * ctx.DeltaTime;
                    pos.Y += mov.VY * ctx.DeltaTime;
                    if (pos.X < 0f) pos.X += WorldSize;
                    else if (pos.X >= WorldSize) pos.X -= WorldSize;
                    if (pos.Y < 0f) pos.Y += WorldSize;
                    else if (pos.Y >= WorldSize) pos.Y -= WorldSize;
                }
            }, input: () => view, parallel: true);
        }, new RuntimeOptions { BaseTickRate = 60, WorkerCount = 4 });

        runtime.Start();
        System.Threading.Thread.Sleep(runSeconds * 1000);
        runtime.Shutdown();
        view.Dispose();
        Console.WriteLine("Done.");
    }

    /// <summary>Profile the ARCHETYPE ACCESSOR path through TyphonRuntime, same as optimized AntHill.</summary>
    public static void ProfileRuntimeAccessor(int entityCount = 100_000, int runSeconds = 10)
    {
        Console.WriteLine($"ProfileRuntimeAccessor: {entityCount:N0} entities, {runSeconds}s via TyphonRuntime");
        using var env = CreateEnv(entityCount);
        using var txView = env.Dbe.CreateQuickTransaction();
        var view = txView.Query<AaBenchAnt>().ToView();

        using var runtime = TyphonRuntime.Create(env.Dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").QuerySystem("Movement", ctx =>
            {
                var ants = ctx.Accessor.For<AaBenchAnt>();
                foreach (var id in ctx.Entities)
                {
                    var entity = ants.OpenMut(id);
                    ref var pos = ref entity.Write(AaBenchAnt.Position);
                    ref readonly var mov = ref entity.Read(AaBenchAnt.Movement);
                    pos.X += mov.VX * ctx.DeltaTime;
                    pos.Y += mov.VY * ctx.DeltaTime;
                    if (pos.X < 0f) pos.X += WorldSize;
                    else if (pos.X >= WorldSize) pos.X -= WorldSize;
                    if (pos.Y < 0f) pos.Y += WorldSize;
                    else if (pos.Y >= WorldSize) pos.Y -= WorldSize;
                }
                ants.Dispose();
            }, input: () => view, parallel: true);
        }, new RuntimeOptions { BaseTickRate = 60, WorkerCount = 4 });

        runtime.Start();
        System.Threading.Thread.Sleep(runSeconds * 1000);
        runtime.Shutdown();
        view.Dispose();
        Console.WriteLine("Done.");
    }

    static void RunStandard(DatabaseEngine dbe, EntityId[] ids, int iterations)
    {
        for (int iter = 0; iter < iterations; iter++)
        {
            using var tx = dbe.CreateQuickTransaction();
            for (int i = 0; i < ids.Length; i++)
            {
                var entity = tx.OpenMut(ids[i]);
                ref var pos = ref entity.Write(AaBenchAnt.Position);
                ref readonly var mov = ref entity.Read(AaBenchAnt.Movement);
                pos.X += mov.VX * 0.016f;
                pos.Y += mov.VY * 0.016f;
                if (pos.X < 0f) pos.X += WorldSize;
                else if (pos.X >= WorldSize) pos.X -= WorldSize;
                if (pos.Y < 0f) pos.Y += WorldSize;
                else if (pos.Y >= WorldSize) pos.Y -= WorldSize;
            }
            tx.Commit();
        }
    }

    static void RunArchetypeAccessor(DatabaseEngine dbe, EntityId[] ids, int iterations)
    {
        for (int iter = 0; iter < iterations; iter++)
        {
            using var tx = dbe.CreateQuickTransaction();
            var ants = tx.For<AaBenchAnt>();
            for (int i = 0; i < ids.Length; i++)
            {
                var entity = ants.OpenMut(ids[i]);
                ref var pos = ref entity.Write(AaBenchAnt.Position);
                ref readonly var mov = ref entity.Read(AaBenchAnt.Movement);
                pos.X += mov.VX * 0.016f;
                pos.Y += mov.VY * 0.016f;
                if (pos.X < 0f) pos.X += WorldSize;
                else if (pos.X >= WorldSize) pos.X -= WorldSize;
                if (pos.Y < 0f) pos.Y += WorldSize;
                else if (pos.Y >= WorldSize) pos.Y -= WorldSize;
            }
            ants.Dispose();
            tx.Commit();
        }
    }

    sealed class BenchEnv : IDisposable
    {
        public DatabaseEngine Dbe { get; }
        public EntityId[] EntityIds { get; }
        private readonly ServiceProvider _sp;

        public static BenchEnv Create(int entityCount, bool enableWal = false)
        {
            var name = $"AABench_{Environment.ProcessId}";
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
              });

            // enableWal=true → on-disk WAL (default WalFileIO). enableWal=false → in-memory WAL, FUA off, idle checkpoint: zero disk I/O for CPU benchmarks.
            if (!enableWal)
            {
                sc.AddSingleton<IWalFileIO>(_ => new InMemoryWalFileIO());
            }
            sc.AddScopedDatabaseEngine(o =>
            {
                if (enableWal)
                {
                    o.Wal = new WalWriterOptions { WalDirectory = Path.Combine(Path.GetTempPath(), $"AABench_wal_{Environment.ProcessId}") };
                }
                else
                {
                    o.Wal = new WalWriterOptions { UseFUA = false };
                    o.Resources.CheckpointIntervalMs = int.MaxValue;
                }
            });

            var sp = sc.BuildServiceProvider();
            sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
            var dbe = sp.GetRequiredService<DatabaseEngine>();

            dbe.RegisterComponentFromAccessor<AaBenchPosition>();
            dbe.RegisterComponentFromAccessor<AaBenchMovement>();
            dbe.InitializeArchetypes();

            var rng = new Random(42);
            var ids = new EntityId[entityCount];
            int remaining = entityCount;
            int offset = 0;
            while (remaining > 0)
            {
                int batch = Math.Min(1000, remaining);
                remaining -= batch;
                using var tx = dbe.CreateQuickTransaction();
                for (int i = 0; i < batch; i++)
                {
                    var pos = new AaBenchPosition((float)(rng.NextDouble() * WorldSize), (float)(rng.NextDouble() * WorldSize));
                    float angle = (float)(rng.NextDouble() * Math.PI * 2);
                    float speed = 20f + (float)(rng.NextDouble() * 60);
                    var mov = new AaBenchMovement(MathF.Cos(angle) * speed, MathF.Sin(angle) * speed);
                    ids[offset + i] = tx.Spawn<AaBenchAnt>(AaBenchAnt.Position.Set(in pos), AaBenchAnt.Movement.Set(in mov));
                }
                tx.Commit();
                offset += batch;
            }

            return new BenchEnv(sp, dbe, ids);
        }

        internal BenchEnv(ServiceProvider sp, DatabaseEngine dbe, EntityId[] ids) { _sp = sp; Dbe = dbe; EntityIds = ids; }

        public void Dispose()
        {
            Dbe?.Dispose();
            _sp?.Dispose();
            try { File.Delete($"AABench_{Environment.ProcessId}.bin"); } catch { }
            try
            {
                var walDir = Path.Combine(Path.GetTempPath(), $"AABench_wal_{Environment.ProcessId}");
                if (Directory.Exists(walDir)) { Directory.Delete(walDir, true); }
            }
            catch { }
        }
    }

    static BenchEnv CreateEnv(int entityCount) => BenchEnv.Create(entityCount);
    static BenchEnv CreateClusterEnv(int entityCount) => BenchEnv.Create(entityCount);
    static BenchEnv CreateWalEnv(int entityCount) => BenchEnv.Create(entityCount, enableWal: true);

    // ── Cluster iteration benchmark ─────────────────────────────────────

    public static void RunCluster(int entityCount = 50_000, int iterations = 500)
    {
        Console.WriteLine("═══════════════════════════════════════════════════════");
        Console.WriteLine($"  Cluster Iteration Benchmark — {entityCount:N0} entities, {iterations} iterations");
        Console.WriteLine("═══════════════════════════════════════════════════════");

        using var env = CreateClusterEnv(entityCount);
        var dbe = env.Dbe;
        var entityIds = env.EntityIds;

        // Warm up all three paths
        RunStandard(dbe, entityIds, 10);
        RunArchetypeAccessor(dbe, entityIds, 10);
        RunClusterIteration(dbe, 10);

        // ── Benchmark: Standard path ────────────────────────────────
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        RunStandard(dbe, entityIds, iterations);
        sw.Stop();
        double standardUs = sw.Elapsed.TotalMicroseconds;
        double standardPerEntity = standardUs / (iterations * entityCount) * 1000;

        // ── Benchmark: ArchetypeAccessor path ───────────────────────
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        sw.Restart();
        RunArchetypeAccessor(dbe, entityIds, iterations);
        sw.Stop();
        double accessorUs = sw.Elapsed.TotalMicroseconds;
        double accessorPerEntity = accessorUs / (iterations * entityCount) * 1000;

        // ── Benchmark: Cluster iteration path ───────────────────────
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        sw.Restart();
        RunClusterIteration(dbe, iterations);
        sw.Stop();
        double clusterUs = sw.Elapsed.TotalMicroseconds;
        double clusterPerEntity = clusterUs / (iterations * entityCount) * 1000;

        Console.WriteLine();
        Console.WriteLine($"  Standard EntityAccessor:    {standardUs / iterations,8:F0} µs/iter  ({standardPerEntity:F1} ns/entity)");
        Console.WriteLine($"  ArchetypeAccessor:          {accessorUs / iterations,8:F0} µs/iter  ({accessorPerEntity:F1} ns/entity)");
        Console.WriteLine($"  ClusterIteration:           {clusterUs / iterations,8:F0} µs/iter  ({clusterPerEntity:F1} ns/entity)");
        Console.WriteLine($"  Speedup (Cluster vs Std):   {standardUs / clusterUs:F2}x");
        Console.WriteLine($"  Speedup (Cluster vs AA):    {accessorUs / clusterUs:F2}x");
        Console.WriteLine();
    }

    static void RunClusterIteration(DatabaseEngine dbe, int iterations)
    {
        for (int iter = 0; iter < iterations; iter++)
        {
            using var tx = dbe.CreateQuickTransaction();
            var ants = tx.For<AaBenchAnt>();
            foreach (var cluster in ants.GetClusterEnumerator())
            {
                var positions = cluster.GetSpan<AaBenchPosition>(AaBenchAnt.Position);
                var movements = cluster.GetReadOnlySpan<AaBenchMovement>(AaBenchAnt.Movement);
                ulong bits = cluster.OccupancyBits;
                while (bits != 0)
                {
                    int idx = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    ref var pos = ref positions[idx];
                    ref readonly var mov = ref movements[idx];
                    pos.X += mov.VX * 0.016f;
                    pos.Y += mov.VY * 0.016f;
                    if (pos.X < 0f) pos.X += WorldSize;
                    else if (pos.X >= WorldSize) pos.X -= WorldSize;
                    if (pos.Y < 0f) pos.Y += WorldSize;
                    else if (pos.Y >= WorldSize) pos.Y -= WorldSize;
                }
            }
            ants.Dispose();
            tx.Commit();
        }
    }

    // ── Tick fence benchmark ───────────────────────────────────────────

    public static void RunTickFenceBench()
    {
        Console.WriteLine("═══════════════════════════════════════════════════════");
        Console.WriteLine("  Tick Fence Benchmark — With WAL Serialization");
        Console.WriteLine("═══════════════════════════════════════════════════════");
        Console.WriteLine();

        int[] entityCounts = [10_000, 50_000, 100_000, 200_000];

        foreach (int count in entityCounts)
        {
            using var env = CreateWalEnv(count);
            var dbe = env.Dbe;
            var ids = env.EntityIds;

            // Check if cluster storage is active
            var meta = Archetype<AaBenchAnt>.Metadata;
            var clusterState = dbe._archetypeStates[meta.ArchetypeId]?.ClusterState;
            bool hasClusters = clusterState != null;

            // Warmup: write all entities + tick fence (10 rounds with drain time)
            for (int w = 0; w < 10; w++)
            {
                WriteAllEntities(dbe, ids);
                dbe.WriteTickFence(w + 1);
                Thread.Sleep(5); // Let WAL writer drain the commit buffer
            }

            // Measure: write all entities, then time WriteTickFence
            const int iterations = 50;
            var tickFenceTimes = new double[iterations];

            for (int i = 0; i < iterations; i++)
            {
                WriteAllEntities(dbe, ids);

                var sw = System.Diagnostics.Stopwatch.StartNew();
                dbe.WriteTickFence(100 + i);
                sw.Stop();
                tickFenceTimes[i] = sw.Elapsed.TotalMicroseconds;
                Thread.Sleep(5); // Let WAL writer drain before next iteration
            }

            Array.Sort(tickFenceTimes);
            double p50 = tickFenceTimes[iterations / 2];
            double p90 = tickFenceTimes[(int)(iterations * 0.90)];
            double p99 = tickFenceTimes[(int)(iterations * 0.99)];
            double mean = 0;
            for (int i = 0; i < iterations; i++) mean += tickFenceTimes[i];
            mean /= iterations;

            Console.WriteLine($"  {count,8:N0} entities (cluster={hasClusters}):");
            Console.WriteLine($"    Mean:  {mean,10:F1} µs  ({mean / count * 1000:F2} ns/entity)");
            Console.WriteLine($"    P50:   {p50,10:F1} µs");
            Console.WriteLine($"    P90:   {p90,10:F1} µs");
            Console.WriteLine($"    P99:   {p99,10:F1} µs");
            Console.WriteLine();
        }
    }

    static void WriteAllEntities(DatabaseEngine dbe, EntityId[] ids)
    {
        using var tx = dbe.CreateQuickTransaction();
        for (int i = 0; i < ids.Length; i++)
        {
            ref var pos = ref tx.OpenMut(ids[i]).Write(AaBenchAnt.Position);
            pos.X += 0.1f;
        }
        tx.Commit();
    }

    // ── Indexed cluster benchmark (Phase 3a) ──────────────────────────

    public static void RunIndexedBench(int entityCount = 50_000, int iterations = 200)
    {
        Console.WriteLine("═══════════════════════════════════════════════════════");
        Console.WriteLine($"  Indexed Cluster Benchmark — {entityCount:N0} entities, {iterations} iterations");
        Console.WriteLine("═══════════════════════════════════════════════════════");
        Console.WriteLine();

        using var env = CreateIndexedEnv(entityCount);
        var dbe = env.Dbe;
        var ids = env.EntityIds;

        var meta = Archetype<AaBenchIdxUnit>.Metadata;
        var clusterState = dbe._archetypeStates[meta.ArchetypeId]?.ClusterState;
        Console.WriteLine($"  Cluster eligible: {meta.IsClusterEligible}");
        Console.WriteLine($"  Has cluster indexes: {meta.HasClusterIndexes}");
        Console.WriteLine($"  Active clusters: {clusterState?.ActiveClusterCount ?? 0}");
        Console.WriteLine($"  Index slots: {clusterState?.IndexSlots?.Length ?? 0}");
        Console.WriteLine();

        // ── 1. Indexed Write benchmark: Write(Data) triggers shadow capture ──
        // Warmup
        for (int w = 0; w < 5; w++)
        {
            WriteAllIndexedEntities(dbe, ids, w);
            dbe.WriteTickFence(w + 1);
        }

        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            WriteAllIndexedEntities(dbe, ids, 100 + i);
        }
        sw.Stop();
        double writeUs = sw.Elapsed.TotalMicroseconds / iterations;
        double writeNs = writeUs * 1000.0 / entityCount;
        Console.WriteLine($"  Indexed Write (shadow capture):");
        Console.WriteLine($"    {writeUs:F1} µs/iter  ({writeNs:F1} ns/entity)");
        Console.WriteLine();

        // ── 2. Tick fence with shadow processing ──
        var tickTimes = new double[50];
        for (int i = 0; i < 50; i++)
        {
            WriteAllIndexedEntities(dbe, ids, 200 + i);
            sw.Restart();
            dbe.WriteTickFence(200 + i);
            sw.Stop();
            tickTimes[i] = sw.Elapsed.TotalMicroseconds;
        }
        Array.Sort(tickTimes);
        double tfMean = 0;
        for (int i = 0; i < 50; i++) tfMean += tickTimes[i];
        tfMean /= 50;
        Console.WriteLine($"  Tick Fence (shadow + B+Tree Move + zone map recompute):");
        Console.WriteLine($"    Mean: {tfMean:F1} µs  ({tfMean / entityCount * 1000:F2} ns/entity)");
        Console.WriteLine($"    P50:  {tickTimes[25]:F1} µs");
        Console.WriteLine($"    P90:  {tickTimes[45]:F1} µs");
        Console.WriteLine();

        // ── 3. Targeted query benchmark ──
        // Warmup
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int w = 0; w < 10; w++)
            {
                tx.Query<AaBenchIdxUnit>().WhereField<AaBenchIdxData>(d => d.Score >= 0 && d.Score < 500).Execute();
            }
        }

        var queryTimes = new double[100];
        for (int i = 0; i < 100; i++)
        {
            using var tx = dbe.CreateQuickTransaction();
            sw.Restart();
            var result = tx.Query<AaBenchIdxUnit>().WhereField<AaBenchIdxData>(d => d.Score >= 0 && d.Score < 500).Count();
            sw.Stop();
            queryTimes[i] = sw.Elapsed.TotalMicroseconds;
        }
        Array.Sort(queryTimes);
        double qMean = 0;
        for (int i = 0; i < 100; i++) qMean += queryTimes[i];
        qMean /= 100;
        Console.WriteLine($"  Targeted Query (WhereField cluster scan, ~1% selectivity):");
        Console.WriteLine($"    Mean: {qMean:F1} µs");
        Console.WriteLine($"    P50:  {queryTimes[50]:F1} µs");
        Console.WriteLine($"    P90:  {queryTimes[90]:F1} µs");
        Console.WriteLine();
    }

    static void WriteAllIndexedEntities(DatabaseEngine dbe, EntityId[] ids, int tick)
    {
        using var tx = dbe.CreateQuickTransaction();
        for (int i = 0; i < ids.Length; i++)
        {
            ref var data = ref tx.OpenMut(ids[i]).Write(AaBenchIdxUnit.Data);
            data.Score = tick * 1000 + i; // Unique value to force B+Tree Move at tick fence
        }
        tx.Commit();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Spatial Cluster Benchmark (Phase 3b)
    // ═══════════════════════════════════════════════════════════════════════

    public static void RunSpatialBench(int entityCount = 50_000, int iterations = 200)
    {
        Console.WriteLine("═══════════════════════════════════════════════════════");
        Console.WriteLine($"  Spatial Cluster Benchmark — {entityCount:N0} entities, {iterations} iterations");
        Console.WriteLine("═══════════════════════════════════════════════════════");
        Console.WriteLine();

        using var env = CreateSpatialEnv(entityCount);
        var dbe = env.Dbe;
        var ids = env.EntityIds;

        var meta = Archetype<AaBenchSpatialUnit>.Metadata;
        var clusterState = dbe._archetypeStates[meta.ArchetypeId]?.ClusterState;
        Console.WriteLine($"  Cluster eligible: {meta.IsClusterEligible}");
        Console.WriteLine($"  Has cluster spatial: {meta.HasClusterSpatial}");
        Console.WriteLine($"  Active clusters: {clusterState?.ActiveClusterCount ?? 0}");
        Console.WriteLine();

        // ── 1. Spatial Write benchmark: Write(Pos) with new bounds ──
        for (int w = 0; w < 5; w++)
        {
            WriteSpatialEntities(dbe, ids, w, 10.0f);
            dbe.WriteTickFence(w + 1);
        }

        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            WriteSpatialEntities(dbe, ids, 100 + i, 0.5f); // Small move within fat AABB
        }
        sw.Stop();
        double writeUs = sw.Elapsed.TotalMicroseconds / iterations;
        double writeNs = writeUs * 1000.0 / entityCount;
        Console.WriteLine($"  Spatial Write (small move, within fat AABB):");
        Console.WriteLine($"    {writeUs:F1} µs/iter  ({writeNs:F1} ns/entity)");
        Console.WriteLine();

        // ── 2. Tick Fence — 100% dirty, small moves (fast path: containment check only) ──
        var tickTimes = new double[50];
        for (int i = 0; i < 50; i++)
        {
            WriteSpatialEntities(dbe, ids, 200 + i, 0.5f);
            sw.Restart();
            dbe.WriteTickFence(200 + i);
            sw.Stop();
            tickTimes[i] = sw.Elapsed.TotalMicroseconds;
        }
        Array.Sort(tickTimes);
        double tfMean = 0;
        for (int i = 0; i < 50; i++) tfMean += tickTimes[i];
        tfMean /= 50;
        Console.WriteLine($"  Tick Fence (100% dirty, small moves → fast path):");
        Console.WriteLine($"    Mean: {tfMean:F1} µs  ({tfMean / entityCount * 1000:F2} ns/entity)");
        Console.WriteLine($"    P50:  {tickTimes[25]:F1} µs");
        Console.WriteLine($"    P90:  {tickTimes[45]:F1} µs");
        Console.WriteLine();

        // ── 3. Tick Fence — 100% dirty, large moves (slow path: remove + reinsert) ──
        for (int i = 0; i < 50; i++)
        {
            WriteSpatialEntities(dbe, ids, 300 + i, 50.0f); // Large move escapes fat AABB
            sw.Restart();
            dbe.WriteTickFence(300 + i);
            sw.Stop();
            tickTimes[i] = sw.Elapsed.TotalMicroseconds;
        }
        Array.Sort(tickTimes);
        tfMean = 0;
        for (int i = 0; i < 50; i++) tfMean += tickTimes[i];
        tfMean /= 50;
        Console.WriteLine($"  Tick Fence (100% dirty, large moves → slow path: remove+reinsert):");
        Console.WriteLine($"    Mean: {tfMean:F1} µs  ({tfMean / entityCount * 1000:F2} ns/entity)");
        Console.WriteLine($"    P50:  {tickTimes[25]:F1} µs");
        Console.WriteLine($"    P90:  {tickTimes[45]:F1} µs");
        Console.WriteLine();

        // ── 4. Spatial AABB Query benchmark ──
        var queryTimes = new double[100];
        for (int i = 0; i < 100; i++)
        {
            using var tx = dbe.CreateQuickTransaction();
            sw.Restart();
            var result = tx.Query<AaBenchSpatialUnit>().WhereInAABB<AaBenchSpatialPos>(-1000, -1000, -1000, 1000, 1000, 1000).Execute();
            sw.Stop();
            queryTimes[i] = sw.Elapsed.TotalMicroseconds;
        }
        Array.Sort(queryTimes);
        double qMean = 0;
        for (int i = 0; i < 100; i++) qMean += queryTimes[i];
        qMean /= 100;
        Console.WriteLine($"  Spatial AABB Query (full extent, {entityCount:N0} entities):");
        Console.WriteLine($"    Mean: {qMean:F1} µs");
        Console.WriteLine($"    P50:  {queryTimes[50]:F1} µs");
        Console.WriteLine($"    P90:  {queryTimes[90]:F1} µs");
        Console.WriteLine();
    }

    static void WriteSpatialEntities(DatabaseEngine dbe, EntityId[] ids, int tick, float delta)
    {
        using var tx = dbe.CreateQuickTransaction();
        for (int i = 0; i < ids.Length; i++)
        {
            ref var pos = ref tx.OpenMut(ids[i]).Write(AaBenchSpatialUnit.Pos);
            float x = i * 20.0f + tick * delta;
            pos.Bounds = new AABB3F { MinX = x - 1, MinY = -1, MinZ = -1, MaxX = x + 1, MaxY = 1, MaxZ = 1 };
        }
        tx.Commit();
    }

    static BenchEnv CreateSpatialEnv(int entityCount)
    {
        var name = $"AABenchSpatial_{Environment.ProcessId}";
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

        var sp = sc.BuildServiceProvider();
        sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
        var dbe = sp.GetRequiredService<DatabaseEngine>();

        dbe.RegisterComponentFromAccessor<AaBenchSpatialPos>();
        dbe.RegisterComponentFromAccessor<AaBenchSpatialMeta>();
        dbe.InitializeArchetypes();

        var ids = new EntityId[entityCount];
        int remaining = entityCount;
        int offset = 0;
        while (remaining > 0)
        {
            int batch = Math.Min(1000, remaining);
            remaining -= batch;
            using var tx = dbe.CreateQuickTransaction();
            for (int i = 0; i < batch; i++)
            {
                float x = (offset + i) * 20.0f;
                var pos = new AaBenchSpatialPos { Bounds = new AABB3F { MinX = x - 1, MinY = -1, MinZ = -1, MaxX = x + 1, MaxY = 1, MaxZ = 1 }, Speed = 1.0f };
                var meta2 = new AaBenchSpatialMeta { Tag = offset + i };
                ids[offset + i] = tx.Spawn<AaBenchSpatialUnit>(AaBenchSpatialUnit.Pos.Set(in pos), AaBenchSpatialUnit.Meta.Set(in meta2));
            }
            tx.Commit();
            offset += batch;
        }

        dbe.WriteTickFence(0);

        return new BenchEnv(sp, dbe, ids);
    }

    static BenchEnv CreateIndexedEnv(int entityCount)
    {
        var name = $"AABench_{Environment.ProcessId}";
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

        var sp = sc.BuildServiceProvider();
        sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
        var dbe = sp.GetRequiredService<DatabaseEngine>();

        dbe.RegisterComponentFromAccessor<AaBenchPosition>();
        dbe.RegisterComponentFromAccessor<AaBenchIdxData>();
        dbe.InitializeArchetypes();

        var ids = new EntityId[entityCount];
        int remaining = entityCount;
        int offset = 0;
        while (remaining > 0)
        {
            int batch = Math.Min(1000, remaining);
            remaining -= batch;
            using var tx = dbe.CreateQuickTransaction();
            for (int i = 0; i < batch; i++)
            {
                var pos = new AaBenchPosition(offset + i, 0);
                var data = new AaBenchIdxData(offset + i, 0); // Unique Score for unique B+Tree index
                ids[offset + i] = tx.Spawn<AaBenchIdxUnit>(AaBenchIdxUnit.Position.Set(in pos), AaBenchIdxUnit.Data.Set(in data));
            }
            tx.Commit();
            offset += batch;
        }

        // Initial tick fence to establish baseline index state
        dbe.WriteTickFence(0);

        return new BenchEnv(sp, dbe, ids);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Benchmark 1: Versioned Cluster (Mixed SV + Versioned)
    // ═══════════════════════════════════════════════════════════════════════

    public static void RunVersionedCluster(int entityCount = 50_000, int iterations = 500)
    {
        Console.WriteLine("═══════════════════════════════════════════════════════");
        Console.WriteLine($"  Versioned Cluster Benchmark — {entityCount:N0} entities");
        Console.WriteLine("═══════════════════════════════════════════════════════");
        Console.WriteLine();

        using var env = CreateMixedClusterEnv(entityCount);
        var dbe = env.Dbe;
        var ids = env.EntityIds;

        // ── (a) Bulk iteration via GetClusterEnumerator ─────────────
        // Warmup
        for (int w = 0; w < 10; w++)
        {
            RunMixedClusterBulk(dbe, 1);
        }

        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        var sw = Stopwatch.StartNew();
        RunMixedClusterBulk(dbe, iterations);
        sw.Stop();
        double bulkUs = sw.Elapsed.TotalMicroseconds / iterations;
        double bulkNs = bulkUs * 1000.0 / entityCount;

        // ── (b) Random access via ArchetypeAccessor.Open ────────────
        // Warmup
        for (int w = 0; w < 10; w++)
        {
            RunMixedClusterRandomRead(dbe, ids, 1);
        }

        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        sw.Restart();
        RunMixedClusterRandomRead(dbe, ids, iterations);
        sw.Stop();
        double randomUs = sw.Elapsed.TotalMicroseconds / iterations;
        double randomNs = randomUs * 1000.0 / entityCount;

        // ── (c) Write + Commit ──────────────────────────────────────
        // Warmup
        for (int w = 0; w < 10; w++)
        {
            RunMixedClusterWrite(dbe, ids, 1);
        }

        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        sw.Restart();
        RunMixedClusterWrite(dbe, ids, iterations);
        sw.Stop();
        double writeUs = sw.Elapsed.TotalMicroseconds / iterations;
        double writeNs = writeUs * 1000.0 / entityCount;

        Console.WriteLine($"  Bulk iteration (SoA):          {bulkUs,8:F0} µs/iter  ({bulkNs:F1} ns/entity)");
        Console.WriteLine($"  Random access (Open):          {randomUs,8:F0} µs/iter  ({randomNs:F1} ns/entity)");
        Console.WriteLine($"  Write + Commit:                {writeUs,8:F0} µs/iter  ({writeNs:F1} ns/entity)");
        Console.WriteLine();
    }

    static void RunMixedClusterBulk(DatabaseEngine dbe, int iterations)
    {
        for (int iter = 0; iter < iterations; iter++)
        {
            using var tx = dbe.CreateQuickTransaction();
            var accessor = tx.For<AaBenchMixedCluster>();
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                ulong bits = cluster.OccupancyBits;
                var positions = cluster.GetReadOnlySpan(AaBenchMixedCluster.Position);
                var healths = cluster.GetReadOnlySpan(AaBenchMixedCluster.Health);
                while (bits != 0)
                {
                    int idx = BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    var p = positions[idx];
                    var h = healths[idx];
                }
            }
            accessor.Dispose();
        }
    }

    static void RunMixedClusterRandomRead(DatabaseEngine dbe, EntityId[] ids, int iterations)
    {
        for (int iter = 0; iter < iterations; iter++)
        {
            using var tx = dbe.CreateQuickTransaction();
            var accessor = tx.For<AaBenchMixedCluster>();
            for (int i = 0; i < ids.Length; i++)
            {
                var entity = accessor.Open(ids[i]);
                var p = entity.Read(AaBenchMixedCluster.Position);
                var h = entity.Read(AaBenchMixedCluster.Health);
            }
            accessor.Dispose();
        }
    }

    static void RunMixedClusterWrite(DatabaseEngine dbe, EntityId[] ids, int iterations)
    {
        for (int iter = 0; iter < iterations; iter++)
        {
            using var tx = dbe.CreateQuickTransaction();
            var accessor = tx.For<AaBenchMixedCluster>();
            for (int i = 0; i < ids.Length; i++)
            {
                var entity = accessor.OpenMut(ids[i]);
                ref var h = ref entity.Write(AaBenchMixedCluster.Health);
                h.Current -= 1;
            }
            accessor.Dispose();
            tx.Commit();
        }
    }

    static BenchEnv CreateMixedClusterEnv(int entityCount)
    {
        var name = $"AABenchMixed_{Environment.ProcessId}";
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

        var sp = sc.BuildServiceProvider();
        sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
        var dbe = sp.GetRequiredService<DatabaseEngine>();

        dbe.RegisterComponentFromAccessor<AaBenchPosition>();
        dbe.RegisterComponentFromAccessor<AaBenchMovement>();
        dbe.RegisterComponentFromAccessor<AaVcHealth>();
        dbe.InitializeArchetypes();

        var rng = new Random(42);
        var ids = new EntityId[entityCount];
        int remaining = entityCount;
        int offset = 0;
        while (remaining > 0)
        {
            int batch = Math.Min(1000, remaining);
            remaining -= batch;
            using var tx = dbe.CreateQuickTransaction();
            for (int i = 0; i < batch; i++)
            {
                var pos = new AaBenchPosition((float)(rng.NextDouble() * WorldSize), (float)(rng.NextDouble() * WorldSize));
                var mov = new AaBenchMovement((float)(rng.NextDouble() * 100), (float)(rng.NextDouble() * 100));
                var health = new AaVcHealth { Current = 100, Max = 100 };
                ids[offset + i] = tx.Spawn<AaBenchMixedCluster>(
                    AaBenchMixedCluster.Position.Set(in pos),
                    AaBenchMixedCluster.Movement.Set(in mov),
                    AaBenchMixedCluster.Health.Set(in health));
            }
            tx.Commit();
            offset += batch;
        }

        dbe.WriteTickFence(0);
        return new BenchEnv(sp, dbe, ids);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Benchmark 2: SIMD Query
    // ═══════════════════════════════════════════════════════════════════════

    public static void RunSimdQuery(int entityCount = 100_000, int iterations = 200)
    {
        Console.WriteLine("═══════════════════════════════════════════════════════");
        Console.WriteLine($"  SIMD Query Benchmark — {entityCount:N0} entities, {iterations} iterations");
        Console.WriteLine("═══════════════════════════════════════════════════════");
        Console.WriteLine();

        using var env = CreateSimdQueryEnv(entityCount);
        var dbe = env.Dbe;

        // Selectivity thresholds: Score is sequential 0..entityCount-1
        // Score >= threshold → (entityCount - threshold) results
        var cases = new[]
        {
            (label: " 1% selectivity", threshold: entityCount - entityCount / 100, expected: entityCount / 100),
            (label: "10% selectivity", threshold: entityCount - entityCount / 10,  expected: entityCount / 10),
            (label: "50% selectivity", threshold: entityCount / 2,                 expected: entityCount - entityCount / 2),
        };

        foreach (var (label, threshold, expected) in cases)
        {
            // Warmup
            for (int w = 0; w < 10; w++)
            {
                using var tx = dbe.CreateQuickTransaction();
                tx.Query<AaBenchIdxUnit>().WhereField<AaBenchIdxData>(d => d.Score >= threshold).Execute();
            }

            int actualCount = 0;
            GC.Collect(2, GCCollectionMode.Aggressive, true, true);
            var sw = Stopwatch.StartNew();
            for (int iter = 0; iter < iterations; iter++)
            {
                using var tx = dbe.CreateQuickTransaction();
                var results = tx.Query<AaBenchIdxUnit>().WhereField<AaBenchIdxData>(d => d.Score >= threshold).Execute();
                if (iter == 0)
                {
                    actualCount = results.Count;
                }
            }
            sw.Stop();
            double meanUs = sw.Elapsed.TotalMicroseconds / iterations;

            Console.WriteLine($"  {label} (Score >= {threshold,6}):  {meanUs,8:F1} µs/query  ({actualCount} results)");
        }

        Console.WriteLine();
    }

    static BenchEnv CreateSimdQueryEnv(int entityCount)
    {
        var name = $"AABenchSimd_{Environment.ProcessId}";
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

        var sp = sc.BuildServiceProvider();
        sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
        var dbe = sp.GetRequiredService<DatabaseEngine>();

        dbe.RegisterComponentFromAccessor<AaBenchPosition>();
        dbe.RegisterComponentFromAccessor<AaBenchIdxData>();
        dbe.InitializeArchetypes();

        var ids = new EntityId[entityCount];
        int remaining = entityCount;
        int offset = 0;
        while (remaining > 0)
        {
            int batch = Math.Min(1000, remaining);
            remaining -= batch;
            using var tx = dbe.CreateQuickTransaction();
            for (int i = 0; i < batch; i++)
            {
                var pos = new AaBenchPosition(offset + i, 0);
                var data = new AaBenchIdxData(offset + i, 0); // Sequential unique Score
                ids[offset + i] = tx.Spawn<AaBenchIdxUnit>(AaBenchIdxUnit.Position.Set(in pos), AaBenchIdxUnit.Data.Set(in data));
            }
            tx.Commit();
            offset += batch;
        }

        dbe.WriteTickFence(0);
        return new BenchEnv(sp, dbe, ids);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Benchmark 3: Ordered Query
    // ═══════════════════════════════════════════════════════════════════════

    public static void RunOrderedQuery(int entityCount = 50_000, int iterations = 200)
    {
        Console.WriteLine("═══════════════════════════════════════════════════════");
        Console.WriteLine($"  Ordered Query Benchmark — {entityCount:N0} entities, {iterations} iterations");
        Console.WriteLine("═══════════════════════════════════════════════════════");
        Console.WriteLine();

        using var env = CreateOrderedQueryEnv(entityCount);
        var dbe = env.Dbe;

        // Case 1: Take(100) from start
        {
            // Warmup
            for (int w = 0; w < 10; w++)
            {
                using var tx = dbe.CreateQuickTransaction();
                tx.Query<AaBenchIdxUnit>()
                    .WhereField<AaBenchIdxData>(d => d.Score >= 0)
                    .OrderByField<AaBenchIdxData, int>(d => d.Score)
                    .Take(100)
                    .ExecuteOrdered();
            }

            GC.Collect(2, GCCollectionMode.Aggressive, true, true);
            var sw = Stopwatch.StartNew();
            for (int iter = 0; iter < iterations; iter++)
            {
                using var tx = dbe.CreateQuickTransaction();
                tx.Query<AaBenchIdxUnit>()
                    .WhereField<AaBenchIdxData>(d => d.Score >= 0)
                    .OrderByField<AaBenchIdxData, int>(d => d.Score)
                    .Take(100)
                    .ExecuteOrdered();
            }
            sw.Stop();
            double meanUs = sw.Elapsed.TotalMicroseconds / iterations;
            Console.WriteLine($"  Take(100) from start:          {meanUs,8:F1} µs/query");
        }

        // Case 2: Skip(25000).Take(100) (middle page)
        {
            int skip = entityCount / 2;

            // Warmup
            for (int w = 0; w < 10; w++)
            {
                using var tx = dbe.CreateQuickTransaction();
                tx.Query<AaBenchIdxUnit>()
                    .WhereField<AaBenchIdxData>(d => d.Score >= 0)
                    .OrderByField<AaBenchIdxData, int>(d => d.Score)
                    .Skip(skip).Take(100)
                    .ExecuteOrdered();
            }

            GC.Collect(2, GCCollectionMode.Aggressive, true, true);
            var sw = Stopwatch.StartNew();
            for (int iter = 0; iter < iterations; iter++)
            {
                using var tx = dbe.CreateQuickTransaction();
                tx.Query<AaBenchIdxUnit>()
                    .WhereField<AaBenchIdxData>(d => d.Score >= 0)
                    .OrderByField<AaBenchIdxData, int>(d => d.Score)
                    .Skip(skip).Take(100)
                    .ExecuteOrdered();
            }
            sw.Stop();
            double meanUs = sw.Elapsed.TotalMicroseconds / iterations;
            Console.WriteLine($"  Skip({skip}).Take(100):        {meanUs,8:F1} µs/query");
        }

        // Case 3: Full ordered (no skip/take, all entities)
        {
            // Warmup
            for (int w = 0; w < 10; w++)
            {
                using var tx = dbe.CreateQuickTransaction();
                tx.Query<AaBenchIdxUnit>()
                    .WhereField<AaBenchIdxData>(d => d.Score >= 0)
                    .OrderByField<AaBenchIdxData, int>(d => d.Score)
                    .ExecuteOrdered();
            }

            GC.Collect(2, GCCollectionMode.Aggressive, true, true);
            var sw = Stopwatch.StartNew();
            for (int iter = 0; iter < iterations; iter++)
            {
                using var tx = dbe.CreateQuickTransaction();
                tx.Query<AaBenchIdxUnit>()
                    .WhereField<AaBenchIdxData>(d => d.Score >= 0)
                    .OrderByField<AaBenchIdxData, int>(d => d.Score)
                    .ExecuteOrdered();
            }
            sw.Stop();
            double meanUs = sw.Elapsed.TotalMicroseconds / iterations;
            Console.WriteLine($"  Full ordered ({entityCount:N0}):          {meanUs,8:F1} µs/query");
        }

        Console.WriteLine();
    }

    static BenchEnv CreateOrderedQueryEnv(int entityCount)
    {
        var name = $"AABenchOrdered_{Environment.ProcessId}";
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

        var sp = sc.BuildServiceProvider();
        sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
        var dbe = sp.GetRequiredService<DatabaseEngine>();

        dbe.RegisterComponentFromAccessor<AaBenchPosition>();
        dbe.RegisterComponentFromAccessor<AaBenchIdxData>();
        dbe.InitializeArchetypes();

        var ids = new EntityId[entityCount];
        int remaining = entityCount;
        int offset = 0;
        while (remaining > 0)
        {
            int batch = Math.Min(1000, remaining);
            remaining -= batch;
            using var tx = dbe.CreateQuickTransaction();
            for (int i = 0; i < batch; i++)
            {
                var pos = new AaBenchPosition(offset + i, 0);
                var data = new AaBenchIdxData(offset + i, 0); // Sequential Score for deterministic ordering
                ids[offset + i] = tx.Spawn<AaBenchIdxUnit>(AaBenchIdxUnit.Position.Set(in pos), AaBenchIdxUnit.Data.Set(in data));
            }
            tx.Commit();
            offset += batch;
        }

        dbe.WriteTickFence(0);
        return new BenchEnv(sp, dbe, ids);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Benchmark 4: Zone Map Pruning
    // ═══════════════════════════════════════════════════════════════════════

    public static void RunZoneMapPruning(int entityCount = 100_000, int iterations = 200)
    {
        Console.WriteLine("═══════════════════════════════════════════════════════");
        Console.WriteLine($"  Zone Map Pruning Benchmark — {entityCount:N0} entities, {iterations} iterations");
        Console.WriteLine("═══════════════════════════════════════════════════════");
        Console.WriteLine();

        using var env = CreateZoneMapEnv(entityCount);
        var dbe = env.Dbe;

        // Selectivity thresholds: Score = entityIndex (0..entityCount-1)
        // Score >= threshold → (entityCount - threshold) results
        var cases = new[]
        {
            (label: "0.1% selectivity", threshold: entityCount - entityCount / 1000, expected: entityCount / 1000),
            (label: "  1% selectivity", threshold: entityCount - entityCount / 100,  expected: entityCount / 100),
            (label: " 10% selectivity", threshold: entityCount - entityCount / 10,   expected: entityCount / 10),
            (label: " 50% selectivity", threshold: entityCount / 2,                  expected: entityCount - entityCount / 2),
        };

        foreach (var (label, threshold, expected) in cases)
        {
            // Warmup
            for (int w = 0; w < 10; w++)
            {
                using var tx = dbe.CreateQuickTransaction();
                tx.Query<AaBenchIdxUnit>().WhereField<AaBenchIdxData>(d => d.Score >= threshold).Execute();
            }

            int actualCount = 0;
            GC.Collect(2, GCCollectionMode.Aggressive, true, true);
            var sw = Stopwatch.StartNew();
            for (int iter = 0; iter < iterations; iter++)
            {
                using var tx = dbe.CreateQuickTransaction();
                var results = tx.Query<AaBenchIdxUnit>().WhereField<AaBenchIdxData>(d => d.Score >= threshold).Execute();
                if (iter == 0)
                {
                    actualCount = results.Count;
                }
            }
            sw.Stop();
            double meanUs = sw.Elapsed.TotalMicroseconds / iterations;

            Console.WriteLine($"  {label} (Score >= {threshold,6}):  {meanUs,8:F1} µs/query  ({actualCount} results)");
        }

        Console.WriteLine();
    }

    static BenchEnv CreateZoneMapEnv(int entityCount)
    {
        var name = $"AABenchZoneMap_{Environment.ProcessId}";
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

        var sp = sc.BuildServiceProvider();
        sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
        var dbe = sp.GetRequiredService<DatabaseEngine>();

        dbe.RegisterComponentFromAccessor<AaBenchPosition>();
        dbe.RegisterComponentFromAccessor<AaBenchIdxData>();
        dbe.InitializeArchetypes();

        // Spawn with Score = entityIndex for contiguous cluster ranges (natural zone map clustering)
        var ids = new EntityId[entityCount];
        int remaining = entityCount;
        int offset = 0;
        while (remaining > 0)
        {
            int batch = Math.Min(1000, remaining);
            remaining -= batch;
            using var tx = dbe.CreateQuickTransaction();
            for (int i = 0; i < batch; i++)
            {
                var pos = new AaBenchPosition(offset + i, 0);
                var data = new AaBenchIdxData(offset + i, 0); // Sequential → natural cluster range partitions
                ids[offset + i] = tx.Spawn<AaBenchIdxUnit>(AaBenchIdxUnit.Position.Set(in pos), AaBenchIdxUnit.Data.Set(in data));
            }
            tx.Commit();
            offset += batch;
        }

        // Tick fence to compute zone maps from cluster data
        dbe.WriteTickFence(0);
        return new BenchEnv(sp, dbe, ids);
    }
}
