namespace Typhon.Workbench.Dtos.Storage;

// DTOs for the Database File Map (Module 15, Track A — A1 coarse tier). Per-page arrays travel as base64-encoded
// raw SoA buffers to keep payloads compact for large databases; the client decodes them into typed arrays.

/// <summary>Top-level metadata for the data file + WAL — the response of <c>GET /dbmap/regions</c>.</summary>
public record StorageRegionsDto(
    string DatabaseName,
    long DataFileBytes,
    int DataFilePageCount,
    long WalBytes,
    int HilbertOrder,
    long CheckpointLsn,
    int DownSampleFactor,
    /// <summary>Pages per A2 detail tile — the client derives which tiles intersect the viewport from this.</summary>
    int DetailTileSize,
    StorageSegmentDto[] Segments);

/// <summary>One logical segment in the segment table.</summary>
public record StorageSegmentDto(
    int Id,
    int RootPageIndex,
    string Kind,
    int PageCount,
    /// <summary>Component type name when the segment is a component table; empty otherwise. Drives map search.</summary>
    string TypeName);

/// <summary>
/// Coarse per-page descriptors for a quadtree node — the response of <c>GET /dbmap/region</c>. In A1 the whole
/// coarse map is returned in one call (node 0, leaf LOD).
/// </summary>
public record StorageRegionDto(
    int Node,
    string Lod,
    int PageCount,
    string PageTypes,
    string OwnerSegmentIds,
    string PageRanks);

/// <summary>The top levels of the Hilbert aggregate pyramid — the response of <c>GET /dbmap/overview</c>.</summary>
public record StorageOverviewDto(
    int HilbertOrder,
    StoragePyramidLevelDto[] Levels);

/// <summary>
/// Aggregate storage health rollup — the response of <c>GET /dbmap/health</c> (GAP-16). A server-side rollup of
/// the whole-DB summary plus a per-segment table, so the Storage Health dashboard makes one call instead of
/// aggregating every segment client-side. Computed in-memory (page classification + segment registry); no page
/// I/O beyond what the coarse map already reads.
/// </summary>
public record StorageHealthDto(
    string DatabaseName,
    long DataFileBytes,
    int DataFilePageCount,
    int UsedPageCount,
    int FreePageCount,
    long WalBytes,
    long CheckpointLsn,
    int SegmentCount,
    /// <summary>Sum of free-chunk bytes across chunk-based segments — space reclaimable by compaction.</summary>
    long ReclaimableBytes,
    /// <summary>Free pages as a percentage of the file (a coarse fragmentation proxy for v1).</summary>
    double FragmentationPct,
    StorageHealthSegmentDto[] Segments);

/// <summary>One segment's health row in the <c>GET /dbmap/health</c> rollup.</summary>
public record StorageHealthSegmentDto(
    int Id,
    string Kind,
    /// <summary>Component type name for component/cluster segments; empty otherwise.</summary>
    string TypeName,
    int PageCount,
    int AllocatedChunkCount,
    int ChunkCapacity,
    /// <summary>Allocated chunks ÷ capacity, as a percentage (0 for non-chunk segments).</summary>
    double ChunkFillPct,
    /// <summary>Free-chunk bytes (free chunks × stride) reclaimable in this segment.</summary>
    long ReclaimableBytes,
    /// <summary>Live entity count for cluster segments; 0 otherwise.</summary>
    long EntityCount,
    /// <summary>Cluster: entities ÷ active-cluster slots; otherwise the chunk fill — the "how full" headline %.</summary>
    double OccupancyPct);

/// <summary>One level of the aggregate pyramid: <c>4^Level</c> nodes, each covering a contiguous page range.</summary>
public record StoragePyramidLevelDto(
    int Level,
    int NodeCount,
    string DominantTypes,
    int[] UsedCounts);
