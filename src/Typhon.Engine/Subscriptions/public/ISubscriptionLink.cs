using JetBrains.Annotations;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine;

/// <summary>
/// One connection's bytes, as the engine uses them: a reliable ordered message channel, an optional unreliable one, and a close.
/// </summary>
/// <remarks>
/// <para>
/// <b>Implemented by the transport, one per connection.</b> A WebSocket link wraps a <c>WebSocket</c>, a TCP link wraps a <c>Socket</c> and adds its own
/// length prefix, and an in-process link wraps a queue — an embedded application, a test, or the differential oracle receives real frames over one and nothing
/// in the engine can tell the difference (<c>design/Subscriptions/04-transport.md § 2</c>).
/// </para>
/// <para>
/// <b>The engine pushes; it never polls.</b> Send pumps walk the sessions that produced a frame this tick and call <see cref="SendAsync"/> for each published
/// frame, strictly in order, <b>at most one in flight per session</b>. That guarantee is the engine's, so a link needs no queue of its own and no ordering
/// logic: it may assume the previous send completed before the next one starts.
/// </para>
/// </remarks>
[PublicAPI]
public interface ISubscriptionLink
{
    /// <summary>
    /// Sends one complete server-to-client message, reliably and in order.
    /// </summary>
    /// <param name="message">
    /// The message's bytes. <b>Borrowed for the duration of the returned task and not a byte longer</b> — it is engine-owned native memory that is recycled
    /// the moment the task completes, so a link that keeps it, or that completes the task before the bytes have been handed to the socket, reads freed memory.
    /// </param>
    /// <param name="ct">Cancels the send. A cancelled send closes the link: a partially written message cannot be un-sent.</param>
    /// <returns>A task that completes when the link no longer needs <paramref name="message"/>.</returns>
    /// <remarks>
    /// <b>Back-pressure belongs here.</b> A link that completes the task as soon as the bytes reach a kernel buffer tells the engine a slow client is keeping
    /// up, and the lag skip then has nothing to see; that is why the TCP transport bounds its send buffer and the WebSocket adapter caps kernel queueing. A
    /// failure is reported by faulting the task, never by throwing synchronously after the message was partly written.
    /// </remarks>
    ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken ct);

    /// <summary>
    /// Whether this link has an unreliable channel beside the reliable one.
    /// </summary>
    /// <remarks>
    /// <see langword="false"/> on TCP and WebSocket, which have no such thing. It exists for the WebTransport path, whose datagrams would carry per-tick
    /// motion snapshots beside — never instead of — the reliable blocks.
    /// </remarks>
    bool SupportsUnreliable { get; }

    /// <summary>
    /// Sends a datagram, best effort.
    /// </summary>
    /// <param name="datagram">The bytes; copied by the link before this returns, or not sent at all.</param>
    /// <returns>
    /// <see langword="false"/> when the link has no unreliable channel or the datagram could not be queued. Never an exception, never a wait.
    /// </returns>
    bool TrySendUnreliable(ReadOnlySpan<byte> datagram);

    /// <summary>
    /// Closes the link.
    /// </summary>
    /// <param name="code">The close code, from <see cref="Typhon.Protocol.CloseCodes"/>. It is a number: a WebSocket close frame carries it big-endian while a
    /// <c>KICK</c> carries it little-endian, so a link passes the value to its own API and never copies bytes between the two.</param>
    /// <param name="reason">Why, at most <see cref="Typhon.Protocol.ProtocolConstants.KickReasonMaxBytes"/> UTF-8 bytes — already truncated at a code-point
    /// boundary by the engine, so a link hands it straight to a close frame. May be <see langword="null"/>.</param>
    /// <remarks>
    /// <b>Called after the engine has already sent whatever the client needs to understand the close</b> — a <c>KICK</c> carrying the same code, except for a
    /// protocol-major mismatch, which is never a <c>KICK</c>. A link must therefore let queued sends reach the peer before it closes, and must still deliver
    /// exactly one <see cref="ISubscriptionConnection.OnClosed"/> afterwards. Calling it twice is a no-op.
    /// </remarks>
    void Close(ushort code, string reason);
}
