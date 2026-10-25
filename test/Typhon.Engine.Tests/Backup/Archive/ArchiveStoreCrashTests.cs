using NUnit.Framework;
using System;
using System.Linq;

namespace Typhon.Engine.Tests.Backup;

/// <summary>
/// AC-A4 (BKP-10): a power cut after any file-system operation of a segment's publish leaves the archive openable, every visible <c>.twal</c> sound,
/// the archive's end at the newest visible segment, and a publish that returned durable — under both directory-durability models.
/// </summary>
[TestFixture]
internal sealed class ArchiveStoreCrashTests
{
    private const string Root = "/s";

    [TestCase(CrashModelKind.Windows)]
    [TestCase(CrashModelKind.Linux)]
    [CancelAfter(60_000)]
    [VerifiesRule("BKP-10")]
    public void ACrashAfterAnyOperationOfAPublish_LeavesEveryVisibleSegmentSound_AndAReturnedPublishDurable(CrashModelKind kind)
    {
        var first = ArchiveTestKit.Segment(1, 1, 12 * 1024);
        var second = ArchiveTestKit.Segment(2, 101, 1_200 * 1024, seed: 2);
        var crashes = 0;
        for (var k = 0; ; k++)
        {
            var fs = new CrashModelFileSystem(kind);
            fs.CreateDirectory("/");
            fs.CreateDirectory(Root);
            fs.SyncAll();
            ArchiveTestKit.Publish(fs, Root, first, 100);

            fs.CrashBefore(k);
            var returned = false;
            try
            {
                ArchiveTestKit.Publish(fs, Root, second, 250);
                returned = true;
            }
            catch (SimulatedCrashException)
            {
            }

            if (!returned)
            {
                crashes++;
            }

            var log = string.Join(" | ", fs.Log.TakeLast(4));
            fs.Crash();

            // The archive opens; its lock holder cleans the temps.
            using (var t = WalArchiveTimeline.TryOpen(fs, Root, ArchiveTestKit.Timeline))
            {
                Assert.That(t, Is.Not.Null);
            }

            var loaded = WalArchiveStore.Load(fs, Root, ArchiveTestKit.Timeline, trustIndex: false);
            Assert.That(loaded.Unreadable, Is.Empty, $"{kind} k={k}: a visible segment is unreadable after [{log}]");
            Assert.That(fs.AllFiles.Any(f => f.EndsWith(".tmp", StringComparison.Ordinal)), Is.False, $"{kind} k={k}: a temp survived the lock holder");
            Assert.That(loaded.Segments.Select(s => s.SegmentId).First(), Is.EqualTo(1), $"{kind} k={k}: the first published segment vanished");
            foreach (var s in loaded.Segments)
            {
                Assert.That(ArchiveTestKit.FullCheck(fs, Root + "/wal/" + ArchiveTestKit.Timeline.ToString("D") + "/" + s.File, s.SegmentId == 1 ? first : second),
                    Is.Null, $"{kind} k={k}: visible segment {s.File} does not verify after [{log}]");
            }

            Assert.That(loaded.ArchivedThroughLsn, Is.EqualTo(loaded.Segments.Count == 2 ? 250 : 100));
            if (returned)
            {
                Assert.That(loaded.Segments.Count, Is.EqualTo(2), $"{kind} k={k}: a publish that returned was lost at the crash");
                Assert.That(crashes, Is.GreaterThan(5), "the sweep crossed the whole publish");
                return;
            }
        }
    }

    [TestCase(CrashModelKind.Windows)]
    [TestCase(CrashModelKind.Linux)]
    [CancelAfter(15_000)]
    [VerifiesRule("BKP-10")]
    public void APublish_NeverReplacesAnArchivedSegment_TheSameBytesAreAlreadyArchived_OtherBytesDiverge(CrashModelKind kind)
    {
        var fs = new CrashModelFileSystem(kind);
        fs.CreateDirectory("/");
        fs.CreateDirectory(Root);
        var segment = ArchiveTestKit.Segment(4, 300, 12 * 1024);
        Assert.That(ArchiveTestKit.Publish(fs, Root, segment, 400).Outcome, Is.EqualTo(ArchivePublishOutcome.Published));
        var path = new WalArchivePaths(Root).Segment(ArchiveTestKit.Timeline, 4);
        var before = fs.ReadAll(path);

        Assert.That(ArchiveTestKit.Publish(fs, Root, segment, 400).Outcome, Is.EqualTo(ArchivePublishOutcome.AlreadyArchived));
        var other = ArchiveTestKit.Segment(4, 300, 12 * 1024, seed: 99);
        var (outcome, existing) = ArchiveTestKit.Publish(fs, Root, other, 400);
        Assert.That(outcome, Is.EqualTo(ArchivePublishOutcome.Diverged));
        Assert.That(existing.Footer.RawCrc, Is.EqualTo(Crc32CUtil.Compute(segment)));
        Assert.That(fs.ReadAll(path), Is.EqualTo(before), "the archived segment is never touched");
        Assert.That(fs.AllFiles.Any(f => f.EndsWith(".tmp", StringComparison.Ordinal)), Is.False);
    }
}
