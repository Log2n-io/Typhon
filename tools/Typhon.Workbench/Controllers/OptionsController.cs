using Microsoft.AspNetCore.Mvc;
using Typhon.Workbench.Hosting;
using Typhon.Workbench.Middleware;

namespace Typhon.Workbench.Controllers;

/// <summary>
/// REST surface for <see cref="WorkbenchOptions"/>: GET (full document), PUT (full replace),
/// PATCH per-category. Bootstrap-token gated like the rest of the MVC API; unauthenticated
/// callers from a malicious webpage cannot mutate.
/// </summary>
[ApiController]
[Route("api/options")]
[RequireBootstrapToken]
public sealed class OptionsController : ControllerBase
{
    private readonly OptionsStore _store;

    public OptionsController(OptionsStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>Return the full options document.</summary>
    [HttpGet]
    public ActionResult<WorkbenchOptions> Get() => Ok(_store.Get());

    /// <summary>Replace the full options document.</summary>
    [HttpPut]
    public ActionResult<WorkbenchOptions> Put([FromBody] WorkbenchOptions body)
    {
        if (body == null) return BadRequest(new { error = "Body required" });
        _store.Replace(body);
        return Ok(_store.Get());
    }

    /// <summary>Patch the editor category.</summary>
    [HttpPatch("editor")]
    public ActionResult<WorkbenchOptions> PatchEditor([FromBody] EditorOptions body)
    {
        if (body == null) return BadRequest(new { error = "Body required" });
        _store.PatchEditor(body);
        return Ok(_store.Get());
    }

    /// <summary>Patch the profiler category.</summary>
    [HttpPatch("profiler")]
    public ActionResult<WorkbenchOptions> PatchProfiler([FromBody] Typhon.Workbench.Hosting.ProfilerOptions body)
    {
        if (body == null) return BadRequest(new { error = "Body required" });
        _store.PatchProfiler(body);
        return Ok(_store.Get());
    }

    /// <summary>
    /// Patch the schema category (registered schema-assembly directories, ADR-055 Phase 2). The submitted
    /// list is normalized server-side: only rooted, non-empty paths survive; each is canonicalized via
    /// <see cref="Path.GetFullPath(string)"/> and de-duplicated case-insensitively, preserving order. A
    /// directory need not exist at registration time — a missing one is simply skipped during resolution.
    /// </summary>
    [HttpPatch("schema")]
    public ActionResult<WorkbenchOptions> PatchSchema([FromBody] SchemaOptions body)
    {
        if (body == null) return BadRequest(new { error = "Body required" });

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dirs = new List<string>();
        foreach (var dir in body.Directories ?? [])
        {
            if (string.IsNullOrWhiteSpace(dir) || !Path.IsPathRooted(dir))
            {
                continue;
            }
            var full = Path.GetFullPath(dir);
            if (seen.Add(full))
            {
                dirs.Add(full);
            }
        }

        _store.PatchSchema(new SchemaOptions { Directories = [.. dirs] });
        return Ok(_store.Get());
    }
}
