using System;
using System.Diagnostics;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Typhon.Engine;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Benchmark;

/// <summary>
/// Times <c>ArchetypeClusterState.RecomputeClusterAabb</c> in isolation, single-threaded — ns per entity slot.
/// </summary>
/// <remarks>
/// <para><b>Why not measure it through the fence.</b> The AabbRefresh phase also runs drift detection, the outlier guard, repair nomination and the per-cell
/// index write, and it runs them across eight workers whose imbalance is part of the wall time. Dividing that span by the slot count gives a figure that moves
/// when any of those change — which is exactly what happened while the rest of this campaign was in flight. This target calls the recompute and nothing else,
/// on one thread, over a cluster set that is already built and warm, so a change to the scan shows up as a change to the number.</para>
/// <para>Reported per OCCUPIED SLOT, because that is the unit the loop iterates. Clusters are ~half full at the shapes the partition benchmark produces, so
/// per-cluster figures would fold the fill factor into the result.</para>
/// </remarks>
public static class ClusterAabbRecomputeProfile
{
    public static void Run(string[] args)
    {
        var entities = ArgInt(args, "--entities", 64_000);
        var perCell = ArgInt(args, "--percell", 64);
        var reps = ArgInt(args, "--reps", 40);
        var dim = ArgInt(args, "--dim", 3);

        Console.WriteLine("Cluster AABB recompute — isolated scan cost");
        Console.WriteLine($"  entities={entities:N0}  perCell={perCell}  dim={dim}  reps={reps}  config={(IsDebug() ? "DEBUG (not comparable)" : "RELEASE")}");
        Console.WriteLine();

        if (dim == 3)
        {
            Measure<SpMatUnit3, SpMatPos3>(entities, perCell, reps, 3, static (ref SpMatPos3 p, float x, float y, float z, float h, int tag) =>
            {
                p.Bounds = new AABB3F { MinX = x - h, MinY = y - h, MinZ = z - h, MaxX = x + h, MaxY = y + h, MaxZ = z + h };
                p.Tag = tag;
            });
        }
        else
        {
            Measure<SpMatUnit2, SpMatPos2>(entities, perCell, reps, 2, static (ref SpMatPos2 p, float x, float y, float z, float h, int tag) =>
            {
                p.Bounds = new AABB2F { MinX = x - h, MinY = y - h, MaxX = x + h, MaxY = y + h };
                p.Tag = tag;
            });
        }
    }

    private delegate void Writer<TPos>(ref TPos pos, float x, float y, float z, float halfExtent, int tag) where TPos : unmanaged;

    private static void Measure<TArch, TPos>(int entities, int perCell, int reps, int dim, Writer<TPos> write)
        where TArch : Archetype<TArch>
        where TPos : unmanaged
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
              o.DatabaseName = $"AabbProf_{Environment.ProcessId}";
              o.DatabaseCacheSize = (ulong)(64L * 1024 * PagedMMF.PageSize);
              o.TestMode = true;
              o.PagesDebugPattern = false;
          })
          .AddInMemoryWalEngine();

        using var sp = sc.BuildServiceProvider();
        sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
        var dbe = sp.GetRequiredService<DatabaseEngine>();

        dbe.RegisterComponentFromAccessor<TPos>();
        const float worldExtent = 100_000f;
        var cellSize = DeriveCellSize(dim, entities, perCell, worldExtent);
        var worldMax = worldExtent;
        dbe.ConfigureSpatialGrid(new SpatialGridConfig(
            new Vector3(0, 0, 0),
            new Vector3(worldExtent, worldExtent, dim == 3 ? worldExtent : cellSize),
            cellSize));
        dbe.InitializeArchetypes();

        var rng = new Random(12345);
        var comp = ArchetypeCompOf<TArch, TPos>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < entities; i++)
            {
                var pos = default(TPos);
                write(ref pos, (float)rng.NextDouble() * worldMax, (float)rng.NextDouble() * worldMax,
                    dim == 3 ? (float)rng.NextDouble() * worldMax : 0f, 0.5f, i);
                tx.Spawn<TArch>(comp.Set(in pos));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);

        var state = dbe._archetypeStates[Archetype<TArch>.Metadata.ArchetypeId].ClusterState;
        var grid = dbe.SpatialGrid;
        Console.WriteLine($"  active clusters = {state.ActiveClusterCount:N0}, mean fill = {entities / (double)state.ActiveClusterCount:F1} of 64 slots");

        // Warm-up: tiered compilation and the page window both need a pass before the measurement means anything.
        for (var w = 0; w < 5; w++)
        {
            ScanOnce(state, grid, out _);
        }

        var samples = new double[reps];
        long slots = 0;
        for (var r = 0; r < reps; r++)
        {
            var sw = Stopwatch.StartNew();
            ScanOnce(state, grid, out slots);
            sw.Stop();
            samples[r] = sw.Elapsed.TotalMilliseconds;
        }

        Array.Sort(samples);
        var median = samples[reps / 2];
        var best = samples[0];
        Console.WriteLine();
        Console.WriteLine($"  slots scanned per pass : {slots:N0}");
        Console.WriteLine($"  median pass            : {median:F3} ms   ->  {median * 1e6 / slots:F1} ns / slot");
        Console.WriteLine($"  best pass              : {best:F3} ms   ->  {best * 1e6 / slots:F1} ns / slot");
        Console.WriteLine($"  bytes touched          : {slots * 28L / 1024.0 / 1024.0:F2} MiB per pass (28 B component stride)");
        Console.WriteLine($"  implied bandwidth      : {slots * 28L / (median / 1000.0) / 1e9:F2} GB/s");
        Console.WriteLine();
    }

    /// <summary>One full pass over every active cluster, calling only the recompute.</summary>
    private static void ScanOnce(ArchetypeClusterState state, SpatialGrid grid, out long slotsScanned)
    {
        long total = 0;
        var accessor = state.ClusterSegment.CreateChunkAccessor();
        try
        {
            for (var i = 0; i < state.ActiveClusterCount; i++)
            {
                var chunkId = state.ActiveClusterIds[i];
                if (chunkId <= 0 || chunkId >= state.ClusterCellMap.Length)
                {
                    continue;
                }

                var cellKey = state.ClusterCellMap[chunkId];
                if (cellKey < 0)
                {
                    continue;
                }

                grid.CellOrigin(cellKey, out var ox, out var oy, out var oz);
                var aabb = state.RecomputeClusterAabb(chunkId, ref accessor, ox, oy, oz, out var scanned);
                total += scanned;

                // Consume the result so the JIT cannot elide the call.
                if (float.IsNaN(aabb.MinX))
                {
                    throw new InvalidOperationException("unreachable");
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        slotsScanned = total;
    }

    private static Comp<TPos> ArchetypeCompOf<TArch, TPos>() where TArch : Archetype<TArch> where TPos : unmanaged
    {
        // The matrix archetypes expose their spatial component as a public static field named Pos.
        var field = typeof(TArch).GetField("Pos");
        return (Comp<TPos>)field.GetValue(null);
    }

    /// <summary>Cell size that puts <paramref name="perCell"/> entities in a cell for a uniform spread over the world extent.</summary>
    private static float DeriveCellSize(int dim, int entities, int perCell, float worldExtent)
    {
        var cells = Math.Max(1, entities / Math.Max(1, perCell));
        var across = MathF.Pow(cells, 1f / dim);
        return worldExtent / MathF.Max(1f, across);
    }

    private static bool IsDebug()
    {
#if DEBUG
        return true;
#else
        return false;
#endif
    }

    private static int ArgInt(string[] args, string name, int fallback)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var v) ? v : fallback;
    }
}
