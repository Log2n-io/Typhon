using System;

namespace Typhon.Protocol;

/// <summary>The first byte of every message (03-wire-protocol § 3). Client-to-server types have the high bit set.</summary>
public static class MessageTypes
{
    /// <summary>Server → client: session opened.</summary>
    public const byte Welcome = 0x01;

    /// <summary>Server → client: one tick's frame.</summary>
    public const byte Tick = 0x02;

    /// <summary>Server → client: reply to <see cref="Ping"/>.</summary>
    public const byte Pong = 0x03;

    /// <summary>Server → client: the session is being closed.</summary>
    public const byte Kick = 0x04;

    /// <summary>Client → server: the first message.</summary>
    public const byte Hello = 0x81;

    /// <summary>Client → server: typed commands.</summary>
    public const byte Commands = 0x83;

    /// <summary>Client → server: liveness and the last applied tick.</summary>
    public const byte Ping = 0x84;

    /// <summary>Client → server: a clean leave.</summary>
    public const byte Bye = 0x85;
}

/// <summary>Reads whole control messages.</summary>
public static class ControlMessage
{
    /// <summary>Reads a message body.</summary>
    /// <typeparam name="T">The message type.</typeparam>
    /// <param name="reader">The reader, positioned after the type byte.</param>
    /// <returns>The message.</returns>
    public delegate T BodyReader<out T>(ref WireReader reader);

    /// <summary>
    /// Reads a whole message: checks its type byte, reads its body, and refuses trailing bytes — a body shorter than its message is malformed, not padded.
    /// </summary>
    /// <typeparam name="T">The message type.</typeparam>
    /// <param name="message">The whole message.</param>
    /// <param name="type">The expected type byte.</param>
    /// <param name="read">The body reader.</param>
    /// <returns>The message.</returns>
    /// <exception cref="WireFormatException">Another type (1002), or a malformed or over-long body (1007).</exception>
    public static T Parse<T>(ReadOnlySpan<byte> message, byte type, BodyReader<T> read)
    {
        var r = new WireReader(message);
        if (r.ReadU8() != type)
        {
            throw WireFormatException.Protocol($"expected message type 0x{type:x2}");
        }

        var result = read(ref r);
        r.ExpectEnd("message");
        return result;
    }
}

/// <summary>The <c>caps</c> bits (W23): only server work a client may decline. Bits 2–31 are reserved, sent as 0 and ignored.</summary>
[Flags]
public enum Capabilities : uint
{
    /// <summary>No optional work.</summary>
    None = 0,

    /// <summary>The <c>STATS</c> block; granted when the catalog declares at least one metric.</summary>
    Stats = 1,

    /// <summary>The <c>DEBUG</c> block; granted when admission authorized it.</summary>
    Debug = 2,
}

/// <summary><c>HELLO</c> (0x81): the client's first message, at most <see cref="ProtocolConstants.HelloMaxBytes"/>.</summary>
public sealed class HelloMessage
{
    /// <summary>The client's protocol major.</summary>
    public ushort Major { get; init; } = ProtocolConstants.Major;

    /// <summary>The client's protocol minor.</summary>
    public ushort Minor { get; init; } = ProtocolConstants.Minor;

    /// <summary>The optional work the client asks for.</summary>
    public Capabilities Caps { get; init; }

    /// <summary>The session kind, an application-declared name the engine never interprets (W21).</summary>
    public string Kind { get; init; }

    /// <summary>An opaque token handed to the application's admission hook (W22).</summary>
    public string Token { get; init; }

    /// <summary>A resume token from an earlier <c>WELCOME</c>, or all zeros (or <see langword="null"/>) for none.</summary>
    public byte[] ResumeToken { get; init; }

    /// <summary>The catalog hash the client already holds, or 0 for none.</summary>
    public ulong ClientCatalogHash { get; init; }

    /// <summary>Application data for the session's <c>Opened</c> hook, at most 256 bytes; empty for none.</summary>
    public byte[] HelloPayload { get; init; }

    /// <summary>Writes the message, type byte included.</summary>
    /// <param name="writer">The writer.</param>
    public void Write(ref WireWriter writer)
    {
        writer.WriteU8(MessageTypes.Hello);
        writer.WriteU16(Major);
        writer.WriteU16(Minor);
        writer.WriteU32((uint)Caps);
        writer.WriteStr(Kind, ProtocolConstants.SessionKindMaxBytes);
        writer.WriteStr(Token, ProtocolConstants.TokenMaxBytes);
        writer.WriteBytes(Token16(ResumeToken));
        Span<byte> hash = stackalloc byte[8];
        CatalogSerializer.WriteHash(ClientCatalogHash, hash);
        writer.WriteBytes(hash);
        writer.WriteBlob(HelloPayload ?? [], ProtocolConstants.HelloPayloadMaxBytes);
    }

    /// <summary>Reads a whole <c>HELLO</c> message: its type byte, its body, and nothing after it.</summary>
    /// <param name="message">The message.</param>
    /// <returns>The message.</returns>
    public static HelloMessage Parse(ReadOnlySpan<byte> message) => ControlMessage.Parse(message, MessageTypes.Hello, Read);

    /// <summary>Reads the message body; the type byte has already been read.</summary>
    /// <param name="reader">The reader, positioned after the type byte.</param>
    /// <returns>The message.</returns>
    public static HelloMessage Read(ref WireReader reader) => new()
    {
        Major = reader.ReadU16(),
        Minor = reader.ReadU16(),
        Caps = (Capabilities)reader.ReadU32(),
        Kind = reader.ReadStr(ProtocolConstants.SessionKindMaxBytes),
        Token = reader.ReadStr(ProtocolConstants.TokenMaxBytes),
        ResumeToken = reader.ReadBytes(16).ToArray(),
        ClientCatalogHash = ReadHash(ref reader),
        HelloPayload = reader.ReadBlob(ProtocolConstants.HelloPayloadMaxBytes).ToArray(),
    };

    internal static ReadOnlySpan<byte> Token16(byte[] token)
    {
        if (token == null)
        {
            return new byte[16];
        }

        return token.Length == 16 ? token : throw new ArgumentException("a resume token is exactly 16 bytes");
    }

    internal static ulong ReadHash(ref WireReader reader) => System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(reader.ReadBytes(8));
}

/// <summary><c>WELCOME</c> (0x01): the server's reply to <c>HELLO</c>.</summary>
public sealed class WelcomeMessage
{
    /// <summary>The negotiated major.</summary>
    public ushort Major { get; init; } = ProtocolConstants.Major;

    /// <summary>The negotiated minor: the lower of the two sides.</summary>
    public ushort Minor { get; init; } = ProtocolConstants.Minor;

    /// <summary>The optional work granted: a subset of what was requested, known and authorized.</summary>
    public Capabilities CapsGranted { get; init; }

    /// <summary>The session's identifier.</summary>
    public uint SessionId { get; init; }

    /// <summary>The token to resume this session with, or all zeros when resume is disabled.</summary>
    public byte[] ResumeToken { get; init; }

    /// <summary>The server's current tick.</summary>
    public uint Tick { get; init; }

    /// <summary>The server's current tick period, in microseconds.</summary>
    public uint TickPeriodUs { get; init; }

    /// <summary>The catalog's digest (W20).</summary>
    public ulong CatalogHash { get; init; }

    /// <summary>The canonical catalog JSON, or empty when the client's hash matched and the catalog is skipped.</summary>
    public byte[] CatalogJson { get; init; }

    /// <summary>Writes the message, type byte included.</summary>
    /// <param name="writer">The writer.</param>
    public void Write(ref WireWriter writer)
    {
        writer.WriteU8(MessageTypes.Welcome);
        writer.WriteU16(Major);
        writer.WriteU16(Minor);
        writer.WriteU32((uint)CapsGranted);
        writer.WriteU32(SessionId);
        writer.WriteBytes(HelloMessage.Token16(ResumeToken));
        writer.WriteU32(Tick);
        writer.WriteU32(TickPeriodUs);
        Span<byte> hash = stackalloc byte[8];
        CatalogSerializer.WriteHash(CatalogHash, hash);
        writer.WriteBytes(hash);
        var json = CatalogJson ?? [];
        writer.WriteVaru((uint)json.Length);
        writer.WriteBytes(json);
    }

    /// <summary>Reads a whole <c>WELCOME</c> message: its type byte, its body, and nothing after it.</summary>
    /// <param name="message">The message.</param>
    /// <returns>The message.</returns>
    public static WelcomeMessage Parse(ReadOnlySpan<byte> message) => ControlMessage.Parse(message, MessageTypes.Welcome, Read);

    /// <summary>Reads the message body; the type byte has already been read.</summary>
    /// <param name="reader">The reader, positioned after the type byte.</param>
    /// <returns>The message.</returns>
    public static WelcomeMessage Read(ref WireReader reader) => new()
    {
        Major = reader.ReadU16(),
        Minor = reader.ReadU16(),
        CapsGranted = (Capabilities)reader.ReadU32(),
        SessionId = reader.ReadU32(),
        ResumeToken = reader.ReadBytes(16).ToArray(),
        Tick = reader.ReadU32(),
        TickPeriodUs = reader.ReadU32(),
        CatalogHash = HelloMessage.ReadHash(ref reader),
        CatalogJson = reader.ReadBlob(int.MaxValue).ToArray(),
    };
}

/// <summary><c>PING</c> (0x84): mandatory at the catalog's <c>pingHz</c>; its <see cref="LastAppliedTick"/> drives the server's lag skip.</summary>
public readonly record struct PingMessage(uint ClientMs, uint LastAppliedTick)
{
    /// <summary>Writes the message, type byte included.</summary>
    /// <param name="writer">The writer.</param>
    public void Write(ref WireWriter writer)
    {
        writer.WriteU8(MessageTypes.Ping);
        writer.WriteU32(ClientMs);
        writer.WriteU32(LastAppliedTick);
    }

    /// <summary>Reads the message body; the type byte has already been read.</summary>
    /// <param name="reader">The reader, positioned after the type byte.</param>
    /// <returns>The message.</returns>
    public static PingMessage Read(ref WireReader reader) => new(reader.ReadU32(), reader.ReadU32());

    /// <summary>Reads a whole <c>PING</c> message: its type byte, its body, and nothing after it.</summary>
    /// <param name="message">The message.</param>
    /// <returns>The message.</returns>
    public static PingMessage Parse(ReadOnlySpan<byte> message) => ControlMessage.Parse(message, MessageTypes.Ping, Read);
}

/// <summary><c>PONG</c> (0x03): the reply to <c>PING</c>, with how far the server is into its tick (W31: <c>u32</c>, a tick exceeds 65 535 µs).</summary>
public readonly record struct PongMessage(uint ClientMs, uint Tick, uint UsIntoTick)
{
    /// <summary>Writes the message, type byte included.</summary>
    /// <param name="writer">The writer.</param>
    public void Write(ref WireWriter writer)
    {
        writer.WriteU8(MessageTypes.Pong);
        writer.WriteU32(ClientMs);
        writer.WriteU32(Tick);
        writer.WriteU32(UsIntoTick);
    }

    /// <summary>Reads the message body; the type byte has already been read.</summary>
    /// <param name="reader">The reader, positioned after the type byte.</param>
    /// <returns>The message.</returns>
    public static PongMessage Read(ref WireReader reader) => new(reader.ReadU32(), reader.ReadU32(), reader.ReadU32());

    /// <summary>Reads a whole <c>PONG</c> message: its type byte, its body, and nothing after it.</summary>
    /// <param name="message">The message.</param>
    /// <returns>The message.</returns>
    public static PongMessage Parse(ReadOnlySpan<byte> message) => ControlMessage.Parse(message, MessageTypes.Pong, Read);
}

/// <summary><c>KICK</c> (0x04): the session is closed with <see cref="Code"/>; the reason is at most 123 UTF-8 bytes (W24).</summary>
public readonly record struct KickMessage(ushort Code, string Reason)
{
    /// <summary>Writes the message, type byte included, truncating the reason at a code-point boundary to fit a WebSocket close frame.</summary>
    /// <param name="writer">The writer.</param>
    public void Write(ref WireWriter writer)
    {
        writer.WriteU8(MessageTypes.Kick);
        writer.WriteU16(Code);
        writer.WriteStr(TruncateUtf8(Reason, ProtocolConstants.KickReasonMaxBytes), ProtocolConstants.KickReasonMaxBytes);
    }

    /// <summary>Reads the message body; the type byte has already been read.</summary>
    /// <param name="reader">The reader, positioned after the type byte.</param>
    /// <returns>The message.</returns>
    public static KickMessage Read(ref WireReader reader) => new(reader.ReadU16(), reader.ReadStr(ProtocolConstants.KickReasonMaxBytes));

    /// <summary>Reads a whole <c>KICK</c> message: its type byte, its body, and nothing after it.</summary>
    /// <param name="message">The message.</param>
    /// <returns>The message.</returns>
    public static KickMessage Parse(ReadOnlySpan<byte> message) => ControlMessage.Parse(message, MessageTypes.Kick, Read);

    /// <summary>The longest prefix of <paramref name="text"/> whose UTF-8 encoding fits <paramref name="maxBytes"/>, cut between code points.</summary>
    /// <param name="text">The text.</param>
    /// <param name="maxBytes">The cap.</param>
    /// <returns>The prefix.</returns>
    public static string TruncateUtf8(string text, int maxBytes)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var bytes = 0;
        var i = 0;
        while (i < text.Length)
        {
            var width = char.IsHighSurrogate(text[i]) && i + 1 < text.Length ? 2 : 1;
            var size = System.Text.Encoding.UTF8.GetByteCount(text.AsSpan(i, width));
            if (bytes + size > maxBytes)
            {
                break;
            }

            bytes += size;
            i += width;
        }

        return text[..i];
    }
}

/// <summary><c>BYE</c> (0x85): a clean leave, with 1000 or a code in 4000–4999 — the codes a browser's <c>close()</c> accepts.</summary>
public readonly record struct ByeMessage(ushort Code)
{
    /// <summary>Writes the message, type byte included.</summary>
    /// <param name="writer">The writer.</param>
    public void Write(ref WireWriter writer)
    {
        if (!CloseCodes.IsValidClientCode(Code))
        {
            throw new ArgumentException($"BYE code {Code} is neither 1000 nor in 4000–4999");
        }

        writer.WriteU8(MessageTypes.Bye);
        writer.WriteU16(Code);
    }

    /// <summary>Reads a whole <c>BYE</c> message: its type byte, its body, and nothing after it.</summary>
    /// <param name="message">The message.</param>
    /// <returns>The message.</returns>
    public static ByeMessage Parse(ReadOnlySpan<byte> message) => ControlMessage.Parse(message, MessageTypes.Bye, Read);

    /// <summary>Reads the message body; the type byte has already been read.</summary>
    /// <param name="reader">The reader, positioned after the type byte.</param>
    /// <returns>The message.</returns>
    public static ByeMessage Read(ref WireReader reader)
    {
        var code = reader.ReadU16();
        return CloseCodes.IsValidClientCode(code) ? new ByeMessage(code) : throw WireFormatException.Malformed($"BYE code {code} is not a client code");
    }
}
