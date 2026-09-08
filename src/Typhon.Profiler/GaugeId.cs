using JetBrains.Annotations;

namespace Typhon.Profiler;

/// <summary>
/// Stable wire-contract identifier for a single gauge value carried inside a <see cref="TraceEventKind.PerTickSnapshot"/> record.
/// </summary>
/// <remarks>
/// <para>
/// Values are 16-bit unsigned and partitioned into category ranges (0x0100 = unmanaged memory, 0x0110 = GC heap, 0x0200 = page cache,
/// 0x0210 = transient store, 0x0300 = WAL, 0x0400 = transactions/UoW). Each category leaves headroom for additional gauges without
/// renumbering. Numeric assignments are part of the <c>.typhon-trace</c> file format — <b>never renumber an existing entry; only append new ones</b>.
/// </para>
/// <para>
/// The <c>valueKind</c> byte preceding each value in the packed payload declares the on-wire representation
/// (see <see cref="GaugeValueKind"/>). The expected value kind per gauge is documented in inline comments and must not change without
/// bumping the wire format version.
/// </para>
/// </remarks>
[PublicAPI]
public enum GaugeId : ushort
{
    /// <summary>Sentinel — never emitted.</summary>
    None = 0x0000,

    // ── Unmanaged memory (PinnedMemoryBlock via NativeMemory) ──────────── 0x0100

    /// <summary>Running total of unmanaged bytes currently allocated through <c>MemoryAllocator.TrackAllocation</c>. Value kind: <see cref="GaugeValueKind.U64Bytes"/>.</summary>
    MemoryUnmanagedTotalBytes = 0x0100,

    /// <summary>All-time peak of <see cref="MemoryUnmanagedTotalBytes"/> since process start. Value kind: <see cref="GaugeValueKind.U64Bytes"/>.</summary>
    MemoryUnmanagedPeakBytes = 0x0101,

    /// <summary>Count of live (not-yet-disposed) <c>PinnedMemoryBlock</c> instances. Value kind: <see cref="GaugeValueKind.U32Count"/>.</summary>
    MemoryUnmanagedLiveBlocks = 0x0102,

    // ── Memory pool / fragmentation (Phase 5) ──────────────────────────── 0x0103

    /// <summary>Allocator fragmentation ratio expressed as hundredths-of-percent (e.g. 5025 = 50.25%). Value kind: <see cref="GaugeValueKind.U32PercentHundredths"/>.</summary>
    MemoryFragmentationPctHundredths = 0x0103,

    /// <summary>Free-block count for stride-64 pool. Value kind: <see cref="GaugeValueKind.U32Count"/>.</summary>
    MemoryPoolFreeBlocksStride64 = 0x0104,

    /// <summary>Free-block count for stride-128 pool. Value kind: <see cref="GaugeValueKind.U32Count"/>.</summary>
    MemoryPoolFreeBlocksStride128 = 0x0105,

    /// <summary>Free-block count for stride-256 pool. Value kind: <see cref="GaugeValueKind.U32Count"/>.</summary>
    MemoryPoolFreeBlocksStride256 = 0x0106,

    /// <summary>Free-block count for stride-512 pool. Value kind: <see cref="GaugeValueKind.U32Count"/>.</summary>
    MemoryPoolFreeBlocksStride512 = 0x0107,

    /// <summary>Free-block count for stride-1024 pool. Value kind: <see cref="GaugeValueKind.U32Count"/>.</summary>
    MemoryPoolFreeBlocksStride1024 = 0x0108,

    /// <summary>Free-block count for stride-2048 pool. Value kind: <see cref="GaugeValueKind.U32Count"/>.</summary>
    MemoryPoolFreeBlocksStride2048 = 0x0109,

    /// <summary>Free-block count for stride-4096 pool. Value kind: <see cref="GaugeValueKind.U32Count"/>.</summary>
    MemoryPoolFreeBlocksStride4096 = 0x010A,

    // ── GC heap — sampled from GC.GetGCMemoryInfo() ────────────────────── 0x0110

    /// <summary>Gen0 size after last GC, in bytes. Value kind: <see cref="GaugeValueKind.U64Bytes"/>.</summary>
    GcHeapGen0Bytes = 0x0110,

    /// <summary>Gen1 size after last GC, in bytes. Value kind: <see cref="GaugeValueKind.U64Bytes"/>.</summary>
    GcHeapGen1Bytes = 0x0111,

    /// <summary>Gen2 size after last GC, in bytes. Value kind: <see cref="GaugeValueKind.U64Bytes"/>.</summary>
    GcHeapGen2Bytes = 0x0112,

    /// <summary>Large-object-heap size after last GC, in bytes. Value kind: <see cref="GaugeValueKind.U64Bytes"/>.</summary>
    GcHeapLohBytes = 0x0113,

    /// <summary>Pinned-object-heap size after last GC, in bytes. Value kind: <see cref="GaugeValueKind.U64Bytes"/>.</summary>
    GcHeapPohBytes = 0x0114,

    /// <summary>Total bytes the GC has committed from the OS. Value kind: <see cref="GaugeValueKind.U64Bytes"/>.</summary>
    GcHeapCommittedBytes = 0x0115,

    // ── PersistentStore / page cache ───────────────────────────────────── 0x0200

    /// <summary>Total pages in the in-memory page cache. Fixed at init — emitted once in the first snapshot. Value kind: <see cref="GaugeValueKind.U32Count"/>.</summary>
    PageCacheTotalPages = 0x0200,

    /// <summary>Pages currently free (empty slots). Value kind: <see cref="GaugeValueKind.U32Count"/>.</summary>
    PageCacheFreePages = 0x0201,

    /// <summary>Pages used, clean (no pending write). Mutually-exclusive with <see cref="PageCacheDirtyUsedPages"/>. Value kind: <see cref="GaugeValueKind.U32Count"/>.</summary>
    PageCacheCleanUsedPages = 0x0202,

    /// <summary>Pages used, dirty (have pending writes awaiting checkpoint). Mutually-exclusive with <see cref="PageCacheCleanUsedPages"/>. Value kind: <see cref="GaugeValueKind.U32Count"/>.</summary>
    PageCacheDirtyUsedPages = 0x0203,

    /// <summary>Pages held under exclusive lock by an in-flight writer. Value kind: <see cref="GaugeValueKind.U32Count"/>.</summary>
    PageCacheExclusivePages = 0x0204,

    /// <summary>Pages pinned by an active epoch guard (cannot be evicted). Value kind: <see cref="GaugeValueKind.U32Count"/>.</summary>
    PageCacheEpochProtectedPages = 0x0205,

    /// <summary>Count of I/O reads currently in flight. Value kind: <see cref="GaugeValueKind.U32Count"/>.</summary>
    PageCachePendingIoReads = 0x0206,

    /// <summary>Stopwatch-tick age of the oldest entry on the LRU list (snapshotted once per tick). Value kind: <see cref="GaugeValueKind.U64Bytes"/>.</summary>
    PageCacheLRUAgeTicks = 0x0207,

    /// <summary>Total file size in bytes for the primary database's <see cref="System.IO.MemoryMappedFiles.MemoryMappedFile"/>. Value kind: <see cref="GaugeValueKind.U64Bytes"/>.</summary>
    PageCacheFileSizeBytes = 0x0208,

    // ── TransientStore ─────────────────────────────────────────────────── 0x0210

    /// <summary>Bytes currently in use by the transient store. Value kind: <see cref="GaugeValueKind.U64Bytes"/>.</summary>
    TransientStoreBytesUsed = 0x0210,

    /// <summary>Configured max bytes for the transient store. Fixed at init — emitted once. Value kind: <see cref="GaugeValueKind.U64Bytes"/>.</summary>
    TransientStoreMaxBytes = 0x0211,

    // ── WAL ────────────────────────────────────────────────────────────── 0x0300

    /// <summary>Bytes currently queued in the WAL commit buffer awaiting drain. Value kind: <see cref="GaugeValueKind.U64Bytes"/>.</summary>
    WalCommitBufferUsedBytes = 0x0300,

    /// <summary>Configured WAL commit buffer capacity. Fixed at init — emitted once. Value kind: <see cref="GaugeValueKind.U64Bytes"/>.</summary>
    WalCommitBufferCapacityBytes = 0x0301,

    /// <summary>Count of WAL frames submitted but not yet durable on disk. Value kind: <see cref="GaugeValueKind.U32Count"/>.</summary>
    WalInflightFrames = 0x0302,

    /// <summary>Count of staging buffers currently checked out from the pool. Value kind: <see cref="GaugeValueKind.U32Count"/>.</summary>
    WalStagingPoolRented = 0x0303,

    /// <summary>Peak of <see cref="WalStagingPoolRented"/> since process start. Value kind: <see cref="GaugeValueKind.U32Count"/>.</summary>
    WalStagingPoolPeakRented = 0x0304,

    /// <summary>Configured staging pool capacity. Fixed at init — emitted once. Value kind: <see cref="GaugeValueKind.U32Count"/>.</summary>
    WalStagingPoolCapacity = 0x0305,

    /// <summary>Cumulative total of staging-pool rents since process start (viewer derives rate as Δ/Δt). Value kind: <see cref="GaugeValueKind.U64Bytes"/>.</summary>
    WalStagingTotalRentsCumulative = 0x0306,

    // ── Transactions + UoW ─────────────────────────────────────────────── 0x0400

    /// <summary>Count of active (not-yet-committed) transaction-chain entries. Value kind: <see cref="GaugeValueKind.U32Count"/>.</summary>
    TxChainActiveCount = 0x0400,

    /// <summary>Current size of the transaction-chain pool (active + pooled-idle). Value kind: <see cref="GaugeValueKind.U32Count"/>.</summary>
    TxChainPoolSize = 0x0401,

    /// <summary>Count of active entries in the UoW registry. Value kind: <see cref="GaugeValueKind.U32Count"/>.</summary>
    UowRegistryActiveCount = 0x0402,

    /// <summary>Count of void (recovery-orphan) UoW registry entries. Typically zero outside of post-crash recovery. Value kind: <see cref="GaugeValueKind.U32Count"/>.</summary>
    UowRegistryVoidCount = 0x0403,

    // ── Cumulative throughput counters ────────────────────────── 0x0410
    // Monotonic counters — the viewer derives per-tick throughput by subtracting consecutive snapshots.
    // Using U64 because session lifetimes can accumulate hundreds of millions of operations; U32 would roll over in ~2 hours at 40K tx/s.

    /// <summary>Cumulative count of transactions committed since engine start. Value kind: <see cref="GaugeValueKind.U64Bytes"/>.</summary>
    TxChainCommitTotal = 0x0410,

    /// <summary>Cumulative count of transactions rolled back since engine start. Value kind: <see cref="GaugeValueKind.U64Bytes"/>.</summary>
    TxChainRollbackTotal = 0x0411,

    /// <summary>Cumulative count of UoW slots allocated since engine start. Value kind: <see cref="GaugeValueKind.U64Bytes"/>.</summary>
    UowRegistryCreatedTotal = 0x0412,

    /// <summary>Cumulative count of UoW slots committed since engine start. Value kind: <see cref="GaugeValueKind.U64Bytes"/>.</summary>
    UowRegistryCommittedTotal = 0x0413,

    /// <summary>Cumulative count of transactions created since engine start. Viewer subtracts consecutive snapshots for per-tick "transactions started" throughput. Value kind: <see cref="GaugeValueKind.U64Bytes"/>.</summary>
    TxChainCreatedTotal = 0x0414,

    // ── Spatial grid (VDB cell grid) ───────────────────────────────────── 0x0500

    /// <summary>Blocks the sparse cell grid has materialised. Value kind: <see cref="GaugeValueKind.U32Count"/>.</summary>
    SpatialGridBlockCount = 0x0500,

    /// <summary>Cells that actually hold something — the population the 64-byte <c>CellState</c> pool is paid for. Value kind: <see cref="GaugeValueKind.U32Count"/>.</summary>
    SpatialGridOccupiedCells = 0x0501,

    /// <summary>
    /// Occupied cells as hundredths of a percent of the blocks' addressable capacity. Value kind: <see cref="GaugeValueKind.U32PercentHundredths"/>.
    /// </summary>
    /// <remarks>A low fill argues for replacing the dense per-block <c>int[]</c> with a bitmask plus compaction; a high one says the dense array is right.</remarks>
    SpatialGridIntraBlockFill = 0x0502,

    /// <summary>Bytes the grid currently holds — block index arrays plus the occupied cells' pool. Value kind: <see cref="GaugeValueKind.U64Bytes"/>.</summary>
    SpatialGridResidentBytes = 0x0503,

    /// <summary>
    /// Bytes a DENSE grid over the same world would have allocated, occupied or not. Value kind: <see cref="GaugeValueKind.U64Bytes"/>.
    /// </summary>
    /// <remarks>
    /// Read against <see cref="SpatialGridResidentBytes"/>: that comparison is the sparse grid's whole argument, and it is also the guard on the one
    /// discipline it depends on — a read path that resolves cells WITH creation makes resident climb toward dense with no other symptom (rule <c>VG-02</c>).
    /// </remarks>
    SpatialGridDenseEquivalentBytes = 0x0504,

    /// <summary>
    /// The previous tick's partitioning-fence SPAN in microseconds — Prep's start to the last phase that dispatched, so the six phase spans plus the
    /// scheduler's gaps between them. Value kind: <see cref="GaugeValueKind.U32Count"/>, carrying microseconds (there is no duration kind; the name carries
    /// the unit, as <see cref="PageCacheLRUAgeTicks"/> does).
    /// </summary>
    /// <remarks>
    /// <b>The one figure that says what the fence cost the frame.</b> Every other spatial-maintenance timing on the wire is summed across workers, and a sum
    /// cannot be compared to a frame budget: W workers busy for 1 ms report W. Read the summed ones against this — their ratio is the parallelism the work
    /// achieved. Absent from a trace whose host drove the fence serially, because the phase-exec systems that time it never ran.
    /// </remarks>
    ClusterFenceSpanUs = 0x0505,
}

/// <summary>
/// On-wire representation selector for a single gauge value inside a <see cref="TraceEventKind.PerTickSnapshot"/> payload.
/// </summary>
/// <remarks>
/// The decoder reads the <c>valueKind</c> byte to determine how many trailing bytes to consume (4 or 8) and how to interpret them.
/// Numeric values are wire-stable — never renumber. Append-only.
/// </remarks>
public enum GaugeValueKind : byte
{
    /// <summary>4-byte unsigned 32-bit count. Payload size: 4 B.</summary>
    U32Count = 0,

    /// <summary>8-byte unsigned 64-bit value (typically bytes). Payload size: 8 B.</summary>
    U64Bytes = 1,

    /// <summary>8-byte signed 64-bit value (for signed deltas). Payload size: 8 B.</summary>
    I64Signed = 2,

    /// <summary>4-byte unsigned percentage expressed as hundredths (e.g., 5025 = 50.25%). Payload size: 4 B. Reserved for future.</summary>
    U32PercentHundredths = 3,
}
