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
/// End-to-end coverage for the profiler live-stream SSE endpoint. Post-#308 the wire format uses
/// typed SSE events (<c>event: metadata</c>, <c>event: tickSummariesAdded</c>, etc.) instead of
/// putting the discriminant inside the payload. Clients install one <c>addEventListener</c> per
/// kind for clean TypeScript narrowing — a regression that drops a field or renames the event type
/// would silently break the attach-mode panel. These tests pin the exact event type and per-event
/// payload shape.
/// </summary>
[TestFixture]
public sealed class ProfilerLiveStreamTests
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
    public async Task Stream_AfterAttach_EmitsMetadata_Heartbeat_AndDeltaFrames()
    {
        // #289 — post-unification, the live SSE stream uses growth deltas instead of per-tick batches:
        //   - "metadata" full snapshot on connect (kind metadata)
        //   - "tickSummariesAdded" / "chunkAdded" / "globalMetricsUpdated" growth deltas
        //   - "heartbeat" status frames
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

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/sessions/{session.SessionId}/profiler/stream");
        req.Headers.Add("X-Session-Token", session.SessionId.ToString());

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(resp.Content.Headers.ContentType!.MediaType, Is.EqualTo("text/event-stream"));

        var seenMetadata = false;
        var seenHeartbeat = false;
        var seenTickSummariesAdded = false;
        string tickJson = null;
        string metadataJson = null;

        using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);
        while (!(seenMetadata && seenHeartbeat && seenTickSummariesAdded))
        {
            var frame = await Fixtures.SseFrameReader.ReadFrameAsync(reader, cts.Token);
            if (frame is null) break;

            switch (frame.Value.EventType)
            {
                case "metadata":
                    seenMetadata = true;
                    metadataJson = frame.Value.Data;
                    break;
                case "heartbeat":
                    seenHeartbeat = true;
                    break;
                case "tickSummariesAdded":
                    seenTickSummariesAdded = true;
                    tickJson = frame.Value.Data;
                    break;
            }
        }

        Assert.That(seenMetadata, Is.True, "client expects a metadata frame on connect");
        Assert.That(seenHeartbeat, Is.True, "client expects a heartbeat frame with the initial connection status");
        Assert.That(seenTickSummariesAdded, Is.True, "client expects at least one tickSummariesAdded delta from block-frame ingest");

        // Post-#308 the kind no longer ships in payload; the SSE event: line above already discriminates. The payload
        // carries only the per-kind sub-object.
        using (var metaDoc = JsonDocument.Parse(metadataJson!))
        {
            Assert.That(metaDoc.RootElement.TryGetProperty("metadata", out var metaProp), Is.True,
                "metadata frame must carry a non-null metadata sub-object");
            Assert.That(metaProp.GetProperty("header").GetProperty("timestampFrequency").GetInt64(),
                Is.EqualTo(10_000_000),
                "mock emits 10 MHz timestamp frequency; regression here would drop decoding precision on the client");
        }

        using (var tickDoc = JsonDocument.Parse(tickJson!))
        {
            Assert.That(tickDoc.RootElement.TryGetProperty("tickSummaries", out var summariesProp), Is.True,
                "tickSummariesAdded frame must carry a non-null tickSummaries array");
            Assert.That(summariesProp.ValueKind, Is.EqualTo(JsonValueKind.Array),
                "the coalesced delta is an ARRAY of summaries — a single object would break the client's batch append");
            Assert.That(summariesProp.GetArrayLength(), Is.GreaterThan(0),
                "an empty batch must never be broadcast");
            Assert.That(summariesProp[0].GetProperty("tickNumber").GetUInt32(), Is.GreaterThan(0u));
        }
    }

    [Test]
    public async Task Stream_CoalescesFinalizedTicks_IntoFewerFramesThanTicks()
    {
        // One SSE frame per finalized tick is one frame per ENGINE tick — 48.8/s against the SWG demo at --hz 50.
        // Every frame flips the client's `metadata` identity, which re-renders the profiler tree and repaints both
        // canvases; `drawTimeArea` ran 7 942 times in 166 s (25 % of main-thread wall clock) for that reason alone.
        // The design doc has always specified "a single SSE frame per 100 ms … burst of summaries each frame"
        // (claude/design/Profiler/08-profiler-live-replay-unification.md §"Delta cadence"); this pins it.
        await using var server = new MockTcpProfilerServer
        {
            BlockInterval = TimeSpan.FromMilliseconds(1),
            MaxBlocks = 400,
        };
        server.Start();

        var attachResp = await _client.PostAsJsonAsync(
            "/api/sessions/attach",
            new CreateAttachSessionRequest($"127.0.0.1:{server.Port}"));
        attachResp.EnsureSuccessStatusCode();
        var session = JsonSerializer.Deserialize<SessionDto>(await attachResp.Content.ReadAsStringAsync(), Json)!;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/sessions/{session.SessionId}/profiler/stream");
        req.Headers.Add("X-Session-Token", session.SessionId.ToString());

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        resp.EnsureSuccessStatusCode();

        var frames = 0;
        var summaries = 0;
        var largestBatch = 0;
        using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);
        while (summaries < 20)
        {
            var frame = await Fixtures.SseFrameReader.ReadFrameAsync(reader, cts.Token);
            if (frame is null) break;
            if (frame.Value.EventType != "tickSummariesAdded") continue;
            using var doc = JsonDocument.Parse(frame.Value.Data);
            var batch = doc.RootElement.GetProperty("tickSummaries").GetArrayLength();
            frames++;
            summaries += batch;
            if (batch > largestBatch) largestBatch = batch;
        }

        Assert.That(summaries, Is.GreaterThanOrEqualTo(20), "the mock must produce enough ticks for the window to be observable");
        // The load-bearing assertion: at least one frame carried several ticks. Revert the coalescing and every frame
        // carries exactly one, so this is 1.
        Assert.That(largestBatch, Is.GreaterThan(1),
            $"deltas must coalesce — saw {frames} frames for {summaries} summaries, largest batch {largestBatch}");
        Assert.That(frames, Is.LessThan(summaries),
            "frame count must be strictly below tick count, or the coalescing bought nothing");
    }

    [Test]
    public async Task Stream_NonAttachSession_Returns401()
    {
        // A database session — even one holding a capture — is not a valid subject of the LIVE stream: the handler casts
        // to AttachSession and bails with 401. Pins the upstream auth boundary.
        var tracePath = TraceFixtureBuilder.BuildMinimalTrace(_factory.DemoDirectory, tickCount: 2, instantsPerTick: 1);
        var session = await CaptureSessionFactory.OpenWithCaptureAsync(_client, _factory.DemoDirectory, tracePath);

        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/sessions/{session.SessionId}/profiler/stream");
        req.Headers.Add("X-Session-Token", session.SessionId.ToString());
        var streamResp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        Assert.That(streamResp.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }
}
