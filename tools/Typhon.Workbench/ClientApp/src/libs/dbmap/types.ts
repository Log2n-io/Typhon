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
}
