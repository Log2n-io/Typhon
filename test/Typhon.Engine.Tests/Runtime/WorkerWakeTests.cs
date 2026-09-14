using System;
using System.Diagnostics;
using System.Threading;
using NUnit.Framework;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// Every dispatch wakes every parked worker, each through its own event: no wake is lost to a worker's Reset or left to the 50 ms backstop (rule WK-01),
/// and a wake that is lost anyway is counted once (WK-02).
/// </summary>
/// <remarks>
/// <para>The wake tests raise the backstop to 30 s, so a worker the scheduler fails to wake stays parked instead of joining 50 ms late; the lost-wake
/// tests shorten it instead, to make it fire. Three run on
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

    /// <summary>Distinctive substring of WK-01's rejection messages, which its mutant must trip.</summary>
    private const string Wk01Marker = "WK-01 violated";

    private ResourceRegistry _registry;

    [SetUp]
    public void SetUp() => _registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "WorkerWake" });

    [TearDown]
    public void TearDown() => _registry?.Dispose();

    [Test]
    [VerifiesRule("WK-01")]
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

        Assert.That(stalls, Is.Zero, $"{Wk01Marker}: a dispatch in tick {stallTick} left a worker parked: the others waited in their chunks for it in vain");
        Assert.That(reached, Is.True, "precondition: the scheduler did not reach tick 50");
    }

    /// <summary>
    /// A worker held between finding the generation unchanged and parking, until the next dispatch has Set its event, then parks on an event that is
    /// already set. It joins that dispatch only if nothing between its check and its wait clears the event.
    /// </summary>
    [Test]
    [VerifiesRule("WK-01")]
    public void AWakeLandingAsAWorkerParks_IsNotLost() => WakeLandingAsAWorkerParks(swallowTheSet: false);

    /// <summary>
    /// The <see cref="RuleMutantAttribute"/> companion: a Set swallowed between the check and the wait. The held worker Resets its own event once the
    /// dispatch has Set it and sits the dispatch out, which the verifier must reject. From then on its chunk-mates wait 1 s for it instead of 3, since here
    /// the stall is the expected outcome.
    /// </summary>
    [Test]
    [RuleMutant("WK-01")]
    public void ASetSwallowedBeforeTheWait_IsCaughtByTheVerifier() =>
        RuleMutants.AssertDetects("WK-01", Wk01Marker, () => WakeLandingAsAWorkerParks(swallowTheSet: true));

    private void WakeLandingAsAWorkerParks(bool swallowTheSet)
    {
        var stalls = 0;
        var stallTick = -1L;
        var caughtWorker = -1;
        var caughtAtTick = long.MaxValue;
        var released = 0;
        var shuttingDown = 0;
        using var scheduler = BuildAllHands(tick =>
        {
            Interlocked.Increment(ref stalls);
            Interlocked.CompareExchange(ref stallTick, tick, -1L);
        }, () => swallowTheSet && Volatile.Read(ref released) == 1 ? TimeSpan.FromSeconds(1) : Wait);
        scheduler.BetweenTickWaitProbe = workerId =>
        {
            // Only a worker whose event is clear as it parks: one still set (by a dispatch the worker ran without parking) would end the hold at once and
            // test nothing.
            if (scheduler.CurrentTickNumber < 3 || scheduler.WorkerWakeEvent(workerId).IsSet
                || Interlocked.CompareExchange(ref caughtWorker, workerId, -1) != -1)
            {
                return;
            }

            // Released by a dispatch's Set, not by Shutdown's: a Set swallowed during shutdown would turn a harmless stall into the mutant's detection.
            Volatile.Write(ref caughtAtTick, scheduler.CurrentTickNumber);
            if (SpinFor(() => scheduler.WorkerWakeEvent(workerId).IsSet, Wait) && Volatile.Read(ref shuttingDown) == 0)
            {
                Volatile.Write(ref released, 1);
                if (swallowTheSet)
                {
                    scheduler.WorkerWakeEvent(workerId).Reset();
                }
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
            Volatile.Write(ref shuttingDown, 1);
            scheduler.Shutdown();
        }

        // With the Reset after this probe instead of before it, the event is still set from the wake at every probe, so no worker is ever caught here.
        Assert.That(caughtWorker, Is.Not.EqualTo(-1), $"{Wk01Marker}: no worker reached its wait with its event clear: the Reset no longer precedes the check");
        Assert.That(released, Is.EqualTo(1), "precondition: no dispatch set the caught worker's event");
        Assert.That(stalls, Is.Zero,
            $"{Wk01Marker}: a dispatch in tick {stallTick} left a worker parked (worker {caughtWorker} was caught in tick {caughtAtTick})");
        Assert.That(reached, Is.True, "precondition: the scheduler did not complete three ticks after the catch");
    }

    /// <summary>
    /// A Set whose generation the worker has already seen — one that lands after the worker left the wait loop without parking — is consumed, not spun on:
    /// the worker parks again at once instead of looping on an event that stays set until the next dispatch.
    /// </summary>
    [Test]
    [VerifiesRule("WK-01")]
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
            $"{Wk01Marker}: the worker went round the wait {spins} times in {ticks} ticks on a Set it had already seen, instead of parking again");
    }

    /// <summary>
    /// A worker thread that first runs after the first dispatch has bumped the generation still takes part in that dispatch: it starts from the generation
    /// <c>Start</c> found, not from the one it reads once it gets going.
    /// </summary>
    [Test]
    [VerifiesRule("WK-01")]
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
        Assert.That(stalls, Is.Zero, $"{Wk01Marker}: a worker that started late sat out the dispatch in tick {stallTick}");
        Assert.That(reached, Is.True, "precondition: the scheduler did not reach tick 3");
    }

    /// <summary>
    /// A wake the dispatcher sent but the worker never got — its Set taken away before it parked — is counted once, in total and in a tick's telemetry, and the
    /// worker is resumed by its backstop.
    /// </summary>
    [Test]
    [VerifiesRule("WK-02")]
    public void ALostWake_IsCountedOnce()
    {
        var victim = -1;
        var stolenAtTick = -1L;
        using var scheduler = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = Workers, BaseTickRate = 50, TelemetryRingCapacity = 64 })
            .PublicTrack.DeclareDag("Idle")
            .CallbackSystem("A", _ => { })
            .Build(_registry.Runtime);

        scheduler.BetweenTickWaitBackstop = TimeSpan.FromMilliseconds(5);
        scheduler.BetweenTickWaitProbe = workerId =>
        {
            // Only a worker whose event is clear as it parks: a Set still there from a dispatch it ran without parking is stale, and taking it loses nothing.
            if (scheduler.CurrentTickNumber < 3 || scheduler.WorkerWakeEvent(workerId).IsSet
                || Interlocked.CompareExchange(ref victim, workerId, -1) != -1)
            {
                return;
            }

            // Held until the next dispatch has Set this worker's event, which is then taken away at once: the wake is lost. A tight spin, because
            // SpinWait.SpinUntil sleeps up to a timer slice (15.6 ms on Windows) between checks — long enough for the worker's 5 ms wait to reach the
            // following dispatch, whose Set would then wake it as usual.
            var wake = scheduler.WorkerWakeEvent(workerId);
            if (SpinFor(() => wake.IsSet, Wait))
            {
                wake.Reset();
                Volatile.Write(ref stolenAtTick, scheduler.CurrentTickNumber);
            }
        };

        // The victim's next Set is held back until the backstop has caught the lost wake: on Windows a 5 ms wait can end up to a timer slice (15.6 ms)
        // late, past the next dispatch, whose Set would then wake the worker and leave nothing to count.
        scheduler.WakeProbe = workerId =>
        {
            if (workerId == Volatile.Read(ref victim) && Volatile.Read(ref stolenAtTick) >= 0 && scheduler.LostWakeCount == 0)
            {
                SpinFor(() => scheduler.LostWakeCount > 0, Wait);
            }
        };

        bool reached;
        try
        {
            scheduler.Start();
            reached = SpinWait.SpinUntil(() =>
            {
                var at = Volatile.Read(ref stolenAtTick);
                return at >= 0 && scheduler.CurrentTickNumber >= at + 3;
            }, TimeSpan.FromSeconds(10));
        }
        finally
        {
            scheduler.Shutdown();
        }

        var ring = scheduler.Telemetry;
        var perTick = 0L;
        for (var t = ring.OldestAvailableTick; t <= ring.NewestTick; t++)
        {
            perTick += ring.GetTick(t).LostWakes;
        }

        Assert.That(reached, Is.True, "precondition: no worker's wake was taken away, or the scheduler stalled after it");
        Assert.That(scheduler.LostWakeCount, Is.EqualTo(1), "the lost wake was not counted exactly once");
        Assert.That(perTick, Is.EqualTo(1), "the tick telemetry does not carry the lost wake exactly once");
    }

    /// <summary>
    /// A backstop that fires between a dispatch's generation bump and the worker's own Set is not a lost wake: that Set is on its way. The dispatcher is held
    /// just before the last worker's Set until that worker, resumed by its backstop past the bump, has come back to its wait; nothing may be counted.
    /// </summary>
    [Test]
    [VerifiesRule("WK-02")]
    public void ABackstopFiringBeforeItsSetArrives_IsNotALostWake()
    {
        const int victim = Workers - 1; // the dispatcher Sets it last
        var holding = 0;
        var parkedBeforeBump = 0;
        var cameBack = 0;
        var held = 0;
        using var scheduler = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = Workers, BaseTickRate = 50 })
            .PublicTrack.DeclareDag("Idle")
            .CallbackSystem("A", _ => { })
            .Build(_registry.Runtime);
        scheduler.BetweenTickWaitBackstop = TimeSpan.FromMilliseconds(2);
        scheduler.BetweenTickWaitProbe = workerId =>
        {
            if (workerId != victim || scheduler.CurrentTickNumber < 3)
            {
                return;
            }

            if (Volatile.Read(ref holding) == 0)
            {
                // About to park while nothing is held: the next bump lands while this worker waits.
                Volatile.Write(ref parkedBeforeBump, 1);
            }
            else
            {
                // Back at its wait while the dispatcher still holds its Set: its backstop resumed it past the bump.
                Volatile.Write(ref cameBack, 1);
            }
        };
        scheduler.WakeProbe = workerId =>
        {
            if (workerId == victim && Volatile.Read(ref parkedBeforeBump) == 1 && Interlocked.CompareExchange(ref held, 1, 0) == 0)
            {
                Volatile.Write(ref holding, 1);
                SpinWait.SpinUntil(() => Volatile.Read(ref cameBack) == 1, Wait);
                Volatile.Write(ref holding, 0);
            }
        };

        bool reached;
        try
        {
            scheduler.Start();
            reached = SpinWait.SpinUntil(() => Volatile.Read(ref cameBack) == 1, TimeSpan.FromSeconds(10));
            var tick = scheduler.CurrentTickNumber;
            reached &= SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= tick + 2, TimeSpan.FromSeconds(10));
        }
        finally
        {
            scheduler.Shutdown();
        }

        Assert.That(held, Is.EqualTo(1), "precondition: the dispatcher was never held before the last worker's Set");
        Assert.That(reached, Is.True, "precondition: the held worker's backstop never resumed it past the bump, or the scheduler stalled after");
        Assert.That(scheduler.LostWakeCount, Is.Zero, "a backstop that fired before its worker's Set arrived was counted as a lost wake");
    }

    /// <summary>
    /// A Set landing after the backstop fired, before the lost-wake check, is not a lost wake. The worker is held between its timed-out wait and the check
    /// until a later dispatch has Set its event and its tick has ended, so the check finds a completed round newer than the worker's, with its event set.
    /// </summary>
    [Test]
    [VerifiesRule("WK-02")]
    public void ASetLandingAfterTheBackstop_IsNotALostWake()
    {
        var victim = -1;
        var observed = 0;
        using var scheduler = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = Workers, BaseTickRate = 50 })
            .PublicTrack.DeclareDag("Idle")
            .CallbackSystem("A", _ => { })
            .Build(_registry.Runtime);
        scheduler.BetweenTickWaitBackstop = TimeSpan.FromMilliseconds(2);
        scheduler.BackstopProbe = workerId =>
        {
            if (scheduler.CurrentTickNumber < 3 || Interlocked.CompareExchange(ref victim, workerId, -1) != -1)
            {
                return;
            }

            var wake = scheduler.WorkerWakeEvent(workerId);
            if (SpinFor(() => wake.IsSet, Wait))
            {
                var tick = scheduler.CurrentTickNumber;
                if (SpinFor(() => scheduler.CurrentTickNumber > tick, Wait))
                {
                    Volatile.Write(ref observed, 1);
                }
            }
        };

        bool reached;
        try
        {
            scheduler.Start();
            reached = SpinWait.SpinUntil(() => Volatile.Read(ref observed) == 1, TimeSpan.FromSeconds(10));
            var tick = scheduler.CurrentTickNumber;
            reached &= SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= tick + 2, TimeSpan.FromSeconds(10));
        }
        finally
        {
            scheduler.Shutdown();
        }

        Assert.That(reached, Is.True, "precondition: no dispatch Set the held worker's event and completed, or the scheduler stalled after");
        Assert.That(scheduler.LostWakeCount, Is.Zero, "a Set that landed after the backstop fired was counted as a lost wake");
    }

    /// <summary>Shutdown wakes the parked workers itself; with the backstop out of reach, a worker it missed would hold <c>JoinWorkers</c> for 5 s.</summary>
    /// <remarks>Sensitive: the assertion is a wall-clock bound, so it belongs in the gate's serial pass.</remarks>
    [Test]
    [Category("Sensitive")]
    [VerifiesRule("WK-01")]
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
    /// The dispatcher bumps the generation before it Sets anyone, so a worker its Set wakes finds the dispatch instead of parking again. One dispatch a
    /// tick, so the workers have long been parked when it comes; the dispatcher is held just before the last worker's Set. Worker 0, Set first, must spend
    /// the hold in its chunk, whose barrier waits for the last worker, and never come back to its wait.
    /// </summary>
    [Test]
    [VerifiesRule("WK-01")]
    public void AWokenWorker_FindsTheDispatch_BeforeTheLastSet()
    {
        var stalls = 0;
        var holding = 0;
        var held = 0;
        var heldAtTick = long.MaxValue;
        var parkedAgain = 0;
        var back = new int[Workers];
        using var scheduler = BuildAllHands(_ => Interlocked.Increment(ref stalls), twoTracks: false);
        scheduler.BetweenTickWaitProbe = workerId =>
        {
            // Back only on a clear event: one parking on a stale Set would leave its Wait at once, and could take the held round's generation
            // without its own Set.
            if (!scheduler.WorkerWakeEvent(workerId).IsSet)
            {
                Volatile.Write(ref back[workerId], 1);
            }

            if (workerId == 0 && Volatile.Read(ref holding) == 1)
            {
                Volatile.Write(ref parkedAgain, 1);
            }
        };
        scheduler.WakeProbe = workerId =>
        {
            if (workerId == 0)
            {
                // Armed only on a round every worker has come back to its wait for. One still on its way back from the last round could take this
                // round's generation without its Set, finish the dispatch during the hold, and worker 0 would then return to its wait legitimately.
                // From here on, a worker that finds the generation unchanged once woken comes straight back to its wait.
                var allBack = true;
                for (var w = 0; w < Workers; w++)
                {
                    allBack &= Interlocked.Exchange(ref back[w], 0) == 1;
                }

                if (allBack && scheduler.CurrentTickNumber >= 3 && Volatile.Read(ref held) == 0 && !scheduler.IsShutdownRequested)
                {
                    Volatile.Write(ref holding, 1);
                }
            }
            else if (workerId == Workers - 1 && Volatile.Read(ref holding) == 1 && Volatile.Read(ref held) == 0)
            {
                Volatile.Write(ref heldAtTick, scheduler.CurrentTickNumber);
                SpinFor(() => Volatile.Read(ref parkedAgain) == 1, TimeSpan.FromMilliseconds(200));
                Volatile.Write(ref holding, 0);
                Volatile.Write(ref held, 1);
            }
        };

        bool reached;
        try
        {
            scheduler.Start();
            reached = SpinWait.SpinUntil(() => Volatile.Read(ref held) == 1 && scheduler.CurrentTickNumber >= Volatile.Read(ref heldAtTick) + 2,
                TimeSpan.FromSeconds(10));
        }
        finally
        {
            scheduler.Shutdown();
        }

        Assert.That(held, Is.EqualTo(1), "precondition: the dispatcher was never held before the last worker's Set");
        Assert.That(parkedAgain, Is.Zero, $"{Wk01Marker}: worker 0, Set before the generation moved, found no dispatch and parked again");
        Assert.That(stalls, Is.Zero, $"{Wk01Marker}: a dispatch left a worker parked");
        Assert.That(reached, Is.True, "precondition: the scheduler did not complete two ticks after the hold");
    }

    /// <summary>
    /// P on the Public track and, with <paramref name="twoTracks"/>, Q on a second track, so every tick dispatches twice back to back, as a runtime tick
    /// does (the Public track, then the fence). Each runs one chunk per worker, and each chunk waits until every worker is inside one: a dispatch
    /// completes only if it woke the whole pool.
    /// </summary>
    private DagScheduler BuildAllHands(Action<long> onStall, Func<TimeSpan> chunkWait = null, bool twoTracks = true)
    {
        var waitForAll = chunkWait ?? (() => Wait);
        var arrived = 0;
        var schedule = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = Workers, BaseTickRate = 1000 });
        var publicDag = schedule.PublicTrack.DeclareDag("AllHands").QuerySystem("P", _ => { }, input: () => null, parallel: true);
        var scheduler = twoTracks
            ? schedule.DeclareTrack("Second").DeclareDag("AllHandsAgain").QuerySystem("Q", _ => { }, input: () => null, parallel: true)
                .Build(_registry.Runtime)
            : publicDag.Build(_registry.Runtime);
        scheduler.BetweenTickWaitBackstop = Backstop;
        scheduler.ParallelQueryPrepareCallback = _ =>
        {
            Volatile.Write(ref arrived, 0);
            return Workers;
        };
        scheduler.ParallelQueryChunkCallback = (_, _, _, _) =>
        {
            // A dispatch still in flight when the test shuts the scheduler down loses its workers to the shutdown exit: not a stall, and not worth waiting on.
            // SpinFor, not SpinWait.SpinUntil: its back-off sleeps 1-15 ms at a time, once per dispatch, which put this fixture over a second.
            Interlocked.Increment(ref arrived);
            SpinFor(() => Volatile.Read(ref arrived) >= Workers || scheduler.IsShutdownRequested, waitForAll());
            if (Volatile.Read(ref arrived) < Workers && !scheduler.IsShutdownRequested)
            {
                onStall(scheduler.CurrentTickNumber);
            }
        };
        scheduler.ParallelQueryCleanupCallback = _ => false;
        return scheduler;
    }

    /// <summary>Spins until <paramref name="condition"/> holds or <paramref name="timeout"/> passes, never sleeping; false on timeout.</summary>
    private static bool SpinFor(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (!condition())
        {
            if (Stopwatch.GetTimestamp() >= deadline)
            {
                return false;
            }

            Thread.SpinWait(20);
        }

        return true;
    }
}
