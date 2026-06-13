using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// On-disk header stored in the metadata zone (bytes 64–191) of page 0 of every Typhon database file.
/// Identifies the file format, tracks the database name and format version,
/// and holds the root page indices (SPIs) of the core system segments.
/// </summary>
/// <remarks>
/// Page 0 has a standard <see cref="PageBaseHeader"/> at bytes 0–63 (managed by the infrastructure
/// for seqlock, checksum, and change tracking). The <see cref="RootFileHeader"/> is placed immediately
/// after, at offset <see cref="PagedMMF.PageBaseHeaderSize"/> (64), using
/// <c>page.StructAt&lt;RootFileHeader&gt;(PagedMMF.PageBaseHeaderSize)</c>.
/// </remarks>
/// <summary>
/// Minimal identity header at the start of page 0. Only fields needed for file validation
/// before the <see cref="BootstrapDictionary"/> can be loaded.
/// All dynamic metadata (SPIs, counters, config) lives in the bootstrap stream that follows.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
unsafe internal struct RootFileHeader
{
    /// <summary>UTF-8 magic string (<c>"TyphonDatabase"</c>) used to identify and validate the file format.</summary>
    public fixed byte HeaderSignature[32];

    /// <summary>On-disk format version. Incremented on breaking layout changes to detect incompatible files.</summary>
    public int DatabaseFormatRevision;

    /// <summary>Chunk size (in bytes) used when growing the underlying database files.</summary>
    public ulong DatabaseFilesChunkSize;

    /// <summary>UTF-8 database name (max 64 bytes). Verified on load to prevent opening the wrong file.</summary>
    public fixed byte DatabaseName[64];

    /// <summary>Returns <see cref="HeaderSignature"/> decoded as a managed string.</summary>
    public string HeaderSignatureString
    {
        get
        {
            fixed (byte* s = HeaderSignature)
            {
                return StringExtensions.LoadString(s);
            }
        }
    }

    /// <summary>Returns <see cref="DatabaseName"/> decoded as a managed string.</summary>
    public string DatabaseNameString
    {
        get
        {
            fixed (byte* s = DatabaseName)
            {
                return StringExtensions.LoadString(s);
            }
        }
    }
}

[PublicAPI]
public class ManagedPagedMMFOptions : PagedMMFOptions
{
}

// ============================================================================================================================================================
// Pages of an empty file (v2 layout — CK-05)
// ------------------------------------------------------------------------------------------------------------------------------------------------------------
// 0: Meta pair slot A (root file header + bootstrap dictionary; A/B alternation — the current valid content is in slot A or B)
// 1: Meta pair slot B
// 2: Occupancy segment root page
// 3: Reserved page for occupancy map growth
// 4: Reserved page for occupancy map next map data (in case we need more than 500 pages to store the occupancy map)
// ============================================================================================================================================================

/// <summary>
/// Memory-mapped file manager with page allocation, segment management, and occupancy tracking.
/// </summary>
/// <remarks>
/// <para>
/// ManagedPagedMMF registers itself under the <see cref="ResourceSubsystem.Storage"/> subsystem
/// in the resource tree. It is typically the storage backend for a <see cref="DatabaseEngine"/>.
/// </para>
/// </remarks>
[PublicAPI]
public partial class ManagedPagedMMF : PagedMMF, IMetricSource, IDebugPropertiesProvider
{
    #region Constants

    // v4 layout (CK-05 + directory-only root): pages 0–1 are the meta pair (A/B slots); the occupancy segment's directory is
    // itself a protected pair, so its root and map-extension twins are pre-reserved at fixed pages to break the genesis
    // chicken-and-egg (twin allocation routes through the occupancy map). With the directory-only root the occupancy root
    // holds NO bitmap data, so its first data page (the L0 words) is also pre-reserved. Layout: 0,1 meta · 2 occ-root
    // (directory only) · 3 occ-root-twin · 4 occ-first-data (L0 bits) · 5 occ-data-reserve · 6 occ-mapext-reserve ·
    // 7 occ-mapext-twin-reserve.
    internal const int InitialReservedPageCount = 8;
    private const int OccupancySegmentRootPageIndex = 2;
    private const int OccupancyRootTwinPageIndex = 3;
    // The occupancy bitmap's first data page. The directory-only root (v4) carries no bitmap words, so the first L0 page must
    // be a distinct, pre-reserved page — the occupancy segment is genesis-created spanning [root=2, firstData=4].
    private const int OccupancyFirstDataPageIndex = 4;
    internal const string HeaderSignature = "TyphonDatabase";

    // The two physical slots of the meta pair (CK-05). The current valid content alternates between them.
    private const int MetaSlotA = 0;
    private const int MetaSlotB = 1;

    // Bootstrap dictionary keys (storage layer)
    // ReSharper disable InconsistentNaming
    internal const string BK_OccupancyMapSPI = "OccupancyMapSPI";
    internal const string BK_OccupancyReserved = "OccupancyReserved";
    // {CheckpointLSN: long, CleanShutdown: bool} packed as Int3 — flips atomically with the meta generation (M12, CK-05).
    internal const string BK_DurabilityWatermarks = "DurabilityWatermarks";
    // ReSharper restore InconsistentNaming

    #endregion

    private ConcurrentDictionary<int, LogicalSegment<PersistentStore>> _segments;
    private LogicalSegment<PersistentStore> _occupancySegment;
    private BitmapL3 _occupancyMap;
    private int _occupancyNextReservedPageIndex;
    private int _occupancyNextReservedMapPageIndex;
    // CK-05 (C2): the twin pre-reserved for the NEXT occupancy map-extension page. Like the map-ext reserve itself, it must
    // be set aside before the occupancy map can be full, since allocating it would itself require a free page.
    private int _occupancyNextReservedMapTwinPageIndex;

    // Meta-pair (CK-05) alternation state: the slot holding the current valid meta content and its generation. Every meta
    // write goes to the OTHER slot (PersistMetaNow), so the valid slot is never in-flight. Serialized by _metaLock.
    private int _metaCurrentSlot;          // MetaSlotA or MetaSlotB — the current valid slot
    private ulong _metaGeneration;
    private readonly Lock _metaLock = new();

    // ── CK-05 (C2) segment-directory A/B slot-pairing ──────────────────────────────────────────────────────────────────
    // A directory page (a segment's root or a map-extension page) carries no WAL records and is not rebuildable, so a torn
    // write would corrupt the segment. Each gets a TWIN (a second physical slot); writes alternate to the non-current slot
    // (gen+1 + CRC) then flip. Keyed by the PRIMARY file-page index (the one the segment's directory references) → its pair
    // state. Populated eagerly: seeded at Create, resolved by a physical both-slots pre-walk before Load. The twin is
    // occupancy-bit-set but never enters any segment's page list, so chunk allocation never sees it.
    private readonly struct DirPair
    {
        public readonly int Twin;          // the shadow physical slot (reciprocal: the twin's TwinPageIndex points back to primary)
        public readonly int CurrentSlot;   // the slot holding the current valid content: == primary, or == Twin
        public readonly ulong Gen;         // the current pair generation (monotonic; higher valid slot wins at open)

        public DirPair(int twin, int currentSlot, ulong gen)
        {
            Twin = twin;
            CurrentSlot = currentSlot;
            Gen = gen;
        }
    }

    // ConcurrentDictionary for lock-free reads on the cold-read path (MapReadOffset); slot-selection + flip are serialized
    // under _pairLock inside PersistProtectedPage so a concurrent writer can never clobber the current-valid slot.
    private readonly ConcurrentDictionary<int, DirPair> _pairState = new();
    private readonly Lock _pairLock = new();

    /// <summary>
    /// Bootstrap dictionary: key-value metadata stored as a compact byte stream on page 0.
    /// Replaces the hard-coded SPI fields in RootFileHeader. Loaded on open, saved on create/shutdown.
    /// </summary>
    public BootstrapDictionary Bootstrap { get; } = new();

    /// <summary>Byte offset within page 0 where the bootstrap stream starts (after the slim identity header).</summary>
    internal static unsafe int BootstrapStreamOffset => PageBaseHeaderSize + sizeof(RootFileHeader);

    // Synchronization for occupancy map operations (replaces lock(_occupancyMap))
    private AccessControl _occupancyMapAccess;

    // Throughput counters (supplement inherited _metrics)
    private long _evictionCount;

    /// <summary>Maximum number of file pages the occupancy bitmap can track (current capacity).</summary>
    public int OccupancyCapacityPages => _occupancyMap?.Capacity ?? 0;

    internal ManagedPagedMMF(IResourceRegistry resourceRegistry, EpochManager epochManager, IMemoryAllocator memoryAllocator, PagedMMFOptions options,
        IResource parent, string resourceName, ILogger<PagedMMF> logger) :
        base(memoryAllocator, epochManager, options, parent, $"ManagedPagedMMF_{options?.DatabaseName ?? Guid.NewGuid().ToString("N")}", logger)
    {
    }

    public int AllocatePage(ChangeSet changeSet = null)
    {
        Span<int> pageId = stackalloc int[1];
        AllocatePages(ref pageId, 0, changeSet);
        return pageId[0];
    }

    public void AllocatePages(ref Span<int> pageIds, int startFrom = 0, ChangeSet changeSet = null)
    {
        var wc = WaitContext.FromTimeout(TimeoutOptions.Current.PageCacheLockTimeout);
        if (!_occupancyMapAccess.EnterExclusiveAccess(ref wc))
        {
            ThrowHelper.ThrowLockTimeout("PageCache/AllocatePages", TimeoutOptions.Current.PageCacheLockTimeout);
        }
        try
        {
            AllocatePagesCore(ref pageIds, startFrom, changeSet);
        }
        finally
        {
            _occupancyMapAccess.ExitExclusiveAccess();
        }
    }

    // Core allocation logic - caller must hold _occupancyMapAccess exclusive lock
    private void AllocatePagesCore(ref Span<int> pageIds, int startFrom, ChangeSet changeSet)
    {
        // Need to grow the occupancy segment if we run out of pages
        while (_occupancyMap.Allocate(ref pageIds, startFrom, changeSet) == false)
        {
            // Will use _occupancyNextReservedPage to grow the segment of one page
            GrowOccupancySegment(changeSet);

            // Now that we can allocate many more pages, reserve the next page to be used when the occupancy map needs to grow again
            // Use core method directly to avoid deadlock (we already hold the lock)
            _occupancyNextReservedPageIndex = AllocatePageCore(changeSet);

            // Persist the updated reserved page indices to the root file header
            UpdateOccupancyReservedPages();
        }
    }

    // Core single-page allocation - caller must hold _occupancyMapAccess exclusive lock
    private int AllocatePageCore(ChangeSet changeSet)
    {
        Span<int> pageId = stackalloc int[1];
        AllocatePagesCore(ref pageId, 0, changeSet);
        return pageId[0];
    }

    // Under lock of caller
    private void GrowOccupancySegment(ChangeSet changeSet)
    {
        // Note: adding one page will allow to track 8000 * 8 more pages which is 500MiB of data stored in the file
        var length = _occupancySegment.Length + 1;
        var pages = (length < 64) ? stackalloc int[length] : new int[length];
        _occupancySegment.Pages.CopyTo(pages);
        pages[length - 1] = _occupancyNextReservedPageIndex;

        // CK-05 (C2): if CreateOrGrow consumes the reserved map-extension page (turning it into a NEW directory page), that
        // page needs a twin. We hold the occupancy lock, so its GetOrAllocateDirectoryTwin must NOT allocate (that re-enters
        // AllocatePages). Pre-seed the reserved map page's pair to the pre-reserved twin so the stamp resolves to it; if the
        // reserve isn't consumed this grow, drop the seed again.
        var reservedMapPage = _occupancyNextReservedMapPageIndex;
        if (reservedMapPage != 0)
        {
            SeedDirectoryPair(reservedMapPage, _occupancyNextReservedMapTwinPageIndex);
        }

        _occupancySegment.CreateOrGrow(PageBlockType.OccupancyMap, pages, length - 1, ref _occupancyNextReservedMapPageIndex, true, changeSet);
        var oldCap = _occupancyMap.Capacity;
        _occupancyMap.Grow();
        // Phase 5: Storage:OccupancyMap:Grow event.
        TyphonEvent.EmitStorageOccupancyMapGrow(oldCap, _occupancyMap.Capacity);

        // If CreateOrGrow used the reserved page for map extension, the value after the call is 0. That page is now a real
        // directory page (its twin seed stays); refill BOTH the map reserve and its twin reserve — lock-free (we hold the
        // lock; the just-grown map has free capacity, so this cannot recurse into another grow). If the reserve was NOT
        // consumed, drop the speculative twin seed so the reserved page stays a plain free page.
        if (_occupancyNextReservedMapPageIndex == 0)
        {
            _occupancyNextReservedMapPageIndex = AllocatePageCore(changeSet);
            _occupancyNextReservedMapTwinPageIndex = AllocatePageCore(changeSet);
        }
        else if (reservedMapPage != 0)
        {
            _pairState.TryRemove(reservedMapPage, out _);
        }
    }

    public bool FreePages(ReadOnlySpan<int> pages, int startFrom = 0, ChangeSet changeSet = null)
    {
        _occupancyMapAccess.EnterExclusiveAccess(ref WaitContext.Null);
        try
        {
            _occupancyMap.Free(pages, startFrom, changeSet);
        }
        finally
        {
            _occupancyMapAccess.ExitExclusiveAccess();
        }

        return false;
    }

    /// <summary>
    /// Frees a contiguous range of pages on the occupancy map. Range overload of <see cref="FreePages(ReadOnlySpan{int},int,ChangeSet)"/>; avoids the caller
    /// having to synthesise an <c>int[]</c> of page ids when the range is already a <c>(first, count)</c> pair.
    /// </summary>
    /// <remarks>
    /// Used by the BulkLoad recovery path (Phase 3b in <see cref="WalRecovery"/>) to free pages referenced by an orphan <see cref="WalChunkType.BulkBegin"/>
    /// manifest. Idempotent — see <see cref="BitmapL3.FreeRange"/>.
    /// </remarks>
    /// <param name="firstPageId">Lowest page id in the range (inclusive).</param>
    /// <param name="count">Number of pages to free. Range covers <c>[firstPageId, firstPageId + count)</c>.</param>
    /// <param name="changeSet">Optional change set for tracking dirty bitmap pages.</param>
    public void FreePages(int firstPageId, int count, ChangeSet changeSet = null)
    {
        _occupancyMapAccess.EnterExclusiveAccess(ref WaitContext.Null);
        try
        {
            _occupancyMap.FreeRange(firstPageId, count, changeSet);
        }
        finally
        {
            _occupancyMapAccess.ExitExclusiveAccess();
        }
    }

    unsafe protected override void OnFileCreating()
    {
        base.OnFileCreating();

        using var guard = EpochGuard.Enter(EpochManager);
        var epoch = guard.Epoch;

        // Write the identity header into the in-memory meta page; the bootstrap + CRC + generation are stamped by
        // PersistMetaNow below (the genesis alternation write).
        RequestPageEpoch(0, epoch, out var memPageIdx);
        var latched = TryLatchPageExclusive(memPageIdx);
        Debug.Assert(latched, "TryLatchPageExclusive failed on root page during file creation");
        var page = GetPage(memPageIdx);

        ref var rootFileHeader = ref page.StructAt<RootFileHeader>(PageBaseHeaderSize);
        fixed (byte* headerSignature = rootFileHeader.HeaderSignature)
        {
            StringExtensions.StoreString(HeaderSignature, headerSignature, 32);
        }
        rootFileHeader.DatabaseFormatRevision = DatabaseFormatRevision;
        fixed (byte* databaseName = rootFileHeader.DatabaseName)
        {
            StringExtensions.StoreString(Options.DatabaseName, databaseName, 64);
        }
        UnlatchPageExclusive(memPageIdx);

        Logger.LogInformation("Initialize DiskPageAllocator service with root at page {PageId}", OccupancySegmentRootPageIndex);

        // Initialize the occupancy segment (root at page 2) and map — pages 0–1 are the meta pair.
        _segments = new ConcurrentDictionary<int, LogicalSegment<PersistentStore>>();
        var cs = CreateChangeSet();

        // CK-05 (C2): the occupancy segment's directory is itself a protected pair, but its twin can't be allocated through
        // the occupancy map (it doesn't exist yet — the chicken-and-egg). Pre-seed the root's pair to the fixed reserved
        // twin (page 3) BEFORE Create, so CreateOrGrow's GetOrAllocateDirectoryTwin returns it instead of recursing into
        // allocation. Seed current = twin → the genesis SaveChanges below lands the first write on the primary (page 2).
        // Directory-only root (v4): the root (page 2) holds no bitmap words, so the segment is created spanning [2, 4] — its
        // first data page (page 4) is where the L0 occupancy words live.
        SeedDirectoryPair(OccupancySegmentRootPageIndex, OccupancyRootTwinPageIndex);
        _occupancySegment = CreateOccupancySegment(OccupancySegmentRootPageIndex, OccupancyFirstDataPageIndex, PageBlockType.OccupancyMap, cs);

        // ReSharper disable InconsistentlySynchronizedField
        _occupancyMap = new BitmapL3(_occupancySegment);

        // Pre-allocated pages: 0–1 meta pair, 2 occupancy root (directory only), 3 occupancy root twin, 4 occupancy first
        // data page (holds the L0 bits — including these very SetL0 calls). Pass the genesis ChangeSet so the bit writes ride
        // its lifecycle (the data page is already tracked by it from Create) — cs.SaveChanges below drains the DirtyCounter to
        // 0, leaving the occupancy clean after genesis (no orphaned dirty mark for the first checkpoint to re-write).
        _occupancyMap.SetL0(MetaSlotA, cs);
        _occupancyMap.SetL0(MetaSlotB, cs);
        _occupancyMap.SetL0(OccupancySegmentRootPageIndex, cs);
        _occupancyMap.SetL0(OccupancyRootTwinPageIndex, cs);
        _occupancyMap.SetL0(OccupancyFirstDataPageIndex, cs);

        // Reserve pages to use when the occupancy map needs to grow (can't allocate them once the map is full): the next data
        // page (5), the next map-extension directory page (6), and ITS twin (7) — the map-ext page is a protected directory
        // page, so its twin must likewise be pre-reserved.
        _occupancyNextReservedPageIndex = 5;
        _occupancyNextReservedMapPageIndex = 6;
        _occupancyNextReservedMapTwinPageIndex = 7;
        _occupancyMap.SetL0(_occupancyNextReservedPageIndex, cs);
        _occupancyMap.SetL0(_occupancyNextReservedMapPageIndex, cs);
        _occupancyMap.SetL0(_occupancyNextReservedMapTwinPageIndex, cs);
        // ReSharper restore InconsistentlySynchronizedField

        // Initialize bootstrap dictionary with core occupancy SPIs + an empty durability watermark block.
        Bootstrap.SetInt(BK_OccupancyMapSPI, OccupancySegmentRootPageIndex);
        Bootstrap.Set(BK_OccupancyReserved,
            BootstrapDictionary.Value.FromInt3(_occupancyNextReservedPageIndex, _occupancyNextReservedMapPageIndex, _occupancyNextReservedMapTwinPageIndex));
        WriteDurabilityWatermarks(0, false);

        // Persist the structural (occupancy) pages, then write the genesis meta page (generation 1) via the alternation path.
        cs.SaveChanges();
        PersistMetaNow();
    }

    protected override void OnFileLoading()
    {
        base.OnFileLoading();

        // CK-05: select + load the current meta slot into the cached meta page (or throw if both slots are corrupt)
        // BEFORE reading the identity header — the header itself lives in the current slot.
        LoadMeta();

        using var guard = EpochGuard.Enter(EpochManager);
        var epoch = guard.Epoch;

        RequestPageEpoch(0, epoch, out var memPageIdx);
        var page = GetPage(memPageIdx);
        ref var h = ref page.StructAt<RootFileHeader>(PageBaseHeaderSize);

        if (h.HeaderSignatureString != HeaderSignature)
        {
            throw new InvalidOperationException(
                $"Invalid database file: expected header signature '{HeaderSignature}', found '{h.HeaderSignatureString}'. File: {Options.BuildDatabasePathFileName()}");
        }

        if (h.DatabaseNameString != Options.DatabaseName)
        {
            throw new InvalidOperationException(
                $"Database name mismatch: expected '{Options.DatabaseName}', found '{h.DatabaseNameString}'. File: {Options.BuildDatabasePathFileName()}");
        }

        if (h.DatabaseFormatRevision != DatabaseFormatRevision)
        {
            throw new InvalidOperationException(
                $"Incompatible database format: file version {h.DatabaseFormatRevision}, engine version {DatabaseFormatRevision}. File: {Options.BuildDatabasePathFileName()}");
        }

        Logger.LogInformation("Load Database '{DatabaseName}' from file '{FilePathName}'", h.DatabaseNameString, Options.BuildDatabasePathFileName());

        // Load bootstrap dictionary from page 0
        LoadBootstrap();

        // Initialize the occupancy segment and map from bootstrap
        _segments = new ConcurrentDictionary<int, LogicalSegment<PersistentStore>>();

        var occupancyMapSPI = Bootstrap.GetInt(BK_OccupancyMapSPI);
        _occupancySegment = LoadOccupancySegment(occupancyMapSPI, PageBlockType.OccupancyMap);

        // ReSharper disable InconsistentlySynchronizedField
        _occupancyMap = new BitmapL3(_occupancySegment);

        var occupancyReserved = Bootstrap.Get(BK_OccupancyReserved);
        _occupancyNextReservedPageIndex = occupancyReserved.GetInt();
        _occupancyNextReservedMapPageIndex = occupancyReserved.GetInt(1);
        _occupancyNextReservedMapTwinPageIndex = occupancyReserved.GetInt(2);
        // ReSharper restore InconsistentlySynchronizedField
    }
    public LogicalSegment<PersistentStore> GetSegment(int filePageIndex)
    {
        var dic = _segments;
        return dic?.GetOrAdd(filePageIndex, fpid =>
        {
            var segment = new LogicalSegment<PersistentStore>(new PersistentStore(this));
            ResolveDirectoryPairsForLoad(fpid);   // CK-05 (C2): register directory-page slot state before the load walks them
            segment.Load(fpid);
            return segment;
        });
    }

    /// <summary>
    /// The authoritative set of every registered persistent segment, keyed by root page. Used by storage introspection (Module 15) so every allocated page
    /// is attributable to a segment (and thus classifiable) — there is no other complete source of "which segments own which pages". Returns a snapshot view.
    /// </summary>
    internal ICollection<LogicalSegment<PersistentStore>> RegisteredSegments => _segments?.Values ?? Array.Empty<LogicalSegment<PersistentStore>>();

    /// <summary>
    /// The pages the occupancy machinery holds in reserve outside any segment: one for the next occupancy-segment data growth, one for the next
    /// bitmap-extension map page, and (CK-05, C2) one for that map page's twin. All are bit-set in the occupancy bitmap but belong to no
    /// <see cref="LogicalSegment{TStore}"/>, so storage-introspection / integrity checks need to account for them explicitly. Any value can be <c>0</c>
    /// (briefly, between consumption by <see cref="GrowOccupancySegment"/> and refill by the next <see cref="AllocatePageCore"/>).
    /// </summary>
    internal (int DataReserve, int MapReserve, int MapTwinReserve) ReservedOccupancyPages
        => (_occupancyNextReservedPageIndex, _occupancyNextReservedMapPageIndex, _occupancyNextReservedMapTwinPageIndex);

    /// <summary>
    /// Snapshot of the live segment-directory pairs (CK-05, C2) as <c>(primary, twin)</c> file-page indices. Each twin is bit-set in the occupancy bitmap but
    /// belongs to no segment's page list, so storage introspection uses this to classify the twin (mirroring its primary) and the integrity check uses it to
    /// account for the twin's occupancy bit — without it, every directory twin would read as an orphan / Unknown page.
    /// </summary>
    internal IReadOnlyList<(int Primary, int Twin)> DirectoryPairs
    {
        get
        {
            var list = new List<(int, int)>(_pairState.Count);
            foreach (var kvp in _pairState)
            {
                list.Add((kvp.Key, kvp.Value.Twin));
            }
            return list;
        }
    }

    internal LogicalSegment<PersistentStore> CreateOccupancySegment(int rootPageIndex, int firstDataPageIndex, PageBlockType type, ChangeSet cs)
    {
        var dic = _segments;
        if (dic == null)
        {
            return null;
        }

        var segment = new LogicalSegment<PersistentStore>(new PersistentStore(this));
        if (dic.TryAdd(rootPageIndex, segment) == false)
        {
            return null;
        }

        // Directory-only root (v4): the occupancy segment spans two genesis pages — the directory root and its first data
        // page (the L0 bitmap words). clear: true zeroes the data page so every page starts as Free.
        Span<int> ids = stackalloc int[2];
        ids[0] = rootPageIndex;
        ids[1] = firstDataPageIndex;
        if (segment.Create(type, StorageSegmentKind.Occupancy, ids, true, cs) == false)
        {
            return null;
        }

        Logger.LogDebug("Create Logical Segment at {StartPageId} using pages {Pages}", segment.Pages[0], segment.Pages.ToArray());
        return segment;
    }

    internal LogicalSegment<PersistentStore> LoadOccupancySegment(int filePageIndex, PageBlockType type)
    {
        var dic = _segments;
        if (dic == null)
        {
            return null;
        }

        var segment = new LogicalSegment<PersistentStore>(new PersistentStore(this));
        if (dic.TryAdd(filePageIndex, segment) == false)
        {
            return null;
        }

        ResolveDirectoryPairsForLoad(filePageIndex);   // CK-05 (C2): register directory-page slot state before the load walks them
        if (segment.Load(filePageIndex) == false)
        {
            return null;
        }

        Logger.LogDebug("Create Logical Segment at {StartPageId} using pages {Pages}", segment.Pages[0], segment.Pages.ToArray());
        return segment;
    }

    public LogicalSegment<PersistentStore> AllocateSegment(PageBlockType type, int length, ChangeSet changeSet = null, 
        StorageSegmentKind kind = StorageSegmentKind.Other)
    {
        var dic = _segments;
        if (dic == null)
        {
            return null;
        }

        // Directory-only root (v4): the root page holds only the segment's page directory, so a segment needs at least one
        // data page beyond it. Clamp here so callers never have to know the layout invariant — a requested length of 1 (or 0)
        // becomes a 2-page segment (directory root + one data page).
        length = Math.Max(length, 2);

        var pages = (length < 64) ? stackalloc int[length] : new int[length];
        AllocatePages(ref pages, 0, changeSet);

        var segment = new LogicalSegment<PersistentStore>(new PersistentStore(this));
        if (!dic.TryAdd(pages[0], segment))
        {
            Debug.Fail("Segment root page already registered in dictionary — duplicate allocation");
        }

        if (!segment.Create(type, kind, pages, false, changeSet))
        {
            return null;
        }

        //Logger.LogDebug("Create Logical Segment at {StartPageId} using pages {Pages}", segment.Pages[0], segment.Pages.ToArray());
        return segment;
    }

    protected override void Dispose(bool disposing)
    {
        if (IsDisposed)
        {
            return;
        }

        if (disposing)
        {
            var dic = Interlocked.Exchange(ref _segments, null);
            if (dic != null)
            {
                foreach (var segment in dic.Values)
                {
                    segment.Dispose();
                }
            }
        }
        base.Dispose(disposing);
    }

    public ChunkBasedSegment<PersistentStore> AllocateChunkBasedSegment(PageBlockType type, int length, int stride, ChangeSet changeSet = null, 
        StorageSegmentKind kind = StorageSegmentKind.Other)
    {
        var dic = _segments;
        if (dic == null)
        {
            return null;
        }

        // Directory-only root (v4): the root holds only the page directory (0 chunks), so a chunk-based segment needs at
        // least one data page. Clamp so a requested length of 1 still yields a usable segment (directory root + data page).
        length = Math.Max(length, 2);

        Span<int> pages = stackalloc int[length];
        AllocatePages(ref pages, 0, changeSet);

        var segment = new ChunkBasedSegment<PersistentStore>(EpochManager, new PersistentStore(this), stride);
        if (!dic.TryAdd(pages[0], segment))
        {
            Debug.Fail("Segment root page already registered in dictionary — duplicate allocation");
        }

        if (!segment.Create(type, kind, pages, false, changeSet))
        {
            return null;
        }

        Logger.LogDebug("Create Chunk Based Logical Segment at {StartPageId} using pages {Pages}", segment.Pages[0], segment.Pages.ToArray());
        return segment;
    }
    
    public ChunkBasedSegment<PersistentStore> LoadChunkBasedSegment(int filePageIndex, int stride)
    {
        var dic = _segments;
        if (dic == null)
        {
            return null;
        }

        var segment = new ChunkBasedSegment<PersistentStore>(EpochManager, new PersistentStore(this), stride);
        if (dic.TryAdd(filePageIndex, segment) == false)
        {
            Debug.Fail("Segment root page already registered in dictionary — duplicate allocation");
        }

        ResolveDirectoryPairsForLoad(filePageIndex);   // CK-05 (C2): register directory-page slot state before the load walks them
        if (segment.Load(filePageIndex) == false)
        {
            return null;
        }

        Logger.LogDebug("Load Chunk Based Logical Segment at {StartPageId} using pages {Pages}", segment.Pages[0], segment.Pages.ToArray());
        return segment;
    }

    /// <summary>Try to load a segment. Returns false (instead of asserting) if the root page is already registered.</summary>
    public bool TryLoadChunkBasedSegment(int filePageIndex, int stride, out ChunkBasedSegment<PersistentStore> result)
    {
        result = null;
        var dic = _segments;
        if (dic == null)
        {
            return false;
        }

        var segment = new ChunkBasedSegment<PersistentStore>(EpochManager, new PersistentStore(this), stride);
        if (!dic.TryAdd(filePageIndex, segment))
        {
            return false; // Already registered — caller should fall back to fresh allocation
        }

        ResolveDirectoryPairsForLoad(filePageIndex);   // CK-05 (C2): register directory-page slot state before the load walks them
        if (!segment.Load(filePageIndex))
        {
            return false;
        }

        result = segment;
        return true;
    }

    /// <summary>
    /// Returns a previously loaded segment for the given page index, or loads it if not yet present.
    /// Safe to call when the segment may already be in the registry (e.g., system component segments loaded by the engine constructor).
    /// </summary>
    public ChunkBasedSegment<PersistentStore> GetOrLoadChunkBasedSegment(int filePageIndex, int stride)
    {
        var dic = _segments;
        if (dic == null)
        {
            return null;
        }

        if (dic.TryGetValue(filePageIndex, out var existing))
        {
            return existing as ChunkBasedSegment<PersistentStore>;
        }

        return LoadChunkBasedSegment(filePageIndex, stride);
    }

    public bool DeleteSegment(int filePageIndex, ChangeSet changeSet = null)
    {
        var dic = _segments;
        if (dic == null)
        {
            return false;
        }

        if (dic.TryRemove(filePageIndex, out var segment) == false)
        {
            return false;
        }

        // CK-05 (C2): every directory page (the root + each map-extension page) carries a TWIN — a second physical slot held
        // only via _pairState, never part of segment.Pages. Freeing just segment.Pages would (a) LEAK those twins and the
        // map-extension pages themselves (directory infrastructure, also outside Pages) — their occupancy bits would stay set
        // forever — and (b) leave a STALE _pairState entry that would mis-route a cold read to the dead twin slot if the
        // primary page is later reallocated to a new segment (silent corruption; exercised by the schema-evolution path).
        // Collect the directory pages, free their twins + the map-extension pages, and clear the PairState before freeing the
        // segment's own pages.
        //
        // PRECONDITION (caller-serialized, load-bearing): DeleteSegment runs as a structural operation with NO concurrent reader
        // of this segment. This matters because between the _pairState.TryRemove below and the primary actually being reallocated,
        // a cold read of `primary` whose current slot was the twin would fall through MapReadOffset to identity and read the stale
        // primary slot. The schema-evolution / drop paths hold the structural lock, so no such reader exists. We deliberately do
        // NOT add a defensive evict/epoch-scan here: no cheap page-evict primitive exists, and the guard would cost more than the
        // race it guards (which the invariant already forecloses). A future caller that deletes a segment with live readers
        // violates THIS precondition — that is the bug to fix, not this routine.
        using (EpochGuard.Enter(EpochManager))
        {
            var dirPages = new List<int> { segment.RootPageIndex };
            segment.CollectDirectoryMapExtensionPages(EpochManager.GlobalEpoch, dirPages);

            var toFree = new List<int>(dirPages.Count * 2);
            foreach (var dirPage in dirPages)
            {
                if (_pairState.TryRemove(dirPage, out var pair) && pair.Twin > 0)
                {
                    toFree.Add(pair.Twin);
                }
            }

            // The map-extension pages (everything after the root) are directory infrastructure, not in segment.Pages.
            for (int i = 1; i < dirPages.Count; i++)
            {
                toFree.Add(dirPages[i]);
            }

            if (toFree.Count > 0)
            {
                FreePages(toFree.ToArray(), 0, changeSet);
            }
        }

        FreePages(segment.Pages, 0, changeSet);
        return true;
    }

    public bool DeleteSegment(LogicalSegment<PersistentStore> segment, ChangeSet changeSet = null) => DeleteSegment(segment.RootPageIndex, changeSet);

    // ═══════════════════════════════════════════════════════════════
    // Bootstrap Dictionary Persistence
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Serialize the bootstrap dictionary to page 0 at <see cref="BootstrapStreamOffset"/>.
    /// Latches page 0 exclusively during write.
    /// </summary>
    internal void SaveBootstrap(ChangeSet changeSet = null)
    {
        // The bootstrap lives on the meta page — persist it via the CK-05 alternation path, not an in-place page write.
        // changeSet is retained for source compatibility but unused (the meta page is not DC-tracked; PersistMetaNow owns it).
        PersistMetaNow();
    }

    /// <summary>
    /// Deserialize the bootstrap dictionary from page 0 at <see cref="BootstrapStreamOffset"/>.
    /// Called during <see cref="OnFileLoading"/> after identity validation.
    /// </summary>
    private unsafe void LoadBootstrap()
    {
        using var guard = EpochGuard.Enter(EpochManager);
        var epoch = guard.Epoch;

        RequestPageEpoch(0, epoch, out var memPageIdx);
        byte* pageAddr = GetMemPageAddress(memPageIdx);
        byte* streamAddr = pageAddr + BootstrapStreamOffset;
        int maxBytes = PageSize - BootstrapStreamOffset;
        Bootstrap.ReadFrom(streamAddr, maxBytes);
    }

    // ═══════════════════════════════════════════════════════════════
    // Meta pair (CK-05) — A/B slot alternation for the root/bootstrap page
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Persists the in-memory meta page (root file header + bootstrap dictionary) by writing it to the ALTERNATE slot
    /// with the next generation + a fresh CRC, fsyncing, then flipping the current-slot pointer (CK-05). The
    /// current-valid slot is never overwritten, so a torn write can never destroy the only good copy. One
    /// <see cref="_metaLock"/> serializes every meta writer (checkpoint-LSN flip, bootstrap/schema saves, clean-shutdown).
    /// </summary>
    internal unsafe void PersistMetaNow()
    {
        lock (_metaLock)
        {
            using var guard = EpochGuard.Enter(EpochManager);
            var epoch = guard.Epoch;

            RequestPageEpoch(0, epoch, out var memPageIdx);
            var latched = TryLatchPageExclusive(memPageIdx);
            Debug.Assert(latched, "TryLatchPageExclusive failed on the meta page during PersistMetaNow");

            byte* pageAddr = GetMemPageAddress(memPageIdx);
            var pageSpan = new Span<byte>(pageAddr, PageSize);

            // Serialize the bootstrap into the in-memory meta page, then stamp the next generation + CRC. The meta page
            // is CRC'd now (unlike the v1 root page, which was checksum-exempt) — the CRC is the torn-slot detector.
            Bootstrap.WriteTo(pageAddr + BootstrapStreamOffset, PageSize - BootstrapStreamOffset);

            var nextGen = _metaGeneration + 1;
            PageBaseHeader.WritePairGeneration(pageSpan, nextGen);
            ((PageBaseHeader*)pageAddr)->PageChecksum = WalCrc.ComputeSkipping(pageSpan, PageBaseHeader.PageChecksumOffset, PageBaseHeader.PageChecksumSize);

            // Write the full image to the alternate slot and fsync BEFORE flipping the current pointer.
            var targetSlot = MetaSlotA + MetaSlotB - _metaCurrentSlot;   // the other slot
            WritePageDirect(targetSlot, pageSpan);
            FlushToDisk();

            UnlatchPageExclusive(memPageIdx);

            _metaCurrentSlot = targetSlot;
            _metaGeneration = nextGen;
        }
    }

    /// <summary>
    /// Reads both meta-pair slots at open and selects the current one — the highest pair generation among CRC-valid
    /// slots — loading its image into the cached meta page (logical page 0). Both-invalid throws (the database cannot
    /// be opened; never a silent fallback). Sets <see cref="_metaCurrentSlot"/> / <see cref="_metaGeneration"/>.
    /// </summary>
    private unsafe void LoadMeta()
    {
        using var guard = EpochGuard.Enter(EpochManager);
        var epoch = guard.Epoch;

        // _metaCurrentSlot defaults to MetaSlotA, so this cold read loads physical slot A into the cached meta page.
        RequestPageEpoch(0, epoch, out var memPageIdx);
        byte* pageAddr = GetMemPageAddress(memPageIdx);
        var slotASpan = new Span<byte>(pageAddr, PageSize);
        var aValid = IsPairSlotValid(slotASpan, out var genA);

        Span<byte> slotB = new byte[PageSize];
        var bValid = false;
        ulong genB = 0;
        if ((MetaSlotB + 1L) * PageSize <= FileSize)
        {
            ReadPageDirect(MetaSlotB, slotB);
            bValid = IsPairSlotValid(slotB, out genB);
        }

        if (!aValid && !bValid)
        {
            throw new InvalidOperationException(
                $"Both meta-pair slots (pages 0 and 1) are corrupt — the database cannot be opened. File: {Options.BuildDatabasePathFileName()}");
        }

        if (bValid && (!aValid || genB > genA))
        {
            slotB.CopyTo(slotASpan);   // make the cached meta page hold slot B's (current) content
            _metaCurrentSlot = MetaSlotB;
            _metaGeneration = genB;
        }
        else
        {
            _metaCurrentSlot = MetaSlotA;
            _metaGeneration = genA;
        }
    }

    /// <summary>
    /// A protected-pair slot (meta or directory) is valid iff its CRC matches and its pair generation is &gt; 0
    /// (0 = never written via the alternation path).
    /// </summary>
    private static bool IsPairSlotValid(ReadOnlySpan<byte> slot, out ulong generation)
    {
        generation = 0;
        var storedCrc = MemoryMarshal.Read<uint>(slot.Slice(PageBaseHeader.PageChecksumOffset));
        var computedCrc = WalCrc.ComputeSkipping(slot, PageBaseHeader.PageChecksumOffset, PageBaseHeader.PageChecksumSize);
        if (storedCrc != computedCrc)
        {
            return false;
        }
        generation = PageBaseHeader.ReadPairGeneration(slot);
        return generation > 0;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Routes a cold read of a protected page to its current valid slot: page 0 → the meta current slot (C1); a
    /// segment-directory page → its <see cref="DirPair.CurrentSlot"/> (C2, one lock-free dictionary probe — only on a cache
    /// MISS, never on a hit). All other pages map identity. The probe per non-zero miss is negligible against the disk read.
    /// </remarks>
    protected override long MapReadOffset(int filePageIndex)
    {
        if (filePageIndex == 0)
        {
            return _metaCurrentSlot * (long)PageSize;
        }

        if (_pairState.TryGetValue(filePageIndex, out var dp))
        {
            return dp.CurrentSlot * (long)PageSize;
        }

        return base.MapReadOffset(filePageIndex);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Only the meta pair is externally persisted (written exclusively by <see cref="PersistMetaNow"/>, excluded from the
    /// checkpoint dirty-write). Segment-directory pages ARE checkpoint-written — just redirected to their alternate slot by
    /// <see cref="PersistProtectedPage"/> — so they must NOT be excluded here.
    /// </remarks>
    protected override bool IsExternallyPersisted(int filePageIndex) => filePageIndex is MetaSlotA or MetaSlotB;

    // ── Durability watermark block (CheckpointLSN + CleanShutdown), packed Int3, flipped atomically by the meta generation ──

    private (long CheckpointLsn, bool CleanShutdown) ReadDurabilityWatermarks()
    {
        if (!Bootstrap.TryGet(BK_DurabilityWatermarks, out var v))
        {
            return (0, false);
        }
        var lsn = (uint)v.GetInt(0) | ((long)v.GetInt(1) << 32);
        return (lsn, v.GetInt(2) != 0);
    }

    private void WriteDurabilityWatermarks(long checkpointLsn, bool cleanShutdown)
        => Bootstrap.Set(BK_DurabilityWatermarks, BootstrapDictionary.Value.FromInt3((int)checkpointLsn, (int)(checkpointLsn >> 32), cleanShutdown ? 1 : 0));

    /// <summary>The persisted checkpoint-LSN watermark (0 on a fresh database).</summary>
    internal long CheckpointLsnWatermark => ReadDurabilityWatermarks().CheckpointLsn;

    /// <summary>The persisted clean-shutdown flag.</summary>
    internal bool CleanShutdownWatermark => ReadDurabilityWatermarks().CleanShutdown;

    /// <summary>Test hook (CK-05): the physical slot (0 or 1) currently holding the valid meta content.</summary>
    internal int MetaCurrentSlotForTest => _metaCurrentSlot;

    /// <summary>Test hook (CK-05): the current meta generation.</summary>
    internal ulong MetaGenerationForTest => _metaGeneration;

    /// <summary>Sets the clean-shutdown flag (preserving CheckpointLSN) and persists the meta page atomically (CK-05).</summary>
    internal void SetCleanShutdown(bool cleanShutdown)
    {
        lock (_metaLock)
        {
            var w = ReadDurabilityWatermarks();
            WriteDurabilityWatermarks(w.CheckpointLsn, cleanShutdown);
            PersistMetaNow();
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Segment-directory A/B slot-pairing (CK-05, C2)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns the twin (second physical slot) for a directory page, allocating it on first request. If the page is
    /// already paired — pre-seeded (the occupancy directory) or re-entered during <c>Grow</c> — returns the existing twin
    /// with no allocation, which is what breaks the occupancy chicken-and-egg (allocating would re-enter the occupancy map).
    /// Called by <see cref="LogicalSegment{TStore}.CreateOrGrow"/> when it initializes a directory page, to stamp
    /// <see cref="LogicalSegmentHeader.TwinPageIndex"/>.
    /// </summary>
    internal int GetOrAllocateDirectoryTwin(int primaryPageIndex, ChangeSet changeSet)
    {
        if (_pairState.TryGetValue(primaryPageIndex, out var dp))
        {
            return dp.Twin;
        }

        var twin = AllocatePage(changeSet);
        SeedDirectoryPair(primaryPageIndex, twin);
        return twin;
    }

    /// <summary>
    /// Seeds a directory pair with <c>current = TWIN</c> (the shadow), generation 0. The first protected write therefore
    /// lands on the PRIMARY (the slot the segment's directory references), which becomes the durable current — and the
    /// primary stays resident (DC&gt;0) from <c>Create</c> until that write, so no cold read can route to the still-empty
    /// twin in the meantime. The twin only becomes valid on the second write.
    /// </summary>
    private void SeedDirectoryPair(int primaryPageIndex, int twinPageIndex) => _pairState[primaryPageIndex] = new DirPair(twinPageIndex, twinPageIndex, 0);

    /// <summary>
    /// Persists a protected directory page (CK-05 write protocol): under <see cref="_pairLock"/>, write the full image to
    /// the NON-current slot with <c>PairGeneration = gen+1</c> and a fresh CRC, fsync, then flip the in-memory current
    /// pointer. The current-valid slot is never overwritten, so a torn write can never destroy the only good copy. The
    /// whole read-slot → write → fsync → flip sequence is atomic so a concurrent writer (a runtime <c>SaveChanges</c>
    /// racing the checkpoint thread — both can touch a directory page via the CP-04 <c>DC=2</c> pattern) can never
    /// stale-read the current slot and clobber it. <paramref name="image"/> is mutated in place (gen + CRC stamped).
    /// </summary>
    internal unsafe void PersistProtectedPage(int primaryPageIndex, byte* image)
    {
        lock (_pairLock)
        {
            var dp = _pairState[primaryPageIndex];
            var alternate = (dp.CurrentSlot == primaryPageIndex) ? dp.Twin : primaryPageIndex;
            var newGen = dp.Gen + 1;

            var span = new Span<byte>(image, PageSize);
            PageBaseHeader.WritePairGeneration(span, newGen);
            ((PageBaseHeader*)image)->PageChecksum = WalCrc.ComputeSkipping(span, PageBaseHeader.PageChecksumOffset, PageBaseHeader.PageChecksumSize);

            WritePageDirect(alternate, span);
            FlushToDisk();

            _pairState[primaryPageIndex] = new DirPair(dp.Twin, alternate, newGen);
        }
    }

    /// <inheritdoc />
    protected override unsafe bool TryPersistProtectedPage(int filePageIndex, byte* image)
    {
        if (!_pairState.ContainsKey(filePageIndex))
        {
            return false;
        }

        PersistProtectedPage(filePageIndex, image);
        return true;
    }

    /// <inheritdoc />
    protected override bool IsProtectedPage(int filePageIndex) => _pairState.ContainsKey(filePageIndex);

    /// <summary>
    /// Before a segment is loaded, registers the CK-05 pair state for every directory page it owns (root + map-extension
    /// chain) by physically reading both slots and selecting the current one (highest valid generation). Runs at the file
    /// level (<see cref="PagedMMF.ReadPageDirect"/>), so it precedes — and is what enables — page-cache routing via
    /// <see cref="MapReadOffset"/>. Walks the directory map chain through the CURRENT slot's
    /// <see cref="LogicalSegmentHeader.LogicalSegmentNextMapPBID"/>. The immutable identity (the <c>IsLogicalSegment</c>
    /// flag + <c>TwinPageIndex</c>) survives a torn primary because it lives in the first 4 KiB sector and never changes
    /// across generations. Both-slots-invalid for any directory page → throws (the segment cannot be loaded).
    /// </summary>
    internal void ResolveDirectoryPairsForLoad(int rootPageIndex)
    {
        Span<byte> bufPrimary = new byte[PageSize];
        Span<byte> bufTwin = new byte[PageSize];

        var primary = rootPageIndex;
        var maxWalk = (FileSize / PageSize) + 1;   // cycle guard: bounded by total file pages
        for (long step = 0; primary != 0 && step < maxWalk; step++)
        {
            if ((primary + 1L) * PageSize > FileSize)
            {
                break;   // page beyond the file — a truncated/never-written chain tail
            }

            ReadPageDirect(primary, bufPrimary);

            var flags = (PageBlockFlags)bufPrimary[0];
            ref var hdrPrimary = ref MemoryMarshal.AsRef<LogicalSegmentHeader>(bufPrimary.Slice(LogicalSegmentHeader.Offset));
            var twin = hdrPrimary.TwinPageIndex;
            if ((flags & PageBlockFlags.IsLogicalSegment) == 0 || twin == 0)
            {
                break;   // not a paired directory page (pre-C2 / not a directory) — nothing to resolve here
            }

            var pValid = IsPairSlotValid(bufPrimary, out var genP);

            // The twin index is read from the primary's header. In a real torn write the first 4 KiB sector (which holds the
            // immutable flag + TwinPageIndex) is intact, so the index is trustworthy even when the primary's CRC fails — but a
            // fully-garbage primary (e.g. a multi-sector smash) can yield an out-of-range index. Bound-check it: only read a
            // twin that is a real in-file page (> 0 and within EOF). An out-of-range twin → no readable sibling → if the
            // primary is also invalid, both-invalid fires below (the loud, correct failure). A twin can also legitimately sit
            // beyond EOF (a freshly seeded pair whose twin has not been written yet — right after genesis). Never read past EOF
            // (a short read would leave stale bytes from a previous iteration in the reused buffer).
            var tValid = false;
            ulong genT = 0;
            if (twin > 0 && (twin + 1L) * PageSize <= FileSize)
            {
                ReadPageDirect(twin, bufTwin);
                tValid = IsPairSlotValid(bufTwin, out genT);
            }

            if (!pValid && !tValid)
            {
                throw new InvalidOperationException(
                    $"Both slots of directory page {primary} (twin {twin}) are corrupt — the segment cannot be loaded. " +
                    $"File: {Options.BuildDatabasePathFileName()}");
            }

            int next;
            if (tValid && (!pValid || genT > genP))
            {
                _pairState[primary] = new DirPair(twin, twin, genT);
                next = MemoryMarshal.AsRef<LogicalSegmentHeader>(bufTwin.Slice(LogicalSegmentHeader.Offset)).LogicalSegmentNextMapPBID;
            }
            else
            {
                _pairState[primary] = new DirPair(twin, primary, genP);
                next = hdrPrimary.LogicalSegmentNextMapPBID;
            }

            primary = next;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Checkpoint Support
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Persists the current <see cref="_occupancyNextReservedPageIndex"/> and <see cref="_occupancyNextReservedMapPageIndex"/> values
    /// to the <see cref="RootFileHeader"/> on page 0. Called after the occupancy map grows and new reserved pages are allocated.
    /// </summary>
    /// <remarks>Caller must hold <see cref="_occupancyMapAccess"/> exclusive lock.</remarks>
    private void UpdateOccupancyReservedPages()
    {
        Bootstrap.Set(BK_OccupancyReserved,
            BootstrapDictionary.Value.FromInt3(_occupancyNextReservedPageIndex, _occupancyNextReservedMapPageIndex, _occupancyNextReservedMapTwinPageIndex));
        PersistMetaNow();
    }

    /// <summary>
    /// Updates the <see cref="RootFileHeader.CheckpointLSN"/> field in page 0 and flushes to disk. Called by the Checkpoint Manager after dirty pages have
    /// been written and fsynced.
    /// </summary>
    /// <param name="checkpointLSN">The new checkpoint LSN to persist.</param>
    /// <param name="epochManager">Epoch manager for page access.</param>
    internal void UpdateCheckpointLSN(long checkpointLSN, EpochManager epochManager)
    {
        // Advance the checkpoint-LSN watermark (preserving CleanShutdown) and flip the meta pair — the generation flip
        // is the atomic, fsynced commit point (CK-05). epochManager is unused now (PersistMetaNow uses the MMF's own).
        lock (_metaLock)
        {
            var w = ReadDurabilityWatermarks();
            WriteDurabilityWatermarks(checkpointLSN, w.CleanShutdown);
            PersistMetaNow();
        }
    }

    #region IMetricSource Implementation

    /// <inheritdoc />
    public void ReadMetrics(IMetricWriter writer)
    {
        var metrics = GetMetrics();

        // Memory: page cache buffer size
        long allocatedBytes = MemPages?.EstimatedMemorySize ?? 0;
        writer.WriteMemory(allocatedBytes, allocatedBytes);

        // Capacity: free vs total memory pages
        long freePages = metrics.FreeMemPageCount;
        long totalPages = MemPagesCount;
        writer.WriteCapacity(totalPages - freePages, totalPages);

        // DiskIO: read/write operations
        writer.WriteDiskIO(
            metrics.ReadFromDiskCount,
            metrics.PageWrittenToDiskCount,
            (long)metrics.ReadFromDiskCount * PageSize,
            (long)metrics.PageWrittenToDiskCount * PageSize);

        // Throughput
        writer.WriteThroughput("CacheHits", metrics.MemPageCacheHit);
        writer.WriteThroughput("CacheMisses", metrics.MemPageCacheMiss);
        writer.WriteThroughput("Evictions", _evictionCount);
    }

    /// <inheritdoc />
    /// <remarks>No high-water-mark fields on this resource — body intentionally empty.</remarks>
    public void ResetPeaks()
    {
    }

    #endregion

    #region IDebugPropertiesProvider Implementation

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object> GetDebugProperties()
    {
        var metrics = GetMetrics();
        metrics.GetMemPageExtraInfo(out var extraInfo);

        return new Dictionary<string, object>
        {
            ["PageCache.FreeCount"]             = extraInfo.FreeMemPageCount,
            ["PageCache.AllocatingCount"]       = extraInfo.AllocatingMemPageCount,
            ["PageCache.IdleCount"]             = extraInfo.IdleMemPageCount,
            ["PageCache.ExclusiveCount"]        = extraInfo.ExclusiveMemPageCount,
            ["PageCache.DirtyCount"]            = extraInfo.DirtyPageCount,
            ["PageCache.PendingIOReadCount"]    = extraInfo.PendingIOReadCount,
            ["ClockSweep.MinCounter"]           = extraInfo.MinClockSweepCounter,
            ["ClockSweep.MaxCounter"]           = extraInfo.MaxClockSweepCounter,
            ["Segments.Count"]                  = _segments?.Count ?? 0,
            ["OccupancyMap.Capacity"]           = _occupancyMap?.Capacity ?? 0,
        };
    }

    #endregion
}