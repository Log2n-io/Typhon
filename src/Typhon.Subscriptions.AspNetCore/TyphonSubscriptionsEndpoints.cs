using JetBrains.Annotations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Buffers;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Typhon.Engine;
using Typhon.Protocol;

namespace Typhon.Subscriptions.AspNetCore;

/// <summary>
/// The browser's door: <c>AddTyphonSubscriptions</c>, <c>MapTyphonSubscriptions</c> and <c>MapTyphonCatalog</c>.
/// </summary>
/// <remarks>
/// <para>
/// Steps 1–6 of <c>design/Subscriptions/04-transport.md § 5</c>, in order: a WebSocket request, an allowed origin, the <c>typhon.2</c> subprotocol, an
/// accept with explicit keep-alive, an acceptor that may refuse, and a receive loop bounded by the protocol's caps. <b>Step 7 — capping kernel queueing
/// through <c>TCP_NOTSENT_LOWAT</c> on the upgraded socket — is not built.</b> Without it a send can complete as soon as the bytes reach a kernel buffer,
/// which tells the engine a slow client is keeping up and leaves the acknowledgement-based lag skip reading a backlog bounded by the OS rather than by us.
/// </para>
/// <para>
/// <b>No <c>permessage-deflate</c>.</b> The payloads are quantized and compress 1.1–1.5× at best, zlib state costs about 268 KB per connection, per-connection
/// compression defeats the encode-once path shared frames rest on, and it is a CRIME/BREACH-class risk on a stream that carries session state.
/// </para>
/// </remarks>
[PublicAPI]
public static class TyphonSubscriptionsEndpoints
{
    /// <summary>Set once the transport's refusal has been logged, so a misconfiguration is reported rather than repeated.</summary>
    private static int _startRefusalLogged;

    /// <summary>
    /// Registers the WebSocket transport and its options.
    /// </summary>
    /// <param name="services">The host's services.</param>
    /// <param name="configure">Configures the options; at least one allowed origin, or <c>AllowAnyOrigin()</c>.</param>
    /// <returns>The services, for chaining.</returns>
    public static IServiceCollection AddTyphonSubscriptions(this IServiceCollection services, Action<TyphonSubscriptionsOptions> configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new TyphonSubscriptionsOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);
        services.AddSingleton<WebSocketSubscriptionTransport>();
        return services;
    }

    /// <summary>
    /// Maps the replication endpoint.
    /// </summary>
    /// <param name="endpoints">The host's endpoint builder.</param>
    /// <param name="pattern">The route, conventionally <c>/ws</c>.</param>
    /// <returns>The endpoint, so auth and rate limiting compose onto it.</returns>
    /// <remarks>
    /// The transport is started against the runtime on the first request rather than at mapping time, because a host maps its endpoints before it starts the
    /// runtime as often as after. Starting it lazily makes the order the operator's business instead of a documented trap.
    /// </remarks>
    public static IEndpointConventionBuilder MapTyphonSubscriptions(this IEndpointRouteBuilder endpoints, string pattern = "/ws")
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var options = endpoints.ServiceProvider.GetRequiredService<TyphonSubscriptionsOptions>();
        options.Validate();

        return endpoints.Map(pattern, static (HttpContext http) => HandleAsync(http)).WithDisplayName("Typhon subscriptions");
    }

    /// <summary>
    /// Maps the catalog endpoint, serving exactly the bytes <c>WELCOME</c> carries.
    /// </summary>
    /// <param name="endpoints">The host's endpoint builder.</param>
    /// <param name="pattern">The route, conventionally <c>/typhon/catalog.json</c>.</param>
    /// <returns>The endpoint.</returns>
    public static IEndpointConventionBuilder MapTyphonCatalog(this IEndpointRouteBuilder endpoints, string pattern = "/typhon/catalog.json")
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        return endpoints.MapGet(pattern, static async (HttpContext http) =>
        {
            var runtime = http.RequestServices.GetService<TyphonRuntime>();
            var catalog = runtime?.SubscriptionsCatalogJson ?? default;
            if (catalog.IsEmpty)
            {
                http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }

            http.Response.ContentType = "application/json; charset=utf-8";
            await http.Response.Body.WriteAsync(catalog, http.RequestAborted).ConfigureAwait(false);
        }).WithDisplayName("Typhon catalog");
    }

    private static async Task HandleAsync(HttpContext http)
    {
        var options = http.RequestServices.GetRequiredService<TyphonSubscriptionsOptions>();
        var transport = http.RequestServices.GetRequiredService<WebSocketSubscriptionTransport>();

        // Step 1 — a WebSocket request, and nothing else. A browser that navigated here gets a plain status rather than a socket that closes immediately.
        if (!http.WebSockets.IsWebSocketRequest)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        // Step 2 — the origin. A WebSocket is not subject to the same-origin policy, so this is the check that stands in for it.
        if (!options.IsOriginAllowed(http.Request.Headers.Origin))
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        // Step 3 — the subprotocol. A major-version mismatch fails here, in HTTP, rather than inside a decoder that cannot assume the framing.
        if (!SupportsOurSubprotocol(http.WebSockets.WebSocketRequestedProtocols))
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var acceptor = Acceptor(http, transport);
        if (acceptor == null)
        {
            http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        // Step 4 — accept with explicit keep-alive. Both values are set because the framework's defaults are two minutes and infinite, which together mean a
        // dead connection holds its session for as long as the process lives.
        using var socket = await http.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
        {
            SubProtocol = ProtocolConstants.WebSocketSubprotocol,
            KeepAliveInterval = options.KeepAliveInterval,
            KeepAliveTimeout = options.KeepAliveTimeout,
        }).ConfigureAwait(false);

        var link = new WebSocketLink(socket);
        try
        {
            // Step 5 — the acceptor may refuse: replication is not running, or the runtime is stopping.
            var connection = acceptor.Accept(link, new LinkInfo
            {
                Remote = RemoteEndPoint(http),
                Transport = "ws",
                SubProtocol = ProtocolConstants.WebSocketSubprotocol,
                User = http.User,
            });

            if (connection == null)
            {
                await socket.CloseOutputAsync((WebSocketCloseStatus)CloseCodes.TryAgainLater, "not accepting", http.RequestAborted).ConfigureAwait(false);
                return;
            }

            // Step 6 — the receive loop.
            await ReceiveLoopAsync(socket, link, connection, options, http.RequestAborted).ConfigureAwait(false);
        }
        finally
        {
            // Before the `using` above disposes the socket. A close frame the engine started is in flight at exactly this moment in the KICK case, and
            // disposing under it turns the code the client needed into an abort it reads as 1006.
            await link.DrainCloseAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            link.Dispose();
        }
    }

    /// <summary>
    /// Reads whole messages and hands each to the connection, bounded by the protocol's caps.
    /// </summary>
    /// <param name="socket">The accepted socket.</param>
    /// <param name="link">The link, for the engine's own close.</param>
    /// <param name="connection">The engine's connection.</param>
    /// <param name="options">The operator's rails, for the inbound ceiling that applies after the first message.</param>
    /// <param name="requestAborted">The host's shutdown token.</param>
    /// <returns>The loop.</returns>
    /// <remarks>
    /// <para>
    /// <b>The cap is the first message's until the first message has been read.</b> <c>HELLO</c> may be 16 KiB because an authentication token has to fit in
    /// it; everything after it is bounded by the session's <c>clientMessageBytes</c>, which the catalog exports so an SDK batches under it. A message above
    /// the cap is closed 1009 <b>before</b> it is decoded, which is what makes the bound a defence rather than a diagnostic.
    /// </para>
    /// <para>
    /// The buffer is rented once for the connection, not per message: a client sends a few hundred bytes a few times a second, and renting per receive would
    /// put the pool on the receive path for no benefit.
    /// </para>
    /// </remarks>
    private static async Task ReceiveLoopAsync(WebSocket socket, WebSocketLink link, ISubscriptionConnection connection, TyphonSubscriptionsOptions options,
        CancellationToken requestAborted)
    {
        var cap = ProtocolConstants.HelloMaxBytes;
        var buffer = ArrayPool<byte>.Shared.Rent(cap);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(requestAborted, link.Closing);

        // One report, from a finally, whatever the loop does. Every path below used to report for itself, which worked until a path appeared that did not: a
        // connection whose OnMessage throws something other than a WireFormatException — an application's Admit hook, say — escaped the loop with the session
        // still open, and a session nothing ever closes holds its slot, its frames and its ingress ring for the life of the process.
        var closeCode = (ushort)0;

        try
        {
            var length = 0;
            while (socket.State == WebSocketState.Open && !stopping.IsCancellationRequested)
            {
                ValueWebSocketReceiveResult result;
                try
                {
                    result = await socket.ReceiveAsync(buffer.AsMemory(length, cap - length), stopping.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (WebSocketException)
                {
                    closeCode = CloseCodes.GoingAway;
                    return;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    closeCode = CloseCodes.Normal;
                    return;
                }

                if (result.MessageType != WebSocketMessageType.Binary)
                {
                    // Text on a binary protocol is a framing error, and a client that sent one cannot be assumed to parse anything that follows.
                    closeCode = CloseCodes.ProtocolError;
                    link.Close(CloseCodes.ProtocolError, "binary only");
                    return;
                }

                length += result.Count;
                if (!result.EndOfMessage)
                {
                    if (length == cap)
                    {
                        // Refused by its length, before a byte of it is decoded — and the client is TOLD, rather than having its socket disposed under it,
                        // which it would read as a network fault (1006) indistinguishable from a dropped connection.
                        closeCode = CloseCodes.MessageTooBig;
                        link.Close(CloseCodes.MessageTooBig, "message too big");
                        return;
                    }

                    continue;
                }

                connection.OnMessage(buffer.AsSpan(0, length));
                length = 0;

                // The first message is HELLO and may be 16 KiB, because a client must be able to send a token before it has been told any limit. Everything
                // after it is bounded by the transport's own ceiling, under which the session's clientMessageBytes still applies in the engine.
                if (cap != options.MaxInboundMessageBytes)
                {
                    cap = options.MaxInboundMessageBytes;
                    if (cap > buffer.Length)
                    {
                        var grown = ArrayPool<byte>.Shared.Rent(cap);
                        ArrayPool<byte>.Shared.Return(buffer);
                        buffer = grown;
                    }

                    // Rent may hand back more than was asked for, and reading into all of it would quietly raise the ceiling this line exists to set.
                    cap = Math.Min(cap, buffer.Length);
                }
            }

            // The loop ended because the engine closed the session, or the host is stopping.
            closeCode = link.CloseCode == 0 ? CloseCodes.GoingAway : link.CloseCode;
        }
        finally
        {
            connection.OnClosed(closeCode == 0 ? CloseCodes.GoingAway : closeCode, null);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool SupportsOurSubprotocol(System.Collections.Generic.IList<string> requested)
    {
        // A client that requested nothing is accepted: only browsers are obliged to send the header, and the handshake's own version check is what actually
        // decides compatibility. A client that requested something else is refused, because it expects framing this endpoint does not speak.
        if (requested.Count == 0)
        {
            return true;
        }

        for (var i = 0; i < requested.Count; i++)
        {
            if (string.Equals(requested[i], ProtocolConstants.WebSocketSubprotocol, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static ISubscriptionAcceptor Acceptor(HttpContext http, WebSocketSubscriptionTransport transport)
    {
        if (transport.IsStopped)
        {
            return null;
        }

        var acceptor = transport.Acceptor;
        if (acceptor != null)
        {
            return acceptor;
        }

        // First request: bind the transport to the runtime now, so a host that maps its endpoints before starting the runtime works as well as one that maps
        // them after. Two requests racing here both call Start, and the second is harmless — the runtime hands out the same acceptor.
        var runtime = http.RequestServices.GetService<TyphonRuntime>();
        if (runtime == null)
        {
            return null;
        }

        try
        {
            runtime.StartSubscriptionTransport(transport);
        }
        catch (InvalidOperationException error)
        {
            // The runtime has not started, or declares no subscriptions. Both are 503 to the client — it should retry — but to the operator they are a
            // configuration mistake whose diagnosis is in the exception text, and an endpoint that answers 503 forever with nothing in the log is
            // indistinguishable from one that is merely busy. Logged once: this runs on every request until the runtime starts.
            if (Interlocked.Exchange(ref _startRefusalLogged, 1) == 0)
            {
                http.RequestServices.GetService<ILoggerFactory>()
                    ?.CreateLogger(typeof(TyphonSubscriptionsEndpoints))
                    .LogWarning(error, "The Typhon subscriptions endpoint cannot start its transport, and will answer 503 until the runtime is started.");
            }

            return null;
        }

        return transport.Acceptor;
    }

    private static System.Net.EndPoint RemoteEndPoint(HttpContext http)
    {
        var address = http.Connection.RemoteIpAddress;
        return address == null ? null : new System.Net.IPEndPoint(address, http.Connection.RemotePort);
    }
}
