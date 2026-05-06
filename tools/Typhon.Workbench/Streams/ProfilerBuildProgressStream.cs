using System.Threading.Channels;
using Typhon.Workbench.Dtos.Profiler;
using Typhon.Workbench.Sessions;
using WbSession = Typhon.Workbench.Sessions.ISession;

namespace Typhon.Workbench.Streams;

/// <summary>
/// SSE stream emitting typed events while a Trace session's sidecar cache is being built (#308):
/// <list type="bullet">
///   <item><c>progress</c> — throttled by the builder's 200 ms interval; carries bytes / counts.</item>
///   <item><c>done</c> — terminal success; empty payload.</item>
///   <item><c>error</c> — terminal failure; carries <see cref="BuildProgressDto.Message"/>.</item>
/// </list>
/// After the terminal <c>done</c> or <c>error</c> the server cleanly closes the stream.
/// </summary>
public static class ProfilerBuildProgressStream
{
    /// <summary>SSE event type for in-progress build frames.</summary>
    public const string ProgressEvent = "progress";

    /// <summary>SSE event type for the terminal success frame.</summary>
    public const string DoneEvent = "done";

    /// <summary>SSE event type for the terminal failure frame.</summary>
    public const string ErrorEvent = "error";

    private record struct Frame(string EventType, BuildProgressDto Payload);

    public static async Task HandleAsync(
        Guid sessionId,
        HttpContext ctx,
        SessionManager sessions,
        CancellationToken ct)
    {
        // Dual-source auth: header first (server-to-server), URL sessionId fallback (browser EventSource).
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

        if (session is not TraceSession trace)
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        await SseExtensions.WriteSseHeadersAsync(ctx, ct);

        var channel = Channel.CreateUnbounded<Frame>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        Action<TraceSessionRuntime.BuildProgressEventArgs> progressHandler = args =>
        {
            channel.Writer.TryWrite(new Frame(ProgressEvent, new BuildProgressDto(
                BytesRead: args.BytesRead,
                TotalBytes: args.TotalBytes,
                TickCount: args.TickCount,
                EventCount: args.EventCount)));
        };

        Action<ProfilerMetadataDto> completedHandler = _ =>
        {
            channel.Writer.TryWrite(new Frame(DoneEvent, new BuildProgressDto()));
            channel.Writer.TryComplete();
        };

        Action<string> failedHandler = msg =>
        {
            channel.Writer.TryWrite(new Frame(ErrorEvent, new BuildProgressDto(Message: msg)));
            channel.Writer.TryComplete();
        };

        // Subscribe BEFORE the terminal-state check. If the build completes between the subscribe
        // and the check, the completedHandler has already queued the terminal event into our
        // channel — FlushLoop will deliver it. Doing the check first (as an earlier version did)
        // produced a race where a build transitioning to complete in between was observed as
        // "not complete" but the event fired before the handler was subscribed, leaving the
        // client hanging on an SSE connection that never emitted a terminal.
        trace.Runtime.BuildProgressChanged += progressHandler;
        trace.Runtime.BuildCompleted += completedHandler;
        trace.Runtime.BuildFailed += failedHandler;

        try
        {
            // Terminal-fast-path: if the build ALREADY finished before we subscribed, seed the
            // channel with the terminal event manually. The event was fired before subscription
            // so we'd never receive it via the handler — but we know the outcome from the runtime's
            // state, so we synthesize the equivalent frame here.
            if (trace.Runtime.IsBuildComplete)
            {
                if (trace.Runtime.Metadata != null)
                {
                    channel.Writer.TryWrite(new Frame(DoneEvent, new BuildProgressDto()));
                }
                else
                {
                    channel.Writer.TryWrite(new Frame(ErrorEvent, new BuildProgressDto(Message: "Build failed before stream connected.")));
                }
                channel.Writer.TryComplete();
            }

            await FlushLoop(ctx, channel.Reader, ct);
        }
        catch (OperationCanceledException)
        {
            // Normal client disconnect.
        }
        catch (IOException)
        {
            // Kestrel can surface forcible disconnect as IOException before the ct fires.
        }
        finally
        {
            trace.Runtime.BuildProgressChanged -= progressHandler;
            trace.Runtime.BuildCompleted -= completedHandler;
            trace.Runtime.BuildFailed -= failedHandler;
            channel.Writer.TryComplete();
        }
    }

    private static async Task FlushLoop(
        HttpContext ctx,
        ChannelReader<Frame> reader,
        CancellationToken ct)
    {
        // Each frame ships as a typed SSE event — clients install one addEventListener per type
        // (progress / done / error) and narrow the payload by event-type at compile time.
        while (await reader.WaitToReadAsync(ct))
        {
            while (reader.TryRead(out var frame))
            {
                await SseExtensions.WriteEventAsync(ctx, frame.EventType, frame.Payload, ct);
                if (frame.EventType is DoneEvent or ErrorEvent)
                {
                    return;
                }
            }
        }
    }
}
