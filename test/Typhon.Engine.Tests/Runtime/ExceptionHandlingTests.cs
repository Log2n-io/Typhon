using NUnit.Framework;
using System;
using System.Threading;

namespace Typhon.Engine.Tests.Runtime;

[TestFixture]
class ExceptionHandlingTests
{
    private ResourceRegistry _registry;

    [SetUp]
    public void SetUp()
    {
        _registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "ExceptionTest" });
    }

    [TearDown]
    public void TearDown()
    {
        _registry?.Dispose();
    }

    [Test]
    public void SystemException_WorkerSurvives_TickContinues()
    {
        var afterSystemCount = 0;

        var dag = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 }).PublicTrack.DeclareDag("Test");
        dag
            .CallbackSystem("Thrower", _ => throw new InvalidOperationException("test"))
            .CallbackSystem("After", _ => Interlocked.Increment(ref afterSystemCount));
        // "After" has no dependency on "Thrower", so it should still run

        using var scheduler = dag.Build(_registry.Runtime);
        scheduler.Start();
        SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= 3, TimeSpan.FromSeconds(5));
        scheduler.Shutdown();

        Assert.That(afterSystemCount, Is.GreaterThanOrEqualTo(3),
            "Independent system should continue executing after another system throws");
    }

    [Test]
    public void SystemException_SuccessorsSkipped()
    {
        var successorCount = 0;

        var dag = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 }).PublicTrack.DeclareDag("Test");
        dag
            .CallbackSystem("Thrower", _ => throw new InvalidOperationException("test"))
            .CallbackSystem("Successor", _ => Interlocked.Increment(ref successorCount), after: "Thrower");

        using var scheduler = dag.Build(_registry.Runtime);
        scheduler.Start();
        SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= 3, TimeSpan.FromSeconds(5));
        scheduler.Shutdown();

        Assert.That(successorCount, Is.EqualTo(0),
            "Successor of a throwing system should be skipped");
    }

    [Test]
    public void SystemException_TelemetryRecordsException()
    {
        var dag = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 }).PublicTrack.DeclareDag("Test");
        dag
            .CallbackSystem("Thrower", _ => throw new InvalidOperationException("test"))
            .CallbackSystem("Successor", _ => { }, after: "Thrower");

        using var scheduler = dag.Build(_registry.Runtime);
        scheduler.Start();
        SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= 2, TimeSpan.FromSeconds(5));
        scheduler.Shutdown();

        var ring = scheduler.Telemetry;
        if (ring.TotalTicksRecorded > 0)
        {
            var systems = ring.GetSystemMetrics(ring.NewestTick);
            Assert.That(systems[0].SkipReason, Is.EqualTo(SkipReason.Exception),
                "Throwing system should have SkipReason.Exception");
            Assert.That(systems[1].SkipReason, Is.EqualTo(SkipReason.DependencyFailed),
                "Successor should have SkipReason.DependencyFailed");
        }
    }

    [Test]
    public void ParallelQueryException_TickCompletes_NoFullCpuWedge()
    {
        // Repro for the chunk-exception wedge: a parallel QuerySystem where one chunk throws
        // would never reach `OnSystemComplete` because the failing worker bailed at the top of
        // `ProcessPipeline` / `ProcessParallelQuery` without claiming + decrementing remaining
        // chunks. `FindReadySystem` then keeps returning the system (chunks still unclaimed) and
        // workers loop into `ProcessSystem` → break → repeat — a full-CPU spin that never
        // advances the tick. Fix: drain remaining chunks on failure (`DrainFailedSystemChunks`).
        // This test fails (timeout) without the fix; passes within ~1 s with it.
        var afterCount = 0;
        var dag = RuntimeSchedule.Create(new RuntimeOptions
        {
            WorkerCount = 4, BaseTickRate = 1000, ParallelQueryMinChunkSize = 64,
        }).PublicTrack.DeclareDag("Test");
        dag
            .QuerySystem("ThrowingParallel", _ => { }, input: () => null, parallel: true)
            .CallbackSystem("After", _ => Interlocked.Increment(ref afterCount));

        using var scheduler = dag.Build(_registry.Runtime);
        scheduler.ParallelQueryPrepareCallback = _ => 4;
        scheduler.ParallelQueryChunkCallback = (_, chunk, _, _) =>
        {
            if (chunk == 0)
            {
                throw new InvalidOperationException("chunk 0 fails");
            }
            // Other chunks may run if a worker grabs them BEFORE the failure flag is set —
            // that's fine, the test only cares that the tick advances.
        };
        scheduler.ParallelQueryCleanupCallback = _ => false;

        scheduler.Start();
        // 2 s budget — without the fix the scheduler wedges and CurrentTickNumber stays at 0,
        // SpinUntil times out, Assert below fails. With the fix the tick completes in <100 ms.
        var advanced = SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= 3, TimeSpan.FromSeconds(2));
        scheduler.Shutdown();

        Assert.That(advanced, Is.True, "Tick should advance even when a parallel-query chunk throws");
        Assert.That(afterCount, Is.GreaterThanOrEqualTo(3),
            "Independent successor should run on every tick after the throwing system");
    }

    [Test]
    public void SystemException_IndependentBranchContinues()
    {
        var branch1Count = 0;
        var branch2Count = 0;
        var joinCount = 0;

        var dag = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 }).PublicTrack.DeclareDag("Test");
        dag
            .CallbackSystem("Root", _ => { })
            .CallbackSystem("Branch1", _ =>
            {
                Interlocked.Increment(ref branch1Count);
                throw new InvalidOperationException("branch1 fails");
            }, after: "Root")
            .CallbackSystem("Branch2", _ => Interlocked.Increment(ref branch2Count), after: "Root")
            .CallbackSystem("Join", _ => Interlocked.Increment(ref joinCount), afterAll: ["Branch1", "Branch2"]);

        using var scheduler = dag.Build(_registry.Runtime);
        scheduler.Start();
        SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= 3, TimeSpan.FromSeconds(5));
        scheduler.Shutdown();

        Assert.That(branch1Count, Is.GreaterThanOrEqualTo(3), "Branch1 executes (then throws)");
        Assert.That(branch2Count, Is.GreaterThanOrEqualTo(3), "Branch2 should continue despite Branch1 failure");
        Assert.That(joinCount, Is.EqualTo(0), "Join should be skipped (Branch1 failed)");
    }
}
