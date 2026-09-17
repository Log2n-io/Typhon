using System.Text.Json.Serialization;

namespace Typhon.Protocol;

/// <summary>
/// The closed set of wire codecs. A client refuses at <c>WELCOME</c> a codec it does not know, unless the field declares
/// <see cref="CatalogCodec.FixedBytes"/> and can therefore be skipped.
/// </summary>
/// <remarks>
/// <para>
/// Closed, and that is the point. Server-side a codec is a <c>struct</c> type parameter of the column loop, so the JIT specializes and inlines it — a
/// specialization detail that cannot cross the wire. Naming each codec by a kind plus its parameters is what lets a TypeScript client decode any Typhon
/// server with one interpreter and no build step.
/// </para>
/// <para>
/// <b>Every member carries its wire token explicitly.</b> The tokens are the contract the TypeScript codec table is keyed by, so they are not left to a
/// naming policy: <c>varu</c>, <c>unorm</c> and <c>snorm</c> are not the camelCase of any readable C# identifier, and a policy that silently produced
/// <c>varU</c> would be a wire break no compiler could catch.
/// </para>
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<CodecKind>))]
public enum CodecKind
{
    /// <summary>One byte, or a single bit inside a <see cref="Bits"/> pack.</summary>
    [JsonStringEnumMemberName("bool")]
    Bool,

    /// <summary>Unsigned 8-bit integer.</summary>
    [JsonStringEnumMemberName("u8")]
    U8,

    /// <summary>Signed 8-bit integer.</summary>
    [JsonStringEnumMemberName("i8")]
    I8,

    /// <summary>Unsigned 16-bit integer.</summary>
    [JsonStringEnumMemberName("u16")]
    U16,

    /// <summary>Signed 16-bit integer.</summary>
    [JsonStringEnumMemberName("i16")]
    I16,

    /// <summary>Unsigned 32-bit integer.</summary>
    [JsonStringEnumMemberName("u32")]
    U32,

    /// <summary>Signed 32-bit integer.</summary>
    [JsonStringEnumMemberName("i32")]
    I32,

    /// <summary>Unsigned LEB-style varint, one to five bytes.</summary>
    [JsonStringEnumMemberName("varu")]
    Varu,

    /// <summary>Zig-zag signed varint, one to five bytes.</summary>
    [JsonStringEnumMemberName("vari")]
    Vari,

    /// <summary>IEEE 754 single.</summary>
    [JsonStringEnumMemberName("f32")]
    F32,

    /// <summary>IEEE 754 half.</summary>
    [JsonStringEnumMemberName("f16")]
    F16,

    /// <summary>Scalar quantized into <see cref="CatalogCodec.Bits"/> over [min, max]; decoding can never leave the range.</summary>
    [JsonStringEnumMemberName("quant")]
    Quant,

    /// <summary>Two-component position, quantized per axis over [min, max].</summary>
    [JsonStringEnumMemberName("pos2")]
    Pos2,

    /// <summary>Three-component position, quantized per axis over [min, max].</summary>
    [JsonStringEnumMemberName("pos3")]
    Pos3,

    /// <summary>Two-component vector at a fixed scale.</summary>
    [JsonStringEnumMemberName("vec2")]
    Vec2,

    /// <summary>Three-component vector at a fixed scale.</summary>
    [JsonStringEnumMemberName("vec3")]
    Vec3,

    /// <summary>Engine-maintained two-component motion: displacement per tick in position quanta divided by <see cref="CatalogCodec.QuantaDiv"/>.</summary>
    [JsonStringEnumMemberName("vel2")]
    Vel2,

    /// <summary>Engine-maintained three-component motion, as <see cref="Vel2"/>.</summary>
    [JsonStringEnumMemberName("vel3")]
    Vel3,

    /// <summary>Float in [0, 1] over <see cref="CatalogCodec.Bits"/>.</summary>
    [JsonStringEnumMemberName("unorm")]
    Unorm,

    /// <summary>Float in [-1, 1] over <see cref="CatalogCodec.Bits"/>.</summary>
    [JsonStringEnumMemberName("snorm")]
    Snorm,

    /// <summary>Radians over <see cref="CatalogCodec.Bits"/>.</summary>
    [JsonStringEnumMemberName("angle")]
    Angle,

    /// <summary>Quaternion, smallest-three in 32 bits.</summary>
    [JsonStringEnumMemberName("quat3")]
    Quat3,

    /// <summary>Integer of <see cref="CatalogCodec.N"/> bits, LSB first, sharing a pack named by <see cref="CatalogCodec.Pack"/>.</summary>
    [JsonStringEnumMemberName("bits")]
    Bits,

    /// <summary>Integer whose names are listed in the catalog's enum table.</summary>
    [JsonStringEnumMemberName("enum")]
    Enum,

    /// <summary>A network identity as a varint; zero is null, and it may name an entity outside the session's view.</summary>
    [JsonStringEnumMemberName("entityRef")]
    EntityRef,

    /// <summary>Length-prefixed UTF-8, bounded by <see cref="CatalogCodec.MaxBytes"/>.</summary>
    [JsonStringEnumMemberName("str")]
    Str,

    /// <summary>Exactly <see cref="CatalogCodec.N"/> raw bytes.</summary>
    [JsonStringEnumMemberName("bytes")]
    Bytes,

    /// <summary>Length-prefixed raw bytes, bounded by <see cref="CatalogCodec.MaxBytes"/>.</summary>
    [JsonStringEnumMemberName("blob")]
    Blob,

    /// <summary>A tick as a varint relative to the frame's tick.</summary>
    [JsonStringEnumMemberName("tickRel")]
    TickRel,

    /// <summary>An absolute tick carried as its low 16 bits; exact while it is less than 2^16 ticks old.</summary>
    [JsonStringEnumMemberName("tickLo")]
    TickLo,
}

/// <summary>
/// One field's codec: a <see cref="CodecKind"/> plus the parameters that kind reads. Parameters left at their default are omitted from the canonical JSON.
/// </summary>
/// <remarks>
/// Serialized as <c>{"t": "pos2", "min": [...], "max": [...], "bits": 24}</c>. Every parameter is optional because each kind reads only the ones that apply to
/// it; a decoder that does not recognise <see cref="Kind"/> can still skip the field when <see cref="FixedBytes"/> says how wide it is.
/// </remarks>
public sealed class CatalogCodec
{
    /// <summary>The codec kind. Serialized as <c>t</c>, the name the TypeScript table is keyed by.</summary>
    [JsonPropertyName("t")]
    public CodecKind Kind { get; init; }

    /// <summary>Quantization width in bits, for the kinds that quantize.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Bits { get; init; }

    /// <summary>Lower bound per axis, for <see cref="CodecKind.Quant"/>, <see cref="CodecKind.Pos2"/> and <see cref="CodecKind.Pos3"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double[] Min { get; init; }

    /// <summary>Upper bound per axis, paired with <see cref="Min"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double[] Max { get; init; }

    /// <summary>Fixed scale for <see cref="CodecKind.Vec2"/> and <see cref="CodecKind.Vec3"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Scale { get; init; }

    /// <summary>Divisor applied to position quanta for <see cref="CodecKind.Vel2"/> and <see cref="CodecKind.Vel3"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int QuantaDiv { get; init; }

    /// <summary>Bit count for <see cref="CodecKind.Bits"/>, or byte count for <see cref="CodecKind.Bytes"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int N { get; init; }

    /// <summary>The name of the bit pack this field shares, for <see cref="CodecKind.Bits"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Pack { get; init; }

    /// <summary>Upper bound on encoded length for <see cref="CodecKind.Str"/> and <see cref="CodecKind.Blob"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int MaxBytes { get; init; }

    /// <summary>
    /// Wire width in bytes when it is fixed. Present so a client that does not know <see cref="Kind"/> can skip the field instead of refusing the catalog.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int FixedBytes { get; init; }
}
