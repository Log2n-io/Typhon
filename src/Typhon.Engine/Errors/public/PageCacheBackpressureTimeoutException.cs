using JetBrains.Annotations;
using System;

namespace Typhon.Engine;

/// <summary>
/// Page cache allocation timed out: no slot became evictable within the back-pressure timeout. The counts say what held them — pages owed a write (the
/// checkpoint behind the writers), pages under dirty marks (write sets still in flight), pages only an epoch pin keeps — and the message names the
/// dominant one. Transient: it clears once that holder lets go, but it also means the work exceeded what the cache and the disk could absorb in time.
/// </summary>
[PublicAPI]
public class PageCacheBackpressureTimeoutException : TyphonTimeoutException
{
    /// <summary>
    /// Creates a new <see cref="PageCacheBackpressureTimeoutException"/> capturing the cache pressure at the time of the timeout.
    /// </summary>
    /// <param name="dirtyPageCount">Pages owed a write (writeback debt) when the timeout fired.</param>
    /// <param name="epochProtectedCount">Pages an epoch pin protects (whatever else holds them) when the timeout fired.</param>
    /// <param name="waitDuration">How long the allocation waited before the timeout fired.</param>
    public PageCacheBackpressureTimeoutException(int dirtyPageCount, int epochProtectedCount, TimeSpan waitDuration) :
        this(dirtyPageCount, epochProtectedCount, waitDuration, 0, 0)
    {
    }

    /// <summary>
    /// Creates a new <see cref="PageCacheBackpressureTimeoutException"/> capturing the cache pressure and the epoch pins at the time of the timeout.
    /// </summary>
    /// <param name="dirtyPageCount">Pages owed a write (writeback debt) when the timeout fired.</param>
    /// <param name="epochProtectedCount">Pages an epoch pin protects (whatever else holds them) when the timeout fired.</param>
    /// <param name="waitDuration">How long the allocation waited before the timeout fired.</param>
    /// <param name="pinnedThreads">Threads holding an epoch pin when the timeout fired.</param>
    /// <param name="oldestPinLag">How many epochs the oldest pin is behind the global epoch.</param>
    public PageCacheBackpressureTimeoutException(int dirtyPageCount, int epochProtectedCount, TimeSpan waitDuration, int pinnedThreads, long oldestPinLag) :
        this(dirtyPageCount, epochProtectedCount, waitDuration, pinnedThreads, oldestPinLag, -1, epochProtectedCount)
    {
    }

    /// <summary>
    /// Creates a new <see cref="PageCacheBackpressureTimeoutException"/> with the cache's full breakdown, taken in one pass when the timeout fired.
    /// </summary>
    /// <param name="dirtyPageCount">Pages owed a write (writeback debt).</param>
    /// <param name="epochProtectedCount">Pages an epoch pin protects, whatever else holds them.</param>
    /// <param name="waitDuration">How long the allocation waited before the timeout fired.</param>
    /// <param name="pinnedThreads">Threads holding an epoch pin.</param>
    /// <param name="oldestPinLag">How many epochs the oldest pin is behind the global epoch.</param>
    /// <param name="markedPageCount">Pages under a dirty mark: a write set still in flight; -1 when not captured.</param>
    /// <param name="epochOnlyCount">Pages nothing but an epoch pin holds.</param>
    public PageCacheBackpressureTimeoutException(int dirtyPageCount, int epochProtectedCount, TimeSpan waitDuration, int pinnedThreads, long oldestPinLag,
        int markedPageCount, int epochOnlyCount) :
        base(TyphonErrorCode.PageCacheBackpressureTimeout,
            Describe(dirtyPageCount, epochProtectedCount, waitDuration, pinnedThreads, oldestPinLag, markedPageCount, epochOnlyCount), waitDuration)
    {
        DirtyPageCount = dirtyPageCount;
        EpochProtectedCount = epochProtectedCount;
        PinnedThreads = pinnedThreads;
        OldestPinLag = oldestPinLag;
        MarkedPageCount = markedPageCount;
        EpochOnlyCount = epochOnlyCount;
    }

    // #1230, #1382: the dominant holder decides the advice. Epoch-held alone has two causes, and the lag tells them apart: one pin held long, or every
    // thread's working set since its last refresh — pinned threads × the refresh interval × the pages an operation touches — larger than the cache. The
    // census counts a page under each holder it has, so "epoch-protected" overlaps the others; "epoch-only" does not, and is what the epoch advice needs:
    // a cache 7,887 pages under dirty marks once read "mostly epoch-held" here, because marks were not counted at all.
    private static string Describe(int debt, int epochHeld, TimeSpan wait, int pins, long lag, int marked, int epochOnly)
    {
        var text = $"Page cache back-pressure timeout after {wait.TotalMilliseconds:F0}ms (owed a write: {debt}";
        if (marked >= 0)
        {
            text += $", under dirty marks: {marked}";
        }

        text += $", epoch-protected: {epochHeld}";
        if (marked >= 0)
        {
            text += $" of which epoch-only: {epochOnly}";
        }

        if (pins == 0)
        {
            return text + ")";
        }

        text += $", pinned threads: {pins}, oldest pin {lag} epochs behind)";
        var markedOrZero = Math.Max(marked, 0);
        if (epochOnly > debt && epochOnly > markedOrZero)
        {
            return text + ". Mostly epoch-held: either a pin held long (the oldest's lag), or a working set larger than the cache — the pages each pinned"
                   + " thread touched since its last epoch refresh. Enlarge the cache, or run fewer concurrent transactions";
        }

        if (markedOrZero > debt)
        {
            return text + ". Mostly under dirty marks: the write sets of transactions or cleanup drains still in flight hold their pages until they end."
                   + " Commit in smaller units of work, or enlarge the cache";
        }

        return debt > 0
            ? text + ". Mostly owed a write: the checkpoint did not write pages back as fast as they were dirtied. Enlarge the cache, or dirty fewer"
                   + " pages per second"
            : text;
    }

    /// <summary>Pages owed a write (writeback debt) when the timeout fired: the checkpoint had not written them back yet.</summary>
    public int DirtyPageCount { get; }

    /// <summary>Pages an epoch pin protected when the timeout fired, whatever else held them.</summary>
    public int EpochProtectedCount { get; }

    /// <summary>Threads holding an epoch pin when the timeout fired; 0 when not captured.</summary>
    public int PinnedThreads { get; }

    /// <summary>How many epochs the oldest pin was behind the global epoch when the timeout fired.</summary>
    public long OldestPinLag { get; }

    /// <summary>Pages under a dirty mark (a write set still in flight) when the timeout fired; -1 when not captured.</summary>
    public int MarkedPageCount { get; }

    /// <summary>Pages nothing but an epoch pin held when the timeout fired.</summary>
    public int EpochOnlyCount { get; }
}
