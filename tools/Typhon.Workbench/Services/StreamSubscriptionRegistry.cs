using System.Collections.Concurrent;

namespace Typhon.Workbench.Services;

/// <summary>
/// Tracks per-connection event subscriptions for the unified data stream (#308 Phase C). Each
/// connection registers under a server-assigned <c>streamId</c> on connect; clients then call
/// <see cref="Subscribe"/> / <see cref="Unsubscribe"/> via the JSON API to grow / shrink the set
/// of event types they want delivered. The unified-stream multiplexer consults
/// <see cref="IsSubscribed"/> before serialising each delta.
/// </summary>
/// <remarks>
/// <para><b>Default policy.</b> A freshly registered connection has an empty subscription set —
/// only the connection-bootstrap events (<c>stream-id</c>, <c>metadata</c>, <c>session-state</c>,
/// <c>heartbeat</c>, <c>shutdown</c>) bypass the filter; everything else (<c>tick</c>, <c>log</c>,
/// <c>topology-changed</c>, <c>error</c>) requires explicit subscription. This avoids accidental
/// fanout to panels that don't need the data and keeps the wire tight.</para>
/// <para><b>Lifetime.</b> Entries are removed on connection disconnect via <see cref="Unregister"/>
/// (called from the stream handler's <c>finally</c> block). The registry never times entries out
/// on its own — connection liveness is the source of truth.</para>
/// <para><b>Concurrency.</b> Backed by <see cref="ConcurrentDictionary{TKey, TValue}"/>; safe for
/// the multiplexer to read from one thread while the controller writes from another. The inner
/// per-stream set is also concurrent so partial subscribe calls don't tear.</para>
/// </remarks>
public sealed class StreamSubscriptionRegistry
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, byte>> _entries = new();

    /// <summary>
    /// Registers a fresh connection under <paramref name="streamId"/> with an empty subscription
    /// set. Returns silently if the streamId is already registered (idempotent — guards against
    /// the multiplexer race where bootstrap events fire before the controller observes the
    /// register call).
    /// </summary>
    public void Register(Guid streamId)
    {
        _entries.TryAdd(streamId, new ConcurrentDictionary<string, byte>());
    }

    /// <summary>
    /// Removes the connection's subscription state. Idempotent — safe to call from the stream
    /// handler's <c>finally</c> even if the connection died before <see cref="Register"/> ran.
    /// </summary>
    public void Unregister(Guid streamId)
    {
        _entries.TryRemove(streamId, out _);
    }

    /// <summary>
    /// Adds <paramref name="events"/> to the subscription set for <paramref name="streamId"/>.
    /// Unknown streamIds are silently ignored — clients racing the connection close don't get a
    /// 4xx for what is effectively no-op cleanup.
    /// </summary>
    /// <returns><see langword="true"/> if the streamId is registered (regardless of which events
    /// were already in the set); <see langword="false"/> if it was unknown.</returns>
    public bool Subscribe(Guid streamId, IReadOnlyList<string> events)
    {
        if (!_entries.TryGetValue(streamId, out var set))
        {
            return false;
        }
        for (var i = 0; i < events.Count; i++)
        {
            set.TryAdd(events[i], 0);
        }
        return true;
    }

    /// <summary>
    /// Removes <paramref name="events"/> from the subscription set for <paramref name="streamId"/>.
    /// Unknown streamIds are silently ignored.
    /// </summary>
    public bool Unsubscribe(Guid streamId, IReadOnlyList<string> events)
    {
        if (!_entries.TryGetValue(streamId, out var set))
        {
            return false;
        }
        for (var i = 0; i < events.Count; i++)
        {
            set.TryRemove(events[i], out _);
        }
        return true;
    }

    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="streamId"/>'s subscription set contains
    /// <paramref name="eventType"/>. Unknown streamIds return <see langword="false"/> — events
    /// destined for a vanished connection are dropped silently. The multiplexer consults this
    /// once per delta on the hot path; the dictionary lookups are O(1) average.
    /// </summary>
    public bool IsSubscribed(Guid streamId, string eventType)
    {
        return _entries.TryGetValue(streamId, out var set) && set.ContainsKey(eventType);
    }

    /// <summary>
    /// Snapshots the subscription set for diagnostics / tests. Allocates — not for hot-path use.
    /// </summary>
    public string[] Snapshot(Guid streamId)
    {
        if (!_entries.TryGetValue(streamId, out var set))
        {
            return [];
        }
        return [.. set.Keys];
    }
}
