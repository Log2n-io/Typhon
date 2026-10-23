import type { DebugGrid, PushGeometry } from '@typhondb/client';

/** The largest window the engine sends is 64 cells a side, so one texture covers every case (CLI3D-03). */
export const CELL_TEXTURE_SIZE = 64;

/** The rectangle the delivered-cell texture covers, in planet metres: origin and extent, for the ground shader's uv. */
export interface CellRect {
  x: number;
  z: number;
  width: number;
  height: number;
}

/**
 * Writes the session's delivered cells into an RGBA texture: one texel per cell of the geometry's window, green where
 * the cell has been delivered and transparent where it has not.
 *
 * **The flat view of a deep window is the slice holding world altitude zero** — not, as this said before, the slice holding
 * the geometry's own anchor, which is what the code would have to do to follow a session parked above the ground.
 * Everything the demo replicates sits at altitude 0, so the two coincide today and nothing observes the difference; a deep
 * realm that stacked cells would need a third axis in the renderer first (CLI3D-04).
 *
 * Note that `fillHeatmap` picks its slab by CAMERA altitude instead, and takes it as an argument. Two overlays, two rules:
 * worth settling on one and passing the slice in, if a realm ever stacks cells.
 *
 * @param geometry The session's geometry, as the server last sent it.
 * @param grid The replication grid its cells are keyed on.
 * @param out An RGBA buffer of `size * size * 4` bytes, rewritten whole.
 * @param size The texture's side, in texels.
 * @param rect Receives the planet-space rectangle the texture covers.
 * @returns How many cells were delivered — 0 when there is nothing to draw.
 */
export function fillDeliveredCells(
  geometry: PushGeometry,
  grid: DebugGrid,
  out: Uint8Array,
  size: number,
  rect: CellRect,
): number {
  out.fill(0);
  const w = geometry.window;
  rect.x = grid.origin[0] + geometry.windowOrigin[0] * grid.cellM;
  rect.z = grid.origin[1] + geometry.windowOrigin[1] * grid.cellM;
  rect.width = w * grid.cellM;
  rect.height = w * grid.cellM;
  if (w === 0 || w > size) {
    return 0;
  }

  // The window's z slice the ground plane lies in. A flat window has one; a deep one is indexed from the grid's origin.
  const cz = geometry.deep ? Math.floor(-grid.origin[2] / grid.cellM) : 0;
  let delivered = 0;
  for (let ly = 0; ly < w; ly++) {
    for (let lx = 0; lx < w; lx++) {
      if (!geometry.delivered(geometry.windowOrigin[0] + lx, geometry.windowOrigin[1] + ly, cz)) {
        continue;
      }

      // Texel (lx, ly) of a w-wide window inside a size-wide texture: the shader's uv covers the WINDOW, not the texture,
      // so the rows beyond w are never sampled and are left cleared. Only the alpha channel carries anything — the
      // colours are the shader's, so that the delivered and the missing tints can be changed without a re-upload.
      out[(ly * size + lx) * 4 + 3] = 255;
      delivered++;
    }
  }

  return delivered;
}

/**
 * The hull's vertices flattened for the ground shader's `uHull`: `(x, z, 0, 0)` per vertex, in the engine's order,
 * padded to `max` with the last vertex so an unused slot draws a zero-length edge rather than a line to the origin.
 *
 * Written into a caller-owned array: this runs every frame the overlay is on and the session's shape is a region, and the
 * one caller already owns the buffer it was copying into.
 *
 * @param geometry The session's geometry.
 * @param out Receives `max * 4` numbers.
 * @param max How many slots the uniform array has.
 * @returns How many vertices are live.
 */
export function hullUniform(geometry: PushGeometry, out: number[], max: number): number {
  const count = Math.min(geometry.vertexCount, max);
  for (let i = 0; i < max; i++) {
    const v = Math.min(i, Math.max(count - 1, 0)) * geometry.dims;
    const at = i * 4;
    out[at] = count === 0 ? 0 : geometry.vertices[v];
    out[at + 1] = count === 0 ? 0 : geometry.vertices[v + 1];
    out[at + 2] = 0;
    out[at + 3] = 0;
  }

  return count;
}
