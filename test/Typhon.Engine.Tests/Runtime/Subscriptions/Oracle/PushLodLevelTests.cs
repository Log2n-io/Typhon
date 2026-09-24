using NUnit.Framework;
using System;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests.Runtime.Subscriptions.Oracle;

/// <summary>
/// The per-session LOD level and its budget loop (09 § 10): a level scales every band's period, gives a bandless profile a band beyond half its radius, and
/// moves with the frame; the loop moves it from the bytes the frames publish.
/// </summary>
[TestFixture]
[NonParallelizable]
sealed class PushLodLevelTests : TestBase<PushLodLevelTests>
{
    /// <summary>
    /// Levels set at random every few ticks — up, down, several steps at once — on walking sessions, with and without declared bands, at three skip rates:
    /// every client still converges. A fall is the hard case: a band's next flush at the shorter period must still carry what the longer one held back.
    /// </summary>
    /// <param name="skipPercent">The percentage of ticks on which some sessions' frames are left undrained.</param>
    /// <param name="banded">Whether the profile declares bands, or gets the implicit one.</param>
    [Test]
    [VerifiesRule("SUB-22")]
    public void LevelChangesConvergeAtEverySkipRate([Values(0, 30, 60)] int skipPercent, [Values] bool banded) => LevelChangesConverge(skipPercent, banded, false);

    /// <summary><see cref="LevelChangesConvergeAtEverySkipRate"/> on the deep implementation (10 § 3.5), which must agree with the flat one.</summary>
    [Test]
    [VerifiesRule("SUB-22")]
    public void LevelChangesConvergeOnTheDeepImplementation() => LevelChangesConverge(30, true, true);

    private void LevelChangesConverge(int skipPercent, bool banded, bool deep)
    {
        using var oracle = OracleHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), seed: 7700 + skipPercent, [skipPercent, 0, 30, skipPercent],
            nameof(PushLodLevelTests), walkRadius: 3000, forceDeep: deep, bands: banded ? b => b.Every(2, beyond: 0.4).Every(4, beyond: 0.7) : null);
        var declaredWindow = oracle.Push.FarWindow;
        oracle.Workload.WalkStrideM = 1.5f;
        oracle.Push.LevelsPinned = true;
        var rng = new Random(skipPercent + (banded ? 1 : 0));
        var levels = new int[oracle.Sessions.Length];
        int rises = 0, falls = 0, widerFolds = 0;

        for (var i = 0; i < 200; i++)
        {
            if (i % 7 == 0)
            {
                foreach (var session in oracle.Sessions)
                {
                    oracle.Push.SetTargetLevel(session, rng.Next(0, PushReplication.MaxLevel + 1));
                }
            }

            oracle.Step();
            widerFolds += oracle.Push.FarWindow > declaredWindow ? 1 : 0;
            for (var s = 0; s < levels.Length; s++)
            {
                var level = oracle.Push.LevelOf(oracle.Sessions[s]);
                rises += level > levels[s] ? 1 : 0;
                falls += level < levels[s] ? 1 : 0;
                levels[s] = level;
            }

            if ((i + 1) % 50 == 0)
            {
                oracle.Quiesce();
                oracle.AssertConverged($"levels moving, {(banded ? "declared bands" : "the implicit band")}, after {i + 1} ticks at {skipPercent}% skipped");
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(rises, Is.GreaterThan(10), "levels rarely rose: the case did not run");
            Assert.That(falls, Is.GreaterThan(10), "levels rarely fell: the case did not run");
            Assert.That(widerFolds, Is.GreaterThan(20), "the fold never went past the declared window: the levels were not what deferred");
            Assert.That(oracle.Push.UpdatesDeferred, Is.GreaterThan(20), "the levels deferred nothing");
            Assert.That(oracle.Push.FarFlushes, Is.GreaterThan(20), "the deferred updates were never flushed");
        });
    }

    /// <summary>
    /// End to end: a budget no frame can meet raises every session's level one step a second, up to the last, and the clients still converge; removing it
    /// brings them back to level 0.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-22")]
    public void ABudgetRaisesTheLevelAndRemovingItLowersIt()
    {
        using var oracle = OracleHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), seed: 7800, [0, 30], nameof(PushLodLevelTests),
            walkRadius: 3000);
        oracle.Workload.WalkStrideM = 1.5f;
        foreach (var session in oracle.Sessions)
        {
            Assert.That(oracle.Frames.Sessions.SetBudget(session, 1), Is.True);
        }

        for (var i = 0; i < 200; i++)
        {
            oracle.Step();
        }

        Assert.That((oracle.Push.FarPhase, oracle.Push.FarWindow), Is.EqualTo((8, 8)), "every session at level 3 with no band: every 8 ticks beyond R/2");

        oracle.Quiesce();
        oracle.AssertConverged("under a budget no frame meets");
        Assert.Multiple(() =>
        {
            foreach (var session in oracle.Sessions)
            {
                Assert.That(oracle.Push.LevelOf(session), Is.EqualTo(PushReplication.MaxLevel), $"session {session.Value}: a level a second over the budget");
            }

            Assert.That(oracle.Push.UpdatesDeferred, Is.GreaterThan(100), "the implicit band deferred almost nothing");
        });

        foreach (var session in oracle.Sessions)
        {
            oracle.Frames.Sessions.SetBudget(session, 0);
        }

        for (var i = 0; i < 20; i++)
        {
            oracle.Step();
        }

        oracle.Quiesce();
        oracle.AssertConverged("after the budget was removed");
        foreach (var session in oracle.Sessions)
        {
            Assert.That(oracle.Push.LevelOf(session), Is.Zero, $"session {session.Value}: no budget, no level");
        }
    }

    /// <summary>
    /// The loop's timing at 50 Hz: at twice the budget the level rises once the EWMA has been over it for a second — not before — and again each second
    /// after, up to <see cref="PushReplication.MaxLevel"/>; silent, it falls once the EWMA has been under 70 % for three seconds; no budget is level 0.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-22")]
    public void TheBudgetLoopRisesAfterASecondOverAndFallsAfterThreeUnder()
    {
        using var oracle = OracleHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), seed: 7900, [0], nameof(PushLodLevelTests));
        oracle.Step();
        var push = oracle.Push;
        var session = oracle.Sessions[0];
        const double tick = 0.02;
        const int budget = 10_000;
        const int twice = 2 * budget / 50;

        // The EWMA (τ = 0.5 s) crosses the budget after 18 ticks; the level rises 50 ticks later (67), and each 50 after while still over.
        Feed(60, twice);
        Assert.That(push.TargetLevelOf(session), Is.Zero, "a second over the budget has not passed");
        Feed(10, twice);
        Assert.That(push.TargetLevelOf(session), Is.EqualTo(1));
        Feed(40, twice);
        Assert.That(push.TargetLevelOf(session), Is.EqualTo(1), "a rise restarts the clock: the next is a second later, not sooner");
        Feed(10, twice);
        Assert.That(push.TargetLevelOf(session), Is.EqualTo(2));
        Feed(50, twice);
        Assert.That(push.TargetLevelOf(session), Is.EqualTo(3), "a level a second while still over");
        Feed(100, twice);
        Assert.That(push.TargetLevelOf(session), Is.EqualTo(PushReplication.MaxLevel), "no level past the last");

        // Silent: under 7 000 B/s after 27 ticks, and three seconds (150 ticks) later a level falls.
        Feed(170, 0);
        Assert.That(push.TargetLevelOf(session), Is.EqualTo(3), "three seconds under the lower mark have not passed");
        Feed(15, 0);
        Assert.That(push.TargetLevelOf(session), Is.EqualTo(2));

        push.Pace(session, 0, 0, tick);
        Assert.That(push.TargetLevelOf(session), Is.Zero, "no budget");

        // Ticks the session was not served (a rate class, a refused frame) count as time: one feed after three ticks weighs three ticks.
        push.Pace(session, 0, budget, tick);
        push.LevelsPinned = true;
        for (var i = 0; i < 3; i++)
        {
            oracle.Step();
        }

        push.LevelsPinned = false;
        push.Pace(session, 3 * twice, budget, tick);
        var expected = (1 - Math.Exp(-3 * tick / PushReplication.RateTauSeconds)) * (3 * twice / (3 * tick));
        Assert.That(push.RateOf(session), Is.EqualTo(expected).Within(0.01).Percent, "an EWMA step over three ticks");

        void Feed(int ticks, int bytes)
        {
            for (var i = 0; i < ticks; i++)
            {
                push.Pace(session, bytes, budget, tick);
            }
        }
    }

    /// <summary>
    /// The census follows the sessions: while one is at a level the fold runs at that level's phase over the whole log, and once it closes the fold goes
    /// back to the declared one — at the next recount, not when its slot is reused.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-22")]
    public void AClosedSessionAtALevelReleasesTheFold()
    {
        using var oracle = OracleHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), seed: 7950, [0, 0], nameof(PushLodLevelTests),
            walkRadius: 3000);
        oracle.Push.LevelsPinned = true;
        var leaving = oracle.Sessions[1];
        oracle.Push.SetTargetLevel(leaving, 2);
        for (var i = 0; i < 3; i++)
        {
            oracle.Step();
        }

        Assert.That((oracle.Push.FarPhase, oracle.Push.FarWindow), Is.EqualTo((4, PushReplication.LogDepth)), "one session at level 2");

        Assert.That(oracle.Frames.Sessions.Close(leaving, SessionCloseReason.Kicked, 4100), Is.True);
        for (var i = 0; i < PushReplication.RecountEvery + 2; i++)
        {
            oracle.Step();
        }

        Assert.That((oracle.Push.FarPhase, oracle.Push.FarWindow), Is.EqualTo((0, 0)), "the census still counts a closed session");
    }
}
