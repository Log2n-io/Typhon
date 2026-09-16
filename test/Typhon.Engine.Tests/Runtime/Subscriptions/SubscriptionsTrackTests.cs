using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Threading;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// #955 — the Engine-Subscriptions track: where it runs in the tick, what suppresses it, and that it runs on both fence paths.
/// </summary>
/// <remarks>
/// <para>
/// These are the first verifiers TP-01 and TP-01a have ever had. Both rules were cited in prose by <c>FencePhaseFailureTests</c> and
/// <c>StrictTickAbortRuntimeTests</c> and carried no <c>verified:</c> line, so amending them for SUB-02 would have left three rules resting on one new test.
/// </para>
/// <para>
/// <b>Order is asserted, never elapsed time.</b> Every assertion below compares sequence numbers from
/// <see cref="SubscriptionsContext"/>'s ordering journal, captured on the TickDriver thread through
/// <c>TyphonRuntime.SubscriptionsJournalObserver</c>. Timing the phases would be a flake generator — they are sub-millisecond, the flush blocks on an
/// fsync of unpredictable length, and the compute half runs on workers this test does not schedule.
/// </para>
/// </remarks>
[TestFixture]
class SubscriptionsTrackTests : TestBase<SubscriptionsTrackTests>
{
    /// <summary>A sealed tick's ordering journal, copied on the driver thread so the test thread never races the reset.</summary>
    private sealed class Journal
    {
        public long Tick;
        public int Fence;
        public int Compute;
        public int Flush;
        public int Publish;
        public int Chunks;
    }

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<EcsPosition>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static RuntimeOptions Options(bool parallelFence = true, SystemExceptionPolicy policy = SystemExceptionPolicy.Isolate) => new()
    {
        WorkerCount = 2,
        BaseTickRate = 1000,
        EnableParallelFence = parallelFence,
        SystemExceptionPolicy = policy,
    };

    /// <summary>
    /// Runs the runtime until a sealed journal satisfies <paramref name="accept"/>, then stops it and returns that journal.
    /// </summary>
    /// <param name="sessionCount">
    /// Sessions to pretend are connected. Zero is the production value until ingress exists (#956 / #957) and is what AC-6 asserts against; any positive value
    /// clears the track's "no session, no work" gate so the rest of the pipeline can be observed.
    /// </param>
    private static Journal RunUntil(DatabaseEngine dbe, RuntimeOptions options, int sessionCount, Func<Journal, bool> accept, bool fatalStop = false)
    {
        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Noop", _ => { });
        }, options);

        return RunUntil(runtime, sessionCount, accept, fatalStop);
    }

    private static Journal RunUntil(TyphonRuntime runtime, int sessionCount, Func<Journal, bool> accept, bool fatalStop)
    {
        Journal captured = null;

        runtime.SubscriptionsContextForTest.SessionCount = sessionCount;
        runtime.SubscriptionsJournalObserver = ctx =>
        {
            if (Volatile.Read(ref captured) != null)
            {
                return;
            }

            var j = new Journal
            {
                Tick = ctx.TickNumber,
                Fence = ctx.FenceSeq,
                Compute = ctx.ComputeSeq,
                Flush = ctx.FlushSeq,
                Publish = ctx.PublishSeq,
                Chunks = ctx.ChunksExecuted,
            };

            if (!accept(j))
            {
                return;
            }

            // Release store: the Journal's fields are written before it, and the test thread's Volatile.Read below is the matching acquire. Paired explicitly
            // because this has to be correct on arm64, not merely on x64.
            Volatile.Write(ref captured, j);
        };

        runtime.Start();

        // SpinUntil rather than a ManualResetEventSlim, deliberately. An event would have to be disposed when this method returns, and neither Shutdown nor
        // FatalStop is a quiescence point — CurrentTickNumber can advance by ONE after either returns. On the timeout path the observer is still
        // installed, so that late tick would Set() an already-disposed event and the ObjectDisposedException would disappear into the tick driver's
        // catch-log-and-continue.
        // Nothing here needs disposing, so the hazard does not exist.
        var got = SpinWait.SpinUntil(() => Volatile.Read(ref captured) != null, TimeSpan.FromSeconds(5));

        if (fatalStop)
        {
            runtime.FatalStop();
        }
        else
        {
            runtime.Shutdown();
        }

        // Cleared only once the runtime has stopped, so a tick that slips through afterwards finds nothing to call.
        runtime.SubscriptionsJournalObserver = null;

        Assert.That(got, Is.True, "no tick sealed a journal matching the predicate within 5 s");
        return Volatile.Read(ref captured);
    }

    [Test]
    [VerifiesRule("TP-01")]
    [VerifiesRule("SUB-02")]
    public void NormalTick_RunsFenceThenComputeThenFlushThenPublish()
    {
        // AC-1. The whole point of the track's placement: all app writers are done (a track is a barrier, PH-01), the fence has published its WAL records, and
        // publication waits for the flush that made them durable.
        using var dbe = SetupEngine();
        var j = RunUntil(dbe, Options(), sessionCount: 4, accept: x => x.Publish > 0);

        Assert.Multiple(() =>
        {
            Assert.That(j.Fence, Is.GreaterThan(0), "the fence must have run");
            Assert.That(j.Compute, Is.GreaterThan(j.Fence), "replication computes AFTER the fence — it reads what the fence completed");
            Assert.That(j.Flush, Is.GreaterThan(j.Compute), "the flush follows the compute half");
            Assert.That(j.Publish, Is.GreaterThan(j.Flush),
                "publication follows the flush: a frame published before its tick is durable would put a client's baseline ahead of the server's WAL");
        });
    }

    [Test]
    [VerifiesRule("TP-01a")]
    [VerifiesRule("SUB-02")]
    public void AbortedTick_StillFencesAndFlushes_ButNeitherComputesNorPublishes()
    {
        // AC-2, and the half of TP-01a that had no verifier: the fence and the flush are unconditional, and the output half is the ONLY one that may be
        // skipped.
        //
        // The gate is load-bearing in a way that is easy to miss. Engine-tagged systems are deliberately EXEMPT from the scheduler's tick-abort guard — that
        // exemption is what makes the fence run on an aborted tick — so the track would keep dispatching after an abort unless it opts out itself.
        using var dbe = SetupEngine();

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Thrower", _ => throw new InvalidOperationException("boom"));
        }, Options(policy: SystemExceptionPolicy.AbortTickAndStop));

        var j = RunUntil(runtime, sessionCount: 4, accept: _ => true, fatalStop: true);

        Assert.Multiple(() =>
        {
            Assert.That(j.Fence, Is.GreaterThan(0), "TP-01a: the fence runs on an aborted tick");
            Assert.That(j.Flush, Is.GreaterThan(j.Fence), "TP-01a: the flush runs on an aborted tick");
            Assert.That(j.Compute, Is.Zero, "SUB-02: nothing is computed for an aborted tick");
            Assert.That(j.Publish, Is.Zero, "SUB-02: nothing is published for an aborted tick");
            Assert.That(j.Chunks, Is.Zero, "no stage may dispatch a chunk once the tick is aborted");
        });
    }

    [Test]
    public void SerialFencePath_StillDispatchesTheTrack()
    {
        // AC-5 — the regression test for the trap this task closes. DispatchDeferredTracks had exactly one call site, inside RunParallelFence, so with
        // EnableParallelFence = false a deferred track silently never executed. Nothing noticed, because the Fence DAG is not declared in serial mode at all:
        // the loop was empty, so nothing was missing. This test fails against that shape and passes against the fixed one.
        using var dbe = SetupEngine();
        var j = RunUntil(dbe, Options(parallelFence: false), sessionCount: 4, accept: x => x.Publish > 0);

        Assert.Multiple(() =>
        {
            Assert.That(j.Compute, Is.GreaterThan(j.Fence), "the track must run on the serial fence path too, and still after the fence");
            Assert.That(j.Flush, Is.GreaterThan(j.Compute));
            Assert.That(j.Publish, Is.GreaterThan(j.Flush));
        });
    }

    [Test]
    public void NoSessionConnected_ComputesNothingAndDispatchesNoChunks()
    {
        // AC-6 — "no session, no work". Zero is the production value until ingress lands.
        //
        // Named for what it can actually see. It was called ...CostsTheGateCheckAndNothingElse, which claimed more than it checked: the expensive half of an
        // idle track is the wake/barrier cycle, and nothing here can observe that. AnIdleTrack_DoesNotWakeTheWorkerPool is the test that can.
        using var dbe = SetupEngine();
        var j = RunUntil(dbe, Options(), sessionCount: 0, accept: x => x.Publish > 0);

        Assert.Multiple(() =>
        {
            Assert.That(j.Compute, Is.Zero, "no stage may clear its gate with nothing subscribed");
            Assert.That(j.Chunks, Is.Zero, "zero chunks dispatched");
            Assert.That(j.Fence, Is.GreaterThan(0), "the rest of the tick is unaffected");
            Assert.That(j.Publish, Is.GreaterThan(j.Flush), "publication still runs — it is the compute half that had nothing to do");
        });
    }

    [Test]
    [VerifiesRule("SUB-02")]
    public void AStageThatThrows_DoesNotStopTheEngine()
    {
        // A throw on an engine-tagged track latches IsFenceFailed, and that latch is TERMINAL — ExecuteCallbacks returns early on every subsequent tick. That
        // is correct for the fence, whose unfinished work leaves cluster pages dirty and un-logged for the checkpoint to persist. It is wrong for replication,
        // which writes only RAM-only blocks: nothing a later tick does can compound the damage, so stopping the database would make a bug in the newest
        // subsystem in the engine strictly more destructive than the same bug in a user system.
        //
        // Without Track.FailureIsTerminal this test stalls at one tick and IsFenceFailed is true.
        using var dbe = SetupEngine();

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Noop", _ => { });
        }, Options());

        runtime.SubscriptionsContextForTest.SessionCount = 4;
        runtime.SubscriptionsContextForTest.FaultGateForTest = true;

        runtime.Start();
        var keptTicking = SpinWait.SpinUntil(() => runtime.CurrentTickNumber >= 5, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        var ctx = runtime.SubscriptionsContextForTest;

        Assert.Multiple(() =>
        {
            Assert.That(keptTicking, Is.True, "a throwing replication stage must not stop the engine — it writes nothing a later tick could compound");
            Assert.That(runtime.Scheduler.IsFenceFailed, Is.False, "a replication failure is not a fence failure and must not be reported as one");
            Assert.That(runtime.Scheduler.IsTickAborted, Is.False, "nor a tick abort — engine tracks are already exempt from that latch");
            Assert.That(runtime.LastTickOutcome.Succeeded, Is.True, "the rest of the tick is unharmed: the fence ran and the flush ran");

            // The other half of the same rule, and the hole that isolating the failure opened. Publication shares v1's `!tickAborted && !fenceFailed` gate,
            // and a stage throw now latches neither — so without the fault flag a tick whose compute blew up would publish as though it had succeeded.
            Assert.That(ctx.Faulted, Is.True, "the stage throw must be recorded");
            Assert.That(ctx.ComputeSeq, Is.Zero, "the gate threw before stamping, so no stage completed its compute");
            Assert.That(ctx.PublishSeq, Is.Zero,
                "SUB-02: compute and publish are skippable together and only together — nothing may be published for a tick whose compute faulted");
        });
    }

    [Test]
    public void AnIdleTrack_DoesNotWakeTheWorkerPool()
    {
        // The claim this guards is "with nothing subscribed the track costs its gate checks". That was false when first written:
        // DispatchTrackMultiThreaded skipped only genuinely EMPTY tracks, so four gated-off stages still bought a generation bump and a Set on every worker —
        // a full wake/barrier cycle, ≈ 0.1 ms, on every tick of every runtime that had never enabled replication.
        //
        // WakeProbe fires once per worker per wake ROUND. One round per tick is the Public track doing its own dispatch; a second round per tick would be the
        // idle Subscriptions track, which is exactly the regression.
        using var dbe = SetupEngine();

        var wakes = 0;
        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Noop", _ => { });
        }, Options(parallelFence: false));

        runtime.SubscriptionsContextForTest.SessionCount = 0;
        runtime.Scheduler.WakeProbe = _ => Interlocked.Increment(ref wakes);

        runtime.Start();
        SpinWait.SpinUntil(() => runtime.CurrentTickNumber >= 10, TimeSpan.FromSeconds(5));
        runtime.Shutdown();
        runtime.Scheduler.WakeProbe = null;

        var ticks = runtime.CurrentTickNumber;
        var rounds = Volatile.Read(ref wakes) / 2.0;   // WorkerCount = 2, one probe call per worker per round

        Assert.That(ticks, Is.GreaterThanOrEqualTo(10), "the runtime must actually have ticked, or this proves nothing");
        Assert.That(rounds, Is.LessThanOrEqualTo(ticks + 1),
            $"expected about one wake round per tick (the Public track's), got {rounds} rounds across {ticks} ticks — "
            + "the idle Subscriptions track is waking the pool");
    }

    [Test]
    public void AppTracksStillSlotBeforeEnginePost_NowThatAnEngineTrackFollowsIt()
    {
        // DeclareTrack inserted at `_tracks.Count - 1`, which meant "before Engine-Post" only while Engine-Post was the LAST track. Adding
        // Engine-Subscriptions behind it would have silently pushed every app track past the fence — app systems running after the tick fence, with nothing
        // validating the order. This is the guard for that, and it is a schedule-level fact needing no runtime.
        var schedule = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 2, BaseTickRate = 1000 });
        var app = schedule.DeclareTrack("App");

        Assert.Multiple(() =>
        {
            Assert.That(app.OrderIndex, Is.GreaterThan(schedule.PublicTrack.OrderIndex), "an app track follows Public");
            Assert.That(app.OrderIndex, Is.LessThan(schedule.EnginePostTrack.OrderIndex), "an app track must still precede Engine-Post");
            Assert.That(schedule.EngineSubscriptionsTrack.OrderIndex, Is.EqualTo(schedule.Tracks.Count - 1),
                "Engine-Subscriptions is last, so replication sees a completed fence");
            Assert.That(schedule.Tracks[^1], Is.SameAs(schedule.EngineSubscriptionsTrack));
        });
    }

    [Test]
    public void EngineSubscriptionsTrack_CannotBeDeclaredByAnApp()
    {
        // The `Engine-` prefix is reserved, so an app cannot declare a second track with this name and cannot place work after the fence by naming its way in.
        var schedule = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 2, BaseTickRate = 1000 });

        var ex = Assert.Throws<InvalidOperationException>(() => schedule.DeclareTrack("Engine-Subscriptions"));
        Assert.That(ex.Message, Does.Contain("reserved"));
    }
}
