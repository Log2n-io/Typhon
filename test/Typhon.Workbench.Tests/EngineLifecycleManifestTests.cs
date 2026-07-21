using System.Linq;
using NUnit.Framework;
using Typhon.Workbench.Fixtures;
using Typhon.Workbench.Schema;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Tests;

/// <summary>
/// Covers schema-assembly resolution in <see cref="EngineLifecycle"/> after ADR-055: the schema DLL is no longer
/// copied per-database. The database records the assemblies that declare its components (AssemblyR1), and the open
/// path resolves each by simple name across the search order { <b>bundled</b> = the Workbench's own deployment
/// directory (here, the test bin), <b>legacy-adjacent</b> = next to the database file }. The bundled (single,
/// current) copy wins over any stale copy left beside a database — which is the exact failure ADR-055 fixes.
/// </summary>
[TestFixture]
[NonParallelizable] // opens engines via EngineLifecycle.OpenAsync — the schema-compat State check reads the process-global ArchetypeRegistry, which must not race with other engine tests (see #554)
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
    public async Task Open_ResolvesSchemaFromBundledDir_RegistersComponents()
    {
        // ADR-055: CreateOrReuse no longer copies the schema DLL beside the database — resolution comes from the
        // Workbench's (here, the test's) own deployment directory.
        var fixture = FixtureDatabase.CreateOrReuse(_tempDir, force: true);
        var fixtureDir = Path.GetDirectoryName(fixture.TyphonFilePath)!;
        Assert.That(File.Exists(Path.Combine(fixtureDir, "Typhon.Workbench.Fixtures.schema.dll")), Is.False,
            "ADR-055: the schema DLL must NOT be copied next to the database anymore");

        using var lifecycle = await EngineLifecycle.OpenAsync(fixture.TyphonFilePath);

        Assert.That(lifecycle.State, Is.EqualTo(SchemaCompatibility.State.Ready));
        Assert.That(lifecycle.LoadedComponentTypes, Is.GreaterThan(0), "fixture components must register from the bundled assembly");
        Assert.That(lifecycle.SchemaStatus, Is.EqualTo("bundled"), "resolution must report the bundled provenance");
        Assert.That(lifecycle.ResolvedSchemaPaths.Any(p => p.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase)),
            Is.True, "the resolved schema DLL must come from the Workbench's own deployment directory");

        var required = lifecycle.Engine.GetRequiredAssemblies().Select(a => a.Name).ToArray();
        Assert.That(required, Does.Contain("Typhon.Workbench.Fixtures.schema"), "the declaring assembly must be recorded in the manifest");

        // The original bug: without the schema the per-archetype segments were never registered, so the File Map rendered
        // them Unknown. With the resolved schema loaded, the cluster archetype's segment must be registered.
        var segments = lifecycle.Engine.EnumerateStorageSegments();
        Assert.That(segments.Any(s => s.Kind == Typhon.Engine.StorageSegmentKind.Cluster), Is.True,
            "the cluster archetype's segment must be registered once the resolved schema is loaded");
    }

    [Test]
    public async Task Open_PrefersBundled_OverStaleAdjacentCopy()
    {
        // The heavy-01 regression: a stale/garbage *.schema.dll left beside the database must NOT be loaded — the
        // single, current bundled assembly wins. (If the adjacent garbage were probed, SchemaLoader would throw a
        // BadImageFormatException and the session would not be Ready.)
        var fixture = FixtureDatabase.CreateOrReuse(_tempDir, force: true);
        var fixtureDir = Path.GetDirectoryName(fixture.TyphonFilePath)!;
        File.WriteAllText(Path.Combine(fixtureDir, "Typhon.Workbench.Fixtures.schema.dll"), "not a real dll — a stale copy");

        using var lifecycle = await EngineLifecycle.OpenAsync(fixture.TyphonFilePath);

        Assert.That(lifecycle.State, Is.EqualTo(SchemaCompatibility.State.Ready), "the stale adjacent copy must be ignored in favour of the bundled assembly");
        Assert.That(lifecycle.SchemaStatus, Is.EqualTo("bundled"));
        Assert.That(lifecycle.LoadedComponentTypes, Is.GreaterThan(0));
    }

    [Test]
    public async Task Open_ExplicitSchemaPath_TakesPrecedence()
    {
        // An explicit user-specified path bypasses manifest resolution entirely (provenance "user-specified").
        var fixture = FixtureDatabase.CreateOrReuse(_tempDir, force: true);
        var bundledSchema = Path.Combine(AppContext.BaseDirectory, "Typhon.Workbench.Fixtures.schema.dll");

        using var lifecycle = await EngineLifecycle.OpenAsync(fixture.TyphonFilePath, [bundledSchema]);

        Assert.That(lifecycle.State, Is.EqualTo(SchemaCompatibility.State.Ready));
        Assert.That(lifecycle.SchemaStatus, Is.EqualTo("user-specified"));
        Assert.That(lifecycle.LoadedComponentTypes, Is.GreaterThan(0));
    }

    [Test]
    public async Task Open_PrefersRegisteredDir_OverBundled()
    {
        // ADR-055 Phase 2: a user-registered schema directory wins over the Workbench's own bundled binaries —
        // the escape hatch for pointing at a custom or recompiled-from-git schema build.
        var fixture = FixtureDatabase.CreateOrReuse(_tempDir, force: true);

        // A separate directory holding a copy of the fixtures schema assembly. The per-session ALC defers
        // Typhon.* dependencies to the default context (already loaded in-process), so the schema DLL alone
        // resolves from the registered directory without its engine deps alongside it.
        var registeredDir = Path.Combine(_tempDir, "registered-schema");
        Directory.CreateDirectory(registeredDir);
        var bundledSchema = Path.Combine(AppContext.BaseDirectory, "Typhon.Workbench.Fixtures.schema.dll");
        var registeredCopy = Path.Combine(registeredDir, "Typhon.Workbench.Fixtures.schema.dll");
        File.Copy(bundledSchema, registeredCopy, overwrite: true);

        using var lifecycle = await EngineLifecycle.OpenAsync(fixture.TyphonFilePath, null, [registeredDir]);

        Assert.That(lifecycle.State, Is.EqualTo(SchemaCompatibility.State.Ready));
        Assert.That(lifecycle.LoadedComponentTypes, Is.GreaterThan(0));
        Assert.That(lifecycle.SchemaStatus, Is.EqualTo("registered"), "a registered dir must win over bundled");
        Assert.That(lifecycle.ResolvedSchemaPaths.Any(p => p.StartsWith(registeredDir, StringComparison.OrdinalIgnoreCase)),
            Is.True, "the schema must resolve from the registered directory, not the bundled one");
    }

    [Test]
    public async Task Open_SkipsNonExistentRegisteredDir_FallsBackToBundled()
    {
        // A registered directory that doesn't exist must be skipped gracefully — resolution falls through to the
        // bundled binaries rather than throwing.
        var fixture = FixtureDatabase.CreateOrReuse(_tempDir, force: true);
        var ghostDir = Path.Combine(_tempDir, "does-not-exist");

        using var lifecycle = await EngineLifecycle.OpenAsync(fixture.TyphonFilePath, null, [ghostDir]);

        Assert.That(lifecycle.State, Is.EqualTo(SchemaCompatibility.State.Ready));
        Assert.That(lifecycle.SchemaStatus, Is.EqualTo("bundled"), "a missing registered dir is skipped; bundled wins");
        Assert.That(lifecycle.LoadedComponentTypes, Is.GreaterThan(0));
    }
}
