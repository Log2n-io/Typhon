using System.Collections.Generic;

namespace Typhon.Workbench.Dtos.Profiler;

// ═══════════════════════════════════════════════════════════════════════════════════════════
// Workbench DTOs for the Query Definition Export feature (#342). Field shapes match
// claude/design/Profiler/11-query-definition-export.md §5.5 verbatim. Surfaced through future
// /api/profiler/queries endpoints (#337+, sub-phases P4-P7).
// ═══════════════════════════════════════════════════════════════════════════════════════════

/// <summary>Unified (Kind, LocalId) identifier for a View or EcsQuery instance in the trace catalog.</summary>
public sealed record QueryInstanceIdDto(byte Kind, uint LocalId);

/// <summary>Repo-relative source location captured at the user call site. Pre-resolved from the QuerySourceStringTable.</summary>
public sealed record QuerySourceLocationDto(string File, int Line, string Method);

/// <summary>Structural evaluator shape (no thresholds — those are per-execution). Field name pre-resolved against the component-type table.</summary>
public sealed record FieldEvaluatorShapeDto(ushort FieldIdx, string FieldName, byte Op, string OpDisplay);

/// <summary>Aggregate per-definition stats rolled up across all observed executions in the loaded trace.</summary>
public sealed record QueryAggregateStatsDto(
    long ExecutionCount,
    long TotalWallNs,
    long AvgWallNs,
    long P50WallNs,
    long P95WallNs,
    long P99WallNs,
    long TotalRowsScanned,
    long TotalRowsReturned,
    double AvgSelectivity);

/// <summary>
/// Catalog row in the Workbench Query Catalog panel. One per distinct (Kind, LocalId) identity observed
/// in the trace's <see cref="Typhon.Profiler.TraceEventKind.QueryDefinitionDescribe"/> stream.
/// </summary>
public sealed record QueryDefinitionDto(
    QueryInstanceIdDto InstanceId,
    ushort TargetComponentType,
    short PrimaryIndexFieldIdx,
    short SortFieldIdx,
    bool SortDescending,
    IReadOnlyList<FieldEvaluatorShapeDto> Evaluators,
    IReadOnlyList<ushort> FieldDependencies,
    IReadOnlyList<int> OwnerSystemIds,
    QueryAggregateStatsDto Aggregate,
    QuerySourceLocationDto UserSource);

/// <summary>A single phase breakdown row within an execution (Parse, DNF, Plan, IndexScan, Filter, Sort, Pagination, Result).</summary>
public sealed record QueryExecutionPhaseDto(
    string PhaseName,
    long? Estimate,
    long? Actual,
    long WallNs,
    string Notes);

/// <summary>
/// Per-execution drill data — one entry per <see cref="Typhon.Profiler.TraceEventKind.QueryPlan"/> span
/// in the trace, paired with the trailing per-execution span chain (Parse / DNF / Plan / IndexScan / Iterate /
/// Filter / Pagination / Count) and the optional <see cref="Typhon.Profiler.TraceEventKind.QueryArgs"/> payload.
/// </summary>
public sealed record QueryExecutionDto(
    QueryInstanceIdDto DefinitionId,
    long SpanId,
    long ParentSpanId,
    long TickIndex,
    int SystemId,
    long StartTs,
    long EndTs,
    IReadOnlyList<long> Args,
    QueryExecutionPhaseDto[] Phases);
