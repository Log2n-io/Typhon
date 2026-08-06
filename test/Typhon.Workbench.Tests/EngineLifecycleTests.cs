using System.Linq;
using NUnit.Framework;
using Typhon.Workbench.Fixtures;
using Typhon.Workbench.Schema;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Tests;

[TestFixture]
[NonParallelizable] // opens engines via EngineLifecycle.OpenAsync — the schema-compat State check reads the process-global ArchetypeRegistry, which must not race with other engine tests (see #554)
public sealed class EngineLifecycleTests
{
    private string _tempDir;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "typhon-wb-engine-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort */ }
    }

    [Test]
    public async Task Open_CreatesEngineAndExposesRegistry()
    {
        var path = Path.Combine(_tempDir, "demo.typhon");
        using var lifecycle = await EngineLifecycle.OpenAsync(path);

        Assert.That(lifecycle.Engine, Is.Not.Null);
        Assert.That(lifecycle.Registry, Is.Not.Null);
        Assert.That(lifecycle.Registry.Root, Is.Not.Null);
        Assert.That(lifecycle.Registry.Root.Children, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task DisposeAndReopen_SamePath_Succeeds()
    {
        var path = Path.Combine(_tempDir, "demo.typhon");

        var first = await EngineLifecycle.OpenAsync(path);
        first.Dispose();

        Assert.DoesNotThrowAsync(async () =>
        {
            using var second = await EngineLifecycle.OpenAsync(path);
            Assert.That(second.Registry.Root, Is.Not.Null);
        }, "File handle should be released so the same path can be reopened in-process");
    }

    [Test]
    public async Task Dispose_IsIdempotent()
    {
        var path = Path.Combine(_tempDir, "demo.typhon");
        var lifecycle = await EngineLifecycle.OpenAsync(path);

        lifecycle.Dispose();
        Assert.DoesNotThrow(() => lifecycle.Dispose());
    }

    /// <summary>
    /// The pause/resume cycle for #621: pause is a <b>dispose</b> — there is no way to hold a mapped, write-open file "softly" — and resume is a fresh
    /// <see cref="EngineLifecycle.OpenAsync"/>. This asserts the cycle is repeatable <i>with schema DLLs loaded</i>, which is the case that can leak.
    ///
    /// <para><b>Why this is the shape of resume, rather than re-opening an engine inside a surviving ALC.</b>
    /// <c>ArchetypeRegistry.UnregisterEngineUse</c> (called from <c>DatabaseEngine.Dispose</c>) clears every registry table entry for Types that came from a
    /// <i>collectible</i> ALC — precisely the Workbench's per-session schema DLLs. Repopulating them runs off the generated
    /// <c>[ModuleInitializer]</c> barrier, and the CLR runs a module initializer <b>at most once per module per ALC</b>. So an ALC that survives the pause
    /// could never re-register what dispose removed. A fresh ALC per resume is not a wasteful choice, it is the only correct one — and it is the same path
    /// close-and-reopen already exercises daily (<see cref="DisposeAndReopen_SamePath_Succeeds"/>).</para>
    ///
    /// <para>What would fail here if the refcounting were unbalanced: <c>State</c> falling out of <c>Ready</c>, <c>LoadedComponentTypes</c> dropping to 0 on
    /// a later cycle, or the cluster segment vanishing — each the signature of registry entries cleared without being restored.</para>
    /// </summary>
    [Test]
    public async Task PauseResumeCycles_WithSchemaLoaded_StayStable()
    {
        var fixture = FixtureDatabase.CreateOrReuse(_tempDir, force: true);

        int firstLoadedTypes = 0;
        long firstClusterSegments = 0;

        for (var cycle = 1; cycle <= 5; cycle++)
        {
            var lifecycle = await EngineLifecycle.OpenAsync(fixture.TyphonFilePath);
            try
            {
                Assert.That(lifecycle.State, Is.EqualTo(SchemaCompatibility.State.Ready), $"cycle {cycle}: schema must still classify as Ready");
                Assert.That(lifecycle.LoadedComponentTypes, Is.GreaterThan(0), $"cycle {cycle}: components must re-register into the fresh engine");

                var clusterSegments = lifecycle.Engine.EnumerateStorageSegments().Count(s => s.Kind == StorageSegmentKind.Cluster);
                Assert.That(clusterSegments, Is.GreaterThan(0), $"cycle {cycle}: archetype metadata must survive — a cleared registry shows up as no cluster segment");

                if (cycle == 1)
                {
                    firstLoadedTypes = lifecycle.LoadedComponentTypes;
                    firstClusterSegments = clusterSegments;
                }
                else
                {
                    // Drift across cycles is the leak signature: a registry that half-repopulates registers fewer types each time.
                    Assert.That(lifecycle.LoadedComponentTypes, Is.EqualTo(firstLoadedTypes), $"cycle {cycle}: registered component count drifted from cycle 1");
                    Assert.That(clusterSegments, Is.EqualTo(firstClusterSegments), $"cycle {cycle}: cluster segment count drifted from cycle 1");
                }
            }
            finally
            {
                lifecycle.Dispose(); // this IS pause — engine → ALC → ServiceProvider, releasing the file handle and db.lock
            }
        }
    }
}
