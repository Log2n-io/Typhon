// unset

using JetBrains.Annotations;
using Microsoft.Extensions.Configuration;
using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
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
///
/// <para>
/// <b>Namespace migration (Phase 0):</b> the configuration namespace was flattened from
/// <c>Typhon:Telemetry:Profiler:*</c> to <c>Typhon:Profiler:*</c>. The legacy paths are still
/// read via a back-compat shim in this release; the shim emits a deprecation warning to
/// <c>Console.Error</c> when activated and will be removed in the next minor. See
/// <see cref="LegacyConfigDetected"/>.
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
/// The legacy paths (<c>TYPHON__TELEMETRY__PROFILER__*</c>) are also accepted for one release.
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
    /// Reads from <c>Typhon:Profiler:Enabled</c>. The back-compat shim recognises
    /// <c>Typhon:Telemetry:Enabled AND Typhon:Telemetry:Profiler:Enabled</c> as the legacy
    /// equivalent (both must be <c>true</c>).
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
    // Greenfield namespace. The legacy `Typhon:Telemetry:Spatial:Enabled` flag was deleted in
    // Phase 0; this Spatial subtree is brand-new with no back-compat fallback. Default-OFF
    // for everything except `ClusterMigration:Execute` which preserves the pre-Phase-3 behavior
    // of kind 60 (the only Spatial event that already shipped).

    /// <summary>Combined gate for the entire Spatial subsystem (parent of all Spatial:* leaves).</summary>
    public static readonly bool SpatialActive;

    // Subtree parents
    public static readonly bool SpatialQueryActive;
    public static readonly bool SpatialRTreeActive;
    public static readonly bool SpatialGridActive;
    public static readonly bool SpatialCellActive;
    public static readonly bool SpatialCellIndexActive;
    public static readonly bool SpatialClusterMigrationActive;
    public static readonly bool SpatialTierIndexActive;
    public static readonly bool SpatialMaintainActive;
    public static readonly bool SpatialTriggerActive;
    public static readonly bool SpatialTriggerOccupantActive;
    public static readonly bool SpatialTriggerCacheActive;

    // Query leaves (kinds 117-122)
    public static readonly bool SpatialQueryAabbActive;
    public static readonly bool SpatialQueryRadiusActive;
    public static readonly bool SpatialQueryRayActive;
    public static readonly bool SpatialQueryFrustumActive;
    public static readonly bool SpatialQueryKnnActive;
    public static readonly bool SpatialQueryCountActive;

    // RTree structural leaves (kinds 123-126)
    public static readonly bool SpatialRTreeInsertActive;
    public static readonly bool SpatialRTreeRemoveActive;
    public static readonly bool SpatialRTreeNodeSplitActive;
    public static readonly bool SpatialRTreeBulkLoadActive;

    // Grid leaves (kinds 127-129)
    public static readonly bool SpatialGridCellTierChangeActive;
    public static readonly bool SpatialGridOccupancyChangeActive;
    public static readonly bool SpatialGridClusterCellAssignActive;

    // Cell:Index leaves (kinds 130-132)
    public static readonly bool SpatialCellIndexAddActive;
    public static readonly bool SpatialCellIndexUpdateActive;
    public static readonly bool SpatialCellIndexRemoveActive;

    // ClusterMigration leaves (kinds 133-135; Execute = existing kind 60)
    public static readonly bool SpatialClusterMigrationDetectActive;
    public static readonly bool SpatialClusterMigrationQueueActive;
    public static readonly bool SpatialClusterMigrationExecuteActive;
    public static readonly bool SpatialClusterMigrationHysteresisActive;

    // TierIndex leaves (kinds 136-137)
    public static readonly bool SpatialTierIndexRebuildActive;
    public static readonly bool SpatialTierIndexVersionSkipActive;

    // Maintain leaves (kinds 138-141)
    public static readonly bool SpatialMaintainInsertActive;
    public static readonly bool SpatialMaintainUpdateSlowPathActive;
    public static readonly bool SpatialMaintainAabbValidateActive;
    public static readonly bool SpatialMaintainBackPointerWriteActive;

    // Trigger leaves (kinds 142-145)
    public static readonly bool SpatialTriggerRegionActive;
    public static readonly bool SpatialTriggerEvalActive;
    public static readonly bool SpatialTriggerOccupantDiffActive;
    public static readonly bool SpatialTriggerCacheInvalidateActive;

    // ═══════════════════════════════════════════════════════════════════════════
    // SCHEDULER & RUNTIME TRACING (Phase 4 — see 04-scheduler-runtime.md)
    // ═══════════════════════════════════════════════════════════════════════════
    // Greenfield deeper subtree. The existing `SchedulerActive` master gate (above) stays;
    // these are NEW sub-tree gates allowing operators to opt into Scheduler depth + Runtime
    // (UoW, Tx Lifecycle, Subscription) tracing per-leaf.

    // Scheduler subtree parents
    public static readonly bool SchedulerSystemActive;
    public static readonly bool SchedulerWorkerActive;
    public static readonly bool SchedulerDispenseActive;
    public static readonly bool SchedulerDependencyActive;
    public static readonly bool SchedulerOverloadActive;
    public static readonly bool SchedulerGraphActive;

    // Scheduler:System leaves (kinds 146-149)
    public static readonly bool SchedulerSystemStartExecutionActive;
    public static readonly bool SchedulerSystemCompletionActive;
    public static readonly bool SchedulerSystemQueueWaitActive;
    public static readonly bool SchedulerSystemSingleThreadedActive;

    // Scheduler:Worker leaves (kinds 150-152)
    public static readonly bool SchedulerWorkerIdleActive;
    public static readonly bool SchedulerWorkerWakeActive;
    public static readonly bool SchedulerWorkerBetweenTickActive;

    // Scheduler:Dependency leaves (kinds 154-155)
    public static readonly bool SchedulerDependencyReadyActive;
    public static readonly bool SchedulerDependencyFanOutActive;

    // Scheduler:Overload leaves (kinds 156-158)
    public static readonly bool SchedulerOverloadLevelChangeActive;
    public static readonly bool SchedulerOverloadSystemShedActive;
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
    public static readonly bool SchedulerGraphBuildActive;
    public static readonly bool SchedulerGraphRebuildActive;

    // Runtime subtree parents
    public static readonly bool RuntimeActive;
    public static readonly bool RuntimePhaseActive;
    public static readonly bool RuntimeTransactionActive;
    public static readonly bool RuntimeSubscriptionActive;
    public static readonly bool RuntimeSubscriptionOutputActive;

    // Runtime:Phase leaves (kinds 161-162)
    public static readonly bool RuntimePhaseUoWCreateActive;
    public static readonly bool RuntimePhaseUoWFlushActive;

    // Runtime:Transaction leaves (kind 163)
    public static readonly bool RuntimeTransactionLifecycleActive;

    // Runtime:Subscription leaves (Phase 4: kind 164; Phase 9: kinds 235-240)
    public static readonly bool RuntimeSubscriptionOutputExecuteActive;

    // Phase 9 (#287) Subscription depth leaves
    public static readonly bool RuntimeSubscriptionSubscriberActive;
    public static readonly bool RuntimeSubscriptionDeltaBuildActive;
    public static readonly bool RuntimeSubscriptionDeltaSerializeActive;
    public static readonly bool RuntimeSubscriptionTransitionBeginSyncActive;
    public static readonly bool RuntimeSubscriptionOutputCleanupActive;
    public static readonly bool RuntimeSubscriptionDeltaDirtyBitmapSupplementActive;

    // ═══════════════════════════════════════════════════════════════════════════
    // STORAGE & MEMORY TRACING (Phase 5 — see 05-storage-memory.md)
    // ═══════════════════════════════════════════════════════════════════════════
    // Storage gets a deeper subtree for the new IDs 165-171; existing kinds 50-59
    // remain controlled by the per-kind suppression list (and the new
    // CompletionThresholdMs knob for 56/57/58).
    // Memory:AlignmentWaste (kind 172) is the only Memory event flag.

    // Storage subtree parents
    public static readonly bool StorageActive;
    public static readonly bool StoragePageCacheActive;
    public static readonly bool StorageSegmentActive;
    public static readonly bool StorageChunkSegmentActive;
    public static readonly bool StorageFileHandleActive;
    public static readonly bool StorageOccupancyMapActive;

    // Storage leaves (kinds 165-171)
    public static readonly bool StoragePageCacheDirtyWalkActive;
    public static readonly bool StorageSegmentCreateActive;
    public static readonly bool StorageSegmentGrowActive;
    public static readonly bool StorageSegmentLoadActive;
    public static readonly bool StorageChunkSegmentGrowActive;
    public static readonly bool StorageFileHandleEnabledActive;
    public static readonly bool StorageOccupancyMapGrowActive;

    /// <summary>
    /// Producer-side duration threshold (ms) for kinds 56/57/58 (PageCache:DiskRead/Write/Flush Completed).
    /// When &gt; 0 the emit path skips records whose duration is shorter than the threshold; when 0 it
    /// matches today's behaviour (always emit when un-suppressed). Default: 1 ms.
    /// </summary>
    public static readonly int StoragePageCacheCompletionThresholdMs;

    // Memory subtree parents
    public static readonly bool MemoryActive;

    // Memory leaves (kind 172)
    public static readonly bool MemoryAlignmentWasteActive;

    // ═══════════════════════════════════════════════════════════════════════════
    // DATA PLANE TRACING (Phase 6 — see 06-data-plane.md)
    // ═══════════════════════════════════════════════════════════════════════════
    // Data:Transaction / Data:MVCC / Data:Index:BTree subtrees. All default off; high-freq
    // leaves (Prepare, ChainWalk, Search, Revalidate, NodeCow) are also added to the per-kind
    // suppression list so that flipping the parent on doesn't drown the ring in events.

    // Data subtree parents
    public static readonly bool DataActive;
    public static readonly bool DataTransactionActive;
    public static readonly bool DataMvccActive;
    public static readonly bool DataIndexActive;
    public static readonly bool DataIndexBTreeActive;

    // Data:Transaction leaves (kinds 173-177)
    public static readonly bool DataTransactionInitActive;
    public static readonly bool DataTransactionPrepareActive;
    public static readonly bool DataTransactionValidateActive;
    public static readonly bool DataTransactionConflictActive;
    public static readonly bool DataTransactionCleanupActive;

    // Data:MVCC leaves (kinds 178-179)
    public static readonly bool DataMvccChainWalkActive;
    public static readonly bool DataMvccVersionCleanupActive;

    // Data:Index:BTree leaves (kinds 180-186)
    public static readonly bool DataIndexBTreeSearchActive;
    public static readonly bool DataIndexBTreeRangeScanActive;
    public static readonly bool DataIndexBTreeRangeScanRevalidateActive;
    public static readonly bool DataIndexBTreeRebalanceFallbackActive;
    public static readonly bool DataIndexBTreeBulkInsertActive;
    public static readonly bool DataIndexBTreeRootActive;
    public static readonly bool DataIndexBTreeNodeCowActive;

    // ═══════════════════════════════════════════════════════════════════════════
    // QUERY / ECS:Query / ECS:View TRACING (Phase 7 — see 07-query-ecs-view.md)
    // ═══════════════════════════════════════════════════════════════════════════

    // Query subtree parents
    public static readonly bool QueryActive;
    public static readonly bool QueryParseActive;
    public static readonly bool QueryPlanActive;
    public static readonly bool QueryExecuteActive;

    // Query leaves (kinds 187-198)
    public static readonly bool QueryParseEnabledActive;
    public static readonly bool QueryParseDnfActive;
    public static readonly bool QueryPlanEnabledActive;
    public static readonly bool QueryEstimateActive;
    public static readonly bool QueryPlanPrimarySelectActive;
    public static readonly bool QueryPlanSortActive;
    public static readonly bool QueryExecuteIndexScanActive;
    public static readonly bool QueryExecuteIterateActive;
    public static readonly bool QueryExecuteFilterActive;
    public static readonly bool QueryExecutePaginationActive;
    public static readonly bool QueryExecuteStorageModeActive;
    public static readonly bool QueryCountActive;

    // ECS subtree parents (and depth from Phase 7)
    public static readonly bool EcsActive;
    public static readonly bool EcsQueryActive;
    public static readonly bool EcsViewActive;

    // ECS:Query depth leaves (kinds 199-203)
    public static readonly bool EcsQueryConstructActive;
    public static readonly bool EcsQueryMaskAndActive;
    public static readonly bool EcsQuerySubtreeExpandActive;
    public static readonly bool EcsQueryConstraintEnabledActive;
    public static readonly bool EcsQuerySpatialAttachActive;

    // ECS:View depth leaves (kinds 204-213)
    public static readonly bool EcsViewRefreshPullActive;
    public static readonly bool EcsViewIncrementalDrainActive;
    public static readonly bool EcsViewDeltaBufferOverflowActive;
    public static readonly bool EcsViewProcessEntryActive;
    public static readonly bool EcsViewProcessEntryOrActive;
    public static readonly bool EcsViewRefreshFullActive;
    public static readonly bool EcsViewRefreshFullOrActive;
    public static readonly bool EcsViewRegistryRegisterActive;
    public static readonly bool EcsViewRegistryDeregisterActive;
    public static readonly bool EcsViewDeltaCacheMissActive;

    // ═══════════════════════════════════════════════════════════════════════════
    // DURABILITY TRACING (Phase 8 — see 08-durability.md)
    // ═══════════════════════════════════════════════════════════════════════════

    // Durability subtree parents
    public static readonly bool DurabilityActive;
    public static readonly bool DurabilityWalActive;
    public static readonly bool DurabilityCheckpointActive;
    public static readonly bool DurabilityRecoveryActive;
    public static readonly bool DurabilityUowActive;

    // Durability:WAL leaves (kinds 214-221)
    public static readonly bool DurabilityWalQueueDrainActive;
    public static readonly bool DurabilityWalOsWriteActive;
    public static readonly bool DurabilityWalSignalActive;
    public static readonly bool DurabilityWalGroupCommitActive;
    public static readonly bool DurabilityWalQueueActive;
    public static readonly bool DurabilityWalBufferActive;
    public static readonly bool DurabilityWalFrameActive;
    public static readonly bool DurabilityWalBackpressureActive;

    // Durability:Checkpoint depth (kinds 222-224)
    public static readonly bool DurabilityCheckpointWriteBatchActive;
    public static readonly bool DurabilityCheckpointBackpressureActive;
    public static readonly bool DurabilityCheckpointSleepActive;

    // Durability:Recovery leaves (kinds 225-232)
    public static readonly bool DurabilityRecoveryStartActive;
    public static readonly bool DurabilityRecoveryDiscoverActive;
    public static readonly bool DurabilityRecoverySegmentActive;
    public static readonly bool DurabilityRecoveryRecordActive;
    public static readonly bool DurabilityRecoveryFpiActive;
    public static readonly bool DurabilityRecoveryRedoActive;
    public static readonly bool DurabilityRecoveryUndoActive;
    public static readonly bool DurabilityRecoveryTickFenceActive;

    // Durability:UoW leaves (kinds 233-234)
    public static readonly bool DurabilityUowStateActive;
    public static readonly bool DurabilityUowDeadlineActive;

    // ═══════════════════════════════════════════════════════════════════════════
    // CONFIGURATION SOURCE TRACKING (for diagnostics)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The configuration file path that was loaded, or null if using defaults/env vars only.
    /// </summary>
    public static readonly string LoadedConfigurationFile;

    /// <summary>
    /// True if any value was read from the legacy <c>Typhon:Telemetry:*</c> namespace via the back-compat shim,
    /// or if any legacy key was present in the loaded configuration. A deprecation warning is emitted to
    /// <c>Console.Error</c> at static-class load when this flag is set.
    /// </summary>
    public static readonly bool LegacyConfigDetected;

    // ═══════════════════════════════════════════════════════════════════════════
    // STATIC CONSTRUCTOR - Runs once on first access to any static member
    // ═══════════════════════════════════════════════════════════════════════════

    static TelemetryConfig()
    {
        var (config, configPath) = BuildConfiguration();
        LoadedConfigurationFile = configPath;

        var legacyDetected = false;

        // ─── Master switch ─────────────────────────────────────────────────────
        // New: Typhon:Profiler:Enabled is the single master.
        // Legacy: required Typhon:Telemetry:Enabled AND Typhon:Telemetry:Profiler:Enabled (both must be true).
        Enabled = ReadMasterEnabled(config, ref legacyDetected);
        ProfilerEnabled = Enabled;
        ProfilerActive = Enabled;

        // ─── Profiler children (live) ──────────────────────────────────────────
        ProfilerGcTracingEnabled = ReadBoolFallback(config,
            "Typhon:Profiler:GcTracing:Enabled",
            "Typhon:Telemetry:Profiler:GcTracing:Enabled",
            false, ref legacyDetected);
        ProfilerGcTracingActive = ProfilerActive && ProfilerGcTracingEnabled;

        ProfilerMemoryAllocationsEnabled = ReadBoolFallback(config,
            "Typhon:Profiler:MemoryAllocations:Enabled",
            "Typhon:Telemetry:Profiler:MemoryAllocations:Enabled",
            false, ref legacyDetected);
        ProfilerMemoryAllocationsActive = ProfilerActive && ProfilerMemoryAllocationsEnabled;

        ProfilerGaugesEnabled = ReadBoolFallback(config,
            "Typhon:Profiler:Gauges:Enabled",
            "Typhon:Telemetry:Profiler:Gauges:Enabled",
            false, ref legacyDetected);
        ProfilerGaugesActive = ProfilerActive && ProfilerGaugesEnabled;

        // ─── Scheduler (live) ──────────────────────────────────────────────────
        // Legacy keys had a structural shape (Telemetry:Scheduler:Track*) — the back-compat fallback
        // remaps them to the new Profiler:Scheduler:Gauges:* tree.
        SchedulerEnabled = ReadBoolFallback(config,
            "Typhon:Profiler:Scheduler:Enabled",
            "Typhon:Telemetry:Scheduler:Enabled",
            false, ref legacyDetected);
        SchedulerActive = Enabled && SchedulerEnabled;

        SchedulerTrackTransitionLatency = ReadBoolFallback(config,
            "Typhon:Profiler:Scheduler:Gauges:TransitionLatency:Enabled",
            "Typhon:Telemetry:Scheduler:TrackTransitionLatency",
            true, ref legacyDetected);
        SchedulerTrackWorkerUtilization = ReadBoolFallback(config,
            "Typhon:Profiler:Scheduler:Gauges:WorkerUtilization:Enabled",
            "Typhon:Telemetry:Scheduler:TrackWorkerUtilization",
            true, ref legacyDetected);
        SchedulerTrackStragglerGap = ReadBoolFallback(config,
            "Typhon:Profiler:Scheduler:Gauges:StragglerGap:Enabled",
            "Typhon:Telemetry:Scheduler:TrackStragglerGap",
            true, ref legacyDetected);

        // ─── Concurrency subtree (Phase 1 + Phase 2 final shape) ───────────────
        // Greenfield namespace — no legacy fallback (the dead Typhon:Telemetry:* tree had no Concurrency concept).
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
        // Greenfield. No legacy fallback (Phase 0 deleted the dead Typhon:Telemetry:Spatial:* tree).
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
                new Node("FPI"),
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
        DurabilityRecoveryFpiActive       = durabilityMap["Durability:Recovery:FPI"];
        DurabilityRecoveryRedoActive      = durabilityMap["Durability:Recovery:Redo"];
        DurabilityRecoveryUndoActive      = durabilityMap["Durability:Recovery:Undo"];
        DurabilityRecoveryTickFenceActive = durabilityMap["Durability:Recovery:TickFence"];

        DurabilityUowStateActive    = durabilityMap["Durability:UoW:State"];
        DurabilityUowDeadlineActive = durabilityMap["Durability:UoW:Deadline"];

        // ─── Legacy-presence detection ─────────────────────────────────────────
        // Even if no fallback fired (e.g., user has only dead-family keys with no live consumers),
        // any populated Typhon:Telemetry:* subtree warrants the deprecation warning.
        if (!legacyDetected && config.GetSection("Typhon:Telemetry").GetChildren().Any())
        {
            legacyDetected = true;
        }

        LegacyConfigDetected = legacyDetected;

        if (legacyDetected)
        {
            EmitDeprecationWarning();
        }
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

    private static bool ReadBoolFallback(
        IConfiguration config,
        string newKey,
        string oldKey,
        bool defaultValue,
        ref bool legacyDetected)
    {
        var newVal = config[newKey];
        if (!string.IsNullOrEmpty(newVal))
        {
            return bool.TryParse(newVal, out var b) ? b : defaultValue;
        }

        var oldVal = config[oldKey];
        if (!string.IsNullOrEmpty(oldVal))
        {
            legacyDetected = true;
            return bool.TryParse(oldVal, out var b) ? b : defaultValue;
        }

        return defaultValue;
    }

    private static bool ReadMasterEnabled(IConfiguration config, ref bool legacyDetected)
    {
        // Prefer the new namespace.
        var newMaster = config["Typhon:Profiler:Enabled"];
        if (!string.IsNullOrEmpty(newMaster))
        {
            return bool.TryParse(newMaster, out var b) && b;
        }

        // Legacy: required Typhon:Telemetry:Enabled AND Typhon:Telemetry:Profiler:Enabled (both must be true).
        var legacyOuter = config["Typhon:Telemetry:Enabled"];
        var legacyInner = config["Typhon:Telemetry:Profiler:Enabled"];
        if (!string.IsNullOrEmpty(legacyOuter) || !string.IsNullOrEmpty(legacyInner))
        {
            legacyDetected = true;
            var outerOn = !string.IsNullOrEmpty(legacyOuter) && bool.TryParse(legacyOuter, out var o) && o;
            var innerOn = !string.IsNullOrEmpty(legacyInner) && bool.TryParse(legacyInner, out var i) && i;
            return outerOn && innerOn;
        }

        return false;
    }

    private static void EmitDeprecationWarning()
    {
        try
        {
            Console.Error.WriteLine(
                "[Typhon.Profiler] Configuration paths under 'Typhon:Telemetry:*' are deprecated; use 'Typhon:Profiler:*' instead. " +
                "The legacy paths are still read via a back-compat shim in this release but will be removed in the next minor.");
        }
        catch
        {
            // Console may not be available in some hosting scenarios — suppress to avoid disrupting startup.
        }
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
           Legacy Config Detected: {LegacyConfigDetected}

           Profiler: Active={ProfilerActive}
             GcTracing={ProfilerGcTracingEnabled} (Active={ProfilerGcTracingActive}),
             MemoryAllocations={ProfilerMemoryAllocationsEnabled} (Active={ProfilerMemoryAllocationsActive}),
             Gauges={ProfilerGaugesEnabled} (Active={ProfilerGaugesActive})

           Scheduler: Active={SchedulerActive}
             Enabled={SchedulerEnabled}, TransitionLatency={SchedulerTrackTransitionLatency},
             WorkerUtilization={SchedulerTrackWorkerUtilization}, StragglerGap={SchedulerTrackStragglerGap}
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
            active.Add(suffix.Count > 0 ? $"Profiler+{string.Join("+", suffix)}" : "Profiler");
        }

        return active.Count > 0 ? $"Profiler: Enabled [{string.Join(", ", active)}]" : "Profiler: Enabled (no components active)";
    }
}
