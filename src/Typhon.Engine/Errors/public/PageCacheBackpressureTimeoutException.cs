using JetBrains.Annotations;
using System;

namespace Typhon.Engine;

/// <summary>
/// Page cache allocation timed out waiting for dirty pages to flush.
/// Transient — IO will eventually complete and free pages.
/// </summary>
[PublicAPI]
public class PageCacheBackpressureTimeoutException : TyphonTimeoutException
{
    /// <summary>
    /// Creates a new <see cref="PageCacheBackpressureTimeoutException"/> capturing the cache pressure at the time of the timeout.
    /// </summary>
    /// <param name="dirtyPageCount">Number of dirty pages awaiting flush when the timeout fired.</param>
    /// <param name="epochProtectedCount">Number of pages pinned by active epochs (not yet evictable) when the timeout fired.</param>
    /// <param name="waitDuration">How long the allocation waited before the timeout fired.</param>
    public PageCacheBackpressureTimeoutException(int dirtyPageCount, int epochProtectedCount, TimeSpan waitDuration) :
        this(dirtyPageCount, epochProtectedCount, waitDuration, 0, 0)
    {
    }

    /// <summary>
    /// Creates a new <see cref="PageCacheBackpressureTimeoutException"/> capturing the cache pressure and the epoch pins at the time of the timeout.
    /// </summary>
    /// <param name="dirtyPageCount">Number of dirty pages awaiting flush when the timeout fired.</param>
    /// <param name="epochProtectedCount">Number of pages pinned by active epochs (not yet evictable) when the timeout fired.</param>
    /// <param name="waitDuration">How long the allocation waited before the timeout fired.</param>
    /// <param name="pinnedThreads">Threads holding an epoch pin when the timeout fired.</param>
    /// <param name="oldestPinLag">How many epochs the oldest pin is behind the global epoch.</param>
    public PageCacheBackpressureTimeoutException(int dirtyPageCount, int epochProtectedCount, TimeSpan waitDuration, int pinnedThreads, long oldestPinLag) :
        base(TyphonErrorCode.PageCacheBackpressureTimeout, Describe(dirtyPageCount, epochProtectedCount, waitDuration, pinnedThreads, oldestPinLag),
            waitDuration)
    {
        DirtyPageCount = dirtyPageCount;
        EpochProtectedCount = epochProtectedCount;
        PinnedThreads = pinnedThreads;
        OldestPinLag = oldestPinLag;
    }

    // #1230: a cache mostly epoch-held has one of two causes, and the numbers here tell them apart: one pin held long (its lag), or every thread's
    // working set since its last refresh — pinned threads × the refresh interval × the pages an operation touches — larger than the cache.
    private static string Describe(int dirty, int epochHeld, TimeSpan wait, int pins, long lag)
    {
        var text = $"Page cache back-pressure timeout after {wait.TotalMilliseconds:F0}ms (dirty: {dirty}, epoch-protected: {epochHeld}";
        if (pins == 0)
        {
            return text + ")";
        }

        text += $", pinned threads: {pins}, oldest pin {lag} epochs behind)";
        return epochHeld > dirty
            ? text + ". Mostly epoch-held: either a pin held long (the oldest's lag), or a working set larger than the cache — the pages each pinned"
                   + " thread touched since its last epoch refresh. Enlarge the cache, or run fewer concurrent transactions"
            : text;
    }

    /// <summary>Number of dirty pages awaiting flush when the timeout fired.</summary>
    public int DirtyPageCount { get; }

    /// <summary>Number of pages pinned by active epochs (not yet evictable) when the timeout fired.</summary>
    public int EpochProtectedCount { get; }

    /// <summary>Threads holding an epoch pin when the timeout fired; 0 when not captured.</summary>
    public int PinnedThreads { get; }

    /// <summary>How many epochs the oldest pin was behind the global epoch when the timeout fired.</summary>
    public long OldestPinLag { get; }
}
