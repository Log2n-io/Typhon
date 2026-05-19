// Client-side analysis metrics for the Database File Map (Module 15, A3, §4.3).
//
// Every metric is derived from the StructuralMap the client already holds — no new server endpoint (the A3
// design decision). Pure functions, unit-tested against crafted fixtures. Two precision tiers: the coarse
// fragmentation / free-space metrics need only the in-memory descriptors; fill density and reclaimable bytes
// need the per-page detail tiles and therefore report how many of a segment's pages were actually sampled.

import { DbPageType, type DbDetailTile, type DbMapData } from './types';

/**
 * Fragmentation %: the fraction of a segment's directory-ordered pages whose physical successor is not the
 * next page on disk. 0 = perfectly contiguous, 1 = every page scattered (SQLite's "non-sequential pages",
 * PostgreSQL's `leaf_fragmentation`). `orderedPages` must be the segment directory order — logical index i at
 * `orderedPages[i]`.
 */
export function fragmentationPercent(orderedPages: readonly number[]): number {
  if (orderedPages.length < 2) {
    return 0;
  }
  let breaks = 0;
  for (let i = 0; i < orderedPages.length - 1; i++) {
    if (orderedPages[i + 1] !== orderedPages[i] + 1) {
      breaks++;
    }
  }
  return breaks / (orderedPages.length - 1);
}

/** Contiguous-run lengths of a directory-ordered page list — the raw data behind the fragmentation histogram. */
export function contiguousRuns(orderedPages: readonly number[]): number[] {
  if (orderedPages.length === 0) {
    return [];
  }
  const runs: number[] = [];
  let len = 1;
  for (let i = 1; i < orderedPages.length; i++) {
    if (orderedPages[i] === orderedPages[i - 1] + 1) {
      len++;
    } else {
      runs.push(len);
      len = 1;
    }
  }
  runs.push(len);
  return runs;
}

/** A detail-tier metric, paired with the sample count so the UI can flag a partially-scanned segment. */
export interface SampledMetric {
  /** The metric value (a 0..1 ratio or a byte count, per the function). */
  value: number;
  /** How many of the segment's pages had a resident detail tile — the rest are not yet scanned. */
  sampledPages: number;
}

/** Looks up a page's detail-tile slot, or null when the covering tile is not resident. */
function tileSlot(
  page: number,
  tiles: Map<number, DbDetailTile>,
  detailTileSize: number,
): { tile: DbDetailTile; i: number } | null {
  const tile = tiles.get(Math.floor(page / detailTileSize));
  if (!tile) {
    return null;
  }
  const i = page - tile.firstPage;
  return i >= 0 && i < tile.pageCount ? { tile, i } : null;
}

/**
 * Fill density: live ÷ allocated chunk slots across a segment's pages, 0..1. Computed only over pages whose
 * detail tile is resident — `sampledPages` reports the coverage so a half-scanned segment is not mistaken for
 * a low-fill one.
 */
export function fillDensity(
  pages: readonly number[],
  tiles: Map<number, DbDetailTile>,
  detailTileSize: number,
): SampledMetric {
  let used = 0;
  let total = 0;
  let sampled = 0;
  for (const page of pages) {
    const slot = tileSlot(page, tiles, detailTileSize);
    if (!slot) {
      continue;
    }
    used += slot.tile.chunkUsed[slot.i];
    total += slot.tile.chunkTotal[slot.i];
    sampled++;
  }
  return { value: total > 0 ? used / total : 0, sampledPages: sampled };
}

/**
 * Estimated reclaimable bytes for a segment — the free chunk slots across its sampled pages × the chunk
 * stride. This is the dominant, honestly-computable term of design §4.3's reclaimable estimate; finer
 * intra-chunk slack needs per-component sizing the client does not hold, so the UI labels the figure
 * "estimated". 0 for a non-chunk-based segment (`stride` 0).
 */
export function segmentReclaimableBytes(
  pages: readonly number[],
  tiles: Map<number, DbDetailTile>,
  detailTileSize: number,
  stride: number,
): SampledMetric {
  let freeSlots = 0;
  let sampled = 0;
  for (const page of pages) {
    const slot = tileSlot(page, tiles, detailTileSize);
    if (!slot) {
      continue;
    }
    freeSlots += Math.max(0, slot.tile.chunkTotal[slot.i] - slot.tile.chunkUsed[slot.i]);
    sampled++;
  }
  return { value: freeSlots * stride, sampledPages: sampled };
}

/** The file-level free-space composition — three parts summing exactly to the data file size (§4.3). */
export interface FreeSpaceComposition {
  totalBytes: number;
  /** Pages holding user data (everything that is neither free nor structural overhead). */
  liveBytes: number;
  /** Root + occupancy + unclassified pages — structural, not user data. */
  overheadBytes: number;
  /** Free (unallocated) pages. */
  freeBytes: number;
}

/** Composes the data file into live / overhead / free bytes — the free-space lens composition bar (§4.3). */
export function freeSpaceComposition(data: DbMapData): FreeSpaceComposition {
  let free = 0;
  let overhead = 0;
  for (let p = 0; p < data.pageCount; p++) {
    const t = data.pageType[p];
    if (t === DbPageType.Free) {
      free++;
    } else if (t === DbPageType.Root || t === DbPageType.Occupancy || t === DbPageType.Unknown) {
      overhead++;
    }
  }
  // Cells map to bytes by their share of the file — exact when the map is exact, and on a down-sampled map
  // (§5.5) this keeps the bar summing to the real file size rather than `cellCount × PAGE_SIZE`.
  const total = data.dataFileBytes;
  const cells = data.pageCount;
  const freeBytes = cells > 0 ? Math.round((free / cells) * total) : 0;
  const overheadBytes = cells > 0 ? Math.round((overhead / cells) * total) : 0;
  return {
    totalBytes: total,
    freeBytes,
    overheadBytes,
    liveBytes: total - freeBytes - overheadBytes,
  };
}
