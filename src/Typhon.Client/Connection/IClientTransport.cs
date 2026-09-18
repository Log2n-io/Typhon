using System;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Client;

/// <summary>
/// A message stream to a Typhon server: whole messages in, whole messages out, and a close code when it ends.
/// </summary>
/// <remarks>
/// <b>Message-oriented, not byte-oriented, and that is the point of the interface.</b> WebSocket already delivers messages; TCP delivers a byte stream and
/// needs a length prefix to become one. Putting the boundary here means <see cref="TyphonClient"/> never sees a partial message and never has to know which
/// transport it is on — the same property the server's <c>ISubscriptionLink</c> has from the other side.
/// </remarks>
public interface IClientTransport : IAsyncDisposable
{
    /// <summary>Whether the stream is still usable.</summary>
    bool IsConnected { get; }

    /// <summary>Opens the stream and completes any transport-level preamble.</summary>
    /// <param name="ct">Cancels the attempt.</param>
    /// <returns>The connect.</returns>
    Task ConnectAsync(CancellationToken ct);

    /// <summary>Sends one whole message.</summary>
    /// <param name="message">The bytes, type byte included.</param>
    /// <param name="ct">Cancels the send.</param>
    /// <returns>The send.</returns>
    ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken ct);

    /// <summary>
    /// Receives the next whole message.
    /// </summary>
    /// <param name="ct">Cancels the receive.</param>
    /// <returns>The message, or <see langword="null"/> when the peer closed the stream.</returns>
    /// <remarks>
    /// The returned array belongs to the caller: a transport that pooled it would hand the same buffer to the decoder and to a recorder, and the recorder
    /// keeps what it is given.
    /// </remarks>
    Task<byte[]> ReceiveAsync(CancellationToken ct);

    /// <summary>The code the peer closed with, or zero when it has not closed.</summary>
    ushort CloseCode { get; }

    /// <summary>Closes the stream, telling the peer why when the transport can carry a reason.</summary>
    /// <param name="code">The close code.</param>
    /// <param name="reason">A short reason.</param>
    /// <returns>The close.</returns>
    Task CloseAsync(ushort code, string reason);
}
