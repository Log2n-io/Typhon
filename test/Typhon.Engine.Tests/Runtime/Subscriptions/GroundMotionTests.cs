using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Typhon.Protocol;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>A strider's grounded state (#1616): a byte, non-zero while on the ground.</summary>
[Component("Typhon.Test.Proj.Gait", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
struct ProjGait
{
    [Field]
    public byte OnGround;
}

/// <summary>A 3D walker whose motion is ground-clamped (#1616): z is up, and a grounded segment omits it.</summary>
[Archetype]
partial class ProjStrider : Archetype<ProjStrider>
{
    public static readonly Comp<ProjBounds3> Bounds = Register<ProjBounds3>();
    public static readonly Comp<ProjGait> Gait = Register<ProjGait>();
}

/// <summary>
/// #1616: a ground-clamped motion sends a grounded entity's segments without the up axis — the client derives the height from its own terrain — and refits
/// them on the horizontal error only, so walking over bumps costs no segment; leaving or reaching the ground is a new segment, and an airborne one is full 3D.
/// </summary>
[TestFixture]
[NonParallelizable]
unsafe class GroundMotionTests : TestBase<GroundMotionTests>
{
    private const string Profile = "ground-world";
    private const int Striders = 16;
    private const int Ticks = 300;

    private static void Declare(SubscriptionsRegistry subs)
    {
        subs.Archetype<ProjStrider>(a => a.Motion(ProjStrider.Bounds, m => m.Tolerance(0.05).Teleport(ProjectionTestSchema.MaxSpeedMps)
            .Grounded(ProjStrider.Gait, g => g.OnGround, up: 2)));
        subs.Profile(Profile, p => p.World().Of<ProjStrider>());
    }

    /// <summary>
    /// Striders walk a straight line over bumps 0.5 m high: grounded, the segments stay few — the vertical never triggers one — and every grounded segment
    /// the client receives carries a NaN height; once they jump, the segments are full 3D with a real height.
    /// </summary>
    [Test]
    public void GroundedSegmentsOmitTheHeightAndIgnoreTheTerrain()
    {
        using var harness = FrameHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider, volumetric: true), Declare,
            nameof(GroundMotionTests));
        harness.RunFence = true;
        Spawn(harness);
        var session = harness.OpenSessions(1, Profile)[0];
        harness.RunTick(1);
        harness.Deliver(session);

        var state = harness.Subscriptions.ReplicationStates[harness.PlanIndex(nameof(ProjStrider))];
        var plan = harness.Subscriptions.CatalogPlan.ArchetypeByName(nameof(ProjStrider));
        Assert.That((plan.Position.Ground, plan.Position.UpAxis), Is.EqualTo((true, 2)), "the catalog names the ground mode and its up axis");

        var before = state.SegmentsEmitted;
        for (var tick = 2L; tick <= Ticks; tick++)
        {
            Walk(harness, tick, grounded: true);
            harness.RunTick(tick);
            harness.Deliver(session);
        }

        var grounded = state.SegmentsEmitted - before;
        var replica = harness.Replica(session);
        var heights = HeadHeights(replica, plan);
        TestContext.Out.WriteLine($"grounded over bumps: {grounded} segments for {Striders} striders over {Ticks - 1} ticks");

        // Then they jump: a change of state is a segment, and an airborne one carries its height.
        Walk(harness, Ticks + 1, grounded: false);
        harness.RunTick(Ticks + 1);
        harness.Deliver(session);
        var airborne = HeadHeights(harness.Replica(session), plan);

        Assert.Multiple(() =>
        {
            // What a grounded straight walk may cost: two refits while the velocity is acquired from the stationary enter, then the MaxAge heartbeats
            // (5 s = 50 ticks at 10 Hz). Ungrounded, these bumps — half a metre, every 7.9 m — break a 5 cm tolerance several times a wavelength, dozens
            // per strider over the run.
            Assert.That(grounded, Is.LessThanOrEqualTo(Striders * (2 + ((Ticks - 1) / 50))),
                "a segment beyond the enter and the heartbeats: the bumps' vertical error was not ignored on the ground");
            Assert.That(heights, Has.All.NaN, "a grounded segment reached the client with a height");
            Assert.That(airborne.Any(double.IsNaN), Is.False, "an airborne segment reached the client without its height");
            Assert.That(state.SegmentsEmitted - before - grounded, Is.GreaterThanOrEqualTo(Striders), "leaving the ground was not a new segment");
        });
    }

    private static List<double> HeadHeights(SessionReplica replica, ArchetypePlan plan)
    {
        var heights = new List<double>();
        var store = replica.Store.Archetypes[plan.Idx];
        foreach (var netId in replica.NetIds(plan.Idx))
        {
            replica.Store.TryLocate(netId, out _, out var slot);
            heights.Add(store.HeadPosition(slot)[2]);
        }

        return heights;
    }

    private static void Spawn(FrameHarness harness)
    {
        using var tx = harness.Engine.CreateQuickTransaction();
        for (var i = 0; i < Striders; i++)
        {
            var bounds = BoundsAt(-500f + (i * 40f), 0f, Height(0));
            var gait = new ProjGait { OnGround = 1 };
            tx.Spawn<ProjStrider>(ProjStrider.Bounds.Set(in bounds), ProjStrider.Gait.Set(in gait));
        }

        tx.Commit();
        harness.Engine.WriteTickFence(1);
    }

    // A straight walk along y at 4 m/s (10 Hz) over bumps: grounded, z follows the terrain; airborne, it leaves it by two metres.
    private static void Walk(FrameHarness harness, long tick, bool grounded)
    {
        using var tx = harness.Engine.CreateQuickTransaction();
        var accessor = tx.For<ProjStrider>();
        var index = 0;
        foreach (var cluster in accessor.GetClusterEnumerator())
        {
            var occupancy = cluster.OccupancyBits;
            while (occupancy != 0)
            {
                var slot = System.Numerics.BitOperations.TrailingZeroCount(occupancy);
                occupancy &= occupancy - 1;
                var y = (float)(tick * 0.4);
                var z = Height(y) + (grounded ? 0f : 2f);
                cluster.WriteSpatial(ProjStrider.Bounds, slot, BoundsAt(-500f + (index * 40f), y, z));
                cluster.GetSpan(ProjStrider.Gait)[slot].OnGround = grounded ? (byte)1 : (byte)0;
                index++;
            }
        }

        tx.Commit();
    }

    private static float Height(float y) => 0.5f * MathF.Sin(y * 0.8f);

    private static ProjBounds3 BoundsAt(float x, float y, float z) => new()
    {
        Bounds = new AABB3F { MinX = x, MinY = y, MinZ = z, MaxX = x + 1f, MaxY = y + 1f, MaxZ = z + 1f },
        Speed = 4f,
    };
}
