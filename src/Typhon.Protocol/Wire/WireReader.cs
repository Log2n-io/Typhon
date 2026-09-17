using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Unicode;

namespace Typhon.Protocol;

/// <summary>
/// Reads the wire's primitives (03-wire-protocol § 2) from a span: little-endian fixed-width integers, LEB128 varints, half and single floats, and
/// length-prefixed strings and blobs.
/// </summary>
/// <remarks>
/// <para>
/// Every read validates against what remains and throws <see cref="WireFormatException"/> with close code 1007 when the input is short or out of range, so a
/// decoder built on it never indexes past its message. It never allocates except in <see cref="ReadStr"/>, which returns a managed string.
/// </para>
/// <para>
/// <b>Varints are read leniently.</b> An over-long encoding (<c>0x80 0x00</c> for zero) is accepted as long as the value fits 32 bits in at most five bytes;
/// encoders always write the minimal form, so golden vectors stay canonical while a decoder does not have to police it.
/// </para>
/// </remarks>
public ref struct WireReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly ReadOnlySpan<byte> _data;
    private int _position;

    /// <summary>Creates a reader over <paramref name="data"/>, positioned at its first byte.</summary>
    /// <param name="data">The bytes to read.</param>
    public WireReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _position = 0;
    }

    /// <summary>Bytes consumed so far.</summary>
    public readonly int Position => _position;

    /// <summary>Bytes left to read.</summary>
    public readonly int Remaining => _data.Length - _position;

    /// <summary>Whether every byte has been consumed.</summary>
    public readonly bool IsAtEnd => _position == _data.Length;

    /// <summary>Reads a <c>u8</c>.</summary>
    /// <returns>The value.</returns>
    public byte ReadU8() => Take(1)[0];

    /// <summary>Reads an <c>i8</c>.</summary>
    /// <returns>The value.</returns>
    public sbyte ReadI8() => (sbyte)Take(1)[0];

    /// <summary>Reads a little-endian <c>u16</c>.</summary>
    /// <returns>The value.</returns>
    public ushort ReadU16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

    /// <summary>Reads a little-endian <c>i16</c>.</summary>
    /// <returns>The value.</returns>
    public short ReadI16() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));

    /// <summary>Reads a little-endian <c>u24</c>: <c>b0 | b1 &lt;&lt; 8 | b2 &lt;&lt; 16</c>.</summary>
    /// <returns>The value, in [0, 2²⁴).</returns>
    public uint ReadU24()
    {
        var b = Take(3);
        return (uint)(b[0] | (b[1] << 8) | (b[2] << 16));
    }

    /// <summary>Reads a little-endian <c>i24</c>: a <c>u24</c> sign-extended from bit 23.</summary>
    /// <returns>The value, in [−2²³, 2²³).</returns>
    public int ReadI24() => ((int)ReadU24() << 8) >> 8;

    /// <summary>Reads a little-endian <c>u32</c>.</summary>
    /// <returns>The value.</returns>
    public uint ReadU32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

    /// <summary>Reads a little-endian <c>i32</c>.</summary>
    /// <returns>The value.</returns>
    public int ReadI32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

    /// <summary>Reads an unsigned integer of <paramref name="bits"/> ∈ {8, 16, 24, 32} bits.</summary>
    /// <param name="bits">The width.</param>
    /// <returns>The value.</returns>
    public uint ReadUnsigned(int bits) => bits switch
    {
        8 => ReadU8(),
        16 => ReadU16(),
        24 => ReadU24(),
        32 => ReadU32(),
        _ => throw new ArgumentOutOfRangeException(nameof(bits), bits, "a byte-aligned width is 8, 16, 24 or 32 bits"),
    };

    /// <summary>Reads a two's-complement integer of <paramref name="bits"/> ∈ {8, 16, 24, 32} bits, sign-extended.</summary>
    /// <param name="bits">The width.</param>
    /// <returns>The value.</returns>
    public int ReadSigned(int bits) => bits switch
    {
        8 => ReadI8(),
        16 => ReadI16(),
        24 => ReadI24(),
        32 => ReadI32(),
        _ => throw new ArgumentOutOfRangeException(nameof(bits), bits, "a byte-aligned width is 8, 16, 24 or 32 bits"),
    };

    /// <summary>Reads a <c>varu</c>: unsigned LEB128, at most five bytes, fitting 32 bits.</summary>
    /// <returns>The value.</returns>
    public uint ReadVaru()
    {
        uint result = 0;
        for (var shift = 0; shift < 35; shift += 7)
        {
            var b = ReadU8();
            if (shift == 28 && b > 0x0F)
            {
                throw WireFormatException.Malformed("varu does not fit 32 bits");
            }

            result |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }
        }

        // Unreachable: a fifth byte above 0x0F throws above, and one at or below it has no continuation bit.
        return result;
    }

    /// <summary>Reads a <c>vari</c>: a zigzag-mapped <c>varu</c>.</summary>
    /// <returns>The value.</returns>
    public int ReadVari()
    {
        var u = ReadVaru();
        return (int)(u >> 1) ^ -(int)(u & 1);
    }

    /// <summary>Reads a <c>varu</c> that must not exceed <paramref name="max"/>, as an <see cref="int"/> ready to use as a count or index.</summary>
    /// <param name="max">The largest acceptable value.</param>
    /// <param name="what">What the value is, for the error message.</param>
    /// <returns>The value.</returns>
    public int ReadVaruAtMost(int max, string what)
    {
        var value = ReadVaru();
        if (value > (uint)max)
        {
            throw WireFormatException.Malformed($"{what} {value} exceeds {max}");
        }

        return (int)value;
    }

    /// <summary>Reads a little-endian IEEE single.</summary>
    /// <returns>The value, widened exactly to a double; every NaN pattern decodes as the canonical NaN (<see cref="WireMath.CanonicalNaN"/>).</returns>
    public double ReadF32() => WireMath.CanonicalizeNaN(BinaryPrimitives.ReadSingleLittleEndian(Take(4)));

    /// <summary>Reads a little-endian IEEE half.</summary>
    /// <returns>The value, widened exactly to a double; every NaN pattern decodes as the canonical NaN.</returns>
    public double ReadF16() => WireMath.DecodeHalf(BinaryPrimitives.ReadUInt16LittleEndian(Take(2)));

    /// <summary>Reads <paramref name="count"/> raw bytes.</summary>
    /// <param name="count">How many.</param>
    /// <returns>A view of the bytes; valid as long as the underlying buffer is.</returns>
    public ReadOnlySpan<byte> ReadBytes(int count) => Take(count);

    /// <summary>Reads a <c>blob</c>: a <c>varu</c> length, at most <paramref name="maxBytes"/>, then that many bytes.</summary>
    /// <param name="maxBytes">The cap the catalog or protocol declares.</param>
    /// <returns>A view of the bytes.</returns>
    public ReadOnlySpan<byte> ReadBlob(int maxBytes) => Take(ReadVaruAtMost(maxBytes, "blob length"));

    /// <summary>Reads a <c>str</c>: a <c>varu</c> byte length, at most <paramref name="maxBytes"/>, then that many bytes of valid UTF-8.</summary>
    /// <param name="maxBytes">The cap the catalog or protocol declares.</param>
    /// <returns>A view of the UTF-8 bytes, validated.</returns>
    public ReadOnlySpan<byte> ReadStrUtf8(int maxBytes)
    {
        var bytes = Take(ReadVaruAtMost(maxBytes, "string length"));
        if (!Utf8.IsValid(bytes))
        {
            throw WireFormatException.Malformed("string is not valid UTF-8");
        }

        return bytes;
    }

    /// <summary>Reads a <c>str</c> as a managed string. Allocates; decoders on a zero-allocation path use <see cref="ReadStrUtf8"/>.</summary>
    /// <param name="maxBytes">The cap the catalog or protocol declares.</param>
    /// <returns>The string.</returns>
    public string ReadStr(int maxBytes)
    {
        var bytes = Take(ReadVaruAtMost(maxBytes, "string length"));
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw WireFormatException.Malformed("string is not valid UTF-8");
        }
    }

    /// <summary>Returns a reader over the next <paramref name="length"/> bytes and advances past them: the way a length-prefixed block is isolated.</summary>
    /// <param name="length">The sub-reader's length.</param>
    /// <returns>A reader over exactly those bytes.</returns>
    public WireReader Slice(int length) => new(Take(length));

    /// <summary>Skips <paramref name="count"/> bytes.</summary>
    /// <param name="count">How many.</param>
    public void Skip(int count) => Take(count);

    /// <summary>Throws unless every byte has been consumed: a block whose content is shorter than its declared length is malformed, not padded.</summary>
    /// <param name="what">What was being read, for the error message.</param>
    public readonly void ExpectEnd(string what)
    {
        if (!IsAtEnd)
        {
            throw WireFormatException.Malformed($"{what}: {Remaining} unread byte(s) after the declared content");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadOnlySpan<byte> Take(int count)
    {
        if ((uint)count > (uint)Remaining)
        {
            ThrowShort(count, Remaining);
        }

        var span = _data.Slice(_position, count);
        _position += count;
        return span;
    }

    // Out of line so the message formatting does not keep Take from inlining into every primitive read.
    [DoesNotReturn]
    private static void ThrowShort(int count, int remaining) => throw WireFormatException.Malformed($"needed {count} byte(s), {remaining} left");
}
