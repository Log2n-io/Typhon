import { POST_SPACING_M } from '../src/terrain/heightfield';
import { applyLayers, createHeightGrid, type TerrainLayer } from '../src/terrain/layers';
import { landformLayers } from '../src/terrain/tatooine-terrain';

/**
 * Sweeps the two layers that make cones, together, because separately they do not explain the count.
 *
 * ```
 * npx vite-node tools/spike-sweep.ts
 * ```
 *
 * `cliff detail` alone makes 45 cones over this window and the terraces alone make 38, but the two together make 271 —
 * five times the sum. They are not independent: the terraces build steep RISERS, `cliff detail` is filtered to fire on
 * steep ground, and slope is measured as a central difference over adjacent posts, so a riser a few posts wide reads as a
 * cliff face and gets decorated with the finest noise in the tree. The sweep is over both at once for that reason.
 */

const POSTS = 1024;
const ORIGIN_M = -2048;

function cones(height: Float32Array, dropM: number): number {
  let count = 0;
  for (let z = 1; z < POSTS - 1; z++) {
    for (let x = 1; x < POSTS - 1; x++) {
      const h = height[z * POSTS + x];
      const drop = Math.min(
        h - height[z * POSTS + x - 1],
        h - height[z * POSTS + x + 1],
        h - height[(z - 1) * POSTS + x],
        h - height[(z + 1) * POSTS + x],
      );
      if (drop > dropM) {
        count++;
      }
    }
  }

  return count;
}

function relief(height: Float32Array): number {
  let lo = Infinity;
  let hi = -Infinity;
  for (const h of height) {
    if (h < lo) {
      lo = h;
    }

    if (h > hi) {
      hi = h;
    }
  }

  return hi - lo;
}

interface Knobs {
  sharpness: number;
  slopeMax: number;
  cliffMin: number;
  cliffAmp: number;
  octaves: number;
  waveZ: number;
}

function build({ sharpness, slopeMax, cliffMin, cliffAmp, octaves, waveZ }: Knobs): TerrainLayer[] {
  const layers = landformLayers();
  for (const layer of layers) {
    if (layer.affector.kind === 'terrace') {
      layer.affector = { ...layer.affector, sharpness };
      const slope = layer.filters[0];
      layer.filters = [{ ...slope, kind: 'slope', max: slopeMax } as typeof slope];
    }

    if (layer.name === 'cliff detail' && layer.affector.kind === 'fractal') {
      layer.affector = {
        ...layer.affector,
        amplitudeM: cliffAmp,
        biasM: -cliffAmp / 3,
        fractal: { ...layer.affector.fractal, octaves, wavelengthXM: waveZ * 1.18, wavelengthZM: waveZ },
      };
      layer.filters = [{ kind: 'slope', min: cliffMin, max: 40, feather: 0.2 }];
    }
  }

  return layers;
}

function bake(layers: readonly TerrainLayer[]): Float32Array {
  const grid = createHeightGrid(POSTS, POST_SPACING_M, ORIGIN_M);
  grid.height.fill(0);
  applyLayers(grid, layers);
  return grid.height;
}

const base: Knobs = { sharpness: 0.34, slopeMax: 0.5, cliffMin: 0.35, cliffAmp: 15, octaves: 3, waveZ: 220 };

const cases: [string, Partial<Knobs>][] = [
  ['before the retune', { sharpness: 0.6, slopeMax: 40, octaves: 4, waveZ: 160 }],
  ['D .08/400/8', { sharpness: 0.08, slopeMax: 40, waveZ: 400, cliffAmp: 8 }],
  ['D9 .08/400/9 (exact bias -3)', { sharpness: 0.08, slopeMax: 40, waveZ: 400, cliffAmp: 9 }],
  ['D9 wider .08/450/9', { sharpness: 0.08, slopeMax: 40, waveZ: 450, cliffAmp: 9 }],
];

console.log(`window ${POSTS} posts at ${POST_SPACING_M} m; a cone drops more than N m to ALL FOUR neighbours\n`);
console.log(`${'variant'.padEnd(34)}${'>1m'.padStart(8)}${'>1.5m'.padStart(8)}${'>3m'.padStart(8)}${'relief'.padStart(10)}`);
for (const [name, over] of cases) {
  const height = bake(build({ ...base, ...over }));
  const row = [cones(height, 1), cones(height, 1.5), cones(height, 3)].map((c) => String(c).padStart(8)).join('');
  console.log(`${name.padEnd(34)}${row}${`${relief(height).toFixed(0)} m`.padStart(10)}`);
}
