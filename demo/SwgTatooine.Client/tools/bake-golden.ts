import { mkdirSync, writeFileSync } from 'node:fs';
import { fractalAt, hashPost, valueNoise, warpPoint, type FractalSpec } from '../src/terrain/hash';
import { Heightfield } from '../src/terrain/heightfield';
import { createHeightGrid } from '../src/terrain/layers';
import { bakeTatooine } from '../src/terrain/tatooine-terrain';

/**
 * Regenerates `test/golden/terrain-hash.json`.
 *
 * **Run this whenever the layer tree changes**, and read the diff before committing it: the grid section pins the baked
 * planet, so a retune is supposed to show up here. That is the golden doing its job, not an obstacle — but it also means a
 * regeneration is the one moment nothing is checking the terrain, so look at the numbers.
 *
 * ```
 * npx vite-node tools/bake-golden.ts
 * ```
 *
 * The hash, noise, fractal, ridged and warp sections should NOT change unless `hash.ts` itself changed; if they move
 * because of a tree edit, something is wrong.
 */

function bitsOf(v: number): string {
  const buffer = new ArrayBuffer(8);
  new Float64Array(buffer)[0] = v;
  return [...new Uint8Array(buffer)].map((b) => b.toString(16).padStart(2, '0')).join('');
}

/**
 * A FIXED reference term, deliberately not tracking the layer tree: its job is to pin `fractalAt`, so it must not move
 * when the tree is retuned. It was the shipping base-swells spec when this file was written.
 */
const fractalSpec: FractalSpec = {
  seed: 0x7a700100,
  octaves: 5,
  wavelengthXM: 2400,
  wavelengthZM: 2900,
  lacunarity: 2.07,
  gain: 0.5,
  ridged: false,
};

/** The ridged counterpart, fixed for the same reason. */
const ridgedSpec: FractalSpec = {
  seed: 0x7a7001a3,
  octaves: 5,
  wavelengthXM: 1300,
  wavelengthZM: 1150,
  lacunarity: 2.11,
  gain: 0.46,
  ridged: true,
};

const GRID_POSTS = 128;
const GRID_SPACING_M = 16384 / GRID_POSTS;

const posts: { ix: number; iz: number; seed: number; u32: number }[] = [];
for (const [ix, iz, seed] of [
  [0, 0, 0],
  [1, 0, 0],
  [0, 1, 0],
  [-1, -1, 0],
  [12345, -6789, 0x7a700100],
  [-2147483648, 2147483647, 0x7a700100],
  [1024, 1024, 0x7a7001a3],
  [7, 13, 0x9e3779b1],
]) {
  posts.push({ ix: ix, iz: iz, seed: seed, u32: hashPost(ix, iz, seed) });
}

const noise: { x: number; z: number; seed: number; bits: string }[] = [];
for (const [x, z] of [
  [0, 0],
  [0.5, 0.5],
  [-3.25, 11.75],
  [1234.5678, -987.6543],
  [1e-7, 1 - 1e-7],
]) {
  noise.push({ x: x, z: z, seed: 0x7a700100, bits: bitsOf(valueNoise(x, z, 0x7a700100)) });
}

const sample = [
  [0, 0],
  [3460, -4768],
  [-2890, 2198],
  [8184, 8184],
  [-8192, -8192],
  [123.456, -789.012],
];
const fractal = sample.map(([x, z]) => ({ x: x, z: z, bits: bitsOf(fractalAt(fractalSpec, x, z)) }));
const ridged = sample.map(([x, z]) => ({ x: x, z: z, bits: bitsOf(fractalAt(ridgedSpec, x, z)) }));

const out = new Float64Array(2);
const warp = sample.map(([x, z]) => {
  warpPoint(x, z, 0x7a700151, 260, 2600, out);
  return { x: x, z: z, xBits: bitsOf(out[0]), zBits: bitsOf(out[1]) };
});

const field = new Heightfield(createHeightGrid(GRID_POSTS, GRID_SPACING_M, -8192));
bakeTatooine(field);
const grid: { ix: number; iz: number; bits: string }[] = [];
for (let iz = 3; iz < GRID_POSTS; iz += 17) {
  for (let ix = 5; ix < GRID_POSTS; ix += 19) {
    grid.push({ ix, iz, bits: bitsOf(field.grid.height[iz * GRID_POSTS + ix]) });
  }
}

/**
 * FNV-1a over the field's raw bytes, as two 32-bit halves so neither runtime needs 64-bit integers.
 *
 * The 128-post grid above pins the tree; this pins the WHOLE field at a resolution where the parts that depend on
 * resolution actually differ — boundary ranges in posts, the grid rim the slope filter reads, and the f32 rounding of
 * sixteen times as many accumulated values. A digest rather than a fixture because the alternative is a 1 MB file in the
 * repository, and a digest is the same evidence.
 */
function digestOf(values: Float32Array): string {
  const bytes = new Uint8Array(values.buffer, values.byteOffset, values.byteLength);
  let hi = 0x811c9dc5;
  let lo = 0x811c9dc5;
  for (let i = 0; i < bytes.length; i++) {
    lo = Math.imul(lo ^ bytes[i], 0x01000193) >>> 0;
    hi = Math.imul(hi ^ lo, 0x01000193) >>> 0;
  }

  return hi.toString(16).padStart(8, '0') + lo.toString(16).padStart(8, '0');
}

const FIELD_POSTS = 512;
const fieldField = new Heightfield(createHeightGrid(FIELD_POSTS, 16384 / FIELD_POSTS, -8192));
bakeTatooine(fieldField);

mkdirSync('test/golden', { recursive: true });
writeFileSync(
  'test/golden/terrain-hash.json',
  JSON.stringify(
    {
      hashPost: posts,
      valueNoise: noise,
      fractal,
      ridged,
      warp,
      fractalSpec,
      ridgedSpec,
      grid: {
        posts: GRID_POSTS,
        spacingM: GRID_SPACING_M,
        originM: -8192,
        minHeightM: bitsOf(field.minHeightM),
        maxHeightM: bitsOf(field.maxHeightM),
        posts_: grid,
      },
      field: {
        posts: FIELD_POSTS,
        spacingM: 16384 / FIELD_POSTS,
        originM: -8192,
        digest: digestOf(fieldField.grid.height),
      },
    },
    null,
    2,
  ) + '\n',
);

console.log(
  `terrain-hash.json rewritten — ${grid.length} grid posts, relief ${(field.maxHeightM - field.minHeightM).toFixed(1)} m ` +
    `(${field.minHeightM.toFixed(1)} … ${field.maxHeightM.toFixed(1)})`,
);
