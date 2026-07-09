// unset

using JetBrains.Annotations;
using System;
using System.Reflection;

namespace Typhon.Schema.Definition;

/// <summary>
/// Identifies the storage type of a component field. The two high bits <see cref="F:Typhon.Schema.Definition.FieldType.Unsigned"/> and
/// <see cref="F:Typhon.Schema.Definition.FieldType.DoubleFloat"/> are flags OR-ed onto a base type to form its unsigned-integer or double-precision variant
/// (e.g. <see cref="F:Typhon.Schema.Definition.FieldType.UInt"/> = <see cref="F:Typhon.Schema.Definition.FieldType.Unsigned"/> |
/// <see cref="F:Typhon.Schema.Definition.FieldType.Int"/>); the remaining low ordinals identify the base types themselves.
/// </summary>
[PublicAPI]
[Flags]
public enum FieldType
{
    /// <summary>No type — unset / sentinel.</summary>
    None        = 0,
    /// <summary>1-byte boolean.</summary>
    Boolean     = 1,
    /// <summary>Signed 8-bit integer (<c>sbyte</c>).</summary>
    Byte        = 2,
    /// <summary>Signed 16-bit integer (<c>short</c>).</summary>
    Short       = 3,
    /// <summary>Signed 32-bit integer (<c>int</c>).</summary>
    Int         = 4,
    /// <summary>Signed 64-bit integer (<c>long</c>).</summary>
    Long        = 5,
    /// <summary>Unsigned 8-bit integer (<c>byte</c>).</summary>
    UByte       = Unsigned | Byte,
    /// <summary>Unsigned 16-bit integer (<c>ushort</c>).</summary>
    UShort      = Unsigned | Short,
    /// <summary>Unsigned 32-bit integer (<c>uint</c>).</summary>
    UInt        = Unsigned | Int,
    /// <summary>Unsigned 64-bit integer (<c>ulong</c>).</summary>
    ULong       = Unsigned | Long,
    /// <summary>32-bit IEEE-754 single-precision float.</summary>
    Float       = 6,
    /// <summary>64-bit IEEE-754 double-precision float.</summary>
    Double      = DoubleFloat | Float,
    /// <summary>UTF-16 code unit (2 bytes).</summary>
    Char        = 7,
    /// <summary>Fixed 64-byte inline UTF-8 string (<see cref="T:Typhon.Schema.Definition.String64"/>).</summary>
    String64    = 8,
    /// <summary>Fixed 1024-byte inline UTF-8 string (<see cref="T:Typhon.Schema.Definition.String1024"/>).</summary>
    String1024  = 9,
    /// <summary>Variable-length string with a 32-byte inline handle; the payload spills to the variable-size buffer (<see cref="VarString"/>).</summary>
    String      = 10,
    /// <summary>Self-describing value stored as a <see cref="T:Typhon.Schema.Definition.Variant"/> — a <see cref="T:Typhon.Schema.Definition.String64"/> of the form <c>"tt:data"</c>.</summary>
    Variant     = String64 | 11,            // Use the Variant type, a String64 of the form "tt:data" storing data of a given type
    /// <summary>2-component single-precision point (<see cref="T:Typhon.Schema.Definition.Point2F"/>).</summary>
    Point2F     = 12,
    /// <summary>3-component single-precision point (<see cref="T:Typhon.Schema.Definition.Point3F"/>).</summary>
    Point3F     = 13,
    /// <summary>4-component single-precision point (<see cref="T:Typhon.Schema.Definition.Point4F"/>).</summary>
    Point4F     = 14,
    /// <summary>2-component double-precision point (<see cref="T:Typhon.Schema.Definition.Point2D"/>).</summary>
    Point2D     = DoubleFloat | Point2F,
    /// <summary>3-component double-precision point (<see cref="T:Typhon.Schema.Definition.Point3D"/>).</summary>
    Point3D     = DoubleFloat | Point3F,
    /// <summary>4-component double-precision point (<see cref="T:Typhon.Schema.Definition.Point4D"/>).</summary>
    Point4D     = DoubleFloat | Point4F,
    /// <summary>Single-precision quaternion (<see cref="T:Typhon.Schema.Definition.QuaternionF"/>).</summary>
    QuaternionF = 15,
    /// <summary>Double-precision quaternion (<see cref="T:Typhon.Schema.Definition.QuaternionD"/>).</summary>
    QuaternionD = DoubleFloat |  15,

    /// <summary>A <see cref="ComponentCollection{T}"/> field — a 4-byte buffer-id handle to a variable-length element buffer.</summary>
    Collection  = 16,
    /// <summary>A nested-component reference (8-byte handle).</summary>
    Component   = 17,

    /// <summary>Single-precision 2D axis-aligned bounding box (<see cref="T:Typhon.Schema.Definition.AABB2F"/>).</summary>
    AABB2F      = 18,
    /// <summary>Single-precision 3D axis-aligned bounding box (<see cref="T:Typhon.Schema.Definition.AABB3F"/>).</summary>
    AABB3F      = 19,
    /// <summary>Single-precision 2D bounding sphere (<see cref="T:Typhon.Schema.Definition.BSphere2F"/>).</summary>
    BSphere2F   = 20,
    /// <summary>Single-precision 3D bounding sphere (<see cref="T:Typhon.Schema.Definition.BSphere3F"/>).</summary>
    BSphere3F   = 21,
    /// <summary>Double-precision 2D axis-aligned bounding box (<see cref="T:Typhon.Schema.Definition.AABB2D"/>).</summary>
    AABB2D      = DoubleFloat | AABB2F,
    /// <summary>Double-precision 3D axis-aligned bounding box (<see cref="T:Typhon.Schema.Definition.AABB3D"/>).</summary>
    AABB3D      = DoubleFloat | AABB3F,
    /// <summary>Double-precision 2D bounding sphere (<see cref="T:Typhon.Schema.Definition.BSphere2D"/>).</summary>
    BSphere2D   = DoubleFloat | BSphere2F,
    /// <summary>Double-precision 3D bounding sphere (<see cref="T:Typhon.Schema.Definition.BSphere3D"/>).</summary>
    BSphere3D   = DoubleFloat | BSphere3F,

    /// <summary>Flag bit marking the unsigned-integer variant of an integer base type.</summary>
    Unsigned    = 256,
    /// <summary>Flag bit marking the double-precision variant of a float, point, quaternion, or bounding-volume base type.</summary>
    DoubleFloat = 512
}

/// <summary>Maps CLR types to their schema <see cref="FieldType"/> and computes the inline byte size a field of a given type occupies in component storage.</summary>
public static class DatabaseSchemaExtensions
{
    /// <summary>Resolves the <see cref="FieldType"/> pair for the CLR type <typeparamref name="T"/>. See <see cref="FromType(Type)"/> for the mapping and return semantics.</summary>
    /// <typeparam name="T">The CLR type to map.</typeparam>
    /// <returns>The resolved field type and, for a <see cref="FieldType.Collection"/>, its element type in <c>under</c>; <see cref="FieldType.None"/> for both when unrecognized.</returns>
    public static (FieldType field, FieldType under) FromType<T>() => FromType(typeof(T));

    /// <summary>
    /// Maps a CLR type to its schema <see cref="FieldType"/>, resolving primitives, the built-in value types (points, quaternions, bounding volumes, strings),
    /// nested components (types marked with <see cref="ComponentAttribute"/>), <see cref="ComponentCollection{T}"/> collections, and <c>EntityLink&lt;T&gt;</c>
    /// foreign keys (indexed as <see cref="FieldType.Long"/>).
    /// </summary>
    /// <param name="t">The CLR type to map.</param>
    /// <returns>
    /// A pair whose <c>field</c> is the resolved <see cref="FieldType"/> and whose <c>under</c> carries the element type for a <see cref="FieldType.Collection"/>
    /// (otherwise <see cref="FieldType.None"/>). Returns (<see cref="FieldType.None"/>, <see cref="FieldType.None"/>) for an unrecognized type.
    /// </returns>
    public static (FieldType field, FieldType under) FromType(Type t)
    {
        switch (Type.GetTypeCode(t))
        {
            case TypeCode.Boolean: return (FieldType.Boolean, FieldType.None);

            case TypeCode.Byte: return (FieldType.UByte, FieldType.None);
            case TypeCode.SByte: return (FieldType.Byte, FieldType.None);
            case TypeCode.Char: return (FieldType.Char, FieldType.None);

            case TypeCode.Double: return (FieldType.Double, FieldType.None);

            case TypeCode.Int16: return (FieldType.Short, FieldType.None);
            case TypeCode.Int32: return (FieldType.Int, FieldType.None);
            case TypeCode.Int64: return (FieldType.Long, FieldType.None);
            case TypeCode.UInt16: return (FieldType.UShort, FieldType.None);
            case TypeCode.UInt32: return (FieldType.UInt, FieldType.None);
            case TypeCode.UInt64: return (FieldType.ULong, FieldType.None);
        }

        var ca = t.GetCustomAttribute<ComponentAttribute>();
        if (ca != null)
        {
            return (FieldType.Component, FieldType.None);
        }

        if (t == typeof(float)) return (FieldType.Float, FieldType.None);
        if (t == typeof(String64)) return (FieldType.String64, FieldType.None);
        if (t == typeof(String1024)) return (FieldType.String1024, FieldType.None);
        if (t == typeof(VarString)) return (FieldType.String, FieldType.None);

        if (t == typeof(Point2F)) return (FieldType.Point2F, FieldType.None);
        if (t == typeof(Point3F)) return (FieldType.Point3F, FieldType.None);
        if (t == typeof(Point4F)) return (FieldType.Point4F, FieldType.None);

        if (t == typeof(Point2D)) return (FieldType.Point2D, FieldType.None);
        if (t == typeof(Point3D)) return (FieldType.Point3D, FieldType.None);
        if (t == typeof(Point4D)) return (FieldType.Point4D, FieldType.None);

        if (t == typeof(QuaternionF)) return (FieldType.QuaternionF, FieldType.None);
        if (t == typeof(QuaternionD)) return (FieldType.QuaternionD, FieldType.None);

        if (t == typeof(AABB2F)) return (FieldType.AABB2F, FieldType.None);
        if (t == typeof(AABB3F)) return (FieldType.AABB3F, FieldType.None);
        if (t == typeof(BSphere2F)) return (FieldType.BSphere2F, FieldType.None);
        if (t == typeof(BSphere3F)) return (FieldType.BSphere3F, FieldType.None);
        if (t == typeof(AABB2D)) return (FieldType.AABB2D, FieldType.None);
        if (t == typeof(AABB3D)) return (FieldType.AABB3D, FieldType.None);
        if (t == typeof(BSphere2D)) return (FieldType.BSphere2D, FieldType.None);
        if (t == typeof(BSphere3D)) return (FieldType.BSphere3D, FieldType.None);

        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ComponentCollection<>))
        {
            return (FieldType.Collection, FromType(t.GenericTypeArguments[0]).field);
        }

        // EntityLink<T> is an 8-byte FK reference (wraps EntityId) — index as Long.
        // Check by name since EntityLink<> is in Typhon.Engine, not Typhon.Schema.Definition.
        if (t.IsGenericType && t.GetGenericTypeDefinition().Name == "EntityLink`1")
        {
            return (FieldType.Long, FieldType.None);
        }

        return (FieldType.None, FieldType.None);
    }

    /// <summary>
    /// Byte size a field of the given <paramref name="field"/> type occupies inline in component storage. The variable-length <see cref="FieldType.String"/>
    /// counts only its 32-byte inline handle (the payload spills to the variable-size buffer); a <see cref="FieldType.Collection"/> is its 4-byte buffer id and
    /// a <see cref="FieldType.Component"/> its 8-byte handle. Returns <c>0</c> for types with no fixed inline footprint.
    /// </summary>
    /// <param name="field">The field type to size.</param>
    /// <returns>The inline size in bytes, or <c>0</c> if the type has no fixed inline footprint.</returns>
    public static int FieldSizeInComp(this FieldType field)
    {
        switch (field)
        {
            case FieldType.UByte:
            case FieldType.Byte:
            case FieldType.Boolean: return 1;

            case FieldType.Char:
            case FieldType.Short:
            case FieldType.UShort: return 2;

            case FieldType.Float:
            case FieldType.UInt:
            case FieldType.Int: return 4;

            case FieldType.Double:
            case FieldType.ULong:
            case FieldType.Long: return 8;

            case FieldType.Point2F: return 8;
            case FieldType.Point3F: return 12;
            case FieldType.Point4F: return 16;

            case FieldType.Point2D: return 16;
            case FieldType.Point3D: return 24;
            case FieldType.Point4D: return 32;

            case FieldType.String: return 32;  // Don't count the overflow in VSB
            case FieldType.String64: return 64;
            case FieldType.String1024: return 1024;

            case FieldType.QuaternionF: return 16;
            case FieldType.QuaternionD: return 32;

            case FieldType.AABB2F: return 16;
            case FieldType.AABB3F: return 24;
            case FieldType.BSphere2F: return 12;
            case FieldType.BSphere3F: return 16;
            case FieldType.AABB2D: return 32;
            case FieldType.AABB3D: return 48;
            case FieldType.BSphere2D: return 24;
            case FieldType.BSphere3D: return 32;

            case FieldType.Collection: return 4;
            case FieldType.Component: return 8;

            default: return 0;
        }
    }
}
