using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Typhon.Engine;
using Typhon.Protocol;

namespace Typhon.Subscriptions.AspNetCore.Tests;

/// <summary>
/// P1-08 — the browser's door, driven through TestHost: a real HTTP request, a real upgrade, a real WebSocket.
/// </summary>
/// <remarks>
/// <para>
/// <b>The acceptor is a fake and the socket is not.</b> What this endpoint adds over the engine's own end-to-end coverage is exactly the HTTP half — the
/// refusals before the upgrade, the subprotocol, the keep-alive, the receive loop's message boundaries and the close — so that is what is exercised here,
/// against an acceptor that records what it was handed. The engine's side of the seam is covered where it lives, over an in-process link.
/// </para>
/// <para>
/// <b>Every refusal is a status code, not a socket that closes.</b> A browser that is told 403 can show its user something; a browser whose socket opens and
/// closes a millisecond later cannot tell a policy refusal from a network fault.
/// </para>
/// </remarks>
[TestFixture]
public class WebSocketEndpointTests
{
    /// <summary>An endpoint with no allowed origin refuses to be mapped at all.</summary>
    /// <remarks>
    /// The framework's own <c>WebSocketOptions.AllowedOrigins</c> treats empty as "allow everything", which on a replication socket is a default nobody
    /// chose. Failing at mapping time means the operator finds out when they start the host, not when somebody else's page opens a session.
    /// </remarks>
    [Test]
    public void AnEmptyOriginListRefusesToStart()
    {
        var options = new TyphonSubscriptionsOptions();

        var error = Assert.Throws<InvalidOperationException>(() => options.Validate());
        Assert.That(error.Message, Does.Contain("AllowAnyOrigin"), "the message has to name the way out, or it reads as a bug in the package");
    }

    /// <summary>A missing <c>Origin</c> is allowed, a listed one is allowed, an unlisted one is not.</summary>
    [Test]
    public void OnlyListedOriginsAreAllowed()
    {
        var options = new TyphonSubscriptionsOptions().AllowOrigin("https://demo.example/");

        Assert.Multiple(() =>
        {
            Assert.That(options.IsOriginAllowed(null), Is.True, "a native client sends no Origin and is not a browser");
            Assert.That(options.IsOriginAllowed("https://demo.example"), Is.True, "the trailing slash is not part of an origin");
            Assert.That(options.IsOriginAllowed("https://DEMO.example"), Is.True, "origins are compared case-insensitively");
            Assert.That(options.IsOriginAllowed("https://evil.example"), Is.False);
            Assert.That(new TyphonSubscriptionsOptions().AllowAnyOrigin().IsOriginAllowed("https://evil.example"), Is.True, "deliberately, when asked");
        });
    }

    /// <summary>A plain GET to the endpoint is 400, not an upgrade and not a 500.</summary>
    [Test]
    public async Task ANonWebSocketRequestIsRefused()
    {
        using var host = await StartAsync(new RecordingAcceptor()).ConfigureAwait(false);
        var response = await host.GetTestClient().GetAsync("/ws").ConfigureAwait(false);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    /// <summary>An origin that was not allowed is 403 before the upgrade.</summary>
    [Test]
    public async Task ADisallowedOriginIsRefusedBeforeTheUpgrade()
    {
        var acceptor = new RecordingAcceptor();
        using var host = await StartAsync(acceptor, o => o.AllowOrigin("https://demo.example")).ConfigureAwait(false);

        var client = host.GetTestServer().CreateWebSocketClient();
        client.ConfigureRequest = request =>
        {
            request.Headers["Sec-WebSocket-Protocol"] = ProtocolConstants.WebSocketSubprotocol;
            request.Headers["Origin"] = "https://evil.example";
        };

        // TestHost surfaces a refused upgrade as a throw carrying the status, which is also what a browser's WebSocket constructor reports: the handshake
        // failed, with a status that says why, rather than a socket that opens and closes.
        var error = Assert.ThrowsAsync<InvalidOperationException>(
            async () => await client.ConnectAsync(new Uri("http://localhost/ws"), CancellationToken.None).ConfigureAwait(false));

        Assert.Multiple(() =>
        {
            Assert.That(error.Message, Does.Contain("403"), "an unlisted origin is refused as a policy, not as a protocol error");
            Assert.That(acceptor.Link, Is.Null, "the acceptor must never see a connection the origin check refused");
        });
    }

    /// <summary>
    /// A client that completes the upgrade gets a session, and the bytes it sends arrive whole.
    /// </summary>
    /// <remarks>
    /// The message boundary is the part worth pinning: a WebSocket delivers frames, the protocol expects messages, and a receive loop that handed a partial
    /// frame to the decoder would fail as a malformed payload rather than as the framing bug it is.
    /// </remarks>
    [Test]
    public async Task AConnectedClientsMessagesArriveWhole()
    {
        var acceptor = new RecordingAcceptor();
        using var host = await StartAsync(acceptor).ConfigureAwait(false);

        var client = host.GetTestServer().CreateWebSocketClient();
        client.ConfigureRequest = request => request.Headers["Sec-WebSocket-Protocol"] = ProtocolConstants.WebSocketSubprotocol;

        using var socket = await client.ConnectAsync(new Uri("http://localhost/ws"), CancellationToken.None).ConfigureAwait(false);

        // Sent as TWO frames, because reassembly is the loop's only non-trivial logic and a single frame exercises none of it. A loop that handed each frame
        // to the decoder would fail as a malformed payload, which reads as a protocol bug rather than as the framing bug it is.
        var message = new byte[] { MessageTypes.Hello, 1, 2, 3, 4, 5 };
        await socket.SendAsync(message.AsMemory(0, 3), WebSocketMessageType.Binary, endOfMessage: false, CancellationToken.None).ConfigureAwait(false);
        await socket.SendAsync(message.AsMemory(3), WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None).ConfigureAwait(false);

        Assert.That(SpinWait.SpinUntil(() => acceptor.Connection != null && acceptor.Connection.Messages.Count > 0, TimeSpan.FromSeconds(5)), Is.True,
            "the message never reached the connection");

        Assert.Multiple(() =>
        {
            Assert.That(acceptor.Info.Transport, Is.EqualTo("ws"), "the name 04 § 2 gives it, beside the TCP transport's \"tcp\"");
            Assert.That(acceptor.Info.SubProtocol, Is.EqualTo(ProtocolConstants.WebSocketSubprotocol));
            Assert.That(acceptor.Connection.Messages, Has.Count.EqualTo(1), "two frames are one message, not two");
            Assert.That(acceptor.Connection.Messages.TryTake(out var received), Is.True);
            Assert.That(received, Is.EqualTo(message).AsCollection, "a whole message, not a frame of one");
        });

        // The engine closing the session ends the socket, which is the path a KICK takes.
        acceptor.Link.Close(CloseCodes.TryAgainLater, "full");
        Assert.That(SpinWait.SpinUntil(() => acceptor.Connection.Closed, TimeSpan.FromSeconds(5)), Is.True,
            "the receive loop never reported the close, so the session would leak its slot");
        Assert.That(acceptor.Connection.CloseCode, Is.EqualTo(CloseCodes.TryAgainLater),
            "the code the engine closed with has to reach the connection, or the session's Closed event names the wrong reason");
    }

    /// <summary>An acceptor that refuses gets no receive loop, and the client is closed rather than left open.</summary>
    [Test]
    public async Task ARefusedConnectionIsClosed()
    {
        var acceptor = new RecordingAcceptor { Refuse = true };
        using var host = await StartAsync(acceptor).ConfigureAwait(false);

        var client = host.GetTestServer().CreateWebSocketClient();
        client.ConfigureRequest = request => request.Headers["Sec-WebSocket-Protocol"] = ProtocolConstants.WebSocketSubprotocol;

        using var socket = await client.ConnectAsync(new Uri("http://localhost/ws"), CancellationToken.None).ConfigureAwait(false);

        var buffer = new byte[16];
        var result = await socket.ReceiveAsync(buffer, CancellationToken.None).ConfigureAwait(false);

        Assert.That(result.MessageType, Is.EqualTo(WebSocketMessageType.Close), "a refused client must be told, not left connected to nothing");
    }

    /// <summary>A client asking for a subprotocol this endpoint does not speak is refused in HTTP, before any upgrade.</summary>
    /// <remarks>
    /// Step 3. A major-version mismatch has to fail here rather than inside a decoder, because a client speaking another major cannot be assumed to parse
    /// this one's framing — including the close frame that would tell it why.
    /// </remarks>
    [Test]
    public async Task AnUnknownSubprotocolIsRefused()
    {
        var acceptor = new RecordingAcceptor();
        using var host = await StartAsync(acceptor).ConfigureAwait(false);

        var client = host.GetTestServer().CreateWebSocketClient();
        client.ConfigureRequest = request => request.Headers["Sec-WebSocket-Protocol"] = "typhon.99";

        var error = Assert.ThrowsAsync<InvalidOperationException>(
            async () => await client.ConnectAsync(new Uri("http://localhost/ws"), CancellationToken.None).ConfigureAwait(false));

        Assert.Multiple(() =>
        {
            Assert.That(error.Message, Does.Contain("400"));
            Assert.That(acceptor.Link, Is.Null, "a client speaking another major must never reach the acceptor");
        });
    }

    /// <summary>
    /// A message above the transport's ceiling is closed 1009, and the client is told rather than having its socket disposed under it.
    /// </summary>
    /// <remarks>
    /// The difference matters: a disposed socket reaches a browser as 1006, which it cannot tell from a dropped connection, so a client that sent something
    /// too large would retry the same thing forever.
    /// </remarks>
    [Test]
    public async Task AnOverCapMessageIsClosedAndNotDropped()
    {
        var acceptor = new RecordingAcceptor();
        using var host = await StartAsync(acceptor, o => o.AllowAnyOrigin()).ConfigureAwait(false);

        var client = host.GetTestServer().CreateWebSocketClient();
        client.ConfigureRequest = request => request.Headers["Sec-WebSocket-Protocol"] = ProtocolConstants.WebSocketSubprotocol;

        using var socket = await client.ConnectAsync(new Uri("http://localhost/ws"), CancellationToken.None).ConfigureAwait(false);

        // One message larger than the HELLO cap, in continuation frames so the loop has to notice the cap rather than the framing.
        var chunk = new byte[4096];
        chunk[0] = MessageTypes.Hello;
        for (var sent = 0; sent < ProtocolConstants.HelloMaxBytes + chunk.Length; sent += chunk.Length)
        {
            try
            {
                await socket.SendAsync(chunk, WebSocketMessageType.Binary, endOfMessage: false, CancellationToken.None).ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
                // The endpoint closed mid-send, which is the outcome under test.
                break;
            }
        }

        Assert.That(SpinWait.SpinUntil(() => acceptor.Connection is { Closed: true }, TimeSpan.FromSeconds(5)), Is.True,
            "an over-cap message must end the session, not be silently dropped");
        Assert.That(acceptor.Connection.CloseCode, Is.EqualTo(CloseCodes.MessageTooBig));
        Assert.That(acceptor.Connection.Messages, Is.Empty, "the message must be refused by its length, before a byte of it is decoded");
    }

    /// <summary>
    /// A connection whose <c>OnMessage</c> throws is still reported closed exactly once.
    /// </summary>
    /// <remarks>
    /// The hole this closes: <c>SubscriptionConnection.OnMessage</c> catches wire faults, but an application's admission hook can throw anything, and such an
    /// exception escaped the receive loop with the session still open. A session nothing ever closes holds its slot, its frames and its ingress ring for
    /// the life of the process — a leak with no error anywhere.
    /// </remarks>
    [Test]
    public async Task AConnectionThatThrowsIsStillReportedClosedOnce()
    {
        var acceptor = new RecordingAcceptor { ThrowOnMessage = true };
        using var host = await StartAsync(acceptor).ConfigureAwait(false);

        var client = host.GetTestServer().CreateWebSocketClient();
        client.ConfigureRequest = request => request.Headers["Sec-WebSocket-Protocol"] = ProtocolConstants.WebSocketSubprotocol;

        using var socket = await client.ConnectAsync(new Uri("http://localhost/ws"), CancellationToken.None).ConfigureAwait(false);
        await socket.SendAsync(new byte[] { MessageTypes.Hello }, WebSocketMessageType.Binary, true, CancellationToken.None).ConfigureAwait(false);

        Assert.That(SpinWait.SpinUntil(() => acceptor.Connection is { Closed: true }, TimeSpan.FromSeconds(5)), Is.True,
            "the session was left open after its connection threw, which leaks its slot for the life of the process");
        Assert.That(acceptor.Connection.CloseCount, Is.EqualTo(1), "exactly once — a second Closed event would reach the application twice");
    }

    /// <summary>The catalog endpoint answers 503 rather than an empty body when no runtime is registered.</summary>
    [Test]
    public async Task TheCatalogIsUnavailableWithoutARuntime()
    {
        using var host = await StartAsync(new RecordingAcceptor()).ConfigureAwait(false);
        var response = await host.GetTestClient().GetAsync("/typhon/catalog.json").ConfigureAwait(false);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
    }

    // ── harness ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static async Task<IHost> StartAsync(RecordingAcceptor acceptor, Action<TyphonSubscriptionsOptions> configure = null)
    {
        var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddTyphonSubscriptions(configure ?? (o => o.AllowAnyOrigin()));
                    services.AddRouting();
                })
                .Configure(app =>
                {
                    app.UseWebSockets();
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapTyphonSubscriptions("/ws");
                        endpoints.MapTyphonCatalog("/typhon/catalog.json");
                    });
                }))
            .StartAsync()
            .ConfigureAwait(false);

        // Bound directly, standing in for the runtime handing its acceptor over at Start. The endpoint's own lazy binding needs a TyphonRuntime, which this
        // fixture deliberately does not have: what is under test is the HTTP half.
        host.Services.GetRequiredService<WebSocketSubscriptionTransport>().Start(acceptor);
        return host;
    }

    private sealed class RecordingAcceptor : ISubscriptionAcceptor
    {
        public bool Refuse { get; init; }

        public bool ThrowOnMessage { get; init; }

        public LinkInfo Info { get; private set; }

        public ISubscriptionLink Link { get; private set; }

        public RecordingConnection Connection { get; private set; }

        public ISubscriptionConnection Accept(ISubscriptionLink link, in LinkInfo info)
        {
            Link = link;
            Info = info;

            if (Refuse)
            {
                return null;
            }

            Connection = new RecordingConnection { ThrowOnMessage = ThrowOnMessage };
            return Connection;
        }
    }

    private sealed class RecordingConnection : ISubscriptionConnection
    {
        private int _closes;

        public bool ThrowOnMessage { get; init; }

        public ConcurrentBag<byte[]> Messages { get; } = [];

        public bool Closed => Volatile.Read(ref _closes) != 0;

        public int CloseCount => Volatile.Read(ref _closes);

        public ushort CloseCode { get; private set; }

        public void OnMessage(ReadOnlySpan<byte> message)
        {
            if (ThrowOnMessage)
            {
                // Not a WireFormatException: the point is an exception the connection does NOT classify, which is what an application's admission hook can
                // raise and what used to escape the receive loop.
                throw new InvalidOperationException("the application's hook threw");
            }

            Messages.Add(message.ToArray());
        }

        public void OnClosed(ushort code, Exception error)
        {
            CloseCode = code;
            Interlocked.Increment(ref _closes);
        }
    }
}
