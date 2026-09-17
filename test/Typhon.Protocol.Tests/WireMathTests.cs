using System;
using NUnit.Framework;
using Typhon.Protocol;

namespace Typhon.Protocol.Tests;

/// <summary>
/// The codec arithmetic of decisions W1–W10, asserted as properties: the exact values a decoder must produce at each boundary, and the round trips an
/// encoder must preserve. The golden vectors pin the bytes; these pin the reasons.
/// </summary>
[TestFixture]
public class WireMathTests
{
    /// <summary>W1: ties round away from zero, never to even — C#'s default would send 2.5 to 2, and JavaScript's Math.round sends −2.5 to −2.</summary>
    [TestCase(2.5, 3)]
    [TestCase(-2.5, -3)]
    [TestCase(0.49999999999999994, 0)]
    [TestCase(-0.5, -1)]
    public void TiesRoundAwayFromZero(double x, double expected) => Assert.That(WireMath.RoundHalfAwayFromZero(x), Is.EqualTo(expected));

    /// <summary>W2: [min, max) with step (max − min) / 2ᵇ — zero exact on a symmetric range, max clamps to the top code, below min and NaN go to 0.</summary>
    [Test]
    public void QuantCoversTheHalfOpenRange()
    {
        const double min = -8192, max = 8192;
        const int bits = 24;
        var step = WireMath.QuantStep(min, max, bits);

        Assert.Multiple(() =>
        {
            Assert.That(step, Is.EqualTo(1.0 / 1024), "the SWG step is dyadic: 2⁻¹⁰ m");
            Assert.That(WireMath.EncodeQuant(0, min, max, bits), Is.EqualTo(8_388_608u));
            Assert.That(WireMath.DecodeQuant(8_388_608u, min, max, bits), Is.EqualTo(0.0));
            Assert.That(WireMath.EncodeQuant(min, min, max, bits), Is.Zero);
            Assert.That(WireMath.EncodeQuant(max, min, max, bits), Is.EqualTo((1u << 24) - 1));
            Assert.That(WireMath.DecodeQuant((1u << 24) - 1, min, max, bits), Is.EqualTo(max - step));
            Assert.That(WireMath.EncodeQuant(double.NaN, min, max, bits), Is.Zero);
            Assert.That(WireMath.EncodeQuant(double.NegativeInfinity, min, max, bits), Is.Zero);
            Assert.That(WireMath.EncodeQuant(double.PositiveInfinity, min, max, bits), Is.EqualTo((1u << 24) - 1));
        });
    }

    /// <summary>A dyadic step survives a float32 narrowing: decode, narrow, re-encode returns the original code.</summary>
    [Test]
    public void ADyadicQuantStepRoundTripsThroughFloat32()
    {
        var rng = new Random(957);
        for (var i = 0; i < 100_000; i++)
        {
            var code = (uint)rng.Next(0, 1 << 24);
            var narrowed = (float)WireMath.DecodeQuant(code, -8192, 8192, 24);
            Assert.That(WireMath.EncodeQuant(narrowed, -8192, 8192, 24), Is.EqualTo(code));
        }
    }

    /// <summary>W6: full and empty are exact — what a snapping bar must show.</summary>
    [Test]
    public void UnormAndSnormHitTheirEndsExactly()
    {
        Assert.Multiple(() =>
        {
            Assert.That(WireMath.DecodeUnorm(255, 8), Is.EqualTo(1.0));
            Assert.That(WireMath.DecodeUnorm(0, 8), Is.EqualTo(0.0));
            Assert.That(WireMath.EncodeUnorm(0.5, 8), Is.EqualTo(128u));
            Assert.That(WireMath.EncodeUnorm(1.5, 8), Is.EqualTo(255u));
            Assert.That(WireMath.DecodeSnorm(127, 8), Is.EqualTo(1.0));
            Assert.That(WireMath.DecodeSnorm(-127, 8), Is.EqualTo(-1.0));
            Assert.That(WireMath.DecodeSnorm(-128, 8), Is.EqualTo(-1.0), "−2ᵇ⁻¹ aliases −1");
            Assert.That(WireMath.EncodeSnorm(0, 8), Is.Zero);
        });
    }

    /// <summary>W4: the code −2ᵇ⁻¹ decodes as −(2ᵇ⁻¹ − 1), so negation stays exact.</summary>
    [Test]
    public void VecClampsSymmetrically()
    {
        Assert.Multiple(() =>
        {
            Assert.That(WireMath.EncodeVec(1000, 0.5, 8), Is.EqualTo(127));
            Assert.That(WireMath.EncodeVec(-1000, 0.5, 8), Is.EqualTo(-127));
            Assert.That(WireMath.DecodeVec(-128, 0.5, 8), Is.EqualTo(-63.5));
            Assert.That(WireMath.EncodeVec(0.25, 0.5, 8), Is.EqualTo(1), "a tie at half a step rounds away from zero");
        });
    }

    /// <summary>W7: π wraps to −π, and 2π to 0.</summary>
    [Test]
    public void AnglesWrapIntoMinusPiToPi()
    {
        Assert.Multiple(() =>
        {
            Assert.That(WireMath.EncodeAngle(Math.PI, 16), Is.EqualTo(-32768));
            Assert.That(WireMath.DecodeAngle(-32768, 16), Is.EqualTo(-Math.PI));
            Assert.That(WireMath.EncodeAngle(WireMath.Tau, 16), Is.Zero);
            Assert.That(WireMath.EncodeAngle(1e300, 16), Is.Zero, "beyond 2⁵³ the code is refused rather than wrapped differently per language");
            Assert.That(WireMath.EncodeAngle(double.NaN, 16), Is.Zero);
        });
    }

    /// <summary>W8: smallest-three at 32 bits stays within a quarter degree of the input rotation.</summary>
    [Test]
    public void Quat3IsWithinAQuarterDegree()
    {
        var rng = new Random(958);
        Span<double> q = stackalloc double[4];
        var worst = 0.0;
        for (var i = 0; i < 20_000; i++)
        {
            double x = rng.NextDouble() * 2 - 1, y = rng.NextDouble() * 2 - 1, z = rng.NextDouble() * 2 - 1, w = rng.NextDouble() * 2 - 1;
            var n = Math.Sqrt(x * x + y * y + z * z + w * w);
            x /= n;
            y /= n;
            z /= n;
            w /= n;
            WireMath.DecodeQuat3(WireMath.EncodeQuat3(x, y, z, w), q);
            var dot = Math.Abs(x * q[0] + y * q[1] + z * q[2] + w * q[3]);
            worst = Math.Max(worst, 2 * Math.Acos(Math.Min(1, dot)) * 180 / Math.PI);
        }

        Assert.That(worst, Is.LessThan(0.3));
    }

    /// <summary>W10: ties to even, overflow to infinity, NaN canonical — and never rounded twice through a float.</summary>
    [Test]
    public void HalfFloatsFollowIeee()
    {
        Assert.Multiple(() =>
        {
            Assert.That(WireMath.EncodeHalf(65504), Is.EqualTo((ushort)0x7BFF));
            Assert.That(WireMath.EncodeHalf(65520), Is.EqualTo((ushort)0x7C00));
            Assert.That(WireMath.EncodeHalf(double.NaN), Is.EqualTo((ushort)0x7E00));
            Assert.That(WireMath.EncodeHalf(1 + Math.Pow(2, -11)), Is.EqualTo((ushort)0x3C00), "the tie goes to even");
            Assert.That(WireMath.EncodeHalf(1.00048828125000022204), Is.EqualTo((ushort)0x3C01), "rounding once, from the double");
            Assert.That(double.IsNaN(WireMath.DecodeHalf(0xFE00)), Is.True);
        });
    }

    /// <summary>W9: tickLo rebuilds a past tick across the 16-bit wrap.</summary>
    [TestCase((ushort)65535, 65536u, 65535u)]
    [TestCase((ushort)0, 65536u, 65536u)]
    [TestCase((ushort)10, 5u, 4294901770u)]
    [TestCase((ushort)1, 131072u, 65537u)]
    public void TickLoRebuildsAPastTick(ushort low, uint frameTick, uint expected) =>
        Assert.That(WireMath.DecodeTickLo(low, frameTick), Is.EqualTo(expected));

    /// <summary>W5: velocity is exact at a dyadic step and a power-of-two divisor.</summary>
    [Test]
    public void VelocityDecodesExactlyInPositionSteps()
    {
        var step = WireMath.QuantStep(-8192, 8192, 24);

        Assert.Multiple(() =>
        {
            Assert.That(WireMath.EncodeVel(step, step, 16, 16), Is.EqualTo(16));
            Assert.That(WireMath.DecodeVel(16, step, 16, 16), Is.EqualTo(step));
            Assert.That(WireMath.EncodeVel(1e9, step, 16, 16), Is.EqualTo(32767));
        });
    }
}
