using NUnit.Framework;
using System;
using System.Numerics;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime.Subscriptions;

/// <summary>
/// AC-21 and Q-M5: how often a moving entity costs a motion segment, under the adopted trigger and under the one that was rejected.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a fixture rather than a recorded run.</b> AC-21 asks for segments per moving entity per second on a recorded SWG session,
/// which is a number nobody can re-derive later without the recording. The property it is really asserting — that the adopted trigger does
/// not emit a segment every tick — is a ratio, and a ratio can be measured from a world this fixture builds and anybody can re-run.
/// </para>
/// <para>
/// <b>Both triggers, from one run.</b> <c>SegmentsEmitted</c> counts what the adopted trigger sent; <c>ShadowSegmentsEmitted</c> counts what
/// the rejected "the quantized velocity changed" trigger would have sent over the same motion, computed alongside with no second pass and no
/// per-entity state of its own. Q-M5 asks for the pair, so the pair is what this reports.
/// </para>
/// <para>
/// <b>The motion is smooth on purpose.</b> A segment exists so a client can extrapolate, and what decides whether one is needed is whether
/// the extrapolation has drifted past the declared tolerance — so an entity teleporting randomly each tick would measure the teleport path
/// and say nothing about the trigger. These creatures move at a constant speed along a straight line, which is the case the trigger should
/// cost almost nothing for, and then turn, which is the case it must pay for.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
unsafe class MotionSegmentRateTests : TestBase<MotionSegmentRateTests>
{
    private const string Profile = "god-world";

    private const int Creatures = 64;

    /// <summary>Ticks per second the rate is expressed in, matching the harness's own tick period.</summary>
    private const double TickHz = 10d;

    /// <summary>How long the run is, in ticks. Six hundred at 10 Hz is a minute of motion.</summary>
    private const int Ticks = 600;

    /// <summary>Metres per second, a walking creature.</summary>
    private const double SpeedMps = 4d;

    /// <summary>AC-21's bound: segments per moving entity per second.</summary>
    private const double Bound = 1.5d;

    /// <summary>What the adopted trigger measured on this workload when the fixture was written (2026-09-18).</summary>
    private const double MeasuredRate = 0.233d;

    /// <summary>The regression guard, at roughly three times the measured rate — tight enough to notice a real change, loose enough for a different box.</summary>
    private const double RegressionBound = 0.5d;

    private static void Declare(SubscriptionsRegistry subs)
    {
        ProjectionTestSchema.DeclareCreature(subs);
        subs.Profile(Profile, p => p.World().Of<ProjCreature>());
    }

    /// <summary>
    /// A minute of smooth motion costs well under AC-21's segment budget, and the rejected trigger costs more.
    /// </summary>
    /// <remarks>
    /// The comparison against the shadow is the part that carries an argument rather than a number: a bound alone is met by a trigger that
    /// emits nothing and lets every client drift, whereas the shadow says what the alternative would have cost over the same motion.
    /// </remarks>
    [Test]
    public void SmoothMotionCostsFarFewerSegmentsThanTheRejectedTrigger()
    {
        using var harness = FrameHarness.Create(
            ProjectionTestSchema.SetupEngine(ServiceProvider),
            Declare,
            nameof(SmoothMotionCostsFarFewerSegmentsThanTheRejectedTrigger));

        Populate(harness);
        var session = harness.OpenSessions(1, Profile)[0];

        // Every tick's WriteSpatial is a push the engine makes itself, and it rides the structure marks the fence publishes.
        harness.RunFence = true;
        harness.RunTick(1);
        harness.Deliver(session);

        var creatures = harness.PlanIndex(nameof(ProjCreature));
        var state = harness.Subscriptions.ReplicationStates[creatures];

        for (var tick = 2L; tick <= Ticks + 1; tick++)
        {
            Advance(harness, tick);
            harness.RunTick(tick);
            harness.Deliver(session);
        }

        var seconds = Ticks / TickHz;
        var adopted = state.SegmentsEmitted / (Creatures * seconds);
        var shadow = state.ShadowSegmentsEmitted / (Creatures * seconds);

        TestContext.Out.WriteLine(
            $"AC-21 over {Ticks} ticks ({seconds:F0} s) and {Creatures} moving creatures: "
            + $"adopted {state.SegmentsEmitted} segments = {adopted:F3}/entity/s, "
            + $"rejected trigger {state.ShadowSegmentsEmitted} = {shadow:F3}/entity/s");

        Assert.Multiple(() =>
        {
            Assert.That(state.SegmentsEmitted, Is.GreaterThan(0),
                "no segment at all was emitted over a minute of motion, so this run measured a world that was not moving");
            Assert.That(adopted, Is.LessThanOrEqualTo(Bound),
                $"the adopted trigger emitted {adopted:F3} segments per moving entity per second, above AC-21's {Bound}");

            // A separate, tighter guard. AC-21's 1.5 is the CRITERION and is six times looser than what the trigger actually costs, so on its own it would
            // not notice a fivefold regression. Keeping both means the criterion stays stated as the criterion and the regression has its own line.
            Assert.That(adopted, Is.LessThanOrEqualTo(RegressionBound),
                $"the adopted trigger emitted {adopted:F3} segments per entity per second against a measured {MeasuredRate:F3}. That is still inside "
                + $"AC-21's {Bound}, but it is a large regression against what this trigger is known to cost");
            // A RATIO, not an inequality. The shadow counter is incremented alongside the real one on the initialize and teleport paths, so
            // `shadow >= adopted` holds by construction and would still pass if the shadow's own trigger never fired at all — which is the only part of it
            // worth verifying, and the only reason Q-M5 asks for the pair.
            Assert.That(shadow, Is.GreaterThan(adopted * 4d),
                $"the rejected trigger emitted {shadow:F3} segments per entity per second against the adopted trigger's {adopted:F3}. The measured "
                + "difference is about eighteen times; anything near parity means the shadow trigger is not firing and the pair says nothing");
        });
    }

    private static void Populate(FrameHarness harness)
    {
        using var tx = harness.Engine.CreateQuickTransaction();
        for (var i = 0; i < Creatures; i++)
        {
            var x = 100f + (i * 3f);
            var bounds = new ProjBounds { Bounds = new AABB2F { MinX = x, MinY = 100f, MaxX = x + 1f, MaxY = 101f }, Speed = (float)SpeedMps };
            var ai = new ProjAi { Template = 3, Level = 10, Mode = ProjAiMode.Idle };
            var vitals = new ProjVitals { Health = 9, MaxHealth = 10 };
            tx.Spawn<ProjCreature>(ProjCreature.Bounds.Set(in bounds), ProjCreature.Ai.Set(in ai), ProjCreature.Vitals.Set(in vitals));
        }

        tx.Commit();
        harness.Engine.WriteTickFence(1);
    }

    /// <summary>
    /// Advances every creature one tick along a path that is straight for a while and then turns.
    /// </summary>
    /// <param name="harness">The harness.</param>
    /// <param name="tick">The tick, which is what places the entity along its path.</param>
    /// <remarks>
    /// A circle would be the worst case and a straight line the best; this is a square-ish path — long straight legs with a corner between
    /// them — because it is the shape a creature's movement actually has, and it exercises both the leg the trigger should be silent on and
    /// the corner it must pay for.
    /// </remarks>
    private static void Advance(FrameHarness harness, long tick)
    {
        var step = SpeedMps / TickHz;
        var leg = tick / 50 % 4;
        var along = tick % 50 * step;

        using var tx = harness.Engine.CreateQuickTransaction();
        var accessor = tx.For<ProjCreature>();
        try
        {
            var index = 0;
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                var occupancy = cluster.OccupancyBits;
                while (occupancy != 0)
                {
                    var slot = BitOperations.TrailingZeroCount(occupancy);
                    occupancy &= occupancy - 1;

                    var originX = 100f + (index * 3f);
                    const float originY = 100f;
                    index++;

                    var (dx, dy) = leg switch
                    {
                        0 => (along, 0d),
                        1 => (50 * step, along),
                        2 => ((50 * step) - along, 50 * step),
                        _ => (0d, (50 * step) - along),
                    };

                    var x = (float)(originX + dx);
                    var y = (float)(originY + dy);
                    cluster.WriteSpatial(
                        ProjCreature.Bounds,
                        slot,
                        new ProjBounds { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x + 1f, MaxY = y + 1f }, Speed = (float)SpeedMps });
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        tx.Commit();
    }
}
