using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using NUnit.Framework;
using Typhon.Workbench.Dtos.Sessions;
using Typhon.Workbench.Fixtures;

namespace Typhon.Workbench.Tests;

/// <summary>
/// Integration coverage for the #337 (P4) Query Catalog endpoints on
/// <see cref="Typhon.Workbench.Controllers.ProfilerController"/>:
/// <c>GET /queries</c>, <c>GET /queries/{kind}/{localId}</c>,
/// <c>GET /queries/{kind}/{localId}/executions</c>, and <c>GET /executions/{spanId}</c>.
/// Uses the minimal fixture trace (no query events) — exercises the empty-catalog path + endpoint
/// plumbing. Richer fixtures with synthetic query events are deferred until a fixture-builder
/// extension lands (P5+).
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
}
