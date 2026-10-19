using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// The freeze (BKP-02): a point is the data file at one instant with no write in flight. Its length is the physical one, its meta pair and its
/// checkpoint LSN are the frozen ones, and the incremental after a full point holds exactly the pages written in between plus the meta pair.
/// </summary>
class FreezeTests : BackupTestBase<FreezeTests>
{
    private const string Marker = "BKP-02 image";

    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("BKP-02")]
    public void FullThenIncremental_EachIsTheFileAtItsFreeze()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        var ids = Spawn(dbe, 300);
        var store = new FakeBackupStore();
        byte[] atFreeze = null;
        dbe.BackupCoordinator.Probe = (phase, _) =>
        {
            if (phase == CapturePhase.AfterFreeze)
            {
                atFreeze = ReadDataFile(dbe);
            }
        };

        var full = Capture(dbe, store);
        var fullPoint = store.Points.Single();
        Assert.Multiple(() =>
        {
            Assert.That(full.Kind, Is.EqualTo(BackupKind.Full), "a session's first point is full");
            Assert.That(fullPoint.ImagePages, Is.EqualTo(atFreeze.Length / PagedMMF.PageSize), "N is the file's length at the freeze");
            Assert.That(fullPoint.Pages.Count, Is.EqualTo(fullPoint.ImagePages), "a full point holds every page");
            Assert.That(fullPoint.Tail, Is.Not.Empty, "a live point always carries the active segment");
        });
        AssertImageIsTheFileAtTheFreeze(fullPoint, atFreeze, Marker);

        // Pages written between the freezes: the incremental's set, plus the meta pair (C21: compared with a write probe).
        var written = new System.Collections.Concurrent.ConcurrentDictionary<int, byte>();
        dbe.MMF.DataFileWriteProbe = (first, count) =>
        {
            for (var p = first; p < first + count; p++)
            {
                written.TryAdd(p, 0);
            }
        };
        Update(dbe, ids.Take(50), 7);
        Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(Budget), Is.True);

        dbe.BackupCoordinator.Probe = (phase, _) =>
        {
            if (phase == CapturePhase.BeforeFreeze)
            {
                dbe.MMF.DataFileWriteProbe = null;   // stop recording: the capture's own switch and checkpoint write pages too, before the freeze
            }
            else if (phase == CapturePhase.AfterFreeze)
            {
                atFreeze = ReadDataFile(dbe);
            }
        };
        var incremental = Capture(dbe, store);
        var incPoint = store.Points[1];
        Assert.Multiple(() =>
        {
            Assert.That(incremental.Kind, Is.EqualTo(BackupKind.Incremental));
            Assert.That(incPoint.Plan.ParentId, Is.EqualTo(full.BackupId));
            Assert.That(incPoint.Pages.Keys, Does.Contain(0).And.Contain(1), "the meta pair is in every point");
            var missing = written.Keys.Where(p => !incPoint.Pages.ContainsKey(p)).OrderBy(p => p).ToList();
            Assert.That(missing, Is.Empty, $"{Marker}: pages written since the last freeze are missing from the incremental");
            Assert.That(incPoint.Pages.Count, Is.LessThan(incPoint.ImagePages), "an incremental holds only what changed");
        });
        AssertImageIsTheFileAtTheFreeze(incPoint, atFreeze, Marker);
    }

    /// <summary>The verifier must see a point holding one page as it was after the freeze.</summary>
    [Test]
    [CancelAfter(15_000)]
    [RuleMutant("BKP-02")]
    public void Mutant_APageFromAfterTheFreeze_IsReported()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        Spawn(dbe, 100);
        var store = new FakeBackupStore();
        byte[] atFreeze = null;
        dbe.BackupCoordinator.Probe = (phase, _) => atFreeze = phase == CapturePhase.AfterFreeze ? ReadDataFile(dbe) : atFreeze;
        Capture(dbe, store);
        var point = store.Points.Single();

        var later = point.Pages.Keys.First(p => p >= 2);
        point.Pages[later] = (byte[])point.Pages[later].Clone();
        point.Pages[later][PagedMMF.PageSize / 2] ^= 0x01;
        RuleMutants.AssertDetects("BKP-02", Marker, () => AssertImageIsTheFileAtTheFreeze(point, atFreeze, Marker));
    }

    /// <summary>
    /// C11: an async batch that grew the file has exited the write gate but not yet advanced the tracked size (it does so after its fsync). The freeze
    /// takes the physical length, or the image would be short of pages the batch already wrote.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("BKP-02")]
    public void N_IsThePhysicalLength_NotTheTrackedSize()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        dbe.BackupCoordinator.Options.CheckpointBeforeFreeze = false;
        var mmf = dbe.MMF;

        using var lagging = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        long trackedAtLag = 0, physicalAtLag = 0;
        var armed = 1;
        mmf.FlushToDiskInterceptor = () =>
        {
            var physical = new FileInfo(mmf.DataFilePath).Length;
            if (physical > mmf.FileSize && Interlocked.Exchange(ref armed, 0) == 1)
            {
                trackedAtLag = mmf.FileSize;
                physicalAtLag = physical;
                lagging.Set();
                release.Wait();
            }
        };

        // A new segment's pages reach the file through an async SavePages batch that grows it.
        var grower = Task.Run(() =>
        {
            var cs = mmf.CreateChangeSet();
            mmf.AllocateSegment(PageBlockType.None, 64, cs);
            cs.SaveChanges();
        });
        try
        {
            Assert.That(lagging.Wait(Budget), Is.True, "premise: an async batch grew the file ahead of the tracked size");
            var store = new FakeBackupStore();
            Capture(dbe, store);
            var point = store.Points.Single();
            Assert.That(trackedAtLag, Is.LessThan(physicalAtLag), "premise: the tracked size lagged");
            Assert.That((long)point.ImagePages * PagedMMF.PageSize, Is.GreaterThanOrEqualTo(physicalAtLag),
                $"{Marker}: N was read from the lagging tracked size, not the file");
        }
        finally
        {
            release.Set();
            grower.Wait(Budget);
            mmf.FlushToDiskInterceptor = null;
        }
    }

    /// <summary>The image's checkpoint LSN is the one its own frozen meta pair holds, and the reclaim floor is at or below it (C14).</summary>
    [Test]
    [CancelAfter(15_000)]
    public void C_IsReadFromTheFrozenMetaPair_AndTheFloorIsBelowIt()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        Spawn(dbe, 50);
        var store = new FakeBackupStore();
        Capture(dbe, store);
        var point = store.Points.Single();
        var capture = dbe.BackupCoordinator.LastCapture;

        var view = BootstrapReader.Read(new FrozenMetaPageSource(point.Pages[0], point.Pages[1]));
        Assert.Multiple(() =>
        {
            Assert.That(point.CheckpointLsn, Is.EqualTo(view.ReadWatermarks().CheckpointLsn));
            Assert.That(point.CheckpointLsn, Is.GreaterThan(0), "premise: the forced checkpoint advanced C");
            Assert.That(capture.ReclaimFloor, Is.LessThanOrEqualTo(point.CheckpointLsn));
            Assert.That(dbe.CheckpointManager.ReclaimFloor, Is.EqualTo(long.MaxValue), "the floor is released once the tail is copied");
            Assert.That(point.PointLsn, Is.GreaterThanOrEqualTo(point.CheckpointLsn));
        });
    }
}
