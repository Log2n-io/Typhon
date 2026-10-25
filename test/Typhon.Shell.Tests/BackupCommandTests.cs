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

        var status = Run((c, j) =>
            BackupStatusCommand.Run(new BackupStatusCommand.Settings { Store = _store, Format = "text" }, c, j, DateTime.UtcNow.AddMinutes(5)));
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

        var listed = Json(Run((c, j) => BackupListCommand.Run(new BackupListCommand.Settings { Store = _store, Format = "json", Restorable = true }, c, j)));
        Assert.That(listed.GetProperty("exitCode").GetInt32(), Is.EqualTo(0), "every verb's JSON carries its exit code");
        var list = listed.GetProperty("listing");
        Assert.That(list.GetProperty("points").GetArrayLength(), Is.EqualTo(1));
        Assert.That(list.GetProperty("restorablePoints")[0].GetUInt32(), Is.EqualTo(1));
        Assert.That(list.GetProperty("chains")[0].GetProperty("depth").GetInt32(), Is.EqualTo(1));

        var verify = Json(Run((c, j) => BackupVerifyCommand.Run(new BackupVerifyCommand.Settings { Store = _store, Format = "json", Point = "1" }, c, j)));
        Assert.That(verify.GetProperty("exitCode").GetInt32(), Is.EqualTo(0));
        Assert.That(verify.GetProperty("report").GetProperty("verdict").GetString(), Is.EqualTo("ok"));

        var status = Json(Run((c, j) => BackupStatusCommand.Run(new BackupStatusCommand.Settings { Store = _store, Format = "json" }, c, j, DateTime.UtcNow)));
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

        var status = Run((c, j) => BackupStatusCommand.Run(new BackupStatusCommand.Settings { Store = _store, Format = "text" }, c, j, DateTime.UtcNow));
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

        var status = Run((c, j) => BackupStatusCommand.Run(new BackupStatusCommand.Settings { Store = _store, Format = "text" }, c, j, DateTime.UtcNow));
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
        using (var provider = Provider(Path.GetDirectoryName(_bundle), yieldable: false))
        using (var scope = provider.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<DatabaseEngine>().InitializeArchetypes();

            var create = Create();
            Assert.That(create.Code, Is.EqualTo(BackupCli.ExitRefused), create.Text);
            Assert.That(create.Text, Does.Contain($"is open in process {Environment.ProcessId} on {Environment.MachineName}")
                .And.Contain("takes no backup requests").And.Contain("engine.Backup.CreateAsync()").And.Contain("stop the process"));
            Assert.That(File.Exists(DatabaseLockFile.RequestPathFor(_bundle)), Is.False, "a holder that never offered to yield is not asked to");
        }

        Assert.That(Directory.Exists(_store) && Directory.EnumerateFiles(_store, "*.tpack", SearchOption.AllDirectories).Any(), Is.False,
            "a refused create publishes nothing");
    }

    /// <summary>
    /// A yieldable engine (the Workbench, #621) sees the request and closes; the capture takes the lock and only then retires the request — while it
    /// exists, the holder must not take the database back.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void AYieldableHolder_IsAskedToRelease_AndTheBackupProceedsWhenItDoes()
    {
        var provider = Provider(Path.GetDirectoryName(_bundle), yieldable: true);
        var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<DatabaseEngine>().InitializeArchetypes();
        var requestPath = DatabaseLockFile.RequestPathFor(_bundle);
        var lockPath = DatabaseLockFile.PathFor(_bundle);
        bool? requestStoodWhenTheCaptureLocked = null;
        var yielder = Task.Run(() =>
        {
            // Stands in for the Workbench's coordinator: watch for a claim, then let go — and watch the claimant take the lock.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline && !File.Exists(requestPath))
            {
                Thread.Sleep(5);
            }

            scope.Dispose();
            provider.Dispose();
            while (DateTime.UtcNow < deadline && !File.Exists(lockPath))
            {
                Thread.SpinWait(20);
            }

            requestStoodWhenTheCaptureLocked = File.Exists(requestPath);
        });

        var create = Create();
        Assert.That(yielder.Wait(TimeSpan.FromSeconds(10)), Is.True);
        Assert.That(create.Code, Is.EqualTo(BackupCli.ExitOk), create.Text);
        Assert.That(requestStoodWhenTheCaptureLocked, Is.True, "the request was retired before the capture held the lock: the holder could take it back");
        Assert.That(File.Exists(requestPath), Is.False, "the request is retired once the capture is done");
    }

    [Test]
    [CancelAfter(30_000)]
    public void AYieldableHolderThatDoesNotRelease_IsRefusedWith4()
    {
        using var provider = Provider(Path.GetDirectoryName(_bundle), yieldable: true);
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<DatabaseEngine>().InitializeArchetypes();

        var settings = new BackupCreateCommand.Settings { Bundle = _bundle, Store = _store, Format = "text", HandoffTimeout = TimeSpan.FromMilliseconds(150) };
        var create = Run((c, j) => BackupCreateCommand.Run(settings, c, j, CancellationToken.None));
        Assert.That(create.Code, Is.EqualTo(BackupCli.ExitRefused), create.Text);
        Assert.That(create.Text, Does.Contain("advertised that it would yield it but did not release it within 150 ms"));
        Assert.That(File.Exists(DatabaseLockFile.RequestPathFor(_bundle)), Is.False, "an unanswered request is withdrawn");
    }

    /// <summary>
    /// M3: the bundle is resolved once, as the capture resolves it. Given as a stem, the handoff still finds the yieldable holder's lock and request
    /// file in the real bundle; raw, it looked for them beside the stem, found nothing, and the capture then refused a database its holder would
    /// have yielded.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void AStem_IsResolvedOnce_SoTheHandoffFindsTheHolder()
    {
        var provider = Provider(Path.GetDirectoryName(_bundle), yieldable: true);
        var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<DatabaseEngine>().InitializeArchetypes();
        var yielder = Task.Run(() =>
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline && !File.Exists(DatabaseLockFile.RequestPathFor(_bundle)))
            {
                Thread.Sleep(5);
            }

            scope.Dispose();
            provider.Dispose();
        });

        var stem = _bundle[..^".typhon".Length];
        var create = Run((c, j) => BackupCreateCommand.Run(new BackupCreateCommand.Settings { Bundle = stem, Store = _store, Format = "text" }, c, j,
            CancellationToken.None));
        Assert.That(yielder.Wait(TimeSpan.FromSeconds(10)), Is.True);
        Assert.That(create.Code, Is.EqualTo(BackupCli.ExitOk), create.Text);
        Assert.That(create.Text, Does.Contain(_bundle), "the message names the bundle the capture locked");
    }

    /// <summary>The handoff wait honours cancellation (Ctrl+C): it withdraws its request and stops, instead of waiting out the timeout.</summary>
    [Test]
    [CancelAfter(30_000)]
    public void TheHandoffWait_StopsOnCancellation_AndWithdrawsItsRequest()
    {
        using var provider = Provider(Path.GetDirectoryName(_bundle), yieldable: true);
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<DatabaseEngine>().InitializeArchetypes();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var settings = new BackupCreateCommand.Settings { Bundle = _bundle, Store = _store, Format = "json", HandoffTimeout = TimeSpan.FromSeconds(20) };

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var create = Run((c, j) => BackupCreateCommand.Run(settings, c, j, cts.Token));
        Assert.That(clock.Elapsed, Is.LessThan(TimeSpan.FromSeconds(10)), "the wait did not stop on cancellation");
        Assert.That(create.Code, Is.EqualTo(BackupCli.ExitFailed), create.Json);
        Assert.That(File.Exists(DatabaseLockFile.RequestPathFor(_bundle)), Is.False, "a cancelled request is withdrawn");
    }

    /// <summary>
    /// M2: every exception the store throws has an exit code (an unenforced store lock is a refusal, a store out of ids a failure), and in JSON mode
    /// nothing escapes as markup: an exception no code maps is still a JSON error, exit 2.
    /// </summary>
    [Test]
    public void EveryStoreException_HasAnExitCode_AndJsonModeAlwaysAnswersInJson()
    {
        Assert.That(BackupCli.ExitCodeFor(new NotSupportedException("locking is not enforced")), Is.EqualTo(BackupCli.ExitRefused));
        Assert.That(BackupCli.ExitCodeFor(new InvalidOperationException("no backup id left")), Is.EqualTo(BackupCli.ExitFailed));
        Assert.That(BackupCli.ExitCodeFor(new ArgumentException("a defect")), Is.EqualTo(-1), "a defect still reaches the shell's handler in text");

        // A target with a NUL is refused by the runtime (Path.GetFullPath) with an ArgumentException: no store code maps it.
        Assert.That(Create().Code, Is.EqualTo(BackupCli.ExitOk));
        var bad = Restore("restored\0here", "json");
        Assert.That(bad.Code, Is.EqualTo(BackupCli.ExitFailed), bad.Text);
        var error = Json(bad).GetProperty("error");
        Assert.That(error.GetProperty("exitCode").GetInt32(), Is.EqualTo(BackupCli.ExitFailed));
        Assert.That(bad.Text, Is.Empty, "nothing as markup in JSON mode");
    }

    [Test]
    public void TheDefaultHandoffWait_IsTheEngines()
        => Assert.That(new BackupCreateCommand.Settings().HandoffTimeout, Is.EqualTo(new PagedMMFOptions().LockHandoffTimeout));

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
        Assert.That(Run((c, j) => BackupStatusCommand.Run(new BackupStatusCommand.Settings { Format = "text" }, c, j, DateTime.UtcNow)).Code,
            Is.EqualTo(BackupCli.ExitUsage));
        var noTarget = new BackupRestoreCommand.Settings { Store = _store, Format = "text" };
        Assert.That(Run((c, j) => BackupRestoreCommand.Run(noTarget, c, j, CancellationToken.None)).Code, Is.EqualTo(BackupCli.ExitUsage),
            "restore without --to");

        foreach (var (level, point) in new (string Level, string Point)[] { ("fast", null), (null, "yesterday"), (null, "0") })
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
        Assert.That(BackupCreateCommand.OptionsFor(new BackupCreateCommand.Settings { WalDir = "/wal" }, CancellationToken.None).WalDirectory,
            Is.EqualTo("/wal"), "--wal-dir reaches the capture");
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
        Assert.That(App("backup", "verify", "--store", _store, "--level", "deep", "--format", "json"), Is.EqualTo(BackupCli.ExitOk));
        Assert.That(App("backup", "status", "--store", _store, "--format", "json"), Is.EqualTo(BackupCli.ExitOk));
        Assert.That(App("backup", "restore", "--store", _store, "--point", "latest", "--to", Path.Combine(_root, "r"), "--format", "json"), Is.EqualTo(0));
        Assert.That(App("backup", "compact", "--store", _store, "--point", "latest", "--format", "json"), Is.EqualTo(BackupCli.ExitOk));
        Assert.That(App("backup", "prune", "--store", _store, "--min-keep", "1", "--max-age", "30d", "--dry-run", "--format", "json"),
            Is.EqualTo(BackupCli.ExitOk));
        Assert.That(App("backup", "prune", "--store", _store, "--max-count", "2"), Is.EqualTo(BackupCli.ExitUsage), "--min-keep is required");
        Assert.That(App("backup", "drill", "--store", _store, "--scratch", Path.Combine(_root, "scratch"), "--format", "json"), Is.EqualTo(BackupCli.ExitOk));
        Assert.That(App("backup", "drill", "--store", _store), Is.EqualTo(BackupCli.ExitUsage), "--scratch is required");

        Assert.That(App("backup", "list", "--store", _store, "--no-such-option"), Is.EqualTo(BackupCli.ExitUsage));
        Assert.That(App("backup", "create"), Is.EqualTo(BackupCli.ExitUsage), "a missing <bundle>");
        Assert.That(Program.ExitCodeForUnhandled(new InvalidOperationException(), ["backup", "list"]), Is.EqualTo(10), "a defect is not a usage error");
        Assert.That(App("check"), Is.EqualTo(10), "the other verbs keep their codes");
    }

    /// <summary>#1415: compact turns an online incremental into a base; prune previews, then deletes what no kept point needs.</summary>
    [Test]
    [CancelAfter(30_000)]
    public void CompactAndPrune_InTextAndJson()
    {
        OnlineChain(points: 3);

        var compact = Compact("2");
        Assert.That(compact.Code, Is.EqualTo(BackupCli.ExitOk), compact.Text);
        Assert.That(compact.Text, Does.Contain("Compacted point #2").And.Contain("chain of 2 pack(s)").And.Contain("Nothing was deleted"));
        var again = Json(Compact("2", "json"));
        Assert.That(again.GetProperty("exitCode").GetInt32(), Is.EqualTo(BackupCli.ExitOk));
        Assert.That(again.GetProperty("result").GetProperty("alreadyBase").GetBoolean(), Is.True);
        Assert.That(Compact("99").Code, Is.EqualTo(BackupCli.ExitStoreDamaged), "a point that does not resolve");

        var packs = Directory.GetFiles(Path.Combine(_store, "packs")).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        var dry = Prune(new BackupPruneCommand.Settings { MinKeep = 1, MaxCount = 1, DryRun = true });
        Assert.That(dry.Code, Is.EqualTo(BackupCli.ExitOk), dry.Text);
        Assert.That(dry.Text, Does.Contain("Would keep 1 point(s) from #3").And.Contain("Would compact #3").And.Contain("Would delete 4 pack(s)"));
        Assert.That(Directory.GetFiles(Path.Combine(_store, "packs")).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal), Is.EqualTo(packs),
            "a dry run changes nothing");

        var applied = Json(Prune(new BackupPruneCommand.Settings { MinKeep = 1, MaxCount = 1, Format = "json" }));
        Assert.That(applied.GetProperty("exitCode").GetInt32(), Is.EqualTo(BackupCli.ExitOk));
        Assert.That(applied.GetProperty("result").GetProperty("deleted").GetArrayLength(), Is.EqualTo(4));
        Assert.That(Directory.GetFiles(Path.Combine(_store, "packs")).Select(Path.GetFileName), Is.EqualTo(new[] { "00000003-C.tpack" }));
        Assert.That(Prune(new BackupPruneCommand.Settings { MinKeep = 1, MaxCount = 1 }).Text, Does.Contain("Nothing to delete"));
    }

    /// <summary>#1416: drill passes (0) and is recorded, so list shows it; a scratch inside the store is refused (4); a damaged point fails (3).</summary>
    [Test]
    [CancelAfter(30_000)]
    public void Drill_InTextAndJson_ItsRecordInList_AndItsExitCodes()
    {
        Assert.That(Create().Code, Is.EqualTo(BackupCli.ExitOk));
        var scratch = Path.Combine(_root, "scratch");

        var passed = Drill(scratch);
        Assert.That(passed.Code, Is.EqualTo(BackupCli.ExitOk), passed.Text);
        Assert.That(passed.Text, Does.Contain("Drill passed").And.Contain("point #1").And.Contain("Sound").And.Contain("Recorded:"));
        Assert.That(Directory.EnumerateFileSystemEntries(scratch), Is.Empty, "the scratch bundle is deleted");

        var json = Json(Drill(scratch, "json"));
        Assert.That(json.GetProperty("exitCode").GetInt32(), Is.EqualTo(BackupCli.ExitOk));
        var report = json.GetProperty("report");
        Assert.That(report.GetProperty("outcome").GetString(), Is.EqualTo("passed"));
        Assert.That(report.GetProperty("point").GetProperty("backupId").GetUInt32(), Is.EqualTo(1u));
        Assert.That(report.GetProperty("recovery").TryGetProperty("recordsReplayed", out _), Is.True, "the recovery's figures, not an empty object");

        var list = Run((c, j) => BackupListCommand.Run(new BackupListCommand.Settings { Store = _store, Format = "text" }, c, j));
        Assert.That(list.Text, Does.Contain("Drill").And.Contain("passed " + DateTime.UtcNow.ToString("yyyy-MM-dd")));

        var inside = Drill(Path.Combine(_store, "scratch"));
        Assert.That(inside.Code, Is.EqualTo(BackupCli.ExitRefused), inside.Text);
        Assert.That(inside.Text, Does.Contain("ScratchInsideStore"));

        // An unreadable pack is no point to drill (nothing to record against): the chain error, exit 3. A damaged page of a readable one is a recorded
        // Failed(Restore) (engine BackupDrillTests).
        TruncateNewestPack();
        var damaged = Drill(scratch);
        Assert.That(damaged.Code, Is.EqualTo(BackupCli.ExitStoreDamaged), damaged.Text);
        Assert.That(damaged.Text, Does.Contain("unreadable"));
    }

    [Test]
    [CancelAfter(30_000)]
    public void APruneThatCannotMeetALimitWithoutBreakingMinKeep_Exits1_AndSaysWhy()
    {
        Assert.That(Create().Code, Is.EqualTo(BackupCli.ExitOk));
        Assert.That(Create(full: true).Code, Is.EqualTo(BackupCli.ExitOk));

        var blocked = Prune(new BackupPruneCommand.Settings { MinKeep = 2, MaxBytes = "1" });
        Assert.That(blocked.Code, Is.EqualTo(BackupCli.ExitWarning), blocked.Text);
        Assert.That(blocked.Text, Does.Contain("MinKeep").And.Contain("Nothing to delete"));
    }

    /// <summary>
    /// A compaction is the one routine full read of a kept chain: one that finds a source pack damaged exits 3 (a kept point may not restore), not 0; one
    /// that fails otherwise exits 2; the worst code wins.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void APruneWhoseCompactionFindsDamage_Exits3_AndAFailedOneExits2()
    {
        OnlineChain(points: 3);
        var packs = Path.Combine(_store, "packs");
        DamageAPageOnlyIn(Path.Combine(packs, "00000001-F.tpack"), Path.Combine(packs, "00000002-I.tpack"), Path.Combine(packs, "00000003-I.tpack"));

        var damaged = Prune(new BackupPruneCommand.Settings { MinKeep = 1, MaxCount = 1 });
        Assert.That(damaged.Code, Is.EqualTo(BackupCli.ExitStoreDamaged), damaged.Text);
        Assert.That(damaged.Text, Does.Contain("compaction of #3 found its chain damaged"));
        Assert.That(File.Exists(Path.Combine(packs, "00000001-F.tpack")), Is.True, "its chain keeps every pack");

        static BackupCompactionFailure F(BackupFailureKind kind) => new(1, kind, "x");
        Assert.That(BackupPruneCommand.ExitCodeOf(BackupRetentionBlock.None, [F(BackupFailureKind.Failed)]), Is.EqualTo(BackupCli.ExitFailed));
        Assert.That(BackupPruneCommand.ExitCodeOf(BackupRetentionBlock.MinKeep, [F(BackupFailureKind.Failed), F(BackupFailureKind.StoreDamaged)]),
            Is.EqualTo(BackupCli.ExitStoreDamaged));
        Assert.That(BackupPruneCommand.ExitCodeOf(BackupRetentionBlock.DamagedChain, [F(BackupFailureKind.Failed)]), Is.EqualTo(BackupCli.ExitStoreDamaged));
        Assert.That(BackupPruneCommand.ExitCodeOf(BackupRetentionBlock.MinKeep, null), Is.EqualTo(BackupCli.ExitWarning));
    }

    /// <summary>Flips a byte of the frame of a page <paramref name="pack"/> holds and none of <paramref name="newer"/> does.</summary>
    private static void DamageAPageOnlyIn(string pack, params string[] newer)
    {
        var shadowed = new System.Collections.Generic.HashSet<uint>();
        foreach (var n in newer)
        {
            using var reader = Typhon.Engine.Internals.PackReader.Open(Typhon.Engine.Internals.StoreFileSystem.Instance, n);
            foreach (var e in reader.LoadIndex())
            {
                shadowed.Add(e.PageId);
            }
        }

        long at;
        using (var reader = Typhon.Engine.Internals.PackReader.Open(Typhon.Engine.Internals.StoreFileSystem.Instance, pack))
        {
            at = reader.LoadIndex().First(e => e.StoredLength is > 0 and <= 8192 && !shadowed.Contains(e.PageId)).DataOffset + 1;
        }

        using var file = new FileStream(pack, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        file.Position = at;
        var b = file.ReadByte();
        file.Position = at;
        file.WriteByte((byte)(b ^ 0x5A));
    }

    [Test]
    public void PruneUsageErrors_Exit64_AndAChangedPlanIsARefusal()
    {
        foreach (var (settings, what) in new (BackupPruneCommand.Settings, string)[]
                 {
                     (new BackupPruneCommand.Settings { MaxCount = 3 }, "--min-keep"),
                     (new BackupPruneCommand.Settings { MinKeep = 1 }, "No limit"),
                     (new BackupPruneCommand.Settings { MinKeep = 1, MaxAge = "7x" }, "Unknown duration"),
                     (new BackupPruneCommand.Settings { MinKeep = 1, MaxBytes = "12XB" }, "Unknown size"),
                     (new BackupPruneCommand.Settings { MinKeep = 1, MaxCount = 3, CompactAt = 1 }, "--compact-at"),
                     (new BackupPruneCommand.Settings { MinKeep = 3, MaxCount = 1 }, "below --min-keep"),
                 })
        {
            var r = Prune(settings);
            Assert.That(r.Code, Is.EqualTo(BackupCli.ExitUsage), what);
            Assert.That(r.Text, Does.Contain(what));
        }

        Assert.That(BackupCli.ExitCodeFor(new BackupPlanChangedException("/s", "aa", "bb")), Is.EqualTo(BackupCli.ExitRefused));
        Assert.That(BackupPruneCommand.ExitCodeOf(BackupRetentionBlock.DamagedChain), Is.EqualTo(BackupCli.ExitStoreDamaged));
        Assert.That(BackupPruneCommand.ExitCodeOf(BackupRetentionBlock.InsufficientSpace), Is.EqualTo(BackupCli.ExitWarning));
    }

    [TestCase("30d", 30 * 86_400)]
    [TestCase("12h", 12 * 3_600)]
    [TestCase("90m", 90 * 60)]
    [TestCase("45s", 45)]
    [TestCase("0s", 0)]
    public void Durations_Parse(string value, int seconds)
    {
        Assert.That(BackupCli.TryParseDuration(value, out var d, out var error), Is.True, error);
        Assert.That(d, Is.EqualTo(TimeSpan.FromSeconds(seconds)));
    }

    [TestCase("500GiB", 500L << 30)]
    [TestCase("2TB", 2_000_000_000_000L)]
    [TestCase("1048576", 1_048_576L)]
    [TestCase("1.5KiB", 1_536L)]
    [TestCase("10mb", 10_000_000L)]
    public void Sizes_Parse(string value, long bytes)
    {
        Assert.That(BackupCli.TryParseBytes(value, out var b, out var error), Is.True, error);
        Assert.That(b, Is.EqualTo(bytes));
    }

    [TestCase("7")]
    [TestCase("d")]
    [TestCase("-3h")]
    [TestCase("1.5h")]
    public void ABadDuration_IsRefused(string value) => Assert.That(BackupCli.TryParseDuration(value, out _, out _), Is.False);

    /// <summary>
    /// #1416 (DL-24, AC-O2): with a running engine whose backup takes requests, <c>create --wait</c> asks it through <c>backup.request</c> and reports the
    /// point it took; <c>status &lt;bundle&gt;</c> is its health (Ok 0; Degraded 1 once stale, judged at read time; a stopped engine still answers).
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void CreateWait_AsksTheRunningEngine_AndTheBundlesStatusIsItsHealth()
    {
        var backup = new BackupOptions { StorePath = _store, Interval = TimeSpan.FromHours(1), Retention = null };
        using (var provider = Provider(Path.GetDirectoryName(_bundle), yieldable: false, backup))
        using (var scope = provider.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<DatabaseEngine>().InitializeArchetypes();
            WaitForStatusFile();

            var create = new BackupCreateCommand.Settings { Bundle = _bundle, Wait = true, Format = "json", PollInterval = TimeSpan.FromMilliseconds(20) };
            var created = Json(Run((c, j) => BackupCreateCommand.Run(create, c, j, CancellationToken.None)));
            Assert.That(created.GetProperty("exitCode").GetInt32(), Is.EqualTo(BackupCli.ExitOk), created.ToString());
            Assert.That(created.GetProperty("point").GetProperty("backupId").GetUInt32(), Is.EqualTo(1));
            Assert.That(BackupOperations.List(_store).Points, Has.Count.EqualTo(1), "the engine took it into its own store");

            var status = Run((c, j) => BackupStatusCommand.Run(new BackupStatusCommand.Settings { Bundle = _bundle, Format = "text" }, c, j, DateTime.UtcNow));
            Assert.That(status.Code, Is.EqualTo(BackupCli.ExitOk), status.Text);
            Assert.That(status.Text, Does.Contain("Ok").And.Contain("takes requests").And.Contain("Newest point #1"));
        }

        var stale = Json(Run((c, j) => BackupStatusCommand.Run(new BackupStatusCommand.Settings { Bundle = _bundle, Format = "json" }, c, j,
            DateTime.UtcNow.AddHours(3))));
        Assert.That(stale.GetProperty("exitCode").GetInt32(), Is.EqualTo(BackupCli.ExitWarning), "Degraded is a warning-level exit");
        Assert.That(stale.GetProperty("report").GetProperty("health").GetString(), Is.EqualTo("degraded"));
        Assert.That(stale.GetProperty("report").GetProperty("engineLive").GetBoolean(), Is.False);

        File.Delete(Path.Combine(_bundle, "backup.status.json"));
        var off = Run((c, j) => BackupStatusCommand.Run(new BackupStatusCommand.Settings { Bundle = _bundle, Format = "text" }, c, j, DateTime.UtcNow));
        Assert.That(off.Code, Is.EqualTo(BackupCli.ExitStoreDamaged), "no backup status is Off: a monitored database with no backup is not healthy");
        var missing = Run((c, j) => BackupStatusCommand.Run(new BackupStatusCommand.Settings { Bundle = Path.Combine(_root, "nope.typhon"), Format = "text" },
            c, j, DateTime.UtcNow));
        Assert.That(missing.Code, Is.EqualTo(BackupCli.ExitFailed), "no such bundle: the command failed");
    }

    [Test]
    [CancelAfter(30_000)]
    public void StaleAfter_DegradesAStoresStatus()
    {
        Assert.That(Create().Code, Is.EqualTo(BackupCli.ExitOk));
        var settings = new BackupStatusCommand.Settings { Store = _store, StaleAfter = "1h", Format = "text" };
        Assert.That(Run((c, j) => BackupStatusCommand.Run(settings, c, j, DateTime.UtcNow)).Code, Is.EqualTo(BackupCli.ExitOk));
        var stale = Run((c, j) => BackupStatusCommand.Run(settings, c, j, DateTime.UtcNow.AddHours(2)));
        Assert.That(stale.Code, Is.EqualTo(BackupCli.ExitWarning), stale.Text);
        Assert.That(stale.Text, Does.Contain("Degraded").And.Contain("older than"));
        Assert.That(BackupCli.ExitCodeOf(BackupHealth.Off), Is.EqualTo(BackupCli.ExitStoreDamaged));
        Assert.That(BackupCli.ExitCodeOf(BackupHealth.Failing), Is.EqualTo(BackupCli.ExitStoreDamaged));
        Assert.That(BackupCli.ExitCodeFor(new TyphonException(TyphonErrorCode.BackupNotConfigured, "x")), Is.EqualTo(BackupCli.ExitRefused));
    }

    /// <summary>The engine's scheduler writes the status file on its first tick, on its own thread: waited for, bounded.</summary>
    private void WaitForStatusFile()
    {
        var path = Path.Combine(_bundle, "backup.status.json");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!File.Exists(path))
        {
            Assert.That(DateTime.UtcNow, Is.LessThan(deadline), "the engine never wrote its status file");
            Task.Delay(10).Wait();
        }
    }

    private static int App(params string[] args) => Program.BuildApp(args).Run(args);

    private (int Code, string Text, string Json) Compact(string point, string format = "text")
        => Run((c, j) => BackupCompactCommand.Run(new BackupCompactCommand.Settings { Store = _store, Point = point, Format = format }, c, j,
            CancellationToken.None));

    private (int Code, string Text, string Json) Drill(string scratch, string format = "text")
        => Run((c, j) => BackupDrillCommand.Run(new BackupDrillCommand.Settings { Store = _store, Scratch = scratch, Format = format }, c, j,
            CancellationToken.None));

    private (int Code, string Text, string Json) Prune(BackupPruneCommand.Settings settings)
    {
        settings.Store ??= _store;
        settings.Format ??= "text";
        return Run((c, j) => BackupPruneCommand.Run(settings, c, j, CancellationToken.None));
    }

    /// <summary>A chain the engine captures online (its lineage continues in-session): a full point, then incrementals.</summary>
    private void OnlineChain(int points)
    {
        using var provider = Provider(Path.GetDirectoryName(_bundle), yieldable: false);
        using var scope = provider.CreateScope();
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.InitializeArchetypes();
        for (var i = 0; i < points; i++)
        {
            var point = dbe.Backup.CreateAsync(_store).GetAwaiter().GetResult();
            Assert.That(point.Kind, Is.EqualTo(i == 0 ? BackupPointKind.Full : BackupPointKind.Incremental), "premise: one online chain");
        }
    }

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
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false },   // under GitHub Actions, its enricher turns ANSI back on
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

    private static void CreateClosedDatabase(string directory)
    {
        using var provider = Provider(directory, yieldable: false);
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<DatabaseEngine>().InitializeArchetypes();
    }

    private static ServiceProvider Provider(string directory, bool yieldable, BackupOptions backup = null)
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
                o.YieldableLock = yieldable;
            })
            .AddScopedDatabaseEngine(o =>
            {
                o.Wal = new WalWriterOptions { UseFUA = false };
                o.Backup = backup;
            });
        return services.BuildServiceProvider();
    }
}
