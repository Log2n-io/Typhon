using Microsoft.AspNetCore.Mvc;
using Typhon.Engine;
using Typhon.Workbench.Dtos.Resources;
using Typhon.Workbench.Middleware;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Controllers;

[ApiController]
[Route("api/sessions/{sessionId:guid}/resources")]
[Tags("Resources")]
[RequireBootstrapToken]
[RequireSession]
public sealed class ResourcesController : WorkbenchControllerBase
{
    /// <summary>Eager depth for the root call; avoids a flurry of round-trips on initial render.</summary>
    private const int InitialDepth = 2;

    /// <summary>Sentinel for unbounded-depth traversal (used by Phase 5 ResourceIndex seeding).</summary>
    private const int UnboundedDepth = int.MaxValue;

    [HttpGet("root")]
    public ActionResult<ResourceGraphDto> GetRoot(Guid sessionId, [FromQuery] string depth = null)
    {
        var session = HttpContext.Items["Session"] as OpenSession;
        if (session == null)
        {
            return ConflictKindMismatch("Resource graph is only available for Open (file) sessions.");
        }

        int effectiveDepth;
        if (string.IsNullOrEmpty(depth))
        {
            effectiveDepth = InitialDepth;
        }
        else if (string.Equals(depth, "all", StringComparison.OrdinalIgnoreCase))
        {
            effectiveDepth = UnboundedDepth;
        }
        else if (int.TryParse(depth, out var parsed) && parsed >= 0)
        {
            effectiveDepth = parsed;
        }
        else
        {
            return BadRequest(new { error = "invalid_depth", detail = "depth must be a non-negative integer or 'all'." });
        }

        return Ok(new ResourceGraphDto(MapNode(session.Engine.Registry.Root, effectiveDepth)));
    }

    [HttpGet("{resourceId}/children")]
    public ActionResult<ResourceNodeDto[]> GetChildren(Guid sessionId, string resourceId)
    {
        var session = HttpContext.Items["Session"] as OpenSession;
        if (session == null)
        {
            return ConflictKindMismatch("Resource graph is only available for Open (file) sessions.");
        }

        var node = FindById(session.Engine.Registry.Root, resourceId);
        if (node == null) return NotFound();

        return Ok(node.Children.Select(c => MapNode(c, 1)).ToArray());
    }

    private static ResourceNodeDto MapNode(IResource r, int depth)
    {
        ResourceNodeDto[] children = null;
        if (depth > 0)
        {
            children = r.Children.Select(c => MapNode(c, depth - 1)).ToArray();
        }
        return new ResourceNodeDto(r.Id, r.Name, r.Type.ToString(), r.Count, children);
    }

    // TODO: O(N) tree walk — acceptable while the graph is small (tens of nodes). If the graph
    // grows beyond ~1000 nodes (ComponentTables, entity indexes, profiler sessions), move the
    // lookup into ResourceRegistry as an `id → IResource` index maintained by RegisterChild /
    // RemoveChild, and expose it via IResourceRegistry. Don't bake the index into the Workbench.
    private static IResource FindById(IResource root, string id)
    {
        if (root.Id == id) return root;
        foreach (var child in root.Children)
        {
            var hit = FindById(child, id);
            if (hit != null) return hit;
        }
        return null;
    }
}
