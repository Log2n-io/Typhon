using System.Threading.Channels;
using Typhon.Workbench.Dtos.Profiler;
using Typhon.Workbench.Services;
using Typhon.Workbench.Sessions;
using WbSession = Typhon.Workbench.Sessions.ISession;

namespace Typhon.Workbench.Streams;

/// <summary>
/// Unified Data API SSE endpoint at <c>GET /api/sessions/{id}/stream</c> (#308 Phase B). Replaces
/// the disparate <c>profiler/stream</c> + ad-hoc per-panel SSE consumers with a single multiplexed
/// channel of typed events:
/// <list type="bullet">
///   <item><b>Bootstrap (always sent, bypasses subscription filter):</b>
///     <c>stream-id</c> → <c>metadata</c> → <c>session-state</c>.</item>
///   <item><b>Live deltas (filtered through <see cref="StreamSubscriptionRegistry"/>):</b>
///     <c>tick</c>, <c>log</c>, <c>topology-changed</c>, <c>error</c>.</item>
///   <item><b>Heartbeat &amp; lifecycle (always sent):</b>
///     <c>heartbeat</c> every 5 s, <c>shutdown</c> when the session ends.</item>
/// </list>
/// Tick events use a 1-slot DropOldest channel so a slow consumer drops intermediate ticks but
/// always sees the most recent one — matching the coalescing semantics of <c>OptionsChangedStream</c>
/// but applied per-event-type so non-coalescing event classes (logs, errors) are not affected.
/// </summary>
/// <remarks>
/// <para><b>Auth.</b> EventSource cannot send custom headers, so the URL sessionId is the
/// auth boundary (per ADR-048; the bootstrap-token gate is enforced at the proxy layer for
/// browser clients, and server-to-server clients can additionally send <c>X-Session-Token</c>).</para>
/// <para><b>Subscription state.</b> A <c>streamId</c> is generated server-side and emitted on
/// the very first frame so clients can target subsequent <c>POST /subscribe</c> /
/// <c>POST /unsubscribe</c> calls at this connection rather than the session — multi-tab clients
/// thus maintain independent subscription sets without coordination.</para>
/// </remarks>
public static class UnifiedDataStream
{
    /// <summary>Heartbeat cadence (also used as the multiplexer's idle timeout).</summary>
    private const int HeartbeatTimeoutMs = 5000;

    /// <summary>Bootstrap events bypass the subscription filter — clients always need them.</summary>
    private static readonly HashSet<string> BootstrapEvents = new(StringComparer.Ordinal)
    {
        "stream-id",
        "metadata",
        "session-state",
        "heartbeat",
        "shutdown",
        "error",
    };

    public static async Task HandleAsync(
        Guid sessionId,
        HttpContext ctx,
        SessionManager sessions,
        StreamSubscriptionRegistry registry,
        CancellationToken ct)
    {
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

        // The unified stream is a profiler-data surface (per design §10). Database-open sessions
        // expose their state via /api/sessions/{id}/state polling; carrying their lifecycle
        // through this channel adds complexity for no consumer in v1.
        if (session is not (AttachSession or TraceSession))
        {
            ctx.Response.StatusCode = StatusCodes.Status409Conflict;
            return;
        }

        var streamId = Guid.NewGuid();
        registry.Register(streamId);
        await SseExtensions.WriteSseHeadersAsync(ctx, ct);

        try
        {
            // ── Bootstrap ───────────────────────────────────────────────────────────────────────
            await SseExtensions.WriteEventAsync(ctx, "stream-id", new { streamId }, ct);

            var initialMetadata = ResolveMetadata(session);
            if (initialMetadata != null)
            {
                await SseExtensions.WriteEventAsync(ctx, "metadata", new { metadata = initialMetadata }, ct);
            }

            await SseExtensions.WriteEventAsync(ctx, "session-state", BuildSessionState(session), ct);

            // ── Per-session-kind delta loop ────────────────────────────────────────────────────
            if (session is AttachSession attach)
            {
                await DispatchAttachAsync(ctx, attach.Runtime, streamId, registry, ct);
            }
            else
            {
                // Trace: no live deltas; just heartbeat. The client still needs reconnect /
                // disconnect detection, and may listen for late `topology-changed` if engine
                // reattach lands in a future v2.
                await HeartbeatOnlyLoopAsync(ctx, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal client disconnect.
        }
        catch (IOException)
        {
            // Kestrel can surface forcible disconnect as IOException before ct fires.
        }
        finally
        {
            registry.Unregister(streamId);
        }
    }

    /// <summary>
    /// Drains the AttachSessionRuntime subscriber channel and writes filtered, classified events to
    /// the SSE response. Runtime kinds map to unified event types as follows:
    /// <list type="bullet">
    ///   <item><c>metadata</c> → <c>metadata</c> (bypasses filter)</item>
    ///   <item><c>tickSummaryAdded</c> → <c>tick</c> (filtered, coalescing)</item>
    ///   <item><c>heartbeat</c> → <c>heartbeat</c> (bypasses filter)</item>
    ///   <item><c>shutdown</c> → <c>shutdown</c> (bypasses filter)</item>
    ///   <item><c>chunkAdded</c> / <c>globalMetricsUpdated</c> / <c>threadInfoAdded</c> → <i>dropped</i> (not on the v1 unified surface — clients still get them via the existing <c>/profiler/stream</c>)</item>
    /// </list>
    /// </summary>
    private static async Task DispatchAttachAsync(
        HttpContext ctx,
        AttachSessionRuntime runtime,
        Guid streamId,
        StreamSubscriptionRegistry registry,
        CancellationToken ct)
    {
        var (subscriberId, reader) = runtime.Subscribe();

        // Per-event-type internal channels (D2). Ticks coalesce to latest-only; everything else
        // is unbounded (these classes are low-frequency or already coalesced upstream).
        var tickChannel = Channel.CreateBounded<TickSummaryDto>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true });
        var passthroughChannel = Channel.CreateUnbounded<UnifiedFrame>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

        // Background classifier task — reads from the runtime, dispatches to the per-class
        // channels. Decoupled from the writer so a slow writer back-pressures via the runtime's
        // existing 1000-entry subscriber buffer instead of blocking the classifier.
        var classifierCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var classifier = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in reader.ReadAllAsync(classifierCts.Token))
                {
                    switch (evt.Kind)
                    {
                        case "metadata" when evt.Metadata != null:
                            passthroughChannel.Writer.TryWrite(new UnifiedFrame("metadata", new { metadata = evt.Metadata }));
                            break;
                        case "tickSummaryAdded" when evt.TickSummary != null:
                            tickChannel.Writer.TryWrite(evt.TickSummary);
                            break;
                        case "heartbeat":
                            passthroughChannel.Writer.TryWrite(new UnifiedFrame("heartbeat", new { status = evt.Status }));
                            break;
                        case "shutdown":
                            passthroughChannel.Writer.TryWrite(new UnifiedFrame("shutdown", new { status = evt.Status ?? "disconnected" }));
                            break;
                        // Other kinds (chunkAdded / globalMetricsUpdated / threadInfoAdded) are
                        // intentionally not surfaced on the unified stream in v1.
                    }
                }
            }
            catch (OperationCanceledException) { /* expected on disconnect */ }
            finally
            {
                tickChannel.Writer.TryComplete();
                passthroughChannel.Writer.TryComplete();
            }
        }, CancellationToken.None);

        try
        {
            // Multiplex pass-through frames + latest-tick + heartbeat onto the wire.
            while (!ct.IsCancellationRequested)
            {
                using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(ct);
                heartbeat.CancelAfter(HeartbeatTimeoutMs);

                var passReady = passthroughChannel.Reader.WaitToReadAsync(heartbeat.Token).AsTask();
                var tickReady = tickChannel.Reader.WaitToReadAsync(heartbeat.Token).AsTask();

                Task winner;
                try
                {
                    winner = await Task.WhenAny(passReady, tickReady);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Heartbeat fallback — neither channel produced anything in HeartbeatTimeoutMs.
                    await SseExtensions.WriteEventAsync(ctx, "heartbeat", new { status = "idle" }, ct);
                    continue;
                }

                if (winner == passReady)
                {
                    if (passReady.IsCompletedSuccessfully && passReady.Result)
                    {
                        while (passthroughChannel.Reader.TryRead(out var frame))
                        {
                            await EmitAsync(ctx, streamId, registry, frame.EventType, frame.Payload, ct);
                        }
                    }
                    else if (passReady.IsCompletedSuccessfully && !passReady.Result)
                    {
                        // Channel completed — classifier exited; bail out so we hit the heartbeat
                        // path or terminate.
                        return;
                    }
                }
                else if (winner == tickReady && tickReady.IsCompletedSuccessfully && tickReady.Result)
                {
                    if (tickChannel.Reader.TryRead(out var tick))
                    {
                        await EmitAsync(ctx, streamId, registry, "tick", new { tickSummary = tick }, ct);
                    }
                }

                // Drain any tick that landed while we were writing — keeps the wire fresh under load.
                if (tickChannel.Reader.TryRead(out var pending))
                {
                    await EmitAsync(ctx, streamId, registry, "tick", new { tickSummary = pending }, ct);
                }
            }
        }
        finally
        {
            classifierCts.Cancel();
            try { await classifier; } catch { /* swallow */ }
            runtime.Unsubscribe(subscriberId);
        }
    }

    /// <summary>
    /// Idle loop for sessions that don't produce live deltas (Trace today). Emits a heartbeat
    /// frame every <see cref="HeartbeatTimeoutMs"/> until the client disconnects.
    /// </summary>
    private static async Task HeartbeatOnlyLoopAsync(HttpContext ctx, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(HeartbeatTimeoutMs, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            await SseExtensions.WriteEventAsync(ctx, "heartbeat", new { status = "idle" }, ct);
        }
    }

    /// <summary>
    /// Writes one event to the wire, applying the subscription filter for non-bootstrap event
    /// types. Bootstrap events (<see cref="BootstrapEvents"/>) bypass the filter — clients always
    /// need them to drive UI lifecycle and reconnect logic.
    /// </summary>
    private static Task EmitAsync(
        HttpContext ctx,
        Guid streamId,
        StreamSubscriptionRegistry registry,
        string eventType,
        object payload,
        CancellationToken ct)
    {
        if (!BootstrapEvents.Contains(eventType) && !registry.IsSubscribed(streamId, eventType))
        {
            return Task.CompletedTask;
        }
        return SseExtensions.WriteEventAsync(ctx, eventType, payload, ct);
    }

    private static ProfilerMetadataDto ResolveMetadata(WbSession session) => session switch
    {
        AttachSession a => a.Runtime.Metadata,
        TraceSession t => t.Runtime.Metadata,
        _ => null,
    };

    /// <summary>
    /// Builds the <c>session-state</c> payload from the current session lifecycle. The shape
    /// matches <c>SessionStateDto</c> minus the kind field — clients already know the kind from
    /// the session-create response.
    /// </summary>
    private static object BuildSessionState(WbSession session)
    {
        if (session is AttachSession attach)
        {
            var ready = attach.Runtime.Metadata != null;
            return new
            {
                lifecycle = ready ? "Ready" : "Loading",
                isStreaming = ready,
                isPaused = false,
                isReattaching = false,
            };
        }
        if (session is TraceSession trace)
        {
            var lifecycle = !trace.Runtime.IsBuildComplete ? "Loading"
                : trace.Runtime.Metadata != null ? "Ready"
                : "Closed";
            return new
            {
                lifecycle,
                isStreaming = false,
                isPaused = false,
                isReattaching = false,
                reason = lifecycle == "Closed" ? "build-failed" : null,
            };
        }
        return new { lifecycle = "Ready", isStreaming = false, isPaused = false, isReattaching = false };
    }

    /// <summary>Internal multiplexer envelope — pairs an event type with its payload.</summary>
    private readonly record struct UnifiedFrame(string EventType, object Payload);
}
