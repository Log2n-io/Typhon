using Microsoft.AspNetCore.Mvc;
using Typhon.Engine;
using Typhon.Workbench.Dtos.Storage;
using Typhon.Workbench.Middleware;
using Typhon.Workbench.Sessions;
using Typhon.Workbench.Storage;

namespace Typhon.Workbench.Controllers;

/// <summary>
/// Read-only REST surface for the Database File Map (Module 15, Track A). A1 serves the coarse tier
/// (regions / overview / region); A2 adds the detail tier — per-page detail tiles and one-off page / segment /
/// chunk decodes. Every endpoint introspects the Open session's live <see cref="DatabaseEngine"/> via
/// <see cref="StorageMapService"/>.
/// </summary>
[ApiController]
[Route("api/sessions/{sessionId:guid}/dbmap")]
[Tags("StorageMap")]
[RequireBootstrapToken]
[RequireSession]
public sealed class StorageMapController : WorkbenchControllerBase
{
    private readonly StorageMapService _service;

    public StorageMapController(StorageMapService service)
    {
        _service = service;
    }

    /// <summary>Data file + WAL metadata and the segment table.</summary>
    [HttpGet("regions")]
    public ActionResult<StorageRegionsDto> GetRegions(Guid sessionId) => Invoke(_service.GetRegions);

    /// <summary>Aggregate storage health rollup — whole-DB summary + per-segment table (GAP-16).</summary>
    [HttpGet("health")]
    public ActionResult<StorageHealthDto> GetHealth(Guid sessionId) => Invoke(_service.GetHealth);

    /// <summary>The top levels of the Hilbert aggregate pyramid.</summary>
    [HttpGet("overview")]
    public ActionResult<StorageOverviewDto> GetOverview(Guid sessionId) => Invoke(_service.GetOverview);

    /// <summary>Coarse per-page descriptors. In A1 the whole coarse map is returned regardless of node / lod.</summary>
    [HttpGet("region")]
    public ActionResult<StorageRegionDto> GetRegion(Guid sessionId, [FromQuery] int node = 0, [FromQuery] string lod = "leaf")
        => Invoke((engine, name) => _service.GetRegion(engine, name, node, lod));

    /// <summary>The detail tier for one quadtree node — per-page fill / write-age / CRC / residency (A2).</summary>
    [HttpGet("region/detail")]
    public ActionResult<StorageRegionDetailDto> GetRegionDetail(Guid sessionId, [FromQuery] int node = 0)
        => Invoke((engine, name) => _service.GetRegionDetail(engine, name, node));

    /// <summary>One page's full decode — header fields, CRC, residency, chunk fill, and the directory for a segment root.</summary>
    [HttpGet("page/{idx:int}")]
    public ActionResult<StoragePageDetailDto> GetPage(Guid sessionId, int idx)
        => InvokeFound((engine, name) => _service.GetPageDetail(engine, name, idx));

    /// <summary>One segment's directory — its kind, layout, and ordered page list.</summary>
    [HttpGet("segment/{id:int}")]
    public ActionResult<StorageSegmentDetailDto> GetSegment(Guid sessionId, int id)
        => InvokeFound((engine, _) => _service.GetSegmentDetail(engine, id));

    /// <summary>One segment's harvest summary — chunk allocation plus cluster / entity-map stats (lazy; A6).</summary>
    [HttpGet("segment/{id:int}/summary")]
    public ActionResult<StorageSegmentSummaryDto> GetSegmentSummary(Guid sessionId, int id)
        => InvokeFound((engine, _) => _service.GetSegmentSummary(engine, id));

    /// <summary>One chunk's decoded L4 content — component fields, a generic byte-class view, or the unknown tile.</summary>
    [HttpGet("chunk/{segId:int}/{chunkId:int}")]
    public ActionResult<StorageChunkDto> GetChunk(Guid sessionId, int segId, int chunkId)
        => InvokeFound((engine, _) => _service.GetChunk(engine, segId, chunkId));

    private ActionResult<T> Invoke<T>(Func<DatabaseEngine, string, T> action)
    {
        var session = ResolveOpenSession(out var conflict);
        return session is null ? conflict : Ok(action(session.Engine.Engine, Path.GetFileName(session.FilePath)));
    }

    private ActionResult<T> InvokeFound<T>(Func<DatabaseEngine, string, T> action) where T : class
    {
        var session = ResolveOpenSession(out var conflict);
        if (session is null)
        {
            return conflict;
        }
        var result = action(session.Engine.Engine, Path.GetFileName(session.FilePath));
        return result is null ? NotFound() : Ok(result);
    }

    /// <summary>
    /// Resolves the request's Open (file) session. RequireSession has already validated the token and stashed
    /// the session; the map needs an engine, so only Open sessions qualify — others degrade to a 409, mirroring
    /// ResourcesController.
    /// </summary>
    private OpenSession ResolveOpenSession(out ActionResult conflict)
    {
        if (HttpContext.Items["Session"] is OpenSession session)
        {
            conflict = null;
            return session;
        }

        conflict = ConflictKindMismatch("The Database File Map is only available for Open (file) sessions.");
        return null;
    }
}
