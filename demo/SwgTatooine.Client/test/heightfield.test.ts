import { describe, expect, it } from 'vitest';
import { CITIES, PLANET_EDGE_M, POIS } from '../src/data/world-data';
import { FIELD_ORIGIN_M, Heightfield, POSTS, POST_SPACING_M, flatHeightfield } from '../src/terrain/heightfield';
import { bakeLayers, createHeightGrid, slopeAtPost } from '../src/terrain/layers';
import { NODE_GRID, TREE_LEVELS } from '../src/render/terrain-quadtree';
import { MIN_FEATURE_M, bakeTatooine, landformLayers } from '../src/terrain/tatooine-terrain';

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

  it('authors nothing narrower than four posts — the rule the pyramids came from breaking', () => {
    // A feature narrower than two posts collapses to a single raised post, and bilinear over one raised post is exactly a
    // four-sided pyramid. Four posts is where the measured spike count reaches zero. `cliff detail` used to put its finest
    // octave at 9.9 m against a 16 m limit and covered the planet in ~1 500 cones; this is that defect as an assertion,
    // stated against the FINEST octave rather than the base wavelength, because the base is not what aliases.
    expect(MIN_FEATURE_M).toBe(4 * POST_SPACING_M);
    for (const layer of landformLayers()) {
      if (layer.affector.kind !== 'fractal') {
        continue;
      }

      const { octaves, lacunarity, wavelengthXM, wavelengthZM } = layer.affector.fractal;
      const shrink = lacunarity ** (octaves - 1);
      const finest = Math.min(wavelengthXM, wavelengthZM) / shrink;
      expect(finest, `${layer.name} finest octave`).toBeGreaterThanOrEqual(MIN_FEATURE_M);
    }
  });
});

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
