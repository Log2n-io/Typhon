using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// T2 (BKP-03): copy-before-write against the scanner, staged rather than waited for. A checkpoint rewrites pages of the frozen image while the scan is
/// paused; the point must still hold every page as the data file had it at the freeze.
/// </summary>
class CbwRaceTests : BackupTestBase<CbwRaceTests>
{
    private const string Marker = "BKP-03 copy-before-write";

    /// <summary>
    /// The scanner has claimed a page and not read it yet; a checkpoint writes that page. The writer must wait until the scanner's read is done, or the
    /// scanner would read the post-freeze bytes.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("BKP-03")]
    public void ScannerClaimedFirst_TheWriterWaits()
    {
        var (point, atFreeze, rewritten) = ScannerClaimedScenario(mutant: false);
        Assert.That(rewritten, Is.True, "premise: the checkpoint rewrote the claimed page after the freeze");
        AssertImageIsTheFileAtTheFreeze(point, atFreeze, Marker);
    }

    /// <summary>A writer that does not wait for a page the scanner claimed lets the scanner read post-freeze bytes: the verifier must see it.</summary>
    [Test]
    [CancelAfter(15_000)]
    [RuleMutant("BKP-03")]
    public void Mutant_TheWriterSkipsTheClaimWait_IsReported()
    {
        var (point, atFreeze, rewritten) = ScannerClaimedScenario(mutant: true);
        Assert.That(rewritten, Is.True, "premise: the checkpoint rewrote the claimed page after the freeze");
        RuleMutants.AssertDetects("BKP-03", Marker, () => AssertImageIsTheFileAtTheFreeze(point, atFreeze, Marker));
    }

    /// <summary>
    /// Writers reach unscanned pages first: each copies its page's pre-image before writing, and the scanner, finding the page claimed, takes the
    /// pre-image instead of reading the rewritten file.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("BKP-03")]
    public void WriterClaimedFirst_TheScannerTakesItsPreImage()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        var ids = Spawn(dbe, 400);
        Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(Budget), Is.True);
        Update(dbe, ids, 99);
        var coordinator = dbe.BackupCoordinator;
        coordinator.Options.CheckpointBeforeFreeze = false;

        byte[] atFreeze = null;
        int[] dirty = null;
        using var frozen = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        coordinator.Probe = (phase, _) =>
        {
            if (phase == CapturePhase.AfterFreeze)
            {
                atFreeze = ReadDataFile(dbe);
                dirty = DirtyFilePages(dbe).ToArray();
                frozen.Set();
                release.Wait();
            }
        };

        var store = new FakeBackupStore();
        var capture = StartCapture(dbe, store, BackupKind.Full);
        Assert.That(frozen.Wait(Budget), Is.True);
        Assert.That(dirty, Is.Not.Empty, "premise: pages are dirty at the freeze");
        Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(Budget), Is.True, "writers ahead of the scanner copy and proceed: nothing waits");
        release.Set();
        Assert.That(capture.Wait(Budget), Is.True);

        var point = store.Points.Single();
        var after = ReadDataFile(dbe);
        Assert.That(dirty.Any(p => !after.AsSpan(p * PagedMMF.PageSize, PagedMMF.PageSize).SequenceEqual(atFreeze.AsSpan(p * PagedMMF.PageSize,
            PagedMMF.PageSize))), Is.True, "premise: the checkpoint rewrote pages of the image");
        Assert.That(coordinator.LastCapture.CbwCopies, Is.GreaterThan(0), "premise: writers copied pre-images");
        AssertImageIsTheFileAtTheFreeze(point, atFreeze, Marker);
    }

    /// <summary>An abort releases a writer waiting on a page the scanner holds: the engine never waits on a dead backup.</summary>
    [Test]
    [CancelAfter(15_000)]
    public void Abort_ReleasesAWriterWaitingOnAClaimedPage()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        var ids = Spawn(dbe, 400);
        Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(Budget), Is.True);
        Update(dbe, ids, 5);
        var coordinator = dbe.BackupCoordinator;
        coordinator.Options.CheckpointBeforeFreeze = false;
        coordinator.Options.ScanRunPages = 1;
        coordinator.Options.CbwStallTimeout = TimeSpan.FromSeconds(30);

        var target = -1;
        using var scannerHolds = new ManualResetEventSlim();
        using var writerWaits = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        coordinator.Probe = (phase, page) =>
        {
            switch (phase)
            {
                case CapturePhase.AfterFreeze:
                    target = DirtyFilePages(dbe).First(p => p >= 2 && p < coordinator.LastCapture.ImagePages);
                    break;
                case CapturePhase.ScannerClaimed when page == target:
                    scannerHolds.Set();
                    release.Wait();
                    break;
                case CapturePhase.WriterWaiting when page == target:
                    writerWaits.Set();
                    break;
            }
        };

        var capture = StartCapture(dbe, new FakeBackupStore(), BackupKind.Full);
        try
        {
            Assert.That(scannerHolds.Wait(Budget), Is.True, "premise: the scanner holds the dirty page");
            var checkpoint = Task.Run(() => dbe.CheckpointManager.ForceCheckpointAndWait(Budget));
            Assert.That(writerWaits.Wait(Budget), Is.True, "premise: the checkpoint waits on the held page");

            coordinator.LastCapture.Abort(BackupAbortReason.Cancelled);
            Assert.That(checkpoint.Wait(Budget) && checkpoint.Result, Is.True, "an abort must release the waiting writer");
        }
        finally
        {
            release.Set();
        }

        var ex = Assert.Throws<AggregateException>(() => capture.Wait(Budget));
        Assert.That(ex.InnerException, Is.TypeOf<BackupAbortedException>());
        Assert.That(((BackupAbortedException)ex.InnerException).Reason, Is.EqualTo(BackupAbortReason.Cancelled));
    }

    private (CapturingSink Point, byte[] AtFreeze, bool Rewritten) ScannerClaimedScenario(bool mutant)
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        var ids = Spawn(dbe, 400);
        Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(Budget), Is.True);
        Update(dbe, ids, 42);
        var coordinator = dbe.BackupCoordinator;
        coordinator.Options.CheckpointBeforeFreeze = false;
        coordinator.Options.ScanRunPages = 1;
        coordinator.Options.CbwStallTimeout = TimeSpan.FromSeconds(30);
        coordinator.CaptureCreated = c => c.SkipClaimWaitForMutant = mutant;

        byte[] atFreeze = null;
        var target = -1;
        using var scannerHolds = new ManualResetEventSlim();
        using var writerWaits = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        coordinator.Probe = (phase, page) =>
        {
            switch (phase)
            {
                case CapturePhase.AfterFreeze:
                    atFreeze = ReadDataFile(dbe);
                    target = DirtyFilePages(dbe).First(p => p >= 2 && p < coordinator.LastCapture.ImagePages);
                    break;
                case CapturePhase.ScannerClaimed when page == target:
                    scannerHolds.Set();
                    release.Wait();
                    break;
                case CapturePhase.WriterWaiting when page == target:
                    writerWaits.Set();
                    break;
            }
        };

        var store = new FakeBackupStore();
        var capture = StartCapture(dbe, store, BackupKind.Full);
        Task<bool> checkpoint;
        try
        {
            Assert.That(scannerHolds.Wait(Budget), Is.True, "premise: the scanner claimed a dirty page and paused before reading it");
            checkpoint = Task.Run(() => dbe.CheckpointManager.ForceCheckpointAndWait(Budget));
            if (mutant)
            {
                Assert.That(checkpoint.Wait(Budget), Is.True, "premise: the mutant writer did not wait");
            }
            else
            {
                Assert.That(writerWaits.Wait(Budget), Is.True, "premise: the checkpoint reached the claimed page and waits");
            }
        }
        finally
        {
            release.Set();
        }

        Assert.That(capture.Wait(Budget), Is.True, "the capture did not finish");
        Assert.That(checkpoint.Wait(Budget) && checkpoint.Result, Is.True, "the checkpoint completes once the page is captured");
        var after = ReadDataFile(dbe);
        var rewritten = !after.AsSpan(target * PagedMMF.PageSize, PagedMMF.PageSize).SequenceEqual(atFreeze.AsSpan(target * PagedMMF.PageSize,
            PagedMMF.PageSize));
        return (store.Points.Single(), atFreeze, rewritten);
    }
}
