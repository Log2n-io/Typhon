namespace Typhon.Protocol;

/// <summary>
/// Type of subscription lifecycle event sent to a client.
/// </summary>
public enum EventType : byte
{
    /// <summary>A new View subscription was added. Incremental sync will follow.</summary>
    Subscribed,

    /// <summary>A View subscription was removed. Client should tear down local cache.</summary>
    Unsubscribed,

    /// <summary>Incremental sync for a View is complete. Normal delta flow begins.</summary>
    SyncComplete,

    /// <summary>
    /// A backpressure overflow forced the server to drop this client's deltas. The client must discard its local cache for the View; the server restarts
    /// incremental sync and re-streams the full View state over subsequent ticks, ending with a <see cref="SyncComplete"/> event.
    /// </summary>
    Resync
}
