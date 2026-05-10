using System;

namespace Typhon.Engine.Internals;

/// <summary>
/// Defines how the page cache responds when no evictable pages are found.
/// </summary>
internal interface IPageCacheBackpressureStrategy : IDisposable
{
    /// <summary>
    /// Called when the clock-sweep fails. The strategy should wait for conditions to change
    /// then return true to retry, or false to give up.
    /// </summary>
    bool OnPressure(ref BackpressureContext ctx, int dirtyPageCount, int epochProtectedCount);

    /// <summary>
    /// Called when a dirty page finishes IO and becomes evictable.
    /// Strategies that use signaling override this; default is a no-op.
    /// </summary>
    void SignalPageAvailable()
    {
        // No-op by default — strategies that don't use signaling ignore this.
    }
}
