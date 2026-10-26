import { fractalAt, valueNoise, warpPoint, type FractalSpec } from './hash';

/**
 * A small height-only layer system, in the shape of SWG's terrain engine and at about a third of its scale.
 *
 * The reason for a layer system rather than a stack of fBm terms is not fidelity to SWG — it is that **plain fBm is
 * statistically identical everywhere at every scale**, which is exactly the tell between ground a player reads as a place
 * and ground they read as noise. Three mechanisms do that perceptual work, and all three are cheap:
 *
 * - **Boundaries** — a layer applies only inside a shape, feathered at its edge, so regions are different *kinds* of
 *   landform rather than one generator at several amplitudes.
 * - **Filters** — a layer applies only where the terrain *already* satisfies a predicate (a height band, a slope band).
 *   This is what makes ground look caused rather than drawn: rock on the faces that are already steep, drift collecting
 *   in the hollows.
 * - **Terracing** — the banded, flat-topped mesa silhouette, which octave noise cannot produce at any parameter setting.
 *
 * Only the height half is built. Colour, shader, flora, road and river affectors are the bulk of the real thing and this
 * demo has none of them.
 */

/** A shape a layer is confined to. `featherM` is how far inside the edge the weight ramps from 0 to 1. */
export type Boundary =
  | {
      readonly kind: 'circle';
      readonly x: number;
      readonly z: number;
      readonly radiusM: number;
      readonly featherM: number;
    }
  | {
      readonly kind: 'rect';
      readonly x: number;
      readonly z: number;
      readonly halfXM: number;
      readonly halfZM: number;
      readonly featherM: number;
    }
  /** `points` is flat `x, z` pairs, in either winding. */
  | { readonly kind: 'polygon'; readonly points: readonly number[]; readonly featherM: number }
  /** A thick line: everything within `halfWidthM` of the polyline. */
  | {
      readonly kind: 'polyline';
      readonly points: readonly number[];
      readonly halfWidthM: number;
      readonly featherM: number;
    };

/**
 * A predicate on the terrain built so far. Both forms are evaluated against a **snapshot** of the accumulator, never
 * against the layer's own partial output, so the result cannot depend on the order posts happen to be visited in.
 */
export type Filter =
  /** Applies within a height band, in metres. */
  | { readonly kind: 'height'; readonly minM: number; readonly maxM: number; readonly featherM: number }
  /** Applies within a slope band, measured as rise over run (so 1.0 is 45°). */
  | { readonly kind: 'slope'; readonly min: number; readonly max: number; readonly feather: number };

/** What a layer does where it applies. */
export type Affector =
  /** Levels the ground to a fixed height — what SWG's `AHCN` does inside a feathered circle to make a town's pad. */
  | { readonly kind: 'constant'; readonly heightM: number }
  /** Adds a fractal term, optionally domain-warped first. */
  | {
      readonly kind: 'fractal';
      readonly amplitudeM: number;
      readonly biasM: number;
      readonly fractal: FractalSpec;
      readonly warpM?: number;
      readonly warpWavelengthM?: number;
    }
  /**
   * Quantises the height that is already there into bands.
   *
   * `sharpness` 0 is the identity; 1 confines the rise to about a ninth of a step and flattens the rest, which is the
   * strata look. It is a function of the accumulator, so it is the one affector that reads before it writes.
   */
  | { readonly kind: 'terrace'; readonly stepM: number; readonly sharpness: number };

/** How an affector's value joins what is already there. */
export type Blend = 'add' | 'replace' | 'max';

export interface TerrainLayer {
  /** For tests and for reading a tree; never shown to a player. */
  readonly name: string;
  /** `null` means global. */
  readonly boundary: Boundary | null;
  readonly filters: readonly Filter[];
  readonly affector: Affector;
  readonly blend: Blend;
}

/**
 * A square grid of height posts covering the planet.
 *
 * The **grid is the contract**, not the continuous function: everything that samples height — the GPU through a texture,
 * the client's CPU, a server twin — reads these posts and interpolates between them. That makes the cross-runtime
 * agreement test a comparison of texels rather than of a function's tail digits.
 */
/**
 * ## What a second implementation of this must match, exactly
 *
 * The grid is the contract, so these are not implementation details — they are the contract's terms, and every one of them
 * changes the resulting heights:
 *
 * 1. **The accumulator is `f32`.** Every layer's output is rounded to float32 on store ({@link HeightGrid.height}), and the
 *    next layer's filters read those rounded values. A twin holding the accumulator in `double` diverges immediately.
 * 2. **The weights are `f32` too.** A layer's per-post weight is written to a float32 scratch buffer and read back before
 *    blending, so the value actually blended is `f32(boundary × filters)`, not the double product.
 * 3. **Intermediates are `f64`.** Everything between those two roundings — the feather and band curves, the noise, the
 *    blend — is double precision, in the operation order written here.
 * 4. **Layer order is the tree's order**, and the second stage's constants are read out of the finished first stage.
 * 5. **The bake is whole-grid and NOT tileable.** The slope filter takes a one-sided difference at the grid rim
 *    ({@link slopeAtPost}), bounding boxes clamp to grid bounds, and each pad's constant is sampled from the completed
 *    landform. Baking a 256 m tile therefore yields different heights at its edges than the same region baked as part of
 *    the whole. A server that wants height must bake all of it in one pass, at the same resolution — or be shipped the
 *    grid, which is the fallback the design named.
 *
 *    A **row window** is the one exception, and it weakens nothing: a window still knows the whole planet's `posts`, so
 *    rim handling and boundary extents are computed against the planet rather than against the window, and a window
 *    carrying {@link haloRows} rows of overlap reproduces the whole-grid heights on its interior EXACTLY. That is what
 *    lets the bake run on several threads at once, and `test/terrain-layers.test.ts` asserts the equality post for post
 *    rather than approximately.
 *
 * `test/golden/terrain-hash.json` pins a coarse bake of the real tree for exactly this reason.
 */
export interface HeightGrid {
  /** Posts per side **of the planet**, not of this window. */
  readonly posts: number;
  /** Metres between posts. */
  readonly spacingM: number;
  /** Planet coordinate of post 0, on both axes. */
  readonly originM: number;
  /** First planet row this grid holds. `0` for a whole grid. */
  readonly rowOffset: number;
  /** Rows held. Equal to {@link HeightGrid.posts} for a whole grid. */
  readonly rows: number;
  /** `posts × rows` heights in metres, row-major with z outer. **f32**, so a twin must round the same way. */
  readonly height: Float32Array;
}

/** Allocates a whole grid covering `[originM, originM + (posts − 1) · spacingM]` on both axes. */
export function createHeightGrid(posts: number, spacingM: number, originM: number): HeightGrid {
  return createHeightGridWindow(posts, spacingM, originM, 0, posts);
}

/**
 * Allocates a grid holding only rows `[rowOffset, rowOffset + rows)` of the planet.
 *
 * Everything else about it is the planet's: `posts` stays the planet's side, so a boundary's extent, the rim of the slope
 * filter and every planet coordinate are computed exactly as they would be in a whole-grid bake. Only the storage and the
 * loop bounds narrow.
 */
export function createHeightGridWindow(
  posts: number,
  spacingM: number,
  originM: number,
  rowOffset: number,
  rows: number,
): HeightGrid {
  if (
    !Number.isInteger(rowOffset) ||
    !Number.isInteger(rows) ||
    rowOffset < 0 ||
    rows <= 0 ||
    rowOffset + rows > posts
  ) {
    throw new Error(`height grid window [${rowOffset}, ${rowOffset + rows}) does not fit ${posts} planet rows`);
  }

  return { posts, spacingM, originM, rowOffset, rows, height: new Float32Array(posts * rows) };
}

/**
 * Wraps an existing buffer as a whole grid, without copying it.
 *
 * For the assembly step: the bands' rows are written into one buffer that is then handed to the finishing worker by
 * transfer, and re-wrapping it must not allocate a second 67 MB.
 *
 * @throws If the buffer is not exactly `posts × posts`, which would otherwise index out of bounds later and read
 *   `undefined` as a height.
 */
export function adoptHeightGrid(posts: number, spacingM: number, originM: number, height: Float32Array): HeightGrid {
  if (height.length !== posts * posts) {
    throw new Error(`height buffer of ${height.length} does not hold ${posts}² posts`);
  }

  return { posts, spacingM, originM, rowOffset: 0, rows: posts, height };
}

/**
 * Rows of overlap a window needs on each side to reproduce the whole-grid bake exactly.
 *
 * One row per slope-filtered layer, and the reason it is not simply one: a slope filter reads the accumulator one row
 * either side, so the layer **before** it has to be correct one row further out again, and so on down the tree. Getting
 * this wrong does not fail loudly — it leaves a faint seam along each band boundary, a line of slightly wrong rock on
 * steep ground — which is why it is derived from the tree rather than written down as a constant.
 */
export function haloRows(layers: readonly TerrainLayer[]): number {
  let slopeFiltered = 0;
  for (const layer of layers) {
    for (const filter of layer.filters) {
      if (filter.kind === 'slope') {
        slopeFiltered++;
        break;
      }
    }
  }

  return slopeFiltered;
}

/**
 * Evaluates a layer tree into a grid, layer by layer over the whole grid.
 *
 * Grid-at-a-time rather than point-at-a-time for one reason that matters: the slope filter needs the gradient of the
 * terrain built by earlier layers, and on a grid that is a central difference between neighbouring posts — O(1). Sampling
 * a continuous height function instead would make every slope-filtered layer re-evaluate every earlier layer four more
 * times, which is where a naive implementation of this idea goes from cheap to unusable.
 *
 * @param grid Receives the heights; cleared first.
 * @param layers Applied in order. Later layers see the terrain earlier ones made.
 * @param scratch Optional reusable weight buffer of `posts × posts`; supplied by a repeated bake to allocate nothing.
 */
export function bakeLayers(grid: HeightGrid, layers: readonly TerrainLayer[], scratch?: Float32Array): void {
  grid.height.fill(0);
  applyLayers(grid, layers, scratch);
}

/**
 * Applies layers **onto** what the grid already holds, without clearing it.
 *
 * This exists because one authored layer cannot be written down until the landform is baked: a town's pad has to level the
 * ground to the height the terrain reached at that spot, which is only known afterwards. So the tree is applied in two
 * stages, and the second stage's constants are read out of the first stage's result.
 */
export function applyLayers(grid: HeightGrid, layers: readonly TerrainLayer[], scratch?: Float32Array): void {
  const n = grid.posts * grid.rows;
  const weight = scratch !== undefined && scratch.length >= n ? scratch : new Float32Array(n);
  const range = new Int32Array(4);
  for (const layer of layers) {
    applyLayer(grid, layer, weight, range);
  }
}

/**
 * The post range a boundary can possibly reach, as `[x0, z0, x1, z1]` inclusive, clamped to the grid.
 *
 * This is the difference between a bake that takes seconds and one that takes a fraction of one, and the reason is the
 * pads: fourteen of them, each covering a few thousand posts of a 4.19 M-post grid. Sweeping the whole grid per layer to
 * find them cost **4.1 s measured** at 2048 posts, most of it in circle tests that were always going to return zero.
 */
function boundaryRange(boundary: Boundary | null, grid: HeightGrid, out: Int32Array): void {
  const last = grid.posts - 1;
  if (boundary === null) {
    out[0] = 0;
    out[1] = 0;
    out[2] = last;
    out[3] = last;
    return;
  }

  let minX = -Infinity;
  let minZ = -Infinity;
  let maxX = Infinity;
  let maxZ = Infinity;
  switch (boundary.kind) {
    case 'circle':
      minX = boundary.x - boundary.radiusM;
      maxX = boundary.x + boundary.radiusM;
      minZ = boundary.z - boundary.radiusM;
      maxZ = boundary.z + boundary.radiusM;
      break;
    case 'rect':
      minX = boundary.x - boundary.halfXM;
      maxX = boundary.x + boundary.halfXM;
      minZ = boundary.z - boundary.halfZM;
      maxZ = boundary.z + boundary.halfZM;
      break;
    case 'polygon':
    case 'polyline': {
      // An odd-length or empty `points` array used to read `undefined` here, make the whole box NaN, and silently drop the
      // layer — a malformed region rendering as nothing at all, with no error anywhere. A layer tree is authored, so this
      // is a programming mistake and should say so.
      if (boundary.points.length < 2 || boundary.points.length % 2 !== 0) {
        throw new Error(
          `terrain boundary ${boundary.kind} needs a non-empty even-length points array, got ${boundary.points.length}`,
        );
      }

      // A polyline's extent grows by its half-width; a polygon's is its vertices'.
      const pad = boundary.kind === 'polyline' ? boundary.halfWidthM : 0;
      minX = Infinity;
      minZ = Infinity;
      maxX = -Infinity;
      maxZ = -Infinity;
      for (let i = 0; i < boundary.points.length; i += 2) {
        const x = boundary.points[i];
        const z = boundary.points[i + 1];
        minX = Math.min(minX, x - pad);
        maxX = Math.max(maxX, x + pad);
        minZ = Math.min(minZ, z - pad);
        maxZ = Math.max(maxZ, z + pad);
      }

      break;
    }
  }

  out[0] = clampPost(Math.floor((minX - grid.originM) / grid.spacingM), last);
  out[1] = clampPost(Math.floor((minZ - grid.originM) / grid.spacingM), last);
  out[2] = clampPost(Math.ceil((maxX - grid.originM) / grid.spacingM), last);
  out[3] = clampPost(Math.ceil((maxZ - grid.originM) / grid.spacingM), last);
}

function clampPost(index: number, last: number): number {
  return index < 0 ? 0 : index > last ? last : index;
}

/** Where a layer applies, and how strongly, in `[0, 1]` per post. Reports whether it applies anywhere at all. */
function fillWeights(grid: HeightGrid, layer: TerrainLayer, weight: Float32Array, range: Int32Array): boolean {
  const { posts, spacingM, originM, rowOffset, rows } = grid;
  const filters = layer.filters;
  const first = Math.max(range[1], rowOffset);
  const last = Math.min(range[3], rowOffset + rows - 1);
  let any = false;
  for (let iz = first; iz <= last; iz++) {
    const z = originM + iz * spacingM;
    const row = (iz - rowOffset) * posts;
    for (let ix = range[0]; ix <= range[2]; ix++) {
      const x = originM + ix * spacingM;
      let w = layer.boundary === null ? 1 : boundaryWeight(layer.boundary, x, z);
      // The filters read the ACCUMULATOR, which this pass does not touch — that is what the two passes buy. In one pass a
      // filtered layer's result would depend on the order posts happen to be visited in.
      for (let f = 0; f < filters.length && w > 0; f++) {
        w *= filterWeight(filters[f], grid, ix, iz);
      }

      weight[row + ix] = w;
      any ||= w > 0;
    }
  }

  return any;
}

function applyLayer(grid: HeightGrid, layer: TerrainLayer, weight: Float32Array, range: Int32Array): void {
  boundaryRange(layer.boundary, grid, range);
  if (!fillWeights(grid, layer, weight, range)) {
    return;
  }

  const { posts, spacingM, originM, rowOffset, rows, height } = grid;
  const warp = new Float64Array(2);
  const first = Math.max(range[1], rowOffset);
  const last = Math.min(range[3], rowOffset + rows - 1);
  for (let iz = first; iz <= last; iz++) {
    const z = originM + iz * spacingM;
    const row = (iz - rowOffset) * posts;
    for (let ix = range[0]; ix <= range[2]; ix++) {
      const at = row + ix;
      const w = weight[at];
      if (w <= 0) {
        continue;
      }

      const current = height[at];
      const value = affectorAt(layer.affector, originM + ix * spacingM, z, current, warp);
      height[at] = blendAt(layer.blend, current, value, w);
    }
  }
}

function blendAt(blend: Blend, current: number, value: number, w: number): number {
  switch (blend) {
    case 'add':
      return current + value * w;
    case 'replace':
      return current + (value - current) * w;
    case 'max':
      // Feathered so it stays continuous: at w = 1 this is a hard max, and below that it lerps toward it.
      return current + (Math.max(current, value) - current) * w;
  }
}

function affectorAt(affector: Affector, x: number, z: number, current: number, warp: Float64Array): number {
  switch (affector.kind) {
    case 'constant':
      return affector.heightM;
    case 'terrace':
      return terraceAt(current, affector.stepM, affector.sharpness);
    case 'fractal': {
      let sx = x;
      let sz = z;
      const amount = affector.warpM ?? 0;
      if (amount !== 0) {
        warpPoint(x, z, affector.fractal.seed ^ 0x2545f491, amount, affector.warpWavelengthM ?? 2000, warp);
        sx = warp[0]!;
        sz = warp[1]!;
      }

      return affector.biasM + fractalAt(affector.fractal, sx, sz) * affector.amplitudeM;
    }
  }
}

/**
 * How wide a terrace riser is **on the ground**, in metres, where the underlying landform has the given slope.
 *
 * The other half of the feature rule, and the half that had no expression at all — the Nyquist check walked straight past
 * every terrace layer because it only understood fractals.
 *
 * {@link terraceAt} spends `1 / gain` of a band on the rise, so the riser is `stepM / gain` of INPUT HEIGHT. What that
 * costs horizontally depends on the ground it is laid over: on a 0.35 grade, 5 m of height happens over 14.8 m — under the
 * four-post floor, on a layer whose slope filter admitted anything up to 40.
 */
export function riserWidthM(stepM: number, sharpness: number, slope: number): number {
  // Transcribed from `terraceAt`; the two must not drift.
  const gain = 1 + sharpness * 8;
  return stepM / gain / slope;
}

/** Quantises a height into bands of `stepM`, with `sharpness` controlling how much of a band the rise occupies. */
export function terraceAt(h: number, stepM: number, sharpness: number): number {
  const k = h / stepM;
  const band = Math.floor(k);
  const frac = k - band;
  // Gain 1 at sharpness 0 is the exact identity, which is what makes the parameter safe to sweep up from nothing.
  const gain = 1 + sharpness * 8;
  const shaped = Math.min(Math.max((frac - 0.5) * gain + 0.5, 0), 1);
  return (band + shaped) * stepM;
}

/**
 * A boundary's weight at a planet point: 1 well inside, 0 outside, a Hermite ramp across `featherM` just inside the edge.
 *
 * Feathering **inward** rather than straddling the edge is deliberate: a layer then never reaches outside its declared
 * shape, which is the property `test/terrain-layers.test.ts` asserts and the thing that keeps one region's landform from
 * leaking into its neighbour.
 */
export function boundaryWeight(boundary: Boundary, x: number, z: number): number {
  switch (boundary.kind) {
    case 'circle':
      return feather(boundary.radiusM - distance(x - boundary.x, z - boundary.z), boundary.featherM);
    case 'rect': {
      const dx = boundary.halfXM - Math.abs(x - boundary.x);
      const dz = boundary.halfZM - Math.abs(z - boundary.z);
      return feather(Math.min(dx, dz), boundary.featherM);
    }
    case 'polygon':
      return feather(signedDistanceInPolygon(boundary.points, x, z), boundary.featherM);
    case 'polyline':
      return feather(boundary.halfWidthM - distanceToPolyline(boundary.points, x, z), boundary.featherM);
  }
}

/** A Hermite ramp over `width` metres inside the edge. `inside` is the distance in, negative outside. */
function feather(inside: number, width: number): number {
  if (inside <= 0) {
    return 0;
  }

  if (width <= 0 || inside >= width) {
    return 1;
  }

  const t = inside / width;
  return t * t * (3 - 2 * t);
}

function filterWeight(filter: Filter, grid: HeightGrid, ix: number, iz: number): number {
  if (filter.kind === 'height') {
    return bandWeight(grid.height[(iz - grid.rowOffset) * grid.posts + ix], filter.minM, filter.maxM, filter.featherM);
  }

  return bandWeight(slopeAtPost(grid, ix, iz), filter.min, filter.max, filter.feather);
}

/**
 * 1 inside `[min, max]`, ramping to 0 over `feather` on each side.
 *
 * **NaN weighs nothing.** A slope read outside a row window is NaN by design (see `slopeAtPost`), and both `value < min`
 * and `value > max` are false for it — so without this line the function falls through to `return 1` and applies the layer
 * at **full strength** exactly where it knows nothing. Today the halo is wide enough that the NaN never reaches a row the
 * band keeps, so the guard changes no baked value; it stops a fourth slope-filtered layer, or one reading two posts out,
 * from turning that margin into a stripe of rock along every band boundary whose shape depends on the core count.
 */
function bandWeight(value: number, min: number, max: number, feather: number): number {
  if (Number.isNaN(value)) {
    return 0;
  }

  if (value < min) {
    return feather <= 0 ? 0 : rampUp((value - (min - feather)) / feather);
  }

  if (value > max) {
    return feather <= 0 ? 0 : rampUp((max + feather - value) / feather);
  }

  return 1;
}

function rampUp(t: number): number {
  const c = Math.min(Math.max(t, 0), 1);
  return c * c * (3 - 2 * c);
}

/**
 * Slope at a post as rise over run, from a central difference of its neighbours.
 *
 * Edge posts take a one-sided difference rather than wrapping: the grid is a planet, not a torus, and wrapping would put a
 * cliff along the seam that a slope-filtered layer would then dutifully decorate.
 */
export function slopeAtPost(grid: HeightGrid, ix: number, iz: number): number {
  const { posts, spacingM, rowOffset, height } = grid;
  const x0 = ix > 0 ? ix - 1 : ix;
  const x1 = ix + 1 < posts ? ix + 1 : ix;
  // The rim is the PLANET's, never the window's: a band that mistook its own first row for the edge of the world would
  // take a one-sided difference in the middle of open ground, and put a seam there.
  const z0 = iz > 0 ? iz - 1 : iz;
  const z1 = iz + 1 < posts ? iz + 1 : iz;
  const runX = (x1 - x0) * spacingM;
  const runZ = (z1 - z0) * spacingM;
  const here = (iz - rowOffset) * posts;
  const dx = runX === 0 ? 0 : (height[here + x1] - height[here + x0]) / runX;
  const dz = runZ === 0 ? 0 : (height[(z1 - rowOffset) * posts + ix] - height[(z0 - rowOffset) * posts + ix]) / runZ;
  return distance(dx, dz);
}

/**
 * Length of a 2-D vector, spelled out rather than `Math.hypot`.
 *
 * **`Math.hypot` is not specified to be correctly rounded and `Math.sqrt` is** (IEEE-754, as are `*` and `+`). Every
 * distance below feeds a boundary weight or a slope filter, and both feed the baked heights that a C# twin must
 * reproduce bit for bit — so a function whose last bit is the engine's business has no place in the bake. Checked when
 * this changed: the golden did not move, so V8's `hypot` happened to agree. Happening to agree is not the contract.
 */
function distance(dx: number, dz: number): number {
  return Math.sqrt(dx * dx + dz * dz);
}

/** Positive inside the polygon, negative outside; the magnitude is the distance to the nearest edge. */
function signedDistanceInPolygon(points: readonly number[], x: number, z: number): number {
  const count = points.length >> 1;
  let inside = false;
  let nearest = Infinity;
  let jx = points[(count - 1) * 2];
  let jz = points[(count - 1) * 2 + 1];
  for (let i = 0; i < count; i++) {
    const vx = points[i * 2];
    const vz = points[i * 2 + 1];
    if (vz > z !== jz > z && x < ((jx - vx) * (z - vz)) / (jz - vz) + vx) {
      inside = !inside;
    }

    nearest = Math.min(nearest, distanceToSegment(x, z, vx, vz, jx, jz));
    jx = vx;
    jz = vz;
  }

  return inside ? nearest : -nearest;
}

function distanceToPolyline(points: readonly number[], x: number, z: number): number {
  const count = points.length >> 1;
  let nearest = Infinity;
  for (let i = 1; i < count; i++) {
    nearest = Math.min(
      nearest,
      distanceToSegment(x, z, points[(i - 1) * 2], points[(i - 1) * 2 + 1], points[i * 2], points[i * 2 + 1]),
    );
  }

  return nearest;
}

function distanceToSegment(px: number, pz: number, ax: number, az: number, bx: number, bz: number): number {
  const abx = bx - ax;
  const abz = bz - az;
  const denominator = abx * abx + abz * abz;
  const t = denominator <= 0 ? 0 : Math.min(Math.max(((px - ax) * abx + (pz - az) * abz) / denominator, 0), 1);
  return distance(px - (ax + abx * t), pz - (az + abz * t));
}

/** Re-exported so a layer tree can be written without importing two modules. */
export { fractalAt, valueNoise };
