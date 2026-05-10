using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Typhon.Engine;

public enum KeyType : byte
{
    Bool = 0,
    Byte = 1,
    SByte = 2,
    Short = 3,
    UShort = 4,
    Int = 5,
    UInt = 6,
    Long = 7,
    ULong = 8,
    Float = 9,
    Double = 10,
    String64 = 11
}

public enum CompareOp : byte
{
    Equal = 0,
    NotEqual = 1,
    GreaterThan = 2,
    LessThan = 3,
    GreaterThanOrEqual = 4,
    LessThanOrEqual = 5
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct FieldEvaluator  // 16 bytes
{
    public byte FieldIndex;      // 1B — index into IndexedFieldInfos (max 63, capped by ring buffer flag encoding)
    public byte FieldSize;       // 1B
    public KeyType KeyType;      // 1B
    public CompareOp CompareOp;  // 1B
    public byte ComponentTag;    // 1B — 0=T1, 1=T2 (for multi-component views)
    public byte BranchIndex;     // 1B — DNF branch index (0 for AND views, 0..15 for OR views)
    public ushort FieldOffset;   // 2B — byte offset within component (max ~64KB, components are small structs)
    public long Threshold;       // 8B — widened constant (reinterpret for float/double)

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe bool Evaluate(ref FieldEvaluator eval, byte* fieldPtr)
    {
        // Read value from fieldPtr based on KeyType, dispatch to compare helpers
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
