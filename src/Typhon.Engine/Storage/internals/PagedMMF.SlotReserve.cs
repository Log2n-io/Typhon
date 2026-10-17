// CS1591: PagedMMF is public in the internal namespace (see PagedMMF.cs); not part of the published API reference.
#pragma warning disable 1591

using System;
using System.Threading;

namespace Typhon.Engine.Internals;

public partial class PagedMMF
{
    /// <summary>The cache size, in pages, below which no slot is reserved: a small cache keeps every slot for its working set.</summary>
    internal const int SlotReserveMinPages = 1024;

    /// <summary>One slot reserved per this many pages of cache, up to <see cref="SlotReserveMaxSlots"/>.</summary>
    internal const int SlotReserveDivisor = 32;

    /// <summary>The reserve's ceiling: what pinned waiters need grows with their number, not with the cache.</summary>
    internal const int SlotReserveMaxSlots = 256;

    /// <summary>The share of the reserve (1 / this) that only the thread holding the cache's floor may draw: younger pins cannot empty it before it.</summary>
    internal const int SlotReserveFloorShareDivisor = 4;

    private readonly object _reserveLock = new();
    private int[] _reserve;
    private int _reserveTarget;
    private int _reservedCount;
    private long _reserveDraws;

    /// <summary>The slots the reserve holds now.</summary>
    internal int ReservedSlotCount => Volatile.Read(ref _reservedCount);

    /// <summary>How many slots the reserve keeps when it is full; 0 on a cache smaller than <see cref="SlotReserveMinPages"/>.</summary>
    internal int SlotReserveTarget => _reserveTarget;

    /// <summary>Test seam: the slots the reserve holds now.</summary>
    internal int[] ReservedSlotsForTests
    {
        get
        {
            lock (_reserveLock)
            {
                return _reserve.AsSpan(0, _reservedCount).ToArray();
            }
        }
    }

    /// <summary>How many allocations were served from the reserve, since the cache was created.</summary>
    internal long ReserveDraws => Interlocked.Read(ref _reserveDraws);

    /// <summary>Test seam: resizes the reserve (0 disables it). The slots it holds past the new size are freed; the free count has them already.</summary>
    internal void SetSlotReserveTargetForTests(int target)
    {
        lock (_reserveLock)
        {
            target = Math.Clamp(target, 0, MemPagesCount / 2);
            while (_reservedCount > target)
            {
                var pi = Slot(_reserve[--_reservedCount]);
                pi.StateSyncRoot.EnterExclusiveAccess(ref WaitContext.Null);
                pi.PageState = PageState.Free;
                pi.StateSyncRoot.ExitExclusiveAccess();
            }

            var resized = new int[target];
            Array.Copy(_reserve, resized, _reservedCount);
            _reserve = resized;
            _reserveTarget = target;
        }
    }

    private void InitSlotReserve()
    {
        _reserveTarget = MemPagesCount >= SlotReserveMinPages ? Math.Min(MemPagesCount / SlotReserveDivisor, SlotReserveMaxSlots) : 0;
        _reserve = new int[_reserveTarget];
    }

    /// <summary>
    /// PS-24 (#1382): an allocation that found <paramref name="memPageIndex"/> (Allocating, out of the directory) gives it to the reserve instead, while
    /// the reserve is short. Its caller then looks for another slot. A reserved slot counts as free, as the gauges count it.
    /// </summary>
    private bool TryStockReserve(int memPageIndex)
    {
        if (Volatile.Read(ref _reservedCount) >= _reserveTarget)
        {
            return false;
        }

        lock (_reserveLock)
        {
            if (_reservedCount >= _reserveTarget)
            {
                return false;
            }

            var pi = Slot(memPageIndex);
            pi.StateSyncRoot.EnterExclusiveAccess(ref WaitContext.Null);
            pi.PageState = PageState.Reserved;
            pi.StateSyncRoot.ExitExclusiveAccess();
            _reserve[_reservedCount] = memPageIndex;
            Volatile.Write(ref _reservedCount, _reservedCount + 1);
            Interlocked.Increment(ref _metrics.FreeMemPageCount);
            return true;
        }
    }

    /// <summary>
    /// PS-24 (#1382): a slot from the reserve, Allocating and out of the directory, for an allocation that would otherwise wait with this thread's epoch
    /// pinned. False when the thread holds no pin, or when the reserve is down to the share kept for the thread whose pin is the cache's floor
    /// (<paramref name="minActiveEpoch"/>), unless this thread is that one: a floor-holder that finds the reserve emptied by younger pins waits for a
    /// timeout, since nothing tagged after its pin can be evicted to refill it.
    /// </summary>
    private bool TryDrawReserve(long minActiveEpoch, out int memPageIndex)
    {
        memPageIndex = -1;
        var epochs = EpochManager;
        if (Volatile.Read(ref _reservedCount) == 0 || epochs is not { IsCurrentThreadInScope: true })
        {
            return false;
        }

        var pinned = epochs.CurrentThreadPinnedEpoch;
        var kept = pinned != 0 && pinned <= minActiveEpoch ? 0 : _reserveTarget / SlotReserveFloorShareDivisor;
        lock (_reserveLock)
        {
            if (_reservedCount <= kept)
            {
                return false;
            }

            memPageIndex = _reserve[_reservedCount - 1];
            Volatile.Write(ref _reservedCount, _reservedCount - 1);
        }

        var pi = Slot(memPageIndex);
        pi.StateSyncRoot.EnterExclusiveAccess(ref WaitContext.Null);
        pi.PageState = PageState.Allocating;
        pi.StateSyncRoot.ExitExclusiveAccess();
        Interlocked.Decrement(ref _metrics.FreeMemPageCount);
        Interlocked.Increment(ref _reserveDraws);
        return true;
    }
}
