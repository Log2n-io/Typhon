using NUnit.Framework;
using System;
using System.Collections.Generic;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// A radius query returns every entity inside the radius, on every tick, while the spatial layer is repairing clusters underneath it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Asked of the public API, not of a subscription.</b> <c>ArchetypeClusterState.QueryRadius</c> is what any application calls and what the interest
/// pass calls; if it drops an entity that has not moved, every consumer of it is wrong and the replication traffic that follows is a symptom rather than
/// the defect.
/// </para>
/// <para>
/// <b>Pinned entities are the probe.</b> A subset is spawned and never written again, so their membership of a fixed disc is a constant of the run and can
/// be computed once by arithmetic. Everything else moves, which is what gives the repair path clusters to nominate. A pinned entity inside the disc that
/// the query fails to return is unambiguous: nothing about it changed, and the answer did.
/// </para>
/// <para>
/// <b>Density is the variable that matters</b>, so this spawns enough entities to fill many clusters. A handful of entities lives in one or two clusters,
/// which repair never touches and which no broad phase can lose.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class ClusterQueryStabilityTests : TestBase<ClusterQueryStabilityTests>
{
    private const int Pinned = 400;
    private const int Movers = 6000;
    private const float SpreadM = 1500f;
    private const double Reach = 400d;
    private const int Ticks = 40;

    private static ProjBounds PointAt(float x, float y) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Speed = 1f };

    [Test]
    public void ARadiusQueryNeverLosesAnEntityThatHasNotMoved()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        var random = new Random(20260922);

        // The pinned set first, at known coordinates, so the arithmetic answer is fixed for the whole run.
        var pinnedInside = new List<(float X, float Y)>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < Pinned; i++)
            {
                // Deliberately well inside the disc — 0.7 of the radius — so no rim effect, no hysteresis question, no ambiguity.
                var angle = random.NextDouble() * Math.PI * 2d;
                var r = Math.Sqrt(random.NextDouble()) * Reach * 0.7d;
                var x = (float)(r * Math.Cos(angle));
                var y = (float)(r * Math.Sin(angle));
                pinnedInside.Add((x, y));
                tx.Spawn<ProjCreature>(ProjCreature.Bounds.Set(PointAt(x, y)));
            }

            for (var i = 0; i < Movers; i++)
            {
                tx.Spawn<ProjCreature>(ProjCreature.Bounds.Set(PointAt(
                    (float)((random.NextDouble() * 2d - 1d) * SpreadM),
                    (float)((random.NextDouble() * 2d - 1d) * SpreadM))));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);

        var clusterState = dbe._archetypeStates[Archetype<ProjCreature>.Metadata.ArchetypeId].ClusterState;
        var worstMissing = 0;
        var worstTick = 0L;
        var ticksShort = 0;

        for (var tick = 2L; tick <= Ticks; tick++)
        {
            // Move everything that is not pinned. The pinned coordinates are never written again after the spawn above.
            using (var tx = dbe.CreateQuickTransaction())
            {
                var accessor = tx.For<ProjCreature>();
                try
                {
                    var seen = 0;
                    foreach (var cluster in accessor.GetClusterEnumerator())
                    {
                        var occupancy = cluster.OccupancyBits;
                        while (occupancy != 0)
                        {
                            var slot = System.Numerics.BitOperations.TrailingZeroCount(occupancy);
                            occupancy &= occupancy - 1;
                            ref readonly var b = ref cluster.GetReadOnlySpan(ProjCreature.Bounds)[slot];

                            // A pinned entity is identified by its coordinates being one of the fixed set; everything else drifts.
                            var cx = (b.Bounds.MinX + b.Bounds.MaxX) * 0.5f;
                            var cy = (b.Bounds.MinY + b.Bounds.MaxY) * 0.5f;
                            if (IsPinned(pinnedInside, cx, cy))
                            {
                                continue;
                            }

                            seen++;
                            cluster.WriteSpatial(ProjCreature.Bounds, slot, PointAt(
                                Clamp(cx + (float)((random.NextDouble() * 2d - 1d) * 25d)),
                                Clamp(cy + (float)((random.NextDouble() * 2d - 1d) * 25d))));
                        }
                    }

                    Assert.That(seen, Is.GreaterThan(0), "nothing moved, so repair had nothing to do and this proves nothing");
                }
                finally
                {
                    accessor.Dispose();
                }

                tx.Commit();
            }

            dbe.WriteTickFence(tick);

            // THE QUERY. Same centre, same radius, every tick.
            var found = new HashSet<long>();
            using (var epoch = EpochGuard.Enter(dbe.EpochManager))
            {
                foreach (var hit in clusterState.QueryRadius(dbe.SpatialGrid, 0d, 0d, 0d, Reach))
                {
                    var cx = (hit.MinX + hit.MaxX) * 0.5;
                    var cy = (hit.MinY + hit.MaxY) * 0.5;
                    found.Add(Key(cx, cy));
                }
            }

            var missing = 0;
            foreach (var p in pinnedInside)
            {
                if (!found.Contains(Key(p.X, p.Y)))
                {
                    missing++;
                }
            }

            if (missing > 0)
            {
                ticksShort++;
                if (missing > worstMissing)
                {
                    worstMissing = missing;
                    worstTick = tick;
                }
            }
        }

        TestContext.Out.WriteLine(
            $"{Pinned} pinned entities well inside a {Reach} m disc, {Movers} movers, {Ticks} ticks: "
            + $"{ticksShort} ticks lost at least one; worst {worstMissing} at tick {worstTick}");

        Assert.That(ticksShort, Is.Zero,
            $"on {ticksShort} ticks the radius query failed to return entities that had not moved since they were spawned, the worst tick losing "
            + $"{worstMissing} of {Pinned}. They sit at 0.7 of the radius, so this is not a boundary question: the query is dropping entities it is "
            + "obliged to return, while the spatial layer repairs clusters around them.");
    }

    private static bool IsPinned(List<(float X, float Y)> pinned, float x, float y)
    {
        for (var i = 0; i < pinned.Count; i++)
        {
            if (Math.Abs(pinned[i].X - x) < 1e-3f && Math.Abs(pinned[i].Y - y) < 1e-3f)
            {
                return true;
            }
        }

        return false;
    }

    private static long Key(double x, double y) => ((long)Math.Round(x * 100d) << 24) ^ (long)Math.Round(y * 100d);

    private static float Clamp(float v) => v < -SpreadM ? -SpreadM : v > SpreadM ? SpreadM : v;
}
