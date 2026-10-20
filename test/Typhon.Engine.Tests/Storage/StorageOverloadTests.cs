using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// Phase 1 of the page-cache starvation policy (#1382). S1: a wait on back-pressure goes on past its timeout while the cache serves pages — slow, not
/// stuck — and gives up only after a whole timeout with no page served, or at the ceiling. S3: the storage health state, from Healthy to Starved, and
/// back once the pressure is over.
/// </summary>
[TestFixture]
[NonParallelizable] // TimeoutOptions.Current and the health tunables are process-wide
class StorageOverloadTests : TestBase<StorageOverloadTests>
{
    private static void SpinFor(TimeSpan span)
    {
        var sw = Stopwatch.StartNew();
        var spin = new SpinWait();
        while (sw.Elapsed < span)
        {
            spin.SpinOnce();
        }
    }

    [Test]
    [VerifiesRule("PS-22")]
    public void TheBudget_AllowsItsFirstTimeout_ThenOneMoreAtATimeWhilePagesAreServed_UpToTheCeiling()
    {
        var timeout = TimeSpan.FromMilliseconds(20);
        var ceiling = TimeSpan.FromSeconds(10);
        long served = 0;
        var budget = default(PagedMMF.OverloadBudget);
        budget.Start(served);
        Assert.That(budget.Allows(timeout, ceiling, served, out var extended), Is.True, "within its first timeout");
        Assert.That(extended, Is.False);

        SpinFor(timeout);
        served++;
        Assert.That(budget.Allows(timeout, ceiling, served, out extended), Is.True, "a page was served during the timeout: slow, not stuck");
        Assert.That(extended, Is.True, "an extension");
        Assert.That(budget.Allows(timeout, ceiling, served, out _), Is.True, "the new timeout runs");

        SpinFor(timeout);
        Assert.That(budget.Allows(timeout, ceiling, served, out _), Is.False, "a whole timeout without a page served: stuck");

        var capped = default(PagedMMF.OverloadBudget);
        capped.Start(served);
        SpinFor(timeout + timeout);
        served++;
        Assert.That(capped.Allows(timeout, TimeSpan.FromMilliseconds(1), served, out _), Is.False, "the ceiling (never below the timeout) ends it");

        var pastTimeout = default(PagedMMF.OverloadBudget);
        pastTimeout.Start(served);
        Assert.That(pastTimeout.AllowsPastTimeout(timeout, ceiling, served), Is.False, "a caller whose own timeout ran out gets no grace period");
        Assert.That(pastTimeout.AllowsPastTimeout(timeout, ceiling, served + 1), Is.True, "only progress extends it");
    }

    private DatabaseEngine OpenEngine(out EntityId id)
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<StatsRanked>();
        dbe.RegisterComponentFromAccessor<StatsTag>();
        dbe.InitializeArchetypes();
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<StatsArch>(StatsArch.Ranked.Set(new StatsRanked(7, 7)), StatsArch.Tag.Set(new StatsTag(7)));
            Assert.That(tx.Commit(), Is.True);
        }

        Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "premise: the pages are clean");
        return dbe;
    }

    /// <summary>
    /// An open held on back-pressure for three times its timeout while pages keep being served completes, with no exception: the work goes through,
    /// slowly. Without pages served it gives up after one timeout.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("PS-22")]
    public void AnOpenHeldOnBackpressure_WaitsPastItsTimeoutWhilePagesAreServed_AndGivesUpWhenNoneAre([Values] bool served)
    {
        using var dbe = OpenEngine(out var id);
        var saved = TimeoutOptions.Current;
        var timeouts = saved.Clone();
        timeouts.PageCacheBackpressureTimeout = TimeSpan.FromMilliseconds(100);
        TimeoutOptions.Current = timeouts;
        try
        {
            using var tx = dbe.CreateQuickTransaction();
            dbe.MMF.EvictEvictablePagesForTest();
            var me = Environment.CurrentManagedThreadId;
            var held = Stopwatch.StartNew();
            var extensions = dbe.MMF.OverloadWaitExtensions;
            dbe.MMF.ForceBackpressureForTest = () =>
            {
                if (held.ElapsedMilliseconds >= 350)
                {
                    return false;
                }

                // Unserved, every thread is held, not this one alone: any allocation counts as a page served (PS-22), the engine's own threads' too,
                // and on the macOS runner one of theirs inside the window extended the wait it was meant to end.
                if (Environment.CurrentManagedThreadId != me)
                {
                    return !served;
                }

                if (served)
                {
                    dbe.MMF.NoteServedForTests();   // other threads' allocations, going through
                }

                return true;
            };
            Exception thrown = null;
            try
            {
                Assert.That(tx.Open(id).TryRead<StatsRanked>(out _), Is.True);
            }
            catch (Exception e)
            {
                thrown = e;
            }
            finally
            {
                dbe.MMF.ForceBackpressureForTest = null;
            }

            if (served)
            {
                Assert.That(thrown, Is.Null, "the open went through");
                Assert.That(held.ElapsedMilliseconds, Is.GreaterThanOrEqualTo(300), "premise: held past three timeouts");
                Assert.That(dbe.MMF.OverloadWaitExtensions - extensions, Is.GreaterThan(0), "its wait was extended");
            }
            else
            {
                Assert.That(thrown, Is.InstanceOf<PageCacheBackpressureTimeoutException>(), "nothing served for a whole timeout: stuck");
                // Not "after one timeout": the engine's own threads allocate while the flag is set (the checkpoint's writes), which is progress too.
                Assert.That(held.ElapsedMilliseconds, Is.LessThan(350), "it gave up while the back-pressure lasted, not once it had lifted");
            }
        }
        finally
        {
            TimeoutOptions.Current = saved;
        }
    }

    /// <summary>
    /// The same on the allocation's own loop, outside an open: a page request held on back-pressure for three timeouts goes through while pages are
    /// served, and gives up while the back-pressure lasts when none are.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("PS-22")]
    public void APageRequestHeldOnBackpressure_WaitsPastItsTimeoutWhilePagesAreServed_AndGivesUpWhenNoneAre([Values] bool served)
    {
        using var dbe = OpenEngine(out _);
        var saved = TimeoutOptions.Current;
        var timeouts = saved.Clone();
        timeouts.PageCacheBackpressureTimeout = TimeSpan.FromMilliseconds(100);
        TimeoutOptions.Current = timeouts;
        try
        {
            var segment = dbe._archetypeStates[Archetype<StatsArch>.Metadata.ArchetypeId].ClusterState.ClusterSegment;
            dbe.MMF.EvictEvictablePagesForTest();
            var me = Environment.CurrentManagedThreadId;
            var held = Stopwatch.StartNew();
            var extensions = dbe.MMF.OverloadWaitExtensions;
            // The wait itself is the subject: staged with the slot reserve drawn empty, as a waiter holding its pin meets it once the
            // reserve has served the others (PS-24).
            dbe.MMF.SetSlotReserveTargetForTests(0);
            dbe.MMF.ForceBackpressureForTest = () =>
            {
                if (held.ElapsedMilliseconds >= 350)
                {
                    return false;
                }

                // Unserved, every thread is held, not this one alone: any allocation counts as a page served (PS-22), the engine's own threads' too,
                // and on the macOS runner one of theirs inside the window extended the wait it was meant to end.
                if (Environment.CurrentManagedThreadId != me)
                {
                    return !served;
                }

                if (served)
                {
                    dbe.MMF.NoteServedForTests();
                }

                return true;
            };
            Exception thrown = null;
            try
            {
                using var guard = EpochGuard.Enter(dbe.EpochManager);
                dbe.MMF.RequestPageEpoch(segment.Pages[0], guard.Epoch, out _);
            }
            catch (Exception e)
            {
                thrown = e;
            }
            finally
            {
                dbe.MMF.ForceBackpressureForTest = null;
            }

            if (served)
            {
                Assert.That(thrown, Is.Null, "the request went through");
                Assert.That(held.ElapsedMilliseconds, Is.GreaterThanOrEqualTo(300), "premise: held past three timeouts");
                Assert.That(dbe.MMF.OverloadWaitExtensions - extensions, Is.GreaterThan(0), "its wait was extended");
            }
            else
            {
                Assert.That(thrown, Is.InstanceOf<PageCacheBackpressureTimeoutException>(), "nothing served for a whole timeout: stuck");
                Assert.That(held.ElapsedMilliseconds, Is.LessThan(350), "it gave up while the back-pressure lasted, not once it had lifted");
            }
        }
        finally
        {
            TimeoutOptions.Current = saved;
        }
    }

    /// <summary>
    /// An engine that falls idle after a burst — no allocation comes to clear the pressure flag — steps back to Healthy from its periodic evaluations,
    /// and is never called Starved for serving nothing while nothing asks.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("PS-22")]
    public void AnEngineFallingIdleAfterABurst_StepsBackToHealthy_NotStarved()
    {
        using var dbe = OpenEngine(out _);
        var mmf = dbe.MMF;
        var interval = PagedMMF.HealthEvaluationInterval;
        var starvedAfter = PagedMMF.StarvedAfter;
        var calm = PagedMMF.CalmBeforeStepDown;
        PagedMMF.HealthEvaluationInterval = TimeSpan.Zero;
        PagedMMF.StarvedAfter = PagedMMF.PressureHold + PagedMMF.PressureHold;   // past the hold, as in production (1 s against 100 ms)
        PagedMMF.CalmBeforeStepDown = TimeSpan.FromMilliseconds(60);
        try
        {
            // Idle before the burst, for longer than StarvedAfter: nothing served then is not starvation, and must not count once pressure begins.
            var idle = Stopwatch.StartNew();
            while (idle.Elapsed < PagedMMF.StarvedAfter + PagedMMF.StarvedAfter)
            {
                mmf.EvaluateStorageHealth();
            }

            mmf.NoteBackpressureRoundForTests();
            Assert.That(dbe.StorageHealth, Is.EqualTo(StorageHealthState.Pressured), "a burst after idle: pressured, not starved at once");

            // Idle: no allocation, only the periodic evaluations (the checkpoint loop's wakes, the resource tree's reads).
            var sw = Stopwatch.StartNew();
            var mostSevere = StorageHealthState.Healthy;
            while (sw.ElapsedMilliseconds < 500 && (dbe.StorageHealth != StorageHealthState.Healthy || sw.ElapsedMilliseconds < 200))
            {
                mmf.EvaluateStorageHealth();
                mostSevere = dbe.StorageHealth > mostSevere ? dbe.StorageHealth : mostSevere;
            }

            Assert.That(mostSevere, Is.LessThan(StorageHealthState.Starved), "idle is not starved");
            Assert.That(dbe.StorageHealth, Is.EqualTo(StorageHealthState.Healthy), "stepped back once the pressure hold passed");
        }
        finally
        {
            PagedMMF.HealthEvaluationInterval = interval;
            PagedMMF.StarvedAfter = starvedAfter;
            PagedMMF.CalmBeforeStepDown = calm;
        }
    }

    /// <summary>
    /// The health state follows the back-pressure: Pressured at its first round, Overloaded once it lasts, Starved once nothing is served, and back to
    /// Healthy once it has been over for the calm period — each change raised, and counted.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("PS-22")]
    public void TheHealthState_FollowsTheBackpressure_AndStepsDownOnceItIsOver()
    {
        using var dbe = OpenEngine(out _);
        var mmf = dbe.MMF;
        var interval = PagedMMF.HealthEvaluationInterval;
        var overloadedAfter = PagedMMF.OverloadedAfter;
        var starvedAfter = PagedMMF.StarvedAfter;
        var calm = PagedMMF.CalmBeforeStepDown;
        var changes = new List<(StorageHealthState From, StorageHealthState To)>();
        EventHandler<StorageHealthChangedEventArgs> onChange = (sender, e) =>
        {
            Assert.That(sender, Is.SameAs(dbe));
            changes.Add((e.Previous, e.Current));
        };
        dbe.StorageHealthChanged += onChange;   // the public event, which the engine forwards from its cache
        PagedMMF.HealthEvaluationInterval = TimeSpan.Zero;
        PagedMMF.OverloadedAfter = TimeSpan.FromMilliseconds(60);
        PagedMMF.StarvedAfter = TimeSpan.FromMilliseconds(60);
        PagedMMF.CalmBeforeStepDown = TimeSpan.FromMilliseconds(60);
        try
        {
            Assert.That(dbe.StorageHealth, Is.EqualTo(StorageHealthState.Healthy), "premise");

            mmf.NoteBackpressureRoundForTests();
            Assert.That(dbe.StorageHealth, Is.EqualTo(StorageHealthState.Pressured), "the first round");

            // Rounds and pages served, for longer than OverloadedAfter.
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 120)
            {
                mmf.NoteServedForTests();
                mmf.NoteBackpressureRoundForTests();
            }

            Assert.That(dbe.StorageHealth, Is.EqualTo(StorageHealthState.Overloaded), "back-pressure that lasts");

            // Rounds and nothing served, for longer than StarvedAfter.
            sw.Restart();
            while (sw.ElapsedMilliseconds < 120)
            {
                mmf.NoteBackpressureRoundForTests();
            }

            Assert.That(dbe.StorageHealth, Is.EqualTo(StorageHealthState.Starved), "nothing served");

            // Over: allocations served and no round, for longer than the calm period.
            // The pressure flag clears PressureHold after the last round, then the calm period runs.
            sw.Restart();
            while (sw.ElapsedMilliseconds < 400 && dbe.StorageHealth != StorageHealthState.Healthy)
            {
                mmf.NoteServedForTests();
                mmf.EvaluateStorageHealth();
            }

            Assert.That(dbe.StorageHealth, Is.EqualTo(StorageHealthState.Healthy), "stepped down once calm");
            Assert.That(changes.GetRange(0, 3), Is.EqualTo(new[]
            {
                (StorageHealthState.Healthy, StorageHealthState.Pressured),
                (StorageHealthState.Pressured, StorageHealthState.Overloaded),
                (StorageHealthState.Overloaded, StorageHealthState.Starved),
            }), "escalated one state at a time");
            for (var i = 3; i < changes.Count; i++)
            {
                Assert.That(changes[i].To, Is.LessThan(changes[i].From), "the recovery only steps down");
            }

            Assert.That(changes[^1].To, Is.EqualTo(StorageHealthState.Healthy));
            Assert.That(mmf.HealthEntries(StorageHealthState.Starved), Is.EqualTo(1));
        }
        finally
        {
            dbe.StorageHealthChanged -= onChange;
            PagedMMF.HealthEvaluationInterval = interval;
            PagedMMF.OverloadedAfter = overloadedAfter;
            PagedMMF.StarvedAfter = starvedAfter;
            PagedMMF.CalmBeforeStepDown = calm;
        }
    }
}
