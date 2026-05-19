// Run-length region bands for the Database File Map (Module 15, A3, §3.4 / §4.5).
//
// A single linear pass over the coarse descriptors coalesces contiguous runs of equal (page type, owning
// segment) into labeled bands — both a rendering summary and the "what is using my space" region table. All
// client-side, from the StructuralMap already in hand.

import { NO_SEGMENT, PAGE_SIZE, type DbDetailTile, type DbMapData } from './types';

/** One contiguous run of same-type, same-segment pages — a row of the region table. */
export interface DbMapRegion {
  /** First file page of the run. */
  startPage: number;
  /** Number of pages in the run. */
  pageCount: number;
  /** Run size in bytes (`pageCount × PAGE_SIZE`). */
  byteSize: number;
  /** `DbPageType` ordinal shared by every page of the run. */
  pageType: number;
  /** Owning segment id shared by the run, or `NO_SEGMENT`. */
  ownerSegmentId: number;
  /** Average fill 0..1 over the run's pages whose detail tile is resident, or null when none is. */
  fillAvg: number | null;
}

/** Sort keys for the region table (§4.5). */
export type RegionSortKey = 'start' | 'size' | 'fill' | 'type' | 'fragmentation';

/** Average detail-tile fill over `[start, start + count)`, or null when no covering tile is resident. */
function averageFill(
  start: number,
  count: number,
  tiles: Map<number, DbDetailTile>,
  detailTileSize: number,
): number | null {
  let sum = 0;
  let sampled = 0;
  for (let p = start; p < start + count; p++) {
    const tile = tiles.get(Math.floor(p / detailTileSize));
    if (!tile) {
      continue;
    }
    const i = p - tile.firstPage;
    if (i < 0 || i >= tile.pageCount) {
      continue;
    }
    sum += tile.fillRatio[i] / 255;
    sampled++;
  }
  return sampled > 0 ? sum / sampled : null;
}

/** Coalesces the coarse map into contiguous same-type / same-segment region bands (§3.4). */
export function buildRegions(data: DbMapData, tiles: Map<number, DbDetailTile>): DbMapRegion[] {
  const regions: DbMapRegion[] = [];
  const n = data.pageCount;
  if (n === 0) {
    return regions;
  }
  let runStart = 0;
  const flush = (end: number): void => {
    const count = end - runStart;
    regions.push({
      startPage: runStart,
      pageCount: count,
      // One cell holds `downSampleFactor` pages (§5.5) — scale the run's byte size by it (factor 1 when exact).
      byteSize: count * PAGE_SIZE * data.downSampleFactor,
      pageType: data.pageType[runStart],
      ownerSegmentId: data.ownerSegmentId[runStart],
      fillAvg: averageFill(runStart, count, tiles, data.detailTileSize),
    });
  };
  for (let p = 1; p < n; p++) {
    if (data.pageType[p] !== data.pageType[runStart] || data.ownerSegmentId[p] !== data.ownerSegmentId[runStart]) {
      flush(p);
      runStart = p;
    }
  }
  flush(n);
  return regions;
}

/**
 * Returns a sorted copy of the region list. The `fragmentation` key ranks a region by how shattered its
 * owning segment is — a segment split into many regions floats to the top, surfacing the worst offenders
 * without a per-row metric column. Ties always break on ascending start page for a stable display.
 */
export function sortRegions(
  regions: readonly DbMapRegion[],
  key: RegionSortKey,
  ascending: boolean,
): DbMapRegion[] {
  const regionsPerSegment = new Map<number, number>();
  if (key === 'fragmentation') {
    for (const r of regions) {
      if (r.ownerSegmentId !== NO_SEGMENT) {
        regionsPerSegment.set(r.ownerSegmentId, (regionsPerSegment.get(r.ownerSegmentId) ?? 0) + 1);
      }
    }
  }
  const value = (r: DbMapRegion): number => {
    switch (key) {
      case 'size':
        return r.pageCount;
      case 'fill':
        return r.fillAvg ?? -1;
      case 'type':
        return r.pageType;
      case 'fragmentation':
        return regionsPerSegment.get(r.ownerSegmentId) ?? 0;
      case 'start':
      default:
        return r.startPage;
    }
  };
  const sorted = [...regions];
  sorted.sort((a, b) => {
    const d = value(a) - value(b);
    if (d !== 0) {
      return ascending ? d : -d;
    }
    return a.startPage - b.startPage;
  });
  return sorted;
}
