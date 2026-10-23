/**
 * The terrain's noise primitive, specified as an **integer hash** rather than the usual `fract(sin(dot(…)))`.
 *
 * Three runtimes have to produce the same height for the same post, or entities float above the ground or sink into it:
 * the client (here), the server if it ever samples height, and — indirectly — the GPU, which samples the baked field as a
 * texture. A float hash cannot deliver that: GPU `fract`/`sin` hashes are not bit-identical across drivers, and neither
 * `Math.sin` nor `double`'s `Sin` is specified to the last bit. So everything below the final interpolation is `u32`
 * multiply / xor / shift — exact in JavaScript through `Math.imul` and `>>> 0`, exact in C# through `unchecked` `uint`,
 * and reproducible in GLSL ES 3.0, which has real integer operations.
 *
 * The float part is `double` throughout (`lerp` and the smoothstep), which IEEE-754 pins exactly given the same operation
 * order — so the twin in C# must keep the same order, and `test/terrain-hash.test.ts` emits the golden vectors that hold
 * it to that.
 */

/** 2⁻³², the exact scale from a `u32` to `[0, 1)`. A power of two, so the multiply is lossless. */
const U32_TO_UNIT = 2.3283064365386963e-10;

/**
 * Finalises a hash: xorshift-multiply avalanche, so two posts one metre apart share no visible structure.
 *
 * The constants are the well-known 32-bit MurmurHash3 finaliser; nothing here depends on them beyond their being odd and
 * well-mixed, but they must be identical in every twin.
 */
function avalanche(h: number): number {
  let x = h;
  x ^= x >>> 16;
  x = Math.imul(x, 0x85ebca6b);
  x ^= x >>> 13;
  x = Math.imul(x, 0xc2b2ae35);
  x ^= x >>> 16;
  return x >>> 0;
}

/**
 * Hashes an integer lattice point to a `u32`.
 *
 * @param ix Lattice x, any 32-bit integer.
 * @param iz Lattice z, any 32-bit integer.
 * @param seed The layer's seed: two layers with the same geometry and different seeds share no structure.
 */
export function hashPost(ix: number, iz: number, seed: number): number {
  let h = seed | 0;
  h = Math.imul(h ^ (ix | 0), 0x27d4eb2d);
  h = Math.imul(h ^ (iz | 0), 0x165667b1);
  return avalanche(h);
}

/** The lattice point's value in `[0, 1)`. */
export function unitAtPost(ix: number, iz: number, seed: number): number {
  return hashPost(ix, iz, seed) * U32_TO_UNIT;
}

/** Hermite ease, `3t² − 2t³`, the smoothstep the interpolation uses. Defined once so every twin eases identically. */
function ease(t: number): number {
  return t * t * (3 - 2 * t);
}

/**
 * Value noise in `[0, 1)`: the four surrounding lattice values, eased and bilinearly blended.
 *
 * Value noise rather than gradient (Perlin) noise because the lattice value IS the hash — there is no gradient vector to
 * agree on, so the cross-runtime contract is as small as it can be. Its one weakness, axis-aligned structure at low
 * octave counts, is invisible once five octaves are summed at a lacunarity that is not exactly 2.
 */
export function valueNoise(x: number, z: number, seed: number): number {
  const ix = Math.floor(x);
  const iz = Math.floor(z);
  const ux = ease(x - ix);
  const uz = ease(z - iz);
  const a = unitAtPost(ix, iz, seed);
  const b = unitAtPost(ix + 1, iz, seed);
  const c = unitAtPost(ix, iz + 1, seed);
  const d = unitAtPost(ix + 1, iz + 1, seed);
  const top = a + (b - a) * ux;
  const bottom = c + (d - c) * ux;
  return top + (bottom - top) * uz;
}

/**
 * A fractal noise term: the parameter set of SWG's `MapFractal`, which is worth copying as a *shape* because it is a good
 * one — independent x/z wavelengths (so a layer can be stretched along a prevailing wind), lacunarity, gain, and a ridged
 * mode for the sharp-crested landforms octave noise cannot make.
 */
export interface FractalSpec {
  /** Distinguishes this term from every other. */
  readonly seed: number;
  /** How many octaves to sum. Five or six is the useful range; more is invisible at 8 m posts. */
  readonly octaves: number;
  /** Wavelength of the first octave along x, in metres. */
  readonly wavelengthXM: number;
  /** Wavelength of the first octave along z, in metres. */
  readonly wavelengthZM: number;
  /**
   * Frequency ratio between octaves. Deliberately **not** exactly 2: at exactly 2 the lattices of successive octaves
   * coincide on the same integer posts and the axis-aligned structure of value noise reinforces into a visible grid.
   */
  readonly lacunarity: number;
  /** Amplitude ratio between octaves. ~0.5 gives the usual 1/f spectrum. */
  readonly gain: number;
  /**
   * Ridged mode: each octave becomes `(1 − |2n − 1|)²`, which folds the noise about its midline and squares the fold.
   * The creases become ridge lines and the squaring flattens the basins between them — the mesa-and-canyon look.
   */
  readonly ridged: boolean;
}

/**
 * Evaluates a fractal term at a planet coordinate, normalised to `[0, 1]`.
 *
 * Normalising by the summed amplitude rather than by the theoretical maximum keeps the output's *mean* near 0.5 whatever
 * the octave count, so changing `octaves` changes the detail and not the elevation — which matters because the authored
 * tree tunes amplitudes by eye.
 */
export function fractalAt(spec: FractalSpec, x: number, z: number): number {
  let fx = 1 / spec.wavelengthXM;
  let fz = 1 / spec.wavelengthZM;
  let amplitude = 1;
  let sum = 0;
  let total = 0;
  for (let o = 0; o < spec.octaves; o++) {
    // Each octave takes its own seed so the octaves are independent fields rather than one field at several zooms; with a
    // shared seed, a lacunarity near 2 makes octave n+1 partly a copy of octave n.
    // `| 0` here, not only inside the hash. `spec.seed + o * 0x9e3779b1` reaches 1.06e10 in JS doubles, which works only
    // because `hashPost` re-truncates; the same expression in C# `int` arithmetic overflows, and under `checked` it throws.
    // Reducing it at the source is what makes the twin a transcription rather than a puzzle.
    const n = valueNoise(x * fx, z * fz, (spec.seed + o * 0x9e3779b1) | 0);
    const shaped = spec.ridged ? ridge(n) : n;
    sum += shaped * amplitude;
    total += amplitude;
    fx *= spec.lacunarity;
    fz *= spec.lacunarity;
    amplitude *= spec.gain;
  }

  return sum / total;
}

/**
 * The narrowest feature a fractal spec can author, in metres.
 *
 * **This is the number a layer has to be checked against, and the ridged case is why it is a function and not a comment.**
 * Plain fBm's finest octave is its shortest wavelength divided by `lacunarity^(octaves−1)`. A RIDGED octave is folded
 * about its midline by {@link ridge}, which turns one smooth hump into two creases — so the period the post grid actually
 * has to carry is **half** the octave's own wavelength.
 *
 * Missing that halving is exactly how `cliff detail` passed a 17.5 m check while authoring 8.8 m creases on a 4 m grid,
 * and put the one-post pyramids back that the same layer had been retuned to remove. The check had the right formula for
 * the wrong signal.
 */
export function finestFeatureM(spec: FractalSpec): number {
  const finest = Math.min(spec.wavelengthXM, spec.wavelengthZM) / spec.lacunarity ** (spec.octaves - 1);
  return spec.ridged ? finest * 0.5 : finest;
}

/** One ridged octave: fold about the midline, then square. */
function ridge(n: number): number {
  const folded = 1 - Math.abs(2 * n - 1);
  return folded * folded;
}

/**
 * Warps the sample point by a second noise field before the first reads it — the cheapest way to break the
 * "everything-is-a-blob" signature of raw fBm, and what makes a dune sea read as wind-driven rather than as noise.
 *
 * @param amountM How far a point may be displaced, in metres.
 * @param wavelengthM Wavelength of the warp field; much longer than the warped field's, or the result is mush.
 * @param out Receives the warped `(x, z)`. Caller-owned, so a bake allocates nothing.
 */
export function warpPoint(
  x: number,
  z: number,
  seed: number,
  amountM: number,
  wavelengthM: number,
  out: Float64Array,
): void {
  const f = 1 / wavelengthM;
  out[0] = x + (valueNoise(x * f, z * f, seed) * 2 - 1) * amountM;
  out[1] = z + (valueNoise(x * f, z * f, seed ^ 0x5bf03635) * 2 - 1) * amountM;
}
