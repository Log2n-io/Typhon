using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Typhon.Protocol;

/// <summary>
/// Receives the values of a decoded section, one call per field, in wire order. Implemented as a <c>struct</c> by a zero-allocation decoder, so the generic
/// call is specialized and inlined.
/// </summary>
public interface IFieldSink
{
    /// <summary>A numeric field's decoded components (<see cref="FieldPlan.Components"/> of them).</summary>
    /// <param name="field">The field.</param>
    /// <param name="components">The values; valid only for the duration of the call.</param>
    void Number(FieldPlan field, scoped ReadOnlySpan<double> components);

    /// <summary>
    /// A 64-bit integer field's decoded components (<see cref="FieldPlan.Components"/> of them, W32), as bit patterns: the value of a <c>u64</c> or
    /// <c>varu64</c>, the two's complement of an <c>i64</c> or <c>vari64</c> — <c>(long)value</c> recovers it. Never a double, which cannot hold them.
    /// </summary>
    /// <param name="field">The field.</param>
    /// <param name="components">The values; valid only for the duration of the call.</param>
    void Integer64(FieldPlan field, scoped ReadOnlySpan<ulong> components);

    /// <summary>A text field's validated UTF-8 bytes.</summary>
    /// <param name="field">The field.</param>
    /// <param name="utf8">The bytes; valid only for the duration of the call.</param>
    void Text(FieldPlan field, scoped ReadOnlySpan<byte> utf8);

    /// <summary>A bytes or blob field's content.</summary>
    /// <param name="field">The field.</param>
    /// <param name="bytes">The bytes; valid only for the duration of the call.</param>
    void Bytes(FieldPlan field, scoped ReadOnlySpan<byte> bytes);

    /// <summary>A list field: <paramref name="count"/> elements of <see cref="FieldPlan.Components"/> numbers each, flattened.</summary>
    /// <param name="field">The field.</param>
    /// <param name="count">The element count.</param>
    /// <param name="components">The flattened element values; valid only for the duration of the call.</param>
    void List(FieldPlan field, int count, scoped ReadOnlySpan<double> components);

    /// <summary>
    /// A collection field (W34): <paramref name="sent"/> elements follow, each opened by <see cref="CollectionElement"/> and made of its element fields'
    /// calls (fields whose <see cref="FieldPlan.Parent"/> is <paramref name="field"/>). <paramref name="sent"/> &lt; <paramref name="total"/> is a
    /// truncation the server made.
    /// </summary>
    /// <param name="field">The collection field.</param>
    /// <param name="total">How many elements the entity holds.</param>
    /// <param name="sent">How many follow.</param>
    /// <remarks>
    /// A no-op by default, for the sinks that never meet one — an event or a command carries no collection (W34). A tick sink that decodes entities
    /// implements it: a default member called through a struct sink boxes it.
    /// </remarks>
    void Collection(FieldPlan field, int total, int sent)
    {
    }

    /// <summary>Opens element <paramref name="index"/> of <paramref name="field"/>: the calls that follow, up to the next element, are its fields.</summary>
    /// <param name="field">The collection field.</param>
    /// <param name="index">The element's index, from 0.</param>
    /// <remarks>A no-op by default, as <see cref="Collection"/> is.</remarks>
    void CollectionElement(FieldPlan field, int index)
    {
    }
}

/// <summary>
/// A value to encode. Numbers carry the components of a numeric field, or the flattened elements of a list; <see cref="Integers"/> the components of a
/// 64-bit integer field; <see cref="Text"/> and <see cref="Bytes"/> carry the rest.
/// </summary>
public sealed class FieldValue
{
    /// <summary>Numeric components, or a list's flattened element components.</summary>
    public double[] Numbers { get; init; }

    /// <summary>
    /// A 64-bit integer field's components as bit patterns (W32): a signed value's two's complement. A 64-bit field given <see cref="Numbers"/> instead
    /// encodes them when each is an integer in the codec's range — exactly representable, so nothing is rounded.
    /// </summary>
    public ulong[] Integers { get; init; }

    /// <summary>Text, for a <c>str</c> field.</summary>
    public string Text { get; init; }

    /// <summary>Bytes, for a <c>bytes</c> or <c>blob</c> field.</summary>
    public byte[] Bytes { get; init; }

    /// <summary>
    /// A collection's elements (W34), each the values of its element fields in wire order (<see cref="FieldPlan.Ordinal"/>); they are the ones sent.
    /// </summary>
    public FieldValue[][] Elements { get; init; }

    /// <summary>A collection's total: how many elements the entity holds, at least <see cref="Elements"/>' count; 0 means exactly those.</summary>
    public int Total { get; init; }

    /// <summary>A collection of <paramref name="elements"/>, every one of them sent.</summary>
    /// <param name="elements">Each element's field values, in wire order.</param>
    /// <returns>The field value.</returns>
    public static FieldValue OfElements(params FieldValue[][] elements) => new() { Elements = elements };

    /// <summary>A scalar.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The field value.</returns>
    public static FieldValue Of(double value) => new() { Numbers = [value] };

    /// <summary>A vector or quaternion.</summary>
    /// <param name="components">The components.</param>
    /// <returns>The field value.</returns>
    public static FieldValue Of(params double[] components) => new() { Numbers = components };

    /// <summary>A string.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The field value.</returns>
    public static FieldValue Of(string text) => new() { Text = text };

    /// <summary>Raw bytes.</summary>
    /// <param name="bytes">The bytes.</param>
    /// <returns>The field value.</returns>
    public static FieldValue Of(byte[] bytes) => new() { Bytes = bytes };

    /// <summary>Unsigned 64-bit components, for a <c>u64</c> or <c>varu64</c> field.</summary>
    /// <param name="components">The components.</param>
    /// <returns>The field value.</returns>
    public static FieldValue OfUInt64(params ulong[] components) => new() { Integers = components };

    /// <summary>Signed 64-bit components, for an <c>i64</c> or <c>vari64</c> field.</summary>
    /// <param name="components">The components.</param>
    /// <returns>The field value.</returns>
    public static FieldValue OfInt64(params long[] components)
    {
        var bits = new ulong[components.Length];
        for (var i = 0; i < bits.Length; i++)
        {
            bits[i] = unchecked((ulong)components[i]);
        }

        return new FieldValue { Integers = bits };
    }
}

/// <summary>
/// Encodes and decodes one section of fields — a record body, an event or command payload — per 03-wire-protocol § 5 and decisions W11–W13: the leading bit
/// pack, then each byte-aligned field in wire order.
/// </summary>
public static class FieldCodec
{
    /// <summary>The largest number of components one numeric decode yields, a list's included.</summary>
    public const int MaxListComponents = ProtocolConstants.MaxListCount * 4;

    /// <summary>Decodes a section, handing each field's value to <paramref name="sink"/>.</summary>
    /// <typeparam name="TSink">The sink type; a struct keeps the call inlined.</typeparam>
    /// <param name="reader">The reader, positioned at the section's first byte.</param>
    /// <param name="section">The section.</param>
    /// <param name="frameTick">The tick of the frame being decoded, which <c>tickLo</c> rebuilds against.</param>
    /// <param name="sink">Receives the values.</param>
    /// <param name="frame">The session's realm frame, which a position field decodes over (SUB-30); <see langword="null"/> when it holds none.</param>
    public static void ReadSection<TSink>(ref WireReader reader, SectionPlan section, uint frameTick, ref TSink sink, RealmFrame frame = null)
        where TSink : IFieldSink, allows ref struct
    {
        Span<double> one = stackalloc double[ProtocolConstants.MaxCount];
        // A field is either numbers or 64-bit integers, and each sink call consumes its span before the next field: one buffer serves both, zeroed once.
        var wide = MemoryMarshal.Cast<double, ulong>(one);
        if (section.PackBytes > 0)
        {
            var pack = reader.ReadBytes(section.PackBytes);
            for (var i = 0; i < section.PackedCount; i++)
            {
                var f = section.Fields[i];
                one[0] = ReadPackedBits(pack, f.BitOffset, f.BitCount);
                sink.Number(f, one[..1]);
            }
        }

        for (var i = section.PackedCount; i < section.Fields.Length; i++)
        {
            var f = section.Fields[i];
            switch (f.ValueKind)
            {
                case FieldValueKind.Number:
                    ReadNumber(ref reader, f, frameTick, one, frame);
                    sink.Number(f, one[..f.Components]);
                    break;
                case FieldValueKind.Integer64:
                    ReadInteger64(ref reader, f, wide);
                    sink.Integer64(f, wide[..f.Components]);
                    break;
                case FieldValueKind.Text:
                    sink.Text(f, reader.ReadStrUtf8(f.Codec.MaxBytes));
                    break;
                case FieldValueKind.Bytes:
                    sink.Bytes(f, f.Kind == CodecKind.Bytes ? reader.ReadBytes(f.Codec.N) : reader.ReadBlob(f.Codec.MaxBytes));
                    break;
                case FieldValueKind.List:
                    ReadList(ref reader, f, frameTick, ref sink, frame);
                    break;
                case FieldValueKind.Collection:
                    ReadCollection(ref reader, f, frameTick, ref sink, frame);
                    break;
                case FieldValueKind.Skipped:
                    reader.Skip(f.Codec.FixedBytes);
                    break;
            }
        }
    }

    /// <summary>Encodes a section from <paramref name="values"/>, which is asked for each field in wire order.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="section">The section.</param>
    /// <param name="values">Supplies each field's value.</param>
    /// <param name="frame">The realm frame a position field is quantized over (SUB-30); <see langword="null"/> when there is none.</param>
    public static void WriteSection(ref WireWriter writer, SectionPlan section, Func<FieldPlan, FieldValue> values, RealmFrame frame = null)
    {
        if (section.PackBytes > 0)
        {
            Span<byte> pack = stackalloc byte[section.PackBytes];
            pack.Clear();
            for (var i = 0; i < section.PackedCount; i++)
            {
                var f = section.Fields[i];
                var v = Require(values(f), f).Numbers[0];
                WritePackedBits(pack, f.BitOffset, f.BitCount, PackedCode(f, v));
            }

            writer.WriteBytes(pack);
        }

        for (var i = section.PackedCount; i < section.Fields.Length; i++)
        {
            var f = section.Fields[i];
            var value = Require(values(f), f);
            switch (f.ValueKind)
            {
                case FieldValueKind.Number:
                    WriteNumber(ref writer, f, value.Numbers, frame);
                    break;
                case FieldValueKind.Integer64:
                    if (value.Integers != null)
                    {
                        WriteInteger64(ref writer, f, value.Integers);
                    }
                    else
                    {
                        WriteInteger64FromNumbers(ref writer, f, value.Numbers);
                    }

                    break;
                case FieldValueKind.Text:
                    writer.WriteStr(value.Text, f.Codec.MaxBytes);
                    break;
                case FieldValueKind.Bytes:
                    if (f.Kind == CodecKind.Bytes)
                    {
                        if (value.Bytes == null || value.Bytes.Length != f.Codec.N)
                        {
                            throw new ArgumentException($"field '{f.Name}' needs exactly {f.Codec.N} bytes");
                        }

                        writer.WriteBytes(value.Bytes);
                    }
                    else
                    {
                        writer.WriteBlob(value.Bytes ?? [], f.Codec.MaxBytes);
                    }

                    break;
                case FieldValueKind.List:
                    WriteList(ref writer, f, value.Numbers ?? [], frame);
                    break;
                case FieldValueKind.Collection:
                    WriteCollection(ref writer, f, value, frame);
                    break;
                case FieldValueKind.Skipped:
                    // A codec newer than this library: only its width is known, so the caller supplies the encoded bytes verbatim.
                    if (value.Bytes == null || value.Bytes.Length != f.Codec.FixedBytes)
                    {
                        throw new ArgumentException($"field '{f.Name}' has an unknown codec; supply exactly {f.Codec.FixedBytes} encoded bytes");
                    }

                    writer.WriteBytes(value.Bytes);
                    break;
            }
        }
    }

    /// <summary>The code a packed field (<c>bool</c>, <c>bits</c>) stores for <paramref name="value"/>.</summary>
    /// <param name="field">A packed field.</param>
    /// <param name="value">Its value.</param>
    /// <returns>The code, <see cref="FieldPlan.BitCount"/> bits wide.</returns>
    /// <exception cref="ArgumentException">A <c>bits</c> value that is not an integer in its range.</exception>
    public static uint PackedCode(FieldPlan field, double value) =>
        field.Kind == CodecKind.Bool ? (value != 0 ? 1u : 0u) : ToUnsignedInteger(value, (1UL << field.BitCount) - 1, field);

    /// <summary>Decodes one numeric value of <paramref name="field"/> into <paramref name="destination"/>.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="field">A byte-aligned numeric field (or list element, or position codec).</param>
    /// <param name="frameTick">The frame's tick, for <c>tickLo</c>.</param>
    /// <param name="destination">Receives <see cref="FieldPlan.Components"/> values.</param>
    /// <param name="frame">The realm frame a position decodes over; a position with none is a protocol error (1002).</param>
    public static void ReadNumber(ref WireReader reader, FieldPlan field, uint frameTick, scoped Span<double> destination, RealmFrame frame = null)
    {
        var c = field.Codec;
        switch (field.Kind)
        {
            case CodecKind.Pos2:
            case CodecKind.Pos3:
            {
                var f = frame ?? throw NoRealm(field);
                var bits = f.PositionBits;
                var min = f.Min;
                var step = f.Step;
                for (var i = 0; i < field.Components; i++)
                {
                    destination[i] = WireMath.DecodeQuantWithStep(reader.ReadUnsigned(bits), min[i], step[i]);
                }

                break;
            }
            case CodecKind.Vec2:
            case CodecKind.Vec3:
                for (var i = 0; i < field.Components; i++)
                {
                    destination[i] = WireMath.DecodeVec(reader.ReadSigned(c.Bits), c.Scale, c.Bits);
                }

                break;
            case CodecKind.Vel2:
            case CodecKind.Vel3:
                for (var i = 0; i < field.Components; i++)
                {
                    destination[i] = WireMath.DecodeVel(reader.ReadSigned(c.Bits), field.VelocityUnitExp, c.Bits);
                }

                break;
            case CodecKind.Quat3:
                WireMath.DecodeQuat3(reader.ReadU32(), destination);
                break;
            default:
                // A scalar codec: Components is its count (W33), each value written in turn.
                for (var i = 0; i < field.Components; i++)
                {
                    destination[i] = ReadScalar(ref reader, field, frameTick);
                }

                break;
        }
    }

    /// <summary>Decodes one 64-bit integer field (W32) into <paramref name="destination"/>, as bit patterns.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="field">A <c>u64</c>, <c>i64</c>, <c>varu64</c> or <c>vari64</c> field.</param>
    /// <param name="destination">Receives <see cref="FieldPlan.Components"/> values.</param>
    public static void ReadInteger64(ref WireReader reader, FieldPlan field, scoped Span<ulong> destination)
    {
        for (var i = 0; i < field.Components; i++)
        {
            destination[i] = field.Kind switch
            {
                CodecKind.U64 => reader.ReadU64(),
                CodecKind.I64 => unchecked((ulong)reader.ReadI64()),
                CodecKind.Varu64 => reader.ReadVaru64(),
                CodecKind.Vari64 => unchecked((ulong)reader.ReadVari64()),
                _ => throw new InvalidOperationException($"'{CodecTokens.ToToken(field.Kind)}' is not a 64-bit integer codec"),
            };
        }
    }

    /// <summary>Encodes one 64-bit integer field (W32) from bit patterns.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="field">A <c>u64</c>, <c>i64</c>, <c>varu64</c> or <c>vari64</c> field.</param>
    /// <param name="components">At least <see cref="FieldPlan.Components"/> values; a signed value as its two's complement.</param>
    public static void WriteInteger64(ref WireWriter writer, FieldPlan field, scoped ReadOnlySpan<ulong> components)
    {
        if (components.Length < field.Components)
        {
            throw new ArgumentException($"field '{field.Name}' needs {field.Components} component(s), got {components.Length}");
        }

        for (var i = 0; i < field.Components; i++)
        {
            var v = components[i];
            switch (field.Kind)
            {
                case CodecKind.U64:
                case CodecKind.I64:
                    writer.WriteU64(v);
                    break;
                case CodecKind.Varu64:
                    writer.WriteVaru64(v);
                    break;
                case CodecKind.Vari64:
                    writer.WriteVari64(unchecked((long)v));
                    break;
                default:
                    throw new InvalidOperationException($"'{CodecTokens.ToToken(field.Kind)}' is not a 64-bit integer codec");
            }
        }
    }

    // A 64-bit field handed doubles: each must be an integer in the codec's range, which a double holds exactly only up to 2^53 — beyond that the caller
    // has already lost the value, and refusing a non-integer is the most this can check.
    private static void WriteInteger64FromNumbers(ref WireWriter writer, FieldPlan field, ReadOnlySpan<double> numbers)
    {
        Span<ulong> bits = stackalloc ulong[ProtocolConstants.MaxCount];
        var signed = field.Kind is CodecKind.I64 or CodecKind.Vari64;
        for (var i = 0; i < field.Components; i++)
        {
            var v = numbers[i];
            var inRange = signed ? v >= -9223372036854775808d && v < 9223372036854775808d : v >= 0 && v < 18446744073709551616d;
            if (!inRange || Math.Floor(v) != v)
            {
                throw new ArgumentException($"field '{field.Name}': {v.ToString(CultureInfo.InvariantCulture)} is not a 64-bit integer of its codec");
            }

            bits[i] = signed ? unchecked((ulong)(long)v) : (ulong)v;
        }

        WriteInteger64(ref writer, field, bits[..field.Components]);
    }

    // One value of a scalar codec, the unit a count repeats.
    private static double ReadScalar(ref WireReader reader, FieldPlan field, uint frameTick)
    {
        var c = field.Codec;
        return field.Kind switch
        {
            CodecKind.U8 => reader.ReadU8(),
            CodecKind.I8 => reader.ReadI8(),
            CodecKind.U16 => reader.ReadU16(),
            CodecKind.I16 => reader.ReadI16(),
            CodecKind.U32 => reader.ReadU32(),
            CodecKind.I32 => reader.ReadI32(),
            CodecKind.Varu or CodecKind.EntityRef => reader.ReadVaru(),
            CodecKind.Vari => reader.ReadVari(),
            CodecKind.F32 => reader.ReadF32(),
            CodecKind.F16 => reader.ReadF16(),
            CodecKind.F64 => reader.ReadF64(),
            CodecKind.Quant => WireMath.DecodeQuantWithStep(reader.ReadUnsigned(c.Bits), c.Min[0], field.QuantStep[0]),
            CodecKind.Unorm => WireMath.DecodeUnorm(reader.ReadUnsigned(c.Bits), c.Bits),
            CodecKind.Snorm => WireMath.DecodeSnorm(reader.ReadSigned(c.Bits), c.Bits),
            CodecKind.Angle => WireMath.DecodeAngle(reader.ReadSigned(c.Bits), c.Bits),
            CodecKind.TickLo => WireMath.DecodeTickLo(reader.ReadU16(), frameTick),
            _ => throw new InvalidOperationException($"'{CodecTokens.ToToken(field.Kind)}' is not a byte-aligned numeric codec"),
        };
    }

    // One value of a scalar codec, encoded.
    private static void WriteScalar(ref WireWriter writer, FieldPlan field, double v)
    {
        var c = field.Codec;
        switch (field.Kind)
        {
            case CodecKind.U8:
                writer.WriteU8((byte)ToUnsignedInteger(v, byte.MaxValue, field));
                break;
            case CodecKind.I8:
                writer.WriteI8((sbyte)ToSignedInteger(v, sbyte.MinValue, sbyte.MaxValue, field));
                break;
            case CodecKind.U16:
                writer.WriteU16((ushort)ToUnsignedInteger(v, ushort.MaxValue, field));
                break;
            case CodecKind.I16:
                writer.WriteI16((short)ToSignedInteger(v, short.MinValue, short.MaxValue, field));
                break;
            case CodecKind.U32:
                writer.WriteU32(ToUnsignedInteger(v, uint.MaxValue, field));
                break;
            case CodecKind.I32:
                writer.WriteI32(ToSignedInteger(v, int.MinValue, int.MaxValue, field));
                break;
            case CodecKind.Varu:
            case CodecKind.EntityRef:
                writer.WriteVaru(ToUnsignedInteger(v, uint.MaxValue, field));
                break;
            case CodecKind.Vari:
                writer.WriteVari(ToSignedInteger(v, int.MinValue, int.MaxValue, field));
                break;
            case CodecKind.F32:
                writer.WriteF32(v);
                break;
            case CodecKind.F16:
                writer.WriteF16(v);
                break;
            case CodecKind.F64:
                writer.WriteF64(v);
                break;
            case CodecKind.Quant:
                writer.WriteBits(WireMath.EncodeQuant(v, c.Min[0], c.Max[0], c.Bits), c.Bits);
                break;
            case CodecKind.Unorm:
                writer.WriteBits(WireMath.EncodeUnorm(v, c.Bits), c.Bits);
                break;
            case CodecKind.Snorm:
                writer.WriteBits((uint)WireMath.EncodeSnorm(v, c.Bits), c.Bits);
                break;
            case CodecKind.Angle:
                writer.WriteBits((uint)WireMath.EncodeAngle(v, c.Bits), c.Bits);
                break;
            case CodecKind.TickLo:
                writer.WriteU16((ushort)(ToUnsignedInteger(v, uint.MaxValue, field) & 0xFFFF));
                break;
            default:
                throw new InvalidOperationException($"'{CodecTokens.ToToken(field.Kind)}' is not a byte-aligned numeric codec");
        }
    }

    /// <summary>Encodes one numeric value of <paramref name="field"/>.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="field">A byte-aligned numeric field (or list element, or position codec).</param>
    /// <param name="components">At least <see cref="FieldPlan.Components"/> values.</param>
    /// <param name="frame">The realm frame a position is quantized over; a position with none is refused.</param>
    public static void WriteNumber(ref WireWriter writer, FieldPlan field, scoped ReadOnlySpan<double> components, RealmFrame frame = null)
    {
        if (components.Length < field.Components)
        {
            throw new ArgumentException($"field '{field.Name}' needs {field.Components} component(s), got {components.Length}");
        }

        var c = field.Codec;
        switch (field.Kind)
        {
            case CodecKind.Pos2:
            case CodecKind.Pos3:
            {
                if (frame == null)
                {
                    throw new InvalidOperationException($"position '{field.Name}' is realm-framed (typhon.3) and no realm frame was given to encode it over");
                }

                var bits = frame.PositionBits;
                var min = frame.Min;
                var max = frame.Max;
                for (var i = 0; i < field.Components; i++)
                {
                    writer.WriteBits(WireMath.EncodeQuant(components[i], min[i], max[i], bits), bits);
                }

                break;
            }
            case CodecKind.Vec2:
            case CodecKind.Vec3:
                for (var i = 0; i < field.Components; i++)
                {
                    writer.WriteBits((uint)WireMath.EncodeVec(components[i], c.Scale, c.Bits), c.Bits);
                }

                break;
            case CodecKind.Vel2:
            case CodecKind.Vel3:
                for (var i = 0; i < field.Components; i++)
                {
                    writer.WriteBits((uint)WireMath.EncodeVel(components[i], field.VelocityUnitExp, c.Bits), c.Bits);
                }

                break;
            case CodecKind.Quat3:
                writer.WriteU32(WireMath.EncodeQuat3(components[0], components[1], components[2], components[3]));
                break;
            default:
                // A scalar codec: Components is its count (W33), each value written in turn.
                for (var i = 0; i < field.Components; i++)
                {
                    WriteScalar(ref writer, field, components[i]);
                }

                break;
        }
    }

    /// <summary>Extracts <paramref name="count"/> bits starting at <paramref name="offset"/> from a pack, least significant bit first (W12).</summary>
    /// <param name="pack">The pack bytes.</param>
    /// <param name="offset">The first bit.</param>
    /// <param name="count">The width, 1 to 24.</param>
    /// <returns>The unsigned value.</returns>
    public static uint ReadPackedBits(ReadOnlySpan<byte> pack, int offset, int count)
    {
        var byteIndex = offset >> 3;
        uint window = 0;
        for (var i = 0; i < 4 && byteIndex + i < pack.Length; i++)
        {
            window |= (uint)pack[byteIndex + i] << (8 * i);
        }

        return (window >> (offset & 7)) & (uint)((1UL << count) - 1);
    }

    /// <summary>Stores <paramref name="count"/> bits of <paramref name="value"/> at <paramref name="offset"/> in a pack, least significant bit first.</summary>
    /// <param name="pack">The pack bytes.</param>
    /// <param name="offset">The first bit.</param>
    /// <param name="count">The width, 1 to 24.</param>
    /// <param name="value">The value; bits above <paramref name="count"/> must be zero.</param>
    public static void WritePackedBits(Span<byte> pack, int offset, int count, uint value)
    {
        for (var i = 0; i < count; i++)
        {
            if (((value >> i) & 1) != 0)
            {
                var bit = offset + i;
                pack[bit >> 3] |= (byte)(1 << (bit & 7));
            }
        }
    }

    private static void ReadList<TSink>(ref WireReader reader, FieldPlan field, uint frameTick, ref TSink sink, RealmFrame frame)
        where TSink : IFieldSink, allows ref struct
    {
        var raw = reader.ReadVaru();
        if (raw > (uint)field.Codec.MaxCount)
        {
            throw WireFormatException.Malformed($"list '{field.Name}' has {raw} element(s); at most {field.Codec.MaxCount} allowed");
        }

        var count = (int)raw;
        if (count < field.Codec.MinCount)
        {
            throw WireFormatException.Malformed($"list '{field.Name}' has {count} element(s); at least {field.Codec.MinCount} required");
        }

        var stride = field.Components;
        Span<double> values = stackalloc double[count * stride];
        for (var e = 0; e < count; e++)
        {
            ReadNumber(ref reader, field.Element, frameTick, values.Slice(e * stride, stride), frame);
        }

        sink.List(field, count, values);
    }

    private static void WriteList(ref WireWriter writer, FieldPlan field, ReadOnlySpan<double> flattened, RealmFrame frame)
    {
        var stride = field.Components;
        if (stride == 0 || flattened.Length % stride != 0)
        {
            throw new ArgumentException($"list '{field.Name}' needs a multiple of {stride} numbers");
        }

        var count = flattened.Length / stride;
        if (count < field.Codec.MinCount || count > field.Codec.MaxCount)
        {
            throw new ArgumentException($"list '{field.Name}' has {count} element(s); {field.Codec.MinCount}..{field.Codec.MaxCount} allowed");
        }

        writer.WriteVaru((uint)count);
        for (var e = 0; e < count; e++)
        {
            WriteNumber(ref writer, field.Element, flattened.Slice(e * stride, stride), frame);
        }
    }

    private static void ReadCollection<TSink>(ref WireReader reader, FieldPlan field, uint frameTick, ref TSink sink, RealmFrame frame)
        where TSink : IFieldSink, allows ref struct
    {
        var total = reader.ReadVaru();
        var sent = reader.ReadVaru();
        if (sent > total || sent > (uint)field.Codec.MaxCount)
        {
            throw WireFormatException.Malformed(
                $"coll '{field.Name}' sends {sent} of {total} element(s); at most {field.Codec.MaxCount}, and never more than its total");
        }

        // Every element is at least one byte: a count the message cannot hold is refused before a store sizes for it.
        if (sent > (uint)reader.Remaining)
        {
            throw WireFormatException.Malformed($"coll '{field.Name}' sends {sent} element(s) with {reader.Remaining} byte(s) left");
        }

        sink.Collection(field, (int)Math.Min(total, int.MaxValue), (int)sent);
        for (var e = 0; e < (int)sent; e++)
        {
            sink.CollectionElement(field, e);
            ReadSection(ref reader, field.ElementSection, frameTick, ref sink, frame);
        }
    }

    /// <summary>
    /// Writes a collection's header, <c>varu total | varu sent</c> (W34); its elements follow, each written with <see cref="WriteSection"/> over
    /// <see cref="FieldPlan.ElementSection"/>.
    /// </summary>
    /// <param name="writer">The writer.</param>
    /// <param name="field">The collection field.</param>
    /// <param name="total">How many elements the entity holds.</param>
    /// <param name="sent">How many follow: at most <paramref name="total"/> and the codec's <c>maxCount</c>.</param>
    public static void WriteCollectionHeader(ref WireWriter writer, FieldPlan field, int total, int sent)
    {
        if (sent < 0 || sent > total || sent > field.Codec.MaxCount)
        {
            throw new ArgumentException($"coll '{field.Name}' cannot send {sent} of {total} element(s); at most {field.Codec.MaxCount}");
        }

        writer.WriteVaru((uint)total);
        writer.WriteVaru((uint)sent);
    }

    private static void WriteCollection(ref WireWriter writer, FieldPlan field, FieldValue value, RealmFrame frame)
    {
        var elements = value.Elements ?? [];
        WriteCollectionHeader(ref writer, field, Math.Max(value.Total, elements.Length), elements.Length);
        foreach (var element in elements)
        {
            WriteSection(ref writer, field.ElementSection, f => (uint)f.Ordinal < (uint)element.Length ? element[f.Ordinal] : null, frame);
        }
    }

    // A position decoded while the session holds no realm: the server sent a positioned value before any REALM (12-realms § 5.2), a protocol error.
    private static WireFormatException NoRealm(FieldPlan field) =>
        WireFormatException.Protocol($"position '{field.Name}' arrived while the session holds no realm");

    private static FieldValue Require(FieldValue value, FieldPlan field)
    {
        if (value == null)
        {
            throw new ArgumentException($"no value supplied for field '{field.Name}'");
        }

        if (field.ValueKind is FieldValueKind.Number && (value.Numbers == null || value.Numbers.Length < Math.Max(1, field.Components)))
        {
            throw new ArgumentException($"field '{field.Name}' needs {Math.Max(1, field.Components)} number(s)");
        }

        if (field.ValueKind is FieldValueKind.Integer64 && (value.Integers?.Length ?? value.Numbers?.Length ?? 0) < field.Components)
        {
            throw new ArgumentException($"field '{field.Name}' needs {field.Components} 64-bit integer(s)");
        }

        return value;
    }

    private static uint ToUnsignedInteger(double v, ulong max, FieldPlan field)
    {
        if (!(v >= 0) || v > max || Math.Floor(v) != v)
        {
            throw new ArgumentException($"field '{field.Name}': {v.ToString(CultureInfo.InvariantCulture)} is not an integer in [0, {max}]");
        }

        return (uint)v;
    }

    private static int ToSignedInteger(double v, long min, long max, FieldPlan field)
    {
        if (!(v >= min) || v > max || Math.Floor(v) != v)
        {
            throw new ArgumentException($"field '{field.Name}': {v.ToString(CultureInfo.InvariantCulture)} is not an integer in [{min}, {max}]");
        }

        return (int)v;
    }

    // Kept for decoders that surface text as a string rather than bytes.
    internal static string Utf8ToString(ReadOnlySpan<byte> utf8) => Encoding.UTF8.GetString(utf8);
}
