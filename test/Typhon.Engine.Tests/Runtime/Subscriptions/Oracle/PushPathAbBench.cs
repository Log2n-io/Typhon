using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime.Subscriptions.Oracle;

/// <summary>
/// The push path's A/B (#1511): the same workloads timed against two builds of the engine, by swapping <c>Typhon.Engine.dll</c> beside this one — so this
/// fixture touches only what both builds have. The player path: 200 Sphere sessions walking a world of movers, the frame stage alone timed, every frame
/// drained. The god path: one World session over 80 000 entities a quarter of which turn every tick — more than a frame carries.
/// </summary>
[TestFixture]
[Explicit("A measurement, not a check: run by hand")]
[Category("Manual")]
[NonParallelizable]
class PushPathAbBench : TestBase<PushPathAbBench>
{
    private const float Limit = ProjectionTestSchema.WorldExtentM - 64f;

    private static ProjBounds At(float x, float y) => new()
    {
        Bounds = new AABB2F { MinX = x - 0.5f, MinY = y - 0.5f, MaxX = x + 0.5f, MaxY = y + 0.5f },
        Speed = 1f,
    };

    private static void Spawn(DatabaseEngine dbe, Random random, int count, float half)
    {
        for (var done = 0; done < count; done += 2048)
        {
            using var tx = dbe.CreateQuickTransaction();
            for (var i = done; i < Math.Min(count, done + 2048); i++)
            {
                var x = (float)(((random.NextDouble() * 2.0) - 1.0) * half);
                var y = (float)(((random.NextDouble() * 2.0) - 1.0) * half);
                tx.Spawn<ProjCreature>(ProjCreature.Bounds.Set(At(x, y)), ProjCreature.Ai.Set(new ProjAi { Template = 1, Mode = ProjAiMode.Wander, Alerted = 1 }),
                    ProjCreature.Vitals.Set(new ProjVitals { Health = 10, MaxHealth = 20 }));
            }

            tx.Commit();
        }
    }

    // A fraction of the creatures turn and step: a segment each.
    private static void Move(DatabaseEngine dbe, Random random, double fraction, float stride)
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
                    if (random.NextDouble() >= fraction)
                    {
                        continue;
                    }

                    var heading = random.NextDouble() * Math.PI * 2;
                    var b = cluster.GetReadOnly(ProjCreature.Bounds, slot).Bounds;
                    var x = Math.Clamp(((b.MinX + b.MaxX) * 0.5f) + (float)(Math.Cos(heading) * stride), -Limit, Limit);
                    var y = Math.Clamp(((b.MinY + b.MaxY) * 0.5f) + (float)(Math.Sin(heading) * stride), -Limit, Limit);
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

    private static double Median(List<double> values)
    {
        values.Sort();
        return values[values.Count / 2];
    }

    /// <summary>The player path: 200 walking Sphere sessions, 120 m discs, over 20 000 creatures in 4 km, a tenth of them turning every tick.</summary>
    [Test, Property("CacheSize", 256 * 1024 * 1024)]
    public void PlayerSpheres()
    {
        const int Sessions = 200;
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        var random = new Random(1511);
        Spawn(dbe, random, 20_000, 2000f);
        using var harness = FrameHarness.Create(dbe, subs =>
        {
            ProjectionTestSchema.DeclareCreature(subs);
            subs.Profile("player", p => p.Sphere(120).Of<ProjCreature>());
        }, nameof(PlayerSpheres), new SubscriptionsOptions
        {
            IngressBytesPerSecond = TestIngress.Budget,
            ReplicationCellM = 40d,
            MaxSessions = Sessions + 4,
            StatePoolBudgetBytes = 512L * 1024 * 1024,
            FramePoolBudgetBytes = 512L * 1024 * 1024,
        });
        harness.RunFence = true;
        var sessions = harness.OpenSessions(Sessions, "player");
        var viewpoints = new Vector3D[Sessions];
        var headings = new double[Sessions];
        for (var s = 0; s < Sessions; s++)
        {
            viewpoints[s] = new Vector3D((random.NextDouble() * 3000) - 1500, (random.NextDouble() * 3000) - 1500, 0d);
            headings[s] = random.NextDouble() * Math.PI * 2;
        }

        var frames = new List<double>();
        for (var tick = 1L; tick <= 400; tick++)
        {
            Move(dbe, random, 0.1, 1.5f);
            for (var s = 0; s < Sessions; s++)
            {
                if (random.Next(30) == 0)
                {
                    headings[s] = random.NextDouble() * Math.PI * 2;
                }

                // A player riding at 40 m/s: four metres a tick, new cells every few ticks.
                viewpoints[s] = new Vector3D(Math.Clamp(viewpoints[s].X + (Math.Cos(headings[s]) * 4), -1900, 1900),
                    Math.Clamp(viewpoints[s].Y + (Math.Sin(headings[s]) * 4), -1900, 1900), 0d);
                harness.Sessions.SetViewpoint(sessions[s], viewpoints[s]);
            }

            harness.RunUpToFrames(tick);
            var from = Stopwatch.GetTimestamp();
            harness.RunFramesOnly(tick);
            var us = (Stopwatch.GetTimestamp() - from) * 1e6 / Stopwatch.Frequency;
            harness.Assembler.Gate.Publish(tick);
            harness.Assembler.ClearReady();
            foreach (var session in sessions)
            {
                harness.Drain(session);
            }

            if (tick > 100)
            {
                frames.Add(us);
            }
        }

        TestContext.Out.WriteLine($"AB PlayerSpheres frameStageUs median {Median(frames):F0} p90 {frames[(int)(frames.Count * 0.9)]:F0} bytes {harness.Assembler.BytesEncoded}");
    }

    /// <summary>The god path: one World session over 80 000 entities, a quarter turning every tick — more updates than a frame carries.</summary>
    [Test, Property("CacheSize", 512 * 1024 * 1024)]
    public void GodWorldSaturated()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        var random = new Random(1512);
        Spawn(dbe, random, 80_000, Limit);
        using var harness = FrameHarness.Create(dbe, subs =>
        {
            ProjectionTestSchema.DeclareCreature(subs);
            subs.Profile("god", p => p.World().Of<ProjCreature>());
        }, nameof(GodWorldSaturated), new SubscriptionsOptions
        {
            IngressBytesPerSecond = TestIngress.Budget,
            ReplicationCellM = 256d,
            MaxSessions = 8,
            StatePoolBudgetBytes = 512L * 1024 * 1024,
            FramePoolBudgetBytes = 512L * 1024 * 1024,
        });
        harness.RunFence = true;
        var session = harness.OpenSessions(1, "god")[0];
        var plan = harness.PlanIndex(nameof(ProjCreature));
        var frames = new List<double>();
        var completeAt = 0L;
        var bytes = 0L;
        for (var tick = 1L; tick <= 300; tick++)
        {
            Move(dbe, random, 0.25, 1.5f);
            harness.RunUpToFrames(tick);
            var from = Stopwatch.GetTimestamp();
            harness.RunFramesOnly(tick);
            frames.Add((Stopwatch.GetTimestamp() - from) * 1e6 / Stopwatch.Frequency);
            harness.Assembler.Gate.Publish(tick);
            harness.Assembler.ClearReady();
            harness.Deliver(session);
            bytes = harness.Assembler.BytesEncoded;
            completeAt = completeAt == 0 && harness.StateOf(session).ViewComplete ? tick : completeAt;
        }

        TestContext.Out.WriteLine($"AB GodWorldSaturated frameStageUs median {Median(frames):F0} completeAt {completeAt} held "
            + $"{harness.Replica(session).NetIds(plan).Length} resets {harness.Subscriptions.Push.Resets} bytes {bytes}");
    }
}
