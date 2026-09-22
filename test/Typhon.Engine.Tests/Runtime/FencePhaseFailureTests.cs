using System;
using System.Numerics;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// #890 — what the runtime does when a FENCE phase throws.
/// </summary>
/// <remarks>
/// <para>
/// Before #890 the answer was "records it in per-system telemetry and keeps ticking". A phase whose <c>Prepare</c> threw was marked
/// <see cref="SkipReason.Exception"/>, every successor phase was skipped as <see cref="SkipReason.DependencyFailed"/>, and nothing reached the host:
/// <c>UnhandledExceptionCallback</c> was invoked only from the WorkerLoop's outer safety net, which a <c>Prepare</c> failure never passes through. That is
/// how #889's <c>NullReferenceException</c> ran for a whole session with no WAL emit, no dormancy sweep and no dirty-ring archive on every tick with dirty
/// data — TP-01a's failure mode exactly, and silent at the point of damage.
/// </para>
/// <para>
/// The injection point is <see cref="ArchetypeClusterState.PrepQueueProbe"/>, which the fence calls at the end of <c>FinishArchetypeFencePrep</c>. No
/// production seam exists for this and none should: the probe is already there, and throwing from it reproduces #889's shape without simulating it.
/// </para>
/// <para>
/// <b>Which GATE it lands on depends on whether Prep sliced, and both are covered because they are different catch sites.</b> An archetype below
/// <see cref="DatabaseEngine.PrepSliceMinClusters"/> runs the atomic <c>ArchetypePrep</c> work item, so the probe throws from <c>DispatchItem</c> — an
/// <b>Execute</b> chunk. A sliced one reaches the same call from Prep's serial tail inside <c>FenceMigrateExecSystem.Prepare</c> — a <b>Prepare</b> gate,
/// which is the one #889 actually hit and the one that reached the host through nothing at all. A fixture that only covered the Execute path would be
/// asserting the case that was already half-working.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]   // drives the static PrepQueueProbe
class FencePhaseFailureTests : TestBase<FencePhaseFailureTests>
{
    private const float CellSize = 100f;
    private const float WorldMax = 1000f;
    private const int Population = 400;
    private const string Marker = "#890 injected fence-phase failure";

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    private static ClMigPos PointAt(float x, float y, int tag) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Tag = tag };

    private DatabaseEngine SetupEngine(out EntityId[] ids)
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClMigPos>();
        dbe.RegisterComponentFromAccessor<ClMigScratch>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(WorldMax, WorldMax), CellSize, reclusterBudgetMs: 0f));
        dbe.InitializeArchetypes();

        var spawned = new EntityId[Population];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < Population; i++)
            {
                spawned[i] = tx.Spawn<ClMigUnit>(ClMigUnit.Pos.Set(PointAt(10f + (i % 40), 10f + (i / 40), i)));
            }

            tx.Commit();
        }

        ids = spawned;

        dbe.WriteTickFence(1);
        return dbe;
    }

    private sealed class Run
    {
        public Exception Unhandled;
        public TickOutcome Outcome;
        public TickOutcome Aborted;
        public int AbortedEvents;
        public long TicksAtFailure;
        public long TicksAtEnd;
        public int ProbeCalls;
        public int SlicesRun;
    }

    /// <summary>
    /// Whether <see cref="ArchetypeClusterState.PrepSlicesRun"/> counts in this build — its bump is <c>[Conditional("DEBUG")]</c>, so Release leaves it at
    /// 0 however many slices run. Probed by bumping it rather than by <c>#if DEBUG</c>, so it cannot drift from the engine's attribute.
    /// </summary>
    private static readonly bool SliceCounterCounts = ProbeSliceCounter();

    private static bool ProbeSliceCounter()
    {
        var before = Volatile.Read(ref ArchetypeClusterState.PrepSlicesRun);
        ArchetypeClusterState.NotePrepSliceRun();
        return Volatile.Read(ref ArchetypeClusterState.PrepSlicesRun) != before;
    }

    /// <summary>
    /// Runs the world for a few ticks, optionally making the fence's Prep throw on the first tick that reaches the throttle.
    /// </summary>
    /// <param name="injectFailure">Whether to throw from the probe.</param>
    /// <param name="sliced">
    /// When true, forces Prep to slice on a world this small so the throw lands in <c>FenceMigrateExecSystem.Prepare</c>; when false the atomic item runs
    /// and it lands in an Execute chunk. Both statics are restored by the caller.
    /// </param>
    private Run RunWith(bool injectFailure, bool sliced = false, bool parallelFence = true)
    {
        var dbe = SetupEngine(out var ids);
        var run = new Run();

        var ticks = 0;
        var thrown = 0;
        var slicesBefore = Volatile.Read(ref ArchetypeClusterState.PrepSlicesRun);
        var savedMinClusters = DatabaseEngine.PrepSliceMinClusters;
        var savedSliceWords = FenceWorkPlan.PrepSliceWords;

        // Each arm PINS its own precondition rather than trusting the ambient default: a 400-entity world has single-digit clusters, which is below the
        // production threshold today but is a fact about two constants that have already moved once. PrepSliceWords comes down with the threshold, or the
        // planner emits one slice covering every word and the tail is reached with nothing to concatenate.
        DatabaseEngine.PrepSliceMinClusters = sliced ? 2 : int.MaxValue;
        FenceWorkPlan.PrepSliceWords = sliced ? 2 : savedSliceWords;
        TyphonRuntime runtime = null;

        if (injectFailure)
        {
            ArchetypeClusterState.PrepQueueProbe = (_, _) =>
            {
                Interlocked.Increment(ref run.ProbeCalls);
                if (Interlocked.Exchange(ref thrown, 1) == 0)
                {
                    run.TicksAtFailure = runtime?.CurrentTickNumber ?? 0;
                    throw new InvalidOperationException(Marker);
                }
            };
        }

        try
        {
            runtime = TyphonRuntime.Create(dbe, schedule =>
            {
                var dag = schedule.PublicTrack.DeclareDag("Test");
                dag.CallbackSystem("Move", ctx =>
                {
                    // Something dirty every tick, so the fence has a Prep tail to reach at all.
                    var n = Interlocked.Increment(ref ticks);
                    var tx = ctx.Transaction;
                    for (var i = 0; i < 32; i++)
                    {
                        ref var pos = ref tx.OpenMut(ids[i]).Write(ClMigUnit.Pos);
                        pos.Bounds = new AABB2F { MinX = 10f + (n % 7), MinY = 10f + (n % 5), MaxX = 10f + (n % 7), MaxY = 10f + (n % 5) };
                    }
                });
            }, new RuntimeOptions { WorkerCount = 2, BaseTickRate = 200, EnableParallelFence = parallelFence });

            using var outcomePublished = new ManualResetEventSlim(false);
            using (runtime)
            {
                runtime.Scheduler.UnhandledExceptionCallback = (_, _, ex) => Interlocked.CompareExchange(ref run.Unhandled, ex, null);
                runtime.OnTickAborted += (_, outcome) =>
                {
                    run.Aborted = outcome;
                    Interlocked.Increment(ref run.AbortedEvents);
                    outcomePublished.Set();
                };

                runtime.Start();
                // The callback fires from whichever thread caught the throw, LONG before OnTickEndInternal publishes the outcome — so waiting on the
                // callback and then sleeping would be racing the publication. `outcomePublished` is set by the OnTickAborted handler, which the runtime
                // raises on the TickDriver immediately AFTER assigning LastTickOutcome; waiting on it is therefore exact rather than approximately long
                // enough. The healthy arm has no such event and falls back to the tick count, which is its own terminating condition.
                SpinWait.SpinUntil(() => outcomePublished.IsSet || Volatile.Read(ref ticks) >= 6, TimeSpan.FromSeconds(15));
                run.SlicesRun = Volatile.Read(ref ArchetypeClusterState.PrepSlicesRun) - slicesBefore;
                run.Outcome = runtime.LastTickOutcome;
                run.TicksAtEnd = runtime.CurrentTickNumber;
                runtime.Shutdown();
            }
        }
        finally
        {
            ArchetypeClusterState.PrepQueueProbe = null;
            DatabaseEngine.PrepSliceMinClusters = savedMinClusters;
            FenceWorkPlan.PrepSliceWords = savedSliceWords;
        }

        return run;
    }

    /// <summary>
    /// The serial fence — the arm a host gets with <c>EnableParallelFence = false</c>, where the whole fence runs inline on the tick driver. Its throw never
    /// passed through the scheduler at all, so before #890 it took the UoW flush and dispose down with it and left the PREVIOUS tick's `Success` standing.
    /// </summary>
    // #890 — a fence phase whose Prepare throws is swallowed into per-system telemetry and the host is never told
    [Test]
    [CancelAfter(60_000)]
    public void ASerialFenceThatThrows_ReachesTheHost_AndStopsTheClock()
    {
        var run = RunWith(injectFailure: true, sliced: false, parallelFence: false);
        Assert.Multiple(() =>
        {
            Assert.That(run.Unhandled, Is.Not.Null, "the serial fence's throw must reach the host too");
            Assert.That(run.Outcome.Reason, Is.EqualTo(TickOutcomeReason.FenceFailure));
            Assert.That(run.Outcome.FailedSystemIndex, Is.EqualTo(-1), "no system owns a driver-side fence failure");
            Assert.That(run.Outcome.FailedSystemName, Is.EqualTo("<tick fence>"));
            Assert.That(run.AbortedEvents, Is.EqualTo(1));
        });
    }

    /// <summary>The control: the same world, the same runtime, nothing injected — every tick reports success and the host is told nothing.</summary>
    [Test]
    [CancelAfter(60_000)]
    public void AHealthyFence_ReportsSuccess_AndTellsTheHostNothing()
    {
        var run = RunWith(injectFailure: false);
        Assert.Multiple(() =>
        {
            Assert.That(run.Unhandled, Is.Null, "a healthy fence must not invoke the unhandled-exception hook");
            Assert.That(run.Outcome.Reason, Is.EqualTo(TickOutcomeReason.Success), "every tick completed as its policy promises");
            Assert.That(run.AbortedEvents, Is.EqualTo(0), "OnTickAborted is for a tick that did not complete");
        });
    }

    // #890 — a fence phase whose Prepare throws is swallowed into per-system telemetry and the host is never told
    [TestCase(true, TestName = "InPrepare_WhenPrepIsSliced")]
    [TestCase(false, TestName = "InExecute_WhenPrepIsAtomic")]
    [CancelAfter(60_000)]
    public void AFencePhaseThatThrows_ReachesTheHost_AndStopsTheClock(bool sliced)
    {
        var run = RunWith(injectFailure: true, sliced);
        Assert.Multiple(() =>
        {
            Assert.That(run.ProbeCalls, Is.GreaterThan(0), "sanity: the injection point must actually be reached, or this fixture asserts nothing");

            // The arm is only worth what its GATE is: without this the sliced case can silently degrade into the atomic one and the fixture goes on
            // passing while the Prepare path — #890's own case — is never entered again. The counter that gate reads is bumped by a
            // [Conditional("DEBUG")] method, so in Release it stays 0 however many slices ran: asking it whether it counts is the difference between a
            // gate and a test that is red in Release for no defect. The negative half still holds either way — 0 is 0 in both builds.
            if (SliceCounterCounts || !sliced)
            {
                Assert.That(run.SlicesRun, sliced ? Is.GreaterThan(0) : Is.EqualTo(0),
                    sliced ? "the sliced arm must actually slice, or the throw lands in Execute like the other arm" : "the atomic arm must not slice");
            }
            Assert.That(run.Unhandled, Is.Not.Null, "the host must be told: this is the whole of #890");
            Assert.That(run.Unhandled?.Message, Does.Contain(Marker), "and told about the exception that actually failed, not a wrapper");

            Assert.That(run.Outcome.Reason, Is.EqualTo(TickOutcomeReason.FenceFailure),
                "a fence that did not finish is not a Success — D3 gives it its own reason rather than reporting it as a tick abort");
            Assert.That(run.Outcome.FailedSystemException?.Message, Does.Contain(Marker));
            Assert.That(run.Outcome.FailedSystemName, Is.Not.Null.And.Not.Empty, "the outcome names the phase that failed");

            Assert.That(run.AbortedEvents, Is.EqualTo(1), "the push counterpart fires exactly once");
            Assert.That(run.Aborted.Reason, Is.EqualTo(TickOutcomeReason.FenceFailure));

            // Terminal: the backstop in ExecuteCallbacks returns before any later tick body runs, so the clock cannot keep climbing while the fence is broken.
            // One more tick than the failing one is admitted — the failure is latched from inside the tick that is already running.
            Assert.That(run.TicksAtEnd, Is.LessThanOrEqualTo(run.TicksAtFailure + 1),
                $"the runtime kept ticking after its fence failed: {run.TicksAtFailure} -> {run.TicksAtEnd}");
        });
    }
}
