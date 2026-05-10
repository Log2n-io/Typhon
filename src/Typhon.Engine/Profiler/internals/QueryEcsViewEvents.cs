// CS0282: split-partial-struct field ordering — benign for TraceEvent ref structs (codec encodes per-field, never as a blob). See #294.
#pragma warning disable CS0282

using Typhon.Profiler;

namespace Typhon.Engine.Internals;

// ═════════════════════════════════════════════════════════════════════════════
// Phase 7 ref structs for Query / ECS:Query / ECS:View span events.
// Instants are emitted directly via EmitX factories (no ref struct needed).
// ═════════════════════════════════════════════════════════════════════════════

[TraceEvent(TraceEventKind.QueryParse, EmitEncoder = true)]
internal ref partial struct QueryParseEvent
{
    [BeginParam]
    public ushort PredicateCount;
    [BeginParam]
    public byte BranchCount;
}

[TraceEvent(TraceEventKind.QueryParseDnf, EmitEncoder = true)]
internal ref partial struct QueryParseDnfEvent
{
    [BeginParam]
    public ushort InBranches;
    [BeginParam]
    public ushort OutBranches;
}

[TraceEvent(TraceEventKind.QueryPlan, EmitEncoder = true)]
internal ref partial struct QueryPlanEvent
{
    [BeginParam]
    public byte EvaluatorCount;
    [BeginParam]
    public ushort IndexFieldIdx;
    [BeginParam]
    public long RangeMin;
    [BeginParam]
    public long RangeMax;
}

[TraceEvent(TraceEventKind.QueryEstimate, EmitEncoder = true)]
internal ref partial struct QueryEstimateEvent
{
    [BeginParam]
    public ushort FieldIdx;
    [BeginParam]
    public long Cardinality;
}

[TraceEvent(TraceEventKind.QueryPlanSort, EmitEncoder = true)]
internal ref partial struct QueryPlanSortEvent
{
    [BeginParam]
    public byte EvaluatorCount;
    [BeginParam]
    public uint SortNs;
}

[TraceEvent(TraceEventKind.QueryExecuteIndexScan, EmitEncoder = true)]
internal ref partial struct QueryExecuteIndexScanEvent
{
    [BeginParam]
    public ushort PrimaryFieldIdx;
    [BeginParam]
    public byte Mode;
}

[TraceEvent(TraceEventKind.QueryExecuteIterate, EmitEncoder = true)]
internal ref partial struct QueryExecuteIterateEvent
{
    public int ChunkCount;
    public int EntryCount;
}

[TraceEvent(TraceEventKind.QueryExecuteFilter, EmitEncoder = true)]
internal ref partial struct QueryExecuteFilterEvent
{
    [BeginParam]
    public byte FilterCount;
    public int RejectedCount;
}

[TraceEvent(TraceEventKind.QueryExecutePagination, EmitEncoder = true)]
internal ref partial struct QueryExecutePaginationEvent
{
    [BeginParam]
    public int Skip;
    [BeginParam]
    public int Take;
    public byte EarlyTerm;
}

[TraceEvent(TraceEventKind.QueryCount, EmitEncoder = true)]
internal ref partial struct QueryCountEvent
{
    public int ResultCount;
}

// ── ECS:Query depth spans ──

[TraceEvent(TraceEventKind.EcsQueryConstruct, EmitEncoder = true)]
internal ref partial struct EcsQueryConstructEvent
{
    [BeginParam]
    public ushort TargetArchId;
    [BeginParam]
    public byte Polymorphic;
    [BeginParam]
    public byte MaskSize;
}

[TraceEvent(TraceEventKind.EcsQuerySubtreeExpand, EmitEncoder = true)]
internal ref partial struct EcsQuerySubtreeExpandEvent
{
    [BeginParam]
    public ushort SubtreeCount;
    [BeginParam]
    public ushort RootId;
}

// ── ECS:View depth spans ──

[TraceEvent(TraceEventKind.EcsViewRefreshPull, EmitEncoder = true)]
internal ref partial struct EcsViewRefreshPullEvent
{
    [BeginParam]
    public uint QueryNs;
    [BeginParam]
    public ushort ArchetypeMaskBits;
}

[TraceEvent(TraceEventKind.EcsViewIncrementalDrain, EmitEncoder = true)]
internal ref partial struct EcsViewIncrementalDrainEvent
{
    public int DeltaCount;
    public byte Overflow;
}

[TraceEvent(TraceEventKind.EcsViewRefreshFull, EmitEncoder = true)]
internal ref partial struct EcsViewRefreshFullEvent
{
    [BeginParam]
    public int OldCount;
    [BeginParam]
    public int NewCount;
    [BeginParam]
    public uint RequeryNs;
}

[TraceEvent(TraceEventKind.EcsViewRefreshFullOr, EmitEncoder = true)]
internal ref partial struct EcsViewRefreshFullOrEvent
{
    [BeginParam]
    public int OldCount;
    [BeginParam]
    public int NewCount;
    [BeginParam]
    public byte BranchCount;
}
