using System.Linq;
using NUnit.Framework;
using Typhon.Workbench.Fixtures;
using Typhon.Workbench.Schema;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Tests;

/// <summary>
/// Covers manifest-driven schema resolution in <see cref="EngineLifecycle"/>: the database records the assemblies that declare its components (AssemblyR1),
/// and the open path locates them next to the file by simple name — replacing the old <c>*.schema.dll</c> filename convention.
/// </summary>
[TestFixture]
public sealed class EngineLifecycleManifestTests
{
    private string _tempDir;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "typhon-wb-manifest-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort */ }
    }

    [Test]
    public async Task Open_ResolvesManifestAssemblyAdjacentToFile_RegistersComponents()
    {
        var fixture = FixtureDatabase.CreateOrReuse(_tempDir, force: true);

        using var lifecycle = await EngineLifecycle.OpenAsync(fixture.TyphonFilePath);

        Assert.That(lifecycle.State, Is.EqualTo(SchemaCompatibility.State.Ready));
        Assert.That(lifecycle.LoadedComponentTypes, Is.GreaterThan(0), "fixture components must register from the manifest-resolved assembly");

        var required = lifecycle.Engine.GetRequiredAssemblies().Select(a => a.Name).ToArray();
        Assert.That(required, Does.Contain("Typhon.Workbench.Fixtures.schema"), "the declaring assembly must be recorded in the manifest");

        // The original bug: without the schema the per-archetype segments were never registered, so the File Map rendered them Unknown. With the manifest-
        // resolved schema loaded, the cluster archetype's segment must be registered — which is exactly what lets ClassifyAllPages attribute those pages.
        var segments = lifecycle.Engine.EnumerateStorageSegments();
        Assert.That(segments.Any(s => s.Kind == Typhon.Engine.StorageSegmentKind.Cluster), Is.True,
            "the cluster archetype's segment must be registered once the manifest-resolved schema is loaded");
    }

    [Test]
    public async Task Open_ResolvesByAssemblyName_WhenFileNameDiffers()
    {
        var fixture = FixtureDatabase.CreateOrReuse(_tempDir, force: true);

        // Rename the schema DLL so the fast {SimpleName}.dll probe misses — forcing the metadata name-match fallback.
        var canonical = Path.Combine(_tempDir, "Typhon.Workbench.Fixtures.schema.dll");
        var renamed = Path.Combine(_tempDir, "fixtures-9.9.9-renamed.dll");
        File.Move(canonical, renamed);

        using var lifecycle = await EngineLifecycle.OpenAsync(fixture.TyphonFilePath);

        Assert.That(lifecycle.State, Is.EqualTo(SchemaCompatibility.State.Ready), "metadata name-match must resolve a non-conventionally-named DLL");
        Assert.That(lifecycle.LoadedComponentTypes, Is.GreaterThan(0));
    }

    [Test]
    public async Task Open_MissingManifestAssembly_SurfacesDiagnostic_WithoutCrashing()
    {
        var fixture = FixtureDatabase.CreateOrReuse(_tempDir, force: true);

        // Remove the schema DLL entirely — resolution must fail gracefully, not throw.
        File.Delete(Path.Combine(_tempDir, "Typhon.Workbench.Fixtures.schema.dll"));

        using var lifecycle = await EngineLifecycle.OpenAsync(fixture.TyphonFilePath);

        Assert.That(lifecycle.State, Is.Not.EqualTo(SchemaCompatibility.State.Ready));
        Assert.That(lifecycle.Diagnostics.Any(d => d.Kind == "missing_assembly"), Is.True, "an unresolved manifest assembly must surface a missing_assembly diagnostic");
        Assert.That(lifecycle.Diagnostics.Any(d => d.ComponentName == "Typhon.Workbench.Fixtures.schema"), Is.True, "the diagnostic must name the missing assembly");
    }
}
