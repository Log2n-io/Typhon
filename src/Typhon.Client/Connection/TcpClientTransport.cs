using System;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Typhon.Protocol;

namespace Typhon.Client;

/// <summary>
/// The built-in TCP transport's client half: a four-byte preamble, then length-prefixed messages.
/// </summary>
/// <remarks>
/// <para>
/// <b>The framing is all this adds to a socket</b>, and it is the exact mirror of the server's link: a little-endian <c>u32</c> length that excludes itself,
/// turning a byte stream into the message stream the protocol is defined over. The <c>TYP2</c> preamble is exchanged first and both sides check it, which is
/// what stops a client talking Typhon at a port that answers something else entirely and then blaming the codec.
/// </para>
/// <para>
/// <b>TCP carries no close code.</b> A FIN says the peer has gone, not why. <see cref="CloseCode"/> therefore reports <c>1001</c> — going away — for an
/// orderly end, which is the code whose meaning is "the connection ended, nothing is wrong with you" and the one whose reconnect behaviour matches what a
/// client should do about a disappeared server. Inventing a more specific code would be inventing information.
/// </para>
/// </remarks>
public sealed class TcpClientTransport : IClientTransport
{
    /// <summary>The length prefix's own width, which it excludes.</summary>
    private const int FrameHeaderBytes = 4;

    private readonly string _host;
    private readonly int _port;
    private readonly int _maxMessageBytes;
    private readonly byte[] _header = new byte[FrameHeaderBytes];
    private readonly byte[] _sendHeader = new byte[FrameHeaderBytes];
    private Socket _socket;
    private NetworkStream _stream;

    /// <summary>Builds a transport for a host and port.</summary>
    /// <param name="host">The host.</param>
    /// <param name="port">The port.</param>
    /// <param name="maxMessageBytes">The largest message to accept before refusing the stream.</param>
    public TcpClientTransport(string host, int port, int maxMessageBytes = ProtocolConstants.WelcomeMaxBytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(host);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(port);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxMessageBytes);

        _host = host;
        _port = port;
        _maxMessageBytes = maxMessageBytes;
    }

    /// <inheritdoc />
    public bool IsConnected => _socket is { Connected: true };

    /// <inheritdoc />
    public ushort CloseCode { get; private set; }

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken ct)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        await socket.ConnectAsync(_host, _port, ct).ConfigureAwait(false);
        _socket = socket;
        _stream = new NetworkStream(socket, ownsSocket: false);
        CloseCode = 0;

        ProtocolConstants.TcpPreamble.CopyTo(_sendHeader);
        await _stream.WriteAsync(_sendHeader.AsMemory(0, FrameHeaderBytes), ct).ConfigureAwait(false);

        if (!await ReadExactlyAsync(_header, ct).ConfigureAwait(false) || !_header.AsSpan().SequenceEqual(ProtocolConstants.TcpPreamble))
        {
            await DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"the peer at {_host}:{_port} did not answer the TYP2 preamble, so it is not a Typhon TCP endpoint");
        }
    }

    /// <inheritdoc />
    public async ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken ct)
    {
        var stream = _stream;
        if (stream == null)
        {
            return;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(_sendHeader, (uint)message.Length);
        await stream.WriteAsync(_sendHeader.AsMemory(0, FrameHeaderBytes), ct).ConfigureAwait(false);
        await stream.WriteAsync(message, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<byte[]> ReceiveAsync(CancellationToken ct)
    {
        var stream = _stream;
        if (stream == null)
        {
            return null;
        }

        try
        {
            if (!await ReadExactlyAsync(_header, ct).ConfigureAwait(false))
            {
                CloseCode = CloseCodes.GoingAway;
                return null;
            }

            var length = BinaryPrimitives.ReadUInt32LittleEndian(_header);
            if (length > (uint)_maxMessageBytes)
            {
                CloseCode = CloseCodes.MessageTooBig;
                await CloseAsync(CloseCodes.MessageTooBig, "length prefix above the cap").ConfigureAwait(false);
                throw new WireFormatException(CloseCodes.MessageTooBig, $"the server announced a {length}-byte message, above the {_maxMessageBytes}-byte cap");
            }

            var message = new byte[length];
            if (!await ReadExactlyAsync(message, ct).ConfigureAwait(false))
            {
                // A prefix that promised more than arrived: the stream ended mid-message, which is not an orderly close however it looks at the socket.
                CloseCode = CloseCodes.GoingAway;
                return null;
            }

            return message;
        }
        catch (Exception exception) when (exception is SocketException or System.IO.IOException or ObjectDisposedException)
        {
            CloseCode = CloseCodes.GoingAway;
            return null;
        }
    }

    /// <inheritdoc />
    public Task CloseAsync(ushort code, string reason)
    {
        // TCP has nowhere to put the code, so the graceful thing is a FIN: the peer learns the stream ended, and the engine's own BYE is what carries a
        // reason when the client has one to give.
        try
        {
            _socket?.Shutdown(SocketShutdown.Both);
        }
        catch (Exception)
        {
            // Already gone. Nothing to report and nobody to report it to.
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_stream != null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
            _stream = null;
        }

        _socket?.Dispose();
        _socket = null;
    }

    private async Task<bool> ReadExactlyAsync(byte[] destination, CancellationToken ct)
    {
        var read = 0;
        while (read < destination.Length)
        {
            var got = await _stream.ReadAsync(destination.AsMemory(read), ct).ConfigureAwait(false);
            if (got == 0)
            {
                return false;
            }

            read += got;
        }

        return true;
    }
}
