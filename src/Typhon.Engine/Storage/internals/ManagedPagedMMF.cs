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
// Pages of an empty file
// ------------------------------------------------------------------------------------------------------------------------------------------------------------
// 0: Root file header
// 1: Occupancy segment root page
// 2: Reserved page for occupancy map growth
// 3: Reserved page for occupancy map next map data (in case we need more than 500 pages to store the occupancy map)
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

    internal const int InitialReservedPageCount = 4;
    private const int OccupancySegmentRootPageIndex = 1;
    internal const string HeaderSignature = "TyphonDatabase";

    // Bootstrap dictionary keys (storage layer)
    // ReSharper disable InconsistentNaming
    internal const string BK_OccupancyMapSPI = "OccupancyMapSPI";
    internal const string BK_OccupancyReserved = "OccupancyReserved";
    internal const string BK_CheckpointLSN = "CheckpointLSN";
    // ReSharper restore InconsistentNaming

    #endregion

    private ConcurrentDictionary<int, LogicalSegment<PersistentStore>> _segments;
    private LogicalSegment<PersistentStore> _occupancySegment;
    private BitmapL3 _occupancyMap;
    private int _occupancyNextReservedPageIndex;
    private int _occupancyNextReservedMapPageIndex;

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

        _occupancySegment.CreateOrGrow(PageBlockType.OccupancyMap, pages, length - 1, ref _occupancyNextReservedMapPageIndex, true, changeSet);
        var oldCap = _occupancyMap.Capacity;
        _occupancyMap.Grow();
        // Phase 5: Storage:OccupancyMap:Grow event.
        TyphonEvent.EmitStorageOccupancyMapGrow(oldCap, _occupancyMap.Capacity);

        // If CreateOrGrow uses the reserved page for map extension, the value after the call is 0, so we need to allocate a new one
        if (_occupancyNextReservedMapPageIndex == 0)
        {
            _occupancyNextReservedMapPageIndex = AllocatePage();
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

    unsafe protected override void OnFileCreating()
    {
        base.OnFileCreating();

        using var guard = EpochGuard.Enter(EpochManager);
        var epoch = guard.Epoch;

        RequestPageEpoch(0, epoch, out var memPageIdx);
        var latched = TryLatchPageExclusive(memPageIdx);
        Debug.Assert(latched, "TryLatchPageExclusive failed on root page during file creation");
        var page = GetPage(memPageIdx);

        // Set header information
        var cs = CreateChangeSet();
        cs.AddByMemPageIndex(memPageIdx);
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

        Logger.LogInformation("Initialize DiskPageAllocator service with root at page {PageId}", OccupancySegmentRootPageIndex);

        // Initialize the occupancy segment and map
        _segments = new ConcurrentDictionary<int, LogicalSegment<PersistentStore>>();

        _occupancySegment = CreateOccupancySegment(OccupancySegmentRootPageIndex, PageBlockType.OccupancyMap, 1, cs);

        // ReSharper disable InconsistentlySynchronizedField
        _occupancyMap = new BitmapL3(_occupancySegment);

        // The first two pages are already manually allocated (file header and occupancy segment root page)
        _occupancyMap.SetL0(0);
        _occupancyMap.SetL0(1);

        // Reserve pages to use when the occupancy map needs to grow, we need to reserve because we can't allocate them by the time the map is full
        _occupancyNextReservedPageIndex = 2;
        _occupancyNextReservedMapPageIndex = 3;
        _occupancyMap.SetL0(_occupancyNextReservedPageIndex);
        _occupancyMap.SetL0(_occupancyNextReservedMapPageIndex);
        // ReSharper restore InconsistentlySynchronizedField

        // Initialize bootstrap dictionary with core occupancy SPIs
        Bootstrap.SetInt(BK_OccupancyMapSPI, OccupancySegmentRootPageIndex);
        Bootstrap.Set(BK_OccupancyReserved, BootstrapDictionary.Value.FromInt2(_occupancyNextReservedPageIndex, _occupancyNextReservedMapPageIndex));

        // Serialize bootstrap stream to page 0
        byte* bootstrapAddr = GetMemPageAddress(memPageIdx) + BootstrapStreamOffset;
        int maxBootstrapBytes = PageSize - BootstrapStreamOffset;
        Bootstrap.WriteTo(bootstrapAddr, maxBootstrapBytes);

        UnlatchPageExclusive(memPageIdx);
        cs.SaveChanges();
        FlushToDisk();
    }

    protected override void OnFileLoading()
    {
        base.OnFileLoading();

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
        // ReSharper restore InconsistentlySynchronizedField
    }
    public LogicalSegment<PersistentStore> GetSegment(int filePageIndex)
    {
        var dic = _segments;
        return dic?.GetOrAdd(filePageIndex, fpid =>
        {
            var segment = new LogicalSegment<PersistentStore>(new PersistentStore(this));
            segment.Load(fpid);
            return segment;
        });
    }

    internal LogicalSegment<PersistentStore> CreateOccupancySegment(int filePageIndex, PageBlockType type, int length, ChangeSet cs)
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

        if (segment.Create(type, filePageIndex, true, cs) == false)
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

        if (segment.Load(filePageIndex) == false)
        {
            return null;
        }

        Logger.LogDebug("Create Logical Segment at {StartPageId} using pages {Pages}", segment.Pages[0], segment.Pages.ToArray());
        return segment;
    }

    public LogicalSegment<PersistentStore> AllocateSegment(PageBlockType type, int length, ChangeSet changeSet = null)
    {
        var dic = _segments;
        if (dic == null)
        {
            return null;
        }

        var pages = (length < 64) ? stackalloc int[length] : new int[length];
        AllocatePages(ref pages, 0, changeSet);

        var segment = new LogicalSegment<PersistentStore>(new PersistentStore(this));
        if (!dic.TryAdd(pages[0], segment))
        {
            Debug.Fail("Segment root page already registered in dictionary — duplicate allocation");
        }

        if (segment.Create(type, pages, false, changeSet) == false)
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

    public ChunkBasedSegment<PersistentStore> AllocateChunkBasedSegment(PageBlockType type, int length, int stride, ChangeSet changeSet = null)
    {
        var dic = _segments;
        if (dic == null)
        {
            return null;
        }

        Span<int> pages = stackalloc int[length];
        AllocatePages(ref pages, 0, changeSet);

        var segment = new ChunkBasedSegment<PersistentStore>(EpochManager, new PersistentStore(this), stride);
        if (!dic.TryAdd(pages[0], segment))
        {
            Debug.Fail("Segment root page already registered in dictionary — duplicate allocation");
        }

        if (!segment.Create(type, pages, false, changeSet))
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
    internal unsafe void SaveBootstrap(ChangeSet changeSet = null)
    {
        using var guard = EpochGuard.Enter(EpochManager);
        var epoch = guard.Epoch;

        RequestPageEpoch(0, epoch, out var memPageIdx);
        var latched = TryLatchPageExclusive(memPageIdx);
        Debug.Assert(latched, "TryLatchPageExclusive failed on root page during bootstrap save");

        var cs = changeSet ?? CreateChangeSet();
        cs.AddByMemPageIndex(memPageIdx);

        byte* pageAddr = GetMemPageAddress(memPageIdx);
        byte* streamAddr = pageAddr + BootstrapStreamOffset;
        int maxBytes = PageSize - BootstrapStreamOffset;
        Bootstrap.WriteTo(streamAddr, maxBytes);

        UnlatchPageExclusive(memPageIdx);
        if (changeSet == null)
        {
            cs.SaveChanges();
        }
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
    // Checkpoint Support
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Persists the current <see cref="_occupancyNextReservedPageIndex"/> and <see cref="_occupancyNextReservedMapPageIndex"/> values
    /// to the <see cref="RootFileHeader"/> on page 0. Called after the occupancy map grows and new reserved pages are allocated.
    /// </summary>
    /// <remarks>Caller must hold <see cref="_occupancyMapAccess"/> exclusive lock.</remarks>
    private unsafe void UpdateOccupancyReservedPages()
    {
        using var guard = EpochGuard.Enter(EpochManager);
        var epoch = guard.Epoch;

        RequestPageEpoch(0, epoch, out var memPageIdx);
        var latched = TryLatchPageExclusive(memPageIdx);
        Debug.Assert(latched, "TryLatchPageExclusive failed on root page during occupancy reserved pages update");

        var cs = CreateChangeSet();
        cs.AddByMemPageIndex(memPageIdx);

        Bootstrap.Set(BK_OccupancyReserved, BootstrapDictionary.Value.FromInt2(_occupancyNextReservedPageIndex, _occupancyNextReservedMapPageIndex));
        byte* bootstrapAddr = GetMemPageAddress(memPageIdx) + BootstrapStreamOffset;
        Bootstrap.WriteTo(bootstrapAddr, PageSize - BootstrapStreamOffset);

        UnlatchPageExclusive(memPageIdx);
        cs.SaveChanges();
    }

    /// <summary>
    /// Updates the <see cref="RootFileHeader.CheckpointLSN"/> field in page 0 and flushes to disk. Called by the Checkpoint Manager after dirty pages have
    /// been written and fsynced.
    /// </summary>
    /// <param name="checkpointLSN">The new checkpoint LSN to persist.</param>
    /// <param name="epochManager">Epoch manager for page access.</param>
    internal unsafe void UpdateCheckpointLSN(long checkpointLSN, EpochManager epochManager)
    {
        using var guard = EpochGuard.Enter(epochManager);
        var epoch = guard.Epoch;

        RequestPageEpoch(0, epoch, out var memPageIdx);
        var latched = TryLatchPageExclusive(memPageIdx);
        Debug.Assert(latched, "TryLatchPageExclusive failed on root page during checkpoint LSN update");

        var cs = CreateChangeSet();
        cs.AddByMemPageIndex(memPageIdx);

        Bootstrap.SetLong(BK_CheckpointLSN, checkpointLSN);
        byte* bootstrapAddr = GetMemPageAddress(memPageIdx) + BootstrapStreamOffset;
        Bootstrap.WriteTo(bootstrapAddr, PageSize - BootstrapStreamOffset);

        UnlatchPageExclusive(memPageIdx);
        cs.SaveChanges();

        // Fsync to make the checkpoint LSN durable
        FlushToDisk();
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