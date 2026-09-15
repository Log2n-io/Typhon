using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Typhon.Engine;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Benchmark;

// ═══════════════════════════════════════════════════════════════════════════════════
// #872 step 12 — where the repair path's time actually goes.
//
// The design budgets a batched relocation at ~60 ns/entity and derives the ~6 ms per
// 100 K-entity cell that makes a full re-sort the RARE path. AC-12.7 measured 1 331 to
// 6 992 ns/entity on a warm engine, 22x to 117x that. This workload exists to say WHY,
// under a tracing profiler, and it is deliberately a faithful replica of the fixture
// that produced the number rather than a reduced model of it.
//
// Faithful means the archetype matters. ClMigPos carries a spatial AABB2F *and* an
// [Index(AllowMultiple = true)] int, and ClMigUnit pairs it with a Transient component —
// so a migrated entity pays a spatial rebase, an index staging record with an element id,
// a zone-map widen, and TWO component copies across two segments. A profile taken against
// a bare position-only archetype would attribute none of that and would be measuring a
// different path.
//
// Run with: bin/Release/net10.0/Typhon.Benchmark.exe --profile-repair [--entities N] [--rounds N] [--no-index] [--no-transient]
// ═══════════════════════════════════════════════════════════════════════════════════

[Component("Typhon.Benchmark.ClRep.Pos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct ClRepPos
{
    [Field]
    [SpatialIndex]
    public AABB2F Bounds;

    /// <summary>Indexed, non-unique — the shape <c>ClMigPos</c> carries, and the reason index staging appears in the profile at all.</summary>
    [Field]
    [Index(AllowMultiple = true)]
    public int Tag;
}

/// <summary>The same spatial + indexed shape with the index dropped, for the <c>--no-index</c> ablation arm.</summary>
[Component("Typhon.Benchmark.ClRep.PosNoIndex", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct ClRepPosNoIndex
{
    [Field]
    [SpatialIndex]
    public AABB2F Bounds;

    [Field]
    public int Tag;
}

[Component("Typhon.Benchmark.ClRep.Scratch", 1, StorageMode = StorageMode.Transient)]
[StructLayout(LayoutKind.Sequential)]
struct ClRepScratch
{
    [Field]
    public int Counter;

    [Field]
    public float Energy;
}

/// <summary>The faithful archetype: spatial + indexed + a Transient neighbour, exactly <c>ClMigUnit</c>'s shape.</summary>
[Archetype]
partial class ClRepUnit : Archetype<ClRepUnit>
{
    public static readonly Comp<ClRepPos> Pos = Register<ClRepPos>();
    public static readonly Comp<ClRepScratch> Scratch = Register<ClRepScratch>();
}

/// <summary>Spatial + indexed, no Transient — isolates the second component copy and its second segment.</summary>
[Archetype]
partial class ClRepUnitNoTransient : Archetype<ClRepUnitNoTransient>
{
    public static readonly Comp<ClRepPos> Pos = Register<ClRepPos>();
}

/// <summary>Spatial only — isolates index staging, the element id and the zone-map widen.</summary>
[Archetype]
partial class ClRepUnitNoIndex : Archetype<ClRepUnitNoIndex>
{
    public static readonly Comp<ClRepPosNoIndex> Pos = Register<ClRepPosNoIndex>();
    public static readonly Comp<ClRepScratch> Scratch = Register<ClRepScratch>();
}

public static class ClusterRepairProfile
{
    /// <summary>
    /// Repair switched off, everything else identical — the baseline the marginal cost is measured against.
    /// </summary>
    /// <remarks>
    /// The timed tick does far more than relocate: it snapshots the dirty bitmap, recomputes zone maps, walks every
    /// occupied slot of every cluster in AabbRefresh, runs drift detection and nomination, sweeps dormancy and emits the
    /// columnar WAL block — all for the 2 000 entities Scramble rewrote a moment earlier, none of it relocation. Dividing
    /// the whole fence by the repaired count charges all of that to the repair. Running the identical workload with
    /// ReclusterBudgetMs at zero isolates it without needing any assumption about which tick repairs and which does not.
    /// </remarks>
    private static bool NoRepair;

    private const float CellSize = 100f;
    private const float WorldMax = 1000f;

    public static void Run(string[] args)
    {
        var entities = ArgValue(args, "--entities", 2000);
        var rounds = ArgValue(args, "--rounds", 8);
        var noIndex = Array.IndexOf(args, "--no-index") >= 0;
        var noTransient = Array.IndexOf(args, "--no-transient") >= 0;
        NoRepair = Array.IndexOf(args, "--no-repair") >= 0;

        var arm = (noIndex ? "no-index" : noTransient ? "no-transient" : "faithful") + (NoRepair ? " +NO-REPAIR (baseline)" : "");
        Console.WriteLine($"#872 step-12 repair profile — arm={arm} entities={entities} rounds={rounds}");

        if (noIndex)
        {
            RunArm<ClRepUnitNoIndex, ClRepPosNoIndex>(entities, rounds, ClRepUnitNoIndex.Pos, MakeNoIndex, ReadNoIndex);
        }
        else if (noTransient)
        {
            RunArm<ClRepUnitNoTransient, ClRepPos>(entities, rounds, ClRepUnitNoTransient.Pos, MakePos, ReadPos);
        }
        else
        {
            RunArm<ClRepUnit, ClRepPos>(entities, rounds, ClRepUnit.Pos, MakePos, ReadPos);
        }
    }

    private static ClRepPos MakePos(float x, float y, int tag) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Tag = tag };

    private static (float x, float y, int tag) ReadPos(in ClRepPos p) =>
        (0.5f * (p.Bounds.MinX + p.Bounds.MaxX), 0.5f * (p.Bounds.MinY + p.Bounds.MaxY), p.Tag);

    private static ClRepPosNoIndex MakeNoIndex(float x, float y, int tag) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Tag = tag };

    private static (float x, float y, int tag) ReadNoIndex(in ClRepPosNoIndex p) =>
        (0.5f * (p.Bounds.MinX + p.Bounds.MaxX), 0.5f * (p.Bounds.MinY + p.Bounds.MaxY), p.Tag);

    private delegate TPos Factory<TPos>(float x, float y, int tag);

    private delegate (float x, float y, int tag) Reader<TPos>(in TPos pos);

    private static unsafe void RunArm<TArch, TPos>(int entities, int rounds, Comp<TPos> comp, Factory<TPos> make, Reader<TPos> read)
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
              o.DatabaseName = $"ClRepProfile_{Environment.ProcessId}";
              o.DatabaseCacheSize = (ulong)(64L * 1024 * PagedMMF.PageSize);
              o.TestMode = true;
              o.PagesDebugPattern = false;
          })
          .AddInMemoryWalEngine();

        using var sp = sc.BuildServiceProvider();
        sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
        var dbe = sp.GetRequiredService<DatabaseEngine>();

        dbe.RegisterComponentFromAccessor<TPos>();
        dbe.RegisterComponentFromAccessor<ClRepScratch>();

        // The budget is set far above any unit's cost on purpose: this workload measures what a repair COSTS, and an
        // admission threshold that refused one would measure nothing. RepairWorstClustersPerUnit = 0 takes the whole
        // cell, which is the shape AC-12.7 timed.
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(
            new Vector2(0, 0),
            new Vector2(WorldMax, WorldMax),
            CellSize,
            clusterRepairExtentRatio: 0.75f,
            reclusterBudgetMs: NoRepair ? 0f : 1000f,
            repairWorstClustersPerUnit: 0,
            // No cooldown: each round scrambles and re-repairs the same cell within a few ticks, which a cooldown would refuse.
            repairCooldownTicks: 0));
        dbe.InitializeArchetypes();

        var meta = Archetype<TArch>.Metadata;
        var archetypeId = meta.ArchetypeId;

        // Scattered spawn order against a first-fit claim: consecutive spawns land far apart, so every cluster ends up
        // holding points from all over the cell. That is the degraded layout, produced by the engine's own placement.
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < entities; i++)
            {
                var x = 4f + ((i * 37) % 92) + ((i / 92) % 4) * 0.2f;
                var y = 4f + ((i * 61) % 92) + ((i / 92) % 4) * 0.2f;
                tx.Spawn<TArch>(comp.Set(make(x, y, i)));
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        // ── The repair tick AND the tick that did not repair ────────────────────────────────────────────────────────
        //
        // Dividing a whole WriteTickFence by RepairedEntityCount was the first version of this workload and it overstates
        // the repair badly: the timed tick ALSO does the ordinary fence work for the 2 000 entities Scramble rewrote a
        // moment earlier — the dirty-bitmap snapshot, the zone-map recompute, an AabbRefresh walking every occupied slot
        // of all ~41 clusters, drift detection, nomination, the dormancy sweep and the columnar WAL emit. None of that is
        // relocation and none of it is what the design's 50-80 ns budget covers.
        //
        // The tell is in the trace: ReadSpatialCenter3D runs 2.2 times per "migrated" entity and does not appear in
        // ExecuteMigrations at all — it belongs to drift detection. So a baseline is not a refinement here, it is the
        // difference between measuring the repair and measuring the tick that contains it.
        //
        // The baseline is free: nomination happens in AabbRefresh and is consumed by the NEXT tick's Prep, so the first
        // fence after a Scramble always repairs nothing while carrying the full scrambled-tick cost. The attempt loop was
        // already discarding exactly those ticks.
        var samples = new List<(int moved, double ms)>();
        var baselines = new List<double>();
        var tick = 2L;
        for (var round = 0; round < rounds; round++)
        {
            for (var attempt = 0; attempt < 6; attempt++)
            {
                var sw = Stopwatch.StartNew();
                dbe.WriteTickFence(tick++);
                sw.Stop();

                var moved = dbe.GetSpatialTelemetry(archetypeId).RepairedEntityCount;
                if (NoRepair)
                {
                    // Nothing repairs, so the tick itself is the sample. Attributed to the same entity count so the two
                    // arms are directly subtractable.
                    samples.Add((entities, sw.Elapsed.TotalMilliseconds));
                    break;
                }

                if (moved > 0)
                {
                    samples.Add((moved, sw.Elapsed.TotalMilliseconds));
                    break;
                }

                baselines.Add(sw.Elapsed.TotalMilliseconds);
            }

            Scramble<TArch, TPos>(dbe, comp, make, read, round);
        }

        var baselineBest = double.MaxValue;
        var baselineTotal = 0d;
        foreach (var ms in baselines)
        {
            baselineBest = Math.Min(baselineBest, ms);
            baselineTotal += ms;
        }

        var baselineMean = baselines.Count > 0 ? baselineTotal / baselines.Count : 0d;
        Console.WriteLine($"baseline (scrambled tick, no repair): {baselines.Count} samples, "
            + $"best {(baselines.Count > 0 ? baselineBest : 0d):F3} ms, mean {baselineMean:F3} ms");

        var best = double.MaxValue;
        var total = 0d;
        var marginalBest = double.MaxValue;
        Console.WriteLine($"repairs measured: {samples.Count}");
        foreach (var (moved, ms) in samples)
        {
            var ns = ms * 1_000_000d / moved;
            var marginal = (ms - baselineBest) * 1_000_000d / moved;
            best = Math.Min(best, ns);
            marginalBest = Math.Min(marginalBest, marginal);
            total += ns;
            Console.WriteLine($"  {moved,7} entities in {ms,8:F3} ms = {ns,9:F1} ns/entity gross, {marginal,9:F1} ns/entity marginal");
        }

        if (samples.Count > 0)
        {
            Console.WriteLine($"  GROSS    best {best:F1} ns/entity, mean {total / samples.Count:F1}");
            Console.WriteLine($"  MARGINAL best {marginalBest:F1} ns/entity ({marginalBest / 60d:F1}x the design's 60 ns) "
                + $"=> {marginalBest * 100_000 / 1_000_000d:F1} ms projected for a 100 K-entity cell (design says ~6 ms)");
        }
    }

    /// <summary>Move every entity to a new pseudo-random position so the next tick has a degraded layout to repair.</summary>
    /// <remarks>
    /// A pure function of (chunkId, slot, round), not a sequential RNG: the world must not depend on the order the cluster
    /// enumerator happens to visit, or successive runs of the same workload profile different inputs.
    /// </remarks>
    private static unsafe void Scramble<TArch, TPos>(DatabaseEngine dbe, Comp<TPos> comp, Factory<TPos> make, Reader<TPos> read, int round)
        where TArch : Archetype<TArch>
        where TPos : unmanaged
    {
        using var tx = dbe.CreateQuickTransaction();
        var accessor = tx.For<TArch>();
        try
        {
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
#pragma warning disable TYPHON009 // Read-only: the tag is carried across, the position goes through WriteSpatial.
                var positions = cluster.GetSpan(comp);
#pragma warning restore TYPHON009
                var bits = cluster.OccupancyBits;
                while (bits != 0)
                {
                    var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;

                    var h = (uint)((cluster.ChunkId * 0x9E3779B1) ^ (slot * 0x85EBCA6B) ^ (round * 0x27D4EB2F));
                    h ^= h >> 15;
                    h *= 0x2C1B3C6D;
                    h ^= h >> 12;
                    var g = h * 0x27D4EB2F;
                    g ^= g >> 15;

                    var (_, _, tag) = read(in positions[slot]);
                    cluster.WriteSpatial(comp, slot, make(4f + (h % 10_000) * (92f / 10_000f), 4f + (g % 10_000) * (92f / 10_000f), tag));
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        tx.Commit();
    }

    private static int ArgValue(string[] args, string name, int fallback)
    {
        var idx = Array.IndexOf(args, name);
        return idx >= 0 && idx + 1 < args.Length && int.TryParse(args[idx + 1], out var v) ? v : fallback;
    }
}
