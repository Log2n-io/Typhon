using System.Diagnostics;
using Typhon.Engine;
using Typhon.Workbench.DataBrowser;
using Typhon.Workbench.Dtos.Data;
using Typhon.Workbench.Dtos.Query;
using Typhon.Workbench.Schema;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Services.Querying;

/// <summary>
/// Stateless singleton powering <c>POST /api/sessions/{id}/query/{plan,execute,parse}</c>. Resolves the sessionId to an
/// <see cref="OpenSession"/>, parses the DSL, compiles to <see cref="CompiledQuery"/>, and either returns cost-chip
/// estimates (<see cref="Plan"/>) or executes + materializes rows (<see cref="Execute"/>). The <see cref="Parse"/> path
/// is used by chip-mode to rebuild from edited DSL text.
/// </summary>
/// <remarks>
/// Mirrors the <see cref="DataBrowserService"/> session-resolution pattern (singleton, injected
/// <see cref="SessionManager"/>, throws <see cref="WorkbenchException"/> for the non-Open session kinds). Phase 1: only
/// Open sessions are accepted; Trace/Attach throw with stable code <c>"data_unavailable"</c> to match the existing
/// Schema/DataBrowser controller behaviour on the same kind mismatch.
/// </remarks>
public sealed class QueryConsoleService
{
    private readonly SessionManager _sessions;

    public QueryConsoleService(SessionManager sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        _sessions = sessions;
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // /query/parse — round-trip DSL → QuerySpec + diagnostics. No engine access.
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    public QueryParseResponse Parse(Guid sessionId, string dsl)
    {
        // Validates session exists but doesn't access the engine — chip mode needs to call /parse on every keystroke,
        // and we want to fail fast if the session is gone (e.g. user closed the file).
        ResolveOpenSession(sessionId);
        return DslParser.Parse(dsl ?? string.Empty);
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // /query/plan — cost-chip estimates without executing.
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    public QueryPlanDto Plan(Guid sessionId, string dsl)
    {
        var open = ResolveOpenSession(sessionId);
        var engine = open.Engine.Engine;

        var parseResult = DslParser.Parse(dsl ?? string.Empty);
        if (parseResult.Errors.Length > 0)
        {
            // Cost chip can't reason about invalid DSL — surface the first diagnostic. UI then renders "—" per design §14.2.
            var first = parseResult.Errors[0];
            throw new WorkbenchException(400, "invalid_query_syntax",
                $"Parse error at line {first.Line}, column {first.Column}: {first.Message}");
        }

        // A read-only transaction is needed because the compiler validates against the live schema (e.g., archetype
        // resolution walks the engine's archetype registry). The transaction is short-lived — disposed before return.
        using var tx = engine.CreateReadOnlyTransaction();
        var compiled = QuerySpecCompiler.Compile(parseResult.Spec, engine, tx);
        try
        {
            return compiled.Estimate();
        }
        catch (NotSupportedException ex)
        {
            // The engine throws this for spatial shapes it can't serve on the matched tier — notably RAY on clustered
            // archetypes (AABB + Radius only, per #230 Option B). Surface as a stable 400 rather than a 500.
            throw new WorkbenchException(400, "spatial_shape_not_supported", ex.Message);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // /query/execute — run + materialize one page of rows.
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    public QueryResultDto Execute(Guid sessionId, QueryExecuteRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var open = ResolveOpenSession(sessionId);
        var engine = open.Engine.Engine;

        var parseResult = DslParser.Parse(request.Dsl ?? string.Empty);
        if (parseResult.Errors.Length > 0)
        {
            var first = parseResult.Errors[0];
            throw new WorkbenchException(400, "invalid_query_syntax",
                $"Parse error at line {first.Line}, column {first.Column}: {first.Message}");
        }

        // Revision override: Phase 1 only honours HEAD (the compiler rejects other kinds, but we also need to refuse
        // when the request body's Revision overrides a HEAD DSL with non-HEAD).
        var spec = parseResult.Spec;
        if (request.Revision != null && !string.Equals(request.Revision.Kind, "head", StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkbenchException(400, "unsupported_revision_kind",
                $"AT REVISION '{request.Revision.Kind}' is not supported in Phase 1. Only HEAD is available.");
        }

        // Pagination: the request can override the spec's TAKE for "load more"-style scrolling. We clamp to sensible
        // bounds (the design §14.6 has no cap; the user explicitly chose to fetch this much).
        var pageOffset = Math.Max(0, request.PageOffset);
        var pageSize = request.PageSize > 0 ? request.PageSize : spec.Take;

        ct.ThrowIfCancellationRequested();

        using var tx = engine.CreateReadOnlyTransaction();
        var resolvedTsn = tx.TSN;

        var compiled = QuerySpecCompiler.Compile(spec, engine, tx);

        // Time the execute path. The engine doesn't yet accept a CancellationToken in its scan loops; check between
        // execution and materialisation so the request can still bow out before we touch each entity.
        var sw = Stopwatch.StartNew();
        List<long> allIds;
        try
        {
            allIds = compiled.Execute(ct);
        }
        catch (NotSupportedException ex)
        {
            // Spatial shape unsupported on the matched tier — notably RAY on clustered archetypes (AABB + Radius only,
            // per #230 Option B). Surface as a stable 400 rather than a 500.
            throw new WorkbenchException(400, "spatial_shape_not_supported", ex.Message);
        }
        var executionNs = (long)(sw.Elapsed.TotalMilliseconds * 1_000_000);
        ct.ThrowIfCancellationRequested();

        // Pagination — server-side slice of the in-memory id list. For "ordered" queries Execute already applied
        // Skip/Take inside the engine, so the list is already the right page; for "unordered" queries the engine
        // returns the full HashSet and we slice here. Either way: TotalCountEstimate carries the pre-slice count.
        var total = allIds.Count;
        var sliceStart = Math.Min(pageOffset, total);
        var sliceEnd = Math.Min(sliceStart + pageSize, total);
        var hasMore = sliceEnd < total;

        var rows = MaterializeRows(engine, tx, allIds, sliceStart, sliceEnd - sliceStart, compiled.ProjectedComponents);

        var plan = compiled.Estimate();
        var warnings = BuildWarnings(total, sliceEnd, hasMore);

        return new QueryResultDto(
            Rows: rows,
            TotalCountEstimate: total,
            HasMore: hasMore,
            ResolvedRevisionTsn: resolvedTsn,
            ExecutionWallNs: executionNs,
            Plan: plan,
            Warnings: warnings);
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    private OpenSession ResolveOpenSession(Guid sessionId)
    {
        if (!_sessions.TryGet(sessionId, out var session))
        {
            // Same shape as SchemaService / DataBrowserService — let the controller's exception filter turn it into a 404.
            throw new SessionNotFoundException(sessionId);
        }
        if (session is not OpenSession open)
        {
            // Trace / Attach: Console is not wired for these in Phase 1 (design §10). Surface a stable code so the
            // client can render "Available only for an open .typhon file" rather than a generic 404.
            throw new WorkbenchException(400, "data_unavailable",
                $"Query Console requires an open `.typhon` file. Session is of kind '{session.Kind}'.");
        }
        return open;
    }

    /// <summary>
    /// Decode a page of entity IDs into result rows. Projects every field of every component the query touched (per
    /// Phase 1 scope: single component via <see cref="QuerySpecCompiler"/>'s <c>AssertSingleComponent</c>). Mirrors
    /// <see cref="DataBrowserService"/>'s <c>BuildPreviewRows</c> decode path: one read-only transaction, slot→name map
    /// resolved on the first opened row.
    /// </summary>
    private static QueryRowDto[] MaterializeRows(
        DatabaseEngine engine,
        Transaction tx,
        List<long> allIds,
        int start,
        int count,
        IReadOnlyList<ComponentTable> projectedComponents)
    {
        var rows = new QueryRowDto[count];
        if (count == 0) return rows;

        // Build the (typeName → definition) map once — used per-cell to look up the field.
        var definitions = new Dictionary<string, DBComponentDefinition>(StringComparer.Ordinal);
        foreach (var pc in projectedComponents)
        {
            definitions[pc.Definition.Name] = pc.Definition;
        }

        Dictionary<string, int> slotByName = null;

        for (var i = 0; i < count; i++)
        {
            var rawId = allIds[start + i];
            // FromRaw is internal — Workbench has InternalsVisibleTo on the engine (AssemblyInfo.cs:12).
            var entityId = EntityId.FromRaw(rawId);
            if (!tx.TryOpen(entityId, out var entity))
            {
                rows[i] = new QueryRowDto(((ulong)rawId).ToString(), []);
                continue;
            }

            // Resolve slot map once per page — the archetype is constant within a query, so the map is too.
            if (slotByName == null)
            {
                slotByName = new Dictionary<string, int>(entity.ComponentCount, StringComparer.Ordinal);
                for (var slot = 0; slot < entity.ComponentCount; slot++)
                {
                    slotByName[entity.GetComponentName(slot)] = slot;
                }
            }

            // For each projected component, emit one cell per field. (Phase 1: project all fields of the components
            // mentioned in WHERE. Phase 2 may add user-driven column selection.)
            var cells = new List<QueryCellDto>();
            foreach (var (typeName, def) in definitions)
            {
                if (!slotByName.TryGetValue(typeName, out var slot)) continue;
                var raw = entity.ReadRaw(slot);
                // MaxFieldId is the exclusive upper bound on the indexer (matches DataBrowserService.cs:114 pattern).
                for (var fieldId = 0; fieldId < def.MaxFieldId; fieldId++)
                {
                    var field = def[fieldId];
                    if (field == null) continue;
                    var dto = ComponentValueDecoder.Decode(field, raw);
                    cells.Add(new QueryCellDto(typeName, fieldId, field.Name, dto.Value, dto.Raw));
                }
            }

            rows[i] = new QueryRowDto(((ulong)rawId).ToString(), cells.ToArray());
        }
        return rows;
    }

    private static QueryWarningDto[] BuildWarnings(long total, int sliceEnd, bool hasMore)
    {
        if (!hasMore) return [];
        return
        [
            new QueryWarningDto(
                Code: "page_truncated",
                Message: $"Result truncated to {sliceEnd} of {total} rows. Raise TAKE or click 'Load more' to fetch the rest."),
        ];
    }
}
