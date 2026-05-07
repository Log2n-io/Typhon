using Typhon.Workbench.Sessions;
using WbSession = Typhon.Workbench.Sessions.ISession;

namespace Typhon.Workbench.Streams;

public static class HeartbeatStream
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    /// <summary>SSE event type. Clients listen via <c>addEventListener('heartbeat', ...)</c>.</summary>
    public const string EventType = "heartbeat";

    public static async Task HandleAsync(
        Guid sessionId,
        HttpContext ctx,
        SessionManager sessions,
        CancellationToken ct)
    {
        // EventSource cannot send custom headers — browsers only allow cookies/URL auth on SSE. Since
        // Phase 3's session token *is* the sessionId (same Guid), the URL path segment is sufficient
        // identification. Accept either the X-Session-Token header (curl / server-to-server) or the
        // URL sessionId (browser EventSource).
        WbSession session = null;
        if (ctx.Request.Headers.TryGetValue("X-Session-Token", out var rawToken)
            && Guid.TryParse(rawToken, out var token)
            && sessions.TryGet(token, out var s))
        {
            session = s;
        }
        else if (sessions.TryGet(sessionId, out var s2))
        {
            session = s2;
        }

        if (session == null)
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        if (session is not OpenSession)
        {
            // Attach/Trace sessions have no heartbeat semantics yet — refuse rather than silently
            // serve zeroed payloads that misrepresent the session state to the UI.
            ctx.Response.StatusCode = StatusCodes.Status409Conflict;
            return;
        }

        await SseExtensions.WriteSseHeadersAsync(ctx, ct);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var payload = new
                {
                    timestamp = DateTimeOffset.UtcNow,
                    // revision: still a placeholder — DatabaseEngine doesn't expose a global monotonic revision yet.
                    // memoryMb: pragmatic stand-in via GC.
                    // tickRate / activeTransactionCount / lastTickDurationMs: null until Workbench hosts a TyphonRuntime (post-bootstrap).
                    revision = 0,
                    memoryMb = GC.GetTotalMemory(forceFullCollection: false) / 1_000_000,
                    tickRate = (int?)null,
                    activeTransactionCount = (int?)null,
                    lastTickDurationMs = (float?)null,
                };
                await SseExtensions.WriteEventAsync(ctx, EventType, payload, ct);
                await Task.Delay(Interval, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected — normal exit.
        }
    }
}
