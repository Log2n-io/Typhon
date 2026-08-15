using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Threading;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// Engine-level acceptance tests for the strict tick-abort policy (issue #567) — the parts that need a real
/// <see cref="DatabaseEngine"/> and <see cref="TyphonRuntime"/>: the outcome surface, tick-end phase behaviour, and the
/// fatal-stop path. The dispatch-level cancellation is covered by <c>StrictTickAbortTests</c>.
/// </summary>
[TestFixture]
class StrictTickAbortRuntimeTests : TestBase<StrictTickAbortRuntimeTests>
{
    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<EcsPosition>();
        dbe.RegisterComponentFromAccessor<EcsVelocity>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static RuntimeOptions Strict() => new()
    {
        WorkerCount = 2,
        BaseTickRate = 1000,
        SystemExceptionPolicy = SystemExceptionPolicy.AbortTickAndStop,
    };

    // WorkerCount 1 puts the whole tick inline on the timer thread (ExecuteTickSingleThreaded), which is the shape the re-entrancy test below needs — and
    // the shape the #404 report was filed against.
    private static RuntimeOptions StrictSingleWorker() => new()
    {
        WorkerCount = 1,
        BaseTickRate = 1000,
        SystemExceptionPolicy = SystemExceptionPolicy.AbortTickAndStop,
    };

    [Test]
    public void AbortedTick_StillRunsFenceAndFlush()
    {
        // AC5 + AC6 — the fence and the UoW flush are unconditional (rule TP-01a); only the output phase is gated.
        //
        // LastTickOutcome is assigned inside OnTickEndInternal *after* the WriteTickFence and UoW.Flush statements. So
        // observing a populated abort outcome proves control flow reached past both — had either been skipped by a
        // stray gate, or thrown, the assignment below would never have happened.
        using var dbe = SetupEngine();
        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Thrower", _ => throw new InvalidOperationException("boom"));
        }, Strict());

        runtime.Start();
        SpinWait.SpinUntil(() => !runtime.LastTickOutcome.Succeeded, TimeSpan.FromSeconds(3));
        runtime.FatalStop();

        Assert.That(runtime.LastTickOutcome.Reason, Is.EqualTo(TickOutcomeReason.SystemException),
            "tick-end processing must run to completion on an aborted tick — fence and flush are never skipped");
    }

    [Test]
    public void AbortedTick_PublishesOutcome_AndFiresOnTickAbortedExactlyOnce()
    {
        // AC8 — the outcome surface reports the first failing system and its exception, and the event fires once.
        var fireCount = 0;
        TickOutcome captured = default;

        using var dbe = SetupEngine();
        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Thrower", _ => throw new InvalidOperationException("boom"));
        }, Strict());

        runtime.OnTickAborted += (_, outcome) =>
        {
            Interlocked.Increment(ref fireCount);
            captured = outcome;
        };

        runtime.Start();
        SpinWait.SpinUntil(() => Volatile.Read(ref fireCount) > 0, TimeSpan.FromSeconds(3));
        // The runtime is terminal, so no further tick can fire the event. Give it a window to prove that.
        SpinWait.SpinUntil(() => Volatile.Read(ref fireCount) > 1, TimeSpan.FromMilliseconds(150));
        runtime.FatalStop();

        Assert.That(Volatile.Read(ref fireCount), Is.EqualTo(1), "OnTickAborted must fire exactly once");
        Assert.That(captured.Succeeded, Is.False);
        Assert.That(captured.Reason, Is.EqualTo(TickOutcomeReason.SystemException));
        Assert.That(captured.FailedSystemName, Is.EqualTo("Thrower"), "the first failing system must be named");
        Assert.That(captured.FailedSystemException, Is.TypeOf<InvalidOperationException>());
        Assert.That(runtime.LastTickOutcome.FailedSystemIndex, Is.GreaterThanOrEqualTo(0));
    }

    // The documented response to OnTickAborted is FatalStop(), and the handler runs on the TICK thread, inside the tick — so that call re-enters the
    // scheduler's shutdown from the very thread it is stopping. Two properties fall out, and both are contract rather than accident (#404):
    //
    //   1. FatalStop() must RETURN, promptly. Nothing may join the tick thread from this path — that would be a self-join. StopTimerThread()'s join is
    //      bounded at 2 s, so implementing #404's original action ("Shutdown() should stop+join the timer thread") without a re-entrancy branch turns every
    //      fatal stop into a 2 s stall that returns false and changes nothing. The 1 s bound below is what reddens if someone does that.
    //   2. CurrentTickNumber advances EXACTLY once after FatalStop() returns: the aborted tick resumes where the handler left it and runs its telemetry
    //      finalizer, whose last statement is the increment. Here the advance is guaranteed, not merely possible — which is what the Shutdown() doc denied
    //      when it promised the counter was "stable once this returns".
    [Test]
    public void FatalStopFromAbortHandler_ReturnsWithoutSelfJoin_AndCostsExactlyOneMoreTick()
    {
        long tickInsideHandler = -1;
        var fatalStopElapsed = TimeSpan.MaxValue;
        using var handlerDone = new ManualResetEventSlim(false);

        using var dbe = SetupEngine();
        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Thrower", _ => throw new InvalidOperationException("boom"));
        }, StrictSingleWorker());

        runtime.OnTickAborted += (r, _) =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            r.FatalStop();
            sw.Stop();

            fatalStopElapsed = sw.Elapsed;
            // Read INSIDE the handler: we are still within the aborted tick, so its increment has not run yet.
            Volatile.Write(ref tickInsideHandler, r.CurrentTickNumber);
            handlerDone.Set();
        };

        runtime.Start();
        Assert.That(handlerDone.Wait(TimeSpan.FromSeconds(5)), Is.True, "the abort handler must run");

        // Deliberate observation window: let the aborted tick post its accounting, and prove nothing follows it.
        Thread.Sleep(50);

        Assert.That(fatalStopElapsed, Is.LessThan(TimeSpan.FromSeconds(1)),
            "FatalStop() from the tick thread must not join the tick thread — a self-join would stall for StopTimerThread's 2 s bound");
        Assert.That(runtime.CurrentTickNumber, Is.EqualTo(Volatile.Read(ref tickInsideHandler) + 1),
            "the aborted tick posts its own accounting after FatalStop() returns — exactly one advance, and no further tick");
    }

    [Test]
    public void SuccessfulTick_ReportsSuccess_AndDoesNotFireOnTickAborted()
    {
        // AC8b (success half) — LastTickOutcome is refreshed every tick, so a host can read it unconditionally.
        var fireCount = 0;

        using var dbe = SetupEngine();
        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Noop", _ => { });
        }, Strict());

        runtime.OnTickAborted += (_, _) => Interlocked.Increment(ref fireCount);

        runtime.Start();
        SpinWait.SpinUntil(() => runtime.CurrentTickNumber >= 3, TimeSpan.FromSeconds(3));
        runtime.Shutdown();

        Assert.That(runtime.LastTickOutcome.Succeeded, Is.True);
        Assert.That(runtime.LastTickOutcome.FailedSystemIndex, Is.EqualTo(-1));
        Assert.That(runtime.LastTickOutcome.FailedSystemException, Is.Null);
        Assert.That(Volatile.Read(ref fireCount), Is.Zero, "OnTickAborted must never fire for a successful tick");
    }

    [Test]
    public void IsolatePolicy_SystemThrows_TickStillReportsSuccess()
    {
        // AC8b (the subtle half) — under Isolate, a tick in which a system threw and its branch was skipped completed
        // exactly as that policy promises. Reporting it as a failure would misdescribe the contract; the per-system
        // detail stays in SkipReason.Exception.
        using var dbe = SetupEngine();
        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Thrower", _ => throw new InvalidOperationException("boom"));
        }, new RuntimeOptions { WorkerCount = 2, BaseTickRate = 1000 });   // default policy = Isolate

        runtime.Start();
        SpinWait.SpinUntil(() => runtime.CurrentTickNumber >= 3, TimeSpan.FromSeconds(3));
        runtime.Shutdown();

        Assert.That(runtime.CurrentTickNumber, Is.GreaterThanOrEqualTo(3), "Isolate keeps ticking through a throwing system");
        Assert.That(runtime.LastTickOutcome.Succeeded, Is.True, "fault isolation is Isolate's contract — such a tick did not fail");
    }

    [Test]
    public void FatalStop_DoesNotRunOnShutdown()
    {
        // AC9 — OnShutdown runs its handlers inside an Immediate-durability transaction, which is the wrong thing after
        // a fatal tick: it would commit shutdown writes derived from a tick that never finished.
        var shutdownRan = 0;

        using var dbe = SetupEngine();
        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Noop", _ => { });
        }, Strict());

        runtime.OnShutdown += _ => Interlocked.Increment(ref shutdownRan);

        runtime.Start();
        SpinWait.SpinUntil(() => runtime.CurrentTickNumber >= 2, TimeSpan.FromSeconds(3));
        runtime.FatalStop();

        Assert.That(Volatile.Read(ref shutdownRan), Is.Zero, "FatalStop must not fire the OnShutdown Immediate transaction");
    }

    [Test]
    public void Shutdown_StillRunsOnShutdown()
    {
        // Guards the refactor that extracted StopInternal — the graceful path must be unchanged.
        var shutdownRan = 0;

        using var dbe = SetupEngine();
        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Noop", _ => { });
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        runtime.OnShutdown += _ => Interlocked.Increment(ref shutdownRan);

        runtime.Start();
        SpinWait.SpinUntil(() => runtime.CurrentTickNumber >= 2, TimeSpan.FromSeconds(3));
        runtime.Shutdown();

        Assert.That(Volatile.Read(ref shutdownRan), Is.EqualTo(1), "graceful Shutdown must still run OnShutdown");
    }
}
