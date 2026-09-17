using JetBrains.Annotations;
using System;

namespace Typhon.Engine;

/// <summary>
/// One connection as the transport drives it: inbound messages, and the end of the link.
/// </summary>
/// <remarks>
/// <b>Implemented by the engine, one per connection.</b> Everything the protocol says — the handshake, the message caps, the close codes, the session — happens
/// behind these two methods, which is what keeps a transport free of protocol knowledge it would otherwise have to keep in step with the wire.
/// </remarks>
[PublicAPI]
public interface ISubscriptionConnection
{
    /// <summary>
    /// Delivers one complete client-to-server message.
    /// </summary>
    /// <param name="message">
    /// The whole message, type byte included and framing excluded. Borrowed: the engine copies whatever it keeps, so the transport may reuse the buffer the
    /// moment this returns.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>Sequential per connection, in the order the peer sent them.</b> The engine does no locking on the strength of that and a transport that overlaps two
    /// calls breaks the session's ordering guarantees, not merely its own.
    /// </para>
    /// <para>
    /// <b>It does not throw, and it does not report.</b> A message that is too large, out of state, unknown or malformed closes the connection with the close
    /// code the protocol gives it — 1009, 1002, 1007 — and the transport learns of it through <see cref="ISubscriptionLink.Close"/>, exactly as it would for a
    /// close the engine decided for any other reason. A transport therefore has one code path for "the engine ended this", not two.
    /// </para>
    /// <para>
    /// A transport may cap message size itself and close 1009 before calling; the engine checks again, because the caps are the protocol's and only the engine
    /// knows the session's own <c>clientMessageBytes</c>.
    /// </para>
    /// </remarks>
    void OnMessage(ReadOnlySpan<byte> message);

    /// <summary>
    /// Reports that the link has ended.
    /// </summary>
    /// <param name="code">The close code observed on the wire, or <see cref="Typhon.Protocol.CloseCodes.Normal"/> when there was none to observe.</param>
    /// <param name="error">The transport failure that ended it, or <see langword="null"/> for an orderly close.</param>
    /// <remarks>
    /// <b>Delivered exactly once, for every connection, whoever ended it</b> — including a close the engine itself asked for through
    /// <see cref="ISubscriptionLink.Close"/>. It is what releases the session's row, so a transport that skips it on a path it considers uninteresting leaks a
    /// session slot for the lifetime of the process. It never throws.
    /// </remarks>
    void OnClosed(ushort code, Exception error);
}
