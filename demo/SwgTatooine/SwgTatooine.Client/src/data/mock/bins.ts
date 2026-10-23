import { PLANET_EDGE_M, PLANET_HALF_EXTENT_M } from '../world-data';
import type { EntitySet } from './world';

/**
 * A uniform grid over the planet, rebuilt by counting sort: `O(entities + cells)` per build, then radius queries walk
 * only the cells a circle overlaps. The mock server's stand-in for the engine's spatial index; one per archetype, shared by
 * the simulation (which reads it at the start of a tick) and interest (which rebuilds it after the tick moved things).
 *
 * The entity arrays are read into locals before each loop: the five archetypes are five object shapes, and reading
 * `set.x` inside a loop over them is a megamorphic load (measured 5× slower on a 168 k build).
 */
export class SpatialBins {
  readonly cellM: number;
  readonly dims: number;
  private readonly cellStart: Int32Array;
  private readonly cursor: Int32Array;
  private items: Int32Array = new Int32Array(0);

  constructor(cellM: number) {
    this.cellM = cellM;
    this.dims = Math.ceil(PLANET_EDGE_M / cellM);
    this.cellStart = new Int32Array(this.dims * this.dims + 1);
    this.cursor = new Int32Array(this.dims * this.dims);
  }

  build(set: EntitySet): void {
    const count = set.count;
    const xs = set.x;
    const zs = set.z;
    const dims = this.dims;
    const cellCount = dims * dims;
    if (this.items.length < count) {
      this.items = new Int32Array(count);
    }

    const start = this.cellStart;
    const inv = 1 / this.cellM;
    start.fill(0);
    for (let i = 0; i < count; i++) {
      start[axis(xs[i], inv, dims) + axis(zs[i], inv, dims) * dims + 1]++;
    }

    for (let c = 0; c < cellCount; c++) {
      start[c + 1] += start[c];
    }

    const cursor = this.cursor;
    cursor.set(start.subarray(0, cellCount));
    const items = this.items;
    for (let i = 0; i < count; i++) {
      items[cursor[axis(xs[i], inv, dims) + axis(zs[i], inv, dims) * dims]++] = i;
    }
  }

  /**
   * Number of entities in the cells a disc overlaps — an upper bound read off the per-cell counts in O(rows), no entity
   * visited: for each row, the span of cells the disc crosses is one prefix difference.
   */
  estimateDisc(x: number, z: number, radius: number): number {
    const inv = 1 / this.cellM;
    const dims = this.dims;
    const cell = this.cellM;
    const z0 = axis(z - radius, inv, dims);
    const z1 = axis(z + radius, inv, dims);
    let n = 0;
    for (let cz = z0; cz <= z1; cz++) {
      const rowMin = cz * cell - PLANET_HALF_EXTENT_M;
      const dz = z < rowMin ? rowMin - z : z > rowMin + cell ? z - rowMin - cell : 0;
      if (dz > radius) {
        continue;
      }

      const half = Math.sqrt(radius * radius - dz * dz);
      const row = cz * dims;
      n += this.cellStart[row + axis(x + half, inv, dims) + 1] - this.cellStart[row + axis(x - half, inv, dims)];
    }

    return n;
  }

  /** Count of entities whose cell lies in the given cell rectangle (inclusive). */
  countRect(x0: number, z0: number, x1: number, z1: number): number {
    const dims = this.dims;
    let n = 0;
    for (let cz = z0; cz <= z1; cz++) {
      const row = cz * dims;
      n += this.cellStart[row + x1 + 1] - this.cellStart[row + x0];
    }

    return n;
  }

  /**
   * Indices within `radius` of `(x, z)` into `out`, returns how many; `distSq` receives each hit's squared distance at the
   * same position. Stops at `out.length`: the caller sizes it.
   */
  queryRadius(set: EntitySet, x: number, z: number, radius: number, out: Int32Array, distSq: Float64Array): number {
    const xs = set.x;
    const zs = set.z;
    const items = this.items;
    const start = this.cellStart;
    const inv = 1 / this.cellM;
    const dims = this.dims;
    const r2 = radius * radius;
    const x0 = axis(x - radius, inv, dims);
    const x1 = axis(x + radius, inv, dims);
    const z0 = axis(z - radius, inv, dims);
    const z1 = axis(z + radius, inv, dims);
    const limit = out.length;
    let n = 0;
    for (let cz = z0; cz <= z1; cz++) {
      const row = cz * dims;
      for (let cx = x0; cx <= x1; cx++) {
        const cell = row + cx;
        const end = start[cell + 1];
        for (let k = start[cell]; k < end; k++) {
          const i = items[k];
          const dx = xs[i] - x;
          const dz = zs[i] - z;
          const d2 = dx * dx + dz * dz;
          if (d2 <= r2) {
            if (n >= limit) {
              return n;
            }

            distSq[n] = d2;
            out[n++] = i;
          }
        }
      }
    }

    return n;
  }

  /** Nearest index within `radius` accepted by `accept`, or -1. `accept` should be allocated once, not per call. */
  nearest(set: EntitySet, x: number, z: number, radius: number, accept: (i: number) => boolean): number {
    const xs = set.x;
    const zs = set.z;
    const items = this.items;
    const start = this.cellStart;
    const inv = 1 / this.cellM;
    const dims = this.dims;
    const x0 = axis(x - radius, inv, dims);
    const x1 = axis(x + radius, inv, dims);
    const z0 = axis(z - radius, inv, dims);
    const z1 = axis(z + radius, inv, dims);
    let best = -1;
    let bestD2 = radius * radius;
    for (let cz = z0; cz <= z1; cz++) {
      const row = cz * dims;
      for (let cx = x0; cx <= x1; cx++) {
        const cell = row + cx;
        const end = start[cell + 1];
        for (let k = start[cell]; k < end; k++) {
          const i = items[k];
          const dx = xs[i] - x;
          const dz = zs[i] - z;
          const d2 = dx * dx + dz * dz;
          if (d2 <= bestD2 && accept(i)) {
            best = i;
            bestD2 = d2;
          }
        }
      }
    }

    return best;
  }
}

function axis(v: number, inv: number, dims: number): number {
  const c = Math.floor((v + PLANET_HALF_EXTENT_M) * inv);
  return c < 0 ? 0 : c >= dims ? dims - 1 : c;
}
