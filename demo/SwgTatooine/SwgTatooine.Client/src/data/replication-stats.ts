import { PushShape, type PushGeometry } from '@typhondb/client';
import type { ReplicationStats } from '../state/stats-store';
import type { DebugView } from './source';

/**
 * The in-radius of the hull the server kept: the shortest distance from its vertex centroid to any of its edges, in
 * metres. 0 when the geometry is not a region or carries no hull yet.
 *
 * **The in-radius, not the circumradius.** The question a viewer is asking is "how far out am I actually served?", and
 * the honest answer is the largest disc that fits *inside* what the server holds. For the demo's square regions the two
 * differ by √2, which is the difference between a ring that sits on the served edge and one that overstates it by 40 %.
 *
 * The centroid is also the point the engine measures near-routing from for a `ClientRegion` session, so this is the same
 * anchor the session's own events are ranged against.
 *
 * @param g The session's geometry.
 * @returns The in-radius in metres, or 0.
 */
export function hullInRadiusM(g: PushGeometry): number {
  if (g.shape !== PushShape.Region || g.vertexCount < 3) {
    return 0;
  }

  const n = g.vertexCount;
  const d = g.dims;
  let cx = 0;
  let cz = 0;
  for (let i = 0; i < n; i++) {
    cx += g.vertices[i * d];
    cz += g.vertices[i * d + 1];
  }

  cx /= n;
  cz /= n;

  let nearest = Infinity;
  for (let i = 0; i < n; i++) {
    const ax = g.vertices[i * d];
    const az = g.vertices[i * d + 1];
    const j = (i + 1) % n;
    const bx = g.vertices[j * d];
    const bz = g.vertices[j * d + 1];
    const ex = bx - ax;
    const ez = bz - az;
    const len2 = ex * ex + ez * ez;
    if (len2 === 0) {
      continue;
    }

    // Distance from the centroid to the infinite line through the edge: correct for a convex hull, and cheaper and
    // better behaved at a corner than a segment distance, which would report the corner and understate the inscribed disc.
    nearest = Math.min(nearest, Math.abs(ex * (az - cz) - ez * (ax - cx)) / Math.sqrt(len2));
  }

  return nearest === Infinity ? 0 : nearest;
}

const SHAPES: Record<number, string> = {
  [PushShape.Sphere]: 'sphere',
  [PushShape.World]: 'world',
  [PushShape.Region]: 'region',
};

/**
 * Reduces the `DEBUG` block to the row the HUD shows (CLI3D-03), or `null` when the server has sent none — the mock, a
 * session without the `DEBUG` cap, or a live session before its first geometry.
 *
 * Counting the delivered cells walks the window, at most 64 × 64 bits; the HUD publishes four times a second, so this
 * is nowhere near a hot path and the alternative — having the decoder keep a running count — would put work on every
 * frame to save it four times a second.
 *
 * @param debug What the source reports.
 * @returns The row, or `null`.
 */
export function replicationStatsOf(debug: DebugView | null): ReplicationStats | null {
  const grid = debug?.grid ?? null;
  const geometry = debug?.geometry ?? null;
  if (grid === null || geometry === null) {
    return null;
  }

  const w = geometry.window;
  const depth = geometry.deep ? w : 1;
  let delivered = 0;
  for (let lz = 0; lz < depth; lz++) {
    for (let ly = 0; ly < w; ly++) {
      for (let lx = 0; lx < w; lx++) {
        if (
          geometry.delivered(
            geometry.windowOrigin[0] + lx,
            geometry.windowOrigin[1] + ly,
            geometry.windowOrigin[2] + lz,
          )
        ) {
          delivered++;
        }
      }
    }
  }

  return {
    shape: SHAPES[geometry.shape] ?? `shape ${geometry.shape}`,
    deliveredCells: delivered,
    windowCells: w * w * depth,
    cellM: grid.cellM,
    held: geometry.held,
    nearBudget: geometry.nearBudget,
    radiusM: geometry.shape === PushShape.Sphere ? geometry.radiusM : 0,
    level: geometry.shape === PushShape.Sphere ? geometry.level : 0,
    viewComplete: geometry.viewComplete,
  };
}
