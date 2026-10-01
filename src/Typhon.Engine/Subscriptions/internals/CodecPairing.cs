using System;
using Typhon.Protocol;

namespace Typhon.Engine.Internals;

/// <summary>
/// How a projected column turns its values into wire codes — the column walk's specialization, decided once per field (design/Subscriptions/13 § 4).
/// </summary>
internal enum ColumnPath : byte
{
    /// <summary>Not a scalar column: a codec this table does not judge (a position, a vector, text, a list).</summary>
    None = 0,

    /// <summary>W1's binary64 arithmetic: the quantizing codecs, and <c>f32</c> from a <see cref="double"/>.</summary>
    Quantizing,

    /// <summary>An integral source into an integer codec whose range covers it: the value is the code, with no rounding, no NaN test and no clamp.</summary>
    ExactInteger,

    /// <summary>An integral source into a narrower integer codec: clamped to the codec's range, and every clamp counted.</summary>
    NarrowingInteger,

    /// <summary>A <see cref="float"/> into <c>f32</c>: its bits are the code, NaN canonicalized.</summary>
    ExactSingle,
}

/// <summary>
/// The source/codec pairing table (design/Subscriptions/13 § 2.3): which codec a source type may travel under, and which column path carries it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Exact by default, lossy only when declared, and never lossy for no gain.</b> An integral source in a codec whose range covers it is exact. A narrower
/// integer codec loses range, so it is legal only when the declaration says <see cref="Codec.Saturate"/> — for every width, not only 64 bits (an enum whose
/// declared values all fit is exempt: its range is its names). A quantizing codec on a <see cref="float"/> or a <see cref="double"/>, and a <c>quant</c> on an
/// integer, are declared losses of precision. What is refused is a pairing that loses data and buys nothing: a float in an integer codec (the same bytes
/// as <c>f32</c>, or more, with the fraction gone — <c>quant</c> says the same thing with a stated range and rounding), and an integer in a float codec other
/// than <c>quant</c>.
/// </para>
/// <para>
/// One table for archetype fields, owner fields, onEnter fields, event fields and command fields: the registry runs it at declaration time, the projection
/// compiler again at <c>Start</c> to choose the path. A codec the table does not judge — a position, a vector, a quaternion, text, bytes, a list, an
/// <see cref="EntityId"/> reference — returns <see cref="ColumnPath.None"/> and is left to the checks that own it.
/// </para>
/// </remarks>
internal static class CodecPairing
{
    /// <summary>Classifies a pairing, refusing it when it loses data for no gain or narrows without saying so.</summary>
    /// <param name="sourceType">The field's CLR type; an enum is judged by its underlying type and its declared values.</param>
    /// <param name="codec">The declared codec.</param>
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
                    "Declare Codec.F32 to send it whole, or Codec.Quant(min, max, bits) to send it in fewer bytes over a stated range and rounding.");
            }

            return type == typeof(float) && kind == CodecKind.F32 ? ColumnPath.ExactSingle : ColumnPath.Quantizing;
        }

        if (!TryRange(type, out var sourceMin, out var sourceMax))
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

        var (codecMin, codecMax) = CodeRange(codec);
        if (kind == CodecKind.TickLo)
        {
            if (type != typeof(uint))
            {
                throw new InvalidOperationException(
                    $"{where} pairs a {sourceType.Name} with tickLo, which carries the low 16 bits of a past tick: the source must be a tick, a uint.");
            }

            return ColumnPath.ExactInteger;
        }

        if (sourceMin >= codecMin && sourceMax <= codecMax)
        {
            return ColumnPath.ExactInteger;
        }

        if (enumType != null && EnumValuesFit(enumType, codecMin, codecMax))
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
            $"{where} pairs a {sourceType.Name} ({sourceMin}..{sourceMax}) with {CodecTokens.ToToken(kind)} ({codecMin}..{codecMax}), which cannot hold " +
            $"every value. Declare {Covering(type)} to send it exactly, or mark the narrowing explicit with .Saturate(): out-of-range values then clamp " +
            "to the codec's range and every clamp is counted.");
    }

    /// <summary>Whether a codec is one of the integer kinds this table judges.</summary>
    public static bool IsIntegerCodec(CodecKind kind) => kind
        is CodecKind.Bool or CodecKind.Bits or CodecKind.U8 or CodecKind.I8 or CodecKind.U16 or CodecKind.I16 or CodecKind.U32 or CodecKind.I32
        or CodecKind.Varu or CodecKind.Vari or CodecKind.TickLo;

    /// <summary>Whether a codec is one of the scalar float kinds this table judges.</summary>
    public static bool IsFloatCodec(CodecKind kind) => kind
        is CodecKind.F32 or CodecKind.F16 or CodecKind.Quant or CodecKind.Unorm or CodecKind.Snorm or CodecKind.Angle;

    /// <summary>The range of codes an integer codec carries, as the narrowing clamp bounds them.</summary>
    /// <param name="codec">An integer codec.</param>
    /// <returns>The inclusive range; (0, 0) for a kind that is not an integer codec.</returns>
    public static (long Min, long Max) CodeRange(CatalogCodec codec) => codec.Kind switch
    {
        CodecKind.Bool => (0L, 1L),
        CodecKind.Bits => (0L, (1L << codec.N) - 1),
        CodecKind.U8 => (0L, byte.MaxValue),
        CodecKind.I8 => (sbyte.MinValue, sbyte.MaxValue),
        CodecKind.U16 => (0L, ushort.MaxValue),
        CodecKind.I16 => (short.MinValue, short.MaxValue),
        CodecKind.U32 or CodecKind.Varu or CodecKind.TickLo => (0L, uint.MaxValue),
        CodecKind.I32 or CodecKind.Vari => (int.MinValue, int.MaxValue),
        _ => (0L, 0L),
    };

    private static bool IsIntegral(Type type) => TryRange(type, out _, out _);

    // The source's range as two Int128 bounds, so a ulong's top half and a long's bottom half compare exactly against any codec. Registration time only.
    private static bool TryRange(Type type, out Int128 min, out Int128 max)
    {
        (min, max) = Type.GetTypeCode(type) switch
        {
            TypeCode.Boolean => Range(0, 1),
            TypeCode.SByte => Range(sbyte.MinValue, sbyte.MaxValue),
            TypeCode.Byte => Range(byte.MinValue, byte.MaxValue),
            TypeCode.Int16 => Range(short.MinValue, short.MaxValue),
            TypeCode.UInt16 => Range(ushort.MinValue, ushort.MaxValue),
            TypeCode.Int32 => Range(int.MinValue, int.MaxValue),
            TypeCode.UInt32 => Range(uint.MinValue, uint.MaxValue),
            TypeCode.Int64 => Range(long.MinValue, long.MaxValue),
            TypeCode.UInt64 => Range(ulong.MinValue, ulong.MaxValue),
            _ => Range(1, 0),
        };

        return min <= max;
    }

    private static (Int128 Min, Int128 Max) Range(Int128 min, Int128 max) => (min, max);

    private static bool EnumValuesFit(Type enumType, long codecMin, long codecMax)
    {
        foreach (var value in Enum.GetValuesAsUnderlyingType(enumType))
        {
            var v = Type.GetTypeCode(value.GetType()) == TypeCode.UInt64 ? (Int128)(ulong)value : (Int128)Convert.ToInt64(value);
            if (v < codecMin || v > codecMax)
            {
                return false;
            }
        }

        return true;
    }

    // The codec that carries every value of an integral type exactly, for a refusal's advice. 64-bit sources have none yet (13 § 3 adds u64 / i64).
    private static string Covering(Type type) => Type.GetTypeCode(type) switch
    {
        TypeCode.Boolean => "Codec.Bool",
        TypeCode.SByte => "Codec.I8",
        TypeCode.Byte => "Codec.U8",
        TypeCode.Int16 => "Codec.I16",
        TypeCode.UInt16 => "Codec.U16",
        TypeCode.Int32 => "Codec.I32 (or Codec.VarInt)",
        TypeCode.UInt32 => "Codec.U32 (or Codec.VarUInt)",
        _ => "a 64-bit codec once they exist (design/Subscriptions/13 § 3; none yet)",
    };
}
