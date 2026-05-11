// CS0282: split-partial-struct field ordering — benign for TraceEvent ref structs (the struct only carries metadata for the generator).
#pragma warning disable CS0282

using Typhon.Profiler;
#pragma warning disable CS0649 // Field is never assigned to, and will always have its default value

namespace Typhon.Engine.Internals;

// ═══════════════════════════════════════════════════════════════════════
// Instant-event producer declarations (Shape = Instant).
//
// Wire layout: [u16 size][u8 kind][u8 threadSlot][i64 timestamp][payload].
//
// The generator emits a direct `EmitX(args)` static method on TyphonEvent
// (two overloads: caller-supplied timestamp + Stopwatch.GetTimestamp()
// internal capture). No ref struct materialization at the call site, no
// Begin/Dispose, no try/finally — single-shot, fully inlinable. The
// producer ref struct declaration here is metadata-only; the generator
// reads [BeginParam] fields to determine the payload layout, then writes
// the encoder inline inside EmitX.
//
// Benchmarked (TickStart): ~42 ns generator-emitted vs ~45 ns legacy
// hand-written codec path (3 ns faster, no allocation, no EH region).
// ═══════════════════════════════════════════════════════════════════════

/// <summary>Producer for <see cref="TraceEventKind.TickStart"/>. No payload — wire is the 12-byte header only.</summary>
[TraceEvent(TraceEventKind.TickStart, Shape = TraceEventShape.Instant)]
internal ref partial struct TickStartEvent { }

/// <summary>Tick boundary. Payload: u8 overloadLevel, u8 tickMultiplier (2 B).</summary>
[TraceEvent(TraceEventKind.TickEnd, Shape = TraceEventShape.Instant)]
internal ref partial struct TickEndEvent
{
    [BeginParam] public byte OverloadLevel;
    [BeginParam] public byte TickMultiplier;
}

/// <summary>Phase boundary. Payload: u8 phase (1 B).</summary>
[TraceEvent(TraceEventKind.PhaseStart, Shape = TraceEventShape.Instant)]
internal ref partial struct PhaseStartEvent
{
    [BeginParam(ParamType = "TickPhase")] public byte Phase;
}

/// <summary>Phase boundary. Payload: u8 phase (1 B).</summary>
[TraceEvent(TraceEventKind.PhaseEnd, Shape = TraceEventShape.Instant)]
internal ref partial struct PhaseEndEvent
{
    [BeginParam(ParamType = "TickPhase")] public byte Phase;
}

/// <summary>System ready marker. Payload: u16 systemIdx, u16 predecessorCount (4 B).</summary>
[TraceEvent(TraceEventKind.SystemReady, Shape = TraceEventShape.Instant)]
internal ref partial struct SystemReadyEvent
{
    [BeginParam] public ushort SystemIdx;
    [BeginParam] public ushort PredecessorCount;
}

// ═══════════════════════════════════════════════════════════════════════
// Memory / scheduler-internal instants
// ═══════════════════════════════════════════════════════════════════════

/// <summary>Memory alloc/free instant. Payload: u8 direction, u16 sourceTag, u64 sizeBytes, u64 totalAfterBytes (19 B).</summary>
[TraceEvent(TraceEventKind.MemoryAllocEvent, Shape = TraceEventShape.Instant, Gate = "ProfilerMemoryAllocationsActive")]
internal ref partial struct MemoryAllocEvent
{
    [BeginParam] public MemoryAllocDirection Direction;
    [BeginParam] public ushort SourceTag;
    [BeginParam] public ulong SizeBytes;
    [BeginParam] public ulong TotalAfterBytes;
}

/// <summary>Scheduler-internal: system was skipped this tick. Payload: u16 systemIdx, u8 skipReason, u16 wouldBeChunkCount, u16 successorsUnblocked (7 B).</summary>
[TraceEvent(TraceEventKind.SystemSkipped, Shape = TraceEventShape.Instant)]
internal ref partial struct SystemSkippedEvent
{
    [BeginParam] public ushort SystemIdx;
    [BeginParam] public byte SkipReason;
    [BeginParam] public ushort WouldBeChunkCount;
    [BeginParam] public ushort SuccessorsUnblocked;
}

// ═══════════════════════════════════════════════════════════════════════
// Concurrency — AccessControl (kinds 90-95)
// ═══════════════════════════════════════════════════════════════════════

/// <summary>AccessControl shared-acquire instant. Payload: u16 threadId, u8 hadToWait, u16 elapsedUs (5 B).</summary>
[TraceEvent(TraceEventKind.ConcurrencyAccessControlSharedAcquire, Shape = TraceEventShape.Instant, Gate = "ConcurrencyAccessControlSharedAcquireActive")]
internal ref partial struct ConcurrencyAccessControlSharedAcquireEvent
{
    [BeginParam] public ushort ThreadId;
    [BeginParam] public bool HadToWait;
    [BeginParam] public ushort ElapsedUs;
}

/// <summary>AccessControl shared-release instant. Payload: u16 threadId (2 B).</summary>
[TraceEvent(TraceEventKind.ConcurrencyAccessControlSharedRelease, Shape = TraceEventShape.Instant, Gate = "ConcurrencyAccessControlSharedReleaseActive")]
internal ref partial struct ConcurrencyAccessControlSharedReleaseEvent
{
    [BeginParam] public ushort ThreadId;
}

/// <summary>AccessControl exclusive-acquire instant. Payload: u16 threadId, u8 hadToWait, u16 elapsedUs (5 B).</summary>
[TraceEvent(TraceEventKind.ConcurrencyAccessControlExclusiveAcquire, Shape = TraceEventShape.Instant, Gate = "ConcurrencyAccessControlExclusiveAcquireActive")]
internal ref partial struct ConcurrencyAccessControlExclusiveAcquireEvent
{
    [BeginParam] public ushort ThreadId;
    [BeginParam] public bool HadToWait;
    [BeginParam] public ushort ElapsedUs;
}

/// <summary>AccessControl exclusive-release instant. Payload: u16 threadId (2 B).</summary>
[TraceEvent(TraceEventKind.ConcurrencyAccessControlExclusiveRelease, Shape = TraceEventShape.Instant, Gate = "ConcurrencyAccessControlExclusiveReleaseActive")]
internal ref partial struct ConcurrencyAccessControlExclusiveReleaseEvent
{
    [BeginParam] public ushort ThreadId;
}

/// <summary>AccessControl promotion/demotion instant. Payload: u16 elapsedUs, u8 variant (3 B).</summary>
[TraceEvent(TraceEventKind.ConcurrencyAccessControlPromotion, Shape = TraceEventShape.Instant, Gate = "ConcurrencyAccessControlPromotionActive")]
internal ref partial struct ConcurrencyAccessControlPromotionEvent
{
    [BeginParam] public ushort ElapsedUs;
    [BeginParam] public byte Variant;
}

/// <summary>AccessControl contention instant — empty payload.</summary>
[TraceEvent(TraceEventKind.ConcurrencyAccessControlContention, Shape = TraceEventShape.Instant, Gate = "ConcurrencyAccessControlContentionActive")]
internal ref partial struct ConcurrencyAccessControlContentionEvent { }

// ═══════════════════════════════════════════════════════════════════════
// Concurrency — AccessControlSmall (kinds 96-100)
// ═══════════════════════════════════════════════════════════════════════

/// <summary>AccessControlSmall shared-acquire instant. Payload: u16 threadId (2 B).</summary>
[TraceEvent(TraceEventKind.ConcurrencyAccessControlSmallSharedAcquire, Shape = TraceEventShape.Instant, Gate = "ConcurrencyAccessControlSmallSharedAcquireActive")]
internal ref partial struct ConcurrencyAccessControlSmallSharedAcquireEvent
{
    [BeginParam] public ushort ThreadId;
}

/// <summary>AccessControlSmall shared-release instant. Payload: u16 threadId (2 B).</summary>
[TraceEvent(TraceEventKind.ConcurrencyAccessControlSmallSharedRelease, Shape = TraceEventShape.Instant, Gate = "ConcurrencyAccessControlSmallSharedReleaseActive")]
internal ref partial struct ConcurrencyAccessControlSmallSharedReleaseEvent
{
    [BeginParam] public ushort ThreadId;
}

/// <summary>AccessControlSmall exclusive-acquire instant. Payload: u16 threadId (2 B).</summary>
[TraceEvent(TraceEventKind.ConcurrencyAccessControlSmallExclusiveAcquire, Shape = TraceEventShape.Instant, Gate = "ConcurrencyAccessControlSmallExclusiveAcquireActive")]
internal ref partial struct ConcurrencyAccessControlSmallExclusiveAcquireEvent
{
    [BeginParam] public ushort ThreadId;
}

/// <summary>AccessControlSmall exclusive-release instant. Payload: u16 threadId (2 B).</summary>
[TraceEvent(TraceEventKind.ConcurrencyAccessControlSmallExclusiveRelease, Shape = TraceEventShape.Instant, Gate = "ConcurrencyAccessControlSmallExclusiveReleaseActive")]
internal ref partial struct ConcurrencyAccessControlSmallExclusiveReleaseEvent
{
    [BeginParam] public ushort ThreadId;
}

/// <summary>AccessControlSmall contention instant — empty payload.</summary>
[TraceEvent(TraceEventKind.ConcurrencyAccessControlSmallContention, Shape = TraceEventShape.Instant, Gate = "ConcurrencyAccessControlSmallContentionActive")]
internal ref partial struct ConcurrencyAccessControlSmallContentionEvent { }

// ═══════════════════════════════════════════════════════════════════════
// Concurrency — ResourceAccessControl (kinds 101-105)
// ═══════════════════════════════════════════════════════════════════════

/// <summary>Resource accessing instant. Payload: u8 success, u8 accessingCount, u16 elapsedUs (4 B).</summary>
[TraceEvent(TraceEventKind.ConcurrencyResourceAccessing, Shape = TraceEventShape.Instant, Gate = "ConcurrencyResourceAccessControlAccessingActive")]
internal ref partial struct ConcurrencyResourceAccessingEvent
{
    [BeginParam] public bool Success;
    [BeginParam] public byte AccessingCount;
    [BeginParam] public ushort ElapsedUs;
}

/// <summary>Resource modify instant. Payload: u8 success, u16 threadId, u16 elapsedUs (5 B).</summary>
[TraceEvent(TraceEventKind.ConcurrencyResourceModify, Shape = TraceEventShape.Instant, Gate = "ConcurrencyResourceAccessControlModifyActive")]
internal ref partial struct ConcurrencyResourceModifyEvent
{
    [BeginParam] public bool Success;
    [BeginParam] public ushort ThreadId;
    [BeginParam] public ushort ElapsedUs;
}

/// <summary>Resource destroy instant. Payload: u8 success, u16 elapsedUs (3 B).</summary>
[TraceEvent(TraceEventKind.ConcurrencyResourceDestroy, Shape = TraceEventShape.Instant, Gate = "ConcurrencyResourceAccessControlDestroyActive")]
internal ref partial struct ConcurrencyResourceDestroyEvent
{
    [BeginParam] public bool Success;
    [BeginParam] public ushort ElapsedUs;
}

/// <summary>Resource modify-promotion instant. Payload: u16 elapsedUs (2 B).</summary>
[TraceEvent(TraceEventKind.ConcurrencyResourceModifyPromotion, Shape = TraceEventShape.Instant, Gate = "ConcurrencyResourceAccessControlModifyPromotionActive")]
internal ref partial struct ConcurrencyResourceModifyPromotionEvent
{
    [BeginParam] public ushort ElapsedUs;
}

/// <summary>Resource contention instant — empty payload.</summary>
[TraceEvent(TraceEventKind.ConcurrencyResourceContention, Shape = TraceEventShape.Instant, Gate = "ConcurrencyResourceAccessControlContentionActive")]
internal ref partial struct ConcurrencyResourceContentionEvent { }

// ═══════════════════════════════════════════════════════════════════════
// Concurrency — Epoch (kinds 106-111)
// ═══════════════════════════════════════════════════════════════════════

/// <summary>EpochGuard scope-enter instant. Payload: u32 epoch, u8 depthBefore, u8 isDormantToActive (6 B).</summary>
[TraceEvent(TraceEventKind.ConcurrencyEpochScopeEnter, Shape = TraceEventShape.Instant, Gate = "ConcurrencyEpochScopeEnterActive")]
internal ref partial struct ConcurrencyEpochScopeEnterEvent
{
    [BeginParam] public uint Epoch;
    [BeginParam] public byte DepthBefore;
    [BeginParam] public bool IsDormantToActive;
}

/// <summary>EpochGuard scope-exit instant. Payload: u32 epoch, u8 isOutermost (5 B).</summary>
[TraceEvent(TraceEventKind.ConcurrencyEpochScopeExit, Shape = TraceEventShape.Instant, Gate = "ConcurrencyEpochScopeExitActive")]
internal ref partial struct ConcurrencyEpochScopeExitEvent
{
    [BeginParam] public uint Epoch;
    [BeginParam] public bool IsOutermost;
}

/// <summary>GlobalEpoch advance instant. Payload: u32 newEpoch (4 B).</summary>
[TraceEvent(TraceEventKind.ConcurrencyEpochAdvance, Shape = TraceEventShape.Instant, Gate = "ConcurrencyEpochAdvanceActive")]
internal ref partial struct ConcurrencyEpochAdvanceEvent
{
    [BeginParam] public uint NewEpoch;
}

/// <summary>Epoch RefreshScope mid-scope bump instant. Payload: u32 oldEpoch, u32 newEpoch (8 B).</summary>
[TraceEvent(TraceEventKind.ConcurrencyEpochRefresh, Shape = TraceEventShape.Instant, Gate = "ConcurrencyEpochRefreshActive")]
internal ref partial struct ConcurrencyEpochRefreshEvent
{
    [BeginParam] public uint OldEpoch;
    [BeginParam] public uint NewEpoch;
}

/// <summary>Epoch slot-claim instant. Payload: u16 slotIndex, u16 threadId, u16 activeCount (6 B).</summary>
[TraceEvent(TraceEventKind.ConcurrencyEpochSlotClaim, Shape = TraceEventShape.Instant, Gate = "ConcurrencyEpochSlotClaimActive")]
internal ref partial struct ConcurrencyEpochSlotClaimEvent
{
    [BeginParam] public ushort SlotIndex;
    [BeginParam] public ushort ThreadId;
    [BeginParam] public ushort ActiveCount;
}

/// <summary>Epoch slot-reclaim instant. Payload: u16 slotIndex, u16 oldOwner, u16 newOwner (6 B).</summary>
[TraceEvent(TraceEventKind.ConcurrencyEpochSlotReclaim, Shape = TraceEventShape.Instant, Gate = "ConcurrencyEpochSlotReclaimActive")]
internal ref partial struct ConcurrencyEpochSlotReclaimEvent
{
    [BeginParam] public ushort SlotIndex;
    [BeginParam] public ushort OldOwner;
    [BeginParam] public ushort NewOwner;
}

// ═══════════════════════════════════════════════════════════════════════
// Concurrency — AdaptiveWaiter / OlcLatch (kinds 112-116)
// ═══════════════════════════════════════════════════════════════════════

/// <summary>AdaptiveWaiter yield-or-sleep instant. Payload: u16 spinCountBefore, u8 transitionKind (3 B). Field is named TransitionKind (not Kind) to avoid clashing with the JSON polymorphic discriminator on the generated DTO.</summary>
[TraceEvent(TraceEventKind.ConcurrencyAdaptiveWaiterYieldOrSleep, Shape = TraceEventShape.Instant, Gate = "ConcurrencyAdaptiveWaiterYieldOrSleepActive")]
internal ref partial struct ConcurrencyAdaptiveWaiterYieldOrSleepEvent
{
    [BeginParam] public ushort SpinCountBefore;
    [BeginParam] public AdaptiveWaiterTransitionKind TransitionKind;
}

/// <summary>OlcLatch write-lock attempt instant. Payload: u32 versionBefore, u8 success (5 B).</summary>
[TraceEvent(TraceEventKind.ConcurrencyOlcLatchWriteLockAttempt, Shape = TraceEventShape.Instant, Gate = "ConcurrencyOlcLatchWriteLockAttemptActive")]
internal ref partial struct ConcurrencyOlcLatchWriteLockAttemptEvent
{
    [BeginParam] public uint VersionBefore;
    [BeginParam] public bool Success;
}

/// <summary>OlcLatch write-unlock instant. Payload: u32 oldVersion, u32 newVersion (8 B).</summary>
[TraceEvent(TraceEventKind.ConcurrencyOlcLatchWriteUnlock, Shape = TraceEventShape.Instant, Gate = "ConcurrencyOlcLatchWriteUnlockActive")]
internal ref partial struct ConcurrencyOlcLatchWriteUnlockEvent
{
    [BeginParam] public uint OldVersion;
    [BeginParam] public uint NewVersion;
}

/// <summary>OlcLatch mark-obsolete instant. Payload: u32 version (4 B).</summary>
[TraceEvent(TraceEventKind.ConcurrencyOlcLatchMarkObsolete, Shape = TraceEventShape.Instant, Gate = "ConcurrencyOlcLatchMarkObsoleteActive")]
internal ref partial struct ConcurrencyOlcLatchMarkObsoleteEvent
{
    [BeginParam] public uint Version;
}

/// <summary>OlcLatch validation-fail instant. Payload: u32 expectedVersion, u32 actualVersion (8 B).</summary>
[TraceEvent(TraceEventKind.ConcurrencyOlcLatchValidationFail, Shape = TraceEventShape.Instant, Gate = "ConcurrencyOlcLatchValidationFailActive")]
internal ref partial struct ConcurrencyOlcLatchValidationFailEvent
{
    [BeginParam] public uint ExpectedVersion;
    [BeginParam] public uint ActualVersion;
}

// ═══════════════════════════════════════════════════════════════════════
// Spatial — Grid (kinds 127-129)
// ═══════════════════════════════════════════════════════════════════════

/// <summary>Spatial grid cell-tier change. Payload: i32 cellKey, u8 oldTier, u8 newTier (6 B).</summary>
[TraceEvent(TraceEventKind.SpatialGridCellTierChange, Shape = TraceEventShape.Instant, Gate = "SpatialGridCellTierChangeActive")]
internal ref partial struct SpatialGridCellTierChangeEvent
{
    [BeginParam] public int CellKey;
    [BeginParam] public byte OldTier;
    [BeginParam] public byte NewTier;
}

/// <summary>Spatial grid occupancy change. Payload: i32 cellKey, i8 delta, u16 occBefore, u16 occAfter (9 B).</summary>
[TraceEvent(TraceEventKind.SpatialGridOccupancyChange, Shape = TraceEventShape.Instant, Gate = "SpatialGridOccupancyChangeActive")]
internal ref partial struct SpatialGridOccupancyChangeEvent
{
    [BeginParam] public int CellKey;
    [BeginParam] public sbyte Delta;
    [BeginParam] public ushort OccBefore;
    [BeginParam] public ushort OccAfter;
}

/// <summary>Spatial grid cluster-cell assign. Payload: i32 clusterChunkId, i32 cellKey, u16 archetypeId (10 B).</summary>
[TraceEvent(TraceEventKind.SpatialGridClusterCellAssign, Shape = TraceEventShape.Instant, Gate = "SpatialGridClusterCellAssignActive")]
internal ref partial struct SpatialGridClusterCellAssignEvent
{
    [BeginParam] public int ClusterChunkId;
    [BeginParam] public int CellKey;
    [BeginParam] public ushort ArchetypeId;
}

// ═══════════════════════════════════════════════════════════════════════
// Spatial — Cell:Index (kinds 130-132)
// ═══════════════════════════════════════════════════════════════════════

/// <summary>Spatial cell-index add. Payload: i32 cellKey, i32 slot, i32 clusterChunkId, i32 capacity (16 B).</summary>
[TraceEvent(TraceEventKind.SpatialCellIndexAdd, Shape = TraceEventShape.Instant, Gate = "SpatialCellIndexAddActive")]
internal ref partial struct SpatialCellIndexAddEvent
{
    [BeginParam] public int CellKey;
    [BeginParam] public int Slot;
    [BeginParam] public int ClusterChunkId;
    [BeginParam] public int Capacity;
}

/// <summary>Spatial cell-index update. Payload: i32 cellKey, i32 slot (8 B).</summary>
[TraceEvent(TraceEventKind.SpatialCellIndexUpdate, Shape = TraceEventShape.Instant, Gate = "SpatialCellIndexUpdateActive")]
internal ref partial struct SpatialCellIndexUpdateEvent
{
    [BeginParam] public int CellKey;
    [BeginParam] public int Slot;
}

/// <summary>Spatial cell-index remove. Payload: i32 cellKey, i32 slot, i32 swappedClusterId (12 B).</summary>
[TraceEvent(TraceEventKind.SpatialCellIndexRemove, Shape = TraceEventShape.Instant, Gate = "SpatialCellIndexRemoveActive")]
internal ref partial struct SpatialCellIndexRemoveEvent
{
    [BeginParam] public int CellKey;
    [BeginParam] public int Slot;
    [BeginParam] public int SwappedClusterId;
}

// ═══════════════════════════════════════════════════════════════════════
// Spatial — ClusterMigration (kinds 133-135)
// ═══════════════════════════════════════════════════════════════════════

/// <summary>Spatial cluster-migration detect. Payload: u16 archetypeId, i32 clusterChunkId, i32 oldCellKey, i32 newCellKey (14 B).</summary>
[TraceEvent(TraceEventKind.SpatialClusterMigrationDetect, Shape = TraceEventShape.Instant, Gate = "SpatialClusterMigrationDetectActive")]
internal ref partial struct SpatialClusterMigrationDetectEvent
{
    [BeginParam] public ushort ArchetypeId;
    [BeginParam] public int ClusterChunkId;
    [BeginParam] public int OldCellKey;
    [BeginParam] public int NewCellKey;
}

/// <summary>Spatial cluster-migration queue. Payload: u16 archetypeId, i32 clusterChunkId, u16 queueLen (8 B).</summary>
[TraceEvent(TraceEventKind.SpatialClusterMigrationQueue, Shape = TraceEventShape.Instant, Gate = "SpatialClusterMigrationQueueActive")]
internal ref partial struct SpatialClusterMigrationQueueEvent
{
    [BeginParam] public ushort ArchetypeId;
    [BeginParam] public int ClusterChunkId;
    [BeginParam] public ushort QueueLen;
}

/// <summary>Spatial cluster-migration hysteresis. Payload: u16 archetypeId, i32 clusterChunkId, f32 escapeDistSq (10 B).</summary>
[TraceEvent(TraceEventKind.SpatialClusterMigrationHysteresis, Shape = TraceEventShape.Instant, Gate = "SpatialClusterMigrationHysteresisActive")]
internal ref partial struct SpatialClusterMigrationHysteresisEvent
{
    [BeginParam] public ushort ArchetypeId;
    [BeginParam] public int ClusterChunkId;
    [BeginParam] public float EscapeDistSq;
}

// ═══════════════════════════════════════════════════════════════════════
// Spatial — TierIndex / Maintain / Trigger (kinds 137-145)
// ═══════════════════════════════════════════════════════════════════════

/// <summary>Spatial tier-index version-skip. Payload: u16 archetypeId, i32 version, u8 reason (7 B).</summary>
[TraceEvent(TraceEventKind.SpatialTierIndexVersionSkip, Shape = TraceEventShape.Instant, Gate = "SpatialTierIndexVersionSkipActive")]
internal ref partial struct SpatialTierIndexVersionSkipEvent
{
    [BeginParam] public ushort ArchetypeId;
    [BeginParam] public int Version;
    [BeginParam] public byte Reason;
}

/// <summary>Spatial maintain AABB validate. Payload: i64 entityPK, u16 componentTypeId, u8 opcode (11 B).</summary>
[TraceEvent(TraceEventKind.SpatialMaintainAabbValidate, Shape = TraceEventShape.Instant, Gate = "SpatialMaintainAabbValidateActive")]
internal ref partial struct SpatialMaintainAabbValidateEvent
{
    [BeginParam] public long EntityPK;
    [BeginParam] public ushort ComponentTypeId;
    [BeginParam] public byte Opcode;
}

/// <summary>Spatial maintain back-pointer write. Payload: i32 componentChunkId, i32 leafChunkId, u16 slotIndex (10 B).</summary>
[TraceEvent(TraceEventKind.SpatialMaintainBackPointerWrite, Shape = TraceEventShape.Instant, Gate = "SpatialMaintainBackPointerWriteActive")]
internal ref partial struct SpatialMaintainBackPointerWriteEvent
{
    [BeginParam] public int ComponentChunkId;
    [BeginParam] public int LeafChunkId;
    [BeginParam] public ushort SlotIndex;
}

/// <summary>Spatial trigger region create/destroy. Payload: u8 op, u16 regionId, u32 categoryMask (7 B).</summary>
[TraceEvent(TraceEventKind.SpatialTriggerRegion, Shape = TraceEventShape.Instant, Gate = "SpatialTriggerRegionActive")]
internal ref partial struct SpatialTriggerRegionEvent
{
    [BeginParam] public byte Op;
    [BeginParam] public ushort RegionId;
    [BeginParam] public uint CategoryMask;
}

/// <summary>Spatial trigger occupant diff. Payload: u16 regionId, u16 prevCount, u16 currCount, u16 enterCount, u16 leaveCount (10 B).</summary>
[TraceEvent(TraceEventKind.SpatialTriggerOccupantDiff, Shape = TraceEventShape.Instant, Gate = "SpatialTriggerOccupantDiffActive")]
internal ref partial struct SpatialTriggerOccupantDiffEvent
{
    [BeginParam] public ushort RegionId;
    [BeginParam] public ushort PrevCount;
    [BeginParam] public ushort CurrCount;
    [BeginParam] public ushort EnterCount;
    [BeginParam] public ushort LeaveCount;
}

/// <summary>Spatial trigger cache invalidate. Payload: u16 regionId, i32 oldVersion, i32 newVersion (10 B).</summary>
[TraceEvent(TraceEventKind.SpatialTriggerCacheInvalidate, Shape = TraceEventShape.Instant, Gate = "SpatialTriggerCacheInvalidateActive")]
internal ref partial struct SpatialTriggerCacheInvalidateEvent
{
    [BeginParam] public ushort RegionId;
    [BeginParam] public int OldVersion;
    [BeginParam] public int NewVersion;
}

// ═══════════════════════════════════════════════════════════════════════
// Scheduler — System / Worker / Dispense / Dependency (kinds 146-155)
// ═══════════════════════════════════════════════════════════════════════

/// <summary>Scheduler system start-execution. Payload: u16 sysIdx (2 B).</summary>
[TraceEvent(TraceEventKind.SchedulerSystemStartExecution, Shape = TraceEventShape.Instant, Gate = "SchedulerSystemStartExecutionActive")]
internal ref partial struct SchedulerSystemStartExecutionEvent
{
    [BeginParam] public ushort SysIdx;
}

/// <summary>Scheduler system completion. Payload: u16 sysIdx, u8 reason, u32 durationUs (7 B).</summary>
[TraceEvent(TraceEventKind.SchedulerSystemCompletion, Shape = TraceEventShape.Instant, Gate = "SchedulerSystemCompletionActive")]
internal ref partial struct SchedulerSystemCompletionEvent
{
    [BeginParam] public ushort SysIdx;
    [BeginParam] public byte Reason;
    [BeginParam] public uint DurationUs;
}

/// <summary>Scheduler system queue wait. Payload: u16 sysIdx, u32 queueWaitUs (6 B).</summary>
[TraceEvent(TraceEventKind.SchedulerSystemQueueWait, Shape = TraceEventShape.Instant, Gate = "SchedulerSystemQueueWaitActive")]
internal ref partial struct SchedulerSystemQueueWaitEvent
{
    [BeginParam] public ushort SysIdx;
    [BeginParam] public uint QueueWaitUs;
}

/// <summary>Scheduler worker wake. Payload: u8 workerId, u32 delayUs (5 B).</summary>
[TraceEvent(TraceEventKind.SchedulerWorkerWake, Shape = TraceEventShape.Instant, Gate = "SchedulerWorkerWakeActive")]
internal ref partial struct SchedulerWorkerWakeEvent
{
    [BeginParam] public byte WorkerId;
    [BeginParam] public uint DelayUs;
}

/// <summary>Scheduler dispense. Payload: u16 sysIdx, i32 chunkIdx, u8 workerId (7 B).</summary>
[TraceEvent(TraceEventKind.SchedulerDispense, Shape = TraceEventShape.Instant, Gate = "SchedulerDispenseActive")]
internal ref partial struct SchedulerDispenseEvent
{
    [BeginParam] public ushort SysIdx;
    [BeginParam] public int ChunkIdx;
    [BeginParam] public byte WorkerId;
}

/// <summary>Scheduler dependency-ready. Payload: u16 fromSysIdx, u16 toSysIdx, u16 fanOut, u16 predRemain (8 B).</summary>
[TraceEvent(TraceEventKind.SchedulerDependencyReady, Shape = TraceEventShape.Instant, Gate = "SchedulerDependencyReadyActive")]
internal ref partial struct SchedulerDependencyReadyEvent
{
    [BeginParam] public ushort FromSysIdx;
    [BeginParam] public ushort ToSysIdx;
    [BeginParam] public ushort FanOut;
    [BeginParam] public ushort PredRemain;
}

// ═══════════════════════════════════════════════════════════════════════
// Scheduler — Overload (kinds 156-158, 242)
// ═══════════════════════════════════════════════════════════════════════

/// <summary>Scheduler overload level-change. Payload: u8 prevLvl, u8 newLvl, f32 ratio, i32 queueDepth, u8 oldMul, u8 newMul (12 B).</summary>
[TraceEvent(TraceEventKind.SchedulerOverloadLevelChange, Shape = TraceEventShape.Instant, Gate = "SchedulerOverloadLevelChangeActive")]
internal ref partial struct SchedulerOverloadLevelChangeEvent
{
    [BeginParam] public byte PrevLvl;
    [BeginParam] public byte NewLvl;
    [BeginParam] public float Ratio;
    [BeginParam] public int QueueDepth;
    [BeginParam] public byte OldMul;
    [BeginParam] public byte NewMul;
}

/// <summary>Scheduler overload system-shed. Payload: u16 sysIdx, u8 level, u16 divisor, u8 decision (6 B).</summary>
[TraceEvent(TraceEventKind.SchedulerOverloadSystemShed, Shape = TraceEventShape.Instant, Gate = "SchedulerOverloadSystemShedActive")]
internal ref partial struct SchedulerOverloadSystemShedEvent
{
    [BeginParam] public ushort SysIdx;
    [BeginParam] public byte Level;
    [BeginParam] public ushort Divisor;
    [BeginParam] public byte Decision;
}

/// <summary>Scheduler overload tick-multiplier. Payload: i64 tick, u8 multiplier, u8 intervalTicks (10 B).</summary>
[TraceEvent(TraceEventKind.SchedulerOverloadTickMultiplier, Shape = TraceEventShape.Instant, Gate = "SchedulerOverloadTickMultiplierActive")]
internal ref partial struct SchedulerOverloadTickMultiplierEvent
{
    [BeginParam] public long Tick;
    [BeginParam] public byte Multiplier;
    [BeginParam] public byte IntervalTicks;
}

/// <summary>Scheduler overload-detector per-tick snapshot. Payload: i64 tick, f32 overrunRatio, u16 consecutiveOverrun, u16 consecutiveUnderrun, u16 consecutiveQueueGrowth, i32 queueDepth, u8 level, u8 multiplier (24 B).</summary>
[TraceEvent(TraceEventKind.SchedulerOverloadDetector, Shape = TraceEventShape.Instant, Gate = "SchedulerOverloadDetectorActive")]
internal ref partial struct SchedulerOverloadDetectorEvent
{
    [BeginParam] public long Tick;
    [BeginParam] public float OverrunRatio;
    [BeginParam] public ushort ConsecutiveOverrun;
    [BeginParam] public ushort ConsecutiveUnderrun;
    [BeginParam] public ushort ConsecutiveQueueGrowth;
    [BeginParam] public int QueueDepth;
    [BeginParam] public byte Level;
    [BeginParam] public byte Multiplier;
}

// ═══════════════════════════════════════════════════════════════════════
// Runtime — UoW (kinds 161-162)
// ═══════════════════════════════════════════════════════════════════════

/// <summary>Runtime UoW create. Payload: i64 tick (8 B).</summary>
[TraceEvent(TraceEventKind.RuntimePhaseUoWCreate, Shape = TraceEventShape.Instant, Gate = "RuntimePhaseUoWCreateActive")]
internal ref partial struct RuntimePhaseUoWCreateEvent
{
    [BeginParam] public long Tick;
}

/// <summary>Runtime UoW flush. Payload: i64 tick, i32 changeCount (12 B).</summary>
[TraceEvent(TraceEventKind.RuntimePhaseUoWFlush, Shape = TraceEventShape.Instant, Gate = "RuntimePhaseUoWFlushActive")]
internal ref partial struct RuntimePhaseUoWFlushEvent
{
    [BeginParam] public long Tick;
    [BeginParam] public int ChangeCount;
}

// ═══════════════════════════════════════════════════════════════════════
// Storage / Memory (kinds 53-58, 65, 66, 71)
// ═══════════════════════════════════════════════════════════════════════

/// <summary>Storage segment create. Payload: i32 segmentId, i32 pageCount (8 B).</summary>
[TraceEvent(TraceEventKind.StorageSegmentCreate, Shape = TraceEventShape.Instant, Gate = "StorageSegmentCreateActive")]
internal ref partial struct StorageSegmentCreateEvent
{
    [BeginParam] public int SegmentId;
    [BeginParam] public int PageCount;
}

/// <summary>Storage segment grow. Payload: i32 segmentId, i32 oldLen, i32 newLen (12 B).</summary>
[TraceEvent(TraceEventKind.StorageSegmentGrow, Shape = TraceEventShape.Instant, Gate = "StorageSegmentGrowActive")]
internal ref partial struct StorageSegmentGrowEvent
{
    [BeginParam] public int SegmentId;
    [BeginParam] public int OldLen;
    [BeginParam] public int NewLen;
}

/// <summary>Storage segment load. Payload: i32 segmentId, i32 pageCount (8 B).</summary>
[TraceEvent(TraceEventKind.StorageSegmentLoad, Shape = TraceEventShape.Instant, Gate = "StorageSegmentLoadActive")]
internal ref partial struct StorageSegmentLoadEvent
{
    [BeginParam] public int SegmentId;
    [BeginParam] public int PageCount;
}

/// <summary>Storage chunk-segment grow. Payload: i32 stride, i32 oldCap, i32 newCap (12 B).</summary>
[TraceEvent(TraceEventKind.StorageChunkSegmentGrow, Shape = TraceEventShape.Instant, Gate = "StorageChunkSegmentGrowActive")]
internal ref partial struct StorageChunkSegmentGrowEvent
{
    [BeginParam] public int Stride;
    [BeginParam] public int OldCap;
    [BeginParam] public int NewCap;
}

/// <summary>Storage file-handle open/close. Payload: u8 op, i32 filePathId, u8 modeOrReason (6 B).</summary>
[TraceEvent(TraceEventKind.StorageFileHandle, Shape = TraceEventShape.Instant, Gate = "StorageFileHandleEnabledActive")]
internal ref partial struct StorageFileHandleEvent
{
    [BeginParam] public byte Op;
    [BeginParam] public int FilePathId;
    [BeginParam] public byte ModeOrReason;
}

/// <summary>Storage occupancy-map grow. Payload: i32 oldCap, i32 newCap (8 B).</summary>
[TraceEvent(TraceEventKind.StorageOccupancyMapGrow, Shape = TraceEventShape.Instant, Gate = "StorageOccupancyMapGrowActive")]
internal ref partial struct StorageOccupancyMapGrowEvent
{
    [BeginParam] public int OldCap;
    [BeginParam] public int NewCap;
}

/// <summary>Memory alignment waste. Payload: i32 size, i32 alignment, u16 wastePctHundredths (10 B).</summary>
[TraceEvent(TraceEventKind.MemoryAlignmentWaste, Shape = TraceEventShape.Instant, Gate = "MemoryAlignmentWasteActive")]
internal ref partial struct MemoryAlignmentWasteEvent
{
    [BeginParam] public int Size;
    [BeginParam] public int Alignment;
    [BeginParam] public ushort WastePctHundredths;
}

// ═══════════════════════════════════════════════════════════════════════
// Data plane (kinds 200, 201, 204-208)
// ═══════════════════════════════════════════════════════════════════════

/// <summary>Data transaction conflict. Payload: i64 tsn, i64 pk, i32 componentTypeId, u8 conflictType (21 B).</summary>
[TraceEvent(TraceEventKind.DataTransactionConflict, Shape = TraceEventShape.Instant, Gate = "DataTransactionConflictActive")]
internal ref partial struct DataTransactionConflictEvent
{
    [BeginParam] public long Tsn;
    [BeginParam] public long Pk;
    [BeginParam] public int ComponentTypeId;
    [BeginParam] public byte ConflictType;
}

/// <summary>Data MVCC chain walk. Payload: i64 tsn, u8 chainLen, u8 visibility (10 B).</summary>
[TraceEvent(TraceEventKind.DataMvccChainWalk, Shape = TraceEventShape.Instant, Gate = "DataMvccChainWalkActive")]
internal ref partial struct DataMvccChainWalkEvent
{
    [BeginParam] public long Tsn;
    [BeginParam] public byte ChainLen;
    [BeginParam] public byte Visibility;
}

/// <summary>Data BTree search-restart. Payload: u8 retryReason, u8 restartCount (2 B).</summary>
[TraceEvent(TraceEventKind.DataIndexBTreeSearch, Shape = TraceEventShape.Instant, Gate = "DataIndexBTreeSearchActive")]
internal ref partial struct DataIndexBTreeSearchEvent
{
    [BeginParam] public byte RetryReason;
    [BeginParam] public byte RestartCount;
}

/// <summary>Data BTree range-scan revalidate. Payload: u8 restartCount (1 B).</summary>
[TraceEvent(TraceEventKind.DataIndexBTreeRangeScanRevalidate, Shape = TraceEventShape.Instant, Gate = "DataIndexBTreeRangeScanRevalidateActive")]
internal ref partial struct DataIndexBTreeRangeScanRevalidateEvent
{
    [BeginParam] public byte RestartCount;
}

/// <summary>Data BTree rebalance-fallback. Payload: u8 reason (1 B).</summary>
[TraceEvent(TraceEventKind.DataIndexBTreeRebalanceFallback, Shape = TraceEventShape.Instant, Gate = "DataIndexBTreeRebalanceFallbackActive")]
internal ref partial struct DataIndexBTreeRebalanceFallbackEvent
{
    [BeginParam] public byte Reason;
}

/// <summary>Data BTree root op. Payload: u8 op, i32 rootChunkId, u8 height (6 B).</summary>
[TraceEvent(TraceEventKind.DataIndexBTreeRoot, Shape = TraceEventShape.Instant, Gate = "DataIndexBTreeRootActive")]
internal ref partial struct DataIndexBTreeRootEvent
{
    [BeginParam] public byte Op;
    [BeginParam] public int RootChunkId;
    [BeginParam] public byte Height;
}

/// <summary>Data BTree node copy-on-write. Payload: i32 srcChunkId, i32 dstChunkId (8 B).</summary>
[TraceEvent(TraceEventKind.DataIndexBTreeNodeCow, Shape = TraceEventShape.Instant, Gate = "DataIndexBTreeNodeCowActive")]
internal ref partial struct DataIndexBTreeNodeCowEvent
{
    [BeginParam] public int SrcChunkId;
    [BeginParam] public int DstChunkId;
}

// ═══════════════════════════════════════════════════════════════════════
// Query / ECS:Query / ECS:View
// ═══════════════════════════════════════════════════════════════════════

/// <summary>Query plan primary-select. Payload: u8 candidates, u8 winnerIdx, u8 reason (3 B).</summary>
[TraceEvent(TraceEventKind.QueryPlanPrimarySelect, Shape = TraceEventShape.Instant, Gate = "QueryPlanPrimarySelectActive")]
internal ref partial struct QueryPlanPrimarySelectEvent
{
    [BeginParam] public byte Candidates;
    [BeginParam] public byte WinnerIdx;
    [BeginParam] public byte Reason;
}

/// <summary>Query execute storage-mode. Payload: u8 mode (1 B).</summary>
[TraceEvent(TraceEventKind.QueryExecuteStorageMode, Shape = TraceEventShape.Instant, Gate = "QueryExecuteStorageModeActive")]
internal ref partial struct QueryExecuteStorageModeEvent
{
    [BeginParam] public byte Mode;
}

/// <summary>ECS query mask-AND. Payload: u16 bitsBefore, u16 bitsAfter, u8 opType (5 B).</summary>
[TraceEvent(TraceEventKind.EcsQueryMaskAnd, Shape = TraceEventShape.Instant, Gate = "EcsQueryMaskAndActive")]
internal ref partial struct EcsQueryMaskAndEvent
{
    [BeginParam] public ushort BitsBefore;
    [BeginParam] public ushort BitsAfter;
    [BeginParam] public byte OpType;
}

/// <summary>ECS query constraint-enabled. Payload: u16 typeId, u8 enableBit (3 B).</summary>
[TraceEvent(TraceEventKind.EcsQueryConstraintEnabled, Shape = TraceEventShape.Instant, Gate = "EcsQueryConstraintEnabledActive")]
internal ref partial struct EcsQueryConstraintEnabledEvent
{
    [BeginParam] public ushort TypeId;
    [BeginParam] public byte EnableBit;
}

/// <summary>ECS query spatial-attach. Payload: u8 spatialType, f32 qbX1, f32 qbY1, f32 qbX2, f32 qbY2 (17 B).</summary>
[TraceEvent(TraceEventKind.EcsQuerySpatialAttach, Shape = TraceEventShape.Instant, Gate = "EcsQuerySpatialAttachActive")]
internal ref partial struct EcsQuerySpatialAttachEvent
{
    [BeginParam] public byte SpatialType;
    [BeginParam] public float QbX1;
    [BeginParam] public float QbY1;
    [BeginParam] public float QbX2;
    [BeginParam] public float QbY2;
}

/// <summary>ECS view delta-buffer overflow. Payload: i64 currentTsn, i64 tailTsn, u16 marginPagesLost (18 B).</summary>
[TraceEvent(TraceEventKind.EcsViewDeltaBufferOverflow, Shape = TraceEventShape.Instant, Gate = "EcsViewDeltaBufferOverflowActive")]
internal ref partial struct EcsViewDeltaBufferOverflowEvent
{
    [BeginParam] public long CurrentTsn;
    [BeginParam] public long TailTsn;
    [BeginParam] public ushort MarginPagesLost;
}

/// <summary>ECS view process-entry. Payload: i64 pk, u16 fieldIdx, u8 pass (11 B).</summary>
[TraceEvent(TraceEventKind.EcsViewProcessEntry, Shape = TraceEventShape.Instant, Gate = "EcsViewProcessEntryActive")]
internal ref partial struct EcsViewProcessEntryEvent
{
    [BeginParam] public long Pk;
    [BeginParam] public ushort FieldIdx;
    [BeginParam] public byte Pass;
}

/// <summary>ECS view process-entry OR. Payload: i64 pk, u8 branchCount, u32 bitmapDelta (13 B).</summary>
[TraceEvent(TraceEventKind.EcsViewProcessEntryOr, Shape = TraceEventShape.Instant, Gate = "EcsViewProcessEntryOrActive")]
internal ref partial struct EcsViewProcessEntryOrEvent
{
    [BeginParam] public long Pk;
    [BeginParam] public byte BranchCount;
    [BeginParam] public uint BitmapDelta;
}

/// <summary>ECS view registry register. Payload: u16 viewId, u16 fieldIdx, u16 regCount (6 B).</summary>
[TraceEvent(TraceEventKind.EcsViewRegistryRegister, Shape = TraceEventShape.Instant, Gate = "EcsViewRegistryRegisterActive")]
internal ref partial struct EcsViewRegistryRegisterEvent
{
    [BeginParam] public ushort ViewId;
    [BeginParam] public ushort FieldIdx;
    [BeginParam] public ushort RegCount;
}

/// <summary>ECS view registry deregister. Payload: u16 viewId, u16 fieldIdx, u16 regCount (6 B).</summary>
[TraceEvent(TraceEventKind.EcsViewRegistryDeregister, Shape = TraceEventShape.Instant, Gate = "EcsViewRegistryDeregisterActive")]
internal ref partial struct EcsViewRegistryDeregisterEvent
{
    [BeginParam] public ushort ViewId;
    [BeginParam] public ushort FieldIdx;
    [BeginParam] public ushort RegCount;
}

/// <summary>ECS view delta-cache miss. Payload: i64 pk, u8 reason (9 B).</summary>
[TraceEvent(TraceEventKind.EcsViewDeltaCacheMiss, Shape = TraceEventShape.Instant, Gate = "EcsViewDeltaCacheMissActive")]
internal ref partial struct EcsViewDeltaCacheMissEvent
{
    [BeginParam] public long Pk;
    [BeginParam] public byte Reason;
}

// ═══════════════════════════════════════════════════════════════════════
// Durability — WAL / Recovery / UoW
// ═══════════════════════════════════════════════════════════════════════

/// <summary>Durability WAL group-commit. Payload: u16 triggerMs, i32 producerThread (6 B).</summary>
[TraceEvent(TraceEventKind.DurabilityWalGroupCommit, Shape = TraceEventShape.Instant, Gate = "DurabilityWalGroupCommitActive")]
internal ref partial struct DurabilityWalGroupCommitEvent
{
    [BeginParam] public ushort TriggerMs;
    [BeginParam] public int ProducerThread;
}

/// <summary>Durability WAL queue. Payload: u8 drainAttempt, i32 dataLen, u8 waitReason (6 B).</summary>
[TraceEvent(TraceEventKind.DurabilityWalQueue, Shape = TraceEventShape.Instant, Gate = "DurabilityWalQueueActive")]
internal ref partial struct DurabilityWalQueueEvent
{
    [BeginParam] public byte DrainAttempt;
    [BeginParam] public int DataLen;
    [BeginParam] public byte WaitReason;
}

/// <summary>Durability WAL frame CRC. Payload: u16 frameCount, u32 crcStart (6 B).</summary>
[TraceEvent(TraceEventKind.DurabilityWalFrame, Shape = TraceEventShape.Instant, Gate = "DurabilityWalFrameActive")]
internal ref partial struct DurabilityWalFrameEvent
{
    [BeginParam] public ushort FrameCount;
    [BeginParam] public uint CrcStart;
}

/// <summary>Durability recovery start. Payload: i64 checkpointLsn, u8 reason (9 B).</summary>
[TraceEvent(TraceEventKind.DurabilityRecoveryStart, Shape = TraceEventShape.Instant, Gate = "DurabilityRecoveryStartActive")]
internal ref partial struct DurabilityRecoveryStartEvent
{
    [BeginParam] public long CheckpointLsn;
    [BeginParam] public byte Reason;
}

/// <summary>Durability recovery record. Payload: u8 chunkType, i64 lsn, i32 size (13 B).</summary>
[TraceEvent(TraceEventKind.DurabilityRecoveryRecord, Shape = TraceEventShape.Instant, Gate = "DurabilityRecoveryRecordActive")]
internal ref partial struct DurabilityRecoveryRecordEvent
{
    [BeginParam] public byte ChunkType;
    [BeginParam] public long Lsn;
    [BeginParam] public int Size;
}

/// <summary>Durability UoW state transition. Payload: u8 from, u8 to, u16 uowId, u8 reason (5 B).</summary>
[TraceEvent(TraceEventKind.DurabilityUowState, Shape = TraceEventShape.Instant, Gate = "DurabilityUowStateActive")]
internal ref partial struct DurabilityUowStateEvent
{
    [BeginParam] public byte From;
    [BeginParam] public byte To;
    [BeginParam] public ushort UowId;
    [BeginParam] public byte Reason;
}

/// <summary>Durability UoW deadline. Payload: i64 deadline, i64 remaining, u8 expired (17 B).</summary>
[TraceEvent(TraceEventKind.DurabilityUowDeadline, Shape = TraceEventShape.Instant, Gate = "DurabilityUowDeadlineActive")]
internal ref partial struct DurabilityUowDeadlineEvent
{
    [BeginParam] public long Deadline;
    [BeginParam] public long Remaining;
    [BeginParam] public byte Expired;
}

// ═══════════════════════════════════════════════════════════════════════
// GC instants (kinds 7-9) — emitted from the GC-ingestion thread, which
// already owns its slot. ExternalSlot=true skips the GetOrAssignSlot()
// claim and uses the caller-supplied slot directly.
// ═══════════════════════════════════════════════════════════════════════

/// <summary>GC start. Payload: u8 generation, u8 reason, u8 type, u32 count (7 B).</summary>
[TraceEvent(TraceEventKind.GcStart, Shape = TraceEventShape.Instant, ExternalSlot = true)]
internal ref partial struct GcStartEvent
{
    [BeginParam] public byte Generation;
    [BeginParam] public GcReason Reason;
    [BeginParam] public GcType Type;
    [BeginParam] public uint Count;
}

/// <summary>GC end carrying the per-gen heap-size snapshot. Payload: u8 generation, u32 count, i64 pauseDurationTicks, 6×u64 sizes (69 B).</summary>
[TraceEvent(TraceEventKind.GcEnd, Shape = TraceEventShape.Instant, ExternalSlot = true)]
internal ref partial struct GcEndEvent
{
    [BeginParam] public byte Generation;
    [BeginParam] public uint Count;
    [BeginParam] public long PauseDurationTicks;
    [BeginParam] public ulong PromotedBytes;
    [BeginParam] public ulong Gen0SizeAfter;
    [BeginParam] public ulong Gen1SizeAfter;
    [BeginParam] public ulong Gen2SizeAfter;
    [BeginParam] public ulong LohSizeAfter;
    [BeginParam] public ulong PohSizeAfter;
    [BeginParam] public ulong TotalCommittedBytes;
}
