using System;
using System.IO;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
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
    /// <summary>How much of a message is read at a time.</summary>
    private const int FrameBytes = 8192;

    /// <summary>The frame buffer, reused for the connection's whole life.</summary>
    /// <remarks>
    /// <b>One buffer per transport, because allocating it per message was 700 MB/s of garbage.</b> A fleet of a few thousand
    /// sessions receives tens of thousands of messages a second, and this method allocated an 8 KiB array AND a
    /// <see cref="MemoryStream"/> for every one of them. The collector that followed stalled the process for hundreds of
    /// milliseconds at a time, which is long enough for the server's silence window to expire on thousands of sessions at once —
    /// so the symptom was not slowness but mass disconnection. Safe to share because <see cref="ReceiveAsync"/> has exactly one
    /// caller: the client's single receive loop, which the protocol requires.
    /// </remarks>
    private readonly byte[] _frame = new byte[FrameBytes];

    private readonly Uri _endpoint;
    private readonly int _maxMessageBytes;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
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
    public string CloseDetail { get; private set; }

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
        CloseDetail = null;
    }

    /// <inheritdoc />
    public async ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken ct)
    {
        var socket = _socket;
        if (socket == null || socket.State != WebSocketState.Open)
        {
            return;
        }

        // One sender at a time: ClientWebSocket allows a single outstanding send, and the ping loop, commands and a refusal's BYE all send.
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(message, WebSocketMessageType.Binary, endOfMessage: true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <inheritdoc />
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<byte[]> ReceiveAsync(CancellationToken ct)
    {
        var socket = _socket;
        if (socket == null)
        {
            return null;
        }

        var buffer = _frame;
        MemoryStream assembled = null;
        while (true)
        {
            // <b>The Memory overload, because the ArraySegment one allocates twice per message.</b> WebSocketReceiveResult is a
            // CLASS and that overload returns a Task, so a fleet taking half a million messages a second was allocating a million
            // objects a second for the privilege of being told a count. ValueWebSocketReceiveResult is a struct in a ValueTask,
            // which completes synchronously whenever bytes are already buffered. The close STATUS is not on the struct and is read
            // from the socket below, which is where this code already read it.
            ValueWebSocketReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            }
            catch (WebSocketException e)
            {
                // An abrupt disconnect. 1006 is what the WebSocket specification calls "closed abnormally": no close frame arrived, so the peer's reason is
                // unknowable and pretending otherwise would feed the reconnect policy a code nobody sent.
                CloseCode = 1006;

                // <b>The LOCAL failure is known even when the peer's intent is not, and it was being thrown away.</b> A fleet that
                // reports "1006 x 3306" cannot tell a reset by the peer from a local abort from a timeout, which are three different
                // investigations — and that is exactly the wall a 5 000-session run hit. The error code and the inner socket errno
                // cost nothing to keep and turn the count into a diagnosis.
                CloseDetail = e.InnerException is SocketException socketError
                    ? $"{e.WebSocketErrorCode}/{socketError.SocketErrorCode}"
                    : e.WebSocketErrorCode.ToString();

                return null;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                CloseCode = (ushort)(socket.CloseStatus.HasValue ? (int)socket.CloseStatus.Value : CloseCodes.Normal);
                return null;
            }

            // <b>The common case is one frame, and it now costs ONE exact-sized array.</b> Nearly every message this protocol
            // sends fits in a frame, so assembling it through a stream meant an 8 KiB buffer, a stream, the stream's own internal
            // array and a copy out of it — four allocations to deliver a hundred bytes. The array returned still belongs to the
            // caller, which the interface requires: a recorder keeps what it is handed.
            if (assembled == null && result.EndOfMessage)
            {
                if (result.Count > _maxMessageBytes)
                {
                    // Left open: the client refuses the stream itself, a BYE ahead of the close frame (03 § 3).
                    CloseCode = CloseCodes.MessageTooBig;
                    throw new WireFormatException(CloseCodes.MessageTooBig, $"the server sent a message above the {_maxMessageBytes}-byte cap");
                }

                var message = new byte[result.Count];
                Buffer.BlockCopy(buffer, 0, message, 0, result.Count);
                return message;
            }

            // A message spanning frames: only now is a stream worth its allocation. Not disposed, deliberately: a MemoryStream over
            // its own array holds no unmanaged resource and has no finalizer, and the alternative is a using that cannot span the
            // loop. It becomes garbage with the message.
            assembled ??= new MemoryStream(FrameBytes * 2);
            assembled.Write(buffer, 0, result.Count);
            if (assembled.Length > _maxMessageBytes)
            {
                CloseCode = CloseCodes.MessageTooBig;
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
        return default;
    }
}
