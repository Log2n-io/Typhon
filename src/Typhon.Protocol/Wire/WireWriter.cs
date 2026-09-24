using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;

namespace Typhon.Protocol;

/// <summary>
/// Writes the wire's primitives (03-wire-protocol § 2) into a caller-owned span.
/// </summary>
/// <remarks>
/// <para>
/// Encoders always write the canonical form: minimal varints, little-endian integers, NaN as <c>0x7E00</c> in a half. Two encoders fed the same values
/// therefore produce the same bytes, which is what lets a golden vector be a byte comparison.
/// </para>
/// <para>
/// Running out of span throws <see cref="InvalidOperationException"/>: the caller sized the buffer, so overflow is a bug on this side, never input from a
/// peer. Values that cannot be represented (a string over its cap, a negative count) throw <see cref="ArgumentException"/> for the same reason.
/// </para>
/// </remarks>
public ref struct WireWriter
{
    private const int MaxVaruBytes = 5;

    private readonly Span<byte> _buffer;
    private int _position;

    /// <summary>Creates a writer over <paramref name="buffer"/>, positioned at its first byte.</summary>
    /// <param name="buffer">Where to write.</param>
    public WireWriter(Span<byte> buffer)
    {
        _buffer = buffer;
        _position = 0;
    }

    /// <summary>Bytes written so far.</summary>
    public readonly int Position => _position;

    /// <summary>The bytes written so far.</summary>
    public readonly ReadOnlySpan<byte> Written => _buffer[.._position];

    /// <summary>Writes a <c>u8</c>.</summary>
    /// <param name="value">The value.</param>
    public void WriteU8(byte value) => Take(1)[0] = value;

    /// <summary>Writes an <c>i8</c>.</summary>
    /// <param name="value">The value.</param>
    public void WriteI8(sbyte value) => Take(1)[0] = (byte)value;

    /// <summary>Writes a little-endian <c>u16</c>.</summary>
    /// <param name="value">The value.</param>
    public void WriteU16(ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(Take(2), value);

    /// <summary>Writes a little-endian <c>i16</c>.</summary>
    /// <param name="value">The value.</param>
    public void WriteI16(short value) => BinaryPrimitives.WriteInt16LittleEndian(Take(2), value);

    /// <summary>Writes the low 24 bits of <paramref name="value"/>, little-endian.</summary>
    /// <param name="value">The value; bits above 23 are dropped, so a negative <see cref="int"/> writes its two's complement.</param>
    public void WriteU24(uint value)
    {
        var b = Take(3);
        b[0] = (byte)value;
        b[1] = (byte)(value >> 8);
        b[2] = (byte)(value >> 16);
    }

    /// <summary>Writes a little-endian <c>u32</c>.</summary>
    /// <param name="value">The value.</param>
    public void WriteU32(uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Take(4), value);

    /// <summary>Writes a little-endian <c>i32</c>.</summary>
    /// <param name="value">The value.</param>
    public void WriteI32(int value) => BinaryPrimitives.WriteInt32LittleEndian(Take(4), value);

    /// <summary>
    /// Writes the low <paramref name="bits"/> ∈ {8, 16, 24, 32} bits of <paramref name="value"/>: an unsigned code, or a signed code's two's complement.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <param name="bits">The width.</param>
    public void WriteBits(uint value, int bits)
    {
        switch (bits)
        {
            case 8:
                WriteU8((byte)value);
                break;
            case 16:
                WriteU16((ushort)value);
                break;
            case 24:
                WriteU24(value);
                break;
            case 32:
                WriteU32(value);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(bits), bits, "a byte-aligned width is 8, 16, 24 or 32 bits");
        }
    }

    /// <summary>Writes a <c>varu</c>: minimal unsigned LEB128.</summary>
    /// <param name="value">The value.</param>
    public void WriteVaru(uint value)
    {
        while (value >= 0x80)
        {
            WriteU8((byte)(value | 0x80));
            value >>= 7;
        }

        WriteU8((byte)value);
    }

    /// <summary>Writes a <c>vari</c>: zigzag, then <c>varu</c>.</summary>
    /// <param name="value">The value.</param>
    public void WriteVari(int value) => WriteVaru((uint)((value << 1) ^ (value >> 31)));

    /// <summary>Writes a little-endian <c>u64</c>.</summary>
    /// <param name="value">The value.</param>
    public void WriteU64(ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(Take(8), value);

    /// <summary>Writes a little-endian IEEE double; NaN as <c>0x7FF8000000000000</c>.</summary>
    /// <param name="value">The value.</param>
    public void WriteF64(double value) => BinaryPrimitives.WriteDoubleLittleEndian(Take(8), WireMath.CanonicalizeNaN(value));

    /// <summary>Writes a little-endian IEEE single, narrowing <paramref name="value"/> with ties to even; NaN as <c>0x7FC00000</c>.</summary>
    /// <param name="value">The value.</param>
    public void WriteF32(double value) => BinaryPrimitives.WriteUInt32LittleEndian(Take(4), WireMath.EncodeSingle(value));

    /// <summary>
    /// Writes a little-endian IEEE half converted straight from <paramref name="value"/> (W10): ties to even, overflow to ∞, NaN as <c>0x7E00</c>.
    /// </summary>
    /// <param name="value">The value, in its source precision — never narrowed through a float first.</param>
    public void WriteF16(double value) => BinaryPrimitives.WriteUInt16LittleEndian(Take(2), WireMath.EncodeHalf(value));

    /// <summary>Writes raw bytes.</summary>
    /// <param name="bytes">The bytes.</param>
    public void WriteBytes(scoped ReadOnlySpan<byte> bytes) => bytes.CopyTo(Take(bytes.Length));

    /// <summary>Writes a <c>blob</c>: a <c>varu</c> length, then the bytes.</summary>
    /// <param name="bytes">The bytes.</param>
    /// <param name="maxBytes">The declared cap.</param>
    public void WriteBlob(scoped ReadOnlySpan<byte> bytes, int maxBytes)
    {
        if (bytes.Length > maxBytes)
        {
            throw new ArgumentException($"blob of {bytes.Length} bytes exceeds its cap of {maxBytes}", nameof(bytes));
        }

        WriteVaru((uint)bytes.Length);
        WriteBytes(bytes);
    }

    /// <summary>Writes a <c>str</c>: a <c>varu</c> byte length, then the UTF-8 bytes.</summary>
    /// <param name="value">The string; <see langword="null"/> writes the empty string.</param>
    /// <param name="maxBytes">The declared cap.</param>
    public void WriteStr(string value, int maxBytes)
    {
        value ??= string.Empty;
        var length = Encoding.UTF8.GetByteCount(value);
        if (length > maxBytes)
        {
            throw new ArgumentException($"string of {length} UTF-8 bytes exceeds its cap of {maxBytes}", nameof(value));
        }

        WriteVaru((uint)length);
        Encoding.UTF8.GetBytes(value, Take(length));
    }

    /// <summary>
    /// Reserves room for a <c>varu</c> length prefix and returns a mark for <see cref="EndLengthPrefixed"/>. The content is written next, and the prefix is
    /// patched in minimal form afterwards, so the caller never has to know a block's size before encoding it.
    /// </summary>
    /// <remarks>
    /// The reservation is five bytes whatever the final prefix, so a buffer must have up to four bytes of slack beyond the encoded size per open prefix.
    /// Prefixes nest, and must be ended innermost first.
    /// </remarks>
    /// <returns>The mark.</returns>
    public int BeginLengthPrefixed()
    {
        var mark = _position;
        Take(MaxVaruBytes);
        return mark;
    }

    /// <summary>Writes the minimal <c>varu</c> length of everything written since <paramref name="mark"/> and moves the content down behind it.</summary>
    /// <param name="mark">The value <see cref="BeginLengthPrefixed"/> returned.</param>
    public void EndLengthPrefixed(int mark)
    {
        var contentStart = mark + MaxVaruBytes;
        Debug.Assert(mark >= 0 && contentStart <= _position, "EndLengthPrefixed called out of nesting order, or with a foreign mark");
        var length = _position - contentStart;
        var prefixBytes = VaruSize((uint)length);
        _buffer.Slice(contentStart, length).CopyTo(_buffer[(mark + prefixBytes)..]);
        var value = (uint)length;
        var at = mark;
        while (value >= 0x80)
        {
            _buffer[at++] = (byte)(value | 0x80);
            value >>= 7;
        }

        _buffer[at] = (byte)value;
        _position = mark + prefixBytes + length;
    }

    /// <summary>The number of bytes <see cref="WriteVaru"/> writes for <paramref name="value"/>.</summary>
    /// <param name="value">The value.</param>
    /// <returns>1 to 5.</returns>
    public static int VaruSize(uint value) => value switch
    {
        < 1u << 7 => 1,
        < 1u << 14 => 2,
        < 1u << 21 => 3,
        < 1u << 28 => 4,
        _ => 5,
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Span<byte> Take(int count)
    {
        if (count > _buffer.Length - _position)
        {
            ThrowOverflow(count, _position, _buffer.Length);
        }

        var span = _buffer.Slice(_position, count);
        _position += count;
        return span;
    }

    [DoesNotReturn]
    private static void ThrowOverflow(int count, int position, int length) =>
        throw new InvalidOperationException($"wire buffer too small: needed {count} more byte(s) at {position} of {length}");
}
