using Microsoft.AspNetCore.Mvc;

namespace Typhon.Workbench.Controllers;

/// <summary>
/// Shared base for the session-scoped Workbench API controllers. Centralizes the two response envelopes that nearly
/// every session endpoint repeated inline: the <c>session_kind_mismatch</c> 409 (the endpoint needs a session kind this
/// session isn't) and the build-in-flight <c>202 Accepted</c> + <c>Retry-After: 1</c> (metadata/cache still building —
/// the SPA's customFetch retries quietly). Defining them once keeps the wire contract in a single place.
/// </summary>
public abstract class WorkbenchControllerBase : ControllerBase
{
    /// <summary>
    /// A 409 <see cref="ProblemDetails"/> titled <c>session_kind_mismatch</c> — the endpoint requires a session kind the
    /// current session does not provide. <paramref name="detail"/> names the endpoint-specific requirement.
    /// </summary>
    protected ConflictObjectResult ConflictKindMismatch(string detail)
        => Conflict(new ProblemDetails
        {
            Title = "session_kind_mismatch",
            Detail = detail,
            Status = StatusCodes.Status409Conflict,
        });

    /// <summary>
    /// <c>202 Accepted</c> with <c>Retry-After: 1</c> — the session's metadata/cache is still building, so the caller
    /// should retry shortly. The SPA hands the hooks a "not ready" envelope and refetches on its interval.
    /// </summary>
    protected StatusCodeResult NotReady()
    {
        Response.Headers["Retry-After"] = "1";
        return StatusCode(StatusCodes.Status202Accepted);
    }

    /// <summary>
    /// A typed <c>trace_build_failed</c> ProblemDetails for the build-dependent profiler endpoints — the background
    /// cache build completed but faulted (no metadata). Surfaces the runtime's real <paramref name="buildError"/> so
    /// the client shows the actual reason (e.g. an unsupported trace version) instead of "see server logs". The common
    /// malformed-file cases are rejected up-front at session creation with a 400; this is the unexpected-fault backstop.
    /// </summary>
    protected ObjectResult TraceBuildFailed(string buildError)
        => Problem(
            title: "trace_build_failed",
            detail: string.IsNullOrEmpty(buildError) ? "Trace cache build failed. See server logs for details." : buildError,
            statusCode: StatusCodes.Status500InternalServerError);
}
