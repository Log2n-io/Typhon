using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct KeyBytes8  // 8 bytes
{
    private long _value;

    public long RawValue
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _value;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => _value = value;
    }

    public bool IsZero
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _value == 0;
    }

    // Static factories
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static KeyBytes8 FromInt(int v) => new() { _value = v };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static KeyBytes8 FromLong(long v) => new() { _value = v };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static KeyBytes8 FromFloat(float v)
    {
        var bits = Unsafe.As<float, int>(ref v);
        return new KeyBytes8 { _value = bits };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static KeyBytes8 FromDouble(double v)
    {
        var bits = Unsafe.As<double, long>(ref v);
        return new KeyBytes8 { _value = bits };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static KeyBytes8 FromBool(bool v) => new() { _value = v ? 1 : 0 };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static KeyBytes8 FromByte(byte v) => new() { _value = v };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static KeyBytes8 FromSByte(sbyte v) => new() { _value = v };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static KeyBytes8 FromShort(short v) => new() { _value = v };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static KeyBytes8 FromUShort(ushort v) => new() { _value = v };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static KeyBytes8 FromUInt(uint v) => new() { _value = v };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static KeyBytes8 FromULong(ulong v) => new() { _value = (long)v };

    // Readback
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int AsInt() => (int)_value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long AsLong() => _value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float AsFloat()
    {
        var bits = (int)_value;
        return Unsafe.As<int, float>(ref bits);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double AsDouble() => Unsafe.As<long, double>(ref _value);

    // Unsafe factory. The low <paramref name="size"/> bytes, zero-extended: a fixed-width load for the four key widths, which a variable-length copy
    // turned into a call per key on every capture and every drain compare (#1346).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe KeyBytes8 FromPointer(byte* ptr, int size)
    {
        switch (size)
        {
            case 8:
                return new KeyBytes8 { _value = Unsafe.ReadUnaligned<long>(ptr) };
            case 4:
                return new KeyBytes8 { _value = Unsafe.ReadUnaligned<uint>(ptr) };
            case 2:
                return new KeyBytes8 { _value = Unsafe.ReadUnaligned<ushort>(ptr) };
            case 1:
                return new KeyBytes8 { _value = *ptr };
        }

        var result = new KeyBytes8();
        Unsafe.CopyBlockUnaligned(ref Unsafe.As<long, byte>(ref result._value), ref *ptr, (uint)size);
        return result;
    }
}