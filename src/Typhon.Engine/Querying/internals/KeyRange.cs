using System;
using System.Runtime.CompilerServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// The single definition of how an index scan range is computed, compared and intersected in the encoded-<c>long</c> domain that
/// <see cref="ExecutionPlan.PrimaryScanMin"/> / <see cref="ExecutionPlan.PrimaryScanMax"/> use.
/// </summary>
/// <remarks>
/// <para>
/// <b>The encoding.</b> A scan bound is a <c>long</c> whose meaning depends on the <see cref="KeyType"/>: signed integers widen, unsigned integers are the raw
/// value reinterpreted (so <c>ulong.MaxValue</c> is stored as <c>-1</c>), and <c>Float</c>/<c>Double</c> hold the raw IEEE-754 bit pattern. Every consumer
/// decodes with <see cref="BitConverter"/> — <c>EcsQuery.CollectClusterLocationsFromBTree</c>, <c>ArchetypeSortedStream.Create</c> — and the round trip is
/// exact, so the encoding itself is sound and is not what this type changes.
/// </para>
/// <para>
/// <b>What it changes is the arithmetic.</b> Two encoded bounds may NOT be compared with <c>&lt;</c> / <c>&gt;</c>. For <c>Float</c> the bit patterns of
/// negative values sort in reverse (<c>-2.0f</c> encodes to <c>-1073741824</c>, <c>-1.0f</c> to <c>-1082130432</c>, so the smaller float compares as the
/// larger long) and for <c>ULong</c> the top half of the range encodes negative. Intersecting two predicates on one field with signed comparison is exactly
/// how <c>Value &gt;= -20f &amp;&amp; Value &lt;= 20f</c> became <c>[float.MinValue, 20f]</c> — 71 rows where 41 are correct (#675).
/// </para>
/// <para>
/// <b>Approximations may only widen, never narrow.</b> Every path that consumes these bounds re-evaluates the full predicate set per emitted row
/// (<c>FieldEvaluator.Evaluate</c>), so a bound that admits too much costs time and a bound that admits too little is a wrong answer. That asymmetry decides
/// the two places this type approximates: an exclusive float endpoint stays inclusive, and an integer endpoint that would overflow saturates to the type's own
/// limit rather than wrapping past it.
/// </para>
/// <para>
/// <b>Why one type.</b> This logic existed twice — <c>PlanBuilder.TypeMinAsLong</c>/<c>ComputeBounds</c> and
/// <c>EcsQuery.GetTypeMinAsLong</c>/<c>IntersectEvaluatorBounds</c> — and the two copies had already drifted apart (only one handled <c>Bool</c>). While
/// stream selection was pinned off that was invisible, because only one copy ever ran. With Path A live both run, on the same query, and any disagreement
/// between them is a query that answers differently depending on which branch the planner took.
/// </para>
/// </remarks>
internal static class KeyRange
{
    /// <summary>Whether the key type's encoded bounds are ordered by plain signed <c>long</c> comparison.</summary>
    internal static bool IsIntegerKeyType(KeyType kt) =>
        kt is KeyType.Bool or KeyType.Byte or KeyType.SByte or KeyType.Short or KeyType.UShort or KeyType.Int or KeyType.UInt or KeyType.Long or KeyType.ULong;

    /// <summary>
    /// Whether a correct B+Tree range scan can be built over this key type.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Bool</c> and <c>String64</c> have no case in <c>EcsQuery.CollectClusterLocationsFromBTree</c> or <c>ArchetypeSortedStream.Create</c>, so proposing
    /// one as a scan stream would produce an EMPTY result rather than an error — the #663 failure shape. (Those switches now also throw on an unhandled type,
    /// so this predicate and they cannot drift apart silently: one of them fails loudly if they do.)
    /// </para>
    /// <para>
    /// <c>ULong</c> used to be excluded here for a different reason: its index was declared <c>L64BTree&lt;<b>long</b>&gt;</c>, so the keys were stored
    /// reinterpreted as SIGNED longs and the tree ordered them that way — anything at or above 2^63 sorted before zero, and the full range read
    /// <c>[0, -1]</c>, i.e. empty. #676 gave those trees a genuine <c>ulong</c> key, which is the level the defect actually lived at; the bound arithmetic
    /// here (<see cref="Compare"/> already compares <c>ULong</c> unsigned) and <c>OrderedKeyEncoding</c> were correct all along.
    /// </para>
    /// <para>
    /// Every excluded type falls to the SoA scan, which evaluates predicates against component data and is correct for any key type — slower, never wrong.
    /// </para>
    /// </remarks>
    internal static bool IsStreamable(KeyType kt) => kt is not (KeyType.Bool or KeyType.String64);

    /// <summary>The encoded lower bound of the key type's full range.</summary>
    /// <remarks>
    /// Not <see cref="long.MinValue"/>: the typed scan narrows the bound to the tree's key type, and <c>(int)long.MinValue == 0</c> would silently invert the
    /// range rather than widen it.
    /// </remarks>
    internal static long TypeMin(KeyType keyType) =>
        keyType switch
        {
            KeyType.Bool => 0L,
            KeyType.Byte => 0L,
            KeyType.SByte => sbyte.MinValue,
            KeyType.Short => short.MinValue,
            KeyType.UShort => 0L,
            KeyType.Int => int.MinValue,
            KeyType.UInt => 0L,
            KeyType.Long => long.MinValue,
            KeyType.ULong => 0L,
            KeyType.Float => BitConverter.SingleToInt32Bits(float.NegativeInfinity),
            KeyType.Double => BitConverter.DoubleToInt64Bits(double.NegativeInfinity),
            _ => long.MinValue
        };

    /// <summary>The encoded upper bound of the key type's full range.</summary>
    internal static long TypeMax(KeyType keyType) =>
        keyType switch
        {
            KeyType.Bool => 1L,
            KeyType.Byte => byte.MaxValue,
            KeyType.SByte => sbyte.MaxValue,
            KeyType.Short => short.MaxValue,
            KeyType.UShort => ushort.MaxValue,
            KeyType.Int => int.MaxValue,
            KeyType.UInt => uint.MaxValue,
            KeyType.Long => long.MaxValue,
            KeyType.ULong => unchecked((long)ulong.MaxValue),
            KeyType.Float => BitConverter.SingleToInt32Bits(float.PositiveInfinity),
            KeyType.Double => BitConverter.DoubleToInt64Bits(double.PositiveInfinity),
            _ => long.MaxValue
        };

    /// <summary>
    /// Order-preserving comparison of two encoded bounds. Negative / zero / positive as <paramref name="a"/> orders before / with / after <paramref name="b"/>.
    /// </summary>
    /// <remarks>
    /// <c>CompareTo</c> rather than <c>&lt;</c> for the floating types: it is a total order, so a NaN threshold produces a deterministic (if useless) range
    /// instead of a range whose endpoints are mutually incomparable and whose intersection depends on evaluation order. NaN predicates are rejected by neither
    /// this type nor the planner — they simply select nothing once the evaluators run, which is the correct answer for every comparison against NaN.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Compare(long a, long b, KeyType keyType)
    {
        switch (keyType)
        {
            case KeyType.Float:
                return BitConverter.Int32BitsToSingle((int)a).CompareTo(BitConverter.Int32BitsToSingle((int)b));
            case KeyType.Double:
                return BitConverter.Int64BitsToDouble(a).CompareTo(BitConverter.Int64BitsToDouble(b));
            case KeyType.Byte:
            case KeyType.UShort:
            case KeyType.UInt:
            case KeyType.ULong:
                return ((ulong)a).CompareTo((ulong)b);
            default:
                return a.CompareTo(b);
        }
    }

    /// <summary>The greater of two encoded bounds under <paramref name="keyType"/>'s ordering.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long Max(long a, long b, KeyType keyType) => Compare(a, b, keyType) >= 0 ? a : b;

    /// <summary>The lesser of two encoded bounds under <paramref name="keyType"/>'s ordering.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long Min(long a, long b, KeyType keyType) => Compare(a, b, keyType) <= 0 ? a : b;

    /// <summary>The range one predicate admits, as encoded bounds, with the open side left at the key type's full extent.</summary>
    /// <remarks>
    /// <c>NotEqual</c> cannot narrow a contiguous range and yields the full extent; callers that care skip it before calling. Strict inequalities step by one
    /// on integer types and stay INCLUSIVE on the floating ones — the next representable float is not the threshold ± 1 in the encoded domain, and widening by
    /// one value the evaluators then reject is cheaper than getting the step wrong.
    /// </remarks>
    internal static (long Min, long Max) ComputeBounds(ref FieldEvaluator eval, KeyType keyType)
    {
        var typeMin = TypeMin(keyType);
        var typeMax = TypeMax(keyType);
        var isInteger = IsIntegerKeyType(keyType);

        switch (eval.CompareOp)
        {
            case CompareOp.Equal:
                return (eval.Threshold, eval.Threshold);
            case CompareOp.GreaterThan:
                return (isInteger ? StepUp(eval.Threshold, typeMax, keyType) : eval.Threshold, typeMax);
            case CompareOp.GreaterThanOrEqual:
                return (eval.Threshold, typeMax);
            case CompareOp.LessThan:
                return (typeMin, isInteger ? StepDown(eval.Threshold, typeMin, keyType) : eval.Threshold);
            case CompareOp.LessThanOrEqual:
                return (typeMin, eval.Threshold);
            default:
                return (typeMin, typeMax);
        }
    }

    /// <summary>
    /// Narrow <paramref name="scanMin"/>/<paramref name="scanMax"/> to the intersection of every range-narrowing predicate on <paramref name="fieldIndex"/>.
    /// </summary>
    /// <remarks>
    /// The bounds must already hold a valid range for <paramref name="keyType"/> — normally its full extent. An empty intersection (min ordering after max) is
    /// left as-is rather than normalised: every scan built from it enumerates nothing, which is the correct answer for a contradictory predicate set.
    /// </remarks>
    internal static void Intersect(FieldEvaluator[] evaluators, int fieldIndex, KeyType keyType, ref long scanMin, ref long scanMax)
    {
        for (var e = 0; e < evaluators.Length; e++)
        {
            ref var eval = ref evaluators[e];
            if (eval.FieldIndex != fieldIndex || eval.CompareOp == CompareOp.NotEqual)
            {
                continue;
            }

            var (evalMin, evalMax) = ComputeBounds(ref eval, keyType);
            scanMin = Max(scanMin, evalMin, keyType);
            scanMax = Min(scanMax, evalMax, keyType);
        }
    }

    /// <summary>The next value above <paramref name="value"/>, saturating at <paramref name="typeMax"/> instead of wrapping.</summary>
    /// <remarks>
    /// Wrapping is the dangerous direction: <c>long.MaxValue + 1</c> is <c>long.MinValue</c>, which turns "greater than the largest value" — an empty range —
    /// into the whole range read backwards. Saturating admits one value too many, which the per-row evaluator then rejects.
    /// </remarks>
    private static long StepUp(long value, long typeMax, KeyType keyType) => Compare(value, typeMax, keyType) >= 0 ? typeMax : value + 1;

    /// <summary>The next value below <paramref name="value"/>, saturating at <paramref name="typeMin"/> instead of wrapping.</summary>
    private static long StepDown(long value, long typeMin, KeyType keyType) => Compare(value, typeMin, keyType) <= 0 ? typeMin : value - 1;
}
