using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime.Subscriptions.Oracle;

/// <summary>
/// 16 § 4 and § 5 measured before anything is built: what a region set's index costs when its regions move (#1409), and what the engine-wide set of wanted
/// keys costs when sessions hold many (#1410).
/// </summary>
/// <remarks>
/// <para>
/// <b>What is timed.</b> The two serial terms the issues name, each by its own diagnostic timer — <c>PushReplication.RegionRefreshTicks</c> (the region
/// read, the compare, any rebuild) and <c>PushHub.WantedTicks</c> (the wanted-key collection) — divided by the ticks measured, beside the whole tick
/// (<see cref="FrameHarness.RunTick"/>, median). The region set has positionless members, which is what turns the wanted-key collection on.
/// </para>
/// <para>
/// <b>#1409's geometry is the issue's:</b> 40 m cells, a reach of 50 m and a leave margin of 50 m, so each region is filed under its box padded by 100 m;
/// regions are 20 m squares over 4 km, a fraction of them walking 2 m a tick. <b>#1410's</b> puts every region within every session's reach, so each
/// session holds them all: W = sessions × keys.
/// </para>
/// </remarks>
[TestFixture]
[Explicit("A measurement, not a check: run by hand")]
[Category("Manual")]
[NonParallelizable]
[Property("CacheSize", 512 * 1024 * 1024)]
class GroupScaleBench : TestBase<GroupScaleBench>
{
    private const string Set = "town";
    private const int WarmTicks = 10;
    private const int MeasureTicks = 30;

    private sealed record Result(string Name, double TickUs, double RegionUs, double WantedUs, long Refiles, double KeysHeld);

    private static void Declare(SubscriptionsRegistry subs, double reachM, double leaveM)
    {
        subs.Archetype<GroupPositionless>(a => a.Field(GroupPositionless.Ai, x => x.Template, Codec.U8, name: "kind"));
        subs.Groups(Set, g => g.Region<GroupTown>(r => r.Key(GroupTown.Zone, z => z.Key).Extent(GroupTown.Zone, z => z.Extent))
            .Member<GroupPositionless>(m => m.Key(GroupPositionless.Key, k => k.Town))
            .Leave(leaveM));
        subs.Profile("p", p => p.Sphere(20).Groups(Set, reachM));
    }

    private static SubscriptionsOptions Options(int sessions, int keysPerSession, double cellM) => new()
    {
        IngressBytesPerSecond = TestIngress.Budget,
        MaxSessions = sessions + 4,
        ReplicationCellM = cellM,
        MaxGroupsPerSession = Math.Max(4, keysPerSession),
        StatePoolBudgetBytes = 512L * 1024 * 1024,
        FramePoolBudgetBytes = 512L * 1024 * 1024,
    };

    /// <summary>Spawns <paramref name="count"/> regions of side <paramref name="sideM"/> uniformly over a square of half-side <paramref name="halfM"/>,
    /// one positionless member each.</summary>
    private static List<EntityId> Populate(DatabaseEngine dbe, Random random, int count, float halfM, float sideM)
    {
        var towns = new List<EntityId>(count);
        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < count; i++)
        {
            var x = ((float)random.NextDouble() * 2f - 1f) * halfM;
            var y = ((float)random.NextDouble() * 2f - 1f) * halfM;
            var key = 1 + i;
            towns.Add(tx.Spawn<GroupTown>(GroupTown.Zone.Set(new GroupZone { Key = key, Extent = GroupTestSchema.Square(x, y, sideM * 0.5f) })));
            tx.Spawn<GroupPositionless>(GroupPositionless.Ai.Set(new ProjAi { Template = (byte)(i & 0xFF) }),
                GroupPositionless.Key.Set(new GroupKey { Town = key }));
        }

        tx.Commit();
        return towns;
    }

    /// <summary>Moves the first <paramref name="moving"/> regions 2 m in a random direction.</summary>
    private static void Walk(DatabaseEngine dbe, Random random, List<EntityId> towns, int moving)
    {
        if (moving == 0)
        {
            return;
        }

        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < moving; i++)
        {
            var zone = tx.Open(towns[i]).Read(GroupTown.Zone);
            var angle = random.NextDouble() * Math.PI * 2;
            var dx = (float)(Math.Cos(angle) * 2d);
            var dy = (float)(Math.Sin(angle) * 2d);
            var e = zone.Extent;
            zone.Extent = new AABB2F { MinX = e.MinX + dx, MinY = e.MinY + dy, MaxX = e.MaxX + dx, MaxY = e.MaxY + dy };
            tx.OpenMut(towns[i]).Set(GroupTown.Zone, zone);
        }

        tx.Commit();
    }

    private Result Run(string name, int regions, double movingFraction, int sessions, float halfM, float sideM, double reachM, double leaveM, double cellM,
        float sessionSpreadM, int seed)
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        var random = new Random(seed);
        var towns = Populate(dbe, random, regions, halfM, sideM);
        var moving = (int)(regions * movingFraction);

        using var harness = FrameHarness.Create(dbe, s => Declare(s, reachM, leaveM), $"scale-{name}", Options(sessions, regions, cellM));
        harness.RunFence = true;
        var ids = harness.OpenSessions(sessions, "p");
        foreach (var id in ids)
        {
            var x = ((random.NextDouble() * 2d) - 1d) * sessionSpreadM;
            var y = ((random.NextDouble() * 2d) - 1d) * sessionSpreadM;
            harness.Sessions.SetViewpoint(id, new Vector3D(x, y, 0d));
        }

        var push = harness.Subscriptions.Push;
        var hub = harness.Subscriptions.Hub;
        var times = new List<double>();
        long regionFrom = 0, wantedFrom = 0, refileFrom = 0;
        var tick = 0L;
        for (var t = 0; t < WarmTicks + MeasureTicks; t++)
        {
            Walk(dbe, random, towns, moving);
            if (t == WarmTicks)
            {
                regionFrom = push.RegionRefreshTicks;
                wantedFrom = hub.WantedTicks;
                refileFrom = push.RegionRefiles;
            }

            var start = Stopwatch.GetTimestamp();
            harness.RunTick(++tick);
            var elapsed = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
            foreach (var id in ids)
            {
                harness.Deliver(id);
            }

            if (t >= WarmTicks)
            {
                times.Add(elapsed);
            }
        }

        times.Sort();
        var scale = 1_000_000d / Stopwatch.Frequency / MeasureTicks;
        var held = hub.WantedForTest(0).Length;
        return new Result(name, times[times.Count / 2], (push.RegionRefreshTicks - regionFrom) * scale, (hub.WantedTicks - wantedFrom) * scale,
            push.RegionRefiles - refileFrom, held);
    }

    private static void Print(Result r) => TestContext.Out.WriteLine(
        $"{r.Name,-28} tick {r.TickUs,10:F0} µs   regions {r.RegionUs,9:F0} µs/tick ({r.Refiles,6} refiles)   "
        + $"wanted {r.WantedUs,9:F0} µs/tick   distinct wanted keys {r.KeysHeld,7:F0}");

    /// <summary>#1409: N regions over 4 km, a percentage of them walking; 8 sessions. One case per configuration: an engine per test.</summary>
    [TestCase(256, 0)]
    [TestCase(256, 10)]
    [TestCase(256, 100)]
    [TestCase(2048, 0)]
    [TestCase(2048, 10)]
    [TestCase(2048, 100)]
    [TestCase(16384, 0)]
    [TestCase(16384, 10)]
    [TestCase(16384, 100)]
    public void MovingRegions(int regions, int movingPercent) =>
        Print(Run($"regions {regions} moving {movingPercent}%", regions, movingPercent / 100d, sessions: 8, halfM: 2000f, sideM: 20f, reachM: 50d,
            leaveM: 50d, cellM: 40d, sessionSpreadM: 2000f, seed: 1409));

    /// <summary>#1410: every session within reach of every region, so W = sessions × keys; the regions are static.</summary>
    [TestCase(64)]
    [TestCase(2048)]
    [TestCase(16384)]
    public void ManyHeldKeys(int keys) =>
        Print(Run($"100 sessions x {keys} keys", keys, 0d, sessions: 100, halfM: 100f, sideM: 1f, reachM: 300d, leaveM: 50d, cellM: 100d, sessionSpreadM: 10f,
            seed: 1410));
}
