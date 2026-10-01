using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Typhon.Protocol;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Internals;

/// <summary>
/// How a projected column turns its values into wire codes — the column walk's specialization, decided once per field (design/Subscriptions/13 § 4).
/// </summary>
internal enum ColumnPath : byte
{
    /// <summary>Not a scalar column: a codec this table does not judge (a position, text, a list).</summary>
    None = 0,

    /// <summary>W1's binary64 arithmetic: the quantizing codecs, <c>f32</c> from a <see cref="double"/>, and a vector's per-axis code.</summary>
    Quantizing,

    /// <summary>An integral source into an integer codec whose range covers it: the value is the code, with no rounding, no NaN test and no clamp.</summary>
    ExactInteger,

    /// <summary>An integral source into a narrower integer codec: clamped to the codec's range, and every clamp counted.</summary>
    NarrowingInteger,

    /// <summary>A <see cref="float"/> into <c>f32</c>: its bits are the code, NaN canonicalized.</summary>
    ExactSingle,

    /// <summary>A <see cref="double"/> into <c>f64</c> (W32): its bits are the code, NaN canonicalized.</summary>
    ExactDouble,

    /// <summary>A quaternion into <c>quat3</c>: four components read into one 32-bit code (W8).</summary>
    Quaternion,
}

/// <summary>
/// The inclusive range of an integral type or an integer codec, in 64 bits. Every such range contains 0, so its low bound is never above 0 and its high
/// bound never below it: <see cref="Min"/> is a <see cref="long"/> (down to <c>i64</c>'s −2⁶³) and <see cref="Max"/> a <see cref="ulong"/> (up to
/// <c>u64</c>'s 2⁶⁴ − 1), and comparing two ranges is one signed and one unsigned compare — no wider type, no sign cases.
/// </summary>
internal readonly record struct IntegerRange
{
    public IntegerRange(long min, ulong max)
    {
        if (min > 0)
        {
            throw new ArgumentOutOfRangeException(nameof(min), min, "An integer range contains 0: its low bound is at most 0.");
        }

        Min = min;
        Max = max;
    }

    /// <summary>The lowest value, at most 0.</summary>
    public long Min { get; }

    /// <summary>The highest value, at least 0.</summary>
    public ulong Max { get; }

    /// <summary>Whether every value of <paramref name="other"/> is in this range.</summary>
    public bool Covers(IntegerRange other) => other.Min >= Min && other.Max <= Max;

    /// <summary>Whether a signed value is in this range.</summary>
    public bool Contains(long value) => value < 0 ? value >= Min : (ulong)value <= Max;

    /// <summary>Whether an unsigned value is in this range.</summary>
    public bool Contains(ulong value) => value <= Max;

    /// <summary><see cref="Max"/> as the <see cref="long"/> a signed reader compares against: <see cref="long.MaxValue"/> when it is above it.</summary>
    public long SignedMax => Max > long.MaxValue ? long.MaxValue : (long)Max;

    /// <inheritdoc/>
    public override string ToString() => $"{Min}..{Max}";
}

/// <summary>
/// A fixed-shape value type the wire carries as <c>count</c> values (W33): its element type, its components in WIRE order, and the hint the catalog
/// names it by.
/// </summary>
/// <remarks>
/// <b>Components by name, not by position.</b> The wire order is the shape's — point <c>x y [z] [w]</c>, quaternion <c>x y z w</c>, AABB
/// <c>minX minY [minZ] maxX maxY [maxZ]</c>, sphere <c>cx cy [cz] r</c> — and each component's offset is read from its member, so a struct whose members
/// were reordered moves no byte on the wire (13 § 2.1).
/// </remarks>
internal sealed class FieldShape
{
    private static readonly Dictionary<Type, FieldShape> Shapes = Build();

    private FieldShape(Type type, Type element, string name, string[] members)
    {
        Type = type;
        Element = element;
        Name = name;
        Offsets = new int[members.Length];
        for (var i = 0; i < members.Length; i++)
        {
            Offsets[i] = (int)Marshal.OffsetOf(type, members[i]);
        }
    }

    /// <summary>The CLR type.</summary>
    public Type Type { get; }

    /// <summary><see cref="float"/> or <see cref="double"/>.</summary>
    public Type Element { get; }

    /// <summary>The catalog's <c>shape</c> hint.</summary>
    public string Name { get; }

    /// <summary>Each component's byte offset inside the struct, in wire order.</summary>
    public int[] Offsets { get; }

    /// <summary>How many components the wire carries.</summary>
    public int Count => Offsets.Length;

    /// <summary>The bytes of one component.</summary>
    public int ElementSize => Element == typeof(double) ? 8 : 4;

    /// <summary>The shape of a type, or <see langword="null"/> for one that is not a fixed shape.</summary>
    public static FieldShape Of(Type type) => type != null && Shapes.TryGetValue(type, out var shape) ? shape : null;

    private static Dictionary<Type, FieldShape> Build()
    {
        string[] p2 = ["X", "Y"], p3 = ["X", "Y", "Z"], p4 = ["X", "Y", "Z", "W"];
        string[] b2 = ["MinX", "MinY", "MaxX", "MaxY"], b3 = ["MinX", "MinY", "MinZ", "MaxX", "MaxY", "MaxZ"];
        string[] s2 = ["CenterX", "CenterY", "Radius"], s3 = ["CenterX", "CenterY", "CenterZ", "Radius"];
        var all = new[]
        {
            new FieldShape(typeof(Point2F), typeof(float), "point2", p2), new FieldShape(typeof(Point3F), typeof(float), "point3", p3),
            new FieldShape(typeof(Point4F), typeof(float), "point4", p4), new FieldShape(typeof(Point2D), typeof(double), "point2", p2),
            new FieldShape(typeof(Point3D), typeof(double), "point3", p3), new FieldShape(typeof(Point4D), typeof(double), "point4", p4),
            new FieldShape(typeof(QuaternionF), typeof(float), "quat", p4), new FieldShape(typeof(QuaternionD), typeof(double), "quat", p4),
            new FieldShape(typeof(AABB2F), typeof(float), "aabb2", b2), new FieldShape(typeof(AABB3F), typeof(float), "aabb3", b3),
            new FieldShape(typeof(AABB2D), typeof(double), "aabb2", b2), new FieldShape(typeof(AABB3D), typeof(double), "aabb3", b3),
            new FieldShape(typeof(BSphere2F), typeof(float), "bsphere2", s2), new FieldShape(typeof(BSphere3F), typeof(float), "bsphere3", s3),
            new FieldShape(typeof(BSphere2D), typeof(double), "bsphere2", s2), new FieldShape(typeof(BSphere3D), typeof(double), "bsphere3", s3),
        };

        var result = new Dictionary<Type, FieldShape>();
        foreach (var shape in all)
        {
            result[shape.Type] = shape;
        }

        return result;
    }
}

/// <summary>
/// The source/codec pairing table (design/Subscriptions/13 § 2.3): which codec a source type may travel under, and which column path carries it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Exact by default, lossy only when declared, and never lossy for no gain.</b> An integral source in a codec whose range covers it is exact. A narrower
/// integer codec loses range, so it is legal only when the declaration says <see cref="Codec.Saturate"/> — for every width (an enum whose declared values
/// all fit is exempt: its range is its names). A quantizing codec on a <see cref="float"/> or a <see cref="double"/>, and a <c>quant</c> on an integer, are
/// declared losses of precision. What is refused is a pairing that loses data and buys nothing: a float in an integer codec (the same bytes as <c>f32</c>,
/// or more, with the fraction gone — <c>quant</c> says the same thing with a stated range and rounding), an integer in a float codec other than
/// <c>quant</c>, and a <see cref="float"/> in <c>f64</c> (four bytes that carry nothing).
/// </para>
/// <para>
/// <b>A shape is judged by its element</b> (W33): a <c>Point3F</c> is three <see cref="float"/>s, so it pairs like a <see cref="float"/> — and its
/// <c>count</c> must be its component count. <see cref="Resolve"/> fills that count, and resolves <see cref="Codec.Exact"/>, before anything is judged.
/// </para>
/// <para>
/// One table for archetype fields, owner fields, onEnter fields, event fields and command fields: the registry runs it at declaration time, the projection
/// compiler again at <c>Start</c> to choose the path. A codec the table does not judge — a position, text, bytes, a list, an <see cref="EntityId"/>
/// reference — returns <see cref="ColumnPath.None"/> and is left to the checks that own it.
/// </para>
/// </remarks>
internal static class CodecPairing
{
    /// <summary>
    /// Resolves a declaration against the type it reads: <see cref="Codec.Exact"/> becomes the type's exact codec, and a shape's count is filled from
    /// the type — or checked against it when the declaration stated one.
    /// </summary>
    /// <param name="storedType">The field's stored type.</param>
    /// <param name="codec">The declared codec.</param>
    /// <param name="where">Names the field in a refusal.</param>
    /// <param name="shape">The catalog's shape hint for the type (W33), or <see langword="null"/>.</param>
    /// <returns>The codec to judge, compile and export.</returns>
    /// <exception cref="InvalidOperationException">An exact codec for a type that has none, or a count that disagrees with the shape.</exception>
    public static Codec Resolve(Type storedType, Codec codec, string where, out string shape)
    {
        ArgumentNullException.ThrowIfNull(storedType);
        var fieldShape = FieldShape.Of(storedType);
        shape = fieldShape?.Name ?? (storedType == typeof(char) ? "char" : null);

        if (codec.IsExact)
        {
            if (fieldShape != null)
            {
                return codec.WithCatalog(new CatalogCodec { Kind = fieldShape.Element == typeof(double) ? CodecKind.F64 : CodecKind.F32, Count = fieldShape.Count });
            }

            if (storedType == typeof(char))
            {
                return codec.WithCatalog(new CatalogCodec { Kind = CodecKind.U16 });
            }

            var byType = MessageContract.DefaultCodec(storedType, out var enumType);
            if (!byType.IsDeclared)
            {
                throw new InvalidOperationException(
                    $"{where} declares Codec.Exact on a {storedType.Name}, which has no exact codec on the wire (13 § 2.1). Declare the codec it travels under.");
            }

            return codec.WithCatalog(byType.Catalog, enumType ?? byType.EnumType);
        }

        var catalog = codec.Catalog;
        if (catalog == null)
        {
            return codec;
        }

        if (fieldShape == null)
        {
            if (catalog.Count > 1)
            {
                throw new InvalidOperationException(
                    $"{where} declares {CodecTokens.ToToken(catalog.Kind)} × {catalog.Count} on a {storedType.Name}, which is one value. A count belongs on a " +
                    "fixed shape — a point, a quaternion, a box, a sphere (W33).");
            }

            return codec;
        }

        switch (catalog.Kind)
        {
            case CodecKind.Vec2:
            case CodecKind.Vec3:
                // A vector codec is a shape of its own: its axis count is the point's.
                var axes = catalog.Kind == CodecKind.Vec2 ? 2 : 3;
                if (fieldShape.Count != axes || !fieldShape.Name.StartsWith("point", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"{where} declares {CodecTokens.ToToken(catalog.Kind)} on a {storedType.Name} ({fieldShape.Count} components): a vector codec carries " +
                        $"a point of {axes} axes. Declare a count of the element's codec instead (13 § 2.3).");
                }

                return codec;
            case CodecKind.Quat3:
                if (fieldShape.Name != "quat")
                {
                    throw new InvalidOperationException($"{where} declares quat3 on a {storedType.Name}, which is not a quaternion.");
                }

                return codec;
        }

        if (!CatalogValidator.TakesCount(catalog.Kind))
        {
            throw new InvalidOperationException(
                $"{where} declares {CodecTokens.ToToken(catalog.Kind)} on a {storedType.Name}, a shape of {fieldShape.Count} values: it travels as a count of a " +
                "scalar codec (Codec.Exact, or e.g. Codec.F16), or as vec2 / vec3 / quat3 where those fit (13 § 2.3).");
        }

        if (catalog.Count == 0)
        {
            return codec.WithCatalog(catalog.WithCount(fieldShape.Count));
        }

        if (catalog.Count != fieldShape.Count)
        {
            throw new InvalidOperationException(
                $"{where} declares {CodecTokens.ToToken(catalog.Kind)} × {catalog.Count} on a {storedType.Name}, which has {fieldShape.Count} components (W33). " +
                "Leave the count out: the shape gives it.");
        }

        return codec;
    }

    /// <summary>Classifies a pairing, refusing it when it loses data for no gain or narrows without saying so.</summary>
    /// <param name="sourceType">The field's CLR type; an enum is judged by its underlying type and its declared values, a shape by its element.</param>
    /// <param name="codec">The declared codec, resolved (<see cref="Resolve"/>).</param>
    /// <param name="saturating">Whether the declaration marked the codec <see cref="Codec.Saturate"/>.</param>
    /// <param name="where">Names the field in a refusal: <c>"Field 'mode' of archetype 'Creature'"</c>.</param>
    /// <param name="message">
    /// Whether the field belongs to a command or an event. A message may carry a netId the client already holds as a <see cref="uint"/> with
    /// <c>entityRef</c> (01 § 7: a command names an entity by its netId, resolved by <c>TryResolve</c>); an entity's state field may not, because its
    /// value is read from storage and only an <see cref="EntityId"/> can be resolved to a netId there (W35).
    /// </param>
    /// <returns>The column path, or <see cref="ColumnPath.None"/> for a codec this table does not judge.</returns>
    /// <exception cref="InvalidOperationException">The pairing is refused; the message names the source, the codec, the loss and what to declare.</exception>
    public static ColumnPath Classify(Type sourceType, CatalogCodec codec, bool saturating, string where, bool message = false)
    {
        ArgumentNullException.ThrowIfNull(sourceType);
        ArgumentNullException.ThrowIfNull(codec);

        var shape = FieldShape.Of(sourceType);
        if (shape != null)
        {
            if (codec.Kind is CodecKind.Vec2 or CodecKind.Vec3)
            {
                return ColumnPath.Quantizing;
            }

            if (codec.Kind == CodecKind.Quat3)
            {
                return ColumnPath.Quaternion;
            }

            // The element is what each component pairs as: Resolve already checked the count against the shape.
            return Classify(shape.Element, codec, saturating, $"{where} ({shape.Name} component)", message);
        }

        var enumType = sourceType.IsEnum ? sourceType : null;
        var type = enumType != null ? Enum.GetUnderlyingType(sourceType) : sourceType;
        var kind = codec.Kind;

        if (kind == CodecKind.EntityRef)
        {
            if (message && type == typeof(uint) && enumType == null)
            {
                return ColumnPath.None;
            }

            if (IsIntegral(type))
            {
                throw new InvalidOperationException(
                    $"{where} pairs a {sourceType.Name} with entityRef. An entityRef carries the netId of the entity a field names, and the engine " +
                    "cannot know an integer is an identity: type the field as an EntityId (or an EntityLink<T>) so it is resolved, or send the number " +
                    "with an integer codec.");
            }

            return ColumnPath.None;
        }

        var integerCodec = IsIntegerCodec(kind);
        var floatCodec = IsFloatCodec(kind);
        if (!integerCodec && !floatCodec)
        {
            return ColumnPath.None;
        }

        if (type == typeof(float) || type == typeof(double))
        {
            if (integerCodec)
            {
                throw new InvalidOperationException(
                    $"{where} pairs a {sourceType.Name} with {CodecTokens.ToToken(kind)}: the fraction is discarded and the bytes saved are none or few. " +
                    $"Declare {(type == typeof(double) ? "Codec.F64" : "Codec.F32")} to send it whole, or Codec.Quant(min, max, bits) to send it in fewer " +
                    "bytes over a stated range and rounding.");
            }

            if (kind == CodecKind.F64)
            {
                if (type == typeof(float))
                {
                    throw new InvalidOperationException(
                        $"{where} pairs a Single with f64: four more bytes that carry nothing a float holds. Declare Codec.F32 (or Codec.Exact) to send it whole.");
                }

                return ColumnPath.ExactDouble;
            }

            return type == typeof(float) && kind == CodecKind.F32 ? ColumnPath.ExactSingle : ColumnPath.Quantizing;
        }

        if (!TryRange(type, out var source))
        {
            // A struct, a string, anything that is not one number: not this table's to judge. The projection compiler refuses it as a scalar column, and
            // a message field with a scalar codec over a struct is refused by the protocol encoder's own type check.
            return ColumnPath.None;
        }

        if (floatCodec)
        {
            // A quant over an integer's range is a real bandwidth trade — 0..1000 in 8 bits, step ≈ 3.9 — with a stated range and W1's rounding, so it is a
            // declared loss like any other quantization. The other float codecs say nothing a quant or an integer codec does not say better.
            if (kind == CodecKind.Quant && type != typeof(bool))
            {
                return ColumnPath.Quantizing;
            }

            throw new InvalidOperationException(
                $"{where} pairs a {sourceType.Name} with {CodecTokens.ToToken(kind)}: an integer converted to that codec gains nothing and can lose its " +
                $"low digits. Declare {Covering(type)} to send it exactly, a narrower integer codec with .Saturate() to send it in fewer bytes, or " +
                "Codec.Quant(min, max, bits) to send its range in fewer bits.");
        }

        var target = CodeRange(codec);
        if (kind == CodecKind.TickLo)
        {
            if (type != typeof(uint))
            {
                throw new InvalidOperationException(
                    $"{where} pairs a {sourceType.Name} with tickLo, which carries the low 16 bits of a past tick: the source must be a tick, a uint.");
            }

            return ColumnPath.ExactInteger;
        }

        if (target.Covers(source))
        {
            return ColumnPath.ExactInteger;
        }

        if (enumType != null && EnumValuesFit(enumType, target))
        {
            // The range is the declared names, which fit: no Saturate needed. Still the clamping walk, because a value cast into the enum from outside its
            // names would otherwise be masked into a different name by the pack — and that clamp is a bug worth counting.
            return ColumnPath.NarrowingInteger;
        }

        if (saturating)
        {
            return ColumnPath.NarrowingInteger;
        }

        throw new InvalidOperationException(
            $"{where} pairs a {sourceType.Name} ({source}) with {CodecTokens.ToToken(kind)} ({target}), which cannot hold " +
            $"every value. Declare {Covering(type)} to send it exactly, or mark the narrowing explicit with .Saturate(): out-of-range values then clamp " +
            "to the codec's range and every clamp is counted.");
    }

    /// <summary>Whether a codec is one of the integer kinds this table judges.</summary>
    public static bool IsIntegerCodec(CodecKind kind) => kind
        is CodecKind.Bool or CodecKind.Bits or CodecKind.U8 or CodecKind.I8 or CodecKind.U16 or CodecKind.I16 or CodecKind.U32 or CodecKind.I32
        or CodecKind.Varu or CodecKind.Vari or CodecKind.TickLo or CodecKind.U64 or CodecKind.I64 or CodecKind.Varu64 or CodecKind.Vari64;

    /// <summary>Whether a codec is one of the scalar float kinds this table judges.</summary>
    public static bool IsFloatCodec(CodecKind kind) => kind
        is CodecKind.F32 or CodecKind.F16 or CodecKind.F64 or CodecKind.Quant or CodecKind.Unorm or CodecKind.Snorm or CodecKind.Angle;

    /// <summary>The range of codes an integer codec carries, exactly.</summary>
    /// <param name="codec">An integer codec.</param>
    /// <returns>The inclusive range; 0..0 for a kind that is not an integer codec.</returns>
    public static IntegerRange CodeRange(CatalogCodec codec) => codec.Kind switch
    {
        CodecKind.Bool => new(0, 1),
        CodecKind.Bits => new(0, (1UL << codec.N) - 1),
        CodecKind.U8 => new(0, byte.MaxValue),
        CodecKind.I8 => new(sbyte.MinValue, (ulong)sbyte.MaxValue),
        CodecKind.U16 => new(0, ushort.MaxValue),
        CodecKind.I16 => new(short.MinValue, (ulong)short.MaxValue),
        CodecKind.U32 or CodecKind.Varu or CodecKind.TickLo => new(0, uint.MaxValue),
        CodecKind.I32 or CodecKind.Vari => new(int.MinValue, int.MaxValue),
        CodecKind.U64 or CodecKind.Varu64 => new(0, ulong.MaxValue),
        CodecKind.I64 or CodecKind.Vari64 => new(long.MinValue, long.MaxValue),
        _ => new(0, 0),
    };

    /// <summary>
    /// The clamp bounds of a narrowing column, as the walk's <see cref="long"/> reader yields them: a <c>u64</c> codec's top is past every <see cref="long"/>
    /// the reader produces, so it is <see cref="long.MaxValue"/> there.
    /// </summary>
    /// <param name="codec">An integer codec.</param>
    /// <returns>The inclusive bounds.</returns>
    public static (long Min, long Max) ClampRange(CatalogCodec codec)
    {
        var range = CodeRange(codec);
        return (range.Min, range.SignedMax);
    }

    /// <summary>The range of an integral CLR type; <see langword="false"/> for any other type.</summary>
    /// <param name="type">The type.</param>
    /// <param name="range">The inclusive range.</param>
    /// <returns>Whether <paramref name="type"/> is integral.</returns>
    private static bool TryRange(Type type, out IntegerRange range)
    {
        range = Type.GetTypeCode(type) switch
        {
            TypeCode.Boolean => new(0, 1),
            TypeCode.SByte => new(sbyte.MinValue, (ulong)sbyte.MaxValue),
            TypeCode.Byte => new(0, byte.MaxValue),
            TypeCode.Int16 => new(short.MinValue, (ulong)short.MaxValue),
            TypeCode.UInt16 => new(0, ushort.MaxValue),
            TypeCode.Char => new(0, char.MaxValue),
            TypeCode.Int32 => new(int.MinValue, int.MaxValue),
            TypeCode.UInt32 => new(0, uint.MaxValue),
            TypeCode.Int64 => new(long.MinValue, long.MaxValue),
            TypeCode.UInt64 => new(0, ulong.MaxValue),
            _ => default,
        };

        return range != default;
    }

    private static bool IsIntegral(Type type) => TryRange(type, out _);

    private static bool EnumValuesFit(Type enumType, IntegerRange codec)
    {
        foreach (var value in Enum.GetValuesAsUnderlyingType(enumType))
        {
            var fits = Type.GetTypeCode(value.GetType()) == TypeCode.UInt64 ? codec.Contains((ulong)value) : codec.Contains(Convert.ToInt64(value));
            if (!fits)
            {
                return false;
            }
        }

        return true;
    }

    // The codec that carries every value of an integral type exactly, for a refusal's advice.
    private static string Covering(Type type) => Type.GetTypeCode(type) switch
    {
        TypeCode.Boolean => "Codec.Bool",
        TypeCode.SByte => "Codec.I8",
        TypeCode.Byte => "Codec.U8",
        TypeCode.Int16 => "Codec.I16",
        TypeCode.UInt16 or TypeCode.Char => "Codec.U16",
        TypeCode.Int32 => "Codec.I32 (or Codec.VarInt)",
        TypeCode.UInt32 => "Codec.U32 (or Codec.VarUInt)",
        TypeCode.Int64 => "Codec.I64 (or Codec.VarInt64)",
        TypeCode.UInt64 => "Codec.U64 (or Codec.VarUInt64)",
        _ => "Codec.Exact",
    };
}
