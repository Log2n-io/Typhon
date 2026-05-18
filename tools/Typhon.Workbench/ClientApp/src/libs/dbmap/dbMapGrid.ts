// Even-grid layout math for the Database File Map's L3 chunk grid and L4 content grid (Module 15, §3.4).
// Chunks and content cells are simple square-ish grids inside their parent rect — physical contiguity makes a
// plain grid correct below the page (no Hilbert). Pure, so the layout is unit-testable.

import type { Rect } from './camera';

/** Column count for a near-square grid holding `count` cells. */
export function gridCols(count: number): number {
  return count <= 1 ? 1 : Math.ceil(Math.sqrt(count));
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
