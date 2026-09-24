using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime.Subscriptions.Oracle;

/// <summary>
/// The 3D validation's cost half (<c>claude/design/Subscriptions/10-phase15-3d-groundwork.md</c> § 10, 1.5.5): a deep grid's index follows events, not
/// the grid, and the volumetric swarm's per-stage numbers against § 2.3's estimates.
/// </summary>
[TestFixture]
[NonParallelizable]
class PushVolumeTests : TestBase<PushVolumeTests>
{
    /// <summary>A 16 × 16 × 4 km world: at c = 16 m, 1 000 × 1 000 × 250 = 2.5 × 10⁸ replication cells.</summary>
    private static SpatialGridConfig Volume16x16x4Km() => new(new Vector3D(-8000, -8000, -2000), new Vector3D(8000, 8000, 2000), 256);

    private static void DeclareFlyer(SubscriptionsRegistry subs, double radius)
    {
        subs.Archetype<ProjFlyer>(a => a
            .Motion(ProjFlyer.Bounds, m => m.Tolerance(0.05).Teleport(ProjectionTestSchema.MaxSpeedMps))
            .Field(ProjFlyer.Ai, x => x.Level, Codec.U16, name: "level"));
        subs.Profile("fly", p => p.Sphere(radius).Of<ProjFlyer>());
    }

    /// <summary>
    /// § 10's empty world, deep: 64 sessions flying over 2.5 × 10⁸ cells and no entity. The index holds no cell on any tick, the occupancy none, and every
    /// cell a sphere reaches is skipped without a cluster query.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-25")]
    public void AnEmptyDeepWorldIndexesNothingWhateverTheGrid()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider, Volume16x16x4Km());
        using var harness = FrameHarness.Create(dbe, subs => DeclareFlyer(subs, 48), nameof(AnEmptyDeepWorldIndexesNothingWhateverTheGrid),
            replicationCellM: 16);
        Assert.That(harness.Subscriptions.Push.Deep, Is.True);

        var sessions = harness.OpenSessions(64, "fly");
        var random = new Random(3305);
        var at = new Vector3D[sessions.Length];
        for (var i = 0; i < at.Length; i++)
        {
            at[i] = new Vector3D((random.NextDouble() * 12000) - 6000, (random.NextDouble() * 12000) - 6000, (random.NextDouble() * 3000) - 1500);
        }

        for (var tick = 1; tick <= 30; tick++)
        {
            for (var i = 0; i < sessions.Length; i++)
            {
                at[i] = new Vector3D(at[i].X + 3, at[i].Y + 1.5, at[i].Z + 1);
                harness.Sessions.SetViewpoint(sessions[i], at[i]);
            }

            harness.RunTick(tick);
            foreach (var session in sessions)
            {
                harness.Deliver(session);
            }

            Assert.That(harness.Subscriptions.Push.IndexCells, Is.Zero, $"tick {tick}: the index holds cells with no event");
        }

        var push = harness.Subscriptions.Push;
        Assert.Multiple(() =>
        {
            Assert.That(push.Occupancy.Count, Is.Zero);
            Assert.That(push.CellsDelivered, Is.GreaterThan(1000), "no cell was delivered, so the test proved nothing");
            Assert.That(push.EmptyCellsSkipped, Is.GreaterThanOrEqualTo(push.CellsDelivered), "every cell is empty");
        });
    }

    /// <summary>
    /// § 10's empty world, deep and timed: the same 64 flying sessions at c = 16 m (2.5 × 10⁸ cells) and c = 256 m (≈ 10⁶), R = 3c in both so every
    /// window is 11³ cells and only the grid differs. The median tick must be within 5 %. Explicit: a timing, run where the machine is quiet.
    /// </summary>
    [Test]
    [Explicit("A timing: run it on a quiet machine")]
    public void AnEmptyDeepWorldCostsTheSameWhateverTheCellSide()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider, Volume16x16x4Km());

        // Paired rounds, the two sides adjacent in time, and the median of the rounds' ratios: this machine's tick drifts by ±15 % between runs of one
        // configuration, which a ratio of two medians taken apart would carry whole.
        var ratios = new List<double>();
        for (var round = 0; round < 5; round++)
        {
            var fine = MedianTickMs(dbe, 16, $"EmptyDeep{round}_16");
            var coarse = MedianTickMs(dbe, 256, $"EmptyDeep{round}_256");
            ratios.Add(fine / coarse);
            TestContext.Out.WriteLine($"round {round}: c = 16 m {fine:F4} ms, c = 256 m {coarse:F4} ms, ratio {fine / coarse:F3}");
        }

        ratios.Sort();
        TestContext.Out.WriteLine($"median ratio, deep: {ratios[ratios.Count / 2]:F3}");
        Assert.That(ratios[ratios.Count / 2], Is.InRange(0.95, 1.05));
    }

    private static double MedianTickMs(DatabaseEngine dbe, double cellM, string name)
    {
        using var harness = FrameHarness.Create(dbe, subs => DeclareFlyer(subs, 3 * cellM), name, replicationCellM: cellM);
        var sessions = harness.OpenSessions(64, "fly");
        var random = new Random(64);
        var at = new Vector3D[sessions.Length];
        for (var i = 0; i < at.Length; i++)
        {
            // Within ±200 m of z = 0: at c = 256 m the world is 16 cells deep, and a window reaching past its Z edges would be clipped — less work at the
            // coarse side for a reason that is the edge's, not the grid's.
            at[i] = new Vector3D((random.NextDouble() * 6000) - 3000, (random.NextDouble() * 6000) - 3000, (random.NextDouble() * 400) - 200);
        }

        var samples = new List<double>();
        for (var tick = 1; tick <= 400; tick++)
        {
            for (var i = 0; i < sessions.Length; i++)
            {
                // An eighth of a cell a tick, climbing a sixty-fourth: one geometry at two scales, the anchor moving every tick, no teleport.
                var step = (tick / 50) % 2 == 0 ? cellM / 8 : -cellM / 8;
                at[i] = new Vector3D(at[i].X + step, at[i].Y + (step / 2), at[i].Z + (step / 16));
                harness.Sessions.SetViewpoint(sessions[i], at[i]);
            }

            var from = Stopwatch.GetTimestamp();
            harness.RunTick(tick);
            var ms = Stopwatch.GetElapsedTime(from).TotalMilliseconds;
            foreach (var session in sessions)
            {
                harness.Deliver(session);
            }

            if (tick > 100)
            {
                samples.Add(ms);
            }
        }

        samples.Sort();
        return samples[samples.Count / 2];
    }

    /// <summary>
    /// The volumetric swarm, in process (§ 10; the full benchmark is 100 k boids and 1 000 remote sessions): flyers that wander in 3D and the sessions
    /// flying among them, at windows of 7³, 11³ and 13³ cells. Reports, per tick, the index's cost per event and the gather's cost per session — the
    /// terms § 2.3 estimated — beside the flat implementation on the same plane coordinates. Explicit: a measurement, not a gate.
    /// </summary>
    [Test]
    [Explicit("A measurement: run it on a quiet machine and read the output")]
    [Property("CacheSize", 256 * 1024 * 1024)]
    public void TheVolumetricSwarmPerStage()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider,
            new SpatialGridConfig(new Vector3D(-2048, -2048, -2048), new Vector3D(2048, 2048, 2048), 128));
        var random = new Random(1555);
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 20_000; i++)
            {
                var b = Box((random.NextDouble() * 3600) - 1800, (random.NextDouble() * 3600) - 1800, (random.NextDouble() * 3600) - 1800);
                var ai = new ProjAi { Level = 1 };
                tx.Spawn<ProjFlyer>(ProjFlyer.Bounds.Set(in b), ProjFlyer.Ai.Set(in ai));
            }

            tx.Commit();
        }

        var tick = 0L;
        foreach (var (reach, name) in new[] { (1, "7³"), (3, "11³"), (4, "13³") })
        {
            Swarm(dbe, cellM: 64, radius: reach * 64, windowName: name, ref tick);
        }
    }

    private static void Swarm(DatabaseEngine dbe, double cellM, double radius, string windowName, ref long engineTick)
    {
        const int Sessions = 256;
        const int Ticks = 120;
        using var harness = FrameHarness.Create(dbe, subs => DeclareFlyer(subs, radius), "Swarm" + windowName, replicationCellM: cellM);
        harness.ProjectionWorkers = 8;
        var random = new Random(1556);
        var sessions = harness.OpenSessions(Sessions, "fly");
        var at = new Vector3D[Sessions];
        for (var i = 0; i < Sessions; i++)
        {
            at[i] = new Vector3D((random.NextDouble() * 3000) - 1500, (random.NextDouble() * 3000) - 1500, (random.NextDouble() * 3000) - 1500);
        }

        var push = harness.Subscriptions.Push;
        long index0 = 0, gather0 = 0, events0 = 0, cells = 0;
        var tick = 0;
        for (tick = 1; tick <= Ticks; tick++)
        {
            // A tenth of the flyers wander a metre and a half, through WriteSpatial so they migrate between clusters as a swarm does.
            using (var tx = dbe.CreateQuickTransaction())
            {
                var accessor = tx.For<ProjFlyer>();
                try
                {
                    foreach (var cluster in accessor.GetClusterEnumerator())
                    {
                        var occupancy = cluster.OccupancyBits;
                        while (occupancy != 0)
                        {
                            var slot = BitOperations.TrailingZeroCount(occupancy);
                            occupancy &= occupancy - 1;
                            if (random.Next(10) != 0)
                            {
                                continue;
                            }

                            var c = cluster.GetReadOnly(ProjFlyer.Bounds, slot).Bounds;
                            cluster.WriteSpatial(ProjFlyer.Bounds, slot, Box(
                                Math.Clamp(((c.MinX + c.MaxX) * 0.5) + ((random.NextDouble() - 0.5) * 3), -2000, 2000),
                                Math.Clamp(((c.MinY + c.MaxY) * 0.5) + ((random.NextDouble() - 0.5) * 3), -2000, 2000),
                                Math.Clamp(((c.MinZ + c.MaxZ) * 0.5) + ((random.NextDouble() - 0.5) * 3), -2000, 2000)));
                        }
                    }
                }
                finally
                {
                    accessor.Dispose();
                }

                tx.Commit();
            }

            for (var i = 0; i < Sessions; i++)
            {
                at[i] = new Vector3D(at[i].X + 0.8, at[i].Y - 0.4, at[i].Z + 0.3);
                harness.Sessions.SetViewpoint(sessions[i], at[i]);
            }

            // One tick counter for the engine and the track: the blocks step reads the fence's structure words by tick.
            var now = ++engineTick;
            dbe.WriteTickFence(now);
            if (tick == 21)
            {
                index0 = push.IndexTicks + push.SortTicks;
                gather0 = push.GatherTicks;
                events0 = push.Events;
                cells = 0;
            }

            harness.RunTick(now, workers: 8);
            if (tick > 20)
            {
                cells += push.IndexCells;
            }

            foreach (var s in sessions)
            {
                harness.Deliver(s);
            }
        }

        var measured = Ticks - 20;
        var f = 1_000_000.0 / Stopwatch.Frequency;
        var events = push.Events - events0;
        var indexUs = ((push.IndexTicks + push.SortTicks) - index0) * f;
        var gatherUs = (push.GatherTicks - gather0) * f;
        TestContext.Out.WriteLine(
            $"swarm {windowName} (c {cellM} m, R {radius} m, {(push.Deep ? "deep" : "flat")}): {events / (double)measured:F0} events and "
            + $"{cells / (double)measured:F0} "
            + $"index cells per tick; index {indexUs / measured:F1} µs CPU per tick ({indexUs * 1000 / Math.Max(1, events):F1} ns per event); gather "
            + $"{gatherUs / (measured * (double)Sessions):F2} µs per session per tick");
    }

    private static ProjBounds3 Box(double x, double y, double z) => new()
    {
        Bounds = new AABB3F
        {
            MinX = (float)x - 0.5f, MinY = (float)y - 0.5f, MinZ = (float)z - 0.5f, MaxX = (float)x + 0.5f, MaxY = (float)y + 0.5f, MaxZ = (float)z + 0.5f,
        },
        Speed = 1f,
    };
}
