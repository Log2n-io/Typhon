using System;
using System.IO;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using Typhon.Engine;
using Typhon.Shell.Commands;

namespace Typhon.Shell.Tests;

/// <summary>
/// <c>typhon backup restore --lsn | --time [--timeline] [--dry-run]</c> (#1418, 07 §4): a restore to an LSN or an instant over a real archive, the plan of
/// one without writing anything, and the exit codes a script sees — 3 for a target the archive cannot reach (naming what can be), 64 for usage.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class BackupRestoreLsnCommandTests
{
    private static ArchivedStoreKit _kit;

    [OneTimeSetUp]
    public void Build() => _kit = ArchivedStoreKit.Build();

    [OneTimeTearDown]
    public void Destroy() => _kit?.Dispose();

    private static (int Code, string Text, string Json) Restore(string to, string format = "text", string point = null, string lsn = null, string time = null,
        string timeline = null, bool dryRun = false) =>
        ArchivedStoreKit.Run((c, j) => BackupRestoreCommand.Run(new BackupRestoreCommand.Settings
        {
            Store = _kit.Store, To = to, Format = format, Point = point, Lsn = lsn, Time = time, Timeline = timeline, DryRun = dryRun,
        }, c, j, CancellationToken.None));

    private static string Target(string name) => Path.Combine(_kit.Root, "restored-" + name);

    [Test]
    [CancelAfter(30_000)]
    public void ARestoreToAnLsn_HoldsEveryCommitAtOrBelowIt()
    {
        var target = _kit.CommitLsns[7];
        var to = Target("lsn");
        var run = Restore(to, "json", lsn: target.ToString());
        var json = ArchivedStoreKit.Json(run);
        Assert.That(run.Code, Is.EqualTo(BackupCli.ExitOk), run.Json);
        var result = json.GetProperty("result");
        Assert.That(result.GetProperty("effectiveLsn").GetInt64(), Is.EqualTo(target), "a commit's LSN ends its frame: the cut is exact");
        Assert.That(result.GetProperty("targetLsn").GetInt64(), Is.EqualTo(target));
        Assert.That(result.GetProperty("timelineId").GetGuid(), Is.EqualTo(_kit.Timeline));
        Assert.That(result.GetProperty("archivedSegments").GetInt32(), Is.GreaterThan(0));
        Assert.That(_kit.CountRestored(to), Is.EqualTo(8), "the commits at or below the LSN, no other");

        var between = Restore(Target("between"), lsn: (_kit.CommitLsns[3] + 1).ToString());
        Assert.That(between.Code, Is.EqualTo(BackupCli.ExitOk), between.Text);
        Assert.That(between.Text, Does.Contain($"up to LSN {_kit.CommitLsns[3]}").And.Contain($"asked {_kit.CommitLsns[3] + 1}"));
        Assert.That(_kit.CountRestored(Target("between")), Is.EqualTo(4));
    }

    [Test]
    [CancelAfter(30_000)]
    public void ARestoreToAnInstant_RestoresTheLsnItResolvesTo()
    {
        var instant = _kit.TickTimes[2].AddSeconds(5);
        var expected = BackupOperations.ResolveTime(_kit.Store, instant);
        Assert.That(expected.Lsn, Is.GreaterThanOrEqualTo(_kit.PointLsn), "premise: the instant lies inside the archive");
        var to = Target("time");
        var run = Restore(to, "json", time: instant.ToString("yyyy-MM-ddTHH:mm:ss") + "+00:00");
        Assert.That(run.Code, Is.EqualTo(BackupCli.ExitOk), run.Json);
        var result = ArchivedStoreKit.Json(run).GetProperty("result");
        Assert.That(result.GetProperty("targetLsn").GetInt64(), Is.EqualTo(expected.Lsn));
        Assert.That(_kit.CountRestored(to), Is.EqualTo(_kit.CommitsThrough(result.GetProperty("effectiveLsn").GetInt64())));
    }

    [Test]
    [CancelAfter(30_000)]
    public void ADryRun_PlansWithoutWriting_AndReportsWhatTheRestoreWouldRefuse()
    {
        var to = Target("dry");
        var run = Restore(to, "json", lsn: _kit.CommitLsns[5].ToString(), dryRun: true);
        Assert.That(run.Code, Is.EqualTo(BackupCli.ExitOk), run.Json);
        var plan = ArchivedStoreKit.Json(run).GetProperty("plan");
        Assert.That(plan.GetProperty("pointLsn").GetInt64(), Is.EqualTo(_kit.CommitLsns[5]));
        Assert.That(plan.GetProperty("fingerprint").GetString(), Has.Length.EqualTo(16));
        Assert.That(plan.GetProperty("canRestore").GetBoolean(), Is.True);
        Assert.That(Directory.Exists(to), Is.False, "a dry run writes nothing");

        var text = Restore(to, lsn: _kit.CommitLsns[5].ToString(), dryRun: true);
        Assert.That(text.Text, Does.Contain("Would restore point #1").And.Contain("Nothing was written"));

        var beyond = Restore(to, "json", lsn: (_kit.ArchivedThroughLsn + 100).ToString(), dryRun: true);
        Assert.That(beyond.Code, Is.EqualTo(BackupCli.ExitStoreDamaged), beyond.Json);
        Assert.That(ArchivedStoreKit.Json(beyond).GetProperty("plan").GetProperty("unreachable").GetString(), Is.EqualTo("beyondArchive"));
        Assert.That(Directory.Exists(to), Is.False);
    }

    [Test]
    [CancelAfter(30_000)]
    public void AnUnreachableTarget_Exits3_NamingWhatIsRestorable()
    {
        var beyond = Restore(Target("beyond"), lsn: (_kit.ArchivedThroughLsn + 100).ToString());
        Assert.That(beyond.Code, Is.EqualTo(BackupCli.ExitStoreDamaged), beyond.Text);
        Assert.That(beyond.Text, Does.Contain("BeyondArchive").And.Contain("Restorable instead").And.Contain(_kit.ArchivedThroughLsn.ToString()));

        var early = Restore(Target("early"), "json", time: "2000-01-01T00:00:00Z");
        Assert.That(early.Code, Is.EqualTo(BackupCli.ExitStoreDamaged), early.Json);
        Assert.That(ArchivedStoreKit.Json(early).GetProperty("error").GetProperty("message").GetString(), Does.Contain("BeforeCoverage"));

        var unknown = Restore(Target("unknown"), lsn: _kit.CommitLsns[0].ToString(), timeline: Guid.NewGuid().ToString());
        Assert.That(unknown.Code, Is.EqualTo(BackupCli.ExitStoreDamaged), unknown.Text);
        Assert.That(unknown.Text, Does.Contain("TimelineUnknown"));
        Assert.That(new[] { "beyond", "early", "unknown" }.Any(n => Directory.Exists(Target(n))), Is.False, "a refused restore writes nothing");
    }

    [TestCase("1", "5", null, null, TestName = "PointAndLsn")]
    [TestCase(null, "5", "2026-10-21T12:00:00Z", null, TestName = "LsnAndTime")]
    [TestCase(null, null, "2026-10-21T12:00:00", null, TestName = "TimeWithoutOffset")]
    [TestCase(null, "abc", null, null, TestName = "LsnNotANumber")]
    [TestCase(null, "0", null, null, TestName = "LsnZero")]
    [TestCase("1", null, null, "00000000-0000-0000-0000-000000000001", TestName = "TimelineWithAPoint")]
    [TestCase(null, "5", null, "not-a-guid", TestName = "TimelineNotAnId")]
    [CancelAfter(30_000)]
    public void AWrongTarget_IsAUsageError(string point, string lsn, string time, string timeline)
    {
        var run = Restore(Target("usage"), point: point, lsn: lsn, time: time, timeline: timeline);
        Assert.That(run.Code, Is.EqualTo(BackupCli.ExitUsage), run.Text);
        Assert.That(Directory.Exists(Target("usage")), Is.False);
    }
}
