using System;
using System.Collections.Generic;
using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// The VDB root key's packing (#872 step 8, AC-8.3): three signed 21-bit block coordinates in one <see cref="long"/>.
/// </summary>
/// <remarks>
/// Its failure mode is why this has its own fixture. A truncated axis does not produce an obviously wrong key — it produces a <b>valid</b> key belonging to
/// a different region, so the offending block silently aliases another one and every query over either returns the other's clusters. That is an
/// <c>SQ-01</c> false negative with no exception anywhere near it.
/// </remarks>
[TestFixture]
class VdbBlockKeyTests
{
    [Test]
    [CancelAfter(15_000)]
    public void Pack_RoundTripsAtTheExtremes_IncludingNegatives()
    {
        int[] axis = [VdbBlockKey.MinCoord, VdbBlockKey.MinCoord + 1, -1, 0, 1, VdbBlockKey.MaxCoord - 1, VdbBlockKey.MaxCoord];
        foreach (int x in axis)
        {
            foreach (int y in axis)
            {
                foreach (int z in axis)
                {
                    Assert.That(VdbBlockKey.Unpack(VdbBlockKey.Pack(x, y, z)), Is.EqualTo((x, y, z)), $"({x}, {y}, {z})");
                }
            }
        }
    }

    [Test]
    [CancelAfter(15_000)]
    public void Pack_IsInjective_OverARandomisedSample()
    {
        // Round-tripping is necessary but not sufficient: a decode could undo an encode that had already collided with another coordinate. Injectivity is
        // the property the root map actually needs.
        var rng = new Random(9021);
        var seen = new Dictionary<long, (int, int, int)>();
        var coords = new HashSet<(int, int, int)>();
        for (int i = 0; i < 20_000; i++)
        {
            var coord = (rng.Next(VdbBlockKey.MinCoord, VdbBlockKey.MaxCoord),
                         rng.Next(VdbBlockKey.MinCoord, VdbBlockKey.MaxCoord),
                         rng.Next(VdbBlockKey.MinCoord, VdbBlockKey.MaxCoord));
            long key = VdbBlockKey.Pack(coord.Item1, coord.Item2, coord.Item3);
            if (seen.TryGetValue(key, out var other))
            {
                Assert.That(other, Is.EqualTo(coord), $"key {key} names two different blocks: {other} and {coord}");
            }
            seen[key] = coord;
            coords.Add(coord);
        }

        // Without this the loop asserts NOTHING when no collision happens, which is the expected outcome — a green test that ran no comparison. Counting
        // distinct keys against distinct coordinates is the same property stated so that it always has something to check.
        Assert.That(seen.Count, Is.EqualTo(coords.Count), "distinct coordinates must produce distinct keys");
    }

    [Test]
    [CancelAfter(15_000)]
    public void Pack_AdjacentCoordinatesNeverAlias()
    {
        // The specific shape a shift-width mistake produces: the axis next door bleeding into this one's field.
        long origin = VdbBlockKey.Pack(0, 0, 0);
        Assert.That(VdbBlockKey.Pack(1, 0, 0), Is.Not.EqualTo(origin));
        Assert.That(VdbBlockKey.Pack(0, 1, 0), Is.Not.EqualTo(origin));
        Assert.That(VdbBlockKey.Pack(0, 0, 1), Is.Not.EqualTo(origin));
        Assert.That(VdbBlockKey.Pack(-1, 0, 0), Is.Not.EqualTo(VdbBlockKey.Pack(0, -1, 0)));
        Assert.That(VdbBlockKey.Pack(0, 0, -1), Is.Not.EqualTo(VdbBlockKey.Pack(-1, 0, 0)));
    }

    [Test]
    [CancelAfter(15_000)]
    public void Pack_OutOfRange_ThrowsRatherThanTruncating()
    {
        foreach (int bad in new[] { VdbBlockKey.MaxCoord + 1, VdbBlockKey.MinCoord - 1, int.MaxValue, int.MinValue })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => VdbBlockKey.Pack(bad, 0, 0), $"x = {bad}");
            Assert.Throws<ArgumentOutOfRangeException>(() => VdbBlockKey.Pack(0, bad, 0), $"y = {bad}");
            Assert.Throws<ArgumentOutOfRangeException>(() => VdbBlockKey.Pack(0, 0, bad), $"z = {bad}");
        }
    }

    [Test]
    [CancelAfter(15_000)]
    public void Range_IsWideEnoughToOutliveTheWorldBoundsClamp()
    {
        // The packing is sized for the unbounded grid §3.2 describes, not for today's clamped one — which is what makes lifting the clamp a config change
        // rather than a re-encoding. At the default 16-cell block this is ±16.7 M cells per axis.
        Assert.That(VdbBlockKey.MaxCoord, Is.EqualTo(1_048_575));
        Assert.That(VdbBlockKey.MinCoord, Is.EqualTo(-1_048_576));
    }
}
