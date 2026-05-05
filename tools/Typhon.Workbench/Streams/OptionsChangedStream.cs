using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Typhon.Workbench.Hosting;

namespace Typhon.Workbench.Streams;

/// <summary>
/// SSE channel that pushes <see cref="WorkbenchOptions"/> changes to the client (#302 Phase 5).
/// The browser opens an <c>EventSource</c> at <c>GET /api/options/stream</c> on app start and stays
/// connected for the session — when the on-disk options file changes (out-of-band edit, or another
/// Workbench window <c>PATCH</c>ing) the store's <c>OptionsChanged</c> event fires and we serialize
/// the new document into a single <c>data:</c> frame.
/// </summary>
/// <remarks>
/// Bootstrap-token-gated via the URL prefix; <c>EventSource</c> can't send custom headers, so the
/// security boundary is whatever Vite's dev proxy injects on the way through (matching how
/// <see cref="HeartbeatStream"/> handles the same constraint). A 30-second keepalive comment frame
/// keeps NAT / proxy idle-timeouts from killing otherwise-quiet streams.
/// </remarks>
public static class OptionsChangedStream
{
    private static readonly TimeSpan KeepaliveInterval = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static async Task HandleAsync(
        HttpContext ctx,
        OptionsStore store,
        CancellationToken ct)
    {
        ctx.Response.Headers.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.Headers.Connection = "keep-alive";

        // Buffer changes via a bounded channel — handler awaits ReadAsync and writes one SSE frame
        // per options snapshot. Bounded(1, DropOldest) coalesces bursts: if two PATCHes fire while
        // the writer is mid-flush, the client only sees the latest (eventually-consistent UX is fine
        // for an options panel; nobody cares about intermediate states).
        var channel = Channel.CreateBounded<WorkbenchOptions>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        void Listener(WorkbenchOptions next)
        {
            channel.Writer.TryWrite(next);
        }

        store.OptionsChanged += Listener;
        try
        {
            // Send the current snapshot immediately so the client can sanity-check its store on connect.
            channel.Writer.TryWrite(store.Get());

            using var keepalive = new PeriodicTimer(KeepaliveInterval);
            while (!ct.IsCancellationRequested)
            {
                var readTask = channel.Reader.ReadAsync(ct).AsTask();
                var keepaliveTask = keepalive.WaitForNextTickAsync(ct).AsTask();
                var winner = await Task.WhenAny(readTask, keepaliveTask);
                if (winner == readTask)
                {
                    var snapshot = await readTask;
                    var payload = JsonSerializer.Serialize(snapshot, JsonOpts);
                    await ctx.Response.WriteAsync($"data: {payload}\n\n", ct);
                    await ctx.Response.Body.FlushAsync(ct);
                }
                else
                {
                    // Comment-only frame keeps idle proxies from harvesting the connection.
                    await ctx.Response.WriteAsync(": keepalive\n\n", ct);
                    await ctx.Response.Body.FlushAsync(ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected — normal exit.
        }
        finally
        {
            store.OptionsChanged -= Listener;
            channel.Writer.TryComplete();
        }
    }
}
