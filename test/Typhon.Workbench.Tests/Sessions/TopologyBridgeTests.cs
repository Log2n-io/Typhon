using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NUnit.Framework;
using Typhon.Workbench.Dtos.Profiler;
using Typhon.Workbench.Dtos.Sessions;
using Typhon.Workbench.Fixtures;

namespace Typhon.Workbench.Tests.Sessions;

/// <summary>
/// Feature #618 (F5) — the system dimension reaches a database (design §4.1).
/// </summary>
/// <remarks>
/// A database on disk has no systems of its own: <c>LiveSchemaProvider.GetSystemRelationships</c> returns an empty set because the Workbench hosts no runtime.
/// The capture has the complete access-declaration set, so attaching one is what gives the database its system dimension — and these routes are where that
/// crosses over. #617 rewired the profiler routes to ask "does this session have a capture?"; the topology routes live on a different controller and were left
/// behind, which is what these cover.
/// </remarks>
[TestFixture]
public sealed class TopologyBridgeTests
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
    public void TearDown()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    [Test]
    public async Task ABareOpenDatabase_HasNoTopology_AndTheReasonNamesTheMissingCapture()
    {
        var session = await OpenDemoAsync();

        var resp = await SendAsync(HttpMethod.Get, $"/api/sessions/{session.SessionId}/topology", session.SessionId);

        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        var body = await resp.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("Attach one"),
            "the old message named the session kind, which became wrong advice the moment a capture could be attached to a database");
    }

    [Test]
    public async Task AttachingACapture_GivesTheDatabaseItsSystemDimension()
    {
        var session = await OpenDemoAsync();
        var capture = await AttachAccessDeclarationTraceAsync(session);

        var resp = await SendAsync(HttpMethod.Get, $"/api/sessions/{session.SessionId}/topology", session.SessionId);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK), $"attached {capture}");

        var topology = JsonSerializer.Deserialize<TopologyProbe>(await resp.Content.ReadAsStringAsync(), Json);
        Assert.That(topology.Systems.Select(s => s.Name), Is.EquivalentTo(new[] { "Movement", "Damage" }),
            "these come from the capture — the database itself has no notion of a system");
    }

    [Test]
    public async Task WhoWrites_AnswersForADatabaseWithACaptureAttached()
    {
        var session = await OpenDemoAsync();
        await AttachAccessDeclarationTraceAsync(session);

        var resp = await SendAsync(HttpMethod.Get, $"/api/sessions/{session.SessionId}/queries/who-writes/Game.Position", session.SessionId);

        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var list = JsonSerializer.Deserialize<SystemListProbe>(await resp.Content.ReadAsStringAsync(), Json);
        Assert.That(list.Systems.Select(s => s.Name), Is.EqualTo(new[] { "Movement" }));
    }

    [Test]
    public async Task WhoReads_SeesSnapshotReadersToo()
    {
        var session = await OpenDemoAsync();
        await AttachAccessDeclarationTraceAsync(session);

        var resp = await SendAsync(HttpMethod.Get, $"/api/sessions/{session.SessionId}/queries/who-reads/Game.Position", session.SessionId);

        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var list = JsonSerializer.Deserialize<SystemListProbe>(await resp.Content.ReadAsStringAsync(), Json);
        Assert.That(list.Systems.Select(s => s.Name), Is.EqualTo(new[] { "Damage" }),
            "Damage reads Position as a snapshot — a read is a read whichever of the four buckets declares it");
    }

    [Test]
    public async Task WhileTheAttachedCaptureIsStillBuilding_NotReadyIs202_Not409()
    {
        // The capability flips the instant a profile is attached, so the client starts asking before the sidecar cache
        // exists — every attach passes through this window. 409 says "this will never work"; 202 says "poll me". Before
        // #618 an OpenSession inherited IsSchemaBuilding => false and answered the permanent one.
        var session = await OpenDemoAsync();
        var profilings = TraceLocation.ProfilingsDirectoryOf(session.FilePath);
        var built = TraceFixtureBuilder.BuildTraceWithAccessDeclarations(profilings);

        var attach = await SendAsync(HttpMethod.Post, $"/api/sessions/{session.SessionId}/profile", session.SessionId,
            new AttachProfileRequest(Path.GetFileName(built)));
        attach.EnsureSuccessStatusCode();

        // Race the build deliberately: whichever side wins, the answer must never be 409.
        var resp = await SendAsync(HttpMethod.Get, $"/api/sessions/{session.SessionId}/profiler/queries", session.SessionId);
        Assert.That(resp.StatusCode, Is.AnyOf(HttpStatusCode.OK, HttpStatusCode.Accepted),
            "a transient build window must not be reported as a permanent conflict");
    }

    [Test]
    public async Task DetachingTheCapture_TakesTheSystemDimensionWithIt()
    {
        var session = await OpenDemoAsync();
        var attached = await AttachAccessDeclarationTraceAsync(session);

        var detach = await SendAsync(HttpMethod.Delete, $"/api/sessions/{session.SessionId}/profile/{attached.ProfileId}", session.SessionId);
        Assert.That(detach.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

        var resp = await SendAsync(HttpMethod.Get, $"/api/sessions/{session.SessionId}/topology", session.SessionId);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Conflict),
            "the bridge follows the capture — a stale system list outliving the profile that justified it is exactly the silent-wrongness §5.7 forbids");
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Copies the RFC-07 access-declaration fixture into the session database's <c>profilings/</c>, attaches it, and waits for the sidecar cache to finish
    /// building — the topology routes 202 until it does, so asserting on content without this would be a race.
    /// </summary>
    private async Task<ProfileDto> AttachAccessDeclarationTraceAsync(SessionDto session)
    {
        var profilings = TraceLocation.ProfilingsDirectoryOf(session.FilePath);
        var built = TraceFixtureBuilder.BuildTraceWithAccessDeclarations(profilings);
        var resp = await SendAsync(HttpMethod.Post, $"/api/sessions/{session.SessionId}/profile", session.SessionId,
            new AttachProfileRequest(Path.GetFileName(built)));
        resp.EnsureSuccessStatusCode();
        var profile = JsonSerializer.Deserialize<ProfileDto>(await resp.Content.ReadAsStringAsync(), Json);

        await WaitForBuildAsync(session.SessionId, TimeSpan.FromSeconds(5));
        return profile;
    }

    /// <summary>
    /// Polls <c>/profiler/metadata</c> until the attached capture's cache build lands. That route answers for an open database with a profile because of #617,
    /// so the readiness signal the F5 routes need already exists — no second mechanism.
    /// </summary>
    private async Task WaitForBuildAsync(Guid sessionId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var resp = await SendAsync(HttpMethod.Get, $"/api/sessions/{sessionId}/profiler/metadata", sessionId);
            if (resp.StatusCode == HttpStatusCode.OK)
            {
                return;
            }
            if (resp.StatusCode != HttpStatusCode.Accepted)
            {
                Assert.Fail($"Unexpected status while waiting for the capture's cache build: {(int)resp.StatusCode} {resp.StatusCode}");
            }
            await Task.Delay(25);
        }
        Assert.Fail("The attached capture's cache build did not complete within the allotted timeout.");
    }

    private async Task<SessionDto> OpenDemoAsync()
    {
        var resp = await _client.PostAsJsonAsync("/api/sessions/file", new { filePath = "demo.typhon" });
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<SessionDto>(await resp.Content.ReadAsStringAsync(), Json);
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, Guid sessionId, object body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-Session-Token", sessionId.ToString());
        if (body != null)
        {
            request.Content = JsonContent.Create(body);
        }
        return _client.SendAsync(request);
    }

    /// <summary>Minimal shapes for the two responses — the full DTOs carry far more than these assertions need.</summary>
    private sealed record SystemProbe(string Name);

    private sealed record TopologyProbe(SystemProbe[] Systems);

    private sealed record SystemListProbe(string Component, SystemProbe[] Systems);
}
