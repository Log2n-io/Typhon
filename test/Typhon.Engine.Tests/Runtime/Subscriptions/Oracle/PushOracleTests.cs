using NUnit.Framework;

namespace Typhon.Engine.Tests.Runtime.Subscriptions.Oracle;

/// <summary>
/// PROTOTYPE (push replication): the differential oracle's gate case, served by the push path in both change-detection modes.
/// </summary>
/// <remarks>
/// <para>
/// The same seeded churn, the same decoded clients and the same comparison against the ECS as <see cref="DifferentialOracleTests"/>: spawns, destroys,
/// sub-tolerance drifts, teleports through <c>WriteSpatial</c> that really migrate, and writes to both change groups through <c>GetSpan</c>. What differs is
/// who decides an entity changed. In <see cref="PushDetection.Automatic"/> the engine compares every live entity; in <see cref="PushDetection.Explicit"/> the
/// workload pushes each slot it writes, and the engine adds spawns, destroys, spatial writes and migrations itself.
/// </para>
/// <para>
/// The sessions sit at the origin with a disc that covers the world, so the truth set is every live entity and the comparison is exactly the pull oracle's.
/// A skipped session is reset rather than caught up (the prototype has no push log), and converging after one is the claim the skip rates test.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
sealed class PushOracleTests : TestBase<PushOracleTests>
{
    private const int GateTicks = 200;
    private const int CompareEvery = 50;

    /// <summary>A client's world is the server's after seeded churn, in both detection modes and at four delivery rates.</summary>
    /// <param name="detection">Who signals a change.</param>
    /// <param name="skipPercent">The percentage of ticks on which the session's frames are left undrained.</param>
    [Test]
    public void AClientsWorldIsTheServersUnderPush(
        [Values(PushDetection.Explicit, PushDetection.Automatic)] PushDetection detection,
        [Values(0, 30, 60, 90)] int skipPercent)
    {
        using var oracle = OracleHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), seed: 20260923 + skipPercent, [skipPercent],
            nameof(PushOracleTests), push: detection);

        for (var i = 0; i < GateTicks; i++)
        {
            oracle.Step();
            if ((i + 1) % CompareEvery == 0)
            {
                oracle.Quiesce();
                oracle.AssertConverged($"{detection} push, after {i + 1} churned ticks at a {skipPercent}% skip rate");
            }
        }

        oracle.Quiesce();
        oracle.AssertConverged($"{detection} push, at the end of a {GateTicks}-tick run at a {skipPercent}% skip rate");

        Assert.That(oracle.Workload.Destroyed, Is.GreaterThan(10), "the workload destroyed too little to put any pressure on identity");
        Assert.That(oracle.Workload.Teleports, Is.GreaterThan(10), "the workload never forced a motion epoch change");
        if (detection == PushDetection.Explicit)
        {
            Assert.That(oracle.Workload.Pushed, Is.GreaterThan(20), "the workload pushed too little for explicit detection to have been exercised");
        }

        AssertLookedAtSomething(oracle);
    }

    /// <summary>The same gate case through a push <c>World</c> observer: cells delivered in grid order behind one cursor, then every event.</summary>
    /// <param name="detection">Who signals a change.</param>
    /// <param name="skipPercent">The percentage of ticks on which the session's frames are left undrained.</param>
    [Test]
    public void AClientsWorldIsTheServersUnderAPushWorldObserver(
        [Values(PushDetection.Explicit, PushDetection.Automatic)] PushDetection detection,
        [Values(0, 30, 60, 90)] int skipPercent)
    {
        using var oracle = OracleHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), seed: 20260924 + skipPercent, [skipPercent, 0],
            nameof(PushOracleTests), push: detection, worldObserver: true);

        for (var i = 0; i < GateTicks; i++)
        {
            oracle.Step();
            if ((i + 1) % CompareEvery == 0)
            {
                oracle.Quiesce();
                oracle.AssertConverged($"{detection} push World, after {i + 1} churned ticks at a {skipPercent}% skip rate");
            }
        }

        Assert.That(oracle.Workload.Destroyed, Is.GreaterThan(10), "the workload destroyed too little to put any pressure on identity");
        if (skipPercent >= 60)
        {
            Assert.That(oracle.Push.LogCatchUps, Is.GreaterThan(0), "no skipped frame was replayed from the log");
        }

        AssertLookedAtSomething(oracle);
    }

    /// <summary>A profile served one tick in two or four converges, every frame carrying the union since the session's last one (the push log).</summary>
    /// <param name="every">The profile's tick divisor.</param>
    /// <param name="world">A World observer rather than a covering disc.</param>
    [Test]
    public void AProfileServedEveryFewTicksConverges([Values(2, 4)] int every, [Values(false, true)] bool world)
    {
        using var oracle = OracleHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), seed: 5150 + every, [0, 30, 0], nameof(PushOracleTests),
            push: PushDetection.Explicit, worldObserver: world, every: every);

        for (var i = 0; i < GateTicks; i++)
        {
            oracle.Step();
            if ((i + 1) % CompareEvery == 0)
            {
                oracle.Quiesce();
                oracle.AssertConverged($"a profile served every {every} ticks, after {i + 1} churned ticks");
            }
        }

        Assert.That(oracle.Push.LogCatchUps, Is.GreaterThan(GateTicks / every), "the rate class never replayed the log");
        AssertLookedAtSomething(oracle);
    }

    /// <summary>
    /// Distance LOD: updates to far entities deferred and flushed every few ticks as a union — walking sessions still hold exactly their disc, with the
    /// right values, once the world is quiet (the quiet window outlasts the flush period).
    /// </summary>
    /// <param name="detection">Who signals a change.</param>
    /// <param name="skipPercent">The percentage of ticks on which each session's frames are left undrained.</param>
    [Test]
    public void DeferredFarUpdatesStillConverge(
        [Values(PushDetection.Explicit, PushDetection.Automatic)] PushDetection detection,
        [Values(0, 30)] int skipPercent)
    {
        using var oracle = OracleHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), seed: 7300 + skipPercent, [skipPercent, 0, 30, 0],
            nameof(PushOracleTests), push: detection, walkRadius: 3000);
        oracle.Push.FarEvery = 4;

        for (var i = 0; i < GateTicks; i++)
        {
            oracle.Step();
            if ((i + 1) % CompareEvery == 0)
            {
                oracle.Quiesce();
                oracle.AssertConverged($"{detection} push with far updates every 4 ticks, after {i + 1} ticks at a {skipPercent}% skip rate");
            }
        }

        Assert.That(oracle.Push.UpdatesDeferred, Is.GreaterThan(20), "no far update was ever deferred, so the LOD was not exercised");
        Assert.That(oracle.Push.FarFlushes, Is.GreaterThan(20), "the deferred updates were never flushed");
    }

    /// <summary>Automatic detection is off unless the runtime is told otherwise (ADR-067): replication is explicit.</summary>
    [Test]
    public void AutomaticDetectionIsRefusedUnlessEnabled()
    {
        var engine = ProjectionTestSchema.SetupEngine(ServiceProvider);
        try
        {
            var refused = Assert.Throws<System.NotSupportedException>(() => FrameHarness.Create(engine, subs =>
            {
                ProjectionTestSchema.DeclareCreature(subs);
                subs.Profile("auto", p => p.Push(PushDetection.Automatic).Sphere(100).Of<ProjCreature>());
            }, nameof(AutomaticDetectionIsRefusedUnlessEnabled)));
            Assert.That(refused.Message, Does.Contain("AllowAutomaticPushDetection"));
        }
        finally
        {
            engine.Dispose();
        }
    }

    /// <summary>Two sessions at opposite delivery rates converge on the same world.</summary>
    [Test]
    public void SessionsAtDifferentDeliveryRatesConvergeUnderPush([Values(PushDetection.Explicit, PushDetection.Automatic)] PushDetection detection)
    {
        using var oracle = OracleHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), seed: 778, [0, 85], nameof(PushOracleTests),
            push: detection);

        for (var i = 0; i < GateTicks; i++)
        {
            oracle.Step();
        }

        oracle.Quiesce();
        oracle.AssertConverged($"{detection} push: two sessions at different delivery rates hold the same world once both are caught up");
        AssertLookedAtSomething(oracle);
    }

    /// <summary>
    /// Walking sessions with a disc a fraction of the world's size hold exactly what their disc names, through strides that move the anchor, teleports
    /// that reset it, identity churn and skipped frames.
    /// </summary>
    /// <param name="detection">Who signals a change.</param>
    /// <param name="skipPercent">The percentage of ticks on which each session's frames are left undrained.</param>
    /// <remarks>
    /// The whole-world cases above never test the known-set's boundary: every entity is inside every disc. Here the geometry is the whole claim — the
    /// crescent sweep as the anchor moves, the cell delivery after a teleport, and the push step's enter and leave as entities cross a disc's edge. An
    /// entity well inside a disc must be held and one well outside must not; the band between is the anchor's slack plus the motion tolerance.
    /// </remarks>
    [Test]
    public void WalkingSessionsHoldExactlyWhatTheirDiscNames(
        [Values(PushDetection.Explicit, PushDetection.Automatic)] PushDetection detection,
        [Values(0, 60)] int skipPercent)
    {
        using var oracle = OracleHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), seed: 9100 + skipPercent, [skipPercent, 0, 30, skipPercent],
            nameof(PushOracleTests), push: detection, walkRadius: 3000);

        var required = 0L;
        for (var i = 0; i < GateTicks; i++)
        {
            oracle.Step();
            if ((i + 1) % CompareEvery == 0)
            {
                oracle.Quiesce();
                oracle.AssertConverged($"{detection} push, walking sessions, after {i + 1} ticks at a {skipPercent}% skip rate");
                oracle.AssertWritesSurvived($"{detection} push, walking sessions, after {i + 1} ticks");
                required += oracle.RequiredAtLastPoint;
            }
        }

        Assert.That(required, Is.GreaterThan(200), "the discs held too little for the comparison to mean anything");
        Assert.That(oracle.ViewpointTeleports, Is.GreaterThan(5), "no session teleported, so the reset path was not exercised");
        Assert.That(oracle.Push.Sweeps, Is.GreaterThan(50), "the anchors barely moved, so the crescent sweep was not exercised");
    }

    /// <summary>
    /// The contract explicit detection rests on, shown failing: a system that writes and does not push leaves its clients stale, and the oracle sees it.
    /// </summary>
    /// <remarks>
    /// This is not a defect of the engine — it is the developer contract <see cref="PushDetection.Explicit"/> accepts, and the reason
    /// <see cref="PushDetection.Automatic"/> exists. It is here so that the explicit arm above cannot pass because the oracle is blind to missing pushes.
    /// </remarks>
    [Test]
    public void AForgottenPushLeavesTheClientStaleAndTheOracleSeesIt()
    {
        using var oracle = OracleHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), seed: 4243, [0], nameof(PushOracleTests),
            push: PushDetection.Explicit);
        oracle.Workload.ForgetPushesAfter = 0;

        RuleMutants.AssertDetects("SUB-10", "on the client and", () =>
        {
            for (var i = 0; i < GateTicks; i++)
            {
                oracle.Step();
            }

            // Churn alone may heal a forgotten write — a later teleport pushes the entity whole — so the run ends with one no entity recovers from.
            oracle.Workload.WriteModeOnEveryCreature();
            oracle.Quiesce();
            oracle.AssertConverged("writes that were never pushed");
        });
    }

    /// <summary>
    /// The forgotten-push validator finds the writes a system did not push, and — sweeping every cluster — heals them, so the client converges anyway.
    /// </summary>
    [Test]
    public void TheValidatorFindsAndHealsForgottenPushes()
    {
        using var oracle = OracleHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), seed: 4244, [0], nameof(PushOracleTests),
            push: PushDetection.Explicit);
        oracle.Workload.ForgetPushesAfter = 0;
        oracle.Push.ValidateClustersPerTick = int.MaxValue;

        for (var i = 0; i < GateTicks; i++)
        {
            oracle.Step();
        }

        oracle.Quiesce();
        oracle.AssertConverged("writes that were never pushed, found by a validator that visits every cluster every tick");
        Assert.That(oracle.Push.ForgottenPushes, Is.GreaterThan(20), "the workload wrote without pushing, and the validator should have seen it");
        AssertLookedAtSomething(oracle);
    }

    /// <summary>A workload that pushes everything it writes gives the validator nothing to find.</summary>
    [Test]
    public void TheValidatorIsSilentWhenEveryWriteIsPushed()
    {
        using var oracle = OracleHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), seed: 4245, [0], nameof(PushOracleTests),
            push: PushDetection.Explicit);
        oracle.Push.ValidateClustersPerTick = int.MaxValue;

        for (var i = 0; i < GateTicks; i++)
        {
            oracle.Step();
        }

        Assert.That(oracle.Push.ValidatedSlots, Is.GreaterThan(1000), "the validator checked too little to mean anything");
        Assert.That(oracle.Push.ForgottenPushes, Is.Zero, "every write was pushed, so anything the validator reports is a false positive");
    }

    private static void AssertLookedAtSomething(OracleHarness oracle)
    {
        var live = oracle.LiveEntityCount;
        Assert.That(live, Is.GreaterThan(20), $"the world held only {live} entities, so the run proves very little whatever the oracle compared");
        Assert.That(oracle.ComparedAtLastPoint, Is.EqualTo((long)live * oracle.Sessions.Length),
            $"the last comparison covered {oracle.ComparedAtLastPoint} entities but the engine holds {live} across {oracle.Sessions.Length} session(s)");
    }
}
