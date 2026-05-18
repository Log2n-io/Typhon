using System;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Typhon.Schema.Definition;

namespace Typhon.Workbench.Storage.Decoders;

/// <summary>
/// Formats one component field's raw bytes into a human-readable value for the Database File Map's L4 content
/// decode (Module 15, A2). The file is little-endian; scalars, points and inline strings decode exactly, every
/// other type falls back to a hex preview — never wrong, refined incrementally (design §10 "decoders are
/// additive").
/// </summary>
internal static class StorageFieldFormatter
{
    /// <summary>Decodes <paramref name="bytes"/> as a value of <paramref name="type"/>. Returns a display string.</summary>
    public static string Format(FieldType type, ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return "—";
        }

        switch (type)
        {
            case FieldType.Boolean:
                return bytes[0] != 0 ? "true" : "false";
            case FieldType.Byte:
                return ((sbyte)bytes[0]).ToString(CultureInfo.InvariantCulture);
            case FieldType.UByte:
                return bytes[0].ToString(CultureInfo.InvariantCulture);
            case FieldType.Char:
                return bytes.Length >= 2 ? ((char)BinaryPrimitives.ReadUInt16LittleEndian(bytes)).ToString() : Hex(bytes);
            case FieldType.Short:
                return bytes.Length >= 2 ? BinaryPrimitives.ReadInt16LittleEndian(bytes).ToString(CultureInfo.InvariantCulture) : Hex(bytes);
            case FieldType.UShort:
                return bytes.Length >= 2 ? BinaryPrimitives.ReadUInt16LittleEndian(bytes).ToString(CultureInfo.InvariantCulture) : Hex(bytes);
            case FieldType.Int:
                return bytes.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(bytes).ToString(CultureInfo.InvariantCulture) : Hex(bytes);
            case FieldType.UInt:
                return bytes.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(bytes).ToString(CultureInfo.InvariantCulture) : Hex(bytes);
            case FieldType.Long:
                return bytes.Length >= 8 ? BinaryPrimitives.ReadInt64LittleEndian(bytes).ToString(CultureInfo.InvariantCulture) : Hex(bytes);
            case FieldType.ULong:
                return bytes.Length >= 8 ? BinaryPrimitives.ReadUInt64LittleEndian(bytes).ToString(CultureInfo.InvariantCulture) : Hex(bytes);
            case FieldType.Float:
                return bytes.Length >= 4 ? Round(BinaryPrimitives.ReadSingleLittleEndian(bytes)) : Hex(bytes);
            case FieldType.Double:
                return bytes.Length >= 8 ? Round(BinaryPrimitives.ReadDoubleLittleEndian(bytes)) : Hex(bytes);
            case FieldType.String64:
            case FieldType.String1024:
            case FieldType.String:
            case FieldType.Variant:
                return InlineString(bytes);
            case FieldType.Point2F:
                return Floats(bytes, 2);
            case FieldType.Point3F:
                return Floats(bytes, 3);
            case FieldType.Point4F:
            case FieldType.QuaternionF:
                return Floats(bytes, 4);
            case FieldType.Point2D:
                return Doubles(bytes, 2);
            case FieldType.Point3D:
                return Doubles(bytes, 3);
            case FieldType.Point4D:
            case FieldType.QuaternionD:
                return Doubles(bytes, 4);
            case FieldType.Collection:
                return bytes.Length >= 4 ? $"→ chunk {BinaryPrimitives.ReadInt32LittleEndian(bytes)}" : Hex(bytes);
            case FieldType.Component:
                return bytes.Length >= 8 ? $"→ entity {BinaryPrimitives.ReadInt64LittleEndian(bytes)}" : Hex(bytes);
            default:
                return Hex(bytes);
        }
    }

    /// <summary>UTF-8 of an inline fixed string, trimmed at the first NUL; non-printable content falls back to hex.</summary>
    private static string InlineString(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0);
        var content = end >= 0 ? bytes[..end] : bytes;
        if (content.IsEmpty)
        {
            return "\"\"";
        }
        foreach (var b in content)
        {
            if (b < 0x20 && b != (byte)'\t' && b != (byte)'\n' && b != (byte)'\r')
            {
                return Hex(bytes.Length > 24 ? bytes[..24] : bytes);
            }
        }
        return $"\"{Encoding.UTF8.GetString(content)}\"";
    }

    private static string Floats(ReadOnlySpan<byte> bytes, int count)
    {
        if (bytes.Length < count * 4)
        {
            return Hex(bytes);
        }
        var sb = new StringBuilder("(");
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }
            sb.Append(Round(BinaryPrimitives.ReadSingleLittleEndian(bytes[(i * 4)..])));
        }
        return sb.Append(')').ToString();
    }

    private static string Doubles(ReadOnlySpan<byte> bytes, int count)
    {
        if (bytes.Length < count * 8)
        {
            return Hex(bytes);
        }
        var sb = new StringBuilder("(");
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }
            sb.Append(Round(BinaryPrimitives.ReadDoubleLittleEndian(bytes[(i * 8)..])));
        }
        return sb.Append(')').ToString();
    }

    private static string Round(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    private static string Hex(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder("0x");
        var n = Math.Min(bytes.Length, 16);
        for (var i = 0; i < n; i++)
        {
            sb.Append(bytes[i].ToString("X2", CultureInfo.InvariantCulture));
        }
        if (bytes.Length > n)
        {
            sb.Append('…');
        }
        return sb.ToString();
    }
}
