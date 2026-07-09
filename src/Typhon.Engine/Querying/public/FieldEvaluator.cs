using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Typhon.Engine;

/// <summary>Scalar type of an indexed field, used to reinterpret its raw bytes when evaluating a predicate.</summary>
public enum KeyType : byte
{
    /// <summary>1-byte boolean (non-zero is <c>true</c>).</summary>
    Bool = 0,
    /// <summary>Unsigned 8-bit integer.</summary>
    Byte = 1,
    /// <summary>Signed 8-bit integer.</summary>
    SByte = 2,
    /// <summary>Signed 16-bit integer.</summary>
    Short = 3,
    /// <summary>Unsigned 16-bit integer.</summary>
    UShort = 4,
    /// <summary>Signed 32-bit integer.</summary>
    Int = 5,
    /// <summary>Unsigned 32-bit integer.</summary>
    UInt = 6,
    /// <summary>Signed 64-bit integer.</summary>
    Long = 7,
    /// <summary>Unsigned 64-bit integer.</summary>
    ULong = 8,
    /// <summary>32-bit IEEE-754 float.</summary>
    Float = 9,
    /// <summary>64-bit IEEE-754 double.</summary>
    Double = 10,
    /// <summary>Fixed 64-byte string key. Reserved for the index/statistics layer; not evaluated by the scalar comparator.</summary>
    String64 = 11
}

/// <summary>Comparison operator applied between a field value and a predicate threshold.</summary>
public enum CompareOp : byte
{
    /// <summary>Field equals the threshold.</summary>
    Equal = 0,
    /// <summary>Field does not equal the threshold.</summary>
    NotEqual = 1,
    /// <summary>Field is strictly greater than the threshold.</summary>
    GreaterThan = 2,
    /// <summary>Field is strictly less than the threshold.</summary>
    LessThan = 3,
    /// <summary>Field is greater than or equal to the threshold.</summary>
    GreaterThanOrEqual = 4,
    /// <summary>Field is less than or equal to the threshold.</summary>
    LessThanOrEqual = 5
}

/// <summary>
/// A single scalar field predicate: compares one indexed field against a constant threshold. Packed to 16 bytes for
/// cache-dense storage in an execution plan's filter chain.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct FieldEvaluator  // 16 bytes
{
    /// <summary>Index into the component's <see cref="ComponentTable.IndexedFieldInfos"/>. Max 63 (capped by ring-buffer flag encoding).</summary>
    public byte FieldIndex;      // 1B — index into IndexedFieldInfos (max 63, capped by ring buffer flag encoding)
    /// <summary>Size of the field in bytes.</summary>
    public byte FieldSize;       // 1B
    /// <summary>Scalar type used to interpret the field bytes.</summary>
    public KeyType KeyType;      // 1B
    /// <summary>Comparison operator to apply.</summary>
    public CompareOp CompareOp;  // 1B
    /// <summary>Which component the field belongs to in a multi-component view: 0 = first (T1), 1 = second (T2).</summary>
    public byte ComponentTag;    // 1B — 0=T1, 1=T2 (for multi-component views)
    /// <summary>Disjunctive-normal-form branch index: 0 for AND views, 0..15 for OR views.</summary>
    public byte BranchIndex;     // 1B — DNF branch index (0 for AND views, 0..15 for OR views)
    /// <summary>Byte offset of the field within its component struct.</summary>
    public ushort FieldOffset;   // 2B — byte offset within component (max ~64KB, components are small structs)
    /// <summary>Comparison constant widened to 64 bits. For float/double fields the raw bit pattern is reinterpreted, not numerically converted.</summary>
    public long Threshold;       // 8B — widened constant (reinterpret for float/double)

    /// <summary>
    /// Evaluate this predicate against a raw field pointer. Returns <c>true</c> if the field satisfies the comparison.
    /// </summary>
    /// <remarks>
    /// <see cref="KeyType.String64"/> is intentionally not handled here — string-typed predicates are rejected upstream by
    /// <c>QueryResolverHelper.MapFieldTypeToKeyType</c> (which throws <see cref="System.NotSupportedException"/> for <c>FieldType.String</c>), and the
    /// <c>String64</c> tag is reserved for the index/statistics layer (e.g., <c>IndexStatistics</c> uses it to mark <c>String64BTree</c> indexes as having no
    /// selectivity statistics). The <c>Threshold</c> slot is only 8 bytes — not large enough to hold a 64-byte <c>String64</c> value — so any
    /// String64 predicate evaluation would have to happen via the B+Tree index path, not this scalar comparator.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe bool Evaluate(ref FieldEvaluator eval, byte* fieldPtr)
    {
        // Read value from fieldPtr based on KeyType, dispatch to compare helpers.
        // String64 is filtered out upstream by MapFieldTypeToKeyType — see remarks above.
        switch (eval.KeyType)
        {
            case KeyType.Bool:
            {
                var val = *fieldPtr != 0;
                var thr = eval.Threshold != 0;
                return eval.CompareOp switch
                {
                    CompareOp.Equal => val == thr,
                    CompareOp.NotEqual => val != thr,
                    _ => false
                };
            }
            case KeyType.Byte:
                return CompareUnsigned(*fieldPtr, (ulong)eval.Threshold, eval.CompareOp);
            case KeyType.SByte:
                return CompareSigned(*(sbyte*)fieldPtr, eval.Threshold, eval.CompareOp);
            case KeyType.Short:
                return CompareSigned(*(short*)fieldPtr, eval.Threshold, eval.CompareOp);
            case KeyType.UShort:
                return CompareUnsigned(*(ushort*)fieldPtr, (ulong)eval.Threshold, eval.CompareOp);
            case KeyType.Int:
                return CompareSigned(*(int*)fieldPtr, eval.Threshold, eval.CompareOp);
            case KeyType.UInt:
                return CompareUnsigned(*(uint*)fieldPtr, (ulong)eval.Threshold, eval.CompareOp);
            case KeyType.Long:
                return CompareSigned(*(long*)fieldPtr, eval.Threshold, eval.CompareOp);
            case KeyType.ULong:
                return CompareUnsigned(*(ulong*)fieldPtr, (ulong)eval.Threshold, eval.CompareOp);
            case KeyType.Float:
            {
                var bits = (int)eval.Threshold;
                var thr = Unsafe.As<int, float>(ref bits);
                return CompareFloat(*(float*)fieldPtr, thr, eval.CompareOp);
            }
            case KeyType.Double:
            {
                var thr = Unsafe.As<long, double>(ref eval.Threshold);
                return CompareFloat(*(double*)fieldPtr, thr, eval.CompareOp);
            }
            default:
                return false;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool CompareSigned(long val, long thr, CompareOp op) =>
        op switch
        {
            CompareOp.Equal => val == thr,
            CompareOp.NotEqual => val != thr,
            CompareOp.GreaterThan => val > thr,
            CompareOp.LessThan => val < thr,
            CompareOp.GreaterThanOrEqual => val >= thr,
            CompareOp.LessThanOrEqual => val <= thr,
            _ => false
        };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool CompareUnsigned(ulong val, ulong thr, CompareOp op) =>
        op switch
        {
            CompareOp.Equal => val == thr,
            CompareOp.NotEqual => val != thr,
            CompareOp.GreaterThan => val > thr,
            CompareOp.LessThan => val < thr,
            CompareOp.GreaterThanOrEqual => val >= thr,
            CompareOp.LessThanOrEqual => val <= thr,
            _ => false
        };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool CompareFloat(double val, double thr, CompareOp op) =>
        op switch
        {
            CompareOp.Equal => val == thr,
            CompareOp.NotEqual => val != thr,
            CompareOp.GreaterThan => val > thr,
            CompareOp.LessThan => val < thr,
            CompareOp.GreaterThanOrEqual => val >= thr,
            CompareOp.LessThanOrEqual => val <= thr,
            _ => false
        };
}
