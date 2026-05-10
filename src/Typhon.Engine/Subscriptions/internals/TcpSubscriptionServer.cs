using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// TCP server that accepts client connections and flushes subscription deltas.
/// Owns the listener thread and I/O flush thread.
/// </summary>
internal sealed partial class TcpSubscriptionServer : IDisposable
{
    private readonly SubscriptionServerOptions _options;
    private readonly ClientConnectionManager _clientManager;
    private readonly SubscriptionOutputPhase _outputPhase;
    private readonly ILogger _logger;

    private Socket _listenerSocket;
    private Thread _listenerThread;
    private Thread _ioFlushThread;
    private volatile bool _shutdown;
    private int _disposed;

    internal TcpSubscriptionServer(SubscriptionServerOptions options, ClientConnectionManager clientManager, SubscriptionOutputPhase outputPhase, ILogger logger)
    {
        _options = options;
        _clientManager = clientManager;
        _outputPhase = outputPhase;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Start the TCP listener and I/O flush threads.</summary>
    internal void Start()
    {
        _listenerSocket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
        _listenerSocket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, false); // Dual-stack
        _listenerSocket.Bind(new IPEndPoint(IPAddress.IPv6Any, _options.Port));
        _listenerSocket.Listen(128);

        LogServerStarted(_options.Port);

        _listenerThread = new Thread(ListenerLoop)
        {
            Name = "Typhon.SubscriptionListener",
            IsBackground = true
        };
        _listenerThread.Start();

        _ioFlushThread = new Thread(IoFlushLoop)
        {
            Name = "Typhon.SubscriptionIO",
            IsBackground = true
        };
        _ioFlushThread.Start();
    }

    /// <summary>Stop accepting connections, drain send buffers, and shut down threads.</summary>
    internal void Shutdown()
    {
        _shutdown = true;

        // Close listener socket to unblock Accept()
        try { _listenerSocket?.Close(); } catch { /* best-effort */ }

        // Signal I/O thread to do final flush
        _outputPhase.FlushSignal.Set();

        // Wait for threads to exit
        _listenerThread?.Join(TimeSpan.FromSeconds(5));
        _ioFlushThread?.Join(TimeSpan.FromSeconds(5));

        // Dispose all client connections
        _clientManager.DisposeAll();

        LogServerStopped();
    }

    // ═══════════════════════════════════════════════════════════════
    // Listener thread
    // ═══════════════════════════════════════════════════════════════

    private void ListenerLoop()
    {
        while (!_shutdown)
        {
            try
            {
                var clientSocket = _listenerSocket.Accept();
                if (_options.MaxClients > 0 && _clientManager.Count >= _options.MaxClients)
                {
                    LogMaxClientsReached(_clientManager.Count);
                    clientSocket.Close();
                    continue;
                }

                var client = new ClientConnection(clientSocket, _options.SendBufferCapacity);
                _clientManager.Add(client);
                LogClientConnected(client.Context.ConnectionId, clientSocket.RemoteEndPoint?.ToString());
            }
            catch (SocketException) when (_shutdown)
            {
                break; // Expected — listener socket closed during shutdown
            }
            catch (ObjectDisposedException) when (_shutdown)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!_shutdown)
                {
                    LogListenerError(ex);
                }
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // I/O flush thread
    // ═══════════════════════════════════════════════════════════════

    private void IoFlushLoop()
    {
        while (!_shutdown)
        {
            // Wait for signal from Output phase
            _outputPhase.FlushSignal.Wait();
            _outputPhase.FlushSignal.Reset();

            if (_shutdown)
            {
                break;
            }

            FlushAllClients();
        }

        // Final flush on shutdown
        FlushAllClients();
    }

    private void FlushAllClients()
    {
        foreach (var client in _clientManager.GetAll())
        {
            if (client.IsDisposed)
            {
                continue;
            }

            try
            {
                while (!client.Buffer.IsEmpty)
                {
                    var data = client.Buffer.GetReadSpan();
                    if (data.IsEmpty)
                    {
                        break;
                    }

                    var sent = client.Socket.Send(data, SocketFlags.None);
                    if (sent <= 0)
                    {
                        // Connection closed by peer
                        HandleDisconnect(client);
                        break;
                    }

                    client.Buffer.AdvanceRead(sent);
                }
            }
            catch (SocketException ex)
            {
                LogClientSendError(client.Context.ConnectionId, ex);
                HandleDisconnect(client);
            }
            catch (ObjectDisposedException)
            {
                // Client already disposed
            }
        }
    }

    private void HandleDisconnect(ClientConnection client)
    {
        LogClientDisconnected(client.Context.ConnectionId);

        // Remove from all subscribed Views' subscriber lists
        foreach (var view in client.ActiveSubscriptions)
        {
            view.RemoveSubscriber(client);
        }

        _clientManager.Remove(client);
        client.Dispose();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Shutdown();
        _listenerSocket?.Dispose();
        _outputPhase.FlushSignal.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════
    // Logging
    // ═══════════════════════════════════════════════════════════════

    [LoggerMessage(LogLevel.Information, "Subscription server started on port {Port}")]
    private partial void LogServerStarted(int port);

    [LoggerMessage(LogLevel.Information, "Subscription server stopped")]
    private partial void LogServerStopped();

    [LoggerMessage(LogLevel.Information, "Client connected: id={ConnectionId}, remote={RemoteEndPoint}")]
    private partial void LogClientConnected(int connectionId, string remoteEndPoint);

    [LoggerMessage(LogLevel.Information, "Client disconnected: id={ConnectionId}")]
    private partial void LogClientDisconnected(int connectionId);

    [LoggerMessage(LogLevel.Warning, "Max clients reached ({Count}), rejecting connection")]
    private partial void LogMaxClientsReached(int count);

    [LoggerMessage(LogLevel.Error, "Listener thread error")]
    private partial void LogListenerError(Exception ex);

    [LoggerMessage(LogLevel.Warning, "Client send error: id={ConnectionId}")]
    private partial void LogClientSendError(int connectionId, SocketException ex);
}
