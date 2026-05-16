/**
 * Matches the TraceEventKind enum in Typhon.Engine.Profiler.Events.
 * Instant kinds < 10 carry no span header; span kinds ≥ 10 include SpanId/ParentSpanId/duration and optional trace context.
 */
export const enum TraceEventKind {
  // Instant
  TickStart = 0,
  TickEnd = 1,
  PhaseStart = 2,
  PhaseEnd = 3,
  SystemReady = 4,
  SystemSkipped = 5,
  Instant = 6,
  GcStart = 7,
  GcEnd = 8,
  MemoryAllocEvent = 9,

  // Span
  SchedulerChunk = 10,

  TransactionCommit = 20,
  TransactionRollback = 21,
  TransactionCommitComponent = 22,
  TransactionPersist = 23,

  EcsSpawn = 30,
  EcsDestroy = 31,
  EcsQueryExecute = 32,
  EcsQueryCount = 33,
  EcsQueryAny = 34,
  EcsViewRefresh = 35,

  BTreeInsert = 40,
  BTreeDelete = 41,
  BTreeNodeSplit = 42,
  BTreeNodeMerge = 43,

  PageCacheFetch = 50,
  PageCacheDiskRead = 51,
  PageCacheDiskWrite = 52,
  PageCacheAllocatePage = 53,
  PageCacheFlush = 54,
  PageEvicted = 55,
  PageCacheDiskReadCompleted = 56,
  PageCacheDiskWriteCompleted = 57,
  PageCacheFlushCompleted = 58,
  PageCacheBackpressure = 59,

  ClusterMigration = 60,

  WalFlush = 80,
  WalSegmentRotate = 81,
  WalWait = 82,

  CheckpointCycle = 83,
  CheckpointCollect = 84,
  CheckpointWrite = 85,
  CheckpointFsync = 86,
  CheckpointTransition = 87,
  CheckpointRecycle = 88,

  StatisticsRebuild = 89,

  GcSuspension = 75,

  // Scheduler worker-loop spans — pinned to the chunk-row band by the renderer (see timeAreaLayout.deriveSlotInfo).
  SchedulerWorkerIdle = 150,
  SchedulerWorkerBetweenTick = 152,
  /**
   * Per-tick gauge snapshot. Numerically ≥ 10 for category grouping with metric records, but wire-shape is INSTANT (no span header
   * extension) — <c>IsSpan()</c> excludes it on both server (C#) and client (this decoder). Special-cased at the top of
   * <c>decodeSpan</c> in <c>chunkDecoder.ts</c>.
   */
  PerTickSnapshot = 76,

  /**
   * Per-slot thread identity — emitted once when a producer thread claims its slot. Carries the managed thread ID and a UTF-8 thread
   * name so the viewer can label lanes with something meaningful. Wire shape is instant (no span header extension); the chunk decoder
   * routes it through its own case, and the chunk cache accumulates the entries into a slot→name map on <c>TraceMetadata</c>.
   */
  ThreadInfo = 77,

  /**
   * Tick lifecycle phase span — covers one TickPhase region inside TyphonRuntime.OnTickEndInternal (SystemDispatch, WriteTickFence,
   * UowFlush, OutputPhase, TierIndexRebuild, DormancySweep). Replaces the old PhaseStart+PhaseEnd instant pair on the producer side:
   * a real span is opened so child spans (PageCacheFlush, BTreeInsert, ClusterMigration, …) attach via parentSpanId. Payload: u8 phase.
   */
  RuntimePhaseSpan = 243,

  /** UoW created at top of OnTickStart. Instant. Payload: i64 tick. Rendered as a glyph in the phase track. */
  RuntimePhaseUoWCreate = 161,

  /** UoW flushed at end of OnTickEnd. Instant. Payload: i64 tick, i32 changeCount. Rendered as a glyph in the phase track. */
  RuntimePhaseUoWFlush = 162,

  NamedSpan = 200,

  /**
   * OS thread context-switch — one record per ON-CPU slice closed for a Typhon-registered OS thread, produced by the engine's
   * EtwSchedulingPump (Windows-only). Wire shape is INSTANT (12-byte common header + 13-byte payload, no span-header extension) —
   * `isInstantKind()` in `chunkDecoder.ts` carves it out. The header's threadSlot is the pump's producer slot; the payload's
   * `targetSlotIdx` is the thread the slice belongs to. The viewer renders the GAPS between consecutive slices (off-CPU intervals).
   */
  ThreadContextSwitch = 254,
}

/**
 * Coarse wait-reason category for off-CPU interval coloring. The raw `ThreadWaitReason` (~38 values) is too fine-grained for a
 * legible palette, so each value collapses into one of these buckets via {@link waitReasonToCategory}. `Idle` is not a wait
 * reason — it's assigned when the source slice's `gettingIdle` flag is set (the CPU went to the System Idle thread next).
 */
export const enum OffCpuCategory {
  /** Blocked on a kernel object / sync primitive (event, mutex, semaphore, LPC, push-lock, ...). */
  SyncWait = 0,
  /** Booted off the CPU by a higher-priority thread or a dispatch interrupt; also voluntary yield. */
  Preempted = 1,
  /** Used up the scheduling quantum — pure CPU pressure with peers competing. */
  QuantumEnd = 2,
  /** Stalled on paging / virtual-memory / pool allocation. */
  Paging = 3,
  /** Explicit user-mode wait — Sleep, WaitForSingleObject, SuspendThread, delay-execution. */
  UserWait = 4,
  /** The CPU went idle after this slice (no contender). Set from the `gettingIdle` flag, not a wait reason. */
  Idle = 5,
  /** Anything not mapped above. */
  Other = 6,
}

/** Human-readable label per {@link OffCpuCategory}, for the legend, tooltip, and detail pane. */
export const OffCpuCategoryNames: Record<number, string> = {
  [OffCpuCategory.SyncWait]: 'Sync wait',
  [OffCpuCategory.Preempted]: 'Preempted',
  [OffCpuCategory.QuantumEnd]: 'Quantum end',
  [OffCpuCategory.Paging]: 'Paging / VM',
  [OffCpuCategory.UserWait]: 'User wait',
  [OffCpuCategory.Idle]: 'Idle',
  [OffCpuCategory.Other]: 'Other',
};

/**
 * Display name for each `ThreadWaitReason` byte — mirrors the C# enum in `src/Typhon.Profiler/ThreadWaitReason.cs`. Surfaced
 * in the off-CPU tooltip + detail pane so the user sees the exact kernel reason, not just the coarse category. Out-of-range
 * values (newer Windows builds appending entries) fall back to `Reason {n}` at the call site.
 */
export const WaitReasonNames: Record<number, string> = {
  0: 'Executive', 1: 'FreePage', 2: 'PageIn', 3: 'PoolAllocation', 4: 'DelayExecution', 5: 'Suspended', 6: 'UserRequest',
  7: 'WrExecutive', 8: 'WrFreePage', 9: 'WrPageIn', 10: 'WrPoolAllocation', 11: 'WrDelayExecution', 12: 'WrSuspended',
  13: 'WrUserRequest', 14: 'WrSpare0', 15: 'WrQueue', 16: 'WrLpcReceive', 17: 'WrLpcReply', 18: 'WrVirtualMemory',
  19: 'WrPageOut', 20: 'WrRendezvous', 21: 'WrKeyedEvent', 22: 'WrTerminated', 23: 'WrProcessInSwap', 24: 'WrCpuRateControl',
  25: 'WrCalloutStack', 26: 'WrKernel', 27: 'WrResource', 28: 'WrPushLock', 29: 'WrMutex', 30: 'WrQuantumEnd',
  31: 'WrDispatchInt', 32: 'WrPreempted', 33: 'WrYieldExecution', 34: 'WrFastMutex', 35: 'WrGuardedMutex', 36: 'WrRundown',
  37: 'MaximumWaitReason',
};

/**
 * Plain-English one-liner for each `ThreadWaitReason` byte — the cryptic kernel `KWAIT_REASON` name (see {@link WaitReasonNames})
 * tells you nothing unless you know the Windows scheduler, so the tooltip shows this instead. Meanings are the well-established
 * Windows-kernel definitions (`Wr` = "wait reason"); paired values like `Executive`/`WrExecutive` describe the same wait.
 */
export const WaitReasonDescriptions: Record<number, string> = {
  0: 'Blocked on a kernel object',
  1: 'Waiting for a free memory page',
  2: 'Waiting for a page fault from disk',
  3: 'Waiting for kernel pool memory',
  4: 'Sleeping (timed delay)',
  5: 'Thread suspended',
  6: 'User-mode wait (WaitFor…)',
  7: 'Blocked on a kernel object',
  8: 'Waiting for a free memory page',
  9: 'Waiting for a page fault from disk',
  10: 'Waiting for kernel pool memory',
  11: 'Sleeping (timed delay)',
  12: 'Thread suspended',
  13: 'User-mode wait (WaitFor…)',
  14: 'Reserved wait reason',
  15: 'Idle in a thread-pool / IO queue',
  16: 'Waiting to receive an LPC/RPC message',
  17: 'Waiting for an LPC/RPC reply',
  18: 'Waiting for virtual memory',
  19: 'Waiting for pages to flush to disk',
  20: 'Waiting at a thread rendezvous',
  21: 'Waiting on a keyed event (lock)',
  22: 'Thread is terminating',
  23: 'Process being swapped into memory',
  24: 'Throttled by a CPU rate limit',
  25: 'Waiting for a kernel callout stack',
  26: 'Generic kernel wait',
  27: 'Waiting on an ERESOURCE read/write lock',
  28: 'Waiting on a push lock',
  29: 'Waiting on a mutex',
  30: 'Scheduling time slice expired',
  31: 'Preempted by a dispatch interrupt',
  32: 'Preempted by a higher-priority thread',
  33: 'Voluntarily yielded the CPU',
  34: 'Waiting on a fast mutex',
  35: 'Waiting on a guarded mutex',
  36: 'Waiting for rundown protection',
  37: 'Unspecified wait reason',
};

/**
 * 256-entry lookup table mapping a raw `ThreadWaitReason` byte → {@link OffCpuCategory}. Built once at module load so the
 * trace-model demux can categorize each context-switch record with a single array index (no per-record switch). Unmapped
 * bytes default to {@link OffCpuCategory.Other}.
 */
const WAIT_REASON_CATEGORY_LUT: Uint8Array = (() => {
  const lut = new Uint8Array(256).fill(OffCpuCategory.Other);
  // Blocked on a kernel object / sync primitive.
  for (const r of [0, 7, 15, 16, 17, 21, 26, 27, 28, 29, 34, 35]) lut[r] = OffCpuCategory.SyncWait;
  // Preempted / yielded / swapped off the core.
  for (const r of [23, 31, 32, 33]) lut[r] = OffCpuCategory.Preempted;
  // Quantum exhausted.
  lut[30] = OffCpuCategory.QuantumEnd;
  // Paging / virtual-memory / pool allocation stalls.
  for (const r of [1, 2, 3, 8, 9, 10, 18, 19]) lut[r] = OffCpuCategory.Paging;
  // Explicit user-mode waits + suspend.
  for (const r of [4, 5, 6, 11, 12, 13]) lut[r] = OffCpuCategory.UserWait;
  return lut;
})();

/**
 * Categorize one off-CPU interval. `gettingIdle` (the source slice ended with the CPU going to the System Idle thread) wins
 * over the wait reason — an idle hand-off is more informative to surface than whatever the thread was nominally waiting on.
 */
export function waitReasonToCategory(waitReason: number, gettingIdle: boolean): OffCpuCategory {
  if (gettingIdle) return OffCpuCategory.Idle;
  return WAIT_REASON_CATEGORY_LUT[waitReason & 0xff] as OffCpuCategory;
}

/**
 * Gauge identifiers. Mirrors <c>GaugeId</c> in <c>Typhon.Engine.Profiler.Events</c>. Wire-stable — numeric values are part of the
 * trace file format. IDs are grouped by category in 0x10 increments so future gauges in a category slot into their range without
 * renumbering existing entries.
 */
export const enum GaugeId {
  // Unmanaged memory (PinnedMemoryBlock via NativeMemory) — 0x0100
  MemoryUnmanagedTotalBytes = 0x0100,
  MemoryUnmanagedPeakBytes = 0x0101,
  MemoryUnmanagedLiveBlocks = 0x0102,

  // GC heap — sampled from GC.GetGCMemoryInfo() — 0x0110
  GcHeapGen0Bytes = 0x0110,
  GcHeapGen1Bytes = 0x0111,
  GcHeapGen2Bytes = 0x0112,
  GcHeapLohBytes = 0x0113,
  GcHeapPohBytes = 0x0114,
  GcHeapCommittedBytes = 0x0115,

  // Persistent store / page cache — 0x0200
  PageCacheTotalPages = 0x0200,           // fixed at init
  PageCacheFreePages = 0x0201,
  PageCacheCleanUsedPages = 0x0202,        // mutually-exclusive bucket
  PageCacheDirtyUsedPages = 0x0203,        // mutually-exclusive bucket
  PageCacheExclusivePages = 0x0204,
  PageCacheEpochProtectedPages = 0x0205,
  PageCachePendingIoReads = 0x0206,

  // Transient store — 0x0210
  TransientStoreBytesUsed = 0x0210,
  TransientStoreMaxBytes = 0x0211,         // fixed at init

  // WAL — 0x0300
  WalCommitBufferUsedBytes = 0x0300,
  WalCommitBufferCapacityBytes = 0x0301,   // fixed
  WalInflightFrames = 0x0302,
  WalStagingPoolRented = 0x0303,
  WalStagingPoolPeakRented = 0x0304,
  WalStagingPoolCapacity = 0x0305,         // fixed
  WalStagingTotalRentsCumulative = 0x0306, // cumulative — viewer derives rate from deltas

  // Transactions + UoW — 0x0400
  TxChainActiveCount = 0x0400,
  TxChainPoolSize = 0x0401,
  UowRegistryActiveCount = 0x0402,
  UowRegistryVoidCount = 0x0403,

  // Cumulative throughput counters — viewer derives per-tick deltas by subtracting consecutive snapshots — 0x0410
  TxChainCommitTotal = 0x0410,
  TxChainRollbackTotal = 0x0411,
  UowRegistryCreatedTotal = 0x0412,
  UowRegistryCommittedTotal = 0x0413,
  TxChainCreatedTotal = 0x0414,
}

/** On-wire value kind for a single gauge — mirrors <c>GaugeValueKind</c>. Not directly used on the client (the server already decoded); included for reference. */
export const enum GaugeValueKind {
  U32Count = 0,
  U64Bytes = 1,
  I64Signed = 2,
  U32PercentHundredths = 3,
}

/**
 * Set of gauge IDs whose values are fixed at initialization time (capacities). The engine emits these only in the first snapshot of a
 * session; subsequent snapshots omit them. The viewer caches the first-seen value so subsequent tick renders can still show the ceiling.
 */
export const FIXED_AT_INIT_GAUGES: ReadonlySet<GaugeId> = new Set<GaugeId>([
  GaugeId.PageCacheTotalPages,
  GaugeId.TransientStoreMaxBytes,
  GaugeId.WalCommitBufferCapacityBytes,
  GaugeId.WalStagingPoolCapacity,
]);

/** Direction of a MemoryAllocEvent — mirrors <c>MemoryAllocDirection</c>. */
export const enum MemoryAllocDirection {
  Alloc = 0,
  Free = 1,
}

/** Matches TickPhase enum in Typhon.Engine.Profiler.Events */
export const enum TickPhase {
  SystemDispatch = 0,
  UowFlush = 1,
  WriteTickFence = 2,
  OutputPhase = 3,
  TierIndexRebuild = 4,
  DormancySweep = 5
}

export const TickPhaseNames: Record<number, string> = {
  0: 'System Dispatch',
  1: 'UoW Flush',
  2: 'Write Tick Fence',
  3: 'Output Phase',
  4: 'Tier Index Rebuild',
  5: 'Dormancy Sweep'
};

export const SkipReasonNames: Record<number, string> = {
  0: 'Not Skipped',
  1: 'ShouldRun False',
  2: 'Empty Input',
  3: 'Empty Events',
  4: 'Throttled',
  5: 'Shed',
  6: 'Exception',
  7: 'Dependency Failed'
};

/**
 * Display name for each span kind. Used by the viewer whenever it needs to render a human-readable
 * label next to a span record. Mirrors the C# `TraceEventKind` enum in
 * `src/Typhon.Profiler/TraceEventKind.cs` — keep this table in sync when new kinds are added on the
 * engine side. Entries that would otherwise need a TS enum extension use raw numeric keys; the TS
 * enum (above) intentionally only declares kinds that other code references symbolically.
 *
 * Note on kind 200 — both `NamedSpan` (legacy) and `EcsQueryMaskAnd` (#277) are assigned `200`
 * on the C# side. NamedSpan is decoded specially in `chunkDecoder.ts` (it has an inline UTF-8
 * payload that the decoder treats verbatim — unrelated to this table). For everything else that
 * lands at kind 200, the modern engine emits `EcsQueryMaskAnd`, so this table labels 200 as
 * `ECS.Query.MaskAnd`. The pre-#277 `NamedSpan` entry below is overwritten by the later
 * `200: 'ECS.Query.MaskAnd'` line — intentional: post-#277 traces dominate.
 */
export const SpanKindNames: Record<number, string> = {
  // Pre-#277 kinds (mostly already in the TS enum).
  [TraceEventKind.SchedulerChunk]: 'Scheduler.Chunk',
  [TraceEventKind.TransactionCommit]: 'Transaction.Commit',
  [TraceEventKind.TransactionRollback]: 'Transaction.Rollback',
  [TraceEventKind.TransactionCommitComponent]: 'Transaction.CommitComponent',
  [TraceEventKind.EcsSpawn]: 'ECS.Spawn',
  [TraceEventKind.EcsDestroy]: 'ECS.Destroy',
  [TraceEventKind.EcsQueryExecute]: 'ECS.Query.Execute',
  [TraceEventKind.EcsQueryCount]: 'ECS.Query.Count',
  [TraceEventKind.EcsQueryAny]: 'ECS.Query.Any',
  [TraceEventKind.EcsViewRefresh]: 'ECS.View.Refresh',
  [TraceEventKind.BTreeInsert]: 'BTree.Insert',
  [TraceEventKind.BTreeDelete]: 'BTree.Delete',
  [TraceEventKind.BTreeNodeSplit]: 'BTree.NodeSplit',
  [TraceEventKind.BTreeNodeMerge]: 'BTree.NodeMerge',
  [TraceEventKind.PageCacheFetch]: 'PageCache.Fetch',
  [TraceEventKind.PageCacheDiskRead]: 'PageCache.DiskRead',
  [TraceEventKind.PageCacheDiskWrite]: 'PageCache.DiskWrite',
  [TraceEventKind.PageCacheAllocatePage]: 'PageCache.AllocatePage',
  [TraceEventKind.PageCacheFlush]: 'PageCache.Flush',
  [TraceEventKind.PageEvicted]: 'PageCache.Evicted',
  [TraceEventKind.PageCacheDiskReadCompleted]: 'PageCache.DiskRead.Completed',
  [TraceEventKind.PageCacheDiskWriteCompleted]: 'PageCache.DiskWrite.Completed',
  [TraceEventKind.PageCacheFlushCompleted]: 'PageCache.Flush.Completed',
  [TraceEventKind.PageCacheBackpressure]: 'PageCache.Backpressure',
  [TraceEventKind.TransactionPersist]: 'Transaction.Persist',
  [TraceEventKind.WalFlush]: 'WAL.Flush',
  [TraceEventKind.WalSegmentRotate]: 'WAL.SegmentRotate',
  [TraceEventKind.WalWait]: 'WAL.Wait',
  [TraceEventKind.CheckpointCycle]: 'Checkpoint.Cycle',
  [TraceEventKind.CheckpointCollect]: 'Checkpoint.Collect',
  [TraceEventKind.CheckpointWrite]: 'Checkpoint.Write',
  [TraceEventKind.CheckpointFsync]: 'Checkpoint.Fsync',
  [TraceEventKind.CheckpointTransition]: 'Checkpoint.Transition',
  [TraceEventKind.CheckpointRecycle]: 'Checkpoint.Recycle',
  [TraceEventKind.StatisticsRebuild]: 'Statistics.Rebuild',
  [TraceEventKind.ClusterMigration]: 'Cluster.Migration',
  [TraceEventKind.GcSuspension]: 'GC.Suspension',
  // `NamedSpan` (= 200) is intentionally NOT listed here: it's the inline-name fallback kind
  // that `chunkDecoder.decodeSpan` handles specially. Kind 200 is also `EcsQueryMaskAnd` on
  // the modern engine — see the entry below in the ECS subtree block.

  // Concurrency subtree (90-116) — added by #277.
  90: 'Concurrency.AccessControl.SharedAcquire',
  91: 'Concurrency.AccessControl.SharedRelease',
  92: 'Concurrency.AccessControl.ExclusiveAcquire',
  93: 'Concurrency.AccessControl.ExclusiveRelease',
  94: 'Concurrency.AccessControl.Promotion',
  95: 'Concurrency.AccessControl.Contention',
  96: 'Concurrency.AccessControlSmall.SharedAcquire',
  97: 'Concurrency.AccessControlSmall.SharedRelease',
  98: 'Concurrency.AccessControlSmall.ExclusiveAcquire',
  99: 'Concurrency.AccessControlSmall.ExclusiveRelease',
  100: 'Concurrency.AccessControlSmall.Contention',
  101: 'Concurrency.Resource.Accessing',
  102: 'Concurrency.Resource.Modify',
  103: 'Concurrency.Resource.Destroy',
  104: 'Concurrency.Resource.ModifyPromotion',
  105: 'Concurrency.Resource.Contention',
  106: 'Concurrency.Epoch.ScopeEnter',
  107: 'Concurrency.Epoch.ScopeExit',
  108: 'Concurrency.Epoch.Advance',
  109: 'Concurrency.Epoch.Refresh',
  110: 'Concurrency.Epoch.SlotClaim',
  111: 'Concurrency.Epoch.SlotReclaim',
  112: 'Concurrency.AdaptiveWaiter.YieldOrSleep',
  113: 'Concurrency.OlcLatch.WriteLockAttempt',
  114: 'Concurrency.OlcLatch.WriteUnlock',
  115: 'Concurrency.OlcLatch.MarkObsolete',
  116: 'Concurrency.OlcLatch.ValidationFail',

  // Spatial subtree (117-145).
  117: 'Spatial.Query.AABB',
  118: 'Spatial.Query.Radius',
  119: 'Spatial.Query.Ray',
  120: 'Spatial.Query.Frustum',
  121: 'Spatial.Query.KNN',
  122: 'Spatial.Query.Count',
  123: 'Spatial.RTree.Insert',
  124: 'Spatial.RTree.Remove',
  125: 'Spatial.RTree.NodeSplit',
  126: 'Spatial.RTree.BulkLoad',
  127: 'Spatial.Grid.CellTierChange',
  128: 'Spatial.Grid.OccupancyChange',
  129: 'Spatial.Grid.ClusterCellAssign',
  130: 'Spatial.Cell.Index.Add',
  131: 'Spatial.Cell.Index.Update',
  132: 'Spatial.Cell.Index.Remove',
  133: 'Spatial.ClusterMigration.Detect',
  134: 'Spatial.ClusterMigration.Queue',
  135: 'Spatial.ClusterMigration.Hysteresis',
  136: 'Spatial.TierIndex.Rebuild',
  137: 'Spatial.TierIndex.VersionSkip',
  138: 'Spatial.Maintain.Insert',
  139: 'Spatial.Maintain.UpdateSlowPath',
  140: 'Spatial.Maintain.AabbValidate',
  141: 'Spatial.Maintain.BackPointerWrite',
  142: 'Spatial.Trigger.Region',
  143: 'Spatial.Trigger.Eval',
  144: 'Spatial.Trigger.Occupant.Diff',
  145: 'Spatial.Trigger.Cache.Invalidate',

  // Scheduler depth subtree (146-160).
  146: 'Scheduler.System.StartExecution',
  147: 'Scheduler.System.Completion',
  148: 'Scheduler.System.QueueWait',
  149: 'Scheduler.System.SingleThreaded',
  150: 'Scheduler.Worker.Idle',
  151: 'Scheduler.Worker.Wake',
  152: 'Scheduler.Worker.BetweenTick',
  153: 'Scheduler.Dispense',
  154: 'Scheduler.Dependency.Ready',
  155: 'Scheduler.Dependency.FanOut',
  156: 'Scheduler.Overload.LevelChange',
  157: 'Scheduler.Overload.SystemShed',
  158: 'Scheduler.Overload.TickMultiplier',
  159: 'Scheduler.Graph.Build',
  160: 'Scheduler.Graph.Rebuild',
  // Phase 4 follow-up (#289) — answer "why is the engine waiting for nothing".
  241: 'Scheduler.Metronome.Wait',
  242: 'Scheduler.Overload.Detector',
  // Per-tick fence-time spatial maintenance spans (always-fire when gates are on; outcome counts
  // are captured by the matching instant events 133–135 and 131).
  249: 'Spatial.ClusterMigration.DetectScan',
  250: 'Spatial.Cluster.AabbRefresh',
  // Per-table fence-time spans inside WriteTickFenceCore. The outer InspectorPhase(WriteTickFence)
  // wrapper still emits kind 243; these break it down per ComponentTable to surface "which table
  // dominated the fence wall?" — prerequisite for the parallelize-the-fence work.
  251: 'Runtime.WriteTickFence.Table',
  252: 'Runtime.WriteTickFence.Shadow',
  253: 'Runtime.WriteTickFence.Spatial',
  // Cluster-scope mirror — these wrap the per-archetype body of WriteClusterTickFence. AntHill
  // and other cluster-backed worlds do all their fence work here, not in the per-table loop.
  // Wire IDs 61-63 land next to ClusterMigration (60) — semantic grouping.
  61: 'Runtime.WriteTickFence.Cluster',
  62: 'Runtime.WriteTickFence.Cluster.Shadow',
  63: 'Runtime.WriteTickFence.Cluster.Spatial',
  // 243 (RuntimePhaseSpan) is a real span but its display name comes from `PHASE_NAMES[evt.phase]`
  // in `traceModel.ts` — this entry is only the `Kind[N]`-fallback safety net.
  243: 'Runtime.Phase',
  244: 'Queue.TickEnd',
  // Per-(system, archetype) entity-touch rollup emitted at parallel-query completion. Span covers the
  // first-chunk-start → last-chunk-end bracket. See `SchedulerSystemArchetypeEvent.cs` /
  // TraceEventKind.SchedulerSystemArchetype (= 245). Feeds the Data Flow `system-archetype/*` tracks.
  245: 'Scheduler.System.Archetype',

  // Runtime subtree (161-164, 235-240).
  161: 'Runtime.Phase.UoWCreate',
  162: 'Runtime.Phase.UoWFlush',
  163: 'Runtime.Transaction.Lifecycle',
  164: 'Runtime.Subscription.OutputExecute',
  235: 'Runtime.Subscription.Subscriber',
  236: 'Runtime.Subscription.Delta.Build',
  237: 'Runtime.Subscription.Delta.Serialize',
  238: 'Runtime.Subscription.Transition.BeginSync',
  239: 'Runtime.Subscription.Output.Cleanup',
  240: 'Runtime.Subscription.Delta.DirtyBitmapSupplement',

  // Storage subtree (165-171).
  165: 'Storage.PageCache.DirtyWalk',
  166: 'Storage.Segment.Create',
  167: 'Storage.Segment.Grow',
  168: 'Storage.Segment.Load',
  169: 'Storage.ChunkSegment.Grow',
  170: 'Storage.FileHandle',
  171: 'Storage.OccupancyMap.Grow',

  // Memory subtree (172).
  172: 'Memory.AlignmentWaste',

  // Data plane (173-186).
  173: 'Data.Transaction.Init',
  174: 'Data.Transaction.Prepare',
  175: 'Data.Transaction.Validate',
  176: 'Data.Transaction.Conflict',
  177: 'Data.Transaction.Cleanup',
  178: 'Data.MVCC.ChainWalk',
  179: 'Data.MVCC.VersionCleanup',
  180: 'Data.Index.BTree.Search',
  181: 'Data.Index.BTree.RangeScan',
  182: 'Data.Index.BTree.RangeScan.Revalidate',
  183: 'Data.Index.BTree.RebalanceFallback',
  184: 'Data.Index.BTree.BulkInsert',
  185: 'Data.Index.BTree.Root',
  186: 'Data.Index.BTree.NodeCow',

  // Query subtree (187-198).
  187: 'Query.Parse',
  188: 'Query.Parse.DNF',
  189: 'Query.Plan',
  190: 'Query.Estimate',
  191: 'Query.Plan.PrimarySelect',
  192: 'Query.Plan.Sort',
  193: 'Query.Execute.IndexScan',
  194: 'Query.Execute.Iterate',
  195: 'Query.Execute.Filter',
  196: 'Query.Execute.Pagination',
  197: 'Query.Execute.StorageMode',
  198: 'Query.Count',

  // ECS subtree depth (199-213). Note: kind 200 collides with NamedSpan (above) — last writer wins
  // for the lookup. NamedSpan is decoded specially upstream so spans of "real" NamedSpan kind get a
  // distinct name from their inline payload regardless of this table.
  199: 'ECS.Query.Construct',
  200: 'ECS.Query.MaskAnd',
  201: 'ECS.Query.SubtreeExpand',
  202: 'ECS.Query.ConstraintEnabled',
  203: 'ECS.Query.SpatialAttach',
  204: 'ECS.View.Refresh.Pull',
  205: 'ECS.View.IncrementalDrain',
  206: 'ECS.View.Delta.BufferOverflow',
  207: 'ECS.View.ProcessEntry',
  208: 'ECS.View.ProcessEntryOr',
  209: 'ECS.View.Refresh.Full',
  210: 'ECS.View.Refresh.FullOr',
  211: 'ECS.View.Registry.Register',
  212: 'ECS.View.Registry.Deregister',
  213: 'ECS.View.Delta.CacheMiss',

  // Durability subtree (214-234).
  214: 'Durability.WAL.QueueDrain',
  215: 'Durability.WAL.OsWrite',
  216: 'Durability.WAL.Signal',
  217: 'Durability.WAL.GroupCommit',
  218: 'Durability.WAL.Queue',
  219: 'Durability.WAL.Buffer',
  220: 'Durability.WAL.Frame',
  221: 'Durability.WAL.Backpressure',
  222: 'Durability.Checkpoint.WriteBatch',
  223: 'Durability.Checkpoint.Backpressure',
  224: 'Durability.Checkpoint.Sleep',
  225: 'Durability.Recovery.Start',
  226: 'Durability.Recovery.Discover',
  227: 'Durability.Recovery.Segment',
  228: 'Durability.Recovery.Record',
  229: 'Durability.Recovery.Fpi',
  230: 'Durability.Recovery.Redo',
  231: 'Durability.Recovery.Undo',
  232: 'Durability.Recovery.TickFence',
  233: 'Durability.UoW.State',
  234: 'Durability.UoW.Deadline',

  // OS thread scheduling (254). Instant-shaped; rendered as off-CPU overlay bars, not a lane span.
  254: 'Thread.ContextSwitch',
};

export interface TraceMetadata {
  header: {
    version: number;
    timestampFrequency: number;
    baseTickRate: number;
    workerCount: number;
    systemCount: number;
    archetypeCount: number;
    componentTypeCount: number;
    createdUtc: string;
    samplingSessionStartQpc: number;
  };
  systems: SystemDef[];
  archetypes: ArchetypeDef[];
  componentTypes: ComponentTypeDef[];
  /**
   * Per-slot thread names, populated as `ThreadInfo` records (kind 77) are decoded from the chunk stream. Sparse — slots without a
   * captured name have no entry. The viewer uses this to label lanes; missing entries fall back to "Slot {n}".
   */
  threadNames?: Record<number, string>;
}

export interface SystemDef {
  index: number;
  name: string;
  type: number;
  priority: number;
  isParallel: boolean;
  tierFilter: number;
  predecessors: number[];
  successors: number[];
}

export interface ArchetypeDef {
  archetypeId: number;
  name: string;
}

export interface ComponentTypeDef {
  componentTypeId: number;
  name: string;
}

/**
 * Flat trace record DTO — matches the server's LiveTraceEvent class. Fields are grouped by presence:
 *
 * - Always present: kind, threadSlot, tickNumber, timestampUs.
 * - Span kinds (≥ 10): also carry durationUs, spanId, parentSpanId; traceIdHi/Lo when the span captured an Activity context.
 * - Kind-specific: phase (PhaseStart/End), systemIndex (SystemReady/Skipped/SchedulerChunk), skipReason (SystemSkipped),
 *   chunkIndex/totalChunks/entitiesProcessed (SchedulerChunk), overloadLevel/tickMultiplier (TickEnd).
 *
 * 64-bit IDs arrive as 16-char lowercase hex strings because JavaScript's Number can't hold the full ulong range.
 */
export interface TraceEvent {
  kind: TraceEventKind;
  threadSlot: number;
  tickNumber: number;
  timestampUs: number;

  durationUs?: number;
  spanId?: string;
  parentSpanId?: string;
  traceIdHi?: string;
  traceIdLo?: string;

  /** #302: compile-time site id from `SourceLocationGenerator`. Resolves via `useSourceLocationStore`. */
  sourceLocationId?: number;

  // Instant-event fields
  phase?: number;
  systemIndex?: number;
  skipReason?: number;
  overloadLevel?: number;
  tickMultiplier?: number;

  // Scheduler chunk span
  chunkIndex?: number;
  totalChunks?: number;
  entitiesProcessed?: number;

  // Transaction spans
  tsn?: string;                // i64 hex string
  componentTypeId?: number;
  componentCount?: number;
  conflictDetected?: boolean;

  // ECS spans
  archetypeId?: number;
  entityId?: string;           // u64 hex string
  cascadeCount?: number;
  resultCount?: number;
  scanMode?: number;
  found?: boolean;
  mode?: number;
  deltaCount?: number;

  // Page-cache spans
  filePageIndex?: number;
  pageCount?: number;

  // Cluster migration span
  migrationCount?: number;

  // SpatialClusterMigrationDetectScan (kind 249) — fence-time scan span. Begin params:
  // archetypeId, scanSlotCount. Optional outcomes published at dispose.
  scanSlotCount?: number;
  migrationsQueued?: number;
  hysteresisAbsorbed?: number;
  clustersTouched?: number;

  // SpatialClusterAabbRefresh (kind 250) — fence-time AABB refresh span. Begin params:
  // archetypeId, clusterScanned. Optional outcomes published at dispose.
  clusterScanned?: number;
  aabbsChanged?: number;
  slotsScanned?: number;
  outlierGuardFires?: number;

  // WriteTickFenceTable (251), WriteTickFenceShadow (252), WriteTickFenceSpatial (253) —
  // per-ComponentTable fence body spans. componentTypeId (declared above for transaction spans)
  // is reused — matches WAL TickFenceHeader.ComponentTypeId.
  dirtyEntryCount?: number;
  walPublished?: number;          // 0/1 — WAL chunk was published this tick for this table
  hasShadow?: number;             // 0/1 — table.HasShadowableIndexes
  hasSpatial?: number;            // 0/1 — table has dynamic spatial index
  indexedFieldCount?: number;     // IndexedFieldInfos.Length
  totalShadowEntries?: number;    // sum of per-field shadow buffer counts drained this tick
  escapedCount?: number;          // entities whose new position escaped the fat AABB

  // WriteTickFenceCluster (61), WriteTickFenceClusterShadow (62), WriteTickFenceClusterSpatial (63).
  // archetypeId (declared above) is reused. The other fields are cluster-fence specific.
  dirtyClusterCount?: number;     // number of words with at least one bit set after occupancy mask
  entryCount?: number;            // total dirty entity slots (popcount of full bitmap)
  migrationsExecuted?: number;    // ClusterState.LastTickMigrationCount

  // Runtime UoW flush instant
  changeCount?: number;

  // Transaction persist
  walLsn?: string;

  // Page cache backpressure
  retryCount?: number;
  dirtyCount?: number;
  epochCount?: number;

  // WAL spans
  batchByteCount?: number;
  frameCount?: number;
  highLsn?: string;
  newSegmentIndex?: number;
  targetLsn?: string;

  // Checkpoint spans
  dirtyPageCount?: number;
  reason?: number;
  writtenCount?: number;
  transitionedCount?: number;
  recycledCount?: number;

  // Statistics
  entityCount?: number;
  mutationCount?: number;
  samplingInterval?: number;

  // Memory allocation (kind 9 — instant)
  direction?: number;        // 0 = alloc, 1 = free
  sourceTag?: number;         // u16 interned tag
  sizeBytes?: number;
  totalAfterBytes?: number;

  // Per-tick gauge snapshot (kind 76). Keys are GaugeId as number → double values. Server emits them as a JSON object,
  // which deserializes here as <c>Record&lt;number, number&gt;</c> (TypeScript numeric keys are strings at the JSON layer
  // but flow through as numbers via <c>Number.parseInt</c> at consumption).
  flags?: number;
  gauges?: Record<number, number>;

  // GC events (kinds 7, 8). Generation + counts on both; reason/type on GcStart; pause duration + promoted bytes on GcEnd.
  generation?: number;
  gcReason?: number;
  gcType?: number;
  gcCount?: number;
  gcPauseDurationUs?: number;
  gcPromotedBytes?: number;

  // ThreadInfo (kind 77). Emitted once per slot claim; carries the managed thread ID, the thread's
  // name (if set), and a 1-byte ThreadKind tag (Main=0, Worker=1, Pool=2, Other=3 — matches the
  // engine's `Typhon.Profiler.ThreadKind`). The kind drives the filter tree's Main/Workers/Other
  // subgrouping in trace mode (live mode reads it from the SSE delta).
  managedThreadId?: number;
  threadName?: string;
  threadKind?: number;

  // SchedulerMetronomeWait (kind 241) — span on the TickDriver lane covering the metronome's
  // inter-tick wait. multiplier reuses the existing tickMultiplier field. (#289 follow-up)
  /** Stopwatch timestamp (µs) of the next-tick target the metronome was waiting for. */
  metronomeScheduledUs?: number;
  /** 0 = CatchUp (target already past at wait start), 1 = Throttled (mult > 1), 2 = Headroom (normal idle). */
  metronomeIntentClass?: number;
  /** Bit 0x1 = Sleep entered, 0x2 = Yield entered, 0x4 = Spin entered. */
  metronomePhaseFlags?: number;

  // SchedulerOverloadDetector (kind 242) — per-tick OverloadDetector state snapshot. (#289 follow-up)
  /** Actual / target tick duration ratio used to drive escalation/deescalation. */
  overrunRatio?: number;
  /** Consecutive ticks above the overrun threshold (1.2× by default). */
  consecutiveOverrun?: number;
  /** Consecutive ticks below the deescalation ratio (0.6× by default). */
  consecutiveUnderrun?: number;
  /** Consecutive ticks where event-queue depth grew. */
  consecutiveQueueGrowth?: number;
  /** Total event-queue depth at this tick. */
  queueDepth?: number;

  // QueueTickEnd (kind 244) — per-(tick, queue) rollup at end-of-tick. Folded by the server's
  // IncrementalCacheBuilder into the v12 QueueTickSummaries cache section; surfaced here so
  // chunk-decoded traces also expose the rollup (e.g., for queue-tooltip drill-downs). The wire
  // format is hand-coded by `QueueTickEndCodec` — instant only, no span-header extension.
  queueId?: number;
  queuePeakDepth?: number;
  queueEndOfTickDepth?: number;
  queueOverflowCount?: number;
  queueProduced?: number;
  queueConsumed?: number;

  // ThreadContextSwitch (kind 254) — one ON-CPU slice for the thread identified by `targetSlotIdx`.
  // The 12-byte common-header timestamp is the slice START; `durationUs` (decoded from a u32 QPC
  // duration) is how long it held the CPU. The viewer renders the GAPS between consecutive slices.
  /** Re-projection target: the thread slot whose ON-CPU slice this is (NOT the producer `threadSlot`). */
  targetSlotIdx?: number;
  /** Logical CPU id the slice ran on. */
  processorNumber?: number;
  /** Raw `ThreadWaitReason` byte — why the thread left the CPU at the END of this slice. See `WaitReasonNames`. */
  waitReason?: number;
  /** Post-switch `System.Diagnostics.ThreadState` raw byte. */
  threadState?: number;
  /** True when the CPU went to the System Idle thread next (no contender for the core). */
  gettingIdle?: boolean;
  /** Time the thread spent on the ready queue immediately before this slice started (µs). 0 = unknown. */
  readyTimeUs?: number;
}

/**
 * One per-tick summary row from /api/trace/open. Matches the server's TickSummary struct (24 bytes). Used to drive the timeline overview without
 * loading any detail events — populated from the sidecar cache, which is built lazily on first open and reused thereafter.
 */
export interface TickSummary {
  tickNumber: number;
  /** Absolute start timestamp in microseconds (same origin as globalStartUs). Enables viewport-range → tick-number lookup without loading chunks. */
  startUs: number;
  durationUs: number;
  eventCount: number;
  maxSystemDurationUs: number;
  /** 64-bit bitmask serialized as a decimal string (exact precision preserved). Bit N set iff system index N ran in this tick (capped at 64). */
  activeSystemsBitmask: string;
  // ── v9 fields (issue #289 follow-up) ──
  /** OverloadDetector level at this tick's TickEnd. 0=Normal..4=PlayerShedding. Zero on v8 traces. */
  overloadLevel?: number;
  /** Effective tick-rate multiplier (1, 2, 3, 4, 6). >1 means engine voluntarily throttled itself. Zero on v8 traces. */
  tickMultiplier?: number;
  /** Duration (µs) of the metronome wait that PRECEDED this tick. Saturates at 65535 µs. Answers "how long did the engine sleep before this tick?". */
  metronomeWaitUs?: number;
  /** Wait intent — 0=CatchUp (target already past), 1=Throttled (mult>1), 2=Headroom (normal idle). */
  metronomeIntentClass?: number;
  // ── v11 fields (issue #289 follow-up) ──
  /** OverloadDetector consecutive-overrun streak at end-of-tick. Climbs to EscalationTicks (5 default), resets on any non-overrun tick. */
  consecutiveOverrun?: number;
  /** OverloadDetector consecutive-underrun streak. Climbs to DeescalationTicks (20 default) for deescalation; resets on any overrun. */
  consecutiveUnderrun?: number;
}

/** Aggregate duration per system across the whole trace. */
export interface SystemAggregate {
  systemIndex: number;
  invocationCount: number;
  totalDurationUs: number;
}

/** Global metrics computed once by the sidecar cache build. Available immediately on open. */
export interface GlobalMetrics {
  globalStartUs: number;
  globalEndUs: number;
  maxTickDurationUs: number;
  maxSystemDurationUs: number;
  p95TickDurationUs: number;
  totalEvents: number;
  totalTicks: number;
  systemAggregates: SystemAggregate[];
}

/** One entry in the chunk manifest — a tick range the server can serve as a single chunk. */
export interface ChunkManifestEntry {
  fromTick: number;
  toTick: number;
  eventCount: number;
  /**
   * True iff this chunk is a continuation of the previous chunk's last tick (intra-tick split, cache v8+). Continuation chunks
   * have no leading `TickStart` record — the decoder must seed its tick counter to `fromTick` directly, not `fromTick - 1`.
   * Multiple chunks can share the same `(fromTick, toTick)` range: the original chunk plus N continuations, all covering the
   * same tick. Older caches (v7 and earlier) never emit this flag, so the server-side DTO always defaults to `false` for them.
   */
  isContinuation: boolean;
}

/** /api/trace/open response shape — metadata + summary + global metrics + chunk manifest in one payload. */
export interface OpenTraceResponse {
  status: 'ready' | 'building';
  /**
   * Hex-encoded SHA-256 fingerprint of the source trace file (computed from mtime, length, and a prefix/suffix sample).
   * Used by the client's OPFS chunk store as the invalidation key: same fingerprint = chunks still valid; different fingerprint
   * (source rebuilt) = cached chunks become orphaned and get garbage-collected by the global cleanup sweep.
   */
  fingerprint: string;
  header: TraceMetadata['header'];
  systems: TraceMetadata['systems'];
  archetypes: TraceMetadata['archetypes'];
  componentTypes: TraceMetadata['componentTypes'];
  spanNames: Record<number, string>;
  globalMetrics: GlobalMetrics;
  tickSummaries: TickSummary[];
  chunkManifest: ChunkManifestEntry[];
  /**
   * Full GC-suspension list for the whole trace, delivered at open time so the per-tick pause-time chart in the GC track can use a
   * STABLE yMax across all chunk load/evict cycles. Without this, yMax was derived from whichever chunks were currently resident and
   * rescaled visibly as the LRU turned over. The server computes this once per session-slot lifetime by walking every chunk and
   * filtering GcSuspension records — cached thereafter. Typical payload is a few hundred entries × ~40 bytes = under 20 KB.
   */
  gcSuspensions: { startUs: number; durationUs: number; threadSlot: number }[];
}

/** /api/trace/chunk response shape — events for a specific tick range. */
export interface ChunkResponse {
  fromTick: number;
  toTick: number;
  events: TraceEvent[];
}

/** A tick source abstracts over file-based and live-streamed data */
export interface TickSource {
  readonly metadata: TraceMetadata;
  getEvents(fromTick: number, toTick: number): Promise<TraceEvent[]>;
}
