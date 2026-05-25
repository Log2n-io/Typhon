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
/// Regression guard for <c>SessionsController.ValidateTraceFileMagic</c>. Without this upfront
/// check a bad file path would create a session that immediately fails its background cache
/// build, flooding <c>/metadata</c> with 500s. The validator returns a clean 400 for three
/// distinct bad-magic cases so the UI can surface a readable error pill.
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
    }

    [TearDown]
    public void TearDown() => _factory.Dispose();

    private string WriteFile(string name, byte[] bytes)
    {
        Directory.CreateDirectory(_factory.DemoDirectory);
        var path = Path.Combine(_factory.DemoDirectory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private async Task<(HttpStatusCode Code, string Detail)> PostTraceAsync(string path)
    {
        var resp = await _client.PostAsJsonAsync("/api/sessions/trace", new CreateTraceSessionRequest(path));
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
        // Positive control — a real fixture passes the magic check and produces a session.
        var path = TraceFixtureBuilder.BuildMinimalTrace(_factory.DemoDirectory, tickCount: 2, instantsPerTick: 1);
        var resp = await _client.PostAsJsonAsync("/api/sessions/trace", new CreateTraceSessionRequest(path));
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Created));
    }

    [Test]
    public async Task Post_NonexistentFile_Returns404()
    {
        // The 404 path is upstream of the magic validator — it fires on File.Exists failure. Pin
        // it here to distinguish it from the 400 bad-magic cases above (users will see different
        // error phrasing).
        var path = Path.Combine(_factory.DemoDirectory, "does-not-exist.typhon-trace");
        var resp = await _client.PostAsJsonAsync("/api/sessions/trace", new CreateTraceSessionRequest(path));
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
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
