import { describe, expect, it } from 'vitest';
import { fractalAt, hashPost, unitAtPost, valueNoise, warpPoint, type FractalSpec } from '../src/terrain/hash';
import { Heightfield } from '../src/terrain/heightfield';
import { createHeightGrid } from '../src/terrain/layers';
import { bakeTatooine } from '../src/terrain/tatooine-terrain';
import goldenFile from './golden/terrain-hash.json';

/**
 * The noise primitive, and the golden vectors that hold a second implementation to it.
 *
 * The point of these is not that the numbers are right — any numbers would be "right" for invented terrain. It is that
 * **the same post gives the same bits in every runtime**, because the moment two runtimes disagree about height, entities
 * float above the ground or sink into it, and the symptom is per-driver. So the file under `test/golden/` is the contract:
 * the C# twin reads it and asserts, and nothing regenerates it casually.
 */

interface GoldenGrid {
  readonly posts: number;
  readonly spacingM: number;
  readonly originM: number;
  readonly minHeightM: string;
  readonly maxHeightM: string;
  readonly posts_: readonly { readonly ix: number; readonly iz: number; readonly bits: string }[];
}

interface GoldenFile {
  readonly hashPost: readonly {
    readonly ix: number;
    readonly iz: number;
    readonly seed: number;
    readonly u32: number;
  }[];
  readonly valueNoise: readonly {
    readonly x: number;
    readonly z: number;
    readonly seed: number;
    readonly bits: string;
  }[];
  readonly fractal: readonly { readonly x: number; readonly z: number; readonly bits: string }[];
  readonly ridged: readonly { readonly x: number; readonly z: number; readonly bits: string }[];
  readonly warp: readonly { readonly x: number; readonly z: number; readonly xBits: string; readonly zBits: string }[];
  readonly fractalSpec: FractalSpec;
  readonly ridgedSpec: FractalSpec;
  readonly grid: GoldenGrid;
}

/** The exact bits of a double, as hex — the only honest way to write "identical" down in a JSON file. */
function bitsOf(v: number): string {
  const buffer = new ArrayBuffer(8);
  new Float64Array(buffer)[0] = v;
  return [...new Uint8Array(buffer)].map((b) => b.toString(16).padStart(2, '0')).join('');
}

const golden = goldenFile as GoldenFile;

/** Mean, extremes and standard deviation of a fractal term over a coarse grid of the whole planet. */
function spread(spec: FractalSpec): { mean: number; min: number; max: number; deviation: number } {
  const side = 200;
  const step = 16384 / side;
  let sum = 0;
  let sumSquares = 0;
  let min = Infinity;
  let max = -Infinity;
  for (let iz = 0; iz < side; iz++) {
    for (let ix = 0; ix < side; ix++) {
      const v = fractalAt(spec, -8192 + ix * step, -8192 + iz * step);
      sum += v;
      sumSquares += v * v;
      min = Math.min(min, v);
      max = Math.max(max, v);
    }
  }

  const n = side * side;
  const mean = sum / n;
  return { mean, min, max, deviation: Math.sqrt(Math.max(sumSquares / n - mean * mean, 0)) };
}

describe('hashPost', () => {
  it('is a u32 for every input, including negative lattice points', () => {
    for (const ix of [-2147483648, -70000, -1, 0, 1, 12345, 2147483647]) {
      for (const iz of [-2147483648, -3, 0, 7, 2147483647]) {
        const h = hashPost(ix, iz, 0x1234);
        expect(Number.isInteger(h)).toBe(true);
        expect(h).toBeGreaterThanOrEqual(0);
        expect(h).toBeLessThanOrEqual(0xffffffff);
      }
    }
  });

  it('separates neighbours: adjacent posts share no low-order structure', () => {
    // Adjacent posts differing by 1 in one axis must not be close in value — a hash that fails this shows as banding.
    let close = 0;
    for (let i = 0; i < 4096; i++) {
      const a = unitAtPost(i, 17, 99);
      const b = unitAtPost(i + 1, 17, 99);
      if (Math.abs(a - b) < 0.01) {
        close++;
      }
    }

    // ~1 % of 4096 pairs land within 0.01 by chance; a structured hash lands far more.
    expect(close).toBeLessThan(120);
  });

  it('gives a different field per seed', () => {
    expect(hashPost(5, 9, 1)).not.toBe(hashPost(5, 9, 2));
    expect(hashPost(5, 9, 1)).not.toBe(hashPost(9, 5, 1));
  });

  it('matches the golden vectors bit for bit', () => {
    for (const row of golden.hashPost) {
      expect(hashPost(row.ix, row.iz, row.seed)).toBe(row.u32);
    }
  });
});

describe('valueNoise', () => {
  it('stays in [0, 1)', () => {
    for (let i = 0; i < 2000; i++) {
      const v = valueNoise(i * 0.37 - 300, i * -0.71 + 55, 0x2222);
      expect(v).toBeGreaterThanOrEqual(0);
      expect(v).toBeLessThan(1);
    }
  });

  it('interpolates its lattice: at a lattice point it IS the post', () => {
    for (const [ix, iz] of [
      [0, 0],
      [-4, 11],
      [7, -3],
    ]) {
      expect(valueNoise(ix, iz, 77)).toBeCloseTo(unitAtPost(ix, iz, 77), 12);
    }
  });

  it('is continuous across a lattice boundary', () => {
    // Either side of the integer line x = 3, a millionth of a cell apart. A discontinuity here is a visible crease.
    const below = valueNoise(3 - 1e-6, 0.4, 31);
    const above = valueNoise(3 + 1e-6, 0.4, 31);
    expect(Math.abs(above - below)).toBeLessThan(1e-5);
  });

  it('matches the golden vectors bit for bit', () => {
    for (const row of golden.valueNoise) {
      expect(bitsOf(valueNoise(row.x, row.z, row.seed))).toBe(row.bits);
    }
  });
});

describe('fractalAt', () => {
  const spec = golden.fractalSpec;

  it('stays in [0, 1] and keeps its mean near the middle whatever the octave count', () => {
    // Sampled over a GRID of the whole planet, and asserted as a drift between octave counts rather than as an absolute
    // band. Two earlier versions walked a 5 km line — about two periods of the shipping wavelength — and read whatever
    // that stretch happened to contain; the second failed at 0.676 against a 0.65 bound the moment the wavelength was
    // retuned, without the property it is named for having changed at all. A mean over two periods is not a mean.
    const means = [1, 3, 5, 8].map((octaves) => spread({ ...spec, octaves }));
    for (const m of means) {
      expect(m.min).toBeGreaterThanOrEqual(0);
      expect(m.max).toBeLessThanOrEqual(1);
    }

    // The property: normalising by the summed amplitude keeps ELEVATION put while octaves add detail. Every count lands
    // within a twentieth of every other, which no unnormalised sum would.
    const lowest = Math.min(...means.map((m) => m.mean));
    const highest = Math.max(...means.map((m) => m.mean));
    expect(highest - lowest).toBeLessThan(0.05);
    expect(lowest).toBeGreaterThan(0.35);
    expect(highest).toBeLessThan(0.65);
  });

  it('ridged keeps far more dynamic range than plain at the same octaves — that is what makes crests and basins', () => {
    // MEASURED over a 300 × 300 grid of the whole planet, not reasoned: ridged spans 0.06…0.96 where plain spans
    // 0.117…0.80, and their means are within 0.02 of each other. So the discriminating property is the SPREAD, not the
    // mean. Two earlier versions of this test asserted a downward mean shift — the analytic 1/3 for a uniform input — and
    // both failed, because interpolated value noise is bell-shaped around 0.5, not uniform.
    // Stated as RATIOS between the two, not as absolute bounds. The absolutes were calibrated against the pre-retune mesa
    // wavelengths and went stale the moment those changed — an absolute bound on `plain` failed at 0.767 against a 0.75
    // limit while the property the test is named for still held comfortably. Measured at the shipping spec: ridged spans
    // 0.029…0.960 (sd 0.181), plain 0.086…0.854 (sd 0.130).
    const ridged = spread(golden.ridgedSpec);
    const plain = spread({ ...golden.ridgedSpec, ridged: false });
    expect(ridged.max - ridged.min).toBeGreaterThan((plain.max - plain.min) * 1.15);
    expect(ridged.deviation).toBeGreaterThan(plain.deviation * 1.2);
    expect(ridged.min).toBeLessThan(plain.min);
    expect(ridged.max).toBeGreaterThan(plain.max);
  });

  it('separates octaves: a shared seed would make octave n+1 a copy of octave n', () => {
    // With per-octave seeds, one octave of a spec and five octaves of it are genuinely different fields.
    const one = fractalAt({ ...spec, octaves: 1 }, 123, 456);
    const five = fractalAt({ ...spec, octaves: 5 }, 123, 456);
    expect(Math.abs(one - five)).toBeGreaterThan(1e-6);
  });

  it('matches the golden vectors bit for bit', () => {
    for (const row of golden.fractal) {
      expect(bitsOf(fractalAt(spec, row.x, row.z))).toBe(row.bits);
    }
  });
});

describe('warpPoint', () => {
  it('displaces by at most the requested amount, on both axes independently', () => {
    const out = new Float64Array(2);
    for (let i = 0; i < 500; i++) {
      const x = i * 31.4 - 4000;
      const z = i * -17.7 + 900;
      warpPoint(x, z, 0x99, 200, 2000, out);
      expect(Math.abs(out[0] - x)).toBeLessThanOrEqual(200);
      expect(Math.abs(out[1] - z)).toBeLessThanOrEqual(200);
    }
  });

  it('warps the two axes differently — one shared offset would just translate the field', () => {
    const out = new Float64Array(2);
    warpPoint(100, 100, 0x99, 200, 2000, out);
    expect(out[0] - 100).not.toBeCloseTo(out[1] - 100, 6);
  });
});

describe('the ridged fold', () => {
  it('matches the golden vectors bit for bit', () => {
    // The mesa belt's own spec, at the SHIPPING wavelengths. Without these, a twin that dropped the square or folded about
    // the wrong midline passed every vector in this file: the only other thing exercising `ridge` was a statistical test,
    // and the spec the goldens carried was the pre-retune one.
    for (const row of golden.ridged) {
      expect(bitsOf(fractalAt(golden.ridgedSpec, row.x, row.z))).toBe(row.bits);
    }
  });

  it('is pinned at the wavelengths the planet actually ships', () => {
    expect([golden.ridgedSpec.wavelengthXM, golden.ridgedSpec.wavelengthZM]).toEqual([1300, 1150]);
  });
});

describe('warpPoint golden', () => {
  it('matches bit for bit on both axes', () => {
    // The dune sea's domain warp, which had no vector at all: neither its `seed ^ 0x2545f491` derivation nor its default
    // wavelength was reachable from any assertion.
    const out = new Float64Array(2);
    for (const row of golden.warp) {
      warpPoint(row.x, row.z, 0x7a700151, 260, 2600, out);
      expect(bitsOf(out[0])).toBe(row.xBits);
      expect(bitsOf(out[1])).toBe(row.zBits);
    }
  });
});

describe('the baked grid', () => {
  it('reproduces the golden posts bit for bit — this is the contract a C# twin must meet', () => {
    // Everything else samples this grid; the modules call it THE contract and nothing pinned it. One coarse bake covers,
    // in one artefact, what no individual vector reaches: the feather and band curves, terracing, the blend modes, the f32
    // weight and accumulator round-trips, the bounding-box sweep, and the two-stage order that reads each pad's constant
    // out of the finished landform.
    //
    // It is pinned AT THIS RESOLUTION on purpose: pad constants are read off the f32 grid, so they are resolution
    // dependent, and a twin must bake this configuration to compare against this file.
    const g = golden.grid;
    const field = new Heightfield(createHeightGrid(g.posts, g.spacingM, g.originM));
    bakeTatooine(field);

    for (const post of g.posts_) {
      expect(bitsOf(field.grid.height[post.iz * g.posts + post.ix]), `post (${post.ix}, ${post.iz})`).toBe(post.bits);
    }

    expect(bitsOf(field.minHeightM)).toBe(g.minHeightM);
    expect(bitsOf(field.maxHeightM)).toBe(g.maxHeightM);
  });
});
