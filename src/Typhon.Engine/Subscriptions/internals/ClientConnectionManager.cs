using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Typhon.Engine.Internals;

/// <summary>
/// Thread-safe registry of connected clients. The listener thread adds connections; the I/O thread or Output phase removes them.
/// </summary>
internal sealed class ClientConnectionManager
{
    private readonly ConcurrentDictionary<int, ClientConnection> _clients = new();

    /// <summary>Register a newly accepted client.</summary>
    internal void Add(ClientConnection client) => _clients[client.Context.ConnectionId] = client;

    /// <summary>Remove a disconnected client.</summary>
    internal bool Remove(ClientConnection client) => _clients.TryRemove(client.Context.ConnectionId, out _);

    /// <summary>Get a client by connection ID.</summary>
    internal ClientConnection Get(int connectionId) => _clients.GetValueOrDefault(connectionId);

    /// <summary>Number of connected clients.</summary>
    internal int Count => _clients.Count;

    /// <summary>Snapshot of all connected clients for iteration. Allocates — use in Output phase (once per tick).</summary>
    internal ICollection<ClientConnection> GetAll() => _clients.Values;

    /// <summary>Dispose all connections.</summary>
    internal void DisposeAll()
    {
        foreach (var client in _clients.Values)
        {
            client.Dispose();
        }

        _clients.Clear();
    }
}
