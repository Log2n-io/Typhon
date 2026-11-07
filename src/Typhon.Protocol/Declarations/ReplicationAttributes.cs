using System;

namespace Typhon.Protocol;

// Replication declared on the data (design/Subscriptions/11 § 5). Inert metadata: the source generator in Typhon.Generators turns the archetype attributes
// into ordinary builder calls and the message attributes into codec descriptors; nothing here is read at run time by reflection. They live in Typhon.Protocol
// (Q4) because the codec kinds do, and because a command's contracts assembly references nothing else (01 § 7).

/// <summary>
/// Opts an archetype into replication by attributes (11 § 5.4): its components' <see cref="ReplicateAttribute"/>, <see cref="OnEnterAttribute"/>,
/// <see cref="OwnerAttribute"/>, <see cref="FractionAttribute"/> and <see cref="HeadingAttribute"/> fields, and its <see cref="MotionAttribute"/> or
/// <see cref="PositionAttribute"/> component, become its projection. Without it no attribute of its components replicates it — a component shared with another
/// archetype does not replicate that one silently. A builder <c>subs.Archetype&lt;T&gt;(…)</c> call replaces the attributes entirely.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ReplicatedAttribute : Attribute
{
    /// <summary>Whether the archetype is static — sent once on enter, never updated (<c>subs.Static&lt;T&gt;</c>).</summary>
    public bool Static { get; set; }
}

/// <summary>
/// On an archetype's <c>Comp&lt;T&gt;</c> field: the component whose spatial field moves the entity, replicated as motion segments (<c>.Motion(comp, …)</c>).
/// Per archetype, because a spaceship and a walking NPC do not deserve the same tolerance.
/// </summary>
[AttributeUsage(AttributeTargets.Field, Inherited = false)]
public sealed class MotionAttribute : Attribute
{
    /// <summary>The extrapolation error that forces a new segment, metres; NaN keeps the builder's default (5 cm).</summary>
    public double ToleranceM { get; set; } = double.NaN;

    /// <summary>The speed past which a jump is a teleport, metres per second; NaN for none.</summary>
    public double TeleportMps { get; set; } = double.NaN;

    /// <summary>The heartbeat bounding velocity-rounding drift, seconds; NaN keeps the builder's default (5 s).</summary>
    public double MaxAgeS { get; set; } = double.NaN;

    /// <summary>
    /// The name of a <see cref="byte"/> (or <see cref="bool"/>) field of this component, non-zero while the entity stands on the ground (#1616,
    /// <c>.Grounded</c>): grounded segments then omit the <see cref="Up"/> axis and the client derives the height from its terrain. <see langword="null"/>
    /// for an ordinary motion.
    /// </summary>
    public string Grounded { get; set; }

    /// <summary>For a <see cref="Grounded"/> motion, the vertical axis: 0, 1 (the default, y up) or 2.</summary>
    public int Up { get; set; } = 1;
}

/// <summary>
/// On an archetype's <c>Comp&lt;T&gt;</c> field: the component whose spatial field is the entity's position, sent on enter (<c>.Position(comp)</c>).
/// </summary>
[AttributeUsage(AttributeTargets.Field, Inherited = false)]
public sealed class PositionAttribute : Attribute
{
}

/// <summary>The codec of a replicated field, named rather than defaulted from its type; the numeric properties parameterize it.</summary>
public abstract class CodecDeclarationAttribute : Attribute
{
    /// <summary>A codec named by kind alone, or <see cref="CodecKind.Unknown"/> to default it from the field's type.</summary>
    protected CodecDeclarationAttribute(CodecKind kind) => Kind = kind;

    /// <summary>The codec's kind; <see cref="CodecKind.Unknown"/> defaults it from the field's CLR type.</summary>
    public CodecKind Kind { get; }

    /// <summary>
    /// Bits, for <c>Quant</c>, <c>Unorm</c>, <c>Snorm</c>, <c>Angle</c>, <c>Bits</c>, <c>Vec2/3</c>, an enum or a position; 0 for the codec's default.
    /// </summary>
    public int Bits { get; set; }

    /// <summary>The lower bound of a <c>Quant</c> codec.</summary>
    public double Min { get; set; }

    /// <summary>The upper bound of a <c>Quant</c> codec.</summary>
    public double Max { get; set; }

    /// <summary>The step of a <c>Vec2/3</c> codec.</summary>
    public double Scale { get; set; }

    /// <summary>The cap of a <c>Str</c>.</summary>
    public int MaxBytes { get; set; }

    /// <summary>The field's wire name; the builder's default (its own name) when unset.</summary>
    public string Name { get; set; }

    /// <summary>Whether a value past the codec's range is clamped to it rather than refused (<c>Codec.Saturate()</c>).</summary>
    public bool Saturate { get; set; }
}

/// <summary>A component field that travels in a change group (<c>.Field(comp, selector, codec, name, group)</c>).</summary>
[AttributeUsage(AttributeTargets.Field, Inherited = false)]
public sealed class ReplicateAttribute : CodecDeclarationAttribute
{
    /// <summary>A field under the codec its type implies.</summary>
    public ReplicateAttribute() : base(CodecKind.Unknown)
    {
    }

    /// <summary>A field under a named codec.</summary>
    /// <param name="kind">The codec.</param>
    public ReplicateAttribute(CodecKind kind) : base(kind)
    {
    }

    /// <summary>The change group; the builder's default when unset.</summary>
    public string Group { get; set; }
}

/// <summary>A component field sent only in enter records (<c>.OnEnter(comp, selector, codec, name)</c>).</summary>
[AttributeUsage(AttributeTargets.Field, Inherited = false)]
public sealed class OnEnterAttribute : CodecDeclarationAttribute
{
    /// <summary>A field under the codec its type implies.</summary>
    public OnEnterAttribute() : base(CodecKind.Unknown)
    {
    }

    /// <summary>A field under a named codec.</summary>
    /// <param name="kind">The codec.</param>
    public OnEnterAttribute(CodecKind kind) : base(kind)
    {
    }
}

/// <summary>A component field only its controlling session sees, in <c>SELF</c> (<c>.Owner(o => o.Field(…))</c>, W17).</summary>
[AttributeUsage(AttributeTargets.Field, Inherited = false)]
public sealed class OwnerAttribute : CodecDeclarationAttribute
{
    /// <summary>An owner field under the codec its type implies.</summary>
    public OwnerAttribute() : base(CodecKind.Unknown)
    {
    }

    /// <summary>An owner field under a named codec.</summary>
    /// <param name="kind">The codec.</param>
    public OwnerAttribute(CodecKind kind) : base(kind)
    {
    }

    /// <summary>The owner change group; the builder's default when unset.</summary>
    public string Group { get; set; }
}

/// <summary>A component field sent as a fraction of another (<c>.Fraction(comp, value, max, bits, name, group)</c>): health of max health.</summary>
[AttributeUsage(AttributeTargets.Field, Inherited = false)]
public sealed class FractionAttribute : Attribute
{
    /// <summary>A fraction of the field named <paramref name="maxField"/>.</summary>
    /// <param name="maxField">The same component's field holding the maximum; <c>nameof</c> it.</param>
    public FractionAttribute(string maxField) => MaxField = maxField;

    /// <summary>The field holding the maximum.</summary>
    public string MaxField { get; }

    /// <summary>The fraction's bits.</summary>
    public int Bits { get; set; } = 8;

    /// <summary>The wire name — required: neither the value's name nor the maximum's describes the ratio.</summary>
    public string Name { get; set; }

    /// <summary>The change group; the builder's default when unset.</summary>
    public string Group { get; set; }
}

/// <summary>A component field that is a heading, sent when it turns past a tolerance (<c>.Heading(comp, selector, bits, toleranceDeg, name)</c>).</summary>
[AttributeUsage(AttributeTargets.Field, Inherited = false)]
public sealed class HeadingAttribute : Attribute
{
    /// <summary>The angle's bits.</summary>
    public int Bits { get; set; } = 16;

    /// <summary>The turn below which nothing is sent, degrees.</summary>
    public double ToleranceDeg { get; set; } = 2;

    /// <summary>The wire name; the builder's default (the field's own) when unset.</summary>
    public string Name { get; set; }
}

/// <summary>
/// On a command struct or an event record: its fields' codecs come from their <see cref="CodecAttribute"/>, <see cref="QuantAttribute"/> and
/// <see cref="EntityRefAttribute"/> attributes, compiled by the generator into a descriptor the engine's builders read (11 § 5.3) — engine-free, so a
/// contracts assembly shared with clients can carry it. The message's policy (rate, coalescing, routing) stays in its builder call.
/// </summary>
[AttributeUsage(AttributeTargets.Struct, Inherited = false)]
public sealed class ReplicatedMessageAttribute : Attribute
{
}

/// <summary>A message field's codec, named by kind.</summary>
[AttributeUsage(AttributeTargets.Field, Inherited = false)]
public sealed class CodecAttribute : CodecDeclarationAttribute
{
    /// <summary>A message field under a named codec.</summary>
    /// <param name="kind">The codec.</param>
    public CodecAttribute(CodecKind kind) : base(kind)
    {
    }
}

/// <summary>A message field quantized over [min, max) at a given number of bits.</summary>
[AttributeUsage(AttributeTargets.Field, Inherited = false)]
public sealed class QuantAttribute : CodecDeclarationAttribute
{
    /// <summary>A quantized field.</summary>
    /// <param name="min">The lower bound.</param>
    /// <param name="max">The upper bound.</param>
    /// <param name="bits">8, 16, 24 or 32.</param>
    public QuantAttribute(double min, double max, int bits) : base(CodecKind.Quant)
    {
        Min = min;
        Max = max;
        Bits = bits;
    }
}

/// <summary>A message field naming an entity: a netId on the wire.</summary>
[AttributeUsage(AttributeTargets.Field, Inherited = false)]
public sealed class EntityRefAttribute : CodecDeclarationAttribute
{
    /// <summary>An entity reference.</summary>
    public EntityRefAttribute() : base(CodecKind.EntityRef)
    {
    }
}

/// <summary>
/// One message field's codec as its attributes declare it: what the generator compiles a <see cref="ReplicatedMessageAttribute"/> type's
/// <see cref="CodecAttribute"/>, <see cref="QuantAttribute"/> and <see cref="EntityRefAttribute"/> fields into. Engine-free, so a contracts assembly
/// that references only this one carries it; the engine turns it into a codec through the same factories a builder call uses, so it is validated the same.
/// </summary>
/// <param name="Field">The struct field's CLR name.</param>
/// <param name="Kind">The codec.</param>
/// <param name="Bits">Its bits, 0 for its default.</param>
/// <param name="Min">A <c>Quant</c>'s lower bound.</param>
/// <param name="Max">A <c>Quant</c>'s upper bound.</param>
/// <param name="Scale">A <c>Vec2/3</c>'s step.</param>
/// <param name="MaxBytes">A <c>Str</c>'s cap.</param>
/// <param name="Name">The wire name, or <see langword="null"/> for the field's own.</param>
/// <param name="Saturate">Whether a value past the codec's range is clamped to it.</param>
public readonly record struct MessageFieldDeclaration(
    string Field,
    CodecKind Kind,
    int Bits,
    double Min,
    double Max,
    double Scale,
    int MaxBytes,
    string Name,
    bool Saturate);

/// <summary>
/// Implemented by the generator on every <see cref="ReplicatedMessageAttribute"/> type: its attributed fields. The engine's command and event builders
/// read it once, at declaration — a field declared in the builder call still wins, and one with no attribute takes its type's default.
/// </summary>
public interface IReplicatedMessage
{
    /// <summary>The fields whose codec the attributes declare.</summary>
    /// <returns>The declarations, in field order.</returns>
    MessageFieldDeclaration[] ReplicatedFields();

    /// <summary>
    /// Every public instance field — attributed or not — with the wire name it travels under by default and its CLR type: what a client binds against the
    /// negotiated catalog to send the struct itself (<see cref="MessageBinding{T}"/>).
    /// </summary>
    /// <returns>The layout, in field declaration order; the same array on every call.</returns>
    MessageFieldLayout[] FieldLayout();

    /// <summary>One field's value, by its index in <see cref="FieldLayout"/>, widened without loss.</summary>
    /// <param name="index">The field's index in <see cref="FieldLayout"/>.</param>
    /// <returns>The value.</returns>
    MessageFieldValue ReadField(int index);
}

/// <summary>A message field's CLR type, as the typed send path reads it (an enum is its underlying integer type).</summary>
public enum MessageFieldType : byte
{
    /// <summary>A type the typed send path cannot read: a binding that needs the field is refused.</summary>
    Unsupported,

    /// <summary><see cref="bool"/>.</summary>
    Bool,

    /// <summary><see cref="sbyte"/>.</summary>
    SByte,

    /// <summary><see cref="byte"/>.</summary>
    Byte,

    /// <summary><see cref="short"/>.</summary>
    Int16,

    /// <summary><see cref="ushort"/>.</summary>
    UInt16,

    /// <summary><see cref="int"/>.</summary>
    Int32,

    /// <summary><see cref="uint"/>.</summary>
    UInt32,

    /// <summary><see cref="long"/>.</summary>
    Int64,

    /// <summary><see cref="ulong"/>.</summary>
    UInt64,

    /// <summary><see cref="float"/>.</summary>
    Single,

    /// <summary><see cref="double"/>.</summary>
    Double,
}

/// <summary>One public instance field of a <see cref="ReplicatedMessageAttribute"/> type, as the generator saw it.</summary>
/// <param name="Field">The CLR field name.</param>
/// <param name="Name">The wire name it travels under unless a builder call renames it: its attribute's <c>Name</c>, else the field's own.</param>
/// <param name="Type">Its CLR type.</param>
/// <param name="IsEnum">Whether the CLR type is an enum (<paramref name="Type"/> is then its underlying type).</param>
public readonly record struct MessageFieldLayout(string Field, string Name, MessageFieldType Type, bool IsEnum);

/// <summary>
/// One message field's value, widened without loss: every type up to 32 bits and both floating types as a <see cref="double"/> (exact — a
/// <see cref="float"/> widens and narrows back to the same bits), a 64-bit integer as its bits.
/// </summary>
public readonly struct MessageFieldValue
{
    private readonly double _number;
    private readonly ulong _bits;
    private readonly byte _kind; // 0 number, 1 signed 64-bit, 2 unsigned 64-bit

    private MessageFieldValue(double number, ulong bits, byte kind)
    {
        _number = number;
        _bits = bits;
        _kind = kind;
    }

    /// <summary>A value of 32 bits or fewer, or a floating one.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The field value.</returns>
    public static MessageFieldValue Of(double value) => new(value, 0, 0);

    /// <summary>A signed 64-bit value.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The field value.</returns>
    public static MessageFieldValue Of(long value) => new(0, unchecked((ulong)value), 1);

    /// <summary>An unsigned 64-bit value.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The field value.</returns>
    public static MessageFieldValue Of(ulong value) => new(0, value, 2);

    /// <summary>Whether the value is a 64-bit integer.</summary>
    public bool IsInteger64 => _kind != 0;

    /// <summary>The value as a number: exact for every type but a 64-bit integer, which rounds as a <c>(double)</c> cast does.</summary>
    public double Number => _kind switch
    {
        0 => _number,
        1 => unchecked((long)_bits),
        _ => _bits,
    };

    /// <summary>A 64-bit integer's bits (two's complement for a signed one); 0 for a number.</summary>
    public ulong Integer64Bits => _bits;
}
