// unset

using JetBrains.Annotations;
using Microsoft.Extensions.Configuration;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;

namespace Typhon.Engine;

/// <summary>
/// Global telemetry configuration for Typhon Engine.
///
/// <para>
/// This class provides <c>static readonly</c> fields that allow the JIT compiler to
/// eliminate disabled telemetry code paths entirely. When a readonly field is <c>false</c>,
/// the JIT can treat <c>if (TelemetryConfig.ProfilerActive)</c> as dead code and
/// remove it completely in Tier 1 compilation.
/// </para>
///
/// <para>
/// <b>IMPORTANT:</b> Call <see cref="EnsureInitialized"/> once at application startup,
/// BEFORE any hot paths are JIT compiled. This ensures the static constructor runs
/// early and the JIT sees the final values when compiling performance-critical methods.
/// </para>
///
/// <para>
/// Configuration precedence (highest to lowest):
/// <list type="number">
///   <item>Environment variables (TYPHON__PROFILER__ENABLED, etc.)</item>
///   <item>typhon.telemetry.json in current directory</item>
///   <item>typhon.telemetry.json next to the assembly</item>
///   <item>Built-in defaults (all disabled)</item>
/// </list>
/// </para>
/// </summary>
/// <remarks>
/// Environment variable naming uses double underscore (<c>__</c>) as hierarchy separator
/// for cross-platform compatibility:
/// <code>
/// TYPHON__PROFILER__ENABLED=true
/// TYPHON__PROFILER__GCTRACING__ENABLED=true
/// TYPHON__PROFILER__SCHEDULER__GAUGES__STRAGGLERGAP__ENABLED=true
/// </code>
/// </remarks>
[PublicAPI]
[ExcludeFromCodeCoverage]
public static class TelemetryConfig
{
    // ═══════════════════════════════════════════════════════════════════════════
    // MASTER SWITCH
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Global profiler enable/disable. When false, all profiler-emitted telemetry is disabled
    /// regardless of individual component settings.
    /// </summary>
    /// <remarks>
    /// Reads from <c>Typhon:Profiler:Enabled</c>.
    /// </remarks>
    public static readonly bool Enabled;

    // ═══════════════════════════════════════════════════════════════════════════
    // SCHEDULER TELEMETRY
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Whether Scheduler component telemetry is enabled in configuration. Reads from <c>Typhon:Profiler:Scheduler:Enabled</c>.</summary>
    public static readonly bool SchedulerEnabled;

    /// <summary>Whether to track per-system transition latency. Reads from <c>Typhon:Profiler:Scheduler:Gauges:TransitionLatency:Enabled</c>.</summary>
    public static readonly bool SchedulerTrackTransitionLatency;

    /// <summary>Whether to track per-worker active/idle time breakdown. Reads from <c>Typhon:Profiler:Scheduler:Gauges:WorkerUtilization:Enabled</c>.</summary>
    public static readonly bool SchedulerTrackWorkerUtilization;

    /// <summary>Whether to track straggler gap (parallel efficiency metric for Patate systems). Reads from <c>Typhon:Profiler:Scheduler:Gauges:StragglerGap:Enabled</c>.</summary>
    public static readonly bool SchedulerTrackStragglerGap;

    /// <summary>
    /// Whether to capture per-(system, archetype) entity-touch counts at parallel-query completion (Workbench Data Flow module).
    /// Reads from <c>Typhon:Profiler:Scheduler:ArchetypeTouches:Enabled</c>.
    /// Default <c>true</c>. JIT dead-code-eliminates the capture path when <c>false</c>.
    /// </summary>
    public static readonly bool SchedulerArchetypeTouchesActive;

    /// <summary>
    /// Combined flag: true only if the new master AND scheduler telemetry are enabled.
    /// Gates deep metrics (straggler gap, per-worker utilization). The ring buffer itself is always on.
    /// </summary>
    public static readonly bool SchedulerActive;

    // ═══════════════════════════════════════════════════════════════════════════
    // PROFILER (#243)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Whether the general-purpose profiler is enabled in configuration.
    /// </summary>
    /// <remarks>
    /// In the new namespace, <c>ProfilerEnabled</c> is identical to <see cref="Enabled"/> — the master
    /// switch <i>is</i> the profiler enable. The two fields are kept distinct for source-compatibility
    /// with code that reads either name.
    /// </remarks>
    public static readonly bool ProfilerEnabled;

    /// <summary>
    /// Combined flag: true if the profiler master switch is on. Identical to <see cref="Enabled"/> in the new namespace.
    /// </summary>
    /// <remarks>
    /// This is the single hot-path gate on every <c>TyphonEvent.BeginSpan</c> producer call. When <c>false</c>, the JIT folds
    /// <c>if (!TelemetryConfig.ProfilerActive) return default;</c> into a no-op on Tier 1 compilation, so the entire profiler producer
    /// path costs zero CPU when disabled.
    /// <para>
    /// Note that <c>TyphonProfiler.Start/Stop</c> only controls consumer + exporter lifecycle — not this gate. The gate is set once at
    /// class load from the config file and never changes for the life of the process.
    /// </para>
    /// </remarks>
    public static readonly bool ProfilerActive;

    /// <summary>
    /// Whether opt-in .NET runtime GC-event tracing is requested by configuration. Reads from <c>Typhon:Profiler:GcTracing:Enabled</c>.
    /// </summary>
    public static readonly bool ProfilerGcTracingEnabled;

    /// <summary>
    /// Combined flag: true only if <see cref="ProfilerActive"/> AND <see cref="ProfilerGcTracingEnabled"/> are set.
    /// </summary>
    public static readonly bool ProfilerGcTracingActive;

    /// <summary>
    /// Whether opt-in per-allocation tracking is requested by configuration. Reads from <c>Typhon:Profiler:MemoryAllocations:Enabled</c>.
    /// </summary>
    public static readonly bool ProfilerMemoryAllocationsEnabled;

    /// <summary>
    /// Combined flag: true only if <see cref="ProfilerActive"/> AND <see cref="ProfilerMemoryAllocationsEnabled"/> are set.
    /// </summary>
    public static readonly bool ProfilerMemoryAllocationsActive;

    /// <summary>
    /// Whether opt-in in-process CPU stack sampling is requested by configuration. Reads from <c>Typhon:Profiler:CpuSampling:Enabled</c>.
    /// </summary>
    public static readonly bool ProfilerCpuSamplingEnabled;

    /// <summary>
    /// Combined flag: true only if <see cref="ProfilerActive"/> AND <see cref="ProfilerCpuSamplingEnabled"/> are set.
    /// </summary>
    public static readonly bool ProfilerCpuSamplingActive;

    /// <summary>
    /// Whether opt-in per-tick gauge snapshots are requested by configuration. Reads from <c>Typhon:Profiler:Gauges:Enabled</c>.
    /// </summary>
    public static readonly bool ProfilerGaugesEnabled;

    /// <summary>
    /// Combined flag: true only if <see cref="ProfilerActive"/> AND <see cref="ProfilerGaugesEnabled"/> are set.
    /// </summary>
    public static readonly bool ProfilerGaugesActive;

    // ═══════════════════════════════════════════════════════════════════════════
    // CONCURRENCY (Phase 1 + Phase 2) — leaf flags for AccessControl, AccessControlSmall,
    // ResourceAccessControl, Epoch, AdaptiveWaiter, OlcLatch.
    //
    // All default false. Parent-implies-children semantics via TelemetryConfigResolver:
    // flipping Profiler:Concurrency:Enabled = true turns the whole subtree on;
    // per-leaf Enabled keys override.
    // ═══════════════════════════════════════════════════════════════════════════

    // ── Root + sub-tree parents ────────────────────────────────────────────────

    /// <summary>Combined Concurrency root gate: master <see cref="Enabled"/> AND <c>Typhon:Profiler:Concurrency:Enabled</c>.</summary>
    public static readonly bool ConcurrencyActive;

    /// <summary>Combined gate for the AccessControl subtree.</summary>
    public static readonly bool ConcurrencyAccessControlActive;

    /// <summary>Combined gate for the AccessControlSmall subtree.</summary>
    public static readonly bool ConcurrencyAccessControlSmallActive;

    /// <summary>Combined gate for the ResourceAccessControl subtree.</summary>
    public static readonly bool ConcurrencyResourceAccessControlActive;

    /// <summary>Combined gate for the Epoch subtree.</summary>
    public static readonly bool ConcurrencyEpochActive;

    /// <summary>Combined gate for the AdaptiveWaiter subtree.</summary>
    public static readonly bool ConcurrencyAdaptiveWaiterActive;

    /// <summary>Combined gate for the OlcLatch subtree.</summary>
    public static readonly bool ConcurrencyOlcLatchActive;

    // ── AccessControl leaves ───────────────────────────────────────────────────

    /// <summary>Combined gate for AccessControl shared-acquire events.</summary>
    public static readonly bool ConcurrencyAccessControlSharedAcquireActive;

    /// <summary>Combined gate for AccessControl shared-release events.</summary>
    public static readonly bool ConcurrencyAccessControlSharedReleaseActive;

    /// <summary>Combined gate for AccessControl exclusive-acquire events.</summary>
    public static readonly bool ConcurrencyAccessControlExclusiveAcquireActive;

    /// <summary>Combined gate for AccessControl exclusive-release events.</summary>
    public static readonly bool ConcurrencyAccessControlExclusiveReleaseActive;

    /// <summary>Combined gate for AccessControl shared↔exclusive promotion/demotion events.</summary>
    public static readonly bool ConcurrencyAccessControlPromotionActive;

    /// <summary>Combined gate for AccessControl contention markers.</summary>
    public static readonly bool ConcurrencyAccessControlContentionActive;

    // ── AccessControlSmall leaves ──────────────────────────────────────────────

    /// <summary>Combined gate for AccessControlSmall shared-acquire events.</summary>
    public static readonly bool ConcurrencyAccessControlSmallSharedAcquireActive;

    /// <summary>Combined gate for AccessControlSmall shared-release events.</summary>
    public static readonly bool ConcurrencyAccessControlSmallSharedReleaseActive;

    /// <summary>Combined gate for AccessControlSmall exclusive-acquire events.</summary>
    public static readonly bool ConcurrencyAccessControlSmallExclusiveAcquireActive;

    /// <summary>Combined gate for AccessControlSmall exclusive-release events.</summary>
    public static readonly bool ConcurrencyAccessControlSmallExclusiveReleaseActive;

    /// <summary>Combined gate for AccessControlSmall contention markers.</summary>
    public static readonly bool ConcurrencyAccessControlSmallContentionActive;

    // ── ResourceAccessControl leaves ───────────────────────────────────────────

    /// <summary>Combined gate for ResourceAccessControl Accessing-mode acquire events.</summary>
    public static readonly bool ConcurrencyResourceAccessControlAccessingActive;

    /// <summary>Combined gate for ResourceAccessControl Modify-mode acquire events.</summary>
    public static readonly bool ConcurrencyResourceAccessControlModifyActive;

    /// <summary>Combined gate for ResourceAccessControl Destroy-mode acquire events.</summary>
    public static readonly bool ConcurrencyResourceAccessControlDestroyActive;

    /// <summary>Combined gate for ResourceAccessControl Modify-promotion slow-path events.</summary>
    public static readonly bool ConcurrencyResourceAccessControlModifyPromotionActive;

    /// <summary>Combined gate for ResourceAccessControl contention markers.</summary>
    public static readonly bool ConcurrencyResourceAccessControlContentionActive;

    // ── Epoch leaves ───────────────────────────────────────────────────────────

    /// <summary>Combined gate for EpochGuard Enter (PinCurrentThread) events.</summary>
    public static readonly bool ConcurrencyEpochScopeEnterActive;

    /// <summary>Combined gate for EpochGuard Dispose events.</summary>
    public static readonly bool ConcurrencyEpochScopeExitActive;

    /// <summary>Combined gate for GlobalEpoch advance events.</summary>
    public static readonly bool ConcurrencyEpochAdvanceActive;

    /// <summary>Combined gate for RefreshScope events.</summary>
    public static readonly bool ConcurrencyEpochRefreshActive;

    /// <summary>Combined gate for EpochThreadRegistry slot-claim events.</summary>
    public static readonly bool ConcurrencyEpochSlotClaimActive;

    /// <summary>Combined gate for EpochThreadRegistry dead-thread slot-reclaim events.</summary>
    public static readonly bool ConcurrencyEpochSlotReclaimActive;

    // ── AdaptiveWaiter leaves ──────────────────────────────────────────────────

    /// <summary>Combined gate for AdaptiveWaiter yield-or-sleep transition events. Phase 2 design (#280) chose transitions only — NOT per-spin — to keep trace volume sane.</summary>
    public static readonly bool ConcurrencyAdaptiveWaiterYieldOrSleepActive;

    // ── OlcLatch leaves ────────────────────────────────────────────────────────

    /// <summary>Combined gate for OlcLatch TryWriteLock-failure events.</summary>
    public static readonly bool ConcurrencyOlcLatchWriteLockAttemptActive;

    /// <summary>Combined gate for OlcLatch WriteUnlock events.</summary>
    public static readonly bool ConcurrencyOlcLatchWriteUnlockActive;

    /// <summary>Combined gate for OlcLatch MarkObsolete events.</summary>
    public static readonly bool ConcurrencyOlcLatchMarkObsoleteActive;

    /// <summary>Combined gate for OlcLatch ValidateVersion-failure events.</summary>
    public static readonly bool ConcurrencyOlcLatchValidationFailActive;

    // ═══════════════════════════════════════════════════════════════════════════
    // SPATIAL TRACING (Phase 3 — see 03-spatial.md)
    // ═══════════════════════════════════════════════════════════════════════════
    // Greenfield namespace under `Typhon:Profiler:Spatial:*`. Default-OFF for everything except
    // `ClusterMigration:Execute` which preserves the pre-Phase-3 behavior of kind 60 (the only
    // Spatial event that already shipped).

    /// <summary>Combined gate for the entire Spatial subsystem (parent of all Spatial:* leaves).</summary>
    public static readonly bool SpatialActive;

    // Subtree parents
    /// <summary>Combined gate for the Spatial:Query subtree (AABB/radius/ray/frustum/kNN/count query spans).</summary>
    public static readonly bool SpatialQueryActive;
    /// <summary>Combined gate for the Spatial:RTree subtree (R-tree structural mutation spans).</summary>
    public static readonly bool SpatialRTreeActive;
    /// <summary>Combined gate for the Spatial:Grid subtree (uniform-grid cell tier/occupancy spans).</summary>
    public static readonly bool SpatialGridActive;
    /// <summary>Combined gate for the Spatial:Cell subtree.</summary>
    public static readonly bool SpatialCellActive;
    /// <summary>Combined gate for the Spatial:Cell:Index subtree (per-cell occupant index add/update/remove spans).</summary>
    public static readonly bool SpatialCellIndexActive;
    /// <summary>Combined gate for the Spatial:ClusterMigration subtree (cross-cell cluster migration spans).</summary>
    public static readonly bool SpatialClusterMigrationActive;
    /// <summary>Combined gate for the Spatial:TierIndex subtree (tier-index rebuild spans).</summary>
    public static readonly bool SpatialTierIndexActive;
    /// <summary>Combined gate for the Spatial:Maintain subtree (index-maintenance spans on entity insert/update).</summary>
    public static readonly bool SpatialMaintainActive;
    /// <summary>Combined gate for the Spatial:Trigger subtree (spatial-trigger region/eval spans).</summary>
    public static readonly bool SpatialTriggerActive;
    /// <summary>Combined gate for the Spatial:Trigger:Occupant subtree.</summary>
    public static readonly bool SpatialTriggerOccupantActive;
    /// <summary>Combined gate for the Spatial:Trigger:Cache subtree.</summary>
    public static readonly bool SpatialTriggerCacheActive;

    // Query leaves (kinds 117-122)
    /// <summary>Combined gate for the Spatial:Query:Aabb span (axis-aligned bounding-box overlap query).</summary>
    public static readonly bool SpatialQueryAabbActive;
    /// <summary>Combined gate for the Spatial:Query:Radius span (radius/sphere overlap query).</summary>
    public static readonly bool SpatialQueryRadiusActive;
    /// <summary>Combined gate for the Spatial:Query:Ray span (ray-cast query).</summary>
    public static readonly bool SpatialQueryRayActive;
    /// <summary>Combined gate for the Spatial:Query:Frustum span (frustum-culling query).</summary>
    public static readonly bool SpatialQueryFrustumActive;
    /// <summary>Combined gate for the Spatial:Query:Knn span (k-nearest-neighbour query).</summary>
    public static readonly bool SpatialQueryKnnActive;
    /// <summary>Combined gate for the Spatial:Query:Count span (count-only spatial query).</summary>
    public static readonly bool SpatialQueryCountActive;

    // RTree structural leaves (kinds 123-126)
    /// <summary>Combined gate for the Spatial:RTree:Insert span.</summary>
    public static readonly bool SpatialRTreeInsertActive;
    /// <summary>Combined gate for the Spatial:RTree:Remove span.</summary>
    public static readonly bool SpatialRTreeRemoveActive;
    /// <summary>Combined gate for the Spatial:RTree:NodeSplit span.</summary>
    public static readonly bool SpatialRTreeNodeSplitActive;
    /// <summary>Combined gate for the Spatial:RTree:BulkLoad span (bulk STR-pack load).</summary>
    public static readonly bool SpatialRTreeBulkLoadActive;

    // Grid leaves (kinds 127-129)
    /// <summary>Combined gate for the Spatial:Grid:CellTierChange span (cell promoted/demoted between density tiers).</summary>
    public static readonly bool SpatialGridCellTierChangeActive;
    /// <summary>Combined gate for the Spatial:Grid:OccupancyChange span.</summary>
    public static readonly bool SpatialGridOccupancyChangeActive;
    /// <summary>Combined gate for the Spatial:Grid:ClusterCellAssign span (cluster assigned to a grid cell).</summary>
    public static readonly bool SpatialGridClusterCellAssignActive;

    // Cell:Index leaves (kinds 130-132)
    /// <summary>Combined gate for the Spatial:Cell:Index:Add span.</summary>
    public static readonly bool SpatialCellIndexAddActive;
    /// <summary>Combined gate for the Spatial:Cell:Index:Update span.</summary>
    public static readonly bool SpatialCellIndexUpdateActive;
    /// <summary>Combined gate for the Spatial:Cell:Index:Remove span.</summary>
    public static readonly bool SpatialCellIndexRemoveActive;

    // ClusterMigration leaves (kinds 133-135; Execute = existing kind 60)
    /// <summary>Combined gate for the Spatial:ClusterMigration:Detect span (migration-needed detection).</summary>
    public static readonly bool SpatialClusterMigrationDetectActive;
    /// <summary>Combined gate for the Spatial:ClusterMigration:Queue span (migration enqueue).</summary>
    public static readonly bool SpatialClusterMigrationQueueActive;
    /// <summary>Combined gate for the Spatial:ClusterMigration:Execute span (migration apply; legacy kind 60, on by default).</summary>
    public static readonly bool SpatialClusterMigrationExecuteActive;
    /// <summary>Combined gate for the Spatial:ClusterMigration:Hysteresis span (hysteresis suppression of a migration).</summary>
    public static readonly bool SpatialClusterMigrationHysteresisActive;

    // TierIndex leaves (kinds 136-137)
    /// <summary>Combined gate for the Spatial:TierIndex:Rebuild span.</summary>
    public static readonly bool SpatialTierIndexRebuildActive;
    /// <summary>Combined gate for the Spatial:TierIndex:VersionSkip span (rebuild skipped because the version was unchanged).</summary>
    public static readonly bool SpatialTierIndexVersionSkipActive;

    // Maintain leaves (kinds 138-141)
    /// <summary>Combined gate for the Spatial:Maintain:Insert span.</summary>
    public static readonly bool SpatialMaintainInsertActive;
    /// <summary>Combined gate for the Spatial:Maintain:UpdateSlowPath span.</summary>
    public static readonly bool SpatialMaintainUpdateSlowPathActive;
    /// <summary>Combined gate for the Spatial:Maintain:AabbValidate span.</summary>
    public static readonly bool SpatialMaintainAabbValidateActive;
    /// <summary>Combined gate for the Spatial:Maintain:BackPointerWrite span.</summary>
    public static readonly bool SpatialMaintainBackPointerWriteActive;

    // Trigger leaves (kinds 142-145)
    /// <summary>Combined gate for the Spatial:Trigger:Region span.</summary>
    public static readonly bool SpatialTriggerRegionActive;
    /// <summary>Combined gate for the Spatial:Trigger:Eval span (trigger-region evaluation).</summary>
    public static readonly bool SpatialTriggerEvalActive;
    /// <summary>Combined gate for the Spatial:Trigger:Occupant:Diff span (enter/exit occupant diff).</summary>
    public static readonly bool SpatialTriggerOccupantDiffActive;
    /// <summary>Combined gate for the Spatial:Trigger:Cache:Invalidate span.</summary>
    public static readonly bool SpatialTriggerCacheInvalidateActive;

    // ═══════════════════════════════════════════════════════════════════════════
    // SCHEDULER & RUNTIME TRACING (Phase 4 — see 04-scheduler-runtime.md)
    // ═══════════════════════════════════════════════════════════════════════════
    // Greenfield deeper subtree. The existing `SchedulerActive` master gate (above) stays;
    // these are NEW sub-tree gates allowing operators to opt into Scheduler depth + Runtime
    // (UoW, Tx Lifecycle, Subscription) tracing per-leaf.

    // Scheduler subtree parents
    /// <summary>Combined gate for the Scheduler:System subtree (per-system execution/queue-wait spans).</summary>
    public static readonly bool SchedulerSystemActive;
    /// <summary>Combined gate for the Scheduler:Worker subtree (worker idle/wake spans).</summary>
    public static readonly bool SchedulerWorkerActive;
    /// <summary>Combined gate for the Scheduler:Dispense span (work-item dispense to workers).</summary>
    public static readonly bool SchedulerDispenseActive;
    /// <summary>Combined gate for the Scheduler:Dependency subtree (dependency-ready/fan-out spans).</summary>
    public static readonly bool SchedulerDependencyActive;
    /// <summary>Combined gate for the Scheduler:Overload subtree (overload level-change/shed spans).</summary>
    public static readonly bool SchedulerOverloadActive;
    /// <summary>Combined gate for the Scheduler:Graph subtree (DAG build/rebuild spans).</summary>
    public static readonly bool SchedulerGraphActive;

    // Scheduler:System leaves (kinds 146-149)
    /// <summary>Combined gate for the Scheduler:System:StartExecution span.</summary>
    public static readonly bool SchedulerSystemStartExecutionActive;
    /// <summary>Combined gate for the Scheduler:System:Completion span.</summary>
    public static readonly bool SchedulerSystemCompletionActive;
    /// <summary>Combined gate for the Scheduler:System:QueueWait span (time a system waited in its queue before running).</summary>
    public static readonly bool SchedulerSystemQueueWaitActive;
    /// <summary>Combined gate for the Scheduler:System:SingleThreaded span.</summary>
    public static readonly bool SchedulerSystemSingleThreadedActive;

    // Scheduler:Worker leaves (kinds 150-152)
    /// <summary>Combined gate for the Scheduler:Worker:Idle span.</summary>
    public static readonly bool SchedulerWorkerIdleActive;
    /// <summary>Combined gate for the Scheduler:Worker:Wake span.</summary>
    public static readonly bool SchedulerWorkerWakeActive;
    /// <summary>Combined gate for the Scheduler:Worker:BetweenTick span (worker gap between ticks).</summary>
    public static readonly bool SchedulerWorkerBetweenTickActive;

    // Scheduler:Dependency leaves (kinds 154-155)
    /// <summary>Combined gate for the Scheduler:Dependency:Ready span (a system's dependencies became satisfied).</summary>
    public static readonly bool SchedulerDependencyReadyActive;
    /// <summary>Combined gate for the Scheduler:Dependency:FanOut span.</summary>
    public static readonly bool SchedulerDependencyFanOutActive;

    // Scheduler:Overload leaves (kinds 156-158)
    /// <summary>Combined gate for the Scheduler:Overload:LevelChange span.</summary>
    public static readonly bool SchedulerOverloadLevelChangeActive;
    /// <summary>Combined gate for the Scheduler:Overload:SystemShed span (a system shed under overload).</summary>
    public static readonly bool SchedulerOverloadSystemShedActive;
    /// <summary>Combined gate for the Scheduler:Overload:TickMultiplier span.</summary>
    public static readonly bool SchedulerOverloadTickMultiplierActive;

    // Scheduler:Overload:Detector leaf (kind 242 — issue #289 follow-up)
    /// <summary>Combined flag for the per-tick OverloadDetector gauge snapshot (overrunRatio, consecutive counters, level, multiplier).</summary>
    public static readonly bool SchedulerOverloadDetectorActive;

    // Scheduler:Metronome subtree (issue #289 follow-up — surfaces inter-tick wait)
    /// <summary>Combined gate for the Scheduler:Metronome subtree.</summary>
    public static readonly bool SchedulerMetronomeActive;
    /// <summary>Combined flag for the SchedulerMetronomeWait span (kind 241).</summary>
    public static readonly bool SchedulerMetronomeWaitActive;

    // Scheduler:Queue subtree (#311 — surfaces per-tick queue depth telemetry)
    /// <summary>Combined gate for the Scheduler:Queue subtree.</summary>
    public static readonly bool SchedulerQueueActive;
    /// <summary>
    /// Combined flag for the QueueTickEnd instant (kind 244). Per-(tick, queue) rollup emitted at end-of-tick;
    /// drives the Workbench Data API <c>queue/&lt;name&gt;</c> tracks (#311 / DAG view backpressure edges).
    /// </summary>
    public static readonly bool SchedulerQueueTickEndActive;

    // Scheduler:Graph leaves (kinds 159-160)
    /// <summary>Combined gate for the Scheduler:Graph:Build span (initial DAG build).</summary>
    public static readonly bool SchedulerGraphBuildActive;
    /// <summary>Combined gate for the Scheduler:Graph:Rebuild span (DAG rebuild after a schema/system change).</summary>
    public static readonly bool SchedulerGraphRebuildActive;

    // Runtime subtree parents
    /// <summary>Combined gate for the Runtime tracing subtree (UoW phase, transaction lifecycle, subscription spans).</summary>
    public static readonly bool RuntimeActive;
    /// <summary>Combined gate for the Runtime:Phase subtree (unit-of-work create/flush spans).</summary>
    public static readonly bool RuntimePhaseActive;
    /// <summary>Combined gate for the Runtime:Transaction subtree (transaction lifecycle spans).</summary>
    public static readonly bool RuntimeTransactionActive;
    /// <summary>Combined gate for the Runtime:Subscription subtree (change-subscription delivery spans).</summary>
    public static readonly bool RuntimeSubscriptionActive;
    /// <summary>Combined gate for the Runtime:Subscription:Output subtree (subscriber output execute/cleanup spans).</summary>
    public static readonly bool RuntimeSubscriptionOutputActive;

    // Runtime:Phase leaves (kinds 161-162)
    /// <summary>Combined gate for the Runtime:Phase:UoWCreate span (unit-of-work creation).</summary>
    public static readonly bool RuntimePhaseUoWCreateActive;
    /// <summary>Combined gate for the Runtime:Phase:UoWFlush span (unit-of-work flush).</summary>
    public static readonly bool RuntimePhaseUoWFlushActive;

    // Runtime:Transaction leaves (kind 163)
    /// <summary>Combined gate for the Runtime:Transaction:Lifecycle span.</summary>
    public static readonly bool RuntimeTransactionLifecycleActive;

    // Runtime:Subscription leaves (Phase 4: kind 164; Phase 9: kinds 235-240)
    /// <summary>Combined gate for the Runtime:Subscription:Output:Execute span (running a subscriber's output callback).</summary>
    public static readonly bool RuntimeSubscriptionOutputExecuteActive;

    // Phase 9 (#287) Subscription depth leaves
    /// <summary>Combined gate for the Runtime:Subscription:Subscriber span (per-subscriber delivery).</summary>
    public static readonly bool RuntimeSubscriptionSubscriberActive;
    /// <summary>Combined gate for the Runtime:Subscription:Delta:Build span (building a change delta).</summary>
    public static readonly bool RuntimeSubscriptionDeltaBuildActive;
    /// <summary>Combined gate for the Runtime:Subscription:Delta:Serialize span (serializing a change delta).</summary>
    public static readonly bool RuntimeSubscriptionDeltaSerializeActive;
    /// <summary>Combined gate for the Runtime:Subscription:Transition:BeginSync span.</summary>
    public static readonly bool RuntimeSubscriptionTransitionBeginSyncActive;
    /// <summary>Combined gate for the Runtime:Subscription:Output:Cleanup span.</summary>
    public static readonly bool RuntimeSubscriptionOutputCleanupActive;
    /// <summary>Combined gate for the Runtime:Subscription:Delta:DirtyBitmapSupplement span.</summary>
    public static readonly bool RuntimeSubscriptionDeltaDirtyBitmapSupplementActive;

    // Runtime:WriteTickFence subtree — per-table (251-253) + per-archetype cluster (254-256) fence spans
    /// <summary>Combined gate for the Runtime:WriteTickFence subtree (per-table and per-archetype cluster write-fence spans).</summary>
    public static readonly bool RuntimeWriteTickFenceActive;
    /// <summary>Combined gate for the Runtime:WriteTickFence:Table span (per-table fence cost).</summary>
    public static readonly bool RuntimeWriteTickFenceTableActive;
    /// <summary>Combined gate for the Runtime:WriteTickFence:Shadow span (shadow-copy portion of a table fence).</summary>
    public static readonly bool RuntimeWriteTickFenceShadowActive;
    /// <summary>Combined gate for the Runtime:WriteTickFence:Spatial span (spatial-index portion of a table fence).</summary>
    public static readonly bool RuntimeWriteTickFenceSpatialActive;
    /// <summary>Combined gate for the Runtime:WriteTickFence:Cluster span (per-archetype cluster fence cost).</summary>
    public static readonly bool RuntimeWriteTickFenceClusterActive;
    /// <summary>Combined gate for the Runtime:WriteTickFence:Cluster:Shadow span.</summary>
    public static readonly bool RuntimeWriteTickFenceClusterShadowActive;
    /// <summary>Combined gate for the Runtime:WriteTickFence:Cluster:Spatial span.</summary>
    public static readonly bool RuntimeWriteTickFenceClusterSpatialActive;

    /// <summary>
    /// Whether OS thread scheduling tracing is requested by configuration. Reads from
    /// <c>Typhon:Profiler:Runtime:ThreadScheduling:Enabled</c>. When enabled (Windows-only),
    /// <c>EtwSchedulingPump</c> opens the NT Kernel Logger and emits one <see cref="Typhon.Profiler.TraceEventKind.ThreadContextSwitch"/> record per on-CPU
    /// slice for every Typhon-registered OS thread. Records carry duration + wait reason so the Workbench can render off-CPU gaps with their cause overlaid
    /// on the affected thread's lane.
    /// </summary>
    /// <remarks>
    /// <b>Privileged operation:</b> opening the NT Kernel Logger requires Administrator or Performance Log Users membership. The pump catches and logs
    /// UnauthorizedAccessException without crashing — operators see a one-time warning that scheduling data won't be available for this session.
    /// <b>Singleton:</b> only one process per machine can own the NT Kernel Logger;
    /// PerfView/WPR/xperf will collide and surface a clear error.
    /// </remarks>
    public static readonly bool RuntimeThreadSchedulingActive;

    // ═══════════════════════════════════════════════════════════════════════════
    // STORAGE & MEMORY TRACING (Phase 5 — see 05-storage-memory.md)
    // ═══════════════════════════════════════════════════════════════════════════
    // Storage gets a deeper subtree for the new IDs 165-171; existing kinds 50-59
    // remain controlled by the per-kind suppression list (and the new
    // CompletionThresholdMs knob for 56/57/58).
    // Memory:AlignmentWaste (kind 172) is the only Memory event flag.

    // Storage subtree parents
    /// <summary>Combined gate for the Storage tracing subtree (page-cache, segment, file-handle, occupancy-map spans).</summary>
    public static readonly bool StorageActive;
    /// <summary>Combined gate for the Storage:PageCache subtree.</summary>
    public static readonly bool StoragePageCacheActive;
    /// <summary>Combined gate for the Storage:Segment subtree (segment create/grow/load spans).</summary>
    public static readonly bool StorageSegmentActive;
    /// <summary>Combined gate for the Storage:ChunkSegment subtree.</summary>
    public static readonly bool StorageChunkSegmentActive;
    /// <summary>Combined gate for the Storage:FileHandle span (OS file-handle lifecycle).</summary>
    public static readonly bool StorageFileHandleActive;
    /// <summary>Combined gate for the Storage:OccupancyMap subtree.</summary>
    public static readonly bool StorageOccupancyMapActive;

    // Storage leaves (kinds 165-171)
    /// <summary>Combined gate for the Storage:PageCache:DirtyWalk span (walk of dirty pages during checkpoint).</summary>
    public static readonly bool StoragePageCacheDirtyWalkActive;
    /// <summary>Combined gate for the Storage:Segment:Create span.</summary>
    public static readonly bool StorageSegmentCreateActive;
    /// <summary>Combined gate for the Storage:Segment:Grow span.</summary>
    public static readonly bool StorageSegmentGrowActive;
    /// <summary>Combined gate for the Storage:Segment:Load span.</summary>
    public static readonly bool StorageSegmentLoadActive;
    /// <summary>Combined gate for the Storage:ChunkSegment:Grow span.</summary>
    public static readonly bool StorageChunkSegmentGrowActive;
    /// <summary>Combined gate for the Storage:FileHandle span (OS file-handle open/close).</summary>
    public static readonly bool StorageFileHandleEnabledActive;
    /// <summary>Combined gate for the Storage:OccupancyMap:Grow span.</summary>
    public static readonly bool StorageOccupancyMapGrowActive;

    /// <summary>
    /// Producer-side duration threshold (ms) for kinds 56/57/58 (PageCache:DiskRead/Write/Flush Completed).
    /// When &gt; 0 the emit path skips records whose duration is shorter than the threshold; when 0 it
    /// matches today's behaviour (always emit when un-suppressed). Default: 1 ms.
    /// </summary>
    public static readonly int StoragePageCacheCompletionThresholdMs;

    // Memory subtree parents
    /// <summary>Combined gate for the Memory tracing subtree.</summary>
    public static readonly bool MemoryActive;

    // Memory leaves (kind 172)
    /// <summary>Combined gate for the Memory:AlignmentWaste span (bytes lost to alignment padding on an allocation).</summary>
    public static readonly bool MemoryAlignmentWasteActive;

    // ═══════════════════════════════════════════════════════════════════════════
    // DATA PLANE TRACING (Phase 6 — see 06-data-plane.md)
    // ═══════════════════════════════════════════════════════════════════════════
    // Data:Transaction / Data:MVCC / Data:Index:BTree subtrees. All default off; high-freq
    // leaves (Prepare, ChainWalk, Search, Revalidate, NodeCow) are also added to the per-kind
    // suppression list so that flipping the parent on doesn't drown the ring in events.

    // Data subtree parents
    /// <summary>Combined gate for the Data-plane tracing subtree (transaction, MVCC, B+Tree index spans).</summary>
    public static readonly bool DataActive;
    /// <summary>Combined gate for the Data:Transaction subtree (init/prepare/validate/conflict/cleanup spans).</summary>
    public static readonly bool DataTransactionActive;
    /// <summary>Combined gate for the Data:MVCC subtree (version chain-walk and cleanup spans).</summary>
    public static readonly bool DataMvccActive;
    /// <summary>Combined gate for the Data:Index subtree.</summary>
    public static readonly bool DataIndexActive;
    /// <summary>Combined gate for the Data:Index:BTree subtree (B+Tree search/scan/mutation spans).</summary>
    public static readonly bool DataIndexBTreeActive;

    // Data:Transaction leaves (kinds 173-177)
    /// <summary>Combined gate for the Data:Transaction:Init span.</summary>
    public static readonly bool DataTransactionInitActive;
    /// <summary>Combined gate for the Data:Transaction:Prepare span.</summary>
    public static readonly bool DataTransactionPrepareActive;
    /// <summary>Combined gate for the Data:Transaction:Validate span (commit-time conflict validation).</summary>
    public static readonly bool DataTransactionValidateActive;
    /// <summary>Combined gate for the Data:Transaction:Conflict span (write-write conflict detected).</summary>
    public static readonly bool DataTransactionConflictActive;
    /// <summary>Combined gate for the Data:Transaction:Cleanup span.</summary>
    public static readonly bool DataTransactionCleanupActive;

    // Data:MVCC leaves (kinds 178-179)
    /// <summary>Combined gate for the Data:MVCC:ChainWalk span (walking a version chain to the visible revision).</summary>
    public static readonly bool DataMvccChainWalkActive;
    /// <summary>Combined gate for the Data:MVCC:VersionCleanup span (reclaiming obsolete versions).</summary>
    public static readonly bool DataMvccVersionCleanupActive;

    // Data:Index:BTree leaves (kinds 180-186)
    /// <summary>Combined gate for the Data:Index:BTree:Search span (point lookup).</summary>
    public static readonly bool DataIndexBTreeSearchActive;
    /// <summary>Combined gate for the Data:Index:BTree:RangeScan span.</summary>
    public static readonly bool DataIndexBTreeRangeScanActive;
    /// <summary>Combined gate for the Data:Index:BTree:RangeScan:Revalidate span (optimistic scan revalidation after a version change).</summary>
    public static readonly bool DataIndexBTreeRangeScanRevalidateActive;
    /// <summary>Combined gate for the Data:Index:BTree:RebalanceFallback span (fallback to a pessimistic rebalance).</summary>
    public static readonly bool DataIndexBTreeRebalanceFallbackActive;
    /// <summary>Combined gate for the Data:Index:BTree:BulkInsert span.</summary>
    public static readonly bool DataIndexBTreeBulkInsertActive;
    /// <summary>Combined gate for the Data:Index:BTree:Root span (root split/replace).</summary>
    public static readonly bool DataIndexBTreeRootActive;
    /// <summary>Combined gate for the Data:Index:BTree:NodeCow span (copy-on-write of a B+Tree node).</summary>
    public static readonly bool DataIndexBTreeNodeCowActive;

    // ═══════════════════════════════════════════════════════════════════════════
    // QUERY / ECS:Query / ECS:View TRACING (Phase 7 — see 07-query-ecs-view.md)
    // ═══════════════════════════════════════════════════════════════════════════

    // Query subtree parents
    /// <summary>Combined gate for the Query tracing subtree (parse/plan/execute spans).</summary>
    public static readonly bool QueryActive;
    /// <summary>Combined gate for the Query:Parse subtree (query-string parsing spans).</summary>
    public static readonly bool QueryParseActive;
    /// <summary>Combined gate for the Query:Plan subtree (planning/optimization spans).</summary>
    public static readonly bool QueryPlanActive;
    /// <summary>Combined gate for the Query:Execute subtree (execution spans).</summary>
    public static readonly bool QueryExecuteActive;

    // Query leaves (kinds 187-198)
    /// <summary>Combined gate for the Query:Parse leaf span (same gate as <see cref="QueryParseActive"/>).</summary>
    public static readonly bool QueryParseEnabledActive;
    /// <summary>Combined gate for the Query:Parse:DNF span (disjunctive-normal-form conversion).</summary>
    public static readonly bool QueryParseDnfActive;
    /// <summary>Combined gate for the Query:Plan leaf span (same gate as <see cref="QueryPlanActive"/>).</summary>
    public static readonly bool QueryPlanEnabledActive;
    /// <summary>Combined gate for the Query:Estimate span (cardinality/cost estimation).</summary>
    public static readonly bool QueryEstimateActive;
    /// <summary>Combined gate for the Query:Plan:PrimarySelect span (primary access-path selection).</summary>
    public static readonly bool QueryPlanPrimarySelectActive;
    /// <summary>Combined gate for the Query:Plan:Sort span (sort planning).</summary>
    public static readonly bool QueryPlanSortActive;
    /// <summary>Combined gate for the Query:Execute:IndexScan span.</summary>
    public static readonly bool QueryExecuteIndexScanActive;
    /// <summary>Combined gate for the Query:Execute:Iterate span (result iteration).</summary>
    public static readonly bool QueryExecuteIterateActive;
    /// <summary>Combined gate for the Query:Execute:Filter span (residual predicate filtering).</summary>
    public static readonly bool QueryExecuteFilterActive;
    /// <summary>Combined gate for the Query:Execute:Pagination span (skip/take pagination).</summary>
    public static readonly bool QueryExecutePaginationActive;
    /// <summary>Combined gate for the Query:Execute:StorageMode span (per-storage-mode execution path).</summary>
    public static readonly bool QueryExecuteStorageModeActive;
    /// <summary>Combined gate for the Query:Count span (count-only execution).</summary>
    public static readonly bool QueryCountActive;

    // ECS subtree parents (and depth from Phase 7)
    /// <summary>Combined gate for the ECS tracing subtree (query-construction and view-maintenance spans).</summary>
    public static readonly bool EcsActive;
    /// <summary>Combined gate for the ECS:Query subtree (archetype-query construction spans).</summary>
    public static readonly bool EcsQueryActive;
    /// <summary>Combined gate for the ECS:View subtree (materialized-view refresh spans).</summary>
    public static readonly bool EcsViewActive;

    // ECS:Query depth leaves (kinds 199-203)
    /// <summary>Combined gate for the ECS:Query:Construct span (query object construction).</summary>
    public static readonly bool EcsQueryConstructActive;
    /// <summary>Combined gate for the ECS:Query:MaskAnd span (component-mask AND intersection).</summary>
    public static readonly bool EcsQueryMaskAndActive;
    /// <summary>Combined gate for the ECS:Query:SubtreeExpand span (archetype-subtree expansion).</summary>
    public static readonly bool EcsQuerySubtreeExpandActive;
    /// <summary>Combined gate for the ECS:Query:Constraint:Enabled span (enabled-constraint evaluation).</summary>
    public static readonly bool EcsQueryConstraintEnabledActive;
    /// <summary>Combined gate for the ECS:Query:Spatial:Attach span (attaching a spatial constraint to a query).</summary>
    public static readonly bool EcsQuerySpatialAttachActive;

    // ECS:View depth leaves (kinds 204-213)
    /// <summary>Combined gate for the ECS:View:RefreshPull span (pull-based view refresh).</summary>
    public static readonly bool EcsViewRefreshPullActive;
    /// <summary>Combined gate for the ECS:View:IncrementalDrain span (draining buffered deltas into a view).</summary>
    public static readonly bool EcsViewIncrementalDrainActive;
    /// <summary>Combined gate for the ECS:View:DeltaBuffer:Overflow span (delta buffer overflowed, forcing a full refresh).</summary>
    public static readonly bool EcsViewDeltaBufferOverflowActive;
    /// <summary>Combined gate for the ECS:View:ProcessEntry span (processing one view entry).</summary>
    public static readonly bool EcsViewProcessEntryActive;
    /// <summary>Combined gate for the ECS:View:ProcessEntryOr span (processing an OR-clause view entry).</summary>
    public static readonly bool EcsViewProcessEntryOrActive;
    /// <summary>Combined gate for the ECS:View:RefreshFull span (full view rebuild).</summary>
    public static readonly bool EcsViewRefreshFullActive;
    /// <summary>Combined gate for the ECS:View:RefreshFullOr span (full rebuild of an OR-clause view).</summary>
    public static readonly bool EcsViewRefreshFullOrActive;
    /// <summary>Combined gate for the ECS:View:Registry:Register span (view registration).</summary>
    public static readonly bool EcsViewRegistryRegisterActive;
    /// <summary>Combined gate for the ECS:View:Registry:Deregister span (view deregistration).</summary>
    public static readonly bool EcsViewRegistryDeregisterActive;
    /// <summary>Combined gate for the ECS:View:DeltaCache:Miss span (delta-cache miss).</summary>
    public static readonly bool EcsViewDeltaCacheMissActive;

    // ═══════════════════════════════════════════════════════════════════════════
    // DURABILITY TRACING (Phase 8 — see 08-durability.md)
    // ═══════════════════════════════════════════════════════════════════════════

    // Durability subtree parents
    /// <summary>Combined gate for the Durability tracing subtree (WAL, checkpoint, recovery, UoW spans).</summary>
    public static readonly bool DurabilityActive;
    /// <summary>Combined gate for the Durability:WAL subtree (write-ahead-log spans).</summary>
    public static readonly bool DurabilityWalActive;
    /// <summary>Combined gate for the Durability:Checkpoint subtree (checkpoint spans).</summary>
    public static readonly bool DurabilityCheckpointActive;
    /// <summary>Combined gate for the Durability:Recovery subtree (crash-recovery spans).</summary>
    public static readonly bool DurabilityRecoveryActive;
    /// <summary>Combined gate for the Durability:UoW subtree (unit-of-work durability spans).</summary>
    public static readonly bool DurabilityUowActive;

    // Durability:WAL leaves (kinds 214-221)
    /// <summary>Combined gate for the Durability:WAL:QueueDrain span (draining the commit queue to the WAL writer).</summary>
    public static readonly bool DurabilityWalQueueDrainActive;
    /// <summary>Combined gate for the Durability:WAL:OsWrite span (OS write of a WAL buffer).</summary>
    public static readonly bool DurabilityWalOsWriteActive;
    /// <summary>Combined gate for the Durability:WAL:Signal span (post-flush signal to waiting committers).</summary>
    public static readonly bool DurabilityWalSignalActive;
    /// <summary>Combined gate for the Durability:WAL:GroupCommit span (group-commit batch).</summary>
    public static readonly bool DurabilityWalGroupCommitActive;
    /// <summary>Combined gate for the Durability:WAL:Queue span (commit-queue enqueue).</summary>
    public static readonly bool DurabilityWalQueueActive;
    /// <summary>Combined gate for the Durability:WAL:Buffer span (WAL buffer fill/rotate).</summary>
    public static readonly bool DurabilityWalBufferActive;
    /// <summary>Combined gate for the Durability:WAL:Frame span (per-frame WAL encoding).</summary>
    public static readonly bool DurabilityWalFrameActive;
    /// <summary>Combined gate for the Durability:WAL:Backpressure span (commit throttled by WAL backpressure).</summary>
    public static readonly bool DurabilityWalBackpressureActive;

    // Durability:Checkpoint depth (kinds 222-224)
    /// <summary>Combined gate for the Durability:Checkpoint:WriteBatch span (writing a batch of dirty pages).</summary>
    public static readonly bool DurabilityCheckpointWriteBatchActive;
    /// <summary>Combined gate for the Durability:Checkpoint:Backpressure span (checkpoint throttled by backpressure).</summary>
    public static readonly bool DurabilityCheckpointBackpressureActive;
    /// <summary>Combined gate for the Durability:Checkpoint:Sleep span (checkpoint pacing sleep).</summary>
    public static readonly bool DurabilityCheckpointSleepActive;

    // Durability:Recovery leaves (kinds 225-232)
    /// <summary>Combined gate for the Durability:Recovery:Start span (recovery session start).</summary>
    public static readonly bool DurabilityRecoveryStartActive;
    /// <summary>Combined gate for the Durability:Recovery:Discover span (discovering WAL segments to replay).</summary>
    public static readonly bool DurabilityRecoveryDiscoverActive;
    /// <summary>Combined gate for the Durability:Recovery:Segment span (replaying one WAL segment).</summary>
    public static readonly bool DurabilityRecoverySegmentActive;
    /// <summary>Combined gate for the Durability:Recovery:Record span (applying one WAL record).</summary>
    public static readonly bool DurabilityRecoveryRecordActive;
    /// <summary>Combined gate for the Durability:Recovery:Redo span (redo pass).</summary>
    public static readonly bool DurabilityRecoveryRedoActive;
    /// <summary>Combined gate for the Durability:Recovery:Undo span (undo of uncommitted effects).</summary>
    public static readonly bool DurabilityRecoveryUndoActive;
    /// <summary>Combined gate for the Durability:Recovery:TickFence span (replaying a write-tick fence boundary).</summary>
    public static readonly bool DurabilityRecoveryTickFenceActive;

    // Durability:UoW leaves (kinds 233-234)
    /// <summary>Combined gate for the Durability:UoW:State span (unit-of-work state transition).</summary>
    public static readonly bool DurabilityUowStateActive;
    /// <summary>Combined gate for the Durability:UoW:Deadline span (unit-of-work deadline check).</summary>
    public static readonly bool DurabilityUowDeadlineActive;

    // ═══════════════════════════════════════════════════════════════════════════
    // CONFIGURATION SOURCE TRACKING (for diagnostics)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The configuration file path that was loaded, or null if using defaults/env vars only.
    /// </summary>
    public static readonly string LoadedConfigurationFile;

    /// <summary>
    /// The merged configuration built by <see cref="BuildConfiguration"/> — <c>typhon.telemetry.json</c> (current dir then assembly dir) overlaid with
    /// environment variables. Exposed so the profiler bootstrap can resolve <see cref="ProfilerLaunchConfig.FromConfiguration"/> from the same source without
    /// re-running the multi-location probe.
    /// </summary>
    internal static readonly IConfiguration Configuration;

    /// <summary>
    /// The profiler launch config resolved from the file/environment layer only — <c>typhon.telemetry.json</c> plus <c>TYPHON__PROFILER__*</c> variables. The
    /// process command line is deliberately NOT read here: a host parses its own arguments and injects the launch config through DI (<c>AddTyphonProfiler</c>),
    /// which <see cref="Typhon.Engine.internals.ProfilerBootstrap"/> merges on top of this. An active value here also turns <see cref="ProfilerActive"/> on — declaring an output
    /// channel in config enables the profiler.
    /// </summary>
    internal static readonly ProfilerLaunchConfig ProfilerLaunch;

    // ═══════════════════════════════════════════════════════════════════════════
    // STATIC CONSTRUCTOR - Runs once on first access to any static member
    // ═══════════════════════════════════════════════════════════════════════════

    static TelemetryConfig()
    {
        var (config, configPath) = BuildConfiguration();
        LoadedConfigurationFile = configPath;
        Configuration = config;

        // Resolve the profiler launch config from the file/environment layer only. The command line is NOT read here —
        // a host parses its own args and injects the launch config through DI (AddTyphonProfiler); see ProfilerBootstrap.
        ProfilerLaunch = ProfilerLaunchConfig.FromConfiguration(config);

        // ─── Master switch ─────────────────────────────────────────────────────
        // Typhon:Profiler:Enabled is the master. A Trace/Live key in config also implies "enabled" — declaring an
        // output channel turns the profiler on. The producer gate is a JIT-folded static, so it must resolve from this
        // ambient config here, before hot paths compile — it cannot come from DI, which is built later.
        Enabled = ReadBool(config, "Typhon:Profiler:Enabled", false) || ProfilerLaunch.IsActive;
        ProfilerEnabled = Enabled;
        ProfilerActive = Enabled;

        // ─── Profiler children (live) ──────────────────────────────────────────
        ProfilerGcTracingEnabled = ReadBool(config, "Typhon:Profiler:GcTracing:Enabled", false);
        ProfilerGcTracingActive = ProfilerActive && ProfilerGcTracingEnabled;

        ProfilerMemoryAllocationsEnabled = ReadBool(config, "Typhon:Profiler:MemoryAllocations:Enabled", false);
        ProfilerMemoryAllocationsActive = ProfilerActive && ProfilerMemoryAllocationsEnabled;

        ProfilerCpuSamplingEnabled = ReadBool(config, "Typhon:Profiler:CpuSampling:Enabled", false);
        ProfilerCpuSamplingActive = ProfilerActive && ProfilerCpuSamplingEnabled;

        ProfilerGaugesEnabled = ReadBool(config, "Typhon:Profiler:Gauges:Enabled", false);
        ProfilerGaugesActive = ProfilerActive && ProfilerGaugesEnabled;

        // ─── Scheduler (live) ──────────────────────────────────────────────────
        SchedulerEnabled = ReadBool(config, "Typhon:Profiler:Scheduler:Enabled", false);
        SchedulerActive = Enabled && SchedulerEnabled;

        SchedulerTrackTransitionLatency = ReadBool(config, "Typhon:Profiler:Scheduler:Gauges:TransitionLatency:Enabled", true);
        SchedulerTrackWorkerUtilization = ReadBool(config, "Typhon:Profiler:Scheduler:Gauges:WorkerUtilization:Enabled", true);
        SchedulerTrackStragglerGap = ReadBool(config, "Typhon:Profiler:Scheduler:Gauges:StragglerGap:Enabled", true);
        SchedulerArchetypeTouchesActive = ReadBool(config, "Typhon:Profiler:Scheduler:ArchetypeTouches:Enabled", true);

        // ─── Concurrency subtree (Phase 1 + Phase 2 final shape) ───────────────
        // Greenfield namespace under Typhon:Profiler:Concurrency:*.
        // Resolver implements parent-implies-children: Concurrency:Enabled = false makes all leaves false even if
        // their own Enabled key is true; Concurrency:Enabled = true (with master Profiler on) flips everything on
        // by default, with per-leaf overrides.
        var concurrencyTree = new Node("Concurrency",
        [
            new Node("AccessControl",
            [
                new Node("SharedAcquire"),
                new Node("SharedRelease"),
                new Node("ExclusiveAcquire"),
                new Node("ExclusiveRelease"),
                new Node("Promotion"),
                new Node("Contention"),
            ]),
            new Node("AccessControlSmall",
            [
                new Node("SharedAcquire"),
                new Node("SharedRelease"),
                new Node("ExclusiveAcquire"),
                new Node("ExclusiveRelease"),
                new Node("Contention"),
            ]),
            new Node("ResourceAccessControl",
            [
                new Node("Accessing"),
                new Node("Modify"),
                new Node("Destroy"),
                new Node("ModifyPromotion"),
                new Node("Contention"),
            ]),
            new Node("Epoch",
            [
                new Node("ScopeEnter"),
                new Node("ScopeExit"),
                new Node("Advance"),
                new Node("Refresh"),
                new Node("SlotClaim"),
                new Node("SlotReclaim"),
            ]),
            new Node("AdaptiveWaiter",
            [
                new Node("YieldOrSleep"),
            ]),
            new Node("OlcLatch",
            [
                new Node("WriteLockAttempt"),
                new Node("WriteUnlock"),
                new Node("MarkObsolete"),
                new Node("ValidationFail"),
            ]),
        ]);
        var concurrencyRootExplicit = ReadBool(config, "Typhon:Profiler:Concurrency:Enabled", false);
        var concurrencyRootEffective = Enabled && concurrencyRootExplicit;
        var concurrencyMap = TelemetryConfigResolver.Resolve(
            concurrencyTree, concurrencyRootEffective, config, "Typhon:Profiler");

        // Root + sub-tree parents
        ConcurrencyActive                          = concurrencyMap["Concurrency"];
        ConcurrencyAccessControlActive             = concurrencyMap["Concurrency:AccessControl"];
        ConcurrencyAccessControlSmallActive        = concurrencyMap["Concurrency:AccessControlSmall"];
        ConcurrencyResourceAccessControlActive     = concurrencyMap["Concurrency:ResourceAccessControl"];
        ConcurrencyEpochActive                     = concurrencyMap["Concurrency:Epoch"];
        ConcurrencyAdaptiveWaiterActive            = concurrencyMap["Concurrency:AdaptiveWaiter"];
        ConcurrencyOlcLatchActive                  = concurrencyMap["Concurrency:OlcLatch"];

        // AccessControl leaves
        ConcurrencyAccessControlSharedAcquireActive    = concurrencyMap["Concurrency:AccessControl:SharedAcquire"];
        ConcurrencyAccessControlSharedReleaseActive    = concurrencyMap["Concurrency:AccessControl:SharedRelease"];
        ConcurrencyAccessControlExclusiveAcquireActive = concurrencyMap["Concurrency:AccessControl:ExclusiveAcquire"];
        ConcurrencyAccessControlExclusiveReleaseActive = concurrencyMap["Concurrency:AccessControl:ExclusiveRelease"];
        ConcurrencyAccessControlPromotionActive        = concurrencyMap["Concurrency:AccessControl:Promotion"];
        ConcurrencyAccessControlContentionActive       = concurrencyMap["Concurrency:AccessControl:Contention"];

        // AccessControlSmall leaves
        ConcurrencyAccessControlSmallSharedAcquireActive    = concurrencyMap["Concurrency:AccessControlSmall:SharedAcquire"];
        ConcurrencyAccessControlSmallSharedReleaseActive    = concurrencyMap["Concurrency:AccessControlSmall:SharedRelease"];
        ConcurrencyAccessControlSmallExclusiveAcquireActive = concurrencyMap["Concurrency:AccessControlSmall:ExclusiveAcquire"];
        ConcurrencyAccessControlSmallExclusiveReleaseActive = concurrencyMap["Concurrency:AccessControlSmall:ExclusiveRelease"];
        ConcurrencyAccessControlSmallContentionActive       = concurrencyMap["Concurrency:AccessControlSmall:Contention"];

        // ResourceAccessControl leaves
        ConcurrencyResourceAccessControlAccessingActive       = concurrencyMap["Concurrency:ResourceAccessControl:Accessing"];
        ConcurrencyResourceAccessControlModifyActive          = concurrencyMap["Concurrency:ResourceAccessControl:Modify"];
        ConcurrencyResourceAccessControlDestroyActive         = concurrencyMap["Concurrency:ResourceAccessControl:Destroy"];
        ConcurrencyResourceAccessControlModifyPromotionActive = concurrencyMap["Concurrency:ResourceAccessControl:ModifyPromotion"];
        ConcurrencyResourceAccessControlContentionActive      = concurrencyMap["Concurrency:ResourceAccessControl:Contention"];

        // Epoch leaves
        ConcurrencyEpochScopeEnterActive  = concurrencyMap["Concurrency:Epoch:ScopeEnter"];
        ConcurrencyEpochScopeExitActive   = concurrencyMap["Concurrency:Epoch:ScopeExit"];
        ConcurrencyEpochAdvanceActive     = concurrencyMap["Concurrency:Epoch:Advance"];
        ConcurrencyEpochRefreshActive     = concurrencyMap["Concurrency:Epoch:Refresh"];
        ConcurrencyEpochSlotClaimActive   = concurrencyMap["Concurrency:Epoch:SlotClaim"];
        ConcurrencyEpochSlotReclaimActive = concurrencyMap["Concurrency:Epoch:SlotReclaim"];

        // AdaptiveWaiter leaves
        ConcurrencyAdaptiveWaiterYieldOrSleepActive = concurrencyMap["Concurrency:AdaptiveWaiter:YieldOrSleep"];

        // OlcLatch leaves
        ConcurrencyOlcLatchWriteLockAttemptActive = concurrencyMap["Concurrency:OlcLatch:WriteLockAttempt"];
        ConcurrencyOlcLatchWriteUnlockActive      = concurrencyMap["Concurrency:OlcLatch:WriteUnlock"];
        ConcurrencyOlcLatchMarkObsoleteActive     = concurrencyMap["Concurrency:OlcLatch:MarkObsolete"];
        ConcurrencyOlcLatchValidationFailActive   = concurrencyMap["Concurrency:OlcLatch:ValidationFail"];

        // ─── Spatial subtree (Phase 3 final shape) ─────────────────────────────
        // Greenfield namespace under Typhon:Profiler:Spatial:*.
        // Default-off everywhere; operators flip Profiler:Spatial:Enabled = true to opt into the subtree.
        var spatialTree = new Node("Spatial",
        [
            new Node("Query",
            [
                new Node("Aabb"),
                new Node("Radius"),
                new Node("Ray"),
                new Node("Frustum"),
                new Node("Knn"),
                new Node("Count"),
            ]),
            new Node("RTree",
            [
                new Node("Insert"),
                new Node("Remove"),
                new Node("NodeSplit"),
                new Node("BulkLoad"),
            ]),
            new Node("Grid",
            [
                new Node("CellTierChange"),
                new Node("OccupancyChange"),
                new Node("ClusterCellAssign"),
            ]),
            new Node("Cell",
            [
                new Node("Index",
                [
                    new Node("Add"),
                    new Node("Update"),
                    new Node("Remove"),
                ]),
            ]),
            new Node("ClusterMigration",
            [
                new Node("Detect"),
                new Node("Queue"),
                new Node("Execute"),
                new Node("Hysteresis"),
            ]),
            new Node("TierIndex",
            [
                new Node("Rebuild"),
                new Node("VersionSkip"),
            ]),
            new Node("Maintain",
            [
                new Node("Insert"),
                new Node("UpdateSlowPath"),
                new Node("AabbValidate"),
                new Node("BackPointerWrite"),
            ]),
            new Node("Trigger",
            [
                new Node("Region"),
                new Node("Eval"),
                new Node("Occupant",
                [
                    new Node("Diff"),
                ]),
                new Node("Cache",
                [
                    new Node("Invalidate"),
                ]),
            ]),
        ]);
        var spatialRootExplicit = ReadBool(config, "Typhon:Profiler:Spatial:Enabled", false);
        var spatialRootEffective = Enabled && spatialRootExplicit;
        var spatialMap = TelemetryConfigResolver.Resolve(
            spatialTree, spatialRootEffective, config, "Typhon:Profiler");

        // Root + sub-tree parents
        SpatialActive                   = spatialMap["Spatial"];
        SpatialQueryActive              = spatialMap["Spatial:Query"];
        SpatialRTreeActive              = spatialMap["Spatial:RTree"];
        SpatialGridActive               = spatialMap["Spatial:Grid"];
        SpatialCellActive               = spatialMap["Spatial:Cell"];
        SpatialCellIndexActive          = spatialMap["Spatial:Cell:Index"];
        SpatialClusterMigrationActive   = spatialMap["Spatial:ClusterMigration"];
        SpatialTierIndexActive          = spatialMap["Spatial:TierIndex"];
        SpatialMaintainActive           = spatialMap["Spatial:Maintain"];
        SpatialTriggerActive            = spatialMap["Spatial:Trigger"];
        SpatialTriggerOccupantActive    = spatialMap["Spatial:Trigger:Occupant"];
        SpatialTriggerCacheActive       = spatialMap["Spatial:Trigger:Cache"];

        // Query leaves
        SpatialQueryAabbActive    = spatialMap["Spatial:Query:Aabb"];
        SpatialQueryRadiusActive  = spatialMap["Spatial:Query:Radius"];
        SpatialQueryRayActive     = spatialMap["Spatial:Query:Ray"];
        SpatialQueryFrustumActive = spatialMap["Spatial:Query:Frustum"];
        SpatialQueryKnnActive     = spatialMap["Spatial:Query:Knn"];
        SpatialQueryCountActive   = spatialMap["Spatial:Query:Count"];

        // RTree structural leaves
        SpatialRTreeInsertActive    = spatialMap["Spatial:RTree:Insert"];
        SpatialRTreeRemoveActive    = spatialMap["Spatial:RTree:Remove"];
        SpatialRTreeNodeSplitActive = spatialMap["Spatial:RTree:NodeSplit"];
        SpatialRTreeBulkLoadActive  = spatialMap["Spatial:RTree:BulkLoad"];

        // Grid leaves
        SpatialGridCellTierChangeActive    = spatialMap["Spatial:Grid:CellTierChange"];
        SpatialGridOccupancyChangeActive   = spatialMap["Spatial:Grid:OccupancyChange"];
        SpatialGridClusterCellAssignActive = spatialMap["Spatial:Grid:ClusterCellAssign"];

        // Cell:Index leaves
        SpatialCellIndexAddActive    = spatialMap["Spatial:Cell:Index:Add"];
        SpatialCellIndexUpdateActive = spatialMap["Spatial:Cell:Index:Update"];
        SpatialCellIndexRemoveActive = spatialMap["Spatial:Cell:Index:Remove"];

        // ClusterMigration leaves
        SpatialClusterMigrationDetectActive     = spatialMap["Spatial:ClusterMigration:Detect"];
        SpatialClusterMigrationQueueActive      = spatialMap["Spatial:ClusterMigration:Queue"];
        SpatialClusterMigrationExecuteActive    = spatialMap["Spatial:ClusterMigration:Execute"];
        SpatialClusterMigrationHysteresisActive = spatialMap["Spatial:ClusterMigration:Hysteresis"];

        // TierIndex leaves
        SpatialTierIndexRebuildActive     = spatialMap["Spatial:TierIndex:Rebuild"];
        SpatialTierIndexVersionSkipActive = spatialMap["Spatial:TierIndex:VersionSkip"];

        // Maintain leaves
        SpatialMaintainInsertActive           = spatialMap["Spatial:Maintain:Insert"];
        SpatialMaintainUpdateSlowPathActive   = spatialMap["Spatial:Maintain:UpdateSlowPath"];
        SpatialMaintainAabbValidateActive     = spatialMap["Spatial:Maintain:AabbValidate"];
        SpatialMaintainBackPointerWriteActive = spatialMap["Spatial:Maintain:BackPointerWrite"];

        // Trigger leaves
        SpatialTriggerRegionActive          = spatialMap["Spatial:Trigger:Region"];
        SpatialTriggerEvalActive            = spatialMap["Spatial:Trigger:Eval"];
        SpatialTriggerOccupantDiffActive    = spatialMap["Spatial:Trigger:Occupant:Diff"];
        SpatialTriggerCacheInvalidateActive = spatialMap["Spatial:Trigger:Cache:Invalidate"];

        // ─── Scheduler depth + Runtime subtrees (Phase 4 final shape) ──────────
        // The existing SchedulerActive master flag (read above) stays as-is. Phase 4 adds the deeper tree:
        // System / Worker / Dispense / Dependency / Overload / Graph. These default off; operators flip
        // Profiler:Scheduler:System:Enabled = true (etc.) to opt in.
        var schedulerDepthTree = new Node("Scheduler",
        [
            new Node("System",
            [
                new Node("StartExecution"),
                new Node("Completion"),
                new Node("QueueWait"),
                new Node("SingleThreaded"),
            ]),
            new Node("Worker",
            [
                new Node("Idle"),
                new Node("Wake"),
                new Node("BetweenTick"),
            ]),
            new Node("Dispense"),
            new Node("Dependency",
            [
                new Node("Ready"),
                new Node("FanOut"),
            ]),
            new Node("Overload",
            [
                new Node("LevelChange"),
                new Node("SystemShed"),
                new Node("TickMultiplier"),
                new Node("Detector"),
            ]),
            new Node("Metronome",
            [
                new Node("Wait"),
            ]),
            new Node("Queue",
            [
                new Node("TickEnd"),
            ]),
            new Node("Graph",
            [
                new Node("Build"),
                new Node("Rebuild"),
            ]),
        ]);
        // Effective root = master profiler && existing Scheduler enabled flag (already computed as SchedulerEnabled).
        var schedulerDepthRootEffective = Enabled && SchedulerEnabled;
        var schedulerDepthMap = TelemetryConfigResolver.Resolve(
            schedulerDepthTree, schedulerDepthRootEffective, config, "Typhon:Profiler");

        // Subtree parents
        SchedulerSystemActive     = schedulerDepthMap["Scheduler:System"];
        SchedulerWorkerActive     = schedulerDepthMap["Scheduler:Worker"];
        SchedulerDispenseActive   = schedulerDepthMap["Scheduler:Dispense"];
        SchedulerDependencyActive = schedulerDepthMap["Scheduler:Dependency"];
        SchedulerOverloadActive   = schedulerDepthMap["Scheduler:Overload"];
        SchedulerGraphActive      = schedulerDepthMap["Scheduler:Graph"];

        // System leaves
        SchedulerSystemStartExecutionActive = schedulerDepthMap["Scheduler:System:StartExecution"];
        SchedulerSystemCompletionActive     = schedulerDepthMap["Scheduler:System:Completion"];
        SchedulerSystemQueueWaitActive      = schedulerDepthMap["Scheduler:System:QueueWait"];
        SchedulerSystemSingleThreadedActive = schedulerDepthMap["Scheduler:System:SingleThreaded"];

        // Worker leaves
        SchedulerWorkerIdleActive        = schedulerDepthMap["Scheduler:Worker:Idle"];
        SchedulerWorkerWakeActive        = schedulerDepthMap["Scheduler:Worker:Wake"];
        SchedulerWorkerBetweenTickActive = schedulerDepthMap["Scheduler:Worker:BetweenTick"];

        // Dependency leaves
        SchedulerDependencyReadyActive  = schedulerDepthMap["Scheduler:Dependency:Ready"];
        SchedulerDependencyFanOutActive = schedulerDepthMap["Scheduler:Dependency:FanOut"];

        // Overload leaves
        SchedulerOverloadLevelChangeActive    = schedulerDepthMap["Scheduler:Overload:LevelChange"];
        SchedulerOverloadSystemShedActive     = schedulerDepthMap["Scheduler:Overload:SystemShed"];
        SchedulerOverloadTickMultiplierActive = schedulerDepthMap["Scheduler:Overload:TickMultiplier"];
        SchedulerOverloadDetectorActive       = schedulerDepthMap["Scheduler:Overload:Detector"];

        // Metronome subtree (issue #289 follow-up)
        SchedulerMetronomeActive     = schedulerDepthMap["Scheduler:Metronome"];
        SchedulerMetronomeWaitActive = schedulerDepthMap["Scheduler:Metronome:Wait"];

        // Queue subtree (#311 — per-tick queue depth telemetry)
        SchedulerQueueActive         = schedulerDepthMap["Scheduler:Queue"];
        SchedulerQueueTickEndActive  = schedulerDepthMap["Scheduler:Queue:TickEnd"];

        // Graph leaves
        SchedulerGraphBuildActive   = schedulerDepthMap["Scheduler:Graph:Build"];
        SchedulerGraphRebuildActive = schedulerDepthMap["Scheduler:Graph:Rebuild"];

        // ─── Runtime subtree (Phase 4 + Phase 9 depth) ─────────────────────────
        var runtimeTree = new Node("Runtime",
        [
            new Node("Phase",
            [
                new Node("UoWCreate"),
                new Node("UoWFlush"),
            ]),
            new Node("Transaction",
            [
                new Node("Lifecycle"),
            ]),
            new Node("Subscription",
            [
                new Node("Subscriber"),
                new Node("Delta",
                [
                    new Node("Build"),
                    new Node("Serialize"),
                    new Node("DirtyBitmapSupplement"),
                ]),
                new Node("Transition",
                [
                    new Node("BeginSync"),
                ]),
                new Node("Output",
                [
                    new Node("Execute"),
                    new Node("Cleanup"),
                ]),
            ]),
            // Per-table detail spans inside WriteTickFenceCore — kinds 251-253.
            // Surfaces "which table dominated the fence wall?" + Shadow/Spatial split, so the upcoming parallelize-the-fence work has data to act on.
            // Default-off; opt in via Profiler:Runtime:WriteTickFence:Enabled = true.
            new Node("WriteTickFence",
            [
                new Node("Table"),
                new Node("Shadow"),
                new Node("Spatial"),
                // Cluster-scope mirror of the above — covers WriteClusterTickFence (the per-archetype loop in DatabaseEngine.cs that does the heavy work for
                // cluster-backed archetypes like AntHill's ants).
                new Node("Cluster",
                [
                    new Node("Shadow"),
                    new Node("Spatial"),
                ]),
            ]),
            // OS thread scheduling — Windows-only, requires admin. Enables EtwSchedulingPump which observes context-switches for Typhon-registered threads and
            // emits one record per on-CPU slice (kind 254). Default is COUPLED to CpuSampling (see the coupling block after the resolve below): enabling
            // CPU sampling pulls this on too, since §8.7 sample classification needs the slices. An explicit Profiler:Runtime:ThreadScheduling:Enabled wins.
            new Node("ThreadScheduling"),
        ]);
        var runtimeRootExplicit = ReadBool(config, "Typhon:Profiler:Runtime:Enabled", false);
        var runtimeRootEffective = Enabled && runtimeRootExplicit;
        var runtimeMap = TelemetryConfigResolver.Resolve(
            runtimeTree, runtimeRootEffective, config, "Typhon:Profiler");

        RuntimeActive                   = runtimeMap["Runtime"];
        RuntimePhaseActive              = runtimeMap["Runtime:Phase"];
        RuntimeTransactionActive        = runtimeMap["Runtime:Transaction"];
        RuntimeSubscriptionActive       = runtimeMap["Runtime:Subscription"];
        RuntimeSubscriptionOutputActive = runtimeMap["Runtime:Subscription:Output"];

        RuntimePhaseUoWCreateActive          = runtimeMap["Runtime:Phase:UoWCreate"];
        RuntimePhaseUoWFlushActive           = runtimeMap["Runtime:Phase:UoWFlush"];
        RuntimeTransactionLifecycleActive    = runtimeMap["Runtime:Transaction:Lifecycle"];
        RuntimeSubscriptionOutputExecuteActive = runtimeMap["Runtime:Subscription:Output:Execute"];

        // Phase 9 — Subscription depth leaves
        RuntimeSubscriptionSubscriberActive                 = runtimeMap["Runtime:Subscription:Subscriber"];
        RuntimeSubscriptionDeltaBuildActive                 = runtimeMap["Runtime:Subscription:Delta:Build"];
        RuntimeSubscriptionDeltaSerializeActive             = runtimeMap["Runtime:Subscription:Delta:Serialize"];
        RuntimeSubscriptionDeltaDirtyBitmapSupplementActive = runtimeMap["Runtime:Subscription:Delta:DirtyBitmapSupplement"];
        RuntimeSubscriptionTransitionBeginSyncActive        = runtimeMap["Runtime:Subscription:Transition:BeginSync"];
        RuntimeSubscriptionOutputCleanupActive              = runtimeMap["Runtime:Subscription:Output:Cleanup"];

        // WriteTickFence detail leaves (kinds 251-253 per-table, 254-256 per-archetype cluster)
        RuntimeWriteTickFenceActive               = runtimeMap["Runtime:WriteTickFence"];
        RuntimeWriteTickFenceTableActive          = runtimeMap["Runtime:WriteTickFence:Table"];
        RuntimeWriteTickFenceShadowActive         = runtimeMap["Runtime:WriteTickFence:Shadow"];
        RuntimeWriteTickFenceSpatialActive        = runtimeMap["Runtime:WriteTickFence:Spatial"];
        RuntimeWriteTickFenceClusterActive        = runtimeMap["Runtime:WriteTickFence:Cluster"];
        RuntimeWriteTickFenceClusterShadowActive  = runtimeMap["Runtime:WriteTickFence:Cluster:Shadow"];
        RuntimeWriteTickFenceClusterSpatialActive = runtimeMap["Runtime:WriteTickFence:Cluster:Spatial"];

        RuntimeThreadSchedulingActive = runtimeMap["Runtime:ThreadScheduling"];

        // §8.7 — CPU sampling is wall-clock thread-time: classifying a sample as truly on-CPU (vs. parked at a lock or
        // the GC barrier) needs the context-switch slices the EtwSchedulingPump emits. So the ThreadScheduling DEFAULT is
        // coupled to CpuSampling — enabling CPU sampling pulls scheduling capture on too, with no second knob. An explicit
        // Profiler:Runtime:ThreadScheduling:Enabled (true OR false) still wins; the coupling only fills an absent default.
        // The pump's own Windows + elevation guards (EtwSchedulingPump.Start) still degrade gracefully where unsupported.
        if (!RuntimeThreadSchedulingActive
            && ProfilerCpuSamplingActive
            && string.IsNullOrEmpty(config["Typhon:Profiler:Runtime:ThreadScheduling:Enabled"]))
        {
            RuntimeThreadSchedulingActive = true;
        }

        // ─── Storage subtree (Phase 5 final shape) ─────────────────────────────
        // Greenfield deeper subtree; the existing per-kind suppression list still controls
        // kinds 50-59. Storage:PageCache:DirtyWalk is a brand-new span (kind 165). Segment +
        // ChunkSegment + FileHandle + OccupancyMap leaves cover kinds 166-171.
        var storageTree = new Node("Storage",
        [
            new Node("PageCache",
            [
                new Node("DirtyWalk"),
            ]),
            new Node("Segment",
            [
                new Node("Create"),
                new Node("Grow"),
                new Node("Load"),
            ]),
            new Node("ChunkSegment",
            [
                new Node("Grow"),
            ]),
            new Node("FileHandle"),
            new Node("OccupancyMap",
            [
                new Node("Grow"),
            ]),
        ]);
        var storageRootExplicit = ReadBool(config, "Typhon:Profiler:Storage:Enabled", false);
        var storageRootEffective = Enabled && storageRootExplicit;
        var storageMap = TelemetryConfigResolver.Resolve(
            storageTree, storageRootEffective, config, "Typhon:Profiler");

        StorageActive             = storageMap["Storage"];
        StoragePageCacheActive    = storageMap["Storage:PageCache"];
        StorageSegmentActive      = storageMap["Storage:Segment"];
        StorageChunkSegmentActive = storageMap["Storage:ChunkSegment"];
        StorageFileHandleActive   = storageMap["Storage:FileHandle"];
        StorageOccupancyMapActive = storageMap["Storage:OccupancyMap"];

        StoragePageCacheDirtyWalkActive = storageMap["Storage:PageCache:DirtyWalk"];
        StorageSegmentCreateActive      = storageMap["Storage:Segment:Create"];
        StorageSegmentGrowActive        = storageMap["Storage:Segment:Grow"];
        StorageSegmentLoadActive        = storageMap["Storage:Segment:Load"];
        StorageChunkSegmentGrowActive   = storageMap["Storage:ChunkSegment:Grow"];
        StorageFileHandleEnabledActive  = storageMap["Storage:FileHandle"];
        StorageOccupancyMapGrowActive   = storageMap["Storage:OccupancyMap:Grow"];

        // Threshold knob — independent of the gate tree (default 1 ms).
        StoragePageCacheCompletionThresholdMs = ReadInt(config,
            "Typhon:Profiler:Storage:PageCache:CompletionThresholdMs", 1);

        // ─── Memory subtree (Phase 5 final shape) ──────────────────────────────
        var memoryTree = new Node("Memory",
        [
            new Node("AlignmentWaste"),
        ]);
        var memoryRootExplicit = ReadBool(config, "Typhon:Profiler:Memory:Enabled", false);
        var memoryRootEffective = Enabled && memoryRootExplicit;
        var memoryMap = TelemetryConfigResolver.Resolve(
            memoryTree, memoryRootEffective, config, "Typhon:Profiler");

        MemoryActive               = memoryMap["Memory"];
        MemoryAlignmentWasteActive = memoryMap["Memory:AlignmentWaste"];

        // ─── Data plane subtree (Phase 6 final shape) ──────────────────────────
        // High-frequency leaves (ChainWalk, Search, NodeCow) are also on the per-kind suppression deny-list at TyphonEvent class load —
        // flipping the parent ON keeps those specific leaves OFF until UnsuppressKind is called explicitly. The deny-list is reserved for
        // truly extreme-frequency kinds (≥10⁵/sec); diagnostic-grade leaves are gated solely by JSON. See TyphonEvent's static ctor for
        // the current deny-list and the rationale per kind.
        var dataTree = new Node("Data",
        [
            new Node("Transaction",
            [
                new Node("Init"),
                new Node("Prepare"),
                new Node("Validate"),
                new Node("Conflict"),
                new Node("Cleanup"),
            ]),
            new Node("MVCC",
            [
                new Node("ChainWalk"),
                new Node("VersionCleanup"),
            ]),
            new Node("Index",
            [
                new Node("BTree",
                [
                    new Node("Search"),
                    new Node("RangeScan",
                    [
                        new Node("Revalidate"),
                    ]),
                    new Node("RebalanceFallback"),
                    new Node("BulkInsert"),
                    new Node("Root"),
                    new Node("NodeCow"),
                ]),
            ]),
        ]);
        var dataRootExplicit = ReadBool(config, "Typhon:Profiler:Data:Enabled", false);
        var dataRootEffective = Enabled && dataRootExplicit;
        var dataMap = TelemetryConfigResolver.Resolve(
            dataTree, dataRootEffective, config, "Typhon:Profiler");

        DataActive             = dataMap["Data"];
        DataTransactionActive  = dataMap["Data:Transaction"];
        DataMvccActive         = dataMap["Data:MVCC"];
        DataIndexActive        = dataMap["Data:Index"];
        DataIndexBTreeActive   = dataMap["Data:Index:BTree"];

        DataTransactionInitActive     = dataMap["Data:Transaction:Init"];
        DataTransactionPrepareActive  = dataMap["Data:Transaction:Prepare"];
        DataTransactionValidateActive = dataMap["Data:Transaction:Validate"];
        DataTransactionConflictActive = dataMap["Data:Transaction:Conflict"];
        DataTransactionCleanupActive  = dataMap["Data:Transaction:Cleanup"];

        DataMvccChainWalkActive       = dataMap["Data:MVCC:ChainWalk"];
        DataMvccVersionCleanupActive  = dataMap["Data:MVCC:VersionCleanup"];

        DataIndexBTreeSearchActive             = dataMap["Data:Index:BTree:Search"];
        DataIndexBTreeRangeScanActive          = dataMap["Data:Index:BTree:RangeScan"];
        DataIndexBTreeRangeScanRevalidateActive = dataMap["Data:Index:BTree:RangeScan:Revalidate"];
        DataIndexBTreeRebalanceFallbackActive  = dataMap["Data:Index:BTree:RebalanceFallback"];
        DataIndexBTreeBulkInsertActive         = dataMap["Data:Index:BTree:BulkInsert"];
        DataIndexBTreeRootActive               = dataMap["Data:Index:BTree:Root"];
        DataIndexBTreeNodeCowActive            = dataMap["Data:Index:BTree:NodeCow"];

        // ─── Query subtree (Phase 7) ───────────────────────────────────────────
        var queryTree = new Node("Query",
        [
            new Node("Parse",
            [
                new Node("DNF"),
            ]),
            new Node("Plan",
            [
                new Node("PrimarySelect"),
                new Node("Sort"),
            ]),
            new Node("Estimate"),
            new Node("Execute",
            [
                new Node("IndexScan"),
                new Node("Iterate"),
                new Node("Filter"),
                new Node("Pagination"),
                new Node("StorageMode"),
            ]),
            new Node("Count"),
        ]);
        var queryRootExplicit = ReadBool(config, "Typhon:Profiler:Query:Enabled", false);
        var queryRootEffective = Enabled && queryRootExplicit;
        var queryMap = TelemetryConfigResolver.Resolve(queryTree, queryRootEffective, config, "Typhon:Profiler");

        QueryActive                  = queryMap["Query"];
        QueryParseActive             = queryMap["Query:Parse"];
        QueryPlanActive              = queryMap["Query:Plan"];
        QueryExecuteActive           = queryMap["Query:Execute"];

        QueryParseEnabledActive      = queryMap["Query:Parse"];   // alias for the leaf gate
        QueryParseDnfActive          = queryMap["Query:Parse:DNF"];
        QueryPlanEnabledActive       = queryMap["Query:Plan"];    // alias
        QueryEstimateActive          = queryMap["Query:Estimate"];
        QueryPlanPrimarySelectActive = queryMap["Query:Plan:PrimarySelect"];
        QueryPlanSortActive          = queryMap["Query:Plan:Sort"];
        QueryExecuteIndexScanActive  = queryMap["Query:Execute:IndexScan"];
        QueryExecuteIterateActive    = queryMap["Query:Execute:Iterate"];
        QueryExecuteFilterActive     = queryMap["Query:Execute:Filter"];
        QueryExecutePaginationActive = queryMap["Query:Execute:Pagination"];
        QueryExecuteStorageModeActive = queryMap["Query:Execute:StorageMode"];
        QueryCountActive             = queryMap["Query:Count"];

        // ─── ECS subtree (Phase 7 depth) ───────────────────────────────────────
        var ecsTree = new Node("ECS",
        [
            new Node("Query",
            [
                new Node("Construct"),
                new Node("MaskAnd"),
                new Node("SubtreeExpand"),
                new Node("Constraint",
                [
                    new Node("Enabled"),
                ]),
                new Node("Spatial",
                [
                    new Node("Attach"),
                ]),
            ]),
            new Node("View",
            [
                new Node("RefreshPull"),
                new Node("IncrementalDrain"),
                new Node("DeltaBuffer",
                [
                    new Node("Overflow"),
                ]),
                new Node("ProcessEntry"),
                new Node("ProcessEntryOr"),
                new Node("RefreshFull"),
                new Node("RefreshFullOr"),
                new Node("Registry",
                [
                    new Node("Register"),
                    new Node("Deregister"),
                ]),
                new Node("DeltaCache",
                [
                    new Node("Miss"),
                ]),
            ]),
        ]);
        var ecsRootExplicit = ReadBool(config, "Typhon:Profiler:ECS:Enabled", false);
        var ecsRootEffective = Enabled && ecsRootExplicit;
        var ecsMap = TelemetryConfigResolver.Resolve(ecsTree, ecsRootEffective, config, "Typhon:Profiler");

        EcsActive       = ecsMap["ECS"];
        EcsQueryActive  = ecsMap["ECS:Query"];
        EcsViewActive   = ecsMap["ECS:View"];

        EcsQueryConstructActive          = ecsMap["ECS:Query:Construct"];
        EcsQueryMaskAndActive            = ecsMap["ECS:Query:MaskAnd"];
        EcsQuerySubtreeExpandActive      = ecsMap["ECS:Query:SubtreeExpand"];
        EcsQueryConstraintEnabledActive  = ecsMap["ECS:Query:Constraint:Enabled"];
        EcsQuerySpatialAttachActive      = ecsMap["ECS:Query:Spatial:Attach"];

        EcsViewRefreshPullActive         = ecsMap["ECS:View:RefreshPull"];
        EcsViewIncrementalDrainActive    = ecsMap["ECS:View:IncrementalDrain"];
        EcsViewDeltaBufferOverflowActive = ecsMap["ECS:View:DeltaBuffer:Overflow"];
        EcsViewProcessEntryActive        = ecsMap["ECS:View:ProcessEntry"];
        EcsViewProcessEntryOrActive      = ecsMap["ECS:View:ProcessEntryOr"];
        EcsViewRefreshFullActive         = ecsMap["ECS:View:RefreshFull"];
        EcsViewRefreshFullOrActive       = ecsMap["ECS:View:RefreshFullOr"];
        EcsViewRegistryRegisterActive    = ecsMap["ECS:View:Registry:Register"];
        EcsViewRegistryDeregisterActive  = ecsMap["ECS:View:Registry:Deregister"];
        EcsViewDeltaCacheMissActive      = ecsMap["ECS:View:DeltaCache:Miss"];

        // ─── Durability subtree (Phase 8) ──────────────────────────────────────
        var durabilityTree = new Node("Durability",
        [
            new Node("WAL",
            [
                new Node("QueueDrain"),
                new Node("OsWrite"),
                new Node("Signal"),
                new Node("GroupCommit"),
                new Node("Queue"),
                new Node("Buffer"),
                new Node("Frame"),
                new Node("Backpressure"),
            ]),
            new Node("Checkpoint",
            [
                new Node("WriteBatch"),
                new Node("Backpressure"),
                new Node("Sleep"),
            ]),
            new Node("Recovery",
            [
                new Node("Start"),
                new Node("Discover"),
                new Node("Segment"),
                new Node("Record"),
                new Node("Redo"),
                new Node("Undo"),
                new Node("TickFence"),
            ]),
            new Node("UoW",
            [
                new Node("State"),
                new Node("Deadline"),
            ]),
        ]);
        var durabilityRootExplicit = ReadBool(config, "Typhon:Profiler:Durability:Enabled", false);
        var durabilityRootEffective = Enabled && durabilityRootExplicit;
        var durabilityMap = TelemetryConfigResolver.Resolve(durabilityTree, durabilityRootEffective, config, "Typhon:Profiler");

        DurabilityActive            = durabilityMap["Durability"];
        DurabilityWalActive         = durabilityMap["Durability:WAL"];
        DurabilityCheckpointActive  = durabilityMap["Durability:Checkpoint"];
        DurabilityRecoveryActive    = durabilityMap["Durability:Recovery"];
        DurabilityUowActive         = durabilityMap["Durability:UoW"];

        DurabilityWalQueueDrainActive    = durabilityMap["Durability:WAL:QueueDrain"];
        DurabilityWalOsWriteActive       = durabilityMap["Durability:WAL:OsWrite"];
        DurabilityWalSignalActive        = durabilityMap["Durability:WAL:Signal"];
        DurabilityWalGroupCommitActive   = durabilityMap["Durability:WAL:GroupCommit"];
        DurabilityWalQueueActive         = durabilityMap["Durability:WAL:Queue"];
        DurabilityWalBufferActive        = durabilityMap["Durability:WAL:Buffer"];
        DurabilityWalFrameActive         = durabilityMap["Durability:WAL:Frame"];
        DurabilityWalBackpressureActive  = durabilityMap["Durability:WAL:Backpressure"];

        DurabilityCheckpointWriteBatchActive   = durabilityMap["Durability:Checkpoint:WriteBatch"];
        DurabilityCheckpointBackpressureActive = durabilityMap["Durability:Checkpoint:Backpressure"];
        DurabilityCheckpointSleepActive        = durabilityMap["Durability:Checkpoint:Sleep"];

        DurabilityRecoveryStartActive     = durabilityMap["Durability:Recovery:Start"];
        DurabilityRecoveryDiscoverActive  = durabilityMap["Durability:Recovery:Discover"];
        DurabilityRecoverySegmentActive   = durabilityMap["Durability:Recovery:Segment"];
        DurabilityRecoveryRecordActive    = durabilityMap["Durability:Recovery:Record"];
        DurabilityRecoveryRedoActive      = durabilityMap["Durability:Recovery:Redo"];
        DurabilityRecoveryUndoActive      = durabilityMap["Durability:Recovery:Undo"];
        DurabilityRecoveryTickFenceActive = durabilityMap["Durability:Recovery:TickFence"];

        DurabilityUowStateActive    = durabilityMap["Durability:UoW:State"];
        DurabilityUowDeadlineActive = durabilityMap["Durability:UoW:Deadline"];
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // CONFIG READING HELPERS
    // ═══════════════════════════════════════════════════════════════════════════

    private static bool ReadBool(IConfiguration config, string key, bool defaultValue)
    {
        var v = config[key];
        if (string.IsNullOrEmpty(v))
        {
            return defaultValue;
        }
        return bool.TryParse(v, out var b) ? b : defaultValue;
    }

    private static int ReadInt(IConfiguration config, string key, int defaultValue)
    {
        var v = config[key];
        if (string.IsNullOrEmpty(v))
        {
            return defaultValue;
        }
        return int.TryParse(v, out var i) ? i : defaultValue;
    }

    private static (IConfiguration config, string loadedPath) BuildConfiguration()
    {
        var builder = new ConfigurationBuilder();
        string loadedPath = null;

        // 1. Look for config file in current directory
        var currentDirPath = Path.Combine(Directory.GetCurrentDirectory(), "typhon.telemetry.json");
        if (File.Exists(currentDirPath))
        {
            builder.AddJsonFile(currentDirPath, true, false);
            loadedPath = currentDirPath;
        }

        // 2. Look for config file next to the assembly (fallback)
        var assemblyLocation = typeof(TelemetryConfig).Assembly.Location;
        if (!string.IsNullOrEmpty(assemblyLocation))
        {
            var assemblyDir = Path.GetDirectoryName(assemblyLocation);
            if (!string.IsNullOrEmpty(assemblyDir))
            {
                var assemblyConfigPath = Path.Combine(assemblyDir, "typhon.telemetry.json");
                if (File.Exists(assemblyConfigPath) && assemblyConfigPath != currentDirPath)
                {
                    builder.AddJsonFile(assemblyConfigPath, true, false);
                    loadedPath ??= assemblyConfigPath;
                }
            }
        }

        // 3. Environment variables override everything
        // Uses __ as hierarchy separator: TYPHON__PROFILER__ENABLED -> Typhon:Profiler:Enabled
        builder.AddEnvironmentVariables();

        return (builder.Build(), loadedPath);
    }

    /// <summary>
    /// Forces early initialization of telemetry configuration.
    /// Call this at application startup to ensure the JIT compiler sees the
    /// readonly field values before compiling hot paths.
    /// </summary>
    /// <remarks>
    /// The <see cref="MethodImplOptions.NoInlining"/> attribute ensures this method
    /// is actually called and not optimized away, guaranteeing the static constructor runs.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void EnsureInitialized() =>
        // Accessing any static field triggers the static constructor.
        // The NoInlining attribute ensures this method call isn't optimized away.
        _ = Enabled;

    /// <summary>
    /// Returns a human-readable summary of the current telemetry configuration.
    /// Useful for logging at application startup.
    /// </summary>
    /// <returns>A multi-line string describing all telemetry settings.</returns>
    public static string GetConfigurationSummary() =>
        $"""
         Typhon Profiler Configuration:
           Config File: {LoadedConfigurationFile ?? "(none - using defaults/env vars)"}
           Master Enabled: {Enabled}

           Profiler: Active={ProfilerActive}
             GcTracing={ProfilerGcTracingEnabled} (Active={ProfilerGcTracingActive}),
             MemoryAllocations={ProfilerMemoryAllocationsEnabled} (Active={ProfilerMemoryAllocationsActive}),
             Gauges={ProfilerGaugesEnabled} (Active={ProfilerGaugesActive}),
             CpuSampling={ProfilerCpuSamplingEnabled} (Active={ProfilerCpuSamplingActive})

           Scheduler: Active={SchedulerActive}
             Enabled={SchedulerEnabled}, TransitionLatency={SchedulerTrackTransitionLatency},
             WorkerUtilization={SchedulerTrackWorkerUtilization}, StragglerGap={SchedulerTrackStragglerGap},
             ArchetypeTouches={SchedulerArchetypeTouchesActive}
         """;

    /// <summary>
    /// Returns a concise one-line summary of active telemetry components.
    /// </summary>
    public static string GetActiveComponentsSummary()
    {
        if (!Enabled)
        {
            return "Profiler: Disabled";
        }

        var active = new System.Collections.Generic.List<string>();

        if (SchedulerActive)
        {
            active.Add("Scheduler");
        }

        if (ProfilerActive)
        {
            var suffix = new System.Collections.Generic.List<string>();
            if (ProfilerGcTracingActive)
            {
                suffix.Add("GcTracing");
            }
            if (ProfilerMemoryAllocationsActive)
            {
                suffix.Add("MemoryAllocations");
            }
            if (ProfilerGaugesActive)
            {
                suffix.Add("Gauges");
            }
            if (ProfilerCpuSamplingActive)
            {
                suffix.Add("CpuSampling");
            }
            active.Add(suffix.Count > 0 ? $"Profiler+{string.Join("+", suffix)}" : "Profiler");
        }

        return active.Count > 0 ? $"Profiler: Enabled [{string.Join(", ", active)}]" : "Profiler: Enabled (no components active)";
    }
}
