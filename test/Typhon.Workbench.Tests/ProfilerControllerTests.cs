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
/// Integration coverage for <see cref="Typhon.Workbench.Controllers.ProfilerController"/> — the
/// only HTTP surface the Workbench SPA hits for trace-session data. Verifies the metadata + chunk
/// round-trip against a real fixture trace so a regression in header projection, payload framing,
/// or pooled-buffer lifecycle surfaces immediately.
/// </summary>
[TestFixture]
public sealed class ProfilerControllerTests
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
            if (resp.StatusCode == HttpStatusCode.OK)
            {
                return;
            }
            if (resp.StatusCode != HttpStatusCode.Accepted)
            {
                Assert.Fail($"Unexpected status while waiting for build: {(int)resp.StatusCode} {resp.StatusCode}");
            }
            await Task.Delay(25);
        }
        Assert.Fail("Trace cache build did not complete within the allotted timeout.");
    }

    [Test]
    public async Task Metadata_ReturnsProjectedHeader_AfterBuildCompletes()
    {
        var session = await CreateTraceSessionAsync(tickCount: 4, instantsPerTick: 2);
        await WaitForBuildAsync(session.SessionId, TimeSpan.FromSeconds(5));

        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/sessions/{session.SessionId}/profiler/metadata");
        req.Headers.Add("X-Session-Token", session.SessionId.ToString());
        var resp = await _client.SendAsync(req);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        // Header fields come from the fixture's DefaultHeader — TimestampFrequency = 10 MHz.
        Assert.That(root.GetProperty("header").GetProperty("timestampFrequency").GetInt64(), Is.EqualTo(10_000_000));
        // Fixture emits 4 ticks.
        Assert.That(root.GetProperty("globalMetrics").GetProperty("totalTicks").GetInt32(), Is.EqualTo(4));
    }

    [Test]
    public async Task Chunks_ReturnOctetStream_WithAllDocumentedHeaders()
    {
        var session = await CreateTraceSessionAsync(tickCount: 5, instantsPerTick: 3);
        await WaitForBuildAsync(session.SessionId, TimeSpan.FromSeconds(5));

        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/sessions/{session.SessionId}/profiler/chunks/0");
        req.Headers.Add("X-Session-Token", session.SessionId.ToString());
        var resp = await _client.SendAsync(req);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK), "chunk 0 must exist for a 5-tick fixture");

        Assert.That(resp.Content.Headers.ContentType!.MediaType, Is.EqualTo("application/octet-stream"));

        // Every documented X-Chunk-* header must be present. The browser worker parses these
        // before touching the body, so a missing header would silently break tick display.
        AssertHeaderPresent(resp, "X-Chunk-From-Tick");
        AssertHeaderPresent(resp, "X-Chunk-To-Tick");
        AssertHeaderPresent(resp, "X-Chunk-Event-Count");
        AssertHeaderPresent(resp, "X-Chunk-Uncompressed-Bytes");
        AssertHeaderPresent(resp, "X-Chunk-Is-Continuation");
        AssertHeaderPresent(resp, "X-Timestamp-Frequency");

        var expose = string.Join(',', resp.Headers.GetValues("Access-Control-Expose-Headers"));
        foreach (var h in new[] { "X-Chunk-From-Tick", "X-Chunk-To-Tick", "X-Chunk-Event-Count",
                                  "X-Chunk-Uncompressed-Bytes", "X-Chunk-Is-Continuation", "X-Timestamp-Frequency" })
        {
            Assert.That(expose, Does.Contain(h),
                $"Access-Control-Expose-Headers must include {h} for cross-origin JS to read it");
        }

        // First chunk starts at tick 1, covers all 5 fixture ticks (fixture produces one chunk).
        var fromTick = int.Parse(resp.Headers.GetValues("X-Chunk-From-Tick").First());
        var toTick = int.Parse(resp.Headers.GetValues("X-Chunk-To-Tick").First());
        Assert.That(fromTick, Is.EqualTo(1));
        Assert.That(toTick, Is.GreaterThan(fromTick));

        // Event count matches the fixture layout: 5 ticks × (TickStart + 3 Instant + TickEnd) = 25 records.
        Assert.That(int.Parse(resp.Headers.GetValues("X-Chunk-Event-Count").First()), Is.EqualTo(5 * (1 + 3 + 1)));

        // Body is non-empty and length matches ContentLength.
        var body = await resp.Content.ReadAsByteArrayAsync();
        Assert.That(body.Length, Is.EqualTo(resp.Content.Headers.ContentLength ?? 0));
        Assert.That(body.Length, Is.GreaterThan(0));
    }

    [Test]
    public async Task TraceStatus_ReportsNewVersion_AfterSourceFileOverwritten()
    {
        var session = await CreateTraceSessionAsync(tickCount: 3, instantsPerTick: 2);
        await WaitForBuildAsync(session.SessionId, TimeSpan.FromSeconds(5));

        // Freshly built session, source untouched — endpoint must report no new version.
        Assert.That(await GetNewVersionAvailableAsync(session.SessionId), Is.False,
            "an untouched source file must report newVersionAvailable=false");

        // Simulate a profiling re-run overwriting the same .typhon-trace in place.
        var replacement = TraceFixtureBuilder.BuildMinimalTrace(_factory.DemoDirectory, tickCount: 6, instantsPerTick: 4);
        File.Copy(replacement, session.FilePath!, overwrite: true);

        // Poll the endpoint through the 1 s server-side debounce + fingerprint re-compute.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        var detected = false;
        while (DateTime.UtcNow < deadline)
        {
            if (await GetNewVersionAvailableAsync(session.SessionId))
            {
                detected = true;
                break;
            }
            await Task.Delay(100);
        }
        Assert.That(detected, Is.True,
            "overwriting the source .typhon-trace must surface via trace-status within the debounce window");
    }

    private async Task<bool> GetNewVersionAvailableAsync(Guid sessionId)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/sessions/{sessionId}/profiler/trace-status");
        req.Headers.Add("X-Session-Token", sessionId.ToString());
        var resp = await _client.SendAsync(req);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("newVersionAvailable").GetBoolean();
    }

    [Test]
    public async Task Chunks_OutOfRangeIndex_Returns404()
    {
        var session = await CreateTraceSessionAsync();
        await WaitForBuildAsync(session.SessionId, TimeSpan.FromSeconds(5));

        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/sessions/{session.SessionId}/profiler/chunks/9999");
        req.Headers.Add("X-Session-Token", session.SessionId.ToString());
        var resp = await _client.SendAsync(req);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    private static void AssertHeaderPresent(HttpResponseMessage resp, string name)
    {
        Assert.That(resp.Headers.Contains(name) || resp.Content.Headers.Contains(name), Is.True,
            $"Expected header {name} on chunk response");
    }
}
