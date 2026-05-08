namespace Typhon.Profiler;

/// <summary>
/// One entry in the v7+ <c>EventQueueCatalog</c> table — describes a single registered event queue's static schema (capacity, event-type name, display name).
/// Augments the existing thin queue-name table with capacity / type info so the Workbench queue panel can display capacity utilisation in % terms against
/// per-tick depth.
/// </summary>
public sealed class EventQueueRecord
{
    /// <summary>Queue index assigned at engine startup (matches <c>QueueTickSummary.QueueId</c>).</summary>
    public ushort QueueIndex { get; init; }

    /// <summary>Display name (mirrors the v12 queue-name table; duplicated here for self-contained reads).</summary>
    public string Name { get; init; }

    /// <summary>Power-of-two ring capacity. Bound on per-tick depth values seen on the wire.</summary>
    public int Capacity { get; init; }

    /// <summary>CLR full name of the event payload type (e.g., <c>Game.Events.PlayerHit</c>).</summary>
    public string EventTypeName { get; init; }
}
