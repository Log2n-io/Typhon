using JetBrains.Annotations;
using System.Net;
using System.Security.Claims;

namespace Typhon.Engine;

/// <summary>
/// What a transport knows about a peer before the peer has said anything.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is <b>transport</b> knowledge, which is why it arrives on this side of the seam rather than inside <c>HELLO</c>: an address the client
/// cannot forge, the wire it came in on, and — when an adapter authenticated the HTTP request before the upgrade — the principal that authentication produced.
/// What the client itself claims (its kind, its token, its capabilities) travels in <c>HELLO</c> and reaches the application through
/// <see cref="AdmissionRequest"/> instead.
/// </para>
/// <para>
/// Every member may be <see langword="null"/>: an in-process link has no endpoint, a TCP link has no subprotocol, and nothing authenticates a connection until
/// the security work lands an authenticator (<c>design/Subscriptions/04-transport.md § 3</c>).
/// </para>
/// </remarks>
[PublicAPI]
public readonly struct LinkInfo
{
    /// <summary>The peer's address as the transport sees it, or <see langword="null"/> for an in-process link.</summary>
    public EndPoint Remote { get; init; }

    /// <summary>The wire this connection came in on: <c>tcp</c>, <c>ws</c>, <c>webtransport</c>, <c>fake</c>. It reaches the admission hook verbatim.</summary>
    public string Transport { get; init; }

    /// <summary>The negotiated subprotocol — <c>typhon.3</c> on WebSocket — or <see langword="null"/> on a wire that has none.</summary>
    public string SubProtocol { get; init; }

    /// <summary>
    /// The principal the adapter's authentication produced, or <see langword="null"/>. Always <see langword="null"/> until the security work lands: the first
    /// version authenticates nothing at the upgrade and hands <c>HELLO</c>'s opaque token to the application instead.
    /// </summary>
    public ClaimsPrincipal User { get; init; }

    /// <inheritdoc />
    public override string ToString() => $"{Transport ?? "?"} {Remote?.ToString() ?? "in-process"}";
}
