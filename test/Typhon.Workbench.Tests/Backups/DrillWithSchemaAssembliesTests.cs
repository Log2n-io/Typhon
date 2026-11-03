using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Typhon.Engine;
using Typhon.Workbench.Backups;
using Typhon.Workbench.Dtos.Backups;
using Typhon.Workbench.Schema;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Tests.Backups;

/// <summary>
/// #1485 with real builds of an application's schema assembly (<c>drill-schema/v1</c> 1.0.0.0, <c>v2</c> 2.0.0.0, <c>v2b</c> 2.0.0.0 with another layout —
/// <c>DrillSchemaBuilds.targets</c>): a point whose WAL tail holds the application's records drills with the build that wrote it, in the drill's own process;
/// an older build, a newer build, a build whose version lies, a missing assembly — each ends the drill as an Error naming what differs, and so does the
/// settle of a restore. A Workbench session never loads another build in place of the one the database names, so it never records it over it.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class DrillWithSchemaAssembliesTests
{
    private const string FixtureAssembly = "Typhon.Backup.DrillSchemaFixture";
    private const string Seeder = "Typhon.Backup.DrillSchemaFixture.DrillSeeder";

    private string _root;
    private string _bundle;
    private string _store;
    private string _scratch;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "typhon-wb-drill-schema", Guid.NewGuid().ToString("N"));
        _bundle = Path.Combine(_root, "app.typhon");
        _store = Path.Combine(_root, "store");
        _scratch = Path.Combine(_root, "scratch");
        Directory.CreateDirectory(_root);
        Assert.That(File.Exists(VariantDll("v2")), Is.True, "premise: the drill schema variants are built beside the tests (DrillSchemaBuilds.targets)");
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private static string Variant(string name) => Path.Combine(AppContext.BaseDirectory, "drill-schema", name);

    private static string VariantDll(string name) => Path.Combine(Variant(name), FixtureAssembly + ".dll");

    /// <summary>
    /// The application, built as <paramref name="variant"/>, writes its database: units, a checkpoint, more units, then a hard crash; then an offline point
    /// of the crashed bundle, whose WAL tail holds the units spawned after the checkpoint — records only that application's assembly can replay.
    /// </summary>
    private void WriteWith(string variant)
    {
        using (var alc = new WorkbenchAssemblyLoadContext($"drill-seed-{variant}-{Guid.NewGuid():N}"))
        {
            var seeder = SchemaLoader.LoadSchemaDlls(alc, [VariantDll(variant)]).Assemblies.Single().GetType(Seeder, throwOnError: true);
            using var services = Services();
            var engine = services.GetRequiredService<DatabaseEngine>();
            engine.UseArchetypesFrom(alc);   // #554: the application's archetypes are its load context's, not this test's
            seeder.GetMethod("Open").Invoke(null, [engine]);
            seeder.GetMethod("Spawn").Invoke(null, [engine, 10, 0]);
            Assert.That(engine.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(30)), Is.True);
            seeder.GetMethod("Spawn").Invoke(null, [engine, 5, 100]);
            engine.SimulateHardCrash();
        }

        var point = BackupOperations.Create(_bundle, _store);
        Assert.That(point.TailSegments, Is.GreaterThan(0), "premise: the point's tail holds the application's records");
    }

    private ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddHighResolutionSharedTimer()
            .AddDeadlineWatchdog()
            .AddManagedPagedMMF(o =>
            {
                o.DatabaseName = "app";
                o.DatabaseDirectory = _root;
                o.DatabaseCacheSize = 16UL << 20;
            })
            .AddDatabaseEngine(o => o.Wal = new WalWriterOptions { UseFUA = false });
        return services.BuildServiceProvider();
    }

    private BackupDrillReportDto Drill(params string[] schema) =>
        BackupChildProcess.Drill(_store, "latest", _scratch, schema, null, CancellationToken.None);

    [Test]
    [CancelAfter(60_000)]
    public void TheBuildThatWroteThePoint_DrillsIt_AndTheReportSaysWhichBuildItRanWith()
    {
        WriteWith("v2");

        var report = Drill(Variant("v2"));

        Assert.That((report.Outcome, report.FailedStage), Is.EqualTo(("Passed", "None")), report.Error);
        Assert.That(report.Recovery.RecordsReplayed, Is.GreaterThan(0), "the tail's records were replayed by the application's assembly");
        Assert.That(report.Assemblies, Is.EqualTo(new[] { $"{FixtureAssembly} 2.0.0.0 (unsigned)" }));
    }

    [Test]
    [CancelAfter(60_000)]
    public void AnOlderBuild_IsAnError_NamingBothVersions()
    {
        WriteWith("v2");

        var report = Drill(Variant("v1"));

        Assert.That((report.Outcome, report.FailedStage), Is.EqualTo(("Error", "Open")));
        Assert.That(report.Error, Does.Contain(FixtureAssembly).And.Contain("written by 2.0.0.0").And.Contain("is 1.0.0.0"));
    }

    [Test]
    [CancelAfter(60_000)]
    public void ANewerBuild_IsAnError_NamingBothVersions()
    {
        WriteWith("v1");

        var report = Drill(Variant("v2"));

        Assert.That((report.Outcome, report.FailedStage), Is.EqualTo(("Error", "Open")));
        Assert.That(report.Error, Does.Contain("written by 1.0.0.0").And.Contain("is 2.0.0.0"));
    }

    /// <summary>The same assembly version, another layout: the version number lies, the exact schema check does not.</summary>
    [Test]
    [CancelAfter(60_000)]
    public void ABuildAtTheSameVersion_WithAnotherLayout_IsAnError_NamingTheField()
    {
        WriteWith("v2");

        var report = Drill(Variant("v2b"));

        Assert.That((report.Outcome, report.FailedStage), Is.EqualTo(("Error", "Open")));
        Assert.That(report.Error, Does.Contain("Typhon.DrillFixture.Position").And.Contain("field 'Z'"));
    }

    /// <summary>
    /// The same version again, another archetype set: the build adds a component to the archetype the point holds. The Workbench does not register a
    /// component the database never stored, so the archetype is unwired — the open must refuse it, not skip it and pass on the records it can replay.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    public void ABuildAtTheSameVersion_WithAnotherArchetypeSet_IsAnError_NamingTheArchetype()
    {
        WriteWith("v2");

        var report = Drill(Variant("v2c"));

        Assert.That((report.Outcome, report.FailedStage), Is.EqualTo(("Error", "Open")), report.Error);
        Assert.That(report.Error, Does.Contain("does not match the database exactly").And.Contain("archetype").And.Contain("DrillUnit"),
            "refused as another build, not left to the replay (7012), which a point without the archetype's records would never reach");
    }

    /// <summary>A file the operator names is the build they mean: another build is refused, even with the right one in a directory also given.</summary>
    [Test]
    [CancelAfter(60_000)]
    public void ANamedFileOfAnotherBuild_IsAnError_EvenWithTheRightBuildFoundElsewhere()
    {
        WriteWith("v2");

        var report = Drill(VariantDll("v1"), Variant("v2"));

        Assert.That((report.Outcome, report.FailedStage), Is.EqualTo(("Error", "Open")), report.Error);
        Assert.That(report.Error, Does.Contain("written by 2.0.0.0").And.Contain("is 1.0.0.0"));
    }

    [Test]
    [CancelAfter(60_000)]
    public void AMissingAssembly_IsAnError_NamingIt()
    {
        WriteWith("v2");
        var empty = Path.Combine(_root, "no-schema-here");
        Directory.CreateDirectory(empty);

        var report = Drill(empty);

        Assert.That((report.Outcome, report.FailedStage), Is.EqualTo(("Error", "Open")));
        Assert.That(report.Error, Does.Contain($"assembly {FixtureAssembly}: not found"));
    }

    /// <summary>A matching build does not mask a damaged point: the drill still fails.</summary>
    [Test]
    [CancelAfter(60_000)]
    public void ADamagedPoint_StillFails_WithTheBuildThatWroteIt()
    {
        WriteWith("v2");
        var pack = Directory.GetFiles(Path.Combine(_store, "packs"), "*.tpack").Single();
        var bytes = File.ReadAllBytes(pack);
        bytes[bytes.Length / 2] ^= 0x5A;
        File.WriteAllBytes(pack, bytes);

        var report = Drill(Variant("v2"));

        Assert.That(report.Outcome, Is.EqualTo("Failed"), report.Error);
    }

    /// <summary>The settle of a restore (the Workbench's settle child) is exact too: another build is refused with the reason, the right one settles.</summary>
    [Test]
    [CancelAfter(60_000)]
    public void ARestoresSettle_RefusesAnotherBuild_AndSettlesWithTheOneThatWroteIt()
    {
        WriteWith("v2");
        var restored = BackupOperations.Restore(_store, PointSelector.Latest, Path.Combine(_root, "restored"));

        var refused = Assert.Throws<BackupJobException>(() => BackupChildProcess.Settle(restored.Target, [Variant("v1")]));
        Assert.That(refused.Message, Does.Contain("written by 2.0.0.0"));

        var settled = BackupChildProcess.Settle(restored.Target, [Variant("v2")]);
        Assert.That((settled.RecoveryRequired, settled.CrashRecoveryRan), Is.EqualTo((true, true)));
        Assert.That(settled.Recovery.RecordsReplayed, Is.GreaterThan(0));
    }

    /// <summary>
    /// A Workbench session given only another build of the assembly the database names does not load it (it would migrate the database and record that
    /// build in its manifest): the banner names both versions, and the manifest still names the build that wrote it.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    public async Task ASession_NeverLoadsAnotherBuild_InPlaceOfTheOneTheDatabaseNames()
    {
        WriteWith("v2");
        Assert.That(BackupOperations.ReadSchemaManifest(_bundle).Single().Version, Is.EqualTo(new Version(2, 0, 0, 0)), "premise");

        using (var lifecycle = await EngineLifecycle.OpenAsync(_bundle, null, [Variant("v1")]))
        {
            Assert.That(lifecycle.State, Is.EqualTo(SchemaCompatibility.State.Incompatible));
            var mismatch = lifecycle.Diagnostics.Single(d => d.Kind == "version_mismatch");
            Assert.That(mismatch.Detail, Does.Contain("1.0.0.0").And.Contain("2.0.0.0"));
            Assert.That(lifecycle.ResolvedSchemaPaths, Is.Empty, "the other build was not loaded");
        }

        Assert.That(BackupOperations.ReadSchemaManifest(_bundle).Single().Version, Is.EqualTo(new Version(2, 0, 0, 0)), "the manifest was not rewritten");
    }
}
