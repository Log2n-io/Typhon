import { Heightfield } from './heightfield';
import { applyLayers, bakeLayers, createHeightGridWindow, haloRows, type HeightGrid } from './layers';
import { landformLayers, padLayers, TERRAIN_SEED } from './tatooine-terrain';

/**
 * Baking the planet in horizontal bands, so the bake runs on every core instead of one.
 *
 * ## Why this exists
 *
 * The bake is **12.0 s measured** at 4096 posts on a 7950X, against 1.5 s at 2048 with the old six-layer tree. It runs off
 * the main thread either way, so nothing stutters — but twelve seconds of flat planet at startup is twelve seconds of the
 * demo showing the wrong thing, and the work is embarrassingly parallel. Split eight ways it is under two.
 *
 * ## Why a band is exact and a tile would not be
 *
 * Every layer is pointwise in the planet's coordinates **except** the slope filter, which reads the accumulator one post
 * either side. So a band that carries {@link haloRows} rows of overlap, and that knows the planet's full `posts` for its
 * rim and boundary arithmetic, produces the whole-grid heights on its interior bit for bit — not nearly, exactly, and
 * `test/terrain-layers.test.ts` asserts it post for post.
 *
 * The **pads** are the part that cannot be banded: each one levels the ground to the height the landform reached at the
 * site's centre, which may lie in another band. They are applied once, on the assembled field, and cost nothing — fourteen
 * feathered circles against a 16.8 M-post grid.
 */

/** What one band bake needs to know. Flat and structured-clone-safe, because it crosses a worker boundary. */
export interface BandRequest {
  /** Posts per side of the **planet**. */
  readonly posts: number;
  readonly spacingM: number;
  readonly originM: number;
  /** First planet row this band owns. */
  readonly rowOffset: number;
  /** Rows it owns — not counting the halo, which it computes and then discards. */
  readonly rows: number;
  readonly seed: number;
}

/** One band's finished rows. */
export interface BandResult {
  readonly rowOffset: number;
  readonly rows: number;
  /** `posts × rows` heights, the band's interior only. */
  readonly height: Float32Array;
}

/**
 * Splits `posts` rows into `bands` contiguous ranges, as evenly as they divide.
 *
 * The remainder goes to the first bands rather than the last, so no band is more than one row larger than any other. With
 * 4096 rows over 8 bands that is exactly 512 each; the general case matters because the band count follows the machine.
 */
export function bandRanges(posts: number, bands: number): { rowOffset: number; rows: number }[] {
  const count = Math.max(1, Math.min(Math.floor(bands), posts));
  const base = Math.floor(posts / count);
  const extra = posts % count;
  const out: { rowOffset: number; rows: number }[] = [];
  let at = 0;
  for (let i = 0; i < count; i++) {
    const rows = base + (i < extra ? 1 : 0);
    out.push({ rowOffset: at, rows });
    at += rows;
  }

  return out;
}

/**
 * Bakes the landform layers over one band and returns that band's rows.
 *
 * The halo is computed from the tree, baked, and then dropped: it exists so the slope filter inside the band sees real
 * neighbours rather than the band's own edge, and it is never part of the result.
 */
export function bakeLandformBand(request: BandRequest): BandResult {
  const { posts, spacingM, originM, rowOffset, rows, seed } = request;
  const layers = landformLayers(seed);
  const halo = haloRows(layers);
  const from = Math.max(0, rowOffset - halo);
  const to = Math.min(posts, rowOffset + rows + halo);
  const grid = createHeightGridWindow(posts, spacingM, originM, from, to - from);
  bakeLayers(grid, layers);

  const interior = (rowOffset - from) * posts;
  return {
    rowOffset,
    rows,
    height: grid.height.slice(interior, interior + rows * posts),
  };
}

/**
 * Writes a band's rows into the assembled planet grid.
 *
 * @throws If the band does not fit — a band count that disagreed between the caller and the workers would otherwise
 *   silently leave a stripe of the planet at zero, which reads as a cliff rather than as a bug.
 */
export function placeBand(grid: HeightGrid, band: BandResult): void {
  if (band.rowOffset < 0 || band.rowOffset + band.rows > grid.rows || band.height.length !== band.rows * grid.posts) {
    throw new Error(`band [${band.rowOffset}, ${band.rowOffset + band.rows}) does not fit the assembled grid`);
  }

  grid.height.set(band.height, band.rowOffset * grid.posts);
}

/**
 * The part of the bake that cannot be banded: the town and landmark pads, then the min/max scan.
 *
 * @param field The assembled landform. Modified in place.
 */
export function finishBake(field: Heightfield): void {
  applyLayers(field.grid, padLayers(field));
  field.measure();
}

/** The whole bake in one thread, band by band. Used by the tests and by anything without workers. */
export function bakeInBands(field: Heightfield, bands: number, seed = TERRAIN_SEED): void {
  const { posts, spacingM, originM } = field.grid;
  for (const range of bandRanges(posts, bands)) {
    placeBand(field.grid, bakeLandformBand({ posts, spacingM, originM, seed, ...range }));
  }

  finishBake(field);
}
