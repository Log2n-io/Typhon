using System;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Typhon.Engine;
using Typhon.Schema.Definition;
using Typhon.Workbench.Dtos.Data;

namespace Typhon.Workbench.DataBrowser;

/// <summary>
/// Decodes a single component field's value out of the raw component bytes returned by <see cref="EntityRef.ReadRaw"/>, using
/// only the field's offset + storage-type enum — no reflection, no boxing beyond the produced <see cref="ComponentValueDto"/>.
/// Primitive scalars (bool, every integer width, float, double, char), fixed UTF-8 strings, and the geometric value types
/// (points, quaternions, AABBs, bounding spheres — both float and double precision) decode to a JSON-native value; the
/// remainder (collections, arrays, nested components) falls back to a hex dump in <see cref="ComponentValueDto.Raw"/> with a
/// null value. Geometric types render as a human-readable, culture-invariant string (e.g. AABB2F → "min(0, 0)\nmax(1, 1)", min
/// and max on separate lines).
/// </summary>
internal static class ComponentValueDecoder
{
    public static ComponentValueDto Decode(DBComponentDefinition.Field field, ReadOnlySpan<byte> componentBytes)
    {
        var offset = field.OffsetInComponentStorage;
        var size = field.SizeInComponentStorage;

        // Defensive: a malformed schema/layout could put the field outside the storage. Never read past the span.
        if (offset < 0 || size <= 0 || offset + size > componentBytes.Length)
        {
            return new ComponentValueDto(field.FieldId, null, "");
        }

        var bytes = componentBytes.Slice(offset, size);
        var raw = Convert.ToHexString(bytes).ToLowerInvariant();
        var value = field.IsArray ? null : DecodeScalar(field.Type, bytes);
        return new ComponentValueDto(field.FieldId, value, raw);
    }

    // Returns a boxed JSON-native scalar (System.Text.Json serializes int/uint/long/float/double/bool→primitive, string→string),
    // or null for field types we don't render inline (the hex dump still carries the bytes).
    private static object DecodeScalar(FieldType type, ReadOnlySpan<byte> bytes) => type switch
    {
        FieldType.Boolean => bytes[0] != 0,
        FieldType.Byte => (sbyte)bytes[0],
        FieldType.UByte => bytes[0],
        FieldType.Short => BinaryPrimitives.ReadInt16LittleEndian(bytes),
        FieldType.UShort => BinaryPrimitives.ReadUInt16LittleEndian(bytes),
        FieldType.Int => BinaryPrimitives.ReadInt32LittleEndian(bytes),
        FieldType.UInt => BinaryPrimitives.ReadUInt32LittleEndian(bytes),
        FieldType.Long => BinaryPrimitives.ReadInt64LittleEndian(bytes),
        FieldType.ULong => BinaryPrimitives.ReadUInt64LittleEndian(bytes),
        FieldType.Float => BinaryPrimitives.ReadSingleLittleEndian(bytes),
        FieldType.Double => BinaryPrimitives.ReadDoubleLittleEndian(bytes),
        FieldType.Char => ((char)BinaryPrimitives.ReadUInt16LittleEndian(bytes)).ToString(),
        FieldType.String64 or FieldType.String1024 or FieldType.Variant => DecodeUtf8(bytes),

        FieldType.Point2F => FormatTuple(bytes, isDouble: false, 2),
        FieldType.Point3F => FormatTuple(bytes, isDouble: false, 3),
        FieldType.Point4F or FieldType.QuaternionF => FormatTuple(bytes, isDouble: false, 4),
        FieldType.Point2D => FormatTuple(bytes, isDouble: true, 2),
        FieldType.Point3D => FormatTuple(bytes, isDouble: true, 3),
        FieldType.Point4D or FieldType.QuaternionD => FormatTuple(bytes, isDouble: true, 4),

        FieldType.AABB2F => FormatAabb(bytes, isDouble: false, dims: 2),
        FieldType.AABB3F => FormatAabb(bytes, isDouble: false, dims: 3),
        FieldType.AABB2D => FormatAabb(bytes, isDouble: true, dims: 2),
        FieldType.AABB3D => FormatAabb(bytes, isDouble: true, dims: 3),

        FieldType.BSphere2F => FormatSphere(bytes, isDouble: false, dims: 2),
        FieldType.BSphere3F => FormatSphere(bytes, isDouble: false, dims: 3),
        FieldType.BSphere2D => FormatSphere(bytes, isDouble: true, dims: 2),
        FieldType.BSphere3D => FormatSphere(bytes, isDouble: true, dims: 3),

        _ => null,
    };

    // Reads element i (i*4 for float, i*8 for double) and formats it culture-invariantly. Floats are formatted as float (not
    // promoted to double) so the printed text matches the stored single-precision value rather than its double widening.
    private static string Component(ReadOnlySpan<byte> bytes, int i, bool isDouble) =>
        isDouble
            ? BinaryPrimitives.ReadDoubleLittleEndian(bytes.Slice(i * 8, 8)).ToString(CultureInfo.InvariantCulture)
            : BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(i * 4, 4)).ToString(CultureInfo.InvariantCulture);

    // Points (n components) and quaternions (x, y, z, w) → "(a, b, …)".
    private static string FormatTuple(ReadOnlySpan<byte> bytes, bool isDouble, int n)
    {
        var sb = new StringBuilder("(");
        for (var i = 0; i < n; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }
            sb.Append(Component(bytes, i, isDouble));
        }
        return sb.Append(')').ToString();
    }

    // AABB layout is [min components][max components] → "min(a, b)\nmax(c, d)" (min / max on separate lines in the detail view).
    private static string FormatAabb(ReadOnlySpan<byte> bytes, bool isDouble, int dims)
    {
        var sb = new StringBuilder("min(");
        for (var i = 0; i < dims; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }
            sb.Append(Component(bytes, i, isDouble));
        }
        sb.Append(")\nmax(");
        for (var i = 0; i < dims; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }
            sb.Append(Component(bytes, dims + i, isDouble));
        }
        return sb.Append(')').ToString();
    }

    // Bounding-sphere layout is [center components][radius] → "center(a, b) r=c".
    private static string FormatSphere(ReadOnlySpan<byte> bytes, bool isDouble, int dims)
    {
        var sb = new StringBuilder("center(");
        for (var i = 0; i < dims; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }
            sb.Append(Component(bytes, i, isDouble));
        }
        sb.Append(") r=").Append(Component(bytes, dims, isDouble));
        return sb.ToString();
    }

    // Fixed-size UTF-8 buffers are null-terminated (see String64 / String1024). Decode the prefix up to the first NUL.
    private static string DecodeUtf8(ReadOnlySpan<byte> bytes)
    {
        var nul = bytes.IndexOf((byte)0);
        var content = nul >= 0 ? bytes[..nul] : bytes;
        return Encoding.UTF8.GetString(content);
    }
}
