namespace Typhon.Workbench.Dtos.Query;

/// <summary>
/// Response from <c>POST /api/sessions/{id}/query/execute</c>: one page of matching entities plus the plan that produced
/// them. Returned to the client's TanStack-Query <c>useQueryConsoleExecute</c> hook; rendered in the result grid (design
/// §4.6) with the plan available via the <c>plan ▾</c> drawer.
/// </summary>
/// <param name="Rows">One entry per matching entity for this page, in primary-stream order (engine default; explicit
/// <c>ORDER BY</c> overrides). The grid client-sorts within the page when the user clicks a column header; sorting across
/// the full result requires re-running with <c>ORDER BY</c>.</param>
/// <param name="TotalCountEstimate">Planner's pre-execution cardinality estimate. Shown as <c>"≈ N rows"</c> when the
/// page boundary is hit (<see cref="HasMore"/> is <c>true</c>); the actual <see cref="Rows"/> length is exact for this
/// page.</param>
/// <param name="HasMore"><c>true</c> when more rows exist past this page (client shows the "Load more" button).</param>
/// <param name="ResolvedRevisionTsn">The MVCC snapshot the query actually ran at. Phase 1: always the open
/// transaction's <c>TSN</c> (HEAD). Phase 2 surfaces this for the As-of-picker's "ran at revision N" badge.</param>
/// <param name="ExecutionWallNs">Wall-clock execution time in nanoseconds. Used by the result toolbar ("142 rows in
/// 4.2 ms") and by the history rail.</param>
/// <param name="Plan">The plan that produced this result. Same shape as <c>/query/plan</c> returns; lets the result
/// drawer (Phase 2) re-use the cost chip's structural plan without a second round-trip.</param>
/// <param name="Warnings">Engine / Console warnings — e.g. "result truncated to 1000 rows", "AT REVISION fell back to
/// HEAD because the requested revision is out of retention". Rendered as inline toolbar pills.</param>
public record QueryResultDto(
    QueryRowDto[] Rows,
    long TotalCountEstimate,
    bool HasMore,
    long ResolvedRevisionTsn,
    long ExecutionWallNs,
    QueryPlanDto Plan,
    QueryWarningDto[] Warnings);

/// <summary>
/// One row in the result grid. Carries the entity identity plus only the projected cells (the columns the result grid
/// shows) — not the entity's full component card. Detail-panel materialisation on selection is a separate per-entity
/// fetch (cross-link with Data Browser).
/// </summary>
/// <remarks>
/// <b>Shape deviation:</b> Design doc §6.3 models this as <c>Dictionary&lt;string, ComponentValueDto&gt;</c> keyed by
/// component typeName, but that's structurally insufficient — each component carries multiple fields. Phase 1 ships the
/// flattened <see cref="QueryCellDto"/>[] shape that the result grid's column model expects directly. Tracked as
/// deviation D6 in the plan.
/// </remarks>
/// <param name="EntityId">Raw 64-bit packed entity value as a decimal string. Same convention as
/// <c>EntityRowDto</c> / <c>EntityDetailDto</c> in the Data Browser.</param>
/// <param name="Cells">One entry per projected column. Column order matches the grid's left-to-right rendering and is
/// stable for the query.</param>
public record QueryRowDto(
    string EntityId,
    QueryCellDto[] Cells);

/// <summary>
/// One cell in a <see cref="QueryRowDto"/>. <see cref="TypeName"/> + <see cref="FieldId"/> joins to the cached
/// <c>ComponentSchemaDto</c> for label + layout — same join the Data Browser uses. <see cref="Value"/> is a JSON-native
/// scalar for primitive fields, null for complex types (in which case <see cref="Raw"/> carries the hex of the field
/// bytes so the value is still inspectable from the Detail panel).
/// </summary>
/// <param name="TypeName">Component <c>typeName</c> the cell's field belongs to.</param>
/// <param name="FieldId">Field id within the component (joins to <c>FieldDto.FieldId</c>).</param>
/// <param name="FieldName">Human-readable field name (e.g. <c>"GuildId"</c>) — added so the result grid can render
/// column headers without a separate schema round-trip. The server already has the <c>Field</c> object when
/// decoding, so populating this is free.</param>
/// <param name="Value">Decoded scalar (number / bool / string / enum-as-string), or null for complex types.</param>
/// <param name="Raw">Lowercase hex of the field's raw bytes (always present, even when <see cref="Value"/> is set —
/// matches <c>ComponentValueDto.Raw</c> contract).</param>
public record QueryCellDto(
    string TypeName,
    int FieldId,
    string FieldName,
    object Value,
    string Raw);

/// <summary>One non-fatal warning attached to a query result. Stable <see cref="Code"/> so the client can render UI
/// affordances per warning kind; <see cref="Message"/> is the human-readable display string.</summary>
/// <param name="Code">Stable warning code (e.g. <c>"result_truncated"</c>, <c>"revision_clamped_to_head"</c>).</param>
/// <param name="Message">User-facing message — rendered as-is in the toolbar warning pill.</param>
public record QueryWarningDto(string Code, string Message);

/// <summary>
/// Body of <c>POST /api/sessions/{id}/query/execute</c>: DSL text plus the runtime parameters that don't belong in the
/// DSL itself (revision target, page window). Keeping these out of the DSL lets the client re-run the same DSL at a
/// different revision / page without re-parsing.
/// </summary>
/// <param name="Dsl">The Query Console DSL text. Parsed server-side; same parser as <c>/query/parse</c>.</param>
/// <param name="Revision">Optional revision override. When non-null, overrides any <c>AT REVISION</c> clause in the DSL.
/// Phase 1 supports only <c>head</c>.</param>
/// <param name="PageOffset">0-based offset for pagination. Combined with <see cref="PageSize"/> to skip results.</param>
/// <param name="PageSize">Page size. The server honours up to the DSL's <c>TAKE</c> (or the policy default 1000) and
/// caps page reads at the requested size.</param>
public record QueryExecuteRequest(
    string Dsl,
    RevisionDto Revision,
    int PageOffset,
    int PageSize);
