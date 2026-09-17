using JetBrains.Annotations;
using System.Threading.Tasks;

namespace Typhon.Engine;

/// <summary>
/// A listener that turns connections into links: the built-in TCP transport, the ASP.NET Core adapter, or a test fake.
/// </summary>
/// <remarks>
/// <para>
/// <b>A transport owns bytes on a wire; the engine owns the protocol and the session.</b> That split is the whole point of this seam
/// (<c>design/Subscriptions/04-transport.md § 1</c>). A transport accepts, negotiates TLS, frames messages, checks the origin and runs the receive loop. It
/// never decodes a <c>HELLO</c>, never decides who is admitted, never tracks a session and never writes a byte the engine did not hand it.
/// </para>
/// <para>
/// <b>What a transport must do.</b> Call <see cref="ISubscriptionAcceptor.Accept"/> once per connection, with a link that is already usable; deliver each
/// complete client-to-server message to <see cref="ISubscriptionConnection.OnMessage"/>, <b>one at a time and in order</b>; deliver exactly one
/// <see cref="ISubscriptionConnection.OnClosed"/> when the link ends, whoever ended it. Message boundaries are the transport's: WebSocket has them natively,
/// TCP adds a length prefix inside its own link, and the engine never parses a byte stream.
/// </para>
/// <para>
/// <b>What a transport must not do.</b> It must not reorder messages, coalesce two into one, split one into two, hold a
/// <see cref="System.ReadOnlySpan{T}"/> handed to <see cref="ISubscriptionConnection.OnMessage"/> past that call, or call into one connection from two threads
/// at once. It must not interpret a message's contents: a version mismatch is settled by the subprotocol or the preamble it owns
/// (<c>design/Subscriptions/03-wire-protocol.md § 10</c>), never by reading a message body.
/// </para>
/// <para>
/// This interface is not <see cref="System.IDisposable"/> on purpose: stopping a listener drains connections, which is asynchronous, and a synchronous
/// <c>Dispose</c> would either block a shutdown path or lie about having finished.
/// </para>
/// </remarks>
[PublicAPI]
public interface ISubscriptionTransport
{
    /// <summary>
    /// Begins listening, handing every connection to <paramref name="acceptor"/>.
    /// </summary>
    /// <param name="acceptor">The engine's acceptor. It is the only engine surface a transport ever calls into to create a connection.</param>
    /// <remarks>
    /// Called once, from the runtime's start path. A transport that cannot bind throws here, where the failure reaches the host as a start failure rather
    /// than as a server that is running and unreachable.
    /// </remarks>
    void Start(ISubscriptionAcceptor acceptor);

    /// <summary>
    /// Stops listening and closes what is still connected.
    /// </summary>
    /// <returns>A task that completes when no link remains and no receive loop is running.</returns>
    /// <remarks>
    /// Every live link is closed with <see cref="Typhon.Protocol.CloseCodes.GoingAway"/> and each connection is told through
    /// <see cref="ISubscriptionConnection.OnClosed"/>, so a transport that returns without doing so leaves session rows the engine will never reclaim.
    /// </remarks>
    ValueTask StopAsync();
}
