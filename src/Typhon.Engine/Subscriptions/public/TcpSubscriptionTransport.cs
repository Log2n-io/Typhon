using JetBrains.Annotations;
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Typhon.Engine.Internals;
using Typhon.Protocol;

namespace Typhon.Engine;

/// <summary>
/// The engine's own listener: an asynchronous <see cref="Socket"/>, the <c>TYP2</c> preamble, <c>u32 len</c> framing, and no ASP.NET Core anywhere.
/// </summary>
/// <remarks>
/// <para>
/// <b>Who it is for.</b> Native and Unity clients, server-to-server links, bots, tests, and deployments that embed the engine and never host HTTP
/// (<c>design/Subscriptions/04-transport.md § 4</c>). A browser needs the WebSocket adapter, which is a separate package precisely so that everything HTTP —
/// TLS policy, origin checks, upgrade limits — stays out of this assembly (AC-19).
/// </para>
/// <para>
/// <b>It binds loopback unless told otherwise.</b> Nothing here authenticates a peer: <c>HELLO</c>'s token is opaque and reaches the application's admission
/// hook unexamined. The previous implementation bound every interface with no authentication, which is the one default that cannot be walked back once a
/// sample has been copied.
/// </para>
/// <para>
/// <b>What it owns and what it refuses to own.</b> Bytes, framing, TLS, the accept loop and the close. Not a single protocol decision: the preamble is the one
/// comparison it makes, and it is a transport-level version check the wire itself defines as never being a <c>KICK</c>
/// (<c>design/Subscriptions/03-wire-protocol.md § 12 W31</c>). Everything else — the handshake, admission, the caps, the close codes — happens behind
/// <see cref="ISubscriptionAcceptor.Accept"/>.
/// </para>
/// <para>
/// <b>Starting it:</b> after <c>runtime.Start()</c>, <c>runtime.StartSubscriptionTransport(new TcpSubscriptionTransport(new TcpSubscriptionOptions { Port =
/// 9100 }))</c>; stop it with <see cref="StopAsync"/> before shutting the runtime down.
/// </para>
/// </remarks>
[PublicAPI]
public sealed class TcpSubscriptionTransport : ISubscriptionTransport
{
    /// <summary><c>IPPROTO_TCP</c>, the option level <c>TCP_NOTSENT_LOWAT</c> lives at.</summary>
    private const int IpProtoTcp = 6;

    /// <summary><c>TCP_NOTSENT_LOWAT</c> from <c>linux/tcp.h</c>.</summary>
    private const int TcpNotSentLowat = 25;

    private readonly TcpSubscriptionOptions _options;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<TcpSubscriptionLink, byte> _links = new();

    private Socket _listener;
    private ISubscriptionAcceptor _acceptor;
    private Task _acceptLoop;
    private int _started;
    private int _stopped;

    /// <summary>Creates the listener. Nothing is bound until <see cref="Start"/>.</summary>
    /// <param name="options">Where to listen and how, or <see langword="null"/> for the loopback defaults.</param>
    public TcpSubscriptionTransport(TcpSubscriptionOptions options = null)
    {
        _options = options ?? TcpSubscriptionOptions.Default;
        _options.Validate();
    }

    /// <summary>
    /// Where the listener actually bound, which is how an ephemeral port (<c>Port = 0</c>) is discovered. <see langword="null"/> before <see cref="Start"/>.
    /// </summary>
    public IPEndPoint BoundEndPoint { get; private set; }

    /// <summary>How many connections are being served right now, the ones still exchanging their preamble included.</summary>
    public int ConnectionCount => _links.Count;

    /// <inheritdoc />
    public void Start(ISubscriptionAcceptor acceptor)
    {
        ArgumentNullException.ThrowIfNull(acceptor);

        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("This TCP subscription transport has already been started.");
        }

        _acceptor = acceptor;

        // Bound synchronously, and a failure is thrown from here: a listener that could not take its port has to reach the host as a start failure rather
        // than as a server that is running and unreachable (ISubscriptionTransport.Start).
        var listener = new Socket(_options.Address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            listener.Bind(_options.ToEndPoint());
            listener.Listen(_options.Backlog);
        }
        catch
        {
            listener.Dispose();
            throw;
        }

        _listener = listener;
        BoundEndPoint = (IPEndPoint)listener.LocalEndPoint;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    /// <inheritdoc />
    public async ValueTask StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        _stopping.Cancel();
        _listener?.Dispose();

        var accepting = _acceptLoop;
        if (accepting != null)
        {
            await accepting.ConfigureAwait(false);
        }

        // Every live link, with the code the seam names for a listener going down — the ones still in their handshake included, which is why a link is tracked
        // from the moment its socket is wrapped rather than from the moment it is adopted. Each connection is told through OnClosed by the link's own teardown,
        // so a session row is released rather than left for a table nobody will sweep.
        foreach (var pair in _links)
        {
            pair.Key.Close(CloseCodes.GoingAway, "server stopping");
        }

        foreach (var pair in _links)
        {
            await pair.Key.Closed.ConfigureAwait(false);
        }
    }

    // ── accepting ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task AcceptLoopAsync()
    {
        var ct = _stopping.Token;

        while (!ct.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await _listener.AcceptAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                // One connection failing to arrive is not the listener's problem; a disposed listener is, and that is the case above.
                continue;
            }

            _ = ServeAsync(socket);
        }
    }

    private async Task ServeAsync(Socket socket)
    {
        TcpSubscriptionLink link = null;
        try
        {
            Configure(socket);
            var remote = socket.RemoteEndPoint;

            bool speaksThisMajor;

            // The deadline covers TLS and the preamble and is disposed with them: keeping it for the connection's life would hold a timer and a registration on
            // the listener's own token per live session, for a decision that was made in the first few milliseconds.
            using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token))
            {
                handshake.CancelAfter(_options.HandshakeTimeoutMs);

                var stream = await AuthenticateAsync(socket, handshake.Token).ConfigureAwait(false);
                link = new TcpSubscriptionLink(socket, stream, _options);
                _links[link] = 0;
                speaksThisMajor = await link.ExchangePreambleAsync(handshake.Token).ConfigureAwait(false);
            }

            if (!speaksThisMajor)
            {
                // A major mismatch: this side's preamble has already gone out, and that is the whole of what the peer is told. Never a KICK — a future major
                // would have to speak this one's KICK framing to send it (03 § 12 W31).
                link.Close(CloseCodes.ProtocolError, null);
                await link.Closed.ConfigureAwait(false);
                return;
            }

            var info = new LinkInfo { Remote = remote, Transport = "tcp" };
            var connection = _acceptor.Accept(link, info);
            if (connection == null)
            {
                // Nothing was admitted, so there is nothing to tell the client: the link is this transport's to close (ISubscriptionAcceptor.Accept).
                link.Close(CloseCodes.TryAgainLater, null);
                await link.Closed.ConfigureAwait(false);
                return;
            }

            link.Attach(connection);

            await link.RunReceiveLoopAsync().ConfigureAwait(false);
            await link.Closed.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A connection that failed before it was adopted is a socket and nothing else. One that failed afterwards has already been reported through its
            // own teardown, which is the only path that ever touches the engine.
            if (link != null)
            {
                link.Close(CloseCodes.Normal, null);
                await link.Closed.ConfigureAwait(false);
            }
            else
            {
                socket.Dispose();
            }
        }
        finally
        {
            if (link != null)
            {
                _links.TryRemove(link, out _);
            }
        }
    }

    // ── socket and TLS setup ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Applies the settings that make back-pressure visible to the engine.
    /// </summary>
    /// <param name="socket">The accepted socket.</param>
    /// <remarks>
    /// <c>TCP_NOTSENT_LOWAT</c> where the platform has it, a bounded send buffer where it does not. Both answer the same failure: a kernel that absorbs
    /// seconds of frames completes every send immediately, so a client that has stopped keeping up looks exactly like one that is
    /// (<c>design/Subscriptions/04-transport.md § 4</c>).
    /// </remarks>
    private void Configure(Socket socket)
    {
        socket.NoDelay = _options.NoDelay;

        if (OperatingSystem.IsLinux() && _options.NotSentLowWaterMarkBytes > 0)
        {
            try
            {
                Span<byte> value = stackalloc byte[sizeof(int)];
                BitConverter.TryWriteBytes(value, _options.NotSentLowWaterMarkBytes);
                socket.SetRawSocketOption(IpProtoTcp, TcpNotSentLowat, value);
                return;
            }
            catch (SocketException)
            {
                // An older kernel without the option: the bounded send buffer below is the same bound, expressed less precisely.
            }
        }

        if (_options.SendBufferBytes > 0)
        {
            socket.SendBufferSize = _options.SendBufferBytes;
        }
    }

    private async Task<SslStream> AuthenticateAsync(Socket socket, CancellationToken ct)
    {
        var certificate = _options.ServerCertificate;
        if (certificate == null)
        {
            return null;
        }

        var ssl = new SslStream(new NetworkStream(socket, ownsSocket: false), leaveInnerStreamOpen: false);
        try
        {
            await ssl.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ClientCertificateRequired = _options.RequireClientCertificate,
                    EnabledSslProtocols = _options.SslProtocols,
                },
                ct).ConfigureAwait(false);
        }
        catch
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return ssl;
    }
}
