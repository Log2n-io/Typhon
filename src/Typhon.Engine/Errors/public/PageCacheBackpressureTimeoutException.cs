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
        base(TyphonErrorCode.PageCacheBackpressureTimeout,
            $"Page cache back-pressure timeout after {waitDuration.TotalMilliseconds:F0}ms (dirty: {dirtyPageCount}, epoch-protected: {epochProtectedCount})",
            waitDuration)
    {
        DirtyPageCount = dirtyPageCount;
        EpochProtectedCount = epochProtectedCount;
    }

    /// <summary>Number of dirty pages awaiting flush when the timeout fired.</summary>
    public int DirtyPageCount { get; }

    /// <summary>Number of pages pinned by active epochs (not yet evictable) when the timeout fired.</summary>
    public int EpochProtectedCount { get; }
}
