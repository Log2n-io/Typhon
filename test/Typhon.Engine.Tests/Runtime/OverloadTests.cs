using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Threading;

namespace Typhon.Engine.Tests.Runtime;

// ═══════════════════════════════════════════════════════════════
// OverloadDetector unit tests (pure state machine, no scheduler)
// ═══════════════════════════════════════════════════════════════

[TestFixture]
public class OverloadDetectorTests
{
    private static OverloadDetector Create(
        float overrunThreshold = 1.2f,
        float deescalationRatio = 0.6f,
        int escalationTicks = 5,
        int deescalationTicks = 20,
        int baseTickRate = 60) =>
        new(new OverloadOptions
        {
            OverrunThreshold = overrunThreshold,
            DeescalationRatio = deescalationRatio,
            EscalationTicks = escalationTicks,
            DeescalationTicks = deescalationTicks
        }, baseTickRate);

    [Test]
    public void Normal_NoOverrun_StaysNormal()
    {
        var detector = Create();

        for (var i = 0; i < 100; i++)
        {
            detector.Update(0.5f); // Well under threshold
        }

        Assert.That(detector.CurrentLevel, Is.EqualTo(OverloadLevel.Normal));
        Assert.That(detector.TickMultiplier, Is.EqualTo(1));
    }

    [Test]
    public void ConsecutiveOverruns_EscalatesToLevel1()
    {
        var detector = Create(escalationTicks: 3);

        // 2 overruns — not enough
        detector.Update(1.5f);
        detector.Update(1.5f);
        Assert.That(detector.CurrentLevel, Is.EqualTo(OverloadLevel.Normal));

        // 3rd overrun — escalate
        detector.Update(1.5f);
        Assert.That(detector.CurrentLevel, Is.EqualTo(OverloadLevel.SystemThrottling));
    }

    [Test]
    public void ContinuedOverruns_EscalatesThroughLevels()
    {
        var detector = Create(escalationTicks: 2);

        // Normal → Level 1
        detector.Update(1.5f);
        detector.Update(1.5f);
        Assert.That(detector.CurrentLevel, Is.EqualTo(OverloadLevel.SystemThrottling));

        // Level 1 → Level 2
        detector.Update(1.5f);
        detector.Update(1.5f);
        Assert.That(detector.CurrentLevel, Is.EqualTo(OverloadLevel.ScopeReduction));

        // Level 2 → Level 3 (enters tick rate modulation)
        detector.Update(1.5f);
        detector.Update(1.5f);
        Assert.That(detector.CurrentLevel, Is.EqualTo(OverloadLevel.TickRateModulation));
        Assert.That(detector.TickMultiplier, Is.EqualTo(2)); // Starts at 2x
    }

    [Test]
    public void Level3_IncreasesMultiplier_BeforeLevel4()
    {
        var detector = Create(escalationTicks: 1);

        // Escalate to Level 3
        detector.ForceLevel(OverloadLevel.TickRateModulation, 2);

        // Further overrun → multiplier increases (2→3)
        detector.Update(1.5f);
        Assert.That(detector.CurrentLevel, Is.EqualTo(OverloadLevel.TickRateModulation));
        Assert.That(detector.TickMultiplier, Is.EqualTo(3));

        // 3→4
        detector.Update(1.5f);
        Assert.That(detector.TickMultiplier, Is.EqualTo(4));

        // 4→6
        detector.Update(1.5f);
        Assert.That(detector.TickMultiplier, Is.EqualTo(6));

        // At max multiplier → next overrun escalates to Level 4
        detector.Update(1.5f);
        Assert.That(detector.CurrentLevel, Is.EqualTo(OverloadLevel.PlayerShedding));
    }

    [Test]
    public void Deescalation_AsymmetricHysteresis()
    {
        var detector = Create(escalationTicks: 2, deescalationTicks: 5);

        // Escalate to Level 1
        detector.Update(1.5f);
        detector.Update(1.5f);
        Assert.That(detector.CurrentLevel, Is.EqualTo(OverloadLevel.SystemThrottling));

        // 4 under-run ticks — not enough to de-escalate
        for (var i = 0; i < 4; i++)
        {
            detector.Update(0.3f);
        }

        Assert.That(detector.CurrentLevel, Is.EqualTo(OverloadLevel.SystemThrottling));

        // 5th under-run → de-escalate
        detector.Update(0.3f);
        Assert.That(detector.CurrentLevel, Is.EqualTo(OverloadLevel.Normal));
    }

    [Test]
    public void Deescalation_ReducesMultiplierBeforeLevel()
    {
        var detector = Create(deescalationTicks: 2);

        // Force to Level 3 at 4x multiplier
        detector.ForceLevel(OverloadLevel.TickRateModulation, 4);

        // De-escalate: multiplier drops first (4→3)
        detector.Update(0.3f);
        detector.Update(0.3f);
        Assert.That(detector.CurrentLevel, Is.EqualTo(OverloadLevel.TickRateModulation));
        Assert.That(detector.TickMultiplier, Is.EqualTo(3));

        // 3→2
        detector.Update(0.3f);
        detector.Update(0.3f);
        Assert.That(detector.TickMultiplier, Is.EqualTo(2));

        // 2→1 then drop to Level 2
        detector.Update(0.3f);
        detector.Update(0.3f);
        Assert.That(detector.CurrentLevel, Is.EqualTo(OverloadLevel.ScopeReduction));
        Assert.That(detector.TickMultiplier, Is.EqualTo(1));
    }

    [Test]
    public void MaxLevel_DoesNotExceed()
    {
        var detector = Create(escalationTicks: 1);

        // Force to max
        detector.ForceLevel(OverloadLevel.PlayerShedding);

        // Further overruns don't go beyond
        detector.Update(2.0f);
        Assert.That(detector.CurrentLevel, Is.EqualTo(OverloadLevel.PlayerShedding));
    }

    [Test]
    public void InterruptedOverruns_ResetCounter()
    {
        var detector = Create(escalationTicks: 3);

        // 2 overruns, then 1 under → resets
        detector.Update(1.5f);
        detector.Update(1.5f);
        detector.Update(0.3f); // Under threshold → resets overrun counter
        detector.Update(1.5f);
        detector.Update(1.5f);

        Assert.That(detector.CurrentLevel, Is.EqualTo(OverloadLevel.Normal),
            "Interrupted overruns should not accumulate");
    }

    [Test]
    public void BetweenThresholds_PreservesCounter()
    {
        var detector = Create(escalationTicks: 3);

        // Ratio between deescalation (0.6) and overrun (1.2) → no counter changes
        detector.Update(1.5f); // 1 overrun
        detector.Update(0.9f); // Between thresholds — counter preserved at 1, not reset
        detector.Update(1.5f); // 2 overruns (counter resumes from 1)

        // Not yet at 3 — shouldn't escalate
        Assert.That(detector.CurrentLevel, Is.EqualTo(OverloadLevel.Normal),
            "Between-threshold tick should preserve counter but not increment it");

        // 3rd overrun — now escalate
        detector.Update(1.5f);
        Assert.That(detector.CurrentLevel, Is.EqualTo(OverloadLevel.SystemThrottling));
    }

    [Test]
    public void ForceLevel_OverridesState()
    {
        var detector = Create();

        detector.ForceLevel(OverloadLevel.ScopeReduction);

        Assert.That(detector.CurrentLevel, Is.EqualTo(OverloadLevel.ScopeReduction));
        Assert.That(detector.TickMultiplier, Is.EqualTo(1));
    }
}

// ═══════════════════════════════════════════════════════════════
// System throttling integration tests (real scheduler)
// ═══════════════════════════════════════════════════════════════

[TestFixture]
[NonParallelizable] // timing-sensitive: asserts exact per-tick execution counts against a real scheduler; concurrent CPU load from peer fixtures perturbs tick cadence (fails at LoP=8)
class OverloadThrottleTests : TestBase<OverloadThrottleTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<EcsPosition>();
        dbe.RegisterComponentFromAccessor<EcsVelocity>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    [Test]
    public void TickDivisor_SkipsEveryNthTick()
    {
        using var dbe = SetupEngine();
        var executeCount = 0;
        var ticksSeen = 0;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            dag.CallbackSystem("EveryOther", _ => Interlocked.Increment(ref executeCount),
                tickDivisor: 2); // Every 2nd tick
            dag.CallbackSystem("Counter", _ => Interlocked.Increment(ref ticksSeen));
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        runtime.Start();
        SpinWait.SpinUntil(() => ticksSeen >= 10, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        // With tickDivisor: 2, system runs on even ticks (tick 0, 2, 4, 6, 8) = ~5 out of 10
        Assert.That(executeCount, Is.LessThan(ticksSeen), "TickDivisor should cause some ticks to be skipped");
        Assert.That(executeCount, Is.GreaterThan(0), "System should still execute on some ticks");
    }

    [Test]
    public void Level1_LowPriority_CanShed_IsShed()
    {
        using var dbe = SetupEngine();
        var shedCount = 0;
        var criticalCount = 0;

        // Use very aggressive overload detection so Level 1 triggers fast
        // EscalationTicks=1, OverrunThreshold=0 → any tick triggers Level 1 immediately
        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            // Deliberately slow system to trigger overrun
            dag.CallbackSystem("Critical", _ =>
            {
                Interlocked.Increment(ref criticalCount);
                Thread.SpinWait(100_000); // Force overrun
            }, priority: SystemPriority.Critical);
            dag.CallbackSystem("Shedable", _ => Interlocked.Increment(ref shedCount),
                priority: SystemPriority.Low, canShed: true, after: "Critical");
        }, new RuntimeOptions
        {
            WorkerCount = 1,
            BaseTickRate = 1000, // 1ms target — SpinWait will overrun
            Overload = new OverloadOptions { OverrunThreshold = 0.01f, EscalationTicks = 1 }
        });

        runtime.Start();
        SpinWait.SpinUntil(() => criticalCount >= 10, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        // Critical system should always execute
        Assert.That(criticalCount, Is.GreaterThanOrEqualTo(10));

        // Shed system should have been shed after overload detected (tick 1+)
        // It may execute on tick 0 (before detection runs) but should be shed thereafter
        Assert.That(shedCount, Is.LessThan(criticalCount),
            "Low-priority CanShed system should be shed under overload");
    }

    [Test]
    public void OverloadLevel_RecordedInTelemetry()
    {
        using var dbe = SetupEngine();
        var ticksSeen = 0;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Noop", _ => Interlocked.Increment(ref ticksSeen));
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        runtime.Start();
        SpinWait.SpinUntil(() => ticksSeen >= 3, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        var ring = runtime.Telemetry;
        ref readonly var tick = ref ring.GetTick(ring.NewestTick);

        Assert.That(tick.CurrentLevel, Is.EqualTo(OverloadLevel.Normal),
            "Telemetry should record Normal level under no load");
        Assert.That(tick.TickMultiplier, Is.EqualTo(1),
            "Telemetry should record multiplier 1 at Normal level");
    }

    [Test]
    public void SkippedSystem_SuccessorsStillExecute()
    {
        using var dbe = SetupEngine();
        var aCount = 0;
        var bCount = 0;
        var ticksSeen = 0;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            // A is shed-able, B depends on A
            dag.CallbackSystem("A", _ => Interlocked.Increment(ref aCount),
                priority: SystemPriority.Low, canShed: true, tickDivisor: 2);
            dag.CallbackSystem("B", _ => Interlocked.Increment(ref bCount),
                priority: SystemPriority.Critical, after: "A");
            dag.CallbackSystem("Counter", _ => Interlocked.Increment(ref ticksSeen), after: "B");
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        runtime.Start();
        SpinWait.SpinUntil(() => ticksSeen >= 6, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        // A skips every other tick (tickDivisor: 2)
        Assert.That(aCount, Is.LessThan(ticksSeen), "A should be skipped on some ticks");
        // B should execute every tick regardless — successors are dispatched when predecessors are skipped
        Assert.That(bCount, Is.EqualTo(ticksSeen), "B should execute every tick even when A is skipped");
    }
}
