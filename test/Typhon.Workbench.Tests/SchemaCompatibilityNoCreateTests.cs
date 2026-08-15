using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Typhon.Engine;
using Typhon.Schema.Definition;
using Typhon.Workbench.Schema;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Tests;

/// <summary>
/// Opening a database must never ADD components to it.
///
/// <para>The Workbench loads the whole schema assembly, so <c>ClassifyAndRegister</c> used to call
/// <c>RegisterComponentByType</c> for every type the DLL declared — and registering a component the database has never held
/// takes the engine's "Create path", allocating its segments and persisting a <c>ComponentR1</c> row. Inspecting a database
/// therefore rewrote its schema: measured at 20 invented components and a 2.2 MB → 8.4 MB data file on a database that
/// declared six. Those invented segments were then destroyed by the next application write, which had no idea they existed
/// (CK-09) — the corruption that started this investigation.</para>
///
/// <para>The database is the reference; the assembly is checked against it. ADR-055's migrate-on-open is unaffected — it
/// concerns components that ARE present and behind — but a component absent from the file is reported, never invented.</para>
/// </summary>
[TestFixture]
[NonParallelizable] // opens engines via EngineLifecycle.OpenAsync — the schema-compat check reads the process-global ArchetypeRegistry (see #554)
public sealed class SchemaCompatibilityNoCreateTests
{
    private string _tempDir;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "typhon-wb-nocreate-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort */ }
    }

    /// <summary>Creates a database declaring only TWO of the sample assembly's components — the asymmetry the Workbench then meets.</summary>
    private string CreateNarrowDatabase()
    {
        const string name = "narrow";
        var services = new ServiceCollection();
        services
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Error))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddHighResolutionSharedTimer()
            .AddDeadlineWatchdog()
            .AddScopedManagedPagedMemoryMappedFile(o => { o.DatabaseName = name; o.DatabaseDirectory = _tempDir; })
            .AddScopedDatabaseEngine(o => { o.Wal = new WalWriterOptions { UseFUA = false }; });

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();

        dbe.RegisterComponentByType(typeof(Typhon.Samples.Swg.Shard.Transform), schemaValidation: SchemaValidationMode.Enforce);
        dbe.RegisterComponentByType(typeof(Typhon.Samples.Swg.Shard.Faction), schemaValidation: SchemaValidationMode.Enforce);
        dbe.InitializeArchetypes();
        dbe.ForceCheckpoint();

        return Path.Combine(_tempDir, $"{name}.typhon");
    }

    private static long DataFileLength(string bundlePath) => new FileInfo(Path.Combine(bundlePath, "data")).Length;

    [Test]
    public async Task OpeningADatabase_MustNotCreateComponentsItDoesNotContain()
    {
        var bundlePath = CreateNarrowDatabase();
        var sizeBefore = DataFileLength(bundlePath);

        using (var lifecycle = await EngineLifecycle.OpenAsync(bundlePath))
        {
            // Absent components are reported, not invented — and reporting them is not an error state.
            var absent = lifecycle.Diagnostics.Where(d => d.Kind == SchemaCompatibility.NotInDatabaseKind).ToArray();
            Assert.That(absent, Is.Not.Empty,
                "the sample assembly declares far more components than this database holds; each must be reported as absent");
            Assert.That(lifecycle.State, Is.EqualTo(SchemaCompatibility.State.Ready),
                "a database is not broken for lacking components it never had — absent must not escalate to MigrationRequired");

            // Assert on the SCHEMA-ASSEMBLY components specifically. The engine's own system tables (Typhon.Schema.*) are
            // created by the engine on any open and are not what this test is about; counting raw totals would also be
            // fragile against when their rows land in the in-memory dictionary.
            var swgPersisted = lifecycle.Engine.PersistedComponents.Keys
                .Where(k => k.StartsWith("Swg.", StringComparison.Ordinal))
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToArray();
            TestContext.Out.WriteLine("SWG components persisted after open: " + string.Join(", ", swgPersisted));

            Assert.That(swgPersisted, Is.EqualTo(new[] { "Swg.Shard.Faction", "Swg.Shard.Transform" }),
                "opening the database in the Workbench added schema-assembly components to it — the inspector mutated the schema it was inspecting");
        }

        Assert.That(DataFileLength(bundlePath), Is.EqualTo(sizeBefore),
            "opening the database grew its data file — segments were allocated for components the database does not contain");
    }

    /// <summary>
    /// The components the database DOES hold must still register normally — otherwise the fix would trade a mutation bug for
    /// a Workbench that shows nothing. This is the arm that keeps ADR-055's migrate-on-open alive.
    /// </summary>
    [Test]
    public async Task OpeningADatabase_StillRegistersTheComponentsItDoesContain()
    {
        var bundlePath = CreateNarrowDatabase();

        using var lifecycle = await EngineLifecycle.OpenAsync(bundlePath);

        Assert.That(lifecycle.State, Is.EqualTo(SchemaCompatibility.State.Ready));
        Assert.That(lifecycle.LoadedComponentTypes, Is.GreaterThan(0),
            "the components this database actually holds must still be registered and browsable");
    }
}
