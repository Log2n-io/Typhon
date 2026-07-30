using NUnit.Framework;
using System;
using System.Threading;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// Acceptance tests for the strict tick-abort policy (issue #567). These drive <see cref="DagScheduler"/> directly —
/// the cancellation lives entirely in dispatch, so no <see cref="DatabaseEngine"/> is needed and the tests stay fast.
/// </summary>
[TestFixture]
class StrictTickAbortTests
{
    private ResourceRegistry _registry;

    [SetUp]
    public void SetUp()
    {
        _registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "StrictAbortTest" });
    }

    [TearDown]
    public void TearDown()
    {
        _registry?.Dispose();
    }

    private static RuntimeOptions Strict(int workerCount) => new()
    {
        WorkerCount = workerCount,
        BaseTickRate = 1000,
        SystemExceptionPolicy = SystemExceptionPolicy.AbortTickAndStop,
    };

    [Test]
    public void Default_Policy_Is_Isolate()
    {
        // AC1 — the feature is strictly opt-in. The five ExceptionHandlingTests cover the behaviour itself; this pins
        // the default so a change to it cannot pass silently.
        Assert.That(new RuntimeOptions().SystemExceptionPolicy, Is.EqualTo(SystemExceptionPolicy.Isolate));
    }

    [Test]
    public void AbortTickAndStop_IndependentSystem_DoesNotRun()
    {
        // AC2 — under Isolate this is exactly SystemException_WorkerSurvives_TickContinues, where "After" DOES run.
        // Single worker: topological order guarantees Thrower is dispatched before After, with no race.
        var afterCount = 0;

        var dag = RuntimeSchedule.Create(Strict(1)).PublicTrack.DeclareDag("Test");
        dag
            .CallbackSystem("Thrower", _ => throw new InvalidOperationException("boom"))
            .CallbackSystem("After", _ => Interlocked.Increment(ref afterCount));

        using var scheduler = dag.Build(_registry.Runtime);
        scheduler.Start();
        SpinWait.SpinUntil(() => scheduler.IsTickAborted, TimeSpan.FromSeconds(2));
        scheduler.Shutdown();

        Assert.That(scheduler.IsTickAborted, Is.True, "the throw should have latched the terminal abort");
        Assert.That(afterCount, Is.Zero, "an independent system that had not started must not run after the abort");
    }

    [Test]
    public void AbortTickAndStop_TickStillCompletes_NoWedge()
    {
        // AC4 — the regression this whole design is shaped around. Cancellation must be dispatch-and-drain: if the
        // abort simply stopped handing out work, _systemsRemaining would never reach 0 and the TickDriver would spin
        // forever. Telemetry is only recorded AFTER the tick body drains, so a recorded tick proves it completed.
        var dag = RuntimeSchedule.Create(Strict(4)).PublicTrack.DeclareDag("Test");
        dag
            .CallbackSystem("Thrower", _ => throw new InvalidOperationException("boom"))
            .CallbackSystem("A", _ => { })
            .CallbackSystem("B", _ => { }, after: "A")
            .CallbackSystem("C", _ => { }, after: "B");

        using var scheduler = dag.Build(_registry.Runtime);
        scheduler.Start();
        var aborted = SpinWait.SpinUntil(() => scheduler.IsTickAborted, TimeSpan.FromSeconds(2));
        var completed = SpinWait.SpinUntil(() => scheduler.Telemetry.TotalTicksRecorded > 0, TimeSpan.FromSeconds(2));
        scheduler.Shutdown();

        Assert.That(aborted, Is.True, "abort should latch");
        Assert.That(completed, Is.True, "the aborted tick must still run to completion — a wedged tick records no telemetry");
    }

    [Test]
    public void AbortTickAndStop_ReadyButUnclaimedRoot_DoesNotRun()
    {
        // AC13 — the claim-time gate (correction C1). Gating only the successor path would let this pass wrongly, so
        // the DAG is built to make the root demonstrably unclaimed at abort time:
        //   2 workers, 3 roots. FindReadySystem scans in index order, so Blocker(0) and Thrower(1) take both workers
        //   and Victim(2) cannot be claimed until one frees up — which only happens after we release Blocker, well
        //   after the abort has latched.
        var blocker = new ManualResetEventSlim(false);
        var throwerReached = new ManualResetEventSlim(false);
        var victimCount = 0;

        var dag = RuntimeSchedule.Create(Strict(2)).PublicTrack.DeclareDag("Test");
        dag
            .CallbackSystem("Blocker", _ => blocker.Wait(TimeSpan.FromSeconds(5)))
            .CallbackSystem("Thrower", _ =>
            {
                throwerReached.Set();
                throw new InvalidOperationException("boom");
            })
            .CallbackSystem("Victim", _ => Interlocked.Increment(ref victimCount));

        using var scheduler = dag.Build(_registry.Runtime);
        scheduler.Start();

        throwerReached.Wait(TimeSpan.FromSeconds(2));
        var aborted = SpinWait.SpinUntil(() => scheduler.IsTickAborted, TimeSpan.FromSeconds(2));
        blocker.Set();                                  // free a worker — Victim becomes claimable only now
        SpinWait.SpinUntil(() => scheduler.Telemetry.TotalTicksRecorded > 0, TimeSpan.FromSeconds(2));
        scheduler.Shutdown();

        Assert.That(aborted, Is.True, "abort should latch before the blocker is released");
        Assert.That(victimCount, Is.Zero, "a root that was ready but unclaimed when the tick aborted must not run");
    }

    [Test]
    public void AbortTickAndStop_RunningSystem_FinishesNormally()
    {
        // AC3 — nothing is interrupted mid-body. The abort fires while "LongRunner" is inside its callback; it must
        // still reach its own last statement.
        var throwerReached = new ManualResetEventSlim(false);
        var longRunnerFinished = false;

        var dag = RuntimeSchedule.Create(Strict(2)).PublicTrack.DeclareDag("Test");
        dag
            .CallbackSystem("LongRunner", _ =>
            {
                throwerReached.Wait(TimeSpan.FromSeconds(2));
                Thread.Yield();
                longRunnerFinished = true;              // must be reached despite the abort latching mid-body
            })
            .CallbackSystem("Thrower", _ =>
            {
                throwerReached.Set();
                throw new InvalidOperationException("boom");
            });

        using var scheduler = dag.Build(_registry.Runtime);
        scheduler.Start();
        SpinWait.SpinUntil(() => scheduler.Telemetry.TotalTicksRecorded > 0, TimeSpan.FromSeconds(3));
        scheduler.Shutdown();

        Assert.That(longRunnerFinished, Is.True, "a system already running when the tick aborts must finish its body");
    }

    [Test]
    public void AbortTickAndStop_IsTerminal_NoFurtherTicks()
    {
        // AC10 — after an abort the runtime must not silently resume on a simulation that only partly ran.
        var runsAfterAbort = 0;

        var dag = RuntimeSchedule.Create(Strict(1)).PublicTrack.DeclareDag("Test");
        dag.CallbackSystem("Thrower", _ =>
        {
            if (Volatile.Read(ref runsAfterAbort) >= 0)
            {
                Interlocked.Increment(ref runsAfterAbort);
            }
            throw new InvalidOperationException("boom");
        });

        using var scheduler = dag.Build(_registry.Runtime);
        scheduler.Start();
        SpinWait.SpinUntil(() => scheduler.IsTickAborted, TimeSpan.FromSeconds(2));

        var runsAtAbort = Volatile.Read(ref runsAfterAbort);
        // BaseTickRate 1000 — without the terminal latch this window is ~100 further ticks.
        SpinWait.SpinUntil(() => Volatile.Read(ref runsAfterAbort) > runsAtAbort, TimeSpan.FromMilliseconds(100));
        scheduler.Shutdown();

        Assert.That(Volatile.Read(ref runsAfterAbort), Is.EqualTo(runsAtAbort),
            "no system should execute on any tick after the abort");
    }

    [Test]
    public void AbortTickAndStop_ConcurrentThrows_RecordsExactlyOneFirstFailure()
    {
        // AC11 — several workers throwing in the same tick must elect exactly one first failure.
        var gate = new ManualResetEventSlim(false);

        var dag = RuntimeSchedule.Create(Strict(4)).PublicTrack.DeclareDag("Test");
        for (var i = 0; i < 4; i++)
        {
            dag.CallbackSystem($"Thrower{i}", _ =>
            {
                gate.Wait(TimeSpan.FromSeconds(2));     // release all four as simultaneously as the pool allows
                throw new InvalidOperationException("boom");
            });
        }

        using var scheduler = dag.Build(_registry.Runtime);
        scheduler.Start();
        gate.Set();
        SpinWait.SpinUntil(() => scheduler.IsTickAborted, TimeSpan.FromSeconds(2));
        scheduler.Shutdown();

        Assert.That(scheduler.IsTickAborted, Is.True);
        Assert.That(scheduler.AbortedOutcome.Reason, Is.EqualTo(TickOutcomeReason.SystemException));
        Assert.That(scheduler.AbortedOutcome.FailedSystemIndex, Is.InRange(0, 3), "the recorded failure must be one of the throwers");
        Assert.That(scheduler.AbortedOutcome.FailedSystemException, Is.Not.Null, "the first failure's exception must be captured");
    }

    [Test]
    public void AbortTickAndStop_StartedParallelSystem_RunsEveryChunk()
    {
        // AC12 / rule D1 — abort granularity is the SYSTEM, never the chunk. Chunk 0 is claimed before the abort, so
        // the system is committed to run; every remaining chunk must execute rather than being drained.
        const int totalChunks = 4;
        var chunksRun = 0;
        var chunkZeroEntered = new ManualResetEventSlim(false);
        DagScheduler scheduler = null;

        var dag = RuntimeSchedule.Create(Strict(4)).PublicTrack.DeclareDag("Test");
        dag
            .QuerySystem("Parallel", _ => { }, input: () => null, parallel: true)
            .CallbackSystem("Thrower", _ =>
            {
                chunkZeroEntered.Wait(TimeSpan.FromSeconds(2));   // ensure the system has STARTED before we abort
                throw new InvalidOperationException("boom");
            });

        using (scheduler = dag.Build(_registry.Runtime))
        {
            scheduler.ParallelQueryPrepareCallback = _ => totalChunks;
            scheduler.ParallelQueryChunkCallback = (_, chunk, _, _) =>
            {
                if (chunk == 0)
                {
                    chunkZeroEntered.Set();
                    SpinWait.SpinUntil(() => scheduler.IsTickAborted, TimeSpan.FromSeconds(2));
                }
                Interlocked.Increment(ref chunksRun);
            };
            scheduler.ParallelQueryCleanupCallback = _ => false;

            scheduler.Start();
            SpinWait.SpinUntil(() => scheduler.Telemetry.TotalTicksRecorded > 0, TimeSpan.FromSeconds(3));
            scheduler.Shutdown();
        }

        Assert.That(Volatile.Read(ref chunksRun), Is.EqualTo(totalChunks),
            "a parallel system whose chunk 0 was claimed before the abort must execute all of its chunks");
    }

    [Test]
    public void AbortTickAndStop_CancelledSystem_ReportsTickAborted()
    {
        // Telemetry must distinguish "cancelled by the tick abort" from "my predecessor failed" — otherwise an aborted
        // tick reads as a dependency cascade in the Workbench.
        var dag = RuntimeSchedule.Create(Strict(1)).PublicTrack.DeclareDag("Test");
        dag
            .CallbackSystem("Thrower", _ => throw new InvalidOperationException("boom"))
            .CallbackSystem("Independent", _ => { });

        using var scheduler = dag.Build(_registry.Runtime);
        scheduler.Start();
        SpinWait.SpinUntil(() => scheduler.Telemetry.TotalTicksRecorded > 0, TimeSpan.FromSeconds(2));
        scheduler.Shutdown();

        var ring = scheduler.Telemetry;
        var systems = ring.GetSystemMetrics(ring.NewestTick);
        Assert.That(systems[0].SkipReason, Is.EqualTo(SkipReason.Exception), "the throwing system reports Exception");
        Assert.That(systems[1].SkipReason, Is.EqualTo(SkipReason.TickAborted),
            "an independent system cancelled by the abort reports TickAborted, not DependencyFailed");
    }
}
