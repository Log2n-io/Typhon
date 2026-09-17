using System;
using System.Text.Json.Serialization;

namespace Typhon.Protocol;

/// <summary>
/// The closed set of wire codecs (03-wire-protocol § 4). A client refuses at <c>WELCOME</c> a codec it does not know, unless the field declares
/// <see cref="CatalogCodec.FixedBytes"/> and can therefore be skipped.
/// </summary>
/// <remarks>
/// <para>
/// Closed, and that is the point. Server-side a codec is a <c>struct</c> type parameter of the column loop, so the JIT specializes and inlines it — a
/// specialization detail that cannot cross the wire. Naming each codec by a kind plus its parameters is what lets a TypeScript client decode any Typhon
/// server with one interpreter and no build step.
/// </para>
/// <para>
/// <b>Every member has an explicit wire token</b> (<see cref="CodecTokens"/>): the tokens are the contract the TypeScript codec table is keyed by, so they are
/// not left to a naming policy — <c>varu</c> and <c>unorm</c> are not the camelCase of any readable identifier. <see cref="Unknown"/> is what a token from a
/// newer server parses to.
/// </para>
/// </remarks>
public enum CodecKind
{
    /// <summary>A token this library does not know. Decodable only by skipping <see cref="CatalogCodec.FixedBytes"/>.</summary>
    Unknown = 0,

    /// <summary>A boolean: always one bit of its section's pack (W12).</summary>
    Bool,

    /// <summary>Unsigned 8-bit integer.</summary>
    U8,

    /// <summary>Signed 8-bit integer.</summary>
    I8,

    /// <summary>Unsigned 16-bit integer, little-endian.</summary>
    U16,

    /// <summary>Signed 16-bit integer, little-endian.</summary>
    I16,

    /// <summary>Unsigned 32-bit integer, little-endian.</summary>
    U32,

    /// <summary>Signed 32-bit integer, little-endian.</summary>
    I32,

    /// <summary>Unsigned LEB128 varint, 1–5 bytes.</summary>
    Varu,

    /// <summary>Zigzag-encoded signed LEB128 varint, 1–5 bytes.</summary>
    Vari,

    /// <summary>IEEE single.</summary>
    F32,

    /// <summary>IEEE half.</summary>
    F16,

    /// <summary>A scalar quantized over [min, max) at <see cref="CatalogCodec.Bits"/> bits (W2).</summary>
    Quant,

    /// <summary>A 2D position, <see cref="Quant"/> per axis (W3).</summary>
    Pos2,

    /// <summary>A 3D position, <see cref="Quant"/> per axis (W3).</summary>
    Pos3,

    /// <summary>A signed 2D vector, <see cref="CatalogCodec.Scale"/> per step (W4).</summary>
    Vec2,

    /// <summary>A signed 3D vector, <see cref="CatalogCodec.Scale"/> per step (W4).</summary>
    Vec3,

    /// <summary>Engine-measured 2D displacement per tick, in position steps ÷ <see cref="CatalogCodec.QuantaDiv"/> (W5). Only inside a position.</summary>
    Vel2,

    /// <summary>Engine-measured 3D displacement per tick (W5). Only inside a position.</summary>
    Vel3,

    /// <summary>A value in [0, 1]: q / (2ᵇ − 1) (W6).</summary>
    Unorm,

    /// <summary>A value in [−1, 1]: max(q / (2ᵇ⁻¹ − 1), −1) (W6).</summary>
    Snorm,

    /// <summary>Radians in [−π, π), a two's-complement code (W7).</summary>
    Angle,

    /// <summary>A rotation, smallest-three in 32 bits (W8).</summary>
    Quat3,

    /// <summary>An unsigned integer of <see cref="CatalogCodec.N"/> ∈ [1, 24] bits, in its section's pack (W12).</summary>
    Bits,

    /// <summary>A netId as <c>varu</c>; 0 is null.</summary>
    EntityRef,

    /// <summary>UTF-8 text: <c>varu</c> byte length, at most <see cref="CatalogCodec.MaxBytes"/>.</summary>
    Str,

    /// <summary>Exactly <see cref="CatalogCodec.N"/> raw bytes.</summary>
    Bytes,

    /// <summary>Raw bytes: <c>varu</c> length, at most <see cref="CatalogCodec.MaxBytes"/>.</summary>
    Blob,

    /// <summary>An absolute past tick from its low 16 bits, rebuilt against the frame's tick (W9).</summary>
    TickLo,

    /// <summary>
    /// <see cref="CatalogCodec.MinCount"/>..<see cref="CatalogCodec.MaxCount"/> elements of <see cref="CatalogCodec.Of"/>: <c>varu count | element*</c> (W28).
    /// </summary>
    List,
}

/// <summary>
/// The wire tokens of <see cref="CodecKind"/>, in both directions.
/// </summary>
public static class CodecTokens
{
    private static readonly string[] Tokens =
    [
        "", "bool", "u8", "i8", "u16", "i16", "u32", "i32", "varu", "vari", "f32", "f16", "quant", "pos2", "pos3", "vec2", "vec3", "vel2", "vel3",
        "unorm", "snorm", "angle", "quat3", "bits", "entityRef", "str", "bytes", "blob", "tickLo", "list",
    ];

    /// <summary>The token of <paramref name="kind"/>.</summary>
    /// <param name="kind">A known kind.</param>
    /// <returns>The token; the empty string for <see cref="CodecKind.Unknown"/>.</returns>
    public static string ToToken(CodecKind kind) => (uint)kind < (uint)Tokens.Length ? Tokens[(int)kind] : string.Empty;

    /// <summary>The kind a token names.</summary>
    /// <param name="token">The token as it appears in a catalog.</param>
    /// <returns>The kind, or <see cref="CodecKind.Unknown"/>.</returns>
    public static CodecKind FromToken(string token)
    {
        for (var i = 1; i < Tokens.Length; i++)
        {
            if (string.Equals(Tokens[i], token, StringComparison.Ordinal))
            {
                return (CodecKind)i;
            }
        }

        return CodecKind.Unknown;
    }
}

/// <summary>
/// A codec: a <see cref="CodecKind"/> and the parameters that kind reads. Parameters a kind does not read stay at their default and are not written.
/// </summary>
/// <remarks>
/// Nothing here names a CLR type or a storage offset. A decoder that does not recognise <see cref="Kind"/> can still skip the field when
/// <see cref="FixedBytes"/> says how wide it is — which only a codec newer than this library carries.
/// </remarks>
public sealed class CatalogCodec
{
    /// <summary>
    /// The codec's wire token, serialized as <c>t</c> — the name the TypeScript table is keyed by. Kept as the token rather than the enum so a codec newer
    /// than this library survives a parse and a re-serialization unchanged.
    /// </summary>
    [JsonPropertyName("t")]
    public string Type { get; init; }

    /// <summary>The codec kind <see cref="Type"/> names; <see cref="CodecKind.Unknown"/> for a token this library does not know.</summary>
    [JsonIgnore]
    public CodecKind Kind
    {
        get => CodecTokens.FromToken(Type);
        init => Type = CodecTokens.ToToken(value);
    }

    /// <summary>Width in bits for the quantizing kinds: 8, 16, 24 or 32.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Bits { get; init; }

    /// <summary>Lower bound per axis (inclusive): one value for <see cref="CodecKind.Quant"/>, two or three for a position.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double[] Min { get; init; }

    /// <summary>Upper bound per axis (exclusive), paired with <see cref="Min"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double[] Max { get; init; }

    /// <summary>The size of one step, for <see cref="CodecKind.Vec2"/> and <see cref="CodecKind.Vec3"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Scale { get; init; }

    /// <summary>Divisor applied to the position step, for <see cref="CodecKind.Vel2"/> and <see cref="CodecKind.Vel3"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int QuantaDiv { get; init; }

    /// <summary>Bit count for <see cref="CodecKind.Bits"/>, or byte count for <see cref="CodecKind.Bytes"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int N { get; init; }

    /// <summary>Upper bound on encoded length for <see cref="CodecKind.Str"/> and <see cref="CodecKind.Blob"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int MaxBytes { get; init; }

    /// <summary>The element codec of a <see cref="CodecKind.List"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public CatalogCodec Of { get; init; }

    /// <summary>The fewest elements a <see cref="CodecKind.List"/> may carry.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int MinCount { get; init; }

    /// <summary>The most elements a <see cref="CodecKind.List"/> may carry, at most 255.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int MaxCount { get; init; }

    /// <summary>
    /// Wire width in bytes, declared only by a codec newer than the protocol minor a client may speak, so that client can skip the field instead of refusing
    /// the catalog.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int FixedBytes { get; init; }
}
