import { POST_SPACING_M } from '../src/terrain/heightfield';
import { applyLayers, createHeightGrid, type TerrainLayer } from '../src/terrain/layers';
import { landformLayers } from '../src/terrain/tatooine-terrain';

/**
 * Counts the one-post pyramids in the authored planet, for the tree against variants of itself.
 *
 * ```
 * npx vite-node tools/spike-probe.ts
 * ```
 *
 * **What a pyramid is, measured rather than described.** A feature narrower than two posts lands on a single post; its
 * four neighbours do not rise with it; and bilinear interpolation over one raised post surrounded by four lower ones is a
 * four-sided cone. So the signature is a strict local maximum whose drop to *every* one of its four neighbours exceeds a
 * threshold — real terrain has ridges and saddles, but a ridge post is high along one axis and level along the other.
 *
 * One variant per changed layer, because a single before/after number cannot say which edit moved it, and the first run of
 * this probe moved the total in the wrong direction.
 */

const POSTS = 1024;
const ORIGIN_M = -2048;
const DROPS_M = [1.0, 1.5, 3.0];

function bake(layers: readonly TerrainLayer[]): Float32Array {
  const grid = createHeightGrid(POSTS, POST_SPACING_M, ORIGIN_M);
  grid.height.fill(0);
  applyLayers(grid, layers);
  return grid.height;
}

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

/** Mean absolute height, so a variant can be seen not to have flattened or inflated the planet. */
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

type Tweak = (layers: TerrainLayer[]) => void;

const oldCliff: Tweak = (layers) => {
  for (const layer of layers) {
    if (layer.name === 'cliff detail' && layer.affector.kind === 'fractal') {
      layer.affector = {
        ...layer.affector,
        fractal: { ...layer.affector.fractal, octaves: 4, wavelengthXM: 190, wavelengthZM: 160 },
      };
    }
  }
};

const oldTerraces: Tweak = (layers) => {
  for (const layer of layers) {
    if (layer.name === 'mesa strata' && layer.affector.kind === 'terrace') {
      layer.affector = { ...layer.affector, sharpness: 0.6 };
      layer.filters = [{ kind: 'slope', min: 0.1, max: 40, feather: 0.06 }];
    }

    if (layer.name === 'highland strata' && layer.affector.kind === 'terrace') {
      layer.affector = { ...layer.affector, sharpness: 0.55 };
      layer.filters = [{ kind: 'slope', min: 0.12, max: 40, feather: 0.07 }];
    }
  }
};

const noCliff: Tweak = (layers) => {
  const at = layers.findIndex((l) => l.name === 'cliff detail');
  layers.splice(at, 1);
};

const noTerraces: Tweak = (layers) => {
  for (let i = layers.length - 1; i >= 0; i--) {
    if (layers[i].affector.kind === 'terrace') {
      layers.splice(i, 1);
    }
  }
};

function variant(...tweaks: Tweak[]): Float32Array {
  const layers = landformLayers();
  for (const tweak of tweaks) {
    tweak(layers);
  }

  return bake(layers);
}

const cases: [string, Float32Array][] = [
  ['as shipped (both retuned)', variant()],
  ['old cliff detail only', variant(oldCliff)],
  ['old terraces only', variant(oldTerraces)],
  ['both as before', variant(oldCliff, oldTerraces)],
  ['no cliff detail at all', variant(noCliff)],
  ['no terraces at all', variant(noTerraces)],
  ['neither layer', variant(noCliff, noTerraces)],
];

const area = ((POSTS * POST_SPACING_M) / 1000) ** 2;
console.log(`window ${POSTS} posts at ${POST_SPACING_M} m = ${area.toFixed(1)} km^2; a cone drops more than N m to ALL FOUR neighbours\n`);
console.log(`${'variant'.padEnd(28)}${DROPS_M.map((d) => `>${d}m`.padStart(9)).join('')}${'relief'.padStart(10)}`);
for (const [name, height] of cases) {
  const counts = DROPS_M.map((d) => String(cones(height, d)).padStart(9)).join('');
  console.log(`${name.padEnd(28)}${counts}${`${relief(height).toFixed(0)} m`.padStart(10)}`);
}
