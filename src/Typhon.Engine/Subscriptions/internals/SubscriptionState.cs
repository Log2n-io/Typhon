namespace Typhon.Engine.Internals;

/// <summary>
/// Per-client per-View subscription state machine.
/// </summary>
internal enum SubscriptionPhase : byte
{
    /// <summary>Not subscribed to this View.</summary>
    None,

    /// <summary>Incremental sync in progress — receiving initial entity batches.</summary>
    Syncing,

    /// <summary>Fully synced — receiving normal deltas each tick.</summary>
    Active
}

/// <summary>
/// Tracks the subscription state for one client on one published View.
/// </summary>
internal sealed class ViewSubscriptionState
{
    public SubscriptionPhase Phase;

    /// <summary>For incremental sync: cursor into the View's entity set (how many entities sent so far).</summary>
    public int SyncCursor;

    /// <summary>Snapshot of entity PKs for incremental sync (captured at subscribe time).</summary>
    public long[] SyncSnapshot;

    /// <summary>True if backpressure overflow occurred — next tick sends full resync instead of delta.</summary>
    public bool NeedsResync;

    /// <summary>Per-client View instance (only for per-client Views; null for shared Views).</summary>
    public ViewBase PerClientView;

    public void Reset()
    {
        Phase = SubscriptionPhase.None;
        SyncCursor = 0;
        SyncSnapshot = null;
        NeedsResync = false;
    }
}
