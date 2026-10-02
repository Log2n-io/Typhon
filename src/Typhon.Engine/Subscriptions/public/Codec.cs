using JetBrains.Annotations;
using System;
using System.Text;
using Typhon.Protocol;

namespace Typhon.Engine;

/// <summary>
/// A wire codec, as an application names it in a projection, an event or a command. One value of the closed set of
/// <see cref="Typhon.Protocol.CodecKind"/>, plus the parameters that kind reads.
/// </summary>
/// <remarks>
/// <para>
/// <b>A declaration, not an encoder.</b> Nothing here encodes anything: the value records what the field should look like on the wire, and the projection
/// compiler turns it into the specialized column loop that does the work. That is why the type is a small immutable value with no behaviour — it is copied
/// into a plan at <c>Start</c> and never touched again.
/// </para>
/// <para>
/// <b>The parameters an application cannot know are left for the engine to fill.</b> A position's bounds come from the spatial grid
/// (<see cref="DatabaseEngine.ConfigureSpatialGrid"/>), and a velocity's width from the archetype's <see cref="MotionBuilder.Teleport"/> speed and the
/// runtime's tick ladder. Declaring them at the call site would be a second, drifting copy of a number the engine already owns.
/// </para>
/// <para>
/// <b>Equality is by declaration, not by reference</b>, so a test — or a catalog comparison — can assert that what came out of the registry is what went
/// in. It compares the canonical text of <see cref="ToString"/>, which allocates: compare codecs at registration, never per entity.
/// </para>
/// </remarks>
[PublicAPI]
public readonly struct Codec : IEquatable<Codec>
{
    // The protocol-level description. Null only for default(Codec), which is what an application gets by forgetting the argument, and what IsDeclared reports.
    private readonly CatalogCodec _catalog;

    // The CLR enum whose names become the catalog's value set (W13). Null unless Enum<T> built this codec. Held as the type rather than the names so the
    // catalog builder decides how to spell them — nothing here enumerates it.
    private readonly Type _enumType;

    // Codec.Exact: no wire codec yet — the stored type's exact one, resolved where the type is known (CodecPairing.Resolve, 13 § 2.1).
    private readonly bool _exact;

    private Codec(CatalogCodec catalog, Type enumType, bool saturating, bool exact = false)
    {
        _catalog = catalog;
        _enumType = enumType;
        Saturating = saturating;
        _exact = exact;
    }

    private Codec(CatalogCodec catalog) : this(catalog, null, false)
    {
    }

    /// <summary>Whether this value names a codec at all. <see langword="false"/> for <c>default(Codec)</c>.</summary>
    public bool IsDeclared => _catalog != null || _exact;

    /// <summary>Whether this is <see cref="Exact"/>, not yet resolved against the field's stored type.</summary>
    internal bool IsExact => _exact && _catalog == null;

    /// <summary>
    /// The codec's wire token — <c>u8</c>, <c>varu</c>, <c>bits</c>, <c>unorm</c> … — as the catalog spells it. The empty string when
    /// <see cref="IsDeclared"/> is <see langword="false"/>.
    /// </summary>
    public string Token => _catalog?.Type ?? string.Empty;

    /// <summary>
    /// Width in bits for the quantizing kinds; the bit count for <see cref="Bits"/>; 0 when the kind reads neither, or when the engine derives it.
    /// </summary>
    public int Width => _catalog == null ? 0 : _catalog.Kind == CodecKind.Bits ? _catalog.N : _catalog.Bits;

    /// <summary>The name of the enum whose value set this codec carries (W13), or <see langword="null"/> when it carries none.</summary>
    public string EnumName => _enumType?.Name;

    /// <summary>
    /// Whether a source value may be narrowed into this integer codec by clamping. Set only by <see cref="Saturate"/>, and required of any field whose
    /// integral source has a range the codec cannot hold — an <c>int</c> in a <c>u8</c> as much as a <c>long</c> in a <c>varu</c>: the narrowing is never
    /// implicit (design/Subscriptions/13 § 2.3).
    /// </summary>
    public bool Saturating { get; }

    /// <summary>The protocol description this codec carries. Internal: an application declares codecs, it does not handle catalog objects.</summary>
    internal CatalogCodec Catalog => _catalog;

    /// <summary>The CLR enum backing <see cref="EnumName"/>, for the catalog builder's value set.</summary>
    internal Type EnumType => _enumType;

    /// <summary>Unsigned 8-bit integer.</summary>
    public static Codec U8 => new(new CatalogCodec { Kind = CodecKind.U8 });

    /// <summary>Signed 8-bit integer.</summary>
    public static Codec I8 => new(new CatalogCodec { Kind = CodecKind.I8 });

    /// <summary>Unsigned 16-bit integer, little-endian.</summary>
    public static Codec U16 => new(new CatalogCodec { Kind = CodecKind.U16 });

    /// <summary>Signed 16-bit integer, little-endian.</summary>
    public static Codec I16 => new(new CatalogCodec { Kind = CodecKind.I16 });

    /// <summary>Unsigned 32-bit integer, little-endian.</summary>
    public static Codec U32 => new(new CatalogCodec { Kind = CodecKind.U32 });

    /// <summary>Signed 32-bit integer, little-endian.</summary>
    public static Codec I32 => new(new CatalogCodec { Kind = CodecKind.I32 });

    /// <summary>Unsigned LEB128 varint, 1–5 bytes. The cheapest codec for a value that is usually small and occasionally large.</summary>
    public static Codec VarUInt => new(new CatalogCodec { Kind = CodecKind.Varu });

    /// <summary>Zigzag-encoded signed LEB128 varint, 1–5 bytes.</summary>
    public static Codec VarInt => new(new CatalogCodec { Kind = CodecKind.Vari });

    /// <summary>Unsigned 64-bit integer, little-endian (W32): an id or a balance above 2³², exact on every client.</summary>
    public static Codec U64 => new(new CatalogCodec { Kind = CodecKind.U64 });

    /// <summary>Signed 64-bit integer, little-endian two's complement (W32).</summary>
    public static Codec I64 => new(new CatalogCodec { Kind = CodecKind.I64 });

    /// <summary>Unsigned LEB128 varint of a 64-bit value, 1–10 bytes (W32): the cheapest exact codec for a 64-bit value that is usually small.</summary>
    public static Codec VarUInt64 => new(new CatalogCodec { Kind = CodecKind.Varu64 });

    /// <summary>Zigzag-encoded signed LEB128 varint of a 64-bit value, 1–10 bytes (W32).</summary>
    public static Codec VarInt64 => new(new CatalogCodec { Kind = CodecKind.Vari64 });

    /// <summary>IEEE double, little-endian (W32): a <see cref="double"/> sent whole.</summary>
    public static Codec F64 => new(new CatalogCodec { Kind = CodecKind.F64 });

    /// <summary>
    /// The exact codec of whatever the field stores (13 § 2.1), resolved where the field's type is known: <c>u64</c> for a <see cref="ulong"/>,
    /// <c>i64</c> for a <see cref="long"/>, <c>f64</c> for a <see cref="double"/>, <c>f32 × 3</c> for a <c>Point3F</c>, <c>i32</c> for an
    /// <see cref="int"/>, a pack of the narrowest width for an enum. "Exact" is one word whatever the type; a field that must be narrower declares the
    /// lossy codec it wants instead.
    /// </summary>
    public static Codec Exact => new(null, null, false, exact: true);

    /// <summary>IEEE single.</summary>
    public static Codec F32 => new(new CatalogCodec { Kind = CodecKind.F32 });

    /// <summary>IEEE half. Saturates at 65 504 — a duration in milliseconds fits, the same duration in microseconds does not.</summary>
    public static Codec F16 => new(new CatalogCodec { Kind = CodecKind.F16 });

    /// <summary>A boolean: always one bit of its section's implicit pack.</summary>
    public static Codec Bool => new(new CatalogCodec { Kind = CodecKind.Bool });

    /// <summary>A netId reference to another entity, as <c>varu</c>; 0 is null. A client can only resolve one the server has shown it.</summary>
    public static Codec EntityRef => new(new CatalogCodec { Kind = CodecKind.EntityRef });

    /// <summary>An absolute past tick sent as its low 16 bits and rebuilt against the frame's tick.</summary>
    public static Codec TickLo => new(new CatalogCodec { Kind = CodecKind.TickLo });

    /// <summary>A rotation, smallest-three in 32 bits.</summary>
    public static Codec Quat3 => new(new CatalogCodec { Kind = CodecKind.Quat3 });

    /// <summary>
    /// A 2D world position, realm-framed (<c>typhon.3</c>, SUB-30): its width and bounds are the session's realm's, sent in the <c>REALM</c> block, so the
    /// catalog carries the kind alone and a position declared here quantizes exactly as the grid's own.
    /// </summary>
    public static Codec Pos2 => new(new CatalogCodec { Kind = CodecKind.Pos2 });

    /// <summary>A 3D world position, realm-framed. <inheritdoc cref="Pos2" path="/summary"/></summary>
    public static Codec Pos3 => new(new CatalogCodec { Kind = CodecKind.Pos3 });

    /// <summary>
    /// The engine-measured 2D displacement of a motion segment. Its width and its divisor are derived at registration from the archetype's
    /// <see cref="MotionBuilder.Teleport"/> speed and the runtime's largest allowed tick multiplier, so nothing here carries them.
    /// </summary>
    public static Codec Vel2 => new(new CatalogCodec { Kind = CodecKind.Vel2 });

    /// <summary>The engine-measured 3D displacement of a motion segment. <inheritdoc cref="Vel2" path="/summary"/></summary>
    public static Codec Vel3 => new(new CatalogCodec { Kind = CodecKind.Vel3 });

    /// <summary>
    /// A realm's default position width: 24 bits per axis, sub-millimetre over a 16 km world and three bytes per axis. The width is the realm's
    /// (<c>REALM.posBits</c>, 12-realms § 5.5), never a codec's.
    /// </summary>
    public const int DefaultPositionBits = 24;

    /// <summary>An unsigned integer of <paramref name="n"/> bits, carried in its section's implicit pack.</summary>
    /// <param name="n">Width in bits, in [1, 24].</param>
    /// <returns>The codec.</returns>
    public static Codec Bits(int n)
    {
        if (n is < 1 or > ProtocolConstants.MaxPackedBits)
        {
            throw new ArgumentOutOfRangeException(nameof(n), n, $"Codec.Bits(n) takes 1 to {ProtocolConstants.MaxPackedBits} bits.");
        }

        return new Codec(new CatalogCodec { Kind = CodecKind.Bits, N = n });
    }

    /// <summary>
    /// An integer codec of <paramref name="bits"/> bits carrying <typeparamref name="TEnum"/>'s value set as a catalog attribute. An enum is an integer plus a
    /// list of names, never a codec of its own, so a client that does not know the names still decodes the number.
    /// </summary>
    /// <typeparam name="TEnum">The enum whose names travel in the catalog.</typeparam>
    /// <param name="bits">Width in bits, in [1, 24]. Wide enough for the whole value set — adding a name past that width re-lays the pack.</param>
    /// <returns>The codec.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The enum has more names than <paramref name="bits"/> bits can index: W13's <c>|names| ≤ 2ⁿ</c>.
    /// </exception>
    /// <remarks>
    /// <b>The width is checked here, at the declaring call, and not at the catalog.</b> W13 lets a value past the end of the name list decode as a bare
    /// integer, so an under-wide field is not a decode failure on either side — it is a set of names the client silently never sees, discovered by a player
    /// rather than by a test. The count is a compile-time fact about the type; refusing it where the author wrote the number is the only place the message
    /// can name both.
    /// </remarks>
    public static Codec Enum<TEnum>(int bits) where TEnum : struct, System.Enum
    {
        var codec = Bits(bits);
        var names = System.Enum.GetNames<TEnum>();
        var capacity = 1 << bits;
        if (names.Length > capacity)
        {
            throw new ArgumentOutOfRangeException(nameof(bits), bits,
                $"Codec.Enum<{typeof(TEnum).Name}>({bits}) carries {names.Length} names and {bits} bit(s) index {capacity} values (W13). " +
                $"'{typeof(TEnum).Name}' needs {BitsNeededFor(names.Length)} bits.");
        }

        return new Codec(codec._catalog, typeof(TEnum), false);
    }

    /// <summary>A scalar quantized over <c>[min, max)</c>.</summary>
    /// <param name="min">Lower bound, inclusive.</param>
    /// <param name="max">Upper bound, exclusive. Must be above <paramref name="min"/>.</param>
    /// <param name="bits">Width in bits: 8, 16, 24 or 32.</param>
    /// <returns>The codec.</returns>
    public static Codec Quant(double min, double max, int bits)
    {
        if (!double.IsFinite(min) || !double.IsFinite(max) || max <= min)
        {
            throw new ArgumentException($"Codec.Quant needs a finite range with max above min; got [{min}, {max}).", nameof(max));
        }

        CheckQuantizingBits(bits, nameof(bits));
        return new Codec(new CatalogCodec { Kind = CodecKind.Quant, Bits = bits, Min = [min], Max = [max] });
    }

    /// <summary>A value in <c>[0, 1]</c> — a fraction, a ratio, a normalized health bar.</summary>
    /// <param name="bits">Width in bits: 8, 16, 24 or 32.</param>
    /// <returns>The codec.</returns>
    public static Codec Unorm(int bits)
    {
        CheckQuantizingBits(bits, nameof(bits));
        return new Codec(new CatalogCodec { Kind = CodecKind.Unorm, Bits = bits });
    }

    /// <summary>A value in <c>[−1, 1]</c>.</summary>
    /// <param name="bits">Width in bits: 8, 16, 24 or 32.</param>
    /// <returns>The codec.</returns>
    public static Codec Snorm(int bits)
    {
        CheckQuantizingBits(bits, nameof(bits));
        return new Codec(new CatalogCodec { Kind = CodecKind.Snorm, Bits = bits });
    }

    /// <summary>Radians in <c>[−π, π)</c> as a two's-complement code, so the wrap needs no special case on either side.</summary>
    /// <param name="bits">Width in bits: 8, 16, 24 or 32.</param>
    /// <returns>The codec.</returns>
    public static Codec Angle(int bits)
    {
        CheckQuantizingBits(bits, nameof(bits));
        return new Codec(new CatalogCodec { Kind = CodecKind.Angle, Bits = bits });
    }

    /// <summary>
    /// A signed 2D vector in steps of <paramref name="scale"/>. Unlike <see cref="Vel2"/> this is an application quantity, so it carries its own scale.
    /// </summary>
    /// <param name="scale">The size of one step, above zero.</param>
    /// <param name="bits">Width in bits per axis: 8, 16, 24 or 32.</param>
    /// <returns>The codec.</returns>
    public static Codec Vec2(double scale, int bits) => Vector(CodecKind.Vec2, scale, bits);

    /// <summary>A signed 3D vector in steps of <paramref name="scale"/>.</summary>
    /// <param name="scale">The size of one step, above zero.</param>
    /// <param name="bits">Width in bits per axis: 8, 16, 24 or 32.</param>
    /// <returns>The codec.</returns>
    public static Codec Vec3(double scale, int bits) => Vector(CodecKind.Vec3, scale, bits);

    /// <summary>UTF-8 text with a <c>varu</c> byte length.</summary>
    /// <param name="maxBytes">The largest encoded length accepted, above zero.</param>
    /// <returns>The codec.</returns>
    public static Codec Str(int maxBytes) => Bounded(CodecKind.Str, maxBytes, nameof(maxBytes));

    /// <summary>Raw bytes with a <c>varu</c> length.</summary>
    /// <param name="maxBytes">The largest encoded length accepted, above zero.</param>
    /// <returns>The codec.</returns>
    public static Codec Blob(int maxBytes) => Bounded(CodecKind.Blob, maxBytes, nameof(maxBytes));

    /// <summary>Exactly <paramref name="n"/> raw bytes, with no length on the wire.</summary>
    /// <param name="n">The byte count, above zero.</param>
    /// <returns>The codec.</returns>
    public static Codec Bytes(int n)
    {
        if (n <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(n), n, "Codec.Bytes(n) needs a positive byte count.");
        }

        return new Codec(new CatalogCodec { Kind = CodecKind.Bytes, N = n });
    }

    /// <summary>
    /// The codec a replication attribute declares for a field of type <typeparamref name="TField"/> (design/Subscriptions/11 § 5): <paramref name="kind"/>
    /// through the factory of that name, validated as that factory validates it, or — for <see cref="CodecKind.Unknown"/> — the codec the type travels
    /// under by default, the one an undeclared command or event field takes. The generator emits it; an application writes the named factory.
    /// </summary>
    /// <typeparam name="TField">The field's type: an enum carries its names on <see cref="CodecKind.Bits"/> and on a default.</typeparam>
    /// <param name="kind">The codec.</param>
    /// <param name="bits">Bits, for the kinds that take them; 0 only where the kind has a default width (a position).</param>
    /// <param name="min">A <c>Quant</c>'s lower bound.</param>
    /// <param name="max">A <c>Quant</c>'s upper bound.</param>
    /// <param name="scale">A <c>Vec2/3</c>'s step.</param>
    /// <param name="maxBytes">A <c>Str</c> or <c>Blob</c>'s cap, or the length of <c>Bytes</c>.</param>
    /// <param name="saturate">Whether the codec clamps out-of-range values (<see cref="Saturate"/>).</param>
    /// <returns>The codec.</returns>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static Codec Declared<TField>(CodecKind kind, int bits = 0, double min = 0, double max = 0, double scale = 0, int maxBytes = 0,
        bool saturate = false)
    {
        var codec = Declared(typeof(TField), kind, bits, min, max, scale, maxBytes);
        return saturate ? codec.Saturate() : codec;
    }

    /// <summary>The non-generic core of <see cref="Declared{TField}"/>, for a message field known only by its <see cref="Type"/>.</summary>
    internal static Codec Declared(Type fieldType, CodecKind kind, int bits, double min, double max, double scale, int maxBytes)
    {
        switch (kind)
        {
            case CodecKind.Unknown:
                // An inline string is its text, capacity less its terminator (13 § 2.1). A message field resolves the same way, and the command and event
                // binders refuse it there: messages carry text as Utf8Text, so a String64 in a message is an error caught at Start either way.
                var textCapacity = CodecPairing.TextCapacityOf(fieldType);
                if (textCapacity > 0)
                {
                    return Str(textCapacity - 1);
                }

                var byType = MessageContract.DefaultCodec(fieldType, out var enumType);
                if (!byType.IsDeclared)
                {
                    throw new ArgumentException(
                        $"A {fieldType.Name} field has no default codec — a 64-bit integer, a double or a struct must say how it travels (01 § 2): name a " +
                        "CodecKind in its attribute.", nameof(kind));
                }

                return enumType == null ? byType : new Codec(byType._catalog, enumType, false);
            case CodecKind.Bool: return Bool;
            case CodecKind.U8: return U8;
            case CodecKind.I8: return I8;
            case CodecKind.U16: return U16;
            case CodecKind.I16: return I16;
            case CodecKind.U32: return U32;
            case CodecKind.I32: return I32;
            case CodecKind.U64: return U64;
            case CodecKind.I64: return I64;
            case CodecKind.Varu64: return VarUInt64;
            case CodecKind.Vari64: return VarInt64;
            case CodecKind.F64: return F64;
            case CodecKind.Varu: return VarUInt;
            case CodecKind.Vari: return VarInt;
            case CodecKind.F32: return F32;
            case CodecKind.F16: return F16;
            case CodecKind.EntityRef: return EntityRef;
            case CodecKind.TickLo: return TickLo;
            case CodecKind.Quat3: return Quat3;
            case CodecKind.Pos2: return bits == 0 ? Pos2 : throw RealmWidth(kind);
            case CodecKind.Pos3: return bits == 0 ? Pos3 : throw RealmWidth(kind);
            case CodecKind.Quant: return Quant(min, max, RequiredBits(kind, bits));
            case CodecKind.Unorm: return Unorm(RequiredBits(kind, bits));
            case CodecKind.Snorm: return Snorm(RequiredBits(kind, bits));
            case CodecKind.Angle: return Angle(RequiredBits(kind, bits));
            case CodecKind.Vec2: return Vec2(scale, RequiredBits(kind, bits));
            case CodecKind.Vec3: return Vec3(scale, RequiredBits(kind, bits));
            case CodecKind.Str: return Str(maxBytes);
            case CodecKind.Blob: return Blob(maxBytes);
            case CodecKind.Bytes: return Bytes(maxBytes);
            case CodecKind.Bits:
                var packed = Bits(RequiredBits(kind, bits));
                if (!fieldType.IsEnum)
                {
                    return packed;
                }

                var names = System.Enum.GetNames(fieldType).Length;
                if (names > 1 << bits)
                {
                    throw new ArgumentOutOfRangeException(nameof(bits), bits,
                        $"CodecKind.Bits with {bits} bit(s) indexes {1 << bits} values and '{fieldType.Name}' has {names} names (W13). It needs " +
                        $"{BitsNeededFor(names)} bits.");
                }

                return new Codec(packed._catalog, fieldType, false);
            default:
                throw new NotSupportedException(
                    $"CodecKind.{kind} cannot be declared by an attribute: it needs arguments one cannot carry (a list's element codec) or exists only " +
                    "inside a position. Declare the field in the builder call.");
        }
    }

    private static int RequiredBits(CodecKind kind, int bits)
        => bits != 0 ? bits : throw new ArgumentException($"CodecKind.{kind} needs its width: set Bits on the attribute.", nameof(bits));

    private static ArgumentException RealmWidth(CodecKind kind) =>
        new($"A {CodecTokens.ToToken(kind)} takes its width from the session's realm (typhon.3, REALM posBits); declare it without bits.");

    private static int CheckedBits(CodecKind kind, int bits)
    {
        CheckQuantizingBits(bits, nameof(bits));
        return bits;
    }

    /// <summary>A counted sequence of <paramref name="of"/>: a <c>varu</c> count, then that many elements.</summary>
    /// <param name="of">The element codec.</param>
    /// <param name="minCount">The fewest elements accepted.</param>
    /// <param name="maxCount">The most elements accepted, at most 255.</param>
    /// <returns>The codec.</returns>
    public static Codec List(Codec of, int minCount, int maxCount)
    {
        if (!of.IsDeclared)
        {
            throw new ArgumentException("Codec.List needs an element codec.", nameof(of));
        }

        if (of.IsExact)
        {
            throw new ArgumentException("Codec.List needs a declared element codec: Codec.Exact resolves against a field's type, and an element has none.",
                nameof(of));
        }

        if (of._catalog.Count > 1)
        {
            throw new ArgumentException($"A list element is one value: '{of}' carries a count (W33). Declare a list of the scalar instead.", nameof(of));
        }

        if (minCount < 0 || minCount > maxCount || maxCount > ProtocolConstants.MaxListCount)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCount), maxCount,
                $"Codec.List needs 0 <= minCount <= maxCount <= {ProtocolConstants.MaxListCount}.");
        }

        return new Codec(new CatalogCodec { Kind = CodecKind.List, Of = of._catalog, MinCount = minCount, MaxCount = maxCount });
    }

    /// <summary>
    /// Marks this integer codec as the explicit narrowing of a wider source: out-of-range values clamp to the codec's range, and a projected field counts
    /// every clamp. A field whose integral source the codec cannot hold — an <c>int</c> in a <c>u8</c>, a <c>long</c> in a <c>varu</c> — is refused
    /// without it, because a silent truncation is a bug that only shows up once a value is large.
    /// </summary>
    /// <returns>The same codec, marked saturating.</returns>
    public Codec Saturate()
    {
        if (_catalog == null)
        {
            throw new InvalidOperationException("Saturate() needs a codec to narrow into.");
        }

        if (!IsIntegerKind(_catalog.Kind))
        {
            throw new InvalidOperationException($"Saturate() applies to an integer codec; '{Token}' is not one.");
        }

        return new Codec(_catalog, _enumType, true);
    }

    /// <summary>
    /// This scalar codec repeated <paramref name="count"/> times (W33): a point, a box, a sphere or a quaternion travels as its components, in its shape's
    /// order. A shape field (<c>Point3F</c>, <c>AABB2D</c>…) gets its count from its type; declaring one is only needed to state it, and must agree.
    /// </summary>
    /// <param name="count">2 to 16.</param>
    /// <returns>The counted codec.</returns>
    public Codec Count(int count)
    {
        if (_catalog == null)
        {
            throw new InvalidOperationException("Count(n) needs a codec to repeat; Codec.Exact takes its count from the field's type.");
        }

        if (!CatalogValidator.TakesCount(_catalog.Kind))
        {
            throw new InvalidOperationException(
                $"Count(n) applies to a byte-aligned scalar codec; '{Token}' is not one — a bool, bits, text, a reference or a vector codec is a shape of its own.");
        }

        if (count is < 2 or > ProtocolConstants.MaxCount)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, $"Count(n) takes 2 to {ProtocolConstants.MaxCount}; one value is the codec itself.");
        }

        return new Codec(_catalog.WithCount(count), _enumType, Saturating);
    }

    /// <summary>The same codec with its catalog form replaced: the resolution of <see cref="Exact"/> and of a shape's count.</summary>
    internal Codec WithCatalog(CatalogCodec catalog, Type enumType = null) => new(catalog, enumType ?? _enumType, Saturating);

    /// <summary>
    /// The codec's canonical text: its token, its parameters, its enum and its saturation — everything that distinguishes one declaration from another.
    /// </summary>
    /// <returns>The canonical text, or <c>"(none)"</c> when nothing was declared.</returns>
    public override string ToString()
    {
        if (_catalog == null)
        {
            return _exact ? "exact" : "(none)";
        }

        var text = new StringBuilder(_catalog.Type);
        switch (_catalog.Kind)
        {
            case CodecKind.Bits:
            case CodecKind.Bytes:
                text.Append('{').Append(_catalog.N).Append('}');
                break;
            case CodecKind.Str:
            case CodecKind.Blob:
                text.Append('{').Append(_catalog.MaxBytes).Append('}');
                break;
            case CodecKind.Quant:
                text.Append('{').Append(_catalog.Min[0]).Append(',').Append(_catalog.Max[0]).Append(',').Append(_catalog.Bits).Append('}');
                break;
            case CodecKind.Vec2:
            case CodecKind.Vec3:
                text.Append('{').Append(_catalog.Scale).Append(',').Append(_catalog.Bits).Append('}');
                break;
            case CodecKind.List:
                text.Append('{').Append(Element).Append(',').Append(_catalog.MinCount).Append("..").Append(_catalog.MaxCount).Append('}');
                break;
            default:
                if (_catalog.Bits != 0)
                {
                    text.Append('{').Append(_catalog.Bits).Append('}');
                }

                break;
        }

        if (_catalog.Count > 1)
        {
            text.Append('x').Append(_catalog.Count);
        }

        if (_enumType != null)
        {
            text.Append(':').Append(_enumType.Name);
        }

        if (Saturating)
        {
            text.Append('!');
        }

        return text.ToString();
    }

    /// <summary>The element codec of a list, or <c>default</c> when this is not a list.</summary>
    public Codec Element => _catalog?.Of == null ? default : new Codec(_catalog.Of);

    /// <summary>Whether two codecs declare the same thing.</summary>
    /// <param name="other">The other codec.</param>
    /// <returns><see langword="true"/> when their canonical texts match.</returns>
    public bool Equals(Codec other) => string.Equals(ToString(), other.ToString(), StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object obj) => obj is Codec other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => ToString().GetHashCode(StringComparison.Ordinal);

    /// <summary>Whether two codecs declare the same thing.</summary>
    /// <param name="left">The left codec.</param>
    /// <param name="right">The right codec.</param>
    /// <returns><see langword="true"/> when their canonical texts match.</returns>
    public static bool operator ==(Codec left, Codec right) => left.Equals(right);

    /// <summary>Whether two codecs declare different things.</summary>
    /// <param name="left">The left codec.</param>
    /// <param name="right">The right codec.</param>
    /// <returns><see langword="true"/> when their canonical texts differ.</returns>
    public static bool operator !=(Codec left, Codec right) => !left.Equals(right);

    private static Codec Vector(CodecKind kind, double scale, int bits)
    {
        if (!double.IsFinite(scale) || scale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "A vector codec needs a positive, finite step size.");
        }

        CheckQuantizingBits(bits, nameof(bits));
        return new Codec(new CatalogCodec { Kind = kind, Scale = scale, Bits = bits });
    }

    private static Codec Bounded(CodecKind kind, int maxBytes, string parameterName)
    {
        if (maxBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, maxBytes, "A length-prefixed codec needs a positive byte cap.");
        }

        return new Codec(new CatalogCodec { Kind = kind, MaxBytes = maxBytes });
    }

    // ⌈log₂(count)⌉, by doubling: the message tells the author the number to write instead of making them compute it. Never in a hot path.
    private static int BitsNeededFor(int count)
    {
        var bits = 1;
        var capacity = 2;
        while (capacity < count && bits < ProtocolConstants.MaxPackedBits)
        {
            capacity <<= 1;
            bits++;
        }

        return bits;
    }

    private static void CheckQuantizingBits(int bits, string parameterName)
    {
        if (bits is not (8 or 16 or 24 or 32))
        {
            throw new ArgumentOutOfRangeException(parameterName, bits, "A quantizing codec takes 8, 16, 24 or 32 bits.");
        }
    }

    private static bool IsIntegerKind(CodecKind kind) => kind
        is CodecKind.Bool or CodecKind.U8 or CodecKind.I8 or CodecKind.U16 or CodecKind.I16 or CodecKind.U32 or CodecKind.I32
        or CodecKind.Varu or CodecKind.Vari or CodecKind.Bits or CodecKind.U64 or CodecKind.I64 or CodecKind.Varu64 or CodecKind.Vari64;
}
