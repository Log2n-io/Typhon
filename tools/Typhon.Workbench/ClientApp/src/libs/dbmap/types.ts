// Database File Map (Module 15, Track A) — shared types.
//
// The DTO interfaces mirror the server records in Dtos/Storage/*.cs. They are hand-written rather than
// Orval-generated — the map is a small, stable shape; regenerating schema/openapi.json + Orval is a follow-up
// once the endpoints settle. A1 added the coarse tier; A2 adds the detail tier (per-page detail tiles, page /
// segment / chunk decodes).

// ── A1 coarse tier ────────────────────────────────────────────────────────────────────────────────────────

/** One logical segment in the segment table — mirrors `StorageSegmentDto`. */
export interface StorageSegmentDto {
  id: number;
  rootPageIndex: number;
  kind: string;
  pageCount: number;
  /** Component type name when the segment is a component table; empty otherwise. */
  typeName: string;
}

/** Response of `GET /dbmap/regions` — mirrors `StorageRegionsDto`. */
export interface StorageRegionsDto {
  databaseName: string;
  dataFileBytes: number;
  dataFilePageCount: number;
  walBytes: number;
  hilbertOrder: number;
  checkpointLsn: number;
  downSampleFactor: number;
  detailTileSize: number;
  segments: StorageSegmentDto[];
}

/** Response of `GET /dbmap/region` — mirrors `StorageRegionDto`. Per-page arrays are base64 SoA buffers. */
export interface StorageRegionDto {
  node: number;
  lod: string;
  pageCount: number;
  pageTypes: string;
  ownerSegmentIds: string;
  /** Per-page normalized rank (0–255) within the owning segment's directory order — base64 byte SoA. */
  pageRanks: string;
}

// ── A2 detail tier ────────────────────────────────────────────────────────────────────────────────────────

/** Response of `GET /dbmap/region/detail` — mirrors `StorageRegionDetailDto`. Buffers are base64 SoA. */
export interface StorageRegionDetailDto {
  node: number;
  firstPage: number;
  pageCount: number;
  fillRatio: string;
  changeRevision: string;
  crcStatus: string;
  residency: string;
  chunkUsed: string;
  chunkTotal: string;
  maxChangeRevision: number;
  entropy: string;
  byteClass: string;
  /** True when the map is down-sampled — each entry's detail is sampled from one representative page (§5.5). */
  approximate: boolean;
  /** Pages per coarse cell — the down-sample factor; 1 when the map is exact. */
  sampleStride: number;
}

/** One decoded content cell — mirrors `StorageContentCellDto`. */
export interface StorageContentCellDto {
  label: string;
  value: string;
  kind: string;
  offset: number;
  size: number;
  colorKey: number;
  /** Per-slot enabled-component bitmask for cluster `entitySlot` cells; 0/absent for other cells. */
  enabledMask?: number;
}

/** Response of `GET /dbmap/page/{idx}` — mirrors `StoragePageDetailDto`. */
export interface StoragePageDetailDto {
  pageIndex: number;
  byteOffset: number;
  pageType: string;
  ownerSegmentId: number;
  ownerSegmentKind: string;
  changeRevision: number;
  formatRevision: number;
  modificationCounter: number;
  storedChecksum: number;
  liveChecksum: number;
  crcStatus: string;
  residency: string;
  dirtyCounter: number;
  chunkUsed: number;
  chunkTotal: number;
  fillRatio: number;
  firstChunkId: number;
  chunkOccupancy: string;
  directoryEntries: StorageContentCellDto[];
  /** `byte[]` (base64) — per-chunk intra-chunk fill 0..255 for container kinds (e.g. cluster). Empty otherwise. */
  chunkFill?: string;
  /** `byte[]` (base64) — per-chunk class (0 slot · 1 containerFill · …). Empty when not populated. */
  chunkClass?: string;
  /** `byte[]` (base64) — occupancy region-map: allocated fraction 0..255 per governed sub-range cell. Occupancy pages only. */
  occupancyMap?: string;
  /** First file page governed by this occupancy page (0 unless occupancy). */
  occupancyFirstPage?: number;
  /** Count of file pages governed by this occupancy page (0 unless occupancy). */
  occupancyGovernedCount?: number;
  /** Column count of the occupancy region-map grid (0 unless occupancy). */
  occupancyGridCols?: number;
  /** Fixed per-page header bytes (base + metadata) on a chunk-based page; 0 otherwise. */
  headerBytes?: number;
  /** Logical-segment directory/index bytes the root page carries (page-index table); 0 for non-root/non-chunk. */
  directoryBytes?: number;
  /** Stride-alignment padding before chunk 0 (dead space the engine inserts to stride-align chunks); 0 when none. */
  paddingBytes?: number;
}

/** Response of `GET /dbmap/segment/{id}` — mirrors `StorageSegmentDetailDto`. */
export interface StorageSegmentDetailDto {
  id: number;
  rootPageIndex: number;
  kind: string;
  pageCount: number;
  stride: number;
  chunkCountPerPage: number;
  totalChunkCapacity: number;
  pages: number[];
}

/** Linear-hash distribution stats for an archetype's entity-map — mirrors `EntityMapStatsDto`. */
export interface EntityMapStatsDto {
  bucketCount: number;
  entryCount: number;
  overflowBucketCount: number;
  maxChainLength: number;
  loadFactor: number;
  fillEmpty: number;
  fillQuarter: number;
  fillHalf: number;
  fillThreeQuarter: number;
  fillFull: number;
}

/** Response of `GET /dbmap/segment/{id}/summary` — mirrors `StorageSegmentSummaryDto` (A6 harvest card). */
export interface StorageSegmentSummaryDto {
  id: number;
  rootPageIndex: number;
  kind: string;
  pageCount: number;
  stride: number;
  allocatedChunkCount: number;
  freeChunkCount: number;
  chunkCapacity: number;
  /** Cluster archetype's live entity count; 0 when not a cluster. */
  entityCount: number;
  /** Active (non-empty) cluster chunks; 0 when not a cluster. */
  activeClusterCount: number;
  /** Slots per cluster; 0 when not a cluster. */
  clusterSize: number;
  /** Entity-map linear-hash stats; null unless the segment is an entity-map. */
  entityMap: EntityMapStatsDto | null;
}

/** Response of `GET /dbmap/chunk/{segId}/{chunkId}` — mirrors `StorageChunkDto`. */
export interface StorageChunkDto {
  segmentId: number;
  chunkId: number;
  decoder: string;
  occupied: boolean;
  byteOffset: number;
  size: number;
  componentType: string;
  cells: StorageContentCellDto[];
  /** Slot-ordered component names for a `cluster` chunk (index = `enabledMask` bit); null/empty otherwise. */
  clusterComponents: string[] | null;
}

// ── Enums (ordinals mirror the engine / server) ───────────────────────────────────────────────────────────

/** Semantic page type — ordinals mirror the engine's `StoragePageType` enum. */
export enum DbPageType {
  Unknown = 0,
  Free = 1,
  Root = 2,
  Occupancy = 3,
  Component = 4,
  Revision = 5,
  Index = 6,
  Cluster = 7,
  Vsbs = 8,
  StringTable = 9,
  Spatial = 10,
  EntityMap = 11,
  System = 12,
}

/** Live CRC verdict — ordinals mirror the server's `StorageCrcStatus`. */
export enum DbCrcStatus {
  Unverified = 0,
  Verified = 1,
  Failed = 2,
}

/** Page-cache residency — ordinals mirror the server's `StorageResidency`. */
export enum DbResidency {
  OnDiskOnly = 0,
  ResidentClean = 1,
  ResidentDirty = 2,
}

/** Per-chunk class for the L3 grid — ordinals mirror the server's `StorageChunkClass` (A6). */
export enum DbChunkClass {
  Slot = 0,
  ContainerFill = 1,
  Leaf = 2,
  Internal = 3,
  Overflow = 4,
  NonData = 5,
}

/** Human-readable label per page type, indexed by ordinal. */
export const PAGE_TYPE_LABELS: readonly string[] = [
  'Unknown',
  'Free',
  'Root',
  'Occupancy',
  'Component',
  'Revision',
  'Index',
  'Cluster',
  'VSBS',
  'String table',
  'Spatial',
  'Entity map',
  'System',
];

/** Sentinel owner-segment id for a page owned by no segment. */
export const NO_SEGMENT = 0xffff;

/** Bytes per file page. */
export const PAGE_SIZE = 8192;

// ── Encodings ─────────────────────────────────────────────────────────────────────────────────────────────

/** The coarse (in-memory) base encodings — A1. */
export type DbMapCoarseEncoding = 'pageType' | 'segment' | 'freeUsed';

/** The detail-tier base encodings — A2 (fill / write-age / CRC / residency) + A3 (decode-free entropy / byte-class). */
export type DbMapDetailEncoding = 'fillDensity' | 'writeAge' | 'crc' | 'residency' | 'entropy' | 'byteClass';

/** The active base encoding — coarse or detail. */
export type DbMapEncoding = DbMapCoarseEncoding | DbMapDetailEncoding;

/** Whether an encoding needs the detail tier (page bodies) rather than the free in-memory coarse map. */
export function isDetailEncoding(encoding: DbMapEncoding): encoding is DbMapDetailEncoding {
  return (
    encoding === 'fillDensity' ||
    encoding === 'writeAge' ||
    encoding === 'crc' ||
    encoding === 'residency' ||
    encoding === 'entropy' ||
    encoding === 'byteClass'
  );
}

/** The active analytical lens (A3, §4.3) — a focused dim/highlight overlay on top of the base encoding. */
export type DbMapLens = 'none' | 'fragmentation' | 'freeSpace' | 'pathology';

/**
 * How file pages are laid out on the 2D grid. `hilbert` (default) preserves byte-offset locality in 2D — adjacent
 * pages stay adjacent on the curve. `sequential` is plain row-major (left-to-right, top-to-bottom) on the same
 * square grid: page order reads naturally but loses 2D locality. Both reuse the same `2^order × 2^order` square.
 */
export type DbMapPageOrder = 'hilbert' | 'sequential';

// ── Decoded client-side structures ────────────────────────────────────────────────────────────────────────

/** The fully decoded coarse map the renderer consumes — assembled from the two A1 endpoints. */
export interface DbMapData {
  databaseName: string;
  dataFileBytes: number;
  /**
   * Descriptor cell count — the length of {@link pageType} / {@link ownerSegmentId} and the Hilbert grid size.
   * Equals the real page count when exact; on a down-sampled map (§5.5) it is `ceil(realPages / downSampleFactor)`.
   */
  pageCount: number;
  /** Pages per descriptor cell — a power of 4; 1 when the map is exact (one cell per page). */
  downSampleFactor: number;
  walBytes: number;
  hilbertOrder: number;
  checkpointLsn: number;
  detailTileSize: number;
  segments: StorageSegmentDto[];
  /** Per-cell semantic type (one `DbPageType` ordinal per cell — the dominant type when down-sampled). */
  pageType: Uint8Array;
  /** Per-cell dense owning-segment id (`NO_SEGMENT` when unowned). */
  ownerSegmentId: Uint16Array;
  /** Per-cell normalized rank (0–255) within the owning segment's directory order — drives the segment encoding's luminosity. */
  pageRank: Uint8Array;
}

/** One decoded detail tile — a contiguous page range, decoded from `StorageRegionDetailDto`. */
export interface DbDetailTile {
  node: number;
  firstPage: number;
  pageCount: number;
  fillRatio: Uint8Array;
  changeRevision: Int32Array;
  crcStatus: Uint8Array;
  residency: Uint8Array;
  chunkUsed: Uint16Array;
  chunkTotal: Uint16Array;
  maxChangeRevision: number;
  /** Per-page Shannon entropy 0..255 (decode-free). */
  entropy: Uint8Array;
  /** Per-page dominant byte class (0 zero · 1 0xFF · 2 ASCII · 3 binary). */
  byteClass: Uint8Array;
  /** True when this tile's detail was sampled from representative pages on a down-sampled map (§5.5). */
  approximate: boolean;
  /** Pages per cell — the down-sample factor; 1 when exact. */
  sampleStride: number;
}

/** One decoded content cell. */
export interface DbContentCell {
  label: string;
  value: string;
  kind: string;
  offset: number;
  size: number;
  colorKey: number;
  /** Per-slot enabled-component bitmask for cluster `entitySlot` cells; 0/absent otherwise. */
  enabledMask?: number;
}

/** A page's decoded detail — drives the L3 chunk grid and the page Detail-panel view. */
export interface DbPageDetail {
  pageIndex: number;
  byteOffset: number;
  pageType: string;
  ownerSegmentId: number;
  ownerSegmentKind: string;
  changeRevision: number;
  formatRevision: number;
  modificationCounter: number;
  storedChecksum: number;
  liveChecksum: number;
  crcStatus: string;
  residency: string;
  dirtyCounter: number;
  chunkUsed: number;
  chunkTotal: number;
  fillRatio: number;
  /** Global chunk id of chunk 0 on this page — an L4 chunk ref is `firstChunkId + i`. */
  firstChunkId: number;
  /** One byte per chunk, 1 = allocated. Empty for non-chunk-based pages. */
  chunkOccupancy: Uint8Array;
  directoryEntries: DbContentCell[];
  /** Per-chunk intra-chunk fill 0..255 for container kinds (e.g. cluster popcount/N). Empty/absent otherwise. */
  chunkFill?: Uint8Array;
  /** Per-chunk class byte (0 slot · 1 containerFill · …). Empty/absent when not populated. */
  chunkClass?: Uint8Array;
  /** Occupancy region-map: allocated fraction 0..255 per governed sub-range cell. Occupancy pages only. */
  occupancyMap?: Uint8Array;
  /** First file page governed by this occupancy page. */
  occupancyFirstPage?: number;
  /** Count of file pages governed by this occupancy page. */
  occupancyGovernedCount?: number;
  /** Column count of the occupancy region-map grid. */
  occupancyGridCols?: number;
  /** Fixed per-page header bytes (base + metadata); the renderer reserves this band. */
  headerBytes?: number;
  /** Logical-segment directory/index bytes the root page carries (page-index table); 0 for non-root. */
  directoryBytes?: number;
  /** Stride-alignment padding before chunk 0 — dead space, rendered distinctly from real overhead. */
  paddingBytes?: number;
}

/** A chunk's decoded L4 content. */
export interface DbChunkContent {
  segmentId: number;
  chunkId: number;
  decoder: string;
  occupied: boolean;
  byteOffset: number;
  size: number;
  componentType: string;
  cells: DbContentCell[];
  /** Slot-ordered component names for a cluster chunk — index = `enabledMask` bit. Drives the overlay picker. Empty for non-cluster chunks. */
  clusterComponents: string[];
}
