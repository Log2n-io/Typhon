using System;
using System.Globalization;
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
}

/// <summary>
/// A value to encode. Numbers carry the components of a numeric field, or the flattened elements of a list; <see cref="Text"/> and <see cref="Bytes"/> carry
/// the rest.
/// </summary>
public sealed class FieldValue
{
    /// <summary>Numeric components, or a list's flattened element components.</summary>
    public double[] Numbers { get; init; }

    /// <summary>Text, for a <c>str</c> field.</summary>
    public string Text { get; init; }

    /// <summary>Bytes, for a <c>bytes</c> or <c>blob</c> field.</summary>
    public byte[] Bytes { get; init; }

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
    public static void ReadSection<TSink>(ref WireReader reader, SectionPlan section, uint frameTick, ref TSink sink)
        where TSink : IFieldSink, allows ref struct
    {
        Span<double> one = stackalloc double[4];
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
                    ReadNumber(ref reader, f, frameTick, one);
                    sink.Number(f, one[..f.Components]);
                    break;
                case FieldValueKind.Text:
                    sink.Text(f, reader.ReadStrUtf8(f.Codec.MaxBytes));
                    break;
                case FieldValueKind.Bytes:
                    sink.Bytes(f, f.Kind == CodecKind.Bytes ? reader.ReadBytes(f.Codec.N) : reader.ReadBlob(f.Codec.MaxBytes));
                    break;
                case FieldValueKind.List:
                    ReadList(ref reader, f, frameTick, ref sink);
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
    public static void WriteSection(ref WireWriter writer, SectionPlan section, Func<FieldPlan, FieldValue> values)
    {
        if (section.PackBytes > 0)
        {
            Span<byte> pack = stackalloc byte[section.PackBytes];
            pack.Clear();
            for (var i = 0; i < section.PackedCount; i++)
            {
                var f = section.Fields[i];
                var v = Require(values(f), f).Numbers[0];
                uint code;
                if (f.Kind == CodecKind.Bool)
                {
                    code = v != 0 ? 1u : 0u;
                }
                else
                {
                    code = ToUnsignedInteger(v, (1UL << f.BitCount) - 1, f);
                }

                WritePackedBits(pack, f.BitOffset, f.BitCount, code);
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
                    WriteNumber(ref writer, f, value.Numbers);
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
                    WriteList(ref writer, f, value.Numbers ?? []);
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

    /// <summary>Decodes one numeric value of <paramref name="field"/> into <paramref name="destination"/>.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="field">A byte-aligned numeric field (or list element, or position codec).</param>
    /// <param name="frameTick">The frame's tick, for <c>tickLo</c>.</param>
    /// <param name="destination">Receives <see cref="FieldPlan.Components"/> values.</param>
    public static void ReadNumber(ref WireReader reader, FieldPlan field, uint frameTick, scoped Span<double> destination)
    {
        var c = field.Codec;
        switch (field.Kind)
        {
            case CodecKind.U8:
                destination[0] = reader.ReadU8();
                break;
            case CodecKind.I8:
                destination[0] = reader.ReadI8();
                break;
            case CodecKind.U16:
                destination[0] = reader.ReadU16();
                break;
            case CodecKind.I16:
                destination[0] = reader.ReadI16();
                break;
            case CodecKind.U32:
                destination[0] = reader.ReadU32();
                break;
            case CodecKind.I32:
                destination[0] = reader.ReadI32();
                break;
            case CodecKind.Varu:
            case CodecKind.EntityRef:
                destination[0] = reader.ReadVaru();
                break;
            case CodecKind.Vari:
                destination[0] = reader.ReadVari();
                break;
            case CodecKind.F32:
                destination[0] = reader.ReadF32();
                break;
            case CodecKind.F16:
                destination[0] = reader.ReadF16();
                break;
            case CodecKind.Quant:
                destination[0] = WireMath.DecodeQuantWithStep(reader.ReadUnsigned(c.Bits), c.Min[0], field.QuantStep[0]);
                break;
            case CodecKind.Pos2:
            case CodecKind.Pos3:
                for (var i = 0; i < field.Components; i++)
                {
                    destination[i] = WireMath.DecodeQuantWithStep(reader.ReadUnsigned(c.Bits), c.Min[i], field.QuantStep[i]);
                }

                break;
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
                    destination[i] = WireMath.DecodeVel(reader.ReadSigned(c.Bits), field.VelocityPositionStep[i], c.QuantaDiv, c.Bits);
                }

                break;
            case CodecKind.Unorm:
                destination[0] = WireMath.DecodeUnorm(reader.ReadUnsigned(c.Bits), c.Bits);
                break;
            case CodecKind.Snorm:
                destination[0] = WireMath.DecodeSnorm(reader.ReadSigned(c.Bits), c.Bits);
                break;
            case CodecKind.Angle:
                destination[0] = WireMath.DecodeAngle(reader.ReadSigned(c.Bits), c.Bits);
                break;
            case CodecKind.Quat3:
                WireMath.DecodeQuat3(reader.ReadU32(), destination);
                break;
            case CodecKind.TickLo:
                destination[0] = WireMath.DecodeTickLo(reader.ReadU16(), frameTick);
                break;
            default:
                throw new InvalidOperationException($"'{CodecTokens.ToToken(field.Kind)}' is not a byte-aligned numeric codec");
        }
    }

    /// <summary>Encodes one numeric value of <paramref name="field"/>.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="field">A byte-aligned numeric field (or list element, or position codec).</param>
    /// <param name="components">At least <see cref="FieldPlan.Components"/> values.</param>
    public static void WriteNumber(ref WireWriter writer, FieldPlan field, scoped ReadOnlySpan<double> components)
    {
        if (components.Length < field.Components)
        {
            throw new ArgumentException($"field '{field.Name}' needs {field.Components} component(s), got {components.Length}");
        }

        var c = field.Codec;
        var v = components[0];
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
            case CodecKind.Quant:
                writer.WriteBits(WireMath.EncodeQuant(v, c.Min[0], c.Max[0], c.Bits), c.Bits);
                break;
            case CodecKind.Pos2:
            case CodecKind.Pos3:
                for (var i = 0; i < field.Components; i++)
                {
                    writer.WriteBits(WireMath.EncodeQuant(components[i], c.Min[i], c.Max[i], c.Bits), c.Bits);
                }

                break;
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
                    writer.WriteBits((uint)WireMath.EncodeVel(components[i], field.VelocityPositionStep[i], c.QuantaDiv, c.Bits), c.Bits);
                }

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
            case CodecKind.Quat3:
                writer.WriteU32(WireMath.EncodeQuat3(components[0], components[1], components[2], components[3]));
                break;
            case CodecKind.TickLo:
                writer.WriteU16((ushort)(ToUnsignedInteger(v, uint.MaxValue, field) & 0xFFFF));
                break;
            default:
                throw new InvalidOperationException($"'{CodecTokens.ToToken(field.Kind)}' is not a byte-aligned numeric codec");
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

    private static void ReadList<TSink>(ref WireReader reader, FieldPlan field, uint frameTick, ref TSink sink)
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
            ReadNumber(ref reader, field.Element, frameTick, values.Slice(e * stride, stride));
        }

        sink.List(field, count, values);
    }

    private static void WriteList(ref WireWriter writer, FieldPlan field, ReadOnlySpan<double> flattened)
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
            WriteNumber(ref writer, field.Element, flattened.Slice(e * stride, stride));
        }
    }

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
