// Database File Map (Module 15, Track A — A1 coarse tier) — shared types.
//
// The DTO interfaces mirror the server records in Dtos/Storage/StorageMapDtos.cs. They are hand-written rather
// than Orval-generated for A1 — the hooks fetch raw (the useTrack.ts pattern) and the coarse map is a tiny,
// stable shape; regenerating schema/openapi.json + Orval is a follow-up once the endpoints settle.

/** One logical segment in the segment table — mirrors `StorageSegmentDto`. */
export interface StorageSegmentDto {
  id: number;
  rootPageIndex: number;
  kind: string;
  pageCount: number;
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

/** The three coarse base encodings selectable in A1. */
export type DbMapEncoding = 'pageType' | 'segment' | 'freeUsed';

/** The fully decoded coarse map the client renders — assembled from the two endpoints. */
export interface DbMapData {
  databaseName: string;
  dataFileBytes: number;
  pageCount: number;
  walBytes: number;
  hilbertOrder: number;
  checkpointLsn: number;
  segments: StorageSegmentDto[];
  /** Per-page semantic type (one `DbPageType` ordinal per page). */
  pageType: Uint8Array;
  /** Per-page dense owning-segment id (`NO_SEGMENT` when unowned). */
  ownerSegmentId: Uint16Array;
}

