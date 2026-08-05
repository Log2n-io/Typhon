using System;
using System.Collections.Generic;
using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// Unit-level coverage of <see cref="KeyRange"/> and of the ordered-long encodings the zone maps and the K-way merge sort by.
/// </summary>
/// <remarks>
/// <para>
/// These are the two places where a value's TYPE and its ENCODING can disagree, and both defects #675 fixed were of that shape: a bound compared with the
/// wrong operator for its encoding, and an encoding that is monotone under unsigned comparison used by code that compares signed. Neither is visible in a
/// query result until the data straddles zero or reaches a type's extreme — which is why they survived ~4 000 tests and are checked directly here rather than
/// only through queries.
/// </para>
/// <para>
/// Every ordering assertion is made against <see cref="IComparable.CompareTo"/> on the value's OWN CLR type. That is an independent oracle: it does not share
/// a line of code with the encoding under test, so an encoding cannot agree with it by being wrong in the same way.
/// </para>
/// </remarks>
[TestFixture]
class KeyRangeTests
{
    /// <summary>One key type, its representative values, and how a scan bound encodes them.</summary>
    internal sealed record TypeAxis(KeyType KeyType, IComparable[] Samples, Func<IComparable, long> Encode)
    {
        public override string ToString() => KeyType.ToString();
    }

    private static readonly TypeAxis[] Axes =
    [
        new(KeyType.Bool, [false, true], v => (bool)v ? 1L : 0L),
        new(KeyType.SByte, [sbyte.MinValue, (sbyte)-1, (sbyte)0, (sbyte)1, sbyte.MaxValue], v => (sbyte)v),
        new(KeyType.Byte, [byte.MinValue, (byte)1, (byte)127, (byte)128, byte.MaxValue], v => (byte)v),
        new(KeyType.Short, [short.MinValue, (short)-1, (short)0, (short)1, short.MaxValue], v => (short)v),
        new(KeyType.UShort, [ushort.MinValue, (ushort)1, (ushort)32767, (ushort)32768, ushort.MaxValue], v => (ushort)v),
        new(KeyType.Int, [int.MinValue, -1, 0, 1, int.MaxValue], v => (int)v),
        new(KeyType.UInt, [uint.MinValue, 1u, (uint)int.MaxValue, 2147483648u, uint.MaxValue], v => (uint)v),
        new(KeyType.Long, [long.MinValue, -1L, 0L, 1L, long.MaxValue], v => (long)v),
        new(KeyType.ULong, [ulong.MinValue, 1UL, (ulong)long.MaxValue, 9223372036854775808UL, ulong.MaxValue], v => unchecked((long)(ulong)v)),
        new(KeyType.Float,
            [float.NegativeInfinity, float.MinValue, -1000.5f, -1f, 0f, 1f, 1000.5f, float.MaxValue, float.PositiveInfinity],
            v => BitConverter.SingleToInt32Bits((float)v)),
        new(KeyType.Double,
            [double.NegativeInfinity, double.MinValue, -1000.5, -1d, 0d, 1d, 1000.5, double.MaxValue, double.PositiveInfinity],
            v => BitConverter.DoubleToInt64Bits((double)v))
    ];

    private static readonly CompareOp[] RangeOps =
    [
        CompareOp.Equal, CompareOp.GreaterThan, CompareOp.GreaterThanOrEqual, CompareOp.LessThan, CompareOp.LessThanOrEqual
    ];

    public static IEnumerable<TestCaseData> AxisCases()
    {
        foreach (var axis in Axes)
        {
            yield return new TestCaseData(axis).SetName($"{{m}}_{axis}");
        }
    }

    // ── Ordering ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Encoded bounds must order exactly as the values they encode do.</summary>
    [TestCaseSource(nameof(AxisCases))]
    public void Compare_OrdersEncodedBoundsLikeTheirValues(TypeAxis axis)
    {
        Assert.Multiple(() =>
        {
            foreach (var a in axis.Samples)
            {
                foreach (var b in axis.Samples)
                {
                    var expected = Math.Sign(a.CompareTo(b));
                    var actual = Math.Sign(KeyRange.Compare(axis.Encode(a), axis.Encode(b), axis.KeyType));
                    Assert.That(actual, Is.EqualTo(expected), $"{axis}: Compare({a}, {b}) — the encoded order must match the value order");
                }
            }
        });
    }

    /// <summary>The full type range must bracket every value of that type, including the extremes it is built from.</summary>
    /// <remarks>
    /// A range that fails to bracket its own type's values silently drops rows: that is precisely what a raw-IEEE
    /// <c>[float.MinValue, float.MaxValue]</c> did to infinities, and what <c>[0, (long)ulong.MaxValue]</c> does to every ulong under signed comparison.
    /// </remarks>
    [TestCaseSource(nameof(AxisCases))]
    public void FullRange_BracketsEveryValueOfTheType(TypeAxis axis)
    {
        var min = KeyRange.TypeMin(axis.KeyType);
        var max = KeyRange.TypeMax(axis.KeyType);

        Assert.Multiple(() =>
        {
            Assert.That(KeyRange.Compare(min, max, axis.KeyType), Is.LessThanOrEqualTo(0), $"{axis}: the full range must not be inverted");
            foreach (var v in axis.Samples)
            {
                var e = axis.Encode(v);
                Assert.That(KeyRange.Compare(min, e, axis.KeyType), Is.LessThanOrEqualTo(0), $"{axis}: {v} falls below the full range's lower bound");
                Assert.That(KeyRange.Compare(e, max, axis.KeyType), Is.LessThanOrEqualTo(0), $"{axis}: {v} falls above the full range's upper bound");
            }
        });
    }

    // ── Bounds ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A single predicate's bounds must admit every value that satisfies it.
    /// </summary>
    /// <remarks>
    /// Only one direction is asserted, and deliberately: the scan re-evaluates the predicate on every row it emits, so a bound that admits too MUCH costs time
    /// while a bound that admits too LITTLE loses rows. Asserting exactness instead would forbid the two approximations the design makes on purpose —
    /// inclusive float endpoints and saturating integer steps.
    /// </remarks>
    [TestCaseSource(nameof(AxisCases))]
    public void SinglePredicateBounds_NeverExcludeAMatchingValue(TypeAxis axis)
    {
        Assert.Multiple(() =>
        {
            foreach (var op in RangeOps)
            {
                foreach (var threshold in axis.Samples)
                {
                    var eval = Evaluator(axis, op, threshold);
                    var (min, max) = KeyRange.ComputeBounds(ref eval, axis.KeyType);

                    foreach (var v in axis.Samples)
                    {
                        if (!Holds(v, op, threshold))
                        {
                            continue;
                        }

                        Assert.That(KeyRange.Compare(min, axis.Encode(v), axis.KeyType), Is.LessThanOrEqualTo(0),
                            $"{axis}: {v} satisfies {op} {threshold} but falls below the computed lower bound");
                        Assert.That(KeyRange.Compare(axis.Encode(v), max, axis.KeyType), Is.LessThanOrEqualTo(0),
                            $"{axis}: {v} satisfies {op} {threshold} but falls above the computed upper bound");
                    }
                }
            }
        });
    }

    /// <summary>
    /// Intersecting two predicates on one field must not widen the range — the #675 defect, expressed at the level it happened.
    /// </summary>
    /// <remarks>
    /// The engine-level version of this is <c>QueryPathEquivalenceTests.TwoSidedWindow_IsNotWidenedByBoundIntersection</c>. This one fails faster and names
    /// the arithmetic directly: the old code intersected with signed <c>long</c> <c>&gt;</c> / <c>&lt;</c>, so for a negative float threshold
    /// <c>max(bits(-20f), bits(float.MinValue))</c> chose the type minimum and the lower bound never tightened at all.
    /// </remarks>
    [TestCaseSource(nameof(AxisCases))]
    public void Intersect_TightensBothEndsAndKeepsEveryMatch(TypeAxis axis)
    {
        Assert.Multiple(() =>
        {
            foreach (var low in axis.Samples)
            {
                foreach (var high in axis.Samples)
                {
                    if (low.CompareTo(high) > 0)
                    {
                        continue;
                    }

                    FieldEvaluator[] evals =
                    [
                        Evaluator(axis, CompareOp.GreaterThanOrEqual, low),
                        Evaluator(axis, CompareOp.LessThanOrEqual, high)
                    ];

                    var min = KeyRange.TypeMin(axis.KeyType);
                    var max = KeyRange.TypeMax(axis.KeyType);
                    KeyRange.Intersect(evals, 0, axis.KeyType, ref min, ref max);

                    Assert.That(KeyRange.Compare(min, axis.Encode(low), axis.KeyType), Is.EqualTo(0),
                        $"{axis}: [{low}, {high}] — the lower bound must tighten to {low}, not stay at the type minimum");
                    Assert.That(KeyRange.Compare(max, axis.Encode(high), axis.KeyType), Is.EqualTo(0),
                        $"{axis}: [{low}, {high}] — the upper bound must tighten to {high}");

                    foreach (var v in axis.Samples)
                    {
                        if (v.CompareTo(low) < 0 || v.CompareTo(high) > 0)
                        {
                            continue;
                        }

                        var e = axis.Encode(v);
                        Assert.That(KeyRange.Compare(min, e, axis.KeyType), Is.LessThanOrEqualTo(0), $"{axis}: {v} is inside [{low}, {high}] but below min");
                        Assert.That(KeyRange.Compare(e, max, axis.KeyType), Is.LessThanOrEqualTo(0), $"{axis}: {v} is inside [{low}, {high}] but above max");
                    }
                }
            }
        });
    }

    /// <summary>A strict inequality at a type's own extreme must saturate, never wrap past it.</summary>
    /// <remarks>
    /// <c>long.MaxValue + 1</c> is <c>long.MinValue</c>: without saturation, "greater than the largest value" — an empty range — becomes the entire range read
    /// backwards, and the same at the bottom end. Saturating admits at most one extra value, which the per-row evaluator rejects.
    /// </remarks>
    [TestCaseSource(nameof(AxisCases))]
    public void StrictInequalityAtTheExtremes_SaturatesInsteadOfWrapping(TypeAxis axis)
    {
        var typeMin = KeyRange.TypeMin(axis.KeyType);
        var typeMax = KeyRange.TypeMax(axis.KeyType);
        var highest = axis.Samples[^1];
        var lowest = axis.Samples[0];

        var gt = Evaluator(axis, CompareOp.GreaterThan, highest);
        var (gtMin, gtMax) = KeyRange.ComputeBounds(ref gt, axis.KeyType);

        var lt = Evaluator(axis, CompareOp.LessThan, lowest);
        var (ltMin, ltMax) = KeyRange.ComputeBounds(ref lt, axis.KeyType);

        Assert.Multiple(() =>
        {
            Assert.That(KeyRange.Compare(gtMin, gtMax, axis.KeyType), Is.LessThanOrEqualTo(0),
                $"{axis}: `> {highest}` produced an inverted range — the endpoint wrapped instead of saturating");
            Assert.That(KeyRange.Compare(gtMin, typeMin, axis.KeyType), Is.GreaterThan(0),
                $"{axis}: `> {highest}` must not reopen the range down to the type minimum");

            Assert.That(KeyRange.Compare(ltMin, ltMax, axis.KeyType), Is.LessThanOrEqualTo(0),
                $"{axis}: `< {lowest}` produced an inverted range — the endpoint wrapped instead of saturating");
            Assert.That(KeyRange.Compare(ltMax, typeMax, axis.KeyType), Is.LessThan(0),
                $"{axis}: `< {lowest}` must not reopen the range up to the type maximum");
        });
    }

    // ── Streamability ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The key types a B+Tree range scan may be built over — the guard that keeps a silently-empty result impossible.</summary>
    [Test]
    public void IsStreamable_ExcludesExactlyTheTypesWithNoCorrectRangeScan()
    {
        Assert.Multiple(() =>
        {
            Assert.That(KeyRange.IsStreamable(KeyType.Bool), Is.False, "no typed B+Tree scan case exists for Bool");
            Assert.That(KeyRange.IsStreamable(KeyType.String64), Is.False, "no typed B+Tree scan case exists for String64");
            Assert.That(KeyRange.IsStreamable(KeyType.ULong), Is.False,
                "a ULong index is an L64BTree<long>, so its full range [0, ulong.MaxValue] encodes to the signed range [0, -1] — empty");

            foreach (var kt in new[] { KeyType.SByte, KeyType.Byte, KeyType.Short, KeyType.UShort, KeyType.Int, KeyType.UInt, KeyType.Long, KeyType.Float,
                         KeyType.Double })
            {
                Assert.That(KeyRange.IsStreamable(kt), Is.True, $"{kt} has a typed B+Tree scan and must remain streamable");
            }
        });
    }

    // ── Ordered-long encodings (zone-map pruning and K-way merge ordering) ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>FloatToOrderedLong</c> / <c>DoubleToOrderedLong</c> must be monotone under SIGNED long comparison, because that is how both consumers compare them.
    /// </summary>
    /// <remarks>
    /// The double encoding was <c>bits &lt; 0 ? ~bits : bits ^ long.MinValue</c> — monotone under UNSIGNED comparison only. The float twin escapes that because
    /// its 32-bit result widens into the positive half of a long, where the two orders coincide; the 64-bit one has nowhere to widen into, so every positive
    /// double came out negative and sorted below every negative one. Zone-map pruning then skipped whole clusters of positive doubles, and the K-way merge
    /// mis-sorted ordered queries on a double column. This is the assertion that fails if either encoding is "simplified" back.
    /// </remarks>
    [Test]
    public void OrderedLongEncodings_AreMonotoneUnderSignedComparison()
    {
        float[] floats = [float.NegativeInfinity, float.MinValue, -1e30f, -1000.5f, -1f, -0f, 0f, 1f, 1000.5f, 1e30f, float.MaxValue, float.PositiveInfinity];
        double[] doubles =
            [double.NegativeInfinity, double.MinValue, -1e300, -1000.5, -1d, -0d, 0d, 1d, 1000.5, 1e300, double.MaxValue, double.PositiveInfinity];

        Assert.Multiple(() =>
        {
            for (var i = 1; i < floats.Length; i++)
            {
                var lo = ZoneMapArray.FloatToOrderedLong(floats[i - 1]);
                var hi = ZoneMapArray.FloatToOrderedLong(floats[i]);
                Assert.That(lo, Is.LessThanOrEqualTo(hi), $"float ordering broken between {floats[i - 1]} and {floats[i]}");
            }

            for (var i = 1; i < doubles.Length; i++)
            {
                var lo = ZoneMapArray.DoubleToOrderedLong(doubles[i - 1]);
                var hi = ZoneMapArray.DoubleToOrderedLong(doubles[i]);
                Assert.That(lo, Is.LessThanOrEqualTo(hi), $"double ordering broken between {doubles[i - 1]} and {doubles[i]}");
            }

            Assert.That(ZoneMapArray.DoubleToOrderedLong(-0d), Is.EqualTo(ZoneMapArray.DoubleToOrderedLong(0d)),
                "-0.0 and +0.0 compare equal, so must encode equal");
            Assert.That(ZoneMapArray.FloatToOrderedLong(-0f), Is.EqualTo(ZoneMapArray.FloatToOrderedLong(0f)),
                "-0.0f and +0.0f compare equal, so must encode equal");
        });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static FieldEvaluator Evaluator(TypeAxis axis, CompareOp op, IComparable threshold) =>
        new() { FieldIndex = 0, KeyType = axis.KeyType, CompareOp = op, Threshold = axis.Encode(threshold) };

    private static bool Holds(IComparable value, CompareOp op, IComparable threshold)
    {
        var c = value.CompareTo(threshold);
        return op switch
        {
            CompareOp.Equal => c == 0,
            CompareOp.NotEqual => c != 0,
            CompareOp.GreaterThan => c > 0,
            CompareOp.GreaterThanOrEqual => c >= 0,
            CompareOp.LessThan => c < 0,
            CompareOp.LessThanOrEqual => c <= 0,
            _ => throw new ArgumentOutOfRangeException(nameof(op), op, "unknown operator")
        };
    }
}
