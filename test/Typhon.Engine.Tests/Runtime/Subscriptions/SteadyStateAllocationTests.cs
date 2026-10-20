using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Numerics;
using Typhon.Engine.Tests.Runtime.Subscriptions.Oracle;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime.Subscriptions;

/// <summary>
/// AC-6's engine half, under push (#1040, 08 § 2.3): no managed allocation in the replication steady state (SUB-07).
/// </summary>
/// <remarks>
/// <para>
/// <b>Counted per thread, so it can be a test.</b> 08 § 2.3's first measurement used the process-wide counter, which the background threads of earlier
/// fixtures' engines pollute, and was kept out of the suite for that reason. The harness runs the whole track — the blocks step, the projection, the push
/// index, the frames — on the calling thread with one worker, so <see cref="GC.GetAllocatedBytesForCurrentThread"/> around it counts the replication's
/// allocations and nobody else's.
/// </para>
/// <para>
/// <b>The fence is measured beside it, not in it.</b> The engine's tick fence runs on the same thread in the runtime, and #1396 records that it allocates;
/// it is the ECS's, not replication's, so it is counted separately and reported, and the assertion is on the track alone.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class SteadyStateAllocationTests : TestBase<SteadyStateAllocationTests>
{
    private const string Profile = "god-world";
    private const int Creatures = 128;
    private const int Sessions = 8;

    private static void Declare(SubscriptionsRegistry subs)
    {
        ProjectionTestSchema.DeclareCreature(subs);
        subs.Profile(Profile, p => p.World().Of<ProjCreature>());
    }

    private static ProjBounds At(float x, float y) => new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Speed = 1f };

    private static void Move(FrameHarness harness, long tick)
    {
        using var tx = harness.Engine.CreateQuickTransaction();
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
                    var b = cluster.GetReadOnly(ProjCreature.Bounds, slot).Bounds;
                    var angle = (tick * 0.7) + (slot * 1.3);
                    cluster.WriteSpatial(ProjCreature.Bounds, slot, At(b.MinX + (float)Math.Cos(angle), b.MinY + (float)Math.Sin(angle)));
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        tx.Commit();
    }

    /// <summary>
    /// Eight world sessions over 128 creatures that all move every tick, every frame delivered: after a warm-up, a thousand ticks of the track allocate
    /// nothing on the managed heap.
    /// </summary>
    /// <remarks>
    /// The warm-up is 128 ticks because the track's last first-time allocation lands on tick 63 — 72 B, once, the first pass of a 64-tick cycle — and
    /// nothing after it in 3 000 ticks (measured 2026-10-19). Before #1040 this read 127 B/tick, all of it the harness's own ready list, which the runtime's
    /// send pump clears every tick and the harness did not.
    /// </remarks>
    [Test]
    [VerifiesRule("SUB-07")]
    public void TheTrackAllocatesNothingInTheSteadyState()
    {
        var (track, fence, ticks) = Measure(warmUp: 128, measured: 1000);
        TestContext.Out.WriteLine($"track {track} B over {ticks} ticks ({(double)track / ticks:F2} B/tick); "
            + $"fence {fence} B ({(double)fence / ticks:F0} B/tick, #1396)");
        Assert.That(track, Is.Zero, $"the replication track allocated {track} B over {ticks} ticks");
    }

    /// <summary>
    /// The same claim over the group paths (#1409, #1410), which the world profile above never runs: six sessions moving between twelve towns whose regions
    /// shift every tick, holding and releasing lazy towns' shards — the region index re-filing, the held keys syncing, the wanted counts rising and falling.
    /// </summary>
    /// <remarks>
    /// The pattern repeats every 120 ticks (each session moves to the next town every ten), so two cycles of warm-up bring every pool and table to its
    /// high-water mark and the measured thousand ticks replay states it has already held.
    /// </remarks>
    [Test]
    [VerifiesRule("SUB-07")]
    public void TheGroupPathsAllocateNothingInTheSteadyState()
    {
        const int warmUp = 240;
        const int measured = 1000;
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        var towns = new List<EntityId>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var key = 1; key <= 12; key++)
            {
                var (x, y) = TownAt(key);
                towns.Add(tx.Spawn<GroupTown>(GroupTown.Zone.Set(new GroupZone { Key = key, Extent = GroupTestSchema.Square(x, y, 40f) })));
                for (var i = 0; i < 3; i++)
                {
                    tx.Spawn<GroupPositionless>(GroupPositionless.Ai.Set(new ProjAi { Template = (byte)((key * 3) + i) }),
                        GroupPositionless.Key.Set(new GroupKey { Town = key }));
                }
            }

            tx.Commit();
        }

        using var harness = FrameHarness.Create(dbe, subs => GroupTestSchema.Declare(subs, 50d, 80d, 20d, shards: true),
            nameof(TheGroupPathsAllocateNothingInTheSteadyState), new SubscriptionsOptions
            {
                IngressBytesPerSecond = TestIngress.Budget,
                MaxSessions = 8,
                MaxGroupsPerSession = 16,
                ReplicationCellM = ProjectionTestSchema.ReplicationCellFor(50d),
                StatePoolBudgetBytes = 16L * 1024 * 1024,
                FramePoolBudgetBytes = 16L * 1024 * 1024,
            });
        harness.RunFence = false;
        harness.DrainNetIds = true;

        var sessions = harness.OpenSessions(6, GroupTestSchema.Profile);
        long track = 0;
        for (long tick = 1; tick <= warmUp + measured; tick++)
        {
            // Outside the measured window: the towns' regions shift (a re-file whenever they cross a cell) and the sessions change town every ten ticks.
            using (var tx = dbe.CreateQuickTransaction())
            {
                for (var t = 0; t < towns.Count; t++)
                {
                    var (x, y) = TownAt(t + 1);
                    var dx = (float)(30d * Math.Sin((tick * 0.05) + t));
                    tx.OpenMut(towns[t]).Set(GroupTown.Zone, new GroupZone { Key = t + 1, Extent = GroupTestSchema.Square(x + dx, y, 40f) });
                }

                tx.Commit();
            }

            if (tick % 10 == 1)
            {
                for (var i = 0; i < sessions.Length; i++)
                {
                    var (x, y) = TownAt((int)(((tick / 10) + i) % 12) + 1);
                    harness.Sessions.SetViewpoint(sessions[i], new Vector3D(x, y, 0d));
                }
            }

            harness.Engine.WriteTickFence(tick);
            var before = GC.GetAllocatedBytesForCurrentThread();
            harness.RunTick(tick);
            var after = GC.GetAllocatedBytesForCurrentThread();
            foreach (var session in sessions)
            {
                harness.Drain(session);
            }

            if (tick > warmUp)
            {
                track += after - before;
            }
        }

        TestContext.Out.WriteLine($"group track {track} B over {measured} ticks ({(double)track / measured:F2} B/tick)");
        Assert.That(track, Is.Zero, $"the group paths allocated {track} B over {measured} ticks");
    }

    private static (float X, float Y) TownAt(int key) => ((float)(600d * Math.Cos(key * Math.PI / 6d)), (float)(600d * Math.Sin(key * Math.PI / 6d)));

    private (long Track, long Fence, int Ticks) Measure(int warmUp, int measured)
    {
        using var harness = FrameHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), Declare, nameof(SteadyStateAllocationTests),
            new SubscriptionsOptions
            {
                MaxSessions = Sessions + 2,
                StatePoolBudgetBytes = 16L * 1024 * 1024,
                FramePoolBudgetBytes = 16L * 1024 * 1024,
                ReplicationCellM = ProjectionTestSchema.ReplicationCellFor(0),
            });
        harness.RunFence = false;

        using (var tx = harness.Engine.CreateQuickTransaction())
        {
            for (var i = 0; i < Creatures; i++)
            {
                var bounds = At(i % 16 * 30f, i / 16 * 30f);
                tx.Spawn<ProjCreature>(ProjCreature.Bounds.Set(in bounds), ProjCreature.Ai.Set(new ProjAi { Template = (byte)i }));
            }

            tx.Commit();
        }

        var sessions = harness.OpenSessions(Sessions, Profile);
        long track = 0;
        long fence = 0;
        for (long tick = 1; tick <= warmUp + measured; tick++)
        {
            Move(harness, tick);

            var before = GC.GetAllocatedBytesForCurrentThread();
            harness.Engine.WriteTickFence(tick);
            var afterFence = GC.GetAllocatedBytesForCurrentThread();
            harness.RunTick(tick);
            var afterTrack = GC.GetAllocatedBytesForCurrentThread();

            foreach (var session in sessions)
            {
                harness.Drain(session);
            }

            if (tick > warmUp)
            {
                fence += afterFence - before;
                track += afterTrack - afterFence;
            }
        }

        return (track, fence, measured);
    }
}
