using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// T5 (BKP-07): a capture that does not complete forgets no page. Its page set goes back into the live change map, so the next incremental holds it;
/// and an engine closed in the middle of a capture closes cleanly with the set merged back.
/// </summary>
class BackupFailurePathTests : BackupTestBase<BackupFailurePathTests>
{
    private const string Marker = "BKP-07 forgotten page";

    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("BKP-07")]
    public void SinkThrowsMidScan_TheNextIncrementalCoversEverything()
    {
        var (failedSet, next) = SinkFailureScenario();
        AssertCovers(next, failedSet);
    }

    /// <summary>The verifier must see a point that lacks one page of the failed capture's set.</summary>
    [Test]
    [CancelAfter(15_000)]
    [RuleMutant("BKP-07")]
    public void Mutant_APageOfTheFailedCaptureIsMissing_IsReported()
    {
        var (failedSet, next) = SinkFailureScenario();
        var dropped = failedSet.First(p => p >= 2);
        next.Pages.Remove(dropped);
        RuleMutants.AssertDetects("BKP-07", Marker, () => AssertCovers(next, failedSet));
    }

    /// <summary>The engine closes in the middle of a capture, at each phase: the close is clean and the live map holds the abandoned page set.</summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("BKP-07")]
    public void DisposeDuringCapture_ClosesCleanly_AndTheMapHoldsThePageSet(
        [Values(CapturePhase.AfterFreeze, CapturePhase.TailCopied, CapturePhase.ScannerClaimed, CapturePhase.BeforeComplete)] CapturePhase phase)
    {
        var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        var coordinator = dbe.BackupCoordinator;
        var store = new FakeBackupStore();
        var ids = Spawn(dbe, 300);
        Capture(dbe, store);
        Update(dbe, ids, 3);
        Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(Budget), Is.True);

        var imageSet = new List<int>();
        using var reached = new ManualResetEventSlim();
        coordinator.Probe = (p, _) =>
        {
            var capture = coordinator.LastCapture;
            if (p == CapturePhase.AfterFreeze)
            {
                for (var page = 0; page < capture.ImagePages; page++)
                {
                    if (capture.IsInImage(page))
                    {
                        imageSet.Add(page);
                    }
                }
            }

            if (p == phase && !reached.IsSet)
            {
                reached.Set();
                var spin = new SpinWait();
                while (capture.AbortReason == BackupAbortReason.None)
                {
                    spin.SpinOnce(-1);
                }
            }
        };

        var running = StartCapture(dbe, store);
        Assert.That(reached.Wait(Budget), Is.True, $"premise: the capture reached {phase}");
        var map = dbe.MMF.ChangeMap;
        var closing = Task.Run(scope.Dispose);
        Assert.That(closing.Wait(Budget), Is.True, "the close must not wait on the capture");

        var ex = Assert.Throws<AggregateException>(() => running.Wait(Budget));
        Assert.That(((BackupAbortedException)ex.InnerException).Reason, Is.EqualTo(BackupAbortReason.EngineStopping));
        Assert.That(store.Points, Has.Count.EqualTo(1), "nothing was published");
        Assert.That(imageSet.Where(p => !map.IsSet(p)), Is.Empty, $"{Marker}: the closed engine's map lacks pages of the abandoned capture");

        // AC-C8: the clean close persisted the merged map, so the next session's first point is an incremental that holds the abandoned set.
        using var reopened = ServiceProvider.CreateScope();
        var next = Open(reopened);
        Assert.That(next.LastOpenBackupStateResult, Is.EqualTo(BackupStateReadResult.Loaded));
        var point = Capture(next, store);
        Assert.That(point.Kind, Is.EqualTo(BackupKind.Incremental));
        AssertCovers(store.Points.Last(), imageSet);
    }

    /// <summary>AC-C8: a hard crash in the middle of a capture leaves the next session untrusted, so its first point is full.</summary>
    [Test]
    [CancelAfter(15_000)]
    public void HardCrashDuringCapture_TheNextPointIsFull()
    {
        var store = new FakeBackupStore();
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            Spawn(dbe, 100);
            Capture(dbe, store);
            var coordinator = dbe.BackupCoordinator;
            using var reached = new ManualResetEventSlim();
            coordinator.Probe = (p, _) =>
            {
                if (p == CapturePhase.TailCopied)
                {
                    reached.Set();
                    var spin = new SpinWait();
                    while (coordinator.LastCapture.AbortReason == BackupAbortReason.None)
                    {
                        spin.SpinOnce(-1);
                    }
                }
            };

            var running = StartCapture(dbe, store);
            Assert.That(reached.Wait(Budget), Is.True);
            dbe.SimulateHardCrash();
            Assert.Throws<AggregateException>(() => running.Wait(Budget));
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            Assert.That(Capture(dbe, store).Kind, Is.EqualTo(BackupKind.Full), "a crash leaves no trusted map");
        }
    }

    private (List<int> FailedSet, CapturingSink Next) SinkFailureScenario()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        var coordinator = dbe.BackupCoordinator;
        var store = new FakeBackupStore();
        var ids = Spawn(dbe, 300);
        Capture(dbe, store);
        Update(dbe, ids, 9);
        Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(Budget), Is.True);

        var failedSet = new List<int>();
        coordinator.Probe = (phase, _) =>
        {
            if (phase == CapturePhase.AfterFreeze)
            {
                var capture = coordinator.LastCapture;
                for (var p = 0; p < capture.ImagePages; p++)
                {
                    if (capture.IsInImage(p))
                    {
                        failedSet.Add(p);
                    }
                }
            }
        };

        var added = 0;
        store.OnBeginPoint = sink => sink.OnAddPage = _ =>
        {
            if (++added == 4)
            {
                throw new IOException("store full");
            }
        };
        var failed = StartCapture(dbe, store);
        var ex = Assert.Throws<AggregateException>(() => failed.Wait(Budget));
        Assert.That(((BackupAbortedException)ex.InnerException).Reason, Is.EqualTo(BackupAbortReason.SinkFailed));
        Assert.That(store.Begun.Last().Aborted, Is.True, "the failed point was abandoned");
        Assert.That(failedSet.Count(p => p >= 2), Is.GreaterThan(3), "premise: the failed capture had pages left to copy");

        store.OnBeginPoint = null;
        coordinator.Probe = null;
        var next = Capture(dbe, store);
        Assert.That(next.Kind, Is.EqualTo(BackupKind.Incremental), "a failed capture leaves the lineage as it was");
        return (failedSet, store.Points.Last());
    }

    private static void AssertCovers(CapturingSink point, List<int> pages)
    {
        var missing = pages.Where(p => !point.Pages.ContainsKey(p)).ToList();
        Assert.That(missing, Is.Empty, $"{Marker}: the next incremental lacks pages of the failed capture: {string.Join(", ", missing)}");
    }
}
