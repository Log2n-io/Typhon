using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Typhon.Workbench.Dtos.Sessions;
using Typhon.Workbench.Fixtures;

namespace Typhon.Workbench.Tests;

/// <summary>
/// Regression guard for the subscribe-before-terminal-check race in
/// <see cref="Typhon.Workbench.Streams.ProfilerBuildProgressStream"/>.
///
/// The bug was: if the trace-cache build completed between the SSE handler's
/// <c>IsBuildComplete</c> check and the event subscription, the terminal callback fired before the
/// handler was wired up — and the client hung forever on an SSE connection that never emitted a
/// <c>done</c> event. The fix subscribes first, then checks, then synthesizes the terminal event
/// if the build already finished. Without the fix this test hangs until the cts timeout.
///
/// The deterministic recipe: pre-build the trace (let the session fully complete) <i>before</i>
/// connecting to the SSE endpoint. On the fixed code the stream exits promptly with a <c>done</c>
/// frame from the terminal fast-path.
/// </summary>
[TestFixture]
public sealed class ProfilerBuildProgressStreamTests
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

    private async Task<SessionDto> CreateTraceSessionAsync()
    {
        var path = TraceFixtureBuilder.BuildMinimalTrace(_factory.DemoDirectory, tickCount: 3, instantsPerTick: 2);
        var resp = await _client.PostAsJsonAsync("/api/sessions/trace", new CreateTraceSessionRequest(path));
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<SessionDto>(await resp.Content.ReadAsStringAsync(), Json)!;
    }

    private async Task WaitForBuildAsync(Guid sessionId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"/api/sessions/{sessionId}/profiler/metadata");
            req.Headers.Add("X-Session-Token", sessionId.ToString());
            var resp = await _client.SendAsync(req);
            if (resp.StatusCode == HttpStatusCode.OK) return;
            await Task.Delay(25);
        }
        Assert.Fail("Build timeout");
    }

    [Test]
    public async Task Subscribe_AfterBuildCompletes_ReceivesTerminalDone()
    {
        // 1. Create session and let the build fully complete BEFORE we connect to the SSE.
        var session = await CreateTraceSessionAsync();
        await WaitForBuildAsync(session.SessionId, TimeSpan.FromSeconds(5));

        // 2. Now subscribe. The fixed handler must observe IsBuildComplete == true and synthesize
        //    a "done" frame via the terminal fast-path. The broken code would wait for an event
        //    that already fired and hang until our cts fires.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/sessions/{session.SessionId}/profiler/build-progress")
        {
            Version = HttpVersion.Version11,
        };
        req.Headers.Add("X-Session-Token", session.SessionId.ToString());

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(resp.Content.Headers.ContentType!.MediaType, Is.EqualTo("text/event-stream"));

        var phase = await ReadFirstTerminalPhaseAsync(resp, cts.Token);
        Assert.That(phase, Is.EqualTo("done"),
            "post-build subscribe must synthesize a terminal `done` via the fast-path; broken code hangs here");
    }

    [Test]
    public async Task Subscribe_DuringBuild_StreamsProgressThenTerminal()
    {
        // Control path — subscribe immediately after session create, before the build had any
        // realistic chance to finish. Proves the normal path still works (progress frames stream,
        // followed by done). Combined with the fast-path test above, any regression to either
        // branch surfaces cleanly.
        var session = await CreateTraceSessionAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/sessions/{session.SessionId}/profiler/build-progress");
        req.Headers.Add("X-Session-Token", session.SessionId.ToString());

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var phase = await ReadFirstTerminalPhaseAsync(resp, cts.Token);
        Assert.That(phase, Is.AnyOf("done", "error"),
            "stream must always close with either `done` or `error`");
    }

    /// <summary>
    /// Reads typed SSE frames from <paramref name="resp"/> until one whose <c>event:</c> line is
    /// <c>done</c> or <c>error</c>. Returns the event type. Throws if the stream ends without a
    /// terminal frame. Post-#308 the build-progress stream emits typed events
    /// (<c>progress</c> / <c>done</c> / <c>error</c>) instead of putting the phase inside the JSON
    /// payload.
    /// </summary>
    private static async Task<string> ReadFirstTerminalPhaseAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        while (true)
        {
            var frame = await Fixtures.SseFrameReader.ReadFrameAsync(reader, ct);
            if (frame is null) break;
            if (frame.Value.EventType is "done" or "error")
            {
                return frame.Value.EventType;
            }
            // otherwise it's a `progress` frame; keep reading.
        }
        throw new InvalidOperationException("SSE stream ended without a terminal frame.");
    }
}
