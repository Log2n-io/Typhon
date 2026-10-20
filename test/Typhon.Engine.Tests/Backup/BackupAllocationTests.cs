using System;
using System.IO;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// What an online backup costs the GC. A data-file writer's copy-before-write allocates nothing, and the backup thread allocates a small fixed amount
/// per point, whatever the database's size: no per-page garbage in the scan, and no copy of a WAL segment to compute the point LSN (it was 64 MiB a
/// segment).
/// </summary>
class BackupAllocationTests : BackupTestBase<BackupAllocationTests>
{
    private const int WrittenPages = 64;   // under the slot pool's 256: every write copies its pre-image with the backup thread held

    private string _store;

    public override void Setup()
    {
        base.Setup();
        _store = Path.Combine(TestStorage.ForFixture(nameof(BackupAllocationTests)), Guid.NewGuid().ToString("N"));
    }

    public override void TearDown()
    {
        base.TearDown();
        try
        {
            Directory.Delete(_store, true);
        }
        catch (IOException)
        {
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public void ACapture_AllocatesNothingPerWriteOrPage_AndLittlePerPoint()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        for (var batch = 0; batch < 10; batch++)
        {
            Spawn(dbe, 10_000, batch * 10_000);
            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(Budget), Is.True);
        }

        Run(dbe);   // a first point JITs every path it takes
        var m = Run(dbe);

        Assert.That(m.ImagePages, Is.GreaterThanOrEqualTo(2048), "premise: enough pages that a per-page cost shows");
        Assert.That(m.CbwCopies, Is.EqualTo(WrittenPages), "premise: every write copied its page's pre-image");
        Assert.That(m.FirstHookedWrite - m.FirstPlainWrite, Is.LessThanOrEqualTo(0),
            "the first write of a backup allocated: a chunk of the fresh change map, or the slot pool's wait");
        Assert.That(m.HookedWrites - m.PlainWrites, Is.LessThanOrEqualTo(0), $"copy-before-write allocated on the writer over {WrittenPages - 1} writes");
        Assert.That(m.PointLsn, Is.LessThanOrEqualTo(16 << 10), "the point LSN's read of the tail allocated a copy of it");
        Assert.That(m.Scan, Is.LessThanOrEqualTo(4 << 10), $"the scan of {m.ImagePages} pages allocated per page");
        Assert.That(m.Capture, Is.LessThanOrEqualTo(128 << 10), "the backup thread's allocations, freeze to completion");
    }

    private record struct Measures(int ImagePages, long CbwCopies, long FirstHookedWrite, long FirstPlainWrite, long HookedWrites, long PlainWrites,
        long PointLsn, long Scan, long Capture);

    /// <summary>
    /// One full point. With the backup thread held after the freeze, <see cref="WrittenPages"/> pages are rewritten through the write chokepoint (each
    /// copies its pre-image), then rewritten again once the point is published, without a backup: the difference is what the backup costs a writer.
    /// The backup thread's own allocations are read at its phases.
    /// </summary>
    private Measures Run(DatabaseEngine dbe)
    {
        long afterFreeze = 0, beforePoint = 0, tailCopied = 0, beforeComplete = 0;
        using var frozen = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var coordinator = dbe.BackupCoordinator;
        coordinator.Probe = (phase, _) =>
        {
            switch (phase)
            {
                case CapturePhase.AfterFreeze:
                    frozen.Set();
                    release.Wait(Budget);
                    afterFreeze = GC.GetAllocatedBytesForCurrentThread();
                    break;
                case CapturePhase.TailBeforePoint:
                    beforePoint = GC.GetAllocatedBytesForCurrentThread();
                    break;
                case CapturePhase.TailCopied:
                    tailCopied = GC.GetAllocatedBytesForCurrentThread();
                    break;
                case CapturePhase.BeforeComplete:
                    beforeComplete = GC.GetAllocatedBytesForCurrentThread();
                    break;
            }
        };

        var mmf = dbe.MMF;
        var page = new byte[PagedMMF.PageSize];
        var running = dbe.Backup.CreateAsync(_store, BackupRequestKind.Full);
        long firstHooked, hooked, cbwCopies;
        int imagePages;
        try
        {
            Assert.That(frozen.Wait(Budget), Is.True, "premise: the capture froze");
            var capture = coordinator.LastCapture;
            imagePages = capture.ImagePages;
            firstHooked = Rewrite(mmf, page, 2, 1);
            hooked = Rewrite(mmf, page, 3, WrittenPages - 1);
            cbwCopies = capture.CbwCopies;
        }
        finally
        {
            release.Set();
        }

        Assert.That(running.Wait(Budget), Is.True, "the capture did not finish");
        coordinator.Probe = null;
        var firstPlain = Rewrite(mmf, page, 2, 1);
        var plain = Rewrite(mmf, page, 3, WrittenPages - 1);
        return new Measures(imagePages, cbwCopies, firstHooked, firstPlain, hooked, plain, tailCopied - beforePoint, beforeComplete - tailCopied,
            beforeComplete - afterFreeze);
    }

    /// <summary>Rewrites file pages <c>[first, first + count)</c> with their own bytes through the write chokepoint; returns the bytes it allocated.</summary>
    private static long Rewrite(PagedMMF mmf, byte[] page, int first, int count)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var p = first; p < first + count; p++)
        {
            mmf.ReadPageDirect(p, page);
            mmf.WritePageDirect(p, page);
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
