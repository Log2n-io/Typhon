using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace Typhon.Client;

/// <summary>
/// Every inbound message, in arrival order, so a session can be replayed into an identical store.
/// </summary>
/// <remarks>
/// <para>
/// <b>Raw messages, not decoded state.</b> What makes a recording worth having is that replaying it exercises the same decoder over the same bytes — a
/// recording of decoded state would only prove that the recorder and the store agree. It is the same reason the golden vectors are bytes.
/// </para>
/// <para>
/// <b>Order is the whole content.</b> Frames do not commute: an update applied before the enter it depends on is not a late update, it is a different world.
/// The format is therefore a flat sequence with no index and no seeking — the only correct way to consume it is from the beginning, and a format that made
/// anything else easy would be inviting the bug.
/// </para>
/// </remarks>
public sealed class Recorder
{
    /// <summary>The file's first four bytes, so a truncated or foreign file fails at the header rather than at the first message.</summary>
    private static ReadOnlySpan<byte> Magic => "TYRC"u8;

    private readonly List<byte[]> _messages = [];

    /// <summary>How many messages have been recorded.</summary>
    public int Count => _messages.Count;

    /// <summary>The messages, in arrival order.</summary>
    public IReadOnlyList<byte[]> Messages => _messages;

    /// <summary>Records one inbound message.</summary>
    /// <param name="message">The bytes, type byte included. Kept by reference; the transport hands out arrays it does not reuse.</param>
    public void Record(byte[] message)
    {
        ArgumentNullException.ThrowIfNull(message);

        _messages.Add(message);
    }

    /// <summary>Forgets everything recorded so far.</summary>
    public void Clear() => _messages.Clear();

    /// <summary>Writes the recording to a stream.</summary>
    /// <param name="stream">The destination.</param>
    public void Save(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        Span<byte> header = stackalloc byte[4];
        Magic.CopyTo(header);
        stream.Write(header);
        BinaryPrimitives.WriteInt32LittleEndian(header, _messages.Count);
        stream.Write(header);

        foreach (var message in _messages)
        {
            BinaryPrimitives.WriteInt32LittleEndian(header, message.Length);
            stream.Write(header);
            stream.Write(message);
        }
    }

    /// <summary>Reads a recording back.</summary>
    /// <param name="stream">The source.</param>
    /// <returns>The recorder, holding the messages in their recorded order.</returns>
    public static Recorder Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var recorder = new Recorder();
        Span<byte> header = stackalloc byte[4];
        ReadExactly(stream, header);
        if (!header.SequenceEqual(Magic))
        {
            throw new InvalidDataException("not a Typhon recording: the file does not begin with TYRC");
        }

        ReadExactly(stream, header);
        var count = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (count < 0)
        {
            throw new InvalidDataException($"the recording claims {count} messages");
        }

        for (var i = 0; i < count; i++)
        {
            ReadExactly(stream, header);
            var length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length < 0)
            {
                throw new InvalidDataException($"message {i} of the recording claims a length of {length}");
            }

            var message = new byte[length];
            ReadExactly(stream, message);
            recorder._messages.Add(message);
        }

        return recorder;
    }

    private static void ReadExactly(Stream stream, Span<byte> destination)
    {
        var read = 0;
        while (read < destination.Length)
        {
            var got = stream.Read(destination[read..]);
            if (got == 0)
            {
                throw new EndOfStreamException("the recording ends inside a message; it was truncated");
            }

            read += got;
        }
    }
}
