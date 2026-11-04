using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Typhon.Schema.Definition;

namespace Typhon.Shell.Schema;

/// <summary>
/// Converts text field values to binary and writes them into an unmanaged buffer at the correct offsets.
/// Also reads field values from a struct buffer for display.
/// </summary>
internal static class TextToStructConverter
{
    /// <summary>
    /// Writes field assignments into a pre-allocated buffer.
    /// </summary>
    public static unsafe void WriteFields(
        byte* buffer,
        int bufferSize,
        ComponentSchema schema,
        IReadOnlyDictionary<string, string> fieldValues)
    {
        foreach (var kvp in fieldValues)
        {
            if (!schema.FieldsByName.TryGetValue(kvp.Key, out var field))
            {
                throw new ArgumentException($"Unknown field '{kvp.Key}' in component {schema.Name}. Known fields: {string.Join(", ", schema.FieldsByName.Keys)}");
            }

            if (field.Offset + field.Size > bufferSize)
            {
                throw new InvalidOperationException($"Field '{field.Name}' extends beyond buffer (offset={field.Offset}, size={field.Size}, buffer={bufferSize}).");
            }

            WriteField(buffer + field.Offset, field, kvp.Value);
        }
    }

    /// <summary>
    /// Reads all field values from a struct buffer for display.
    /// </summary>
    /// <param name="buffer">The component's value bytes.</param>
    /// <param name="bufferSize">Their size.</param>
    /// <param name="schema">The component's schema.</param>
    /// <param name="readText">Resolves a <c>String1024</c> handle to its text (#1094); null shows the handle.</param>
    public static unsafe Dictionary<string, object> ReadFields(byte* buffer, int bufferSize, ComponentSchema schema, Func<int, string> readText = null)
    {
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        foreach (var field in schema.Fields)
        {
            if (field.Offset + field.Size > bufferSize)
            {
                continue;
            }

            result[field.Name] = field.Type == FieldType.String1024
                ? ReadText(*(int*)(buffer + field.Offset), readText)
                : ReadField(buffer + field.Offset, field);
        }

        return result;
    }

    private static unsafe void WriteField(byte* ptr, ComponentSchema.FieldInfo field, string text)
    {
        // Strip suffixes for parsing
        var cleanText = StripNumericSuffix(text);

        switch (field.Type)
        {
            case FieldType.Boolean:
                *(bool*)ptr = bool.Parse(cleanText);
                break;
            case FieldType.Byte:
                *(sbyte*)ptr = sbyte.Parse(cleanText, CultureInfo.InvariantCulture);
                break;
            case FieldType.UByte:
                *ptr = byte.Parse(cleanText, CultureInfo.InvariantCulture);
                break;
            case FieldType.Char:
                *(char*)ptr = cleanText.Length > 0 ? cleanText[0] : '\0';
                break;
            case FieldType.Short:
                *(short*)ptr = short.Parse(cleanText, CultureInfo.InvariantCulture);
                break;
            case FieldType.UShort:
                *(ushort*)ptr = ushort.Parse(cleanText, CultureInfo.InvariantCulture);
                break;
            case FieldType.Int:
                *(int*)ptr = int.Parse(cleanText, CultureInfo.InvariantCulture);
                break;
            case FieldType.UInt:
                *(uint*)ptr = uint.Parse(cleanText, CultureInfo.InvariantCulture);
                break;
            case FieldType.Float:
                *(float*)ptr = float.Parse(cleanText, CultureInfo.InvariantCulture);
                break;
            case FieldType.Double:
                *(double*)ptr = double.Parse(cleanText, CultureInfo.InvariantCulture);
                break;
            case FieldType.Long:
                *(long*)ptr = long.Parse(cleanText, CultureInfo.InvariantCulture);
                break;
            case FieldType.ULong:
                *(ulong*)ptr = ulong.Parse(cleanText, CultureInfo.InvariantCulture);
                break;
            case FieldType.String64:
                String64 s64 = cleanText; // implicit operator String64(string)
                *(String64*)ptr = s64;
                break;
            case FieldType.String1024:
                // A handle to an out-of-line text (#1094): its text is stored through the entity, never written into the value's bytes.
                throw new NotSupportedException($"Field '{field.Name}' is a String1024, whose text is stored out of line: the shell cannot set it. Set it "
                    + "from code with EntityRefMut.SetText.");
            case FieldType.Point2F:
            {
                var c = ParseTupleComponents(cleanText, 2, field.Name);
                *(Point2F*)ptr = new Point2F
                {
                    X = float.Parse(c[0], CultureInfo.InvariantCulture),
                    Y = float.Parse(c[1], CultureInfo.InvariantCulture)
                };
                break;
            }
            case FieldType.Point3F:
            {
                var c = ParseTupleComponents(cleanText, 3, field.Name);
                *(Point3F*)ptr = new Point3F
                {
                    X = float.Parse(c[0], CultureInfo.InvariantCulture),
                    Y = float.Parse(c[1], CultureInfo.InvariantCulture),
                    Z = float.Parse(c[2], CultureInfo.InvariantCulture)
                };
                break;
            }
            case FieldType.Point4F:
            {
                var c = ParseTupleComponents(cleanText, 4, field.Name);
                *(Point4F*)ptr = new Point4F
                {
                    X = float.Parse(c[0], CultureInfo.InvariantCulture),
                    Y = float.Parse(c[1], CultureInfo.InvariantCulture),
                    Z = float.Parse(c[2], CultureInfo.InvariantCulture),
                    W = float.Parse(c[3], CultureInfo.InvariantCulture)
                };
                break;
            }
            case FieldType.Point2D:
            {
                var c = ParseTupleComponents(cleanText, 2, field.Name);
                *(Point2D*)ptr = new Point2D
                {
                    X = double.Parse(c[0], CultureInfo.InvariantCulture),
                    Y = double.Parse(c[1], CultureInfo.InvariantCulture)
                };
                break;
            }
            case FieldType.Point3D:
            {
                var c = ParseTupleComponents(cleanText, 3, field.Name);
                *(Point3D*)ptr = new Point3D
                {
                    X = double.Parse(c[0], CultureInfo.InvariantCulture),
                    Y = double.Parse(c[1], CultureInfo.InvariantCulture),
                    Z = double.Parse(c[2], CultureInfo.InvariantCulture)
                };
                break;
            }
            case FieldType.Point4D:
            {
                var c = ParseTupleComponents(cleanText, 4, field.Name);
                *(Point4D*)ptr = new Point4D
                {
                    X = double.Parse(c[0], CultureInfo.InvariantCulture),
                    Y = double.Parse(c[1], CultureInfo.InvariantCulture),
                    Z = double.Parse(c[2], CultureInfo.InvariantCulture),
                    W = double.Parse(c[3], CultureInfo.InvariantCulture)
                };
                break;
            }
            case FieldType.QuaternionF:
            {
                var c = ParseTupleComponents(cleanText, 4, field.Name);
                *(QuaternionF*)ptr = new QuaternionF
                {
                    X = float.Parse(c[0], CultureInfo.InvariantCulture),
                    Y = float.Parse(c[1], CultureInfo.InvariantCulture),
                    Z = float.Parse(c[2], CultureInfo.InvariantCulture),
                    W = float.Parse(c[3], CultureInfo.InvariantCulture)
                };
                break;
            }
            case FieldType.QuaternionD:
            {
                var c = ParseTupleComponents(cleanText, 4, field.Name);
                *(QuaternionD*)ptr = new QuaternionD
                {
                    X = double.Parse(c[0], CultureInfo.InvariantCulture),
                    Y = double.Parse(c[1], CultureInfo.InvariantCulture),
                    Z = double.Parse(c[2], CultureInfo.InvariantCulture),
                    W = double.Parse(c[3], CultureInfo.InvariantCulture)
                };
                break;
            }
            case FieldType.Variant:
                // Store as string variant
                *(Variant*)ptr = new Variant(cleanText, true);
                break;
            default:
                throw new NotSupportedException($"Field type {field.Type} is not yet supported for text conversion.");
        }
    }

    private static string ReadText(int chunkId, Func<int, string> readText) =>
        chunkId == 0 ? string.Empty : readText != null ? readText(chunkId) : $"(text #{chunkId})";

    private static unsafe object ReadField(byte* ptr, ComponentSchema.FieldInfo field) =>
        field.Type switch
        {
            FieldType.Boolean  => *(bool*)ptr,
            FieldType.Byte     => *(sbyte*)ptr,
            FieldType.UByte    => *ptr,
            FieldType.Char     => *(char*)ptr,
            FieldType.Short    => *(short*)ptr,
            FieldType.UShort   => *(ushort*)ptr,
            FieldType.Int      => *(int*)ptr,
            FieldType.UInt     => *(uint*)ptr,
            FieldType.Float    => *(float*)ptr,
            FieldType.Double   => *(double*)ptr,
            FieldType.Long     => *(long*)ptr,
            FieldType.ULong    => *(ulong*)ptr,
            FieldType.String64     => (*(String64*)ptr).ToString(),
            FieldType.Point2F      => *(Point2F*)ptr,
            FieldType.Point3F      => *(Point3F*)ptr,
            FieldType.Point4F      => *(Point4F*)ptr,
            FieldType.Point2D      => *(Point2D*)ptr,
            FieldType.Point3D      => *(Point3D*)ptr,
            FieldType.Point4D      => *(Point4D*)ptr,
            FieldType.QuaternionF  => *(QuaternionF*)ptr,
            FieldType.QuaternionD  => *(QuaternionD*)ptr,
            FieldType.Variant      => (*(Variant*)ptr).ToString(),
            _                      => $"<unsupported:{field.Type}>"
        };

    private static string StripNumericSuffix(string text)
    {
        if (text.Length == 0)
        {
            return text;
        }

        // Strip uL/UL/ul suffix
        if (text.Length >= 2)
        {
            var last2 = text[^2..];
            if (last2 is "uL" or "UL" or "ul" or "Ul")
            {
                return text[..^2];
            }
        }

        var last = text[^1];
        if (last is 'u' or 'U' or 'L' or 'l' or 'd' or 'D' or 'f' or 'F')
        {
            return text[..^1];
        }

        return text;
    }

    /// <summary>
    /// Formats a field value for display.
    /// </summary>
    public static string FormatValue(object value, FieldType fieldType) =>
        fieldType switch
        {
            FieldType.Float        => ((float)value).ToString("G", CultureInfo.InvariantCulture),
            FieldType.Double       => ((double)value).ToString("G", CultureInfo.InvariantCulture),
            FieldType.Boolean      => ((bool)value).ToString().ToLowerInvariant(),
            FieldType.String64     => $"\"{value}\"",
            FieldType.String1024   => $"\"{value}\"",
            FieldType.Point2F      => FormatPoint2F((Point2F)value),
            FieldType.Point3F      => FormatPoint3F((Point3F)value),
            FieldType.Point4F      => FormatPoint4F((Point4F)value),
            FieldType.Point2D      => FormatPoint2D((Point2D)value),
            FieldType.Point3D      => FormatPoint3D((Point3D)value),
            FieldType.Point4D      => FormatPoint4D((Point4D)value),
            FieldType.QuaternionF  => FormatQuaternionF((QuaternionF)value),
            FieldType.QuaternionD  => FormatQuaternionD((QuaternionD)value),
            FieldType.Variant      => $"\"{value}\"",
            _                      => value?.ToString() ?? "null"
        };

    private static string[] ParseTupleComponents(string text, int expectedCount, string fieldName)
    {
        // Strip surrounding parentheses if present: (1.0, 2.0, 3.0) → 1.0, 2.0, 3.0
        var inner = text.Trim();
        if (inner.StartsWith('(') && inner.EndsWith(')'))
        {
            inner = inner[1..^1];
        }

        var parts = inner.Split(',');
        if (parts.Length != expectedCount)
        {
            throw new ArgumentException(
                $"Field '{fieldName}' expects {expectedCount} components but got {parts.Length}. Use format: ({string.Join(", ", new string[expectedCount].Select((_, i) => "v" + i))})");
        }

        for (var i = 0; i < parts.Length; i++)
        {
            parts[i] = StripNumericSuffix(parts[i].Trim());
        }

        return parts;
    }

    private static string F(float v) => v.ToString("G", CultureInfo.InvariantCulture);
    private static string D(double v) => v.ToString("G", CultureInfo.InvariantCulture);

    private static string FormatPoint2F(Point2F p) => $"({F(p.X)}, {F(p.Y)})";
    private static string FormatPoint3F(Point3F p) => $"({F(p.X)}, {F(p.Y)}, {F(p.Z)})";
    private static string FormatPoint4F(Point4F p) => $"({F(p.X)}, {F(p.Y)}, {F(p.Z)}, {F(p.W)})";
    private static string FormatPoint2D(Point2D p) => $"({D(p.X)}, {D(p.Y)})";
    private static string FormatPoint3D(Point3D p) => $"({D(p.X)}, {D(p.Y)}, {D(p.Z)})";
    private static string FormatPoint4D(Point4D p) => $"({D(p.X)}, {D(p.Y)}, {D(p.Z)}, {D(p.W)})";
    private static string FormatQuaternionF(QuaternionF q) => $"({F(q.X)}, {F(q.Y)}, {F(q.Z)}, {F(q.W)})";
    private static string FormatQuaternionD(QuaternionD q) => $"({D(q.X)}, {D(q.Y)}, {D(q.Z)}, {D(q.W)})";
}
