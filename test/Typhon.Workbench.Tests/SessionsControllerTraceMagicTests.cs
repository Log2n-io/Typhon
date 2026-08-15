using System;
using System.Buffers.Binary;
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
/// Regression guard for <c>ProfilesController.ValidateCaptureMagic</c>. Without this upfront
/// check a bad file would create a runtime that immediately fails its background cache
/// build, flooding <c>/metadata</c> with 500s. The validator returns a clean 400 for three
/// distinct bad-magic cases so the UI can surface a readable error pill.
///
/// <para>#621 moved the guard from the deleted standalone-trace route to the attach path — with two entry modes,
/// attaching is the only way a user hands the Workbench a capture file, so it is where a wrong file now arrives.
/// The cases are unchanged; only the door they knock on is.</para>
///
/// The most common user mistake (pasting the <c>.typhon-trace-cache</c> sidecar instead of the
/// source) gets a specific hint in the error message — we pin that too so the diagnostic doesn't
/// silently regress to a generic "invalid magic" when users need the redirect the most.
/// </summary>
[TestFixture]
public sealed class SessionsControllerTraceMagicTests
{
    private WorkbenchFactory _factory;
    private HttpClient _client;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    [SetUp]
    public void SetUp()
    {
        _factory = new WorkbenchFactory();
        _client = _factory.CreateAuthenticatedClient();

        // The bundle must exist before it is opened: POST /api/sessions/file no longer auto-creates a database from a
        // path that is not there (a typo used to fabricate an empty one instead of failing). These tests are about
        // capture validation, so the database is setup — create it rather than lean on the old create-if-missing.
        Directory.CreateDirectory(BundlePath);
    }

    [TearDown]
    public void TearDown() => _factory.Dispose();

    /// <summary>Writes a candidate file straight into a fresh database's <c>profilings/</c>, which is where attach resolves names.</summary>
    private string WriteFile(string name, byte[] bytes)
    {
        var profilings = TraceLocation.ProfilingsDirectoryOf(BundlePath);
        Directory.CreateDirectory(profilings);
        var path = Path.Combine(profilings, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private string BundlePath => Path.Combine(_factory.DemoDirectory, "magic.typhon");

    private async Task<(HttpStatusCode Code, string Detail)> PostTraceAsync(string path)
    {
        // Open the database, then try to attach the candidate file by name — the path a user takes since #621.
        var opened = await _client.PostAsJsonAsync("/api/sessions/file", new { filePath = BundlePath });
        opened.EnsureSuccessStatusCode();
        var session = JsonSerializer.Deserialize<SessionDto>(await opened.Content.ReadAsStringAsync(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/sessions/{session.SessionId}/profile")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { fileName = Path.GetFileName(path) }),
        };
        req.Headers.Add("X-Session-Token", session.SessionId.ToString());
        var resp = await _client.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        var detail = "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("detail", out var d))
            {
                detail = d.GetString() ?? "";
            }
        }
        catch { /* leave detail empty on non-JSON */ }
        return (resp.StatusCode, detail);
    }

    [Test]
    public async Task Post_SidecarCache_Returns400_WithSidecarHint()
    {
        // TPCH magic — the most common user mistake (pasting the cache file). The validator has
        // a tailored hint message specifically to redirect them to the source .typhon-trace.
        var bytes = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), 0x48435054); // "TPCH"
        var path = WriteFile("fake-sidecar.typhon-trace-cache", bytes);

        var (code, detail) = await PostTraceAsync(path);

        Assert.That(code, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(detail, Does.Contain("sidecar"),
            "error detail must redirect the user to the source file, not just say 'bad magic'");
    }

    [Test]
    public async Task Post_WrongMagic_Returns400_WithMagicBytesInDetail()
    {
        // Random bytes — magic neither TYTR nor TPCH. Detail should include the observed magic
        // so the user can diagnose (e.g., "you opened a JPEG by accident").
        var bytes = new byte[] { 0xAB, 0xCD, 0xEF, 0x01, 0, 0, 0, 0 };
        var path = WriteFile("wrong-magic.bin", bytes);

        var (code, detail) = await PostTraceAsync(path);

        Assert.That(code, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(detail, Does.Contain("File magic"));
        Assert.That(detail, Does.Contain("TYTR"), "detail names the expected magic so the user knows what a valid trace looks like");
    }

    [Test]
    public async Task Post_TooSmallFile_Returns400()
    {
        // Fewer than 4 bytes — can't even read the magic.
        var path = WriteFile("tiny.bin", [0x01, 0x02]);

        var (code, detail) = await PostTraceAsync(path);

        Assert.That(code, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(detail, Does.Contain("too small"));
    }

    [Test]
    public async Task Post_ValidTraceFixture_Returns201()
    {
        // Positive control — a real fixture passes the magic check and attaches. Without this the bad-magic cases above
        // would pass just as happily if the validator rejected everything.
        var built = TraceFixtureBuilder.BuildMinimalTrace(_factory.DemoDirectory, tickCount: 2, instantsPerTick: 1);
        var session = await CaptureSessionFactory.OpenWithCaptureAsync(_client, _factory.DemoDirectory, built);
        Assert.That(session.Capabilities, Does.Contain("profiler"), "a valid capture must attach and grant the profiler capability");
    }

    [Test]
    public async Task Post_NonexistentFile_Returns404()
    {
        // The 404 path is upstream of the magic validator — it fires on File.Exists failure. Pin it here to distinguish
        // it from the 400 bad-magic cases above (users see different error phrasing).
        var (code, _) = await PostTraceAsync(Path.Combine(TraceLocation.ProfilingsDirectoryOf(BundlePath), "does-not-exist.typhon-trace"));
        Assert.That(code, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Post_OldVersionTrace_Returns400_WithReRecordHint()
    {
        // Valid TYTR magic but an unsupported (old) on-disk format version. The up-front validator must reject it here
        // with a clear 400 — otherwise the session is created and its background build faults at ReadHeader, surfacing
        // a 500 on /metadata with a "see server logs" message instead of the actionable reason.
        var bytes = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), Typhon.Profiler.TraceFileHeader.MagicValue);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4, 2), 9); // pre-v11 — below MinSupportedVersion
        var path = WriteFile("old-version.typhon-trace", bytes);

        var (code, detail) = await PostTraceAsync(path);

        Assert.That(code, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(detail, Does.Contain("version"));
        Assert.That(detail, Does.Contain("Re-record"), "detail must tell the user to re-record against a current build");
    }
}
