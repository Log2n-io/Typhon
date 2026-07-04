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

    // ─── Method-block detection (FindEnclosingMethodLines) ───────────────────────────────────────

    /// <summary>A two-method class with a local function, plus brace-in-string / brace-in-comment traps.</summary>
    private static readonly string SampleSource = string.Join('\n', new[]
    {
        "namespace N;",                       //  1
        "class C",                            //  2
        "{",                                  //  3
        "    void Alpha()",                   //  4
        "    {",                              //  5
        "        var s = \"} not a brace\";", //  6
        "        // } also not a brace",      //  7
        "    }",                              //  8
        "",                                   //  9
        "    int Beta(int x)",                // 10
        "    {",                              // 11
        "        void Local()",               // 12
        "        {",                          // 13
        "            x++;",                   // 14
        "        }",                          // 15
        "        Local();",                   // 16
        "        return x;",                  // 17
        "    }",                              // 18
        "}",                                  // 19
    });

    [Test]
    public void FindEnclosingMethodLines_LineInsideMethod_ReturnsMethodBlock()
    {
        // Line 6 has a brace inside a string, line 7 a brace inside a comment — neither may truncate
        // Alpha()'s block (lines 4–8).
        var block = ProfilerSourceController.FindEnclosingMethodLines(SampleSource, 6);
        Assert.That(block, Is.Not.Null);
        Assert.That(block!.Value.Start, Is.EqualTo(4));
        Assert.That(block.Value.End, Is.EqualTo(8));
    }

    [Test]
    public void FindEnclosingMethodLines_LineInsideLocalFunction_ReturnsInnermostMember()
    {
        // Line 14 is inside Local() (12–15), itself inside Beta() (10–18) — the innermost wins.
        var block = ProfilerSourceController.FindEnclosingMethodLines(SampleSource, 14);
        Assert.That(block, Is.Not.Null);
        Assert.That(block!.Value.Start, Is.EqualTo(12));
        Assert.That(block.Value.End, Is.EqualTo(15));
    }

    [Test]
    public void FindEnclosingMethodLines_LineInMethodButOutsideLocalFunction_ReturnsOuterMethod()
    {
        // Line 16 is in Beta() but past Local() — resolves to Beta() (10–18).
        var block = ProfilerSourceController.FindEnclosingMethodLines(SampleSource, 16);
        Assert.That(block, Is.Not.Null);
        Assert.That(block!.Value.Start, Is.EqualTo(10));
        Assert.That(block.Value.End, Is.EqualTo(18));
    }

    [Test]
    public void FindEnclosingMethodLines_LineOutsideAnyMethod_ReturnsNull()
    {
        // Line 2 is the class declaration — no enclosing method-like member.
        Assert.That(ProfilerSourceController.FindEnclosingMethodLines(SampleSource, 2), Is.Null);
    }

    [Test]
    public void FindEnclosingMethodLines_NonCSharpText_ReturnsNull()
    {
        Assert.That(ProfilerSourceController.FindEnclosingMethodLines("not c# at all\nmore text", 1), Is.Null);
    }

    [Test]
    public async Task GetSource_CSharpFile_ReturnsEnclosingMethodBlock()
    {
        var filePath = Path.Combine(_tempRoot, "method-target.cs");
        var content = string.Join('\n', new[]
        {
            "class C",                  // 1
            "{",                        // 2
            "    void M()",             // 3
            "    {",                    // 4
            "        var s = \"}\";",   // 5
            "    }",                    // 6
            "}",                        // 7
        });
        await File.WriteAllTextAsync(filePath, content);

        // A generous context=50 would yield the whole file (1–7) if it fell back to a window; the
        // method-block path must instead return exactly M()'s span (3–6).
        var resp = await _client.GetAsync(
            $"/api/profiler/source?path={Uri.EscapeDataString(filePath)}&line=5&context=50");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK), await resp.Content.ReadAsStringAsync());

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.That(doc.RootElement.GetProperty("startLine").GetInt32(), Is.EqualTo(3));
        Assert.That(doc.RootElement.GetProperty("endLine").GetInt32(), Is.EqualTo(6));
    }
}
