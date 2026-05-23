// Hilbert-curve mapping for the Database File Map page layout (Module 15, §3.4).
//
// Pages are laid out on a 2^order × 2^order grid by a Hilbert curve so byte-offset locality is preserved in 2D
// and every quadtree node is a contiguous page-index range. The curve position d equals the page index.
//
// A `sequential` (row-major) layout is offered as an alternative ordering on the *same* square grid — see the
// {@link pageToXY} / {@link xyToPage} dispatchers at the bottom, which the renderer routes every page↔cell
// mapping through so a single toolbar setting flips the whole map between curves.

import type { DbMapPageOrder } from './types';

/** Grid side length (cells per axis) for a curve of the given order. */
export function hilbertSide(order: number): number {
  return 1 << order;
}

/** Smallest order whose 2^order × 2^order grid holds `pageCount` cells. */
export function hilbertOrderFor(pageCount: number): number {
  let order = 0;
  let cells = 1;
  while (cells < pageCount) {
    cells <<= 2;
    order++;
  }
  return order;
}

/** Maps a curve position `d` (page index) to its grid cell on an order-`order` Hilbert curve. */
export function hilbertD2XY(order: number, d: number): { x: number; y: number } {
  let x = 0;
  let y = 0;
  let t = d;
  const side = 1 << order;
  for (let s = 1; s < side; s <<= 1) {
    const rx = 1 & (t >> 1);
    const ry = 1 & (t ^ rx);
    if (ry === 0) {
      if (rx === 1) {
        x = s - 1 - x;
        y = s - 1 - y;
      }
      const tmp = x;
      x = y;
      y = tmp;
    }
    x += s * rx;
    y += s * ry;
    t >>= 2;
  }
  return { x, y };
}

/** Maps a grid cell to its curve position `d` (page index) on an order-`order` Hilbert curve. */
export function hilbertXY2D(order: number, x: number, y: number): number {
  let xx = x;
  let yy = y;
  let d = 0;
  for (let s = (1 << order) >> 1; s > 0; s >>= 1) {
    const rx = (xx & s) > 0 ? 1 : 0;
    const ry = (yy & s) > 0 ? 1 : 0;
    d += s * s * ((3 * rx) ^ ry);
    if (ry === 0) {
      if (rx === 1) {
        xx = s - 1 - xx;
        yy = s - 1 - yy;
      }
      const tmp = xx;
      xx = yy;
      yy = tmp;
    }
  }
  return d;
}

// ── Layout dispatchers ─────────────────────────────────────────────────────────────────────────────────────
//
// Row-major (sequential) on the same 2^order × 2^order square: page index → (col, row) and back, in O(1). The
// dispatchers below pick Hilbert or row-major from the active `DbMapPageOrder`; the renderer routes every
// page↔cell mapping through them so the whole map flips with one setting (the grid, camera, fit and detail-tile
// addressing are all ordering-agnostic, so nothing else changes).

/** Maps a page index `d` to its grid cell under the given page ordering, on an order-`order` square grid. */
export function pageToXY(order: number, mode: DbMapPageOrder, d: number): { x: number; y: number } {
  if (mode === 'sequential') {
    const side = 1 << order;
    return { x: d & (side - 1), y: d >> order };
  }
  return hilbertD2XY(order, d);
}

/** Maps a grid cell to its page index under the given page ordering, on an order-`order` square grid. */
export function xyToPage(order: number, mode: DbMapPageOrder, x: number, y: number): number {
  if (mode === 'sequential') {
    return (y << order) + x;
  }
  return hilbertXY2D(order, x, y);
}
