using System;
using System.Threading;

namespace Typhon.Engine;

// Read-only page-body detail surface consumed by the Workbench Database File Map's detail tier (Module 15,// Track A, A2). Unlike the coarse introspection in
// DatabaseEngine.StorageIntrospection.cs — which touches only in-memory structures — these methods fault page bodies in through the page cache, but via the
// no-clock-sweep read path so introspection never perturbs the eviction heuristic protecting the live working set.
public partial class DatabaseEngine
{
    private long _pageBodyReadCount;

    /// <summary>
    /// Number of page bodies read through <see cref="TryReadPageBody"/> since the engine opened. The Database File Map's detail tier is viewport-scoped, never
    /// a full-file scan — the A2 tests assert this counter grows only by the size of the requested region.
    /// </summary>
    public long PageBodyReadCount => _pageBodyReadCount;

    /// <summary>
    /// Copies the raw page (8 KiB — <see cref="PageBaseHeader"/> + metadata region + chunk data) of file page <paramref name="filePageIndex"/> into
    /// <paramref name="dest"/>. The read goes through the page cache via the no-clock-sweep path (<c>RequestPageEpochNoSweep</c>): it does not bump the
    /// eviction heuristic and does not satisfy the page's pending CRC verification — the caller recomputes the CRC itself.
    /// Returns <c>false</c> on an out-of-range index or a lost eviction race; <c>true</c> with
    /// <paramref name="dest"/> filled otherwise.
    /// </summary>
    public unsafe bool TryReadPageBody(int filePageIndex, Span<byte> dest)
    {
        var mmf = MMF;
        var pageSize = mmf.StoragePageSize;
        if ((uint)filePageIndex >= (uint)mmf.StorageFilePageCount)
        {
            return false;
        }
        if (dest.Length < pageSize)
        {
            throw new ArgumentException($"Destination span too small: need {pageSize} bytes, got {dest.Length}.", nameof(dest));
        }

        using var guard = EpochGuard.Enter(EpochManager);
        if (!mmf.RequestPageEpochNoSweep(filePageIndex, guard.Epoch, out var memPageIndex))
        {
            return false;
        }

        new ReadOnlySpan<byte>(mmf.GetMemPageAddress(memPageIndex), pageSize).CopyTo(dest);
        Interlocked.Increment(ref _pageBodyReadCount);
        return true;
    }

    /// <summary>
    /// Reports whether file page <paramref name="filePageIndex"/> is currently resident in the page cache and, if so, whether it is dirty. Non-faulting — it
    /// never triggers page I/O — so it must be called <b>before</b> <see cref="TryReadPageBody"/> for that page if pre-introspection residency is wanted (the
    /// read faults the page in). An out-of-range index yields <c>resident = false</c>.
    /// </summary>
    public void GetPageResidency(int filePageIndex, out bool resident, out bool dirty)
    {
        var mmf = MMF;
        if ((uint)filePageIndex >= (uint)mmf.StorageFilePageCount)
        {
            resident = false;
            dirty = false;
            return;
        }

        mmf.TryGetPageResidency(filePageIndex, out resident, out dirty);
    }
}
