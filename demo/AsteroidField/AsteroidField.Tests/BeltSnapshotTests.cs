using System;
using System.Threading.Tasks;
using AsteroidField.Client;
using AsteroidField.Server;
using NUnit.Framework;

namespace AsteroidField.Tests;

/// <summary>
/// What the Unity client draws: an entity at render time on the segment in force then, a ship's nose on the heading it had then, and snapshots the
/// link refills instead of allocating.
/// </summary>
[TestFixture]
public class BeltSnapshotTests
{
    // Two segments of one entity: east from tick 10, then a correction at tick 20 that puts it at (5, 5) heading north.
    private static (BeltSnapshot World, EntityView Entity) TwoSegments(byte secondEpoch = 0)
    {
        var world = new BeltSnapshot();
        world.Segments.Add(new MotionSegment { T0 = 10, X = 0, Y = 0, Vx = 1, Vy = 0 });
        world.Segments.Add(new MotionSegment { T0 = 20, Epoch = secondEpoch, X = 5, Y = 5, Vx = 0, Vy = 1 });
        return (world, new EntityView { SegmentFirst = 0, SegmentCount = 2, X0 = 5, Y0 = 5, Vy = 1, T0 = 20 });
    }

    [TestCase(12, 0.5, 2.5, 0.0, TestName = "On the older segment, extrapolated along it")]
    [TestCase(19, 0.5, 7.0, 2.5, TestName = "The last tick before a correction is carried onto the new origin")]
    [TestCase(25, 0.0, 5.0, 10.0, TestName = "Past the correction, on the newer segment")]
    [TestCase(5, 0.0, -5.0, 0.0, TestName = "Before every segment, the oldest one backwards")]
    public void APositionIsTakenOnTheSegmentInForceAtRenderTime(long renderTick, double frac, double x, double y)
    {
        var (world, e) = TwoSegments();

        world.PositionAt(in e, renderTick, frac, out var px, out var py);

        Assert.That((px, py), Is.EqualTo((x, y)));
    }

    [Test]
    public void ATeleportIsNotBlendedAcross()
    {
        var (world, e) = TwoSegments(secondEpoch: 1);

        world.PositionAt(in e, 19, 0.5, out var x, out var y);

        Assert.That((x, y), Is.EqualTo((9.5, 0.0)), "the old segment, extrapolated, until the teleport's tick");
    }

    [Test]
    public void TheNewestSegmentExtrapolatedBackwardsWouldCutTheCorner()
    {
        // The bug this replaced: the head alone puts the entity at (5, -1.5) at tick 13.5, off a path that never left y = 0 before tick 20.
        var (world, e) = TwoSegments();

        world.PositionAt(in e, 13, 0.5, out var x, out var y);
        e.PositionAt(13.5, out var headX, out var headY);

        Assert.Multiple(() =>
        {
            Assert.That((x, y), Is.EqualTo((3.5, 0.0)));
            Assert.That((headX, headY), Is.EqualTo((5.0, -1.5)));
        });
    }

    [TestCase(9, 0.1)]
    [TestCase(11, 0.1)]
    [TestCase(12, 0.5)]
    [TestCase(30, 0.5)]
    public void AHeadingIsTheOneRenderTimeHasReached(long renderTick, double heading)
    {
        var world = new BeltSnapshot();
        world.Headings.Add(new HeadingSample { Tick = 10, Heading = 0.1 });
        world.Headings.Add(new HeadingSample { Tick = 12, Heading = 0.5 });
        var ship = new EntityView { HeadingFirst = 0, HeadingCount = 2, Heading = 0.5 };

        Assert.That(world.HeadingAt(in ship, renderTick), Is.EqualTo(heading));
    }

    [Test]
    public async Task EveryEntityCarriesTheSegmentsItIsDrawnFromAndARecycledSnapshotIsRefilled()
    {
        using var server = AsteroidServer.Start(new ServerOptions { Port = 0, Asteroids = 2000, Drones = 2, PilotSpawnRadiusM = 20f });
        await using var link = new BeltLink("127.0.0.1", server.Port);
        await link.ConnectAsync();

        var first = await Take(link, w => w.TryGetShip(w.SelfNetId, out _) && w.Asteroids.Count > 0);
        var firstTick = first.Tick;
        foreach (var e in first.Asteroids)
        {
            // At the newest tick the newest segment is the one in force: both ways of reading the position agree.
            Assert.That(e.SegmentCount, Is.GreaterThanOrEqualTo(1), $"asteroid {e.NetId}");
            first.PositionAt(in e, first.Tick, 0, out var x, out var y);
            e.PositionAt(first.Tick, out var headX, out var headY);
            Assert.That(Math.Abs(x - headX) + Math.Abs(y - headY), Is.LessThan(1e-9), $"asteroid {e.NetId}");
        }

        Assert.That(first.TryGetShip(first.SelfNetId, out var self) && self.HeadingCount >= 1, Is.True, "a ship carries its headings");

        link.Recycle(first);
        var refilled = await Take(link, w => ReferenceEquals(w, first));
        Assert.Multiple(() =>
        {
            Assert.That(refilled.Tick, Is.GreaterThan(firstTick), "refilled from a later frame");
            Assert.That(refilled.Asteroids, Is.Not.Empty);
            Assert.That(refilled.Segments.Count, Is.GreaterThanOrEqualTo(refilled.Asteroids.Count + refilled.Ships.Count));
        });
    }

    private static async Task<BeltSnapshot> Take(BeltLink link, Func<BeltSnapshot, bool> condition)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < until)
        {
            if (link.TryTakeLatest(out var world) && condition(world))
            {
                return world;
            }

            await Task.Delay(10);
        }

        Assert.Fail("timed out");
        return null;
    }
}
