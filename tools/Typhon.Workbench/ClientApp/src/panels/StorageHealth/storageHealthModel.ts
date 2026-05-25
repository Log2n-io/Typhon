import type { HealthSegment } from '@/hooks/dbmap/useDbMapHealth';

/** Sortable columns of the Storage Health per-segment table (PC-5). */
export type HealthSortKey =
  | 'id'
  | 'kind'
  | 'typeName'
  | 'pageCount'
  | 'occupancyPct'
  | 'chunkFillPct'
  | 'reclaimableBytes'
  | 'entityCount';

/**
 * Sort the per-segment rows by a column. Numeric columns sort numerically, string columns lexically. Returns a
 * new array (never mutates). `desc` puts the worst offenders (highest occupancy / most reclaimable) on top.
 */
export function sortHealthSegments(segments: HealthSegment[], key: HealthSortKey, dir: 'asc' | 'desc'): HealthSegment[] {
  const sign = dir === 'desc' ? -1 : 1;
  return [...segments].sort((a, b) => {
    const av = a[key];
    const bv = b[key];
    const cmp = typeof av === 'number' && typeof bv === 'number' ? av - bv : String(av).localeCompare(String(bv));
    return cmp * sign;
  });
}
