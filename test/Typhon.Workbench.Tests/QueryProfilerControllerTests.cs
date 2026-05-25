using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Workbench.Dtos.Sessions;
using Typhon.Workbench.Fixtures;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Tests;

/// <summary>
/// Integration coverage for the #337 (P4) Query Catalog endpoints on
/// <see cref="Typhon.Workbench.Controllers.ProfilerController"/>:
/// <c>GET /queries</c>, <c>GET /queries/{kind}/{localId}</c>,
/// <c>GET /queries/{kind}/{localId}/executions</c>, and <c>GET /executions/{spanId}</c>.
/// The minimal-fixture tests exercise the empty-catalog path + endpoint plumbing; the
/// <c>with-queries</c> fixture tests (#376 Stage-3 4A) drive the populated path end-to-end —
/// fixture file → TraceFileReader → QueryCatalogBuilder → endpoint — proving the fixture is faithful.
/// </summary>
[TestFixture]
public sealed class QueryProfilerControllerTests
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
            if (resp.StatusCode != HttpStatusCode.Accepted)
            {
                Assert.Fail($"Unexpected status while waiting for build: {(int)resp.StatusCode}");
            }
            await Task.Delay(25);
        }
        Assert.Fail("Trace cache build did not complete within timeout.");
    }

    private async Task<HttpResponseMessage> SendAsync(Guid sessionId, string path)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/sessions/{sessionId}/profiler/{path}");
        req.Headers.Add("X-Session-Token", sessionId.ToString());
        return await _client.SendAsync(req);
    }

    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task GetQueries_ReturnsEmpty_ForTraceWithoutQueryData()
    {
        var session = await CreateTraceSessionAsync();
        await WaitForBuildAsync(session.SessionId, TimeSpan.FromSeconds(5));

        var resp = await SendAsync(session.SessionId, "queries");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.That(doc.RootElement.ValueKind, Is.EqualTo(JsonValueKind.Array));
        Assert.That(doc.RootElement.GetArrayLength(), Is.EqualTo(0),
            "minimal fixture has no query events — catalog must be empty");
    }

    [Test]
    public async Task GetQuery_ReturnsNotFound_WhenDefinitionDoesNotExist()
    {
        var session = await CreateTraceSessionAsync();
        await WaitForBuildAsync(session.SessionId, TimeSpan.FromSeconds(5));

        var resp = await SendAsync(session.SessionId, "queries/0/99999");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task GetQueryExecutions_ReturnsEmpty_WhenNoMatchingExecutions()
    {
        var session = await CreateTraceSessionAsync();
        await WaitForBuildAsync(session.SessionId, TimeSpan.FromSeconds(5));

        var resp = await SendAsync(session.SessionId, "queries/0/1/executions");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.That(doc.RootElement.ValueKind, Is.EqualTo(JsonValueKind.Array));
        Assert.That(doc.RootElement.GetArrayLength(), Is.EqualTo(0));
    }

    [Test]
    public async Task GetQueryExecutions_FilterQueryStringParams_AreAccepted()
    {
        // Smoke-test that the filter query params parse correctly — endpoint must still return 200
        // for valid filter values even when no executions match.
        var session = await CreateTraceSessionAsync();
        await WaitForBuildAsync(session.SessionId, TimeSpan.FromSeconds(5));

        var resp = await SendAsync(session.SessionId, "queries/0/1/executions?from=1&to=100&system=5&pageOffset=0&pageSize=50");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task GetExecution_ReturnsNotFound_ForUnknownSpanId()
    {
        var session = await CreateTraceSessionAsync();
        await WaitForBuildAsync(session.SessionId, TimeSpan.FromSeconds(5));

        var resp = await SendAsync(session.SessionId, "executions/1234567890");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task GetQuery_BadRequest_WhenKindIsOutOfRange()
    {
        var session = await CreateTraceSessionAsync();
        await WaitForBuildAsync(session.SessionId, TimeSpan.FromSeconds(5));

        // Kind > 255 → BadRequest. Route binds kind as int so it accepts the value, then the
        // controller validates the byte range.
        var resp = await SendAsync(session.SessionId, "queries/999/1");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task QueryCatalogEndpoints_WhileTraceBuildInProgress_Return202_Accepted()
    {
        // Mirrors SchemaController's IsSchemaBuilding flow: a trace session whose static state isn't ready yet must
        // surface 202 Accepted (with a Retry-After hint) from every query-catalog endpoint so the SPA hooks poll
        // quietly via refetchInterval instead of logging "Query catalog is only available …" on every panel mount.
        // Uses a fake ISession with `IsSchemaBuilding=true` and `StaticSchemaProvider=null`, identical to the
        // schema-controller test pattern, so we exercise the 202 branch without racing the real cache builder.
        var manager = _factory.Services.GetRequiredService<SessionManager>();
        var fakeId = Guid.NewGuid();
        manager.Create(new FakeBuildingSession(fakeId));

        foreach (var route in new[]
                 {
                     "queries",
                     "queries/0/1",
                     "queries/0/1/executions",
                     "executions/12345",
                     "executions/by-parent/12345",
                     "executions/by-system-tick/0/1",
                 })
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"/api/sessions/{fakeId}/profiler/{route}");
            req.Headers.Add("X-Session-Token", fakeId.ToString());
            var resp = await _client.SendAsync(req);
            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Accepted), $"route={route}");
            Assert.That(resp.Headers.Contains("Retry-After"), Is.True, $"route={route} missing Retry-After");
        }
    }

    // ── with-queries fixture (#376 Stage-3 4A) — the populated catalog path ──

    private async Task<SessionDto> CreateQueryTraceSessionAsync()
    {
        var path = TraceFixtureBuilder.BuildTraceWithQueries(_factory.DemoDirectory);
        var resp = await _client.PostAsJsonAsync("/api/sessions/trace", new CreateTraceSessionRequest(path));
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<SessionDto>(await resp.Content.ReadAsStringAsync(), Json)!;
    }

    [Test]
    public async Task GetQueries_WithQueriesFixture_ReturnsRankedCatalogWithResolvedStats()
    {
        var session = await CreateQueryTraceSessionAsync();
        await WaitForBuildAsync(session.SessionId, TimeSpan.FromSeconds(5));

        var resp = await SendAsync(session.SessionId, "queries");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var defs = doc.RootElement;
        Assert.That(defs.GetArrayLength(), Is.EqualTo(2), "two View definitions in the fixture");

        JsonElement def1 = default, def2 = default;
        foreach (var d in defs.EnumerateArray())
        {
            var localId = d.GetProperty("instanceId").GetProperty("localId").GetInt64();
            if (localId == 1) def1 = d;
            else if (localId == 2) def2 = d;
        }
        Assert.That(def1.ValueKind, Is.EqualTo(JsonValueKind.Object), "definition localId=1 present");
        Assert.That(def2.ValueKind, Is.EqualTo(JsonValueKind.Object), "definition localId=2 present");

        static long Total(JsonElement d) => d.GetProperty("aggregate").GetProperty("totalWallNs").GetInt64();
        Assert.That(Total(def1), Is.GreaterThan(Total(def2)), "def #1 (140 µs) outranks def #2 (60 µs) by TotalWallNs");

        var agg1 = def1.GetProperty("aggregate");
        Assert.That(agg1.GetProperty("executionCount").GetInt64(), Is.EqualTo(2), "def #1 has two executions");
        Assert.That(agg1.GetProperty("p95WallNs").GetInt64(), Is.GreaterThan(0), "percentiles computed");
        Assert.That(agg1.GetProperty("avgSelectivity").GetDouble(), Is.GreaterThan(0.0), "selectivity from Iterate/Count rows");

        var owners = def1.GetProperty("ownerSystemIds");
        Assert.That(owners.GetArrayLength(), Is.GreaterThan(0), "owning system attributed from OwnerSystemIdx");
        Assert.That(owners[0].GetInt32(), Is.EqualTo(0), "the seeded Movement system (index 0)");

        var src = def1.GetProperty("userSource");
        Assert.That(src.GetProperty("file").GetString(), Is.Not.Empty, "UserSource resolved from QuerySourceStringTable");
        Assert.That(src.GetProperty("method").GetString(), Is.Not.Empty);
    }

    [Test]
    public async Task GetQueryExecutions_WithQueriesFixture_ReturnsExecutionsWithPhaseBreakdown()
    {
        var session = await CreateQueryTraceSessionAsync();
        await WaitForBuildAsync(session.SessionId, TimeSpan.FromSeconds(5));

        var resp = await SendAsync(session.SessionId, "queries/0/1/executions");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var execs = doc.RootElement;
        Assert.That(execs.GetArrayLength(), Is.EqualTo(2), "def #1 has two executions");

        var phases = execs[0].GetProperty("phases");
        Assert.That(phases.GetArrayLength(), Is.GreaterThanOrEqualTo(4), "Parse/Iterate/Filter/Count phases present");
        var anyWall = false;
        foreach (var p in phases.EnumerateArray())
        {
            if (p.GetProperty("wallNs").GetInt64() > 0) { anyWall = true; break; }
        }
        Assert.That(anyWall, Is.True, "phase breakdown carries wall time");
    }

    /// <summary>
    /// Test fake — mirrors a trace session whose background cache build is still in flight. Reports
    /// <c>IsSchemaBuilding=true</c> so the controller's <c>CatalogNotReadyResponse</c> returns 202 (not 409).
    /// </summary>
    private sealed record FakeBuildingSession(Guid Id) : ISession
    {
        public SessionKind Kind => SessionKind.Trace;
        public SessionState State => SessionState.Trace;
        public string FilePath => string.Empty;
        public Typhon.Workbench.Schema.IStaticSchemaProvider StaticSchemaProvider => null;
        public bool IsSchemaBuilding => true;
    }
}
