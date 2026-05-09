using System;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using NUnit.Framework;
using Typhon.Workbench.Dtos.Sessions;
using Typhon.Workbench.Fixtures;

namespace Typhon.Workbench.Tests.Data;

/// <summary>
/// Integration tests for <see cref="Typhon.Workbench.Controllers.DataController"/>.
/// Covers the <c>X-Workbench-Api</c> version negotiation filter, track schema round-trip,
/// and input validation (unknown trackId, bad range, empty aggregate body).
/// </summary>
[TestFixture]
public sealed class DataControllerTests
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

    private async Task<SessionDto> CreateTraceSessionAsync(int tickCount = 3, int instantsPerTick = 2)
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
                Assert.Fail($"Unexpected status waiting for build: {resp.StatusCode}");
            await Task.Delay(25);
        }
        Assert.Fail("Trace cache build did not complete within the allotted timeout.");
    }

    private HttpRequestMessage DataReq(HttpMethod method, Guid sessionId, string path, string apiVersion = null)
    {
        var req = new HttpRequestMessage(method, $"/api/sessions/{sessionId}/{path}");
        req.Headers.Add("X-Session-Token", sessionId.ToString());
        if (apiVersion != null)
            req.Headers.Add("X-Workbench-Api", apiVersion);
        return req;
    }

    // ── X-Workbench-Api version negotiation ──

    [Test]
    public async Task Topology_MissingVersionHeader_SoftLaunchPassesThrough()
    {
        var session = await CreateTraceSessionAsync();

        // No X-Workbench-Api header: soft-launch defaults to v1. Expect 200 or 202 (build may be in flight),
        // but never 400 or 426 (those come from the version filter).
        var req = DataReq(HttpMethod.Get, session.SessionId, "topology"); // no version header
        var resp = await _client.SendAsync(req);
        Assert.That((int)resp.StatusCode, Is.AnyOf(200, 202),
            "missing X-Workbench-Api should soft-launch as v1, not reject");
    }

    [Test]
    public async Task Topology_VersionHeader1_PassesThrough()
    {
        var session = await CreateTraceSessionAsync();

        var req = DataReq(HttpMethod.Get, session.SessionId, "topology", apiVersion: "1");
        var resp = await _client.SendAsync(req);
        Assert.That((int)resp.StatusCode, Is.AnyOf(200, 202),
            "X-Workbench-Api: 1 is the supported version and should pass through");
    }

    [Test]
    public async Task Topology_VersionHeader2_Returns426()
    {
        var session = await CreateTraceSessionAsync();

        var req = DataReq(HttpMethod.Get, session.SessionId, "topology", apiVersion: "2");
        var resp = await _client.SendAsync(req);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.UpgradeRequired));

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var type = doc.RootElement.GetProperty("type").GetString();
        Assert.That(type, Is.EqualTo("api-version-mismatch"));
    }

    [Test]
    public async Task Topology_VersionHeaderNonInteger_Returns400()
    {
        var session = await CreateTraceSessionAsync();

        var req = DataReq(HttpMethod.Get, session.SessionId, "topology", apiVersion: "not-a-number");
        var resp = await _client.SendAsync(req);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var type = doc.RootElement.GetProperty("type").GetString();
        Assert.That(type, Is.EqualTo("invalid-api-version"));
    }

    // ── /tracks schema round-trip ──

    [Test]
    public async Task Tracks_ReturnsExpectedSchema_AfterBuildCompletes()
    {
        var session = await CreateTraceSessionAsync(tickCount: 4, instantsPerTick: 2);
        await WaitForBuildAsync(session.SessionId, TimeSpan.FromSeconds(5));

        var req = DataReq(HttpMethod.Get, session.SessionId, "tracks", apiVersion: "1");
        var resp = await _client.SendAsync(req);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var tracks = doc.RootElement.GetProperty("tracks");
        // v1 had 2 tracks; v2 (#311) added 3 track families: system/<name>, queue/<name>, posttick/<phase>.
        // v3 (#327) added 3 more for the Workbench Data Flow module: archetype/<label>, system-archetype/<sys>/<arch>, component-family/<name>.
        Assert.That(tracks.GetArrayLength(), Is.EqualTo(8), "v3 exposes 8 tracks (2 v1 + 3 v2 + 3 v3 family descriptors)");

        var ids = Enumerable.Range(0, tracks.GetArrayLength())
            .Select(i => tracks[i].GetProperty("id").GetString())
            .ToArray();
        Assert.That(ids, Is.EquivalentTo(new[]
        {
            "tick/summary", "metronome/wait",
            "system/<name>", "queue/<name>", "posttick/<phase>",
            "archetype/<label>", "system-archetype/<system>/<archetype>", "component-family/<family>",
        }));

        var tickSummary = tracks[0];
        Assert.That(tickSummary.GetProperty("id").GetString(), Is.EqualTo("tick/summary"));
        Assert.That(tickSummary.GetProperty("kind").GetString(), Is.EqualTo("perTick"));

        var fields = tickSummary.GetProperty("fields");
        var fieldNames = Enumerable.Range(0, fields.GetArrayLength())
            .Select(i => fields[i].GetProperty("name").GetString())
            .ToArray();

        Assert.That(fieldNames, Is.EquivalentTo(new[]
        {
            "tickNumber", "startUs", "durationUs", "eventCount",
            "maxSystemDurationUs", "overloadLevel", "tickMultiplier",
            "consecutiveOverrun", "consecutiveUnderrun",
        }), "tick/summary must expose all 9 spec-defined fields");

        var metronome = tracks[1];
        Assert.That(metronome.GetProperty("id").GetString(), Is.EqualTo("metronome/wait"));
        var mFields = metronome.GetProperty("fields");
        var mFieldNames = Enumerable.Range(0, mFields.GetArrayLength())
            .Select(i => mFields[i].GetProperty("name").GetString())
            .ToArray();
        Assert.That(mFieldNames, Is.EquivalentTo(new[] { "tickNumber", "waitUs", "intentClass" }),
            "metronome/wait must expose exactly 3 spec-defined fields");

        // ── v2 family descriptors (#311) ───────────────────────────────────
        var systemTrack = tracks[2];
        Assert.That(systemTrack.GetProperty("kind").GetString(), Is.EqualTo("perTickPerSystem"));
        Assert.That(EnumerateNames(systemTrack), Does.Contain("readyUs").And.Contain("workersTouched").And.Contain("skipReason"));

        var queueTrack = tracks[3];
        Assert.That(queueTrack.GetProperty("kind").GetString(), Is.EqualTo("perTickPerQueue"));
        Assert.That(EnumerateNames(queueTrack), Does.Contain("peakDepth").And.Contain("overflowCount"));

        var postTrack = tracks[4];
        Assert.That(postTrack.GetProperty("kind").GetString(), Is.EqualTo("perTick"));
        Assert.That(EnumerateNames(postTrack), Is.EquivalentTo(new[] { "tickNumber", "durationUs" }));

        static string[] EnumerateNames(JsonElement track)
        {
            var f = track.GetProperty("fields");
            var names = new string[f.GetArrayLength()];
            for (var i = 0; i < names.Length; i++) names[i] = f[i].GetProperty("name").GetString();
            return names;
        }
    }

    // ── /track/{trackId} input validation ──

    [Test]
    public async Task Track_UnknownTrackId_Returns400()
    {
        var session = await CreateTraceSessionAsync();

        var req = DataReq(HttpMethod.Get, session.SessionId, "track/bad/trackid", apiVersion: "1");
        var resp = await _client.SendAsync(req);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.That(doc.RootElement.GetProperty("title").GetString(), Is.EqualTo("unknown-track"));
    }

    [Test]
    public async Task Track_BadRange_Returns400()
    {
        var session = await CreateTraceSessionAsync();

        // from=10 > to=5 is invalid; the check fires before metadata resolution.
        var req = DataReq(HttpMethod.Get, session.SessionId, "track/tick/summary?from=10&to=5", apiVersion: "1");
        var resp = await _client.SendAsync(req);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.That(doc.RootElement.GetProperty("title").GetString(), Is.EqualTo("bad-range"));
    }

    [Test]
    public async Task Track_TickSummary_ReturnsRecordsAfterBuild()
    {
        var session = await CreateTraceSessionAsync(tickCount: 4, instantsPerTick: 2);
        await WaitForBuildAsync(session.SessionId, TimeSpan.FromSeconds(5));

        var req = DataReq(HttpMethod.Get, session.SessionId, "track/tick/summary", apiVersion: "1");
        var resp = await _client.SendAsync(req);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.That(doc.RootElement.GetProperty("trackId").GetString(), Is.EqualTo("tick/summary"));
        var records = doc.RootElement.GetProperty("records");
        Assert.That(records.GetArrayLength(), Is.EqualTo(4), "fixture has 4 ticks");

        // Each record must carry tickNumber.
        for (var i = 0; i < records.GetArrayLength(); i++)
        {
            Assert.That(records[i].TryGetProperty("tickNumber", out _), Is.True, $"record[{i}] missing tickNumber");
        }
    }

    // ── /aggregate input validation ──

    [Test]
    public async Task Aggregate_EmptyBody_Returns400()
    {
        var session = await CreateTraceSessionAsync();

        var req = DataReq(HttpMethod.Post, session.SessionId, "aggregate", apiVersion: "1");
        req.Content = new StringContent("{\"queries\":[]}", System.Text.Encoding.UTF8, "application/json");
        var resp = await _client.SendAsync(req);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Aggregate_ValidQuery_ReturnsResultsAfterBuild()
    {
        var session = await CreateTraceSessionAsync(tickCount: 4, instantsPerTick: 2);
        await WaitForBuildAsync(session.SessionId, TimeSpan.FromSeconds(5));

        var body = """
            {"queries":[{"trackId":"tick/summary","field":"durationUs","op":"count","range":[1,999]}]}
            """;
        var req = DataReq(HttpMethod.Post, session.SessionId, "aggregate", apiVersion: "1");
        req.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        var resp = await _client.SendAsync(req);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var results = doc.RootElement.GetProperty("results");
        Assert.That(results.GetArrayLength(), Is.EqualTo(1));
        var value = results[0].GetProperty("value").GetDouble();
        Assert.That(value, Is.EqualTo(4.0).Within(1e-9), "fixture has 4 ticks in range [1,999]");
    }
}
