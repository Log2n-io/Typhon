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

    // ── Query Definition Export extensions (v9, #335) ──
    // Identifies the logical query instance (View or EcsQuery) this execution belongs to. The (Kind, LocalId) pair is the dedup key used by the Workbench
    // catalog to fold N executions into one definition row. Population happens in PlanBuilder via runtime-supplied parameters from the View/EcsQuery caller.
    //
    // Wire-format note: fields are [Optional] with always-set masks at v9+ producers so that v8 traces (pre-dating this extension) decode gracefully — the
    // missing optMask byte reads as 0 and these fields surface as null. v9 producers always set the mask bits, so v9 traces always carry these values.
    // ExecutionSourceFileId == 0 means "no user execution site" (scheduler-driven View.Refresh) — the Workbench falls back to the owning system attribution
    // already on the span.

    [Optional(MaskValue = 0x01)]
    private byte _queryInstanceKind;          // 0 = View, 1 = EcsQuery
    [Optional(MaskValue = 0x02)]
    private uint _queryInstanceLocalId;       // ViewId or EcsQueryId
    [Optional(MaskValue = 0x04)]
    private ushort _executionSourceFileId;
    [Optional(MaskValue = 0x08)]
    private int _executionSourceLine;
    [Optional(MaskValue = 0x10)]
    private ushort _executionSourceMethodId;

    // Runtime-emitted system attribution for the per-tick QueryPlan spans bracketed by TyphonRuntime.OnSystemEnd. PlanBuilder-emitted spans leave this unset
    // (mask bit 0 → decoder surfaces null) and continue to be attributed by their wrapping span tree. Lets the Workbench round-trip from a clicked scheduler
    // chunk (which carries systemIndex) to the matching per-tick execution, since chunks in multi-threaded mode never share a parent span.
    [Optional(MaskValue = 0x20)]
    private ushort _ownerSystemIdx;
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
