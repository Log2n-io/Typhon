using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Workbench.Hosting;

namespace Typhon.Workbench.Tests;

/// <summary>
/// Covers <see cref="Typhon.Workbench.Controllers.OptionsController"/> — GET / PUT / PATCH per-category +
/// bootstrap-token gating. Round-trip is verified end-to-end (GET → PATCH → GET shows the new value)
/// and out-of-band file edits get picked up via the <see cref="OptionsStore"/>'s FileSystemWatcher.
/// </summary>
[TestFixture]
public sealed class OptionsControllerTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private WorkbenchFactory _factory;
    private HttpClient _client;

    [SetUp]
    public void SetUp()
    {
        _factory = new WorkbenchFactory();
        _client = _factory.CreateAuthenticatedClient();
    }

    [TearDown]
    public void TearDown() => _factory.Dispose();

    [Test]
    public async Task Get_WithoutBootstrapToken_Returns401()
    {
        using var unauth = _factory.CreateClient();
        var resp = await unauth.GetAsync("/api/options");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Get_ReturnsDefaultsOnFreshStore()
    {
        var opts = await _client.GetFromJsonAsync<WorkbenchOptions>("/api/options", JsonOpts);
        Assert.That(opts, Is.Not.Null);
        Assert.That(opts.Editor.Kind, Is.EqualTo(EditorKind.VsCode));
        Assert.That(opts.Profiler.WorkspaceRoot, Is.EqualTo(string.Empty));
    }

    [Test]
    public async Task PatchEditor_PersistsAndRoundTripsThroughGet()
    {
        var newEditor = new EditorOptions { Kind = EditorKind.Rider, CustomCommand = "rider --line {line} {file}" };
        var patch = await _client.PatchAsJsonAsync("/api/options/editor", newEditor, JsonOpts);
        Assert.That(patch.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var opts = await _client.GetFromJsonAsync<WorkbenchOptions>("/api/options", JsonOpts);
        Assert.That(opts.Editor.Kind, Is.EqualTo(EditorKind.Rider));
        Assert.That(opts.Editor.CustomCommand, Is.EqualTo("rider --line {line} {file}"));
    }

    [Test]
    public async Task PatchProfiler_LeavesEditorUntouched()
    {
        await _client.PatchAsJsonAsync("/api/options/editor",
            new EditorOptions { Kind = EditorKind.Cursor, CustomCommand = "" }, JsonOpts);

        await _client.PatchAsJsonAsync("/api/options/profiler",
            new Typhon.Workbench.Hosting.ProfilerOptions { WorkspaceRoot = "/tmp/ws" }, JsonOpts);

        var opts = await _client.GetFromJsonAsync<WorkbenchOptions>("/api/options", JsonOpts);
        Assert.That(opts.Editor.Kind, Is.EqualTo(EditorKind.Cursor));
        Assert.That(opts.Profiler.WorkspaceRoot, Is.EqualTo("/tmp/ws"));
    }

    [Test]
    public async Task PatchSchema_NormalizesAndRoundTripsThroughGet()
    {
        // ADR-055 Phase 2: the server keeps only rooted paths, canonicalizes via GetFullPath, and de-duplicates
        // case-insensitively while preserving order. A relative path is dropped; a duplicate collapses to one.
        var abs = Path.Combine(Path.GetTempPath(), "typhon-schema-registered");
        var patch = await _client.PatchAsJsonAsync("/api/options/schema",
            new SchemaOptions { Directories = ["relative/skip-me", abs, abs] }, JsonOpts);
        Assert.That(patch.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var opts = await _client.GetFromJsonAsync<WorkbenchOptions>("/api/options", JsonOpts);
        Assert.That(opts.Schema.Directories, Has.Length.EqualTo(1), "relative path dropped + duplicate de-duped");
        Assert.That(opts.Schema.Directories[0], Is.EqualTo(Path.GetFullPath(abs)));
    }

    [Test]
    public async Task PatchSchema_LeavesOtherCategoriesUntouched()
    {
        await _client.PatchAsJsonAsync("/api/options/editor",
            new EditorOptions { Kind = EditorKind.Rider, CustomCommand = "" }, JsonOpts);

        var dir = Path.Combine(Path.GetTempPath(), "typhon-schema-untouched");
        await _client.PatchAsJsonAsync("/api/options/schema",
            new SchemaOptions { Directories = [dir] }, JsonOpts);

        var opts = await _client.GetFromJsonAsync<WorkbenchOptions>("/api/options", JsonOpts);
        Assert.That(opts.Editor.Kind, Is.EqualTo(EditorKind.Rider), "schema patch must not disturb the editor category");
        Assert.That(opts.Schema.Directories, Has.Length.EqualTo(1));
    }

    [Test]
    public async Task OutOfBandFileEdit_IsPickedUpByGet()
    {
        var store = _factory.Services.GetRequiredService<OptionsStore>();
        var path = store.FilePath;

        // Edit the file out-of-band — exactly what a user does when they hand-edit options.json.
        var edited = new WorkbenchOptions
        {
            Editor = new EditorOptions { Kind = EditorKind.Custom, CustomCommand = "external" },
            Profiler = new Typhon.Workbench.Hosting.ProfilerOptions { WorkspaceRoot = "/tmp/edited" },
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(edited, JsonOpts));

        // Wait for FileSystemWatcher debounce + reload (200 ms watcher debounce + slack for filesystem latency).
        var deadline = DateTime.UtcNow.AddSeconds(2);
        WorkbenchOptions live = null;
        while (DateTime.UtcNow < deadline)
        {
            live = await _client.GetFromJsonAsync<WorkbenchOptions>("/api/options", JsonOpts);
            if (live.Editor.Kind == EditorKind.Custom) break;
            await Task.Delay(50);
        }
        Assert.That(live?.Editor.Kind, Is.EqualTo(EditorKind.Custom));
        Assert.That(live.Profiler.WorkspaceRoot, Is.EqualTo("/tmp/edited"));
    }
}
