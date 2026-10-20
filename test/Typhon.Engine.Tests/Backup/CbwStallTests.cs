using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// BKP-12 / AC-C4: a backup that cannot keep up aborts; the engine does not wait on it past <c>CbwStallTimeout</c>. Two copy-before-write slots, a
/// backup thread that never drains them, and a checkpoint rewriting more image pages than that: the third writer gives up at its budget, the
/// capture aborts with <see cref="BackupAbortReason.CbwStall"/>, the checkpoint completes, and the next incremental still covers every page.
/// </summary>
/// <remarks>
/// The budget is checked on a logical clock (<see cref="BackupCoordinator.CbwClock"/>), not on wall time. On a loaded box a writer polling the last
/// timer tick before its deadline was measured off the CPU for 12 ms between two of its checks, then gave up at the first check past the deadline:
/// the scheduler's time, which no budget can promise away. What BKP-12 does promise is checked exactly: with the clock held the writer keeps waiting;
/// moved to the deadline, it gives up having spent exactly its budget; and none of its blocking waits reaches into the last timer tick before the
/// deadline, where a kernel wait would wake a tick late.
/// </remarks>
[Category("Sensitive")]
class CbwStallTests : BackupTestBase<CbwStallTests>
{
    private static readonly TimeSpan Stall = TimeSpan.FromMilliseconds(50);
    private static readonly long StallTicks = (long)(Stall.TotalSeconds * Stopwatch.Frequency);

    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("BKP-12")]
    public void SlotPoolExhausted_TheWriterReturnsWithinBudget_AndTheCaptureAborts()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        var coordinator = dbe.BackupCoordinator;
        var store = new FakeBackupStore();
        var ids = Spawn(dbe, 600);
        Capture(dbe, store);

        // Pages written after the first freeze are the next incremental's; dirtied again, the checkpoint after the next freeze rewrites them.
        Update(dbe, ids, 1);
        Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(Budget), Is.True);
        Update(dbe, ids, 2);

        coordinator.Options.CheckpointBeforeFreeze = false;
        coordinator.Options.CbwBufferPages = 2;
        coordinator.Options.CbwStallTimeout = Stall;
        var clock = Stopwatch.GetTimestamp();
        coordinator.CbwClock = () => Volatile.Read(ref clock);
        var intoTheLastTick = 0;
        using var writerBlocked = new ManualResetEventSlim();
        var imageSet = new HashSet<int>();
        using var frozen = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        coordinator.Probe = (phase, _) =>
        {
            if (phase == CapturePhase.AfterFreeze)
            {
                var capture = coordinator.LastCapture;
                capture.Slots.BlockingWaitProbe = (now, ms, deadline) =>
                {
                    if (now + ms * Stopwatch.Frequency / 1000 > deadline - CbwSlotPool.TimerSlackTicks)
                    {
                        Interlocked.Increment(ref intoTheLastTick);
                    }

                    writerBlocked.Set();
                };
                for (var p = 0; p < capture.ImagePages; p++)
                {
                    if (capture.IsInImage(p))
                    {
                        imageSet.Add(p);
                    }
                }

                frozen.Set();
                release.Wait();
            }
        };

        var stalled = StartCapture(dbe, store);
        var lsnBefore = dbe.CheckpointManager.CheckpointLsn;
        try
        {
            Assert.That(frozen.Wait(Budget), Is.True);
            var rewrites = DirtyFilePages(dbe).Count(imageSet.Contains);
            Assert.That(rewrites, Is.GreaterThan(2), "premise: the checkpoint rewrites more image pages than there are slots");

            var checkpoint = Task.Run(() => dbe.CheckpointManager.ForceCheckpointAndWait(Budget));
            Assert.That(writerBlocked.Wait(Budget), Is.True, "premise: a writer found the slot pool empty and waits");
            Assert.That(checkpoint.Wait(200), Is.False, "the writer gave up before its budget ran out (the clock is held)");

            Interlocked.Add(ref clock, StallTicks);   // every budget started at the held instant: this is each one's deadline
            Assert.That(checkpoint.Wait(Budget), Is.True, "BKP-12: a writer kept waiting on the stalled backup past its deadline");
            Assert.That(checkpoint.Result, Is.True);
        }
        finally
        {
            release.Set();
        }

        var ex = Assert.Throws<AggregateException>(() => stalled.Wait(Budget));
        // The hook's own measure: from the start of a write's budget (a SavePages batch's, shared by its runs) to its return.
        var worst = coordinator.LastCapture.MaxHookElapsedTicks;
        Assert.Multiple(() =>
        {
            Assert.That(ex.InnerException, Is.TypeOf<BackupAbortedException>());
            Assert.That(((BackupAbortedException)ex.InnerException).Reason, Is.EqualTo(BackupAbortReason.CbwStall));
            AssertWithinBudget(worst);
            Assert.That(intoTheLastTick, Is.Zero, "BKP-12: a slot wait blocked into the last timer tick before its deadline");
            Assert.That(dbe.CheckpointManager.CheckpointLsn, Is.GreaterThan(lsnBefore), "the checkpoint advanced past the aborted capture");
        });

        // The lineage survived the abort and the page set was merged back: the next point is an incremental that holds it.
        coordinator.Probe = null;
        coordinator.CbwClock = null;
        coordinator.Options.CbwBufferPages = 256;
        var next = Capture(dbe, store);
        var nextPoint = store.Points.Last();
        Assert.That(next.Kind, Is.EqualTo(BackupKind.Incremental), "an abort leaves the lineage intact");
        Assert.That(imageSet.Where(p => !nextPoint.Pages.ContainsKey(p)), Is.Empty, "BKP-07: the aborted capture's pages are in the next incremental");
    }

    /// <summary>The budget check must reject a write that spent longer in the hook than the stall budget allows.</summary>
    [Test]
    [RuleMutant("BKP-12")]
    public void Mutant_AWriteOverItsBudget_IsReported()
        => RuleMutants.AssertDetects("BKP-12", "BKP-12: the slowest write", () => AssertWithinBudget(StallTicks + 1));

    /// <summary>On the logical clock: exactly the budget, no slack.</summary>
    private static void AssertWithinBudget(long slowestWriteTicks)
        => Assert.That(slowestWriteTicks, Is.LessThanOrEqualTo(StallTicks), "BKP-12: the slowest write spent longer in the backup hook than its budget");
}
