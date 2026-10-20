using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests.Runtime.Subscriptions.Oracle;

/// <summary>
/// #1407's cost, measured: sessions each holding H tiny towns of one building, every key complete — the steady state — and the frame stage's gather per
/// session and tick. A session's group work must grow with the keys it holds, never quadratically.
/// </summary>
/// <remarks>
/// H comes from <c>TYPHON_DENSE_H</c> (comma-separated, default 512,1024,2048,16384), so the same file runs against a build whose bound stops at 1 024.
/// Each H runs <see cref="Rounds"/> times on a fresh runtime over its own engine; reported: the gather's µs per session-tick, median of the rounds.
/// </remarks>
[TestFixture]
[Explicit("A measurement, not a check: run by hand")]
[Category("Manual")]
[NonParallelizable]
[Property("CacheSize", 256 * 1024 * 1024)]
class GroupDenseBench : TestBase<GroupDenseBench>
{
    private const int Sessions = 8;
    private const int MeasureTicks = 100;
    private const int Rounds = 5;

    [TearDown]
    public void StopPhaseTiming() => FrameAssembler.PhaseTimingEnabled = false;

    [Test]
    public void TheSteadyStateGatherPerSessionGrowsWithTheKeysHeld()
    {
        var hs = (Environment.GetEnvironmentVariable("TYPHON_DENSE_H") ?? "512,1024,2048,16384").Split(',').Select(int.Parse).ToArray();
        // One engine per test: each H's towns in an area of their own, 3 km apart, keyed from their own base.
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        var areas = new (float X, int KeyBase)[hs.Length];
        for (var j = 0; j < hs.Length; j++)
        {
            areas[j] = (-6000f + (j * 3000f), (j + 1) * 1_000_000);
            Populate(dbe, hs[j], areas[j].X, areas[j].KeyBase);
        }

        var lines = new List<string>();
        for (var j = 0; j < hs.Length; j++)
        {
            var h = hs[j];
            var samples = new List<double>();
            for (var round = 0; round < Rounds; round++)
            {
                samples.Add(Measure(dbe, h, areas[j].X, round));
            }

            samples.Sort();
            lines.Add($"H={h,6}: gather {samples[samples.Count / 2],9:F1} µs/session-tick (min {samples[0]:F1}, max {samples[^1]:F1})");
        }

        TestContext.Out.WriteLine(string.Join(Environment.NewLine, lines));
    }

    private static void Populate(DatabaseEngine dbe, int h, float cx, int keyBase)
    {
        var side = (int)Math.Ceiling(Math.Sqrt(h));
        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < h; i++)
        {
            var x = cx + ((i % side) * 2f) - side;
            var y = ((i / side) * 2f) - side;
            tx.Spawn<GroupTown>(GroupTown.Zone.Set(new GroupZone { Key = keyBase + i, Extent = GroupTestSchema.Square(x, y, 0.5f) }));
            var bounds = GroupTestSchema.At(x, y);
            var ai = new ProjAi { Template = (byte)i };
            tx.Spawn<GroupBuilding>(GroupBuilding.Bounds.Set(in bounds), GroupBuilding.Ai.Set(in ai),
                GroupBuilding.Key.Set(new GroupKey { Town = keyBase + i }));
        }

        tx.Commit();
    }

    private double Measure(DatabaseEngine dbe, int h, float cx, int round)
    {
        var side = (int)Math.Ceiling(Math.Sqrt(h));
        var reach = side * 1.5d;
        using var harness = FrameHarness.Create(dbe, s => GroupTestSchema.Declare(s, 120d, reach, 20d), $"dense-{h}-{round}", new SubscriptionsOptions
        {
            IngressBytesPerSecond = TestIngress.Budget,
            MaxSessions = Sessions + 4,
            ReplicationCellM = ProjectionTestSchema.ReplicationCellFor(120d),
            MaxGroupsPerSession = h,
            EnterBudgetPerFrame = 2000,
            StatePoolBudgetBytes = 256L * 1024 * 1024,
            FramePoolBudgetBytes = 256L * 1024 * 1024,
        });
        harness.RunFence = true;
        var sessions = harness.OpenSessions(Sessions, GroupTestSchema.Profile);
        foreach (var session in sessions)
        {
            harness.Sessions.SetViewpoint(session, new Vector3D(cx, 0d, 0d));
        }

        var tick = 0L;
        var fill = (h / 2000) + 4;
        FrameAssembler.PhaseTimingEnabled = true;
        var before = default((double Gather, double Sort, double Encode, double Publish));
        for (var t = 0; t < fill + MeasureTicks; t++)
        {
            if (t == fill)
            {
                before = harness.Assembler.PhaseMilliseconds;
            }

            tick++;
            harness.RunTick(tick);
            foreach (var session in sessions)
            {
                harness.Deliver(session);
            }
        }

        var after = harness.Assembler.PhaseMilliseconds;
        var held = sessions.Min(s => harness.Replica(s).Store.CompleteGroupCount);
        Assert.That(held, Is.EqualTo(h), "precondition: every session holds every town, complete");
        return (after.Gather - before.Gather) * 1000d / (MeasureTicks * Sessions);
    }
}
