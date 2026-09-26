using System;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Typhon.Protocol;

namespace Typhon.Client;

/// <summary>The browser's transport, from .NET: a <see cref="ClientWebSocket"/> speaking the <c>typhon.3</c> subprotocol.</summary>
/// <remarks>
/// <para>
/// <b>The subprotocol is requested, not assumed.</b> A server that does not offer <c>typhon.3</c> has not agreed to speak this protocol, and finding that
/// out at the upgrade is far better than discovering it from the first message that fails to parse. The negotiated value is checked after the handshake for
/// the same reason.
/// </para>
/// <para>
/// <b>Messages are reassembled here.</b> A WebSocket message may arrive in several frames; nothing above this line should ever see a partial one, because
/// every message the protocol defines is parsed as a whole and a half-message is indistinguishable from a malformed one.
/// </para>
/// </remarks>
public sealed class WebSocketClientTransport : IClientTransport
{
    private readonly Uri _endpoint;
    private readonly int _maxMessageBytes;
    private ClientWebSocket _socket;

    /// <summary>Builds a transport for an endpoint.</summary>
    /// <param name="endpoint">A <c>ws://</c> or <c>wss://</c> URL.</param>
    /// <param name="maxMessageBytes">The largest message to accept before refusing the stream; defaults to the protocol's <c>WELCOME</c> cap.</param>
    public WebSocketClientTransport(Uri endpoint, int maxMessageBytes = ProtocolConstants.WelcomeMaxBytes)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxMessageBytes);

        _endpoint = endpoint;
        _maxMessageBytes = maxMessageBytes;
    }

    /// <inheritdoc />
    public bool IsConnected => _socket is { State: WebSocketState.Open };

    /// <inheritdoc />
    public ushort CloseCode { get; private set; }

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol(ProtocolConstants.WebSocketSubprotocol);
        await socket.ConnectAsync(_endpoint, ct).ConfigureAwait(false);

        if (!string.Equals(socket.SubProtocol, ProtocolConstants.WebSocketSubprotocol, StringComparison.Ordinal))
        {
            socket.Dispose();
            throw new InvalidOperationException(
                $"the server negotiated '{socket.SubProtocol}' rather than '{ProtocolConstants.WebSocketSubprotocol}', so it has not agreed to speak this "
                + "protocol; talking to it anyway would turn a clear refusal into a parse failure on the first message");
        }

        _socket = socket;
        CloseCode = 0;
    }

    /// <inheritdoc />
    public ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken ct)
    {
        var socket = _socket;
        return socket == null || socket.State != WebSocketState.Open
            ? ValueTask.CompletedTask
            : socket.SendAsync(message, WebSocketMessageType.Binary, endOfMessage: true, ct);
    }

    /// <inheritdoc />
    public async Task<byte[]> ReceiveAsync(CancellationToken ct)
    {
        var socket = _socket;
        if (socket == null)
        {
            return null;
        }

        var buffer = new byte[8192];
        using var assembled = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
                // An abrupt disconnect. 1006 is what the WebSocket specification calls "closed abnormally": no close frame arrived, so the peer's reason is
                // unknowable and pretending otherwise would feed the reconnect policy a code nobody sent.
                CloseCode = 1006;
                return null;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                CloseCode = (ushort)(socket.CloseStatus.HasValue ? (int)socket.CloseStatus.Value : CloseCodes.Normal);
                return null;
            }

            assembled.Write(buffer, 0, result.Count);
            if (assembled.Length > _maxMessageBytes)
            {
                await CloseAsync(CloseCodes.MessageTooBig, "message above the negotiated cap").ConfigureAwait(false);
                throw new WireFormatException(CloseCodes.MessageTooBig, $"the server sent a message above the {_maxMessageBytes}-byte cap");
            }

            if (result.EndOfMessage)
            {
                return assembled.ToArray();
            }
        }
    }

    /// <inheritdoc />
    public async Task CloseAsync(ushort code, string reason)
    {
        var socket = _socket;
        if (socket == null || socket.State != WebSocketState.Open)
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await socket.CloseAsync((WebSocketCloseStatus)code, reason ?? "", timeout.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A close that cannot be delivered is still a close. The alternative is throwing out of a teardown path, where there is nobody left to tell.
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _socket?.Dispose();
        _socket = null;
        return ValueTask.CompletedTask;
    }
}
