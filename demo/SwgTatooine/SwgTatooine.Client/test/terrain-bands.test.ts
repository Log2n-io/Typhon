import { describe, expect, it } from 'vitest';
import { PLANET_EDGE_M } from '../src/data/world-data';
import { bakeInBands, bandRanges, placeBand } from '../src/terrain/band-bake';
import { FIELD_ORIGIN_M, Heightfield } from '../src/terrain/heightfield';
import {
  adoptHeightGrid,
  applyLayers,
  bakeLayers,
  createHeightGrid,
  createHeightGridWindow,
  haloRows,
  slopeAtPost,
} from '../src/terrain/layers';
import { bakeTatooine, landformLayers } from '../src/terrain/tatooine-terrain';

/**
 * Baking the planet in horizontal bands, one per core.
 *
 * The whole of this is worth one property: **a band must produce the same heights as the whole grid, exactly**. If it does
 * not, the failure is a seam along every band boundary — a line of subtly wrong rock on steep ground, which no summary
 * statistic shows and which moves with the machine's core count, so it would reproduce for some people and not others.
 * Everything else here exists to make that property hold or to say why it might not.
 */

function coarse(): Heightfield {
  return new Heightfield(createHeightGrid(256, PLANET_EDGE_M / 256, FIELD_ORIGIN_M));
}

describe('band ranges', () => {
  it('covers every row exactly once, with no band more than one row off any other', () => {
    for (const [posts, bands] of [
      [4096, 8],
      [4096, 1],
      [4096, 6],
      [255, 8],
      [7, 3],
    ]) {
      const ranges = bandRanges(posts, bands);
      let at = 0;
      let smallest = Infinity;
      let largest = 0;
      for (const range of ranges) {
        expect(range.rowOffset, `${posts} over ${bands}`).toBe(at);
        at += range.rows;
        smallest = Math.min(smallest, range.rows);
        largest = Math.max(largest, range.rows);
      }

      expect(at, `${posts} over ${bands}`).toBe(posts);
      expect(largest - smallest).toBeLessThanOrEqual(1);
    }
  });

  it('never returns an empty band, however many are asked for', () => {
    // More bands than rows would otherwise produce zero-row ranges, and a zero-row window throws.
    const ranges = bandRanges(4, 16);
    expect(ranges.length).toBe(4);
    for (const range of ranges) {
      expect(range.rows).toBeGreaterThan(0);
    }
  });
});

describe('the halo', () => {
  it('is one row per slope-filtered layer, because each one pushes the layer below it further out', () => {
    expect(haloRows([])).toBe(0);
    expect(
      haloRows([
        { name: 'a', boundary: null, filters: [], blend: 'add', affector: { kind: 'constant', heightM: 1 } },
        {
          name: 'b',
          boundary: null,
          filters: [{ kind: 'height', minM: 0, maxM: 1, featherM: 0 }],
          blend: 'add',
          affector: { kind: 'constant', heightM: 1 },
        },
      ]),
    ).toBe(0);
    expect(
      haloRows([
        {
          name: 'c',
          boundary: null,
          filters: [{ kind: 'slope', min: 0.1, max: 9, feather: 0.1 }],
          blend: 'add',
          affector: { kind: 'constant', heightM: 1 },
        },
      ]),
    ).toBe(1);
    // The shipping tree: `mesa strata`, `highland strata` and `cliff detail`.
    expect(haloRows(landformLayers())).toBe(3);
  });
});

describe('a height grid window', () => {
  it('holds only its own rows but keeps the planet width', () => {
    const window = createHeightGridWindow(64, 4, -128, 16, 8);
    expect(window.posts).toBe(64);
    expect(window.rows).toBe(8);
    expect(window.rowOffset).toBe(16);
    expect(window.height.length).toBe(64 * 8);
  });

  it('refuses a window that does not fit, rather than indexing out of it later', () => {
    expect(() => createHeightGridWindow(64, 4, -128, 60, 8)).toThrow();
    expect(() => createHeightGridWindow(64, 4, -128, -1, 8)).toThrow();
    expect(() => createHeightGridWindow(64, 4, -128, 0, 0)).toThrow();
    expect(() => adoptHeightGrid(64, 4, -128, new Float32Array(64 * 63))).toThrow();
  });

  it('takes the rim of the PLANET, not of the window, when it measures slope', () => {
    // The seam bug in miniature. A band whose first row is planet row 8 must take a central difference there; treating it
    // as the edge of the world would take a one-sided one and halve the slope, which the cliff-detail filter then reads.
    const whole = createHeightGrid(16, 10, 0);
    const window = createHeightGridWindow(16, 10, 0, 8, 4);
    for (let iz = 0; iz < 16; iz++) {
      for (let ix = 0; ix < 16; ix++) {
        const h = iz * 7 + ix * 3;
        whole.height[iz * 16 + ix] = h;
        if (iz >= 8 && iz < 12) {
          window.height[(iz - 8) * 16 + ix] = h;
        }
      }
    }

    for (let iz = 9; iz < 11; iz++) {
      expect(slopeAtPost(window, 5, iz)).toBe(slopeAtPost(whole, 5, iz));
    }
  });
});

describe('the banded bake', () => {
  it('reproduces the whole-grid bake post for post, at every band count', () => {
    const whole = coarse();
    bakeTatooine(whole);
    for (const bands of [1, 2, 3, 8, 13]) {
      const banded = coarse();
      bakeInBands(banded, bands);
      let differing = 0;
      for (let i = 0; i < whole.grid.height.length; i++) {
        if (whole.grid.height[i] !== banded.grid.height[i]) {
          differing++;
        }
      }

      expect(differing, `${bands} bands`).toBe(0);
      expect(banded.maxHeightM).toBe(whole.maxHeightM);
      expect(banded.minHeightM).toBe(whole.minHeightM);
    }
  });

  it('is only exact BECAUSE of the halo — drop it and the seams appear', () => {
    // A mutation test kept as a test, because the halo is invisible in the output when it is right and this is the only
    // thing that says what it buys. Bake each band with no overlap at all and count the posts that move: they land on the
    // band boundaries, where the slope filter read the band's own edge instead of the terrain.
    // LANDFORM against landform, with no pads on either side: the pads are applied once on the assembled field and are
    // not part of what a band computes, so including them would have counted fourteen level discs as seams.
    const whole = coarse();
    bakeLayers(whole.grid, landformLayers());
    const { posts, spacingM, originM } = whole.grid;
    const seamed = coarse();
    for (const range of bandRanges(posts, 8)) {
      // Deliberately NO halo: this is `bakeLandformBand` with the one line that matters removed.
      const grid = createHeightGridWindow(posts, spacingM, originM, range.rowOffset, range.rows);
      applyLayers(grid, landformLayers());
      placeBand(seamed.grid, { rowOffset: range.rowOffset, rows: range.rows, height: grid.height });
    }

    let differing = 0;
    for (let i = 0; i < whole.grid.height.length; i++) {
      if (whole.grid.height[i] !== seamed.grid.height[i]) {
        differing++;
      }
    }

    expect(differing).toBeGreaterThan(0);

    // And they are confined to the band boundaries: a difference spread over the whole planet would mean the window
    // arithmetic is wrong rather than the halo missing, which is a different bug with the same symptom. The reach is the
    // halo depth plus one — a wrong row feeds the next slope filter, which reaches one row further each time.
    const reach = haloRows(landformLayers()) + 1;
    const seams = bandRanges(posts, 8)
      .map((r) => r.rowOffset)
      .filter((row) => row > 0);
    let elsewhere = 0;
    for (let iz = 0; iz < posts; iz++) {
      const nearSeam = seams.some((seam) => Math.abs(iz - seam) <= reach);
      if (nearSeam) {
        continue;
      }

      for (let ix = 0; ix < posts; ix++) {
        if (whole.grid.height[iz * posts + ix] !== seamed.grid.height[iz * posts + ix]) {
          elsewhere++;
        }
      }
    }

    expect(elsewhere).toBe(0);
  });
});
