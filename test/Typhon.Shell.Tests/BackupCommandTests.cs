using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Spectre.Console;
using Typhon.Engine;
using Typhon.Shell.Commands;

namespace Typhon.Shell.Tests;

/// <summary>
/// <c>typhon backup</c> (#1414, 07 §4): every verb's happy path in text and JSON, and every exit code a script can see — 0 ok, 1 warning, 2 failed,
/// 3 store damaged, 4 refused, 64 usage — over a real closed database and a real store.
/// </summary>
[TestFixture]
public sealed class BackupCommandTests
{
    private const string DatabaseName = "Game";
    private string _root;
    private string _bundle;
    private string _store;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "typhon-shell-backup", Guid.NewGuid().ToString("N"));
        var dbDir = Path.Combine(_root, "db");
        Directory.CreateDirectory(dbDir);
        _bundle = Path.Combine(dbDir, DatabaseName + ".typhon");
        _store = Path.Combine(_root, "store");
        CreateClosedDatabase(dbDir);
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

    [Test]
    [CancelAfter(30_000)]
    public void EveryVerb_HappyPath_InText()
    {
        var create = Create();
        Assert.That(create.Code, Is.EqualTo(BackupCli.ExitOk), create.Text);
        Assert.That(create.Text, Does.Contain("Backed up").And.Contain("point #1").And.Contain("Full"));

        var listSettings = new BackupListCommand.Settings { Store = _store, Format = "text", Verbose = true, Restorable = true };
        var list = Run((c, j) => BackupListCommand.Run(listSettings, c, j));
        Assert.That(list.Code, Is.EqualTo(BackupCli.ExitOk), list.Text);
        Assert.That(list.Text, Does.Contain("1 point(s)").And.Contain("Full").And.Contain("offline").And.Contain("Restorable").And.Contain("yes"));

        var verify = Run((c, j) => BackupVerifyCommand.Run(new BackupVerifyCommand.Settings { Store = _store, Format = "text", Level = "full" }, c, j));
        Assert.That(verify.Code, Is.EqualTo(BackupCli.ExitOk), verify.Text);
        Assert.That(verify.Text, Does.Contain("Ok").And.Contain("Full verify of 1 pack(s)").And.Contain("1 of 1 point(s) restorable"));

        var status = Run((c, j) => BackupStatusCommand.Run(new BackupSettings { Store = _store, Format = "text" }, c, j, DateTime.UtcNow.AddMinutes(5)));
        Assert.That(status.Code, Is.EqualTo(BackupCli.ExitOk), status.Text);
        Assert.That(status.Text, Does.Contain("Ok").And.Contain("Newest point #1").And.Contain("min old").And.Contain("Head age"));

        var to = Path.Combine(_root, "restored");
        var restore = Restore(to);
        Assert.That(restore.Code, Is.EqualTo(BackupCli.ExitOk), restore.Text);
        Assert.That(restore.Text, Does.Contain("Restored point #1"));
        Assert.That(File.Exists(Path.Combine(to, DatabaseName + ".typhon", "data")), Is.True, "the restore wrote the bundle where it said");
    }

    [Test]
    [CancelAfter(30_000)]
    public void EveryVerb_Json_CarriesTheOutcomeAndItsExitCode()
    {
        var create = Json(Create("json"));
        Assert.That(create.GetProperty("exitCode").GetInt32(), Is.EqualTo(0));
        Assert.That(create.GetProperty("point").GetProperty("backupId").GetUInt32(), Is.EqualTo(1));
        Assert.That(create.GetProperty("point").GetProperty("kind").GetString(), Is.EqualTo("full"));

        var list = Json(Run((c, j) => BackupListCommand.Run(new BackupListCommand.Settings { Store = _store, Format = "json", Restorable = true }, c, j)));
        Assert.That(list.GetProperty("points").GetArrayLength(), Is.EqualTo(1));
        Assert.That(list.GetProperty("restorablePoints")[0].GetUInt32(), Is.EqualTo(1));
        Assert.That(list.GetProperty("chains")[0].GetProperty("depth").GetInt32(), Is.EqualTo(1));

        var verify = Json(Run((c, j) => BackupVerifyCommand.Run(new BackupVerifyCommand.Settings { Store = _store, Format = "json", Point = "1" }, c, j)));
        Assert.That(verify.GetProperty("exitCode").GetInt32(), Is.EqualTo(0));
        Assert.That(verify.GetProperty("report").GetProperty("verdict").GetString(), Is.EqualTo("ok"));

        var status = Json(Run((c, j) => BackupStatusCommand.Run(new BackupSettings { Store = _store, Format = "json" }, c, j, DateTime.UtcNow)));
        Assert.That(status.GetProperty("health").GetString(), Is.EqualTo("ok"));
        Assert.That(status.GetProperty("storeBytes").GetInt64(), Is.GreaterThan(0));
        Assert.That(status.GetProperty("latest").GetProperty("backupId").GetUInt32(), Is.EqualTo(1));
        Assert.That(status.GetProperty("chains")[0].GetProperty("headBackupId").GetUInt32(), Is.EqualTo(1));

        var restore = Json(Restore(Path.Combine(_root, "restored"), "json"));
        Assert.That(restore.GetProperty("exitCode").GetInt32(), Is.EqualTo(0));
        Assert.That(restore.GetProperty("result").GetProperty("point").GetProperty("backupId").GetUInt32(), Is.EqualTo(1));
    }

    [Test]
    [CancelAfter(30_000)]
    public void ANewestPackThatCannotBeRead_IsAWarningForStatus_AndAFailureForVerify_WhileAnOlderPointRestores()
    {
        Assert.That(Create().Code, Is.EqualTo(0));
        Assert.That(Create(full: true).Code, Is.EqualTo(0));
        TruncateNewestPack();

        var status = Run((c, j) => BackupStatusCommand.Run(new BackupSettings { Store = _store, Format = "text" }, c, j, DateTime.UtcNow));
        Assert.That(status.Code, Is.EqualTo(BackupCli.ExitWarning), status.Text);
        Assert.That(status.Text, Does.Contain("Warning"));

        var verify = Run((c, j) => BackupVerifyCommand.Run(new BackupVerifyCommand.Settings { Store = _store, Format = "text" }, c, j));
        Assert.That(verify.Code, Is.EqualTo(BackupCli.ExitFailed), verify.Text);
        Assert.That(verify.Text, Does.Contain("Damaged").And.Contain("1 of 2 point(s) restorable"));

        Assert.That(Restore(Path.Combine(_root, "older"), point: "1").Code, Is.EqualTo(BackupCli.ExitOk));
    }

    [Test]
    [CancelAfter(30_000)]
    public void AStoreWithNoRestorablePoint_Exits3_ForStatusVerifyAndRestore()
    {
        Assert.That(Create().Code, Is.EqualTo(0));
        TruncateNewestPack();

        var status = Run((c, j) => BackupStatusCommand.Run(new BackupSettings { Store = _store, Format = "text" }, c, j, DateTime.UtcNow));
        Assert.That(status.Code, Is.EqualTo(BackupCli.ExitStoreDamaged), status.Text);
        Assert.That(status.Text, Does.Contain("Failing"));

        var verify = Run((c, j) => BackupVerifyCommand.Run(new BackupVerifyCommand.Settings { Store = _store, Format = "text" }, c, j));
        Assert.That(verify.Code, Is.EqualTo(BackupCli.ExitStoreDamaged), verify.Text);

        var restore = Restore(Path.Combine(_root, "restored"), "json");
        Assert.That(restore.Code, Is.EqualTo(BackupCli.ExitStoreDamaged), restore.Json);
        Assert.That(Json(restore).GetProperty("error").GetProperty("exitCode").GetInt32(), Is.EqualTo(BackupCli.ExitStoreDamaged));
    }

    [Test]
    [CancelAfter(30_000)]
    public void ADatabaseALiveEngineHolds_IsRefusedWith4_AndTheMessageSaysWhatToDo()
    {
        WriteLock(yieldable: false);

        var create = Create();
        Assert.That(create.Code, Is.EqualTo(BackupCli.ExitRefused), create.Text);
        Assert.That(create.Text, Does.Contain("backup.request").And.Contain("#1416").And.Contain("stop the process"));
        Assert.That(Directory.Exists(_store) && Directory.EnumerateFiles(_store, "*.tpack", SearchOption.AllDirectories).Any(), Is.False,
            "a refused create publishes nothing");
    }

    [Test]
    [CancelAfter(30_000)]
    public void AYieldableHolder_IsAskedToRelease_AndTheBackupProceedsWhenItDoes()
    {
        WriteLock(yieldable: true);
        var holder = Task.Run(() =>
        {
            // The Workbench's side of #621: see the request, let go of the database.
            while (!DatabaseLockFile.HasLiveRequest(_bundle, DateTimeOffset.UtcNow))
            {
                Thread.Sleep(5);
            }

            File.Delete(DatabaseLockFile.PathFor(_bundle));
        });

        var create = Create();
        Assert.That(holder.Wait(TimeSpan.FromSeconds(5)), Is.True);
        Assert.That(create.Code, Is.EqualTo(BackupCli.ExitOk), create.Text);
        Assert.That(File.Exists(DatabaseLockFile.RequestPathFor(_bundle)), Is.False, "the request is withdrawn once served");
    }

    [Test]
    [CancelAfter(30_000)]
    public void AYieldableHolderThatDoesNotRelease_IsRefusedWith4()
    {
        WriteLock(yieldable: true);
        var create = Run((c, j) => BackupCreateCommand.Run(new BackupCreateCommand.Settings
        {
            Bundle = _bundle, Store = _store, Format = "text", HandoffTimeout = TimeSpan.FromMilliseconds(100),
        }, c, j, CancellationToken.None));

        Assert.That(create.Code, Is.EqualTo(BackupCli.ExitRefused), create.Text);
        Assert.That(create.Text, Does.Contain("did not release it"));
        Assert.That(File.Exists(DatabaseLockFile.RequestPathFor(_bundle)), Is.False, "an unanswered request is withdrawn");
    }

    [Test]
    [CancelAfter(30_000)]
    public void ARestoreOntoAnExistingBundle_IsRefusedWith4_UnlessReplace()
    {
        Assert.That(Create().Code, Is.EqualTo(0));
        var to = Path.Combine(_root, "restored");
        Assert.That(Restore(to).Code, Is.EqualTo(BackupCli.ExitOk));

        var again = Restore(to);
        Assert.That(again.Code, Is.EqualTo(BackupCli.ExitRefused), again.Text);

        var replace = new BackupRestoreCommand.Settings { Store = _store, To = to, Replace = true, Format = "text" };
        var replaced = Run((c, j) => BackupRestoreCommand.Run(replace, c, j, CancellationToken.None));
        Assert.That(replaced.Code, Is.EqualTo(BackupCli.ExitOk), replaced.Text);
        Assert.That(replaced.Text, Does.Contain("The bundle that was there is now"));
    }

    [Test]
    [CancelAfter(30_000)]
    public void ABundleThatDoesNotExist_FailsWith2()
    {
        var missing = new BackupCreateCommand.Settings { Bundle = Path.Combine(_root, "nope.typhon"), Store = _store, Format = "text" };
        var create = Run((c, j) => BackupCreateCommand.Run(missing, c, j, CancellationToken.None));
        Assert.That(create.Code, Is.EqualTo(BackupCli.ExitFailed), create.Text);
    }

    [Test]
    public void UsageErrors_Exit64()
    {
        var none = new BackupCreateCommand.Settings { Bundle = _bundle, Format = "text" };
        Assert.That(Run((c, j) => BackupCreateCommand.Run(none, c, j, CancellationToken.None)).Code, Is.EqualTo(BackupCli.ExitUsage), "create without --store");
        Assert.That(Run((c, j) => BackupListCommand.Run(new BackupListCommand.Settings { Format = "text" }, c, j)).Code, Is.EqualTo(BackupCli.ExitUsage));
        Assert.That(Run((c, j) => BackupStatusCommand.Run(new BackupSettings { Format = "text" }, c, j, DateTime.UtcNow)).Code,
            Is.EqualTo(BackupCli.ExitUsage));
        var noTarget = new BackupRestoreCommand.Settings { Store = _store, Format = "text" };
        Assert.That(Run((c, j) => BackupRestoreCommand.Run(noTarget, c, j, CancellationToken.None)).Code, Is.EqualTo(BackupCli.ExitUsage),
            "restore without --to");

        foreach (var (level, point) in new (string Level, string Point)[] { ("deep", null), ("fast", null), (null, "yesterday"), (null, "0") })
        {
            var verify = new BackupVerifyCommand.Settings { Store = _store, Format = "text", Level = level, Point = point };
            var r = Run((c, j) => BackupVerifyCommand.Run(verify, c, j));
            Assert.That(r.Code, Is.EqualTo(BackupCli.ExitUsage), $"level {level}, point {point}: {r.Text}");
        }

        var json = Run((c, j) => BackupListCommand.Run(new BackupListCommand.Settings { Store = _store, Format = "xml" }, c, j));
        Assert.That(json.Code, Is.EqualTo(BackupCli.ExitUsage));
    }

    [Test]
    public void Full_ReachesTheCapture()
    {
        // An offline create cannot show it end to end: only a database an engine backed up before has a lineage to continue incrementally.
        Assert.That(BackupCreateCommand.OptionsFor(new BackupCreateCommand.Settings { Full = true }, CancellationToken.None).Full, Is.True);
        Assert.That(BackupCreateCommand.OptionsFor(new BackupCreateCommand.Settings(), CancellationToken.None).Full, Is.False);
    }

    /// <summary>Through the real command line: routing to each verb, and Spectre's own parse errors under <c>backup</c> exit 64.</summary>
    [Test]
    [CancelAfter(30_000)]
    public void TheCommandLine_RoutesTheVerbs_AndAParseErrorUnderBackupIs64()
    {
        Assert.That(App("backup", "create", _bundle, "--store", _store, "--format", "json"), Is.EqualTo(BackupCli.ExitOk));
        Assert.That(App("backup", "list", "--store", _store, "--format", "json"), Is.EqualTo(BackupCli.ExitOk));
        Assert.That(App("backup", "verify", "--store", _store, "--level", "full", "--format", "json"), Is.EqualTo(BackupCli.ExitOk));
        Assert.That(App("backup", "status", "--store", _store, "--format", "json"), Is.EqualTo(BackupCli.ExitOk));
        Assert.That(App("backup", "restore", "--store", _store, "--point", "latest", "--to", Path.Combine(_root, "r"), "--format", "json"), Is.EqualTo(0));

        Assert.That(App("backup", "list", "--store", _store, "--no-such-option"), Is.EqualTo(BackupCli.ExitUsage));
        Assert.That(App("backup", "create"), Is.EqualTo(BackupCli.ExitUsage), "a missing <bundle>");
        Assert.That(Program.ExitCodeForUnhandled(new InvalidOperationException(), ["backup", "list"]), Is.EqualTo(10), "a defect is not a usage error");
        Assert.That(App("check"), Is.EqualTo(10), "the other verbs keep their codes");
    }

    private static int App(params string[] args) => Program.BuildApp(args).Run(args);

    private (int Code, string Text, string Json) Create(string format = "text", bool full = false)
        => Run((c, j) => BackupCreateCommand.Run(new BackupCreateCommand.Settings { Bundle = _bundle, Store = _store, Format = format, Full = full }, c, j,
            CancellationToken.None));

    private (int Code, string Text, string Json) Restore(string to, string format = "text", string point = null)
        => Run((c, j) => BackupRestoreCommand.Run(new BackupRestoreCommand.Settings { Store = _store, To = to, Point = point, Format = format }, c, j,
            CancellationToken.None));

    private static (int Code, string Text, string Json) Run(Func<IAnsiConsole, TextWriter, int> verb)
    {
        var text = new StringWriter();
        var json = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(text),
        });
        console.Profile.Width = 4000;
        var code = verb(console, json);
        return (code, text.ToString(), json.ToString());
    }

    private static JsonElement Json((int Code, string Text, string Json) run)
    {
        Assert.That(run.Json, Is.Not.Empty, "nothing was written as JSON: " + run.Text);
        return JsonDocument.Parse(run.Json).RootElement;
    }

    private void TruncateNewestPack()
    {
        var newest = Directory.GetFiles(Path.Combine(_store, "packs"), "*.tpack").OrderBy(f => f, StringComparer.Ordinal).Last();
        using var fs = new FileStream(newest, FileMode.Open, FileAccess.ReadWrite);
        fs.SetLength(fs.Length - 64);
    }

    private void WriteLock(bool yieldable)
        => File.WriteAllText(DatabaseLockFile.PathFor(_bundle),
            DatabaseLockFile.SerializeLock(Environment.ProcessId, DateTimeOffset.UtcNow, Environment.MachineName, yieldable));

    private static void CreateClosedDatabase(string directory)
    {
        var services = new ServiceCollection();
        services
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddHighResolutionSharedTimer()
            .AddDeadlineWatchdog()
            .AddScopedManagedPagedMemoryMappedFile(o =>
            {
                o.DatabaseName = DatabaseName;
                o.DatabaseDirectory = directory;
                o.DatabaseCacheSize = 16UL * 1024 * 1024;
            })
            .AddScopedDatabaseEngine(o => o.Wal = new WalWriterOptions { UseFUA = false });
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<DatabaseEngine>().InitializeArchetypes();
    }
}
