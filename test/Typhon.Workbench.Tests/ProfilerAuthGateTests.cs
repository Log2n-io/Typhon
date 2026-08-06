using System;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using NUnit.Framework;
using Typhon.Workbench.Dtos.Sessions;
using Typhon.Workbench.Fixtures;
using Typhon.Workbench.Security;

namespace Typhon.Workbench.Tests;

/// <summary>
/// Security-gate tests for every profiler endpoint. The Workbench binds only to loopback, but a
/// malicious webpage in the user's browser can still <c>fetch('http://localhost:5200/...')</c> — so
/// every MVC endpoint requires both the process-scoped bootstrap token (defeats that attack) and a
/// session token bound to the URL's session id (defeats lateral movement between sessions).
///
/// These tests pin the contract: missing bootstrap token → 401; missing / wrong session token →
/// 401 / 403; correct both → 200/202. A regression here would silently open sensitive endpoints to
/// any JS running in the user's browser.
/// </summary>
[TestFixture]
public sealed class ProfilerAuthGateTests
{
    private WorkbenchFactory _factory;
    private HttpClient _authed;
    private HttpClient _unauthed;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    [SetUp]
    public void SetUp()
    {
        _factory = new WorkbenchFactory();
        _authed = _factory.CreateAuthenticatedClient();
        _unauthed = _factory.CreateClient(); // no bootstrap-token handler
    }

    [TearDown]
    public void TearDown() => _factory.Dispose();

    private async Task<SessionDto> CreateTraceSessionAsync()
    {
        var path = TraceFixtureBuilder.BuildMinimalTrace(_factory.DemoDirectory, tickCount: 3, instantsPerTick: 2);
        return await CaptureSessionFactory.OpenWithCaptureAsync(_authed, _factory.DemoDirectory, path);
    }

    private async Task WaitForBuildAsync(Guid sessionId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"/api/sessions/{sessionId}/profiler/metadata");
            req.Headers.Add("X-Session-Token", sessionId.ToString());
            var resp = await _authed.SendAsync(req);
            if (resp.StatusCode == HttpStatusCode.OK) return;
            await Task.Delay(25);
        }
        Assert.Fail("Build timeout");
    }

    // ── /profiler/metadata ──

    [Test]
    public async Task Metadata_MissingBootstrapToken_Returns401()
    {
        var session = await CreateTraceSessionAsync();
        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/sessions/{session.SessionId}/profiler/metadata");
        req.Headers.Add("X-Session-Token", session.SessionId.ToString());
        // Intentionally hitting _unauthed — no X-Workbench-Token.
        var resp = await _unauthed.SendAsync(req);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Metadata_MissingSessionToken_Returns401()
    {
        var session = await CreateTraceSessionAsync();
        var resp = await _authed.GetAsync($"/api/sessions/{session.SessionId}/profiler/metadata");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Metadata_WrongSessionToken_Returns403()
    {
        var sessionA = await CreateTraceSessionAsync();
        var sessionB = await CreateTraceSessionAsync();

        // Session A's token used against session B's URL — the RequireSession gate's sessionId
        // route-match check blocks it with 403.
        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/sessions/{sessionB.SessionId}/profiler/metadata");
        req.Headers.Add("X-Session-Token", sessionA.SessionId.ToString());
        var resp = await _authed.SendAsync(req);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    // ── /profiler/chunks/{chunkIdx} ──

    [Test]
    public async Task Chunks_MissingBootstrapToken_Returns401()
    {
        var session = await CreateTraceSessionAsync();
        await WaitForBuildAsync(session.SessionId, TimeSpan.FromSeconds(5));

        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/sessions/{session.SessionId}/profiler/chunks/0");
        req.Headers.Add("X-Session-Token", session.SessionId.ToString());
        var resp = await _unauthed.SendAsync(req);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Chunks_MissingSessionToken_Returns401()
    {
        var session = await CreateTraceSessionAsync();
        await WaitForBuildAsync(session.SessionId, TimeSpan.FromSeconds(5));

        var resp = await _authed.GetAsync($"/api/sessions/{session.SessionId}/profiler/chunks/0");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Chunks_WrongSessionToken_Returns403()
    {
        var sessionA = await CreateTraceSessionAsync();
        var sessionB = await CreateTraceSessionAsync();
        await WaitForBuildAsync(sessionB.SessionId, TimeSpan.FromSeconds(5));

        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/sessions/{sessionB.SessionId}/profiler/chunks/0");
        req.Headers.Add("X-Session-Token", sessionA.SessionId.ToString());
        var resp = await _authed.SendAsync(req);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    // ── /profiler/build-progress (SSE) ──
    // SSE endpoints are intentionally NOT bootstrap-token gated (EventSource can't attach custom
    // headers). They're gated by the URL sessionId alone, which is a 128-bit random value only
    // obtainable via an already-gated POST /api/sessions/*. Here we verify the session-token
    // gating path still works for direct HTTP clients.

    [Test]
    public async Task BuildProgress_UnknownSession_Returns401()
    {
        // An unknown session id in the URL → the SSE handler's dual-source auth (header token or
        // URL session id) both fail and it returns 401 before any stream is opened. This is the
        // contract for a stranger guessing a session id: no stream body, no information leak.
        var unknown = Guid.NewGuid();
        var resp = await _authed.GetAsync($"/api/sessions/{unknown}/profiler/build-progress");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    // ── Header constant alignment ──

    [Test]
    public void BootstrapTokenHeader_MatchesBootstrapTokenGateContract()
    {
        Assert.That(BootstrapTokenGate.HeaderName, Is.EqualTo("X-Workbench-Token"),
            "changing this header name requires updating the Vite proxy config and the Playwright helpers too");
    }
}
