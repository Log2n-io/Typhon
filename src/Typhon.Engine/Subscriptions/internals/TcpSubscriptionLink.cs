using System;
using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Typhon.Protocol;

namespace Typhon.Engine.Internals;

/// <summary>
/// One TCP connection as the engine uses it: <c>u32 len</c> framing over an asynchronous <see cref="Socket"/>, or over the <c>SslStream</c> wrapped around one.
/// </summary>
/// <remarks>
/// <para>
/// <b>The framing is the whole of what this type adds to a socket.</b> A length prefix, little-endian and excluding itself, turns a byte stream into the
/// message channel <see cref="ISubscriptionLink"/> promises (<c>design/Subscriptions/03-wire-protocol.md § 10</c>, W31). The limit is checked against the
/// prefix <b>before</b> a byte of the body is read, so a client that announces a gigabyte costs one failed comparison rather than an allocation.
/// </para>
/// <para>
/// <b>A frame is never copied.</b> The bytes arrive as a <see cref="ReadOnlyMemory{T}"/> over engine-owned native memory — a
/// <see cref="NativeFrameMemoryManager"/> view — and are handed to
/// <see cref="Socket.SendAsync(ReadOnlyMemory{byte},SocketFlags,CancellationToken)"/> exactly as they came. Only the 4-byte prefix comes from this link, which is why the write is two calls rather than one: .NET's gather-write surfaces take
/// <c>ArraySegment&lt;byte&gt;</c>, and reaching one from native memory means either copying the frame onto the heap or pinning a managed array to hand out its
/// address — the first defeats the point of the frame pool and the second is the pattern this codebase forbids outright.
/// </para>
/// <para>
/// <b>One close, whoever caused it.</b> Every path out — the engine's <see cref="Close"/>, a framing refusal, a peer that vanished, the transport stopping —
/// goes through the same single-shot teardown: drain the send in flight so a <c>KICK</c> reaches the peer ahead of the <c>FIN</c>, shut the socket down, then
/// deliver exactly one <see cref="ISubscriptionConnection.OnClosed"/>.
/// </para>
/// </remarks>
internal sealed class TcpSubscriptionLink : ISubscriptionLink
{
    /// <summary>The length prefix's own width, which it excludes.</summary>
    internal const int FrameHeaderBytes = 4;

    /// <summary>The smallest receive buffer worth allocating: a <c>PING</c> is 9 bytes and a <c>BYE</c> is 3, but a <c>HELLO</c> carries a token.</summary>
    private const int MinReceiveBufferBytes = 1024;

    /// <summary>
    /// Up to this many bytes, a message is copied behind its prefix and written once; above it, the prefix goes on its own and the message is written where it
    /// lies.
    /// </summary>
    /// <remarks>
    /// <b>The trade is a memcpy against a syscall and a segment.</b> With <c>NoDelay</c> a 4-byte prefix written on its own is a 4-byte packet: 40 bytes of
    /// headers to carry 4, and a second call into the kernel, which for a <c>PONG</c> every quarter second per session is the dominant cost of sending it.
    /// Copying a kilobyte of native memory is tens of nanoseconds. For a real frame the arithmetic inverts — the prefix amortizes to nothing against 256 KiB
    /// and the copy does not — so a frame is never copied, which is the property the whole native-memory path exists for.
    /// </remarks>
    private const int CoalesceMaxBytes = 2048;

    private readonly Socket _socket;
    private readonly Stream _stream;
    private readonly TcpSubscriptionOptions _options;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly CancellationTokenSource _receiveStopping = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly byte[] _sendHeader = new byte[FrameHeaderBytes];
    private readonly byte[] _receiveHeader = new byte[FrameHeaderBytes];

    private byte[] _receiveBuffer;
    private NativeFrameMemoryManager _sendScratch;
    private ISubscriptionConnection _connection;
    private Exception _closeError;
    private ushort _closeCode;
    private int _closeStarted;
    private int _closeReported;

    /// <summary>Wraps an accepted socket.</summary>
    /// <param name="socket">The accepted socket. Owned from here: this link disposes it.</param>
    /// <param name="stream">The TLS stream over it, or <see langword="null"/> for a plaintext link.</param>
    /// <param name="options">The listener's options, for the caps and the close drain.</param>
    public TcpSubscriptionLink(Socket socket, Stream stream, TcpSubscriptionOptions options)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(options);

        _socket = socket;
        _stream = stream;
        _options = options;
    }

    /// <inheritdoc />
    public bool SupportsUnreliable => false;

    /// <summary>Whether a close has begun. The receive loop reads it after every message the engine handled.</summary>
    public bool IsClosing => Volatile.Read(ref _closeStarted) != 0;

    /// <summary>The code this link closed with.</summary>
    public ushort CloseCode => Volatile.Read(ref _closeCode);

    /// <summary>Completes when the teardown has finished and <see cref="ISubscriptionConnection.OnClosed"/> has been delivered.</summary>
    public Task Closed => _closed.Task;

    /// <inheritdoc />
    /// <remarks>
    /// The engine sends at most one message per link at a time, so the gate here is not a queue: it is what a close waits on to let a <c>KICK</c> out before
    /// the socket goes down, and what keeps the prefix and its body adjacent on the wire if that guarantee ever slipped.
    /// </remarks>
    public ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken ct)
    {
        if (message.IsEmpty || IsClosing)
        {
            // A zero-length message would frame as `len` 0, which is the wire's own protocol error; there is nothing to send and nothing to report.
            return ValueTask.CompletedTask;
        }

        return new ValueTask(SendFramedAsync(message, ct));
    }

    /// <inheritdoc />
    public bool TrySendUnreliable(ReadOnlySpan<byte> datagram) => false;

    /// <inheritdoc />
    public void Close(ushort code, string reason) => BeginClose(code, null);

    /// <summary>
    /// Exchanges the 4-byte preamble: this side's is written first and unconditionally, then the peer's is read and compared.
    /// </summary>
    /// <param name="ct">Cancels the exchange — the handshake deadline.</param>
    /// <returns><see langword="true"/> when the peer speaks this major.</returns>
    /// <remarks>
    /// <b>Writing first is what makes a mismatch diagnosable.</b> A major mismatch is never a <c>KICK</c> — a major-N server would have to speak major 2's
    /// <c>KICK</c> framing to say so (<c>design/Subscriptions/03-wire-protocol.md § 12 W31</c>) — so the only thing the peer ever learns is which major it
    /// reached, and it learns it from these four bytes followed by a close.
    /// </remarks>
    public async Task<bool> ExchangePreambleAsync(CancellationToken ct)
    {
        ProtocolConstants.TcpPreamble.CopyTo(_sendHeader);
        await WriteAllAsync(_sendHeader, ct).ConfigureAwait(false);

        var read = await ReadUpToAsync(_receiveHeader, ct).ConfigureAwait(false);
        return read == FrameHeaderBytes && _receiveHeader.AsSpan().SequenceEqual(ProtocolConstants.TcpPreamble);
    }

    /// <summary>
    /// Hands this link the connection the acceptor made for it.
    /// </summary>
    /// <param name="connection">The engine's connection.</param>
    public void Attach(ISubscriptionConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        Volatile.Write(ref _connection, connection);

        if (IsClosing)
        {
            // The connection's own HELLO deadline can fire between Accept returning and this call; the report is idempotent, so claiming it here costs a
            // compare and closes the window in which a close would have had nobody to tell.
            ReportClosed();
        }
    }

    /// <summary>
    /// Runs the receive loop until the peer, the engine or a framing refusal ends it.
    /// </summary>
    /// <returns>A task that completes when the loop has stopped and the teardown has been started.</returns>
    /// <remarks>
    /// <b>Sequential by construction.</b> One loop per link, and <see cref="ISubscriptionConnection.OnMessage"/> is called from it synchronously, so the
    /// engine's "one message at a time, in order" guarantee costs no lock. The span it receives points into this link's own buffer and is never held past the
    /// call: the next message overwrites it.
    /// </remarks>
    public async Task RunReceiveLoopAsync()
    {
        var ct = _receiveStopping.Token;
        var first = true;

        try
        {
            while (!IsClosing && !ct.IsCancellationRequested)
            {
                var headerRead = await ReadUpToAsync(_receiveHeader, ct).ConfigureAwait(false);
                if (headerRead == 0)
                {
                    // A FIN between messages: the peer went away without a BYE, which on TCP is as orderly as it gets.
                    BeginClose(CloseCodes.Normal, null);
                    return;
                }

                if (headerRead != FrameHeaderBytes)
                {
                    BeginClose(CloseCodes.Normal, new EndOfStreamException("the peer disappeared inside a length prefix"));
                    return;
                }

                var length = BinaryPrimitives.ReadUInt32LittleEndian(_receiveHeader);
                if (length == 0)
                {
                    // 1002, and nothing is read: a zero-length frame is a framing error, not an empty message (03 § 10).
                    BeginClose(CloseCodes.ProtocolError, null);
                    return;
                }

                var cap = first ? ProtocolConstants.HelloMaxBytes : _options.MaxInboundMessageBytes;
                if (length > (uint)cap)
                {
                    // 1009, decided on the prefix alone. Reading the body first is what this check exists to avoid: the bytes are the attack.
                    BeginClose(CloseCodes.MessageTooBig, null);
                    return;
                }

                var body = EnsureReceiveBuffer((int)length).AsMemory(0, (int)length);
                var bodyRead = await ReadUpToAsync(body, ct).ConfigureAwait(false);
                if (bodyRead != body.Length)
                {
                    BeginClose(CloseCodes.Normal, new EndOfStreamException("the peer disappeared inside a message"));
                    return;
                }

                first = false;

                var connection = Volatile.Read(ref _connection);
                if (connection == null)
                {
                    BeginClose(CloseCodes.Normal, null);
                    return;
                }

                connection.OnMessage(body.Span);
            }

            BeginClose(CloseCodes.Normal, null);
        }
        catch (OperationCanceledException)
        {
            BeginClose(CloseCodes.Normal, null);
        }
        catch (ObjectDisposedException)
        {
            BeginClose(CloseCodes.Normal, null);
        }
        catch (Exception e)
        {
            BeginClose(CloseCodes.Normal, e);
        }
    }

    // ── framing ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task SendFramedAsync(ReadOnlyMemory<byte> message, CancellationToken ct)
    {
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsClosing)
            {
                return;
            }

            if (message.Length <= CoalesceMaxBytes)
            {
                // A control message, behind its prefix in this link's own native scratch: one write, one segment, and still no managed byte on the path.
                var scratch = EnsureSendScratch();
                var span = scratch.GetSpan();
                BinaryPrimitives.WriteUInt32LittleEndian(span, (uint)message.Length);
                message.Span.CopyTo(span[FrameHeaderBytes..]);
                await WriteAllAsync(scratch.Memory[..(FrameHeaderBytes + message.Length)], ct).ConfigureAwait(false);
                return;
            }

            BinaryPrimitives.WriteUInt32LittleEndian(_sendHeader, (uint)message.Length);
            await WriteAllAsync(_sendHeader, ct).ConfigureAwait(false);

            // The frame itself, straight through: engine-owned native memory, pinned by its own MemoryManager, never copied and never on the heap.
            await WriteAllAsync(message, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async ValueTask WriteAllAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct)
    {
        if (_stream != null)
        {
            await _stream.WriteAsync(buffer, ct).ConfigureAwait(false);
            return;
        }

        while (!buffer.IsEmpty)
        {
            var sent = await _socket.SendAsync(buffer, SocketFlags.None, ct).ConfigureAwait(false);
            if (sent <= 0)
            {
                throw new SocketException((int)SocketError.ConnectionReset);
            }

            buffer = buffer[sent..];
        }
    }

    /// <summary>Reads until <paramref name="buffer"/> is full or the peer stops sending.</summary>
    /// <param name="buffer">Where to read into.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>How many bytes were read: the buffer's length, zero for a clean end, or something between for a truncated one.</returns>
    private async ValueTask<int> ReadUpToAsync(Memory<byte> buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = _stream != null
                ? await _stream.ReadAsync(buffer[total..], ct).ConfigureAwait(false)
                : await _socket.ReceiveAsync(buffer[total..], SocketFlags.None, ct).ConfigureAwait(false);

            if (read <= 0)
            {
                return total;
            }

            total += read;
        }

        return total;
    }

    /// <summary>The send-side scratch, allocated on the first small message and held for the link's life. Touched only under the send gate.</summary>
    /// <returns>The scratch view.</returns>
    private NativeFrameMemoryManager EnsureSendScratch()
        => _sendScratch ??= NativeFrameMemoryManager.Allocate(FrameHeaderBytes + CoalesceMaxBytes);

    private byte[] EnsureReceiveBuffer(int length)
    {
        var buffer = _receiveBuffer;
        if (buffer != null && buffer.Length >= length)
        {
            return buffer;
        }

        // Grown, never pooled: the buffer lives as long as the connection does and its ceiling is the inbound cap, so a pool would hold the same bytes with a
        // rent and a return per message on top.
        buffer = new byte[Math.Max(length, MinReceiveBufferBytes)];
        _receiveBuffer = buffer;
        return buffer;
    }

    // ── teardown ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts the one teardown this link will ever run.
    /// </summary>
    /// <param name="code">The close code to report.</param>
    /// <param name="error">The failure that ended it, or <see langword="null"/>.</param>
    /// <remarks>
    /// It never blocks: the engine calls <see cref="Close"/> from inside its connection lock, and draining a send there would hold that lock across a syscall.
    /// </remarks>
    private void BeginClose(ushort code, Exception error)
    {
        if (Interlocked.Exchange(ref _closeStarted, 1) != 0)
        {
            return;
        }

        Volatile.Write(ref _closeError, error);
        Volatile.Write(ref _closeCode, code);
        _ = Task.Run(CloseCoreAsync);
    }

    private async Task CloseCoreAsync()
    {
        try
        {
            // The queued send first. The engine writes a KICK and then closes, so a teardown that raced it would deliver a FIN and no reason.
            var drained = await _sendGate.WaitAsync(_options.CloseDrainTimeoutMs).ConfigureAwait(false);
            try
            {
                try
                {
                    // FIN after the bytes already handed to the kernel: a graceful close still transmits them.
                    _socket.Shutdown(SocketShutdown.Send);
                }
                catch (SocketException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
            }
            finally
            {
                if (drained)
                {
                    // Only with the gate in hand: the scratch is native memory a send may still be writing from, and freeing it under a send that outlasted
                    // the drain would be a read of freed memory on a socket thread. A link that hung holds 2 KiB instead, which is the right way to lose.
                    var scratch = _sendScratch;
                    _sendScratch = null;
                    scratch?.Release();
                    _sendGate.Release();
                }
            }

            try
            {
                _receiveStopping.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            _stream?.Dispose();
            _socket.Dispose();
        }
        catch (Exception)
        {
            // A teardown cannot fail in a way anybody can act on: what matters is that the connection is told exactly once, which is below.
        }
        finally
        {
            // Neither the gate nor the token source is disposed: a send or a receive can still be unwinding through them on another thread, and an
            // ObjectDisposedException there would be a real failure reported for a link that had already closed cleanly. Both are managed-only.
            ReportClosed();
            _closed.TrySetResult();
        }
    }

    private void ReportClosed()
    {
        var connection = Volatile.Read(ref _connection);
        if (connection == null || Interlocked.Exchange(ref _closeReported, 1) != 0)
        {
            return;
        }

        connection.OnClosed(Volatile.Read(ref _closeCode), Volatile.Read(ref _closeError));
    }
}
