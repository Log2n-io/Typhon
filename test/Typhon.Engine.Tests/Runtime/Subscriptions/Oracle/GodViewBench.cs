using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime.Subscriptions.Oracle;

/// <summary>
/// What the SWG god camera costs: one session served a whole 16 km planet of 80 000 entities, 1 500 of them riding straight at 15 m/s and a fraction of
/// the rest turning every tick, while creatures die and spawn — the fill (ticks until <c>VIEW_COMPLETE</c>) and the steady state (bytes a frame, records a
/// frame by kind, the frame stage's CPU), for the profile shapes the engine offers.
/// </summary>
/// <remarks>
/// <b>What is timed.</b> The frame stage's own phase timers (<see cref="FrameAssembler.PhaseMilliseconds"/>: gather, sort, encode, publish), summed over
/// workers, per tick — the session's cost, not the projection's, which every session shares. Bytes are <see cref="FrameAssembler.BytesEncoded"/>.
/// </remarks>
[TestFixture]
[Explicit("A measurement, not a check: run by hand")]
[Category("Manual")]
[NonParallelizable]
[Property("CacheSize", 512 * 1024 * 1024)]
class GodViewBench : TestBase<GodViewBench>
{
    private const float Limit = ProjectionTestSchema.WorldExtentM - 64f;

    private static ProjBounds At(float x, float y) => new()
    {
        Bounds = new AABB2F { MinX = x - 0.5f, MinY = y - 0.5f, MaxX = x + 0.5f, MaxY = y + 0.5f },
        Speed = 1f,
    };

    private sealed class World
    {
        public readonly List<EntityId> Creatures = [];
        public readonly HashSet<long> Swoops = [];
        public readonly Dictionary<long, double> Headings = [];
        public readonly Random Random = new(906);
    }

    private static float Coordinate(Random random) => (float)(((random.NextDouble() * 2.0) - 1.0) * Limit);

    private static void Spawn(DatabaseEngine dbe, World world, int count)
    {
        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < count; i++)
        {
            var ai = new ProjAi { Template = (byte)(1 + world.Random.Next(200)), Mode = (ProjAiMode)(1 + world.Random.Next(4)), Alerted = 1 };
            world.Creatures.Add(tx.Spawn<ProjCreature>(ProjCreature.Bounds.Set(At(Coordinate(world.Random), Coordinate(world.Random))),
                ProjCreature.Ai.Set(in ai), ProjCreature.Vitals.Set(new ProjVitals { Health = 10, MaxHealth = 20 })));
        }

        tx.Commit();
    }

    private static void Churn(DatabaseEngine dbe, World world, int count)
    {
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < count; i++)
            {
                var index = world.Random.Next(world.Creatures.Count);
                var id = world.Creatures[index];
                if (world.Swoops.Contains((long)id.RawValue))
                {
                    continue;
                }

                tx.Destroy(id);
                world.Creatures[index] = world.Creatures[^1];
                world.Creatures.RemoveAt(world.Creatures.Count - 1);
            }

            tx.Commit();
        }

        Spawn(dbe, world, count);
    }

    // Swoops ride straight and turn now and then; wanderers — a fraction of the rest — take a fresh heading every tick, a segment each.
    private static void Move(DatabaseEngine dbe, World world, double wanderers)
    {
        using var tx = dbe.CreateQuickTransaction();
        var accessor = tx.For<ProjCreature>();
        try
        {
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                var occupancy = cluster.OccupancyBits;
                while (occupancy != 0)
                {
                    var slot = BitOperations.TrailingZeroCount(occupancy);
                    occupancy &= occupancy - 1;
                    var id = (long)cluster.GetEntityId(slot).RawValue;
                    float stride;
                    double heading;
                    if (world.Swoops.Contains(id))
                    {
                        stride = 1.5f;
                        if (!world.Headings.TryGetValue(id, out heading) || world.Random.Next(50) == 0)
                        {
                            heading = world.Random.NextDouble() * Math.PI * 2;
                        }
                    }
                    else if (world.Random.NextDouble() < wanderers)
                    {
                        stride = 0.5f;
                        heading = world.Random.NextDouble() * Math.PI * 2;
                    }
                    else
                    {
                        continue;
                    }

                    var b = cluster.GetReadOnly(ProjCreature.Bounds, slot).Bounds;
                    var x = ((b.MinX + b.MaxX) * 0.5f) + (float)(Math.Cos(heading) * stride);
                    var y = ((b.MinY + b.MaxY) * 0.5f) + (float)(Math.Sin(heading) * stride);
                    if (Math.Abs(x) > Limit || Math.Abs(y) > Limit)
                    {
                        heading += Math.PI;
                        x = Math.Clamp(x, -Limit, Limit);
                        y = Math.Clamp(y, -Limit, Limit);
                    }

                    world.Headings[id] = heading;
                    cluster.WriteSpatial(ProjCreature.Bounds, slot, At(x, y));
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        tx.Commit();
    }

    public enum Shape
    {
        World,
        WorldFill5000,
        WorldEvery2,
        Region4Km,
    }

    // One method per arm: a parameterised case does not carry the method's CacheSize property, and 80 000 entities do not fit the default 8 MiB.
    [Test, Property("CacheSize", 512 * 1024 * 1024)]
    public void World_SwoopsOnly() => GodCamera(Shape.World, 0);

    [Test, Property("CacheSize", 512 * 1024 * 1024)]
    public void World_FivePercentWander() => GodCamera(Shape.World, 50);

    [Test, Property("CacheSize", 512 * 1024 * 1024)]
    public void WorldFill5000_FivePercentWander() => GodCamera(Shape.WorldFill5000, 50);

    [Test, Property("CacheSize", 512 * 1024 * 1024)]
    public void WorldFill5000_TwentyFivePercentWander() => GodCamera(Shape.WorldFill5000, 250);

    [Test, Property("CacheSize", 512 * 1024 * 1024)]
    public void WorldEvery2_FivePercentWander() => GodCamera(Shape.WorldEvery2, 50);

    [Test, Property("CacheSize", 512 * 1024 * 1024)]
    public void Region4Km_FivePercentWander() => GodCamera(Shape.Region4Km, 50);

    private void GodCamera(Shape shape, int wanderersPerMille)
    {
        const int Entities = 80_000;
        const int Swoops = 1_500;
        const int ChurnPerTick = 20;
        const int SteadyTicks = 100;

        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        var world = new World();
        for (var spawned = 0; spawned < Entities; spawned += 2048)
        {
            Spawn(dbe, world, Math.Min(2048, Entities - spawned));
        }
        for (var i = 0; i < Swoops; i++)
        {
            world.Swoops.Add((long)world.Creatures[i].RawValue);
        }

        using var harness = FrameHarness.Create(dbe, subs =>
        {
            ProjectionTestSchema.DeclareCreature(subs);
            subs.Profile("god", p =>
            {
                switch (shape)
                {
                    case Shape.WorldEvery2:
                        p.Every(2).World().Of<ProjCreature>();
                        break;
                    case Shape.WorldFill5000:
                        p.World().Fill(5000).Of<ProjCreature>();
                        break;
                    case Shape.Region4Km:
                        p.ClientRegion(4000).Of<ProjCreature>();
                        break;
                    default:
                        p.World().Of<ProjCreature>();
                        break;
                }
            });
        }, $"{nameof(GodCamera)}{shape}", new SubscriptionsOptions
        {
            IngressBytesPerSecond = TestIngress.Budget,
            ReplicationCellM = 256d,
            MaxSessions = 16,
            StatePoolBudgetBytes = 512L * 1024 * 1024,
            FramePoolBudgetBytes = 512L * 1024 * 1024,
        });
        harness.RunFence = true;
        harness.DrainNetIds = true;
        var session = harness.OpenSessions(1, "god")[0];
        if (shape == Shape.Region4Km)
        {
            Assert.That(harness.Subscriptions.Ingress.SetRegionForTest(session,
                [new RegionVertex { X = -2000, Y = -2000 }, new RegionVertex { X = 2000, Y = -2000 }, new RegionVertex { X = 2000, Y = 2000 },
                    new RegionVertex { X = -2000, Y = 2000 }], 2), Is.True);
        }

        var a = harness.Assembler;
        var timing = FrameAssembler.PhaseTimingEnabled;
        FrameAssembler.PhaseTimingEnabled = true;
        try
        {
            var tick = 0L;
            var fillWall = Stopwatch.StartNew();
            var fillTicks = 0;
            for (; fillTicks < 3000 && harness.StateOf(session) is not { ViewComplete: true }; fillTicks++)
            {
                Move(dbe, world, wanderersPerMille / 1000d);
                Churn(dbe, world, ChurnPerTick);
                harness.RunTick(++tick);
                harness.Deliver(session);
            }

            fillWall.Stop();
            var held = harness.Replica(session).NetIds(harness.PlanIndex(nameof(ProjCreature))).Length;

            var bytes0 = a.BytesEncoded;
            var (g0, s0, e0, p0) = a.PhaseMilliseconds;
            long enters = 0, segments = 0, states = 0, leaves = 0, frames = 0;
            var largest = 0L;
            for (var t = 0; t < SteadyTicks; t++)
            {
                Move(dbe, world, wanderersPerMille / 1000d);
                Churn(dbe, world, ChurnPerTick);
                var before = a.BytesEncoded;
                harness.RunTick(++tick);
                largest = Math.Max(largest, a.BytesEncoded - before);
                foreach (var log in harness.DeliverLogged(session))
                {
                    frames++;
                    enters += log.Enters.Count;
                    segments += log.Segments.Count;
                    states += log.States.Count;
                    leaves += log.Leaves.Count;
                }
            }

            var (g1, s1, e1, p1) = a.PhaseMilliseconds;
            var bytes = a.BytesEncoded - bytes0;
            TestContext.Out.WriteLine(
                $"RESULT {shape} wanderers {wanderersPerMille}‰: fill {fillTicks} ticks ({fillWall.Elapsed.TotalSeconds:F1} s wall), held {held}; steady "
                + $"{bytes / SteadyTicks} B/tick (largest {largest}), {frames} frames, per frame enter {enters / Math.Max(1, frames)} seg {segments / Math.Max(1, frames)} "
                + $"state {states / Math.Max(1, frames)} leave {leaves / Math.Max(1, frames)}; B/record {(double)bytes / Math.Max(1, enters + segments + states + leaves):F1}; "
                + $"stage us/tick gather {(g1 - g0) * 1000 / SteadyTicks:F0} sort {(s1 - s0) * 1000 / SteadyTicks:F0} encode {(e1 - e0) * 1000 / SteadyTicks:F0} "
                + $"publish {(p1 - p0) * 1000 / SteadyTicks:F0}; owedLargest {a.OwedLargest}, resets {harness.Subscriptions.Push.Resets}");
        }
        finally
        {
            FrameAssembler.PhaseTimingEnabled = timing;
        }
    }
}
