// Map-derived pathology flags for the Database File Map (Module 15, A3, §4.3).
//
// A3 ships only the flags derivable from the map itself — under-filled chunk-based pages. DC-inflation
// hotspots (#133) and leaked-chunk detection need engine introspection that does not exist today and are
// deferred to a later issue (the A3 scope decision). Flags are computed over resident detail tiles only — a
// page outside the scanned viewport is simply not yet evaluated, never falsely cleared.

import type { DbDetailTile, DbMapData } from './types';

/** Fill ratio (0..1) below which a chunk-based page is flagged under-filled. */
export const LOW_FILL_THRESHOLD = 0.25;

/** One flagged page — a chunk-based page whose chunk occupancy is below {@link LOW_FILL_THRESHOLD}. */
export interface PathologyFlag {
  pageIndex: number;
  ownerSegmentId: number;
  /** The fill ratio (0..1) that triggered the flag. */
  fillRatio: number;
}

/**
 * Finds under-filled chunk-based pages across the resident detail tiles. Pages with no chunks (free / root /
 * index) are never flagged — a low fill ratio is only meaningful where chunk occupancy applies.
 */
export function findUnderFilledPages(
  data: DbMapData,
  tiles: Map<number, DbDetailTile>,
  threshold: number = LOW_FILL_THRESHOLD,
): PathologyFlag[] {
  const flags: PathologyFlag[] = [];
  for (const tile of tiles.values()) {
    for (let i = 0; i < tile.pageCount; i++) {
      const total = tile.chunkTotal[i];
      if (total <= 0) {
        continue;
      }
      const ratio = tile.fillRatio[i] / 255;
      if (ratio < threshold) {
        const page = tile.firstPage + i;
        flags.push({ pageIndex: page, ownerSegmentId: data.ownerSegmentId[page], fillRatio: ratio });
      }
    }
  }
  flags.sort((a, b) => a.fillRatio - b.fillRatio);
  return flags;
}
