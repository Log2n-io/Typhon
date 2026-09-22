using System;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Typhon.Engine;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Benchmark;

// ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// Minimal repro for: Transaction.OpenMut throws
//     InvalidOperationException: Entity Entity(Key=N, Arch=1) not found or not visible at TSN M
// after sustained migration. Found by the #872 partitioning matrix, which lost 4 of 96 runs to it.
//
// The workload is deliberately the smallest thing that reproduces it: spawn once, then MOVE every entity and run the
// fence, for many ticks. No destroys, no spawns after the first tick, no queries, no concurrency. An entity that
// becomes unreachable here was lost by migration and nothing else.
//
// Run: bin/Release/net10.0/Typhon.Benchmark.exe --profile-openmut [--entities N] [--ticks N] [--cell F] [--seeds N]
// ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════

public static class OpenMutLossProfile
{
    private const float World = 1_000f;

    public static void Run(string[] args)
    {
        var entities = ArgInt(args, "--entities", 4_000);
        var ticks = ArgInt(args, "--ticks", 120);
        var cell = ArgFloat(args, "--cell", 252f);
        var seeds = ArgInt(args, "--seeds", 8);
        var noIndex = Array.IndexOf(args, "--no-index") >= 0;
        var budget = ArgFloat(args, "--budget", 1.0f);
        // Ablation arms. Repair off: nominate nothing (extent tops out near 1 + hysteresis, so 1.19 is unreachable) and disable the valve.
        // Drift off: a target region larger than any cluster can be, so DetectDriftersInCluster's level-1 gate never opens.
        var noRepair = Array.IndexOf(args, "--no-repair") >= 0;
        var noDrift = Array.IndexOf(args, "--no-drift") >= 0;

        Console.WriteLine($"migration fault repro — entities={entities} ticks={ticks} cell={cell} seeds={seeds} arm={(noIndex ? "NO-INDEX" : "indexed")} budget={budget} repair={!noRepair} drift={!noDrift}");

        var failures = 0;
        for (var s = 0; s < seeds; s++)
        {
            var seed = 20260904 + (s * 7919);
            var (failedTick, message) = noIndex
                ? RunOne<SpMatUnit3NoIx, SpMatPos3NoIx>(entities, ticks, cell, seed, budget, noRepair, noDrift, SpMatUnit3NoIx.Pos, WriteNoIx)
                : RunOne<SpMatUnit3, SpMatPos3>(entities, ticks, cell, seed, budget, noRepair, noDrift, SpMatUnit3.Pos, WriteIx);
            if (failedTick >= 0)
            {
                failures++;
                Console.WriteLine($"  seed {seed}: FAIL at tick {failedTick} — {message}");
            }
            else
            {
                Console.WriteLine($"  seed {seed}: ok ({ticks} ticks)");
            }
        }

        Console.WriteLine($"{failures} of {seeds} seed(s) lost an entity.");
    }

    private delegate void Writer<TPos>(ref TPos pos, float x, float y, float z, float half, int tag);

    private static void WriteIx(ref SpMatPos3 p, float x, float y, float z, float h, int tag)
    {
        p.Bounds = Box(x, y, z, h);
        p.Tag = tag;
    }

    private static void WriteNoIx(ref SpMatPos3NoIx p, float x, float y, float z, float h, int tag)
    {
        p.Bounds = Box(x, y, z, h);
        p.Tag = tag;
    }

    private static (int failedTick, string message) RunOne<TArch, TPos>(int entities, int ticks, float cell, int seed, float budget,
        bool noRepair, bool noDrift, Comp<TPos> comp, Writer<TPos> write)
        where TArch : Archetype<TArch>
        where TPos : unmanaged
    {
        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
          .AddResourceRegistry().AddMemoryAllocator().AddEpochManager()
          .AddHighResolutionSharedTimer().AddDeadlineWatchdog()
          .AddScopedManagedPagedMemoryMappedFile(o =>
          {
              o.DatabaseName = $"OpenMutRepro_{Environment.ProcessId}";
              o.DatabaseCacheSize = (ulong)(64L * 1024 * PagedMMF.PageSize);
              o.TestMode = true;
              o.PagesDebugPattern = false;
          })
          .AddInMemoryWalEngine();

        using var sp = sc.BuildServiceProvider();
        sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
        var dbe = sp.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<TPos>();
        dbe.ConfigureSpatialGrid(new SpatialGridConfig(
            new Vector3(0, 0, 0), new Vector3(World, World, World), cell,
            clusterTargetExtentRatio: noDrift ? 100f : 0.25f,
            clusterRepairExtentRatio: noRepair ? 1.19f : 0.75f,
            reclusterBudgetMs: budget,
            clusterRepairCriticalExtentRatio: noRepair ? 0f : 1.0f));
        dbe.InitializeArchetypes();

        var rng = new Random(seed);
        var xs = new float[entities];
        var ys = new float[entities];
        var zs = new float[entities];
        var ids = new EntityId[entities];
        var half = cell * 0.01f;

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < entities; i++)
            {
                xs[i] = (float)(rng.NextDouble() * World);
                ys[i] = (float)(rng.NextDouble() * World);
                zs[i] = (float)(rng.NextDouble() * World);
                var p = default(TPos);
                write(ref p, xs[i], ys[i], zs[i], half, i);
                ids[i] = tx.Spawn<TArch>(comp.Set(in p));
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        for (var t = 0; t < ticks; t++)
        {
            var step = cell * 0.25f;
            for (var i = 0; i < entities; i++)
            {
                xs[i] = Math.Clamp(xs[i] + (float)((rng.NextDouble() - 0.5) * step), 5f, World - 5f);
                ys[i] = Math.Clamp(ys[i] + (float)((rng.NextDouble() - 0.5) * step), 5f, World - 5f);
                zs[i] = Math.Clamp(zs[i] + (float)((rng.NextDouble() - 0.5) * step), 5f, World - 5f);
            }

            try
            {
                using var tx = dbe.CreateQuickTransaction();
                for (var i = 0; i < entities; i++)
                {
                    ref var p = ref tx.OpenMut(ids[i]).Write(comp);
                    write(ref p, xs[i], ys[i], zs[i], half, i);
                }
                tx.Commit();
            }
            catch (Exception ex)
            {
                return (t, $"[write] {ex.GetType().Name}: {ex.Message}");
            }

            try
            {
                dbe.WriteTickFence(t + 2);
            }
            catch (Exception ex)
            {
                return (t, $"[fence] {ex.GetType().Name}: {ex.Message}");
            }
        }

        return (-1, null);
    }

    private static AABB3F Box(float x, float y, float z, float h) =>
        new() { MinX = x - h, MinY = y - h, MinZ = z - h, MaxX = x + h, MaxY = y + h, MaxZ = z + h };

    private static int ArgInt(string[] args, string name, int fallback)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var v) ? v : fallback;
    }

    private static float ArgFloat(string[] args, string name, float fallback)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length && float.TryParse(args[i + 1], out var v) ? v : fallback;
    }
}

/// <summary>The same spatial shape with no secondary index — the ablation arm.</summary>
[Component("Typhon.Benchmark.SpMat.Pos3NoIx", 1, StorageMode = StorageMode.SingleVersion)]
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
struct SpMatPos3NoIx
{
    [Field]
    [SpatialIndex]
    public AABB3F Bounds;

    [Field]
    public int Tag;
}

[Archetype]
partial class SpMatUnit3NoIx : Archetype<SpMatUnit3NoIx>
{
    public static readonly Comp<SpMatPos3NoIx> Pos = Register<SpMatPos3NoIx>();
}
