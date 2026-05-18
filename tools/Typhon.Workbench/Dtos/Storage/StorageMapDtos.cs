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
    StorageSegmentDto[] Segments);

/// <summary>One logical segment in the segment table.</summary>
public record StorageSegmentDto(
    int Id,
    int RootPageIndex,
    string Kind,
    int PageCount);

/// <summary>
/// Coarse per-page descriptors for a quadtree node — the response of <c>GET /dbmap/region</c>. In A1 the whole
/// coarse map is returned in one call (node 0, leaf LOD).
/// </summary>
public record StorageRegionDto(
    int Node,
    string Lod,
    int PageCount,
    string PageTypes,
    string OwnerSegmentIds);

/// <summary>The top levels of the Hilbert aggregate pyramid — the response of <c>GET /dbmap/overview</c>.</summary>
public record StorageOverviewDto(
    int HilbertOrder,
    StoragePyramidLevelDto[] Levels);

/// <summary>One level of the aggregate pyramid: <c>4^Level</c> nodes, each covering a contiguous page range.</summary>
public record StoragePyramidLevelDto(
    int Level,
    int NodeCount,
    string DominantTypes,
    int[] UsedCounts);
