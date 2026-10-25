using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.IO;
using System.Linq;
using Typhon.Engine.Tests.Backup;

namespace Typhon.Engine.Tests;

/// <summary>
/// AC-A1 (BKP-10, WA-4): a store that stops answering stalls the archiver, never the engine. The floor holds the segments up to the backlog limit; past it
/// the checkpoint releases the floor within one cycle, counted once, with a point requested; when the store answers again archiving resumes at the oldest
/// segment still present, and the hole is recorded in that segment's footer as a gap with its cause.
/// </summary>
[NonParallelizable]
class WalArchiveBacklogTests : ArchiveTestBase<WalArchiveBacklogTests>
{
    private const string Marker = "BKP-10: a release";

    protected override long BacklogLimit => 2L * SegmentSize;

    /// <summary>
    /// The timed switch seals only a segment holding a durable record (WalArchiver.Step's activeHolds; the writer's ServeSwitchRequest rotates only a
    /// segment written past its header): an idle database, its store down for many MaxSegmentAge, seals nothing, holds nothing, releases nothing.
    /// </summary>
    [Test]
    [CancelAfter(20_000)]
    public void AnIdleDatabase_WithItsStoreDown_SealsNoSegment_AndIsNeverReleased()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        var fs = new SwitchableStoreFileSystem();
        var archiver = NewArchiver(dbe, fs);
        Tick(archiver, 0);

        // Premise: a segment holding a record is switched at its age and archived.
        Spawn(dbe, 1, 1);
        Tick(archiver, 1);
        var servedBefore = dbe.WalManager.SwitchesServed;
        Tick(archiver, 7);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (dbe.WalManager.SwitchesServed == servedBefore)
        {
            Assert.That(clock.Elapsed, Is.LessThan(Budget), "premise: the timed switch of a segment holding a record");
            System.Threading.Thread.Yield();
        }

        Tick(archiver, 8);
        Tick(archiver, 9);
        Assert.That(archiver.ArchivedThroughLsn, Is.EqualTo(dbe.WalManager.DurableLsn), "premise: the switched segment is archived");

        // Idle, the store down, twelve MaxSegmentAge (5 s) on the archiver's clock, checkpoints cycling, a tick fence per second as an idle runtime
        // writes one (nothing dirty: it logs nothing).
        fs.Failing = true;
        var served = dbe.WalManager.SwitchesServed;
        var generation = dbe.WalManager.SegmentManager.SealedGeneration;
        var durable = dbe.WalManager.DurableLsn;
        for (var t = 10; t <= 70; t++)
        {
            dbe.WriteTickFence(t);
            Tick(archiver, t);
            if (t % 5 == 0)
            {
                ForceCheckpoint(dbe);
            }
        }

        Assert.That(dbe.WalManager.DurableLsn, Is.EqualTo(durable), "premise: an idle tick fence logs nothing");
        Assert.That(dbe.WalManager.SwitchesServed, Is.EqualTo(served), "no switch of an unchanged segment");
        Assert.That(dbe.WalManager.SegmentManager.SealedGeneration, Is.EqualTo(generation), "no segment sealed");
        Assert.That(dbe.CheckpointManager.ArchiveReleases, Is.Zero, "nothing held, nothing released");
        var state = archiver.State;
        Assert.That((state.GapsTotal, state.LagBytes, state.Condition), Is.EqualTo((0L, 0L, WalArchiveCondition.Archiving)),
            "no gap, nothing at risk, and the store never asked");
    }

    [Test]
    [CancelAfter(20_000)]
    [VerifiesRule("BKP-10")]
    public void AStalledStore_HoldsUpToTheLimit_ThenReleasesOnce_AndTheGapIsRecordedWhenItAnswersAgain()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        var fs = new SwitchableStoreFileSystem();
        var archiver = NewArchiver(dbe, fs);
        var archived = SpawnAndSeal(dbe, 20, 0);
        Tick(archiver, 0);
        Tick(archiver, 1);
        Assert.That(archiver.ArchivedThroughLsn, Is.EqualTo(archived.LastLsn), "premise: a first segment archived");
        CapturePoint(dbe, BackupKind.Full);

        // The store stops answering: the archiver stalls, the floor holds, checkpoints keep cycling.
        fs.Failing = true;
        var held = new[] { SpawnAndSeal(dbe, 20, 100), SpawnAndSeal(dbe, 20, 200) };
        Spawn(dbe, 1, 9_000);
        Tick(archiver, 2);
        ForceCheckpoint(dbe);
        ForceCheckpoint(dbe);
        Tick(archiver, 3);
        Assert.That(archiver.State.Condition, Is.EqualTo(WalArchiveCondition.Stalled));
        Assert.That(held.All(s => File.Exists(s.Path)), Is.True, "within the limit: every un-archived segment is held");
        Assert.That(dbe.CheckpointManager.ArchiveReleases, Is.Zero);
        Assert.That(archiver.State.LagBytes, Is.GreaterThan(0));
        Assert.That(archiver.State.BacklogBytes, Is.EqualTo(2L * SegmentSize));

        // One more segment: the bytes the floor alone holds pass the limit; the next cycle releases it, once, holding at most limit + one segment.
        var third = SpawnAndSeal(dbe, 20, 300);
        Spawn(dbe, 1, 9_001);
        Assert.That(dbe.WalManager.SegmentManager.BytesHeldBetween(dbe.CheckpointManager.ArchiveFloor, long.MaxValue), Is.LessThanOrEqualTo(BacklogLimit + SegmentSize));
        ForceCheckpoint(dbe);
        Assert.That(dbe.CheckpointManager.ArchiveReleases, Is.EqualTo(1), Marker + " was not counted");
        Assert.That(held.Concat([third]).All(s => !File.Exists(s.Path)), Is.True, "released: the checkpoint reclaims as if archiving were off");
        Tick(archiver, 4);
        Assert.That(archiver.State.Condition, Is.EqualTo(WalArchiveCondition.Released));
        Assert.That(archiver.ConsumeImmediatePointRequest(), Is.True, "a release asks for a point, once");
        Assert.That(archiver.ConsumeImmediatePointRequest(), Is.False);
        ForceCheckpoint(dbe);
        Assert.That(dbe.CheckpointManager.ArchiveReleases, Is.EqualTo(1), "released stays released: no second count");

        // The store answers again: archiving resumes at the oldest segment present, recording the hole before it.
        fs.Failing = false;
        var resumed = SpawnAndSeal(dbe, 20, 400);
        Tick(archiver, 5);
        Tick(archiver, 6);
        Assert.That(archiver.ArchivedThroughLsn, Is.EqualTo(resumed.LastLsn));
        Assert.That(dbe.CheckpointManager.ArchiveFloor, Is.EqualTo(resumed.LastLsn + 1), "the archiver's raise re-engages the floor");
        var segment = Archived(dbe).Segments.Single(s => s.SegmentId == resumed.Id);
        Assert.That(segment.Footer.GapBefore, Is.True);
        Assert.That((segment.Footer.GapFromLsn, segment.Footer.GapToLsn, segment.Footer.GapReason),
            Is.EqualTo((archived.LastLsn + 1, resumed.FirstLsn - 1, ArchiveGapReason.BacklogReleased)));
        Assert.That(archiver.State.Condition, Is.EqualTo(WalArchiveCondition.Archiving));
        Assert.That((archiver.State.LastGapFromLsn, archiver.State.LastGapToLsn, archiver.State.GapsTotal),
            Is.EqualTo((archived.LastLsn + 1, resumed.FirstLsn - 1, 1L)));
        Assert.That(archiver.ConsumeImmediatePointRequest(), Is.True, "a gap asks for a point");

        // No restore crosses the hole: a target inside it, or past it from the point before it, refuses naming the range.
        var view = StoreView.Open(StorePath, trustCatalog: false);
        foreach (var target in new[] { archived.LastLsn + 1, resumed.LastLsn })
        {
            var refused = Assert.Throws<RestoreTargetUnreachableException>(() =>
                ArchiveRestorePlanner.Plan(StoreFileSystem.Instance, view, PointSelector.AtLsn(target)));
            Assert.That(refused.Reason, Is.EqualTo(RestoreUnreachableReason.Gap), $"{Marker}: a restore across it, target {target}");
            Assert.That(refused.FromLsn, Is.LessThanOrEqualTo(archived.LastLsn + 1));
        }
    }
}
