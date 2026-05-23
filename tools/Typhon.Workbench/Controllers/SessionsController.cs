using Microsoft.AspNetCore.Mvc;
using Typhon.Workbench.Dtos.Sessions;
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
    private readonly ILogger<SessionsController> _logger;

    public SessionsController(SessionManager sessions, DemoDataProvider demoData, ILogger<SessionsController> logger)
    {
        _sessions = sessions;
        _demoData = demoData;
        _logger = logger;
    }

    [HttpPost("file")]
    public async Task<ActionResult<SessionDto>> CreateFileSession([FromBody] CreateFileSessionRequest request, CancellationToken ct)
    {
        // Resolve the file path. The bundled "demo" stem still goes through DemoDataProvider for
        // Phase 3 compat; any other path is used verbatim (Phase 4's real file picker).
        var resolvedFile = ResolveFilePath(request.FilePath);

        // Determine schema DLL list: an explicit (user-specified) list wins; otherwise EngineLifecycle resolves the assemblies from the database's persisted
        // manifest (engine.GetRequiredAssemblies) by locating them next to the file — no filename convention.
        string[] schemaDllPaths;
        string schemaStatus;
        if (request.SchemaDllPaths is { Length: > 0 })
        {
            schemaDllPaths = request.SchemaDllPaths;
            schemaStatus = "user-specified";
        }
        else
        {
            schemaDllPaths = [];
            schemaStatus = "manifest";
        }

        // Phase 3 compat: single-session at a time per file path.
        _sessions.RemoveWhere(s => s is OpenSession os && string.Equals(os.FilePath, resolvedFile, StringComparison.OrdinalIgnoreCase));

        var engine = await EngineLifecycle.OpenAsync(resolvedFile, schemaDllPaths, ct);

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
            schemaStatus,
            schemaDllPaths,
            engine.LoadedComponentTypes,
            engine.Diagnostics);

        _sessions.Create(session);
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

    [HttpPost("trace")]
    public ActionResult<SessionDto> CreateTraceSession([FromBody] CreateTraceSessionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.FilePath))
        {
            throw new WorkbenchException(400, "invalid_path", "filePath is required.");
        }
        var resolvedFile = Path.GetFullPath(request.FilePath);
        if (!System.IO.File.Exists(resolvedFile))
        {
            throw new WorkbenchException(404, "trace_file_not_found", $"Trace file not found: {resolvedFile}");
        }

        // Validate file magic up-front — rejects unrelated files immediately with 400 rather than creating a session that'll fault its
        // background build and flood /metadata with 500s. Both .typhon-trace ("TYTR") and .typhon-replay ("TPCH" + IsSelfContained) are
        // accepted; TraceSessionRuntime detects which by extension and takes the right load path.
        ValidateTraceOrReplayMagic(resolvedFile);

        // Single-session-per-file invariant matches the Open-mode pattern (above). Reopens are cheap because
        // the sidecar cache is fingerprint-cached on disk (or, for replay files, the file IS the cache).
        _sessions.RemoveWhere(s => s is TraceSession ts && string.Equals(ts.FilePath, resolvedFile, StringComparison.OrdinalIgnoreCase));

        var runtime = TraceSessionRuntime.Start(resolvedFile, _logger);
        var session = new TraceSession(Guid.NewGuid(), resolvedFile, runtime);
        _sessions.Create(session);
        LogSessionCreated(session.Id, "trace");
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

    private static SessionDto ToDto(WbSession s)
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
        if (s is TraceSession trace)
        {
            var lifecycle = !trace.Runtime.IsBuildComplete ? "Loading"
                : trace.Runtime.Metadata != null ? "Ready"
                : "Closed";
            return new SessionDto(
                trace.Id,
                trace.Kind.ToString(),
                trace.State.ToString(),
                trace.FilePath,
                Lifecycle: lifecycle,
                Reason: lifecycle == "Closed" ? "build-failed" : null);
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
            return new SessionStateDto(
                os.Kind.ToString(),
                Lifecycle: "Ready",
                IsStreaming: false,
                IsPaused: false,
                IsReattaching: false,
                SchemaCompatibility: schemaCompatibility,
                Reason: null);
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
        if (s is TraceSession trace)
        {
            var lifecycle = !trace.Runtime.IsBuildComplete ? "Loading"
                : trace.Runtime.Metadata != null ? "Ready"
                : "Closed";
            return new SessionStateDto(
                trace.Kind.ToString(),
                Lifecycle: lifecycle,
                IsStreaming: false,
                IsPaused: false,
                IsReattaching: false,
                SchemaCompatibility: null,
                Reason: lifecycle == "Closed" ? "build-failed" : null);
        }
        return new SessionStateDto(s.Kind.ToString(), Lifecycle: "Ready", IsStreaming: false, IsPaused: false,
            IsReattaching: false, SchemaCompatibility: null, Reason: null);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {SessionId} created via {Mode}")]
    private partial void LogSessionCreated(Guid sessionId, string mode);

    /// <summary>
    /// Validates the file at <paramref name="path"/> as either a <c>.typhon-trace</c> source (magic "TYTR") OR a
    /// <c>.typhon-replay</c> self-contained cache (magic "TPCH"). Throws 400 with a human-readable reason on any other content. The
    /// extension determines the expected magic — opening a <c>.typhon-trace-cache</c> file (TPCH magic but conventional sidecar role)
    /// from the trace open dialog is rejected with a hint to open the parent <c>.typhon-trace</c> instead.
    /// </summary>
    private static void ValidateTraceOrReplayMagic(string path)
    {
        Span<byte> magicBytes = stackalloc byte[4];
        try
        {
            using var fs = System.IO.File.OpenRead(path);
            if (fs.Length < 4 || fs.Read(magicBytes) != 4)
            {
                throw new WorkbenchException(400, "invalid_trace_file", $"File is too small to be a valid trace: {path}");
            }
        }
        catch (IOException ex)
        {
            throw new WorkbenchException(400, "invalid_trace_file", $"Cannot read trace file: {ex.Message}");
        }

        var magic = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(magicBytes);
        var extension = Path.GetExtension(path);
        var isReplay = string.Equals(extension, ".typhon-replay", StringComparison.OrdinalIgnoreCase);

        if (isReplay)
        {
            if (magic == Typhon.Profiler.CacheHeader.MagicValue)
            {
                return;
            }
            var asAscii = System.Text.Encoding.ASCII.GetString(magicBytes);
            throw new WorkbenchException(400, "invalid_replay_file",
                $"File magic is '{asAscii}' (0x{magic:X8}); expected 'TPCH' for a .typhon-replay file.");
        }

        // Default: source .typhon-trace file with TYTR magic.
        if (magic == Typhon.Profiler.TraceFileHeader.MagicValue)
        {
            return;
        }

        // Common-mistake hint: a TPCH file with .typhon-trace-cache extension is the auto-built sidecar; the user should open the parent.
        var ascii = System.Text.Encoding.ASCII.GetString(magicBytes);
        var hint = magic == Typhon.Profiler.CacheHeader.MagicValue
            ? "This looks like a .typhon-trace-cache sidecar. Open the matching source .typhon-trace file instead, or use .typhon-replay extension if this is a saved replay file."
            : $"File magic is '{ascii}' (0x{magic:X8}); expected 'TYTR' for a .typhon-trace file.";
        throw new WorkbenchException(400, "invalid_trace_file", hint);
    }
}
