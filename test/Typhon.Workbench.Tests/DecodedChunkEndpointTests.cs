using System;
using System.IO;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using NUnit.Framework;
using Typhon.Workbench.Dtos.Sessions;
using Typhon.Workbench.Fixtures;

namespace Typhon.Workbench.Tests;

/// <summary>
/// Coverage for <c>GET /api/sessions/{id}/profiler/chunks/{idx}/decoded</c> — the JSON projection
/// over a profiler chunk. The binary <c>/chunks/{idx}</c> endpoint already has full coverage in
/// <see cref="ProfilerControllerTests"/>; these tests focus on the new decode + filter paths and
/// confirm the auth gates apply equally.
/// </summary>
[TestFixture]
public sealed class DecodedChunkEndpointTests
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

    private async Task<SessionDto> CreateTraceSessionAsync(int tickCount = 5, int instantsPerTick = 3)
    {
        var path = TraceFixtureBuilder.BuildMinimalTrace(_factory.DemoDirectory, tickCount, instantsPerTick);
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
            if (resp.StatusCode != HttpStatusCode.Accepted)
            {
                Assert.Fail($"Unexpected status while waiting for build: {(int)resp.StatusCode}");
            }
            await Task.Delay(25);
        }
        Assert.Fail("Trace cache build did not complete within the allotted timeout.");
    }

    private async Task<JsonDocument> FetchDecodedAsync(Guid sessionId, int chunkIdx, string queryString = "")
    {
        var url = $"/api/sessions/{sessionId}/profiler/chunks/{chunkIdx}/decoded{queryString}";
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-Session-Token", sessionId.ToString());
        var resp = await _client.SendAsync(req);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK), $"GET {url} returned {(int)resp.StatusCode}");
        Assert.That(resp.Content.Headers.ContentType!.MediaType, Is.EqualTo("application/json"));
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
    }

    [Test]
    public async Task DecodedChunk_ReturnsJsonProjection_WithCoreFields()
    {
        var session = await CreateTraceSessionAsync(tickCount: 5, instantsPerTick: 3);
        await WaitForBuildAsync(session.SessionId, TimeSpan.FromSeconds(5));

        using var doc = await FetchDecodedAsync(session.SessionId, 0);
        var root = doc.RootElement;

        Assert.That(root.GetProperty("fromTick").GetInt32(), Is.EqualTo(1));
        Assert.That(root.GetProperty("toTick").GetInt32(), Is.GreaterThan(1));
        Assert.That(root.GetProperty("isContinuation").GetBoolean(), Is.False);
        Assert.That(root.GetProperty("timestampFrequency").GetInt64(), Is.EqualTo(10_000_000));

        // Fixture writes 5 ticks × (TickStart + 3 Instant + TickEnd) = 25 records on disk, but the
        // generic Instant kind (=6) is intentionally ignored by RecordDecoder (no DTO mapping in
        // its switch arm). What surfaces is therefore 5 ticks × (TickStart + TickEnd) = 10 events.
        // The endpoint reports the decoder's output, not the on-disk record count.
        var totalEvents = root.GetProperty("eventCount").GetInt32();
        var filtered = root.GetProperty("filteredEventCount").GetInt32();
        Assert.That(totalEvents, Is.EqualTo(10));
        Assert.That(filtered, Is.EqualTo(10));

        var events = root.GetProperty("events");
        Assert.That(events.GetArrayLength(), Is.EqualTo(10));

        // First event must be TickStart (Kind=0) for tick 1.
        var first = events[0];
        Assert.That(first.GetProperty("kind").GetInt32(), Is.EqualTo(0));
        Assert.That(first.GetProperty("tickNumber").GetInt32(), Is.EqualTo(1));
    }

    [Test]
    public async Task DecodedChunk_KindsFilter_ReturnsMatchingEventsOnly()
    {
        var session = await CreateTraceSessionAsync(tickCount: 5, instantsPerTick: 3);
        await WaitForBuildAsync(session.SessionId, TimeSpan.FromSeconds(5));

        // Filter to TickStart (0) only → 5 events (one per tick). All decoded events are already
        // TickStart/TickEnd (Instants don't surface — see ReturnsJsonProjection_WithCoreFields).
        using var doc = await FetchDecodedAsync(session.SessionId, 0, "?kinds=0");
        var root = doc.RootElement;

        Assert.That(root.GetProperty("eventCount").GetInt32(), Is.EqualTo(10), "eventCount reports unfiltered total");
        Assert.That(root.GetProperty("filteredEventCount").GetInt32(), Is.EqualTo(5));

        var events = root.GetProperty("events");
        Assert.That(events.GetArrayLength(), Is.EqualTo(5));

        foreach (var ev in events.EnumerateArray())
        {
            Assert.That(ev.GetProperty("kind").GetInt32(), Is.EqualTo(0));
        }
    }

    [Test]
    public async Task DecodedChunk_TickFilter_ReturnsOnlyMatchingTick()
    {
        var session = await CreateTraceSessionAsync(tickCount: 5, instantsPerTick: 3);
        await WaitForBuildAsync(session.SessionId, TimeSpan.FromSeconds(5));

        // Tick 3 → TickStart + TickEnd surfaced (Instants dropped by decoder) = 2 events.
        using var doc = await FetchDecodedAsync(session.SessionId, 0, "?tick=3");
        var root = doc.RootElement;

        Assert.That(root.GetProperty("filteredEventCount").GetInt32(), Is.EqualTo(2));
        foreach (var ev in root.GetProperty("events").EnumerateArray())
        {
            Assert.That(ev.GetProperty("tickNumber").GetInt32(), Is.EqualTo(3));
        }
    }

    [Test]
    public async Task DecodedChunk_KindsAndTickFilters_Compose()
    {
        var session = await CreateTraceSessionAsync(tickCount: 5, instantsPerTick: 3);
        await WaitForBuildAsync(session.SessionId, TimeSpan.FromSeconds(5));

        // tick=4 ∧ kinds=1 (TickEnd) → exactly 1 event.
        using var doc = await FetchDecodedAsync(session.SessionId, 0, "?tick=4&kinds=1");
        var root = doc.RootElement;

        Assert.That(root.GetProperty("filteredEventCount").GetInt32(), Is.EqualTo(1));
        var ev = root.GetProperty("events")[0];
        Assert.That(ev.GetProperty("tickNumber").GetInt32(), Is.EqualTo(4));
        Assert.That(ev.GetProperty("kind").GetInt32(), Is.EqualTo(1));
    }

    [Test]
    public async Task DecodedChunk_MalformedKindsTokens_AreSkipped()
    {
        var session = await CreateTraceSessionAsync(tickCount: 5, instantsPerTick: 3);
        await WaitForBuildAsync(session.SessionId, TimeSpan.FromSeconds(5));

        // "0,not-a-number,1" → TickStart + TickEnd kept (10 events), "not-a-number" silently dropped.
        using var doc = await FetchDecodedAsync(session.SessionId, 0, "?kinds=0,not-a-number,1");
        Assert.That(doc.RootElement.GetProperty("filteredEventCount").GetInt32(), Is.EqualTo(10));
    }

    [Test]
    public async Task DecodedChunk_OutOfRangeIndex_Returns404()
    {
        var session = await CreateTraceSessionAsync();
        await WaitForBuildAsync(session.SessionId, TimeSpan.FromSeconds(5));

        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/sessions/{session.SessionId}/profiler/chunks/9999/decoded");
        req.Headers.Add("X-Session-Token", session.SessionId.ToString());
        var resp = await _client.SendAsync(req);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task DecodedChunk_MissingBootstrapToken_Returns401()
    {
        var session = await CreateTraceSessionAsync();
        await WaitForBuildAsync(session.SessionId, TimeSpan.FromSeconds(5));

        var unauthed = _factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/sessions/{session.SessionId}/profiler/chunks/0/decoded");
        req.Headers.Add("X-Session-Token", session.SessionId.ToString());
        var resp = await unauthed.SendAsync(req);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task DecodedChunk_MissingSessionToken_Returns401()
    {
        var session = await CreateTraceSessionAsync();
        await WaitForBuildAsync(session.SessionId, TimeSpan.FromSeconds(5));

        var resp = await _client.GetAsync($"/api/sessions/{session.SessionId}/profiler/chunks/0/decoded");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task DecodedChunk_MalformedKindsFilter_DoesNotCrash_SilentlyDropsBadTokens()
    {
        // The ?kinds= filter accepts CSV ints. Out-of-range / non-numeric tokens should be silently dropped
        // (current behaviour) — the alternative is a 400 which would break clients that mix valid+invalid kinds
        // when probing the API. Pin the silent-drop semantic so a future refactor can't change it without
        // everyone noticing. Mixed valid (10) + bogus (99999, NaN) — request must succeed and respect the
        // valid filter only.
        var session = await CreateTraceSessionAsync();
        await WaitForBuildAsync(session.SessionId, TimeSpan.FromSeconds(5));

        var req = new HttpRequestMessage(HttpMethod.Get,
            $"/api/sessions/{session.SessionId}/profiler/chunks/0/decoded?kinds=10,99999,not-a-number,-1");
        req.Headers.Add("X-Session-Token", session.SessionId.ToString());
        var resp = await _client.SendAsync(req);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK), "malformed kinds should not 4xx");

        // Body is valid JSON — the parsing succeeded even with bogus tokens.
        var body = await resp.Content.ReadAsStringAsync();
        Assert.That(body, Does.StartWith("{").Or.StartsWith("["));
    }
}
