using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Typhon.Workbench.Dtos.Profiler;
using Typhon.Workbench.Dtos.Sessions;
using Typhon.Workbench.Fixtures;

namespace Typhon.Workbench.Tests;

/// <summary>
/// On-demand tick capture (#805) — the HTTP and SSE surface. Covers AC-11 (the capture endpoint), AC-12 (the
/// <c>captureStateChanged</c> SSE delta) and AC-13 (the attach-time mode choice).
/// </summary>
[TestFixture]
public sealed class CaptureApiTests
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

    private async Task<(SessionDto Session, MockTcpProfilerServer Server)> AttachAsync(bool cherryPick)
    {
        var server = new MockTcpProfilerServer
        {
            BlockInterval = TimeSpan.FromMilliseconds(30),
            MaxBlocks = 200,
        };
        server.Start();

        var resp = await _client.PostAsJsonAsync(
            "/api/sessions/attach",
            new CreateAttachSessionRequest($"127.0.0.1:{server.Port}", CherryPick: cherryPick));
        resp.EnsureSuccessStatusCode();
        var session = JsonSerializer.Deserialize<SessionDto>(await resp.Content.ReadAsStringAsync(), Json)!;
        return (session, server);
    }

    private HttpRequestMessage Authorized(HttpMethod method, string url, Guid sessionId)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-Session-Token", sessionId.ToString());
        return req;
    }

    /// <summary>AC-13 — cherry-pick starts idle; capture-everything preserves the pre-#805 behaviour.</summary>
    [Test]
    public async Task AttachMode_DeterminesInitialCaptureState()
    {
        var (cherry, cherryServer) = await AttachAsync(cherryPick: true);
        await using (cherryServer)
        {
            var req = Authorized(HttpMethod.Get, $"/api/sessions/{cherry.SessionId}/profiler/capture", cherry.SessionId);
            using var resp = await _client.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            var state = JsonSerializer.Deserialize<CaptureStateDto>(await resp.Content.ReadAsStringAsync(), Json)!;
            Assert.Multiple(() =>
            {
                Assert.That(state.State, Is.EqualTo("Idle"), "cherry-pick attaches idle");
                Assert.That(state.Mode, Is.EqualTo("CherryPick"));
            });
        }

        var (everything, everythingServer) = await AttachAsync(cherryPick: false);
        await using (everythingServer)
        {
            var req = Authorized(HttpMethod.Get, $"/api/sessions/{everything.SessionId}/profiler/capture", everything.SessionId);
            using var resp = await _client.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            var state = JsonSerializer.Deserialize<CaptureStateDto>(await resp.Content.ReadAsStringAsync(), Json)!;
            Assert.Multiple(() =>
            {
                Assert.That(state.State, Is.EqualTo("Everything"), "the default must remain today's always-on behaviour");
                Assert.That(state.Mode, Is.EqualTo("Everything"));
            });
        }
    }

    /// <summary>AC-11 — arming through the endpoint moves the session into Recording with the requested budget.</summary>
    [Test]
    public async Task PostCapture_ArmsAWindow_AndReportsRemaining()
    {
        var (session, server) = await AttachAsync(cherryPick: true);
        await using var _ = server;

        var req = Authorized(HttpMethod.Post, $"/api/sessions/{session.SessionId}/profiler/capture", session.SessionId);
        req.Content = JsonContent.Create(new CaptureRequest(25));
        using var resp = await _client.SendAsync(req);
        resp.EnsureSuccessStatusCode();

        var state = JsonSerializer.Deserialize<CaptureStateDto>(await resp.Content.ReadAsStringAsync(), Json)!;
        Assert.Multiple(() =>
        {
            Assert.That(state.State, Is.EqualTo("Recording"), "the click must be acknowledged immediately, not one tick later");
            Assert.That(state.Remaining, Is.EqualTo(25));
        });
    }

    /// <summary>
    /// AC-11 — arming a capture-everything session is a conflict, not a silent mode switch. Allowing it would leave the
    /// session reporting a bounded window while its mode still said "everything": a state nobody asked for and the UI
    /// has no way to describe.
    /// </summary>
    [Test]
    public async Task PostCapture_OnCaptureEverythingSession_Is409()
    {
        var (session, server) = await AttachAsync(cherryPick: false);
        await using var _ = server;

        var req = Authorized(HttpMethod.Post, $"/api/sessions/{session.SessionId}/profiler/capture", session.SessionId);
        req.Content = JsonContent.Create(new CaptureRequest(10));
        using var resp = await _client.SendAsync(req);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
    }

    /// <summary>AC-11 — a negative budget is a client error, not a silently-clamped one.</summary>
    [Test]
    public async Task PostCapture_RejectsNegativeTickCount()
    {
        var (session, server) = await AttachAsync(cherryPick: true);
        await using var _ = server;

        var req = Authorized(HttpMethod.Post, $"/api/sessions/{session.SessionId}/profiler/capture", session.SessionId);
        req.Content = JsonContent.Create(new CaptureRequest(-5));
        using var resp = await _client.SendAsync(req);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    /// <summary>AC-11 — the endpoint is gated by the bootstrap token like every sibling under <c>/api/sessions</c>.</summary>
    [Test]
    public async Task PostCapture_WithoutBootstrapToken_Is401()
    {
        var (session, server) = await AttachAsync(cherryPick: true);
        await using var _ = server;

        using var unauthenticated = _factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/sessions/{session.SessionId}/profiler/capture");
        req.Headers.Add("X-Session-Token", session.SessionId.ToString());
        req.Content = JsonContent.Create(new CaptureRequest(10));
        using var resp = await unauthenticated.SendAsync(req);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    /// <summary>
    /// AC-12 — a subscriber sees the capture state on connect and again when a window is armed. The connect-time frame
    /// matters as much as the delta: without it, a client subscribing mid-window would render "not recording" for the
    /// whole capture.
    /// </summary>
    [Test]
    public async Task Stream_EmitsCaptureStateOnConnect_AndOnArm()
    {
        var (session, server) = await AttachAsync(cherryPick: true);
        await using var _ = server;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var streamReq = Authorized(HttpMethod.Get, $"/api/sessions/{session.SessionId}/profiler/stream", session.SessionId);
        using var resp = await _client.SendAsync(streamReq, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);

        string firstCaptureJson = null;
        while (firstCaptureJson is null)
        {
            var frame = await Fixtures.SseFrameReader.ReadFrameAsync(reader, cts.Token);
            if (frame is null) break;
            if (frame.Value.EventType == "captureStateChanged")
            {
                firstCaptureJson = frame.Value.Data;
            }
        }
        Assert.That(firstCaptureJson, Is.Not.Null, "the stream must seed capture state on connect");
        using (var doc = JsonDocument.Parse(firstCaptureJson!))
        {
            Assert.That(doc.RootElement.GetProperty("captureState").GetProperty("state").GetString(),
                Is.EqualTo("Idle"), "a cherry-pick session starts idle");
        }

        // Arm, then expect a fresh delta reporting Recording.
        var armReq = Authorized(HttpMethod.Post, $"/api/sessions/{session.SessionId}/profiler/capture", session.SessionId);
        armReq.Content = JsonContent.Create(new CaptureRequest(50));
        (await _client.SendAsync(armReq, cts.Token)).EnsureSuccessStatusCode();

        var sawRecording = false;
        while (!sawRecording)
        {
            var frame = await Fixtures.SseFrameReader.ReadFrameAsync(reader, cts.Token);
            if (frame is null) break;
            if (frame.Value.EventType != "captureStateChanged") continue;
            using var doc = JsonDocument.Parse(frame.Value.Data);
            if (doc.RootElement.GetProperty("captureState").GetProperty("state").GetString() == "Recording")
            {
                sawRecording = true;
            }
        }
        Assert.That(sawRecording, Is.True, "arming must fan out a captureStateChanged delta to SSE subscribers");
    }
}
