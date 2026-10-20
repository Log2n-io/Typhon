using System;
using System.Text.Json.Nodes;
using NUnit.Framework;
using Typhon.Protocol;

namespace Typhon.Client.Tests;

/// <summary>
/// The cross-language pin of 03 § 6's interpolation of an <c>angle</c> and a <c>quat3</c> (#1398): <see cref="WireMath.InterpolateAngle"/> and
/// <see cref="WireMath.InterpolateQuat3"/> over decoded values, against the committed <c>interpolation</c> vector the TypeScript and native SDKs replay.
/// </summary>
[TestFixture]
public class InterpolationGoldenTests
{
    private const string VectorName = "interpolation";

    private const string Description =
        "03 § 6's interpolation, pinned across the SDKs (#1398). `angle`: interpolateAngle(from, to, t, bits) over decoded angle values — in code space, "
        + "Δ = wrap_b(q1 − q0), q0 + t·Δ re-wrapped and decoded — must equal `value`. `quat3`: interpolateQuat3(from, to, t) over decoded rotations — the "
        + "shorter hemisphere (`to` negated when the dot product is negative), then a normalized lerp — must equal `value` (x, y, z, w). Every double is its "
        + "IEEE bits as 16 hex digits.";

    private static readonly double[] Fractions = [0, 0.25, 0.5, 0.75, 1, 1.5, -0.5];

    [Test]
    public void InterpolationMatchesTheCommittedVector()
    {
        var angles = new JsonArray();
        foreach (var bits in new[] { 8, 16, 24, 32 })
        {
            var h = (long)WireMath.Pow2(bits - 1);
            (long From, long To)[] pairs =
            [
                (0, 3),                     // a small step
                (5, 5),                     // no motion
                (h - 2, -h + 3),            // across +π, the short way round
                (-h + 1, h - 4),            // across −π
                (0, -h),                    // exactly half a turn: the wrap decides the way
                (h / 4, -(h / 4)),          // a quarter each side of zero
                (h / 3, (h / 3) + (h / 2)), // over half a turn the long way: taken the short way
            ];

            foreach (var (from, to) in pairs)
            {
                var a = WireMath.DecodeAngle((int)from, bits);
                var b = WireMath.DecodeAngle((int)to, bits);
                foreach (var t in Fractions)
                {
                    angles.Add(new JsonObject
                    {
                        ["bits"] = bits,
                        ["from"] = GoldenFiles.Bits(a),
                        ["to"] = GoldenFiles.Bits(b),
                        ["t"] = GoldenFiles.Bits(t),
                        ["value"] = GoldenFiles.Bits(WireMath.InterpolateAngle(a, b, t, bits)),
                    });
                }
            }
        }

        // Inputs no field decodes to, which an application can still pass: off-grid radians (each SDK rounds them through the same encoder), and NaN or
        // an infinity as an end or as the fraction. The vector pins what every SDK does with them, whatever that is.
        (double From, double To, double T)[] raw =
        [
            (1.0, 2.5, 0.25), (1.0, 2.5, 0.75), (-3.0, 3.0, 0.5),
            (double.NaN, 0.5, 0.5), (double.PositiveInfinity, 0.5, 0.5), (0.3, double.NegativeInfinity, 0.5),
            (0.1, 0.2, double.NaN), (0.1, 0.2, double.PositiveInfinity), (0.1, 0.2, double.NegativeInfinity),
        ];
        foreach (var bits in new[] { 8, 16, 32 })
        {
            foreach (var (from, to, t) in raw)
            {
                angles.Add(new JsonObject
                {
                    ["bits"] = bits,
                    ["from"] = GoldenFiles.Bits(from),
                    ["to"] = GoldenFiles.Bits(to),
                    ["t"] = GoldenFiles.Bits(t),
                    ["value"] = GoldenFiles.Bits(WireMath.InterpolateAngle(from, to, t, bits)),
                });
            }
        }

        var quats = new JsonArray();
        var s = Math.Sqrt(0.5);
        (double[] From, double[] To)[] rotations =
        [
            ([0, 0, 0, 1], [0, 0, s, s]),               // identity to 90° about z
            ([0, 0, s, s], [0, 0, -s, -s]),             // the same rotation, the other hemisphere: no motion
            ([0, 0, 0, 1], [0, 0, -0.2, -0.98]),        // a small turn written in the far hemisphere
            ([s, 0, 0, s], [0, s, 0, s]),               // x to y
            ([0.5, 0.5, 0.5, 0.5], [-0.5, 0.5, -0.5, 0.5]),
        ];

        Span<double> decodedFrom = stackalloc double[4];
        Span<double> decodedTo = stackalloc double[4];
        Span<double> value = stackalloc double[4];
        foreach (var (from, to) in rotations)
        {
            // Through the codec, so the inputs are values a client actually holds.
            WireMath.DecodeQuat3(WireMath.EncodeQuat3(from[0], from[1], from[2], from[3]), decodedFrom);
            WireMath.DecodeQuat3(WireMath.EncodeQuat3(to[0], to[1], to[2], to[3]), decodedTo);
            foreach (var t in Fractions)
            {
                WireMath.InterpolateQuat3(decodedFrom, decodedTo, t, value);
                quats.Add(new JsonObject
                {
                    ["from"] = GoldenFiles.Bits(decodedFrom),
                    ["to"] = GoldenFiles.Bits(decodedTo),
                    ["t"] = GoldenFiles.Bits(t),
                    ["value"] = GoldenFiles.Bits(value),
                });
            }
        }

        // Raw inputs, past the codec: the identity branch (a zero norm, a NaN), a dot product of exactly 0 and of −0, and quaternions that are not unit.
        (double[] From, double[] To)[] edges =
        [
            ([0, 0, 0, 0], [0, 0, 0, 1]),
            ([double.NaN, 0, 0, 1], [0, 0, 0, 1]),
            ([1, 0, 0, 0], [0, 1, 0, 0]),
            ([1, 0, 0, 0], [-0.0, 1, 0, 0]),
            ([0, 0, 0, 2], [0, 0, 2, 0]),
            ([0, 0, 0, 1], [0, 0, double.PositiveInfinity, 1]),
        ];
        foreach (var (from, to) in edges)
        {
            foreach (var t in new[] { 0d, 0.5, 1d })
            {
                WireMath.InterpolateQuat3(from, to, t, value);
                quats.Add(new JsonObject
                {
                    ["from"] = GoldenFiles.Bits(from),
                    ["to"] = GoldenFiles.Bits(to),
                    ["t"] = GoldenFiles.Bits(t),
                    ["value"] = GoldenFiles.Bits(value),
                });
            }
        }

        GoldenFiles.Assert(VectorName, [], new JsonObject { ["description"] = Description, ["angle"] = angles, ["quat3"] = quats });
    }

    /// <summary>The properties the vector's values rest on, asserted here so a regenerated vector cannot quietly encode a wrong rule.</summary>
    [Test]
    public void TheShortWayAndTheHemisphereAreTaken()
    {
        var h = WireMath.Pow2(7);
        var nearPlusPi = WireMath.DecodeAngle((int)h - 2, 8);
        var nearMinusPi = WireMath.DecodeAngle((int)-h + 3, 8);
        var mid = WireMath.InterpolateAngle(nearPlusPi, nearMinusPi, 0.5, 8);

        var value = new double[4];
        var s = Math.Sqrt(0.5);
        WireMath.InterpolateQuat3([0, 0, s, s], [0, 0, -s, -s], 0.5, value);

        Assert.Multiple(() =>
        {
            Assert.That(Math.Abs(mid), Is.GreaterThan(Math.PI * 0.95), "across ±π the short way passes through π, not through 0");
            Assert.That(WireMath.InterpolateAngle(nearPlusPi, nearMinusPi, 0, 8), Is.EqualTo(nearPlusPi));
            Assert.That(WireMath.InterpolateAngle(nearPlusPi, nearMinusPi, 1, 8), Is.EqualTo(nearMinusPi));
            Assert.That(value[2], Is.EqualTo(s).Within(1e-12), "a rotation and its negation are one rotation: no motion between them");
            Assert.That(value[3], Is.EqualTo(s).Within(1e-12));
        });
    }
}
