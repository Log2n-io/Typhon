import type { PlacedShape } from './structure-collision';

/**
 * A walk across a city: a path around the buildings between two points on open ground, on a grid of the structures'
 * walls.
 *
 * <b>Why a grid and not the colliders themselves.</b> The world's walls are a few hundred boxes per city, and a walk's
 * question is "which way round"; rasterising the walls once into a metre grid and searching it (A*, eight neighbours,
 * no corner cutting) answers it in a few milliseconds over a whole city, and the result is then pulled straight
 * wherever the grid says the line is clear — so the body walks diagonals, not staircases.
 *
 * <b>A wall is what a body cannot step onto</b>: a collider whose top stands more than a step above its structure's
 * base and whose bottom is under a head. Steps, ramps (sloped tops) and floors are walked; a porch higher than a step
 * is a wall, which is right — it is reached by its ramp, and a path to a porch is not a path a city walk needs.
 */

/** Height above a structure's base a body steps onto without climbing, metres (the server's `PlayerStepM`). */
const STEP_M = 0.4;

/** Height a collider must start under to block a body, metres: anything higher is overhead. */
const HEAD_M = 1.8;

/** The walk grid's cell, metres. */
export const PATH_CELL_M = 1;

/** A walkable grid over a rectangle of the world. */
export interface WalkGrid {
  readonly minX: number;
  readonly minZ: number;
  readonly cols: number;
  readonly rows: number;
  /** 1 where a body standing in the cell would touch a wall. */
  readonly blocked: Uint8Array;
}

/**
 * Rasterises the walls of the structures over a rectangle, grown by the body's half-extent.
 *
 * @param shapes The placed structures.
 * @param count How many are live.
 * @param minX The rectangle, west edge.
 * @param minZ South edge.
 * @param maxX East edge.
 * @param maxZ North edge.
 * @param half The body's half-extent.
 */
export function wallGrid(
  shapes: readonly PlacedShape[],
  count: number,
  minX: number,
  minZ: number,
  maxX: number,
  maxZ: number,
  half: number,
): WalkGrid {
  const cols = Math.max(1, Math.ceil((maxX - minX) / PATH_CELL_M));
  const rows = Math.max(1, Math.ceil((maxZ - minZ) / PATH_CELL_M));
  const blocked = new Uint8Array(cols * rows);
  for (let i = 0; i < count; i++) {
    const place = shapes[i];
    const q = place.quarters & 3;
    const bounds = place.shape.bounds[q];
    if (
      place.x + bounds.maxX < minX ||
      place.x + bounds.minX > maxX ||
      place.z + bounds.maxZ < minZ ||
      place.z + bounds.minZ > maxZ
    ) {
      continue;
    }

    for (const box of place.shape.turned[q]) {
      const sloped = box.slopeX !== 0 || box.slopeZ !== 0;
      if (sloped || box.maxY <= STEP_M || box.minY >= HEAD_M) {
        continue;
      }

      const c0 = Math.max(0, Math.floor((place.x + box.minX - half - minX) / PATH_CELL_M));
      const c1 = Math.min(cols - 1, Math.floor((place.x + box.maxX + half - minX) / PATH_CELL_M));
      const r0 = Math.max(0, Math.floor((place.z + box.minZ - half - minZ) / PATH_CELL_M));
      const r1 = Math.min(rows - 1, Math.floor((place.z + box.maxZ + half - minZ) / PATH_CELL_M));
      for (let r = r0; r <= r1; r++) {
        blocked.fill(1, r * cols + c0, r * cols + c1 + 1);
      }
    }
  }

  return { minX, minZ, cols, rows, blocked };
}

function cellOf(grid: WalkGrid, x: number, z: number): number {
  const c = Math.min(grid.cols - 1, Math.max(0, Math.floor((x - grid.minX) / PATH_CELL_M)));
  const r = Math.min(grid.rows - 1, Math.max(0, Math.floor((z - grid.minZ) / PATH_CELL_M)));
  return r * grid.cols + c;
}

function centreOf(grid: WalkGrid, cell: number): [number, number] {
  const c = cell % grid.cols;
  const r = (cell - c) / grid.cols;
  return [grid.minX + (c + 0.5) * PATH_CELL_M, grid.minZ + (r + 0.5) * PATH_CELL_M];
}

/** The nearest open cell to one, searching outwards ring by ring, or -1. */
function nearestOpen(grid: WalkGrid, cell: number, reach: number): number {
  if (grid.blocked[cell] === 0) {
    return cell;
  }

  const c0 = cell % grid.cols;
  const r0 = (cell - c0) / grid.cols;
  for (let ring = 1; ring <= reach; ring++) {
    for (let dr = -ring; dr <= ring; dr++) {
      for (let dc = -ring; dc <= ring; dc++) {
        if (Math.max(Math.abs(dr), Math.abs(dc)) !== ring) {
          continue;
        }

        const r = r0 + dr;
        const c = c0 + dc;
        if (r >= 0 && r < grid.rows && c >= 0 && c < grid.cols && grid.blocked[r * grid.cols + c] === 0) {
          return r * grid.cols + c;
        }
      }
    }
  }

  return -1;
}

/** Whether the straight line between two points crosses no blocked cell, sampled at a third of a cell. */
export function lineClear(grid: WalkGrid, ax: number, az: number, bx: number, bz: number): boolean {
  const steps = Math.ceil(Math.hypot(bx - ax, bz - az) / (PATH_CELL_M / 3));
  for (let i = 1; i < steps; i++) {
    const t = i / steps;
    if (grid.blocked[cellOf(grid, ax + (bx - ax) * t, az + (bz - az) * t)] !== 0) {
      return false;
    }
  }

  return true;
}

/**
 * A path from one point to another around the walls, as waypoints (the start excluded, the goal last), or `null` when
 * there is none inside the grid.
 */
export function findPath(
  grid: WalkGrid,
  fromX: number,
  fromZ: number,
  toX: number,
  toZ: number,
): [number, number][] | null {
  const start = nearestOpen(grid, cellOf(grid, fromX, fromZ), 4);
  const goal = nearestOpen(grid, cellOf(grid, toX, toZ), 4);
  if (start < 0 || goal < 0) {
    return null;
  }

  const n = grid.cols * grid.rows;
  const g = new Float32Array(n).fill(Infinity);
  const came = new Int32Array(n).fill(-1);
  const closed = new Uint8Array(n);
  const [gx, gz] = centreOf(grid, goal);
  const heuristic = (cell: number): number => {
    const [x, z] = centreOf(grid, cell);
    const dx = Math.abs(x - gx);
    const dz = Math.abs(z - gz);
    return Math.max(dx, dz) + (Math.SQRT2 - 1) * Math.min(dx, dz);
  };

  // A binary heap of (f, cell).
  const heapF: number[] = [];
  const heapC: number[] = [];
  const push = (f: number, cell: number): void => {
    heapF.push(f);
    heapC.push(cell);
    let i = heapF.length - 1;
    while (i > 0) {
      const p = (i - 1) >> 1;
      if (heapF[p] <= heapF[i]) {
        break;
      }

      [heapF[p], heapF[i]] = [heapF[i], heapF[p]];
      [heapC[p], heapC[i]] = [heapC[i], heapC[p]];
      i = p;
    }
  };
  const pop = (): number => {
    const top = heapC[0];
    const lastF = heapF.pop()!;
    const lastC = heapC.pop()!;
    if (heapF.length > 0) {
      heapF[0] = lastF;
      heapC[0] = lastC;
      let i = 0;
      for (;;) {
        const l = 2 * i + 1;
        const r = l + 1;
        let m = i;
        if (l < heapF.length && heapF[l] < heapF[m]) {
          m = l;
        }

        if (r < heapF.length && heapF[r] < heapF[m]) {
          m = r;
        }

        if (m === i) {
          break;
        }

        [heapF[m], heapF[i]] = [heapF[i], heapF[m]];
        [heapC[m], heapC[i]] = [heapC[i], heapC[m]];
        i = m;
      }
    }

    return top;
  };

  g[start] = 0;
  push(heuristic(start), start);
  const cols = grid.cols;
  while (heapF.length > 0) {
    const cell = pop();
    if (closed[cell] !== 0) {
      continue;
    }

    if (cell === goal) {
      break;
    }

    closed[cell] = 1;
    const c = cell % cols;
    const r = (cell - c) / cols;
    for (let dr = -1; dr <= 1; dr++) {
      for (let dc = -1; dc <= 1; dc++) {
        if (dr === 0 && dc === 0) {
          continue;
        }

        const nr = r + dr;
        const nc = c + dc;
        if (nr < 0 || nr >= grid.rows || nc < 0 || nc >= cols) {
          continue;
        }

        const next = nr * cols + nc;
        // No corner cutting: a diagonal needs both of the cells it passes between to be open.
        if (
          grid.blocked[next] !== 0 ||
          (dr !== 0 && dc !== 0 && (grid.blocked[r * cols + nc] !== 0 || grid.blocked[nr * cols + c] !== 0))
        ) {
          continue;
        }

        const cost = g[cell] + (dr !== 0 && dc !== 0 ? Math.SQRT2 : 1) * PATH_CELL_M;
        if (cost < g[next]) {
          g[next] = cost;
          came[next] = cell;
          push(cost + heuristic(next), next);
        }
      }
    }
  }

  if (start !== goal && came[goal] < 0) {
    return null;
  }

  // Back from the goal, then pulled straight: each kept point is the furthest along the cell path still in sight.
  const cells: number[] = [];
  for (let cell = goal; cell !== start && cell >= 0; cell = came[cell]) {
    cells.push(cell);
  }

  cells.reverse();
  const out: [number, number][] = [];
  let ax = fromX;
  let az = fromZ;
  let i = 0;
  while (i < cells.length) {
    let j = cells.length - 1;
    while (j > i) {
      const [bx, bz] = centreOf(grid, cells[j]);
      if (lineClear(grid, ax, az, bx, bz)) {
        break;
      }

      j--;
    }

    const [px, pz] = j === cells.length - 1 ? [toX, toZ] : centreOf(grid, cells[j]);
    out.push([px, pz]);
    ax = px;
    az = pz;
    i = j + 1;
  }

  if (out.length === 0) {
    out.push([toX, toZ]);
  }

  return out;
}
