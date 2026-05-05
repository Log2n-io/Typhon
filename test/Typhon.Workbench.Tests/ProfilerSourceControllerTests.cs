using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using NUnit.Framework;
using Typhon.Workbench.Controllers;

namespace Typhon.Workbench.Tests;

/// <summary>
/// Covers <see cref="ProfilerSourceController"/> — workspace-root resolution, path-traversal guard,
/// absolute-path branch (#302 system attribution), source-window read, OpenInEditor 400 cases.
/// Live editor launches are NOT exercised here (covered by <see cref="EditorLauncherTests"/>); these
/// tests focus on the controller's request-shape, response-shape, and the security boundary.
/// </summary>
[TestFixture]
public sealed class ProfilerSourceControllerTests
{
    private WorkbenchFactory _factory;
    private HttpClient _client;
    private string _tempRoot;

    [SetUp]
    public void SetUp()
    {
        _factory = new WorkbenchFactory();
        _client = _factory.CreateAuthenticatedClient();

        _tempRoot = Path.Combine(Path.GetTempPath(), "typhon-wb-source-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        // Plant a .git marker so AutoDetectRepoRoot would resolve here if walked up to.
        Directory.CreateDirectory(Path.Combine(_tempRoot, ".git"));
    }

    [TearDown]
    public void TearDown()
    {
        _factory.Dispose();
        try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true); }
        catch { /* best-effort */ }
    }

    [Test]
    public void ResolveAbsolutePath_RepoRelative_StripsPrefixAndJoinsRoot()
    {
        var resolved = ProfilerSourceController.ResolveAbsolutePath("/_/src/Foo.cs", @"C:\repo");
        // Path.GetFullPath normalizes separators per host OS; just assert the right joining happened.
        Assert.That(resolved, Does.EndWith("Foo.cs"));
        Assert.That(resolved, Does.Contain("repo"));
    }

    [Test]
    public void ResolveAbsolutePath_AlreadyAbsolute_DoesNotJoinWorkspaceRoot()
    {
        var input = OperatingSystem.IsWindows() ? @"C:\Other\repo\Foo.cs" : "/other/repo/Foo.cs";
        var resolved = ProfilerSourceController.ResolveAbsolutePath(input, @"C:\different\workspace");
        Assert.That(resolved, Does.Contain("Other").Or.Contain("other"));
        Assert.That(resolved, Does.Not.Contain("workspace"));
    }

    [Test]
    public async Task GetSource_BlocksTraversalOnRepoRelativePaths()
    {
        // Request a path that starts /_/ but tries to escape via ../.. — the controller's traversal
        // guard must reject it because the resolved path lands outside the workspace root.
        var resp = await _client.GetAsync(
            "/api/profiler/source?path=" + Uri.EscapeDataString("/_/../../../../../etc/passwd") + "&line=1");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var body = await resp.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("outside the workspace root").IgnoreCase);
    }

    [Test]
    public async Task GetSource_AbsolutePath_BypassesWorkspaceGuardAndReadsFile()
    {
        // PDB-resolved system attribution paths (e.g. AntHill at C:\Dev\github\Typhon\test\AntHill)
        // live outside the Typhon workspace. The controller permits absolute paths because they
        // came from a trace manifest the bootstrap-token-gated server itself ingested.
        var filePath = Path.Combine(_tempRoot, "abs-target.cs");
        var content = "line 1\nline 2\nline 3\nline 4\nline 5\n";
        await File.WriteAllTextAsync(filePath, content);

        var resp = await _client.GetAsync(
            $"/api/profiler/source?path={Uri.EscapeDataString(filePath)}&line=3&context=1");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK), await resp.Content.ReadAsStringAsync());

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var lines = doc.RootElement.GetProperty("lines").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.That(lines, Is.EqualTo(new[] { "line 2", "line 3", "line 4" }));
    }

    [Test]
    public async Task GetSource_MissingFile_Returns404()
    {
        var bogus = Path.Combine(_tempRoot, "does-not-exist.cs");
        var resp = await _client.GetAsync($"/api/profiler/source?path={Uri.EscapeDataString(bogus)}&line=1");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task GetSource_BadLine_Returns400()
    {
        var resp = await _client.GetAsync($"/api/profiler/source?path=/_/foo.cs&line=0");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task GetWorkspaceRoot_ReturnsConfiguredOrAutoDetected()
    {
        var resp = await _client.GetAsync("/api/profiler/workspace-root");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var effective = doc.RootElement.GetProperty("effective").GetString();
        var source = doc.RootElement.GetProperty("source").GetString();
        Assert.That(effective, Is.Not.Null.And.Not.Empty);
        Assert.That(source, Is.AnyOf("configured", "auto-detected", "cwd-fallback"));
    }

    [Test]
    public async Task OpenInEditor_MissingFile_Returns400()
    {
        var resp = await _client.PostAsJsonAsync("/api/profiler/open-in-editor", new { file = "", line = 1 });
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task OpenInEditor_BadLine_Returns400()
    {
        var resp = await _client.PostAsJsonAsync("/api/profiler/open-in-editor", new { file = "/_/x.cs", line = 0 });
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }
}
