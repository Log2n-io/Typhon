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
    /// Default timeout for <see cref="Transaction.Commit()"/> when called without an explicit
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
    /// Timeout for WAL commit buffer back-pressure waits.
    /// When the ring buffer is full, commit threads spin-wait for the WAL writer to drain it.
    /// </summary>
    public TimeSpan WalBackPressureTimeout { get; set; } = TimeSpan.FromSeconds(5);
}
