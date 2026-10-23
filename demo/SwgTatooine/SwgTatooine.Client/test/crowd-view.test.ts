import { describe, expect, it } from 'vitest';

import { ENTER_NEAR_PX, LEAVE_NEAR_PX } from '../src/render/view-math';
import {
  CULLED,
  HYSTERESIS,
  SPRITE_TIER,
  TIER_ENTER_PX,
  classify,
  packTiers,
  tierFor,
  type ViewParams,
} from '../src/render/crowd-view';

/**
 * Which bodies the crowd draws, at which level, and where their instance data lands.
 *
 * <b>The seam with the entity layer is the point of several of these.</b> The crowd's coarsest threshold and the
 * entity layer's near band are the same 7 px, and below it the crowd draws NOTHING — the body is handed back to the
 * existing far sprite. The spike drew its coarsest mesh down there instead, with a comment saying this tier was meant
 * to become the client's sprite band. It becomes it here, so a body crosses one boundary rather than two a pixel
 * apart.
 */

/** A frustum that accepts everything: six planes facing inward from very far away. */
function openView(pixelsPerMetre = 500): ViewParams {
  const planes = new Float32Array(24);
  const dirs = [
    [1, 0, 0],
    [-1, 0, 0],
    [0, 1, 0],
    [0, -1, 0],
    [0, 0, 1],
    [0, 0, -1],
  ];
  for (const [i, d] of dirs.entries()) {
    planes[i * 4] = d[0];
    planes[i * 4 + 1] = d[1];
    planes[i * 4 + 2] = d[2];
    planes[i * 4 + 3] = 1e6;
  }

  return { eyeX: 0, eyeY: 0, eyeZ: 0, pixelsPerMetre, planes };
}

/** One body's matrix, with its feet at (x, y, z). */
function bodies(positions: readonly (readonly [number, number, number])[]): Float32Array {
  const m = new Float32Array(positions.length * 16);
  for (const [i, p] of positions.entries()) {
    m[i * 16] = 1;
    m[i * 16 + 5] = 1;
    m[i * 16 + 10] = 1;
    m[i * 16 + 15] = 1;
    m[i * 16 + 12] = p[0];
    m[i * 16 + 13] = p[1];
    m[i * 16 + 14] = p[2];
  }

  return m;
}

describe('the crowd tiers', () => {
  it('hands the far tier to the sprite band at the SAME pixel size the entity layer uses', () => {
    // One boundary in the world. If these ever diverge, a body either pops twice or disappears in the gap.
    expect(TIER_ENTER_PX.at(-1)).toBe(ENTER_NEAR_PX);
    expect(SPRITE_TIER).toBe(TIER_ENTER_PX.length);
  });

  it('chooses the tier by projected height', () => {
    expect(tierFor(400, CULLED)).toBe(0);
    expect(tierFor(150, CULLED)).toBe(0);
    expect(tierFor(149, CULLED)).toBe(1);
    expect(tierFor(40, CULLED)).toBe(1);
    expect(tierFor(39, CULLED)).toBe(2);
    expect(tierFor(7, CULLED)).toBe(2);
    expect(tierFor(6.9, CULLED)).toBe(SPRITE_TIER);
  });

  it('hands the body back at exactly the pixel the packer takes it, with no window in between', () => {
    // The crowd's own hysteresis would leave the last tier at 7 x 0.85 = 5.95 px, while the packer holds Band.Near
    // — and therefore skips the box — until 5. Between the two the body was drawn by NOBODY.
    const last = TIER_ENTER_PX.length - 1;
    expect(tierFor(ENTER_NEAR_PX * (1 - HYSTERESIS), last)).toBe(last);
    expect(tierFor(LEAVE_NEAR_PX, last)).toBe(last);
    expect(tierFor(LEAVE_NEAR_PX - 0.01, last)).toBe(SPRITE_TIER);
  });

  it('does not take the body back EARLY either, which would draw it twice', () => {
    // The clamp is on leaving only. A body arriving from the sprite band must wait for the packer's own 7 px, or
    // between 5 and 7 the packer draws its sprite and the crowd draws a mesh in the same place.
    const last = TIER_ENTER_PX.length - 1;
    expect(tierFor(ENTER_NEAR_PX - 0.01, SPRITE_TIER)).toBe(SPRITE_TIER);
    expect(tierFor(ENTER_NEAR_PX, SPRITE_TIER)).toBe(last);
    expect(tierFor(LEAVE_NEAR_PX + 0.5, CULLED)).toBe(SPRITE_TIER);
  });

  it('is sticky, so a body standing on a boundary does not flicker between two meshes', () => {
    // Already in tier 0: it keeps it until 15 % below the entry threshold.
    expect(tierFor(140, 0)).toBe(0);
    expect(tierFor(150 * (1 - HYSTERESIS) - 0.1, 0)).toBe(1);
    // Arriving from culled takes the plain thresholds — it has no history to be sticky about.
    expect(tierFor(140, CULLED)).toBe(1);
  });

  it('culls what is outside the frustum and keeps its tier out of the totals', () => {
    const planes = new Float32Array(24);
    // One plane: everything with x >= 0 is inside.
    planes[0] = 1;
    planes[3] = 0;
    for (let k = 1; k < 6; k++) {
      planes[k * 4] = 1;
      planes[k * 4 + 3] = 1e6;
    }

    const view: ViewParams = { eyeX: 0, eyeY: 0, eyeZ: 0, pixelsPerMetre: 500, planes };
    const m = bodies([
      [10, 0, 10],
      [-100, 0, 10],
    ]);
    const tiers = new Uint8Array(2).fill(CULLED);
    const totals = new Int32Array(SPRITE_TIER + 1);
    classify(m, 2, 1.64, view, tiers, totals);
    expect(tiers[1]).toBe(CULLED);
    expect(tiers[0]).not.toBe(CULLED);
    expect(totals.reduce((a, b) => a + b, 0)).toBe(1);
  });

  it('counts only what the CROWD draws, not what the sprite band will', () => {
    const view = openView(500);
    // 1.64 m tall at 500 px/m: 1 m away is 820 px (tier 0), 500 m away is 1.6 px (sprite).
    const m = bodies([
      [0, 0, 1],
      [0, 0, 500],
    ]);
    const tiers = new Uint8Array(2).fill(CULLED);
    const drawn = classify(m, 2, 1.64, view, tiers, null);
    expect(tiers[0]).toBe(0);
    expect(tiers[1]).toBe(SPRITE_TIER);
    expect(drawn, 'a body handed to the sprite band is not drawn by the crowd').toBe(1);
  });

  it('uses each body OWN height, because height drives the tier and the cull sphere', () => {
    // The spike used 1.7 m for every outfit while the published bodies are 1.64. One constant, three errors.
    // 1 000 px/m at ~10 m: a 4 m body projects to ~392 px (tier 0), a 1 m body to ~100 px (tier 1).
    const view = openView(1000);
    const m = bodies([
      [0, 0, 10],
      [0, 0, 10],
    ]);
    const heights = Float32Array.from([4, 1]);
    const tiers = new Uint8Array(2).fill(CULLED);
    classify(m, 2, heights, view, tiers, null);
    expect(tiers[0], 'a taller body at the same distance is a finer tier').toBeLessThan(tiers[1]);
  });
});

describe('packing the crowd into its draws', () => {
  it('compacts each tier into its own slot, in body order', () => {
    const tiers = Uint8Array.from([0, 1, 0, CULLED, 1]);
    const matrices = Float32Array.from({ length: 5 * 2 }, (_, i) => i);
    const counts = new Int32Array(2);
    const targets = [[new Float32Array(10)], [new Float32Array(10)]];
    packTiers(tiers, 5, Int8Array.from([0, 1, 1, -1]), [{ data: matrices, stride: 2 }], targets, counts);
    expect([...counts]).toEqual([2, 2]);
    // Bodies 0 and 2 to slot 0; bodies 1 and 4 to slot 1.
    expect([...targets[0][0].subarray(0, 4)]).toEqual([0, 1, 4, 5]);
    expect([...targets[1][0].subarray(0, 4)]).toEqual([2, 3, 8, 9]);
  });

  it('packs nothing for the sprite tier, however the slots are mapped', () => {
    // The guard is on the tier, not only on the slot table: a body below 7 px is the sprite band's, full stop.
    const tiers = Uint8Array.from([SPRITE_TIER, SPRITE_TIER]);
    const counts = new Int32Array(1);
    const targets = [[new Float32Array(4)]];
    packTiers(tiers, 2, Int8Array.from([0, 0, 0, 0]), [{ data: new Float32Array(4), stride: 2 }], targets, counts);
    expect(counts[0]).toBe(0);
  });

  it('collapses coarse tiers onto an outfit that carries fewer levels, in one draw', () => {
    // Two tiers sharing a slot must land in the SAME buffer, sequentially — not overwrite each other. The spike's
    // note is the reason this matters: thin-instance buffers live on the Geometry, so a per-tier clone of a mesh
    // would have each clone stamping over the other's instances.
    const tiers = Uint8Array.from([1, 2]);
    const matrices = Float32Array.from([1, 2, 3, 4]);
    const counts = new Int32Array(1);
    const targets = [[new Float32Array(4)]];
    packTiers(tiers, 2, Int8Array.from([0, 0, 0, -1]), [{ data: matrices, stride: 2 }], targets, counts);
    expect(counts[0]).toBe(2);
    expect([...targets[0][0]]).toEqual([1, 2, 3, 4]);
  });

  it('carries every per-instance buffer together, so the clip follows the matrix', () => {
    const tiers = Uint8Array.from([CULLED, 0]);
    const matrices = Float32Array.from([9, 9, 7, 7]);
    const clips = Float32Array.from([0, 0, 5, 6]);
    const counts = new Int32Array(1);
    const targets = [[new Float32Array(2), new Float32Array(2)]];
    packTiers(
      tiers,
      2,
      Int8Array.from([0, -1, -1, -1]),
      [
        { data: matrices, stride: 2 },
        { data: clips, stride: 2 },
      ],
      targets,
      counts,
    );
    expect([...targets[0][0]]).toEqual([7, 7]);
    expect([...targets[0][1]]).toEqual([5, 6]);
  });

  it('refuses a target set that does not match the sources', () => {
    expect(() => {
      packTiers(
        new Uint8Array(1),
        1,
        Int8Array.from([0]),
        [{ data: new Float32Array(1), stride: 1 }],
        [[]],
        new Int32Array(1),
      );
    }).toThrow(/one target per source/);
  });
});
