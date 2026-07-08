using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Typhon.Workbench.Dtos.Query;
using Typhon.Workbench.Middleware;
using Typhon.Workbench.Schema;
using Typhon.Workbench.Services.Querying;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Controllers;

/// <summary>
/// Session-scoped Query Console API — three endpoints powering the chip-mode / DSL / Explain authoring loop (issue #386
/// Phase 1). Mirrors <see cref="SchemaController"/> / <see cref="DataBrowserController"/> auth + routing conventions.
/// </summary>
/// <remarks>
/// <para><b>Routes</b>:</para>
/// <list type="bullet">
/// <item><c>POST /api/sessions/{id}/query/plan</c> — cost-chip estimates without execution (debounced 250 ms on the client).</item>
/// <item><c>POST /api/sessions/{id}/query/execute</c> — run + paged result rows (the Run button path).</item>
/// <item><c>POST /api/sessions/{id}/query/parse</c> — round-trip DSL → <see cref="QuerySpecDto"/> for chip-mode rebuild.</item>
/// </list>
/// <para>All error paths surface as <c>WorkbenchException</c> with stable codes (see plan deviation D7). The global
/// <c>WorkbenchExceptionHandler</c> in <c>WorkbenchHost</c> turns them into RFC 7807 <see cref="ProblemDetails"/>; this
/// controller only catches the controller-local <see cref="SessionNotFoundException"/> for the 404 case.</para>
/// </remarks>
[ApiController]
[Route("api/sessions/{sessionId:guid}/query")]
[Tags("QueryConsole")]
[RequireBootstrapToken]
[RequireSession]
public sealed partial class QueryConsoleController : ControllerBase
{
    private readonly QueryConsoleService _query;
    private readonly ILogger<QueryConsoleController> _logger;

    public QueryConsoleController(QueryConsoleService query, ILogger<QueryConsoleController> logger)
    {
        _query = query;
        _logger = logger;
    }

    /// <summary>Cost-chip estimates for a DSL query — no execution. Returns <c>QueryPlanDto</c> on success.</summary>
    [HttpPost("plan")]
    public ActionResult<QueryPlanDto> GetPlan(Guid sessionId, [FromBody] QueryPlanRequest request)
        => Invoke(() => _query.Plan(sessionId, request?.Dsl));

    /// <summary>
    /// Run the query and return one page of decoded result rows. <paramref name="ct"/> is auto-injected by ASP.NET Core
    /// and signals client abort; the service checks between execution and materialisation.
    /// </summary>
    [HttpPost("execute")]
    public ActionResult<QueryResultDto> Execute(Guid sessionId, [FromBody] QueryExecuteRequest request, CancellationToken ct)
        => Invoke(() => _query.Execute(sessionId, request, ct));

    /// <summary>Round-trip parser exposed for chip-mode reconstruction (DSL → chips). Diagnostics surface in the response.</summary>
    [HttpPost("parse")]
    public ActionResult<QueryParseResponse> Parse(Guid sessionId, [FromBody] QueryParseRequest request)
        => Invoke(() => _query.Parse(sessionId, request?.Dsl));

    private ActionResult<T> Invoke<T>(Func<T> action)
    {
        try
        {
            return Ok(action());
        }
        catch (SessionNotFoundException)
        {
            return NotFound();
        }
        catch (WorkbenchException)
        {
            // Propagate to the global WorkbenchExceptionHandler in WorkbenchHost for uniform RFC 7807 mapping with
            // the stable error code (e.g. invalid_query_syntax, unknown_archetype, multi_component_where_…).
            throw;
        }
        catch (OperationCanceledException)
        {
            // Client abort — ASP.NET Core handles it; no response body needed.
            throw;
        }
        catch (Exception ex)
        {
            // Catch-all: anything that escaped the compiler/service unhandled would otherwise hit the global
            // ASP.NET error page ("An error occurred while processing your request"), which gives the client zero
            // signal about what failed. Wrap as a stable `internal_error` ProblemDetails so the user sees the
            // exception type + message in the result card, and log the full stack server-side for diagnosis.
            //
            // TargetInvocationException unwrap: the compiler invokes EcsQuery methods via reflection, so any
            // exception thrown by the engine is wrapped in TIE. Surfacing the wrapper ("Exception has been
            // thrown by the target of an invocation") tells the user nothing — peel it to reach the real cause.
            var cause = ex is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner : ex;
            LogUnhandledQueryException(cause);
            throw new WorkbenchException(
                500,
                "internal_error",
                $"{cause.GetType().Name}: {cause.Message}",
                cause);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception in QueryConsoleController — surfaced to client as internal_error")]
    private partial void LogUnhandledQueryException(Exception ex);
}
