namespace Typhon.Workbench.Dtos.Storage;

// DTOs for the Database File Map detail tier (Module 15, Track A — A2). The per-page detail arrays travel as
// base64-encoded raw SoA buffers, exactly like the A1 coarse tier; one-off page / segment / chunk decodes are
// plain JSON. Every endpoint here is read-only and viewport-scoped — never a full-file scan.

/// <summary>Live CRC verdict for a page — mirrors the client's <c>DbCrcStatus</c>.</summary>
public enum StorageCrcStatus : byte
{
    /// <summary>The stored checksum is zero — the page was never checksummed (predates FPI / not yet flushed).</summary>
    Unverified = 0,

    /// <summary>The live CRC32C matches the stored checksum — the page is intact.</summary>
    Verified = 1,

    /// <summary>The live CRC32C disagrees with the stored checksum — the page is corrupt or mid-write.</summary>
    Failed = 2,
}

/// <summary>
/// Per-chunk class for the L3 chunk grid (Module 15, A6) — tells the renderer how to colour an occupied chunk. <c>Slot</c> chunks colour by binary occupancy
/// (the A2 behaviour, e.g. Component rows); <c>ContainerFill</c> chunks colour by their <c>ChunkFill</c> value. The remaining values are reserved for the
/// later per-kind passes (Index / Hashmap) and are not emitted yet.
/// </summary>
public enum StorageChunkClass : byte
{
    /// <summary>A slot-like chunk — only occupied/free is meaningful (e.g. a Component row). Colour by occupancy.</summary>
    Slot = 0,

    /// <summary>A container-like chunk with an intra-chunk fill (e.g. a Cluster's popcount/N). Colour by <c>ChunkFill</c>.</summary>
    ContainerFill = 1,

    /// <summary>Reserved (Index): a B-tree leaf node.</summary>
    Leaf = 2,

    /// <summary>Reserved (Index): a B-tree internal node.</summary>
    Internal = 3,

    /// <summary>Reserved (Hashmap): a bucket that overflows to a chain.</summary>
    Overflow = 4,

    /// <summary>Reserved: a non-data chunk (index directory, hashmap meta/directory) — render hatched, no fill.</summary>
    NonData = 5,
}

/// <summary>Page-cache residency of a page — mirrors the client's <c>DbResidency</c>.</summary>
public enum StorageResidency : byte
{
    /// <summary>The page is not in the page cache — it exists only on disk.</summary>
    OnDiskOnly = 0,

    /// <summary>The page is resident and clean (<c>DirtyCounter == 0</c>).</summary>
    ResidentClean = 1,

    /// <summary>The page is resident and dirty (<c>DirtyCounter &gt; 0</c>) — pending checkpoint.</summary>
    ResidentDirty = 2,
}

/// <summary>
/// The detail tier for one quadtree node (a contiguous page range) — the response of
/// <c>GET /dbmap/region?node=&amp;lod=detail</c>. Each base64 buffer is a per-page SoA array covering
/// <c>[FirstPage, FirstPage + PageCount)</c>.
/// </summary>
public record StorageRegionDetailDto(
    int Node,
    int FirstPage,
    int PageCount,
    /// <summary><c>byte[]</c> — fill ratio 0..255 (chunk occupancy); 0 for non-chunk-based pages.</summary>
    string FillRatio,
    /// <summary><c>int[]</c> — <c>PageBaseHeader.ChangeRevision</c> per page.</summary>
    string ChangeRevision,
    /// <summary><c>byte[]</c> — <see cref="StorageCrcStatus"/> per page.</summary>
    string CrcStatus,
    /// <summary><c>byte[]</c> — <see cref="StorageResidency"/> per page.</summary>
    string Residency,
    /// <summary><c>ushort[]</c> — allocated chunk count per page.</summary>
    string ChunkUsed,
    /// <summary><c>ushort[]</c> — total chunk capacity per page.</summary>
    string ChunkTotal,
    /// <summary>Highest <c>ChangeRevision</c> in this tile — the region-relative write-age ramp anchor.</summary>
    int MaxChangeRevision,
    /// <summary><c>byte[]</c> — Shannon entropy 0..255 per page (decode-free; 0 for free pages).</summary>
    string Entropy,
    /// <summary><c>byte[]</c> — dominant byte class per page (0 zero · 1 0xFF · 2 ASCII · 3 binary).</summary>
    string ByteClass,
    /// <summary>
    /// True when the map is down-sampled (§5.5): each entry's detail comes from one representative page sampled
    /// per <see cref="SampleStride"/>-page cell, not an exact per-page read. The client labels the encoding.
    /// </summary>
    bool Approximate,
    /// <summary>Pages per coarse cell — the down-sample factor; 1 when the map is exact (one entry per page).</summary>
    int SampleStride);

/// <summary>One fully-decoded page — the response of <c>GET /dbmap/page/{idx}</c>.</summary>
public record StoragePageDetailDto(
    int PageIndex,
    long ByteOffset,
    string PageType,
    int OwnerSegmentId,
    string OwnerSegmentKind,
    int ChangeRevision,
    int FormatRevision,
    int ModificationCounter,
    uint StoredChecksum,
    uint LiveChecksum,
    string CrcStatus,
    string Residency,
    int DirtyCounter,
    int ChunkUsed,
    int ChunkTotal,
    double FillRatio,
    /// <summary>Global chunk id of chunk 0 on this page — the client derives an L4 chunk ref as <c>FirstChunkId + i</c>.</summary>
    int FirstChunkId,
    /// <summary><c>byte[]</c> (base64) — one byte per chunk, 1 = allocated; empty for non-chunk-based pages.</summary>
    string ChunkOccupancy,
    /// <summary>Decoded directory entries when this page is a logical-segment root; empty otherwise.</summary>
    StorageContentCellDto[] DirectoryEntries,
    /// <summary>
    /// <c>byte[]</c> (base64) — per-chunk intra-chunk fill 0..255 for container kinds (e.g. cluster popcount/N). Empty when the page's segment has no
    /// container-fill signal (Component / non-chunk pages); the renderer then falls back to binary occupancy.
    /// </summary>
    string ChunkFill = "",
    /// <summary><c>byte[]</c> (base64) — per-chunk <c>ChunkClass</c> byte (0 slot · 1 containerFill · reserved 2 leaf · 3 internal · 4 overflow · 5 nonData). Empty when not populated.</summary>
    string ChunkClass = "",
    /// <summary>
    /// <c>byte[]</c> (base64) — occupancy region-map: allocated fraction 0..255 per governed sub-range cell. Empty unless this is an occupancy page (the
    /// densest pages in the file — each governs a contiguous file-page range; see <see cref="OccupancyFirstPage"/>).
    /// </summary>
    string OccupancyMap = "",
    /// <summary>First file page governed by this occupancy page (0 unless this is an occupancy page).</summary>
    long OccupancyFirstPage = 0,
    /// <summary>Count of file pages governed by this occupancy page (0 unless this is an occupancy page).</summary>
    int OccupancyGovernedCount = 0,
    /// <summary>Column count of the occupancy region-map grid (0 unless this is an occupancy page).</summary>
    int OccupancyGridCols = 0,
    /// <summary>
    /// The fixed per-page header (base + metadata), e.g. 192 B — the engine overhead every chunk-based page carries
    /// (0 for non-chunk pages). The renderer reserves this as the first overhead band.
    /// </summary>
    int HeaderBytes = 0,
    /// <summary>
    /// The logical-segment index/directory section that *only the root page* carries (the page-index table for the
    /// following pages), 0 for non-root/non-chunk pages. This is the chief reason a segment's root page holds fewer
    /// chunks than its later pages; the renderer draws it as a distinct hatched band.
    /// </summary>
    int DirectoryBytes = 0,
    /// <summary>
    /// Stride-alignment padding between the header/directory and chunk 0 — wasted bytes the engine inserts so chunks
    /// start at a stride-aligned page offset (larger for large strides). 0 when the stride already divides the header.
    /// The renderer marks it as dead space (X-crosshatch), distinct from real overhead, so the surface stays honest.
    /// </summary>
    int PaddingBytes = 0);

/// <summary>One segment's directory — the response of <c>GET /dbmap/segment/{id}</c>.</summary>
public record StorageSegmentDetailDto(
    int Id,
    int RootPageIndex,
    string Kind,
    int PageCount,
    int Stride,
    int ChunkCountPerPage,
    int TotalChunkCapacity,
    int[] Pages);

/// <summary>
/// A segment's harvest summary — the response of <c>GET /dbmap/segment/{id}/summary</c> (Module 15, A6). Surfaces the engine's already-computed,
/// per-segment stats: chunk allocation for every chunk-based segment, plus kind-specific extras for clusters (entity-level fill) and entity-maps (the
/// linear-hash distribution). Fetched <b>lazily</b> — only when the card opens — because the entity-map stats walk every bucket + overflow chain.
/// </summary>
public record StorageSegmentSummaryDto(
    int Id,
    int RootPageIndex,
    string Kind,
    int PageCount,
    int Stride,
    /// <summary>Live allocated chunk count (chunk-based segments; 0 otherwise).</summary>
    int AllocatedChunkCount,
    /// <summary>Free chunk count = capacity − allocated (chunk-based segments; 0 otherwise).</summary>
    int FreeChunkCount,
    /// <summary>Provisioned chunk capacity (chunk-based segments; 0 otherwise).</summary>
    int ChunkCapacity,
    /// <summary>Cluster archetype's live entity count; 0 when the segment is not a cluster.</summary>
    long EntityCount = 0,
    /// <summary>Active (non-empty) cluster chunks; 0 when the segment is not a cluster.</summary>
    int ActiveClusterCount = 0,
    /// <summary>Slots per cluster; 0 when the segment is not a cluster.</summary>
    int ClusterSize = 0,
    /// <summary>Entity-map linear-hash stats; <c>null</c> unless the segment is an entity-map.</summary>
    EntityMapStatsDto EntityMap = null);

/// <summary>Linear-hash distribution stats for an archetype's entity-map (a projection of the engine's <c>EntityMapStats</c>).</summary>
public record EntityMapStatsDto(
    int BucketCount,
    long EntryCount,
    int OverflowBucketCount,
    int MaxChainLength,
    double LoadFactor,
    int FillEmpty,
    int FillQuarter,
    int FillHalf,
    int FillThreeQuarter,
    int FillFull);

/// <summary>The decoded L4 content of one chunk — the response of <c>GET /dbmap/chunk/{segId}/{chunkId}</c>.</summary>
public record StorageChunkDto(
    int SegmentId,
    int ChunkId,
    /// <summary>Which decoder produced <see cref="Cells"/>: <c>component</c> / <c>directory</c> / <c>generic</c> / <c>unknown</c>.</summary>
    string Decoder,
    bool Occupied,
    long ByteOffset,
    int Size,
    /// <summary>Component type name when <see cref="Decoder"/> is <c>component</c>; empty otherwise.</summary>
    string ComponentType,
    StorageContentCellDto[] Cells,
    /// <summary>
    /// Slot-ordered component names for a <c>cluster</c> chunk — index <c>c</c> is bit <c>c</c> of each cell's <c>EnabledMask</c>. Drives the client's
    /// per-component enabled-state overlay picker (A6). Empty for every non-cluster decoder.
    /// </summary>
    string[] ClusterComponents = null);

/// <summary>
/// One decoded content cell — a component field, a directory entry, or a generic byte run. <c>ColorKey</c> is a
/// stable hashable id (field id, entry index, byte class) the client maps to a hue for L4 archetype/field
/// coloring.
/// </summary>
public record StorageContentCellDto(
    string Label,
    string Value,
    string Kind,
    int Offset,
    int Size,
    int ColorKey,
    /// <summary>
    /// Per-slot enabled-component bitmask for cluster <c>entitySlot</c> cells: bit <c>c</c> set ⇒ component slot <c>c</c> is enabled for the entity in this
    /// slot. 0 for every non-cluster cell. Lets the client render the per-component overlay and per-slot tooltip from one decode (no round-trip on selection).
    /// </summary>
    long EnabledMask = 0);
