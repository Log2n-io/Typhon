using System;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Passively waits for in-flight IO to complete, making dirty pages evictable.
/// Signaled when a page is left with no mutator marks and no writeback debt: by <see cref="PagedMMF.MarkCaptured"/> after a checkpoint write, or by
/// a ChangeSet releasing a page's last mark.
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
