import { describe, expect, it } from 'vitest';
import {
  applyLayers,
  bakeLayers,
  boundaryWeight,
  createHeightGrid,
  slopeAtPost,
  terraceAt,
  type Boundary,
  type HeightGrid,
  type TerrainLayer,
} from '../src/terrain/layers';

/**
 * The layer evaluator. Every test here is a property of the architecture rather than of the authored planet, because the
 * planet is invented and its numbers may be retuned by eye at any time — while a layer leaking outside its boundary, or a
 * filter that depends on scan order, would be a defect whatever the tree says.
 */

function grid(posts = 33, spacingM = 10, originM = -160): HeightGrid {
  return createHeightGrid(posts, spacingM, originM);
}

function at(g: HeightGrid, ix: number, iz: number): number {
  return g.height[iz * g.posts + ix];
}

/** Fills a grid with a linear ramp along x of the given slope, as the base for the slope-filter tests. */
function ramp(g: HeightGrid, slope: number): void {
  for (let iz = 0; iz < g.posts; iz++) {
    for (let ix = 0; ix < g.posts; ix++) {
      g.height[iz * g.posts + ix] = ix * g.spacingM * slope;
    }
  }
}

const RAISE_10: TerrainLayer = {
  name: 'raise',
  boundary: null,
  filters: [],
  blend: 'add',
  affector: { kind: 'constant', heightM: 10 },
};

describe('boundaryWeight', () => {
  it('is 1 well inside, 0 outside, and exactly the Hermite ramp across the feather', () => {
    const circle: Boundary = { kind: 'circle', x: 0, z: 0, radiusM: 100, featherM: 40 };
    expect(boundaryWeight(circle, 0, 0)).toBe(1);
    expect(boundaryWeight(circle, 59, 0)).toBe(1);
    expect(boundaryWeight(circle, 100, 0)).toBe(0);
    expect(boundaryWeight(circle, 140, 0)).toBe(0);
    // 20 m inside the edge is halfway through a 40 m feather, where 3t² − 2t³ is exactly 0.5.
    expect(boundaryWeight(circle, 80, 0)).toBeCloseTo(0.5, 12);
  });

  it('never reaches outside the declared shape, for every shape — feathering is INWARD', () => {
    // A layer that leaked past its boundary would let one region's landform bleed into its neighbour, which is the one
    // thing boundaries exist to prevent.
    const shapes: Boundary[] = [
      { kind: 'circle', x: 10, z: -20, radiusM: 50, featherM: 30 },
      { kind: 'rect', x: -5, z: 12, halfXM: 40, halfZM: 25, featherM: 15 },
      { kind: 'polygon', points: [0, 0, 100, 10, 80, 90, -20, 60], featherM: 25 },
      { kind: 'polyline', points: [-100, 0, 0, 40, 100, -10], halfWidthM: 30, featherM: 12 },
    ];
    for (const shape of shapes) {
      for (let i = 0; i < 4000; i++) {
        const x = ((i * 37) % 601) - 300;
        const z = ((i * 53) % 601) - 300;
        const w = boundaryWeight(shape, x, z);
        expect(w).toBeGreaterThanOrEqual(0);
        expect(w).toBeLessThanOrEqual(1);
      }
    }

    // And specifically: a point outside each shape is exactly zero, not merely small.
    expect(boundaryWeight(shapes[0], 10, 31)).toBe(0);
    expect(boundaryWeight(shapes[1], 36, 12)).toBe(0);
    expect(boundaryWeight(shapes[2], -60, 0)).toBe(0);
    expect(boundaryWeight(shapes[3], 0, 80)).toBe(0);
  });

  it('is continuous across a boundary edge — no seam', () => {
    const polygon: Boundary = { kind: 'polygon', points: [0, 0, 200, 0, 200, 200, 0, 200], featherM: 50 };
    // Either side of the edge x = 200, a hundredth of a millimetre apart. Both must be ~0 for the edge not to show.
    expect(boundaryWeight(polygon, 200 - 1e-5, 100)).toBeLessThan(1e-8);
    expect(boundaryWeight(polygon, 200 + 1e-5, 100)).toBe(0);
    // And a hair inside a CORNER, where two edges meet, is also near zero rather than jumping.
    expect(boundaryWeight(polygon, 0.001, 0.001)).toBeLessThan(1e-8);
  });

  it('a rect is bounded by its SHORT axis, not by whichever it was given first', () => {
    // A rect 80 × 10: a point 20 m from the centre along x is inside, 20 m along z is outside. An implementation that
    // took the wrong axis, or max instead of min, would pass a square test and fail this one.
    const rect: Boundary = { kind: 'rect', x: 0, z: 0, halfXM: 80, halfZM: 10, featherM: 0 };
    expect(boundaryWeight(rect, 20, 0)).toBe(1);
    expect(boundaryWeight(rect, 0, 20)).toBe(0);
  });
});

describe('a layer outside its boundary', () => {
  it('contributes EXACTLY zero, post for post, against the same bake without it', () => {
    const base: TerrainLayer[] = [RAISE_10];
    const withRegion: TerrainLayer[] = [
      RAISE_10,
      {
        name: 'region',
        boundary: { kind: 'circle', x: 0, z: 0, radiusM: 60, featherM: 20 },
        filters: [],
        blend: 'add',
        affector: { kind: 'constant', heightM: 1000 },
      },
    ];
    const a = grid();
    const b = grid();
    bakeLayers(a, base);
    bakeLayers(b, withRegion);
    let differing = 0;
    for (let iz = 0; iz < a.posts; iz++) {
      for (let ix = 0; ix < a.posts; ix++) {
        const x = a.originM + ix * a.spacingM;
        const z = a.originM + iz * a.spacingM;
        if (Math.hypot(x, z) >= 60) {
          expect(at(b, ix, iz)).toBe(at(a, ix, iz));
        } else {
          differing++;
        }
      }
    }

    // The test would also pass on a layer that did nothing at all, so assert it DID something inside.
    expect(differing).toBeGreaterThan(20);
    expect(at(b, 16, 16)).toBeCloseTo(1010, 6);
  });

  it('reaches every post its boundary covers — the bounding box that bounds the sweep must not clip it', () => {
    // The sweep is restricted to the boundary's bounding box, which is worth 4× on a full bake. A box one post too tight
    // would shave the outermost ring of every region and pad, and the seam would be invisible in a picture of the whole
    // planet.
    const g = grid(41, 10, -200);
    const circle: Boundary = { kind: 'circle', x: 3.7, z: -6.2, radiusM: 77.3, featherM: 0 };
    applyLayers(g, [
      {
        name: 'off-grid circle',
        boundary: circle,
        filters: [],
        blend: 'add',
        affector: { kind: 'constant', heightM: 1 },
      },
    ]);

    // Every post, against the boundary's OWN answer — not a handful near the edge. A first version of this test sampled
    // four posts 6 m inside the rim, and a bounding box shifted outward by a whole post still passed it, because 6 m is
    // less than the post spacing. Comparing the two sets is the only formulation a near miss cannot satisfy.
    let touched = 0;
    for (let iz = 0; iz < g.posts; iz++) {
      for (let ix = 0; ix < g.posts; ix++) {
        const x = g.originM + ix * g.spacingM;
        const z = g.originM + iz * g.spacingM;
        const expected = boundaryWeight(circle, x, z);
        expect(at(g, ix, iz), `post (${ix}, ${iz})`).toBe(expected);
        touched += expected > 0 ? 1 : 0;
      }
    }

    expect(touched).toBeGreaterThan(150);
  });
});

describe('the slope filter', () => {
  it('contributes nothing at all on flat ground', () => {
    const g = grid();
    bakeLayers(g, [
      RAISE_10,
      {
        name: 'cliff detail',
        boundary: null,
        filters: [{ kind: 'slope', min: 0.05, max: 10, feather: 0.02 }],
        blend: 'add',
        affector: { kind: 'constant', heightM: 500 },
      },
    ]);
    for (let i = 0; i < g.height.length; i++) {
      expect(g.height[i]).toBe(10);
    }
  });

  it('reads a SNAPSHOT of the accumulator, so its result cannot depend on scan order', () => {
    // A ramp of slope 0.5 everywhere, and a slope-filtered layer that FLATTENS it. Every post's pre-layer slope is 0.5,
    // which is squarely inside the band, so every post must be flattened to exactly 0.
    //
    // A single-pass evaluator fails this hard: once post (0, z) is flattened, post (1, z)'s central difference is taken
    // between a flattened neighbour and a ramped one, so its slope halves, its weight drops, and the layer's effect
    // decays along the scan direction — asymmetrically in +x and −x.
    const g = grid();
    ramp(g, 0.5);
    applyLayers(g, [
      {
        name: 'flatten',
        boundary: null,
        filters: [{ kind: 'slope', min: 0.1, max: 10, feather: 0.05 }],
        blend: 'replace',
        affector: { kind: 'constant', heightM: 0 },
      },
    ]);
    for (let i = 0; i < g.height.length; i++) {
      expect(g.height[i]).toBe(0);
    }
  });

  it('measures slope as rise over run, one-sided at the edges rather than wrapped', () => {
    const g = grid();
    ramp(g, 0.25);
    expect(slopeAtPost(g, 5, 5)).toBeCloseTo(0.25, 6);
    // The edge posts too: a wrap would see the whole ramp's drop across one spacing and report a cliff.
    expect(slopeAtPost(g, 0, 5)).toBeCloseTo(0.25, 6);
    expect(slopeAtPost(g, g.posts - 1, 5)).toBeCloseTo(0.25, 6);
  });
});

describe('the height filter', () => {
  it('applies only inside its band, feathered', () => {
    const g = grid();
    // A ramp along x, so each post sits at a known height, then a layer banded to 100…200 m.
    ramp(g, 1);
    applyLayers(g, [
      {
        name: 'band',
        boundary: null,
        filters: [{ kind: 'height', minM: 100, maxM: 200, featherM: 0 }],
        blend: 'add',
        affector: { kind: 'constant', heightM: 1000 },
      },
    ]);
    // Post ix sits at ix · 10 m: posts 10…20 are in the band, 9 and 21 are not.
    expect(at(g, 9, 3)).toBe(90);
    expect(at(g, 10, 3)).toBe(1100);
    expect(at(g, 20, 3)).toBe(1200);
    expect(at(g, 21, 3)).toBe(210);
  });
});

describe('terraceAt', () => {
  it('is the exact identity at sharpness 0, so the parameter is safe to sweep up from nothing', () => {
    for (const h of [-31.4, -0.001, 0, 0.5, 7.25, 123.456]) {
      expect(terraceAt(h, 8.5, 0)).toBeCloseTo(h, 9);
    }
  });

  it('flattens most of each band at sharpness 1 and keeps the band edges exact', () => {
    const step = 10;
    // Inside a band, away from the middle, the height snaps to the band's floor or ceiling: that flat IS the strata look.
    expect(terraceAt(21, step, 1)).toBe(20);
    expect(terraceAt(29, step, 1)).toBe(30);
    // The rise is confined to the middle ninth of the band, so 25 stays at 25.
    expect(terraceAt(25, step, 1)).toBeCloseTo(25, 9);
  });

  it('is monotonic — a terrace may flatten ground but must never invert it', () => {
    let previous = -Infinity;
    for (let h = -40; h < 40; h += 0.05) {
      const t = terraceAt(h, 8.5, 0.72);
      expect(t).toBeGreaterThanOrEqual(previous - 1e-9);
      previous = t;
    }
  });

  it('works below zero: floor, not truncation', () => {
    // `Math.trunc` would make a double-width band across zero, which shows as one anomalous step on every slope that
    // crosses sea level.
    // −9 is the case that tells the two apart: it is near the FLOOR of the band [−10, 0), so it snaps to −10. Under
    // truncation its band would be [0, 10) and it would snap to 0 — the same answer −1 gives, which is why −1 alone
    // proves nothing.
    expect(terraceAt(-9, 10, 1)).toBe(-10);
    expect(terraceAt(-19, 10, 1)).toBe(-20);
    expect(terraceAt(-1, 10, 1)).toBe(0);
    expect(terraceAt(-11, 10, 1)).toBe(-10);
  });
});

describe('blends', () => {
  it('replace lerps toward the value, max lerps toward the maximum, add scales the value', () => {
    const shapes: { blend: 'add' | 'replace' | 'max'; expected: number }[] = [
      { blend: 'add', expected: 10 + 100 * 0.5 },
      { blend: 'replace', expected: 10 + (100 - 10) * 0.5 },
      { blend: 'max', expected: 10 + (100 - 10) * 0.5 },
    ];
    for (const shape of shapes) {
      const g = grid(3, 10, -10);
      g.height.fill(10);
      applyLayers(g, [
        {
          name: shape.blend,
          // A circle whose feather puts the centre post at exactly half weight: radius 20, feather 20, post at r = 10.
          boundary: { kind: 'circle', x: -10, z: -10, radiusM: 20, featherM: 20 },
          filters: [],
          blend: shape.blend,
          affector: { kind: 'constant', heightM: 100 },
        },
      ]);
      expect(at(g, 1, 0)).toBeCloseTo(shape.expected, 5);
    }
  });

  it('max below the current height leaves it alone, where replace would pull it down', () => {
    const g = grid(3, 10, -10);
    g.height.fill(50);
    applyLayers(g, [
      { name: 'low', boundary: null, filters: [], blend: 'max', affector: { kind: 'constant', heightM: 5 } },
    ]);
    expect(at(g, 1, 1)).toBe(50);
    applyLayers(g, [
      { name: 'low', boundary: null, filters: [], blend: 'replace', affector: { kind: 'constant', heightM: 5 } },
    ]);
    expect(at(g, 1, 1)).toBe(5);
  });
});
