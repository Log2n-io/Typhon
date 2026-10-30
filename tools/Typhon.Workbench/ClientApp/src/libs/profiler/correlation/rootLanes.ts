import { percentile, type RequestRow } from './requests';

/**
 * The time area's root lanes (#1517): one lane of HTTP requests, one of client commands, each root a bar packed into the first row free at its start.
 * Concurrent roots stack; past the lane's row cap they share the last one (drawn translucent, with a count) instead of growing the lane without bound.
 * The cap follows the lane's display mode: {@link ROOT_ROWS_EXPANDED} expanded, {@link ROOT_ROWS_DOUBLE} in the extended (`double`) mode. A load of
 * thousands of clients puts hundreds of commands in flight at once, more than any lane can show row by row: the Requests panel's filter narrows what
 * is packed, and the collapsed lane draws their density instead.
 */

export const ROOT_ROWS_EXPANDED = 4;
export const ROOT_ROWS_DOUBLE = 32;

/** Half the width of a dropped command's notch, in pixels: the hit test uses the same tolerance, so what is drawn is what can be hovered. */
export const DROPPED_MARK_HALF_WIDTH = 4;

export const ROOT_HTTP_TRACK_ID = 'roots-http';
export const ROOT_COMMANDS_TRACK_ID = 'roots-commands';

export const isRootLaneId = (id: string): boolean => id === ROOT_HTTP_TRACK_ID || id === ROOT_COMMANDS_TRACK_ID;

/** Rows a root lane may use in a display mode; a collapsed lane draws a density strip, packed as if expanded. */
export const rootRowCap = (state: 'summary' | 'expanded' | 'double' | undefined): number => (state === 'double' ? ROOT_ROWS_DOUBLE : ROOT_ROWS_EXPANDED);

export interface PackedRoot {
  row: RequestRow;
  /** Row inside its lane, 0-based, below the lane's row cap. */
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
  /** Rows the lane needs, 1..its row cap; 0 when the lane has no root. */
  rowCount: number;
  overflowCount: number;
  /** The 99th percentile of the lane's root durations, dropped commands aside: the density strip marks the columns holding a slower root. */
  p99Us: number;
}

export interface RootLanes {
  http: RootLane;
  commands: RootLane;
}

const EMPTY_LANE: RootLane = { roots: [], rows: [], rowCount: 0, overflowCount: 0, p99Us: 0 };

export const EMPTY_ROOT_LANES: RootLanes = { http: EMPTY_LANE, commands: EMPTY_LANE };

/** Packs the roots into their two lanes, each within its row cap. Input order does not matter. */
export function packRootLanes(
  rows: readonly RequestRow[], caps: { http: number; commands: number } = { http: ROOT_ROWS_EXPANDED, commands: ROOT_ROWS_EXPANDED },
): RootLanes {
  const http: RequestRow[] = [];
  const commands: RequestRow[] = [];
  for (const r of rows) (r.kind === 'http' ? http : commands).push(r);
  return { http: packLane(http, caps.http), commands: packLane(commands, caps.commands) };
}

/**
 * Each root goes to the lowest row free at its start. Two heaps keep that at log(rows) a root instead of a scan of every row: the busy rows by end,
 * and the freed rows by index. A busy entry is stale once its row has been given a later end (the overflow row), and skipped when popped.
 */
function packLane(rows: RequestRow[], maxRows: number): RootLane {
  if (rows.length === 0) return EMPTY_LANE;
  rows.sort((a, b) => a.startUs - b.startUs || b.endUs - a.endUs);
  const rowEnds: number[] = [];
  const busy = new MinHeap<{ end: number; row: number }>((a, b) => a.end - b.end || a.row - b.row);
  const free = new MinHeap<number>((a, b) => a - b);
  const roots: PackedRoot[] = [];
  const durations: number[] = [];
  let overflowCount = 0;
  for (const row of rows) {
    if (row.kind === 'dropped') {
      roots.push({ row, lane: 0, overflow: false });
      continue;
    }
    durations.push(row.totalUs);
    while (busy.size > 0 && busy.peek().end <= row.startUs) {
      const done = busy.pop();
      if (rowEnds[done.row] === done.end) free.push(done.row);
    }
    let lane: number;
    let overflow = false;
    if (free.size > 0) {
      lane = free.pop();
      rowEnds[lane] = row.endUs;
    } else if (rowEnds.length < maxRows) {
      lane = rowEnds.length;
      rowEnds.push(row.endUs);
    } else {
      lane = maxRows - 1;
      overflow = true;
      overflowCount++;
      if (row.endUs <= rowEnds[lane]) {
        roots.push({ row, lane, overflow });
        continue;
      }
      rowEnds[lane] = row.endUs;
    }
    busy.push({ end: rowEnds[lane], row: lane });
    roots.push({ row, lane, overflow });
  }
  durations.sort((a, b) => a - b);
  const p99Us = percentile(durations, 0.99);

  const rowCount = Math.max(1, rowEnds.length);
  const index: RootRowIndex[] = Array.from({ length: rowCount }, () => ({ idx: [], endMax: [] }));
  for (let i = 0; i < roots.length; i++) {
    const r = index[roots[i].lane];
    const prev = r.endMax.length > 0 ? r.endMax[r.endMax.length - 1] : -Infinity;
    r.idx.push(i);
    r.endMax.push(Math.max(prev, roots[i].row.endUs));
  }
  return { roots, rows: index, rowCount, overflowCount, p99Us };
}

/** A binary min-heap over `compare`. */
class MinHeap<T> {
  private readonly items: T[] = [];

  constructor(private readonly compare: (a: T, b: T) => number) {}

  get size(): number {
    return this.items.length;
  }

  peek(): T {
    return this.items[0];
  }

  push(item: T): void {
    const a = this.items;
    a.push(item);
    let i = a.length - 1;
    while (i > 0) {
      const parent = (i - 1) >> 1;
      if (this.compare(a[i], a[parent]) >= 0) break;
      [a[i], a[parent]] = [a[parent], a[i]];
      i = parent;
    }
  }

  pop(): T {
    const a = this.items;
    const top = a[0];
    const last = a.pop() as T;
    if (a.length > 0) {
      a[0] = last;
      let i = 0;
      for (;;) {
        const l = 2 * i + 1;
        const r = l + 1;
        let m = i;
        if (l < a.length && this.compare(a[l], a[m]) < 0) m = l;
        if (r < a.length && this.compare(a[r], a[m]) < 0) m = r;
        if (m === i) break;
        [a[i], a[m]] = [a[m], a[i]];
        i = m;
      }
    }
    return top;
  }
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
