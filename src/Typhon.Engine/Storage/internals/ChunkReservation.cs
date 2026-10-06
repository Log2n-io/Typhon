using System;
using System.Diagnostics;

namespace Typhon.Engine.Internals;

/// <summary>
/// Chunks a writer allocates <b>before</b> it takes its latches, so that the allocations a structure modification makes while it holds them never touch
/// the allocator or the page cache (rule IXW-07).
/// </summary>
/// <remarks>
/// <para>
/// An allocation can grow the segment — up to 1 024 new pages — and fault pages in, and either can wait seconds on page-cache back-pressure or throw. Made
/// under a latch, the wait holds every other writer of that node or bucket, and the throw leaves the latch held for good. Measured in MarketHardeningTests:
/// a B+Tree split waiting in its allocation outlasted the other writers' retry budget, which reported a liveness defect in the tree (IXW-01, two workers in
/// one storm), and an entity-map bucket left locked froze a storm at 175 623 operations until the process died.
/// </para>
/// <para>
/// One per thread and per store type. A writer opens it with <see cref="Begin"/> on its segment, fills it with <see cref="Fill"/> while it holds nothing, and
/// the allocation sites it reaches under its latches take from it through <see cref="AllocateUnderLatch"/>. <see cref="End"/>, outside the latches again,
/// frees what was not taken and unpins the pages. Every reserved chunk's page is pinned by slot reference until <see cref="End"/>, so writing a taken chunk
/// under a latch never faults a page in either. Re-entrant on one segment: an entity-map insert that splits a bucket reserves for both in one scope.
/// </para>
/// </remarks>
internal sealed class ChunkReservation<TStore> where TStore : struct, IPageStore
{
    /// <summary>
    /// Chunks a reservation holds before it grows, and the cap on what a writer reserves from an unlatched estimate. An EXACT need, counted under the
    /// latch, is reserved whole however large: <see cref="Fill"/> grows the reservation, with nothing held. A cap there would leave the rest to be
    /// allocated under the latch — mid-rewrite, for a bucket split, where a fault loses the half already moved.
    /// </summary>
    internal const int InitialCapacity = 64;

    [ThreadStatic]
    private static ChunkReservation<TStore> t_instance;

    /// <summary>
    /// Allocations this thread made at a latched site with nothing reserved: an empty tree's first root, or a writer whose reservation fell short. Per thread
    /// so a test reads only its own writers. Test seam.
    /// </summary>
    [ThreadStatic]
    internal static long UnreservedAllocations;

    private ChunkBasedSegment<TStore> _segment;
    private int _depth;
    private int[] _chunks = new int[InitialCapacity];
    private int _chunkCount;
    private int[] _pinnedPages = new int[InitialCapacity * 2];
    private int _pinCount;

    /// <summary>The calling thread's reservation.</summary>
    internal static ChunkReservation<TStore> Current => t_instance ??= new ChunkReservation<TStore>();

    /// <summary>Chunks reserved and not yet taken.</summary>
    internal int Available => _chunkCount;

    /// <summary>Opens (or re-enters) the reservation for <paramref name="segment"/>. Every <see cref="Begin"/> is paired with an <see cref="End"/>.</summary>
    internal void Begin(ChunkBasedSegment<TStore> segment)
    {
        if (_depth == 0)
        {
            _segment = segment;
        }
        else if (!ReferenceEquals(_segment, segment))
        {
            throw new InvalidOperationException(
                "A chunk reservation is already open on another segment on this thread: writers that reserve do not nest across segments.");
        }

        _depth++;
    }

    /// <summary>
    /// Reserves until <paramref name="target"/> chunks are available, growing the reservation if it must. Allocates through <paramref name="accessor"/>, so
    /// each chunk is cleared and its page marked dirty under that accessor, and pins each page. MUST be called while the caller holds no latch: this is the
    /// allocation that may wait.
    /// </summary>
    internal void Fill(int target, ChangeSet changeSet, ref ChunkAccessor<TStore> accessor)
    {
        Debug.Assert(_depth > 0, "Fill outside Begin/End");
        if (target > _chunks.Length)
        {
            Array.Resize(ref _chunks, Math.Max(target, _chunks.Length * 2));
        }

        while (_chunkCount < target)
        {
            var chunkId = _segment.AllocateChunk(changeSet, ref accessor);
            _chunks[_chunkCount++] = chunkId;
            Pin(chunkId, ref accessor);
        }
    }

    /// <summary>
    /// Pins the page holding <paramref name="chunkId"/> until <see cref="End"/>, so a write under a latch finds it resident. Once per page: a page already
    /// pinned is not pinned again, so a split that re-pins its chain on every pass holds each page once. No-op for a transient store, whose pages never
    /// leave memory.
    /// </summary>
    /// <remarks>
    /// The page is resolved through <paramref name="accessor"/> first, so the accessor's own window pins it while the slot reference is added: a plain
    /// increment then cannot land on a slot being reclaimed, and needs none of <see cref="IPageStore.AcquirePageForRead"/>'s slot lock — which, taken for
    /// every reserved chunk by every writer, contended on the few pages the allocator was handing chunks out of.
    /// </remarks>
    internal unsafe void Pin(int chunkId, ref ChunkAccessor<TStore> accessor)
    {
        Debug.Assert(_depth > 0, "Pin outside Begin/End");
        if (typeof(TStore) == typeof(TransientStore))
        {
            return;
        }

        var address = accessor.GetChunkAddress(chunkId);
        var memPageIndex = PagedMMF.MemPageIndexOfRawData(address, _segment.Store.MemPagesBaseAddress);
        for (var i = 0; i < _pinCount; i++)
        {
            if (_pinnedPages[i] == memPageIndex)
            {
                return;
            }
        }

        if (_pinCount == _pinnedPages.Length)
        {
            Array.Resize(ref _pinnedPages, _pinnedPages.Length * 2);
        }

        _segment.Store.IncrementSlotRefCount(memPageIndex);
        _pinnedPages[_pinCount++] = memPageIndex;
    }

    /// <summary>
    /// The allocation a writer makes while it holds a latch: a reserved chunk when the calling thread has one for <paramref name="segment"/>, otherwise a
    /// plain allocation, counted in <see cref="UnreservedAllocations"/> because it is the wait IXW-07 exists to keep out of a latch.
    /// </summary>
    internal static int AllocateUnderLatch(ChunkBasedSegment<TStore> segment, ChangeSet changeSet, ref ChunkAccessor<TStore> accessor)
    {
        var r = t_instance;
        if (r != null && r._chunkCount > 0 && ReferenceEquals(r._segment, segment))
        {
            return r._chunks[--r._chunkCount];
        }

        UnreservedAllocations++;
        return segment.AllocateChunk(changeSet, ref accessor);
    }

    /// <summary>
    /// Gives back a chunk taken with <see cref="AllocateUnderLatch"/> that a fault stopped the writer from linking: <see cref="End"/> then frees it with the
    /// rest, instead of leaving it allocated with nothing reaching it. No-op without an open reservation on <paramref name="segment"/>.
    /// </summary>
    internal static void ReturnUnlinked(ChunkBasedSegment<TStore> segment, int chunkId)
    {
        var r = t_instance;
        if (r == null || r._depth == 0 || !ReferenceEquals(r._segment, segment))
        {
            return;
        }

        if (r._chunkCount == r._chunks.Length)
        {
            Array.Resize(ref r._chunks, r._chunks.Length * 2);
        }

        r._chunks[r._chunkCount++] = chunkId;
    }

    /// <summary>Closes one level; the outermost frees every chunk not taken and unpins every page. Called with no latch held.</summary>
    internal void End()
    {
        Debug.Assert(_depth > 0, "End without Begin");
        if (--_depth > 0)
        {
            return;
        }

        try
        {
            while (_chunkCount > 0)
            {
                _segment.FreeChunk(_chunks[--_chunkCount]);
            }
        }
        finally
        {
            while (_pinCount > 0)
            {
                _segment.Store.DecrementSlotRefCount(_pinnedPages[--_pinCount]);
            }

            _chunkCount = 0;
            _segment = null;
        }
    }
}
