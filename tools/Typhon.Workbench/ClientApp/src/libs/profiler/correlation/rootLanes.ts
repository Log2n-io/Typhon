import type { RequestRow } from './requests';

/**
 * The time area's root lanes (#1517): one lane of HTTP requests, one of client commands, each root a bar packed into the first row free at its start.
 * Concurrent roots stack; past {@link MAX_ROOT_ROWS} rows they share the last one (drawn translucent) instead of growing the lane without bound.
 */

export const MAX_ROOT_ROWS = 4;

/** Half the width of a dropped command's notch, in pixels: the hit test uses the same tolerance, so what is drawn is what can be hovered. */
export const DROPPED_MARK_HALF_WIDTH = 4;

export const ROOT_HTTP_TRACK_ID = 'roots-http';
export const ROOT_COMMANDS_TRACK_ID = 'roots-commands';

export interface PackedRoot {
  row: RequestRow;
  /** Row inside its lane, 0-based, < {@link MAX_ROOT_ROWS}. */
  lane: number;
  /** True when every row was busy at its start, so it shares the last row with whatever is drawn there. */
  overflow: boolean;
}

/**
 * One row's roots, as indices into {@link RootLane.roots} by start time, with endMax[k] the latest end among the first k + 1. Packing keeps a row's
 * bars disjoint (but for dropped notches and the overflow row), so the first one reaching a time is a binary search away even past a long bar.
 */
export interface RootRowIndex {
  idx: number[];
  endMax: number[];
}

export interface RootLane {
  /** By start time. Dropped commands are zero-length marks on row 0 and take no row. */
  roots: PackedRoot[];
  /** Per row, for the draw and the hit test. */
  rows: RootRowIndex[];
  /** Rows the lane needs, 1..{@link MAX_ROOT_ROWS}; 0 when the lane has no root. */
  rowCount: number;
  overflowCount: number;
}

export interface RootLanes {
  http: RootLane;
  commands: RootLane;
}

const EMPTY_LANE: RootLane = { roots: [], rows: [], rowCount: 0, overflowCount: 0 };

export const EMPTY_ROOT_LANES: RootLanes = { http: EMPTY_LANE, commands: EMPTY_LANE };

/** Packs the roots into their two lanes. Input order does not matter. */
export function packRootLanes(rows: readonly RequestRow[]): RootLanes {
  const http: RequestRow[] = [];
  const commands: RequestRow[] = [];
  for (const r of rows) (r.kind === 'http' ? http : commands).push(r);
  return { http: packLane(http), commands: packLane(commands) };
}

function packLane(rows: RequestRow[]): RootLane {
  if (rows.length === 0) return EMPTY_LANE;
  rows.sort((a, b) => a.startUs - b.startUs || b.endUs - a.endUs);
  const rowEnds: number[] = [];
  const roots: PackedRoot[] = [];
  let overflowCount = 0;
  for (const row of rows) {
    if (row.kind === 'dropped') {
      roots.push({ row, lane: 0, overflow: false });
      continue;
    }
    let lane = -1;
    for (let r = 0; r < rowEnds.length; r++) {
      if (rowEnds[r] <= row.startUs) {
        lane = r;
        break;
      }
    }
    let overflow = false;
    if (lane < 0) {
      if (rowEnds.length < MAX_ROOT_ROWS) {
        lane = rowEnds.length;
        rowEnds.push(row.endUs);
      } else {
        lane = MAX_ROOT_ROWS - 1;
        overflow = true;
        overflowCount++;
        rowEnds[lane] = Math.max(rowEnds[lane], row.endUs);
      }
    } else {
      rowEnds[lane] = row.endUs;
    }
    roots.push({ row, lane, overflow });
  }

  const rowCount = Math.max(1, rowEnds.length);
  const index: RootRowIndex[] = Array.from({ length: rowCount }, () => ({ idx: [], endMax: [] }));
  for (let i = 0; i < roots.length; i++) {
    const r = index[roots[i].lane];
    const prev = r.endMax.length > 0 ? r.endMax[r.endMax.length - 1] : -Infinity;
    r.idx.push(i);
    r.endMax.push(Math.max(prev, roots[i].row.endUs));
  }
  return { roots, rows: index, rowCount, overflowCount };
}

/** Position, in row <paramref name="row"/>'s index, of the first root whose running end reaches <paramref name="us"/>: none before it can show there. */
export function firstRootEndingAfter(lane: RootLane, row: number, us: number): number {
  const endMax = lane.rows[row]?.endMax ?? [];
  let lo = 0;
  let hi = endMax.length;
  while (lo < hi) {
    const mid = (lo + hi) >>> 1;
    if (endMax[mid] < us) lo = mid + 1;
    else hi = mid;
  }
  return lo;
}

/**
 * The root under (us, row), or null. In order of preference: a dropped notch (drawn over the bars), a bar that contains the time — its own row's over
 * an overflow one — and only then a bar within <paramref name="tolUs"/>, for bars too narrow to aim at.
 */
export function findRootAt(lane: RootLane, us: number, row: number, tolUs: number): PackedRoot | null {
  const rowIdx = lane.rows[row]?.idx;
  if (rowIdx === undefined) return null;
  let best: PackedRoot | null = null;
  let bestScore = 0;
  for (let k = firstRootEndingAfter(lane, row, us - tolUs); k < rowIdx.length; k++) {
    const p = lane.roots[rowIdx[k]];
    if (p.row.startUs - tolUs > us) break;
    if (p.row.endUs + tolUs < us) continue;
    const contains = p.row.startUs <= us && us <= p.row.endUs;
    const score = p.row.kind === 'dropped' ? 4 : contains ? (p.overflow ? 2 : 3) : 1;
    if (score > bestScore) {
      best = p;
      bestScore = score;
    }
  }
  return best;
}
