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
    public PageCacheBackpressureTimeoutException(int dirtyPageCount, int epochProtectedCount, TimeSpan waitDuration) : 
        base(TyphonErrorCode.PageCacheBackpressureTimeout,
            $"Page cache back-pressure timeout after {waitDuration.TotalMilliseconds:F0}ms (dirty: {dirtyPageCount}, epoch-protected: {epochProtectedCount})",
            waitDuration)
    {
        DirtyPageCount = dirtyPageCount;
        EpochProtectedCount = epochProtectedCount;
    }

    public int DirtyPageCount { get; }
    public int EpochProtectedCount { get; }
}
