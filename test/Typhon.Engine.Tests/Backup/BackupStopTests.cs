using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// Stopping a capture (BKP-07, BKP-12): an engine closing during a capture neither waits on its cancellable waits nor frees memory under a backup thread
/// it could not join; a request nobody took is failed, not run during the close; the lineage a stuck capture would publish stays revoked; the gate's
/// exclusive attempts stall writers within one budget in all; the freeze converges when the file's tracked length lags; the floor is released
/// whatever the failure path throws.
/// </summary>
class BackupStopTests : BackupTestBase<BackupStopTests>
{
    private GatedWalFileIO _gatedIo;

    protected override IWalFileIO CreateWalFileIO() => _gatedIo = new GatedWalFileIO();

    private static void ShortStop(BackupCoordinator coordinator)
    {
        coordinator.Options.FreezeAcquireTimeout = TimeSpan.FromMilliseconds(200);
        coordinator.Options.UninstallTimeout = TimeSpan.FromMilliseconds(200);
        coordinator.Options.StoreStopGrace = TimeSpan.FromMilliseconds(200);
    }

    /// <summary>
    /// The store is stuck in Complete when the engine closes: the close gives up joining within its budget, leaves the capture its buffers (freeing them
    /// would be a use-after-free once the store returns), and revokes the lineage; the publish that follows cannot trust it again.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    public void ACloseThatCannotJoin_LeavesTheCaptureItsMemory_AndThePublishCannotRetrust()
    {
        var store = new FakeBackupStore();
        var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        var coordinator = dbe.BackupCoordinator;
        ShortStop(coordinator);
        Spawn(dbe, 100);
        Capture(dbe, store);
        Assert.That(coordinator.Lineage.Trusted, Is.True, "premise: a published point trusts the lineage");
        var bundle = dbe.MMF.BundleDirectory;

        using var stuck = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        store.OnBeginPoint = sink => sink.OnComplete = () =>
        {
            stuck.Set();
            release.Wait();
        };
        var running = StartCapture(dbe, store);
        Task closing;
        try
        {
            Assert.That(stuck.Wait(Budget), Is.True, "premise: the store is stuck in Complete");
            var capture = coordinator.LastCapture;
            closing = Task.Run(scope.Dispose);
            Assert.That(closing.Wait(Budget), Is.True, "the close must not wait on a stuck store past its budget");
            Assert.Multiple(() =>
            {
                Assert.That(coordinator.Detached, Is.True, "premise: the close gave up joining");
                Assert.That(capture.ScanBufferAlive, Is.True, "the close freed a buffer the still-running capture owns");
                Assert.That(coordinator.Lineage.Trusted, Is.False, "the close must revoke the lineage it could not settle");
            });
        }
        finally
        {
            release.Set();
        }

        Assert.That(SpinUntil(() => running.IsCompleted), Is.True, "the capture finishes once the store returns");
        Assert.That(coordinator.Lineage.Trusted, Is.False, "a publish after the revoke made the lineage trusted again");
        Assert.That(File.Exists(BackupStateFile.PathIn(bundle)), Is.False, "the close must not have vouched for the map");
    }

    /// <summary>The capture waits on its forced checkpoint, held mid-cycle: a close cancels the wait instead of sitting it out.</summary>
    [Test]
    [CancelAfter(15_000)]
    public void ACloseDuringTheForcedCheckpoint_DoesNotWaitForIt()
    {
        var store = new FakeBackupStore();
        var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        var coordinator = dbe.BackupCoordinator;
        ShortStop(coordinator);
        coordinator.Options.CheckpointTimeout = TimeSpan.FromSeconds(60);
        var ids = Spawn(dbe, 200);
        Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(Budget), Is.True);
        Update(dbe, ids, 3);

        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var armed = 1;
        dbe.CheckpointManager.BeforeSettleProbe = () =>
        {
            if (Interlocked.Exchange(ref armed, 0) == 1)
            {
                held.Set();
                release.Wait();
            }
        };
        var cycle = Task.Run(() => dbe.CheckpointManager.ForceCheckpointAndWait(Budget));
        Task closing;
        try
        {
            Assert.That(held.Wait(Budget), Is.True, "premise: a cycle is held mid-pass");
            using var waiting = new ManualResetEventSlim();
            dbe.CheckpointManager.AfterForceRequested = () => waiting.Set();
            var running = StartCapture(dbe, store);
            Assert.That(waiting.Wait(Budget), Is.True, "premise: the capture requested its checkpoint");
            Assert.That(running.Wait(100), Is.False, "premise: the capture waits for its checkpoint");
            closing = Task.Run(scope.Dispose);
            Assert.That(SpinUntil(() => running.IsCompleted, TimeSpan.FromSeconds(3)), Is.True,
                "the capture's checkpoint wait ignored the close: it sat out the held cycle");
            Assert.That(coordinator.Detached, Is.False, "the close joined the backup thread");
        }
        finally
        {
            release.Set();
        }

        cycle.Wait(Budget);
        Assert.That(closing.Wait(Budget), Is.True);
    }

    /// <summary>The capture waits for the WAL to become durable through the freeze, and the WAL writer is held: a close cancels the wait.</summary>
    [Test]
    [CancelAfter(15_000)]
    public void ACloseDuringTheTailFlush_DoesNotWaitForIt()
    {
        var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        var coordinator = dbe.BackupCoordinator;
        ShortStop(coordinator);
        coordinator.Options.CheckpointBeforeFreeze = false;
        coordinator.Options.WalFlushTimeout = TimeSpan.FromSeconds(60);
        coordinator.Options.SegmentSwitchTimeout = TimeSpan.Zero;   // the held writer cannot serve the switch; go straight to the freeze
        var ids = Spawn(dbe, 50);
        Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(Budget), Is.True);

        _gatedIo.Hold();
        Task closing;
        try
        {
            Update(dbe, ids, 2, DurabilityMode.Deferred);
            dbe.WalManager.RequestFlush();
            Assert.That(_gatedIo.WriterHeld.Wait(Budget), Is.True, "premise: the WAL writer is held with commits published");
            using var frozen = new ManualResetEventSlim();
            coordinator.Probe = (phase, _) =>
            {
                if (phase == CapturePhase.AfterFreeze)
                {
                    frozen.Set();
                }
            };
            var running = StartCapture(dbe, new FakeBackupStore());
            Assert.That(frozen.Wait(Budget), Is.True, "premise: the capture froze and went on to its tail");
            Assert.That(running.Wait(200), Is.False, "premise: the capture waits for the WAL");
            closing = Task.Run(scope.Dispose);
            Assert.That(SpinUntil(() => running.IsCompleted, TimeSpan.FromSeconds(3)), Is.True,
                "the capture's WAL wait ignored the close: it sat out the held writer");
            Assert.That(coordinator.Detached, Is.False, "the close joined the backup thread");
        }
        finally
        {
            _gatedIo.Release();
        }

        Assert.That(closing.Wait(Budget), Is.True);
    }

    /// <summary>A request the backup thread has not taken when the close begins is failed by the close, never run during it.</summary>
    [Test]
    [CancelAfter(15_000)]
    public void ARequestPendingAtTheClose_IsFailed_NotRun()
    {
        var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        var coordinator = dbe.BackupCoordinator;
        ShortStop(coordinator);
        using var woke = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        coordinator.BeforeTakingRequest = () =>
        {
            woke.Set();
            release.Wait();
        };

        var request = StartCapture(dbe, new FakeBackupStore());
        Task closing;
        try
        {
            Assert.That(woke.Wait(Budget), Is.True, "premise: the thread woke and has not taken the request");
            closing = Task.Run(scope.Dispose);
            Assert.That(SpinUntil(() => request.IsCompleted, TimeSpan.FromSeconds(3)), Is.True, "the close left a pending request to run during it");
        }
        finally
        {
            release.Set();
        }

        Assert.That(closing.Wait(Budget), Is.True);
        var ex = Assert.Throws<AggregateException>(() => request.Wait());
        Assert.That(((BackupAbortedException)ex.InnerException).Reason, Is.EqualTo(BackupAbortReason.EngineStopping));
        Assert.That(coordinator.LastCapture, Is.Null, "no capture may start once the close has begun");
    }

    /// <summary>
    /// BKP-12, cumulative: the hook's removal cannot get the gate (a write holds it), so it gives up within its budget, and a writer's total stall over
    /// all the attempts stays within that budget too, not the budget times the attempts.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("BKP-12")]
    public void AnUninstallThatCannotGetTheGate_StallsWritersWithinOneBudget()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        var coordinator = dbe.BackupCoordinator;
        coordinator.Options.UninstallTimeout = TimeSpan.FromMilliseconds(300);
        coordinator.Options.FreezeAcquireTimeout = TimeSpan.FromSeconds(2);
        Spawn(dbe, 50);
        var gate = dbe.MMF.WriteGate;

        using var holding = new ManualResetEventSlim();
        using var stop = new CancellationTokenSource();
        var holder = new Thread(() =>
        {
            var token = gate.EnterShared();
            holding.Set();
            stop.Token.WaitHandle.WaitOne();
            gate.ExitShared(token);
        });
        long stalled = 0, worst = 0;
        var writer = new Thread(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                var t0 = Stopwatch.GetTimestamp();
                var token = gate.EnterShared();
                var spent = Stopwatch.GetTimestamp() - t0;
                gate.ExitShared(token);
                stalled += spent;
                worst = Math.Max(worst, spent);
                Thread.Yield();
            }
        });

        coordinator.Probe = (phase, _) =>
        {
            if (phase == CapturePhase.BeforeComplete)
            {
                holder.Start();
                holding.Wait();
                writer.Start();
            }
        };

        var sw = Stopwatch.StartNew();
        BackupPointInfo point;
        try
        {
            point = Capture(dbe, new FakeBackupStore());
        }
        finally
        {
            stop.Cancel();
            holder.Join();
            if (writer.ThreadState != System.Threading.ThreadState.Unstarted)
            {
                writer.Join();
            }
        }

        var budget = coordinator.Options.UninstallTimeout + TimeSpan.FromMilliseconds(250);
        Assert.Multiple(() =>
        {
            Assert.That(point.PageCount, Is.GreaterThan(0), "the point completes; only its hook stays");
            Assert.That(sw.Elapsed, Is.LessThan(budget + TimeSpan.FromSeconds(1)), "BKP-12: the uninstall exceeded its budget");
            Assert.That(TimeSpan.FromSeconds(stalled / (double)Stopwatch.Frequency), Is.LessThan(budget),
                "BKP-12: a writer's stall summed over the uninstall's attempts exceeded the budget");
            Assert.That(TimeSpan.FromSeconds(worst / (double)Stopwatch.Frequency), Is.LessThan(BackupCapture.GateAttemptSlice + TimeSpan.FromMilliseconds(50)),
                "one attempt held writers back longer than a slice");
        });
    }

    /// <summary>
    /// The tracked length lags an async batch that grew the file past the freeze's margin: the freeze retries sized from the length it saw, and
    /// converges instead of exhausting its attempts.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    public void AFreezeThatFindsTheFileLonger_RetriesSizedFromWhatItSaw()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        var coordinator = dbe.BackupCoordinator;
        coordinator.Options.CheckpointBeforeFreeze = false;
        coordinator.Options.FreezeGrowthMarginPages = 0;
        var mmf = dbe.MMF;

        using var lagging = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var armed = 1;
        mmf.FlushToDiskInterceptor = () =>
        {
            if (new FileInfo(mmf.DataFilePath).Length > mmf.FileSize && Interlocked.Exchange(ref armed, 0) == 1)
            {
                lagging.Set();
                release.Wait();
            }
        };

        var grower = Task.Run(() =>
        {
            var cs = mmf.CreateChangeSet();
            mmf.AllocateSegment(PageBlockType.None, 64, cs);
            cs.SaveChanges();
        });
        try
        {
            Assert.That(lagging.Wait(Budget), Is.True, "premise: the tracked size lags an async batch");
            var store = new FakeBackupStore();
            Capture(dbe, store);
            Assert.That(coordinator.LastCapture.FreezeRetries, Is.GreaterThan(0), "premise: the first attempt found the file longer");
            Assert.That(store.Points.Single().ImagePages, Is.EqualTo((int)(new FileInfo(mmf.DataFilePath).Length / PagedMMF.PageSize)));
        }
        finally
        {
            release.Set();
            grower.Wait(Budget);
            mmf.FlushToDiskInterceptor = null;
        }
    }

    /// <summary>The failure path releases the reclaim floor even when a step of it throws (here, a store whose Abort breaks its contract).</summary>
    [Test]
    [CancelAfter(15_000)]
    public void TheFloorIsReleased_EvenWhenTheFailurePathThrows()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        Spawn(dbe, 50);
        var store = new FakeBackupStore();
        store.OnBeginPoint = sink => sink.OnAbort = () => throw new InvalidOperationException("Abort threw");
        dbe.BackupCoordinator.Probe = (phase, _) =>
        {
            if (phase == CapturePhase.AfterFreeze)
            {
                Assert.That(dbe.CheckpointManager.ReclaimFloor, Is.Not.EqualTo(long.MaxValue), "premise: the floor is held");
                throw new IOException("failure while the floor is held");
            }
        };
        Assert.Throws<AggregateException>(() => StartCapture(dbe, store).Wait(Budget));
        Assert.That(dbe.CheckpointManager.ReclaimFloor, Is.EqualTo(long.MaxValue), "a failure path that threw left WAL reclaim held");
    }

    [Test]
    public void ScanAndSlotCounts_RejectZero()
    {
        var options = new BackupCaptureOptions();
        Assert.Throws<ArgumentOutOfRangeException>(() => options.ScanRunPages = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => options.CbwBufferPages = 0);
    }

    private static bool SpinUntil(Func<bool> condition, TimeSpan? within = null)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)((within ?? Budget).TotalSeconds * Stopwatch.Frequency);
        while (!condition())
        {
            if (Stopwatch.GetTimestamp() >= deadline)
            {
                return false;
            }

            Thread.Yield();
        }

        return true;
    }
}
