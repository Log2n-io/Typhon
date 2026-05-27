// unset

using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine.Internals;

internal enum PageClearMode
{
    None = 0,
    Header = 1,
    WholePage = 2
}

[PublicAPI]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct LogicalSegmentHeader
{
    unsafe public static readonly int Size = sizeof(LogicalSegmentHeader);
    public static readonly int TotalSize =  PageBaseHeader.Size + Size;
    public static readonly int Offset = PageBaseHeader.Size;

    /// <summary>
    /// If the Page Block is a Logical Segment, will store the index to the next block storing Map Data, 0 if there's none.
    /// </summary>
    public int LogicalSegmentNextMapPBID;
    /// <summary>
    /// If the Page Block is a Logical Segment, will store the index to the next block storing Raw Data, 0 if there's none.
    /// </summary>
    public int LogicalSegmentNextRawDataPBID;
    /// <summary>
    /// The segment's runtime role, written on the root page at Create time and read back on Load — makes the segment self-describing so storage
    /// introspection (Module 15) can classify every page without re-deriving ownership from context. Only meaningful on the root page.
    /// </summary>
    public StorageSegmentKind Kind;
}

/// <summary>
/// Expose a Logical segment of Pages, generic over <typeparamref name="TStore"/> for persistent/transient backing.
/// </summary>
/// <remarks>
/// Logical Segment is made of several Pages which IDs are stored in a dedicated private section of its raw data.
/// The segment can easily be shrunk/grown by removing/adding more pages. The first page of the Logical Segment is split in two parts
///  - The Page Directory: 500 entries that reference the first 500 pages of the Logical Segment, overflown data is stored into
///    subsequent dedicated pages that store only indices, so 2000 per page.
///  - The segment first raw data, which is 6000 bytes, instead of 8000 for all subsequent pages.
/// The segment also maintain a linked list in the Page Header to allow faster forward traversal.
/// There is some basic API that allow to store/enumerate fixed size elements, indexed into the logical segment.
/// </remarks>
[PublicAPI]
public class LogicalSegment<TStore> : IDisposable where TStore : struct, IPageStore
{
    internal const int RootHeaderIndexSectionCount = 500;
    internal const int RootHeaderIndexSectionLength = RootHeaderIndexSectionCount * sizeof(int);
    internal const int NextHeadersIndexSectionCount = PagedMMF.PageRawDataSize / sizeof(int);

    protected TStore _store;

    private readonly Lock _growLock = new();
    private volatile int[] _pages;
    private StorageSegmentKind _kind;

    /// <summary>
    /// The segment's runtime role, persisted in the root-page <see cref="LogicalSegmentHeader"/>. Set at <c>Create</c>, restored at <c>Load</c>.
    /// Consumed by the Database File Map (Module 15) so every allocated page classifies without context-derived ownership.
    /// </summary>
    public StorageSegmentKind Kind => _kind;

    public int RootPageIndex
    {
        get
        {
            var pages = _pages;
            if (pages == null || pages.Length == 0)
            {
                throw new InvalidOperationException("Logical segment has not been initialized.");
            }
            return pages[0];
        }
    }

    public int Length => _pages.Length;
    public ReadOnlySpan<int> Pages => _pages;

    /// <summary>The underlying page store.</summary>
    public ref TStore Store => ref _store;

    /// <summary>
    /// Get a typed <see cref="PageAccessor"/> for a segment page via epoch-based protection.
    /// Caller must be inside an <see cref="EpochGuard"/> scope.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PageAccessor GetPage(int segmentPageIndex, long epoch, out int memPageIndex)
    {
        _store.RequestPageEpoch(Pages[segmentPageIndex], epoch, out memPageIndex);
        return _store.GetPage(memPageIndex);
    }

    /// <summary>
    /// Get a typed <see cref="PageAccessor"/> for a segment page with exclusive latch.
    /// Caller must be inside an <see cref="EpochGuard"/> scope.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PageAccessor GetPageExclusive(int segmentPageIndex, long epoch, out int memPageIndex)
    {
        _store.RequestPageEpoch(Pages[segmentPageIndex], epoch, out memPageIndex);
        var latched = _store.TryLatchPageExclusive(memPageIndex);
        Debug.Assert(latched, "TryLatchPageExclusive failed after RequestPageEpoch — page should be Idle");
        return _store.GetPage(memPageIndex);
    }

    /// <summary>
    /// Like <see cref="GetPageExclusive"/> but skips CRC verification.
    /// Used during segment growth where the page content will be immediately overwritten.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal PageAccessor GetPageExclusiveUnchecked(int segmentPageIndex, long epoch, out int memPageIndex)
    {
        _store.RequestPageEpochUnchecked(Pages[segmentPageIndex], epoch, out memPageIndex);
        var latched = _store.TryLatchPageExclusive(memPageIndex);
        Debug.Assert(latched, "TryLatchPageExclusive failed after RequestPageEpochUnchecked — page should be Idle");
        return _store.GetPage(memPageIndex);
    }

    /// <summary>
    /// Get the raw memory address for a segment page via epoch-based protection.
    /// Caller must be inside an <see cref="EpochGuard"/> scope.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe byte* GetPageAddress(int segmentPageIndex, long epoch, out int memPageIndex)
    {
        _store.RequestPageEpoch(Pages[segmentPageIndex], epoch, out memPageIndex);
        return _store.GetMemPageAddress(memPageIndex);
    }

    /// <summary>
    /// Get the raw memory address for a segment page with exclusive latch.
    /// Caller must be inside an <see cref="EpochGuard"/> scope.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe byte* GetPageAddressExclusive(int segmentPageIndex, long epoch, out int memPageIndex)
    {
        _store.RequestPageEpoch(Pages[segmentPageIndex], epoch, out memPageIndex);
        var latched = _store.TryLatchPageExclusive(memPageIndex);
        Debug.Assert(latched, "TryLatchPageExclusive failed after RequestPageEpoch — page should be Idle");
        return _store.GetMemPageAddress(memPageIndex);
    }

    public delegate bool PageMapWalkPredicate(int pageMapIndex, PageAccessor page, int memPageIndex);
    public delegate bool PageMapWalkPredicate<in T>(int pageMapIndex, PageAccessor page, int memPageIndex, T extra) where T : allows ref struct;

    public void WalkIndicesMap(PageMapWalkPredicate predicate, long epoch)
    {
        var pages = _pages;

        var curPageIndex = pages[0];
        var pageMapIndex = 0;
        while (true)
        {
            _store.RequestPageEpoch(curPageIndex, epoch, out var memPageIndex);
            var page = _store.GetPage(memPageIndex);

            if (predicate(pageMapIndex++, page, memPageIndex) == false)
            {
                break;
            }

            ref var lsh = ref page.StructAt<LogicalSegmentHeader>(LogicalSegmentHeader.Offset);
            curPageIndex = lsh.LogicalSegmentNextMapPBID;
            if (curPageIndex == 0)
            {
                break;
            }
        }
    }
    public void WalkIndicesMap<T>(PageMapWalkPredicate<T> predicate, long epoch, T extra) where T : allows ref struct
    {
        var pages = _pages;

        var curPageIndex = pages[0];
        var pageMapIndex = 0;
        while (true)
        {
            _store.RequestPageEpoch(curPageIndex, epoch, out var memPageIndex);
            var page = _store.GetPage(memPageIndex);

            if (predicate(pageMapIndex++, page, memPageIndex, extra) == false)
            {
                break;
            }

            ref var lsh = ref page.StructAt<LogicalSegmentHeader>(LogicalSegmentHeader.Offset);
            curPageIndex = lsh.LogicalSegmentNextMapPBID;
            if (curPageIndex == 0)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Grows the logical segment to the specified new length.
    /// </summary>
    /// <param name="newLength">The new length (must be greater than current length).</param>
    /// <param name="clearNewPages">Whether to clear the content of newly allocated pages.</param>
    /// <param name="changeSet">Optional change set for tracking modifications.</param>
    /// <remarks>
    /// This method is thread-safe. Concurrent reads of existing pages remain valid during growth.
    /// The <see cref="_pages"/> field is volatile, ensuring visibility of the new array after growth.
    /// </remarks>
    public void Grow(int newLength, bool clearNewPages, ChangeSet changeSet = null)
    {
        lock (_growLock)
        {
            var curPages = _pages;
            if (curPages == null)
            {
                throw new InvalidOperationException("Logical segment has not been initialized.");
            }
            if (newLength <= curPages.Length)
            {
                // Already at or above requested size (may have been grown by another thread)
                return;
            }

            var oldLen = curPages.Length;
            var newPages = new int[newLength];
            var newPagesAsSpan = newPages.AsSpan();
            curPages.CopyTo(newPagesAsSpan);
            _store.AllocatePages(ref newPagesAsSpan, curPages.Length, changeSet);

            int noNextMap = 0;
            CreateOrGrow(PageBlockType.None, newPages, curPages.Length, ref noNextMap, clearNewPages, changeSet);

            // Phase 5: Storage:Segment:Grow event. Use the first page id as a stable segment identifier.
            TyphonEvent.EmitStorageSegmentGrow(newPages[0], oldLen, newLength);
        }
    }

    internal LogicalSegment(TStore store)
    {
        _store = store;
    }

    public void Dispose() => _pages = null;

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static int GetMaxItemCount<T>(bool firstPage) where T : unmanaged => GetMaxItemCount(firstPage, Marshal.SizeOf<T>());
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static int GetMaxItemCount(bool firstPage, int itemSize) => (firstPage ? (PagedMMF.PageRawDataSize - RootHeaderIndexSectionLength) : PagedMMF.PageRawDataSize) / itemSize;
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static int GetItemCount<T>(int pageCount) where T : unmanaged => GetItemCount(pageCount, Marshal.SizeOf<T>());
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static int GetItemCount(int pageCount, int itemSize) => ((pageCount * PagedMMF.PageRawDataSize) - RootHeaderIndexSectionLength) / itemSize;
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static (int, int) GetItemLocation<T>(int itemIndex) => GetItemLocation(itemIndex, Marshal.SizeOf<T>());
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static (int, int) GetItemLocation(int itemIndex, int itemSize)
    {
        var s = itemSize;
        var fs = PagedMMF.PageRawDataSize - RootHeaderIndexSectionLength;
        var ss = PagedMMF.PageRawDataSize;

        var fc = fs / s;
        if (itemIndex < fc)
        {
            return (0, itemIndex);
        }

        var pi = Math.DivRem(itemIndex - fc, ss / s, out var off);
        return (pi + 1, off);
    }

    internal bool Create(PageBlockType type, StorageSegmentKind kind, int filePageIndex, bool clear, ChangeSet changeSet = null)
    {
        Span<int> ids = stackalloc int[1];
        ids[0] = filePageIndex;
        return Create(type, kind, ids, clear, changeSet);
    }

    internal virtual bool Create(PageBlockType type, StorageSegmentKind kind, Span<int> filePageIndices, bool clear, ChangeSet changeSet = null)
    {
        // The kind is persisted into the root page's header by CreateOrGrow (read back by Load) — set it before so the root-page write captures it.
        _kind = kind;
        // Phase 5: Storage:Segment:Create event. First page id doubles as the segment identifier.
        if (filePageIndices.Length > 0)
        {
            TyphonEvent.EmitStorageSegmentCreate(filePageIndices[0], filePageIndices.Length);
        }
        int noNextMap = 0;
        return CreateOrGrow(type, filePageIndices, 0, ref noNextMap, clear, changeSet);
    }

    internal unsafe bool CreateOrGrow(PageBlockType type, Span<int> filePageIndices, int growFrom, ref int nextMap, bool clear, ChangeSet changeSet)
    {
        var epoch = _store.EpochManager.GlobalEpoch;

        // Compute the number of indices map pages needed to store the indices (root + subsequent).
        // The end of the indices list is marked by a 0 value, we need to save space for this entry too, so the next line is accurate, if you wonder.
        var mapPageCount = 1 + ((filePageIndices.Length - RootHeaderIndexSectionCount + NextHeadersIndexSectionCount) / NextHeadersIndexSectionCount);

        // Store the indices, code is complex because we may need multiple pages to store them all.
        // Reminder of how data is structured:
        // - Each page is 8192 bytes, with 192 bytes of header, and 8000 bytes of raw data.
        // - The first page is the root page, its raw data contains the first 500 indices, and the first 6000 bytes of data.
        // - If the segment is bigger than 500 pages, we allocate dedicated pages to store the remaining indices, so 2000 indices per page.
        // - Subsequent data pages are storing data only, so 8000 bytes each.
        // In the headers, we maintain two linked lists:
        // 1. The logical segment next map page ID (LogicalSegmentNextMapPBID), which is used to traverse the indices pages.
        // 2. The logical segment next raw data page ID(LogicalSegmentNextRawDataPBID), which is used to traverse the data pages.
        // Both of these linked lists are terminated by 0.
        {
            // Start by building and/or allocating the indices pages, considering the growFrom parameter.
            Span<int> mapIndices = stackalloc int[mapPageCount];
            mapIndices[0] = filePageIndices[0];                         // The first page is always the root page, so we set it here.;
            var mapIndexAllocStartFrom = 0;

            if (mapPageCount > 1)
            {
                // Need to rebuild the indices pages
                if (growFrom > 0)
                {
                    WalkIndicesMap((i, _, memIdx, span) =>
                    {
                        span[i] = _store.GetFilePageIndex(memIdx);
                        mapIndexAllocStartFrom = i + 1;                 // Update the start index for the first page to allocate
                        return true;
                    }, epoch, mapIndices);
                }

                // If a nextMap is provided, we need to use it as the first new map page
                var allocStartFrom = mapIndexAllocStartFrom;
                if ((nextMap != 0) && (allocStartFrom < mapIndices.Length))
                {
                    mapIndices[allocStartFrom++] = nextMap;
                    nextMap = 0;                                        // Signal the caller that we used the given nextMap
                }

                // Allocated the remaining indices pages using the allocator
                allocStartFrom = Math.Max(1, allocStartFrom);           // Ensure we start from the second page, as the first is always the root page
                if (allocStartFrom < mapIndices.Length)
                {
                    var pagesToAllocate = mapIndices[1..];
                    _store.AllocatePages(ref pagesToAllocate, allocStartFrom - 1, changeSet);
                }
            }

            bool isFirstPage = true;
            var remainingIndices = filePageIndices.Length;
            var mapIndexBaseOffset = 0;
            var curIndexMapIndex = 0;
            var curFilePageIndex = 0;
            var curStartPageIndex = growFrom;

            while (remainingIndices > 0)
            {
                var curIndicesCount = Math.Min(remainingIndices, isFirstPage ? RootHeaderIndexSectionCount : NextHeadersIndexSectionCount);

                var isNewPage = (curIndexMapIndex >= mapIndexAllocStartFrom) && ((curIndexMapIndex > 0) || (growFrom == 0));
                var isLastAllocated = curIndexMapIndex == (mapIndexAllocStartFrom - 1);
                var curMapPageIndex = mapIndices[curIndexMapIndex];
                var hasPage = false;
                PageAccessor page = default;
                int memPageIdx = -1;
                var isPageDirty = false;

                // If it's a new page, initialize it (skip CRC — page will be fully overwritten)
                if (isNewPage)
                {
                    _store.RequestPageEpochUnchecked(curMapPageIndex, epoch, out memPageIdx);
                    var latched = _store.TryLatchPageExclusive(memPageIdx);
                    Debug.Assert(latched, "TryLatchPageExclusive failed after RequestPageEpochUnchecked");
                    page = _store.GetPage(memPageIdx);
                    hasPage = true;

                    InitHeader(page.Address, PageClearMode.Header,
                        PageBlockFlags.IsLogicalSegment | (isFirstPage ? PageBlockFlags.IsLogicalSegmentRoot : PageBlockFlags.None),
                        type, 1);
                    isPageDirty = true;
                }

                // Update the indices map linked list, starting the index map before the first to allocate
                if (isNewPage || isLastAllocated)
                {
                    if (hasPage == false)
                    {
                        _store.RequestPageEpoch(curMapPageIndex, epoch, out memPageIdx);
                        var latched = _store.TryLatchPageExclusive(memPageIdx);
                        Debug.Assert(latched, "TryLatchPageExclusive failed after RequestPageEpoch");
                        page = _store.GetPage(memPageIdx);
                        hasPage = true;
                    }
                    ref var lsh = ref page.StructAt<LogicalSegmentHeader>(LogicalSegmentHeader.Offset);
                    lsh.LogicalSegmentNextMapPBID = ((curIndexMapIndex + 1) < mapIndices.Length) ? mapIndices[curIndexMapIndex + 1] : 0;
                    isPageDirty = true;
                }

                // In the current map, set the page indices it contains
                if ((curStartPageIndex >= mapIndexBaseOffset) && (curStartPageIndex < (mapIndexBaseOffset + curIndicesCount)))
                {
                    if (hasPage == false)
                    {
                        _store.RequestPageEpoch(curMapPageIndex, epoch, out memPageIdx);
                        var latched = _store.TryLatchPageExclusive(memPageIdx);
                        Debug.Assert(latched, "TryLatchPageExclusive failed after RequestPageEpoch");
                        page = _store.GetPage(memPageIdx);
                        hasPage = true;
                    }

                    var rd = page.RawData<int>();
                    int j = curStartPageIndex - mapIndexBaseOffset;
                    curFilePageIndex += j;
                    for (; j < curIndicesCount; j++)
                    {
                        rd[j] = filePageIndices[curFilePageIndex++];
                    }

                    if ((remainingIndices - curIndicesCount) == 0)
                    {
                        if (j < rd.Length)
                        {
                            rd[j] = 0;
                        }

                        // The current page is full, we need on fetch one more... just to store the termination 0 value
                        else
                        {
                            _store.RequestPageEpochUnchecked(mapIndices[curIndexMapIndex + 1], epoch, out var endMemIdx);
                            var endLatched = _store.TryLatchPageExclusive(endMemIdx);
                            Debug.Assert(endLatched, "TryLatchPageExclusive failed after RequestPageEpochUnchecked");
                            var endPage = _store.GetPage(endMemIdx);
                            InitHeader(endPage.Address, PageClearMode.Header, PageBlockFlags.IsLogicalSegment, type, 1);
                            changeSet?.AddByMemPageIndex(endMemIdx);
                            endPage.RawData<int>(0, 1)[0] = 0;
                            _store.EnsureDirtyAtLeast(endMemIdx, 1);  // durability: see comments below in main map-page block
                            _store.UnlatchPageExclusive(endMemIdx);
                        }
                    }
                    isPageDirty = true;
                }
                else
                {
                    curFilePageIndex += curIndicesCount;
                }

                mapIndexBaseOffset += curIndicesCount;
                remainingIndices -= curIndicesCount;

                // Slide the curStartPageIndex range to the next map page if we are after the growFrom index
                // In other words, keep the growFrom index if we didn't reach it yet
                if (curStartPageIndex < mapIndexBaseOffset)
                {
                    curStartPageIndex = mapIndexBaseOffset;
                }

                if (isPageDirty)
                {
                    changeSet?.AddByMemPageIndex(memPageIdx);
                    // Durability: directory map-page write (root or extension) must survive a checkpoint regardless of whether the caller provided a
                    // ChangeSet. Same rationale as the data-page-init loop above. Without this the directory entries get evicted as clean and the next Load
                    // sees a truncated page list.
                    _store.EnsureDirtyAtLeast(memPageIdx, 2);
                }

                if (hasPage)
                {
                    _store.UnlatchPageExclusive(memPageIdx);
                }

                isFirstPage = false;
                curIndexMapIndex++;
            }
        }

        // Patch the OLD tail's forward-chain pointer when growing. The data-page header chain (LogicalSegmentNextRawDataPBID) is the segment's
        // structural-integrity invariant — chain count must equal the directory count at every healthy moment. Before this fix the inner data-page-init loop
        // only touched pages [growFrom, filePageIndices.Length), so the OLD tail (page at growFrom-1) was left with its prior 0 terminator and the chain was
        // permanently truncated at the original allocation size. With it, every Grow extends the chain by exactly the newly-added pages.
        if (growFrom > 0)
        {
            var oldTailFilePage = filePageIndices[growFrom - 1];
            _store.RequestPageEpoch(oldTailFilePage, epoch, out var oldTailMemIdx);
            var oldTailLatched = _store.TryLatchPageExclusive(oldTailMemIdx);
            Debug.Assert(oldTailLatched, "TryLatchPageExclusive failed on old-tail page during Grow chain-patch");
            var oldTailPage = _store.GetPage(oldTailMemIdx);
            ref var oldTailLsh = ref oldTailPage.StructAt<LogicalSegmentHeader>(LogicalSegmentHeader.Offset);
            oldTailLsh.LogicalSegmentNextRawDataPBID = filePageIndices[growFrom];
            changeSet?.AddByMemPageIndex(oldTailMemIdx);
            // Durability: EnsureDirtyAtLeast guarantees the page survives one checkpoint cycle so the write reaches disk regardless of whether the caller
            // provided a ChangeSet. UnlatchPageExclusive only releases the latch (it doesn't mark dirty), and many CreateOrGrow callers pass `null` for
            // changeSet — so without this call the chain-pointer write can be evicted as clean and lost on reopen.
            _store.EnsureDirtyAtLeast(oldTailMemIdx, 1);
            _store.UnlatchPageExclusive(oldTailMemIdx);
        }

        // Initialize the subsequent pages on disk
        // Use unchecked access: these are new pages about to be fully overwritten (cleared + header init).
        // In WAL mode, CRC verification may fail because the growth path doesn't write WAL/FPI records, so evicted pages would have stale CRCs with
        // no FPI available for repair.
        for (var i = growFrom; i < filePageIndices.Length; i++)
        {
            var pageIndex = filePageIndices[i];
            _store.RequestPageEpochUnchecked(pageIndex, epoch, out var memPageIdx);
            var latched = _store.TryLatchPageExclusive(memPageIdx);
            Debug.Assert(latched, "TryLatchPageExclusive failed after RequestPageEpochUnchecked");
            var page = _store.GetPage(memPageIdx);

            changeSet?.AddByMemPageIndex(memPageIdx);

            if (clear)
            {
                var offset = page.IsRoot ? RootHeaderIndexSectionLength : 0;
                page.RawData<byte>(offset, PagedMMF.PageRawDataSize - offset).Clear();
            }

            InitHeader(page.Address, PageClearMode.None, PageBlockFlags.IsLogicalSegment | (i == 0 ? PageBlockFlags.IsLogicalSegmentRoot : PageBlockFlags.None), type, 1);

            // Update link list of the pages that make the segment
            ref var lsh = ref page.StructAt<LogicalSegmentHeader>(LogicalSegmentHeader.Offset);
            lsh.LogicalSegmentNextRawDataPBID = ((i + 1) < filePageIndices.Length) ? filePageIndices[i + 1] : 0;
            // Persist the segment kind on the root page (self-describing for storage introspection, Module 15).
            if (i == 0)
            {
                lsh.Kind = _kind;
            }

            // Durability: same rationale as the old-tail patch above — UnlatchPageExclusive doesn't mark the page dirty and ChangeSet may be null
            // (DatabaseEngine.InitializeArchetypes allocates segments with null ChangeSet for EntityMap / Cluster / per-archetype-index — see lines 4242,
            // 4263, 4312, 4344). Without EnsureDirtyAtLeast the chain-pointer + KIND + clear-data writes are evicted as clean and the next reopen sees
            // zeros, producing the chain↔directory mismatches the integrity check catches.
            _store.EnsureDirtyAtLeast(memPageIdx, 2);

            _store.UnlatchPageExclusive(memPageIdx);
        }

        _pages = filePageIndices.ToArray();

        // Post-condition #1: walk the chain in memory. Mismatch here ⇒ bug in CreateOrGrow pointer writes (not persistence).
        var memChainCount = WalkForwardChainPageCount(epoch);
        if (memChainCount != _pages.Length)
        {
            throw new InvalidOperationException(
                $"CreateOrGrow IN-MEMORY chain mismatch: root={_pages[0]} kind={_kind} growFrom={growFrom} " +
                $"expected={_pages.Length} chain={memChainCount} (diff={memChainCount - _pages.Length:+0;-#}) " +
                $"— bug is in CreateOrGrow's pointer writes, not persistence.");
        }

        // Post-condition #2: walk the directory section in memory by reading the root + extension map pages RIGHT NOW (same code-path Load uses). Mismatch
        // here ⇒ bug in CreateOrGrow's directory writes (not persistence).
        var memDirCount = WalkDirectoryPageCount(epoch);
        if (memDirCount != _pages.Length)
        {
            throw new InvalidOperationException(
                $"CreateOrGrow IN-MEMORY directory mismatch: root={_pages[0]} kind={_kind} growFrom={growFrom} " +
                $"expected={_pages.Length} directory={memDirCount} (diff={memDirCount - _pages.Length:+0;-#}) " +
                $"— bug is in CreateOrGrow's directory writes, not persistence.");
        }

        return true;
    }

    /// <summary>
    /// Walks the on-page directory section (root + extension map pages reached via <c>LogicalSegmentNextMapPBID</c>) and counts non-zero entries, terminating
    /// at the first zero — mirroring exactly what <see cref="Load"/> does. Used by the post-condition assertion at the end of <see cref="CreateOrGrow"/> to
    /// isolate whether the in-memory directory after a write matches <c>_pages.Length</c>, which discriminates "CreateOrGrow logic bug" from "persistence bug".
    /// </summary>
    internal int WalkDirectoryPageCount(long epoch)
    {
        var rootIndex = RootPageIndex;
        _store.RequestPageEpoch(rootIndex, epoch, out var memPageIndex);
        var page = _store.GetPage(memPageIndex);

        var count = 0;
        var rd = page.RawDataReadOnly<int>(0, RootHeaderIndexSectionCount);
        var maxIndicesForPage = RootHeaderIndexSectionCount;
        var i = 0;
        while (rd[i] != 0)
        {
            count++;
            if (++i != maxIndicesForPage)
            {
                continue;
            }
            ref var lsh = ref page.StructAt<LogicalSegmentHeader>(LogicalSegmentHeader.Offset);
            if (lsh.LogicalSegmentNextMapPBID == 0)
            {
                break;
            }
            _store.RequestPageEpoch(lsh.LogicalSegmentNextMapPBID, epoch, out memPageIndex);
            page = _store.GetPage(memPageIndex);
            rd = page.RawDataReadOnly<int>(0, NextHeadersIndexSectionCount);
            i = 0;
            maxIndicesForPage = NextHeadersIndexSectionCount;
        }
        return count;
    }

    /// <summary>
    /// Initialize page header directly from a raw pointer (epoch-based path).
    /// </summary>
    internal static unsafe void InitHeader(byte* pageAddr, PageClearMode clearMode, PageBlockFlags flags, PageBlockType type, short formatRevision)
    {
        ref var header = ref Unsafe.AsRef<PageBaseHeader>(pageAddr + PageBaseHeader.Offset);

        if (clearMode == PageClearMode.Header)
        {
            // Preserve ModificationCounter across clear — it's the seqlock counter managed by TryLatchPageExclusive/UnlatchPageExclusive. Zeroing it while
            // the page is latched leaves the counter odd after unlatch, causing CopyPageWithSeqlock to spin forever.
            var savedModCounter = header.ModificationCounter;
            new Span<byte>(pageAddr, PagedMMF.PageHeaderSize).Clear();
            header.ModificationCounter = savedModCounter;
        }
        else if (clearMode == PageClearMode.WholePage)
        {
            var savedModCounter = header.ModificationCounter;
            new Span<byte>(pageAddr, PagedMMF.PageSize).Clear();
            header.ModificationCounter = savedModCounter;
        }

        header.Flags = flags;
        header.Type = type;
        header.FormatRevision = formatRevision;
    }

    internal virtual bool Load(int filePageIndex)
    {
        var epoch = _store.EpochManager.GlobalEpoch;
        _store.RequestPageEpoch(filePageIndex, epoch, out var memPageIndex);
        var page = _store.GetPage(memPageIndex);

        // Restore the persisted segment kind from the root page before `page` is reassigned to traverse map pages.
        _kind = page.StructAt<LogicalSegmentHeader>(LogicalSegmentHeader.Offset).Kind;

        var pages = new List<int>();
        var rd = page.RawDataReadOnly<int>(0, RootHeaderIndexSectionCount);
        var maxIndicesForPage = RootHeaderIndexSectionCount;
        var i = 0;
        while (rd[i] != 0)
        {
            pages.Add(rd[i]);

            if (++i != maxIndicesForPage)
            {
                continue;
            }

            // We reached the end of the root page, we need to load more pages
            ref var lsh = ref page.StructAt<LogicalSegmentHeader>(LogicalSegmentHeader.Offset);
            if (lsh.LogicalSegmentNextMapPBID == 0)
            {
                break; // No more pages
            }

            _store.RequestPageEpoch(lsh.LogicalSegmentNextMapPBID, epoch, out memPageIndex);
            page = _store.GetPage(memPageIndex);
            rd = page.RawDataReadOnly<int>(0, NextHeadersIndexSectionCount);
            i = 0; // Reset index for the new page

            maxIndicesForPage = NextHeadersIndexSectionCount;
        }

        _pages = pages.ToArray();

        // Structural integrity invariant: the data-page forward chain (LogicalSegmentNextRawDataPBID) must reach exactly as many pages as the persisted Page
        // Directory enumerates. The two are written by independent code paths in CreateOrGrow — the chain pointer is updated per-page in the data-page-init
        // loop and on the old tail; the Directory entries are written into root/extension-map raw-data sections. A mismatch means one of the two writes lost
        // durability across a crash / checkpoint race, leaving a structurally inconsistent segment that would silently lose addressing of some pages. We
        // throw early at Load rather than let the corruption propagate. Zero storage cost — both fields already exist; cost is O(N) page-header reads per
        // segment on Open, paid once.
        var chainCount = WalkForwardChainPageCount(epoch);
        if (chainCount != _pages.Length)
        {
            throw new InvalidOperationException(
                $"LogicalSegment integrity check failed at Load: root={filePageIndex} kind={_kind} directory={_pages.Length} chain={chainCount} " +
                $"(diff={chainCount - _pages.Length:+0;-#}). Signature of a lost-write durability bug — either the directory append or the " +
                $"forward-chain pointer didn't persist before the previous close.");
        }

        // Phase 5: Storage:Segment:Load event.
        TyphonEvent.EmitStorageSegmentLoad(filePageIndex, _pages.Length);

        return true;
    }

    /// <summary>
    /// Walks the segment's data-page forward chain — start at the root, follow each page's <see cref="LogicalSegmentHeader.LogicalSegmentNextRawDataPBID"/>
    /// pointer until it reaches <c>0</c>, counting pages along the way.
    /// </summary>
    /// <remarks>
    /// Pure integrity-check helper — read-only, no allocations. Caller must be inside an <see cref="EpochGuard"/> scope (or pass an epoch known to be live).
    /// Used by <see cref="Load"/> as the chain↔directory cross-check that catches lost-write durability bugs at the earliest moment.
    /// </remarks>
    internal int WalkForwardChainPageCount(long epoch)
    {
        var rootIndex = RootPageIndex;
        _store.RequestPageEpoch(rootIndex, epoch, out var memPageIndex);
        var page = _store.GetPage(memPageIndex);

        var count = 1;
        // Cycle guard: any healthy chain is bounded by the directory's page count. A runaway chain (cycle or wildly past the directory's length) is itself a
        // corruption signal — the caller's mismatch detection will flag it against the directory count.
        var maxWalk = ((_pages?.Length ?? 0) * 2) + 16;
        while (count < maxWalk)
        {
            var next = page.StructAt<LogicalSegmentHeader>(LogicalSegmentHeader.Offset).LogicalSegmentNextRawDataPBID;
            if (next == 0)
            {
                return count;
            }
            _store.RequestPageEpoch(next, epoch, out memPageIndex);
            page = _store.GetPage(memPageIndex);
            count++;
        }
        return count;
    }

    /// <summary>
    /// Enumerates the file-page indices of the segment's <b>directory map extension pages</b> — the pages outside the root that hold the page-index list when
    /// the segment owns more than <see cref="RootHeaderIndexSectionCount"/> data pages. Walks <see cref="LogicalSegmentHeader.LogicalSegmentNextMapPBID"/>
    /// starting from the root (which is excluded — it is already exposed via <c>Pages[0]</c>) until the chain terminates with <c>0</c>.
    /// </summary>
    /// <remarks>
    /// Used by storage-integrity audits: dir-map ext pages are bit-set in the occupancy bitmap but are not data pages, so they don't appear in
    /// <see cref="Pages"/>. Without this walk a healthy engine would falsely look like it has orphan pages. Caller must be inside an <see cref="EpochGuard"/>
    /// scope.
    /// </remarks>
    internal void CollectDirectoryMapExtensionPages(long epoch, List<int> dest)
    {
        var rootIndex = RootPageIndex;
        _store.RequestPageEpoch(rootIndex, epoch, out var memPageIndex);
        var page = _store.GetPage(memPageIndex);
        var maxWalk = (_pages?.Length ?? 0) / NextHeadersIndexSectionCount + 4; // cycle guard
        var step = 0;
        while (step < maxWalk)
        {
            var next = page.StructAt<LogicalSegmentHeader>(LogicalSegmentHeader.Offset).LogicalSegmentNextMapPBID;
            if (next == 0)
            {
                return;
            }
            dest.Add(next);
            _store.RequestPageEpoch(next, epoch, out memPageIndex);
            page = _store.GetPage(memPageIndex);
            step++;
        }
    }

    public void Clear()
    {
        var epoch = _store.EpochManager.GlobalEpoch;
        var cs = _store.CreateChangeSet();
        for (int i = 0; i < Length; i++)
        {
            var page = GetPageExclusive(i, epoch, out var memPageIdx);
            cs?.AddByMemPageIndex(memPageIdx);
            page.RawData<byte>().Clear();
            _store.UnlatchPageExclusive(memPageIdx);
        }
        cs?.SaveChanges();
    }

    public void Fill(byte value)
    {
        var epoch = _store.EpochManager.GlobalEpoch;
        var cs = _store.CreateChangeSet();
        for (int i = 0; i < Length; i++)
        {
            var page = GetPageExclusive(i, epoch, out var memPageIdx);
            cs?.AddByMemPageIndex(memPageIdx);
            var offset = page.IsRoot ? RootHeaderIndexSectionLength : 0;
            page.RawData<byte>(offset, PagedMMF.PageRawDataSize - offset).Fill(value);
            _store.UnlatchPageExclusive(memPageIdx);
        }
        cs?.SaveChanges();
    }
}
