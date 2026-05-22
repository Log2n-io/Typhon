// L0 composition reducer for the Database File Map (Module 15, §3.4 / §4.2).
//
// At L0 — the camera fully zoomed out — every page is sub-pixel and the L1 Hilbert image collapses to a flat
// fill. Instead of throwing away that signal, we collapse it to a one-dimensional column of vertical stripes
// whose heights are exactly proportional to byte counts: the L1 image reduced to 1D, honest by construction.
// Stripes morph into the L1 image during the L0→L1 crossfade because every stripe uses the same colour the
// matching L1 cells will use.
//
// Pure functions — no DOM, no renderer state. The renderer caches the result and the panel hit-tests against
// the rects derived from it.
//
// The composition is sourced **only** from the coarse arrays already held client-side (`pageType[]`,
// `ownerSegmentId[]`, `segments[]`). Zero new page-body I/O — respects the §5.1 "L0 costs zero I/O"
// invariant. Detail encodings (`fillDensity` / `writeAge` / `crc` / `residency` / `entropy` / `byteClass`)
// fall back to the pageType composition at L0; the per-page detail-tier colours only materialise during the
// L0→L1 crossfade once detail tiles are resident.

import {
  FREE_RGB,
  PAGE_TYPE_RGB,
  USED_RGB,
  segmentRgb,
  type Rgb,
} from './dbMapColors';
import {
  DbPageType,
  NO_SEGMENT,
  PAGE_SIZE,
  PAGE_TYPE_LABELS,
  type DbMapData,
  type DbMapEncoding,
  type StorageSegmentDto,
} from './types';

/** Up to this many segments are surfaced as individual stripes; the rest collapse into an "Other (N)" stripe. */
export const L0_SEGMENT_TOP_N = 12;

/** What kind of slice a stripe represents — drives the click handler and the tooltip body. */
export type L0StripeKind = 'type' | 'segment' | 'other';

/**
 * One composition stripe — the L0 surface is a vertical stack of these inside the data rect. Heights are
 * proportional to {@link fraction}; colours match the L1 cells of the same slice so the L0→L1 crossfade is
 * continuous.
 */
export interface L0Stripe {
  /** Stable identity — '`type:4`' / '`seg:17`' / '`other`'. Used as the React/cache key. */
  key: string;
  kind: L0StripeKind;
  /** Display label for the stripe body + legend + tooltip. */
  label: string;
  /** [r,g,b] — exactly the colour L1 will paint for this slice's pages. */
  color: Rgb;
  /** Page count (descriptor cells, not real pages) — the unit the data arrays speak. */
  pageCount: number;
  /** Bytes attributed to this stripe — proportional to {@link pageCount} ÷ total cells × `dataFileBytes`. */
  byteCount: number;
  /** Share of the data file, 0..1. The collection sums to exactly 1 (modulo float ε). */
  fraction: number;
  /** Set on `kind: 'type'` — drives the "switch encoding / focus this type" click action. */
  pageType?: DbPageType;
  /** Set on `kind: 'segment'` — drives the "focus this segment" click action. */
  segmentId?: number;
  /** Set on `kind: 'other'` — the segment ids collapsed into the bucket, descending by byteCount. */
  bucketed?: readonly number[];
}

/**
 * The order in which page-type stripes are emitted. Stable across databases so the L0 view of two DBs reads
 * comparably (muscle memory beats one-shot ranking). Free is always last — "what's used sits on top, what's
 * empty pools at the bottom". Unknown sits just above Free so the unclassified band reads near the empty
 * region rather than mixed into the user-data block.
 */
const PAGE_TYPE_STRIPE_ORDER: readonly DbPageType[] = [
  DbPageType.Component,
  DbPageType.Revision,
  DbPageType.Index,
  DbPageType.Spatial,
  DbPageType.Cluster,
  DbPageType.Vsbs,
  DbPageType.StringTable,
  DbPageType.EntityMap,
  DbPageType.System,
  DbPageType.Root,
  DbPageType.Occupancy,
  DbPageType.Unknown,
  DbPageType.Free,
];

/**
 * Reduces the coarse map to its L0 stripe stack under the active encoding. Output ordering / cardinality:
 *
 * - `freeUsed` → 2 stripes (Used first, Free last) — omitting either if its count is 0.
 * - `segment`  → up to {@link L0_SEGMENT_TOP_N} segment stripes (sorted by byteCount desc), then a single
 *               "Other (N)" stripe for the remainder, then the "Unowned" stripe (`ownerSegmentId == NO_SEGMENT`,
 *               same colour as Free) at the bottom. Each is omitted when its count is 0.
 * - `pageType` and every detail encoding → stripes in {@link PAGE_TYPE_STRIPE_ORDER}, omitting zero-count
 *               types; Free is always the last stripe when present.
 *
 * The byte count uses the file-level convention from `freeSpaceComposition`: `(count / cells) * dataFileBytes`,
 * so the stripes sum to exactly the on-disk file size on a down-sampled map (where one descriptor cell
 * represents multiple pages).
 */
export function computeComposition(data: DbMapData, encoding: DbMapEncoding): L0Stripe[] {
  if (data.pageCount === 0) {
    return [];
  }
  if (encoding === 'segment') {
    return composeBySegment(data);
  }
  if (encoding === 'freeUsed') {
    return composeByFreeUsed(data);
  }
  // `pageType` and all detail encodings share the pageType stripe layout at L0 (zero-I/O fallback).
  return composeByPageType(data);
}

function composeByPageType(data: DbMapData): L0Stripe[] {
  const counts = new Int32Array(PAGE_TYPE_RGB.length);
  for (let p = 0; p < data.pageCount; p++) {
    counts[data.pageType[p]]++;
  }
  const stripes: L0Stripe[] = [];
  for (const type of PAGE_TYPE_STRIPE_ORDER) {
    const c = counts[type];
    if (c <= 0) {
      continue;
    }
    stripes.push({
      key: `type:${type}`,
      kind: 'type',
      label: PAGE_TYPE_LABELS[type] ?? 'Unknown',
      color: PAGE_TYPE_RGB[type] ?? PAGE_TYPE_RGB[DbPageType.Unknown],
      pageCount: c,
      byteCount: bytesFor(c, data),
      fraction: c / data.pageCount,
      pageType: type,
    });
  }
  return stripes;
}

function composeByFreeUsed(data: DbMapData): L0Stripe[] {
  let free = 0;
  for (let p = 0; p < data.pageCount; p++) {
    if (data.pageType[p] === DbPageType.Free) {
      free++;
    }
  }
  const used = data.pageCount - free;
  const stripes: L0Stripe[] = [];
  if (used > 0) {
    stripes.push({
      key: 'type:used',
      kind: 'type',
      label: 'Used',
      color: USED_RGB,
      pageCount: used,
      byteCount: bytesFor(used, data),
      fraction: used / data.pageCount,
    });
  }
  if (free > 0) {
    stripes.push({
      key: 'type:free',
      kind: 'type',
      label: 'Free',
      color: FREE_RGB,
      pageCount: free,
      byteCount: bytesFor(free, data),
      fraction: free / data.pageCount,
      pageType: DbPageType.Free,
    });
  }
  return stripes;
}

function composeBySegment(data: DbMapData): L0Stripe[] {
  // Count pages per segment id from the dense per-cell array — the segment list itself doesn't reflect
  // down-sampling, but `ownerSegmentId[]` does.
  const counts = new Map<number, number>();
  let unowned = 0;
  for (let p = 0; p < data.pageCount; p++) {
    const sid = data.ownerSegmentId[p];
    if (sid === NO_SEGMENT) {
      unowned++;
    } else {
      counts.set(sid, (counts.get(sid) ?? 0) + 1);
    }
  }
  const segmentMeta = new Map<number, StorageSegmentDto>();
  for (const s of data.segments) {
    segmentMeta.set(s.id, s);
  }
  // Sort segments by count desc, tie-break by id asc for stability across renders.
  const entries = [...counts.entries()].sort((a, b) => (b[1] - a[1]) || (a[0] - b[0]));
  const topN = entries.slice(0, L0_SEGMENT_TOP_N);
  const rest = entries.slice(L0_SEGMENT_TOP_N);

  const stripes: L0Stripe[] = [];
  for (const [id, c] of topN) {
    stripes.push({
      key: `seg:${id}`,
      kind: 'segment',
      label: segmentDisplayLabel(id, segmentMeta.get(id)),
      color: segmentRgb(id),
      pageCount: c,
      byteCount: bytesFor(c, data),
      fraction: c / data.pageCount,
      segmentId: id,
    });
  }
  if (rest.length > 0) {
    const restCount = rest.reduce((acc, [, c]) => acc + c, 0);
    stripes.push({
      key: 'other',
      kind: 'other',
      label: `Other (${rest.length})`,
      // Neutral muted tone — distinct from any individual segment colour. Matches the byte-class "0xFF" slate.
      color: [148, 163, 184],
      pageCount: restCount,
      byteCount: bytesFor(restCount, data),
      fraction: restCount / data.pageCount,
      bucketed: rest.map(([id]) => id),
    });
  }
  if (unowned > 0) {
    stripes.push({
      key: 'unowned',
      kind: 'segment',
      label: 'Unowned',
      // `pageColorRgb` maps NO_SEGMENT to Free's slate, keeping the L0→L1 crossfade consistent.
      color: PAGE_TYPE_RGB[DbPageType.Free],
      pageCount: unowned,
      byteCount: bytesFor(unowned, data),
      fraction: unowned / data.pageCount,
      segmentId: NO_SEGMENT,
    });
  }
  return stripes;
}

function segmentDisplayLabel(id: number, meta: StorageSegmentDto | undefined): string {
  if (!meta) {
    return `segment #${id}`;
  }
  return meta.typeName.length > 0 ? `${meta.kind} #${meta.id} · ${meta.typeName}` : `${meta.kind} #${meta.id}`;
}

/**
 * Stripe byte attribution: `(cellCount / totalCells) × dataFileBytes`. Matches the convention used by
 * `freeSpaceComposition` so the stripe sum is exactly the on-disk file size — accurate on both exact maps
 * (`downSampleFactor === 1`) and down-sampled maps (each cell stands for `downSampleFactor` pages).
 *
 * Falls back to `cellCount × PAGE_SIZE` when `dataFileBytes` is 0 (synthetic fixtures / tests).
 */
function bytesFor(count: number, data: DbMapData): number {
  if (data.dataFileBytes > 0 && data.pageCount > 0) {
    return Math.round((count / data.pageCount) * data.dataFileBytes);
  }
  return count * PAGE_SIZE * Math.max(1, data.downSampleFactor);
}
