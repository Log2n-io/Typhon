using System;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using Typhon.Workbench.Fixtures;
using Typhon.Workbench.Schema;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Tests;

/// <summary>
/// AC6 of the Workbench ALC lifecycle fix. Verifies the full end-to-end behaviour that motivated the work:
/// open a Dev Fixture-generated database in the Workbench, close it, open a *different* fixture-generated
/// database — both opens must succeed. Pre-fix, the second open hit static-registry pollution from the first
/// (the per-session collectible ALC couldn't unload because the engine's registry pinned its Types), surfacing
/// as `Archetype <X> not registered` assertions or `RawValuePagedHashMap.ExecuteSplit` invariant violations
/// during entity spawn.
///
/// <para>This test runs against the actual <see cref="EngineLifecycle.OpenAsync"/> path that the
/// Workbench's <c>SessionsController</c> calls. The fixture schema (`Typhon.Workbench.Fixtures.schema.dll`)
/// is the one that triggered the original bug — it's loaded twice into separate collectible ALCs in the
/// real-world failure scenario, exactly what we exercise here.</para>
/// </summary>
[TestFixture]
[NonParallelizable] // touches the process-global ArchetypeRegistry — must not race with other engine tests
public sealed class SessionLifecycleTests
{
    private string _tempRoot;

    [SetUp]
    public void SetUp()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "typhon-wb-session-lifecycle", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [TearDown]
    public void TearDown()
    {
        try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true); }
        catch { /* best-effort */ }
    }

    [Test]
    public async Task Sequential_OpenCloseOpen_DifferentDatabases_BothSucceed()
    {
        // Step 1: generate two fixture databases on disk. Both use the same fixture schema (loaded into
        // separate collectible ALCs by the Workbench's open path); the registry-lifecycle fix is what
        // makes the second open succeed without trampling the first's state.
        // Small, distinct configs — fast to generate; the two differ so they materialise as separate databases.
        var smallA = FixtureConfig.Default with
        {
            ResourceTypeCount = 5, GuildCount = 2, RecipeCount = 5, PlayerCount = 10,
            DepositCount = 5, HarvesterCount = 3, FactoryCount = 1, ItemCount = 10,
        };
        var dbAPath = GenerateFixture("db-a", smallA);
        var dbBPath = GenerateFixture("db-b", smallA with { ItemCount = 20 });

        // Step 2: open the first DB. Assert it's Ready (engine + schema load both succeeded).
        EngineLifecycle a = await EngineLifecycle.OpenAsync(dbAPath);
        try
        {
            Assert.That(a.State, Is.EqualTo(SchemaCompatibility.State.Ready), "First open: schema state should be Ready");
            Assert.That(a.LoadedComponentTypes, Is.GreaterThan(0), "First open: at least one component type registered");
        }
        finally
        {
            // CRITICAL: dispose the first lifecycle before opening the second. The dispose flow:
            //   1. `engine.Dispose()` → `ArchetypeRegistry.UnregisterEngineUse(...)` releases Type refs.
            //   2. `WorkbenchAssemblyLoadContext.Unload()` requests ALC unload.
            //   3. Service provider disposes → MMF file handle released.
            // Without step 1 (the new lifecycle hook), step 2's Unload is a no-op because the registry's
            // strong refs pin the ALC indefinitely.
            a.Dispose();
        }

        // Step 3: open the second DB. THIS is the test that fails pre-fix — the registry state from open
        // #1 collides with open #2's freshly-loaded schema (same archetype IDs, different CLR Type instances
        // from the new ALC). With the fix, the registry was emptied of the first ALC's entries during a's
        // dispose, so the second open re-populates cleanly.
        EngineLifecycle b = await EngineLifecycle.OpenAsync(dbBPath);
        try
        {
            Assert.That(b.State, Is.EqualTo(SchemaCompatibility.State.Ready), "Second open: schema state should be Ready");
            Assert.That(b.LoadedComponentTypes, Is.GreaterThan(0), "Second open: components should re-register cleanly");
            // The opened DB should report the FilePath we asked for (sanity check that we're not still
            // pointing at the first DB).
            Assert.That(b.FilePath, Is.EqualTo(Path.GetFullPath(dbBPath)));
        }
        finally
        {
            b.Dispose();
        }
    }

    /// <summary>
    /// Generates a small fixture database under the test temp root and returns the path to its `.typhon`
    /// marker file. Uses tiny entity counts (100 particles) so the test runs in single-digit seconds.
    /// </summary>
    private string GenerateFixture(string databaseName, FixtureConfig config)
    {
        var outputDir = Path.Combine(_tempRoot, databaseName);
        Directory.CreateDirectory(outputDir);
        var result = FixtureDatabase.CreateOrReuse(
            outputDir,
            force: true,
            config: config,
            progress: null,
            ct: default,
            databaseName: databaseName);
        Assert.That(Directory.Exists(result.TyphonFilePath), Is.True,
            $"Fixture generation failed: {result.TyphonFilePath} bundle not produced");
        return result.TyphonFilePath;
    }
}
