using System.IO;
using System.Text;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;
using Typhon.Workbench.Streams;

namespace Typhon.Workbench.Tests;

/// <summary>
/// Wire-format pin for the shared SSE writer (#308). Every Workbench stream goes through these
/// helpers — a regression in framing (missing <c>event:</c>, missing trailing blank line, wrong
/// camelCase casing) breaks every consumer.
/// </summary>
[TestFixture]
public sealed class SseExtensionsTests
{
    [Test]
    public async Task WriteEventAsync_EmitsTypedEventLine_ThenDataLine_ThenBlank()
    {
        var (ctx, body) = MakeContext();

        await SseExtensions.WriteEventAsync(ctx, "tick", new { tickNumber = 42 }, default);

        var wire = ReadAll(body);
        Assert.That(wire, Is.EqualTo("event: tick\ndata: {\"tickNumber\":42}\n\n"),
            "wire format must be event:\\ndata:\\n\\n with no extra whitespace; clients depend on the exact layout");
    }

    [Test]
    public async Task WriteEventAsync_PayloadSerialisedWithCamelCase_AndIgnoresNulls()
    {
        var (ctx, body) = MakeContext();

        await SseExtensions.WriteEventAsync(
            ctx,
            "demo",
            new SamplePayload(BytesRead: 100, Message: null),
            default);

        var wire = ReadAll(body);
        // bytesRead camelCase, message dropped (null + WhenWritingNull policy from SseJsonOptions.Web).
        Assert.That(wire, Does.Contain("\"bytesRead\":100"));
        Assert.That(wire, Does.Not.Contain("message"),
            "null fields must be omitted from the wire — keeps payloads tight under heavy ingest");
    }

    [Test]
    public async Task WriteCommentAsync_EmitsCommentLine_ThenBlank()
    {
        var (ctx, body) = MakeContext();

        await SseExtensions.WriteCommentAsync(ctx, "keepalive", default);

        var wire = ReadAll(body);
        Assert.That(wire, Is.EqualTo(": keepalive\n\n"),
            "comment frames are colon-prefixed, ignored by EventSource consumers but visible to NAT idle timers");
    }

    [Test]
    public async Task WriteSseHeadersAsync_SetsContentType_CacheControl_Connection()
    {
        var (ctx, _) = MakeContext();

        await SseExtensions.WriteSseHeadersAsync(ctx, default);

        Assert.That(ctx.Response.Headers.ContentType.ToString(), Is.EqualTo("text/event-stream"));
        Assert.That(ctx.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-cache"));
        Assert.That(ctx.Response.Headers.Connection.ToString(), Is.EqualTo("keep-alive"));
    }

    private static (HttpContext ctx, MemoryStream body) MakeContext()
    {
        var body = new MemoryStream();
        var ctx = new DefaultHttpContext { Response = { Body = body } };
        return (ctx, body);
    }

    private static string ReadAll(MemoryStream body)
    {
        body.Position = 0;
        return Encoding.UTF8.GetString(body.ToArray());
    }

    private record SamplePayload(long? BytesRead = null, string Message = null);
}
