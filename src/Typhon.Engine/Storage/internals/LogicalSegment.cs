// CS1591: this file declares public-accessibility types that live in the internal namespace (Phase 2b entanglement, see
// claude/research/PublicVsInternalApiClassification.md). They are excluded from the published API reference, so consumer-facing
// doc coverage is not enforced here.
#pragma warning disable 1591

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
    /// <summary>
    /// CK-05 A/B slot-pairing: the physical file-page index of this directory page's TWIN slot (the alternate of the two slots the directory page's
    /// current content alternates between). Set once at allocation, immutable thereafter, identical in both slots. <c>0</c> = "no twin" (a normal,
    /// non-directory page, or a page that predates C2). Present on every directory page — the root page and each map-extension page.
    /// </summary>
    public int TwinPageIndex;
}

/// <summary>
/// Expose a Logical segment of Pages, generic over <typeparamref name="TStore"/> for persistent/transient backing.
/// </summary>
/// <remarks>
/// Logical Segment is made of several Pages which IDs are stored in a dedicated private section of its raw data.
/// The segment can easily be shrunk/grown by removing/adding more pages. The first page (the ROOT) is a pure <b>directory
/// page</b>: its entire 8000-byte raw-data area holds the page directory — 2000 entries that reference the first 2000 pages
/// of the segment. Beyond 2000 pages, overflow entries spill into dedicated map-extension pages (also 2000 entries each).
/// The root carries <b>no</b> segment data, so the CK-05 twin that shadows every directory page protects only the immutable
/// directory, never live data, and root/extension directory addressing stays uniform. Consequently a segment always spans
/// at least 2 pages (the directory root + at least one data page); the allocators clamp to this minimum.
/// The segment also maintain a linked list in the Page Header to allow faster forward traversal.
/// There is some basic API that allow to store/enumerate fixed size elements, indexed into the logical segment.
/// </remarks>
[PublicAPI]
public class LogicalSegment<TStore> : IDisposable where TStore : struct, IPageStore
{
    internal const int NextHeadersIndexSectionCount = PagedMMF.PageRawDataSize / sizeof(int);
    // Directory-only root (v4): the root page's whole raw-data area is the page directory, so it holds the SAME number of
    // entries as a map-extension page. This makes the root carry no segment data — the CK-05 twin protects only directory.
    internal const int RootHeaderIndexSectionCount = NextHeadersIndexSectionCount;
    internal const int RootHeaderIndexSectionLength = RootHeaderIndexSectionCount * sizeof(int);

    protected TStore _store;

    private readonly Lock _growLock = new();
    private volatile PageList _pageList;
    private StorageSegmentKind _kind;

    // The directory pages, root first, in chain order: what a grow reads instead of walking the directory chain on disk, and what it appends to. Written
    // under _growLock (and by Create/Load before the segment is shared) (#1205).
    private int[] _mapPages;
    private int _mapPageCount;

    /// <summary>The directory pages, root first, in chain order. Test seam.</summary>
    internal ReadOnlySpan<int> DirectoryPagesForTest => _mapPages.AsSpan(0, _mapPageCount);

    /// <summary>
    /// The segment's page list as one immutable snapshot: <see cref="Items"/> may be longer than <see cref="Count"/>, holding room for later grows, which
    /// append in place past <see cref="Count"/> — where no reader of this snapshot looks — and publish a new snapshot. A grow therefore copies the list only
    /// when the room runs out, doubling, instead of on every grow: that copy, twice per grow, made a segment's growth quadratic in its size (#1205).
    /// </summary>
    private sealed class PageList
    {
        internal readonly int[] Items;
        internal readonly int Count;

        internal PageList(int[] items, int count)
        {
            Items = items;
            Count = count;
        }
    }

    /// <summary>
    /// Test hook: runs before each step of a Create or Grow that can still fail, with the file page index it is about to fault in or latch: each
    /// new data page it initializes, then each page it pins and each it latches for the publish (rule PS-11). Null in production.
    /// </summary>
    internal Action<int> GrowStepProbe;

    /// <summary>
    /// The segment's runtime role, persisted in the root-page <see cref="LogicalSegmentHeader"/>. Set at <c>Create</c>, restored at <c>Load</c>.
    /// Consumed by the Database File Map (Module 15) so every allocated page classifies without context-derived ownership.
    /// </summary>
    public StorageSegmentKind Kind => _kind;

    public int RootPageIndex
    {
        get
        {
            var list = _pageList;
            if (list == null || list.Count == 0)
            {
                throw new InvalidOperationException("Logical segment has not been initialized.");
            }
            return list.Items[0];
        }
    }

    /// <summary>Number of file pages this segment owns; <c>0</c> when it was never materialised or has been disposed.</summary>
    /// <remarks>
    /// Null-safe deliberately. Callers guard with <c>Length == 0</c> to mean "nothing here, skip it" (see <c>DatabaseEngine.AddSegment</c>) — which threw a
    /// <see cref="NullReferenceException"/> out of the very check written to prevent it whenever the segment had never been created.
    /// </remarks>
    public int Length => _pageList?.Count ?? 0;

    public ReadOnlySpan<int> Pages
    {
        get
        {
            var list = _pageList;
            return list == null ? default : new ReadOnlySpan<int>(list.Items, 0, list.Count);
        }
    }

    /// <summary>The underlying page store.</summary>
    public ref TStore Store => ref _store;

    /// <summary>
    /// How many bytes of a page's metadata region this segment claims for its own use, and therefore how much is left
    /// for the per-sector verification footer (which grows down from the end of the region).
    /// </summary>
    /// <param name="isRootPage">Whether the page is the segment's directory root, which can have a different chunk count.</param>
    /// <remarks>
    /// <b>This must be answered before the page is first written, not after.</b> A page is unlatched and dirty between
    /// <c>CreateOrGrow</c> initialising it and its owner finishing with it, so a checkpoint can persist it in that window.
    /// If the page declared a finer geometry than its bitmap allows — even transiently — the footer stamp would land on
    /// top of the chunk-occupancy bitmap and corrupt chunk allocation. A plain logical segment keeps no bitmap, so the
    /// base answer is zero and its pages get the finest granularity.
    /// </remarks>
    protected virtual int MetadataReservedBytes(bool isRootPage) => 0;

    /// <summary>
    /// The chunk stride to record on every page of this segment, or <c>0</c> for a segment that has no chunks.
    /// </summary>
    /// <remarks>
    /// Consulted at page init for the same reason as <see cref="MetadataReservedBytes"/>: the value must be on the page
    /// before anything can write it out, and a post-hoc stamp would race a checkpoint that caught the page in between.
    /// See <see cref="SegmentGeometry"/> for why the file needs it at all.
    /// </remarks>
    protected virtual int ChunkStrideForGeometry => 0;

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
    /// A page of this segment for a whole-segment read (EP-02): pinned, not epoch-tagged, until <see cref="ReleasePageForRead"/>. Release it before
    /// reading the next page, so the scan holds one page of the cache at a time however large the segment is.
    /// </summary>
    internal PageAccessor AcquirePageForRead(int segmentPageIndex, out int memPageIndex)
    {
        _store.AcquirePageForRead(Pages[segmentPageIndex], out memPageIndex);
        return _store.GetPage(memPageIndex);
    }

    /// <summary>Releases a page taken by <see cref="AcquirePageForRead"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ReleasePageForRead(int memPageIndex) => _store.ReleasePageForRead(memPageIndex);

    /// <summary>
    /// Fault <paramref name="filePageIndex"/> into the page cache and acquire its exclusive latch with NO eviction window
    /// (#2 fix). The epoch tag does not pin a not-yet-latched slot — the grow path tags pages with a bare
    /// <see cref="EpochManager.GlobalEpoch"/> snapshot (it cannot hold an <see cref="EpochGuard"/> across the fetch, which
    /// blocks on page-cache back-pressure — an EBR pin held across a blocking wait deadlocks reclamation), and that
    /// snapshot goes stale the moment the global epoch advances. So the just-faulted slot can be evicted+reused before we
    /// latch it; writing/unlatching it then corrupts another thread's page. Defence: <see cref="IPageStore.IncrementSlotRefCount"/>
    /// pins the one slot across the request→latch gap (Interlocked, no reclamation stall), and a bounded retry re-faults on
    /// the rare residual miss. We NEVER proceed without the latch — exhausting retries throws rather than writing a slot we
    /// don't own.
    /// </summary>
    private int RequestExclusiveForGrow(int filePageIndex, long epoch, bool verifyCrc)
    {
        const int maxAttempts = 64;
        for (var attempt = 0; ; attempt++)
        {
            int memPageIndex;
            if (verifyCrc)
            {
                _store.RequestPageEpoch(filePageIndex, epoch, out memPageIndex);
            }
            else
            {
                _store.RequestPageEpochUnchecked(filePageIndex, epoch, out memPageIndex);
            }

            // Pin the slot so it cannot be evicted between here and the latch, then latch. Once latched (PageState=Exclusive)
            // the page is eviction-proof on its own, so the SlotRefCount pin can be dropped immediately. The drop MUST run
            // even if TryLatchPageExclusive throws (e.g. PageCache lock-timeout) — otherwise the SlotRefCount leaks, the page
            // becomes permanently un-evictable, and accumulated leaks fill the cache → back-pressure stalls grows that hold a
            // page latch → the checkpoint spins forever in CopyPageWithSeqlock on that latched page. Hence try/finally.
            _store.IncrementSlotRefCount(memPageIndex);
            bool latched;
            try
            {
                latched = _store.TryLatchPageExclusive(memPageIndex);
            }
            finally
            {
                _store.DecrementSlotRefCount(memPageIndex);
            }
            if (latched)
            {
                return memPageIndex;
            }

            if (attempt >= maxAttempts)
            {
                throw new InvalidOperationException(
                    $"LogicalSegment grow could not exclusively latch file page {filePageIndex} after {maxAttempts} attempts (page-cache slot contention).");
            }
        }
    }

    /// <summary>
    /// Faults <paramref name="filePageIndex"/> in and holds its slot with a slot reference, so the publish step of a grow can latch it later without
    /// waiting for a free cache slot (rule PS-11). The caller drops the reference with <see cref="IPageStore.DecrementSlotRefCount"/>.
    /// </summary>
    /// <remarks>
    /// The latch <see cref="RequestExclusiveForGrow"/> takes is what proves the slot still holds this file page when the reference lands. It is
    /// released straight away: held across the rest of the grow, it would stall the checkpoint that a back-pressure wait depends on (PS-09).
    /// </remarks>
    private int PinForPublish(int filePageIndex, long epoch, bool verifyCrc)
    {
        GrowStepProbe?.Invoke(filePageIndex);
        var memPageIndex = RequestExclusiveForGrow(filePageIndex, epoch, verifyCrc);
        _store.IncrementSlotRefCount(memPageIndex);
        _store.UnlatchPageExclusive(memPageIndex);
        return memPageIndex;
    }

    /// <summary>
    /// Gives back pages a grow allocated and never published (rule PS-11). Releasing needs the page cache too, so it can fail in turn; that failure
    /// is recorded on <paramref name="failure"/> rather than replacing it, since the grow's own failure is the one the caller has to see. The pages
    /// then leak, which corrupts nothing: nothing references them.
    /// </summary>
    private void ReleaseUnpublished(ReadOnlySpan<int> pageIds, ChangeSet changeSet, Exception failure)
    {
        try
        {
            _store.ReleaseUnpublishedPages(pageIds, changeSet);
        }
        catch (Exception releaseFailure)
        {
            failure.Data[$"PS-11: {pageIds.Length} pages from {pageIds[0]} not released"] = releaseFailure.ToString();
        }
    }

    /// <summary>
    /// Initializes data pages [<paramref name="from"/>, end) of <paramref name="filePageIndices"/>: a full clear (except the root), the header, and
    /// each page's forward link to the next, the last one ending the chain.
    /// </summary>
    private unsafe void InitDataPages(PageBlockType type, Span<int> filePageIndices, int from, ChangeSet changeSet, long epoch)
    {
        // Use unchecked access: these are new pages about to be fully overwritten (cleared + header init).
        // In WAL mode, CRC verification may fail because the growth path doesn't write WAL/FPI records, so evicted pages would have stale CRCs with
        // no FPI available for repair.
        for (var i = from; i < filePageIndices.Length; i++)
        {
            var pageIndex = filePageIndices[i];
            GrowStepProbe?.Invoke(pageIndex);
            var memPageIdx = RequestExclusiveForGrow(pageIndex, epoch, false);
            var page = _store.GetPage(memPageIdx);

            // PS-14: every data page is cleared in full, header included. Its page number may be reused from a deleted segment, so the slot holds
            // that page's bytes (resident, or just read back from disk) and the clear on slot assignment never runs for it. The root (i == 0, a
            // Create only) is the exception: its raw data is the directory the directory pass has just written, after clearing it.
            InitHeader(
                page.Address,
                i == 0 ? PageClearMode.None : PageClearMode.WholePage,
                PageBlockFlags.IsLogicalSegment | (i == 0 ? PageBlockFlags.IsLogicalSegmentRoot : PageBlockFlags.None),
                type,
                1,
                MetadataReservedBytes(i == 0),
                ChunkStrideForGeometry);

            // Update link list of the pages that make the segment
            ref var lsh = ref page.StructAt<LogicalSegmentHeader>(LogicalSegmentHeader.Offset);
            lsh.LogicalSegmentNextRawDataPBID = ((i + 1) < filePageIndices.Length) ? filePageIndices[i + 1] : 0;
            // Persist the segment kind on the root page (self-describing for storage introspection, Module 15).
            if (i == 0)
            {
                lsh.Kind = _kind;
                // CK-05 (C2): the root is a directory page — give it a twin so a torn root write can't brick the segment.
                // Stamped only at Create (growFrom == 0 reaches i == 0); on Grow the root keeps its existing twin. A
                // transient store returns 0 ("no twin"); the occupancy root resolves to its pre-reserved twin (page 3).
                lsh.TwinPageIndex = _store.GetOrAllocateDirectoryTwin(filePageIndices[0], changeSet);
            }

            // Durability: see comment in the map-page-update block of CreateOrGrow — CP-04 race defence needs DC ≥ 2 BEFORE
            // the checkpoint snapshot. With ChangeSet, two tracked IncrementDirty calls (Add + RegisterReDirty) take DC to 2
            // and ReleaseDirtyMarks drains the excess via the same primitive as the checkpoint — no race.
            // Without ChangeSet, fall back to untracked EnsureDirtyAtLeast(2).
            if (changeSet != null)
            {
                changeSet.AddByMemPageIndex(memPageIdx);
                changeSet.RegisterReDirty(memPageIdx);
            }
            else
            {
                _store.MarkPageModified(memPageIdx);
            }

            _store.UnlatchPageExclusive(memPageIdx);
        }
    }

    /// <summary>
    /// Get a typed <see cref="PageAccessor"/> for a segment page with exclusive latch.
    /// Caller must be inside an <see cref="EpochGuard"/> scope.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PageAccessor GetPageExclusive(int segmentPageIndex, long epoch, out int memPageIndex)
    {
        memPageIndex = RequestExclusiveForGrow(Pages[segmentPageIndex], epoch, true);
        return _store.GetPage(memPageIndex);
    }

    /// <summary>
    /// Like <see cref="GetPageExclusive"/> but skips CRC verification.
    /// Used during segment growth where the page content will be immediately overwritten.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal PageAccessor GetPageExclusiveUnchecked(int segmentPageIndex, long epoch, out int memPageIndex)
    {
        memPageIndex = RequestExclusiveForGrow(Pages[segmentPageIndex], epoch, false);
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
        memPageIndex = RequestExclusiveForGrow(Pages[segmentPageIndex], epoch, true);
        return _store.GetMemPageAddress(memPageIndex);
    }

    public delegate bool PageMapWalkPredicate(int pageMapIndex, PageAccessor page, int memPageIndex);
    public delegate bool PageMapWalkPredicate<in T>(int pageMapIndex, PageAccessor page, int memPageIndex, T extra) where T : allows ref struct;

    public void WalkIndicesMap(PageMapWalkPredicate predicate, long epoch)
    {
        var curPageIndex = RootPageIndex;
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
        var curPageIndex = RootPageIndex;
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
    /// <param name="changeSet">Optional change set for tracking modifications.</param>
    /// <remarks>
    /// This method is thread-safe. Concurrent reads of existing pages remain valid during growth: the new pages are written past the published snapshot's
    /// count, where no reader looks, and a new snapshot is published once the grow has succeeded. The cost is the new pages plus the directory pages they
    /// land on, not the segment's size (#1205).
    /// </remarks>
    public void Grow(int newLength, ChangeSet changeSet = null)
    {
        lock (_growLock)
        {
            var current = _pageList;
            if (current == null)
            {
                throw new InvalidOperationException("Logical segment has not been initialized.");
            }
            if (newLength <= current.Count)
            {
                // Already at or above requested size (may have been grown by another thread)
                return;
            }

            var oldLen = current.Count;
            var items = current.Items;
            if (newLength > items.Length)
            {
                var grown = new int[(int)Math.Clamp(items.Length * 2L, newLength, int.MaxValue)];
                Array.Copy(items, grown, oldLen);
                items = grown;
            }

            var newPagesAsSpan = items.AsSpan(0, newLength);
            _store.AllocatePages(ref newPagesAsSpan, oldLen, changeSet);

            int noNextMap = 0;
            CreateOrGrow(PageBlockType.None, newPagesAsSpan, oldLen, ref noNextMap, changeSet, releaseNewPagesOnFailure: true, publishBacking: items);

            // Phase 5: Storage:Segment:Grow event. Use the first page id as a stable segment identifier.
            TyphonEvent.EmitStorageSegmentGrow(items[0], oldLen, newLength);
        }
    }

    internal LogicalSegment(TStore store)
    {
        _store = store;
    }

    public void Dispose() => _pageList = null;

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
        // Directory-only root (v4): the root holds no items, so item 0 is on page 1.
        var pi = Math.DivRem(itemIndex, PagedMMF.PageRawDataSize / itemSize, out var off);
        return (pi + 1, off);
    }

    internal bool Create(PageBlockType type, StorageSegmentKind kind, int filePageIndex, ChangeSet changeSet = null)
    {
        Span<int> ids = stackalloc int[1];
        ids[0] = filePageIndex;
        return Create(type, kind, ids, changeSet);
    }

    internal virtual bool Create(PageBlockType type, StorageSegmentKind kind, Span<int> filePageIndices, ChangeSet changeSet = null)
    {
        // Directory-only root (v4): the root page holds only the segment's page directory and carries no data, so every segment
        // needs at least one data page beyond it (a 1-page segment would have ChunkCountRootPage==0 → zero usable chunks).
        // The persistent allocators (AllocateSegment / AllocateChunkBasedSegment) clamp the page count to >= 2, but this is the
        // single choke point through which ALL creation funnels — transient component/index segments (ComponentTable), the
        // cluster segment (DatabaseEngine), genesis occupancy — so enforce the invariant here so any caller that under-allocates
        // (today, or a future StartingSize tuned to 1) fails loudly in Debug/test rather than silently producing a 0-chunk segment.
        Debug.Assert(filePageIndices.Length >= 2,
            $"A v4 directory-only-root segment requires at least 2 pages (directory root + >= 1 data page); got {filePageIndices.Length}.");

        // The kind is persisted into the root page's header by CreateOrGrow (read back by Load) — set it before so the root-page write captures it.
        _kind = kind;
        // Phase 5: Storage:Segment:Create event. First page id doubles as the segment identifier.
        if (filePageIndices.Length > 0)
        {
            TyphonEvent.EmitStorageSegmentCreate(filePageIndices[0], filePageIndices.Length);
        }
        int noNextMap = 0;
        return CreateOrGrow(type, filePageIndices, 0, ref noNextMap, changeSet);
    }

    // releaseNewPagesOnFailure: true when this call owns filePageIndices[growFrom..] and must give them back if it fails before publishing (a Grow);
    // false when the caller manages them: a Create's pages, or the occupancy grow's reserved page.
    // publishBacking: the array filePageIndices is a prefix of, published as the segment's page list without a copy (a Grow, whose array keeps room for
    // later grows); null to publish a copy (a Create, whose pages the caller owns, or the occupancy grow).
    internal unsafe bool CreateOrGrow(PageBlockType type, Span<int> filePageIndices, int growFrom, ref int nextMap, ChangeSet changeSet,
        bool releaseNewPagesOnFailure = false, int[] publishBacking = null)
    {
        var epoch = _store.EpochManager.GlobalEpoch;

        // Compute the number of indices map pages needed to store the indices (root + subsequent).
        // The end of the indices list is marked by a 0 value, we need to save space for this entry too, so the next line is accurate, if you wonder.
        var mapPageCount = 1 + ((filePageIndices.Length - RootHeaderIndexSectionCount + NextHeadersIndexSectionCount) / NextHeadersIndexSectionCount);

        // The first directory page a grow writes: the one holding entry growFrom, where the old terminator was. Every page before it keeps its entries and
        // its link, so a grow reads, pins and latches only [firstWrittenMap, mapPageCount) — O(pages added), not O(directory) (#1205). A Create writes
        // every one.
        var firstWrittenMap = MapPageOfEntry(growFrom);

        // Store the indices, code is complex because we may need multiple pages to store them all.
        // Reminder of how data is structured:
        // - Each page is 8192 bytes, with 192 bytes of header, and 8000 bytes of raw data.
        // - The first page is the root page, a directory only (v4): its raw data holds the first 2000 indices and no data.
        // - If the segment is bigger than 2000 pages, we allocate dedicated map-extension pages to store the remaining indices, 2000 per page.
        // - Subsequent data pages are storing data only, so 8000 bytes each.
        // In the headers, we maintain two linked lists:
        // 1. The logical segment next map page ID (LogicalSegmentNextMapPBID), which is used to traverse the indices pages.
        // 2. The logical segment next raw data page ID(LogicalSegmentNextRawDataPBID), which is used to traverse the data pages.
        // Both of these linked lists are terminated by 0.

        // Start by building and/or allocating the indices pages, considering the growFrom parameter. On the heap past a small count: a directory has a page
        // per 2 000 data pages, so a stack copy of it is a stack overflow on a segment of a few hundred GiB.
        Span<int> mapIndices = mapPageCount <= 256 ? stackalloc int[mapPageCount] : new int[mapPageCount];
        mapIndices[0] = filePageIndices[0];                             // The first page is always the root page, so we set it here.
        var mapIndexAllocStartFrom = 0;
        var allocatedMapFrom = mapPageCount;                            // mapIndices[allocatedMapFrom..] are the directory pages this call allocated
        var usesNextMap = false;

        // PS-11: a grow writes nothing the published segment can reach (the root, an existing map page, the old tail) until every step that can
        // fail has succeeded. Those steps initialize the new pages, which nothing references yet, then pin and latch every page the publish writes.
        // If one throws, the pages this grow allocated go back and the segment is exactly as it was. The publish then only re-enters latches it
        // already holds, on resident pages, so nothing in it waits or can fail. A Create has nothing published to protect: nothing references its
        // root until it returns.
        var writtenMapPages = mapPageCount - firstWrittenMap;
        Span<int> pinned = growFrom > 0 ? (writtenMapPages < 256 ? stackalloc int[writtenMapPages + 1] : new int[writtenMapPages + 1]) : default;
        var pinnedCount = 0;
        var latchedCount = 0;                                           // pinned[..latchedCount] are also latched
        var publishing = false;
        try
        {
            if (mapPageCount > 1)
            {
                // Need to rebuild the indices pages: a grow takes the existing ones from the in-memory list, rather than walking the chain on disk —
                // a read of every directory page, on every grow (#1205).
                if (growFrom > 0)
                {
                    _mapPages.AsSpan(0, _mapPageCount).CopyTo(mapIndices);
                    mapIndexAllocStartFrom = _mapPageCount;
                }

                // If a nextMap is provided, we need to use it as the first new map page
                var allocStartFrom = mapIndexAllocStartFrom;
                if ((nextMap != 0) && (allocStartFrom < mapIndices.Length))
                {
                    mapIndices[allocStartFrom++] = nextMap;
                    usesNextMap = true;                                 // The caller is told once nothing can fail any more, below
                }

                // Allocated the remaining indices pages using the allocator
                allocStartFrom = Math.Max(1, allocStartFrom);           // Ensure we start from the second page, as the first is always the root page
                if (allocStartFrom < mapIndices.Length)
                {
                    var pagesToAllocate = mapIndices[1..];
                    _store.AllocatePages(ref pagesToAllocate, allocStartFrom - 1, changeSet);
                    allocatedMapFrom = allocStartFrom;
                }
            }

            if (growFrom > 0)
            {
                // The directory pass below stamps a twin on each new map-extension page — those it fills and the one that holds only the terminator
                // when the entries end at a page's end (CK-05). Allocate them now, so the stamp only looks them up.
                var firstNewMapPage = Math.Max(1, mapIndexAllocStartFrom);
                for (var m = firstNewMapPage; m < mapPageCount; m++)
                {
                    _store.GetOrAllocateDirectoryTwin(mapIndices[m], changeSet);
                }

                InitDataPages(type, filePageIndices, growFrom, changeSet, epoch);

                // Count a pin only once it is taken: `pinned[pinnedCount++] = PinForPublish(…)` would count it before the call, and a throw would then
                // release a slot this grow never pinned. Only the directory pages the publish writes: those from the one holding entry growFrom on.
                for (var m = firstWrittenMap; m < mapPageCount; m++)
                {
                    var memPageIndex = PinForPublish(mapIndices[m], epoch, m < firstNewMapPage);
                    pinned[pinnedCount++] = memPageIndex;
                }
                var oldTailPin = PinForPublish(filePageIndices[growFrom - 1], epoch, true);
                pinned[pinnedCount++] = oldTailPin;

                // Take the publish's latches before its first write, so that another thread holding one of these pages, or a lock timeout, fails the
                // grow here, cleanly. Every page is resident and slot-pinned, so this waits for nothing the checkpoint has to do first (PS-09). The
                // directory pass and the old-tail patch below re-enter these latches.
                for (var m = firstWrittenMap; m <= mapPageCount; m++)
                {
                    var filePageIndex = m < mapPageCount ? mapIndices[m] : filePageIndices[growFrom - 1];
                    GrowStepProbe?.Invoke(filePageIndex);
                    var latchedMem = RequestExclusiveForGrow(filePageIndex, epoch, false);
                    Debug.Assert(latchedMem == pinned[latchedCount], "a slot-pinned page cannot have moved");
                    latchedCount++;
                }
            }

            publishing = true;
            if (usesNextMap)
            {
                nextMap = 0;                                            // Signal the caller that we used the given nextMap
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

                // If it's a new page, initialize it (skip CRC — page will be fully overwritten). WholePage, not Header (PS-14): the directory below
                // fills only the entries in range plus a terminator, and the page may be a reused page number still holding a deleted page's bytes.
                if (isNewPage)
                {
                    memPageIdx = RequestExclusiveForGrow(curMapPageIndex, epoch, false);
                    page = _store.GetPage(memPageIdx);
                    hasPage = true;

                    InitHeader(page.Address, PageClearMode.WholePage,
                        PageBlockFlags.IsLogicalSegment | (isFirstPage ? PageBlockFlags.IsLogicalSegmentRoot : PageBlockFlags.None),
                        type, 1, MetadataReservedBytes(isFirstPage), ChunkStrideForGeometry);
                    isPageDirty = true;
                }

                // Update the indices map linked list, starting the index map before the first to allocate
                if (isNewPage || isLastAllocated)
                {
                    if (hasPage == false)
                    {
                        memPageIdx = RequestExclusiveForGrow(curMapPageIndex, epoch, true);
                        page = _store.GetPage(memPageIdx);
                        hasPage = true;
                    }
                    ref var lsh = ref page.StructAt<LogicalSegmentHeader>(LogicalSegmentHeader.Offset);
                    lsh.LogicalSegmentNextMapPBID = ((curIndexMapIndex + 1) < mapIndices.Length) ? mapIndices[curIndexMapIndex + 1] : 0;
                    // CK-05 (C2): a NEW map-extension directory page gets a twin (a second physical slot it alternates between)
                    // so a torn write can never corrupt the segment's page directory. The root's twin is stamped in the
                    // data-page loop below; here we cover only the extension pages (curIndexMapIndex > 0). Idempotent on a store
                    // that returns 0 (transient — not protected).
                    if (isNewPage && (curIndexMapIndex > 0))
                    {
                        lsh.TwinPageIndex = _store.GetOrAllocateDirectoryTwin(curMapPageIndex, changeSet);
                    }
                    isPageDirty = true;
                }

                // In the current map, set the page indices it contains
                if ((curStartPageIndex >= mapIndexBaseOffset) && (curStartPageIndex < (mapIndexBaseOffset + curIndicesCount)))
                {
                    if (hasPage == false)
                    {
                        memPageIdx = RequestExclusiveForGrow(curMapPageIndex, epoch, true);
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
                            var endMemIdx = RequestExclusiveForGrow(mapIndices[curIndexMapIndex + 1], epoch, false);
                            var endPage = _store.GetPage(endMemIdx);
                            // WholePage (PS-14): this page receives a single int, so everything else on it must not carry a previous occupant's bytes.
                            InitHeader(endPage.Address, PageClearMode.WholePage, PageBlockFlags.IsLogicalSegment, type, 1, MetadataReservedBytes(false),
                                ChunkStrideForGeometry);
                            // A directory page like any other, so it has a twin (CK-05). It had none: the next grow fills it, and the reopen walk, which
                            // stopped at a page without a twin, then read every later directory page from its primary slot — a stale one half the time.
                            endPage.StructAt<LogicalSegmentHeader>(LogicalSegmentHeader.Offset).TwinPageIndex =
                                _store.GetOrAllocateDirectoryTwin(mapIndices[curIndexMapIndex + 1], changeSet);
                            changeSet?.AddByMemPageIndex(endMemIdx);
                            endPage.RawData<int>(0, 1)[0] = 0;
                            // Durability: AddByMemPageIndex already bumps DC to 1 via tracked IncrementDirty. Without a
                            // ChangeSet, fall back to untracked EnsureDirtyAtLeast(1) — same DC outcome, just untracked.
                            if (changeSet == null)
                            {
                                _store.MarkPageModified(endMemIdx);
                            }

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
                    // Durability: directory map-page write (root or extension) must survive a checkpoint regardless of
                    // whether the caller provided a ChangeSet. CP-04 race defence needs DC ≥ 2 BEFORE the checkpoint
                    // snapshot fires, so even one DecrementDirty leaves DC ≥ 1 and the page stays dirty for the next
                    // cycle. With a ChangeSet, two tracked IncrementDirty calls (Add + RegisterReDirty) take DC to 2 —
                    // ReleaseDirtyMarks then drains the excess via the same primitive the checkpoint uses, no
                    // race (issue #385). Without a ChangeSet, fall back to untracked EnsureDirtyAtLeast(2).
                    if (changeSet != null)
                    {
                        changeSet.AddByMemPageIndex(memPageIdx);
                        changeSet.RegisterReDirty(memPageIdx);
                    }
                    else
                    {
                        _store.MarkPageModified(memPageIdx);
                    }
                }

                if (hasPage)
                {
                    _store.UnlatchPageExclusive(memPageIdx);
                }

                isFirstPage = false;
                curIndexMapIndex++;
            }

            // Patch the OLD tail's forward-chain pointer when growing. The data-page header chain (LogicalSegmentNextRawDataPBID) is the segment's
            // structural-integrity invariant — chain count must equal the directory count at every healthy moment. Before this fix the inner
            // data-page-init loop only touched pages [growFrom, filePageIndices.Length), so the OLD tail (page at growFrom-1) was left with its prior 0
            // terminator and the chain was permanently truncated at the original allocation size. With it, every Grow extends the chain by exactly the
            // newly-added pages.
            if (growFrom > 0)
            {
                var oldTailFilePage = filePageIndices[growFrom - 1];
                var oldTailMemIdx = RequestExclusiveForGrow(oldTailFilePage, epoch, true);
                var oldTailPage = _store.GetPage(oldTailMemIdx);
                ref var oldTailLsh = ref oldTailPage.StructAt<LogicalSegmentHeader>(LogicalSegmentHeader.Offset);
                oldTailLsh.LogicalSegmentNextRawDataPBID = filePageIndices[growFrom];
                // Durability: AddByMemPageIndex already bumps DC to 1 via tracked IncrementDirty. Without a ChangeSet, fall
                // back to untracked EnsureDirtyAtLeast(1) for the same DC outcome — see the comment block in InitDataPages
                // for the full CP-04 rationale.
                if (changeSet != null)
                {
                    changeSet.AddByMemPageIndex(oldTailMemIdx);
                }
                else
                {
                    _store.MarkPageModified(oldTailMemIdx);
                }
                _store.UnlatchPageExclusive(oldTailMemIdx);
            }
            else
            {
                // A Create's root is both its directory and its data page 0, and the directory pass above re-stamped the root's header, so the
                // data-page fields go on after it.
                InitDataPages(type, filePageIndices, 0, changeSet, epoch);
            }
        }
        catch (Exception failure) when (!publishing)
        {
            if (allocatedMapFrom < mapPageCount)
            {
                ReleaseUnpublished(mapIndices[allocatedMapFrom..], changeSet, failure);
            }
            if (releaseNewPagesOnFailure)
            {
                ReleaseUnpublished(filePageIndices[growFrom..], changeSet, failure);
            }
            throw;
        }
        finally
        {
            // The latches first, then the slot references that kept those slots: pinned[i] is the slot latched i-th.
            for (var i = latchedCount - 1; i >= 0; i--)
            {
                _store.UnlatchPageExclusive(pinned[i]);
            }
            for (var i = 0; i < pinnedCount; i++)
            {
                _store.DecrementSlotRefCount(pinned[i]);
            }
        }

        // The in-memory directory list follows the one on disk: the pages it already had, then the ones this call added. Before the page list is
        // published: an allocation that throws here leaves both as they were, where a short directory list under a longer page list would make the next
        // grow treat an existing directory page as new.
        if (_mapPages == null || _mapPages.Length < mapPageCount)
        {
            var grownMap = new int[Math.Max(mapPageCount, (_mapPages?.Length ?? 0) * 2)];
            _mapPages?.AsSpan(0, _mapPageCount).CopyTo(grownMap);
            _mapPages = grownMap;
        }

        var firstNewMapIndex = growFrom > 0 ? _mapPageCount : 0;
        mapIndices[firstNewMapIndex..mapPageCount].CopyTo(_mapPages.AsSpan(firstNewMapIndex));

        var newCount = filePageIndices.Length;
        var published = new PageList(publishBacking ?? filePageIndices.ToArray(), newCount);
        _mapPageCount = mapPageCount;
        _pageList = published;

        // Post-condition #1: verify the data-page forward chain over the pages this call actually wrote — the root, the old tail at growFrom-1, and every new
        // page. Mismatch here ⇒ bug in CreateOrGrow's pointer writes (not persistence).
        //
        // The range is bounded on purpose (#838), and each page is read and released (EP-02), so a grow leaves nothing pinned. Read through
        // RequestPageEpoch, a page stays unevictable until the CALLER's epoch scope exits — a transaction's, for its whole life — and the write loops above
        // end pinning nothing (UnlatchPageExclusive resets AccessEpoch), so this check was the one thing a grow left pinned: the whole segment while it walked
        // the chain (a commit waiting for eviction of pages its own pin protected, #838), then one page per page grown. Segments grow by doubling, so that was
        // still the size of the segment, up to 1 024 pages: a 128-page cluster segment growing in a 256-page cache stalled its commit with 128 pages pinned.
        var pageList = Pages;
        var badLink = VerifyGrownChainLinks(pageList, growFrom, out var actualNext);
        if (badLink >= 0)
        {
            throw new InvalidOperationException(
                $"CreateOrGrow IN-MEMORY chain mismatch: root={pageList[0]} kind={_kind} growFrom={growFrom} " +
                $"page[{badLink}]={pageList[badLink]} points at {actualNext}, expected {((badLink + 1) < pageList.Length ? pageList[badLink + 1] : 0)} " +
                $"— bug is in CreateOrGrow's pointer writes, not persistence.");
        }

        // There is deliberately NO strict-mode escape hatch that re-runs the exhaustive walk here. CheckConfig.Enabled is the switch a user flips to diagnose
        // a stall, and this walk is what produces the stall — arming it from the diagnostic gate would deadlock the very investigation it is meant to serve
        // (this suite runs strict mode on for every fixture, so that is not hypothetical). What the narrowing does give up is stated in EP-01: prefix damage
        // this method did NOT cause — a lost write leaving a stale pointer below growFrom-1, or a cycle in the prefix — is no longer noticed at the next grow.
        // It is still caught, by the exhaustive chain↔directory cross-check on every reopen (Load) and on demand over every segment (RunStorageIntegrityCheck
        // / `typhon check`), just later. That is the right trade: a post-condition's job is to prove ITS OWN writes correct, the old check mis-attributed
        // prefix damage to CreateOrGrow anyway ("not persistence"), and the bounded check is strictly more thorough than the count comparison over the range
        // this method can actually corrupt.

        // Post-condition #2: read the directory pages this call wrote RIGHT NOW and verify each entry matches the in-memory page list position-by-position.
        // Mismatch here ⇒ bug in CreateOrGrow's directory writes (not persistence). Positional verification is store-agnostic — works for both
        // PersistentStore (where page index 0 is reserved by the MMF bootstrap) and TransientStore (where page index 0 is a valid entry). Bounded like
        // post-condition #1, and for the same reason: the pages before the one holding entry growFrom were not written, and reading them all made every grow
        // O(segment) (#1205). The full comparison stays available to the integrity check (VerifyDirectoryAgainst).
        var memDirCount = VerifyDirectoryFrom(_mapPages.AsSpan(0, _mapPageCount), firstWrittenMap, pageList);
        if (memDirCount != pageList.Length)
        {
            throw new InvalidOperationException(
                $"CreateOrGrow IN-MEMORY directory mismatch: root={pageList[0]} kind={_kind} growFrom={growFrom} " +
                $"expected={pageList.Length} directory={memDirCount} (diff={memDirCount - pageList.Length:+0;-#}) " +
                $"— bug is in CreateOrGrow's directory writes, not persistence.");
        }

        return true;
    }

    /// <summary>The directory page (0 = the root) holding entry <paramref name="entry"/> of the page list — or, at the list's length, its terminator.</summary>
    private static int MapPageOfEntry(int entry)
        => entry < RootHeaderIndexSectionCount ? 0 : 1 + ((entry - RootHeaderIndexSectionCount) / NextHeadersIndexSectionCount);

    /// <summary>The first entry directory page <paramref name="mapPage"/> holds.</summary>
    private static int FirstEntryOfMapPage(int mapPage)
        => mapPage == 0 ? 0 : RootHeaderIndexSectionCount + ((mapPage - 1) * NextHeadersIndexSectionCount);

    /// <summary>
    /// <see cref="VerifyDirectoryAgainst"/> over the directory pages from <paramref name="firstMapPage"/> on: reads each (EP-02), compares its entries with
    /// <paramref name="expected"/> from the page's first entry, and checks each page links to the next while entries remain. Returns the entry index at
    /// which the comparison stopped — <c>expected.Length</c> when every entry from the first page's matched.
    /// </summary>
    private int VerifyDirectoryFrom(ReadOnlySpan<int> mapPages, int firstMapPage, ReadOnlySpan<int> expected)
    {
        var entry = FirstEntryOfMapPage(firstMapPage);
        for (var m = firstMapPage; m < mapPages.Length && entry < expected.Length; m++)
        {
            var count = m == 0 ? RootHeaderIndexSectionCount : NextHeadersIndexSectionCount;
            _store.AcquirePageForRead(mapPages[m], out var memPageIndex);
            try
            {
                var page = _store.GetPage(memPageIndex);
                var rd = page.RawDataReadOnly<int>(0, count);
                for (var i = 0; i < count && entry < expected.Length; i++, entry++)
                {
                    if (rd[i] != expected[entry])
                    {
                        return entry;
                    }
                }

                if (entry < expected.Length
                    && (m + 1 >= mapPages.Length
                        || page.StructAt<LogicalSegmentHeader>(LogicalSegmentHeader.Offset).LogicalSegmentNextMapPBID != mapPages[m + 1]))
                {
                    return entry;
                }
            }
            finally
            {
                _store.ReleasePageForRead(memPageIndex);
            }
        }

        return entry;
    }

    /// <summary>
    /// Verifies the data-page forward chain (<see cref="LogicalSegmentHeader.LogicalSegmentNextRawDataPBID"/>) over <b>only the pages
    /// <see cref="CreateOrGrow"/> just wrote</b> — the root, the old tail at <paramref name="growFrom"/><c>-1</c>, and every newly added page. Compares
    /// positionally: page <c>i</c>'s forward pointer must be <c>_pages[i+1]</c>, and <c>0</c> at the last page. Returns <c>-1</c> when every link matches,
    /// else the index into <paramref name="pages"/> whose pointer is wrong, with <paramref name="actualNext"/> set to what that page actually held.
    /// </summary>
    /// <param name="pages">
    /// The segment's page directory. Passed in rather than re-read from <c>_pages</c> so the index this returns and the array the caller reports it against
    /// are provably the same object — two independent reads of a volatile field are two chances to disagree.
    /// </param>
    /// <param name="growFrom">The first index this call wrote; <c>0</c> for <c>Create</c>.</param>
    /// <param name="actualNext">On a mismatch, the forward pointer the offending page actually held.</param>
    /// <remarks>
    /// <para>
    /// The bound is the point (#838). <see cref="WalkForwardChainPageCount"/> walks from the root and fetches every data page; each fetch raises that page's
    /// <c>AccessEpoch</c> by CAS-max, and the only thing that lowers it is <c>UnlatchPageExclusive</c>, which resets it to 0 (PS-03). A page that is merely
    /// READ therefore stays unevictable under PS-01 until the enclosing epoch scope exits — and that scope belongs to the caller (a transaction), not to the
    /// walk, which turned a post-condition on a small grow into an O(segment) pin held for a whole transaction.
    /// </para>
    /// <para>
    /// Each page is read and released (EP-02), so the check pins nothing past its own read. It used to tag each page with the caller's epoch, which made it
    /// the one thing a grow left pinned — one page per page grown, for the caller's scope, and since segments grow by doubling, as many pages as the
    /// segment held. Bounding the range is what EP-01 requires; reading and releasing is what makes the bound cost nothing.
    /// </para>
    /// <para>
    /// Coverage over the range is strictly stronger than the count comparison it replaces: positional comparison also catches "right count, wrong target",
    /// for the same reason <see cref="VerifyDirectoryAgainst"/> is positional. What it gives up is prefix damage <see cref="CreateOrGrow"/> did not cause,
    /// which the crash path's allocator scan still catches after an unclean close, and the exhaustive walk on demand (<c>RunStorageIntegrityCheck</c>).
    /// </para>
    /// <para>
    /// <b>Index 0 is always verified</b>, even when it falls outside <c>[growFrom-1, end]</c>. A grow that pushes the page directory past
    /// <see cref="RootHeaderIndexSectionCount"/> allocates a map-extension page and rewrites the ROOT page's <see cref="LogicalSegmentHeader"/> to chain to
    /// it — and <see cref="LogicalSegmentHeader.LogicalSegmentNextMapPBID"/> sits directly beside
    /// <see cref="LogicalSegmentHeader.LogicalSegmentNextRawDataPBID"/> in that struct, so a wrong-field write there is precisely the bug class this
    /// post-condition exists to catch. It costs no extra fetch in practice: <see cref="VerifyDirectoryAgainst"/> faults the root immediately afterwards.
    /// </para>
    /// </remarks>
    private int VerifyGrownChainLinks(ReadOnlySpan<int> pages, int growFrom, out int actualNext)
    {
        actualNext = 0;

        if (pages.Length == 0)
        {
            return -1;
        }

        // A start beyond the array would make the loop body vanish and the check report "clean" without having verified anything — the one failure mode a
        // detector must not have. Unreachable today (Grow rejects newLength <= current, GrowOccupancySegment passes length-1 for a length-page array).
        Debug.Assert(growFrom <= pages.Length,
            $"CreateOrGrow growFrom={growFrom} exceeds the {pages.Length}-page directory; the chain check would be vacuous.");

        var start = Math.Max(0, growFrom - 1);
        if ((start > 0) && !ChainLinkMatches(pages, 0, out actualNext))
        {
            return 0;
        }

        for (var i = start; i < pages.Length; i++)
        {
            if (!ChainLinkMatches(pages, i, out actualNext))
            {
                return i;
            }
        }

        actualNext = 0;
        return -1;
    }

    /// <summary>
    /// Reads page <paramref name="index"/>'s forward-chain pointer and reports whether it names the next page in <paramref name="pages"/> (or <c>0</c> at the
    /// tail). <paramref name="actualNext"/> always receives what the page actually held, so the caller can put both values in its message. Reads and
    /// releases the page (EP-02).
    /// </summary>
    private bool ChainLinkMatches(ReadOnlySpan<int> pages, int index, out int actualNext)
    {
        actualNext = ReadNextRawDataPage(pages[index]);
        return actualNext == (((index + 1) < pages.Length) ? pages[index + 1] : 0);
    }

    /// <summary>
    /// Walks the in-memory directory section (root + extension map pages reached via <c>LogicalSegmentNextMapPBID</c>) and
    /// verifies it matches <paramref name="expected"/> position-by-position. Returns the number of entries that matched in
    /// order before either: (a) the expected list ran out (full match — caller asserts equal to <c>expected.Length</c>),
    /// (b) the persisted directory entry diverged from <paramref name="expected"/>, or (c) the map-page chain was truncated
    /// before reaching <c>expected.Length</c> entries. Used by the post-condition assertion at the end of
    /// <see cref="CreateOrGrow"/> to isolate "CreateOrGrow logic bug" from "persistence bug" (#385).
    /// </summary>
    /// <remarks>
    /// Replaces the original zero-terminator walker — that one assumed page index 0 could never appear as a real directory
    /// entry, an assumption that holds for <see cref="PersistentStore"/> (MMF reserve carves out page 0 for the bootstrap)
    /// but NOT for <see cref="TransientStore"/> (in-memory allocator can hand out page 0 as the first segment page).
    /// Positional comparison is store-agnostic AND strictly more thorough — it catches not just count mismatches but also
    /// "right count, wrong content" bugs that the original walker silently passed. Reads and releases each directory page (EP-02): the integrity
    /// check runs it over every segment.
    /// </remarks>
    internal int VerifyDirectoryAgainst(ReadOnlySpan<int> expected)
    {
        if (expected.Length == 0)
        {
            return 0;
        }

        _store.AcquirePageForRead(RootPageIndex, out var memPageIndex);
        try
        {
            var page = _store.GetPage(memPageIndex);
            var matched = 0;
            var rd = page.RawDataReadOnly<int>(0, RootHeaderIndexSectionCount);
            var maxIndicesForPage = RootHeaderIndexSectionCount;
            var i = 0;
            while (matched < expected.Length)
            {
                if (i == maxIndicesForPage)
                {
                    var nextMap = page.StructAt<LogicalSegmentHeader>(LogicalSegmentHeader.Offset).LogicalSegmentNextMapPBID;
                    if (nextMap == 0)
                    {
                        // Map-page chain truncated before the expected entry count — caller's assertion will fire.
                        return matched;
                    }

                    _store.ReleasePageForRead(memPageIndex);
                    memPageIndex = -1;
                    // Into a fresh local: AcquirePageForRead assigns its out parameter before it can throw, and a throw after that holds no pin — writing
                    // memPageIndex directly would have the finally release a pin this method never took.
                    _store.AcquirePageForRead(nextMap, out var nextMemPageIndex);
                    memPageIndex = nextMemPageIndex;
                    page = _store.GetPage(memPageIndex);
                    rd = page.RawDataReadOnly<int>(0, NextHeadersIndexSectionCount);
                    i = 0;
                    maxIndicesForPage = NextHeadersIndexSectionCount;
                }

                if (rd[i] != expected[matched])
                {
                    // Persisted directory entry diverged from in-memory page list — caller's assertion will fire with the
                    // diff. Stop here so we return the count of consecutive matching entries (useful for diagnosis).
                    return matched;
                }
                matched++;
                i++;
            }
            return matched;
        }
        finally
        {
            if (memPageIndex >= 0)
            {
                _store.ReleasePageForRead(memPageIndex);
            }
        }
    }

    /// <summary>
    /// Initialize page header directly from a raw pointer (epoch-based path).
    /// </summary>
    /// <param name="pageAddr">Address of the page image.</param>
    /// <param name="clearMode">How much of the page to zero before stamping.</param>
    /// <param name="flags">Role flags for the page.</param>
    /// <param name="type">Block type for the page.</param>
    /// <param name="formatRevision">Type-scoped format revision.</param>
    /// <param name="reservedMetadataBytes">
    /// Bytes of the page metadata region the owning segment will claim for its chunk-occupancy bitmap. Decides how many
    /// per-sector verification slots fit in what is left (<see cref="PageSectorFooter"/>). Zero for segments with no chunk
    /// bitmap, which is the common case and yields the finest granularity.
    /// </param>
    /// <param name="chunkStride">
    /// Chunk stride recorded on the page so an offline reader can locate chunk <i>n</i> without the schema assembly
    /// (<see cref="SegmentGeometry"/>). Zero for segments that hold no chunks.
    /// </param>
    internal static unsafe void InitHeader(byte* pageAddr, PageClearMode clearMode, PageBlockFlags flags, PageBlockType type, short formatRevision,
        int reservedMetadataBytes = 0, int chunkStride = 0)
    {
        ref var header = ref Unsafe.AsRef<PageBaseHeader>(pageAddr + PageBaseHeader.Offset);

        if (clearMode == PageClearMode.WholePage)
        {
            // Two counters survive the clear.
            // - ModificationCounter: the seqlock counter managed by TryLatchPageExclusive/UnlatchPageExclusive. Zeroing it while the page is latched
            //   leaves the counter odd after unlatch, causing CopyPageWithSeqlock to spin forever.
            // - ChangeRevision: PageSectorFooter stamps every sector with its low 16 bits. A page number reused from a deleted segment arrives with
            //   its old revision (resident, or read back from disk); keeping it means a file page's revision never goes backwards. It does not
            //   make the next stamp differ from every stale sector: a checkpoint bumps only its staging copy (CP-08), so the live revision can
            //   trail the one on disk.
            var savedModCounter = header.ModificationCounter;
            var savedChangeRevision = header.ChangeRevision;
            new Span<byte>(pageAddr, PagedMMF.PageSize).Clear();
            header.ModificationCounter = savedModCounter;
            header.ChangeRevision = savedChangeRevision;
        }

        header.Flags = flags;
        header.Type = type;
        header.FormatRevision = formatRevision;

        // Declare the page's per-sector verification geometry AFTER any clear, since the clear would wipe it. A chunk-based
        // segment re-declares with its real bitmap size once it knows the stride; everything else keeps the finest
        // granularity, which is correct because a page with no chunk bitmap has the whole metadata region free.
        PageSectorFooter.DeclareGeometry(new Span<byte>(pageAddr, PagedMMF.PageSize), reservedMetadataBytes);

        // Record the chunk arithmetic on the page itself, for the same reason and at the same moment: after any clear,
        // before the page can be written out. Without it an offline reader can classify a page but not locate a chunk
        // inside it, which is what blocks every cross-structure check.
        SegmentGeometry.WriteStride(new Span<byte>(pageAddr, PagedMMF.PageSize), chunkStride);
    }

    internal virtual bool Load(int filePageIndex)
    {
        var epoch = _store.EpochManager.GlobalEpoch;
        _store.RequestPageEpoch(filePageIndex, epoch, out var memPageIndex);
        var page = _store.GetPage(memPageIndex);

        // Restore the persisted segment kind from the root page before `page` is reassigned to traverse map pages.
        _kind = page.StructAt<LogicalSegmentHeader>(LogicalSegmentHeader.Offset).Kind;

        var pages = new List<int>();
        var mapPages = new List<int> { filePageIndex };
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

            mapPages.Add(lsh.LogicalSegmentNextMapPBID);
            _store.RequestPageEpoch(lsh.LogicalSegmentNextMapPBID, epoch, out memPageIndex);
            page = _store.GetPage(memPageIndex);
            rd = page.RawDataReadOnly<int>(0, NextHeadersIndexSectionCount);
            i = 0; // Reset index for the new page

            maxIndicesForPage = NextHeadersIndexSectionCount;
        }

        // Every directory lists at least the root itself, so an empty one is a root page that was never written — what a checkpoint interrupted before the
        // root landed leaves behind its SPI. The chain walk that used to run below caught it by accident (its first step, RootPageIndex, threw); without a
        // throw the segment would load as zero pages and fail far from the cause, and the crash path could not replace it (TryLoadChunkBasedSegment, RB-01).
        if (pages.Count == 0)
        {
            throw new InvalidOperationException(
                $"LogicalSegment load failed: root={filePageIndex} kind={_kind} — the root page lists no page, not even itself: it was never written.");
        }

        _pageList = new PageList(pages.ToArray(), pages.Count);

        // The directory pages a grow appends after: as many as CreateOrGrow sizes a directory of this length, read off the chain just walked. A page that
        // holds only the terminator (the previous page exactly full) is one of them; the walk above reached it through the full page's link.
        var expectedMapPages = MapPageOfEntry(pages.Count) + 1;
        if (mapPages.Count != expectedMapPages)
        {
            throw new InvalidOperationException(
                $"LogicalSegment load failed: root={filePageIndex} kind={_kind} — a directory of {pages.Count} pages spans {expectedMapPages} directory "
                + $"pages, but its chain links {mapPages.Count}.");
        }

        _mapPages = mapPages.ToArray();
        _mapPageCount = mapPages.Count;

        // The directory only: a page per 2 000 entries. The cross-check of the directory against the data pages' forward chain (a lost-write detector, #382)
        // is NOT made here. It reads every data page, and it used to, on every open, under the open's one epoch: O(database) reads, every page pinned until the
        // open ended, so a database larger than its page cache could not be opened (#1143). On the crash path a chunk-based segment checks its links during
        // the free-chunk scan it makes then (ChunkBasedSegment.ScanForAllocatorState). Otherwise the check is the offline scanner's, at Quick depth and deeper
        // — not at the default Spine depth — and a mismatch found by open-time verification refuses the open unless recovery will rebuild the segment
        // (DatabaseIntegrityException.RefusesOpen).

        // Phase 5: Storage:Segment:Load event.
        TyphonEvent.EmitStorageSegmentLoad(filePageIndex, pages.Count);

        return true;
    }

    /// <summary>
    /// Walks the segment's data-page forward chain — start at the root, follow each page's <see cref="LogicalSegmentHeader.LogicalSegmentNextRawDataPBID"/>
    /// pointer until it reaches <c>0</c>, counting pages along the way.
    /// </summary>
    /// <remarks>
    /// Pure integrity-check helper — read-only, no allocations. No longer called by <see cref="Load"/> (#1143): tests and storage introspection use it
    /// to cross-check the chain against the directory. Each page is read and released before the next (EP-02), so the walk holds one page of the cache
    /// however long the chain is; it used to tag every page with the caller's epoch and pin the whole segment for the caller's scope (#1144).
    /// </remarks>
    internal int WalkForwardChainPageCount()
    {
        var count = 1;
        var next = ReadNextRawDataPage(RootPageIndex);
        // Cycle guard: any healthy chain is bounded by the directory's page count. A runaway chain (cycle or wildly past the directory's length) is itself a
        // corruption signal — the caller's mismatch detection will flag it against the directory count.
        var maxWalk = (Length * 2) + 16;
        while (count < maxWalk)
        {
            if (next == 0)
            {
                return count;
            }

            next = ReadNextRawDataPage(next);
            count++;
        }
        return count;
    }

    /// <summary>The forward-chain pointer of file page <paramref name="filePageIndex"/>, read and released (EP-02).</summary>
    private int ReadNextRawDataPage(int filePageIndex)
    {
        _store.AcquirePageForRead(filePageIndex, out var memPageIndex);
        try
        {
            return _store.GetPage(memPageIndex).StructAt<LogicalSegmentHeader>(LogicalSegmentHeader.Offset).LogicalSegmentNextRawDataPBID;
        }
        finally
        {
            _store.ReleasePageForRead(memPageIndex);
        }
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
        // A segment that is registered but never materialised — a component the application declared and has not yet written a single row to — owns no root
        // page, and therefore owns no directory-map extension pages either. "None" is the correct answer to give, so give it instead of throwing out of a walk
        // over every segment. Reading RootPageIndex here used to throw, which took down the crash-path occupancy re-derive
        // (BuildOwnedPageBitmap ← RederiveOccupancyOnCrash) for ANY database holding an empty component — which is nearly every real schema.
        var length = Length;
        if (length == 0)
        {
            return;
        }

        _store.RequestPageEpoch(RootPageIndex, epoch, out var memPageIndex);
        var page = _store.GetPage(memPageIndex);
        var maxWalk = length / NextHeadersIndexSectionCount + 4; // cycle guard
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
