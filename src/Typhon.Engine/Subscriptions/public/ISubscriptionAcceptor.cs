using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// The engine's side of the transport seam: it turns a link into a connection that speaks the protocol.
/// </summary>
/// <remarks>
/// <b>Implemented by the engine, called by a transport.</b> It is the only way a transport reaches replication, and it is deliberately one method: everything a
/// connection needs afterwards — the handshake, admission, the session, the send loop, ingress — is behind the
/// <see cref="ISubscriptionConnection"/> it hands back.
/// </remarks>
[PublicAPI]
public interface ISubscriptionAcceptor
{
    /// <summary>
    /// Adopts one connection.
    /// </summary>
    /// <param name="link">The transport's link, already usable for sending and closing.</param>
    /// <param name="info">What the transport knows about the peer.</param>
    /// <returns>
    /// The connection to deliver messages to, or <see langword="null"/> when the engine declines it outright — replication is not running, or the runtime is
    /// stopping. A <see langword="null"/> means the transport closes the link itself; nothing was admitted, so there is nothing to tell the client about.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>It returns before the client has said anything.</b> The connection starts out waiting for <c>HELLO</c> and closes itself with
    /// <see cref="Typhon.Protocol.CloseCodes.HelloTimeout"/> if one does not arrive within <see cref="Typhon.Protocol.ProtocolConstants.HelloTimeoutMs"/>,
    /// so a transport never has to run a connect deadline of its own.
    /// </para>
    /// <para>
    /// <b>A refusal is not a <see langword="null"/>.</b> A client the application's admission hook rejects, or one that does not fit the session table, is
    /// admitted far enough to be told why: it gets a <c>KICK</c> with a close code and then the link is closed. That happens inside the connection, after
    /// this call.
    /// </para>
    /// </remarks>
    ISubscriptionConnection Accept(ISubscriptionLink link, in LinkInfo info);
}
