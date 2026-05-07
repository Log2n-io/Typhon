using System.Text.Json;

namespace Typhon.Workbench.Streams;

/// <summary>
/// Shared SSE writer helpers used by every Workbench stream (#308). Centralises the wire format so
/// every stream emits typed <c>event:</c> + <c>data:</c> frames identically — clients can use
/// <c>addEventListener('&lt;event-type&gt;', ...)</c> for clean TypeScript narrowing instead of
/// switching on a discriminator inside the JSON payload.
/// </summary>
public static class SseExtensions
{
    /// <summary>
    /// Sets the three SSE response headers (<c>Content-Type</c>, <c>Cache-Control</c>,
    /// <c>Connection</c>) and flushes them so the client unblocks before the first event arrives.
    /// Call once at the top of every stream handler after auth succeeds.
    /// </summary>
    public static async Task WriteSseHeadersAsync(HttpContext ctx, CancellationToken ct)
    {
        ctx.Response.Headers.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.Headers.Connection = "keep-alive";
        await ctx.Response.Body.FlushAsync(ct);
    }

    /// <summary>
    /// Writes one typed SSE frame: <c>event: &lt;eventType&gt;\ndata: &lt;json&gt;\n\n</c>, then
    /// flushes. The payload is serialised with <see cref="SseJsonOptions.Web"/> for consistent
    /// camelCase wire format — never call <see cref="JsonSerializer.Serialize{T}(T, JsonSerializerOptions)"/>
    /// directly inside a stream.
    /// </summary>
    public static async Task WriteEventAsync<T>(
        HttpContext ctx,
        string eventType,
        T payload,
        CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, SseJsonOptions.Web);
        await ctx.Response.WriteAsync($"event: {eventType}\ndata: {json}\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);
    }

    /// <summary>
    /// Writes one typed SSE frame using a pre-serialised JSON string. Useful when the caller has
    /// custom serialisation needs (e.g., <see cref="JsonDocument"/> reuse) and wants to avoid the
    /// double-serialisation cost.
    /// </summary>
    public static async Task WriteEventRawAsync(
        HttpContext ctx,
        string eventType,
        string json,
        CancellationToken ct)
    {
        await ctx.Response.WriteAsync($"event: {eventType}\ndata: {json}\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);
    }

    /// <summary>
    /// Writes a comment-only SSE frame (<c>: &lt;text&gt;\n\n</c>) for keepalives. EventSource
    /// clients ignore comment frames but proxies and NAT idle-timeout watchers see them as live
    /// traffic, preventing connection harvesting on otherwise-quiet streams.
    /// </summary>
    public static async Task WriteCommentAsync(HttpContext ctx, string text, CancellationToken ct)
    {
        await ctx.Response.WriteAsync($": {text}\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);
    }
}
