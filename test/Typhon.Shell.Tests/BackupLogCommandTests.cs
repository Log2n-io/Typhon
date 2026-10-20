using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using NUnit.Framework;
using Typhon.Engine;
using Typhon.Shell.Commands;

namespace Typhon.Shell.Tests;

/// <summary>
/// <c>typhon backup log</c> (#1418, AC-A9): the archive's timelines, segments, gaps and session starts; a range's frames with <c>--commits</c>; JSON that
/// round-trips into the facade's types; exit 0, 1 for a gap no later point covers, 3 for an archive that cannot be read, 64 for usage.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class BackupLogCommandTests
{
    private ArchivedStoreKit _kit;

    [SetUp]
    public void Build() => _kit = ArchivedStoreKit.Build(segmentPerTick: true);

    [TearDown]
    public void Destroy() => _kit?.Dispose();

    private (int Code, string Text, string Json) Log(BackupLogCommand.Settings settings)
    {
        settings.Store ??= _kit.Store;
        return ArchivedStoreKit.Run((c, j) => BackupLogCommand.Run(settings, c, j));
    }

    [Test]
    [CancelAfter(30_000)]
    public void TheListing_ShowsTheTimeline_ItsSegments_AndRoundTripsAsJson()
    {
        var status = ArchivedStoreKit.Run((c, j) => BackupStatusCommand.Run(new BackupSettings { Store = _kit.Store, Format = "text" }, c, j, DateTime.UtcNow));
        Assert.That(status.Code, Is.EqualTo(BackupCli.ExitOk), status.Text);
        Assert.That(status.Text, Does.Contain("Archived through").And.Contain(_kit.ArchivedThroughLsn.ToString()).And.Contain("none"));

        var text = Log(new BackupLogCommand.Settings { Format = "text" });
        Assert.That(text.Code, Is.EqualTo(BackupCli.ExitOk), text.Text);
        Assert.That(text.Text, Does.Contain($"timeline {_kit.Timeline}").And.Contain("Genesis").And.Contain($"archived through LSN {_kit.ArchivedThroughLsn}"));

        var run = Log(new BackupLogCommand.Settings { Format = "json" });
        Assert.That(run.Code, Is.EqualTo(BackupCli.ExitOk), run.Json);
        var listing = ArchivedStoreKit.Json(run).GetProperty("listing").Deserialize<BackupArchiveListing>(BackupCli.JsonOptions);
        var direct = BackupOperations.ListArchive(_kit.Store);
        Assert.That(listing.Timelines.Select(t => (t.TimelineId, t.Reason, t.ArchivedThroughLsn, t.Segments.Count)),
            Is.EqualTo(direct.Timelines.Select(t => (t.TimelineId, t.Reason, t.ArchivedThroughLsn, t.Segments.Count))));
        Assert.That(listing.Timelines[0].Segments, Is.EqualTo(direct.Timelines[0].Segments), "the segments round-trip");
        Assert.That(listing.Timelines[0].Gaps, Is.Empty);
        Assert.That(listing.Timelines[0].Samples, Is.Not.Empty);
    }

    [Test]
    [CancelAfter(30_000)]
    public void Commits_ListEveryFrameOfABoundedRange_WithItsTsn()
    {
        var from = _kit.CommitLsns[2];
        var to = _kit.CommitLsns[8];
        var run = Log(new BackupLogCommand.Settings { Format = "json", LsnFrom = from, LsnTo = to, Commits = true });
        Assert.That(run.Code, Is.EqualTo(BackupCli.ExitOk), run.Json);
        var log = ArchivedStoreKit.Json(run).GetProperty("log").Deserialize<BackupArchiveLog>(BackupCli.JsonOptions);
        var transactions = log.Frames.Where(f => f.Kind == BackupLogFrameKind.Transaction).ToList();
        Assert.That(transactions.Select(f => f.LastLsn), Is.EqualTo(_kit.CommitLsns.Skip(2).Take(7)), "one frame per commit in the range, ending at its TxCommit");
        Assert.That(transactions.All(f => f.Tsn > 0 && f.CommitStamp > 0 && f.Records > 0), Is.True);
        Assert.That(transactions.All(f => f.ApproxUtc.HasValue), Is.True, "every frame is placed between the samples");
        Assert.That(log.Frames.All(f => f.LastLsn >= from && f.LastLsn <= to), Is.True);
        Assert.That(log.Truncated, Is.False);

        var limited = Log(new BackupLogCommand.Settings { Format = "json", LsnFrom = from, LsnTo = to, Commits = true, Limit = 2 });
        var cut = ArchivedStoreKit.Json(limited).GetProperty("log");
        Assert.That((cut.GetProperty("frames").GetArrayLength(), cut.GetProperty("truncated").GetBoolean()), Is.EqualTo((2, true)));

        var text = Log(new BackupLogCommand.Settings { Format = "text", LsnFrom = from, LsnTo = to, Commits = true });
        Assert.That(text.Text, Does.Contain("Transaction").And.Contain("≈ 2026-10-21"));

        var byTime = Log(new BackupLogCommand.Settings { Format = "json", From = "2026-10-21T12:00:05Z", To = "2026-10-21T13:00:00Z", Commits = true });
        Assert.That(byTime.Code, Is.EqualTo(BackupCli.ExitOk), byTime.Json);
        Assert.That(ArchivedStoreKit.Json(byTime).GetProperty("log").GetProperty("frames").GetArrayLength(), Is.GreaterThan(0));
    }

    [Test]
    [CancelAfter(30_000)]
    public void AGapNoLaterPointCovers_Exits1_AndAnUnreadableSegment_Exits3()
    {
        var segments = Directory.GetFiles(Path.Combine(_kit.Store, "wal", _kit.Timeline.ToString("D")), "*.twal").OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        Assert.That(segments.Count, Is.GreaterThanOrEqualTo(3), "premise: a segment between two others");
        File.Delete(segments[1]);
        var gap = Log(new BackupLogCommand.Settings { Format = "text" });
        Assert.That(gap.Code, Is.EqualTo(BackupCli.ExitWarning), gap.Text);
        Assert.That(gap.Text, Does.Contain("gap").And.Contain("no later point covers it"));

        // status: the archive's coverage per timeline; a gap no point lies past is a warning.
        var clean = ArchivedStoreKit.Run((c, j) => BackupStatusCommand.Run(new BackupSettings { Store = _kit.Store, Format = "json" }, c, j, DateTime.UtcNow));
        Assert.That(clean.Code, Is.EqualTo(BackupCli.ExitWarning), clean.Json);
        var archive = ArchivedStoreKit.Json(clean).GetProperty("archive")[0];
        Assert.That(archive.GetProperty("timelineId").GetGuid(), Is.EqualTo(_kit.Timeline));
        Assert.That(archive.GetProperty("fromLsn").GetInt64(), Is.EqualTo(_kit.PointLsn));
        Assert.That(archive.GetProperty("uncoveredGaps").GetArrayLength(), Is.EqualTo(1));
        var statusText = ArchivedStoreKit.Run((c, j) => BackupStatusCommand.Run(new BackupSettings { Store = _kit.Store, Format = "text" }, c, j, DateTime.UtcNow));
        Assert.That(statusText.Text, Does.Contain("Warning").And.Contain("misses LSN").And.Contain("Uncovered gaps"));

        var bytes = File.ReadAllBytes(segments[2]);
        bytes[10] ^= 0xFF;
        File.WriteAllBytes(segments[2], bytes);
        var unreadable = Log(new BackupLogCommand.Settings { Format = "json" });
        Assert.That(unreadable.Code, Is.EqualTo(BackupCli.ExitStoreDamaged), unreadable.Json);
    }

    [Test]
    [CancelAfter(30_000)]
    public void AWrongCommandLine_IsAUsageError()
    {
        Assert.That(Log(new BackupLogCommand.Settings { Commits = true }).Code, Is.EqualTo(BackupCli.ExitUsage), "--commits needs a bound");
        Assert.That(Log(new BackupLogCommand.Settings { From = "2026-10-21T12:00:00Z", LsnFrom = 1 }).Code, Is.EqualTo(BackupCli.ExitUsage));
        Assert.That(Log(new BackupLogCommand.Settings { From = "2026-10-21T12:00:00" }).Code, Is.EqualTo(BackupCli.ExitUsage), "an instant without offset");
        Assert.That(Log(new BackupLogCommand.Settings { Timeline = "x" }).Code, Is.EqualTo(BackupCli.ExitUsage));
        Assert.That(Log(new BackupLogCommand.Settings { LsnFrom = 1, Limit = 0, Commits = true }).Code, Is.EqualTo(BackupCli.ExitUsage));
    }
}
