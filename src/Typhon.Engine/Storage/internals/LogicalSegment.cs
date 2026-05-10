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

            CreateOrGrow(PageBlockType.None, newPages, curPages.Length, ref NoNextMap, clearNewPages, changeSet);

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

    internal bool Create(PageBlockType type, int filePageIndex, bool clear, ChangeSet changeSet = null)
    {
        Span<int> ids = stackalloc int[1];
        ids[0] = filePageIndex;
        return Create(type, ids, clear, changeSet);
    }

    private static int NoNextMap;

    internal virtual bool Create(PageBlockType type, Span<int> filePageIndices, bool clear, ChangeSet changeSet = null)
    {
        // Phase 5: Storage:Segment:Create event. First page id doubles as the segment identifier.
        if (filePageIndices.Length > 0)
        {
            TyphonEvent.EmitStorageSegmentCreate(filePageIndices[0], filePageIndices.Length);
        }
        return CreateOrGrow(type, filePageIndices, 0, ref NoNextMap, clear, changeSet);
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
                    WalkIndicesMap((i, page, memIdx, span) =>
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
                }

                if (hasPage)
                {
                    _store.UnlatchPageExclusive(memPageIdx);
                }

                isFirstPage = false;
                curIndexMapIndex++;
            }
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

            _store.UnlatchPageExclusive(memPageIdx);
        }

        _pages = filePageIndices.ToArray();

        return true;
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

        // Phase 5: Storage:Segment:Load event.
        TyphonEvent.EmitStorageSegmentLoad(filePageIndex, _pages.Length);

        return true;
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
