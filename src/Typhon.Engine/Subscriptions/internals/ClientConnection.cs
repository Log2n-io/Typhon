using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using Typhon.Protocol;

namespace Typhon.Engine.Internals;

/// <summary>
/// Represents a connected TCP client with its subscription state and send buffer.
/// </summary>
/// <remarks>
/// <para>Thread safety model:</para>
/// <list type="bullet">
/// <item><see cref="SetSubscriptions"/> is called from game systems (worker threads) — writes to <c>_pendingSubscriptions</c> via atomic swap.</item>
/// <item>Output phase reads <c>_pendingSubscriptions</c> (CAS to null), updates <c>_activeSubscriptions</c> (uncontested).</item>
/// <item>I/O thread reads from <see cref="Buffer"/> (SPSC consumer side).</item>
/// </list>
/// </remarks>
internal sealed class ClientConnection : IDisposable
{
    private static int _nextConnectionId;

    /// <summary>Public client context (connection ID, user data).</summary>
    public ClientContext Context { get; }

    /// <summary>Underlying TCP socket.</summary>
    internal Socket Socket { get; }

    /// <summary>Outbound data buffer (SPSC: Output writes, I/O reads).</summary>
    internal SendBuffer Buffer { get; }

    // Active subscription state — read/written only by Output phase (single-threaded, no contention).
    internal readonly Dictionary<ushort, ViewSubscriptionState> ViewStates = new();

    // Active subscription set — the set of PublishedViews this client is subscribed to.
    // Only modified by Output phase during subscription transition processing.
    internal readonly HashSet<PublishedView> ActiveSubscriptions = [];

    // Pending subscription events to include in this tick's TickDelta.
    // Written by ProcessSubscriptionTransitions, consumed by BuildClientTickDelta.
    internal readonly List<SubscriptionEvent> PendingEvents = [];

    // Pending subscription set — written by game systems via SetSubscriptions, read by Output phase.
    // Null means no pending change. Atomic swap via Interlocked.Exchange.
    private PublishedView[] _pendingSubscriptions;

    // Connection lifecycle
    private int _disposed;

    internal ClientConnection(Socket socket, int sendBufferCapacity)
    {
        Socket = socket;
        Buffer = new SendBuffer(sendBufferCapacity);
        Context = new ClientContext { ConnectionId = Interlocked.Increment(ref _nextConnectionId) };

        // Enable TCP_NODELAY — deltas are already tick-batched, no benefit from Nagle
        socket.NoDelay = true;
    }

    /// <summary>
    /// Set the client's subscription set. Replaces the previous set atomically.
    /// Called from game systems (worker threads). The transition is applied during the next tick's Output phase.
    /// </summary>
    /// <remarks>
    /// <para>This is a full-list replacement, not a delta. The runtime diffs old vs new to generate
    /// Subscribed/Unsubscribed events.</para>
    /// <para>If called multiple times within a tick, the last call wins.</para>
    /// </remarks>
    public void SetSubscriptions(params PublishedView[] views)
    {
        ArgumentNullException.ThrowIfNull(views);
        Interlocked.Exchange(ref _pendingSubscriptions, views);
    }

    /// <summary>
    /// Check if there are pending subscription changes and consume them.
    /// Called by Output phase only (single-threaded).
    /// </summary>
    /// <returns>The pending subscription set, or null if no changes.</returns>
    internal PublishedView[] ConsumePendingSubscriptions() => Interlocked.Exchange(ref _pendingSubscriptions, null);

    /// <summary>True if this connection has been disposed (disconnected).</summary>
    public bool IsDisposed => _disposed != 0;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try { Socket.Shutdown(SocketShutdown.Both); } catch { /* best-effort */ }
        Socket.Dispose();
        Buffer.Dispose();

        // Clean up per-client Views
        foreach (var state in ViewStates.Values)
        {
            state.PerClientView?.Dispose();
        }

        ViewStates.Clear();
        ActiveSubscriptions.Clear();
    }
}
