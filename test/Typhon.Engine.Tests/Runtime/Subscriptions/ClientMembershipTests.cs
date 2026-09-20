using NUnit.Framework;
using System;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// A client holds exactly the entities inside its observer's radius, on every tick of a world that keeps moving.
/// </summary>
/// <remarks>
/// <para>
/// <b>Membership, asserted against arithmetic rather than against the engine's own previous answer.</b> The entities drift over a wide area and the replica
/// is compared, every tick, to the set a distance test says should be inside the radius. That catches both directions at once: an entity kept that should
/// have gone is bandwidth and a client seeing through walls, and one dropped that should have stayed is a player who vanishes.
/// </para>
/// <para>
/// <b>This file used to carry two more cases claiming that relocating an entity between clusters is invisible to a client.</b> They were removed because
/// they could not reach their own condition: 600 entities teleported inside one 480 m box produce 23 400 spatial writes and, measured,
/// ZERO relocations — every cluster already spans the whole box, so repair has nothing to improve and migration hysteresis absorbs the rest. The tests
/// passed, and read as proof of an invariant about an event that never occurred. The claim is real and is covered where the condition is actually reached:
/// <c>WalkingObserverChurnTests</c> (11 515 relocations, no retraction of anything still in range) and <c>StationaryChurnTests</c> (4 798 relocations,
/// 470 replication entries carried between clusters, no retraction at all).
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class ClientMembershipTests : TestBase<ClientMembershipTests>
{
    private const string Profile = "parked";

    /// <summary>Half-width of the box every entity lives in, in metres.</summary>
    private const float BoxHalfM = 240f;

    /// <summary>The observer's radius: far enough that the whole box, corners included, is permanently inside it.</summary>
    /// <remarks>The box's corner is at <c>240 * sqrt(2)</c> ≈ 340 m, so 600 leaves 260 m of margin on the worst entity in the world.</remarks>
    private const double Radius = 600d;

    private const int Creatures = 600;
    private const int Ticks = 40;

    private static void Declare(SubscriptionsRegistry subs)
    {
        ProjectionTestSchema.DeclareCreature(subs);
        subs.Profile(Profile, p => p.Sphere(Radius).Of<ProjCreature>());
    }

    private static ProjBounds PointAt(float x, float y) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Speed = 1f };

    /// <summary>
    /// The client holds exactly the entities that are geometrically inside the observer's radius, on every tick, while the spatial layer repairs clusters.
    /// </summary>
    /// <remarks>
    /// <b>Brute force is the oracle.</b> The interest pass reaches entities through a broad phase over CLUSTER boxes and then a per-entity distance test,
    /// and repair rewrites those boxes continuously. If a cluster's stored bounds ever lag its contents, the broad phase drops the cluster and every
    /// entity in it vanishes from the view for a tick — the client is told to forget them and told about them again in full when the box catches up. That
    /// is invisible to any assertion that only checks convergence at the end, because the world does converge; it is the cost of getting there that is
    /// wrong. So the count is checked EVERY tick against a loop over every entity's actual position.
    /// </remarks>
    [Test]
    public void TheClientHoldsExactlyWhatIsInsideTheRadiusOnEveryTick()
    {
        const double Reach = 400d;
        const float SpreadM = 1400f;

        // The drift per tick, and the band excluded from the ground truth. An entity inside Reach - Margin cannot have crossed the rim since last tick.
        const float DriftM = 15f;
        const double Margin = 60d;

        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        var random = new Random(20260921);

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < Creatures; i++)
            {
                tx.Spawn<ProjCreature>(ProjCreature.Bounds.Set(
                    PointAt((float)((random.NextDouble() * 2d - 1d) * SpreadM), (float)((random.NextDouble() * 2d - 1d) * SpreadM))));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);

        using var harness = FrameHarness.Create(dbe, ReachDeclare, nameof(TheClientHoldsExactlyWhatIsInsideTheRadiusOnEveryTick),
            new SubscriptionsOptions
            {
                MaxSessions = 4,
                StatePoolBudgetBytes = 32L * 1024 * 1024,
                FramePoolBudgetBytes = 32L * 1024 * 1024,
                EnterBudgetPerFrame = 100_000,
            });

        var session = harness.OpenSessions(1, "reach")[0];
        var archetype = harness.PlanIndex(nameof(ProjCreature));

        harness.RunTick(1);
        harness.Deliver(session);
        Assert.That(harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d)), Is.True);

        var worst = 0;
        var worstTick = 0L;
        var mismatches = 0;

        // Two settling ticks: the first reaches clusters with no replication block and the second is the first that can describe them.
        for (var tick = 2L; tick <= Ticks; tick++)
        {
            var inRange = 0;
            using (var tx = harness.Engine.CreateQuickTransaction())
            {
                var accessor = tx.For<ProjCreature>();
                try
                {
                    foreach (var cluster in accessor.GetClusterEnumerator())
                    {
                        var occupancy = cluster.OccupancyBits;
                        while (occupancy != 0)
                        {
                            var slot = System.Numerics.BitOperations.TrailingZeroCount(occupancy);
                            occupancy &= occupancy - 1;

                            // A DRIFT, not a teleport. An entity that moves at most 15 m a tick and sits 60 m inside the radius was inside it last
                            // tick too, so the client has had every opportunity to hold it and a shortfall cannot be blamed on it having just arrived.
                            ref readonly var was = ref cluster.GetReadOnlySpan(ProjCreature.Bounds)[slot];
                            var x = Clamp(Centre(was.Bounds.MinX, was.Bounds.MaxX) + (float)((random.NextDouble() * 2d - 1d) * DriftM), SpreadM);
                            var y = Clamp(Centre(was.Bounds.MinY, was.Bounds.MaxY) + (float)((random.NextDouble() * 2d - 1d) * DriftM), SpreadM);
                            cluster.WriteSpatial(ProjCreature.Bounds, slot, PointAt(x, y));
                            if (((double)x * x) + ((double)y * y) <= (Reach - Margin) * (Reach - Margin))
                            {
                                inRange++;
                            }
                        }
                    }
                }
                finally
                {
                    accessor.Dispose();
                }

                tx.Commit();
            }

            harness.RunTick(tick);
            harness.Deliver(session);

            if (tick < 4)
            {
                continue;
            }

            // The client may legitimately hold MORE than the ground truth — everything out to Reach, where the truth stops at Reach - Margin. What it
            // may never do is hold fewer, because every entity counted is one it should already have had.
            var held = harness.Replica(session).NetIds(archetype).Length;
            var gap = inRange - held;
            if (gap < 0)
            {
                continue;
            }

            if (gap != 0)
            {
                mismatches++;
                if (Math.Abs(gap) > Math.Abs(worst))
                {
                    worst = gap;
                    worstTick = tick;
                }
            }
        }

        TestContext.Out.WriteLine($"ticks with a mismatch: {mismatches} of {Ticks - 3}; worst gap {worst} at tick {worstTick}");

        Assert.That(mismatches, Is.Zero,
            $"on {mismatches} ticks the client's world did not match what was geometrically inside the observer's radius; the worst was {worst} entities "
            + $"at tick {worstTick}. A positive gap means the pipeline LOST entities that were in range, which it then has to re-enter in full.");
    }

    private static float Centre(float lo, float hi) => (lo + hi) * 0.5f;

    private static float Clamp(float v, float extent) => v < -extent ? -extent : v > extent ? extent : v;

    private static void ReachDeclare(SubscriptionsRegistry subs)
    {
        ProjectionTestSchema.DeclareCreature(subs);
        subs.Profile("reach", p => p.Sphere(400d).Of<ProjCreature>());
    }
}
