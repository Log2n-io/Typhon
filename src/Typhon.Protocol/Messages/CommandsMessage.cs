using System;
using System.Collections.Generic;

namespace Typhon.Protocol;

/// <summary>
/// Receives a decoded <c>COMMANDS</c> message: each command's header, then its fields through the <see cref="IFieldSink"/> members.
/// </summary>
public interface ICommandSink : IFieldSink
{
    /// <summary>A command begins.</summary>
    /// <param name="type">The command type.</param>
    /// <param name="seq">The client's sequence number; wraps, compared with serial arithmetic (RFC 1982).</param>
    /// <param name="clientTick">The client's tick when it produced the batch, shared by every command in the message.</param>
    void Command(MessagePlan type, ushort seq, uint clientTick);
}

/// <summary>
/// <c>COMMANDS</c> (0x83): <c>u32 clientTick | varu count ≥ 1 | (varu cmdTypeIdx | u16 seq | fields)*</c>, the client's batch for one rendered frame.
/// </summary>
/// <remarks>
/// <b>Validated whole before anything is delivered</b> (03-wire-protocol § 8: known type → length consumed exactly → decode). The message is decoded once into
/// a checking sink — every type known, every enum value within its names (W13), not a byte left over — and only then decoded again into the caller's sink,
/// so a malformed tail can never deliver the valid commands in front of it. Commands are small and rate-limited; decoding twice costs nothing measurable.
/// </remarks>
public static class CommandsMessage
{
    /// <summary>Writes a <c>COMMANDS</c> message, type byte included.</summary>
    /// <param name="w">The writer.</param>
    /// <param name="clientTick">The client's tick for the batch.</param>
    /// <param name="commands">At least one command: its type, sequence and field values.</param>
    public static void Write(ref WireWriter w, uint clientTick, IReadOnlyList<(MessagePlan Type, ushort Seq, RecordValues Values)> commands)
    {
        if (commands.Count == 0)
        {
            throw new ArgumentException("a COMMANDS message carries at least one command");
        }

        w.WriteU8(MessageTypes.Commands);
        w.WriteU32(clientTick);
        w.WriteVaru((uint)commands.Count);
        foreach (var (type, seq, values) in commands)
        {
            w.WriteVaru((uint)type.Idx);
            w.WriteU16(seq);
            FieldCodec.WriteSection(ref w, type.Body, values.For);
        }
    }

    /// <summary>Validates, then decodes, a whole <c>COMMANDS</c> message, type byte included.</summary>
    /// <typeparam name="TSink">The sink type.</typeparam>
    /// <param name="message">The message.</param>
    /// <param name="plan">The session's compiled catalog.</param>
    /// <param name="sink">Receives the commands, only once the whole message has been validated.</param>
    /// <exception cref="WireFormatException">The message is malformed; nothing reached <paramref name="sink"/>.</exception>
    public static void Read<TSink>(ReadOnlySpan<byte> message, CatalogPlan plan, ref TSink sink)
        where TSink : ICommandSink, allows ref struct
    {
        var check = default(ValidatingSink);
        Decode(message, plan, ref check);
        Decode(message, plan, ref sink);
    }

    private static void Decode<TSink>(ReadOnlySpan<byte> message, CatalogPlan plan, ref TSink sink)
        where TSink : ICommandSink, allows ref struct
    {
        var r = new WireReader(message);
        if (r.ReadU8() != MessageTypes.Commands)
        {
            throw WireFormatException.Protocol("not a COMMANDS message");
        }

        var clientTick = r.ReadU32();
        var count = r.ReadVaru();
        if (count == 0)
        {
            throw WireFormatException.Malformed("a COMMANDS message carries at least one command");
        }

        for (; count > 0; count--)
        {
            var type = plan.Command(r.ReadVaruAtMost(int.MaxValue, "command index"));
            var seq = r.ReadU16();
            sink.Command(type, seq, clientTick);

            // A tickLo command field rebuilds against the client's claimed tick, the only frame a command has.
            FieldCodec.ReadSection(ref r, type.Body, clientTick, ref sink);
        }

        r.ExpectEnd("COMMANDS");
    }

    /// <summary>Checks what the decoder alone cannot: a command's enum values stay within their names.</summary>
    private struct ValidatingSink : ICommandSink
    {
        public readonly void Command(MessagePlan type, ushort seq, uint clientTick)
        {
        }

        public readonly void Number(FieldPlan field, scoped ReadOnlySpan<double> components)
        {
            if (field.EnumCount > 0 && components[0] >= field.EnumCount)
            {
                throw WireFormatException.Malformed($"command field '{field.Name}' carries enum value {components[0]}; it has {field.EnumCount} names");
            }
        }

        public readonly void Text(FieldPlan field, scoped ReadOnlySpan<byte> utf8)
        {
        }

        public readonly void Bytes(FieldPlan field, scoped ReadOnlySpan<byte> bytes)
        {
        }

        public readonly void List(FieldPlan field, int count, scoped ReadOnlySpan<double> components)
        {
        }
    }
}
