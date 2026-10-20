using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.IO;
using System.Linq;

namespace Typhon.Engine.Tests;

/// <summary>
/// The archiver driven on the test's thread with logical clocks (impl/1418 §3.3): every sealed segment archived with its samples and the floor raised
/// after the copy is durable; samples paired with a durable LSN read before their clock, deduplicated, clamped when the clock steps back; switches at
/// MaxSegmentAge counted from the first durable record; the state's lag.
/// </summary>
[NonParallelizable]
class WalArchiverStepTests : ArchiveTestBase<WalArchiverStepTests>
{
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("BKP-10")]
    public void EverySealedSegment_IsArchivedWithItsSamples_AndTheFloorRisesOnlyOnceItIsDurable()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        var archiver = NewArchiver(dbe);
        Tick(archiver, 0);
        Assert.That(archiver.State.Condition, Is.EqualTo(WalArchiveCondition.Starting), "the first step ran before the worker opened the store");
        Tick(archiver, 0.5);
        Assert.That(archiver.State.Condition, Is.EqualTo(WalArchiveCondition.Archiving));
        Assert.That(dbe.CheckpointManager.ArchiveFloor, Is.EqualTo(1), "nothing archived: the floor holds every LSN");

        Spawn(dbe, 20, 0);
        Tick(archiver, 1);
        Tick(archiver, 2);
        var first = SpawnAndSeal(dbe, 20, 100);

        long floorAtPublish = -1;
        archiver.AfterPublishProbe = _ => floorAtPublish = dbe.CheckpointManager.ArchiveFloor;
        Tick(archiver, 3);
        Assert.That(floorAtPublish, Is.EqualTo(1), "the floor is raised after the copy is durable, never before (raise-before-publish is the mutant)");
        Assert.That(dbe.CheckpointManager.ArchiveFloor, Is.EqualTo(first.LastLsn + 1));
        Assert.That(archiver.ArchivedThroughLsn, Is.EqualTo(first.LastLsn));

        var archived = Archived(dbe);
        Assert.That(archived.Segments.Select(s => s.SegmentId), Is.EqualTo(Sealed(dbe).Where(s => s.LastLsn <= first.LastLsn).Select(s => s.Id)));
        var segment = archived.Segments.Single(s => s.SegmentId == first.Id);
        using (var reader = ArchivedSegmentReader.Open(StoreFileSystem.Instance, Path.Combine(StorePath, "wal", dbe.TimelineId.ToString("D"), segment.File)))
        {
            var samples = reader.ReadSamples();
            Assert.That(samples, Is.Not.Empty);
            Assert.That(samples.All(s => s.Lsn >= segment.FirstLsn && s.Lsn <= segment.LastLsn), Is.True);
            Assert.That(samples.Select(s => s.UtcTicks), Is.Ordered);
        }

        Assert.That(archiver.PendingSamples().All(s => s.Lsn > first.LastLsn), Is.True, "archived samples leave the ring");
        Assert.That(File.Exists(Path.Combine(StorePath, "wal", dbe.TimelineId.ToString("D"), "timeline.json")), Is.True);
    }

    /// <summary>
    /// A step right after a switch samples the sealed segment's end, which an archived segment can no longer take: the sample is filed with the next one
    /// rather than lost, so the instant still resolves to that LSN.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    public void ASampleOfAnArchivedSegmentsEnd_IsFiledWithTheNextSegment()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        var archiver = NewArchiver(dbe);
        Tick(archiver, 0);
        Tick(archiver, 0.5);
        var first = SpawnAndSeal(dbe, 20, 0);
        Tick(archiver, 1);
        Assert.That(archiver.ArchivedThroughLsn, Is.EqualTo(first.LastLsn), "premise: the segment is archived before its end is sampled");
        Tick(archiver, 2);
        Assert.That(archiver.PendingSamples().Select(s => s.Lsn), Does.Contain(first.LastLsn), "premise: the next step sampled the archived end");

        var second = SpawnAndSeal(dbe, 20, 100);
        Tick(archiver, 3);
        var segment = Archived(dbe).Segments.Single(s => s.SegmentId == second.Id);
        using (var reader = ArchivedSegmentReader.Open(StoreFileSystem.Instance, Path.Combine(StorePath, "wal", dbe.TimelineId.ToString("D"), segment.File)))
        {
            Assert.That(reader.ReadSamples()[0], Is.EqualTo(new ArchiveTimeSample(first.LastLsn, Utc(2).Ticks)));
        }

        var timelines = ArchiveRestorePlanner.LoadTimelines(StoreFileSystem.Instance, StorePath);
        Assert.That(ArchiveRestorePlanner.ResolveTime(StoreFileSystem.Instance, StorePath, timelines, dbe.TimelineId, Utc(2.5)).Lsn, Is.EqualTo(first.LastLsn));
    }

    [Test]
    [CancelAfter(15_000)]
    public void ASample_PairsTheDurableLsnOfThePreviousStepWithThisStepsClock_KeptOnlyWhenItMoved_AndClampedWhenTheClockStepsBack()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        var archiver = NewArchiver(dbe);
        Tick(archiver, 0);
        Spawn(dbe, 5);
        var durable = dbe.WalManager.DurableLsn;
        Tick(archiver, 1);   // reads `durable` at its end
        Assert.That(archiver.PendingSamples().Any(s => s.Lsn == durable), Is.False, "a durable LSN is never stamped with a clock read before it");
        Tick(archiver, 2);
        Assert.That(archiver.PendingSamples().Single(s => s.Lsn == durable).UtcTicks, Is.EqualTo(Utc(2).Ticks));

        var count = archiver.PendingSamples().Length;
        Tick(archiver, 3);
        Tick(archiver, 4);
        Assert.That(archiver.PendingSamples().Length, Is.EqualTo(count), "an idle database adds no sample");

        Spawn(dbe, 5, 10);
        var later = dbe.WalManager.DurableLsn;
        archiver.Step(Utc(5), Mono(5), default);
        archiver.Step(Utc(1), Mono(6), default);   // the wall clock stepped back 4 s
        var clamped = archiver.PendingSamples().Single(s => s.Lsn == later);
        Assert.That(clamped.UtcTicks, Is.EqualTo(Utc(2).Ticks), "clamped to the previous sample: the time index stays monotonic");
    }

    [Test]
    [CancelAfter(15_000)]
    public void ASwitch_IsRequestedAtMaxSegmentAge_CountedFromTheFirstDurableRecord_NotTheSegmentsCreation()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        var archiver = NewArchiver(dbe);
        var wal = dbe.WalManager;
        Tick(archiver, 0);
        Tick(archiver, 30);   // an old, empty active segment: nothing to switch
        Assert.That(wal.SwitchesServed, Is.Zero);

        Spawn(dbe, 5);
        Tick(archiver, 31);   // first seen holding a durable record
        Tick(archiver, 35.9);
        Assert.That(wal.SwitchesServed, Is.Zero, "younger than MaxSegmentAge since its first durable record");
        Tick(archiver, 36);
        WaitServed(wal, 1);
        Assert.That(Sealed(dbe).Count, Is.EqualTo(1));

        Tick(archiver, 50);   // the new active segment holds nothing
        Assert.That(wal.SwitchesServed, Is.EqualTo(1));
        Spawn(dbe, 5, 10);
        Tick(archiver, 51);
        Tick(archiver, 56);
        WaitServed(wal, 2);
        Tick(archiver, 57);
        Assert.That(Archived(dbe).Segments.Count, Is.EqualTo(2), "each sealed segment archived");
    }

    [Test]
    [CancelAfter(15_000)]
    public void LagSeconds_IsZeroWhenEverythingDurableIsArchived_ElseTheAgeOfTheFirstSamplePastTheArchive()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        var archiver = NewArchiver(dbe);
        Tick(archiver, 0);
        Tick(archiver, 1);
        Assert.That(archiver.State.LagSeconds, Is.Zero, "an idle database has nothing at risk");

        Spawn(dbe, 5);
        Tick(archiver, 10);
        Tick(archiver, 11);   // samples the new LSN at t=11
        Tick(archiver, 14);
        Assert.That(archiver.State.LagSeconds, Is.EqualTo(3).Within(0.001));
        Assert.That(archiver.State.LagBytes, Is.GreaterThan(0));

        Switch(dbe);
        Tick(archiver, 15);
        Tick(archiver, 16);
        Assert.That(archiver.State.LagSeconds, Is.Zero);
        Assert.That(archiver.State.LagBytes, Is.Zero);
    }

    private static void WaitServed(WalManager wal, long ticket)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (wal.SwitchesServed < ticket)
        {
            Assert.That(clock.Elapsed, Is.LessThan(Budget), "the switch was not served");
            System.Threading.Thread.Yield();
        }
    }
}
