using System;
using System.Diagnostics;
using System.Threading;
using NUnit.Framework;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// Every dispatch wakes every parked worker, each through its own event: no wake is lost to a worker's Reset or left to the 50 ms backstop.
/// </summary>
/// <remarks>
/// <para>Each test raises the backstop to 30 s, so a worker the scheduler fails to wake stays parked instead of joining 50 ms late. Three run on
/// <see cref="BuildAllHands"/>, whose chunks each wait until every worker of the pool is inside one: a dispatch that leaves a worker parked makes its
/// chunk-mates' waits time out.</para>
/// <para>Every wait is bounded and every probe returns on its own, so a regression fails the test instead of wedging the suite.</para>
/// </remarks>
[TestFixture]
public class WorkerWakeTests
{
    private const int Workers = 4;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Backstop = TimeSpan.FromSeconds(30);

    private ResourceRegistry _registry;

    [SetUp]
    public void SetUp() => _registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "WorkerWake" });

    [TearDown]
    public void TearDown() => _registry?.Dispose();

    [Test]
    public void EveryDispatch_WakesEveryWorker()
    {
        var stalls = 0;
        var stallTick = -1L;
        using var scheduler = BuildAllHands(tick =>
        {
            Interlocked.Increment(ref stalls);
            Interlocked.CompareExchange(ref stallTick, tick, -1L);
        });
        bool reached;
        try
        {
            scheduler.Start();
            reached = SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= 50 || Volatile.Read(ref stalls) != 0, TimeSpan.FromSeconds(10));
        }
        finally
        {
            scheduler.Shutdown();
        }

        Assert.That(stalls, Is.Zero, $"a dispatch in tick {stallTick} left a worker parked: the others waited in their chunks for it in vain");
        Assert.That(reached, Is.True, "precondition: the scheduler did not reach tick 50");
    }

    /// <summary>
    /// A worker held between finding the generation unchanged and parking, until the next dispatch has Set its event, then parks on an event that is
    /// already set. It joins that dispatch only if nothing between its check and its wait clears the event.
    /// </summary>
    [Test]
    public void AWakeLandingAsAWorkerParks_IsNotLost()
    {
        var stalls = 0;
        var stallTick = -1L;
        var caughtWorker = -1;
        var caughtAtTick = long.MaxValue;
        var released = 0;
        using var scheduler = BuildAllHands(tick =>
        {
            Interlocked.Increment(ref stalls);
            Interlocked.CompareExchange(ref stallTick, tick, -1L);
        });
        scheduler.BetweenTickWaitProbe = workerId =>
        {
            // Only a worker whose event is clear as it parks: one still set (by a dispatch the worker ran without parking) would end the hold at once and
            // test nothing.
            if (scheduler.CurrentTickNumber < 3 || scheduler.WorkerWakeEvent(workerId).IsSet
                || Interlocked.CompareExchange(ref caughtWorker, workerId, -1) != -1)
            {
                return;
            }

            Volatile.Write(ref caughtAtTick, scheduler.CurrentTickNumber);
            if (SpinWait.SpinUntil(() => scheduler.WorkerWakeEvent(workerId).IsSet, Wait))
            {
                Volatile.Write(ref released, 1);
            }
        };

        bool reached;
        try
        {
            scheduler.Start();
            reached = SpinWait.SpinUntil(() => Volatile.Read(ref stalls) != 0
                || (Volatile.Read(ref released) == 1 && scheduler.CurrentTickNumber >= Volatile.Read(ref caughtAtTick) + 3), TimeSpan.FromSeconds(10));
        }
        finally
        {
            scheduler.Shutdown();
        }

        // A worker that never reaches its wait with the event clear fails here too: that is what a Reset moved between this probe and the wait does.
        Assert.That(caughtWorker, Is.Not.EqualTo(-1), "no worker reached its wait with its event clear");
        Assert.That(released, Is.EqualTo(1), "precondition: no dispatch set the caught worker's event");
        Assert.That(stalls, Is.Zero, $"a dispatch in tick {stallTick} left a worker parked (worker {caughtWorker} was caught in tick {caughtAtTick})");
        Assert.That(reached, Is.True, "precondition: the scheduler did not complete three ticks after the catch");
    }

    /// <summary>
    /// A Set whose generation the worker has already seen — one that lands after the worker left the wait loop without parking — is consumed, not spun on:
    /// the worker parks again at once instead of looping on an event that stays set until the next dispatch.
    /// </summary>
    [Test]
    public void AWakeAlreadySeen_IsConsumedNotSpunOn()
    {
        var calls = new int[Workers];
        var staleWorker = -1;
        var callsAtStale = 0;
        var staleAtTick = -1L;
        using var scheduler = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = Workers, BaseTickRate = 100 })
            .PublicTrack.DeclareDag("Idle")
            .CallbackSystem("A", _ => { })
            .Build(_registry.Runtime);
        scheduler.BetweenTickWaitBackstop = Backstop;
        scheduler.BetweenTickWaitProbe = workerId =>
        {
            var n = Interlocked.Increment(ref calls[workerId]);
            if (scheduler.CurrentTickNumber >= 3 && Interlocked.CompareExchange(ref staleWorker, workerId, -1) == -1)
            {
                // The worker has just found the generation unchanged, and its event is set anyway.
                Volatile.Write(ref callsAtStale, n);
                Volatile.Write(ref staleAtTick, scheduler.CurrentTickNumber);
                scheduler.WorkerWakeEvent(workerId).Set();
            }
        };

        bool reached;
        try
        {
            scheduler.Start();
            reached = SpinWait.SpinUntil(() =>
            {
                var at = Volatile.Read(ref staleAtTick);
                return at >= 0 && scheduler.CurrentTickNumber >= at + 3;
            }, TimeSpan.FromSeconds(10));
        }
        finally
        {
            scheduler.Shutdown();
        }

        Assert.That(staleWorker, Is.Not.EqualTo(-1), "precondition: no worker reached the wait after tick 3");
        Assert.That(reached, Is.True, "precondition: the scheduler did not complete three ticks after the stale Set");

        // Counted in ticks, not time: the worker parks once per dispatch (one per tick here), plus the round the stale Set costs, and a tick may still
        // complete after Shutdown returns. A spin is tens of thousands of rounds.
        var ticks = scheduler.CurrentTickNumber - Volatile.Read(ref staleAtTick);
        var spins = Volatile.Read(ref calls[staleWorker]) - callsAtStale;
        Assert.That(spins, Is.LessThanOrEqualTo(ticks + 3),
            $"the worker went round the wait {spins} times in {ticks} ticks on a Set it had already seen, instead of parking again");
    }

    /// <summary>
    /// A worker thread that first runs after the first dispatch has bumped the generation still takes part in that dispatch: it starts from the generation
    /// <c>Start</c> found, not from the one it reads once it gets going.
    /// </summary>
    [Test]
    public void AWorkerThatStartsLate_JoinsTheFirstDispatch()
    {
        var stalls = 0;
        var stallTick = -1L;
        var held = 0;
        using var scheduler = BuildAllHands(tick =>
        {
            Interlocked.Increment(ref stalls);
            Interlocked.CompareExchange(ref stallTick, tick, -1L);
        });
        scheduler.WorkerStartProbe = workerId =>
        {
            // Worker 0 gets going only once the first dispatch has bumped the generation and set its event.
            if (workerId == 0)
            {
                Volatile.Write(ref held, SpinWait.SpinUntil(() => scheduler.WorkerWakeEvent(0).IsSet, Wait) ? 1 : -1);
            }
        };

        bool reached;
        try
        {
            scheduler.Start();
            reached = SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= 3 || Volatile.Read(ref stalls) != 0, TimeSpan.FromSeconds(10));
        }
        finally
        {
            scheduler.Shutdown();
        }

        Assert.That(held, Is.EqualTo(1), "precondition: worker 0 was not held until the first dispatch had woken it");
        Assert.That(stalls, Is.Zero, $"a worker that started late sat out the dispatch in tick {stallTick}");
        Assert.That(reached, Is.True, "precondition: the scheduler did not reach tick 3");
    }

    /// <summary>Shutdown wakes the parked workers itself; with the backstop out of reach, a worker it missed would hold <c>JoinWorkers</c> for 5 s.</summary>
    /// <remarks>Sensitive: the assertion is a wall-clock bound, so it belongs in the gate's serial pass.</remarks>
    [Test]
    [Category("Sensitive")]
    public void Shutdown_WakesEveryParkedWorker()
    {
        using var scheduler = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = Workers, BaseTickRate = 100 })
            .PublicTrack.DeclareDag("Idle")
            .CallbackSystem("A", _ => { })
            .Build(_registry.Runtime);
        scheduler.BetweenTickWaitBackstop = Backstop;
        scheduler.Start();
        var reached = SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= 3, TimeSpan.FromSeconds(10));

        var clock = Stopwatch.StartNew();
        scheduler.Shutdown();
        clock.Stop();

        Assert.That(reached, Is.True, "precondition: the scheduler did not reach tick 3");
        Assert.That(clock.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2)), "Shutdown waited for a worker it had not woken");
    }

    /// <summary>
    /// P on the Public track and Q on a second track, so every tick dispatches twice back to back, as a runtime tick does (the Public track, then the
    /// fence). Each runs one chunk per worker, and each chunk waits until every worker is inside one: a dispatch completes only if it woke the whole pool.
    /// </summary>
    private DagScheduler BuildAllHands(Action<long> onStall)
    {
        var arrived = 0;
        var schedule = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = Workers, BaseTickRate = 1000 });
        schedule.PublicTrack.DeclareDag("AllHands").QuerySystem("P", _ => { }, input: () => null, parallel: true);
        var scheduler = schedule.DeclareTrack("Second").DeclareDag("AllHandsAgain").QuerySystem("Q", _ => { }, input: () => null, parallel: true)
            .Build(_registry.Runtime);
        scheduler.BetweenTickWaitBackstop = Backstop;
        scheduler.ParallelQueryPrepareCallback = _ =>
        {
            Volatile.Write(ref arrived, 0);
            return Workers;
        };
        scheduler.ParallelQueryChunkCallback = (_, _, _, _) =>
        {
            // A dispatch still in flight when the test shuts the scheduler down loses its workers to the shutdown exit: not a stall, and not worth waiting on.
            // A plain spin, not SpinWait.SpinUntil: its back-off sleeps 1-15 ms at a time, once per dispatch, which put this fixture over a second.
            Interlocked.Increment(ref arrived);
            var deadline = Stopwatch.GetTimestamp() + (long)(Wait.TotalSeconds * Stopwatch.Frequency);
            while (Volatile.Read(ref arrived) < Workers && !scheduler.IsShutdownRequested && Stopwatch.GetTimestamp() < deadline)
            {
                Thread.SpinWait(20);
            }

            if (Volatile.Read(ref arrived) < Workers && !scheduler.IsShutdownRequested)
            {
                onStall(scheduler.CurrentTickNumber);
            }
        };
        scheduler.ParallelQueryCleanupCallback = _ => false;
        return scheduler;
    }
}
