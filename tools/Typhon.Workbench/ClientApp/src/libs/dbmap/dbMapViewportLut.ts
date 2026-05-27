// Per-frame viewport page-index lookup table — pure helper extracted from {@link DbMapRenderer.precomputeViewportLut}
// so the inner fill loop is unit-testable without instantiating a canvas / DOM.
//
// The renderer's hot path was dominated by `hilbertXY2D` at 55.5% self-time because every L1 annotation pass scanned
// the visible cell rect and called `xyToPage` per cell, with the same (cx, cy) being converted 3-5 times across the
// passes. This module's `fillViewportPageLut` converts the rect ONCE; callers then read the LUT by index for O(1).

import type { DbMapPageOrder } from './types';
import { xyToPage } from './hilbert';

/** Rectangle of grid cells in `(cx0..cx1, cy0..cy1)` inclusive form — same shape `visibleCellRect()` returns. */
export interface CellRectInclusive {
  cx0: number;
  cy0: number;
  cx1: number;
  cy1: number;
}

/**
 * Fills a flat, row-major `Int32Array` with the page index for every cell in `rect`. Off-file cells (cell maps to
 * a page index outside `[0, pageCount)`) store the sentinel value -1; this lets the caller do a single sentinel
 * check instead of dual range tests on each lookup.
 *
 * **Pure** — writes into the provided buffer, no allocation, deterministic. The output buffer's length MUST be at
 * least `(cx1 - cx0 + 1) * (cy1 - cy0 + 1)`; only that prefix is touched. Returns the LUT's logical width / height
 * so callers can verify the buffer size + derive their own index math.
 *
 * Element `(cx, cy)` (for `cx0 ≤ cx ≤ cx1`, `cy0 ≤ cy ≤ cy1`) lives at `out[(cy - cy0) * w + (cx - cx0)]`.
 */
export function fillViewportPageLut(
  out: Int32Array,
  rect: CellRectInclusive,
  order: number,
  pageCount: number,
  mode: DbMapPageOrder,
): { w: number; h: number } {
  const w = rect.cx1 - rect.cx0 + 1;
  const h = rect.cy1 - rect.cy0 + 1;
  let idx = 0;
  for (let cy = rect.cy0; cy <= rect.cy1; cy++) {
    for (let cx = rect.cx0; cx <= rect.cx1; cx++) {
      const page = xyToPage(order, mode, cx, cy);
      out[idx++] = page >= 0 && page < pageCount ? page : -1;
    }
  }
  return { w, h };
}

/**
 * O(1) lookup helper paired with {@link fillViewportPageLut}. Returns the page index at cell `(cx, cy)`, or -1 if
 * outside the LUT rect or stored as -1 (off-file). Mirrors the renderer's private `lutPageAt` so external tests
 * can exercise the exact same lookup discipline.
 */
export function lutPageAt(
  lut: Int32Array,
  rect: CellRectInclusive,
  w: number,
  h: number,
  cx: number,
  cy: number,
): number {
  const ix = cx - rect.cx0;
  const iy = cy - rect.cy0;
  if (ix < 0 || iy < 0 || ix >= w || iy >= h) {
    return -1;
  }
  return lut[iy * w + ix];
}
