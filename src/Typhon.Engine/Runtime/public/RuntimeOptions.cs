using JetBrains.Annotations;
using System;

namespace Typhon.Engine;

/// <summary>
/// Configuration options for the DAG scheduler and runtime tick loop.
/// </summary>
[PublicAPI]
public class RuntimeOptions
{
    /// <summary>
    /// Minimum expected entries per bucket (<c>migrants / bucketCount</c>) before migration stages its EntityMap patches for the bulk phase instead of
    /// applying them inline. Below it, the inline per-entity path runs and the phase emits nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The bulk path's entire gain comes from entries sharing a bucket, and a small batch has none to share.</b> Measured A/B on <c>--fence-phase</c>
    /// against a 400 000-entity archetype whose map holds ~57 000 buckets, comparing <c>Migrate CPU + EntityMapUpdate CPU</c> per migrant:
    /// </para>
    /// <list type="table">
    ///   <item><description>100 000 migrants (~1.8 per bucket): bulk <b>402 / 511 / 558</b> ns at W = 1 / 4 / 8 against inline <b>691 / 849 / 758</b>. Bulk
    ///   wins at every worker count.</description></item>
    ///   <item><description>10 000 migrants (~0.17 per bucket): bulk <b>1287 / 1240 / 1350</b> ns against inline <b>1571 / 1022 / 1066</b>. Inline wins at
    ///   W ≥ 2, and the spread within a single arm is wide enough that the two are not separable — the EntityMap term is a small part of a noisy Migrate
    ///   phase at that batch size.</description></item>
    /// </list>
    /// <para>
    /// <b>The default is set from the mechanism, not from a resolved crossover.</b> Runs shorter than one entry amortise nothing by construction, so
    /// <c>1.0</c> is where the bulk path's advantage begins to exist at all; the crossover is somewhere below it and the benchmark could not resolve where.
    /// Treat the value as provisional and re-measure when step 10's re-clustering produces a realistic migration rate — that is the workload this should be
    /// tuned against, not the synthetic churn above.
    /// </para>
    /// <para>
    /// Set to <c>0</c> to force the bulk path always, and <see cref="float.MaxValue"/> to force the inline path for any archetype whose map has buckets;
    /// both arms of the measurement above used exactly that, via <c>--fence-phase --bulk-min=</c>. The <see cref="float.MaxValue"/> sentinel relies on
    /// .NET Core 3.0+ SATURATING float-to-integer conversion — <c>(long)(float.MaxValue * bucketCount)</c> overflows to infinity and saturates to
    /// <see cref="long.MaxValue"/>, which no migrant count reaches. Under the older unspecified conversion the same expression could yield
    /// <see cref="long.MinValue"/> and force the BULK path instead, silently inverting the sentinel.
    /// </para>
    /// </remarks>
    public float EntityMapBulkMinEntriesPerBucket { get; init; } = EntityMapUpdateStaging.DefaultMinEntriesPerBucket;

    /// <summary>
    /// Target tick rate in Hz. Default: 60.
    /// The scheduler uses metronome-style tick advancement to prevent drift.
    /// </summary>
    public int BaseTickRate { get; set; } = 60;

    /// <summary>
    /// Number of worker threads for parallel system execution.
    /// Set to -1 (default) for automatic: <c>Math.Max(1, Environment.ProcessorCount - 4)</c>.
    /// Set to 1 for single-threaded debug mode (systems execute in topological order on the timer thread).
    /// </summary>
    public int WorkerCount { get; set; } = -1;

    /// <summary>
    /// Capacity of the telemetry ring buffer (number of ticks retained). Must be a power of 2.
    /// Default: 1024 (~17 seconds at 60Hz, ~200KB).
    /// </summary>
    public int TelemetryRingCapacity { get; set; } = 1024;

    /// <summary>
    /// Overload detection and response configuration. Always active with sensible defaults.
    /// </summary>
    public OverloadOptions Overload { get; set; } = new();

    /// <summary>
    /// What an unhandled system exception means for the rest of the tick. Default:
    /// <see cref="Typhon.Engine.SystemExceptionPolicy.Isolate"/> — fault isolation, the behaviour Typhon has always had.
    /// Set <see cref="Typhon.Engine.SystemExceptionPolicy.AbortTickAndStop"/> for hosts that treat one tick as the unit
    /// of publication and must not publish a partially-executed tick (issue #567). That policy is <b>terminal</b>: the
    /// runtime stops ticking after the first fatal system exception.
    /// </summary>
    public SystemExceptionPolicy SystemExceptionPolicy { get; set; } = SystemExceptionPolicy.Isolate;

    /// <summary>
    /// Minimum number of entities per chunk for parallel QuerySystem dispatch.
    /// Controls granularity: fewer entities per chunk = more parallelism but more overhead (Transaction creation per chunk).
    /// Entity sets smaller than this value still use the parallel chunk path with <c>totalChunks=1</c>.
    /// Default: 64.
    /// </summary>
    public int ParallelQueryMinChunkSize { get; set; } = 64;

    /// <summary>
    /// Size parallel <see cref="QuerySystem"/> chunks by their measured cost rather than their entity count. Default: true.
    /// </summary>
    /// <remarks>
    /// <para>From its second dispatch on, a system's chunk count comes from its previous dispatch's worker time per entity. Its chunks spread over the
    /// width, <c>round(WorkerCount × ChunksPerWorker)</c>, as long as each carries 25–100 µs of work; below that band it gets fewer chunks of 25 µs, above it
    /// more chunks than the width, of 100 µs, up to twice the width, which the workers claim as they come free, so the pool no longer waits on one long last
    /// chunk. At most one entity per chunk, and beyond the width at most one cluster per chunk.</para>
    /// <para>A system's first dispatch, a checkerboard system and a system with its own <see cref="SystemDefinition.MinChunkSize"/> keep the entity
    /// rule: <c>min(round(WorkerCount × ChunksPerWorker), ceil(entities / ParallelQueryMinChunkSize))</c>.</para>
    /// </remarks>
    public bool CostBasedChunking { get; set; } = true;

    /// <summary>
    /// When true (default), <c>WriteTickFence</c> is parallelized across the worker pool via the internal sub-DAG (<c>FenceExec</c>).
    /// When false, the runtime falls back to the legacy single-threaded serial fence — useful for diagnostics and as a safety
    /// fallback if a regression is suspected. Enabling adds <c>FenceExec</c> to the scheduler's full system array, but
    /// <see cref="DagScheduler.SystemCount"/> reports user-registered systems only — see <see cref="DagScheduler.AllSystemCount"/>
    /// for the total including internal systems.
    /// </summary>
    public bool EnableParallelFence { get; set; } = true;

    /// <summary>
    /// Oversubscription factor for fence-chunk dispatch: the chunk-count cap becomes <c>FenceChunkOversubscription × WorkerCount</c>.
    /// Above 1 lets the scheduler smooth out per-worker preemption jitter — a healthy worker can pick up the next queued chunk
    /// while a preempted worker finishes its current one. Must be ≥ 1. Default: 2.
    /// </summary>
    public int FenceChunkOversubscription { get; set; } = 2;

    /// <summary>
    /// Cost model coefficients for the fence work-planner. Each per-stage cost is the hint count multiplied by the matching coefficient;
    /// the planner uses the total cost to pick chunk count and split splittable items. Defaults are 1.0 everywhere — tune from
    /// real-world traces.
    /// <para>When <see cref="AdaptiveFenceCost"/> is true (default), this value is only used as the initial seed; runtime
    /// continuously recalibrates <c>MigrationCost</c> and <c>AabbCost</c> from measured chunk wall-time.</para>
    /// </summary>
    public FenceCostModel FenceCostModel { get; set; } = FenceCostModel.Default;

    /// <summary>
    /// When true, MigrationCost and AabbCost are continuously calibrated from a 64-tick sliding window of per-chunk
    /// measurements. Static <see cref="FenceCostModel"/> values seed the model at startup; subsequent ticks converge
    /// toward the measured µs/unit. Disable for repeatable benchmarks or to pin behaviour to the static seed.
    /// </summary>
    public bool AdaptiveFenceCost { get; set; } = true;

    /// <summary>
    /// Memory and policy rails for engine-owned replication — the pools, the per-session ceilings and the skip thresholds. Always present with defaults;
    /// a runtime with no session connected pays nothing for it.
    /// </summary>
    /// <remarks>
    /// It lives here rather than on <see cref="TyphonOptions"/> because replication rides the tick: every budget below is spent by the subscriptions track,
    /// which the runtime dispatches, and none of it is database state. What an application <i>declares</i> — projections, profiles, commands — goes to
    /// <see cref="TyphonRuntime.Subscriptions"/> instead; this is the operator's half of the same subsystem.
    /// </remarks>
    public SubscriptionsOptions Subscriptions { get; set; } = new();

    /// <summary>
    /// Resolves the effective worker count, applying the auto-detect formula if <see cref="WorkerCount"/> is -1.
    /// </summary>
    internal int ResolveWorkerCount() => WorkerCount == -1 ? Math.Max(1, Environment.ProcessorCount - 4) : WorkerCount;
}

/// <summary>
/// Per-stage cost coefficients used by the fence work-planner to size chunks. Each value scales the corresponding work-hint
/// (migration count, dirty-cluster count, shadow-entry count, spatial-entry count) into a unitless cost figure that the
/// planner bin-packs across workers.
/// <para>
/// <b>Unit:</b> 1 cost unit ≈ 1 µs of single-worker wall time. <see cref="Default"/> is calibrated against AntHill traces
/// (migration ≈ 33 µs/entity, AABB recompute ≈ 2.4 µs/cluster). Shadow / Spatial coefficients are placeholders pending
/// measurement. <b>Other workload profiles (shadow-heavy SV writes, sparse spatial) should override these via
/// <see cref="RuntimeOptions.FenceCostModel"/></b>; the defaults will load-balance against ratios that don't match
/// your workload, leading to less optimal chunk packing.
/// </para>
/// </summary>
[PublicAPI]
public sealed record FenceCostModel(float MigrationCost, float AabbCost, float ShadowCost, float SpatialCost)
{
    /// <summary>
    /// µs per staged index value update, for the IndexMassUpdate phase (#872 step 6).
    /// </summary>
    /// <remarks>
    /// <b>Three orders of magnitude below <see cref="MigrationCost"/>, which is exactly why it needs its own coefficient.</b> The phase's first
    /// implementation reused <c>MigrationCost</c> — ≈33 µs, the cost of MOVING an entity — to price an operation measured at 0.055-0.077 µs. Nothing
    /// mis-computes, but every index batch then looks enormously expensive to the planner, so <c>ComputeMaxChunks</c>'s
    /// <c>floor(totalCost / MinChunkCostUs)</c> term saturates at its <c>2 × workerCount × oversubscription</c> cap for any batch at all, and the phase
    /// splits into the smallest chunks it is allowed to — precisely the regime the 200 µs floor exists to avoid.
    /// <para>
    /// Seeded at 0.06 from #872 step 6's <c>--fence-parallel</c> measurement: 10 000 uniform updates on a 1 M-entry tree at 55-77 ns each, depending on how
    /// many leaves the batch touches. Calibrated live from the phase's own wall time when <see cref="RuntimeOptions.AdaptiveFenceCost"/> is on.
    /// </para>
    /// </remarks>
    public float IndexUpdateCost { get; init; } = 0.06f;

    /// <summary>
    /// Cost per staged EntityMap location patch, in the planner's arbitrary units. Seeded from step 7's measurement — ~40 ns/entity for the serial apply,
    /// against ~11 ns for an index update — and refined at runtime by <see cref="LiveFenceCostModel"/> like every other coefficient.
    /// </summary>
    public float EntityMapUpdateCost { get; init; } = 0.04f;

    /// <summary>
    /// µs per dirty cluster of Finalize's WAL emit when it runs as slices (#889). Seeded from the measurement that motivated the slicing — 0.6 ms for
    /// ~2 000 dirty clusters of 64 entities on Matrix M — and refined at runtime by <see cref="LiveFenceCostModel"/> like every other coefficient.
    /// </summary>
    public float FinalizeEmitCost { get; init; } = 0.3f;

    /// <summary>
    /// Default coefficients, calibrated against AntHill traces: migration ≈ 33.3 µs/entity, AABB recompute ≈ 2.4 µs/cluster.
    /// Shadow and Spatial coefficients are placeholders (1.0) pending measurement — override them for shadow-heavy or spatial workloads.
    /// </summary>
    public static readonly FenceCostModel Default = new(33.3f, 2.4f, 1f, 1f);
}
