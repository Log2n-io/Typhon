using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Controls when WAL records become crash-safe. Specified per Unit of Work at creation time.
/// </summary>
[PublicAPI]
public enum DurabilityMode : byte
{
    /// <summary>
    /// WAL records buffered. Durable only after explicit Flush()/FlushAsync(). Commit latency: ~1-2µs. Data-at-risk: until Flush().
    /// Best for: game ticks, batch imports, simulation steps.
    /// </summary>
    Deferred = 0,

    /// <summary>
    /// WAL writer auto-flushes every N ms (default 5ms). Commit latency: ~1-2µs. Data-at-risk: ≤ GroupCommitInterval.
    /// Best for: general server workload, request handlers.
    /// </summary>
    GroupCommit = 1,

    /// <summary>
    /// FUA on every tx.Commit(). Blocks until WAL record is on stable media. Commit latency: ~15-85µs. Data-at-risk: zero.
    /// Best for: financial trades, irreversible state changes.
    /// </summary>
    Immediate = 2,
}

/// <summary>
/// Per-transaction override for durability. Can only escalate (never downgrade).
/// </summary>
[PublicAPI]
public enum DurabilityOverride : byte
{
    /// <summary>Use the owning UoW's DurabilityMode.</summary>
    Default = 0,

    /// <summary>Force FUA for this specific commit (escalation only).</summary>
    Immediate = 1,
}

/// <summary>
/// State machine for UoW lifecycle. Transitions are one-way.
/// </summary>
/// <remarks>
/// <code>
/// Free → Pending → WalDurable → Committed → Free (normal path)
/// Free → Pending → Void → Free (crash recovery path, after GC)
/// </code>
/// </remarks>
[PublicAPI]
public enum UnitOfWorkState : byte
{
    /// <summary>Slot available for reuse. Default state for zeroed memory.</summary>
    Free = 0,

    /// <summary>Created, transactions may be in progress. WAL records volatile.</summary>
    Pending = 1,

    /// <summary>WAL flush complete (FUA). Survives crash. Pages may still be dirty.</summary>
    WalDurable = 2,

    /// <summary>Data pages checkpointed. WAL segments recyclable.</summary>
    Committed = 3,

    /// <summary>Crash recovery: UoW was Pending at crash time. All revisions invisible.</summary>
    Void = 4,
}
