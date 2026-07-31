using Microsoft.AspNetCore.Mvc;
using Typhon.Engine;
using Typhon.Workbench.Dtos.Profiler;
using Typhon.Workbench.Middleware;
using Typhon.Workbench.Services;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Controllers;

/// <summary>
/// Profiling captures as a <b>sub-resource of an open database</b> (#617, design D-10).
/// </summary>
/// <remarks>
/// <para>
/// A capture attaches <i>to</i> the session rather than being a peer session: the database is the persistent context and the profile a transient lens. That
/// keeps one session id, one token and one <c>customFetch</c> on the client, so the single-session choke point is never reached.
/// </para>
/// <para>
/// The existing <c>/api/sessions/{id}/profiler/*</c> routes are untouched and keep serving whatever profile is active — only their guard changed, from asking
/// what kind of session this is to asking whether it has a profiler runtime.
/// </para>
/// </remarks>
[ApiController]
[Route("api/sessions/{sessionId:guid}")]
[Tags("Profiles")]
[RequireBootstrapToken]
[RequireSession]
public sealed partial class ProfilesController : WorkbenchControllerBase
{
    private readonly ILogger<ProfilesController> _logger;

    public ProfilesController(ILogger<ProfilesController> logger) => _logger = logger;

    /// <summary>
    /// Lists every capture in this database's <c>profilings/</c> directory, attached or not, newest first — rendered from trace headers alone.
    /// </summary>
    [HttpGet("profiles")]
    public ActionResult<ProfileListDto> ListProfiles(Guid sessionId)
    {
        if (HttpContext.Items["Session"] is not OpenSession open)
        {
            return ConflictKindMismatch(NoDatabaseDetail("Listing profiles"));
        }
        return Ok(ProfileCatalog.List(open));
    }

    /// <summary>
    /// Attaches a capture from this database's <c>profilings/</c> directory and makes it the active profile — after which the session's profiler endpoints
    /// serve it and the client's profiler panels become available.
    /// </summary>
    [HttpPost("profile")]
    public ActionResult<ProfileDto> AttachProfile(Guid sessionId, [FromBody] AttachProfileRequest request)
    {
        if (HttpContext.Items["Session"] is not OpenSession open)
        {
            return ConflictKindMismatch(NoDatabaseDetail("Attaching a profile"));
        }

        var fileName = request?.FileName;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new WorkbenchException(400, "invalid_profile", "fileName is required.");
        }

        // A bare file name, resolved against the session's own directory. Accepting a path would let a caller attach any file on disk and have it presented as
        // this database's profile — the co-location that makes correlation structural only means anything if it cannot be faked.
        if (fileName.Contains(Path.DirectorySeparatorChar) || fileName.Contains(Path.AltDirectorySeparatorChar) || Path.IsPathRooted(fileName))
        {
            throw new WorkbenchException(400, "invalid_profile", "fileName must be a file name in this database's profilings/ directory, not a path.");
        }

        var profilings = TraceLocation.ProfilingsDirectoryOf(open.FilePath);
        var path = Path.Combine(profilings, fileName);
        if (!System.IO.File.Exists(path))
        {
            throw new WorkbenchException(404, "profile_not_found", $"No capture named '{fileName}' in {profilings}.");
        }

        // Co-location is not provenance (D-1): the bundle may have been copied or restored since the capture was written. #614 recorded the database id so
        // this is a check rather than an assumption.
        var databaseId = open.Engine.Engine?.DatabaseId ?? Guid.Empty;
        if (!ProfileCatalog.BelongsToDatabase(path, databaseId, out var reason))
        {
            throw new WorkbenchException(409, "profile_database_mismatch", reason);
        }

        var runtime = TraceSessionRuntime.Start(path, _logger);
        var profileId = open.AttachProfile(runtime);
        LogProfileAttached(_logger, sessionId, fileName, profileId);

        var row = ProfileCatalog.List(open).Profiles.FirstOrDefault(p => p.ProfileId == profileId);
        return Ok(row);
    }

    /// <summary>Detaches one profile and disposes its runtime. Idempotent enough to be safe on a double-click: an unknown id is a 404, not a fault.</summary>
    [HttpDelete("profile/{profileId:guid}")]
    public IActionResult DetachProfile(Guid sessionId, Guid profileId)
    {
        if (HttpContext.Items["Session"] is not OpenSession open)
        {
            return ConflictKindMismatch(NoDatabaseDetail("Detaching a profile"));
        }

        if (!open.DetachProfile(profileId))
        {
            throw new WorkbenchException(404, "profile_not_attached", $"No profile {profileId} is attached to this session.");
        }

        LogProfileDetached(_logger, sessionId, profileId);
        return NoContent();
    }

    /// <summary>
    /// Explains the 409 for a session with no database behind it. Trace and Attach sessions already <i>are</i> a capture, so attaching one to them is not a
    /// missing feature but a category error — the message says which, rather than leaving the caller to guess.
    /// </summary>
    private static string NoDatabaseDetail(string action) =>
        $"{action} requires a session with an open database. Trace sessions already are a capture, and an attached engine's database cannot be opened while "
        + "it is running.";

    [LoggerMessage(EventId = 6170, Level = LogLevel.Information, Message = "Session {SessionId}: attached profile '{FileName}' as {ProfileId}")]
    static partial void LogProfileAttached(ILogger logger, Guid sessionId, string fileName, Guid profileId);

    [LoggerMessage(EventId = 6171, Level = LogLevel.Information, Message = "Session {SessionId}: detached profile {ProfileId}")]
    static partial void LogProfileDetached(ILogger logger, Guid sessionId, Guid profileId);
}
