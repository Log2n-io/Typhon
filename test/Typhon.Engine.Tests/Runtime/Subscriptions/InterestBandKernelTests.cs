using NUnit.Framework;
using System;
using System.Runtime.Intrinsics;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// <see cref="InterestBandKernel"/>: every width against the reference arithmetic — the candidate filter's, applied entity by entity — on columns seeded
/// with NaN, infinite, inverted and huge bounds, and on sparse and dense occupancies.
/// </summary>
[TestFixture]
class InterestBandKernelTests
{
    /// <summary>The arithmetic <c>HitArena.FilterCandidatesInto</c> applies to one candidate, after the broad phase's degenerate skip.</summary>
    private static (ulong Far, ulong Near) Reference(float[] columns, ulong occupancy, double cx, double cy, double leave, double enterSq)
    {
        var leaveSq = leave * leave;
        ulong far = 0, near = 0;
        for (var slot = 0; slot < 64; slot++)
        {
            if ((occupancy & (1UL << slot)) == 0)
            {
                continue;
            }

            double minX = columns[slot], minY = columns[64 + slot], maxX = columns[128 + slot], maxY = columns[192 + slot];
            if (!(minX <= maxX) || !(minY <= maxY))
            {
                continue;
            }

            if (maxX < cx - leave || minX > cx + leave || maxY < cy - leave || minY > cy + leave)
            {
                continue;
            }

            var dx = double.MaxNative(0d, double.MaxNative(minX - cx, cx - maxX));
            var dy = double.MaxNative(0d, double.MaxNative(minY - cy, cy - maxY));
            var distSq = (dx * dx) + (dy * dy);
            if (distSq > leaveSq)
            {
                continue;
            }

            far |= 1UL << slot;
            if (distSq <= enterSq)
            {
                near |= 1UL << slot;
            }
        }

        return (far, near);
    }

    private static float Special(Random random, float regular) => random.Next(40) switch
    {
        0 => float.NaN,
        1 => float.PositiveInfinity,
        2 => float.NegativeInfinity,
        3 => 6.9e10f,
        _ => regular,
    };

    [Test]
    public void EveryWidthMatchesTheCandidateFiltersArithmetic()
    {
        var random = new Random(23);
        var columns = new float[InterestBandKernel.ColumnFloats];
        var checkedPairs = 0;
        var farHits = 0;
        var nearHits = 0;
        for (var round = 0; round < 4000; round++)
        {
            for (var slot = 0; slot < 64; slot++)
            {
                var x = (float)(random.NextDouble() * 600d);
                var y = (float)(random.NextDouble() * 600d);
                var w = (float)(random.NextDouble() * (round % 3 == 0 ? 0d : 6d));
                var h = (float)(random.NextDouble() * 6d);

                // One in eight boxes inverted on some axis: the degenerate rule must drop it at every width.
                var inverted = random.Next(8) == 0;
                columns[slot] = Special(random, x);
                columns[64 + slot] = Special(random, y);
                columns[128 + slot] = Special(random, inverted ? x - 1f : x + w);
                columns[192 + slot] = Special(random, y + h);
            }

            ulong occupancy = (round % 4) switch
            {
                0 => ulong.MaxValue,
                1 => (1UL << random.Next(1, 64)) - 1,
                2 => (ulong)random.NextInt64() ^ ((ulong)random.NextInt64() << 1),
                _ => 1UL << random.Next(64),
            };
            var cx = random.NextDouble() * 600d;
            var cy = random.NextDouble() * 600d;
            var leave = 20d + (random.NextDouble() * 250d);
            var enter = leave * (0.7d + (random.NextDouble() * 0.3d));
            var enterSq = enter * enter;

            var (far, near) = Reference(columns, occupancy, cx, cy, leave, enterSq);
            farHits += System.Numerics.BitOperations.PopCount(far);
            nearHits += System.Numerics.BitOperations.PopCount(near);

            var scalar = InterestBandKernel.MatchScalar(ref columns[0], occupancy, cx, cy, leave, enterSq, out var scalarNear);
            Assert.That((scalar, scalarNear), Is.EqualTo((far, near)), $"scalar, round {round}");

            if (Vector256.IsHardwareAccelerated)
            {
                var v256 = InterestBandKernel.Match256(ref columns[0], occupancy, cx, cy, leave, enterSq, out var near256);
                Assert.That((v256, near256), Is.EqualTo((far, near)), $"256-bit, round {round}");
            }

            if (Vector512.IsHardwareAccelerated)
            {
                var v512 = InterestBandKernel.Match512(ref columns[0], occupancy, cx, cy, leave, enterSq, out var near512);
                Assert.That((v512, near512), Is.EqualTo((far, near)), $"512-bit, round {round}");
            }

            checkedPairs++;
        }

        // Anti-vacuity: the seeding must produce both answers in quantity, and a near set strictly smaller than the far one.
        Assert.That(checkedPairs, Is.EqualTo(4000));
        Assert.That(farHits, Is.GreaterThan(10_000), "too few entities matched for the comparison to mean anything");
        Assert.That(nearHits, Is.GreaterThan(1_000).And.LessThan(farHits), "the enter radius never narrowed the answer");
    }

    /// <summary>
    /// The boundaries, pinned by hand rather than by the reference: an entity exactly at the leave radius is in, exactly at the enter radius is near, and a
    /// box whose edge lies exactly on the query box's edge is not rejected by it. Random doubles almost never land on equality, so a <c>&lt;</c> turned into
    /// <c>&lt;=</c> in either would pass the comparison above.
    /// </summary>
    [Test]
    public void AnEntityExactlyOnARadiusIsInside()
    {
        const double cx = 10d, cy = 20d, leave = 3d, enter = 2d;
        var columns = new float[InterestBandKernel.ColumnFloats];

        void Box(int slot, float minX, float minY, float maxX, float maxY)
        {
            columns[slot] = minX;
            columns[64 + slot] = minY;
            columns[128 + slot] = maxX;
            columns[192 + slot] = maxY;
        }

        Box(0, 13f, 20f, 13f, 20f);                      // on the leave radius, along X: far
        Box(1, 6f, 20f, 7f, 20f);                        // max X on the query box's min X, distance 3: far
        Box(2, 10f, 22f, 10f, 22f);                      // on the enter radius: near
        Box(3, 13.000001f, 20f, 13.000001f, 20f);        // one float step beyond the leave radius: out
        Box(4, 12f, 21f, 14f, 23f);                      // closest corner at distance² 5: far, not near
        Box(5, 11f, 20f, 10.9f, 20f);                    // inverted: dropped
        Box(6, float.NaN, 20f, 11f, 20f);                // NaN bound: dropped
        Box(7, 9f, 19f, 11f, 21f);                       // contains the viewpoint: near
        Box(8, 10f, 16f, 10f, 17f);                      // max Y on the query box's min Y, distance 3: far
        const ulong occupancy = (1UL << 9) - 1;
        const ulong expectedFar = (1UL << 0) | (1UL << 1) | (1UL << 2) | (1UL << 4) | (1UL << 7) | (1UL << 8);
        const ulong expectedNear = (1UL << 2) | (1UL << 7);

        Assert.That(Reference(columns, occupancy, cx, cy, leave, enter * enter), Is.EqualTo((expectedFar, expectedNear)), "reference");
        var scalar = InterestBandKernel.MatchScalar(ref columns[0], occupancy, cx, cy, leave, enter * enter, out var scalarNear);
        Assert.That((scalar, scalarNear), Is.EqualTo((expectedFar, expectedNear)), "scalar");
        if (Vector256.IsHardwareAccelerated)
        {
            var v256 = InterestBandKernel.Match256(ref columns[0], occupancy, cx, cy, leave, enter * enter, out var near256);
            Assert.That((v256, near256), Is.EqualTo((expectedFar, expectedNear)), "256-bit");
        }

        if (Vector512.IsHardwareAccelerated)
        {
            var v512 = InterestBandKernel.Match512(ref columns[0], occupancy, cx, cy, leave, enter * enter, out var near512);
            Assert.That((v512, near512), Is.EqualTo((expectedFar, expectedNear)), "512-bit");
        }
    }

    [Test]
    public void AnEmptyOccupancyReadsNothingAndMatchesNothing()
    {
        var columns = new float[InterestBandKernel.ColumnFloats];
        var far = InterestBandKernel.Match(ref columns[0], 0UL, 0d, 0d, 100d, 100d, out var near);
        Assert.That((far, near), Is.EqualTo((0UL, 0UL)));
    }
}
