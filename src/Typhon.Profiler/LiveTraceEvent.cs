namespace Typhon.Profiler;

/// <summary>
/// JSON-serializable trace record DTO for the live SSE stream and the file-based chunk endpoint. Flat shape with nullable
/// kind-specific fields — the server emits only the fields relevant for a given <see cref="Kind"/>, and <c>DefaultIgnoreCondition.WhenWritingNull</c>
/// elides the rest from the wire.
/// </summary>
/// <remarks>
/// <para>
/// <b>Always present:</b> <see cref="Kind"/>, <see cref="ThreadSlot"/>, <see cref="TickNumber"/>, <see cref="TimestampUs"/>.
/// </para>
/// <para>
/// <b>Span records (Kind ≥ 10):</b> also carry <see cref="DurationUs"/>, <see cref="SpanId"/>, <see cref="ParentSpanId"/>, and — when the record
/// has a distributed-trace context — <see cref="TraceIdHi"/> + <see cref="TraceIdLo"/>.
/// </para>
/// <para>
/// <b>64-bit IDs as decimal strings:</b> <see cref="SpanId"/>, <see cref="ParentSpanId"/>, <see cref="TraceIdHi"/>, <see cref="TraceIdLo"/>,
/// <see cref="EntityId"/>, and <see cref="Tsn"/> are serialized as decimal strings because JavaScript's <c>Number</c> can't represent the full
/// <c>ulong</c>/<c>long</c> range. The browser viewer treats them as opaque strings — formatting only, no arithmetic.
/// </para>
/// </remarks>
public sealed record LiveTraceEvent
{
    public int Kind { get; init; }
    public byte ThreadSlot { get; init; }
    public int TickNumber { get; init; }
    public double TimestampUs { get; init; }

    public double? DurationUs { get; init; }
    public string SpanId { get; init; }
    public string ParentSpanId { get; init; }
    public string TraceIdHi { get; init; }
    public string TraceIdLo { get; init; }

    // Instant-event fields
    public int? Phase { get; init; }
    public int? SystemIndex { get; init; }
    public int? SkipReason { get; init; }
    public int? OverloadLevel { get; init; }
    public int? TickMultiplier { get; init; }

    // Scheduler chunk
    public int? ChunkIndex { get; init; }
    public int? TotalChunks { get; init; }
    public int? EntitiesProcessed { get; init; }

    // Transaction
    public string Tsn { get; init; }
    public int? ComponentTypeId { get; init; }
    /// <summary>
    /// Component-instance count. For Transaction events: number of components committed / rolled back. For
    /// <see cref="TraceEventKind.ClusterMigration"/>: total component slots moved across the batch
    /// (entities × per-entity slot count). Disambiguated by <see cref="Kind"/>.
    /// </summary>
    public int? ComponentCount { get; init; }
    public bool? ConflictDetected { get; init; }

    // ECS
    public int? ArchetypeId { get; init; }
    public string EntityId { get; init; }
    public int? CascadeCount { get; init; }
    public int? ResultCount { get; init; }
    public int? ScanMode { get; init; }
    public bool? Found { get; init; }
    public int? Mode { get; init; }
    public int? DeltaCount { get; init; }

    // Page cache
    public int? FilePageIndex { get; init; }
    public int? PageCount { get; init; }

    // Cluster migration
    public int? MigrationCount { get; init; }

    // Transaction persist
    public string WalLsn { get; init; }

    // Page cache backpressure
    public int? RetryCount { get; init; }
    public int? DirtyCount { get; init; }
    public int? EpochCount { get; init; }

    // WAL
    public int? BatchByteCount { get; init; }
    public int? FrameCount { get; init; }
    public string HighLsn { get; init; }
    public int? NewSegmentIndex { get; init; }
    public string TargetLsn { get; init; }

    // Checkpoint
    public int? DirtyPageCount { get; init; }
    public int? Reason { get; init; }
    public int? WrittenCount { get; init; }
    public int? TransitionedCount { get; init; }
    public int? RecycledCount { get; init; }

    // Statistics
    public int? EntityCount { get; init; }
    public int? MutationCount { get; init; }
    public int? SamplingInterval { get; init; }

    // Memory allocation
    public int? Direction { get; init; }
    public int? SourceTag { get; init; }
    public double? SizeBytes { get; init; }
    public double? TotalAfterBytes { get; init; }

    // Per-tick gauge snapshot
    public uint? Flags { get; init; }
    public System.Collections.Generic.Dictionary<int, double> Gauges { get; init; }

    // GC events
    public int? Generation { get; init; }
    public int? GcReason { get; init; }
    public int? GcType { get; init; }
    public uint? GcCount { get; init; }
    public double? GcPauseDurationUs { get; init; }
    public double? GcPromotedBytes { get; init; }

    // Thread info
    public int? ManagedThreadId { get; init; }
    public string ThreadName { get; init; }
    /// <summary>Producer-thread category — drives the viewer's filter tree's Main / Workers / Other split.</summary>
    public ThreadKind? ThreadKind { get; init; }

    // Spatial query / R-tree / maintain / trigger / tier-index payload fields
    /// <summary>Number of R-tree nodes visited during a spatial query traversal.</summary>
    public int? NodesVisited { get; init; }
    /// <summary>Number of R-tree leaves entered during an AABB query.</summary>
    public int? LeavesEntered { get; init; }
    /// <summary>Number of restarts during a spatial query (snapshot revalidation).</summary>
    public int? RestartCount { get; init; }
    /// <summary>Category mask supplied as a query filter or applied to a trigger region.</summary>
    public uint? CategoryMask { get; init; }
    /// <summary>Radius for a Radius spatial query.</summary>
    public double? Radius { get; init; }
    /// <summary>Maximum distance for a Ray spatial query.</summary>
    public double? MaxDist { get; init; }
    /// <summary>Number of frustum planes for a Frustum spatial query.</summary>
    public int? PlaneCount { get; init; }
    /// <summary>K (neighbour count) for a KNN spatial query.</summary>
    public int? K { get; init; }
    /// <summary>Iteration count during a KNN spatial query (radius doublings).</summary>
    public int? IterCount { get; init; }
    /// <summary>Final search radius reached by a KNN spatial query.</summary>
    public double? FinalRadius { get; init; }
    /// <summary>Variant discriminator for the Count spatial query (0 = AABB, 1 = Radius).</summary>
    public int? Variant { get; init; }
    /// <summary>R-tree node depth for an Insert/NodeSplit operation.</summary>
    public int? Depth { get; init; }
    /// <summary><c>true</c> if a Spatial R-tree Insert triggered a node split.</summary>
    public bool? DidSplit { get; init; }
    /// <summary><c>true</c> if a Spatial R-tree Remove caused a leaf collapse.</summary>
    public bool? LeafCollapse { get; init; }
    /// <summary>Split axis index (0 = X, 1 = Y, 2 = Z) for a Spatial R-tree NodeSplit.</summary>
    public int? SplitAxis { get; init; }
    /// <summary>Entry count placed in the left half of a Spatial R-tree NodeSplit.</summary>
    public int? LeftCount { get; init; }
    /// <summary>Entry count placed in the right half of a Spatial R-tree NodeSplit.</summary>
    public int? RightCount { get; init; }
    /// <summary>Leaf count produced by a Spatial R-tree BulkLoad.</summary>
    public int? LeafCount { get; init; }
    /// <summary><c>true</c> if a Spatial Maintain Insert produced a degenerate AABB.</summary>
    public bool? DidDegenerate { get; init; }
    /// <summary>Squared escape distance for a Spatial Maintain UpdateSlowPath span.</summary>
    public double? EscapeDistSq { get; init; }
    /// <summary>Cluster count for a Spatial TierIndex Rebuild.</summary>
    public int? ClusterCount { get; init; }
    /// <summary>Previous version for a Spatial TierIndex Rebuild.</summary>
    public int? OldVersion { get; init; }
    /// <summary>New version for a Spatial TierIndex Rebuild.</summary>
    public int? NewVersion { get; init; }
    /// <summary>Spatial trigger region id.</summary>
    public int? RegionId { get; init; }
    /// <summary>Occupant count inside a Spatial trigger region during a single evaluation.</summary>
    public int? OccupantCount { get; init; }
    /// <summary>Number of entities entering a Spatial trigger region during a single evaluation.</summary>
    public int? EnterCount { get; init; }
    /// <summary>Number of entities leaving a Spatial trigger region during a single evaluation.</summary>
    public int? LeaveCount { get; init; }

    // Scheduler / Runtime / Data tx / MVCC / index payload fields
    /// <summary>Scheduler system index (used by Scheduler:System and RuntimeTransactionLifecycle spans).</summary>
    public int? SysIdx { get; init; }
    /// <summary><c>true</c> if a Scheduler SingleThreaded system was running a parallel query.</summary>
    public bool? IsParallelQuery { get; init; }
    /// <summary>Chunk count processed by a Scheduler SingleThreaded system.</summary>
    public int? ChunkCount { get; init; }
    /// <summary>Worker thread id for Scheduler:Worker spans.</summary>
    public int? WorkerId { get; init; }
    /// <summary>Spin count observed during a Scheduler:WorkerIdle wait.</summary>
    public int? SpinCount { get; init; }
    /// <summary>Idle time (µs) for a Scheduler:WorkerIdle span.</summary>
    public double? IdleUs { get; init; }
    /// <summary>Wait time (µs) for a Scheduler:WorkerBetweenTick span.</summary>
    public double? WaitUs { get; init; }
    /// <summary>Wake reason byte for a Scheduler:WorkerBetweenTick span.</summary>
    public int? WakeReason { get; init; }
    /// <summary>Completing system index for a Scheduler:DependencyFanOut span.</summary>
    public int? CompletingSysIdx { get; init; }
    /// <summary>Successor count for a Scheduler:DependencyFanOut span.</summary>
    public int? SuccCount { get; init; }
    /// <summary>Skipped successor count for a Scheduler:DependencyFanOut span.</summary>
    public int? SkippedCount { get; init; }
    /// <summary>System count for a Scheduler:GraphBuild span.</summary>
    public int? SysCount { get; init; }
    /// <summary>Edge count for a Scheduler:GraphBuild span.</summary>
    public int? EdgeCount { get; init; }
    /// <summary>Topological-sort length for a Scheduler:GraphBuild span.</summary>
    public int? TopoLen { get; init; }
    /// <summary>Old system count for a Scheduler:GraphRebuild span.</summary>
    public int? OldSysCount { get; init; }
    /// <summary>New system count for a Scheduler:GraphRebuild span.</summary>
    public int? NewSysCount { get; init; }
    /// <summary>Transaction duration (µs) for a RuntimeTransactionLifecycle span.</summary>
    public double? TxDurUs { get; init; }
    /// <summary><c>true</c> if a RuntimeTransactionLifecycle span ended with a successful commit.</summary>
    public bool? Success { get; init; }
    /// <summary>Tick number for a RuntimeSubscriptionOutputExecute span (carries its own tick — independent of the global TickNumber).</summary>
    public long? Tick { get; init; }
    /// <summary>Level / depth for a RuntimeSubscriptionOutputExecute span.</summary>
    public int? Level { get; init; }
    /// <summary>Client count fanned out during a RuntimeSubscriptionOutputExecute span.</summary>
    public int? ClientCount { get; init; }
    /// <summary>Views refreshed during a RuntimeSubscriptionOutputExecute span.</summary>
    public int? ViewsRefreshed { get; init; }
    /// <summary>Total delta records pushed during a RuntimeSubscriptionOutputExecute span.</summary>
    public uint? DeltasPushed { get; init; }
    /// <summary>Overflow count observed during a RuntimeSubscriptionOutputExecute span.</summary>
    public int? OverflowCount { get; init; }
    /// <summary>Page range start for a StoragePageCacheDirtyWalk span.</summary>
    public int? RangeStart { get; init; }
    /// <summary>Page range length for a StoragePageCacheDirtyWalk span.</summary>
    public int? RangeLen { get; init; }
    /// <summary>Time the page range had been dirty when the walk visited it (ms).</summary>
    public int? DirtyMs { get; init; }
    /// <summary>UoW id for DataTransaction:{Init,Prepare,Validate,Cleanup} spans.</summary>
    public int? UowId { get; init; }
    /// <summary>Primary key (entity id) targeted by a DataMvccVersionCleanup span.</summary>
    public string Pk { get; init; }
    /// <summary>Number of MVCC versions freed by a DataMvccVersionCleanup span.</summary>
    public int? EntriesFreed { get; init; }
    /// <summary>Buffer id targeted by a DataIndexBTreeBulkInsert span.</summary>
    public int? BufferId { get; init; }
    /// <summary>Entry count appended by a DataIndexBTreeBulkInsert span.</summary>
    public int? EntryCount { get; init; }

    // Query pipeline payload fields
    /// <summary>Predicate count parsed during a QueryParse span.</summary>
    public int? PredicateCount { get; init; }
    /// <summary>Branch count parsed during a QueryParse span (also QueryParseDnf input/output).</summary>
    public int? BranchCount { get; init; }
    /// <summary>Input branch count for a QueryParseDnf span.</summary>
    public int? InBranches { get; init; }
    /// <summary>Output branch count produced by a QueryParseDnf span.</summary>
    public int? OutBranches { get; init; }
    /// <summary>Evaluator count produced by a QueryPlan / QueryPlanSort span.</summary>
    public int? EvaluatorCount { get; init; }
    /// <summary>Index-field index targeted by a QueryPlan span.</summary>
    public int? IndexFieldIdx { get; init; }
    /// <summary>Range minimum used by a QueryPlan span.</summary>
    public string RangeMin { get; init; }
    /// <summary>Range maximum used by a QueryPlan span.</summary>
    public string RangeMax { get; init; }
    /// <summary>Field index targeted by a QueryEstimate span.</summary>
    public int? FieldIdx { get; init; }
    /// <summary>Cardinality estimated by a QueryEstimate span.</summary>
    public string Cardinality { get; init; }
    /// <summary>Sort time (ns) for a QueryPlanSort span.</summary>
    public uint? SortNs { get; init; }
    /// <summary>Primary index field index for a QueryExecuteIndexScan span.</summary>
    public int? PrimaryFieldIdx { get; init; }
    /// <summary>Filter count applied during a QueryExecuteFilter span.</summary>
    public int? FilterCount { get; init; }
    /// <summary>Rejected entity count from a QueryExecuteFilter span.</summary>
    public int? RejectedCount { get; init; }
    /// <summary>Pagination skip value for a QueryExecutePagination span.</summary>
    public int? Skip { get; init; }
    /// <summary>Pagination take value for a QueryExecutePagination span.</summary>
    public int? Take { get; init; }
    /// <summary><c>true</c> if a QueryExecutePagination span terminated early after Take satisfied.</summary>
    public bool? EarlyTerm { get; init; }

    // ECS query construct + view refresh
    /// <summary>Target archetype id for an EcsQueryConstruct span.</summary>
    public int? TargetArchId { get; init; }
    /// <summary><c>true</c> if an EcsQueryConstruct produced a polymorphic query.</summary>
    public bool? Polymorphic { get; init; }
    /// <summary>Archetype mask bit width for an EcsQueryConstruct span.</summary>
    public int? MaskSize { get; init; }
    /// <summary>Subtree count for an EcsQuerySubtreeExpand span.</summary>
    public int? SubtreeCount { get; init; }
    /// <summary>Root archetype id for an EcsQuerySubtreeExpand span.</summary>
    public int? RootId { get; init; }
    /// <summary>Query time (ns) for an EcsViewRefreshPull span.</summary>
    public uint? QueryNs { get; init; }
    /// <summary>Archetype mask bit count for an EcsViewRefreshPull span.</summary>
    public int? ArchetypeMaskBits { get; init; }
    /// <summary><c>true</c> if an EcsViewIncrementalDrain span hit overflow.</summary>
    public bool? Overflow { get; init; }
    /// <summary>Old entity count for an EcsViewRefreshFull / EcsViewRefreshFullOr span.</summary>
    public int? OldCount { get; init; }
    /// <summary>New entity count for an EcsViewRefreshFull / EcsViewRefreshFullOr span.</summary>
    public int? NewCount { get; init; }
    /// <summary>Re-query time (ns) for an EcsViewRefreshFull span.</summary>
    public uint? RequeryNs { get; init; }

    // Durability recovery payload fields
    /// <summary>Segment count discovered during a DurabilityRecoveryDiscover span.</summary>
    public int? SegCount { get; init; }
    /// <summary>Total bytes covered by a DurabilityRecoveryDiscover span.</summary>
    public long? TotalBytes { get; init; }
    /// <summary>First segment id processed during a DurabilityRecoveryDiscover span.</summary>
    public int? FirstSegId { get; init; }
    /// <summary>Segment id processed during a DurabilityRecoverySegment span.</summary>
    public int? SegId { get; init; }
    /// <summary>Record count processed during a DurabilityRecoverySegment span.</summary>
    public int? RecCount { get; init; }
    /// <summary>Bytes processed during a DurabilityRecoverySegment span.</summary>
    public long? Bytes { get; init; }
    /// <summary><c>true</c> if a DurabilityRecoverySegment span hit a truncated tail.</summary>
    public bool? Truncated { get; init; }
    /// <summary>FPI count seen during a DurabilityRecoveryFpi span.</summary>
    public int? FpiCount { get; init; }
    /// <summary>Repaired page count from a DurabilityRecoveryFpi span.</summary>
    public int? RepairedCount { get; init; }
    /// <summary>FPI mismatch count from a DurabilityRecoveryFpi span.</summary>
    public int? Mismatches { get; init; }
    /// <summary>Records replayed during a DurabilityRecoveryRedo span.</summary>
    public int? RecordsReplayed { get; init; }
    /// <summary>UoWs replayed during a DurabilityRecoveryRedo span.</summary>
    public int? UowsReplayed { get; init; }
    /// <summary>Recovery duration (µs) for a DurabilityRecoveryRedo span.</summary>
    public uint? DurUs { get; init; }
    /// <summary>UoWs voided during a DurabilityRecoveryUndo span.</summary>
    public int? VoidedUowCount { get; init; }
    /// <summary>TickFence chunk count processed during a DurabilityRecoveryTickFence span.</summary>
    public int? TickFenceCount { get; init; }
    /// <summary>Entry count processed during a DurabilityRecoveryTickFence span.</summary>
    public int? Entries { get; init; }

    // Durability WAL/Checkpoint depth-span payload fields (codecs authored 2026-05-10).
    /// <summary>Aligned byte count for a DurabilityWal{QueueDrain,OsWrite,Buffer} span.</summary>
    public int? BytesAligned { get; init; }
    /// <summary>Producer thread id captured by a DurabilityWalBackpressure span.</summary>
    public int? ProducerThread { get; init; }
    /// <summary>Pages written in a DurabilityCheckpointWriteBatch span.</summary>
    public int? WriteBatchSize { get; init; }
    /// <summary>Staging buffer bytes allocated during a DurabilityCheckpointWriteBatch span.</summary>
    public int? StagingAllocated { get; init; }
    /// <summary>Backpressure wait time (ms) for a DurabilityCheckpointBackpressure span.</summary>
    public uint? WaitMs { get; init; }
    /// <summary><c>true</c> if the staging-buffer pool was exhausted during a DurabilityCheckpointBackpressure span.</summary>
    public bool? Exhausted { get; init; }
    /// <summary>Sleep duration (ms) for a DurabilityCheckpointSleep span.</summary>
    public uint? SleepMs { get; init; }
}
