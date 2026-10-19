using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
#if NET
using System.Numerics;
using System.Text.Unicode;
#endif

namespace Typhon.Protocol;

/// <summary>
/// The BCL members this assembly needs that netstandard2.1 lacks (#1034, Unity). On .NET each is an inlined call to the runtime's own, so net10.0's
/// code is what it was; on netstandard2.1 each is a port that gives the same bits — the golden vectors run against both builds.
/// </summary>
internal static class Bcl
{
    /// <summary><c>x × 2ⁿ</c>, correctly rounded (<see cref="Math"/>.ScaleB).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ScaleB(double x, int n)
    {
#if NET
        return Math.ScaleB(x, n);
#else
        // A product with an exact power of two rounds once, as ScaleB does; past the normal exponents the power is split, which can round twice only
        // where the result is subnormal — far outside every unit the wire uses (a velocity unit is 2^-20 to 2^10 metres).
        if (n is >= -1022 and <= 1023)
        {
            return x * Pow2(n);
        }

        var half = n / 2;
        return x * Pow2(half) * Pow2(n - half);
#endif
    }

    /// <summary>The next double toward +∞ (<see cref="Math"/>.BitIncrement).</summary>
    public static double BitIncrement(double x)
    {
#if NET
        return Math.BitIncrement(x);
#else
        // The runtime's own algorithm (System.Math.BitIncrement).
        var bits = BitConverter.DoubleToInt64Bits(x);
        if (((bits >> 32) & 0x7FF00000) >= 0x7FF00000)
        {
            return bits == unchecked((long)0xFFF0000000000000) ? double.MinValue : x;
        }

        if (bits == unchecked((long)0x8000000000000000))
        {
            return double.Epsilon;
        }

        bits += bits < 0 ? -1 : +1;
        return BitConverter.Int64BitsToDouble(bits);
#endif
    }

    /// <summary>The number of leading zero bits of <paramref name="value"/> (<c>BitOperations.LeadingZeroCount</c>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int LeadingZeroCount(ulong value)
    {
#if NET
        return BitOperations.LeadingZeroCount(value);
#else
        if (value == 0)
        {
            return 64;
        }

        var n = 0;
        if ((value & 0xFFFFFFFF00000000UL) == 0) { n += 32; value <<= 32; }
        if ((value & 0xFFFF000000000000UL) == 0) { n += 16; value <<= 16; }
        if ((value & 0xFF00000000000000UL) == 0) { n += 8; value <<= 8; }
        if ((value & 0xF000000000000000UL) == 0) { n += 4; value <<= 4; }
        if ((value & 0xC000000000000000UL) == 0) { n += 2; value <<= 2; }
        if ((value & 0x8000000000000000UL) == 0) { n += 1; }
        return n;
#endif
    }

    /// <summary>Writes a binary64 little-endian.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteDoubleLittleEndian(Span<byte> destination, double value)
    {
#if NET
        BinaryPrimitives.WriteDoubleLittleEndian(destination, value);
#else
        BinaryPrimitives.WriteInt64LittleEndian(destination, BitConverter.DoubleToInt64Bits(value));
#endif
    }

    /// <summary>Reads a binary64 little-endian.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ReadDoubleLittleEndian(ReadOnlySpan<byte> source)
    {
#if NET
        return BinaryPrimitives.ReadDoubleLittleEndian(source);
#else
        return BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(source));
#endif
    }

    /// <summary>Reads a binary32 little-endian.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float ReadSingleLittleEndian(ReadOnlySpan<byte> source)
    {
#if NET
        return BinaryPrimitives.ReadSingleLittleEndian(source);
#else
        return BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(source));
#endif
    }

    /// <summary>The bits of a binary32.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint SingleToUInt32Bits(float value)
    {
#if NET
        return BitConverter.SingleToUInt32Bits(value);
#else
        return unchecked((uint)BitConverter.SingleToInt32Bits(value));
#endif
    }

    /// <summary>A double converted straight to IEEE half bits: ties to even, overflow to infinity. The caller handles NaN.</summary>
    public static ushort DoubleToHalfBits(double value)
    {
#if NET
        return BitConverter.HalfToUInt16Bits((Half)value);
#else
        // The TypeScript SDK's encodeF16 (math.ts), which the golden vectors pin against the runtime's (Half) conversion: never through a float32.
        var s = BitConverter.DoubleToInt64Bits(value) < 0 ? 0x8000 : 0;
        var a = Math.Abs(value);
        if (double.IsPositiveInfinity(a))
        {
            return (ushort)(s | 0x7C00);
        }

        if (a < Pow2(-14))
        {
            // Subnormal; r = 1024 lands on the smallest normal.
            return (ushort)(s | RoundHalfEven(a * Pow2(24)));
        }

        var e = (int)((BitConverter.DoubleToInt64Bits(a) >> 52) & 0x7FF) - 1023;
        if (e > 15)
        {
            return (ushort)(s | 0x7C00);
        }

        var r = RoundHalfEven(a * Pow2(10 - e));
        if (r == 2048)
        {
            r = 1024;
            e += 1;
        }

        return e > 15 ? (ushort)(s | 0x7C00) : (ushort)(s | ((e + 15) << 10) | (r - 1024));
#endif
    }

    /// <summary>IEEE half bits widened exactly to a double. A NaN pattern decodes as some NaN; the caller canonicalizes it.</summary>
    public static double HalfBitsToDouble(ushort bits)
    {
#if NET
        return (double)BitConverter.UInt16BitsToHalf(bits);
#else
        var e = (bits >> 10) & 31;
        var f = bits & 0x3FF;
        double v;
        if (e == 0)
        {
            v = f / 16777216.0;
        }
        else if (e == 31)
        {
            if (f != 0)
            {
                return double.NaN;
            }

            v = double.PositiveInfinity;
        }
        else
        {
            v = (1024 + f) * Pow2(e - 25);
        }

        return (bits & 0x8000) != 0 ? -v : v;
#endif
    }

    /// <summary>Whether <paramref name="utf8"/> is well-formed UTF-8 (no overlong form, no surrogate, nothing past U+10FFFF).</summary>
    public static bool IsValidUtf8(ReadOnlySpan<byte> utf8)
    {
#if NET
        return Utf8.IsValid(utf8);
#else
        // The strict decoder refuses exactly what Utf8.IsValid refuses. It throws only on invalid input, which the wire reader then refuses anyway.
        try
        {
            StrictUtf8.GetCharCount(utf8);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
#endif
    }

#if !NET
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static double Pow2(int n) => BitConverter.Int64BitsToDouble((long)(n + 1023) << 52);

    // Ties to even over m < 2^11, where every step is exact.
    private static int RoundHalfEven(double m)
    {
        var f = Math.Floor(m);
        var d = m - f;
        return (int)(d > 0.5 ? f + 1 : d < 0.5 ? f : f + (f % 2));
    }
#endif
}
