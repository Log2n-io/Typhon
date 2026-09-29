import { PLANET_EDGE_M, PLANET_HALF_EXTENT_M } from '../data/world-data';
import { createHeightGrid, type HeightGrid } from './layers';

/**
 * Posts per side of the baked field. 4096 × 4096 f32 is **67.1 MB**, once on the CPU and once as the GPU's R32F texture.
 *
 * It was 2048 (8 m posts), and the reason it is not is measured rather than aesthetic: a hill whose features are narrower
 * than two posts collapses to a single raised post, and bilinear over one raised post is exactly a four-sided pyramid.
 * `mesa strata` and `cliff detail` were authoring features that narrow — **5.4 such spikes per km²**, about 1 500 across
 * the planet, every one of them a visible cone. Resolution alone did not fix that (it measured 7.9/km² at 4 m, because the
 * layers simply aliased at a finer scale), so the octave floor in `tatooine-terrain.ts` moved at the same time: the field
 * and the tree that bakes into it are one decision, not two.
 *
 * A power of two, which keeps it a legal non-mipmapped texture everywhere without padding. 4096 is the largest size that
 * is safe without asking about the GPU — the WebGL2 floor for `MAX_TEXTURE_SIZE` is 2048, but no shipping desktop or
 * integrated part reports under 16 384.
 */
export const POSTS = 4096;

/** 4 m between posts, from 16 384 m over 4096 posts. One metre is out of the question: 268 M samples, ~1 GB. */
export const POST_SPACING_M = PLANET_EDGE_M / POSTS;

/** Planet coordinate of post 0 on both axes. The last post is therefore at `+8188`, and beyond it the field clamps. */
export const FIELD_ORIGIN_M = -PLANET_HALF_EXTENT_M;

/**
 * The baked heightfield: the one source of truth for ground height, for the CPU and — uploaded as a texture — for the GPU.
 *
 * **Everything interpolates these posts; nothing re-evaluates the layer tree.** That is what keeps the GPU's displaced
 * vertices, the altitude a sprite is drawn at and any future server sample in agreement: they are not three
 * implementations of one function, they are three readers of one array.
 */
export class Heightfield {
  readonly grid: HeightGrid;
  /** Lowest and highest post, filled by {@link measure}. Reported by the tests and available to a caller; nothing in the render path reads them today. */
  minHeightM = 0;
  maxHeightM = 0;

  constructor(grid: HeightGrid = createHeightGrid(POSTS, POST_SPACING_M, FIELD_ORIGIN_M)) {
    this.grid = grid;
  }

  /**
   * Ground height in metres at a planet coordinate, bilinear between posts, clamped outside the field.
   *
   * On a row window the clamp is to the **window's** rows, not the planet's, so a coordinate outside the band reads the
   * band's nearest edge. That is the right answer for the one caller that can see a window — nothing does today, since
   * pads and every runtime sampler run against the assembled whole — and a silently wrong index would be the wrong one.
   */
  heightAt(x: number, z: number): number {
    const { posts, spacingM, originM, rowOffset, rows, height } = this.grid;
    const last = posts - 1;
    const fx = clamp((x - originM) / spacingM, 0, last);
    const fz = clamp((z - originM) / spacingM, rowOffset, rowOffset + rows - 1);
    const x0 = Math.min(Math.floor(fx), last);
    const z0 = Math.min(Math.floor(fz), rowOffset + rows - 1);
    const x1 = Math.min(x0 + 1, last);
    const z1 = Math.min(z0 + 1, rowOffset + rows - 1);
    const tx = fx - x0;
    const tz = fz - z0;
    const row0 = (z0 - rowOffset) * posts;
    const row1 = (z1 - rowOffset) * posts;
    const a = height[row0 + x0];
    const b = height[row0 + x1];
    const c = height[row1 + x0];
    const d = height[row1 + x1];
    const top = a + (b - a) * tx;
    const bottom = c + (d - c) * tx;
    return top + (bottom - top) * tz;
  }

  /**
   * Ground gradient at a planet coordinate: `out[0]` is ∂h/∂x, `out[1]` is ∂h/∂z, both rise over run.
   *
   * A central difference over one post spacing rather than the analytic derivative of the bilinear patch, because the
   * analytic one is discontinuous at every post edge and a shading normal built from it shows the grid.
   */
  gradientAt(x: number, z: number, out: Float64Array): void {
    const s = this.grid.spacingM;
    out[0] = (this.heightAt(x + s, z) - this.heightAt(x - s, z)) / (2 * s);
    out[1] = (this.heightAt(x, z + s) - this.heightAt(x, z - s)) / (2 * s);
  }

  /** Slope as rise over run, so 1.0 is 45°. */
  slopeAt(x: number, z: number): number {
    const s = this.grid.spacingM;
    const dx = (this.heightAt(x + s, z) - this.heightAt(x - s, z)) / (2 * s);
    const dz = (this.heightAt(x, z + s) - this.heightAt(x, z - s)) / (2 * s);
    return Math.hypot(dx, dz);
  }

  /** Recomputes {@link minHeightM} and {@link maxHeightM}. Call once after a bake. */
  measure(): void {
    const height = this.grid.height;
    let min = Infinity;
    let max = -Infinity;
    for (let i = 0; i < height.length; i++) {
      const h = height[i];
      if (h < min) {
        min = h;
      }

      if (h > max) {
        max = h;
      }
    }

    // `Number.isFinite` rather than a length check: a single NaN post used to leave `minHeightM` at Infinity, which is a
    // worse failure than reporting zero because it propagates silently into anything that compares against it.
    this.minHeightM = Number.isFinite(min) ? min : 0;
    this.maxHeightM = Number.isFinite(max) ? max : 0;
  }
}

/**
 * A flat field, for the regression that matters most: with every height zero the client must render exactly what it
 * rendered before terrain existed. Cheap to make and the one guard that none of this breaks what already works.
 */
export function flatHeightfield(posts = 16): Heightfield {
  return new Heightfield(createHeightGrid(posts, PLANET_EDGE_M / posts, FIELD_ORIGIN_M));
}

/**
 * Clamps, and maps NaN to `lo` rather than passing it through.
 *
 * Written this way round deliberately: `v < lo ? lo : v > hi ? hi : v` returns NaN for NaN, which then indexes the height
 * array with NaN, reads `undefined`, and produces a NaN altitude. Entity positions come off the wire, so one bad value
 * would put a sprite nowhere at all.
 */
function clamp(v: number, lo: number, hi: number): number {
  return v > lo ? (v < hi ? v : hi) : lo;
}
