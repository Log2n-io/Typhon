using System;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// CK-14 (#1382): the checkpoint's durable-metadata hook never waits for a slot only its own cycle frees. Under a write storm the hook's page — the
/// archetype table's — had been evicted while every other slot owed a write; the hook waited in back-pressure for the whole timeout, the checkpoint
/// wrote nothing meanwhile, and every writer behind it timed out too (3 of 3 instrumented market failures). A hook that would wait now gives up, the
/// cycle only writes, and the next cycle runs the hook again.
/// </summary>
[TestFixture]
[NonParallelizable] // TimeoutOptions.Current is process-wide
class CheckpointHookDeferralTests : TestBase<CheckpointHookDeferralTests>
{
    /// <summary>An engine whose next cycle's hook has an archetype to persist, and whose archetype-table page is out of the cache.</summary>
    private DatabaseEngine OpenEngineWithAColdHookPage()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<StatsRanked>();
        dbe.RegisterComponentFromAccessor<StatsTag>();
        dbe.InitializeArchetypes();
        Spawn(dbe, 0);
        Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "premise: the archetype state is persisted, clean");

        // New entities move the archetype's NextEntityKey: the next hook updates its row, so it loads the archetype table's page.
        Spawn(dbe, 1_000);
        dbe.MMF.EvictEvictablePagesForTest();
        return dbe;
    }

    private static void Spawn(DatabaseEngine dbe, int from)
    {
        using var tx = dbe.CreateQuickTransaction();
        for (var i = from; i < from + 64; i++)
        {
            tx.Spawn<StatsArch>(StatsArch.Ranked.Set(new StatsRanked(i, i)), StatsArch.Tag.Set(new StatsTag(i)));
        }

        Assert.That(tx.Commit(), Is.True);
    }

    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("CK-14")]
    public void AHookThatWouldWaitOnBackpressure_GivesUp_AndTheCycleOnlyWrites()
    {
        using var dbe = OpenEngineWithAColdHookPage();
        var ckpt = dbe.CheckpointManager;
        var lsnBefore = ckpt.CheckpointLsn;
        var deferrals = ckpt.TotalHookDeferrals;
        var flushOnly = ckpt.TotalFlushOnlyCycles;
        var me = Environment.CurrentManagedThreadId;
        dbe.MMF.ForceBackpressureForTest = () => Environment.CurrentManagedThreadId == me;
        var sw = Stopwatch.StartNew();
        try
        {
            ckpt.RunCheckpointCycle(dbe.WalManager.DurableLsn);
        }
        finally
        {
            dbe.MMF.ForceBackpressureForTest = null;
        }

        sw.Stop();
        Assert.That(ckpt.TotalHookDeferrals - deferrals, Is.EqualTo(1), "the hook gave up");
        Assert.That(ckpt.TotalFlushOnlyCycles - flushOnly, Is.EqualTo(1), "the cycle only wrote");
        Assert.That(ckpt.CheckpointLsn, Is.EqualTo(lsnBefore), "a flush-only cycle does not move CheckpointLSN");
        Assert.That(sw.Elapsed, Is.LessThan(TimeoutOptions.Current.PageCacheBackpressureTimeout), "it never waited out the timeout");
        Assert.That(ckpt.Health, Is.Not.EqualTo(DurabilityHealth.Fatal));
        Assert.That(dbe.MMF.CountUnevictablePages().Marked, Is.Zero, "the hook given up left no page under its marks");

        // Served, the next cycle runs the hook to its end and advances.
        ckpt.RunCheckpointCycle(dbe.WalManager.DurableLsn);
        Assert.That(ckpt.TotalHookDeferrals - deferrals, Is.EqualTo(1), "the hook ran");
        Assert.That(ckpt.CheckpointLsn, Is.GreaterThan(lsnBefore), "the full cycle advanced CheckpointLSN");
    }

    /// <summary>
    /// A hook that is never served cannot stall CheckpointLSN: once it has not run for <see cref="CheckpointManager.HookDeferralWindow"/> it waits for
    /// its page, as before, bounded by the back-pressure timeout.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("CK-14")]
    public void OnceTheHookHasNotRunForItsWindow_ItWaitsForItsPage()
    {
        using var dbe = OpenEngineWithAColdHookPage();
        var ckpt = dbe.CheckpointManager;
        var deferrals = ckpt.TotalHookDeferrals;
        var saved = TimeoutOptions.Current;
        var window = CheckpointManager.HookDeferralWindow;
        var timeouts = saved.Clone();
        timeouts.PageCacheBackpressureTimeout = TimeSpan.FromMilliseconds(100);
        timeouts.PageCacheOverloadWaitCeiling = TimeSpan.FromMilliseconds(500);   // the engine's own threads may serve pages meanwhile (S1)
        TimeoutOptions.Current = timeouts;
        var me = Environment.CurrentManagedThreadId;
        var waitedRounds = 0;
        dbe.MMF.ForceBackpressureForTest = () =>
        {
            if (Environment.CurrentManagedThreadId != me)
            {
                return false;
            }

            if (PagedMMF.NoBackpressureWaitDepth == 0)
            {
                waitedRounds++;
            }

            return true;
        };
        try
        {
            CheckpointManager.HookDeferralWindow = TimeSpan.MaxValue;
            ckpt.RunCheckpointCycle(dbe.WalManager.DurableLsn);
            Assert.That(ckpt.TotalHookDeferrals - deferrals, Is.EqualTo(1), "premise: within its window the hook deferred");
            Assert.That(waitedRounds, Is.Zero, "premise: the deferred hook did not wait");

            CheckpointManager.HookDeferralWindow = TimeSpan.Zero;
            ckpt.RunCheckpointCycle(dbe.WalManager.DurableLsn);
        }
        finally
        {
            dbe.MMF.ForceBackpressureForTest = null;
            TimeoutOptions.Current = saved;
            CheckpointManager.HookDeferralWindow = window;
        }

        Assert.That(ckpt.TotalHookDeferrals - deferrals, Is.EqualTo(1), "past its window the hook did not defer");
        Assert.That(waitedRounds, Is.GreaterThan(0), "it waited for its page");
        Assert.That(ckpt.Health, Is.EqualTo(DurabilityHealth.Degraded), "its wait timed out, a transient cycle failure (CK-06)");
    }

    /// <summary>
    /// Past its window, the hook that must run waits for its page outside its epoch (#1382): it gives up at a lap's end, its epoch scope unwound, waits
    /// there and runs again — for as long as pages are served — and completes once its page comes, the cycle advancing CheckpointLSN. Waiting inside its
    /// allocation, the checkpoint thread's pin was the oldest of a cache gone epoch-held.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("CK-14")]
    public void PastItsWindow_TheHookWaitsOutsideItsEpoch_AndCompletesOnceServed()
    {
        using var dbe = OpenEngineWithAColdHookPage();
        var ckpt = dbe.CheckpointManager;
        var lsnBefore = ckpt.CheckpointLsn;
        var restarts = ckpt.TotalHookRestarts;
        var saved = TimeoutOptions.Current;
        var window = CheckpointManager.HookDeferralWindow;
        var timeouts = saved.Clone();
        timeouts.PageCacheBackpressureTimeout = TimeSpan.FromMilliseconds(100);
        TimeoutOptions.Current = timeouts;
        var me = Environment.CurrentManagedThreadId;
        var held = Stopwatch.StartNew();
        var waitedWithItsPin = 0;
        dbe.MMF.ForceBackpressureForTest = () =>
        {
            if (Environment.CurrentManagedThreadId != me || held.ElapsedMilliseconds >= 300)
            {
                return false;
            }

            if (PagedMMF.NoBackpressureWaitDepth == 0)
            {
                waitedWithItsPin++;
            }

            dbe.MMF.NoteServedForTests();   // other threads' allocations, going through
            return true;
        };
        try
        {
            CheckpointManager.HookDeferralWindow = TimeSpan.Zero;
            ckpt.RunCheckpointCycle(dbe.WalManager.DurableLsn);
        }
        finally
        {
            dbe.MMF.ForceBackpressureForTest = null;
            TimeoutOptions.Current = saved;
            CheckpointManager.HookDeferralWindow = window;
        }

        Assert.That(held.ElapsedMilliseconds, Is.GreaterThanOrEqualTo(300), "premise: held past three timeouts");
        Assert.That(ckpt.TotalHookRestarts - restarts, Is.GreaterThan(0), "it gave its epoch up to wait");
        Assert.That(waitedWithItsPin, Is.Zero, "no round waited inside its allocation");
        Assert.That(ckpt.Health, Is.Not.EqualTo(DurabilityHealth.Degraded), "no timeout");
        Assert.That(ckpt.CheckpointLsn, Is.GreaterThan(lsnBefore), "the hook completed and the cycle advanced CheckpointLSN");
        Assert.That(dbe.MMF.CountUnevictablePages().Marked, Is.Zero, "the runs given up left no page under their marks");
    }
}
