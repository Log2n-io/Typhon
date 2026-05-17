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
    /// Subscription server configuration. Set to non-null to enable the TCP subscription server.
    /// If null, no subscription server is started (subscriptions disabled).
    /// </summary>
    public SubscriptionServerOptions SubscriptionServer { get; set; }

    /// <summary>
    /// Overload detection and response configuration. Always active with sensible defaults.
    /// </summary>
    public OverloadOptions Overload { get; set; } = new();

    /// <summary>
    /// Minimum number of entities per chunk for parallel QuerySystem dispatch.
    /// Controls granularity: fewer entities per chunk = more parallelism but more overhead (Transaction creation per chunk).
    /// Entity sets smaller than this value still use the parallel chunk path with <c>totalChunks=1</c>.
    /// Default: 64.
    /// </summary>
    public int ParallelQueryMinChunkSize { get; set; } = 64;

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
    public static readonly FenceCostModel Default = new(33.3f, 2.4f, 1f, 1f);
}
