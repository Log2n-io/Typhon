using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Spectre.Console;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using Typhon.Engine;
using Typhon.Shell.Commands;
using Typhon.Workbench.Schema;

namespace Typhon.Shell.Tests;

/// <summary>
/// #1485 through the command line: <c>typhon backup drill --schema</c> drills a point whose WAL tail holds an application's records with that application's
/// assemblies, exactly (the drill schema variants, <c>DrillSchemaBuilds.targets</c>), and refuses another build with the reason; <c>typhon backup restore</c>,
/// which does not open what it restores, says which build to open it with.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class BackupDrillSchemaCommandTests
{
    private const string FixtureAssembly = "Typhon.Backup.DrillSchemaFixture";

    private string _root;
    private string _bundle;
    private string _store;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "typhon-shell-drill-schema", Guid.NewGuid().ToString("N"));
        _bundle = Path.Combine(_root, "app.typhon");
        _store = Path.Combine(_root, "store");
        Directory.CreateDirectory(_root);
        WriteWithV2();
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    private static string Variant(string name) => Path.Combine(AppContext.BaseDirectory, "drill-schema", name);

    /// <summary>The application, built as 2.0.0.0, writes units, checkpoints, writes more, crashes; an offline point of it holds those in its tail.</summary>
    private void WriteWithV2()
    {
        using (var alc = new WorkbenchAssemblyLoadContext($"drill-seed-{Guid.NewGuid():N}"))
        {
            var seeder = SchemaLoader.LoadSchemaDlls(alc, [Path.Combine(Variant("v2"), FixtureAssembly + ".dll")]).Assemblies.Single()
                .GetType("Typhon.Backup.DrillSchemaFixture.DrillSeeder", throwOnError: true);
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
            using var provider = services.BuildServiceProvider();
            var engine = provider.GetRequiredService<DatabaseEngine>();
            engine.UseArchetypesFrom(alc);   // #554: the application's archetypes are its load context's, not this test's
            seeder.GetMethod("Open").Invoke(null, [engine]);
            seeder.GetMethod("Spawn").Invoke(null, [engine, 10, 0]);
            Assert.That(engine.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(30)), Is.True);
            seeder.GetMethod("Spawn").Invoke(null, [engine, 5, 100]);
            engine.SimulateHardCrash();
        }

        Assert.That(BackupOperations.Create(_bundle, _store).TailSegments, Is.GreaterThan(0), "premise: the tail holds the application's records");
    }

    private (int Code, string Text) Drill(string schema)
    {
        var text = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(text),
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false },
        });
        console.Profile.Width = 4000;
        var settings = new BackupDrillCommand.Settings { Store = _store, Scratch = Path.Combine(_root, "scratch"), Schema = [schema], Format = "text" };
        var code = BackupDrillCommand.Run(settings, console, new StringWriter(), CancellationToken.None);
        return (code, text.ToString());
    }

    [Test]
    [CancelAfter(60_000)]
    public void Drill_WithTheBuildThatWroteThePoint_Passes_AndSaysWhichBuildItRanWith()
    {
        var (code, text) = Drill(Variant("v2"));

        Assert.That(code, Is.EqualTo(BackupCli.ExitOk), text);
        Assert.That(text, Does.Contain("Drill passed").And.Contain($"Ran with {FixtureAssembly} 2.0.0.0 (unsigned"));
    }

    [Test]
    [CancelAfter(60_000)]
    public void Drill_WithAnotherBuild_CouldNotRun_AndNamesBothVersions()
    {
        var (code, text) = Drill(Variant("v1"));

        Assert.That(code, Is.EqualTo(BackupCli.ExitFailed), text);
        Assert.That(text, Does.Contain("could not run").And.Contain("written by 2.0.0.0").And.Contain("is 1.0.0.0"));
    }

    [Test]
    [CancelAfter(60_000)]
    public void Restore_SaysWhichBuildToOpenTheRestoredDatabaseWith()
    {
        var text = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(text),
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false },
        });
        console.Profile.Width = 4000;
        var settings = new BackupRestoreCommand.Settings { Store = _store, To = Path.Combine(_root, "restored"), Format = "text" };

        var code = BackupRestoreCommand.Run(settings, console, new StringWriter(), CancellationToken.None);

        Assert.That(code, Is.EqualTo(BackupCli.ExitOk), text.ToString());
        Assert.That(text.ToString(), Does.Contain($"Open it with the schema assemblies that wrote it, exactly: {FixtureAssembly} 2.0.0.0 (unsigned)."));
    }
}
