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

        // Reject a file that is not a capture BEFORE starting a runtime for it (#621). This guard used to live on the
        // standalone-trace route; with that route gone, attaching is the only way a user hands the Workbench a file, so
        // it is where the "pasted the sidecar cache instead of the capture" mistake now surfaces. Without it the session
        // gets a runtime whose background build faults, and /metadata answers 500 in a loop instead of saying why.
        ValidateCaptureMagic(path);

        // Co-location is not provenance (D-1): the bundle may have been copied or restored since the capture was written. #614 recorded the database id so
        // this is a check rather than an assumption.
        // Empty while paused (#621) — BelongsToDatabase already treats an unknown id as "cannot answer" and allows the attach, which is the same allowance
        // pre-#614 captures get. Attaching a capture is a file operation; it must not require the database the capture describes to be open.
        var databaseId = open.Engine?.Engine?.DatabaseId ?? Guid.Empty;
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
        $"{action} requires a session with an open database. An attached engine's database cannot be opened while "
        + "it is running.";

    [LoggerMessage(EventId = 6170, Level = LogLevel.Information, Message = "Session {SessionId}: attached profile '{FileName}' as {ProfileId}")]
    static partial void LogProfileAttached(ILogger logger, Guid sessionId, string fileName, Guid profileId);

    [LoggerMessage(EventId = 6171, Level = LogLevel.Information, Message = "Session {SessionId}: detached profile {ProfileId}")]
    static partial void LogProfileDetached(ILogger logger, Guid sessionId, Guid profileId);

    /// <summary>
    /// Validates the file at <paramref name="path"/> as either a <c>.typhon-trace</c> source (magic "TYTR") OR a
    /// <c>.typhon-replay</c> self-contained cache (magic "TPCH"). Throws 400 with a human-readable reason on any other content. The
    /// extension determines the expected magic — opening a <c>.typhon-trace-cache</c> file (TPCH magic but conventional sidecar role)
    /// from the trace open dialog is rejected with a hint to open the parent <c>.typhon-trace</c> instead.
    /// </summary>
    private static void ValidateCaptureMagic(string path)
    {
        // Read magic (4 bytes) + on-disk format version (next 2 bytes) in one peek — the version gate below catches an
        // old/newer .typhon-trace up-front, so an unsupported file fails here with a clear 400 instead of creating a
        // session whose background build faults at TraceFileReader.ReadHeader and surfaces a 500 on /metadata.
        Span<byte> head = stackalloc byte[6];
        int read;
        try
        {
            using var fs = System.IO.File.OpenRead(path);
            read = fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        }
        catch (IOException ex)
        {
            throw new WorkbenchException(400, "invalid_trace_file", $"Cannot read trace file: {ex.Message}");
        }
        if (read < 4)
        {
            throw new WorkbenchException(400, "invalid_trace_file", $"File is too small to be a valid trace: {path}");
        }

        var magic = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(head);
        var extension = Path.GetExtension(path);
        var isReplay = string.Equals(extension, ".typhon-replay", StringComparison.OrdinalIgnoreCase);

        if (isReplay)
        {
            if (magic == Typhon.Profiler.CacheHeader.MagicValue)
            {
                return;
            }
            var asAscii = System.Text.Encoding.ASCII.GetString(head[..4]);
            throw new WorkbenchException(400, "invalid_replay_file",
                $"File magic is '{asAscii}' (0x{magic:X8}); expected 'TPCH' for a .typhon-replay file.");
        }

        // Default: source .typhon-trace file with TYTR magic.
        if (magic == Typhon.Profiler.TraceFileHeader.MagicValue)
        {
            // Magic is valid — also gate the on-disk format version so an old/newer trace fails with an immediate,
            // actionable 400 (mirrors TraceFileReader.ReadHeader's range check, which would otherwise fault the build).
            var version = read >= 6 ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(head[4..6]) : (ushort)0;
            if (version < Typhon.Profiler.TraceFileReader.MinSupportedVersion || version > Typhon.Profiler.TraceFileHeader.CurrentVersion)
            {
                throw new WorkbenchException(400, "unsupported_trace_version",
                    $"Unsupported trace file version: {version}. This build reads versions "
                    + $"{Typhon.Profiler.TraceFileReader.MinSupportedVersion}..{Typhon.Profiler.TraceFileHeader.CurrentVersion}. Re-record against a current build.");
            }
            return;
        }

        // Common-mistake hint: a TPCH file with .typhon-trace-cache extension is the auto-built sidecar; the user should open the parent.
        var ascii = System.Text.Encoding.ASCII.GetString(head[..4]);
        var hint = magic == Typhon.Profiler.CacheHeader.MagicValue
            ? "This looks like a .typhon-trace-cache sidecar. Open the matching source .typhon-trace file instead, or use .typhon-replay extension if this is a saved replay file."
            : $"File magic is '{ascii}' (0x{magic:X8}); expected 'TYTR' for a .typhon-trace file.";
        throw new WorkbenchException(400, "invalid_trace_file", hint);
    }

}
