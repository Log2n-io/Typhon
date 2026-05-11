namespace Typhon.Profiler;

/// <summary>
/// Discriminant for a trace record. Every record starts with a 12-byte common header whose third byte carries this value; the kind determines how
/// the bytes after the header are interpreted (span header or minimal instant header, then per-kind typed payload).
/// </summary>
/// <remarks>
/// <para>
/// <b>Wire stability:</b> numeric values are part of the <c>.typhon-trace</c> file format. Never renumber or reuse an ID — append new entries only.
/// Gaps in numbering are intentional so related categories can grow contiguously without renumbering existing kinds.
/// </para>
/// <para>
/// <b>Span vs instant:</b> kinds &lt; 10 are <i>instant</i> events — they carry only the 12-byte common header plus optional minimal payload, no
/// span extension (no duration, no spanId, no parent linkage). Kinds ≥ 10 are <i>span</i> events — they include the 25-byte span header extension
/// plus an optional 16-byte trace context and per-kind typed payload. Use <see cref="TraceEventKindExtensions.IsSpan"/> for the range check.
/// </para>
/// </remarks>
public enum TraceEventKind : byte
{
    // ── Instant events (no span header — minimal 12-byte common header + optional tiny payload) ──

    /// <summary>Scheduler tick started (empty payload). Emitted at the top of each <c>DagScheduler.RunTick</c>.</summary>
    TickStart = 0,

    /// <summary>Scheduler tick ended. Payload: <c>overloadLevel: u8</c>, <c>tickMultiplier: u8</c>.</summary>
    TickEnd = 1,

    /// <summary>Tick phase started. Payload: <c>phase: u8</c> (TickPhase enum).</summary>
    PhaseStart = 2,

    /// <summary>Tick phase ended. Payload: <c>phase: u8</c>.</summary>
    PhaseEnd = 3,

    /// <summary>A system became ready (all predecessors completed). Payload: <c>systemIdx: u16</c>, <c>predecessorCount: u16</c>.</summary>
    SystemReady = 4,

    /// <summary>A system was skipped. Payload: <c>systemIdx: u16</c>, <c>skipReason: u8</c>.</summary>
    SystemSkipped = 5,

    /// <summary>Generic instant marker. Payload: <c>nameId: i32</c> (interned), <c>payload: i32</c>.</summary>
    Instant = 6,

    /// <summary>
    /// .NET runtime GC boundary — a garbage collection has started. Payload: <c>u8 generation</c>, <c>u8 reason</c> (<see cref="GcReason"/>),
    /// <c>u8 type</c> (<see cref="GcType"/>), <c>u32 count</c>. Emitted by the profiler's GC ingestion thread on observing <c>GCStart_V2</c>.
    /// Only produced when <c>TelemetryConfig.ProfilerGcTracingActive</c> is set at class load.
    /// </summary>
    GcStart = 7,

    /// <summary>
    /// .NET runtime GC boundary — a garbage collection has ended. Payload: <c>u8 generation</c>, <c>u32 count</c>, <c>i64 pauseDurationTicks</c>,
    /// <c>u64 promotedBytes</c>, five <c>u64</c> per-generation size-after values (Gen0/Gen1/Gen2/LOH/POH), <c>u64 totalCommittedBytes</c>.
    /// Sizes are snapshotted by the ingestion thread via <see cref="System.GC.GetGCMemoryInfo()"/> on the <c>GCEnd_V1</c> event.
    /// </summary>
    GcEnd = 8,

    /// <summary>
    /// Discrete unmanaged-memory allocation or free event. Every <c>PinnedMemoryBlock</c> construct/dispose emits one of these when
    /// <c>TelemetryConfig.ProfilerMemoryAllocationsActive</c> is set. Payload: <c>u8 direction</c> (<see cref="MemoryAllocDirection"/>),
    /// <c>u16 sourceTag</c> (<see cref="MemoryAllocSource"/>), <c>u64 sizeBytes</c>, <c>u64 totalAfterBytes</c>. Wire size: 31 B.
    /// </summary>
    MemoryAllocEvent = 9,

    // ── Span events (span header extension: 25B + optional 16B trace context, then typed payload) ──

    /// <summary>Scheduler chunk executed on a worker. Payload: <c>systemIdx: u16</c>, <c>chunkIdx: u16</c>, <c>totalChunks: u16</c>, <c>entitiesProcessed: i32</c>.</summary>
    SchedulerChunk = 10,

    // ── Transaction (span) ──

    /// <summary>Transaction commit. Required: <c>tsn: i64</c>. Optional: <c>componentCount: i32</c>, <c>conflictDetected: bool</c>.</summary>
    TransactionCommit = 20,

    /// <summary>Transaction rollback. Required: <c>tsn: i64</c>. Optional: <c>componentCount: i32</c>.</summary>
    TransactionRollback = 21,

    /// <summary>Per-component commit sub-span. Required: <c>tsn: i64</c>, <c>componentTypeId: i32</c>.</summary>
    TransactionCommitComponent = 22,

    /// <summary>WAL serialization inside Transaction.Commit. Required: <c>tsn: i64</c>. Optional: <c>walLsn: i64</c>.</summary>
    TransactionPersist = 23,

    // ── ECS (span) ──

    /// <summary>Entity spawn. Required: <c>archetypeId: u16</c>. Optional: <c>entityId: u64</c>, <c>tsn: i64</c>.</summary>
    EcsSpawn = 30,

    /// <summary>Entity destroy. Required: <c>entityId: u64</c>. Optional: <c>cascadeCount: i32</c>, <c>tsn: i64</c>.</summary>
    EcsDestroy = 31,

    /// <summary>Query execute. Required: <c>archetypeTypeId: u16</c>. Optional: <c>resultCount: i32</c>, <c>scanMode: u8</c>.</summary>
    EcsQueryExecute = 32,

    /// <summary>Query count. Required: <c>archetypeTypeId: u16</c>. Optional: <c>resultCount: i32</c>, <c>scanMode: u8</c>.</summary>
    EcsQueryCount = 33,

    /// <summary>Query any. Required: <c>archetypeTypeId: u16</c>. Optional: <c>found: bool</c>, <c>scanMode: u8</c>.</summary>
    EcsQueryAny = 34,

    /// <summary>View refresh. Required: <c>archetypeTypeId: u16</c>. Optional: <c>mode: u8</c>, <c>resultCount: i32</c>, <c>deltaCount: i32</c>.</summary>
    EcsViewRefresh = 35,

    // ── B+Tree (span) ──

    /// <summary>B+Tree insert. No payload — kind is the only data.</summary>
    BTreeInsert = 40,

    /// <summary>B+Tree delete. No payload.</summary>
    BTreeDelete = 41,

    /// <summary>B+Tree node split. No payload.</summary>
    BTreeNodeSplit = 42,

    /// <summary>B+Tree node merge. No payload.</summary>
    BTreeNodeMerge = 43,

    // ── Page cache (span) ──

    /// <summary>Page cache fetch. Required: <c>filePageIndex: i32</c>.</summary>
    PageCacheFetch = 50,

    /// <summary>Page cache disk read. Required: <c>filePageIndex: i32</c>.</summary>
    PageCacheDiskRead = 51,

    /// <summary>Page cache disk write. Required: <c>filePageIndex: i32</c>. Optional: <c>pageCount: i32</c>.</summary>
    PageCacheDiskWrite = 52,

    /// <summary>Page cache allocate page. Required: <c>filePageIndex: i32</c>.</summary>
    PageCacheAllocatePage = 53,

    /// <summary>Page cache flush. Required: <c>pageCount: i32</c>.</summary>
    PageCacheFlush = 54,

    /// <summary>
    /// A cached page was displaced by <see cref="PageCacheAllocatePage"/> to make room for a new page being fetched. Required:
    /// <c>filePageIndex: i32</c> (the displaced page's file index, NOT the incoming page).
    /// </summary>
    /// <remarks>
    /// Emitted as a zero-duration span (start == end). Parents under the enclosing <see cref="PageCacheAllocatePage"/> span via TLS, so the
    /// viewer renders eviction events as instant markers nested inside the AllocatePage bar — "allocation ran for 12 µs and kicked out page N."
    /// Reuses the <c>PageCacheEventCodec</c> wire shape (common header + span extension + 4 B filePageIndex + 1 B optMask), so no new codec
    /// is needed. Suppressed by default alongside the other PageCache.* kinds.
    /// </remarks>
    PageEvicted = 55,

    /// <summary>
    /// Completion marker for an async <see cref="PageCacheDiskRead"/>. Required: <c>filePageIndex: i32</c>. The record carries the original
    /// DiskRead span's <c>SpanId</c> as its own <c>SpanId</c> (same value — the viewer uses this to correlate kickoff → completion) and a
    /// <c>StartTimestamp</c> equal to the original's <c>StartTimestamp</c>, so the record's <c>durationTicks</c> field is the full async tail:
    /// <c>completionTimestamp - beginTimestamp</c>. Emitted from the thread-pool worker that completes the <c>RandomAccess.ReadAsync</c>.
    /// </summary>
    /// <remarks>
    /// Suppressed by default alongside the other PageCache.* kinds. Opt in with <c>TyphonEvent.UnsuppressKind(PageCacheDiskReadCompleted)</c>
    /// (must ALSO have <see cref="PageCacheDiskRead"/> unsuppressed, otherwise there's no kickoff span to correlate with and the completion
    /// is skipped at the call site). Reuses <see cref="PageCacheEventCodec"/> wire shape verbatim — decoder needs only a match-arm entry.
    /// </remarks>
    PageCacheDiskReadCompleted = 56,

    /// <summary>
    /// Completion marker for an async <see cref="PageCacheDiskWrite"/>. Required: <c>filePageIndex: i32</c>. Same correlation pattern as
    /// <see cref="PageCacheDiskReadCompleted"/> — record's <c>SpanId</c> matches the original DiskWrite span, <c>durationTicks</c> is the
    /// full async tail including the OS write completion.
    /// </summary>
    PageCacheDiskWriteCompleted = 57,

    /// <summary>
    /// Completion marker for an async <see cref="PageCacheFlush"/>. Required: <c>pageCount: i32</c> (stored in the primary <c>filePageIndex</c>
    /// slot per Flush convention). Record's <c>durationTicks</c> covers the full flush tail: <c>max(WriteAsync completions)</c> + <c>fsync</c>.
    /// The delta between this record's duration and the max of the enclosed <see cref="PageCacheDiskWriteCompleted"/> events is pure fsync cost.
    /// </summary>
    PageCacheFlushCompleted = 58,

    /// <summary>Page cache backpressure wait — clock-sweep retry loop couldn't find a free page. Required: <c>retryCount: i32</c>,
    /// <c>dirtyCount: i32</c>, <c>epochCount: i32</c>. Suppressed by default alongside other PageCache.* kinds.</summary>
    PageCacheBackpressure = 59,

    // ── Cluster migration (span) ──

    /// <summary>Cluster migration between spatial cells. Required: <c>archetypeId: u16</c>, <c>migrationCount: i32</c>.</summary>
    ClusterMigration = 60,

    // ── .NET runtime GC suspension (span) ──

    /// <summary>
    /// .NET runtime Execution-Engine suspension window. Opened on <c>GCSuspendEEBegin_V1</c> (ETW id 9), closed on <c>GCRestartEEEnd_V1</c> (ETW id 3).
    /// Payload: <c>u8 reason</c> (<see cref="GcSuspendReason"/>), <c>u8 optMask</c> (reserved).
    /// <c>ParentSpanId</c> is always 0 (process-level, not caller-attributed). No <see cref="System.Diagnostics.Activity"/> capture.
    /// </summary>
    GcSuspension = 75,

    /// <summary>
    /// Per-tick gauge snapshot — packed bundle of (gaugeId, value) pairs emitted once per tick by the scheduler thread at end-of-tick.
    /// Instant-style record: no span header extension, no duration semantics. Common header + fixed prefix
    /// (<c>u32 tickNumber</c>, <c>u16 fieldCount</c>, <c>u32 flags</c>) then repeated
    /// <c>{u16 gaugeId; u8 valueKind; [4 or 8 B] value}</c> entries. Gated on <c>TelemetryConfig.ProfilerGaugesActive</c>.
    /// See <see cref="GaugeId"/> for the wire-stable gauge ID registry.
    /// </summary>
    /// <remarks>
    /// Although this kind's numeric value is ≥ 10, it is <b>not</b> a span record. <see cref="TraceEventKindExtensions.IsSpan"/> explicitly
    /// excludes it so the consumer never tries to read the 25-byte span header extension after the common header.
    /// </remarks>
    PerTickSnapshot = 76,

    /// <summary>
    /// Per-slot thread identity — emitted once when a producer thread claims its slot. Carries the managed thread ID and a UTF-8 thread name
    /// so the viewer can label lanes with something meaningful ("DagScheduler", "TyphonProfilerConsumer", pool worker name, ...) instead of
    /// just the numeric slot index. Wire layout after the 12-byte common header: <c>i32 managedThreadId</c>, <c>u16 nameByteCount</c>,
    /// <c>byte[nameByteCount] nameUtf8</c>. Instant-style (no span-header extension); see <see cref="TraceEventKindExtensions.IsSpan"/>.
    /// </summary>
    ThreadInfo = 77,

    // ── WAL (span) ──

    /// <summary>WAL writer drain-write-signal cycle. Required: <c>batchByteCount: i32</c>, <c>frameCount: i32</c>, <c>highLsn: i64</c>.</summary>
    WalFlush = 80,

    /// <summary>WAL segment rotation. Required: <c>newSegmentIndex: i32</c>.</summary>
    WalSegmentRotate = 81,

    /// <summary>Thread blocked waiting for WAL durability. Required: <c>targetLsn: i64</c>. Emitted on the calling thread, not the WAL writer.</summary>
    WalWait = 82,

    // ── Checkpoint (span) ──

    /// <summary>Full checkpoint cycle. Required: <c>targetLsn: i64</c>, <c>reason: u8</c> (<see cref="CheckpointReason"/>). Optional: <c>dirtyPageCount: i32</c>.</summary>
    CheckpointCycle = 83,

    /// <summary>Checkpoint phase: collect dirty page indices.</summary>
    CheckpointCollect = 84,

    /// <summary>Checkpoint phase: write dirty pages to data file. Optional: <c>writtenCount: i32</c>.</summary>
    CheckpointWrite = 85,

    /// <summary>Checkpoint phase: fsync data file.</summary>
    CheckpointFsync = 86,

    /// <summary>Checkpoint phase: transition UoW entries from WalDurable to Committed. Optional: <c>transitionedCount: i32</c>.</summary>
    CheckpointTransition = 87,

    /// <summary>Checkpoint phase: recycle WAL segments below checkpoint LSN. Optional: <c>recycledCount: i32</c>.</summary>
    CheckpointRecycle = 88,

    // ── Statistics (span) ──

    /// <summary>Statistics rebuild for a ComponentTable. Required: <c>entityCount: i32</c>, <c>mutationCount: i32</c>, <c>samplingInterval: i32</c>.</summary>
    StatisticsRebuild = 89,

    // ═══════════════════════════════════════════════════════════════════════════════════════
    // Concurrency tracing (Phase 2, #280) — INSTANT events 90–116, no span header extension.
    // All gated on TelemetryConfig.Concurrency*Active leaf flags (Phase 1 Tier-2 mechanism).
    // See claude/design/Profiler/07-tracing-instrumentation/02-concurrency.md for details.
    //
    // TODO: per-resource contention metrics regression. The pre-#280 IContentionTarget pathway fed
    // ManagedPagedMMF/ComponentTable counters that surfaced as Contention.* columns in the resource
    // graph + Workbench Schema Inspector. Phase 2 deleted the producer (per Q2). Consumer-side
    // plumbing (IMetricWriter.WriteContention, ContentionMetrics, NodeSnapshot.Contention,
    // ResourceSnapshot.FindContentionHotspots, ResourceMetricsExporter contention OTel exports,
    // ResourceAlert.CascadingEffects, ResourceHealthChecker hotspot reporting) was removed in the
    // same change. To restore: either (a) add a per-lock id field to the wire format below and
    // build a trace-ring-fed aggregator, or (b) accept the loss as the new baseline. Tracked by
    // the umbrella issue at #277.
    // ═══════════════════════════════════════════════════════════════════════════════════════

    // ── AccessControl (instant, large ref-counted lock) ──

    /// <summary>AccessControl shared (reader) acquire success. Payload: <c>threadId: u16</c>, <c>hadToWait: u8</c>, <c>elapsedUs: u16</c>.</summary>
    ConcurrencyAccessControlSharedAcquire = 90,

    /// <summary>AccessControl shared (reader) release. Payload: <c>threadId: u16</c>.</summary>
    ConcurrencyAccessControlSharedRelease = 91,

    /// <summary>AccessControl exclusive (writer) acquire success. Payload: <c>threadId: u16</c>, <c>hadToWait: u8</c>, <c>elapsedUs: u16</c>.</summary>
    ConcurrencyAccessControlExclusiveAcquire = 92,

    /// <summary>AccessControl exclusive (writer) release. Payload: <c>threadId: u16</c>.</summary>
    ConcurrencyAccessControlExclusiveRelease = 93,

    /// <summary>AccessControl shared→exclusive promotion (or exclusive→shared demotion via variant byte). Payload: <c>elapsedUs: u16</c>, <c>variant: u8</c> (0=promote, 1=demote).</summary>
    ConcurrencyAccessControlPromotion = 94,

    /// <summary>AccessControl contention marker — flag-set instant, fires when a thread enters a wait. Payload: empty.</summary>
    ConcurrencyAccessControlContention = 95,

    // ── AccessControlSmall (instant, compact 4-byte lock) ──

    /// <summary>AccessControlSmall shared acquire. Payload: <c>threadId: u16</c>.</summary>
    ConcurrencyAccessControlSmallSharedAcquire = 96,

    /// <summary>AccessControlSmall shared release. Payload: <c>threadId: u16</c>.</summary>
    ConcurrencyAccessControlSmallSharedRelease = 97,

    /// <summary>AccessControlSmall exclusive acquire. Payload: <c>threadId: u16</c>.</summary>
    ConcurrencyAccessControlSmallExclusiveAcquire = 98,

    /// <summary>AccessControlSmall exclusive release. Payload: <c>threadId: u16</c>.</summary>
    ConcurrencyAccessControlSmallExclusiveRelease = 99,

    /// <summary>AccessControlSmall contention marker. Payload: empty.</summary>
    ConcurrencyAccessControlSmallContention = 100,

    // ── ResourceAccessControl (instant, three-mode lock: Accessing/Modify/Destroy) ──

    /// <summary>ResourceAccessControl Accessing-mode acquire (try or wait). Payload: <c>success: u8</c>, <c>accessingCount: u8</c>, <c>elapsedUs: u16</c>.</summary>
    ConcurrencyResourceAccessing = 101,

    /// <summary>ResourceAccessControl Modify-mode acquire (try or wait). Payload: <c>success: u8</c>, <c>threadId: u16</c>, <c>elapsedUs: u16</c>.</summary>
    ConcurrencyResourceModify = 102,

    /// <summary>ResourceAccessControl Destroy-mode acquire. Payload: <c>success: u8</c>, <c>elapsedUs: u16</c>.</summary>
    ConcurrencyResourceDestroy = 103,

    /// <summary>ResourceAccessControl Modify promotion slow path (wait for accessors to drain). Payload: <c>elapsedUs: u16</c>.</summary>
    ConcurrencyResourceModifyPromotion = 104,

    /// <summary>ResourceAccessControl contention marker. Payload: empty.</summary>
    ConcurrencyResourceContention = 105,

    // ── Epoch (instant, EBR scope lifecycle) ──

    /// <summary>EpochGuard scope enter (PinCurrentThread). Payload: <c>currentEpoch: u32</c>, <c>depthBefore: u8</c>, <c>isDormantToActive: u8</c>.</summary>
    ConcurrencyEpochScopeEnter = 106,

    /// <summary>EpochGuard scope exit (Dispose). Payload: <c>newEpoch: u32</c>, <c>isOutermost: u8</c>.</summary>
    ConcurrencyEpochScopeExit = 107,

    /// <summary>GlobalEpoch advance (Interlocked.Increment in ExitScope/ExitScopeUnordered). Payload: <c>newEpoch: u32</c>.</summary>
    ConcurrencyEpochAdvance = 108,

    /// <summary>RefreshScope — bump epoch mid-scope to release retired memory while staying pinned. Payload: <c>oldEpoch: u32</c>, <c>newEpoch: u32</c>.</summary>
    ConcurrencyEpochRefresh = 109,

    /// <summary>EpochThreadRegistry slot claim. Payload: <c>slotIndex: u16</c>, <c>threadId: u16</c>, <c>activeCount: u16</c>.</summary>
    ConcurrencyEpochSlotClaim = 110,

    /// <summary>EpochThreadRegistry dead-thread slot reclaim. Payload: <c>slotIndex: u16</c>, <c>oldOwner: u16</c>, <c>newOwner: u16</c>.</summary>
    ConcurrencyEpochSlotReclaim = 111,

    // ── AdaptiveWaiter (instant, transition only) ──

    /// <summary>AdaptiveWaiter Wait() call yielded or slept (transition only — NOT per-spin). Payload: <c>spinCountBefore: u16</c>, <c>kind: u8</c> (1=yield, 2=sleep).</summary>
    ConcurrencyAdaptiveWaiterYieldOrSleep = 112,

    // ── OlcLatch (instant, optimistic-latch coordinator) ──

    /// <summary>OlcLatch.TryWriteLock failed (raced or write-locked). Payload: <c>versionBefore: u32</c>, <c>success: u8</c> (always 0 on emit).</summary>
    ConcurrencyOlcLatchWriteLockAttempt = 113,

    /// <summary>OlcLatch.WriteUnlock — version bumped from oldVersion to newVersion. Payload: <c>oldVersion: u32</c>, <c>newVersion: u32</c>.</summary>
    ConcurrencyOlcLatchWriteUnlock = 114,

    /// <summary>OlcLatch.MarkObsolete — node retired, future readers will fail validation. Payload: <c>version: u32</c>.</summary>
    ConcurrencyOlcLatchMarkObsolete = 115,

    /// <summary>OlcLatch.ValidateVersion failed — version mismatch detected on optimistic re-read. Payload: <c>expectedVersion: u32</c>, <c>actualVersion: u32</c>.</summary>
    ConcurrencyOlcLatchValidationFail = 116,

    // ═══════════════════════════════════════════════════════════════════════════════════════
    // Spatial tracing (Phase 3, #281) — IDs 117-145.
    // Mix of spans (queries, RTree structural, Maintain wrappers, TierIndex.Rebuild,
    // Trigger.Eval) and instants (Grid ops, Cell:Index ops, ClusterMigration Detect/Queue/
    // Hysteresis, TierIndex.VersionSkip, Maintain.AabbValidate/BackPointerWrite, Trigger
    // Region/Occupant/Cache).
    //
    // Existing kind 60 (ClusterMigration) is logically retitled to Spatial:ClusterMigration:Execute
    // in codec metadata + Workbench labels — wire ID unchanged.
    //
    // See claude/design/Profiler/07-tracing-instrumentation/03-spatial.md for details.
    // ═══════════════════════════════════════════════════════════════════════════════════════

    // ── Spatial Queries (span — one per iterator pass) ──

    /// <summary>AABB query iterator pass. Payload: <c>nodesVisited: u16</c>, <c>leavesEntered: u16</c>, <c>resultCount: u16</c>, <c>restartCount: u8</c>, <c>categoryMask: u32</c>.</summary>
    SpatialQueryAabb = 117,

    /// <summary>Radius (sphere) query iterator pass. Payload: <c>nodesVisited: u16</c>, <c>resultCount: u16</c>, <c>radius: f32</c>, <c>restartCount: u8</c>.</summary>
    SpatialQueryRadius = 118,

    /// <summary>Ray query iterator pass. Payload: <c>nodesVisited: u16</c>, <c>resultCount: u16</c>, <c>maxDist: f32</c>, <c>restartCount: u8</c>.</summary>
    SpatialQueryRay = 119,

    /// <summary>Frustum query iterator pass. Payload: <c>nodesVisited: u16</c>, <c>resultCount: u16</c>, <c>planeCount: u8</c>, <c>restartCount: u8</c>.</summary>
    SpatialQueryFrustum = 120,

    /// <summary>KNN query (full call covering all radius-expansion iterations). Payload: <c>k: u16</c>, <c>iterCount: u8</c>, <c>finalRadius: f32</c>, <c>resultCount: u16</c>.</summary>
    SpatialQueryKnn = 121,

    /// <summary>Count query (CountInAABB / CountInRadius merged via variant byte). Payload: <c>variant: u8</c> (0=AABB, 1=Radius), <c>nodesVisited: u16</c>, <c>resultCount: i32</c>.</summary>
    SpatialQueryCount = 122,

    // ── Spatial RTree structural (span) ──

    /// <summary>RTree insert (descent + leaf write or split fallout). Payload: <c>entityId: i64</c>, <c>depth: u8</c>, <c>didSplit: u8</c>, <c>restartCount: u8</c>.</summary>
    SpatialRTreeInsert = 123,

    /// <summary>RTree remove (leaf modify, possibly cascade up). Payload: <c>entityId: i64</c>, <c>leafCollapse: u8</c>.</summary>
    SpatialRTreeRemove = 124,

    /// <summary>RTree node split (sub-event of Insert when leaf is full). Payload: <c>depth: u8</c>, <c>splitAxis: u8</c>, <c>leftCount: u8</c>, <c>rightCount: u8</c>.</summary>
    SpatialRTreeNodeSplit = 125,

    /// <summary>RTree bulk-load (init-time). Payload: <c>entityCount: i32</c>, <c>leafCount: i32</c>.</summary>
    SpatialRTreeBulkLoad = 126,

    // ── Spatial Coarse Grid (instant) ──

    /// <summary>Cell tier change (only emits when SetCellTier actually flips the tier byte). Payload: <c>cellKey: i32</c>, <c>oldTier: u8</c>, <c>newTier: u8</c>.</summary>
    SpatialGridCellTierChange = 127,

    /// <summary>Cell occupancy change (Increment/Decrement merged via signed delta). Payload: <c>cellKey: i32</c>, <c>delta: i8</c>, <c>occBefore: u16</c>, <c>occAfter: u16</c>.</summary>
    SpatialGridOccupancyChange = 128,

    /// <summary>Cluster-cell assignment (cluster claimed in a particular cell). Payload: <c>clusterChunkId: i32</c>, <c>cellKey: i32</c>, <c>archetypeId: u16</c>.</summary>
    SpatialGridClusterCellAssign = 129,

    // ── Spatial Per-cell cluster index (instant) ──

    /// <summary>CellSpatialIndex.Add — append cluster to cell's SoA. Payload: <c>cellKey: i32</c>, <c>slot: i32</c>, <c>clusterChunkId: i32</c>, <c>capacity: i32</c>.</summary>
    SpatialCellIndexAdd = 130,

    /// <summary>CellSpatialIndex.UpdateAt — in-place AABB rewrite at slot. Payload: <c>cellKey: i32</c>, <c>slot: i32</c>.</summary>
    SpatialCellIndexUpdate = 131,

    /// <summary>CellSpatialIndex.RemoveAt — swap-with-last. Payload: <c>cellKey: i32</c>, <c>slot: i32</c>, <c>swappedClusterId: i32</c>.</summary>
    SpatialCellIndexRemove = 132,

    // ── Spatial Cluster Migration (instant; Execute = existing kind 60) ──

    /// <summary>Cluster migration detected (cell crossing observed, queued for execute). Payload: <c>archetypeId: u16</c>, <c>clusterChunkId: i32</c>, <c>oldCellKey: i32</c>, <c>newCellKey: i32</c>.</summary>
    SpatialClusterMigrationDetect = 133,

    /// <summary>Cluster migration queue append. Payload: <c>archetypeId: u16</c>, <c>clusterChunkId: i32</c>, <c>queueLen: u16</c>.</summary>
    SpatialClusterMigrationQueue = 134,

    /// <summary>Cluster migration hysteresis absorb — crossing detected but absorbed by margin (no migration queued). Payload: <c>archetypeId: u16</c>, <c>clusterChunkId: i32</c>, <c>escapeDistSq: f32</c>.</summary>
    SpatialClusterMigrationHysteresis = 135,

    // ── Spatial Tier Index ──

    /// <summary>TierClusterIndex rebuild span. Payload: <c>archetypeId: u16</c>, <c>clusterCount: i32</c>, <c>oldVersion: i32</c>, <c>newVersion: i32</c>.</summary>
    SpatialTierIndexRebuild = 136,

    /// <summary>TierClusterIndex version-skip — rebuild bypassed because version unchanged. Payload: <c>archetypeId: u16</c>, <c>version: i32</c>, <c>reason: u8</c>.</summary>
    SpatialTierIndexVersionSkip = 137,

    // ── Spatial Maintain pipeline ──

    /// <summary>Maintain.InsertSpatial — wraps RTree.Insert + back-pointer + occupancy. Payload: <c>entityPK: i64</c>, <c>componentTypeId: u16</c>, <c>didDegenerate: u8</c>.</summary>
    SpatialMaintainInsert = 138,

    /// <summary>Maintain.UpdateSpatial slow-path — escape detected, remove + reinsert. Payload: <c>entityPK: i64</c>, <c>componentTypeId: u16</c>, <c>escapeDistSq: f32</c>.</summary>
    SpatialMaintainUpdateSlowPath = 139,

    /// <summary>Degenerate AABB validation failure. Payload: <c>entityPK: i64</c>, <c>componentTypeId: u16</c>, <c>opcode: u8</c> (0=insert, 1=update, 2=remove).</summary>
    SpatialMaintainAabbValidate = 140,

    /// <summary>Spatial back-pointer write (componentChunkId → leafChunkId+slotIndex). Payload: <c>componentChunkId: i32</c>, <c>leafChunkId: i32</c>, <c>slotIndex: u16</c>.</summary>
    SpatialMaintainBackPointerWrite = 141,

    // ── Spatial Trigger system ──

    /// <summary>Trigger region create/destroy (merged via op variant). Payload: <c>op: u8</c> (0=create, 1=destroy), <c>regionId: u16</c>, <c>categoryMask: u32</c>.</summary>
    SpatialTriggerRegion = 142,

    /// <summary>Trigger region eval span. Payload: <c>regionId: u16</c>, <c>occupantCount: u16</c>, <c>enterCount: u16</c>, <c>leaveCount: u16</c>.</summary>
    SpatialTriggerEval = 143,

    /// <summary>Trigger occupant XOR-diff stats (NO bitmap). Payload: <c>regionId: u16</c>, <c>prevCount: u16</c>, <c>currCount: u16</c>, <c>enterCount: u16</c>, <c>leaveCount: u16</c>.</summary>
    SpatialTriggerOccupantDiff = 144,

    /// <summary>Trigger static-tree cache invalidate (mutation observed bumps cached version). Payload: <c>regionId: u16</c>, <c>oldVersion: i32</c>, <c>newVersion: i32</c>.</summary>
    SpatialTriggerCacheInvalidate = 145,

    // ═══════════════════════════════════════════════════════════════════════════════════════
    // Scheduler & Runtime tracing (Phase 4, #282) — IDs 146-164.
    //
    // Existing kind 10 (SchedulerChunk) is wire-stable; logically retitled to Scheduler:Chunk
    // in codec metadata + Workbench labels — no renumber.
    //
    // Existing kind 12 (SystemSkipped) gets a wire-additive payload extension (1 → 5 bytes:
    // reason u8 + wouldBeChunkCount u16 + successorsUnblocked u16). Decoders that respect the
    // record-size header continue to work; old shorter payloads default the extra fields to 0.
    //
    // See claude/design/Profiler/07-tracing-instrumentation/04-scheduler-runtime.md.
    // ═══════════════════════════════════════════════════════════════════════════════════════

    // ── Scheduler:System (instant + span) ──

    /// <summary>System first chunk dispatched — execution boundary (after CAS, before work). Payload: <c>sysIdx: u16</c>.</summary>
    SchedulerSystemStartExecution = 146,

    /// <summary>System completion (RecordSystemDone). Payload: <c>sysIdx: u16</c>, <c>reason: u8</c> (0=ok, 1..N=SkipReason), <c>durationUs: u32</c>.</summary>
    SchedulerSystemCompletion = 147,

    /// <summary>Ready → first-grab latency (queue wait). Payload: <c>sysIdx: u16</c>, <c>queueWaitUs: u32</c>.</summary>
    SchedulerSystemQueueWait = 148,

    /// <summary>Single-threaded system execution span. Payload: <c>sysIdx: u16</c>, <c>isParallelQuery: u8</c>, <c>chunkCount: u16</c>.</summary>
    SchedulerSystemSingleThreaded = 149,

    // ── Scheduler:Worker (instant + span) ──

    /// <summary>Worker idle span (per spin spell). Payload: <c>workerId: u8</c>, <c>spinCount: u16</c>, <c>idleUs: u32</c>.</summary>
    SchedulerWorkerIdle = 150,

    /// <summary>Worker wake from kernel signal. Payload: <c>workerId: u8</c>, <c>delayUs: u32</c>.</summary>
    SchedulerWorkerWake = 151,

    /// <summary>Worker between-tick wait (kernel wait span). Payload: <c>workerId: u8</c>, <c>waitUs: u32</c>, <c>wakeReason: u8</c> (0=signal, 1=shutdown).</summary>
    SchedulerWorkerBetweenTick = 152,

    // ── Scheduler:Dispense (instant) ──

    /// <summary>Successful chunk-grab CAS. Payload: <c>sysIdx: u16</c>, <c>chunkIdx: i32</c>, <c>workerId: u8</c>.</summary>
    SchedulerDispense = 153,

    // ── Scheduler:Dependency (instant + span) ──

    /// <summary>Successor mark-ready (only when depsLeft → 0). Payload: <c>fromSysIdx: u16</c>, <c>toSysIdx: u16</c>, <c>fanOut: u16</c>, <c>predRemain: u16</c>.</summary>
    SchedulerDependencyReady = 154,

    /// <summary>Dependency fan-out span over OnSystemComplete successor loop. Payload: <c>completingSysIdx: u16</c>, <c>succCount: u16</c>, <c>skippedCount: u16</c>.</summary>
    SchedulerDependencyFanOut = 155,

    // ── Scheduler:Overload (instant) ──

    /// <summary>Overload level transition (only on change). Payload: <c>prevLvl: u8</c>, <c>newLvl: u8</c>, <c>ratio: f32</c>, <c>queueDepth: i32</c>, <c>oldMul: u8</c>, <c>newMul: u8</c>.</summary>
    SchedulerOverloadLevelChange = 156,

    /// <summary>Per-system shed/throttle decision (when overload level &gt; Normal). Payload: <c>sysIdx: u16</c>, <c>level: u8</c>, <c>divisor: u16</c>, <c>decision: u8</c> (0=runIfFalse, 1=throttled, 2=shed).</summary>
    SchedulerOverloadSystemShed = 157,

    /// <summary>Tick multiplier applied (per tick). Payload: <c>tick: i64</c>, <c>multiplier: u8</c>, <c>intervalTicks: u8</c>.</summary>
    SchedulerOverloadTickMultiplier = 158,

    // ── Scheduler:Graph (span) ──

    /// <summary>DAG build at startup. Payload: <c>sysCount: u16</c>, <c>edgeCount: u16</c>, <c>topoLen: u16</c>.</summary>
    SchedulerGraphBuild = 159,

    /// <summary>DAG rebuild (dynamic system registration — design stub, no producer in Phase 4). Payload: <c>oldSysCount: u16</c>, <c>newSysCount: u16</c>, <c>reason: u8</c>.</summary>
    SchedulerGraphRebuild = 160,

    // ── Runtime:Phase + Transaction (instant + span) ──

    /// <summary>UoW create (per tick, OnTickStart). Payload: <c>tick: i64</c>.</summary>
    RuntimePhaseUoWCreate = 161,

    /// <summary>UoW flush (per tick, OnTickEnd). Payload: <c>tick: i64</c>, <c>changeCount: i32</c>.</summary>
    RuntimePhaseUoWFlush = 162,

    /// <summary>Per-system Transaction lifecycle span. Payload: <c>sysIdx: u16</c>, <c>txDurUs: u32</c>, <c>success: u8</c>.</summary>
    RuntimeTransactionLifecycle = 163,

    // ── Runtime:Subscription (span) ──

    /// <summary>Subscription output phase (per tick). Payload: <c>tick: i64</c>, <c>level: u8</c>, <c>clientCount: u16</c>, <c>viewsRefreshed: u16</c>, <c>deltasPushed: u32</c>, <c>overflowCount: u16</c>.</summary>
    RuntimeSubscriptionOutputExecute = 164,

    // ═══════════════════════════════════════════════════════════════════════════════════════
    // Storage & Memory tracing (Phase 5, #283) — IDs 165-172.
    //
    // Existing kind 55 (PageEvicted) gets a wire-additive payload extension (4 B → 5 B).
    // The producer always emits the OptDirtyBit mask flag and a trailing 1-byte dirtyBit;
    // older decoders that don't recognise the mask bit ignore it, and the record-size
    // header in the common header tells them how many bytes to skip — wire-stable.
    //
    // Existing kinds 56/57/58 (DiskRead/Write/Flush Completed) gain a producer-side
    // duration-threshold gate (config-driven, no wire change).
    //
    // See claude/design/Profiler/07-tracing-instrumentation/05-storage-memory.md.
    // ═══════════════════════════════════════════════════════════════════════════════════════

    // ── Storage:PageCache (span — joins existing kinds 50-59) ──

    /// <summary>Dirty-walk during checkpoint (collect dirty page indices). Payload: <c>rangeStart: i32, rangeLen: i32, dirtyMs: i32</c>.</summary>
    StoragePageCacheDirtyWalk = 165,

    // ── Storage:Segment (instant) ──

    /// <summary>LogicalSegment.Create. Payload: <c>segmentId: i32, pageCount: i32</c>.</summary>
    StorageSegmentCreate = 166,

    /// <summary>LogicalSegment.Grow. Payload: <c>segmentId: i32, oldLen: i32, newLen: i32</c>.</summary>
    StorageSegmentGrow = 167,

    /// <summary>LogicalSegment.Load. Payload: <c>segmentId: i32, pageCount: i32</c>.</summary>
    StorageSegmentLoad = 168,

    // ── Storage:ChunkSegment (instant) ──

    /// <summary>ChunkBasedSegment.Grow. Payload: <c>stride: i32, oldCap: i32, newCap: i32</c>.</summary>
    StorageChunkSegmentGrow = 169,

    // ── Storage:FileHandle (instant, op variant) ──

    /// <summary>File handle Open/Close (merged via op variant). Payload: <c>op: u8</c> (0=open, 1=close), <c>filePathId: i32, modeOrReason: u8</c>.</summary>
    StorageFileHandle = 170,

    // ── Storage:OccupancyMap (instant) ──

    /// <summary>OccupancyMap L3 grow path. Payload: <c>oldCap: i32, newCap: i32</c>.</summary>
    StorageOccupancyMapGrow = 171,

    // ── Memory:AlignmentWaste (instant, only on waste &gt; 0) ──

    /// <summary>Alignment-induced waste on an allocation (only emitted when waste &gt; 0). Payload: <c>size: i32, alignment: i32, wastePctHundredths: u16</c>.</summary>
    MemoryAlignmentWaste = 172,

    // ═══════════════════════════════════════════════════════════════════════════════════════
    // Data plane tracing (Phase 6, #284) — IDs 173-186.
    //
    // Existing kind 21 (TransactionRollback) gets a wire-additive payload extension via the
    // OptReason mask bit (0x02) + trailing reason u8. Existing kind 22 (TransactionCommitComponent)
    // gets a wire-additive +rowCount i32 via OptRowCount (0x04). Both follow the Phase 5 pattern.
    //
    // See claude/design/Profiler/07-tracing-instrumentation/06-data-plane.md.
    // ═══════════════════════════════════════════════════════════════════════════════════════

    // ── Data:Transaction (mostly span; Conflict is instant) ──

    /// <summary>Transaction Init / snapshot acquire span. Payload: <c>tsn: i64, uowId: u16</c>.</summary>
    DataTransactionInit = 173,

    /// <summary>Transaction PrepareForMutation span (high-freq; default-suppressed). Payload: <c>tsn: i64</c>.</summary>
    DataTransactionPrepare = 174,

    /// <summary>Transaction Validate span — wraps the commit-loop validation pass. Payload: <c>tsn: i64, entryCount: i32</c>.</summary>
    DataTransactionValidate = 175,

    /// <summary>Transaction Conflict instant — fired only when an actual conflict is detected during commit. Payload: <c>tsn: i64, pk: i64, componentTypeId: i32, conflictType: u8</c>.</summary>
    DataTransactionConflict = 176,

    /// <summary>Transaction Cleanup span — wraps deferred-cleanup batch enqueue. Payload: <c>tsn: i64, entityCount: i32</c>.</summary>
    DataTransactionCleanup = 177,

    // ── Data:MVCC ──

    /// <summary>MVCC chain-walk slow path (only emitted when full walk runs, not the single-entry fast path). Payload: <c>tsn: i64, chainLen: u8, visibility: u8</c>.</summary>
    DataMvccChainWalk = 178,

    /// <summary>MVCC version-cleanup span. Payload: <c>pk: i64, entriesFreed: u16</c>.</summary>
    DataMvccVersionCleanup = 179,

    // ── Data:Index:BTree ──

    /// <summary>BTree FindLeaf slow-path instant (b-link hop / OLC restart). Payload: <c>retryReason: u8, restartCount: u8</c>.</summary>
    DataIndexBTreeSearch = 180,

    /// <summary>BTree range-scan span (covers the whole enumeration). Payload: <c>resultCount: i32, restartCount: u8</c>.</summary>
    DataIndexBTreeRangeScan = 181,

    /// <summary>BTree range-scan OLC revalidate instant (per restart). Payload: <c>restartCount: u8</c>.</summary>
    DataIndexBTreeRangeScanRevalidate = 182,

    /// <summary>BTree pessimistic fallback after OLC failure. Payload: <c>reason: u8</c> (0=LeafFull, 1=OlcFail).</summary>
    DataIndexBTreeRebalanceFallback = 183,

    /// <summary>BTree multi-value bulk-insert span. Payload: <c>bufferId: i32, entryCount: i32</c>.</summary>
    DataIndexBTreeBulkInsert = 184,

    /// <summary>BTree root operation (Init or Split, merged via op variant). Payload: <c>op: u8</c> (0=Init, 1=Split), <c>rootChunkId: i32, height: u8</c>.</summary>
    DataIndexBTreeRoot = 185,

    /// <summary>BTree node CoW (PreDirtyForWrite triggered). Payload: <c>srcChunkId: i32, dstChunkId: i32</c>.</summary>
    DataIndexBTreeNodeCow = 186,

    // ═══════════════════════════════════════════════════════════════════════════════════════
    // Query, ECS:Query depth, ECS:View depth tracing (Phase 7, #285) — IDs 187-213.
    //
    // D2 (additive flavor): kind 32 (EcsQueryExecute) gets a wire-additive +variant u8 byte.
    // Kinds 33 (EcsQueryCount) and 34 (EcsQueryAny) are SOFT-DEPRECATED — still emitted by
    // existing factories, never reuse the wire IDs. Future producers should switch to kind 32
    // with EcsQueryVariant byte (Execute=0, Count=1, Any=2).
    //
    // Per-entity field eval is explicitly NOT instrumented (>100 k/sec hot path).
    //
    // See claude/design/Profiler/07-tracing-instrumentation/07-query-ecs-view.md.
    // ═══════════════════════════════════════════════════════════════════════════════════════

    // ── Query (mostly span; PrimarySelect + StorageMode are instants) ──

    /// <summary>Query expression parse. Payload: <c>predicateCount: u16, branchCount: u8</c>.</summary>
    QueryParse = 187,

    /// <summary>Query DNF normalization. Payload: <c>inBranches: u16, outBranches: u16</c>.</summary>
    QueryParseDnf = 188,

    /// <summary>Query plan build. Payload: <c>evaluatorCount: u8, indexFieldIdx: u16, rangeMin: i64, rangeMax: i64</c>.</summary>
    QueryPlan = 189,

    /// <summary>Selectivity estimate. Payload: <c>fieldIdx: u16, cardinality: i64</c>.</summary>
    QueryEstimate = 190,

    /// <summary>Primary stream selection (instant). Payload: <c>candidates: u8, winnerIdx: u8, reason: u8</c>.</summary>
    QueryPlanPrimarySelect = 191,

    /// <summary>Predicate ordering / sort by cardinality. Payload: <c>evaluatorCount: u8, sortNs: u32</c>.</summary>
    QueryPlanSort = 192,

    /// <summary>B+Tree range-scan dispatch. Payload: <c>primaryFieldIdx: u16, mode: u8</c> (0=Single, 1=Multi).</summary>
    QueryExecuteIndexScan = 193,

    /// <summary>Index iteration (chunk-grain). Payload: <c>chunkCount: i32, entryCount: i32</c>.</summary>
    QueryExecuteIterate = 194,

    /// <summary>Filter pass (post-primary). Payload: <c>filterCount: u8, rejectedCount: i32</c>.</summary>
    QueryExecuteFilter = 195,

    /// <summary>Pagination (Skip/Take). Payload: <c>skip: i32, take: i32, earlyTerm: u8</c>.</summary>
    QueryExecutePagination = 196,

    /// <summary>Storage-mode dispatch instant. Payload: <c>mode: u8</c> (0=SV, 1=Versioned, 2=Transient).</summary>
    QueryExecuteStorageMode = 197,

    /// <summary>Count-only path. Payload: <c>resultCount: i32</c>.</summary>
    QueryCount = 198,

    // ── ECS:Query depth (mostly span; MaskAnd/Constraint/Spatial are instants) ──

    /// <summary>EcsQuery construct (archetype resolution). Payload: <c>targetArchId: u16, polymorphic: u8, maskSize: u8</c>.</summary>
    EcsQueryConstruct = 199,

    /// <summary>EcsQuery mask AND. Payload: <c>bitsBefore: u16, bitsAfter: u16, opType: u8</c>.</summary>
    EcsQueryMaskAnd = 200,

    /// <summary>EcsQuery subtree expand. Payload: <c>subtreeCount: u16, rootId: u16</c>.</summary>
    EcsQuerySubtreeExpand = 201,

    /// <summary>EcsQuery Enabled/Disabled constraint add. Payload: <c>typeId: u16, enableBit: u8</c>.</summary>
    EcsQueryConstraintEnabled = 202,

    /// <summary>EcsQuery spatial predicate attach. Payload: <c>spatialType: u8, queryBox: 4xf32 (x1, y1, x2, y2)</c>.</summary>
    EcsQuerySpatialAttach = 203,

    // ── ECS:View depth (mix) ──

    /// <summary>View RefreshPull (full re-query branch). Payload: <c>queryNs: u32, archetypeMaskBits: u16</c>.</summary>
    EcsViewRefreshPull = 204,

    /// <summary>View IncrementalDrain. Payload: <c>deltaCount: i32, overflow: u8</c>.</summary>
    EcsViewIncrementalDrain = 205,

    /// <summary>View delta-buffer overflow (operationally critical — never default-suppressed). Payload: <c>currentTsn: i64, tailTsn: i64, marginPagesLost: u16</c>.</summary>
    EcsViewDeltaBufferOverflow = 206,

    /// <summary>View single-branch entry process (instant, high-freq, default-suppressed). Payload: <c>pk: i64, fieldIdx: u16, pass: u8</c>.</summary>
    EcsViewProcessEntry = 207,

    /// <summary>View OR-mode entry process (instant, high-freq, default-suppressed). Payload: <c>pk: i64, branchCount: u8, bitmapDelta: u32</c>.</summary>
    EcsViewProcessEntryOr = 208,

    /// <summary>View Refresh-from-overflow (Pull). Payload: <c>oldCount: i32, newCount: i32, requeryNs: u32</c>.</summary>
    EcsViewRefreshFull = 209,

    /// <summary>View RefreshFullOr. Payload: <c>oldCount: i32, newCount: i32, branchCount: u8</c>.</summary>
    EcsViewRefreshFullOr = 210,

    /// <summary>ViewRegistry Register. Payload: <c>viewId: u16, fieldIdx: u16, regCount: u16</c>.</summary>
    EcsViewRegistryRegister = 211,

    /// <summary>ViewRegistry Deregister. Payload: <c>viewId: u16, fieldIdx: u16, regCount: u16</c>.</summary>
    EcsViewRegistryDeregister = 212,

    /// <summary>View delta-cache miss (entity re-eval). Payload: <c>pk: i64, reason: u8</c>.</summary>
    EcsViewDeltaCacheMiss = 213,

    // ═══════════════════════════════════════════════════════════════════════════════════════
    // Durability tracing (Phase 8, #286) — IDs 214-234.
    //
    // D2 WAL split (additive flavor, mirrors Phase 7): kind 80 (WalFlush) is SOFT-DEPRECATED but
    // still emitted by BeginWalFlush. New producers should switch to the three split kinds 214-216
    // (QueueDrain / OsWrite / Signal) once tooling is updated. Producer migration is a follow-up.
    //
    // Recovery:Record (228), UoW:State (233), UoW:Deadline (234) are extreme-frequency and get
    // belt-and-suspenders deny-list entries on top of their leaf gates.
    //
    // See claude/design/Profiler/07-tracing-instrumentation/08-durability.md.
    // ═══════════════════════════════════════════════════════════════════════════════════════

    // ── Durability:WAL split + new (mostly span; GroupCommit/Queue/Frame are instants) ──

    /// <summary>WAL flush phase 1: drain coalesce. Payload: <c>bytesAligned: i32, frameCount: i32</c>.</summary>
    DurabilityWalQueueDrain = 214,

    /// <summary>WAL flush phase 2: OS write + fsync. Payload: <c>bytesAligned: i32, frameCount: i32, highLsn: i64</c>.</summary>
    DurabilityWalOsWrite = 215,

    /// <summary>WAL flush phase 3: LSN advance + signal. Payload: <c>highLsn: i64</c>.</summary>
    DurabilityWalSignal = 216,

    /// <summary>WAL group-commit boundary instant. Payload: <c>triggerMs: u16, producerThread: i32</c>.</summary>
    DurabilityWalGroupCommit = 217,

    /// <summary>WAL drain decision instant. Payload: <c>drainAttempt: u8, dataLen: i32, waitReason: u8</c>.</summary>
    DurabilityWalQueue = 218,

    /// <summary>WAL staging-buffer copy span. Payload: <c>bytesAligned: i32, pad: i32</c>.</summary>
    DurabilityWalBuffer = 219,

    /// <summary>WAL per-frame CRC instant (extreme-freq, deny-listed). Payload: <c>frameCount: u16, crcStart: u32</c>.</summary>
    DurabilityWalFrame = 220,

    /// <summary>WAL commit-buffer-full backpressure span. Payload: <c>waitUs: u32, producerThread: i32</c>.</summary>
    DurabilityWalBackpressure = 221,

    // ── Durability:Checkpoint depth (span) ──

    /// <summary>Checkpoint write-batch span. Payload: <c>writeBatchSize: i32, stagingAllocated: i32</c>.</summary>
    DurabilityCheckpointWriteBatch = 222,

    /// <summary>Checkpoint staging-exhaustion backpressure span. Payload: <c>waitMs: u32, exhausted: u8</c>.</summary>
    DurabilityCheckpointBackpressure = 223,

    /// <summary>Checkpoint between-cycle sleep span. Payload: <c>sleepMs: u32, wakeReason: u8</c>.</summary>
    DurabilityCheckpointSleep = 224,

    // ── Durability:Recovery (mix; Start + Record are instants) ──

    /// <summary>Recovery start instant. Payload: <c>checkpointLsn: i64, reason: u8</c>.</summary>
    DurabilityRecoveryStart = 225,

    /// <summary>Recovery discover/enumerate WAL segments. Payload: <c>segCount: i32, totalBytes: i64, firstSegId: i32</c>.</summary>
    DurabilityRecoveryDiscover = 226,

    /// <summary>Recovery per-segment scan. Payload: <c>segId: i32, recCount: i32, bytes: i64, truncated: u8</c>.</summary>
    DurabilityRecoverySegment = 227,

    /// <summary>Recovery per-record decode/apply (extreme-freq, deny-listed). Payload: <c>chunkType: u8, lsn: i64, size: i32</c>.</summary>
    DurabilityRecoveryRecord = 228,

    /// <summary>Recovery FPI torn-page repair span. Payload: <c>fpiCount: i32, repairedCount: i32, mismatches: i32</c>.</summary>
    DurabilityRecoveryFpi = 229,

    /// <summary>Recovery redo replay span. Payload: <c>recordsReplayed: i32, uowsReplayed: i32, durUs: u32</c>.</summary>
    DurabilityRecoveryRedo = 230,

    /// <summary>Recovery undo (voided UoWs) span. Payload: <c>voidedUowCount: i32</c>.</summary>
    DurabilityRecoveryUndo = 231,

    /// <summary>Recovery TickFence replay span. Payload: <c>tickFenceCount: i32, entries: i32, tickNumber: i64</c>.</summary>
    DurabilityRecoveryTickFence = 232,

    // ── Durability:UoW (instant, extreme-freq, deny-listed) ──

    /// <summary>UoW state transition instant. Payload: <c>from: u8, to: u8, uowId: u16, reason: u8</c>.</summary>
    DurabilityUowState = 233,

    /// <summary>UoW deadline check instant. Payload: <c>deadline: i64, remaining: i64, expired: u8</c>.</summary>
    DurabilityUowDeadline = 234,

    // ═══════════════════════════════════════════════════════════════════════════════════════
    // Subscription dispatch tracing (Phase 9, #287) — IDs 235-240. All spans.
    //
    // Phase 4 already shipped kind 164 (RuntimeSubscriptionOutputExecute) as the per-tick
    // parent. Phase 9 fills in the per-subscriber/per-client children. Producer wiring is
    // deferred per Q4 — dispatch path is still in flux per umbrella sequencing.
    //
    // See claude/design/Profiler/07-tracing-instrumentation/09-subscription-dispatch.md.
    // ═══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Per-subscriber invocation span (high-freq, deny-listed). Payload: <c>subscriberId: u32, viewId: u16, deltaCount: i32</c>.</summary>
    RuntimeSubscriptionSubscriber = 235,

    /// <summary>Delta-builder span. Payload: <c>viewId: u16, added: i32, removed: i32, modified: i32</c>.</summary>
    RuntimeSubscriptionDeltaBuild = 236,

    /// <summary>Per-client delta serialize span (high-freq, deny-listed). Payload: <c>clientId: u32, viewId: u16, bytes: i32, format: u8</c>.</summary>
    RuntimeSubscriptionDeltaSerialize = 237,

    /// <summary>Subscription transition (BeginSync) span. Payload: <c>clientId: u32, viewId: u16, entitySnapshot: i32</c>.</summary>
    RuntimeSubscriptionTransitionBeginSync = 238,

    /// <summary>Dead-client cleanup span. Payload: <c>deadCount: i32, deregCount: i32</c>.</summary>
    RuntimeSubscriptionOutputCleanup = 239,

    /// <summary>Dirty-bitmap supplement span (when ring overflows). Payload: <c>modifiedFromRing: i32, supplementCount: i32, unionSize: i32</c>.</summary>
    RuntimeSubscriptionDeltaDirtyBitmapSupplement = 240,

    // ── Scheduler:Metronome (span) — issue #289 follow-up: surface the timer thread's inter-tick wait ──

    /// <summary>
    /// Metronome wait between ticks (TickDriver thread, Sleep→Yield→Spin phases). Span covers the entire gap
    /// from the moment <c>ExecuteCallbacks</c> returned (or timer started) until the next tick fires. Payload:
    /// <c>scheduledTimestamp: i64, multiplier: u8, intentClass: u8, phaseFlags: u8</c> (11 B).
    /// <para><c>intentClass</c>: 0=CatchUp (target already past), 1=Throttled (mult&gt;1), 2=Headroom (mult==1 normal idle).</para>
    /// <para><c>phaseFlags</c> bits: 0x1=Sleep entered, 0x2=Yield entered, 0x4=Spin entered.</para>
    /// </summary>
    SchedulerMetronomeWait = 241,

    // ── Scheduler:Overload:Detector (instant) — issue #289 follow-up: per-tick OverloadDetector state ──

    /// <summary>
    /// Per-tick OverloadDetector state snapshot (instant on TickDriver). Carries the full state used to drive
    /// escalation/deescalation decisions so an offline trace can audit why the engine throttled. Payload:
    /// <c>tick: i64, overrunRatio: f32, consecutiveOverrun: u16, consecutiveUnderrun: u16,
    /// consecutiveQueueGrowth: u16, queueDepth: i32, level: u8, multiplier: u8</c> (24 B).
    /// </summary>
    SchedulerOverloadDetector = 242,

    /// <summary>
    /// Tick lifecycle phase span — covers the full duration of one <see cref="TickPhase"/> region inside
    /// <c>TyphonRuntime.OnTickEndInternal</c> (SystemDispatch, WriteTickFence, UoWFlush, OutputPhase, TierIndexRebuild, DormancySweep).
    /// Replaces the previous <see cref="PhaseStart"/>+<see cref="PhaseEnd"/> instant pair on the producer side: a real span is opened at
    /// the top of <c>InspectorPhase</c> and disposed at the bottom, so child spans (<c>PageCacheFlush</c>, <c>BTreeInsert</c>, etc.) attach
    /// via <c>parentSpanId</c> for proper hierarchy. Payload: <c>phase: u8</c> (TickPhase enum).
    /// </summary>
    RuntimePhaseSpan = 243,

    /// <summary>
    /// Per-(tick, queue) rollup emitted at end-of-tick. Captures queue-depth telemetry that's local to the engine's
    /// <c>EventQueueBase</c> accumulators (peak depth, end-of-tick depth, overflow count, produced/consumed counts).
    /// Folded by <see cref="IncrementalCacheBuilder"/> into the v12 <see cref="CacheSectionId.QueueTickSummaries"/> section
    /// for the Workbench Data API <c>queue/&lt;name&gt;</c> tracks (#311).
    /// Payload: <c>tick: u32, queueId: u16, peakDepth: u32, endOfTickDepth: u32, overflowCount: u32, produced: u32, consumed: u32</c>
    /// = 26 bytes after the common header.
    /// </summary>
    QueueTickEnd = 244,

    /// <summary>
    /// Per-(system, archetype) entity-touch rollup emitted at parallel-query completion. Captures the cross-dimension that
    /// <see cref="SchedulerChunk"/> (per-system) and <see cref="EcsQueryExecute"/> (per-archetype) leave separate. Feeds the
    /// Workbench Data Flow module's <c>archetype/*</c>, <c>system-archetype/*</c>, and <c>component-family/*</c> track families.
    /// Payload: <c>systemIndex: u16, archetypeId: u16, entityCount: i32, chunkCount: i32</c> = 12 bytes after the span header.
    /// Gated by <see cref="Typhon.Engine.Observability.TelemetryConfig.SchedulerArchetypeTouchesActive"/>.
    /// </summary>
    SchedulerSystemArchetype = 245,

    // ── Query Definition Export (issue #342, sub-issues #334/#335/#336) — IDs 247-248. ──
    //
    // Both are instant-style records with VARIABLE-LENGTH payloads emitted by the query pipeline once
    // per distinct View/EcsQuery identity (Describe) and once per execution (Args). Wire layout is owned
    // by hand-written codecs (QueryDefinitionDescribeEventCodec / QueryArgsEventCodec) — the
    // [TraceEvent] source generator does not support variable-length payloads. Gated by
    // <see cref="Typhon.Engine.Observability.TelemetryConfig.QueryActive"/>.
    //
    // See claude/design/Profiler/11-query-definition-export.md §4.5, §4.6 for wire shape.

    /// <summary>
    /// One-shot definition descriptor emitted on first observation of a View/EcsQuery identity within a
    /// profiling session. Payload (variable-length):
    /// <c>kind: u8</c> (0=View, 1=EcsQuery), <c>localId: u32</c>, <c>targetComponentType: u16</c>,
    /// <c>primaryIndexFieldIdx: i16</c>, <c>sortFieldIdx: i16</c>, <c>sortDescending: u8</c>,
    /// <c>definitionSourceFileId: u16</c>, <c>definitionSourceLine: i32</c>,
    /// <c>definitionSourceMethodId: u16</c>, <c>evaluatorCount: u16</c>,
    /// <c>evaluators[evaluatorCount]: { fieldIdx: u16, op: u8, reserved: u8 }</c>,
    /// <c>fieldDependencyCount: u16</c>, <c>fieldDependencies[fieldDependencyCount]: u16</c>.
    /// </summary>
    QueryDefinitionDescribe = 247,

    /// <summary>
    /// Per-execution arguments payload emitted immediately after each <see cref="QueryPlan"/> when the
    /// query carries at least one evaluator (skipped when <c>EvaluatorCount == 0</c>). Payload:
    /// <c>evaluatorCount: u16</c>, <c>thresholds[evaluatorCount]: i64</c> (widened threshold constants
    /// matching <c>FieldEvaluator.Threshold</c>).
    /// </summary>
    QueryArgs = 248,

    // ── Fallback ──

    /// <summary>
    /// User-defined span with inline UTF-8 null-terminated name. Used for dynamic-string call sites (tests, demo code).
    /// Payload: null-terminated UTF-8 bytes.
    /// <para>
    /// Was <c>200</c> until 2026-05-10, which collided with <see cref="EcsQueryMaskAnd"/>. The collision was latent in
    /// production because <c>EcsQueryMaskAnd</c> is default-suppressed, but unsuppressing it would have made the two
    /// kinds indistinguishable on the wire. Reassigned to 246 (next free slot above <see cref="SchedulerSystemArchetype"/>).
    /// Wire format bumped to v8 in <see cref="TraceFileHeader.CurrentVersion"/> to signal the change.
    /// </para>
    /// </summary>
    NamedSpan = 246,
}

/// <summary>
/// Helpers for <see cref="TraceEventKind"/> range classification. Used by readers and the consumer drain loop to decide which header shape to parse.
/// </summary>
public static class TraceEventKindExtensions
{
    /// <summary>
    /// <c>true</c> when the kind uses the span header extension (25-byte span preamble after the 12-byte common header, plus optional 16-byte trace context).
    /// Instant kinds (&lt; 10) use only the common header + optional tiny payload.
    /// </summary>
    /// <remarks>
    /// <see cref="TraceEventKind.PerTickSnapshot"/> is explicitly excluded: its numeric ID is ≥ 10 for category grouping with other metric
    /// records, but its wire shape is instant (no span header extension). Any future instant-style kind placed above 9 must be added to this
    /// exclusion — otherwise the consumer will misread the 25 bytes immediately after the common header as span metadata.
    /// <para>
    /// Concurrency tracing kinds 90–116 (Phase 2) are also instant-style and excluded as a contiguous range.
    /// Spatial tracing kinds 117–145 (Phase 3) are a mix; the per-kind exclusions are listed in the body.
    /// </para>
    /// </remarks>
    public static bool IsSpan(this TraceEventKind kind)
    {
        var v = (byte)kind;
        if (v < 10)
        {
            return false;
        }
        if (kind == TraceEventKind.PerTickSnapshot || kind == TraceEventKind.ThreadInfo)
        {
            return false;
        }
        // Concurrency tracing instant range (Phase 2, #280): 90–116.
        if (v >= 90 && v <= 116)
        {
            return false;
        }
        // Spatial tracing (Phase 3, #281): mixed shape. Instants are 127–135, 137, 140–142, 144–145.
        // Spans are 117–126, 136, 138–139, 143 — fall through to the default `return true`.
        if ((v >= 127 && v <= 135) || v == 137 || (v >= 140 && v <= 142) || v == 144 || v == 145)
        {
            return false;
        }
        // Scheduler & Runtime tracing (Phase 4, #282): mixed shape.
        // Instants: 146 (SystemStartExecution), 147 (SystemCompletion), 148 (SystemQueueWait),
        //           151 (WorkerWake), 153 (Dispense), 154 (DependencyReady),
        //           156-158 (Overload trio), 161-162 (UoWCreate/Flush).
        // Spans: 149 (SystemSingleThreaded), 150 (WorkerIdle), 152 (WorkerBetweenTick),
        //        155 (DependencyFanOut), 159-160 (GraphBuild/Rebuild),
        //        163 (TransactionLifecycle), 164 (SubscriptionOutputExecute) — fall through.
        if ((v >= 146 && v <= 148) || v == 151 || v == 153 || v == 154
            || (v >= 156 && v <= 158) || v == 161 || v == 162)
        {
            return false;
        }
        // Storage & Memory tracing (Phase 5, #283): mostly instant.
        // Span: 165 (StoragePageCacheDirtyWalk) — falls through.
        // Instants: 166-172 (Segment Create/Grow/Load, ChunkSegmentGrow, FileHandle, OccupancyMapGrow, AlignmentWaste).
        if (v >= 166 && v <= 172)
        {
            return false;
        }
        // Data plane tracing (Phase 6, #284): mixed.
        // Spans: 173 (TxInit), 174 (TxPrepare), 175 (TxValidate), 177 (TxCleanup), 179 (MvccVersionCleanup),
        //        181 (BTreeRangeScan), 184 (BTreeBulkInsert) — fall through to default `return true`.
        // Instants: 176 (TxConflict), 178 (MvccChainWalk), 180 (BTreeSearch), 182 (BTreeRangeScanRevalidate),
        //           183 (BTreeRebalanceFallback), 185 (BTreeRoot), 186 (BTreeNodeCow).
        if (v == 176 || v == 178 || v == 180 || v == 182 || v == 183 || v == 185 || v == 186)
        {
            return false;
        }
        // Query / ECS:Query / ECS:View tracing (Phase 7, #285): mixed shape.
        // Instants: 191 (PrimarySelect), 197 (StorageMode), 200 (MaskAnd), 202 (ConstraintEnabled),
        //           203 (SpatialAttach), 206 (DeltaBufferOverflow), 207 (ProcessEntry), 208 (ProcessEntryOr),
        //           211 (RegistryRegister), 212 (RegistryDeregister), 213 (DeltaCacheMiss).
        // Spans: 187-190, 192-196, 198, 199, 201, 204, 205, 209, 210 — fall through.
        if (v == 191 || v == 197 || v == 200 || v == 202 || v == 203 || v == 206
            || v == 207 || v == 208 || v == 211 || v == 212 || v == 213)
        {
            return false;
        }
        // Durability tracing (Phase 8, #286): mixed shape.
        // Instants: 217 (WalGroupCommit), 218 (WalQueue), 220 (WalFrame), 225 (RecoveryStart),
        //           228 (RecoveryRecord), 233 (UowState), 234 (UowDeadline).
        // Spans: 214-216 (WAL split), 219 (WalBuffer), 221 (WalBackpressure), 222-224 (Checkpoint depth),
        //        226 (Discover), 227 (Segment), 229 (FPI), 230-232 (Redo/Undo/TickFence) — fall through.
        if (v == 217 || v == 218 || v == 220 || v == 225 || v == 228 || v == 233 || v == 234)
        {
            return false;
        }
        // Phase 4 follow-up (#289):
        //   241 (SchedulerMetronomeWait) — span (falls through to `return true`).
        //   242 (SchedulerOverloadDetector) — instant.
        //   243 (RuntimePhaseSpan) — span (falls through; replaces the PhaseStart+PhaseEnd instant pair).
        //   244 (QueueTickEnd) — instant rollup. Hand-coded by `QueueTickEndCodec` with a 28-byte
        //         payload after the 12-byte common header — NO 25-byte span-header extension. Without
        //         this carve-out, span-aware decoders read 25 payload bytes as a fake span header,
        //         producing garbage `durationUs`/`spanId`/`parentSpanId` and rendering the rollup as
        //         a phantom nested span on the TickDriver thread lane.
        if (v == 242 || v == 244)
        {
            return false;
        }
        // Query Definition Export (#342, sub-issues #334/#335/#336):
        //   247 (QueryDefinitionDescribe), 248 (QueryArgs) — instant-style with variable payloads.
        if (v == 247 || v == 248)
        {
            return false;
        }
        return true;
    }
}
