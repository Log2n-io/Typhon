using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Typhon.Engine;
using Typhon.Protocol;

namespace Typhon.Subscriptions.AspNetCore;

/// <summary>
/// One browser connection, as the engine uses it: a reliable ordered binary channel and a close.
/// </summary>
/// <remarks>
/// <para>
/// <b>It owns no queue and needs none.</b> The engine sends at most one message per session at a time — that is the send pump's guarantee, not a hope — so
/// this type may hand every message straight to the socket. What it does own is the close: a WebSocket close is a handshake, and it has to be attempted once,
/// from whichever side noticed, without throwing into the engine.
/// </para>
/// <para>
/// <b>The bytes are borrowed.</b> <see cref="SendAsync"/>'s buffer is engine-owned native memory recycled the moment the returned task completes, so this
/// awaits the socket write rather than queueing it. A link that returned early would hand a recycled buffer to the kernel.
/// </para>
/// </remarks>
internal sealed class WebSocketLink : ISubscriptionLink
{
    /// <summary>
    /// The 1xxx close codes an endpoint may put in a close frame. The others — 1002, 1005, 1006, 1007, 1009, 1015 — are reserved for the receiving end to
    /// infer, and sending one aborts the connection instead of closing it, which is why the engine sends a <c>KICK</c> carrying the real code first.
    /// </summary>
    private static readonly ushort[] SendableStatusCodes = [1000, 1001, 1008, 1011, 1012, 1013];

    private readonly WebSocket _socket;
    private readonly CancellationTokenSource _closing = new();
    private Task _closeTask = Task.CompletedTask;
    private int _closes;

    /// <summary>Adopts an accepted WebSocket.</summary>
    /// <param name="socket">The socket, already upgraded and carrying the <c>typhon.2</c> subprotocol.</param>
    public WebSocketLink(WebSocket socket)
    {
        ArgumentNullException.ThrowIfNull(socket);
        _socket = socket;
    }

    /// <summary>Cancelled when the engine asks for a close, so the receive loop stops waiting.</summary>
    public CancellationToken Closing => _closing.Token;

    /// <summary>The code the engine closed with, or zero.</summary>
    public ushort CloseCode { get; private set; }

    /// <summary>The reason the engine closed with, already truncated to the protocol's limit.</summary>
    public string CloseReason { get; private set; }

    /// <inheritdoc />
    public bool SupportsUnreliable => false;

    /// <inheritdoc />
    /// <remarks>
    /// <b>A closed socket faults rather than completing.</b> Completing would tell the send pump the frame reached the client, and the pump would advance the
    /// session's sequence for bytes nobody received — after which the client's baseline is ahead of what it was actually sent, which no later frame corrects
    /// because records describe the present rather than a delta (SUB-03). Faulting takes the pump's failure path, which closes the session.
    /// </remarks>
    public async ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken ct)
    {
        if (_socket.State != WebSocketState.Open)
        {
            throw new WebSocketException(WebSocketError.InvalidState, $"the socket is {_socket.State}, so this frame was not sent");
        }

        await _socket.SendAsync(message, WebSocketMessageType.Binary, endOfMessage: true, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public bool TrySendUnreliable(ReadOnlySpan<byte> datagram) => false;

    /// <inheritdoc />
    public void Close(ushort code, string reason)
    {
        if (Interlocked.Increment(ref _closes) != 1)
        {
            return;
        }

        CloseCode = code;
        CloseReason = reason;

        // The close frame is started and not awaited HERE: this is called from the engine — from a tick, or from a send pump that has just failed — and
        // neither may block on a network round trip. The task is kept so the endpoint can await it before it disposes the socket; dropping it was a race that
        // aborted the connection and lost the engine's close code in exactly the KICK case it exists for.
        _closeTask = CloseSocketAsync(code, reason);
        _closing.Cancel();
    }

    /// <summary>
    /// Waits for the close frame this link started, so the socket is not disposed underneath it.
    /// </summary>
    /// <param name="timeout">How long to wait before giving up on a peer that is not reading.</param>
    /// <returns>The wait.</returns>
    public async Task DrainCloseAsync(TimeSpan timeout)
    {
        var pending = _closeTask;
        if (pending.IsCompleted)
        {
            return;
        }

        // A peer that has stopped reading would otherwise hold the request open for as long as it liked. The frame is best effort; the session is already
        // closed either way.
        await Task.WhenAny(pending, Task.Delay(timeout)).ConfigureAwait(false);
    }

    /// <summary>Releases the cancellation source once the endpoint's loop has finished with the link.</summary>
    public void Dispose() => _closing.Dispose();

    private async Task CloseSocketAsync(ushort code, string reason)
    {
        try
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                // The engine has already sent a KICK carrying the real code, so the frame's job is to end the connection with the most informative code
                // it is allowed to carry. The private range passes through untouched; of the 1xxx codes only some may be sent, and collapsing all of them to
                // "normal" would throw away the retry signal 1013 exists to give — which is the one a client most needs to act on.
                var status = code is >= 4000 and <= 4999 || Array.IndexOf(SendableStatusCodes, code) >= 0
                    ? (WebSocketCloseStatus)code
                    : WebSocketCloseStatus.NormalClosure;
                await _socket.CloseOutputAsync(status, Truncate(reason), CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            _socket.Abort();
        }
    }

    private static string Truncate(string reason)
        => string.IsNullOrEmpty(reason) ? null : KickMessage.TruncateUtf8(reason, ProtocolConstants.KickReasonMaxBytes);
}
