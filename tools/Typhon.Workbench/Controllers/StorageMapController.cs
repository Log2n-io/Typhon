using Microsoft.AspNetCore.Mvc;
using Typhon.Engine;
using Typhon.Workbench.Dtos.Storage;
using Typhon.Workbench.Middleware;
using Typhon.Workbench.Sessions;
using Typhon.Workbench.Storage;

namespace Typhon.Workbench.Controllers;

/// <summary>
/// Read-only REST surface for the Database File Map (Module 15, Track A — A1 coarse tier). Every endpoint
/// introspects the Open session's live <see cref="DatabaseEngine"/> via <see cref="StorageMapService"/>.
/// </summary>
[ApiController]
[Route("api/sessions/{sessionId:guid}/dbmap")]
[Tags("StorageMap")]
[RequireBootstrapToken]
[RequireSession]
public sealed class StorageMapController : ControllerBase
{
    private readonly StorageMapService _service;

    public StorageMapController(StorageMapService service)
    {
        _service = service;
    }

    /// <summary>Data file + WAL metadata and the segment table.</summary>
    [HttpGet("regions")]
    public ActionResult<StorageRegionsDto> GetRegions(Guid sessionId) => Invoke(_service.GetRegions);

    /// <summary>The top levels of the Hilbert aggregate pyramid.</summary>
    [HttpGet("overview")]
    public ActionResult<StorageOverviewDto> GetOverview(Guid sessionId) => Invoke(_service.GetOverview);

    /// <summary>Coarse per-page descriptors. In A1 the whole coarse map is returned regardless of node / lod.</summary>
    [HttpGet("region")]
    public ActionResult<StorageRegionDto> GetRegion(Guid sessionId, [FromQuery] int node = 0, [FromQuery] string lod = "leaf")
        => Invoke((engine, name) => _service.GetRegion(engine, name, node, lod));

    private ActionResult<T> Invoke<T>(Func<DatabaseEngine, string, T> action)
    {
        // RequireSession has already validated the token and stashed the session; the map needs an engine, so
        // only Open (file) sessions qualify — others degrade to a 409, mirroring ResourcesController.
        if (HttpContext.Items["Session"] is not OpenSession session)
        {
            return Conflict(new ProblemDetails
            {
                Title = "session_kind_mismatch",
                Detail = "The Database File Map is only available for Open (file) sessions.",
                Status = StatusCodes.Status409Conflict,
            });
        }
        return Ok(action(session.Engine.Engine, Path.GetFileName(session.FilePath)));
    }
}
