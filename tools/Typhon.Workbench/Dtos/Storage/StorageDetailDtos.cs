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
    string ByteClass);

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
    StorageContentCellDto[] DirectoryEntries);

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
    StorageContentCellDto[] Cells);

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
    int ColorKey);
