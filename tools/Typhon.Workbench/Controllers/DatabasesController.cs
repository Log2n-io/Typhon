using Microsoft.AspNetCore.Mvc;
using Typhon.Engine;
using Typhon.Workbench.Dtos.Databases;
using Typhon.Workbench.Middleware;

namespace Typhon.Workbench.Controllers;

/// <summary>
/// The machine-local database registry (#622, design D-7) — every database any Typhon process on this machine has opened.
/// </summary>
/// <remarks>
/// <para>
/// <b>Session-free on purpose.</b> This is how you <i>find</i> a database, so it has to answer before any session exists — the same reasoning that keeps
/// <c>/api/fs</c> outside the session routes. It is bootstrap-token gated like every other API surface.
/// </para>
/// <para>
/// <b>Discoverability only</b> (D-8). Nothing here is load-bearing for correlating a capture with a database: captures live inside the bundle (D-1) and carry
/// their own identity (D-2). A missing or stale registry costs the user a file-browse, never a wrong answer.
/// </para>
/// </remarks>
[ApiController]
[Route("api/databases")]
[Tags("Databases")]
[RequireBootstrapToken]
public sealed class DatabasesController : ControllerBase
{
    private readonly DatabaseRegistry _registry;

    public DatabasesController(DatabaseRegistry registry) => _registry = registry;

    /// <summary>Every known database, most-recently-opened first, plus whether the registry is switched on.</summary>
    [HttpGet]
    public ActionResult<KnownDatabaseListDto> List() => Ok(Snapshot());

    /// <summary>
    /// Forgets one database. Removes the registry row only — the database itself is untouched.
    /// </summary>
    /// <remarks>
    /// <b>Idempotent.</b> Forgetting something already forgotten achieved what the user asked for, so it answers 200 with the refreshed list rather than 404.
    /// The refreshed list comes back in the same response so the client cannot render a row it has just removed.
    /// </remarks>
    /// <param name="path">The bundle path to forget, as returned by <see cref="List"/>.</param>
    [HttpDelete]
    public ActionResult<KnownDatabaseListDto> Forget([FromQuery] string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Problem(statusCode: 400, title: "path_required", detail: "A 'path' query parameter naming the database bundle is required.");
        }

        _registry.Forget(path);
        return Ok(Snapshot());
    }

    /// <summary>
    /// Drops every entry whose bundle is no longer on disk. Explicit by design — listing validates and <i>offers</i> to prune, it never deletes on its own.
    /// </summary>
    [HttpPost("prune")]
    public ActionResult<KnownDatabaseListDto> Prune()
    {
        _registry.PruneMissing();
        return Ok(Snapshot());
    }

    private KnownDatabaseListDto Snapshot()
    {
        var enabled = _registry.IsEnabled(out var disabledReason);
        var entries = new List<KnownDatabaseDto>();
        foreach (var e in _registry.List())
        {
            entries.Add(new KnownDatabaseDto(e.Name, e.BundlePath, e.DatabaseId, e.FirstSeenUtc, e.LastOpenedUtc, e.LastOpenedBy, e.Exists));
        }

        // The entries are still listed when the registry is disabled: rows recorded before someone switched it off are real databases that are still there, and
        // hiding them would turn "stop recording" into "lose what was recorded".
        return new KnownDatabaseListDto(enabled, disabledReason, _registry.Directory, entries);
    }
}
