using JetBrains.Annotations;

namespace Typhon.Engine.Internals;

/// <summary>
/// Point-in-time page-cache composition read by the profiler at tick boundary. All four bucket counters (<see cref="FreePages"/>, <see cref="CleanUsedPages"/>,
/// <see cref="DirtyUsedPages"/>, <see cref="ExclusivePages"/>) are mutually exclusive — each of the <see cref="TotalPages"/> entries contributes to exactly one.
/// The two trailing overlay counters (<see cref="EpochProtectedPages"/>, <see cref="PendingIoReads"/>) are independent: any page may count in those regardless
/// of which bucket it lives in.
/// </summary>
/// <remarks>
/// Produced by <c>PagedMMF.GetGaugeSnapshot</c>. The mutually-exclusive invariant lets the viewer render the cache composition as a stacked area chart without
/// double-counting.
/// </remarks>
[PublicAPI]
internal readonly struct PageCacheGaugeSnapshot
{
    /// <summary>Total in-memory page slots (fixed at <see cref="PagedMMF"/> construction — never grows).</summary>
    public int TotalPages { get; }

    /// <summary>Pages unallocated / available for reuse (<c>PageState.Free</c>).</summary>
    public int FreePages { get; }

    /// <summary>Idle pages with no pending checkpoint writes (<c>PageState.Idle &amp;&amp; DirtyCounter == 0</c>).</summary>
    public int CleanUsedPages { get; }

    /// <summary>Idle pages with pending checkpoint writes (<c>PageState.Idle &amp;&amp; DirtyCounter &gt; 0</c>).</summary>
    public int DirtyUsedPages { get; }

    /// <summary>Pages held under exclusive latch or transient Allocating state (<c>PageState.Exclusive</c> or <c>PageState.Allocating</c>).</summary>
    public int ExclusivePages { get; }

    /// <summary>Overlay: pages pinned by an active epoch guard (not mutually exclusive with the four buckets).</summary>
    public int EpochProtectedPages { get; }

    /// <summary>Overlay: pages with an in-flight I/O read task (not mutually exclusive with the four buckets).</summary>
    public int PendingIoReads { get; }

    internal PageCacheGaugeSnapshot(int totalPages, int freePages, int cleanUsedPages, int dirtyUsedPages, int exclusivePages, int epochProtectedPages, 
        int pendingIoReads)
    {
        TotalPages = totalPages;
        FreePages = freePages;
        CleanUsedPages = cleanUsedPages;
        DirtyUsedPages = dirtyUsedPages;
        ExclusivePages = exclusivePages;
        EpochProtectedPages = epochProtectedPages;
        PendingIoReads = pendingIoReads;
    }
}
