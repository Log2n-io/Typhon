using NUnit.Framework;
using System;
using System.Collections.Generic;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests.Runtime.Subscriptions.Oracle;

/// <summary>
/// AC-8: after a run of seeded churn, every client's decoded world is the server's projection — at four delivery rates, including one where nearly every
/// frame is skipped.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the subsystem's backbone test.</b> Every other fixture in this directory asserts one mechanism against a scenario its author chose. This one
/// asserts the composition of all of them against orders nobody chose, and it is the only test that would catch a defect living in the SEAM between two
/// correct pieces — which, on the evidence of this phase, is where the defects have actually been.
/// </para>
/// <para>
/// <b>The skip rate is the axis that matters.</b> At 0 % the union path of SUB-03 is never taken, because a session that never falls behind never has a
/// baseline to catch up from. At 90 % almost every frame finds both slots full and the next one delivered has to carry every enter, leave and group change
/// since that session's baseline — a netId released and re-leased inside the window included, which is the collision D1's quarantine exists to prevent.
/// </para>
/// <para>
/// <b>What the gate runs, and what it does not.</b> [09 § 6](../../../../../claude/design/Subscriptions/09-phase1-build-plan.md) asks for 200 ticks per
/// skip rate here and a 10 000-tick × 20-seed sweep nightly. The nightly half is not built: there is no nightly harness for this subsystem yet, and saying
/// so is better than implying the coverage exists.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
sealed class DifferentialOracleTests : TestBase<DifferentialOracleTests>
{
    /// <summary>How many churned ticks a gate case runs: the plan's number, and long enough for identities to be released and re-leased many times.</summary>
    private const int GateTicks = 200;

    /// <summary>How often the run stops to compare. Comparing only at the end would report the divergence hundreds of ticks after it happened.</summary>
    private const int CompareEvery = 50;

    /// <summary>How many ticks the byte-determinism comparison runs. Shorter than a gate case: it runs the world twice and keeps every frame.</summary>
    private const int DeterminismTicks = 60;

    /// <summary>
    /// The same gate case with each cluster's records encoded ONCE and referenced by every session watching it (17 § 18).
    /// </summary>
    /// <param name="skipPercent">The percentage of ticks on which the session's frames are left undrained.</param>
    /// <remarks>
    /// <para>
    /// <b>This is the test that decides whether the feature may ship</b>, because what it changes is not a cost but WHICH BYTES a client receives. A shared
    /// run is referenced on the strength of four O(1) predicates — the run is this tick's, no identity has moved, the session still reaches every slot it
    /// names, and it has already been told about all of them — and every one of those is a claim about state some other session, some other stage or some
    /// earlier tick wrote. The oracle is the only thing here that would catch one of them being subtly false: it decodes each client's world with the real
    /// decoder and compares it against the server's projection, so a session told about an entity it cannot see, or not told about one it can, diverges.
    /// </para>
    /// <para>
    /// <b>The skip rates matter more here than anywhere else.</b> A session that falls behind may NOT reference a run — the mask a run carries names the
    /// groups that changed in one tick, and a session further back is owed the union since its baseline. At a 90 % skip rate almost every frame is in that
    /// state, so this arm is mostly a test that the feature correctly refuses itself.
    /// </para>
    /// </remarks>
    [Test]
    [VerifiesRule("SUB-03")]
    public void AClientsWorldIsTheServersWithSharedClusterRuns([Values(0, 30, 60, 90)] int skipPercent)
    {
        using var oracle = OracleHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), seed: 20260920 + skipPercent, [skipPercent],
            nameof(DifferentialOracleTests), sharedClusterBlocks: true);

        for (var i = 0; i < GateTicks; i++)
        {
            oracle.Step();
            if ((i + 1) % CompareEvery == 0)
            {
                oracle.Quiesce();
                oracle.AssertConverged($"after {i + 1} churned ticks at a {skipPercent}% skip rate, sharing cluster runs");
            }
        }

        oracle.Quiesce();
        oracle.AssertConverged($"at the end of a {GateTicks}-tick run at a {skipPercent}% skip rate, sharing cluster runs");

        Assert.That(oracle.Workload.Destroyed, Is.GreaterThan(10), "the workload destroyed too little to put any pressure on identity");
        AssertLookedAtSomething(oracle);

        // Vacuous otherwise. A run that shared nothing would converge for the same reason the arm above does, and would say nothing at all about the
        // feature; the whole point of this fixture is that the bytes a client received came through the shared path.
        if (skipPercent == 0)
        {
            Assert.That(oracle.SharedRunUse.Runs, Is.GreaterThan(0), "no cluster run was ever referenced, so this arm tested nothing");
            Assert.That(oracle.SharedRunUse.Records, Is.GreaterThan(0), "the referenced runs carried no records");
        }
    }

    /// <summary>
    /// The gate case: one seeded run per skip rate, compared every 50 ticks and at the end.
    /// </summary>
    /// <param name="skipPercent">The percentage of ticks on which the session's frames are left undrained.</param>
    [Test]
    [VerifiesRule("SUB-03")]
    public void AClientsWorldIsTheServersAfterSeededChurn([Values(0, 30, 60, 90)] int skipPercent)
    {
        using var oracle = OracleHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), seed: 20260918 + skipPercent, [skipPercent],
            nameof(DifferentialOracleTests));

        for (var i = 0; i < GateTicks; i++)
        {
            oracle.Step();
            if ((i + 1) % CompareEvery == 0)
            {
                oracle.Quiesce();
                oracle.AssertConverged($"after {i + 1} churned ticks at a {skipPercent}% skip rate");
            }
        }

        oracle.Quiesce();
        oracle.AssertConverged($"at the end of a {GateTicks}-tick run at a {skipPercent}% skip rate");

        // The scenario has to have actually exercised what it claims to. Identity churn is what makes the run interesting; without destroys there is no
        // netId to reuse, and the whole quarantine question never arises.
        Assert.That(oracle.Workload.Destroyed, Is.GreaterThan(10), "the workload destroyed too little to put any pressure on identity");
        Assert.That(oracle.Workload.Teleports, Is.GreaterThan(10), "the workload never forced a motion epoch change");
        AssertLookedAtSomething(oracle);
    }

    /// <summary>
    /// A session that is skipped nearly every tick really is skipped, and still converges.
    /// </summary>
    /// <remarks>
    /// Without this the 90 % case above could pass while the engine never skipped at all — if K were larger than the run ever filled, or if the producer had
    /// silently stopped policing the outstanding count, every frame would simply be delivered late and the union path would go untested. Asserting the skip
    /// counter is what makes the parameterization mean something.
    /// </remarks>
    [Test]
    public void AHeavilySkippedSessionActuallySkipsAndStillConverges()
    {
        using var oracle = OracleHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), seed: 4242, [90], nameof(DifferentialOracleTests));

        for (var i = 0; i < GateTicks; i++)
        {
            oracle.Step();
        }

        Assert.That(oracle.FramesSkipped, Is.GreaterThan(0), "a session skipped nine ticks in ten never filled its slots, so nothing under test was exercised");

        oracle.Quiesce();
        oracle.AssertConverged("a session that spent the run behind catches up once its slots drain");
        AssertLookedAtSomething(oracle);
    }

    /// <summary>
    /// Two sessions at opposite delivery rates converge on the same world, from the same ticks.
    /// </summary>
    /// <remarks>
    /// The per-session case that a single-session run cannot reach: the frame assembler keeps one baseline and one known-set per session, and a bug that
    /// leaked either between sessions — a shared scratch list, a known-set indexed by slot rather than by identity — shows here and nowhere else. The two
    /// sessions are in the same partition, running in the same stage, on the same ticks.
    /// </remarks>
    [Test]
    public void SessionsAtDifferentDeliveryRatesConvergeOnTheSameWorld()
    {
        using var oracle = OracleHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), seed: 777, [0, 85], nameof(DifferentialOracleTests));

        for (var i = 0; i < GateTicks; i++)
        {
            oracle.Step();
        }

        oracle.Quiesce();
        oracle.AssertConverged("two sessions at different delivery rates hold the same world once both are caught up");
        AssertLookedAtSomething(oracle);
    }

    /// <summary>
    /// The oracle fails when the engine is wrong: with SUB-03's baseline rule broken, a skipped session's world diverges.
    /// </summary>
    /// <remarks>
    /// <b>An oracle that has never failed is a decoration.</b> The mutant is the one move SUB-03 forbids — a baseline advanced by a tick that produced no
    /// frame — and it is flipped on the production assembler, not on a copy. Without this test, nothing distinguishes "the worlds agree" from "the
    /// comparison never looked at anything", and the comparison is the only thing the whole fixture rests on.
    /// </remarks>
    [Test]
    [RuleMutant("SUB-03")]
    public void TheOracleDetectsABaselineThatAdvancesOnASkippedTick()
    {
        using var oracle = OracleHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), seed: 31337, [70], nameof(DifferentialOracleTests));
        oracle.Assembler.BaselineAdvancesOnSkipForTest = true;

        RuleMutants.AssertDetects("SUB-03", "the client's world is not the server's", () =>
        {
            for (var i = 0; i < GateTicks; i++)
            {
                oracle.Step();
            }

            oracle.Quiesce();
            oracle.AssertConverged("a baseline that advanced on a skipped tick loses the records that tick would have carried");
        });
    }

    /// <summary>
    /// Two sessions on the same profile, drained on the same ticks, receive byte-identical frames.
    /// </summary>
    /// <param name="workers">The worker-pool width.</param>
    /// <remarks>
    /// <para>
    /// This is the premise AC-1 and SUB-07 rest on: 110 identical <c>World</c> sessions are supposed to cost one encode and a copy each, and that is only
    /// sound if two sessions in the same state really do produce the same bytes. Nothing asserted it — every other fixture compares decoded MEANING, and
    /// meaning is exactly what survives a byte-level order dependence, such as a record list sorted by something that is only usually the netId or a value
    /// taken from whichever worker reached a block first.
    /// </para>
    /// <para>
    /// It is a weaker statement than run-to-run reproducibility, which needs two engines and is covered elsewhere; it is the half that can be asserted
    /// inside one world, and it is the half that shared frames will depend on directly when P1-15 lands.
    /// </para>
    /// </remarks>
    [Test]
    public void TwoIdenticalSessionsReceiveTheSameBytes([Values(1, 4)] int workers)
    {
        using var oracle = OracleHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), seed: 606, [0, 0], nameof(DifferentialOracleTests));

        var compared = 0;
        for (var i = 0; i < DeterminismTicks; i++)
        {
            var perSession = oracle.StepCollecting(workers);
            Assert.That(perSession[1].Count, Is.EqualTo(perSession[0].Count), $"tick {i}: the two sessions were sent a different number of frames");
            for (var frame = 0; frame < perSession[0].Count; frame++)
            {
                Assert.That(perSession[1][frame], Is.EqualTo(perSession[0][frame]),
                    $"tick {i}: two sessions on the same profile and the same baseline were sent different bytes at {workers} worker(s)");
                compared++;
            }
        }

        Assert.That(compared, Is.GreaterThan(20), $"only {compared} frame pairs were compared, so the run proves very little");
    }

    /// <summary>
    /// Asserts that the comparison had something to compare.
    /// </summary>
    /// <param name="oracle">The oracle that has just run.</param>
    /// <remarks>
    /// Every convergence assertion here passes trivially against an empty server-truth set, and an empty set has no other symptom — the run is green, fast
    /// and meaningless. An absolute floor would catch only the empty case: a walk that lost most of the world still clears any small constant. So the check
    /// is against the engine's OWN live entity count, which the last comparison point must have matched one for one, per session — a walk that dropped a
    /// single cluster fails it.
    /// </remarks>
    private static void AssertLookedAtSomething(OracleHarness oracle)
    {
        var live = oracle.LiveEntityCount;
        Assert.That(live, Is.GreaterThan(20), $"the world held only {live} entities, so the run proves very little whatever the oracle compared");
        Assert.That(oracle.ComparedAtLastPoint, Is.EqualTo((long)live * oracle.Sessions.Length),
            $"the last comparison covered {oracle.ComparedAtLastPoint} entities but the engine holds {live} across {oracle.Sessions.Length} session(s) — "
            + "the oracle's view of the server's world is not the server's world");
    }
}
