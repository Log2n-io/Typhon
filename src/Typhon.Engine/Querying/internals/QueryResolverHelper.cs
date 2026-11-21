using System;
using System.Runtime.CompilerServices;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Internals;

internal static class QueryResolverHelper
{
    public static FieldEvaluator[] ResolveEvaluators(FieldPredicate[] predicates, ComponentTable ct, byte componentTag, byte branchIndex = 0)
    {
        var definition = ct.Definition;
        var evaluators = new FieldEvaluator[predicates.Length];

        for (var i = 0; i < predicates.Length; i++)
        {
            var pred = predicates[i];

            if (!definition.FieldsByName.TryGetValue(pred.FieldName, out var field))
            {
                throw new InvalidOperationException($"Field '{pred.FieldName}' not found on component '{definition.Name}'.");
            }

            if (!field.HasIndex)
            {
                throw new InvalidOperationException(
                    $"Field '{definition.Name}.{pred.FieldName}' is not indexed — WhereField seeks through an index. Add [Index] to the field, or use "
                    + "Where<T>(Func<T, bool>), which evaluates per entity without one.");
            }

            var fieldIndex = FindFieldIndex(definition, field);
            var keyType = MapFieldTypeToKeyType(field.Type);
            var op = pred.Operator;
            var threshold = NormalizeThreshold(pred.Value, keyType, ref op);

            if (fieldIndex >= 64)
            {
                throw new InvalidOperationException(
                    $"Field '{pred.FieldName}' has index {fieldIndex} which exceeds the maximum of 63. Ring buffer delta encoding uses 6 bits for field index.");
            }

            evaluators[i] = new FieldEvaluator
            {
                FieldIndex = (byte)fieldIndex,
                FieldOffset = (ushort)field.OffsetInComponentStorage,
                FieldSize = (byte)field.FieldSize,
                KeyType = keyType,
                CompareOp = op,
                Threshold = threshold,
                ComponentTag = componentTag,
                BranchIndex = branchIndex
            };
        }

        return evaluators;
    }

    /// <summary>
    /// Finds the index into IndexedFieldInfos[] by replicating the iteration order from BuildIndexedFieldInfo.
    /// </summary>
    public static int FindFieldIndex(DBComponentDefinition definition, DBComponentDefinition.Field targetField)
    {
        var index = 0;
        for (var i = 0; i < definition.MaxFieldId; i++)
        {
            var f = definition[i];
            if (f == null || !f.HasIndex)
            {
                continue;
            }

            if (f == targetField)
            {
                return index;
            }

            index++;
        }

        throw new InvalidOperationException($"Field '{targetField.Name}' not found in indexed fields.");
    }

    /// <summary>The tree key type of <paramref name="fieldType"/>; false for a type no key encodes (a string).</summary>
    internal static bool TryMapFieldTypeToKeyType(FieldType fieldType, out KeyType keyType)
    {
        (keyType, var known) = fieldType switch
        {
            FieldType.Boolean => (KeyType.Bool, true),
            FieldType.Byte => (KeyType.SByte, true),
            FieldType.UByte => (KeyType.Byte, true),
            FieldType.Short => (KeyType.Short, true),
            FieldType.UShort => (KeyType.UShort, true),
            FieldType.Int => (KeyType.Int, true),
            FieldType.UInt => (KeyType.UInt, true),
            FieldType.Long => (KeyType.Long, true),
            FieldType.ULong => (KeyType.ULong, true),
            FieldType.Float => (KeyType.Float, true),
            FieldType.Double => (KeyType.Double, true),
            _ => (default(KeyType), false)
        };
        return known;
    }

    private static KeyType MapFieldTypeToKeyType(FieldType fieldType) =>
        TryMapFieldTypeToKeyType(fieldType, out var keyType)
            ? keyType
            : throw new NotSupportedException($"Field type {fieldType} is not supported for view predicates.");

    /// <summary>
    /// Encodes <paramref name="value"/> into <paramref name="keyType"/>, rewriting <paramref name="op"/> when the constant has no exact image in the key
    /// type, so the evaluator selects exactly the rows the C# predicate selects (#1654).
    /// </summary>
    /// <remarks>
    /// <para>
    /// C# compares an integral field against a non-integral or wider constant after widening the FIELD (<c>p =&gt; p.Level &gt; 3.7</c> is
    /// <c>(double)p.Level &gt; 3.7</c>), and a <c>float</c> field against a <c>double</c> constant in double precision. Converting the constant to the key
    /// type instead rounded it: <c>&gt; 3.7</c> ran as <c>&gt; 4</c> and dropped <c>Level == 4</c>, <c>== 3.5</c> matched 4, and an out-of-range constant
    /// threw an <see cref="OverflowException"/> from <c>Convert</c>. The transaction's own pending spawns are filtered by the compiled lambda, so committed
    /// and pending rows disagreed.
    /// </para>
    /// <para>
    /// An inexact constant keeps the operator's meaning: <c>&gt; 3.7</c> becomes <c>&gt;= 4</c>, <c>&lt;= 3.7</c> becomes <c>&lt;= 3</c>. A comparison no row
    /// can satisfy (<c>== 3.5</c>, <c>&gt; 300</c> on a byte) becomes <c>&gt; MaxValue</c>, and one every row satisfies (<c>!= 3.5</c>, <c>&lt; 300</c> on a
    /// byte) becomes <c>&gt;= MinValue</c>: both are ordinary ranges, so the index and the per-entity evaluator need nothing new. A threshold held in a
    /// variable can legitimately land out of range at run time, which is why these are answered rather than refused.
    /// </para>
    /// </remarks>
    internal static long NormalizeThreshold(object value, KeyType keyType, ref CompareOp op)
    {
        if (value == null)
        {
            // A null constant (an empty nullable): C#'s lifted comparison is false for every operator but !=, which is true. Encoding it as 0 ran
            // `p.I > null` as `p.I > 0`.
            return NormalizeAlwaysOrNever(op == CompareOp.NotEqual, keyType, ref op);
        }

        if (keyType == KeyType.Float)
        {
            return NormalizeFloat(value, ref op);
        }

        if (keyType == KeyType.Double && value is double or float && double.IsNaN(Convert.ToDouble(value)))
        {
            // As for a float field: a NaN threshold is stated as the range it selects. Left as a NaN threshold, the vectorised evaluator's ordered
            // not-equal answered false where C#'s != answers true.
            return NormalizeAlwaysOrNever(op == CompareOp.NotEqual, keyType, ref op);
        }

        if (!TryIntegralRange(keyType, out var min, out var max))
        {
            return EncodeThreshold(value, keyType);
        }

        switch (value)
        {
            case double d:
                return NormalizeAgainstReal(d, false, min, max, keyType, ref op);
            case float f:
                return NormalizeAgainstReal(f, true, min, max, keyType, ref op);
            case decimal m:
                // The field widens to decimal exactly, so floor/ceiling in decimal are exact too.
                return NormalizeAgainstBounds((Int128)decimal.Floor(m), (Int128)decimal.Ceiling(m), min, max, keyType, ref op);
            default:
                return TryGetInteger(value, out var v) ? NormalizeAgainstBounds(v, v, min, max, keyType, ref op) : EncodeThreshold(value, keyType);
        }
    }

    /// <summary>
    /// <c>(W)field op c</c> for an integral field, where <c>W</c> is <c>double</c> (or <c>float</c> when <paramref name="asFloat"/>), as C# evaluates it.
    /// </summary>
    /// <remarks>
    /// Below 2^53 (2^24 for float) every integer near <paramref name="c"/> widens exactly, so <c>floor</c>/<c>ceiling</c> of <paramref name="c"/> decide the
    /// comparison; computed in double arithmetic, which is exact there (decimal is not: it rounds a double to 15 significant digits). At or beyond that
    /// magnitude the widening itself rounds — <c>(double)long.MaxValue &lt; 9.2233720368547758E18</c> is false — so the bounds are found by a binary search
    /// over the field's range with the widening C# applies, which is monotone. An equality there can hold for several integers at once; a single
    /// evaluator cannot express that set, and the clause raises rather than answer one of them.
    /// </remarks>
    private static long NormalizeAgainstReal(double c, bool asFloat, Int128 min, Int128 max, KeyType keyType, ref CompareOp op)
    {
        if (double.IsNaN(c))
        {
            // Every ordered comparison and == against NaN is false; != is true.
            return op == CompareOp.NotEqual ? Always(min, keyType, ref op) : Never(max, keyType, ref op);
        }

        if (Math.Abs(c) < (asFloat ? 16777216.0 : 9007199254740992.0))
        {
            return NormalizeAgainstBounds((long)Math.Floor(c), (long)Math.Ceiling(c), min, max, keyType, ref op);
        }

        // [firstAtLeast, firstAbove - 1] is the set of field values whose widened image equals c.
        var firstAtLeast = FirstWidenedAtLeast(c, asFloat, false, min, max, keyType);
        var firstAbove = FirstWidenedAtLeast(c, asFloat, true, min, max, keyType);
        switch (op)
        {
            case CompareOp.GreaterThan:
                return AtLeast(firstAbove, min, max, keyType, ref op);
            case CompareOp.GreaterThanOrEqual:
                return AtLeast(firstAtLeast, min, max, keyType, ref op);
            case CompareOp.LessThan:
                return AtMost(firstAtLeast - 1, min, max, keyType, ref op);
            case CompareOp.LessThanOrEqual:
                return AtMost(firstAbove - 1, min, max, keyType, ref op);
            default:
                if (firstAtLeast >= firstAbove)
                {
                    return op == CompareOp.NotEqual ? Always(min, keyType, ref op) : Never(max, keyType, ref op);
                }
                if (firstAbove - firstAtLeast == 1)
                {
                    return EncodeIntegral(firstAtLeast, keyType);
                }
                throw new NotSupportedException(
                    $"An {op} against {c:R} on an integral field holds for {firstAbove - firstAtLeast} distinct values: the field is widened to "
                    + $"{(asFloat ? "float" : "double")}, which cannot tell them apart at this magnitude. Compare against an integral value instead.");
        }
    }

    /// <summary>The smallest field value whose widened image is &gt;= <paramref name="c"/> (&gt; when <paramref name="strict"/>), or max + 1.</summary>
    private static Int128 FirstWidenedAtLeast(double c, bool asFloat, bool strict, Int128 min, Int128 max, KeyType keyType)
    {
        Int128 lo = min, hi = max + 1;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            var w = Widen(mid, asFloat, keyType);
            if (strict ? w > c : w >= c)
            {
                hi = mid;
            }
            else
            {
                lo = mid + 1;
            }
        }
        return lo;
    }

    /// <summary>The value C# compares when it widens a field of <paramref name="keyType"/> holding <paramref name="x"/> to double, or float.</summary>
    private static double Widen(Int128 x, bool asFloat, KeyType keyType) => keyType switch
    {
        KeyType.ULong => asFloat ? (float)(ulong)x : (double)(ulong)x,
        KeyType.Long => asFloat ? (float)(long)x : (double)(long)x,
        KeyType.UInt => asFloat ? (float)(uint)x : (double)(uint)x,
        _ => asFloat ? (float)(int)x : (double)(int)x
    };

    /// <summary>
    /// The comparison against a constant lying in [<paramref name="floor"/>, <paramref name="ceiling"/>], both integers: equal when the constant is
    /// integral, one apart when it is not. Exact for any field value, the field being integral.
    /// </summary>
    private static long NormalizeAgainstBounds(Int128 floor, Int128 ceiling, Int128 min, Int128 max, KeyType keyType, ref CompareOp op)
    {
        var integral = floor == ceiling;
        switch (op)
        {
            case CompareOp.Equal:
                return integral && floor >= min && floor <= max ? EncodeIntegral(floor, keyType) : Never(max, keyType, ref op);
            case CompareOp.NotEqual:
                return integral && floor >= min && floor <= max ? EncodeIntegral(floor, keyType) : Always(min, keyType, ref op);
            case CompareOp.GreaterThan:
                // x > v  <=>  x >= floor(v) + 1, for integral x.
                return AtLeast(floor + 1, min, max, keyType, ref op);
            case CompareOp.GreaterThanOrEqual:
                return AtLeast(ceiling, min, max, keyType, ref op);
            case CompareOp.LessThan:
                // x < v  <=>  x <= ceiling(v) - 1.
                return AtMost(ceiling - 1, min, max, keyType, ref op);
            default:
                return AtMost(floor, min, max, keyType, ref op);
        }
    }

    private static long AtLeast(Int128 t, Int128 min, Int128 max, KeyType keyType, ref CompareOp op)
    {
        if (t <= min)
        {
            return Always(min, keyType, ref op);
        }
        if (t > max)
        {
            return Never(max, keyType, ref op);
        }
        op = CompareOp.GreaterThanOrEqual;
        return EncodeIntegral(t, keyType);
    }

    private static long AtMost(Int128 t, Int128 min, Int128 max, KeyType keyType, ref CompareOp op)
    {
        if (t >= max)
        {
            return Always(min, keyType, ref op);
        }
        if (t < min)
        {
            return Never(max, keyType, ref op);
        }
        op = CompareOp.LessThanOrEqual;
        return EncodeIntegral(t, keyType);
    }

    /// <summary>A comparison every row satisfies (<paramref name="all"/>) or none does, as a range of <paramref name="keyType"/>.</summary>
    private static long NormalizeAlwaysOrNever(bool all, KeyType keyType, ref CompareOp op)
    {
        if (TryIntegralRange(keyType, out var min, out var max))
        {
            return all ? Always(min, keyType, ref op) : Never(max, keyType, ref op);
        }

        switch (keyType)
        {
            case KeyType.Float:
                op = all ? CompareOp.GreaterThanOrEqual : CompareOp.LessThan;
                return EncodeThreshold(float.NegativeInfinity, KeyType.Float);
            case KeyType.Double:
                op = all ? CompareOp.GreaterThanOrEqual : CompareOp.LessThan;
                return EncodeThreshold(double.NegativeInfinity, KeyType.Double);
            case KeyType.Bool:
                op = all ? CompareOp.GreaterThanOrEqual : CompareOp.GreaterThan;
                return all ? 0L : 1L;
            default:
                throw new NotSupportedException($"A null constant cannot be compared with a {keyType} field.");
        }
    }

    /// <summary><c>&gt;= MinValue</c>: every row matches, and the scan range is the key type's full extent.</summary>
    private static long Always(Int128 min, KeyType keyType, ref CompareOp op)
    {
        op = CompareOp.GreaterThanOrEqual;
        return EncodeIntegral(min, keyType);
    }

    /// <summary>
    /// <c>&gt; MaxValue</c>: no row matches. Encoded at the top of the range, whose scan saturates to the single key MaxValue — not at the bottom, where an
    /// unsigned type's range would be [0, 0] and a sparse index (which never holds 0, IX-09) would send the query to a full cluster scan to find nothing.
    /// </summary>
    private static long Never(Int128 max, KeyType keyType, ref CompareOp op)
    {
        op = CompareOp.GreaterThan;
        return EncodeIntegral(max, keyType);
    }

    private static long EncodeIntegral(Int128 v, KeyType keyType) => keyType == KeyType.ULong ? unchecked((long)(ulong)v) : (long)v;

    private static bool TryIntegralRange(KeyType keyType, out Int128 min, out Int128 max)
    {
        (min, max) = keyType switch
        {
            KeyType.SByte => ((Int128)sbyte.MinValue, (Int128)sbyte.MaxValue),
            KeyType.Byte => (byte.MinValue, byte.MaxValue),
            KeyType.Short => (short.MinValue, short.MaxValue),
            KeyType.UShort => (ushort.MinValue, ushort.MaxValue),
            KeyType.Int => (int.MinValue, int.MaxValue),
            KeyType.UInt => (uint.MinValue, uint.MaxValue),
            KeyType.Long => (long.MinValue, long.MaxValue),
            KeyType.ULong => (ulong.MinValue, ulong.MaxValue),
            _ => (Int128.Zero, (Int128)(-1))
        };
        return min <= max;
    }

    private static bool TryGetInteger(object value, out Int128 v)
    {
        switch (value)
        {
            case sbyte x: v = x; return true;
            case byte x: v = x; return true;
            case short x: v = x; return true;
            case ushort x: v = x; return true;
            case char x: v = x; return true;
            case int x: v = x; return true;
            case uint x: v = x; return true;
            case long x: v = x; return true;
            case ulong x: v = x; return true;
            default: v = 0; return false;
        }
    }

    private static long NormalizeFloat(object value, ref CompareOp op)
    {
        if (value is float)
        {
            return EncodeThreshold(value, KeyType.Float);
        }

        var d = Convert.ToDouble(value);
        if (double.IsNaN(d))
        {
            // Every comparison against NaN is false but !=; the tree orders keys and has no NaN-aware answer, so state the outcome as a range. A field
            // that itself holds NaN is outside this contract (neither range admits it).
            op = op == CompareOp.NotEqual ? CompareOp.GreaterThanOrEqual : CompareOp.LessThan;
            return EncodeThreshold(float.NegativeInfinity, KeyType.Float);
        }

        var f = (float)d;
        if ((double)f == d)
        {
            return EncodeThreshold(f, KeyType.Float);
        }

        // d lies strictly between two floats and f is the nearer one; compare against f with the operator that keeps the double comparison's meaning.
        var fAbove = (double)f > d;
        switch (op)
        {
            case CompareOp.Equal:
                op = CompareOp.LessThan;
                return EncodeThreshold(float.NegativeInfinity, KeyType.Float);
            case CompareOp.NotEqual:
                op = CompareOp.GreaterThanOrEqual;
                return EncodeThreshold(float.NegativeInfinity, KeyType.Float);
            case CompareOp.GreaterThan:
            case CompareOp.GreaterThanOrEqual:
                op = fAbove ? CompareOp.GreaterThanOrEqual : CompareOp.GreaterThan;
                return EncodeThreshold(f, KeyType.Float);
            default:
                op = fAbove ? CompareOp.LessThan : CompareOp.LessThanOrEqual;
                return EncodeThreshold(f, KeyType.Float);
        }
    }

    public static long EncodeThreshold(object value, KeyType keyType)
    {
        switch (keyType)
        {
            case KeyType.Bool:
                return Convert.ToBoolean(value) ? 1L : 0L;
            case KeyType.SByte:
                return Convert.ToSByte(value);
            case KeyType.Byte:
                return Convert.ToByte(value);
            case KeyType.Short:
                return Convert.ToInt16(value);
            case KeyType.UShort:
                return Convert.ToUInt16(value);
            case KeyType.Int:
                return Convert.ToInt32(value);
            case KeyType.UInt:
                return Convert.ToUInt32(value);
            case KeyType.Long:
                return Convert.ToInt64(value);
            case KeyType.ULong:
                return (long)Convert.ToUInt64(value);
            case KeyType.Float:
            {
                var f = Convert.ToSingle(value);
                return Unsafe.As<float, int>(ref f);
            }
            case KeyType.Double:
            {
                var d = Convert.ToDouble(value);
                return Unsafe.As<double, long>(ref d);
            }
            default:
                throw new NotSupportedException($"Key type {keyType} is not supported.");
        }
    }
}
