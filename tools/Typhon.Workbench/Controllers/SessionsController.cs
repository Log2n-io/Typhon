using Microsoft.AspNetCore.Mvc;
using Typhon.Workbench.Dtos.Sessions;
using Typhon.Workbench.Hosting;
using Typhon.Workbench.Middleware;
using Typhon.Workbench.Schema;
using Typhon.Workbench.Sessions;
using WbSession = Typhon.Workbench.Sessions.ISession;

namespace Typhon.Workbench.Controllers;

[ApiController]
[Route("api/sessions")]
[Tags("Sessions")]
[RequireBootstrapToken]
public sealed partial class SessionsController : ControllerBase
{
    private readonly SessionManager _sessions;
    private readonly DemoDataProvider _demoData;
    private readonly OptionsStore _options;
    private readonly DatabasePauseCoordinator _pause;
    private readonly ILogger<SessionsController> _logger;

    public SessionsController(
        SessionManager sessions,
        DemoDataProvider demoData,
        OptionsStore options,
        DatabasePauseCoordinator pause,
        ILogger<SessionsController> logger)
    {
        _sessions = sessions;
        _demoData = demoData;
        _options = options;
        _pause = pause;
        _logger = logger;
    }

    [HttpPost("file")]
    public async Task<ActionResult<SessionDto>> CreateFileSession([FromBody] CreateFileSessionRequest request, CancellationToken ct)
    {
        // Resolve the file path. The bundled "demo" stem still goes through DemoDataProvider for
        // Phase 3 compat; any other path is used verbatim (Phase 4's real file picker).
        var resolvedFile = ResolveFilePath(request.FilePath);

        // Schema resolution + provenance are decided by EngineLifecycle during open (ADR-055): an explicit user list
        // wins; otherwise it resolves the persisted manifest across { bundled, legacy-adjacent }. The controller no
        // longer guesses the status up-front — it reads the actually-resolved paths + status back off the engine.
        var requestedSchemaDllPaths = request.SchemaDllPaths is { Length: > 0 } ? request.SchemaDllPaths : [];

        // Phase 3 compat: single-session at a time per file path.
        _sessions.RemoveWhere(s => s is OpenSession os && string.Equals(os.FilePath, resolvedFile, StringComparison.OrdinalIgnoreCase));

        // ADR-055 Phase 2: the persisted registered schema directories feed manifest resolution at priority 2 (above
        // the Workbench's own bundled binaries). Ignored when an explicit user-specified list is supplied above.
        var registeredSchemaDirs = _options.Get().Schema?.Directories ?? [];

        EngineLifecycle engine;
        try
        {
            engine = await EngineLifecycle.OpenAsync(resolvedFile, requestedSchemaDllPaths, registeredSchemaDirs, ct);
        }
        catch (Exception ex) when (ex is DatabaseLockedException || (ex is WorkbenchException { ErrorCode: "file_locked" }))
        {
            // Another process holds the database (#621). Open PAUSED rather than refuse: the session still knows its bundle, so its captures list and attach
            // with no engine involved, and the coordinator promotes it to a real open the moment the holder exits. That is the cold-start case — reaching for
            // the profiler *while* the application is running — which a hard failure leaves with no session at all, and therefore nothing to show.
            //
            // Only a LOCKED database earns this. Corrupt, missing and schema-incompatible databases still fail below, because waiting for a lock that was
            // never the problem would convert a clear error into a session that silently never resumes.
            // Two ways a database says "someone else has me", and they must not behave differently:
            //   • DatabaseLockedException — the advisory lock was found and names its holder.
            //   • file_locked — the OS refused the handle (ERROR_SHARING_VIOLATION). This happens when the lock file is
            //     absent or stale but a process still holds the mapping: a holder that died before writing its lock, or
            //     simply the instant before another opener finishes writing one. It is ALWAYS another process.
            // Only the first can name the holder from the exception; for the second we read db.lock if it happens to be
            // there, and otherwise say plainly that we do not know who. Failing hard on the second while pausing on the
            // first would make the same situation behave differently depending on which race the caller lost.
            var holder = ex is DatabaseLockedException locked
                ? new DatabaseHolder(locked.OwnerPid, locked.OwnerMachine, locked.StartedAt)
                : DatabaseLockFile.TryReadHolder(resolvedFile, out var pid, out var machine, out var startedAt)
                    ? new DatabaseHolder(pid, machine, startedAt)
                    : null;
            var pausedSession = new OpenSession(Guid.NewGuid(), resolvedFile, holder, requestedSchemaDllPaths);
            _sessions.Create(pausedSession);
            _pause.TrackPausedSession(pausedSession, requestedSchemaDllPaths);
            LogSessionCreatedPaused(pausedSession.Id, holder?.Describe() ?? "an unidentified process");
            return CreatedAtAction(nameof(GetSession), new { id = pausedSession.Id }, ToDto(pausedSession));
        }

        var sessionState = engine.State switch
        {
            SchemaCompatibility.State.Ready => SessionState.Ready,
            SchemaCompatibility.State.MigrationRequired => SessionState.MigrationRequired,
            SchemaCompatibility.State.Incompatible => SessionState.Incompatible,
            _ => SessionState.Ready,
        };

        var session = new OpenSession(
            Guid.NewGuid(),
            resolvedFile,
            engine,
            sessionState,
            engine.SchemaStatus,
            engine.ResolvedSchemaPaths,
            engine.LoadedComponentTypes,
            engine.Diagnostics);

        _sessions.Create(session);
        // #621 — a live session is watched too, so the yieldable advertisement its lock file makes is actually honoured
        // when an application asks for the database.
        _pause.TrackLiveSession(session, requestedSchemaDllPaths);
        LogSessionCreated(session.Id, "file");
        return CreatedAtAction(nameof(GetSession), new { id = session.Id }, ToDto(session));
    }

    [HttpPost("attach")]
    public async Task<ActionResult<SessionDto>> CreateAttachSession([FromBody] CreateAttachSessionRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.EndpointAddress))
        {
            throw new WorkbenchException(400, "invalid_endpoint", "endpointAddress is required.");
        }

        // Single-session-per-endpoint invariant — matches the file/trace patterns. Reopening the same endpoint
        // recycles the prior socket cleanly rather than racing two read loops.
        _sessions.RemoveWhere(s => s is AttachSession a
            && string.Equals(a.EndpointAddress, request.EndpointAddress, StringComparison.OrdinalIgnoreCase));

        // AttachSessionRuntime.StartAsync does 3 × 2 s upfront TCP retry; throws WorkbenchException(503) on total failure.
        // Session id is generated up front so the live cache temp file path matches the public sessionId.
        var sessionId = Guid.NewGuid();
        var runtime = await AttachSessionRuntime.StartAsync(sessionId, request.EndpointAddress, _logger, ct);

        var session = new AttachSession(sessionId, request.EndpointAddress, runtime);
        _sessions.Create(session);
        LogSessionCreated(session.Id, "attach");
        return CreatedAtAction(nameof(GetSession), new { id = session.Id }, ToDto(session));
    }

    /// <summary>
    /// Lists every active session — bootstrap-token-only so the API explorer / debug tools can
    /// discover which session GUIDs to plug into session-scoped routes. The SPA keeps its session
    /// in client-side state and never advertises it server-side, so this endpoint exists primarily
    /// for human troubleshooting.
    /// </summary>
    [HttpGet]
    public ActionResult<SessionDto[]> ListSessions()
    {
        var snap = _sessions.Snapshot();
        var dtos = new SessionDto[snap.Count];
        for (var i = 0; i < snap.Count; i++) dtos[i] = ToDto(snap[i]);
        return Ok(dtos);
    }

    [HttpGet("{id:guid}")]
    [RequireSession]
    public ActionResult<SessionDto> GetSession(Guid id)
    {
        var session = (WbSession)HttpContext.Items["Session"]!;
        return Ok(ToDto(session));
    }

    [HttpGet("{id:guid}/state")]
    [RequireSession]
    public ActionResult<SessionStateDto> GetState(Guid id)
    {
        var session = (WbSession)HttpContext.Items["Session"]!;
        return Ok(ToStateDto(session));
    }

    [HttpDelete("{id:guid}")]
    [RequireSession]
    public IActionResult DeleteSession(Guid id)
    {
        _sessions.Remove(id);
        return NoContent();
    }

    private string ResolveFilePath(string requestPath)
    {
        if (string.IsNullOrWhiteSpace(requestPath))
        {
            throw new WorkbenchException(400, "invalid_path", "filePath is required.");
        }
        // Bundled demo alias: "demo.typhon" → DemoDataProvider path. Any other path is used as-is.
        var stem = Path.GetFileNameWithoutExtension(requestPath);
        if (string.Equals(stem, "demo", StringComparison.OrdinalIgnoreCase)
            && !Path.IsPathRooted(requestPath))
        {
            return _demoData.Resolve(requestPath);
        }
        return Path.GetFullPath(requestPath);
    }

    /// <summary>
    /// Projects a session to its wire shape, then stamps the capability set on whatever the per-kind branches produced (#617).
    /// </summary>
    /// <remarks>
    /// Applied once here rather than repeated in each branch: capabilities are read off <see cref="ISession"/> identically for every kind, and a branch that
    /// forgot them would surface as a session whose panels silently never appear — a bug with no error attached to it.
    /// </remarks>
    private static SessionDto ToDto(WbSession s)
    {
        var dto = ToDtoCore(s);
        return dto with
        {
            Capabilities = [.. s.Capabilities],
            ActiveProfileId = s.ActiveProfileId,
        };
    }

    private static SessionDto ToDtoCore(WbSession s)
    {
        if (s is OpenSession os)
        {
            var diags = os.SchemaDiagnostics?
                .Select(d => new SessionDiagnosticDto(d.ComponentName, d.Kind, d.Detail))
                .ToArray();
            var schemaCompatibility = os.State switch
            {
                SessionState.Ready => "Compatible",
                SessionState.MigrationRequired => "MigrationRequired",
                SessionState.Incompatible => "Incompatible",
                _ => "Compatible",
            };
            // #621 — a paused session is Lifecycle "Paused" with IsPaused set and Reason naming the holder. The client must be able to tell "released, will
            // come back" apart from both "still loading" and "failed": the first is a normal state with working profiler panels, and rendering it as an error
            // would teach the user to close and reopen, which is precisely what pausing exists to avoid.
            if (os.IsPaused)
            {
                return new SessionDto(
                    os.Id,
                    os.Kind.ToString(),
                    os.State.ToString(),
                    os.FilePath,
                    os.SchemaDllPaths,
                    os.SchemaStatus,
                    os.LoadedComponentTypes,
                    diags,
                    Lifecycle: "Paused",
                    IsPaused: true,
                    SchemaCompatibility: schemaCompatibility,
                    Reason: os.PausedBy is { } holder
                        ? $"Database released to {holder.Describe()}. Captures remain available; the Workbench reopens it automatically when that process exits."
                        : "Database released. The Workbench reopens it automatically when it becomes available.");
            }

            return new SessionDto(
                os.Id,
                os.Kind.ToString(),
                os.State.ToString(),
                os.FilePath,
                os.SchemaDllPaths,
                os.SchemaStatus,
                os.LoadedComponentTypes,
                diags,
                Lifecycle: "Ready",
                SchemaCompatibility: schemaCompatibility);
        }
        if (s is AttachSession attach)
        {
            var isReady = attach.Runtime.Metadata != null;
            return new SessionDto(
                attach.Id,
                attach.Kind.ToString(),
                attach.State.ToString(),
                attach.FilePath,
                Lifecycle: isReady ? "Ready" : "Loading",
                IsStreaming: isReady);
        }
        return new SessionDto(s.Id, s.Kind.ToString(), s.State.ToString(), s.FilePath, Lifecycle: "Ready");
    }

    private static SessionStateDto ToStateDto(WbSession s)
    {
        if (s is OpenSession os)
        {
            var schemaCompatibility = os.State switch
            {
                SessionState.Ready => "Compatible",
                SessionState.MigrationRequired => "MigrationRequired",
                SessionState.Incompatible => "Incompatible",
                _ => "Compatible",
            };
            // #621 — kept in step with ToDtoCore. Two projections of the same session state is one too many, but the DTOs differ and the client reads both;
            // what must never differ is whether they agree the session is paused.
            return new SessionStateDto(
                os.Kind.ToString(),
                Lifecycle: os.IsPaused ? "Paused" : "Ready",
                IsStreaming: false,
                IsPaused: os.IsPaused,
                IsReattaching: false,
                SchemaCompatibility: schemaCompatibility,
                Reason: os.IsPaused && os.PausedBy is { } holder ? $"Database released to {holder.Describe()}." : null);
        }
        if (s is AttachSession attach)
        {
            var isReady = attach.Runtime.Metadata != null;
            return new SessionStateDto(
                attach.Kind.ToString(),
                Lifecycle: isReady ? "Ready" : "Loading",
                IsStreaming: isReady,
                IsPaused: false,
                IsReattaching: false,
                SchemaCompatibility: null,
                Reason: null);
        }
        return new SessionStateDto(s.Kind.ToString(), Lifecycle: "Ready", IsStreaming: false, IsPaused: false,
            IsReattaching: false, SchemaCompatibility: null, Reason: null);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {SessionId} created via {Mode}")]
    private partial void LogSessionCreated(Guid sessionId, string mode);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {SessionId} created PAUSED — database held by {Holder}; watching for release")]
    private partial void LogSessionCreatedPaused(Guid sessionId, string holder);

}
