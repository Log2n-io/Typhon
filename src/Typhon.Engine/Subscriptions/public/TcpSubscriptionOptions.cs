using JetBrains.Annotations;
using System;
using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Typhon.Protocol;

namespace Typhon.Engine;

/// <summary>
/// What the built-in TCP transport listens on, and the few socket settings a real deployment has to be able to move.
/// </summary>
/// <remarks>
/// <para>
/// <b>It binds <see cref="IPAddress.Loopback"/> unless told otherwise.</b> The transport authenticates nothing by itself — <c>HELLO</c>'s token reaches the
/// application's admission hook and that is the whole of it (<c>design/Subscriptions/04-transport.md § 3</c>) — so a default that bound every interface would
/// put an unauthenticated listener on the public internet the first time somebody ran the sample. The previous implementation did exactly that, which is why
/// the default is named in the design rather than left to whoever writes the host.
/// </para>
/// <para>
/// <b>The send-side knobs exist for the lag skip, not for throughput.</b> A kernel that happily buffers seconds of frames makes a slow client look fast: the
/// send completes, the engine is told the client kept up, and the ack-based skip has nothing to see until the backlog is already unrecoverable. Bounding what
/// the kernel will absorb — <c>TCP_NOTSENT_LOWAT</c> where the platform has it, a small <see cref="System.Net.Sockets.Socket.SendBufferSize"/> where it does
/// not — is what keeps back-pressure visible to the engine (<c>design/Subscriptions/04-transport.md § 4</c>).
/// </para>
/// </remarks>
[PublicAPI]
public sealed class TcpSubscriptionOptions
{
    /// <summary>
    /// The address to bind. <see cref="IPAddress.Loopback"/> by default: exposing the listener beyond the machine is an explicit decision, because nothing in
    /// this transport authenticates a peer.
    /// </summary>
    public IPAddress Address { get; init; } = IPAddress.Loopback;

    /// <summary>
    /// The port to bind. Zero — the default — takes an ephemeral port from the operating system and publishes it as the transport's bound endpoint, which is
    /// what a test wants; a host that clients have to find sets the port it advertises.
    /// </summary>
    public int Port { get; init; }

    /// <summary>How many connections the kernel may hold half-accepted before it refuses them.</summary>
    public int Backlog { get; init; } = 128;

    /// <summary>
    /// Whether to disable Nagle's algorithm. On by default: a frame is a whole message the client cannot act on in halves, so delaying a small one to coalesce
    /// it with the next tick's costs a tick of latency for a few bytes.
    /// </summary>
    public bool NoDelay { get; init; } = true;

    /// <summary>
    /// The socket send buffer, in bytes, on platforms without <c>TCP_NOTSENT_LOWAT</c> (Windows). It bounds what the kernel absorbs before a send stops
    /// completing immediately, which is what makes a slow client observable to the lag skip rather than invisible behind a megabyte of kernel queue.
    /// </summary>
    public int SendBufferBytes { get; init; } = 64 * 1024;

    /// <summary>
    /// The <c>TCP_NOTSENT_LOWAT</c> watermark, in bytes, on platforms that have it (Linux). A send completes once the unsent backlog drops below it, so the
    /// same bound is expressed without shrinking the window the kernel may use for data already in flight.
    /// </summary>
    public int NotSentLowWaterMarkBytes { get; init; } = 16 * 1024;

    /// <summary>
    /// The largest framed client message this transport will read after the first one, in bytes. The first message is <c>HELLO</c> and is bounded by
    /// <see cref="ProtocolConstants.HelloMaxBytes"/> instead, because a client must be able to send a token before it has been told any session's limit.
    /// </summary>
    /// <remarks>
    /// This is the transport's own ceiling and it is deliberately the loosest of the two: the session's <c>clientMessageBytes</c> is the protocol's limit and
    /// only the engine knows it, so a message that passes here can still be refused 1009 by the connection. What this number buys is that an absurd length
    /// prefix never turns into an allocation or a read.
    /// </remarks>
    public int MaxInboundMessageBytes { get; init; } = ProtocolConstants.HelloMaxBytes;

    /// <summary>
    /// How long a freshly accepted socket has to complete TLS and the 4-byte preamble, in milliseconds, before it is dropped. The <c>HELLO</c> deadline is the
    /// connection's own and starts after this (<see cref="ProtocolConstants.HelloTimeoutMs"/>).
    /// </summary>
    public int HandshakeTimeoutMs { get; init; } = ProtocolConstants.HelloTimeoutMs;

    /// <summary>
    /// How long a close waits for an in-flight send to reach the kernel before it stops waiting, in milliseconds. A <c>KICK</c> is written and then the link is
    /// closed, so this is what makes the reason arrive rather than race the <c>FIN</c>.
    /// </summary>
    public int CloseDrainTimeoutMs { get; init; } = 2000;

    /// <summary>
    /// The server certificate, or <see langword="null"/> for a plaintext listener. When set, every connection is wrapped in an <c>SslStream</c> and the
    /// preamble is exchanged inside TLS, as <c>design/Subscriptions/03-wire-protocol.md § 10</c> requires.
    /// </summary>
    public X509Certificate2 ServerCertificate { get; init; }

    /// <summary>
    /// The TLS versions to offer. <see cref="SslProtocols.None"/> — the default — takes the operating system's own policy, which is the correct one.
    /// </summary>
    public SslProtocols SslProtocols { get; init; } = SslProtocols.None;

    /// <summary>Whether the TLS handshake asks for a client certificate. Ignored without a <see cref="ServerCertificate"/>.</summary>
    public bool RequireClientCertificate { get; init; }

    /// <summary>Every default: loopback, an ephemeral port, no TLS.</summary>
    public static TcpSubscriptionOptions Default { get; } = new();

    /// <summary>The endpoint <see cref="Address"/> and <see cref="Port"/> name.</summary>
    /// <returns>The endpoint to bind.</returns>
    public IPEndPoint ToEndPoint() => new(Address, Port);

    /// <summary>
    /// Refuses a configuration that cannot work, before a socket exists.
    /// </summary>
    /// <exception cref="ArgumentException">A value is out of range.</exception>
    internal void Validate()
    {
        if (Address == null)
        {
            throw new ArgumentException("A TCP subscription listener needs an address to bind; loopback is the default.", nameof(Address));
        }

        if (Port is < 0 or > 65535)
        {
            throw new ArgumentException($"Port {Port} is not a TCP port. Zero takes an ephemeral one.", nameof(Port));
        }

        if (Backlog <= 0)
        {
            throw new ArgumentException($"A backlog of {Backlog} accepts nothing.", nameof(Backlog));
        }

        if (MaxInboundMessageBytes < ProtocolConstants.HelloMaxBytes)
        {
            throw new ArgumentException(
                $"MaxInboundMessageBytes ({MaxInboundMessageBytes}) is below the HELLO limit of {ProtocolConstants.HelloMaxBytes} bytes, so a legal " +
                "handshake would be refused 1009 before it was read.",
                nameof(MaxInboundMessageBytes));
        }

        if (HandshakeTimeoutMs <= 0)
        {
            throw new ArgumentException("A handshake deadline of zero drops every connection before it can speak.", nameof(HandshakeTimeoutMs));
        }

        if (CloseDrainTimeoutMs < 0)
        {
            throw new ArgumentException("A negative close drain is not a duration.", nameof(CloseDrainTimeoutMs));
        }
    }
}
