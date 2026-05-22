// Even-grid layout math for the Database File Map's L3 chunk grid and L4 content grid (Module 15, §3.4).
// Chunks and content cells are simple square-ish grids inside their parent rect — physical contiguity makes a
// plain grid correct below the page (no Hilbert). Pure, so the layout is unit-testable.

import type { Rect } from './camera';

/** Column count for a near-square grid holding `count` cells. */
export function gridCols(count: number): number {
  return count <= 1 ? 1 : Math.ceil(Math.sqrt(count));
}

/**
 * The sub-rect of a page cell that the chunk grid occupies, after reserving the byte-proportional overhead band at
 * the top — the header, the root-only segment directory, and the stride-alignment padding (all the bytes before
 * chunk 0). Keeping the chunk grid inside this rect (instead of the whole cell) makes the L3 surface map the page's
 * *real* data area, so a root page visibly fits fewer chunks than its later pages. With no overhead it returns
 * `parent` unchanged.
 */
export function chunkAreaRect(parent: Rect, overheadBytes: number, pageSize: number): Rect {
  const top = pageSize > 0 ? (parent.h * Math.max(0, overheadBytes)) / pageSize : 0;
  return { x: parent.x, y: parent.y + top, w: parent.w, h: parent.h - top };
}

/** The world rect of cell `index` in an evenly-tiled `cols × rows` grid inside `parent`. */
export function gridSubRect(parent: Rect, cols: number, rows: number, index: number): Rect {
  const col = index % cols;
  const row = Math.floor(index / cols);
  return {
    x: parent.x + (col / cols) * parent.w,
    y: parent.y + (row / rows) * parent.h,
    w: parent.w / cols,
    h: parent.h / rows,
  };
}
