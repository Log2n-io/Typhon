using System;
using System.Diagnostics;
using System.Threading;
using NUnit.Framework;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// A worker idle inside a dispatch parks on its own event, and whoever publishes work or ends the track wakes it (rule WK-03).
/// </summary>
/// <remarks>
/// <para>Every test runs with no hot spinner and parks at once, so every idle worker parks, and with the park backstop raised to 30 s, so a worker nobody
/// wakes stays parked instead of re-checking two milliseconds later. The parallel systems are "all hands": each chunk waits until every worker of the pool
/// is inside one, so a dispatch that leaves a parked worker asleep makes its chunk-mates' waits time out.</para>
/// <para>Every wait is bounded, so a regression fails the test instead of wedging the suite.</para>
/// </remarks>
[TestFixture]
public class WorkerParkingTests
{
    private const int Workers = 4;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Backstop = TimeSpan.FromSeconds(30);

    private ResourceRegistry _registry;

    [SetUp]
    public void SetUp() => _registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "WorkerParking" });

    [TearDown]
    public void TearDown() => _registry?.Dispose();

    /// <summary>
    /// A serial system runs long enough for every other worker to park; the parallel system it releases must then wake enough of them for every chunk.
    /// </summary>
    [Test]
    [VerifiesRule("WK-03")]
    public void AParallelDispatchAfterASerialGap_WakesTheParkedPool()
    {
        var stalls = 0;
        var schedule = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = Workers, BaseTickRate = 500 });
        var scheduler = schedule.PublicTrack.DeclareDag("Gap")
            .CallbackSystem("Gate", _ => SpinFor(() => false, TimeSpan.FromMilliseconds(2)))
            .QuerySystem("P", _ => { }, after: "Gate", input: () => null, parallel: true)
            .QuerySystem("R", _ => { }, after: "P", input: () => null, parallel: true)
            .Build(_registry.Runtime);
        using (scheduler)
        {
            ArmAllHands(scheduler, () => Interlocked.Increment(ref stalls));
            var reached = Run(scheduler, 40, () => Volatile.Read(ref stalls) != 0);

            Assert.That(stalls, Is.Zero, "WK-03 violated: a parallel dispatch left a parked worker asleep, and its chunk-mates waited for it in vain");
            Assert.That(reached, Is.True, "precondition: the scheduler did not reach tick 40");
            Assert.That(scheduler.WorkerIdle.Parks, Is.GreaterThan(0), "precondition: no worker ever parked, so nothing was tested");
        }
    }

    /// <summary>
    /// One serial system per tick: every other worker parks inside the dispatch, and the completion that ends it must send them back to the between-tick
    /// wait. A worker left parked is still inside the finished dispatch — it reaches the between-tick wait only when its backstop expires or some later
    /// parallel dispatch happens to wake it, so it sits out every tick in between.
    /// </summary>
    [Test]
    [VerifiesRule("WK-03")]
    public void TheEndOfADispatch_ReturnsEveryParkedWorkerToTheBetweenTickWait()
    {
        var reachedWait = new int[Workers];
        var scheduler = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = Workers, BaseTickRate = 200 })
            .PublicTrack.DeclareDag("Serial")
            .CallbackSystem("Long", _ => SpinFor(() => false, TimeSpan.FromMilliseconds(1)))
            .Build(_registry.Runtime);
        using (scheduler)
        {
            ArmAllHands(scheduler, () => { });
            scheduler.BetweenTickWaitProbe = workerId => Interlocked.Increment(ref reachedWait[workerId]);
            const int Ticks = 30;
            var reached = Run(scheduler, Ticks, () => false);

            Assert.That(reached, Is.True, "precondition: the scheduler did not reach the tick count");
            Assert.That(scheduler.WorkerIdle.Parks, Is.GreaterThan(0), "precondition: no worker ever parked, so nothing was tested");
            for (var w = 0; w < Workers; w++)
            {
                Assert.That(reachedWait[w], Is.GreaterThanOrEqualTo(Ticks / 2),
                    $"WK-03 violated: worker {w} reached the between-tick wait {reachedWait[w]} times in {Ticks} ticks — it stayed parked inside a finished "
                    + "dispatch");
            }
        }
    }

    /// <summary>
    /// Short serial gates between all-hands stages, for hundreds of ticks: workers park and are woken thousands of times, in every interleaving the pool
    /// produces, and not one wake may be lost.
    /// </summary>
    [Test]
    [VerifiesRule("WK-03")]
    public void NoWakeIsLost_AcrossThousandsOfParks()
    {
        var stalls = 0;
        var schedule = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = Workers, BaseTickRate = 1000 });
        var scheduler = schedule.PublicTrack.DeclareDag("Churn")
            .CallbackSystem("G1", _ => SpinFor(() => false, TimeSpan.FromMicroseconds(80)))
            .QuerySystem("P1", _ => { }, after: "G1", input: () => null, parallel: true)
            .CallbackSystem("G2", _ => SpinFor(() => false, TimeSpan.FromMicroseconds(80)), after: "P1")
            .QuerySystem("P2", _ => { }, after: "G2", input: () => null, parallel: true)
            .CallbackSystem("G3", _ => SpinFor(() => false, TimeSpan.FromMicroseconds(80)), after: "P2")
            .QuerySystem("P3", _ => { }, after: "G3", input: () => null, parallel: true)
            .Build(_registry.Runtime);
        using (scheduler)
        {
            ArmAllHands(scheduler, () => Interlocked.Increment(ref stalls));
            var reached = Run(scheduler, 400, () => Volatile.Read(ref stalls) != 0);

            TestContext.Out.WriteLine($"{scheduler.WorkerIdle.Parks} parks, {scheduler.WorkerIdle.Wakes} wakes over {scheduler.CurrentTickNumber} ticks");
            Assert.That(stalls, Is.Zero, "WK-03 violated: a wake was lost — a parked worker slept through a dispatch it was needed for");
            Assert.That(reached, Is.True, "precondition: the scheduler did not reach tick 400");
            Assert.That(scheduler.WorkerIdle.Parks, Is.GreaterThan(400), "precondition: too few parks to exercise the protocol");
        }
    }

    /// <summary>No hot spinner, park at once, a backstop no test waits out, and every parallel system all hands.</summary>
    private static void ArmAllHands(DagScheduler scheduler, Action onStall)
    {
        scheduler.HotSpinners = 0;
        scheduler.ParkAfterUs = 0;
        scheduler.MeasureIdle = true;
        scheduler.ParkBackstop = Backstop;
        scheduler.BetweenTickWaitBackstop = Backstop;

        var arrived = new int[scheduler.AllSystemCount];
        scheduler.ParallelQueryPrepareCallback = sysIdx =>
        {
            Volatile.Write(ref arrived[sysIdx], 0);
            return Workers;
        };
        scheduler.ParallelQueryChunkCallback = (sysIdx, _, _, _) =>
        {
            Interlocked.Increment(ref arrived[sysIdx]);
            SpinFor(() => Volatile.Read(ref arrived[sysIdx]) >= Workers || scheduler.IsShutdownRequested, Wait);
            if (Volatile.Read(ref arrived[sysIdx]) < Workers && !scheduler.IsShutdownRequested)
            {
                onStall();
            }
        };
        scheduler.ParallelQueryCleanupCallback = _ => false;
    }

    private static bool Run(DagScheduler scheduler, long ticks, Func<bool> stalled)
    {
        try
        {
            scheduler.Start();
            return SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= ticks || stalled(), TimeSpan.FromSeconds(20)) && !stalled();
        }
        finally
        {
            scheduler.Shutdown();
        }
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
