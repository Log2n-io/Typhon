using System.Threading.Channels;
using Typhon.Workbench.Dtos.Profiler;
using Typhon.Workbench.Sessions;
using WbSession = Typhon.Workbench.Sessions.ISession;

namespace Typhon.Workbench.Streams;

/// <summary>
/// SSE stream of growth-deltas for an Attach session (#289 unified pipeline; retyped for #308).
/// Emits a full <c>metadata</c> snapshot on connect / reconnect, then per-tick / per-chunk / 1 Hz
/// metrics deltas as the builder grows the in-memory cache. Each delta ships as a typed SSE event
/// (<c>event: tickSummaryAdded</c> etc.) — clients install one <c>addEventListener</c> per kind for
/// clean TypeScript narrowing instead of switching on a discriminator inside the JSON payload.
/// <c>heartbeat</c> on connection-state changes and on 5 s idle timeouts; <c>shutdown</c> when the
/// engine ends the session.
/// </summary>
public static class ProfilerLiveStream
{
    private const int HeartbeatTimeoutMs = 5000;

    public static async Task HandleAsync(
        Guid sessionId,
        HttpContext ctx,
        SessionManager sessions,
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

        if (session is not AttachSession attach)
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        await SseExtensions.WriteSseHeadersAsync(ctx, ct);

        var runtime = attach.Runtime;
        var (subscriberId, reader) = runtime.Subscribe();

        try
        {
            // Emit current state on connect so the client doesn't have to wait for the next event cycle. The metadata DTO
            // is a full grown-so-far snapshot — clients use it to seed their store and then apply incoming deltas.
            if (runtime.Metadata != null)
            {
                await WriteEventAsync(ctx, new LiveStreamEventDto(Kind: "metadata", Metadata: runtime.Metadata), ct);
            }
            // Replay accumulated thread-info mappings so the client doesn't depend on chunks loading first to know slot names.
            foreach (var info in runtime.GetThreadInfosSnapshot())
            {
                await WriteEventAsync(ctx, new LiveStreamEventDto(Kind: "threadInfoAdded", ThreadInfo: info), ct);
            }
            await WriteEventAsync(ctx, new LiveStreamEventDto(Kind: "heartbeat", Status: runtime.ConnectionStatus), ct);

            await DrainLoopAsync(ctx, reader, runtime, ct);
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
            runtime.Unsubscribe(subscriberId);
        }
    }

    private static async Task DrainLoopAsync(
        HttpContext ctx,
        ChannelReader<LiveStreamEventDto> reader,
        AttachSessionRuntime runtime,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            LiveStreamEventDto evt = null;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(HeartbeatTimeoutMs);

                if (await reader.WaitToReadAsync(timeout.Token))
                {
                    if (!reader.TryRead(out evt))
                    {
                        continue;
                    }
                }
                else
                {
                    return;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                await WriteEventAsync(
                    ctx,
                    new LiveStreamEventDto(Kind: "heartbeat", Status: runtime.ConnectionStatus),
                    ct);
                continue;
            }

            if (evt != null)
            {
                await WriteEventAsync(ctx, evt, ct);
            }
        }
    }

    /// <summary>
    /// Emits one <see cref="LiveStreamEventDto"/> as a typed SSE event. The <see cref="LiveStreamEventDto.Kind"/>
    /// becomes the <c>event:</c> line; <see cref="JsonIgnoreAttribute"/> on <c>Kind</c> keeps it out of the
    /// payload so the wire stays clean.
    /// </summary>
    private static Task WriteEventAsync(HttpContext ctx, LiveStreamEventDto evt, CancellationToken ct)
        => SseExtensions.WriteEventAsync(ctx, evt.Kind, evt, ct);
}
