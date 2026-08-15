using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc;
using Typhon.Workbench.DataBrowser;
using Typhon.Workbench.Dtos.Data;
using Typhon.Workbench.Dtos.Profiler;
using Typhon.Workbench.Middleware;
using Typhon.Workbench.Schema;

namespace Typhon.Workbench.Controllers;

/// <summary>
/// Session-scoped read-only Data Browser API (Module 06, v1): paged entity enumeration + per-entity component decode over an
/// Open session's live engine. Mirrors <see cref="SchemaController"/>'s auth + error-mapping model.
/// </summary>
[ApiController]
[Route("api/sessions/{sessionId:guid}/data")]
[Tags("DataBrowser")]
[RequireBootstrapToken]
[RequireSession]
public sealed class DataBrowserController : ControllerBase
{
    private readonly DataBrowserService _data;

    public DataBrowserController(DataBrowserService data)
    {
        _data = data;
    }

    /// <summary>
    /// A page of the archetype's entities. <paramref name="offset"/>/<paramref name="limit"/> index the cached snapshot (limit capped at 1000).
    /// <paramref name="preview"/> is an optional comma-separated list of <c>typeName:fieldId</c> preview columns decoded per row.
    /// </summary>
    [HttpGet("archetypes/{archetypeId}/entities")]
    public ActionResult<EntityPageDto> GetEntities(
        Guid sessionId,
        string archetypeId,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 200,
        [FromQuery] string preview = null)
        => Invoke(() => _data.GetEntityPage(sessionId, archetypeId, offset, limit, preview));

    /// <summary>Full component-card detail for one entity. <paramref name="entityId"/> is the raw 64-bit packed value as a decimal string.</summary>
    [HttpGet("archetypes/{archetypeId}/entities/{entityId}")]
    public ActionResult<EntityDetailDto> GetEntity(Guid sessionId, string archetypeId, string entityId)
        => Invoke(() => _data.GetEntityDetail(sessionId, archetypeId, entityId));

    /// <summary>
    /// Splits a cohort of entity ids into those the database still holds and those it does not (#620, design §4.4) — the
    /// "1,240 spawned here, 830 still alive" answer.
    /// </summary>
    /// <remarks>POST rather than GET because a cohort's id list does not fit in a query string.</remarks>
    [HttpPost("archetypes/{archetypeId}/entities/resolve")]
    public ActionResult<CohortResolutionDto> ResolveCohort(Guid sessionId, string archetypeId, [FromBody] CohortRequestDto body)
        => Invoke(() => _data.ResolveCohort(sessionId, archetypeId, body?.EntityIds ?? []));

    /// <summary>
    /// A page of rows for an explicit list of entity ids, in the order given — what makes "open these 830 in the Data Browser" a navigation rather than a
    /// filter the user has to rebuild by hand.
    /// </summary>
    [HttpPost("archetypes/{archetypeId}/entities/page")]
    public ActionResult<EntityPageDto> GetEntityPageForIds(
        Guid sessionId,
        string archetypeId,
        [FromBody] CohortRequestDto body,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 200,
        [FromQuery] string preview = null)
        => Invoke(() => _data.GetEntityPageForIds(sessionId, archetypeId, body?.EntityIds ?? [], offset, limit, preview));

    private ActionResult<T> Invoke<T>(Func<T> action)
    {
        try
        {
            return Ok(action());
        }
        catch (DataUnavailableException ex)
        {
            // Non-Open session (Trace / live Attach) — no engine to browse. Distinct title so the SPA can render a dedicated empty state.
            return NotFound(new ProblemDetails
            {
                Title = "data_unavailable",
                Detail = ex.Message,
                Status = StatusCodes.Status404NotFound,
            });
        }
        catch (SchemaIncompatibleException ex)
        {
            // Engine open but schema mismatched — decoding would be unsafe. 409 drives the incompatibility banner.
            return Conflict(new ProblemDetails
            {
                Title = "schema_incompatible",
                Detail = ex.Message,
                Status = StatusCodes.Status409Conflict,
            });
        }
        catch (SessionNotFoundException)
        {
            return NotFound();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }
}
