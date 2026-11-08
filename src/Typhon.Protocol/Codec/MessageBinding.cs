using System;
using System.Collections.Generic;
using System.Globalization;

namespace Typhon.Protocol;

/// <summary>A message struct does not match the command or event the negotiated catalog declares under its name.</summary>
public sealed class MessageBindingException : InvalidOperationException
{
    /// <summary>A binding refused for <paramref name="problems"/>.</summary>
    /// <param name="type">The struct.</param>
    /// <param name="problems">Every mismatch found, not just the first.</param>
    public MessageBindingException(Type type, IReadOnlyList<string> problems)
        : base($"'{type?.Name}' cannot be sent against this catalog: {string.Join("; ", problems)}")
    {
        Problems = problems;
    }

    /// <summary>Every mismatch found.</summary>
    public IReadOnlyList<string> Problems { get; }
}

/// <summary>
/// A <see cref="ReplicatedMessageAttribute"/> struct bound to the command the negotiated catalog declares under its name: every catalog field mapped to the
/// struct field it comes from, so the struct encodes straight into a <c>COMMANDS</c> body — no name lookup, no value boxing, no allocation per send.
/// </summary>
/// <typeparam name="T">The command struct.</typeparam>
/// <remarks>
/// <para>
/// <b>The catalog is the authority.</b> The wire order, the codecs and the names are the server's: a builder call can rename a field or ignore one, and the
/// struct cannot see that. So the binding is checked once, both ways, and refused whole with every mismatch listed (<see cref="MessageBindingException"/>):
/// a catalog field the struct does not carry would travel as zero, and a struct field the catalog does not carry would be set by the caller and silently
/// dropped. Either is the name-typo bug this path exists to remove, moved from a field name to a declaration — so neither is tolerated.
/// </para>
/// <para>
/// <b>The bytes are the interpreter's.</b> Every field goes through the same <see cref="FieldCodec"/> routines a <see cref="RecordValues"/> send uses, fed
/// the widened value (<see cref="MessageFieldValue"/>): a <see cref="float"/> becomes a <see cref="double"/> and back without a bit changing, so an
/// <c>f32</c> field is written bit-exact.
/// </para>
/// </remarks>
public sealed class MessageBinding<T> where T : unmanaged, IReplicatedMessage
{
    private readonly int[] _sources;

    private MessageBinding(MessagePlan plan, int[] sources, int maxBytes)
    {
        Plan = plan;
        _sources = sources;
        MaxBytes = maxBytes;
    }

    /// <summary>The command or event the struct is bound to.</summary>
    public MessagePlan Plan { get; }

    /// <summary>An upper bound of one encoded body, header excluded.</summary>
    public int MaxBytes { get; }

    /// <summary>Binds <typeparamref name="T"/> to <paramref name="plan"/>.</summary>
    /// <param name="plan">The command (or event) the catalog declares.</param>
    /// <returns>The binding.</returns>
    /// <exception cref="MessageBindingException">The struct and the catalog disagree; the message lists every disagreement.</exception>
    public static MessageBinding<T> Bind(MessagePlan plan)
    {
        if (plan == null)
        {
            throw new ArgumentNullException(nameof(plan));
        }

        var layout = default(T).FieldLayout();
        var problems = new List<string>();
        var sources = new int[plan.Body.Fields.Length];
        var used = new bool[layout.Length];
        var maxBytes = plan.Body.PackBytes;
        for (var i = 0; i < sources.Length; i++)
        {
            var f = plan.Body.Fields[i];
            var source = Find(layout, f.Name);
            sources[i] = source;
            if (source < 0)
            {
                problems.Add($"the catalog's field '{f.Name}' ({f.Codec.Type}) is not a field of the struct");
                continue;
            }

            used[source] = true;
            var clr = layout[source];
            if (clr.Type == MessageFieldType.Unsupported)
            {
                problems.Add($"field '{clr.Field}' has a type the typed send path cannot read");
            }
            else if (f.ValueKind is not (FieldValueKind.Number or FieldValueKind.Integer64) || f.Components != 1)
            {
                problems.Add($"field '{clr.Field}' travels as '{f.Codec.Type}', which a scalar struct field cannot supply");
            }
            else if (clr.Type is MessageFieldType.Single or MessageFieldType.Double && IsIntegerCodec(f.Kind))
            {
                // A fraction would be refused on every send that carries one: the declarations disagree, and that is found here, not mid-game.
                problems.Add($"field '{clr.Field}' is a {clr.Type.ToString().ToLowerInvariant()} but travels as the integer codec '{f.Codec.Type}'");
            }
            else if ((clr.Type == MessageFieldType.Int64 && f.Kind is CodecKind.U64 or CodecKind.Varu64)
                || (clr.Type == MessageFieldType.UInt64 && f.Kind is CodecKind.I64 or CodecKind.Vari64))
            {
                // A 64-bit value travels as its bits: a signedness mismatch would send -1 as 2^64 - 1 without a word, where the name-keyed path refuses it.
                problems.Add($"field '{clr.Field}' is a {clr.Type.ToString().ToLowerInvariant()} but travels as '{f.Codec.Type}', of the other signedness");
            }

            maxBytes += f.Packed ? 0 : MaxScalarBytes(f);
        }

        for (var i = 0; i < layout.Length; i++)
        {
            if (!used[i])
            {
                problems.Add(string.Equals(layout[i].Field, layout[i].Name, StringComparison.Ordinal)
                    ? $"the struct's field '{layout[i].Field}' is not in the catalog (renamed or ignored by the server's declaration?)"
                    : $"the struct's field '{layout[i].Field}' (wire name '{layout[i].Name}') is not in the catalog");
            }
        }

        if (problems.Count > 0)
        {
            throw new MessageBindingException(typeof(T), problems);
        }

        return new MessageBinding<T>(plan, sources, maxBytes);
    }

    /// <summary>Encodes <paramref name="message"/>'s body — the fields, in the catalog's wire order.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="message">The struct.</param>
    /// <param name="frame">The realm frame a realm-framed field travels over; a scalar body never needs one.</param>
    /// <exception cref="ArgumentException">A value outside its codec's range, refused as a <see cref="RecordValues"/> send refuses it.</exception>
    public void WriteBody(ref WireWriter writer, in T message, RealmFrame frame = null)
    {
        // One copy, so each ReadField call does not make its own defensive one: T is a generic struct, so the compiler cannot know the member is readonly.
        var m = message;
        var section = Plan.Body;
        var fields = section.Fields;
        if (section.PackBytes > 0)
        {
            Span<byte> pack = stackalloc byte[section.PackBytes];
            pack.Clear();
            for (var i = 0; i < section.PackedCount; i++)
            {
                var f = fields[i];
                FieldCodec.WritePackedBits(pack, f.BitOffset, f.BitCount, FieldCodec.PackedCode(f, m.ReadField(_sources[i]).Number));
            }

            writer.WriteBytes(pack);
        }

        Span<double> number = stackalloc double[1];
        Span<ulong> integer = stackalloc ulong[1];
        for (var i = section.PackedCount; i < fields.Length; i++)
        {
            var f = fields[i];
            var value = m.ReadField(_sources[i]);
            if (f.ValueKind == FieldValueKind.Integer64)
            {
                if (value.IsInteger64)
                {
                    integer[0] = value.Integer64Bits;
                    FieldCodec.WriteInteger64(ref writer, f, integer);
                }
                else
                {
                    number[0] = value.Number;
                    FieldCodec.WriteInteger64FromNumbers(ref writer, f, number);
                }
            }
            else
            {
                number[0] = value.Number;
                FieldCodec.WriteNumber(ref writer, f, number, frame);
            }
        }
    }

    private static int Find(MessageFieldLayout[] layout, string wireName)
    {
        for (var i = 0; i < layout.Length; i++)
        {
            if (string.Equals(layout[i].Name, wireName, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsIntegerCodec(CodecKind kind) => kind is CodecKind.U8 or CodecKind.I8 or CodecKind.U16 or CodecKind.I16 or CodecKind.U32
        or CodecKind.I32 or CodecKind.Varu or CodecKind.Vari or CodecKind.EntityRef or CodecKind.TickLo or CodecKind.Bits or CodecKind.U64
        or CodecKind.I64 or CodecKind.Varu64 or CodecKind.Vari64;

    // The widest a byte-aligned scalar codec can be: what a body is reserved at before it is written.
    private static int MaxScalarBytes(FieldPlan f) => f.Kind switch
    {
        CodecKind.U8 or CodecKind.I8 => 1,
        CodecKind.U16 or CodecKind.I16 or CodecKind.F16 or CodecKind.TickLo => 2,
        CodecKind.U32 or CodecKind.I32 or CodecKind.F32 => 4,
        CodecKind.Varu or CodecKind.Vari or CodecKind.EntityRef => 5,
        CodecKind.F64 or CodecKind.U64 or CodecKind.I64 => 8,
        CodecKind.Varu64 or CodecKind.Vari64 => 10,
        // A quantizing codec (WriteBits): at most 32 bits.
        _ => 4,
    };

    /// <inheritdoc />
    public override string ToString() => $"{typeof(T).Name} → {Plan.Name} ({Plan.Body.Fields.Length.ToString(CultureInfo.InvariantCulture)} fields)";
}
