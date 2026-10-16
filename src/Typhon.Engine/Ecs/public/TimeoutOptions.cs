using JetBrains.Annotations;
using System;

namespace Typhon.Engine;

/// <summary>
/// Configuration options for lock acquisition timeouts across all engine subsystems.
/// </summary>
/// <remarks>
/// <para>
/// Each subsystem has its own timeout property, allowing fine-grained tuning based on expected
/// contention patterns. All defaults are 5 seconds except for <see cref="TransactionChainLockTimeout"/>
/// and <see cref="SegmentAllocationLockTimeout"/> which default to 10 seconds due to higher expected
/// contention during heavy write workloads.
/// </para>
/// <para>
/// When a lock acquisition exceeds its timeout, the caller throws <see cref="LockTimeoutException"/>
/// with the resource name and elapsed duration for diagnostics.
/// </para>
/// </remarks>
[PublicAPI]
public class TimeoutOptions
{
    /// <summary>
    /// The singleton instance set by <see cref="DatabaseEngine"/> during initialization.
    /// Accessible from any subsystem without requiring a reference to the engine.
    /// </summary>
    public static TimeoutOptions Current { get; internal set; } = new();

    /// <summary>
    /// Default lock timeout used when no subsystem-specific timeout applies.
    /// </summary>
    public TimeSpan DefaultLockTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Timeout for page cache state-transition locks in <c>PagedMMF</c> and <c>ManagedPagedMMF</c>.
    /// </summary>
    public TimeSpan PageCacheLockTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Timeout for B+Tree index locks during insert, delete, and lookup operations.
    /// </summary>
    public TimeSpan BTreeLockTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Timeout for transaction chain locks during create, remove, and walk operations.
    /// </summary>
    /// <remarks>
    /// Defaults to 10 seconds because transaction chain operations can involve heavier contention
    /// under high-throughput write workloads.
    /// </remarks>
    public TimeSpan TransactionChainLockTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Timeout for revision chain locks during MVCC revision read, add, and cleanup operations.
    /// </summary>
    public TimeSpan RevisionChainLockTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Timeout for segment allocation locks in chained block allocators and variable-sized buffer segments.
    /// </summary>
    /// <remarks>
    /// Defaults to 10 seconds because segment allocation may trigger page-level operations under the hood.
    /// </remarks>
    public TimeSpan SegmentAllocationLockTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Default timeout for <see cref="Transaction.Commit(ConcurrencyConflictHandler)"/> when called without an explicit
    /// <see cref="UnitOfWorkContext"/>. Individual lock timeouts (5-10s) provide tighter bounds within this limit.
    /// </summary>
    public TimeSpan DefaultCommitTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Default lifetime timeout for a Unit of Work when none is specified at creation.
    /// Individual lock timeouts (5-10s) provide tighter bounds within this limit.
    /// </summary>
    public TimeSpan DefaultUowTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Timeout for page cache back-pressure waits.
    /// When all cache pages are dirty or epoch-protected, allocation threads wait
    /// for IO completion to make pages evictable.
    /// </summary>
    public TimeSpan PageCacheBackpressureTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a commit's publish may wait out page-cache back-pressure, from the moment its records are in the WAL, before giving up and stopping the
    /// engine (AP-03).
    /// </summary>
    /// <remarks>
    /// Past the append a commit cannot roll back: an allocation that gave up after <see cref="PageCacheBackpressureTimeout"/> would leave a partial
    /// publish, which stops the engine until a reopen recovers the commit from the log. The publish waits instead, logging each
    /// <see cref="PageCacheBackpressureTimeout"/> it outlasts. One bound for the whole publish, however many allocations it makes. It is far above any
    /// checkpoint stall, and still makes a cache that cannot free a page — every page held by the epochs of running transactions, the publisher's own among
    /// them — stop the engine instead of hanging it.
    /// </remarks>
    public TimeSpan CommitPublishBackpressureTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long a page allocation waits on back-pressure, in all, while the cache keeps serving other allocations (#1382, S1 of the starvation policy).
    /// Past <see cref="PageCacheBackpressureTimeout"/> a wait goes on, a fresh timeout at a time, as long as pages were served since its last one: the
    /// engine is slow, not stuck, and the work goes through. A wait that sees no page served for a whole timeout, or reaches this ceiling, throws
    /// <see cref="PageCacheBackpressureTimeoutException"/>. The storage health state (<see cref="StorageHealthState"/>) reports such waits.
    /// </summary>
    public TimeSpan PageCacheOverloadWaitCeiling { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Timeout for WAL commit buffer back-pressure waits.
    /// When the ring buffer is full, commit threads spin-wait for the WAL writer to drain it.
    /// </summary>
    public TimeSpan WalBackPressureTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>A copy of these options, every property included — for a test that changes one and restores the rest.</summary>
    internal TimeoutOptions Clone() => (TimeoutOptions)MemberwiseClone();
}
