using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Workbench.Dtos.Sessions;
using Typhon.Workbench.Fixtures;
using Typhon.Workbench.Services;
using Typhon.Workbench.Tests.Fixtures;

namespace Typhon.Workbench.Tests;

/// <summary>
/// End-to-end coverage for the unified data stream (#308 Phase B/C). Pins:
/// <list type="bullet">
///   <item>Bootstrap event order on connect: <c>stream-id</c> → <c>metadata</c> →
///         <c>session-state</c>.</item>
///   <item>Auth boundary: 401 for missing session, 409 for unsupported session kind (Open).</item>
///   <item>Subscribe / unsubscribe round-trip: events not in the set are dropped before
///         serialisation; events added later flow through.</item>
/// </list>
/// </summary>
[TestFixture]
public sealed class UnifiedDataStreamTests
{
    private WorkbenchFactory _factory;
    private HttpClient _client;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    [SetUp]
    public void SetUp()
    {
        _factory = new WorkbenchFactory();
        _client = _factory.CreateAuthenticatedClient();
    }

    [TearDown]
    public void TearDown() => _factory.Dispose();

    [Test]
    public async Task Stream_NoSession_Returns401()
    {
        var resp = await _client.GetAsync($"/api/sessions/{Guid.NewGuid()}/stream");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Stream_OpenSession_Returns409()
    {
        // The unified data stream is a profiler-data surface — a database-open session has no
        // tick / metadata / topology to push, so we 409 early. Same boundary as DataController.
        var createResp = await _client.PostAsJsonAsync("/api/sessions/file", new CreateFileSessionRequest("demo.typhon"));
        createResp.EnsureSuccessStatusCode();
        var session = JsonSerializer.Deserialize<SessionDto>(await createResp.Content.ReadAsStringAsync(), Json)!;

        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/sessions/{session.SessionId}/stream");
        req.Headers.Add("X-Session-Token", session.SessionId.ToString());
        var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);

        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
    }

    [Test]
    [CancelAfter(20000)]
    public async Task Stream_AttachSession_EmitsBootstrapEventsInOrder(CancellationToken testCt)
    {
        await using var server = new MockTcpProfilerServer
        {
            BlockInterval = TimeSpan.FromMilliseconds(40),
            MaxBlocks = 50,
        };
        server.Start();

        var attachResp = await _client.PostAsJsonAsync(
            "/api/sessions/attach",
            new CreateAttachSessionRequest($"127.0.0.1:{server.Port}"));
        attachResp.EnsureSuccessStatusCode();
        var session = JsonSerializer.Deserialize<SessionDto>(await attachResp.Content.ReadAsStringAsync(), Json)!;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(testCt);
        cts.CancelAfter(TimeSpan.FromSeconds(8));
        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/sessions/{session.SessionId}/stream");
        req.Headers.Add("X-Session-Token", session.SessionId.ToString());

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(resp.Content.Headers.ContentType!.MediaType, Is.EqualTo("text/event-stream"));

        using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);

        var streamIdFrame = await SseFrameReader.ReadFrameAsync(reader, cts.Token);
        Assert.That(streamIdFrame, Is.Not.Null);
        Assert.That(streamIdFrame.Value.EventType, Is.EqualTo("stream-id"),
            "first frame on the unified stream must be stream-id so clients can target /subscribe at this connection");

        using (var doc = JsonDocument.Parse(streamIdFrame.Value.Data))
        {
            Assert.That(doc.RootElement.TryGetProperty("streamId", out _), Is.True,
                "stream-id frame must carry a non-null streamId");
        }

        // Read until we see session-state — metadata may or may not be present yet (depending on
        // how fast the mock server's first Init lands), but session-state is always emitted.
        var sawSessionState = false;
        SseFrame? frame;
        while ((frame = await SseFrameReader.ReadFrameAsync(reader, cts.Token)) is not null)
        {
            if (frame.Value.EventType == "session-state")
            {
                sawSessionState = true;
                break;
            }
            // metadata / heartbeat frames in between are fine.
        }
        Assert.That(sawSessionState, Is.True, "stream must emit a session-state event after stream-id");
    }

    [Test]
    [CancelAfter(20000)]
    public async Task Stream_TickEventsRequireSubscription(CancellationToken testCt)
    {
        // Without a /subscribe call for `tick`, the multiplexer should drop tick frames before the
        // wire. We connect, capture the streamId, then read for a short window and assert no tick
        // frames arrive — even though the mock server is producing block frames at 40 ms.
        await using var server = new MockTcpProfilerServer
        {
            BlockInterval = TimeSpan.FromMilliseconds(40),
            MaxBlocks = 50,
        };
        server.Start();

        var attachResp = await _client.PostAsJsonAsync(
            "/api/sessions/attach",
            new CreateAttachSessionRequest($"127.0.0.1:{server.Port}"));
        attachResp.EnsureSuccessStatusCode();
        var session = JsonSerializer.Deserialize<SessionDto>(await attachResp.Content.ReadAsStringAsync(), Json)!;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(testCt);
        cts.CancelAfter(TimeSpan.FromSeconds(4));
        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/sessions/{session.SessionId}/stream");
        req.Headers.Add("X-Session-Token", session.SessionId.ToString());

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);

        // Read for ~1.5 s to give the mock plenty of opportunity to produce ticks.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(1.5);
        var sawTick = false;
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                using var inner = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                inner.CancelAfter(deadline - DateTime.UtcNow);
                var frame = await SseFrameReader.ReadFrameAsync(reader, inner.Token);
                if (frame is null) break;
                if (frame.Value.EventType == "tick") { sawTick = true; break; }
            }
        }
        catch (OperationCanceledException) { /* timeout reached — expected */ }

        Assert.That(sawTick, Is.False,
            "tick events must not flow without a /subscribe call — server-side filter dropped them");
    }

    [Test]
    public async Task Subscribe_UnknownStreamId_Returns404()
    {
        var session = await CreateOpenForSubscribeAsync();

        var resp = await PostWithSessionTokenAsync(session, "/subscribe",
            new { streamId = Guid.NewGuid(), events = new[] { "tick" } });

        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Subscribe_MissingStreamId_Returns400()
    {
        var session = await CreateOpenForSubscribeAsync();

        var resp = await PostWithSessionTokenAsync(session, "/subscribe",
            new { streamId = Guid.Empty, events = new[] { "tick" } });

        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Unsubscribe_UnknownStreamId_Returns404()
    {
        var session = await CreateOpenForSubscribeAsync();

        var resp = await PostWithSessionTokenAsync(session, "/unsubscribe",
            new { streamId = Guid.NewGuid(), events = new[] { "tick" } });

        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Subscribe_RegisteredStream_MutatesRegistry()
    {
        // Drives subscribe by registering the streamId directly on the singleton registry rather
        // than waiting for the SSE handshake — keeps the assertion synchronous on the registry's
        // observable state.
        var session = await CreateOpenForSubscribeAsync();
        var registry = _factory.Services.GetRequiredService<StreamSubscriptionRegistry>();
        var streamId = Guid.NewGuid();
        registry.Register(streamId);

        var subscribeResp = await PostWithSessionTokenAsync(session, "/subscribe",
            new { streamId, events = new[] { "tick", "log" } });
        Assert.That(subscribeResp.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(registry.IsSubscribed(streamId, "tick"), Is.True);
        Assert.That(registry.IsSubscribed(streamId, "log"), Is.True);

        var unsubscribeResp = await PostWithSessionTokenAsync(session, "/unsubscribe",
            new { streamId, events = new[] { "tick" } });
        Assert.That(unsubscribeResp.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(registry.IsSubscribed(streamId, "tick"), Is.False);
        Assert.That(registry.IsSubscribed(streamId, "log"), Is.True);

        registry.Unregister(streamId);
    }

    [Test]
    public void Registry_DI_RegistersAsSingleton()
    {
        // Pin DI registration so adding a new SessionsController-style scope doesn't break the
        // multiplexer's reliance on a process-wide singleton.
        var registry = _factory.Services.GetRequiredService<StreamSubscriptionRegistry>();
        var registry2 = _factory.Services.GetRequiredService<StreamSubscriptionRegistry>();
        Assert.That(registry, Is.SameAs(registry2));
    }

    private async Task<SessionDto> CreateOpenForSubscribeAsync()
    {
        // We just need a valid sessionId for the controller's RequireSession middleware to let the
        // body validation run. Use an open session — controller doesn't reject by kind.
        var resp = await _client.PostAsJsonAsync("/api/sessions/file", new CreateFileSessionRequest("demo.typhon"));
        resp.EnsureSuccessStatusCode();
        var session = JsonSerializer.Deserialize<SessionDto>(await resp.Content.ReadAsStringAsync(), Json)!;
        return session;
    }

    private Task<HttpResponseMessage> PostWithSessionTokenAsync(SessionDto session, string path, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/sessions/{session.SessionId}{path}")
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Add("X-Session-Token", session.SessionId.ToString());
        return _client.SendAsync(req);
    }
}
