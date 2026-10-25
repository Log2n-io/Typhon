using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.IO;
using System.Linq;
using Typhon.Engine.Tests.Backup;

namespace Typhon.Engine.Tests;

/// <summary>
/// The restore planner (impl/1418 §3.7, BKP-11) over a real archived workload: every refusal names its range and what is restorable instead; a base whose
/// tail disagrees with the archive is never mixed with it; a restore never crosses a fork, the ancestry choosing the side.
/// </summary>
[NonParallelizable]
class ArchiveRestorePlannerTests : ArchiveTestBase<ArchiveRestorePlannerTests>
{
    private const string Marker = "BKP-11: the planner";

    private static ArchiveRestorePlan Plan(PointSelector target) =>
        ArchiveRestorePlanner.Plan(StoreFileSystem.Instance, StoreView.Open(StorePath, trustCatalog: false), target);

    private static RestoreTargetUnreachableException Refused(PointSelector target) => Assert.Throws<RestoreTargetUnreachableException>(() => Plan(target));

    private static string SegmentFile(Guid timeline, long id) => new WalArchivePaths(StorePath).Segment(timeline, id);

    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("BKP-11")]
    public void EveryRefusal_NamesItsRange_AndWhatIsRestorableInstead()
    {
        var run = RunArchivedWorkload(TestSeed.For("bkp-archive-planner"), stepsPerPhase: 4);
        var view = StoreView.Open(StorePath, trustCatalog: false);
        var p1 = view.Find(run.Points[0]).Original;
        var end = WalArchiveStore.Load(StoreFileSystem.Instance, StorePath, run.Timeline, trustIndex: false).ArchivedThroughLsn;

        Assert.Multiple(() =>
        {
            var noBase = Refused(PointSelector.AtLsn(Math.Max(1, p1.PointLsn - 1)));
            Assert.That(noBase.Reason, Is.EqualTo(RestoreUnreachableReason.NoBase), $"{Marker}: a target below every point");
            Assert.That(noBase.NearestRestorable, Does.Contain(p1.PointLsn));
            Assert.That(noBase.ErrorCode, Is.EqualTo(TyphonErrorCode.RestoreTargetUnreachable));

            var beyond = Refused(PointSelector.AtLsn(end + 50));
            Assert.That((beyond.Reason, beyond.FromLsn, beyond.ToLsn), Is.EqualTo((RestoreUnreachableReason.BeyondArchive, end + 1, end + 50)));
            Assert.That(beyond.NearestRestorable, Does.Contain(end));

            var early = Refused(PointSelector.AtTime(new DateTime(run.Samples[0].UtcTicks, DateTimeKind.Utc).AddHours(-1)));
            Assert.That(early.Reason, Is.EqualTo(RestoreUnreachableReason.BeforeCoverage));

            var unknown = Refused(PointSelector.AtLsn(end, Guid.NewGuid()));
            Assert.That(unknown.Reason, Is.EqualTo(RestoreUnreachableReason.TimelineUnknown));

            var plan = Plan(PointSelector.AtLsn(end));
            Assert.That((plan.EffectiveLsn, plan.TimelineId), Is.EqualTo((end, run.Timeline)), "the archive's end is restorable");
        });

        // A gap: an archived segment past the first point's tail and before the second point, gone. A target past it from the first point refuses,
        // naming the hole; the second point still restores what follows it.
        var archived = WalArchiveStore.Load(StoreFileSystem.Instance, StorePath, run.Timeline, trustIndex: false).Segments;
        var p2 = view.Find(run.Points[1]).Original;
        var victim = archived.First(s => s.FirstLsn > p1.PointLsn + 1 && s.End < p2.CheckpointLsn && s != archived[^1]);
        File.Delete(SegmentFile(run.Timeline, victim.SegmentId));
        var gap = Refused(PointSelector.AtLsn(p2.PointLsn - 1));
        Assert.That(gap.Reason, Is.EqualTo(RestoreUnreachableReason.Gap), $"{Marker}: a target past a hole");
        Assert.That(gap.FromLsn, Is.LessThanOrEqualTo(victim.FirstLsn));
        Assert.That(gap.ToLsn, Is.GreaterThanOrEqualTo(victim.End - 1));
        Assert.That(Plan(PointSelector.AtLsn(end)).Chain.Point.BackupId, Is.EqualTo(p2.BackupId), "past the second point, the gap is behind its base");
    }

    /// <summary>
    /// A restore to an LSN needs every archived segment's header and footer, never its time samples: a segment whose samples section is damaged is still
    /// replayed (its WAL bytes are checked when decoded), and a time target is resolved from the segments the instant lies in.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    public void ARestoreToAnLsn_DoesNotReadTheTimeSamples()
    {
        var run = RunArchivedWorkload(TestSeed.For("bkp-archive-planner-samples"), stepsPerPhase: 4);
        var withBounds = WalArchiveStore.Load(StoreFileSystem.Instance, StorePath, run.Timeline, trustIndex: false).Segments;
        var target = StoreView.Open(StorePath, trustCatalog: false).Find(run.Points[1]).Original.PointLsn - 1;   // from the first point: the longest replay
        var plan = Plan(PointSelector.AtLsn(target));
        var victim = withBounds.FirstOrDefault(s => s.FirstSampleLsn > 0 && s != withBounds[^1] && plan.Segments.Any(p => p.Archived?.SegmentId == s.SegmentId));
        Assert.That(victim, Is.Not.Null, "premise: the plan replays an archived segment with samples, not the newest");

        using (var f = new FileStream(SegmentFile(run.Timeline, victim.SegmentId), FileMode.Open, FileAccess.ReadWrite))
        {
            f.Seek(victim.Footer.SamplesOffset + ArchiveFormat.SectionPrefixSize, SeekOrigin.Begin);
            var b = f.ReadByte();
            f.Seek(-1, SeekOrigin.Current);
            f.WriteByte((byte)(b ^ 0x5A));
        }

        Assert.Throws<BackupPackCorruptException>(() =>
        {
            using var r = ArchivedSegmentReader.Open(StoreFileSystem.Instance, SegmentFile(run.Timeline, victim.SegmentId));
            r.ReadSamples();
        }, "premise: the samples section is damaged");
        var again = Plan(PointSelector.AtLsn(target));
        Assert.That(again.Segments.Select(s => s.SegmentId), Is.EqualTo(plan.Segments.Select(s => s.SegmentId)), "the same WAL, samples unread");
        var newest = withBounds[^1];
        var atTime = Plan(PointSelector.AtTime(new DateTime(newest.LastSampleUtcTicks, DateTimeKind.Utc)));
        Assert.That(atTime.TargetLsn, Is.EqualTo(newest.LastSampleLsn), "an instant resolves from the newest segment's samples");
    }

    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("BKP-11")]
    public void ABaseWhoseTailDisagreesWithTheArchive_IsNeverMixedWithIt()
    {
        var run = RunArchivedWorkload(TestSeed.For("bkp-archive-planner-divergent"), stepsPerPhase: 4);
        var view = StoreView.Open(StorePath, trustCatalog: false);
        var p1 = view.Find(run.Points[0]).Original;
        var p2 = view.Find(run.Points[1]).Original;
        var target = p2.PointLsn - 1;
        Assert.That(Plan(PointSelector.AtLsn(target)).Chain.Point.BackupId, Is.EqualTo(p1.BackupId), "premise: the first point is the base");

        // Another history under the first point's tail segment's name: its archived copy no longer starts with the tail's bytes.
        using (var reader = PackReader.Open(StoreFileSystem.Instance, view.Paths.PackPath(p1.File)))
        {
            var first = reader.LoadTailDirectory().Segments[0];
            var path = SegmentFile(run.Timeline, first.SegmentId);
            ArchivedSegmentInfo original;
            using (var r = ArchivedSegmentReader.Open(StoreFileSystem.Instance, path))
            {
                original = WalArchiveStore.Describe(Path.GetFileName(path), r);
            }

            File.Delete(path);
            var forged = ArchiveTestKit.Segment(first.SegmentId, first.FirstLsn, (int)original.Footer.ArchiveLength, original.Header.DeclaredSize, seed: 77);
            using var writer = new ArchivedSegmentWriter(null);
            using var t = WalArchiveTimeline.TryOpen(StoreFileSystem.Instance, StorePath, run.Timeline);
            var header = original.Header;
            t.Publish(writer, header, forged.AsSpan(0, 4096), forged.AsSpan(4096), original.Footer, []);
        }

        var refused = Refused(PointSelector.AtLsn(target));
        Assert.That(refused.Reason, Is.EqualTo(RestoreUnreachableReason.NoBase), $"{Marker}: a base mixed with another history");
    }

    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("BKP-11")]
    public void ARestore_NeverCrossesAFork_TheAncestryPicksTheSide()
    {
        Guid parent, child;
        long origin;
        uint before, after;
        long last;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            parent = dbe.TimelineId;
            var archiver = NewArchiver(dbe);
            Tick(archiver, 1);
            before = CapturePoint(dbe, BackupKind.Full);
            Spawn(dbe, 10, 0);
            ArchiveAll(dbe, archiver);
            Spawn(dbe, 10, 100);
            dbe.ForkTimeline(TimelineReason.SchemaChange, switchSegment: true);
            child = dbe.TimelineId;
            origin = dbe.TimelineOriginLsn;
            Spawn(dbe, 10, 200);
            ArchiveAll(dbe, archiver);
            Spawn(dbe, 10, 300);
            after = CapturePoint(dbe, BackupKind.Incremental);
            Spawn(dbe, 10, 400);
            ArchiveAll(dbe, archiver);
            last = archiver.ArchivedThroughLsn;
            Assert.That(archiver.ConsumeImmediatePointRequest(), Is.True, "a fork asks for a point");
        }

        var view = StoreView.Open(StorePath, trustCatalog: false);
        Assert.That(view.Find(after).Original.TimelineId, Is.EqualTo(child));
        Assert.That(WalArchiveStore.ReadTimelineFile(StoreFileSystem.Instance, StorePath, child) is { ParentTimelineId: var p, OriginLsn: var o }
                    && p == parent && o == origin, Is.True, "the child's timeline.json names its parent and origin");

        var onParent = Plan(PointSelector.AtLsn(origin));
        Assert.That((onParent.TimelineId, onParent.Chain.Point.BackupId), Is.EqualTo((parent, before)), "at or below the origin: the parent, its point");
        var onChild = Plan(PointSelector.AtLsn(last));
        Assert.That((onChild.TimelineId, onChild.Chain.Point.BackupId), Is.EqualTo((child, after)), "past the origin: the child, its point");
        var between = Refused(PointSelector.AtLsn(view.Find(after).Original.PointLsn - 1));
        Assert.That(between.Reason, Is.EqualTo(RestoreUnreachableReason.NoBase), $"{Marker}: a child target below its first point would cross the fork");
        Assert.That(between.TimelineId, Is.EqualTo(child));
    }
}
