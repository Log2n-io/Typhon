import { describe, expect, it } from 'vitest';
import { CITIES, PLANET_EDGE_M, POIS } from '../src/data/world-data';
import { FIELD_ORIGIN_M, Heightfield, POSTS, POST_SPACING_M, flatHeightfield } from '../src/terrain/heightfield';
import { applyLayers, bakeLayers, createHeightGrid, riserWidthM, slopeAtPost } from '../src/terrain/layers';
import { NODE_GRID, TREE_LEVELS } from '../src/render/terrain-quadtree';
import { MIN_FEATURE_M, bakeTatooine, landformLayers } from '../src/terrain/tatooine-terrain';
import { finestFeatureM } from '../src/terrain/hash';

/**
 * The baked field and the authored planet.
 *
 * The planet is baked at **512 posts / 32 m** here rather than the shipping 4096 / 4 m: the tree's properties — pads
 * level, relief in range, deterministic in its seed — hold at any resolution, and the coarse bake is 65 ms against a
 * measured 12 s.
 *
 * The pad test is the one that has to say which it means, and it asserts on the **posts** rather than on interpolated
 * samples. That is not a weakening: the grid IS the contract, and between two level posts every sampler returns a level
 * height. Sampling the continuous field instead measures the bilinear blend at the rim against whatever the neighbouring
 * post outside the level zone holds, which at 32 m posts is a statement about the test's resolution and not about the pad.
 */

function coarsePlanet(seed?: number): Heightfield {
  const field = new Heightfield(createHeightGrid(512, PLANET_EDGE_M / 512, FIELD_ORIGIN_M));
  bakeTatooine(field, seed);
  return field;
}

describe('Heightfield sampling', () => {
  const field = new Heightfield(createHeightGrid(4, 100, -150));

  it('returns the post itself at a post, and the average halfway between two', () => {
    field.grid.height.set([0, 10, 20, 30, 0, 10, 20, 30, 0, 10, 20, 30, 0, 10, 20, 30]);
    expect(field.heightAt(-150, -150)).toBe(0);
    expect(field.heightAt(-50, -150)).toBe(10);
    expect(field.heightAt(-100, -150)).toBe(5);
    expect(field.heightAt(-150, -50)).toBe(0);
  });

  it('interpolates on both axes, not just one', () => {
    // A field that rises along z only: an implementation that dropped the z lerp would pass the test above and fail here.
    field.grid.height.set([0, 0, 0, 0, 10, 10, 10, 10, 20, 20, 20, 20, 30, 30, 30, 30]);
    expect(field.heightAt(-150, -100)).toBe(5);
    expect(field.heightAt(0, -50)).toBe(10);
  });

  it('clamps outside the field rather than wrapping or reading out of bounds', () => {
    field.grid.height.set([0, 10, 20, 30, 0, 10, 20, 30, 0, 10, 20, 30, 0, 10, 20, 30]);
    expect(field.heightAt(-1e6, 0)).toBe(0);
    expect(field.heightAt(1e6, 0)).toBe(30);
    expect(field.heightAt(0, 1e6)).toBe(field.heightAt(0, 150));
    expect(Number.isFinite(field.heightAt(1e9, -1e9))).toBe(true);
  });

  it('reports the gradient of a ramp as the ramp slope, signed per axis', () => {
    const ramp = new Heightfield(createHeightGrid(8, 10, 0));
    for (let iz = 0; iz < 8; iz++) {
      for (let ix = 0; ix < 8; ix++) {
        // Rising in x, falling in z, at different rates — so a swapped or unsigned axis fails.
        ramp.grid.height[iz * 8 + ix] = ix * 10 * 0.25 - iz * 10 * 0.5;
      }
    }

    const out = new Float64Array(2);
    ramp.gradientAt(35, 35, out);
    expect(out[0]).toBeCloseTo(0.25, 9);
    expect(out[1]).toBeCloseTo(-0.5, 9);
    expect(ramp.slopeAt(35, 35)).toBeCloseTo(Math.hypot(0.25, 0.5), 9);
  });

  it('measures its own extremes', () => {
    field.grid.height.set([0, 10, 20, 30, 0, 10, 20, 30, 0, 10, -5, 30, 0, 10, 20, 44]);
    field.measure();
    expect(field.minHeightM).toBe(-5);
    expect(field.maxHeightM).toBe(44);
  });
});

describe('the flat field', () => {
  it('is zero everywhere, with zero slope — the regression guard for "nothing changed"', () => {
    const flat = flatHeightfield();
    flat.measure();
    expect(flat.minHeightM).toBe(0);
    expect(flat.maxHeightM).toBe(0);
    for (const [x, z] of [
      [0, 0],
      [3460, -4768],
      [-8192, 8191],
      [1e6, -1e6],
    ]) {
      expect(flat.heightAt(x, z)).toBe(0);
      expect(flat.slopeAt(x, z)).toBe(0);
    }
  });
});

describe('the shipping field geometry', () => {
  it('covers the planet at 4 m posts in 67.1 MB', () => {
    expect(POSTS).toBe(4096);
    expect(POST_SPACING_M).toBe(4);
    expect(POSTS * POST_SPACING_M).toBe(PLANET_EDGE_M);
    expect(FIELD_ORIGIN_M).toBe(-8192);
    expect(POSTS * POSTS * 4).toBe(67108864);
  });

  it('gives the finest quadtree node exactly one post per vertex', () => {
    // The pair that has to move together. Either constant alone buys nothing: a finer field the renderer reads every other
    // post of, or a finer vertex grid with two vertices sharing a post. This is the equation that says they agree.
    const finestNodeM = PLANET_EDGE_M / (1 << (TREE_LEVELS - 1));
    expect(finestNodeM / NODE_GRID).toBe(POST_SPACING_M);
  });

  it('authors nothing narrower than four posts — EVERY layer, not just the fractals', () => {
    // A feature narrower than two posts collapses to a single raised post, and bilinear over one raised post is exactly a
    // four-sided pyramid. Four posts is where the measured spike count reaches zero.
    //
    // This assertion existed and the pyramids came back anyway, because it was wrong twice over:
    //
    //   1. It recomputed the finest octave inline and never read `ridged`. The fold at `ridge()` turns one hump into two,
    //      so a ridged layer authors at HALF its finest octave — `cliff detail` passed at 17.5 m while putting 8.8 m
    //      creases on a 4 m grid. It now asks `finestFeatureM`, which is the same expression the tree's own comments
    //      quote, so a layer and its check cannot disagree about what the rule says.
    //
    //   2. It `continue`d past every affector that is not a fractal, which is every TERRACE — and a terrace riser is a
    //      feature with a width like any other. `mesa strata` spent 5.17 m of height on its riser, which on a 0.35 grade
    //      is 14.8 m of ground, on a layer whose slope filter admitted anything up to 40.
    expect(MIN_FEATURE_M).toBe(4 * POST_SPACING_M);

    let fractals = 0;
    let terraces = 0;
    for (const layer of landformLayers()) {
      if (layer.affector.kind === 'fractal') {
        fractals++;
        const finest = finestFeatureM(layer.affector.fractal);
        expect(finest, `${layer.name} finest feature`).toBeGreaterThanOrEqual(MIN_FEATURE_M);
        continue;
      }

      if (layer.affector.kind !== 'terrace') {
        continue;
      }

      terraces++;
      // Measured at a REFERENCE GRADE, not at the slope filter's ceiling.
      //
      // The ceiling was tried and it was the wrong control. A terrace has no natural steepest case — the filter admits up
      // to 40, and no riser of any height is a post wide on ground that steep — so the obvious remedy was to cap the
      // filter. Capping it at 0.5 took the cone census from 120 to 157 and capping at 0.35 made it worse again: the cap is
      // itself a spatial boundary whose feather lands on exactly the steep ground where a feather is sub-post.
      //
      // So the rule is stated where it can be met and is worth meeting: at 45°, the steepest ground a walker treats as
      // ground rather than as a wall, a riser must still span four posts. Above that the terrain is a cliff face and the
      // banding is not what the eye is reading. The hard guard on what this rule is a proxy FOR is the cone census below.
      const width = riserWidthM(layer.affector.stepM, layer.affector.sharpness, REFERENCE_GRADE);
      expect(width, `${layer.name} riser at a 45-degree grade`).toBeGreaterThanOrEqual(MIN_FEATURE_M);
    }

    // The premise: both loops above ran. A rename that made every `kind` miss would leave this test green and silent.
    expect(fractals, 'fractal layers checked').toBeGreaterThan(3);
    expect(terraces, 'terrace layers checked').toBe(2);
  });

  it('leaves almost no one-post cones in the ground it bakes', () => {
    // **The assertion the other two are proxies for.** Every rule above is about what a layer may author; this counts what
    // the bake actually produced, which is the only thing a screenshot shows.
    //
    // A cone is a strict local maximum that drops on ALL FOUR sides — real terrain has ridges and saddles, but a ridge
    // post is high along one axis and level along the other, so only an isolated raised post qualifies. That is precisely
    // what bilinear interpolation makes of a feature narrower than two posts.
    //
    // It also catches what neither rule could: `cliff detail` and the terraces are NOT independent. Alone they leave 45
    // and 38 cones over this window; together they left 271, because the terraces build risers, `cliff detail` is filtered
    // on slope, and slope is a central difference over adjacent posts — so a riser reads as a cliff face and gets the
    // finest noise in the tree painted onto it. No per-layer rule can see an interaction between two layers.
    //
    // The thresholds are the measured figures with room to move: 41/9/1 as authored, against 212/105/24 before the
    // retune. A regression puts them back into the hundreds, which is the range these bound.
    const posts = 1024;
    const grid = createHeightGrid(posts, POST_SPACING_M, -2048);
    grid.height.fill(0);
    applyLayers(grid, landformLayers());

    const census = (dropM: number): number => {
      let count = 0;
      for (let z = 1; z < posts - 1; z++) {
        for (let x = 1; x < posts - 1; x++) {
          const h = grid.height[z * posts + x];
          const drop = Math.min(
            h - grid.height[z * posts + x - 1],
            h - grid.height[z * posts + x + 1],
            h - grid.height[(z - 1) * posts + x],
            h - grid.height[(z + 1) * posts + x],
          );
          if (drop > dropM) {
            count++;
          }
        }
      }

      return count;
    };

    expect(census(1), 'cones dropping more than 1 m on all four sides').toBeLessThan(80);
    expect(census(1.5), 'cones dropping more than 1.5 m').toBeLessThan(25);
    expect(census(3), 'cones dropping more than 3 m').toBeLessThan(6);

    // And the planet was not flattened to get there: the retune is supposed to spread the relief, not remove it.
    let lo = Infinity;
    let hi = -Infinity;
    for (const h of grid.height) {
      lo = Math.min(lo, h);
      hi = Math.max(hi, h);
    }

    expect(hi - lo, 'relief over the probe window').toBeGreaterThan(200);
  });

  it('counts the fold: a ridged spec authors at half its finest octave', () => {
    // The unit behind the rule above, pinned on its own so the halving cannot be quietly dropped from `finestFeatureM`
    // and leave every layer passing again.
    const base = { seed: 1, octaves: 3, wavelengthXM: 260, wavelengthZM: 220, lacunarity: 2.09, gain: 0.55 };
    const plain = finestFeatureM({ ...base, ridged: false });
    const ridged = finestFeatureM({ ...base, ridged: true });

    expect(plain).toBeCloseTo(220 / 2.09 ** 2, 6);
    expect(ridged).toBeCloseTo(plain / 2, 6);
  });
});

/**
 * The grade a terrace riser's width is measured at: 45 degrees.
 *
 * Not the slope filter's ceiling — see the rule below for why that was tried and withdrawn. This is the steepest ground a
 * walker reads as ground rather than as a wall, and therefore the steepest at which banding is a thing the eye resolves.
 */
const REFERENCE_GRADE = 1.0;

describe('the authored planet', () => {
  const field = coarsePlanet();

  it('carries real relief, and enough grade to be seen from the ground', () => {
    // This test used to bound relief under the 256 m spatial cell, on the strength of `Ecs/Components.cs:117-120` — *"a
    // third partitioned axis would be one cell deep"*. That argument is about a hypothetical THIRD axis: the spatial index
    // is 2D and never reads Y, so the bound was costing scenery to protect a decision nobody has taken. Loïc lifted it on
    // 2026-09-29, and what replaces it is a floor, not a ceiling — the failure mode this tree has actually had, twice, is
    // being flat.
    const relief = field.maxHeightM - field.minHeightM;
    expect(relief).toBeGreaterThan(300);
    expect(relief).toBeLessThan(700);

    // Relief alone does not make ground readable: what the eye reads is grade. Two versions of this tree measured 130 m
    // and 140 m of relief and both looked like a plane, because the relief sat on kilometre wavelengths. Mean slope is the
    // number that moved when they were fixed — 0.005 in the flat version, 0.058 in the second, and this is the third.
    let slope = 0;
    let n = 0;
    for (let iz = 1; iz + 1 < field.grid.posts; iz++) {
      for (let ix = 1; ix + 1 < field.grid.posts; ix++) {
        slope += slopeAtPost(field.grid, ix, iz);
        n++;
      }
    }

    expect(slope / n).toBeGreaterThan(0.09);
  });

  it('is a function of its seed: same seed identical, different seed different', () => {
    const again = coarsePlanet();
    expect(Array.from(again.grid.height)).toEqual(Array.from(field.grid.height));
    const other = coarsePlanet(0x1234567);
    expect(other.maxHeightM).not.toBe(field.maxHeightM);
  });

  it('is not homogeneous — the mesa belt is measurably rougher than the rest', () => {
    // The whole reason for a layer system over a stack of fBm terms. If regions were amplitude scalings of one generator
    // this would hold weakly; with boundaries and a ridged affector it holds by a wide margin.
    const belt = roughness(field, 2500, 3000, 1200);
    const elsewhere = roughness(field, -1500, -4500, 1200);
    expect(belt).toBeGreaterThan(elsewhere * 1.5);
  });

  it('levels every town and landmark pad across its whole site radius', () => {
    // The one property the world builder depends on: buildings are placed by `Rng.PointInDisc` inside the site radius, so
    // a sloped pad puts a cantina half underground. Asserted on the POSTS inside the radius, which is exact at any test
    // resolution — see the note at the top of this file.
    const { posts, spacingM, originM, height } = field.grid;
    for (const site of [...CITIES, ...POIS]) {
      const centre = field.heightAt(site.x, site.z);
      let worst = 0;
      let tested = 0;
      const reach = Math.ceil(site.radius / spacingM);
      const cx = Math.round((site.x - originM) / spacingM);
      const cz = Math.round((site.z - originM) / spacingM);
      for (let iz = Math.max(0, cz - reach); iz <= Math.min(posts - 1, cz + reach); iz++) {
        for (let ix = Math.max(0, cx - reach); ix <= Math.min(posts - 1, cx + reach); ix++) {
          const dx = originM + ix * spacingM - site.x;
          const dz = originM + iz * spacingM - site.z;
          if (dx * dx + dz * dz > site.radius * site.radius) {
            continue;
          }

          worst = Math.max(worst, Math.abs(height[iz * posts + ix] - centre));
          tested++;
        }
      }

      // A site whose disc held no post at all would pass the line below vacuously, and the three smallest sites are only a
      // few posts across at this resolution.
      expect(tested, `${site.name} covered no post`).toBeGreaterThan(0);
      expect(worst, `${site.name} pad is not level`).toBeLessThan(0.01);
    }
  });

  it('has a slope-filtered layer that contributes only where the ground is already steep', () => {
    // Bake the tree without its cliff-detail layer and compare: the difference must be confined to steep ground. This is
    // the mechanism that makes terrain look caused rather than drawn, and it is the one layer whose absence is otherwise
    // invisible in any single statistic.
    const withoutCliffs = new Heightfield(createHeightGrid(512, PLANET_EDGE_M / 512, FIELD_ORIGIN_M));
    const grid = withoutCliffs.grid;
    bakeLayers(
      grid,
      landformLayers().filter((l) => l.name !== 'cliff detail'),
    );
    withoutCliffs.measure();

    // The reference is the LANDFORM, with no pads on either side. Comparing against the fully baked planet instead does not
    // isolate one layer: a pad's constant is read out of the landform at the site centre, so removing the cliff layer moves
    // the pads too, and six level pad posts showed up as "the cliff layer touched flat ground". They were pads.
    const reference = new Heightfield(createHeightGrid(512, PLANET_EDGE_M / 512, FIELD_ORIGIN_M));
    bakeLayers(reference.grid, landformLayers());

    // Measured with `slopeAtPost` on the pre-layer grid — the exact quantity the filter read — against the filter's own
    // support, `min − feather`.
    const support = 0.35 - 0.2;
    let outsideTheBand = 0;
    let insideTheBand = 0;
    for (let iz = 0; iz < grid.posts; iz++) {
      for (let ix = 0; ix < grid.posts; ix++) {
        const delta = Math.abs(reference.grid.height[iz * grid.posts + ix] - grid.height[iz * grid.posts + ix]);
        if (delta < 1e-4) {
          continue;
        }

        if (slopeAtPost(grid, ix, iz) <= support) {
          outsideTheBand++;
        } else {
          insideTheBand++;
        }
      }
    }

    expect(insideTheBand).toBeGreaterThan(200);
    expect(outsideTheBand).toBe(0);
  });
});

/** Mean absolute post-to-post height difference in a square window: a crude, effective roughness. */
function roughness(field: Heightfield, x: number, z: number, halfM: number): number {
  const step = 32;
  let sum = 0;
  let n = 0;
  for (let dz = -halfM; dz <= halfM; dz += step) {
    for (let dx = -halfM; dx <= halfM; dx += step) {
      sum += Math.abs(field.heightAt(x + dx + step, z + dz) - field.heightAt(x + dx, z + dz));
      n++;
    }
  }

  return sum / n;
}
