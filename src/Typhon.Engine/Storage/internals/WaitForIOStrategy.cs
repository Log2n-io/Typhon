using System;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Passively waits for in-flight IO to complete, making dirty pages evictable.
/// Signaled by <see cref="PagedMMF.DecrementDirty"/> when a page's dirty counter reaches 0.
/// </summary>
internal sealed class WaitForIOStrategy : IPageCacheBackpressureStrategy, IDisposable
{
    private readonly ManualResetEventSlim _pageAvailable = new(false);

    public bool OnPressure(ref BackpressureContext ctx, int dirtyPageCount, int epochProtectedCount)
    {
        if (ctx.ShouldGiveUp)
        {
            return false;
        }

        ctx.RecordRetry();

        // Wait up to 50ms per iteration (retry loop re-checks deadline)
        _pageAvailable.Wait(50);
        _pageAvailable.Reset();
        return true;
    }

    /// <summary>Called by DecrementDirty when a page becomes evictable.</summary>
    public void SignalPageAvailable() => _pageAvailable.Set();

    public void Dispose() => _pageAvailable.Dispose();
}
