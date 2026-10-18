using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests.Runtime.Subscriptions.Oracle;

/// <summary>
/// 14 § 3.6's numbers, measured (step 4.1's acceptance): what a 456 m town costs a 192 m Sphere, against the 192 m Sphere alone — the region's own cost — and
/// against the only alternative expressible without groups, the Sphere widened to 456 m over citizens and buildings both.
/// </summary>
/// <remarks>
/// <para>
/// <b>The world is SWG's shape, scaled to a fixture.</b> Four towns of 456 m with 190 static buildings each; citizens everywhere, denser in the towns, a tenth
/// of them walking every tick; sessions walking at 2 m a tick inside a town. The anchor moves, so the cell sweep and the crescent are paid at a realistic
/// cadence, and the citizens move, so the widened Sphere's 5.6× updates are paid too.
/// </para>
/// <para>
/// <b>Same binary, interleaved.</b> The three arms run in turn, <see cref="Pairs"/> times, each on a fresh runtime over the same engine and world, so a
/// thermal or frequency drift lands on all three alike. Reported: the track's µs per tick (median over the measured ticks), the bytes a session is sent per
/// tick, and what a session holds. Asserted: only that each arm measured what it claims — the region arm holds its town complete, the baseline holds no
/// building.
/// </para>
/// </remarks>
[TestFixture]
[Explicit("A measurement, not a check: run by hand")]
[Category("Manual")]
[NonParallelizable]
[Property("CacheSize", 256 * 1024 * 1024)]
class GroupRegionBench : TestBase<GroupRegionBench>
{
    private const double SphereM = 192d;
    private const double WideM = 456d;
    private const float TownHalfM = 228f;
    private const int BuildingsPerTown = 190;
    private const int Sessions = 128;
    private const int FillTicks = 40;
    private const int MeasureTicks = 200;
    private const int Pairs = 6;

    private static readonly (float X, float Y)[] Towns = [(-1000f, -1000f), (1000f, -1000f), (-1000f, 1000f), (1000f, 1000f)];

    private enum Arm
    {
        SphereAlone,
        SphereAndTown,
        WideSphere,
    }

    private static void Declare(SubscriptionsRegistry subs, Arm arm)
    {
        subs.Static<GroupBuilding>(a => a.Position(GroupBuilding.Bounds).Field(GroupBuilding.Ai, x => x.Template, Codec.U8, name: "kind"));
        subs.Archetype<GroupCitizen>(a => a
            .Motion(GroupCitizen.Bounds, m => m.Tolerance(0.05).Teleport(ProjectionTestSchema.MaxSpeedMps))
            .OnEnter(GroupCitizen.Ai, x => x.Template, Codec.U8, name: "template"));
        if (arm == Arm.SphereAndTown)
        {
            subs.Groups(GroupTestSchema.Set, g => g
                .Region<GroupTown>(r => r.Key(GroupTown.Zone, z => z.Key).Extent(GroupTown.Zone, z => z.Extent))
                .Member<GroupBuilding>(m => m.Key(GroupBuilding.Key, k => k.Town))
                .Leave(20d));
        }

        subs.Profile("bench", p =>
        {
            switch (arm)
            {
                case Arm.SphereAlone:
                    p.Sphere(SphereM).Of<GroupCitizen>();
                    break;
                case Arm.SphereAndTown:
                    p.Sphere(SphereM).Of<GroupCitizen>().Groups(GroupTestSchema.Set, 50d);
                    break;
                default:
                    p.Sphere(WideM).Of<GroupCitizen>().Of<GroupBuilding>();
                    break;
            }
        });
    }

    private static SubscriptionsOptions Options(Arm arm) => new()
    {
        IngressBytesPerSecond = TestIngress.Budget,
        MaxSessions = Sessions + 4,
        // One cell for the three arms, the smallest the widened Sphere accepts (ceil(R / c) <= 5): the arms differ in what they observe, not in the grid.
        ReplicationCellM = 96d,
        MaxGroupsPerSession = 4,
        StatePoolBudgetBytes = 256L * 1024 * 1024,
        FramePoolBudgetBytes = 256L * 1024 * 1024,
    };

    private static void Populate(DatabaseEngine dbe, Random random, List<EntityId> walkers)
    {
        using var tx = dbe.CreateQuickTransaction();
        var template = 1;
        for (var t = 0; t < Towns.Length; t++)
        {
            var (cx, cy) = Towns[t];
            tx.Spawn<GroupTown>(GroupTown.Zone.Set(new GroupZone { Key = 100 + t, Extent = GroupTestSchema.Square(cx, cy, TownHalfM) }));
            for (var b = 0; b < BuildingsPerTown; b++)
            {
                var bounds = GroupTestSchema.At(cx + Spread(random, TownHalfM), cy + Spread(random, TownHalfM));
                var ai = new ProjAi { Template = (byte)(template++ & 0xFF) };
                tx.Spawn<GroupBuilding>(GroupBuilding.Bounds.Set(in bounds), GroupBuilding.Ai.Set(in ai), GroupBuilding.Key.Set(new GroupKey { Town = 100 + t }));
            }

            for (var c = 0; c < 200; c++)
            {
                walkers.Add(Citizen(tx, random, cx + Spread(random, TownHalfM), cy + Spread(random, TownHalfM), ref template));
            }
        }

        for (var c = 0; c < 3000; c++)
        {
            walkers.Add(Citizen(tx, random, Spread(random, 2000f), Spread(random, 2000f), ref template));
        }

        tx.Commit();
    }

    private static EntityId Citizen(Transaction tx, Random random, float x, float y, ref int template)
    {
        var bounds = GroupTestSchema.At(x, y);
        var ai = new ProjAi { Template = (byte)(template++ & 0xFF) };
        return tx.Spawn<GroupCitizen>(GroupCitizen.Bounds.Set(in bounds), GroupCitizen.Ai.Set(in ai), GroupCitizen.Key.Set(default));
    }

    private static float Spread(Random random, float half) => ((float)random.NextDouble() * 2f - 1f) * half;

    /// <summary>A tenth of the citizens walk a metre, through WriteSpatial so crossings migrate.</summary>
    private static void WalkCitizens(DatabaseEngine dbe, Random random)
    {
        using var tx = dbe.CreateQuickTransaction();
        var accessor = tx.For<GroupCitizen>();
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

                    var b = cluster.GetReadOnly(GroupCitizen.Bounds, slot).Bounds;
                    var angle = random.NextDouble() * Math.PI * 2;
                    var x = Math.Clamp(((b.MinX + b.MaxX) * 0.5f) + (float)Math.Cos(angle), -2040f, 2040f);
                    var y = Math.Clamp(((b.MinY + b.MaxY) * 0.5f) + (float)Math.Sin(angle), -2040f, 2040f);
                    cluster.WriteSpatial(GroupCitizen.Bounds, slot, GroupTestSchema.At(x, y));
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        tx.Commit();
    }

    private sealed record Result(Arm Arm, double MedianUs, double GatherUs, double EncodeUs, double BytesPerSessionTick, double Buildings, double Citizens,
        int Complete);

    private Result Measure(DatabaseEngine dbe, Arm arm, int seed, ref long tick)
    {
        var random = new Random(seed);
        using var harness = FrameHarness.Create(dbe, s => Declare(s, arm), $"bench-{arm}-{seed}", Options(arm));
        harness.RunFence = true;
        var sessions = harness.OpenSessions(Sessions, "bench");
        var at = new Vector3D[Sessions];
        var heading = new double[Sessions];
        for (var i = 0; i < Sessions; i++)
        {
            var (cx, cy) = Towns[i % Towns.Length];
            at[i] = new Vector3D(cx + Spread(random, TownHalfM * 0.6f), cy + Spread(random, TownHalfM * 0.6f), 0d);
            heading[i] = random.NextDouble() * Math.PI * 2;
            harness.Sessions.SetViewpoint(sessions[i], at[i]);
        }

        var times = new List<double>();
        long bytes = 0;
        var phases = default((double Gather, double Sort, double Encode, double Publish));
        for (var t = 0; t < FillTicks + MeasureTicks; t++)
        {
            WalkCitizens(dbe, random);
            for (var i = 0; i < Sessions; i++)
            {
                var (cx, cy) = Towns[i % Towns.Length];
                var next = new Vector3D(at[i].X + (Math.Cos(heading[i]) * 2d), at[i].Y + (Math.Sin(heading[i]) * 2d), 0d);
                if (Math.Abs(next.X - cx) > TownHalfM * 0.8 || Math.Abs(next.Y - cy) > TownHalfM * 0.8)
                {
                    heading[i] += Math.PI;
                    next = at[i];
                }

                at[i] = next;
                harness.Sessions.SetViewpoint(sessions[i], at[i]);
            }

            tick++;
            if (t == FillTicks)
            {
                phases = harness.Assembler.PhaseMilliseconds;
            }

            var start = Stopwatch.GetTimestamp();
            harness.RunTick(tick);
            var elapsed = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
            foreach (var session in sessions)
            {
                if (t >= FillTicks)
                {
                    bytes += DrainBytes(harness, session);
                }
                else
                {
                    harness.Deliver(session);
                }
            }

            if (t >= FillTicks)
            {
                times.Add(elapsed);
            }
        }

        times.Sort();
        var end = harness.Assembler.PhaseMilliseconds;
        var buildingPlan = harness.CatalogPlan.ArchetypeByName(nameof(GroupBuilding)).Idx;
        var citizenPlan = harness.CatalogPlan.ArchetypeByName(nameof(GroupCitizen)).Idx;
        return new Result(arm, times[times.Count / 2], (end.Gather - phases.Gather) * 1000d / MeasureTicks, (end.Encode - phases.Encode) * 1000d / MeasureTicks,
            (double)bytes / (Sessions * MeasureTicks),
            sessions.Average(s => harness.Replica(s).NetIds(buildingPlan).Length), sessions.Average(s => harness.Replica(s).NetIds(citizenPlan).Length),
            sessions.Count(s => harness.Replica(s).Store.CompleteGroupCount > 0));
    }

    private static unsafe long DrainBytes(FrameHarness harness, SessionId session)
    {
        var replica = harness.Replica(session);
        ref var send = ref harness.Assembler.SendStateOf(session.Slot);
        long bytes = 0;
        while (send.TryClaimFrame(harness.Assembler.Gate.CommittedTick, out var frame))
        {
            bytes += frame.Length;
            replica.Apply(new ReadOnlySpan<byte>(frame.Bytes, frame.Length));
            send.CompleteSend(frame.Sequence);
        }

        return bytes;
    }

    [TearDown]
    public void StopPhaseTiming() => FrameAssembler.PhaseTimingEnabled = false;

    [Test]
    public void ATownOnASmallSphereAgainstTheSphereAloneAndTheWidenedSphere()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        Populate(dbe, new Random(17), []);
        FrameAssembler.PhaseTimingEnabled = true;
        var tick = 0L;
        var results = new List<Result>();
        for (var pair = 0; pair < Pairs; pair++)
        {
            foreach (var arm in pair % 2 == 0 ? new[] { Arm.SphereAlone, Arm.SphereAndTown, Arm.WideSphere } : [Arm.WideSphere, Arm.SphereAndTown, Arm.SphereAlone])
            {
                results.Add(Measure(dbe, arm, 1000 + pair, ref tick));
            }
        }

        foreach (var arm in Enum.GetValues<Arm>())
        {
            var r = results.Where(x => x.Arm == arm).ToList();
            var us = r.Select(x => x.MedianUs).OrderBy(x => x).ToList();
            var gather = r.Select(x => x.GatherUs).OrderBy(x => x).ToList();
            var encode = r.Select(x => x.EncodeUs).OrderBy(x => x).ToList();
            TestContext.Out.WriteLine(
                $"{arm,-14} track µs/tick median {us[us.Count / 2],8:F1} (min {us[0]:F1}, max {us[^1]:F1})  gather {gather[gather.Count / 2],7:F1} "
                + $"(min {gather[0]:F1})  encode {encode[encode.Count / 2],7:F1} µs/tick  bytes/session/tick {r.Average(x => x.BytesPerSessionTick),7:F1}"
                + $"  buildings held {r.Average(x => x.Buildings),6:F1}  citizens held {r.Average(x => x.Citizens),6:F1}  sessions with a complete town {r[0].Complete}");
        }

        Assert.Multiple(() =>
        {
            Assert.That(results.Where(r => r.Arm == Arm.SphereAlone).All(r => r.Buildings == 0), Is.True, "the baseline holds no building");
            Assert.That(results.Where(r => r.Arm == Arm.SphereAndTown).All(r => r.Complete == Sessions), Is.True, "every region session holds its town complete");
            Assert.That(results.Where(r => r.Arm == Arm.SphereAndTown).All(r => r.Buildings >= BuildingsPerTown), Is.True);
        });
    }
}
