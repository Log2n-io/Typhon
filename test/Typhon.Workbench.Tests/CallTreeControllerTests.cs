using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using NUnit.Framework;
using Typhon.Workbench.Dtos.Profiler;
using Typhon.Workbench.Dtos.Sessions;
using Typhon.Workbench.Fixtures;

namespace Typhon.Workbench.Tests;

/// <summary>
/// Integration coverage for the #351 Phase 4 Call Tree endpoints — <c>GET .../profiler/cpu-frames</c> and
/// <c>POST .../profiler/calltree</c>. Runs against real fixture traces (one carrying a <c>CpuSampleSection</c>, one
/// without) so a regression in trailer-section loading, frame resolution, or the fold surfaces immediately.
/// </summary>
[TestFixture]
public sealed class CallTreeControllerTests
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

    private async Task<SessionDto> CreateTraceSessionAsync(string tracePath)
    {
        var resp = await _client.PostAsJsonAsync("/api/sessions/trace", new CreateTraceSessionRequest(tracePath));
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

    private async Task<T> GetAsync<T>(Guid sessionId, string route)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/sessions/{sessionId}/profiler/{route}");
        req.Headers.Add("X-Session-Token", sessionId.ToString());
        var resp = await _client.SendAsync(req);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return JsonSerializer.Deserialize<T>(await resp.Content.ReadAsStringAsync(), Json)!;
    }

    private async Task<CallTreeResponseDto> PostCallTreeAsync(Guid sessionId, CallTreeRequestDto request)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/sessions/{sessionId}/profiler/calltree")
        {
            Content = JsonContent.Create(request),
        };
        req.Headers.Add("X-Session-Token", sessionId.ToString());
        var resp = await _client.SendAsync(req);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return JsonSerializer.Deserialize<CallTreeResponseDto>(await resp.Content.ReadAsStringAsync(), Json)!;
    }

    private async Task<SampleDensityDto> PostSampleDensityAsync(Guid sessionId, SampleDensityRequestDto request)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/sessions/{sessionId}/profiler/sample-density")
        {
            Content = JsonContent.Create(request),
        };
        req.Headers.Add("X-Session-Token", sessionId.ToString());
        var resp = await _client.SendAsync(req);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return JsonSerializer.Deserialize<SampleDensityDto>(await resp.Content.ReadAsStringAsync(), Json)!;
    }

    [Test]
    public async Task CpuFrames_ReturnsResolvedManifest_ForTraceWithCpuSamples()
    {
        var path = TraceFixtureBuilder.BuildTraceWithCpuSamples(_factory.DemoDirectory);
        var session = await CreateTraceSessionAsync(path);
        await WaitForBuildAsync(session.SessionId, TimeSpan.FromSeconds(5));

        var manifest = await GetAsync<CpuFrameManifestDto>(session.SessionId, "cpu-frames");

        Assert.That(manifest.Frames, Has.Length.EqualTo(3));
        var byId = manifest.Frames.ToDictionary(f => f.FrameId);
        Assert.That(byId[0].Method, Is.EqualTo("AntHill.MovementSystem.Execute"));
        Assert.That(byId[0].Line, Is.EqualTo(42));
        Assert.That(byId[0].File, Does.Contain("MovementSystem.cs"));
        // Frame 2 is the source-less BCL frame — line 0, no file.
        Assert.That(byId[2].Line, Is.EqualTo(0));

        var categoryNames = manifest.Categories.Select(c => c.Name).ToArray();
        Assert.That(categoryNames, Does.Contain("Ecs"));
        Assert.That(categoryNames, Does.Contain("Storage"));
        Assert.That(categoryNames, Does.Contain("BCL"));
    }

    [Test]
    public async Task CallTree_FoldsSamples_ForTraceWithCpuSamples()
    {
        var path = TraceFixtureBuilder.BuildTraceWithCpuSamples(_factory.DemoDirectory);
        var session = await CreateTraceSessionAsync(path);
        await WaitForBuildAsync(session.SessionId, TimeSpan.FromSeconds(5));

        var tree = await PostCallTreeAsync(session.SessionId, new CallTreeRequestDto(null, null, null, "wall-clock"));

        Assert.That(tree.TotalSamples, Is.EqualTo(3));
        Assert.That(tree.ManagedSamples, Is.EqualTo(2));
        Assert.That(tree.ExternalSamples, Is.EqualTo(1));
        // Nodes[0] is the synthetic root; it has one real root frame (MovementSystem.Execute), total 3.
        var root = tree.Nodes[0];
        Assert.That(root.FrameId, Is.EqualTo(-1));
        Assert.That(root.Children, Has.Length.EqualTo(1));
        var rootFrame = tree.Nodes[root.Children[0]];
        Assert.That(rootFrame.FrameId, Is.EqualTo(0));
        Assert.That(rootFrame.TotalSamples, Is.EqualTo(3));
        Assert.That(tree.CategoryBreakdown, Is.Not.Empty);
    }

    [Test]
    public async Task CallTree_OnCpuViewMode_DropsExternalSample()
    {
        var path = TraceFixtureBuilder.BuildTraceWithCpuSamples(_factory.DemoDirectory);
        var session = await CreateTraceSessionAsync(path);
        await WaitForBuildAsync(session.SessionId, TimeSpan.FromSeconds(5));

        var tree = await PostCallTreeAsync(session.SessionId, new CallTreeRequestDto(null, null, null, "on-cpu"));

        Assert.That(tree.TotalSamples, Is.EqualTo(2));
        Assert.That(tree.ExternalSamples, Is.EqualTo(0));
    }

    [Test]
    public async Task CpuFrames_And_CallTree_AreEmpty_ForTraceWithoutCpuSamples()
    {
        var path = TraceFixtureBuilder.BuildMinimalTrace(_factory.DemoDirectory, tickCount: 3, instantsPerTick: 2);
        var session = await CreateTraceSessionAsync(path);
        await WaitForBuildAsync(session.SessionId, TimeSpan.FromSeconds(5));

        var manifest = await GetAsync<CpuFrameManifestDto>(session.SessionId, "cpu-frames");
        Assert.That(manifest.Frames, Is.Empty);

        var tree = await PostCallTreeAsync(session.SessionId, new CallTreeRequestDto(null, null, null, "wall-clock"));
        Assert.That(tree.TotalSamples, Is.EqualTo(0));
        Assert.That(tree.Nodes, Has.Length.EqualTo(1));
        Assert.That(tree.Nodes[0].Children, Is.Empty);
    }

    [Test]
    public async Task CallTree_SpanKindScope_FoldsOnlySamplesInsideThatKindsWindows()
    {
        // SchedulerSystemArchetype is kind 245. The fixture's two span instances of that kind cover qpc [1000,1500)
        // and [3000,3500); the three CPU samples sit at qpc 1200 / 2000 / 3200, so the middle one is out of scope.
        var path = TraceFixtureBuilder.BuildTraceWithScopableCpuSamples(_factory.DemoDirectory);
        var session = await CreateTraceSessionAsync(path);
        await WaitForBuildAsync(session.SessionId, TimeSpan.FromSeconds(5));

        var tree = await PostCallTreeAsync(session.SessionId, new CallTreeRequestDto(null, null, null, "wall-clock", SpanKind: 245));

        Assert.That(tree.TotalSamples, Is.EqualTo(2));
    }

    [Test]
    public async Task CallTree_SpanKindScope_UnknownKind_ReturnsEmptyTree()
    {
        var path = TraceFixtureBuilder.BuildTraceWithScopableCpuSamples(_factory.DemoDirectory);
        var session = await CreateTraceSessionAsync(path);
        await WaitForBuildAsync(session.SessionId, TimeSpan.FromSeconds(5));

        // No span of kind 999 exists — the scope resolves to no windows and the fold yields an empty tree.
        var tree = await PostCallTreeAsync(session.SessionId, new CallTreeRequestDto(null, null, null, "wall-clock", SpanKind: 999));

        Assert.That(tree.TotalSamples, Is.EqualTo(0));
    }

    [Test]
    public async Task CallTree_SystemScope_EndpointAcceptsSystemIndexAxis()
    {
        // This fixture carries no folded SystemTickSummary rows, so a system scope resolves to no windows → empty tree.
        // The test proves the endpoint deserializes the systemIndex axis and routes through the scope resolver; the
        // window-resolution arithmetic for system / phase scopes is covered exhaustively by ScopeResolverTests.
        var path = TraceFixtureBuilder.BuildTraceWithScopableCpuSamples(_factory.DemoDirectory);
        var session = await CreateTraceSessionAsync(path);
        await WaitForBuildAsync(session.SessionId, TimeSpan.FromSeconds(5));

        var tree = await PostCallTreeAsync(session.SessionId, new CallTreeRequestDto(null, null, null, "wall-clock", SystemIndex: 0));

        Assert.That(tree.TotalSamples, Is.EqualTo(0));
        Assert.That(tree.Nodes, Has.Length.EqualTo(1)); // lone synthetic root
    }

    [Test]
    public async Task SampleDensity_BinsEveryInScopeSample()
    {
        var path = TraceFixtureBuilder.BuildTraceWithCpuSamples(_factory.DemoDirectory);
        var session = await CreateTraceSessionAsync(path);
        await WaitForBuildAsync(session.SessionId, TimeSpan.FromSeconds(5));

        var density = await PostSampleDensityAsync(
            session.SessionId,
            new SampleDensityRequestDto(new CallTreeRequestDto(null, null, null, "wall-clock"), 16));

        long total = 0;
        foreach (var bin in density.Bins)
        {
            total += bin.Count;
        }
        Assert.That(total, Is.EqualTo(3)); // the fixture's three CPU samples
    }

    [Test]
    public async Task SampleDensity_IsEmpty_ForTraceWithoutCpuSamples()
    {
        var path = TraceFixtureBuilder.BuildMinimalTrace(_factory.DemoDirectory, tickCount: 3, instantsPerTick: 2);
        var session = await CreateTraceSessionAsync(path);
        await WaitForBuildAsync(session.SessionId, TimeSpan.FromSeconds(5));

        var density = await PostSampleDensityAsync(
            session.SessionId,
            new SampleDensityRequestDto(new CallTreeRequestDto(null, null, null, "wall-clock"), 16));

        Assert.That(density.Bins, Is.Empty);
    }

    [Test]
    public async Task CallTree_DeepStack_SerializesWithoutDepthError()
    {
        // A deep call stack folds to a deep tree. The flat wire form must serialize cleanly through the real HTTP +
        // System.Text.Json path — a nested-object tree this deep exceeds STJ's MaxDepth and 500s the endpoint.
        const int depth = 50;
        var path = TraceFixtureBuilder.BuildTraceWithDeepCpuStack(_factory.DemoDirectory, depth);
        var session = await CreateTraceSessionAsync(path);
        await WaitForBuildAsync(session.SessionId, TimeSpan.FromSeconds(5));

        var tree = await PostCallTreeAsync(session.SessionId, new CallTreeRequestDto(null, null, null, "wall-clock"));

        Assert.That(tree.TotalSamples, Is.EqualTo(2));
        // Synthetic root + one node per stack frame, all in a single flat array.
        Assert.That(tree.Nodes, Has.Length.EqualTo(depth + 1));
    }
}
