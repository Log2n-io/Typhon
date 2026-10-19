using System;
using System.Collections.Concurrent;
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
/// backup thread that never drains them, and a checkpoint rewriting more image pages than that: the third writer gives up within the budget, the
/// capture aborts with <see cref="BackupAbortReason.CbwStall"/>, the checkpoint completes, and the next incremental still covers every page.
/// </summary>
[Category("Sensitive")]
class CbwStallTests : BackupTestBase<CbwStallTests>
{
    private static readonly TimeSpan Stall = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan Slack = TimeSpan.FromMilliseconds(10);

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
        var imageSet = new HashSet<int>();
        using var frozen = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        coordinator.Probe = (phase, _) =>
        {
            if (phase == CapturePhase.AfterFreeze)
            {
                var capture = coordinator.LastCapture;
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
        var started = new ConcurrentDictionary<int, long>();
        var worst = 0L;
        try
        {
            Assert.That(frozen.Wait(Budget), Is.True);
            var rewrites = DirtyFilePages(dbe).Count(imageSet.Contains);
            Assert.That(rewrites, Is.GreaterThan(2), "premise: the checkpoint rewrites more image pages than there are slots");

            dbe.MMF.PageWriteInterceptor = _ => started[Environment.CurrentManagedThreadId] = Stopwatch.GetTimestamp();
            dbe.MMF.DataFileWriteProbe = (_, _) =>
            {
                if (started.TryRemove(Environment.CurrentManagedThreadId, out var t0))
                {
                    var spent = Stopwatch.GetTimestamp() - t0;
                    long seen;
                    while (spent > (seen = Volatile.Read(ref worst)) && Interlocked.CompareExchange(ref worst, spent, seen) != seen)
                    {
                    }
                }
            };

            var checkpoint = Task.Run(() => dbe.CheckpointManager.ForceCheckpointAndWait(Budget));
            Assert.That(checkpoint.Wait(TimeSpan.FromSeconds(2)), Is.True, "BKP-12: a writer waited on the stalled backup past its budget");
            Assert.That(checkpoint.Result, Is.True);
        }
        finally
        {
            dbe.MMF.PageWriteInterceptor = null;
            dbe.MMF.DataFileWriteProbe = null;
            release.Set();
        }

        var ex = Assert.Throws<AggregateException>(() => stalled.Wait(Budget));
        Assert.Multiple(() =>
        {
            Assert.That(ex.InnerException, Is.TypeOf<BackupAbortedException>());
            Assert.That(((BackupAbortedException)ex.InnerException).Reason, Is.EqualTo(BackupAbortReason.CbwStall));
            AssertWithinBudget(TimeSpan.FromSeconds((double)worst / Stopwatch.Frequency));
            Assert.That(dbe.CheckpointManager.CheckpointLsn, Is.GreaterThan(lsnBefore), "the checkpoint advanced past the aborted capture");
        });

        // The lineage survived the abort and the page set was merged back: the next point is an incremental that holds it.
        coordinator.Probe = null;
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
        => RuleMutants.AssertDetects("BKP-12", "BKP-12: the slowest write", () => AssertWithinBudget(Stall + Slack + TimeSpan.FromMilliseconds(1)));

    private static void AssertWithinBudget(TimeSpan slowestWrite)
        => Assert.That(slowestWrite, Is.LessThanOrEqualTo(Stall + Slack), "BKP-12: the slowest write spent longer in the backup hook than its budget");
}
