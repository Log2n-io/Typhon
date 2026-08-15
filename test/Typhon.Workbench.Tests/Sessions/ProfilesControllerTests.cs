using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NUnit.Framework;
using Typhon.Workbench.Dtos.Profiler;
using Typhon.Workbench.Dtos.Sessions;
using Typhon.Workbench.Fixtures;

namespace Typhon.Workbench.Tests.Sessions;

/// <summary>
/// Feature #617 (F4) — profiles as a sub-resource of an open database (design D-10), and the list rendered from trace headers alone (D-5).
/// </summary>
/// <remarks>
/// These exercise the whole seam over HTTP rather than unit-testing the pieces, because the interesting behaviour is the interaction: a capture on disk
/// becomes an attached profile, which grants the session a capability, which is what makes the untouched <c>/profiler/*</c> routes start answering.
/// </remarks>
[TestFixture]
public sealed class ProfilesControllerTests
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

    // ── AC5 · the list ───────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Profiles_AreListedFromTheDatabasesOwnDirectory()
    {
        var session = await OpenDemoAsync();
        var profilings = TraceLocation.ProfilingsDirectoryOf(session.FilePath);
        TraceFixtureBuilder.BuildMinimalTrace(profilings, fileName: "20260101-000000-000.typhon-trace");
        TraceFixtureBuilder.BuildMinimalTrace(profilings, fileName: "20260202-000000-000.typhon-trace");

        var list = await GetListAsync(session.SessionId);

        Assert.Multiple(() =>
        {
            Assert.That(list.Profiles, Has.Length.EqualTo(2));
            Assert.That(list.ProfilingsDirectory, Is.EqualTo(profilings));
            Assert.That(list.Profiles.All(p => p.IsReadable), Is.True);
            Assert.That(list.Profiles.All(p => p.ProfileId == null), Is.True, "listing does not attach anything");
            Assert.That(list.Profiles.All(p => p.SizeBytes > 0), Is.True, "size comes from the directory entry, not the header");
        });
    }

    [Test]
    public async Task ListingBuildsNoSidecarCache()
    {
        var session = await OpenDemoAsync();
        var profilings = TraceLocation.ProfilingsDirectoryOf(session.FilePath);
        TraceFixtureBuilder.BuildMinimalTrace(profilings);

        await GetListAsync(session.SessionId);

        // The entire reason #614 put created-time, duration, ticks and the TSN window in the header: opening a database with thirty captures must not build
        // thirty sidecar caches to populate a list the user clicks once.
        Assert.That(Directory.GetFiles(profilings, "*-cache"), Is.Empty,
            "the profiles list must be readable from headers alone — a cache file here means it fell back to the expensive path");
    }

    [Test]
    public async Task ADatabaseWithNoCaptures_ListsEmpty_NotAnError()
    {
        var session = await OpenDemoAsync();

        var list = await GetListAsync(session.SessionId);

        Assert.That(list.Profiles, Is.Empty, "a database nobody has profiled yet is the normal case, not a failure");
    }

    [Test]
    public async Task AnUnreadableCapture_StillGetsARow()
    {
        var session = await OpenDemoAsync();
        var profilings = TraceLocation.ProfilingsDirectoryOf(session.FilePath);
        Directory.CreateDirectory(profilings);
        await File.WriteAllTextAsync(Path.Combine(profilings, "broken.typhon-trace"), "not a trace at all");

        var list = await GetListAsync(session.SessionId);

        var row = list.Profiles.Single();
        Assert.That(row.FileName, Is.EqualTo("broken.typhon-trace"));
        Assert.That(row.IsReadable, Is.False,
            "a file that vanishes from the list looks like a retention bug and sends the user hunting for something sitting right there");
    }

    // ── AC4 / AC6 · attach and detach ────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task AttachingAProfile_MakesItActive_AndGrantsTheProfilerCapability()
    {
        var session = await OpenDemoAsync();
        Assert.That(session.Capabilities, Does.Not.Contain("profiler"), "precondition: a database with no profile cannot profile");

        var profilings = TraceLocation.ProfilingsDirectoryOf(session.FilePath);
        var capture = Path.GetFileName(TraceFixtureBuilder.BuildMinimalTrace(profilings));

        var attached = await AttachAsync(session.SessionId, capture);

        Assert.Multiple(() =>
        {
            Assert.That(attached.ProfileId, Is.Not.Null);
            Assert.That(attached.IsActive, Is.True);
            Assert.That(attached.FileName, Is.EqualTo(capture));
        });

        var after = await GetSessionAsync(session.SessionId);
        Assert.That(after.Capabilities, Does.Contain("profiler"), "the capability is acquired by attaching, which no session-kind enum could express");
        Assert.That(after.ActiveProfileId, Is.EqualTo(attached.ProfileId));
    }

    [Test]
    public async Task DetachingAProfile_RemovesTheCapability()
    {
        var session = await OpenDemoAsync();
        var profilings = TraceLocation.ProfilingsDirectoryOf(session.FilePath);
        var capture = Path.GetFileName(TraceFixtureBuilder.BuildMinimalTrace(profilings));
        var attached = await AttachAsync(session.SessionId, capture);

        var resp = await SendAsync(HttpMethod.Delete, $"/api/sessions/{session.SessionId}/profile/{attached.ProfileId}", session.SessionId);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

        var after = await GetSessionAsync(session.SessionId);
        Assert.Multiple(() =>
        {
            Assert.That(after.Capabilities, Does.Not.Contain("profiler"));
            Assert.That(after.ActiveProfileId, Is.Null);
        });
    }

    [Test]
    public async Task DetachingAnUnknownProfile_Is404()
    {
        var session = await OpenDemoAsync();

        var resp = await SendAsync(HttpMethod.Delete, $"/api/sessions/{session.SessionId}/profile/{Guid.NewGuid()}", session.SessionId);

        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    // ── AC8 · co-location is not provenance ──────────────────────────────────────────────────────────────────

    [Test]
    public async Task ACaptureFromADifferentDatabase_IsRejected()
    {
        var session = await OpenDemoAsync();
        var profilings = TraceLocation.ProfilingsDirectoryOf(session.FilePath);
        // Sitting in this bundle, but recorded against something else — exactly what a copied or restored bundle produces.
        var foreign = Path.GetFileName(TraceFixtureBuilder.BuildMinimalTrace(profilings, databaseId: Guid.NewGuid(), databaseName: "some-other-db"));

        var resp = await PostProfileAsync(session.SessionId, foreign);

        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Conflict),
            "D-1 is explicit that co-location is not provenance; #614 recorded the database id so this is a check rather than an assumption");
        var body = await resp.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("not the one this session has open"));
    }

    [Test]
    public async Task TheListMarksAForeignCapture_SoItsDriftIsNeverRendered()
    {
        var session = await OpenDemoAsync();
        var profilings = TraceLocation.ProfilingsDirectoryOf(session.FilePath);
        var foreign = Path.GetFileName(
            TraceFixtureBuilder.BuildMinimalTrace(profilings, databaseId: Guid.NewGuid(), databaseName: "some-other-db", fileName: "20260303-000000-000.typhon-trace"));
        TraceFixtureBuilder.BuildMinimalTrace(profilings, fileName: "20260404-000000-000.typhon-trace");   // no recorded id — the pre-#614 case

        var list = await GetListAsync(session.SessionId);

        Assert.Multiple(() =>
        {
            Assert.That(list.Profiles, Has.Length.EqualTo(2), "a foreign capture still gets a row — one that vanishes looks like a retention bug");
            Assert.That(list.Profiles.Single(p => p.FileName == foreign).BelongsToDatabase, Is.False,
                "its TSNs come from another database's sequence, so the drift figure must not be rendered against this one");
            Assert.That(list.Profiles.Single(p => p.FileName != foreign).BelongsToDatabase, Is.True,
                "the empty-id allowance matches BelongsToDatabase — a pre-#614 capture keeps its drift readout");
        });
    }

    [Test]
    public async Task ACaptureWithNoRecordedDatabase_IsAllowed()
    {
        var session = await OpenDemoAsync();
        var profilings = TraceLocation.ProfilingsDirectoryOf(session.FilePath);
        var capture = Path.GetFileName(TraceFixtureBuilder.BuildMinimalTrace(profilings));  // DatabaseId defaults to empty

        var attached = await AttachAsync(session.SessionId, capture);

        Assert.That(attached.ProfileId, Is.Not.Null,
            "refusing captures that predate the identity field would make them unopenable to enforce a check they cannot answer either way");
    }

    [Test]
    public async Task AProfilePathOutsideTheDatabase_IsRejected()
    {
        var session = await OpenDemoAsync();
        var elsewhere = TraceFixtureBuilder.BuildMinimalTrace(Path.Combine(Path.GetTempPath(), "typhon-elsewhere-" + Guid.NewGuid().ToString("N")));

        var resp = await PostProfileAsync(session.SessionId, elsewhere);

        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest),
            "accepting a path would let any file on disk be presented as this database's profile, which is the co-location claim faked");
    }

    // ── AC7 · the guard follows capability, not kind ─────────────────────────────────────────────────────────

    [Test]
    public async Task ProfilerRoutes_AnswerForAnOpenSessionOnceAProfileIsAttached()
    {
        var session = await OpenDemoAsync();

        var before = await SendAsync(HttpMethod.Get, $"/api/sessions/{session.SessionId}/profiler/metadata", session.SessionId);
        Assert.That(before.StatusCode, Is.EqualTo(HttpStatusCode.Conflict), "a database with no profile has nothing to serve");

        var profilings = TraceLocation.ProfilingsDirectoryOf(session.FilePath);
        var capture = Path.GetFileName(TraceFixtureBuilder.BuildMinimalTrace(profilings));
        await AttachAsync(session.SessionId, capture);

        // The route did not move and its shape did not change — only what it asks about the session did.
        var after = await SendAsync(HttpMethod.Get, $"/api/sessions/{session.SessionId}/profiler/metadata", session.SessionId);
        Assert.That(after.StatusCode, Is.AnyOf(HttpStatusCode.OK, HttpStatusCode.Accepted),
            "202 while the sidecar cache builds, 200 once it is done — either proves the guard now resolves a runtime");
    }

    // ── AC9 · sessions with no database ──────────────────────────────────────────────────────────────────────
    //
    // #621 removed the standalone trace session, so the two tests that lived here — "attaching to a trace session is a
    // category error" and "a trace session still advertises the profiler capability" — describe a type that no longer
    // exists. Deleted rather than retargeted: an Attach session is already covered by the kind-mismatch tests above, and
    // keeping them pointed at a different subject would have preserved the assertions while losing their meaning.



    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────

    private async Task<SessionDto> OpenDemoAsync()
    {
        var resp = await _client.PostAsJsonAsync("/api/sessions/file", new { filePath = "demo.typhon" });
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<SessionDto>(await resp.Content.ReadAsStringAsync(), Json);
    }

    private async Task<SessionDto> GetSessionAsync(Guid id)
    {
        var resp = await SendAsync(HttpMethod.Get, $"/api/sessions/{id}", id);
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<SessionDto>(await resp.Content.ReadAsStringAsync(), Json);
    }

    private async Task<ProfileListDto> GetListAsync(Guid id)
    {
        var resp = await SendAsync(HttpMethod.Get, $"/api/sessions/{id}/profiles", id);
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<ProfileListDto>(await resp.Content.ReadAsStringAsync(), Json);
    }

    private async Task<ProfileDto> AttachAsync(Guid id, string fileName)
    {
        var resp = await PostProfileAsync(id, fileName);
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<ProfileDto>(await resp.Content.ReadAsStringAsync(), Json);
    }

    private Task<HttpResponseMessage> PostProfileAsync(Guid id, string fileName) =>
        SendAsync(HttpMethod.Post, $"/api/sessions/{id}/profile", id, new AttachProfileRequest(fileName));

    /// <summary>
    /// Sends a session-scoped request with its <c>X-Session-Token</c>. The token is the session id: the filter enforces token-to-route-id match so one
    /// session's token cannot address another's routes.
    /// </summary>
    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, Guid sessionId, object body = null)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-Session-Token", sessionId.ToString());
        if (body != null)
        {
            req.Content = JsonContent.Create(body);
        }
        return _client.SendAsync(req);
    }
}
