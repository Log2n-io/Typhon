using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Typhon.Engine;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Benchmark;

// ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// #872 — the spatial partitioning matrix.
//
// ONE workload, run across a cross product of world shape, motion model, density and population, with the same
// instrumentation taken every time. The point is not any single number: it is which axis each cost actually tracks.
// The design's own open questions are the ones this answers with measurements rather than estimates — Q2 (escape
// rate), Q4 (ns/entity for re-clustering), Q6 (the budget → tightness → selectivity curve) and Q7 (migration rate
// under real motion, and how much hysteresis absorbs).
//
// Everything reported is either a wall-clock measurement taken here or a counter the engine already publishes on
// SpatialMigrationTelemetry. Nothing is derived from a model.
//
// Run:  bin/Release/net10.0/Typhon.Benchmark.exe --profile-partition [--matrix A|B|C|D|all] [--ticks N] [--csv path]
// ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════

[Component("Typhon.Benchmark.SpMat.Pos2", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct SpMatPos2
{
    [Field]
    [SpatialIndex]
    public AABB2F Bounds;

    /// <summary>Indexed and non-unique — a realistic archetype pays index staging on every migration, and omitting it
    /// would measure a shape nothing ships.</summary>
    [Field]
    [Index(AllowMultiple = true)]
    public int Tag;
}

[Component("Typhon.Benchmark.SpMat.Pos3", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct SpMatPos3
{
    [Field]
    [SpatialIndex]
    public AABB3F Bounds;

    [Field]
    [Index(AllowMultiple = true)]
    public int Tag;
}

[Archetype]
partial class SpMatUnit2 : Archetype<SpMatUnit2>
{
    public static readonly Comp<SpMatPos2> Pos = Register<SpMatPos2>();
}

[Archetype]
partial class SpMatUnit3 : Archetype<SpMatUnit3>
{
    public static readonly Comp<SpMatPos3> Pos = Register<SpMatPos3>();
}

/// <summary>How entities move between ticks. The names are behavioural, not cosmetic — each stresses a different part.</summary>
internal enum MotionModel
{
    /// <summary>Nothing moves. The floor: what a tick costs when the partition has no work at all.</summary>
    Static,

    /// <summary>Small jitter well inside a cell. No crossings; maximum INTRA-cell drift, which is what relocation and repair exist for.</summary>
    Drift,

    /// <summary>Steady motion crossing a cell every few ticks — the ordinary case the hysteresis margin is sized for.</summary>
    Cruise,

    /// <summary>A tenth of the population teleports to a random point each tick. The migration-storm shape.</summary>
    Warp,

    /// <summary>Circular motion about the world centre. Smooth, coherent, and every entity crosses cells at a predictable rate.</summary>
    Orbit,

    /// <summary>Groups whose centroids random-walk and whose members follow. Locality is PRESERVED while cells are crossed.</summary>
    Swarm,
}

/// <summary>Where entities are at spawn.</summary>
internal enum Distribution
{
    /// <summary>Even over the world. Every cell holds roughly the same count.</summary>
    Uniform,

    /// <summary>90 % of the population inside 5 % of the world. Cell occupancy is wildly uneven, which is what the repair queue ranks over.</summary>
    Clustered,
}

/// <summary>One row of the matrix: everything measured for one (shape, motion, distribution, population, config) point.</summary>
internal sealed class RunResult
{
    public string Matrix;
    public int Dim;
    public MotionModel Motion;
    public Distribution Dist;
    public int Entities;
    public float CellSize;
    public float BudgetMs;
    public int Ticks;

    // Build
    public double SpawnMs;
    public double FirstFenceMs;

    /// <summary>
    /// <c>RuntimeOptions.WorkerCount</c> the run was driven at — the variable this whole harness exists to sweep.
    /// </summary>
    /// <remarks>
    /// <b>The first version of this harness called <c>dbe.WriteTickFence()</c> directly and had no such field.</b> That entry point runs
    /// <c>WriteTickFenceCore</c>, a serial <c>foreach</c> over component tables; the parallel fence is a DAG dispatched by <c>TyphonRuntime</c> through
    /// <c>FenceWorkPlan</c>, and nothing but the runtime reaches it. Every migration and fence number that harness produced was therefore a
    /// single-threaded measurement of a subsystem whose design is explicitly "parallel across cells, divided by W" — compared, in the write-up, against
    /// per-worker projections. The numbers were not conservative, they were answering a different question.
    /// </remarks>
    public int Workers;

    /// <summary>Whether positions were written through the spatial barrier rather than <c>OpenMut</c>.</summary>
    public bool Barrier;

    /// <summary>
    /// Which branch of Prep the run actually took: 1 = clean-bitmap (barrier), 2 = dirty-bitmap (OpenMut), 0 = no work.
    /// </summary>
    /// <remarks>
    /// Recorded rather than inferred from the flag, because declaring <c>SetSpatialBarrierOnly</c> and then writing
    /// through <c>OpenMut</c> silently takes the dirty branch anyway — a contract break the telemetry tests warn about.
    /// A run that means to measure path 1 and reports 2 has measured the wrong thing, which is exactly what happened
    /// before this column existed.
    /// </remarks>
    public int FenceBranchPath;

    /// <summary>Fraction of the population that moved per tick — 1.0 is the stress case, not the expected one.</summary>
    public float MovingFraction = 1f;

    /// <summary>Aging warm-up applied before the measured ticks (#886 lead B); zero for a fresh world.</summary>
    public float ChurnFraction;
    public int ChurnTicks;

    /// <summary>
    /// Adjacent inversions in <c>ActiveClusterIds</c> at the end of the run — how far from ascending the list is. Zero on a fresh world; ~half the list on
    /// an aged one. Re-sorting it was measured as SLOWER and reverted (see <c>ArchetypeClusterState.RemoveFromActiveList</c>), so this column is the
    /// evidence that the aging worked, not a number anything tries to drive to zero.
    /// </summary>
    public long ActiveListInversions;

    // Steady-state tick cost. FenceSpanMs is WALL time — what a frame budget is actually spent against. The Cpu columns sum per-chunk time across workers,
    // which is what the design's per-entity costs are quoted in and what the throttle's budget charges. Reporting one as the other is how a 12-thread box
    // reads as a 12x regression, or a serial run as a pass.
    public double TickMsMean;
    public double TickMsP50;
    public double TickMsP99;
    public double TickMsMax;

    // Per-phase, from TyphonRuntime's own instrumentation rather than a stopwatch wrapped around something adjacent. Span = wall, Cpu = summed across
    // chunks. The six together are the whole fence, which is what makes the residual attributable.
    public double PrepSpanUs, PrepCpuUs;
    public double MigrateSpanUs, MigrateCpuUs;
    public double IndexSpanUs, IndexCpuUs;
    public double EntityMapSpanUs, EntityMapCpuUs;
    public double AabbSpanUs, AabbCpuUs;
    public double FinalizeSpanUs, FinalizeCpuUs;
    public double FenceSpanUs, FenceCpuUs;
    public long FenceChunks;
    public double MoveMs;

    /// <summary>Prep's Prepare start to the last chunk end of the last phase: the six spans plus the scheduler's gaps between them (µs/tick).</summary>
    public double FenceWallUs;

    /// <summary>The serial steps inside the phases, µs/tick: what no worker count shrinks. See <c>TyphonRuntime.LastFenceSerialTicks</c>.</summary>
    public double MigrateTailUs, IndexMergeUs, EntityMapMergeUs, FinalizeEmitUs;

    /// <summary>The WAL-append part of <see cref="FinalizeEmitUs"/>, and the commit-buffer swaps the tick caused.</summary>
    public double FinalizeAppendUs, WalSwapsPerTick;

    /// <summary>The Migrate workers' per-chunk sorts, µs of CPU per tick summed over the chunks: index runs, EntityMap runs, dirty-delta grouping.</summary>
    public double IndexSortCpuUs, MapSortCpuUs, DirtySortCpuUs;

    /// <summary>Per-phase chunks the planner dispatched and items it packed into them, per tick — the parallel width each phase actually got.</summary>
    public double PrepChunks, PrepItems, MigrateChunks, MigrateItems, IndexChunks, IndexItems, AabbChunks, AabbItems, FinalizeChunks, FinalizeItems;

    // What the fence actually did, per tick
    public double MigrationsPerTick;
    public double MigrationExecuteMs;
    public double MigrationTotalMs;
    public double DriftersPerTick;
    public double DriftAbsorbedPerTick;
    public double HysteresisAbsorbedPerTick;
    public double ThrottledPerTick;

    /// <summary>Relocations dropped because a crossing already claimed the entity (#877) — a normal, healthy number on a moving world, not a defect signal.</summary>
    public double SupersededPerTick;

    /// <summary>Prep's internal split, ms/tick, in phase order — the measurement that gates the design's ranking of which step to optimise.</summary>
    public double PrepSnapshotMs, PrepMaskMs, PrepShadowMs, PrepZoneMapMs, PrepDetectMs, PrepThrottleMs, PrepPlanMs, PrepSortMs, PrepPreSizeMs;

    /// <summary>Clusters surviving the occupancy mask — the domain a sliced Prep would partition.</summary>
    public double PrepDirtyClustersPerTick;
    public double UnplacedPerTick;
    public double ClustersScannedPerTick;

    /// <summary>
    /// Entity slots the AABB refresh actually read, per tick — the refresh's cost in the unit that scales with the world.
    /// </summary>
    /// <remarks>
    /// <c>ClustersScannedPerTick</c> counts clusters the pass decided had something to say, which is measured AFTER the skip; it cannot tell a pass that
    /// opened ten clusters from one that opened two thousand. This one can, and it is the column that shows whether the fence is doing work proportional to
    /// what MOVED or to what EXISTS.
    /// </remarks>
    public double SlotsScannedPerTick;

    // Repair
    public double RepairEntitiesPerTick;
    public double RepairUnitsPerTick;
    public double RepairRefusedPerTick;

    // Step-14 detection counters — see claude/research/Spatial/intra-cell-maintenance-2026-09/03-detection-economics.md §5
    public double DriftGatedPerTick;
    public double DriftSuppressedByDensityPerTick;
    public double DriftersSpilledPerTick;

    /// <summary>Tightness right after the spawn and its first fence, before any motion: what placement alone produced (step 15).</summary>
    public double TightnessAtSpawnPct;
    public double TightnessAtSpawnP90Pct;

    /// <summary>The occupancy price of tightness, in the same row: clusters per live cell, and live entities as a percentage of the slots those clusters hold.</summary>
    public double ClustersPerCell;
    public double SlotOccupancyPct;
    public double ClustersPerCellAtSpawn;
    public double SlotOccupancyAtSpawnPct;
    public double UnplacedNoCandidatePerTick;
    public double PinsRejectedPerTick;

    public string Arm = "";

    public string Point = "";

    public int Round;
    public double RelocationsAdmittedPerTick;
    public double CrossingsQueuedPerTick;
    public double RelocationSpendMs;
    public double RepairStarvedMs;
    public double BudgetUsedMs;
    public double MeasuredNsPerEntity;
    public double QueueDepth;
    public long QueueEvicted;
    public double QueueMaintenanceMs;
    public long ValveFires;

    // Partition shape
    public int ActiveClusters;
    public int LiveCells;
    public double EntitiesPerCluster;
    public double EntitiesPerCell;
    public double TightnessPct;      // mean cluster max-axis extent as % of cell size — the selectivity proxy
    public double TightnessP90Pct;

    // Query cost (ns per query) and yield (hits per query)
    public double AabbSmallNs, AabbSmallHits;
    public double AabbMediumNs, AabbMediumHits;
    public double AabbLargeNs, AabbLargeHits;
    public double RadiusNs, RadiusHits;
    public double RayNs, RayHits;
    public double FrustumNs, FrustumHits;
    public double BruteForceNs;      // same medium AABB, scanned entity by entity through the public API

    /// <summary>Empty when the run completed; otherwise the exception that ended it. Recorded rather than thrown — see <c>RunOne</c>.</summary>
    public string Failure = "";

    public string Key => $"{Dim}D/{Motion}/{Dist}/{Entities}";
}

public static class SpatialPartitionMatrix
{
    /// <summary>
    /// World side, in world units. <b>Fixed across every run on purpose</b> — query regions are expressed as fractions
    /// of it, so holding it constant is what makes a query time from one run comparable to another's.
    /// </summary>
    private const float WorldExtent = 1_000f;

    /// <summary>
    /// Worker count every matrix but W runs at.
    /// </summary>
    /// <remarks>
    /// <b>Fixed rather than auto-detected</b> (<c>WorkerCount = -1</c> resolves to <c>ProcessorCount - 4</c>, which is 28 on this box) so that rows from
    /// different runs and different machines describe the same configuration. Eight is one CCD's worth of physical cores on the Zen 4 part this is
    /// calibrated on, which is where the competitive benchmarks already cap: crossing the CCD boundary changes the memory system under the measurement,
    /// and a partitioning study should not have that confounded into it. Matrix W is where the worker count is the variable.
    /// </remarks>
    private const int DefaultWorkers = 8;

    /// <summary>
    /// Write positions through <c>ClusterRef.WriteSpatial</c> (the spatial barrier) instead of <c>Transaction.OpenMut</c>.
    /// </summary>
    /// <remarks>
    /// Selects which branch of Prep runs, and therefore which body of work is being measured — see the note beside
    /// <c>SetSpatialBarrierOnly</c> in <c>RunOneTyped</c>. Every first-party workload declares barrier-only, so
    /// <c>--barrier</c> is the configuration that corresponds to shipped code and the default is the one that does not.
    /// </remarks>
    private static bool Barrier;
    internal static bool FirstFit;

    internal static bool LeastEnlargement;
    internal static bool GrowthCap;
    internal static int OpenCap = 4;
    internal static bool NoSpawnSort;

    /// <summary>
    /// Fraction of the population that moves on any given tick. <c>1.0</c> (the default) means every entity moves every tick.
    /// </summary>
    /// <remarks>
    /// <b>1.0 is a stress case, not a workload.</b> Five of the six motion models moved every entity by construction, and the writer wrote
    /// every entity whenever any had moved — so every cluster was dirty on every tick and the campaign reported "97-99 % of clusters dirty" as though
    /// it were a property of moving worlds. A simulation in which a quarter of the entities move in a tick is already busy; most are far below that.
    /// Since Prep's cost is what this harness exists to characterise, and Prep's per-cluster work is gated on dirtiness, the moving fraction is a
    /// first-class variable rather than a constant nobody chose.
    /// </remarks>
    private static float MovingFraction = 1f;
    private static bool AdaptiveCost;
    private static bool ForceBulkMap;
    private static bool TraceTightness;

    // The engine's defaults, overridable for #918 AC-17.3: the drift gate sits at slack × bound, so a mechanism driven by it cannot reach a tightness-to-bound
    // below the slack, and the repair floor decides which band repair owns instead.
    private static float PackingSlack = 1.5f;
    private static float RepairRatio = 0.75f;
    private static float RepairCriticalRatio = 1.0f;

    // ── Aging (#886 lead B) ──────────────────────────────────────────────────────────────────────────────────────────
    //
    // A freshly spawned world has its active cluster list in ascending chunk-id order by construction, and stays that way for the 40 ticks a matrix point
    // runs. A server does not: every cluster free is a swap-with-last on that list, and after ~N·ln N frees it is statistically random — which is the
    // state in which the AabbRefresh walk misses its page window three times more often and its workers' slices stop being disjoint page ranges. No
    // motion model reaches that state, because motion never empties a cluster. Churn does: --churn <fraction> --churn-ticks <n> runs n warm-up ticks
    // before the measured ones, alternating "destroy every entity in enough random cells to cover <fraction> of the population" with "respawn them at
    // the positions they had". Population, cells and density are preserved; the cluster ids' order in the active list and the slot packing inside the
    // cells are what change. Warm-up ticks are excluded from every reported number.
    //
    // What it showed (#886 lead B): the scrambled list is NOT slower. Restoring ascending order was measured against leaving it alone, ten
    // interleaved pairs across two variants, and lost every pair. The engine-side sort was reverted; the aging stays because the fresh world it replaces
    // is the wrong world to measure a server against, and because an aged run at 25 % moving is 30-35 % FASTER than a fresh one — same migrations, same
    // tightness — which nobody has explained yet.
    private static float ChurnFraction;
    private static int ChurnTicks;

    /// <summary>
    /// Cell size that puts <paramref name="entitiesPerCell"/> entities in the average cell for this population.
    /// </summary>
    /// <remarks>
    /// <para><b>Density is the variable that matters, and it is not the one anybody sets.</b> A first cut of this
    /// harness fixed the world at 10 000 units and the cell at 250, which at 16 000 entities gives 64 000 cells and
    /// <b>1.13 entities per cluster</b> — every cluster a singleton, whose AABB is the entity. Nothing can be
    /// re-clustered, so the budget sweep measured seven identical partitions and reported that the budget buys nothing.
    /// It buys nothing THERE, which is a fact about the geometry, not about the mechanism.</para>
    /// <para>So the world stays fixed and the CELL is derived: cells = population / entitiesPerCell, and the side is
    /// that count's square (2D) or cube (3D) root. Occupancy is then the controlled variable across every population,
    /// and Matrix C sweeps it deliberately.</para>
    /// </remarks>
    private static float DeriveCellSize(int dim, int entities, int entitiesPerCell)
    {
        var cells = Math.Max(1d, (double)entities / entitiesPerCell);
        var side = dim == 3 ? Math.Cbrt(cells) : Math.Sqrt(cells);
        return (float)(WorldExtent / Math.Max(1d, side));
    }

    public static void Run(string[] args)
    {
        var which = ArgString(args, "--matrix", "all").ToUpperInvariant();
        var ticks = ArgInt(args, "--ticks", 60);
        var csvPath = ArgString(args, "--csv", "spatial-partition-matrix.csv");
        Barrier = Array.IndexOf(args, "--barrier") >= 0;
        MovingFraction = ArgFloat(args, "--moving", 1f);
        ChurnFraction = ArgFloat(args, "--churn", 0f);
        ChurnTicks = ArgInt(args, "--churn-ticks", 0);
        // A/B switch for #886 lead D: same binary, the sliced Prep on (default) or the one-item-per-archetype path it replaces.
        if (Array.IndexOf(args, "--no-prep-slice") >= 0)
        {
            DatabaseEngine.PrepSliceMinClusters = int.MaxValue;
        }

        FenceWorkPlan.PrepSliceWords = ArgInt(args, "--prep-slice-words", FenceWorkPlan.PrepSliceWords);

        // A/B switches for #889: the worker-aware chunk count (default) against the 200 µs-per-chunk rule it replaces, the smallest chunk it will
        // dispatch, and the live cost model in place of the pinned seeds (see the RuntimeOptions remark below for why pinned is the default here).
        FenceWorkPlan.WorkerAwareChunking = Array.IndexOf(args, "--legacy-chunking") < 0;
        FenceWorkPlan.MinUsefulChunkUs = Math.Min(FenceWorkPlan.MinChunkCostUs, ArgFloat(args, "--chunk-floor-us", FenceWorkPlan.MinUsefulChunkUs));
        AdaptiveCost = Array.IndexOf(args, "--adaptive-cost") >= 0;
        // Step 15 placement switches (A/B against the cursor first fit they replace): --first-fit turns least enlargement off, --growth-cap opens a
        // fresh cluster when an arrival would stretch its cluster past the population-aware cap, --open-cap N bounds the open clusters per cell,
        // --no-spawn-sort disables the per-cell Morton ordering of batch spawns.
        // Mirrors the SHIPPED defaults since step 15: least enlargement is OFF (measured a wash where repair runs), so the harness has to opt IN to
        // it rather than out. Before this the matrix measured a configuration the engine does not ship, and --first-fit was a no-op against the default.
        LeastEnlargement = Array.IndexOf(args, "--least-enlargement") >= 0;
        FirstFit = !LeastEnlargement;
        GrowthCap = Array.IndexOf(args, "--growth-cap") >= 0;
        OpenCap = ArgInt(args, "--open-cap", 4);
        NoSpawnSort = Array.IndexOf(args, "--no-spawn-sort") >= 0;
        // --trace-tightness prints one row per fence from the spawn's own onward: AC-17.3 is a trajectory ("within 20 ticks", "never rises"), which a
        // run's mean cannot show.
        TraceTightness = Array.IndexOf(args, "--trace-tightness") >= 0;
        PackingSlack = ArgFloat(args, "--packing-slack", PackingSlack);
        RepairRatio = ArgFloat(args, "--repair-ratio", RepairRatio);
        // The validator wants the critical ratio at 0 or between the repair ratio and 1.2; a repair ratio raised past the default critical switches it off.
        RepairCriticalRatio = RepairRatio >= RepairCriticalRatio ? 0f : RepairCriticalRatio;
        if (Array.IndexOf(args, "--repair-ratio") >= 0 || Array.IndexOf(args, "--packing-slack") >= 0)
        {
            // Two variables move when the repair ratio passes the critical one, so the second is printed rather than left to be inferred.
            Console.WriteLine($"  packing slack {PackingSlack:F2}, repair ratio {RepairRatio:F2}, critical valve "
                + (RepairCriticalRatio > 0f ? $"at {RepairCriticalRatio:F2}" : "OFF (switched off because the repair ratio reached it)"));
        }
        // --force-bulk-map stages every EntityMap patch for the bulk phase whatever the batch size, so its per-chunk sort is exercised on a world whose
        // migrations would otherwise take the inline path. (The --compare-sort / --compare-new-sorts A/B flags of #889 and #891 went with the comparison
        // sorts they selected, once the numbers in design 14 §5 were accepted.)
        ForceBulkMap = Array.IndexOf(args, "--force-bulk-map") >= 0;
        if (Array.IndexOf(args, "--no-finalize-slice") >= 0)
        {
            DatabaseEngine.FinalizeSliceMinRanges = int.MaxValue;
        }
        if (DatabaseEngine.PrepSliceMinClusters != int.MaxValue)
        {
            // The minimum is "two slices' worth" and is computed from the width once at static init; a swept width must carry it along.
            DatabaseEngine.PrepSliceMinClusters = 2 * FenceWorkPlan.PrepSliceWords;
        }

        var results = new List<RunResult>();
        var sw = Stopwatch.StartNew();

        Console.WriteLine("#872 spatial partitioning matrix");
        Console.WriteLine($"  matrix={which}  ticks/run={ticks}  csv={csvPath}");
        if (ChurnTicks > 0)
        {
            Console.WriteLine($"  aging: churn={ChurnFraction:P0} x {ChurnTicks} warm-up ticks");
        }
        Console.WriteLine($"  cores={Environment.ProcessorCount}  config={(IsDebug() ? "DEBUG (numbers are not comparable to Release)" : "RELEASE")}");
        Console.WriteLine();

        // One discarded run before anything is recorded. Tiered compilation, the allocator and the page cache are all
        // process-wide, so whichever matrix point happens to run first otherwise absorbs the warm-up and reports it as
        // its own cost. Both dispatch paths, for the reason RunMatrixW records.
        Console.WriteLine("  (warm-up, discarded)");
        foreach (var warm in new[] { 1, 8 })
        {
            RunOne("warmup", 3, MotionModel.Cruise, Distribution.Uniform, 16_000, DeriveCellSize(3, 16_000, 64), budgetMs: 1.0f, ticks: 12, workers: warm);
        }

        if (which is "A" or "ALL")
        {
            RunMatrixA(results, ticks);
        }
        if (which is "B" or "ALL")
        {
            RunMatrixB(results, ticks);
        }
        if (which is "C" or "ALL")
        {
            RunMatrixC(results, ticks);
        }
        if (which is "D" or "ALL")
        {
            RunMatrixD(results, ticks);
        }
        if (which is "M" or "ALL")
        {
            RunMatrixM(results, ticks);
        }
        if (which is "W" or "ALL")
        {
            RunMatrixW(results, ticks);
        }
        if (which is "P")
        {
            RunMatrixP(results, ticks, args);
        }
        if (which is "S")
        {
            RunSweep(results, ticks, args);
        }

        sw.Stop();
        WriteCsv(results, csvPath);
        Console.WriteLine();
        Console.WriteLine($"{results.Count} run(s) in {sw.Elapsed.TotalSeconds:F1}s -> {Path.GetFullPath(csvPath)}");
    }

    // ── Matrix A: the main cross product ────────────────────────────────────────────────────────────────────────
    //
    // Every combination of shape, motion and distribution at three populations. This is the matrix that says which
    // axis each cost tracks; the others hold everything but one variable fixed to trace a curve.

    private static void RunMatrixA(List<RunResult> results, int ticks)
    {
        Console.WriteLine("── Matrix A — shape × motion × distribution × population ──────────────────────────────────");
        int[] populations = [2_000, 8_000, 32_000];
        MotionModel[] motions = [MotionModel.Static, MotionModel.Drift, MotionModel.Cruise, MotionModel.Warp, MotionModel.Orbit, MotionModel.Swarm];
        Distribution[] dists = [Distribution.Uniform, Distribution.Clustered];

        foreach (var dim in new[] { 2, 3 })
        {
            foreach (var motion in motions)
            {
                foreach (var dist in dists)
                {
                    foreach (var n in populations)
                    {
                        // 64 per cell — one full cluster's worth, so a cell holds a handful of clusters and the
                        // partition has something to be good or bad at.
                        var r = RunOne("A", dim, motion, dist, n, DeriveCellSize(dim, n, 64), budgetMs: 1.0f, ticks: ticks);
                        results.Add(r);
                        Report(r);
                    }
                }
            }
        }
        Console.WriteLine();
    }

    // ── Matrix B: the budget sweep — design question Q6 ─────────────────────────────────────────────────────────
    //
    // Everything fixed but ReclusterBudgetMs. Q6 asks how much selectivity a given frame budget actually buys; the
    // exchange rate is (tightness gained) per (ms spent), and it is the number the whole architecture trades on.

    private static void RunMatrixB(List<RunResult> results, int ticks)
    {
        Console.WriteLine("── Matrix B — budget sweep (Q6: budget → tightness → query cost) ──────────────────────────");
        float[] budgets = [0f, 0.25f, 0.5f, 1f, 2f, 4f, 8f, 16f];

        // 512 per cell — eight clusters deep, so a cell CAN degrade and a repair unit has something to re-pack. At the
        // 64-per-cell default of Matrix A most clusters are close to singletons and the budget has nothing to spend on.
        var cell = DeriveCellSize(3, 16_000, 512);
        foreach (var b in budgets)
        {
            var r = RunOne("B", 3, MotionModel.Drift, Distribution.Uniform, 16_000, cell, budgetMs: b, ticks: ticks);
            results.Add(r);
            Report(r);
        }
        Console.WriteLine();
    }

    // ── Matrix C: density ───────────────────────────────────────────────────────────────────────────────────────
    //
    // Population and world held fixed, cell size swept so that occupancy runs from 4 to 4 096 entities per cell. This
    // is the axis the whole design turns on: ADR-046 rejected a per-cell tree on the strength of ~16 entities per cell,
    // and section 4.1 says the premise inverts at 100 K. A cell-size sweep and a density sweep are the same experiment;
    // naming it density says which quantity the reader should watch.

    private static void RunMatrixC(List<RunResult> results, int ticks)
    {
        Console.WriteLine("── Matrix C — density sweep (entities per cell) ───────────────────────────────────────────");
        int[] perCell = [4, 16, 64, 256, 1_024, 4_096];
        foreach (var d in perCell)
        {
            var r = RunOne("C", 3, MotionModel.Cruise, Distribution.Uniform, 16_000, DeriveCellSize(3, 16_000, d), budgetMs: 1.0f, ticks: ticks);
            results.Add(r);
            Report(r);
        }
        Console.WriteLine();
    }

    // ── Matrix D: population scaling ────────────────────────────────────────────────────────────────────────────
    //
    // One motion model, one distribution, population across a decade and a half. Whether tick cost and query cost
    // scale linearly, sub-linearly or worse is the question the grid exists to answer.

    private static void RunMatrixD(List<RunResult> results, int ticks)
    {
        Console.WriteLine("── Matrix D — population scaling ──────────────────────────────────────────────────────────");
        int[] populations = [1_000, 2_000, 4_000, 8_000, 16_000, 32_000, 64_000, 128_000];

        // Density held at 64 per cell across the sweep, so the grid RESOLUTION scales with the population and what is
        // being measured is the mechanism's scaling rather than the world getting emptier.
        foreach (var n in populations)
        {
            var r = RunOne("D", 3, MotionModel.Cruise, Distribution.Uniform, n, DeriveCellSize(3, n, 64), budgetMs: 1.0f, ticks: ticks);
            results.Add(r);
            Report(r);
        }
        Console.WriteLine();
    }

    // ── Matrix M: how busy is the world? ────────────────────────────────────────────────────────────────────────
    //
    // The moving fraction is the variable nobody chose and everything depends on. Prep's per-cluster work is gated on
    // a cluster being dirty, and a cluster is dirty if ANY of its ~31 entities moved — so the population that moves
    // and the clusters that go dirty are related by an amplification, not a ratio. This matrix measures that curve
    // rather than assuming a point on it.

    private static void RunMatrixM(List<RunResult> results, int ticks)
    {
        Console.WriteLine("── Matrix M — moving fraction (how much of the world moves per tick) ──────────────────────");
        float[] fractions = [1.0f, 0.50f, 0.25f, 0.10f, 0.05f, 0.01f];
        var saved = MovingFraction;
        var cell = DeriveCellSize(3, 64_000, 64);
        foreach (var f in fractions)
        {
            MovingFraction = f;
            var r = RunOne("M", 3, MotionModel.Cruise, Distribution.Uniform, 64_000, cell, budgetMs: 1.0f, ticks: ticks);
            r.MovingFraction = f;
            results.Add(r);
            Report(r);
        }

        MovingFraction = saved;
        Console.WriteLine();
    }

    // ── Matrix P: ONE point, every knob on the command line ───────────────────────────────────
    //
    // The sweeps above each run six to sixteen points in one process, which is what makes their rows comparable and
    // what makes them useless to a PROFILER: a tracing session over Matrix M reports one set of call counts covering
    // every moving fraction at once, and the mixture cannot be unpicked afterwards. Matrix P exists so one scenario can
    // be run alone, under a profiler, with the shape stated on the command line rather than compiled in.
    //
    //   --moving F  --workers W  --entities N  --percell K  --budget MS  --dim 2|3  --motion Cruise  --dist Uniform
    //
    // The warm-up in Run() still happens, so the profile does include a discarded 16 000-entity run. That is deliberate:
    // without it the measured point absorbs tiered compilation, and a profile of the JIT is worse than a profile that
    // contains a labelled warm-up.

    private static void RunMatrixP(List<RunResult> results, int ticks, string[] args)
    {
        var dim = ArgInt(args, "--dim", 3);
        var entities = ArgInt(args, "--entities", 64_000);
        var perCell = ArgInt(args, "--percell", 64);
        var workers = ArgInt(args, "--workers", DefaultWorkers);
        var budget = ArgFloat(args, "--budget", 1.0f);
        var motion = Enum.Parse<MotionModel>(ArgString(args, "--motion", "Cruise"), ignoreCase: true);
        var dist = Enum.Parse<Distribution>(ArgString(args, "--dist", "Uniform"), ignoreCase: true);
        var cell = DeriveCellSize(dim, entities, perCell);

        Console.WriteLine("── Matrix P — one point ────────────────────────────────────────────────");
        Console.WriteLine($"  dim={dim} motion={motion} dist={dist} n={entities} perCell={perCell} cell={cell:F2} " +
            $"moving={MovingFraction:P0} W={workers} budget={budget:F2} barrier={Barrier} churn={ChurnFraction:P0}x{ChurnTicks}");

        var r = RunOne("P", dim, motion, dist, entities, cell, budget, ticks, workers);
        r.MovingFraction = MovingFraction;
        results.Add(r);
        Report(r);
        Console.WriteLine();
    }

    // ── Matrix S: the sweep — every arm and every point in ONE process ─────────────────────────────────────────
    //
    // The measurement harness was costing more than the work it measured. A four-point, three-round A/B was twelve
    // `dotnet run` invocations at ~35 s each, and the population and the tick loop account for well under a second of
    // that: the rest is process start, assembly load and tier-0 JIT of the whole fence, paid twelve times over. This
    // runs the cross product inside one process, so JIT is paid once and every arm after the first is measured warm.
    //
    // Arms are INTERLEAVED within a round rather than batched, because a batch-versus-batch comparison on a shared box
    // drifts with whatever else is running on it — the A/B discipline the repo already writes down.
    private static void RunSweep(List<RunResult> results, int ticks, string[] args)
    {
        var dim = ArgInt(args, "--dim", 3);
        var entities = ArgInt(args, "--entities", 16_000);
        var perCell = ArgInt(args, "--percell", 512);
        var workers = ArgInt(args, "--workers", DefaultWorkers);
        var rounds = ArgInt(args, "--rounds", 3);
        var dist = Enum.Parse<Distribution>(ArgString(args, "--dist", "Uniform"), ignoreCase: true);
        var cell = DeriveCellSize(dim, entities, perCell);
        var points = ArgString(args, "--points", "drift8,drift1,cruise8,swarm8").Split(',', StringSplitOptions.RemoveEmptyEntries);
        var arms = ArgString(args, "--arms", "base").Split(',', StringSplitOptions.RemoveEmptyEntries);

        Console.WriteLine("── Matrix S — sweep, one process ───────────────────────────────────────");
        Console.WriteLine($"  dim={dim} n={entities} perCell={perCell} cell={cell:F2} W={workers} ticks={ticks} rounds={rounds}");
        Console.WriteLine($"  points=[{string.Join(" ", points)}] arms=[{string.Join(" ", arms)}]");

        var sw = Stopwatch.StartNew();
        for (var round = 0; round < rounds; round++)
        {
            foreach (var point in points)
            {
                var (motion, budget) = ParsePoint(point);
                foreach (var arm in arms)
                {
                    ApplyArm(arm);
                    var r = RunOne("S", dim, motion, dist, entities, cell, budget, ticks, workers);
                    r.Arm = arm;
                    r.Round = round;
                    r.Point = point;
                    results.Add(r);
                    Console.WriteLine($"  r{round} {point,-8} {arm,-10} fence {r.FenceSpanUs / 1000d,6:F2} ms  aabb {r.AabbSpanUs,7:F1} us  "
                        + $"tight {r.TightnessPct,5:F1}%  pinsRej {r.PinsRejectedPerTick,7:F1}/t  mig {r.MigrationsPerTick,7:F0}/t  "
                        + $"repairEnt {r.RepairEntitiesPerTick,7:F0}/t  clusters {r.ActiveClusters}");
                }
            }
        }

        ApplyArm("base");
        sw.Stop();
        Console.WriteLine($"  sweep of {results.Count} runs in {sw.Elapsed.TotalSeconds:F1}s");
        Console.WriteLine();
        SweepSummary(results, points, arms);
    }

    private static (MotionModel motion, float budget) ParsePoint(string point)
    {
        var name = point.Trim().ToLowerInvariant();
        var digits = name.Length > 0 && char.IsDigit(name[^1]) ? name[^1] - '0' : 1;
        var motion = name.StartsWith("drift", StringComparison.Ordinal) ? MotionModel.Drift
            : name.StartsWith("cruise", StringComparison.Ordinal) ? MotionModel.Cruise
            : name.StartsWith("swarm", StringComparison.Ordinal) ? MotionModel.Swarm
            : MotionModel.Static;
        return (motion, digits);
    }

    /// <summary>Set the engine switches an arm names. Every arm must set EVERY switch it cares about — arms run in sequence in one process.</summary>
    private static void ApplyArm(string arm)
    {
        switch (arm.Trim().ToLowerInvariant())
        {
            case "base":
                LeastEnlargement = false;
                FirstFit = true;
                break;
            case "le":
                LeastEnlargement = true;
                FirstFit = false;
                break;
            default:
                throw new ArgumentException($"unknown sweep arm '{arm}'");
        }
    }

    private static void SweepSummary(List<RunResult> results, string[] points, string[] arms)
    {
        Console.WriteLine("  medians ─────────────────────────────────────────────────────────────");
        Console.WriteLine($"  {"point",-9}{"arm",-9}{"fence ms",10}{"aabb us",10}{"tight %",9}{"pinsRej/t",11}{"mig/t",9}{"repEnt/t",10}{"clusters",9}");
        foreach (var point in points)
        {
            foreach (var arm in arms)
            {
                var rows = results.FindAll(r => r.Point == point && r.Arm == arm);
                if (rows.Count == 0)
                {
                    continue;
                }

                Console.WriteLine($"  {point,-9}{arm,-9}{Median(rows, r => r.FenceSpanUs / 1000d),10:F2}{Median(rows, r => r.AabbSpanUs),10:F1}"
                    + $"{Median(rows, r => r.TightnessPct),9:F1}{Median(rows, r => r.PinsRejectedPerTick),11:F1}"
                    + $"{Median(rows, r => r.MigrationsPerTick),9:F0}{Median(rows, r => r.RepairEntitiesPerTick),10:F0}"
                    + $"{Median(rows, r => r.ActiveClusters),9:F0}");
            }
        }
    }

    private static double Median(List<RunResult> rows, Func<RunResult, double> pick)
    {
        var v = new List<double>(rows.Count);
        foreach (var r in rows)
        {
            var d = pick(r);
            if (double.IsFinite(d))
            {
                v.Add(d);
            }
        }

        if (v.Count == 0)
        {
            return 0d;
        }

        v.Sort();
        return v.Count % 2 == 1 ? v[v.Count / 2] : 0.5 * (v[(v.Count / 2) - 1] + v[v.Count / 2]);
    }

    // ── Matrix W: worker scaling — the one the first campaign could not ask ─────────────────────────────────────
    //
    // The design budgets the fence as "parallel across cells... divided by W". The first version of this harness drove
    // dbe.WriteTickFence() directly, which is the SERIAL path, so every fence number it produced was W=1 measured
    // against per-worker projections. This matrix is the correction: same workload, same population, W swept, and both
    // the wall span and the summed CPU reported per phase so the two can be told apart.

    private static void RunMatrixW(List<RunResult> results, int ticks)
    {
        Console.WriteLine("── Matrix W — worker scaling (the fence is designed to divide by W; does it?) ─────────────");
        int[] workerCounts = [1, 2, 4, 8, 16];

        // 512 per cell, the density Matrix B uses: deep enough that a cell holds several clusters, so Prep, AabbRefresh
        // and the repair planner all have real work to slice. At 64 per cell most clusters are near-singletons and the
        // parallel phases have nothing to divide.
        var cell = DeriveCellSize(3, 64_000, 512);

        // Discarded. Tiered compilation is process-wide, so without it the FIRST worker count measured absorbs the
        // runtime warming up and reads slow — which, since the sweep runs W ascending, manufactures a speedup curve out
        // of JIT. Measured: W=1 read 29.0 ms as the first run of a process and 12.4 ms once warm, turning a real 1.55x
        // into a reported 3.85x. The same run is discarded by FenceIndexPhaseBench for the same reason.
        // BOTH paths, because they are different code: DagScheduler.DispatchTrack branches on `_workerCount == 1` into
        // RunTrackSingleThreaded, so warming only the multi-threaded path leaves the W=1 arm — the DENOMINATOR of every
        // speedup below — measured cold. With a W=4-only warm-up, W=1 still read 27.2 ms on one run of three against
        // 18.4 and 18.3 on the others.
        foreach (var warm in new[] { 1, 8 })
        {
            RunOne("W", 3, MotionModel.Cruise, Distribution.Uniform, 64_000, cell, budgetMs: 1.0f, ticks: Math.Min(ticks, 12), workers: warm);
        }

        foreach (var w in workerCounts)
        {
            var r = RunOne("W", 3, MotionModel.Cruise, Distribution.Uniform, 64_000, cell, budgetMs: 1.0f, ticks: ticks, workers: w);
            results.Add(r);
            Report(r);
        }
        Console.WriteLine();
    }

    // ── One run ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs one matrix point, reporting rather than propagating a failure.
    /// </summary>
    /// <remarks>
    /// <b>A failed run is DATA, not an excuse to stop.</b> The first full campaign died at run 25 of 96 on an
    /// <c>InvalidOperationException</c> from <c>OpenMut</c> ("not found or not visible") and lost the 71 runs behind it.
    /// The same scenario passes in isolation, so what it says is that something does not survive many engines in one
    /// process — which is worth knowing and is recorded per row rather than swallowed. The row is emitted with
    /// <c>Failure</c> set so it appears in the CSV as an outcome instead of as an absence.
    /// </remarks>
    private static RunResult RunOne(string matrix, int dim, MotionModel motion, Distribution dist, int entities, float cellSize, float budgetMs, int ticks,
        int workers = DefaultWorkers, bool barrier = false)
    {
        try
        {
            return dim == 2
                ? RunOneTyped<SpMatUnit2, SpMatPos2>(matrix, 2, motion, dist, entities, cellSize, budgetMs, ticks, workers, Barrier, SpMatUnit2.Pos, Write2, Read2, Tag2)
                : RunOneTyped<SpMatUnit3, SpMatPos3>(matrix, 3, motion, dist, entities, cellSize, budgetMs, ticks, workers, Barrier, SpMatUnit3.Pos, Write3, Read3, Tag3);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  !! {matrix} {dim}D {motion} {dist} n={entities} cell={cellSize:F1} budget={budgetMs:F2} FAILED: {ex.GetType().Name}: {ex.Message}");
            return new RunResult
            {
                Matrix = matrix, Dim = dim, Motion = motion, Dist = dist, Entities = entities,
                CellSize = cellSize, BudgetMs = budgetMs, Ticks = ticks,
                Failure = SanitizeForCsv($"{ex.GetType().Name}: {ex.Message}"),
            };
        }
    }

    private delegate void PosWriter<TPos>(ref TPos pos, float x, float y, float z, float half, int tag);

    private delegate (float x, float y, float z) PosReader<TPos>(in TPos pos);

    private static void Write2(ref SpMatPos2 p, float x, float y, float z, float half, int tag)
    {
        p.Bounds = new AABB2F { MinX = x - half, MinY = y - half, MaxX = x + half, MaxY = y + half };
        p.Tag = tag;
    }

    private static (float, float, float) Read2(in SpMatPos2 p) => (0.5f * (p.Bounds.MinX + p.Bounds.MaxX), 0.5f * (p.Bounds.MinY + p.Bounds.MaxY), 0f);

    private static int Tag2(in SpMatPos2 p) => p.Tag;

    private static int Tag3(in SpMatPos3 p) => p.Tag;

    private static void Write3(ref SpMatPos3 p, float x, float y, float z, float half, int tag)
    {
        p.Bounds = new AABB3F { MinX = x - half, MinY = y - half, MinZ = z - half, MaxX = x + half, MaxY = y + half, MaxZ = z + half };
        p.Tag = tag;
    }

    private static (float, float, float) Read3(in SpMatPos3 p)
        => (0.5f * (p.Bounds.MinX + p.Bounds.MaxX), 0.5f * (p.Bounds.MinY + p.Bounds.MaxY), 0.5f * (p.Bounds.MinZ + p.Bounds.MaxZ));

    /// <summary>Recovers the entity's index from the component it is being written through — the barrier path has no id in hand.</summary>
    private delegate int TagReader<TPos>(in TPos pos);

    private static RunResult RunOneTyped<TArch, TPos>(string matrix, int dim, MotionModel motion, Distribution dist, int entities, float cellSize,
        float budgetMs, int ticks, int workers, bool barrier, Comp<TPos> comp, PosWriter<TPos> write, PosReader<TPos> read, TagReader<TPos> tag)
        where TArch : Archetype<TArch>
        where TPos : unmanaged
    {
        var r = new RunResult
        {
            Matrix = matrix, Dim = dim, Motion = motion, Dist = dist, Entities = entities,
            CellSize = cellSize, BudgetMs = budgetMs, Ticks = ticks, Workers = workers, MovingFraction = MovingFraction,
            ChurnFraction = ChurnFraction, ChurnTicks = ChurnTicks,
        };

        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
          .AddResourceRegistry()
          .AddMemoryAllocator()
          .AddEpochManager()
          .AddHighResolutionSharedTimer()
          .AddDeadlineWatchdog()
          .AddScopedManagedPagedMemoryMappedFile(o =>
          {
              o.DatabaseName = $"SpMat_{Environment.ProcessId}";
              // 512 MiB. Not larger: the size reaches an int-typed allocation, and 2 GiB of pages overflows it into a
              // negative size that surfaces as "Size must be positive" three frames away from the cause.
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
            new Vector3(0, 0, 0),
            new Vector3(WorldExtent, WorldExtent, dim == 3 ? WorldExtent : cellSize),
            cellSize,
            reclusterBudgetMs: budgetMs,
            clusterTargetPackingSlack: PackingSlack,
            clusterRepairExtentRatio: RepairRatio,
            clusterRepairCriticalExtentRatio: RepairCriticalRatio,
            leastEnlargementPlacement: LeastEnlargement,
            growthCapPlacement: GrowthCap,
            maxOpenClustersPerCell: OpenCap,
            batchSpawnSortThreshold: NoSpawnSort ? 0 : 128));
        dbe.InitializeArchetypes();

        // The write path is a MEASUREMENT VARIABLE, not a detail — it selects which branch of Prep runs.
        //
        // Writing through Transaction.OpenMut sets ClusterDirtyBitmap and routes Prep down the dirty-bitmap branch,
        // which runs the shadow drain, the zone-map recompute and the dormancy sweep. Writing through
        // ClusterRef.WriteSpatial sets ClusterProcessBitmap and leaves the dirty bitmap clean, which routes Prep down
        // the CLEAN branch, where all three of those are skipped and the only population-scaled work left is the
        // spatialBits build.
        //
        // Barrier-only is opt-in by API and universal in first-party practice: AntHill declares it for all four of its
        // archetypes, SpaceBattle for all five, and the guide sample for its one. So the dirty path this harness
        // measured by default is the path no shipped workload uses, and comparing the two is the point.
        if (barrier)
        {
            dbe.SetSpatialBarrierOnly<TArch>();
        }

        var archetypeId = Archetype<TArch>.Metadata.ArchetypeId;
        var rng = new Random(20260904);

        // ── spawn ──
        var xs = new float[entities];
        var ys = new float[entities];
        var zs = new float[entities];
        var groupOf = new int[entities];
        const int groupCount = 64;
        SeedPositions(dist, motion, rng, dim, entities, xs, ys, zs, groupOf, groupCount);

        // An entity is 2 % of a cell across. Fixed as a FRACTION rather than in world units so the shape of the
        // problem — how many entities fit in a cell, how much of a cluster's box is the entities' own size — is the
        // same at every cell size the matrices sweep.
        var halfExtent = cellSize * 0.01f;

        var ids = new EntityId[entities];
        var sw = Stopwatch.StartNew();
        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = default(TPos);
            for (var i = 0; i < entities; i++)
            {
                write(ref pos, xs[i], ys[i], zs[i], halfExtent, i);
                ids[i] = tx.Spawn<TArch>(comp.Set(in pos));
            }
            tx.Commit();
        }
        sw.Stop();
        r.SpawnMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        dbe.WriteTickFence(1);
        sw.Stop();
        r.FirstFenceMs = sw.Elapsed.TotalMilliseconds;

        // Placement alone (step 15): the partition as the spawn and its first fence left it, before a single entity has moved.
        MeasurePartition(dbe, archetypeId, r);
        r.TightnessAtSpawnPct = r.TightnessPct;
        r.TightnessAtSpawnP90Pct = r.TightnessP90Pct;
        r.ClustersPerCellAtSpawn = r.ClustersPerCell;
        r.SlotOccupancyAtSpawnPct = r.SlotOccupancyPct;

        // ── steady state, under the REAL runtime ───────────────────────────────────────────
        //
        // Driven through TyphonRuntime and not dbe.WriteTickFence(), and the difference is the whole point of this
        // harness. WriteTickFenceCore is a serial `foreach` over component tables; the parallel fence is a DAG the
        // scheduler dispatches through FenceWorkPlan, and only the runtime reaches it. The design specifies this work as
        // "parallel across cells... divided by W in the window", so a serial measurement of it is not a conservative
        // version of the real number — it answers a different question.
        //
        // Motion runs as a system inside the tick, so the OpenMut writes are charged to the tick they belong to and the
        // fence sees them through the same path production does. Sampling runs FIRST and reads the PREVIOUS tick's phase
        // counters: the fence is the scheduler's tick-END callback, so no system can observe its own tick's fence.
        var acc = new Accumulator();
        var groupVx = new float[groupCount];
        var groupVy = new float[groupCount];
        var groupVz = new float[groupCount];
        for (var g = 0; g < groupCount; g++)
        {
            groupVx[g] = (float)((rng.NextDouble() - 0.5) * cellSize * 0.5);
            groupVy[g] = (float)((rng.NextDouble() - 0.5) * cellSize * 0.5);
            groupVz[g] = dim == 3 ? (float)((rng.NextDouble() - 0.5) * cellSize * 0.5) : 0f;
        }

        var movedFlags = new bool[entities];
        var phase = new PhaseAccumulator();
        long lastSwapGen = -1;   // -1 = no sample yet; 0 is a legitimate generation on a buffer that has never swapped
        var moveMsTotal = 0d;

        // -- The Move system is inside a scheduler callback, and a callback that throws is not a callback that reported an error ------------------------
        //
        // The 3D barrier arm threw NotSupportedException on its first WriteSpatial (ClusterRef.WriteSpatial supports AABB2F only), the scheduler absorbed
        // it, and the run completed and reported fence = 0.33 ms, mig/t = 0, tightness = 96.7 % -- identical at all six moving fractions, because nothing had
        // moved at any of them. The harness printed a motionless world as a RESULT. A comment two hundred lines below already said "A measurement harness must
        // never be able to report that outcome as a result"; the guard it describes did not cover the exception route.
        //
        // Two independent nets, because either alone can be defeated: the exception is captured and surfaced as the row's Failure, AND the writes are counted
        // so an arm that silently applies none is caught even when it throws nothing at all.
        Exception moveFailure = null;
        var spatialWritesApplied = 0L;
        var entitiesRequestedToMove = 0L;
        var observed = 0;
        var motionTick = 0;
        var toUs = 1_000_000d / Stopwatch.Frequency;

        // Aging state (#886 lead B). Cells are bucketed once, from the seeded positions, because motion is suspended during the warm-up.
        var churnTicks = ChurnTicks;
        var churnFraction = ChurnFraction;
        List<int>[] churnCells = null;
        int[] churnCellOf = null;
        var churnPending = new List<int>();
        // Its own seed, so the aged arm's motion draws the same random sequence as the fresh arm's.
        var churnRng = new Random(20260905);
        void ChurnTick(Transaction tx, int t)
        {
            if (churnCells == null)
            {
                var side = (int)Math.Ceiling(WorldExtent / cellSize) + 1;
                var index = new Dictionary<long, int>();
                var buckets = new List<List<int>>();
                churnCellOf = new int[entities];
                for (var i = 0; i < entities; i++)
                {
                    var cx = (int)(xs[i] / cellSize);
                    var cy = (int)(ys[i] / cellSize);
                    var cz = dim == 3 ? (int)(zs[i] / cellSize) : 0;
                    var key = cx + (long)side * (cy + (long)side * cz);
                    if (!index.TryGetValue(key, out var b))
                    {
                        b = buckets.Count;
                        index[key] = b;
                        buckets.Add(new List<int>());
                    }
                    buckets[b].Add(i);
                    churnCellOf[i] = b;
                }
                churnCells = buckets.ToArray();
            }

            // Destroy on even ticks, respawn on odd — and never end the warm-up on a destroy, or the first motion tick opens dead ids.
            if ((t & 1) == 0 && t != churnTicks - 1)
            {
                // Whole cells, so the clusters behind them actually empty and get freed at the fence — a per-entity destroy would only free slots that
                // the respawn refills, and the active list would never move.
                var target = (int)Math.Ceiling(churnFraction * entities);
                var destroyed = 0;
                var guard = 0;
                while (destroyed < target && guard++ < churnCells.Length * 8)
                {
                    var cell = churnCells[churnRng.Next(churnCells.Length)];
                    if (cell.Count == 0)
                    {
                        continue;
                    }
                    foreach (var i in cell)
                    {
                        tx.Destroy(ids[i]);
                        churnPending.Add(i);
                    }
                    destroyed += cell.Count;
                    cell.Clear();
                }
            }
            else
            {
                foreach (var i in churnPending)
                {
                    var pos = default(TPos);
                    write(ref pos, xs[i], ys[i], zs[i], halfExtent, i);
                    ids[i] = tx.Spawn<TArch>(comp.Set(in pos));
                    churnCells[churnCellOf[i]].Add(i);
                }
                churnPending.Clear();
            }
        }

        var tightnessTrace = TraceTightness && matrix != "warmup" ? new List<string>() : null;
        TyphonRuntime runtime = null;
        runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Partition");

            dag.CallbackSystem("Sample", _ =>
            {
                var n = Interlocked.Increment(ref observed);

                // Before the discard below, which is about phase timings: the trajectory is wanted from the spawn's own fence onward. Callback n runs
                // at the start of tick n and so sees fence n − 1 — the row is labelled with the fence it describes, and the first callback has none.
                if (n > 1)
                {
                    tightnessTrace?.Add(TraceTightnessRow(dbe, archetypeId, n - 1));
                }

                // The first two ticks are discarded from the PHASE numbers: tick 1 has no previous fence to report, and
                // tick 2 reports the first fence after spawn, which carries the page faults for every chunk it touches.
                if (n <= 2 + churnTicks)
                {
                    return;
                }

                var prep = runtime.LastPrepStats;
                var mig = runtime.LastMigrateStats;
                var idx = runtime.LastIndexMassUpdateStats;
                var map = runtime.LastEntityMapUpdateStats;
                var aabb = runtime.LastAabbRefreshStats;
                var fin = runtime.LastFinalizeStats;

                phase.Add(
                    prep.SpanTicks * toUs, prep.CpuTicks * toUs,
                    mig.SpanTicks * toUs, mig.CpuTicks * toUs,
                    idx.SpanTicks * toUs, idx.CpuTicks * toUs,
                    map.SpanTicks * toUs, map.CpuTicks * toUs,
                    aabb.SpanTicks * toUs, aabb.CpuTicks * toUs,
                    fin.SpanTicks * toUs, fin.CpuTicks * toUs,
                    prep.Chunks + mig.Chunks + idx.Chunks + map.Chunks + aabb.Chunks + fin.Chunks);

                var serial = runtime.LastFenceSerialTicks;
                var swapGen = dbe.WalManager?.CommitBuffer?.SwapGeneration ?? 0;
                var swaps = lastSwapGen < 0 ? 0 : swapGen - lastSwapGen;
                lastSwapGen = swapGen;
                phase.AddSerial(runtime.LastFenceWallTicks * toUs, serial.MigrateTail * toUs, serial.IndexMerge * toUs, serial.EntityMapMerge * toUs,
                    serial.FinalizeEmit * toUs, serial.FinalizeAppend * toUs, swaps);
                var chunkSort = runtime.LastFenceChunkSortTicks;
                phase.AddChunkSorts(chunkSort.IndexSort * toUs, chunkSort.MapSort * toUs, chunkSort.DirtySort * toUs);
                phase.AddWidths(runtime);

                acc.Add(dbe.GetSpatialTelemetry(archetypeId));
            });

            dag.CallbackSystem("Move", ctx =>
            {
                var t = motionTick++;
                if (t < churnTicks)
                {
                    try
                    {
                        ChurnTick(ctx.Transaction, t);
                    }
                    catch (Exception ex)
                    {
                        moveFailure ??= ex;
                    }
                    return;
                }
                var moveSw = Stopwatch.StartNew();
                // Rebased past the warm-up so an aged arm samples the motion model at the same phase as a fresh one.
                var moved = ApplyMotion(motion, rng, dim, entities, cellSize, xs, ys, zs, groupOf, groupVx, groupVy, groupVz, t - churnTicks, movedFlags);
                entitiesRequestedToMove += moved;
                try
                {
                if (moved > 0)
                {
                    var tx = ctx.Transaction;
                    if (barrier)
                    {
                        // Fails LOUDLY. ClusterRef.WriteSpatial supports AABB2F only (ClusterRef.cs:335-342), so a 3D archetype throws
                        // NotSupportedException on the first slot of every tick. The scheduler did not surface that: the run completed, reported
                        // drift = 0 / migrations = 0 / fence = 0.077 ms, and looked like a 100x speedup rather than a workload that never moved.
                        // A measurement harness must never be able to report that outcome as a result.

                        // The spatial write barrier: iterate clusters, recover each entity's index from the Tag field it
                        // was spawned with, and write the position the motion model computed for THAT entity. Preserving
                        // the tag mapping is what keeps the two arms comparable — a barrier arm that wrote arbitrary
                        // positions per slot would be a different workload, not the same workload on a different path.
                        // Disposed in a finally, as ClusterThrottleBudgetTests.MoveAll does. Without it the writes do not
                        // reach storage: the first version of this arm reported drift = 0 and migrations = 0 on a
                        // quarter-cell Cruise workload, i.e. it measured a world where nothing moved and called the
                        // result a 100x speedup.
                        var acc2 = tx.For<TArch>();
                        try
                        {
                            foreach (var cluster in acc2.GetClusterEnumerator())
                            {
                                var bits = cluster.OccupancyBits;
                                while (bits != 0)
                                {
                                    var slot = BitOperations.TrailingZeroCount(bits);
                                    bits &= bits - 1;

                                    var idx = tag(in cluster.GetReadOnly(comp, slot));
                                    if ((uint)idx >= (uint)entities || !movedFlags[idx])
                                    {
                                        continue;
                                    }

                                    var next = default(TPos);
                                    write(ref next, xs[idx], ys[idx], zs[idx], halfExtent, idx);
                                    cluster.WriteSpatial(comp, slot, in next);
                                    spatialWritesApplied++;
                                }
                            }
                        }
                        finally
                        {
                            acc2.Dispose();
                        }
                    }
                    else
                    {
                        // Only the entities the motion model actually moved. Writing the whole population regardless was what
                        // dirtied every cluster on every tick and made the campaign's dirty fraction an artefact of this loop.
                        for (var i = 0; i < entities; i++)
                        {
                            if (!movedFlags[i])
                            {
                                continue;
                            }

                            ref var p = ref tx.OpenMut(ids[i]).Write(comp);
                            write(ref p, xs[i], ys[i], zs[i], halfExtent, i);
                            spatialWritesApplied++;
                        }
                    }
                }
                }
                catch (Exception ex)
                {
                    // Recorded, not rethrown: throwing out of a scheduler callback is exactly how this became invisible. The row is failed below instead.
                    moveFailure ??= ex;
                }

                moveSw.Stop();
                moveMsTotal += moveSw.Elapsed.TotalMilliseconds;
            }, after: "Sample");
        }, new RuntimeOptions
        {
            WorkerCount = workers,

            // Deliberately below what a heavy fence needs, so the loop is never rate-limited into idling and ticks run
            // back to back. The scheduler falls behind rather than breaking; what is measured is each phase's own span,
            // not the tick period, so a missed deadline costs wall-clock and nothing else.
            BaseTickRate = 200,
            EnableParallelFence = true,

            // OFF by default, and that is a measurement decision worth stating. With the live cost model on, FenceWorkPlan
            // picks its chunk count from the PREVIOUS ticks' observed cost, so a W-sweep would partly measure the model
            // adapting rather than the work parallelising, and two runs of one point would not be comparable. Production
            // leaves it on; a scaling study cannot. `--adaptive-cost` turns it on for the runs that ask what production
            // sees: the pinned index seed (0.06 µs/entry) is 7x below what this world measures (0.43), and a planner fed
            // that number under-chunks the index phase in a way production never would (#889).
            AdaptiveFenceCost = AdaptiveCost,
            EntityMapBulkMinEntriesPerBucket = ForceBulkMap ? 0f : EntityMapUpdateStaging.DefaultMinEntriesPerBucket,
        });

        // Exceptions raised once teardown has begun are DISCARDED, and this is a workaround for an engine defect
        // rather than a convenience.
        //
        // DagScheduler.Dispose() disposes `_tickStartSignal` and only then calls base.Dispose(), which is what stops the
        // TIMER thread. Shutdown() joins the WORKERS but explicitly does not wait for the tick in flight — its own
        // remarks say so ("Neither this nor Shutdown is a quiescence point... Dispose the runtime when you need
        // nothing is running"). So a tick already running can reach DispatchDeferredTracks -> DispatchTrackMultiThreaded
        // -> _tickStartSignal.Reset() (DagScheduler.cs:2206) after that signal has been disposed, and throws
        // ObjectDisposedException from the tick thread. Observed intermittently: run 1 clean, run 2 dead, run 3 clean.
        //
        // Every sample this harness reports has already been collected by then — the failure is in teardown, after the
        // last tick was observed — so discarding it costs no data. It is NOT swallowed silently: an exception before
        // teardown still fails the row, and the defect is filed rather than absorbed here.
        var tearingDown = 0;
        Exception unhandled = null;
        runtime.Scheduler.UnhandledExceptionCallback = (_, _, ex) =>
        {
            if (Volatile.Read(ref tearingDown) == 0)
            {
                Interlocked.CompareExchange(ref unhandled, ex, null);
            }
        };

        using (runtime)
        {
            runtime.Start();

            // +2 for the two discarded warm-up ticks. The timeout is a backstop against a hung fence, not a budget: at
            // 128 000 entities a tick legitimately takes tens of milliseconds and the loop simply runs behind.
            if (!SpinWait.SpinUntil(() => Volatile.Read(ref observed) >= ticks + 2 + churnTicks, TimeSpan.FromSeconds(180)))
            {
                r.Failure = $"runtime reached only {Volatile.Read(ref observed)} of {ticks + 2 + churnTicks} ticks in 180 s";
            }

            Volatile.Write(ref tearingDown, 1);
            runtime.Shutdown();
        }

        if (unhandled != null)
        {
            r.Failure = SanitizeForCsv($"{unhandled.GetType().Name}: {unhandled.Message} @ {unhandled.StackTrace}");
            return r;
        }

        r.Workers = workers;
        r.Barrier = barrier;
        r.FenceBranchPath = ClusterStateBranchPath(dbe, archetypeId);
        r.ActiveListInversions = CountActiveListInversions(dbe, archetypeId);
        // Motion ticks only: the churn warm-up increments motionTick without accumulating move time.
        r.MoveMs = moveMsTotal / Math.Max(1, motionTick - churnTicks);
        if (tightnessTrace != null)
        {
            foreach (var row in tightnessTrace)
            {
                Console.WriteLine(row);
            }
        }

        // A row that moved nothing is not a fast row, it is a broken one. Both nets report through Failure, which WriteCsv and Report already surface.
        if (moveFailure != null)
        {
            r.Failure = SanitizeForCsv($"Move system threw and the scheduler absorbed it: {moveFailure.GetType().Name}: {moveFailure.Message}");
        }
        else if (entitiesRequestedToMove > 0 && spatialWritesApplied == 0)
        {
            r.Failure = SanitizeForCsv($"the motion model asked for {entitiesRequestedToMove} moves and the write path applied NONE - "
                + "this run measured a motionless world");
        }
        phase.Fill(r);
        acc.FillPerTick(r, Math.Max(1, phase.Samples));

        // The fence's WALL cost is the sum of its phase spans: the phases are sequenced by the DAG's After() edges, so
        // they never overlap each other even though each is internally parallel.
        r.TickMsMean = r.FenceSpanUs / 1000d;
        r.TickMsP50 = phase.FenceSpanP50Us / 1000d;
        r.TickMsP99 = phase.FenceSpanP99Us / 1000d;
        r.TickMsMax = phase.FenceSpanMaxUs / 1000d;

        // ── partition shape ──
        MeasurePartition(dbe, archetypeId, r);

        // ── queries ──
        MeasureQueries<TArch, TPos>(dbe, dim, cellSize, r, read, comp, ids);

        return r;
    }

    // ── Motion ──────────────────────────────────────────────────────────────────────────────────────────────────

    private static void SeedPositions(Distribution dist, MotionModel motion, Random rng, int dim, int n,
        float[] xs, float[] ys, float[] zs, int[] groupOf, int groupCount)
    {
        // Hotspots for the clustered distribution: 90 % of the population inside 5 % of the world's linear extent.
        var hotspots = new (float x, float y, float z)[8];
        for (var h = 0; h < hotspots.Length; h++)
        {
            hotspots[h] = ((float)(rng.NextDouble() * WorldExtent), (float)(rng.NextDouble() * WorldExtent),
                dim == 3 ? (float)(rng.NextDouble() * WorldExtent) : 0f);
        }

        var swarmCentres = new (float x, float y, float z)[groupCount];
        for (var g = 0; g < groupCount; g++)
        {
            swarmCentres[g] = ((float)(rng.NextDouble() * WorldExtent), (float)(rng.NextDouble() * WorldExtent),
                dim == 3 ? (float)(rng.NextDouble() * WorldExtent) : 0f);
        }

        for (var i = 0; i < n; i++)
        {
            groupOf[i] = i % groupCount;

            if (motion == MotionModel.Swarm)
            {
                // A swarm's members start together whatever the distribution says — the model IS the distribution here.
                var c = swarmCentres[groupOf[i]];
                xs[i] = Clamp(c.x + (float)((rng.NextDouble() - 0.5) * 200));
                ys[i] = Clamp(c.y + (float)((rng.NextDouble() - 0.5) * 200));
                zs[i] = dim == 3 ? Clamp(c.z + (float)((rng.NextDouble() - 0.5) * 200)) : 0f;
                continue;
            }

            if (dist == Distribution.Clustered && rng.NextDouble() < 0.9)
            {
                var h = hotspots[rng.Next(hotspots.Length)];
                var spread = WorldExtent * 0.05f;
                xs[i] = Clamp(h.x + (float)((rng.NextDouble() - 0.5) * spread));
                ys[i] = Clamp(h.y + (float)((rng.NextDouble() - 0.5) * spread));
                zs[i] = dim == 3 ? Clamp(h.z + (float)((rng.NextDouble() - 0.5) * spread)) : 0f;
                continue;
            }

            xs[i] = (float)(rng.NextDouble() * WorldExtent);
            ys[i] = (float)(rng.NextDouble() * WorldExtent);
            zs[i] = dim == 3 ? (float)(rng.NextDouble() * WorldExtent) : 0f;
        }
    }

    /// <summary>Advance the world one tick. Returns how many entities were touched.</summary>
    /// <summary>
    /// Advances the motion model and records WHICH entities moved in <paramref name="movedFlags"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>The flags exist because the previous version of this harness ignored the count it returned.</b> The caller ran
    /// <c>if (moved > 0) { for every entity: write }</c>, so a model that moved a tenth of the population still wrote all of it and dirtied every
    /// cluster. That is what produced the "97-99 % of clusters are dirty every tick" figure the design was written against — a property of this loop,
    /// not of any workload.</para>
    /// <para><b>And five of the six models move every entity by construction anyway</b>, which is a stress case rather than a realistic one.
    /// <see cref="MovingFraction"/> is the knob that makes the moving population a variable, so the partition can be measured at the 5-25 % that a
    /// real simulation is likelier to produce.</para>
    /// </remarks>
    private static int ApplyMotion(MotionModel motion, Random rng, int dim, int n, float cellSize,
        float[] xs, float[] ys, float[] zs, int[] groupOf, float[] gvx, float[] gvy, float[] gvz, int tick,
        bool[] movedFlags)
    {
        Array.Clear(movedFlags, 0, n);
        switch (motion)
        {
            case MotionModel.Static:
                return 0;

            case MotionModel.Drift:
            {
                // Deliberately sub-cell: no crossing is possible, so everything this produces is INTRA-cell drift,
                // which is what relocation and the repair path exist to absorb.
                var step = cellSize * 0.02f;
                var movedD = 0;
                for (var i = 0; i < n; i++)
                {
                    if (!Moves(rng))
                    {
                        continue;
                    }

                    movedFlags[i] = true;
                    movedD++;
                    xs[i] = Clamp(xs[i] + (float)((rng.NextDouble() - 0.5) * step));
                    ys[i] = Clamp(ys[i] + (float)((rng.NextDouble() - 0.5) * step));
                    if (dim == 3)
                    {
                        zs[i] = Clamp(zs[i] + (float)((rng.NextDouble() - 0.5) * step));
                    }
                }
                return movedD;
            }

            case MotionModel.Cruise:
            {
                var step = cellSize * 0.25f;
                var movedC = 0;
                for (var i = 0; i < n; i++)
                {
                    if (!Moves(rng))
                    {
                        continue;
                    }

                    movedFlags[i] = true;
                    movedC++;
                    xs[i] = Clamp(xs[i] + (float)((rng.NextDouble() - 0.5) * step));
                    ys[i] = Clamp(ys[i] + (float)((rng.NextDouble() - 0.5) * step));
                    if (dim == 3)
                    {
                        zs[i] = Clamp(zs[i] + (float)((rng.NextDouble() - 0.5) * step));
                    }
                }
                return movedC;
            }

            case MotionModel.Warp:
            {
                // A tenth of the population teleports. Every one of those is a guaranteed cell crossing, usually a long
                // one — the shape that produces a migration storm and the case the throttle is sized against.
                var count = Math.Max(1, (int)(n / 10 * MovingFraction));
                var movedW = 0;
                for (var k = 0; k < count; k++)
                {
                    var i = rng.Next(n);
                    if (!movedFlags[i])
                    {
                        movedFlags[i] = true;
                        movedW++;
                    }

                    xs[i] = (float)(rng.NextDouble() * WorldExtent);
                    ys[i] = (float)(rng.NextDouble() * WorldExtent);
                    zs[i] = dim == 3 ? (float)(rng.NextDouble() * WorldExtent) : 0f;
                }
                return movedW;
            }

            case MotionModel.Orbit:
            {
                // Rigid rotation about the world centre. Every entity moves, coherently, at a rate set by its radius —
                // so the crossing rate varies across the population without any randomness in it at all.
                const float centre = WorldExtent / 2f;
                var theta = 0.02f;
                var cos = MathF.Cos(theta);
                var sin = MathF.Sin(theta);
                var movedO = 0;
                for (var i = 0; i < n; i++)
                {
                    if (!Moves(rng))
                    {
                        continue;
                    }

                    movedFlags[i] = true;
                    movedO++;
                    var dx = xs[i] - centre;
                    var dy = ys[i] - centre;
                    xs[i] = Clamp(centre + ((dx * cos) - (dy * sin)));
                    ys[i] = Clamp(centre + ((dx * sin) + (dy * cos)));
                }
                return movedO;
            }

            case MotionModel.Swarm:
            {
                // Group centroids random-walk; members follow with jitter. Locality is preserved across cell crossings,
                // which is the case a cluster-based partition should handle best and a per-entity index worst.
                for (var g = 0; g < gvx.Length; g++)
                {
                    if ((tick % 20) == 0)
                    {
                        gvx[g] = (float)((rng.NextDouble() - 0.5) * cellSize * 0.5);
                        gvy[g] = (float)((rng.NextDouble() - 0.5) * cellSize * 0.5);
                        gvz[g] = dim == 3 ? (float)((rng.NextDouble() - 0.5) * cellSize * 0.5) : 0f;
                    }
                }
                var movedS = 0;
                for (var i = 0; i < n; i++)
                {
                    if (!Moves(rng))
                    {
                        continue;
                    }

                    movedFlags[i] = true;
                    movedS++;
                    var g = groupOf[i];
                    xs[i] = Clamp(xs[i] + gvx[g] + (float)((rng.NextDouble() - 0.5) * 4));
                    ys[i] = Clamp(ys[i] + gvy[g] + (float)((rng.NextDouble() - 0.5) * 4));
                    if (dim == 3)
                    {
                        zs[i] = Clamp(zs[i] + gvz[g] + (float)((rng.NextDouble() - 0.5) * 4));
                    }
                }
                return movedS;
            }

            default:
                return 0;
        }
    }

    /// <summary>Does this entity move on this tick? <c>true</c> for every entity at the default <see cref="MovingFraction"/> of 1.</summary>
    private static bool Moves(Random rng) => MovingFraction >= 1f || rng.NextDouble() < MovingFraction;

    private static float Clamp(float v) => Math.Clamp(v, 5f, WorldExtent - 5f);

    // ── Instrumentation ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Sums the per-tick telemetry so the report can quote a rate rather than one arbitrary tick.</summary>
    /// <summary>
    /// Per-tick fence phase timings, kept as span and CPU separately.
    /// </summary>
    /// <remarks>
    /// <para><b>Span is wall time, CPU is the sum across chunks, and conflating them is the mistake this type exists to prevent.</b> A phase that runs
    /// 8-way at 1 ms of span costs 8 ms of CPU; quoting the CPU figure as the tick cost makes a healthy parallel fence look like an 8x regression, and
    /// quoting span as the per-entity cost understates what the throttle's budget is actually spending. The design quotes per-entity costs in CPU; a frame
    /// budget is spent in span. Both are recorded, both are labelled.</para>
    /// <para>The fence total is the SUM of the six phase spans rather than a stopwatch around the lot: the phases are sequenced by the fence DAG's
    /// <c>After()</c> edges, so they do not overlap one another, and summing the instrumented spans excludes the scheduler's own dispatch gaps — which
    /// belong to the runtime rather than to the partitioning this harness is measuring.</para>
    /// </remarks>
    private sealed class PhaseAccumulator
    {
        private double _prepSpan, _prepCpu, _migSpan, _migCpu, _idxSpan, _idxCpu;
        private double _mapSpan, _mapCpu, _aabbSpan, _aabbCpu, _finSpan, _finCpu;
        private double _wall, _migTail, _idxMerge, _mapMerge, _finEmit, _finAppend, _swaps;
        private double _idxSortCpu, _mapSortCpu, _dirtySortCpu;
        private readonly double[] _phaseChunks = new double[5];
        private readonly double[] _phaseItems = new double[5];
        private long _chunks;
        private readonly List<double> _fenceSpans = [];

        public int Samples => _fenceSpans.Count;

        public double FenceSpanP50Us => Percentile(0.50);

        public double FenceSpanP99Us => Percentile(0.99);

        public double FenceSpanMaxUs => _fenceSpans.Count == 0 ? 0d : Sorted()[^1];

        public void Add(double prepSpan, double prepCpu, double migSpan, double migCpu, double idxSpan, double idxCpu,
            double mapSpan, double mapCpu, double aabbSpan, double aabbCpu, double finSpan, double finCpu, long chunks)
        {
            _prepSpan += prepSpan; _prepCpu += prepCpu;
            _migSpan += migSpan; _migCpu += migCpu;
            _idxSpan += idxSpan; _idxCpu += idxCpu;
            _mapSpan += mapSpan; _mapCpu += mapCpu;
            _aabbSpan += aabbSpan; _aabbCpu += aabbCpu;
            _finSpan += finSpan; _finCpu += finCpu;
            _chunks += chunks;
            _fenceSpans.Add(prepSpan + migSpan + idxSpan + mapSpan + aabbSpan + finSpan);
        }

        public void AddSerial(double wall, double migTail, double idxMerge, double mapMerge, double finEmit, double finAppend, double swaps)
        {
            _wall += wall; _migTail += migTail; _idxMerge += idxMerge; _mapMerge += mapMerge; _finEmit += finEmit;
            _finAppend += finAppend; _swaps += swaps;
        }

        public void AddChunkSorts(double idxSort, double mapSort, double dirtySort)
        {
            _idxSortCpu += idxSort; _mapSortCpu += mapSort; _dirtySortCpu += dirtySort;
        }

        public void AddWidths(TyphonRuntime runtime)
        {
            AddWidth(0, runtime.FencePrepExec);
            AddWidth(1, runtime.FenceMigrateExec);
            AddWidth(2, runtime.FenceIndexMassUpdateExec);
            AddWidth(3, runtime.FenceAabbRefreshExec);
            AddWidth(4, runtime.FenceFinalizeExec);
        }

        private void AddWidth(int i, FencePhaseExecSystemBase exec)
        {
            if (exec == null)
            {
                return;
            }

            _phaseChunks[i] += exec.PlanForTest.ChunkCount;
            _phaseItems[i] += exec.PlanForTest.ItemCount;
        }

        public void Fill(RunResult r)
        {
            var n = Math.Max(1, _fenceSpans.Count);
            r.PrepSpanUs = _prepSpan / n; r.PrepCpuUs = _prepCpu / n;
            r.MigrateSpanUs = _migSpan / n; r.MigrateCpuUs = _migCpu / n;
            r.IndexSpanUs = _idxSpan / n; r.IndexCpuUs = _idxCpu / n;
            r.EntityMapSpanUs = _mapSpan / n; r.EntityMapCpuUs = _mapCpu / n;
            r.AabbSpanUs = _aabbSpan / n; r.AabbCpuUs = _aabbCpu / n;
            r.FinalizeSpanUs = _finSpan / n; r.FinalizeCpuUs = _finCpu / n;
            r.FenceSpanUs = r.PrepSpanUs + r.MigrateSpanUs + r.IndexSpanUs + r.EntityMapSpanUs + r.AabbSpanUs + r.FinalizeSpanUs;
            r.FenceCpuUs = r.PrepCpuUs + r.MigrateCpuUs + r.IndexCpuUs + r.EntityMapCpuUs + r.AabbCpuUs + r.FinalizeCpuUs;
            r.FenceChunks = _chunks / n;
            r.FenceWallUs = _wall / n;
            r.MigrateTailUs = _migTail / n; r.IndexMergeUs = _idxMerge / n;
            r.EntityMapMergeUs = _mapMerge / n; r.FinalizeEmitUs = _finEmit / n;
            r.FinalizeAppendUs = _finAppend / n; r.WalSwapsPerTick = _swaps / n;
            r.IndexSortCpuUs = _idxSortCpu / n; r.MapSortCpuUs = _mapSortCpu / n; r.DirtySortCpuUs = _dirtySortCpu / n;
            r.PrepChunks = _phaseChunks[0] / n; r.PrepItems = _phaseItems[0] / n;
            r.MigrateChunks = _phaseChunks[1] / n; r.MigrateItems = _phaseItems[1] / n;
            r.IndexChunks = _phaseChunks[2] / n; r.IndexItems = _phaseItems[2] / n;
            r.AabbChunks = _phaseChunks[3] / n; r.AabbItems = _phaseItems[3] / n;
            r.FinalizeChunks = _phaseChunks[4] / n; r.FinalizeItems = _phaseItems[4] / n;
        }

        private List<double> Sorted()
        {
            _fenceSpans.Sort();
            return _fenceSpans;
        }

        private double Percentile(double q)
        {
            if (_fenceSpans.Count == 0)
            {
                return 0d;
            }

            var sorted = Sorted();
            return sorted[Math.Clamp((int)(sorted.Count * q), 0, sorted.Count - 1)];
        }
    }

    private sealed class Accumulator
    {
        private double _migrations, _migExecMs, _migTotalMs, _drifters, _driftAbsorbed, _hyst, _throttled, _superseded, _unplaced, _scanned, _slotsScanned;
        private double _repairEntities, _repairUnits, _repairRefused, _budgetUsed, _measuredNs, _queueDepth, _queueMaint;
        private long _queueEvicted, _valveFires;
        private double _prepSnapshot, _prepMask, _prepShadow, _prepZoneMap, _prepDetect, _prepThrottle, _prepPlan, _prepSort, _prepPreSize;
        private double _prepDirtyClusters;
        private int _measuredSamples;
        private double _driftGated, _driftSuppressedByDensity, _driftersSpilled, _unplacedNoCandidate, _pinsRejected, _relocAdmitted, _crossingsQueued;
        private double _relocSpendNs, _repairStarvedNs;

        public void Add(in SpatialMigrationTelemetry t)
        {
            _migrations += t.MigrationCount;
            _migExecMs += t.MigrationExecuteMs;
            _migTotalMs += t.MigrationTotalMs;
            _drifters += t.DriftersDetected;
            _driftAbsorbed += t.DriftAbsorbedCount;
            _hyst += t.HysteresisAbsorbedCount;
            _throttled += t.RelocationsThrottled;
            _superseded += t.RelocationsSuperseded;
            _prepSnapshot += t.PrepSnapshotMs;
            _prepMask += t.PrepMaskMs;
            _prepShadow += t.PrepShadowMs;
            _prepZoneMap += t.PrepZoneMapMs;
            _prepDetect += t.PrepDetectMs;
            _prepThrottle += t.PrepThrottleMs;
            _prepPlan += t.PrepPlanMs;
            _prepSort += t.PrepSortMs;
            _prepPreSize += t.PrepPreSizeMs;
            _prepDirtyClusters += t.PrepDirtyClusters;
            _unplaced += t.DriftersUnplaced;
            _driftGated += t.DriftGatedClusters;
            _driftSuppressedByDensity += t.DriftSuppressedByDensity;
            _driftersSpilled += t.DriftersSpilled;
            _unplacedNoCandidate += t.DriftersUnplacedNoCandidate;
            _pinsRejected += t.PinsRejected;
            _relocAdmitted += t.RelocationsAdmitted;
            _crossingsQueued += t.CrossingsQueued;
            _relocSpendNs += t.RelocationSpendNs;
            _repairStarvedNs += t.RepairBudgetStarvedNs;
            _scanned += t.ClustersScanned;
            _slotsScanned += t.SlotsScanned;
            _repairEntities += t.RepairedEntityCount;
            _repairUnits += t.RepairUnitCount;
            _repairRefused += t.RepairUnitsRefused;
            _budgetUsed += t.ReclusterBudgetUsedMs;
            _queueDepth += t.RepairQueueDepth;
            _queueMaint += t.RepairQueueMaintenanceMs;
            _queueEvicted = Math.Max(_queueEvicted, t.RepairQueueEvicted);
            _valveFires += t.RepairValveFires;

            // Averaging a zero from a tick that measured nothing would drag the estimate toward zero and report a cost
            // model that never converged.
            if (t.MeasuredNsPerEntity > 0d)
            {
                _measuredNs += t.MeasuredNsPerEntity;
                _measuredSamples++;
            }
        }

        public void FillPerTick(RunResult r, int ticks)
        {
            r.MigrationsPerTick = _migrations / ticks;
            r.SupersededPerTick = _superseded / ticks;
            r.PrepSnapshotMs = _prepSnapshot / ticks;
            r.PrepMaskMs = _prepMask / ticks;
            r.PrepShadowMs = _prepShadow / ticks;
            r.PrepZoneMapMs = _prepZoneMap / ticks;
            r.PrepDetectMs = _prepDetect / ticks;
            r.PrepThrottleMs = _prepThrottle / ticks;
            r.PrepPlanMs = _prepPlan / ticks;
            r.PrepSortMs = _prepSort / ticks;
            r.PrepPreSizeMs = _prepPreSize / ticks;
            r.PrepDirtyClustersPerTick = _prepDirtyClusters / ticks;
            r.MigrationExecuteMs = _migExecMs / ticks;
            r.MigrationTotalMs = _migTotalMs / ticks;
            r.DriftersPerTick = _drifters / ticks;
            r.DriftAbsorbedPerTick = _driftAbsorbed / ticks;
            r.HysteresisAbsorbedPerTick = _hyst / ticks;
            r.ThrottledPerTick = _throttled / ticks;
            r.UnplacedPerTick = _unplaced / ticks;
            r.DriftGatedPerTick = _driftGated / ticks;
            r.DriftSuppressedByDensityPerTick = _driftSuppressedByDensity / ticks;
            r.DriftersSpilledPerTick = _driftersSpilled / ticks;
            r.UnplacedNoCandidatePerTick = _unplacedNoCandidate / ticks;
            r.PinsRejectedPerTick = _pinsRejected / ticks;
            r.RelocationsAdmittedPerTick = _relocAdmitted / ticks;
            r.CrossingsQueuedPerTick = _crossingsQueued / ticks;
            r.RelocationSpendMs = _relocSpendNs / ticks / 1_000_000d;
            r.RepairStarvedMs = _repairStarvedNs / ticks / 1_000_000d;
            r.ClustersScannedPerTick = _scanned / ticks;
            r.SlotsScannedPerTick = _slotsScanned / ticks;
            r.RepairEntitiesPerTick = _repairEntities / ticks;
            r.RepairUnitsPerTick = _repairUnits / ticks;
            r.RepairRefusedPerTick = _repairRefused / ticks;
            r.BudgetUsedMs = _budgetUsed / ticks;
            r.QueueDepth = _queueDepth / ticks;
            r.QueueMaintenanceMs = _queueMaint / ticks;
            r.QueueEvicted = _queueEvicted;
            r.ValveFires = _valveFires;
            r.MeasuredNsPerEntity = _measuredSamples > 0 ? _measuredNs / _measuredSamples : 0d;
        }
    }

    /// <summary>
    /// Cluster tightness — the mean and 90th percentile of each cluster's largest axis extent, as a percentage of the
    /// cell size.
    /// </summary>
    /// <remarks>
    /// This is the design's own selectivity proxy: a query opens a cluster when its box overlaps, so a cluster spanning
    /// 100 % of its cell is opened by every query that touches the cell, and one spanning 25 % by a sixteenth of them in
    /// 2D. It is measured here rather than inferred because it is the quantity the whole re-clustering budget is spent
    /// buying, and section 1.1's headline factor is a claim about it.
    /// </remarks>
    private static void MeasurePartition(DatabaseEngine dbe, int archetypeId, RunResult r)
    {
        var cs = dbe._archetypeStates[archetypeId].ClusterState;
        if (cs?.ClusterAabbs == null)
        {
            return;
        }

        var cellSize = dbe.SpatialGrid.Config.CellSize;
        var extents = new List<double>(cs.ActiveClusterCount);
        for (var i = 0; i < cs.ActiveClusterCount; i++)
        {
            var id = cs.ActiveClusterIds[i];
            if ((uint)id >= (uint)cs.ClusterAabbs.Length)
            {
                continue;
            }

            ref var b = ref cs.ClusterAabbs[id];
            var ex = Math.Max(b.MaxX - b.MinX, Math.Max(b.MaxY - b.MinY, b.MaxZ - b.MinZ));
            if (float.IsFinite(ex) && ex >= 0f)
            {
                extents.Add(100d * ex / cellSize);
            }
        }

        r.ActiveClusters = cs.ActiveClusterCount;
        // Cells that HOLD something, not cells that exist: under a clustered distribution the grid materialises cells the population never reaches,
        // and clusters per cell read low against them.
        var liveCells = 0;
        for (var key = 0; key < dbe.SpatialGrid.CellCount; key++)
        {
            if (dbe.SpatialGrid.GetCell(key).EntityCount > 0)
            {
                liveCells++;
            }
        }

        r.LiveCells = liveCells;
        r.EntitiesPerCluster = r.ActiveClusters > 0 ? (double)r.Entities / r.ActiveClusters : 0d;
        r.EntitiesPerCell = r.LiveCells > 0 ? (double)r.Entities / r.LiveCells : 0d;

        if (extents.Count == 0)
        {
            return;
        }

        extents.Sort();
        var s = 0d;
        foreach (var e in extents)
        {
            s += e;
        }
        r.TightnessPct = s / extents.Count;
        r.TightnessP90Pct = extents[Math.Min(extents.Count - 1, (int)(extents.Count * 0.9))];
        r.ClustersPerCell = r.LiveCells > 0 ? (double)r.ActiveClusters / r.LiveCells : 0d;
        // Over the archetype's REAL slot count (49 for these components), not the 64-slot ceiling — the ceiling read every figure 23 % low.
        r.SlotOccupancyPct = r.ActiveClusters > 0 ? 100d * r.Entities / (r.ActiveClusters * (double)cs.Layout.ClusterSize) : 0d;
    }

    /// <summary>
    /// One fence's tightness-to-bound (#918 AC-17.3), twice: as the engine sampled it, and over EVERY active cluster.
    /// </summary>
    /// <remarks>
    /// The engine's figure averages only the clusters its AABB pass examined that fence, which is every moving cluster and none of the still ones. The
    /// whole-archetype extent is divided by the SAMPLED mean bound, which is exact
    /// for a uniform world (every cell carries the same population, so every cell the same bound) and an approximation otherwise; the row prints both so
    /// the reader can see when they part.
    /// </remarks>
    private static string TraceTightnessRow(DatabaseEngine dbe, int archetypeId, int fence)
    {
        var t = dbe.GetSpatialTelemetry(archetypeId);
        var cs = dbe._archetypeStates[archetypeId].ClusterState;
        var cellSize = dbe.SpatialGrid.Config.CellSize;
        var sum = 0d;
        var count = 0;
        for (var i = 0; i < cs.ActiveClusterCount; i++)
        {
            var id = cs.ActiveClusterIds[i];
            if ((uint)id >= (uint)cs.ClusterAabbs.Length)
            {
                continue;
            }

            ref var b = ref cs.ClusterAabbs[id];
            var ex = Math.Max(b.MaxX - b.MinX, Math.Max(b.MaxY - b.MinY, b.MaxZ - b.MinZ));
            if (float.IsFinite(ex) && ex >= 0f)
            {
                sum += ex / cellSize;
                count++;
            }
        }

        var extentAll = count > 0 ? sum / count : 0d;
        var toBoundAll = t.MeanPackingBound > 0d ? extentAll / t.MeanPackingBound : 0d;
        return $"  fence {fence,4}  ttb(sampled) {t.MeanTightnessToBound,6:F3}  ttb(all) {toBoundAll,6:F3}  extent(all) {extentAll,6:F3}  "
            + $"bound {t.MeanPackingBound,6:F3}  clusters {cs.ActiveClusterCount,6}  gated {t.DriftGatedClusters,5}  repairs {t.RepairsExecuted,6}  "
            + $"boost {cs.DriftTargetBoost,5:F2}  budgetUsed {t.ReclusterBudgetUsedMs,6:F3}  relocAdmitted {t.RelocationsAdmitted,5}  "
            + $"throttled {t.RelocationsThrottled,5}";
    }

    private static void MeasureQueries<TArch, TPos>(DatabaseEngine dbe, int dim, float cellSize, RunResult r,
        PosReader<TPos> read, Comp<TPos> comp, EntityId[] ids)
        where TArch : Archetype<TArch>
        where TPos : unmanaged
    {
        const int reps = 24;
        var rng = new Random(777);
        var sw = new Stopwatch();

        // Warm-up, discarded. Every query shape below is generic over the archetype, so the FIRST call of each one
        // pays JIT for a fresh instantiation — measured at 300-450 us against a 5 us steady-state cost, which landed
        // entirely on whichever shape happened to be timed first and read as a partitioning result. One pass of every
        // shape, thrown away, before any of them is timed.
        WarmUpQueries<TArch, TPos>(dbe, dim);

        // Three AABB sizes as fractions of the WORLD, not of the cell.
        //
        // Scaling them by cell size made the numbers meaningless the moment Matrix C swept it: at 4 cells across, a
        // "medium" query covered the whole world at large cell sizes and returned all 16 000 entities, so the column
        // read as a query-cost regression when it was really a query-SIZE change. As world fractions the selectivity is
        // the same experiment at every point in every matrix, which is what makes the times comparable.
        (r.AabbSmallNs, r.AabbSmallHits) = TimeAabb<TArch, TPos>(dbe, dim, reps, rng, sw, WorldExtent * 0.01f);
        (r.AabbMediumNs, r.AabbMediumHits) = TimeAabb<TArch, TPos>(dbe, dim, reps, rng, sw, WorldExtent * 0.05f);
        (r.AabbLargeNs, r.AabbLargeHits) = TimeAabb<TArch, TPos>(dbe, dim, reps, rng, sw, WorldExtent * 0.20f);

        // Radius
        {
            var totalTicks = 0L;
            var hits = 0L;
            for (var k = 0; k < reps; k++)
            {
                var cx = rng.NextDouble() * WorldExtent;
                var cy = rng.NextDouble() * WorldExtent;
                var cz = dim == 3 ? rng.NextDouble() * WorldExtent : 0d;
                using var tx = dbe.CreateQuickTransaction();
                sw.Restart();
                var res = tx.Query<TArch>().WhereNearby<TPos>(cx, cy, cz, WorldExtent * 0.05f).Execute();
                sw.Stop();
                totalTicks += sw.ElapsedTicks;
                hits += res.Count;
            }
            r.RadiusNs = TicksToNs(totalTicks) / reps;
            r.RadiusHits = (double)hits / reps;
        }

        // Ray — a full diagonal traverse, which is the worst case for the cell walk.
        {
            var totalTicks = 0L;
            var hits = 0L;
            for (var k = 0; k < reps; k++)
            {
                var ox = rng.NextDouble() * WorldExtent;
                var oy = rng.NextDouble() * WorldExtent;
                var oz = dim == 3 ? rng.NextDouble() * WorldExtent : 0d;
                var inv = 1d / Math.Sqrt(dim == 3 ? 3d : 2d);
                using var tx = dbe.CreateQuickTransaction();
                sw.Restart();
                var res = tx.Query<TArch>().WhereRay<TPos>(ox, oy, oz, inv, inv, dim == 3 ? inv : 0d, WorldExtent).Execute();
                sw.Stop();
                totalTicks += sw.ElapsedTicks;
                hits += res.Count;
            }
            r.RayNs = TicksToNs(totalTicks) / reps;
            r.RayHits = (double)hits / reps;
        }

        // Frustum — an axis-aligned box expressed as six planes, sized like the medium AABB so the two are comparable
        // and the difference is the plane classification rather than the region.
        {
            var totalTicks = 0L;
            var hits = 0L;
            var half = WorldExtent * 0.05f;
            for (var k = 0; k < reps; k++)
            {
                var cx = rng.NextDouble() * WorldExtent;
                var cy = rng.NextDouble() * WorldExtent;
                var cz = dim == 3 ? rng.NextDouble() * WorldExtent : 0d;
                double minX = cx - half, minY = cy - half, minZ = cz - half;
                double maxX = cx + half, maxY = cy + half, maxZ = cz + half;

                double[] planes = dim == 3
                    ? [+1, 0, 0, -minX, -1, 0, 0, +maxX, 0, +1, 0, -minY, 0, -1, 0, +maxY, 0, 0, +1, -minZ, 0, 0, -1, +maxZ]
                    : [+1, 0, -minX, -1, 0, +maxX, 0, +1, -minY, 0, -1, +maxY];
                var planeCount = dim == 3 ? 6 : 4;

                using var tx = dbe.CreateQuickTransaction();
                sw.Restart();
                var res = tx.Query<TArch>().WhereFrustum<TPos>(planes, planeCount, minX, minY, minZ, maxX, maxY, maxZ).Execute();
                sw.Stop();
                totalTicks += sw.ElapsedTicks;
                hits += res.Count;
            }
            r.FrustumNs = TicksToNs(totalTicks) / reps;
            r.FrustumHits = (double)hits / reps;
        }

        // Brute force over the same medium region — the number the index has to beat, measured through the public API
        // rather than estimated. Few repetitions on purpose: it is O(population) per query and the point is the RATIO.
        {
            const int bfReps = 4;
            var totalTicks = 0L;
            var half = WorldExtent * 0.05f;
            for (var k = 0; k < bfReps; k++)
            {
                var cx = rng.NextDouble() * WorldExtent;
                var cy = rng.NextDouble() * WorldExtent;
                var cz = dim == 3 ? rng.NextDouble() * WorldExtent : 0d;
                using var tx = dbe.CreateQuickTransaction();
                sw.Restart();
                var found = 0;
                for (var i = 0; i < ids.Length; i++)
                {
                    ref readonly var p = ref tx.Open(ids[i]).Read(comp);
                    var (x, y, z) = read(in p);
                    if (Math.Abs(x - cx) <= half && Math.Abs(y - cy) <= half && (dim == 2 || Math.Abs(z - cz) <= half))
                    {
                        found++;
                    }
                }
                sw.Stop();
                totalTicks += sw.ElapsedTicks;
                GC.KeepAlive(found);
            }
            r.BruteForceNs = TicksToNs(totalTicks) / bfReps;
        }
    }

    /// <summary>Times an AABB query over a random region of the given half-extent.</summary>
    /// <remarks>
    /// <b>The arguments are the same shape for both dimensions</b>, and that is worth a note because it was not always
    /// true. <c>EcsQuery</c> used to read a 2D component's max corner from slots 2 and 3 — the caller's <c>minZ</c> and
    /// <c>maxX</c> — producing a degenerate box and a silently empty answer. This harness's first full run reported
    /// <c>0 hits</c> on every one of its 2D rows for that reason, and it read as a partitioning result rather than an
    /// engine defect. Fixed in #872 step 13; the harness passes <c>(min, max)</c> uniformly again.
    /// </remarks>
    /// <summary>Runs one of every query shape and discards the result, so JIT does not land on the first timed shape.</summary>
    private static void WarmUpQueries<TArch, TPos>(DatabaseEngine dbe, int dim)
        where TArch : Archetype<TArch>
        where TPos : unmanaged
    {
        var h = WorldExtent * 0.05f;
        double[] planes = dim == 3
            ? [+1, 0, 0, -0d, -1, 0, 0, +WorldExtent, 0, +1, 0, -0d, 0, -1, 0, +WorldExtent, 0, 0, +1, -0d, 0, 0, -1, +WorldExtent]
            : [+1, 0, -0d, -1, 0, +WorldExtent, 0, +1, -0d, 0, -1, +WorldExtent];
        var planeCount = dim == 3 ? 6 : 4;

        using var tx = dbe.CreateQuickTransaction();
        _ = tx.Query<TArch>().WhereInAABB<TPos>(0, 0, 0, h, h, h).Execute().Count;
        _ = tx.Query<TArch>().WhereNearby<TPos>(h, h, dim == 3 ? h : 0d, h).Execute().Count;
        _ = tx.Query<TArch>().WhereRay<TPos>(0, 0, 0, 1, 0, 0, WorldExtent).Execute().Count;
        _ = tx.Query<TArch>().WhereFrustum<TPos>(planes, planeCount, 0, 0, 0, WorldExtent, WorldExtent, WorldExtent).Execute().Count;
    }

    private static (double ns, double hits) TimeAabb<TArch, TPos>(DatabaseEngine dbe, int dim, int reps, Random rng, Stopwatch sw, float half)
        where TArch : Archetype<TArch>
        where TPos : unmanaged
    {
        var totalTicks = 0L;
        var hits = 0L;
        for (var k = 0; k < reps; k++)
        {
            var cx = rng.NextDouble() * WorldExtent;
            var cy = rng.NextDouble() * WorldExtent;
            var cz = dim == 3 ? rng.NextDouble() * WorldExtent : 0d;
            using var tx = dbe.CreateQuickTransaction();
            sw.Restart();
            var res = tx.Query<TArch>()
                .WhereInAABB<TPos>(cx - half, cy - half, cz - half, cx + half, cy + half, cz + half)
                .Execute();
            sw.Stop();
            totalTicks += sw.ElapsedTicks;
            hits += res.Count;
        }

        return (TicksToNs(totalTicks) / reps, (double)hits / reps);
    }

    private static double TicksToNs(long ticks) => ticks * (1_000_000_000d / Stopwatch.Frequency);

    // ── Output ──────────────────────────────────────────────────────────────────────────────────────────────────

    private static void Report(RunResult r)
    {
        if (r.Failure.Length > 0)
        {
            // Printed HERE as well as recorded in the CSV. RunOne only prints failures it CAUGHT itself; a run that completed while its Move system was
            // silently absorbed by the scheduler reaches this method with a Failure set and nothing on the console, which is how a motionless world got
            // reported as a 0.33 ms fence for an entire campaign. Every column below would be zero or meaningless, so the row is the message.
            Console.WriteLine($"  !! {r.Matrix} {r.Dim}D {r.Motion} {r.Dist} n={r.Entities} W={r.Workers} moving={r.MovingFraction:P0} INVALID: {r.Failure}");
            return;
        }

        // Span AND cpu, side by side, because the ratio between them is the headline this harness exists to produce: it is the fence's actual parallel
        // efficiency, and the serial version of this harness could not report it at all.
        var par = r.FenceSpanUs > 0d ? r.FenceCpuUs / r.FenceSpanUs : 0d;

        Console.WriteLine(
            $"  {r.Matrix} {r.Dim}D {r.Motion,-7} {r.Dist,-9} n={r.Entities,-6} cell={r.CellSize,-6:F1} bud={r.BudgetMs,-4:F2} W={r.Workers,-2} | "
            + $"fence {r.TickMsMean,7:F3} ms wall (p99 {r.TickMsP99,7:F3}) cpu {r.FenceCpuUs / 1000d,8:F3} ms = {par,5:F2}x | "
            + $"prep {r.PrepSpanUs / 1000d,6:F2} mig {r.MigrateSpanUs / 1000d,6:F2} idx {r.IndexSpanUs / 1000d,6:F2} "
            + $"map {r.EntityMapSpanUs / 1000d,6:F2} aabb {r.AabbSpanUs / 1000d,6:F2} fin {r.FinalizeSpanUs / 1000d,6:F2} | "
            + $"wall {r.FenceWallUs / 1000d,6:F2} serial tail {r.MigrateTailUs / 1000d:F2} "
            + $"idxMerge {r.IndexMergeUs / 1000d:F2} emit {r.FinalizeEmitUs / 1000d:F2} "
            + $"(append {r.FinalizeAppendUs / 1000d:F2}, swaps {r.WalSwapsPerTick:F2}) | "
            + $"chunks/items p {r.PrepChunks:F0}/{r.PrepItems:F0} m {r.MigrateChunks:F0}/{r.MigrateItems:F0} i {r.IndexChunks:F0}/{r.IndexItems:F0} "
            + $"a {r.AabbChunks:F0}/{r.AabbItems:F0} f {r.FinalizeChunks:F0}/{r.FinalizeItems:F0} | "
            + $"mig/t {r.MigrationsPerTick,8:F1} | tight0 {r.TightnessAtSpawnPct,6:F1}% tight {r.TightnessPct,6:F1}% | clusters {r.ActiveClusters,6} "
            + $"cl/cell {r.ClustersPerCell,5:F2} occ {r.SlotOccupancyPct,5:F1}% | "
            + $"aabbM {r.AabbMediumNs / 1000d,7:F1} us | bf {r.BruteForceNs / 1000d,8:F1} us"
            + (r.ChurnTicks > 0 ? $" | aged {r.ChurnFraction:P0}x{r.ChurnTicks} inv {r.ActiveListInversions}" : string.Empty));
        ReportDetectionCounters(r);
    }

    /// <summary>
    /// Wave-2 detection counters (K1, K2, K5, K6, K9) as a second line under the main row, so the main row's columns stay where they are.
    /// </summary>
    private static void ReportDetectionCounters(RunResult r)
    {
        if (r.Failure.Length > 0)
        {
            return;
        }

        Console.WriteLine(
            $"      detect: gated {r.DriftGatedPerTick,8:F1}/t suppressed {r.DriftSuppressedByDensityPerTick,8:F1}/t spilled {r.DriftersSpilledPerTick,7:F1}/t | "
            + $"unplaced-no-cand {r.UnplacedNoCandidatePerTick,8:F1}/t | pins-rejected {r.PinsRejectedPerTick,7:F1}/t | "
            + $"admitted reloc {r.RelocationsAdmittedPerTick,8:F1}/t crossings {r.CrossingsQueuedPerTick,8:F1}/t | "
            + $"reloc spend {r.RelocationSpendMs,6:F3} ms repair-starved {r.RepairStarvedMs,6:F3} ms");
    }

    private static void WriteCsv(List<RunResult> results, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',',
            "matrix", "dim", "motion", "dist", "entities", "cellSize", "budgetMs", "ticks", "workers", "barrier", "branchPath", "movingFraction",
            "spawnMs", "firstFenceMs", "moveMs",
            "fenceSpanMs", "fenceSpanP50Ms", "fenceSpanP99Ms", "fenceSpanMaxMs", "fenceCpuUs", "fenceChunks",
            "prepSpanUs", "prepCpuUs", "migrateSpanUs", "migrateCpuUs", "indexSpanUs", "indexCpuUs",
            "entityMapSpanUs", "entityMapCpuUs", "aabbSpanUs", "aabbCpuUs", "finalizeSpanUs", "finalizeCpuUs",
            "migrationsPerTick", "migrationExecuteMs", "migrationTotalMs", "driftersPerTick", "driftAbsorbedPerTick",
            "hysteresisAbsorbedPerTick", "throttledPerTick", "prepSnapshotMs", "prepMaskMs", "prepShadowMs", "prepZoneMapMs", "prepDetectMs", "prepThrottleMs", "prepPlanMs", "prepSortMs", "prepPreSizeMs",
            "prepDirtyClusters", "supersededPerTick", "unplacedPerTick", "clustersScannedPerTick", "slotsScannedPerTick",
            "repairEntitiesPerTick", "repairUnitsPerTick", "repairRefusedPerTick", "budgetUsedMs", "measuredNsPerEntity",
            "driftGatedPerTick", "driftSuppressedByDensityPerTick", "driftersSpilledPerTick", "unplacedNoCandidatePerTick", "pinsRejectedPerTick", "relocationsAdmittedPerTick",
            "crossingsQueuedPerTick", "relocationSpendMs", "repairStarvedMs",
            "queueDepth", "queueEvicted", "queueMaintenanceMs", "valveFires",
            "activeClusters", "liveCells", "entitiesPerCluster", "entitiesPerCell", "tightnessPct", "tightnessP90Pct",
            "tightnessAtSpawnPct", "tightnessAtSpawnP90Pct", "clustersPerCell", "slotOccupancyPct", "clustersPerCellAtSpawn", "slotOccupancyAtSpawnPct",
            "aabbSmallNs", "aabbSmallHits", "aabbMediumNs", "aabbMediumHits", "aabbLargeNs", "aabbLargeHits",
            "radiusNs", "radiusHits", "rayNs", "rayHits", "frustumNs", "frustumHits", "bruteForceNs",
            "churnFraction", "churnTicks", "activeListInversions",
            "fenceWallUs", "migrateTailUs", "indexMergeUs", "entityMapMergeUs", "finalizeEmitUs", "finalizeAppendUs", "walSwapsPerTick",
            "prepChunks", "prepItems", "migrateChunks", "migrateItems", "indexChunks", "indexItems", "aabbChunks", "aabbItems", "finalizeChunks",
            "finalizeItems", "indexSortCpuUs", "mapSortCpuUs", "dirtySortCpuUs", "failure"));

        var c = CultureInfo.InvariantCulture;
        foreach (var r in results)
        {
            sb.AppendLine(string.Join(',',
                r.Matrix, r.Dim, r.Motion, r.Dist, r.Entities,
                r.CellSize.ToString(c), r.BudgetMs.ToString(c), r.Ticks, r.Workers, r.Barrier ? 1 : 0, r.FenceBranchPath, F(r.MovingFraction),
                F(r.SpawnMs), F(r.FirstFenceMs), F(r.MoveMs),
                F(r.TickMsMean), F(r.TickMsP50), F(r.TickMsP99), F(r.TickMsMax), F(r.FenceCpuUs), r.FenceChunks,
                F(r.PrepSpanUs), F(r.PrepCpuUs), F(r.MigrateSpanUs), F(r.MigrateCpuUs), F(r.IndexSpanUs), F(r.IndexCpuUs),
                F(r.EntityMapSpanUs), F(r.EntityMapCpuUs), F(r.AabbSpanUs), F(r.AabbCpuUs), F(r.FinalizeSpanUs), F(r.FinalizeCpuUs),
                F(r.MigrationsPerTick), F(r.MigrationExecuteMs), F(r.MigrationTotalMs), F(r.DriftersPerTick), F(r.DriftAbsorbedPerTick),
                F(r.HysteresisAbsorbedPerTick), F(r.ThrottledPerTick), F(r.PrepSnapshotMs), F(r.PrepMaskMs), F(r.PrepShadowMs), F(r.PrepZoneMapMs), F(r.PrepDetectMs), F(r.PrepThrottleMs),
                F(r.PrepPlanMs), F(r.PrepSortMs), F(r.PrepPreSizeMs), F(r.PrepDirtyClustersPerTick), F(r.SupersededPerTick), F(r.UnplacedPerTick), F(r.ClustersScannedPerTick), F(r.SlotsScannedPerTick),
                F(r.RepairEntitiesPerTick), F(r.RepairUnitsPerTick), F(r.RepairRefusedPerTick), F(r.BudgetUsedMs), F(r.MeasuredNsPerEntity),
                F(r.DriftGatedPerTick), F(r.DriftSuppressedByDensityPerTick), F(r.DriftersSpilledPerTick), F(r.UnplacedNoCandidatePerTick), F(r.PinsRejectedPerTick), F(r.RelocationsAdmittedPerTick),
                F(r.CrossingsQueuedPerTick), F(r.RelocationSpendMs), F(r.RepairStarvedMs),
                F(r.QueueDepth), r.QueueEvicted, F(r.QueueMaintenanceMs), r.ValveFires,
                r.ActiveClusters, r.LiveCells, F(r.EntitiesPerCluster), F(r.EntitiesPerCell), F(r.TightnessPct), F(r.TightnessP90Pct),
                F(r.TightnessAtSpawnPct), F(r.TightnessAtSpawnP90Pct), F(r.ClustersPerCell), F(r.SlotOccupancyPct), F(r.ClustersPerCellAtSpawn),
                F(r.SlotOccupancyAtSpawnPct),
                F(r.AabbSmallNs), F(r.AabbSmallHits), F(r.AabbMediumNs), F(r.AabbMediumHits), F(r.AabbLargeNs), F(r.AabbLargeHits),
                F(r.RadiusNs), F(r.RadiusHits), F(r.RayNs), F(r.RayHits), F(r.FrustumNs), F(r.FrustumHits), F(r.BruteForceNs),
                F(r.ChurnFraction), r.ChurnTicks, r.ActiveListInversions,
                F(r.FenceWallUs), F(r.MigrateTailUs), F(r.IndexMergeUs), F(r.EntityMapMergeUs), F(r.FinalizeEmitUs),
                F(r.FinalizeAppendUs), F(r.WalSwapsPerTick),
                F(r.PrepChunks), F(r.PrepItems), F(r.MigrateChunks), F(r.MigrateItems), F(r.IndexChunks), F(r.IndexItems), F(r.AabbChunks), F(r.AabbItems),
                F(r.FinalizeChunks), F(r.FinalizeItems), F(r.IndexSortCpuUs), F(r.MapSortCpuUs), F(r.DirtySortCpuUs), r.Failure));
        }

        File.WriteAllText(path, sb.ToString());
    }

    /// <summary>Adjacent inversions in the archetype's active cluster list — zero when ascending. See <see cref="RunResult.ActiveListInversions"/>.</summary>
    private static long CountActiveListInversions(DatabaseEngine dbe, int archetypeId)
    {
        var st = dbe._archetypeStates[archetypeId]?.ClusterState;
        if (st == null)
        {
            return 0;
        }
        var ids = st.ActiveClusterIds;
        var n = st.ActiveClusterCount;
        var inv = 0L;
        for (var i = 1; i < n; i++)
        {
            if (ids[i - 1] > ids[i])
            {
                inv++;
            }
        }
        return inv;
    }

    private static int ClusterStateBranchPath(DatabaseEngine dbe, int archetypeId)
    {
        var st = dbe._archetypeStates[archetypeId]?.ClusterState;
        return st?.FenceBranchPath ?? 0;
    }

    private static string F(double v) => v.ToString("F4", CultureInfo.InvariantCulture);

    /// <summary>Flattens an exception message into one comma-free CSV cell.</summary>
    private static string SanitizeForCsv(string text) => text.Replace(',', ';').Replace('\n', ' ').Replace('\r', ' ');

    private static bool IsDebug()
    {
#if DEBUG
        return true;
#else
        return false;
#endif
    }

    private static float ArgFloat(string[] args, string name, float fallback)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length && float.TryParse(args[i + 1], CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    private static int ArgInt(string[] args, string name, int fallback)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var v) ? v : fallback;
    }

    private static string ArgString(string[] args, string name, string fallback)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
    }
}
