using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Typhon.Profiler;
using Typhon.Workbench.Dtos.Sessions;
using Typhon.Workbench.Fixtures;
using Typhon.Workbench.Tests.Fixtures;

namespace Typhon.Workbench.Tests;

/// <summary>
/// #1083 — the realm capability is replayed to a subscriber that connects after it was earned.
/// </summary>
/// <remarks>
/// <para>
/// <b>The bug this fixture exists for, and it had already been "fixed" once.</b> An attach session earns
/// <c>SessionCapability.Realms</c> from its own stream, and the runtime announces it with a single
/// <c>capabilitiesChanged</c> delta on the transition. The first per-realm record lands within the first ticks of
/// attaching — <i>before</i> the browser has opened the SSE stream. The delta went to no subscribers, and because it
/// fires exactly once it never came again: the client kept the capability set it was handed at attach, and the Realms
/// view stayed absent from the View menu and the palette over a session the server had already granted it to.
/// </para>
/// <para>
/// <c>ProfilerLiveStream</c> replays the metadata snapshot, the accumulated thread infos and the capture state on
/// connect for exactly this reason — the capture-state line's own comment reads "without this a client that subscribes
/// between two transitions would render 'not recording' through an entire in-flight window, since deltas only fire on
/// change". The realm capability is that sentence one field over, and this test is ordered to prove it: the record is
/// fed and the capability confirmed <b>first</b>, and only then does the subscriber connect.
/// </para>
/// </remarks>
[TestFixture]
public sealed class ProfilerLiveStreamRealmCapabilityTests
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
    public async Task ASubscriberThatConnectsAfterTheCapabilityWasEarned_IsStillToldAboutIt()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await using var server = new MockTcpProfilerServer { Scripted = true };
        server.Start();

        var attachResp = await _client.PostAsJsonAsync(
            "/api/sessions/attach",
            new CreateAttachSessionRequest($"127.0.0.1:{server.Port}"),
            cts.Token);
        attachResp.EnsureSuccessStatusCode();
        var session = JsonSerializer.Deserialize<SessionDto>(await attachResp.Content.ReadAsStringAsync(cts.Token), Json)!;

        Assert.That(session.Capabilities, Does.Not.Contain("realms"), "precondition: the capability is earned, not default");

        await server.WaitForClientAsync(cts.Token);

        // One per-realm record, laid out inside a normal tick so the block is well-formed.
        await server.SendBlockAsync(
            MockRecordFactory.Concat(
                MockRecordFactory.TickStart(1_000_000),
                MockRecordFactory.GenericSpan(TraceEventKind.SpatialRealmTelemetry, 1_000_200, durationTicks: 0, payloadBytes: 16),
                MockRecordFactory.TickEnd(1_005_000)),
            cts.Token);

        // The capability must be earned BEFORE anything subscribes — that ordering is the whole point of the test.
        await WaitForRealmCapabilityAsync(session.SessionId, cts.Token);

        var frames = await ReadFramesUntilAsync(session.SessionId, "capabilitiesChanged", cts.Token);

        Assert.That(frames, Contains.Item("capabilitiesChanged"),
            "a subscriber that connects after the capability was earned must be told on connect, not left waiting for a delta that already fired");
    }

    /// <summary>A session with no realm telemetry must not be told it has realms — the replay is conditional, not unconditional.</summary>
    [Test]
    public async Task ASubscriberOnAStreamWithNoRealmTelemetry_IsNotToldAboutRealms()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await using var server = new MockTcpProfilerServer { Scripted = true };
        server.Start();

        var attachResp = await _client.PostAsJsonAsync(
            "/api/sessions/attach",
            new CreateAttachSessionRequest($"127.0.0.1:{server.Port}"),
            cts.Token);
        attachResp.EnsureSuccessStatusCode();
        var session = JsonSerializer.Deserialize<SessionDto>(await attachResp.Content.ReadAsStringAsync(cts.Token), Json)!;

        await server.WaitForClientAsync(cts.Token);

        // Read up to the heartbeat, which the stream writes last among its on-connect frames — so anything replayed
        // ahead of it has certainly been seen by the time it arrives.
        var frames = await ReadFramesUntilAsync(session.SessionId, "heartbeat", cts.Token);

        Assert.That(frames, Does.Not.Contain("capabilitiesChanged"),
            "an engine with one realm reports no per-realm records, so there is no capability to replay");
    }

    /// <summary>Poll the session projection until it advertises <c>realms</c>. Fails loudly rather than hanging.</summary>
    private async Task WaitForRealmCapabilityAsync(Guid sessionId, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        var lastSeen = "(never read)";
        while (DateTime.UtcNow < deadline)
        {
            // The session projection is session-token gated like everything else under /api/sessions/{id}.
            using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/sessions/{sessionId}");
            req.Headers.Add("X-Session-Token", sessionId.ToString());
            var resp = await _client.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadAsStringAsync(ct);
            var dto = JsonSerializer.Deserialize<SessionDto>(body, Json)!;
            lastSeen = dto.Capabilities == null ? $"(null) raw={body}" : string.Join(',', dto.Capabilities);
            if (dto.Capabilities?.Contains("realms") == true)
            {
                return;
            }
            await Task.Delay(20, ct);
        }

        Assert.Fail(
            "the session never advertised the realms capability, so the replay this test is about could not be reached. "
            + $"Last capabilities seen: {lastSeen}");
    }

    /// <summary>Open the SSE stream and collect event names until <paramref name="stop"/> arrives or the stream ends.</summary>
    private async Task<string[]> ReadFramesUntilAsync(Guid sessionId, string stop, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/sessions/{sessionId}/profiler/stream");
        req.Headers.Add("X-Session-Token", sessionId.ToString());

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new System.IO.StreamReader(stream);

        var seen = new System.Collections.Generic.List<string>();
        while (seen.Count < 32)
        {
            var frame = await SseFrameReader.ReadFrameAsync(reader, ct);
            if (frame is null)
            {
                break;
            }
            seen.Add(frame.Value.EventType);
            if (frame.Value.EventType == stop)
            {
                break;
            }
        }

        return [.. seen];
    }
}
