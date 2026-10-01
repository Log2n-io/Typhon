import { FLAT_GROUND } from '../src/terrain/ground-sampler';
import { WorldStore, type WorldSchema } from '@typhondb/client';
import { describe, expect, it } from 'vitest';
import { Placement } from '../src/data/placement';
import { TICK_PERIOD_MS } from '../src/data/swg-schema';
import { LayerPacker, type FrameView } from '../src/render/layer-packer';
import { styleFor } from '../src/render/styles';
import { extractFrustumPlanes } from '../src/render/view-math';

/*
 * CLI3D-04 — a three-axis archetype.
 *
 * Every position reader in this client assumed `dims === 2` and indexed a fixed layout, so in a `pos3` realm the
 * altitude was drawn as the second ground axis and a velocity component was read as a heading input. These cases run a
 * store that really has three dimensions, which is the only thing that can tell the two layouts apart: with `dims === 2`
 * the old indices and the new ones agree exactly, which is why nothing caught it.
 */

/** One archetype, three-dimensional, otherwise the shape of a SWG one. */
const DEEP_SCHEMA: WorldSchema = {
  tickPeriodUs: TICK_PERIOD_MS * 1000,
  archetypes: [
    {
      index: 0,
      name: 'Player',
      position: { kind: 'motion', dims: 3 },
      groups: ['state', 'vitals'],
      fields: [
        { name: 'activity', kind: 'u8', group: 0 },
        { name: 'controller', kind: 'u8', group: 0 },
        { name: 'target', kind: 'u32', group: 0 },
        { name: 'hp', kind: 'f32', group: 1 },
      ],
    },
  ],
};

function view(): FrameView {
  const m = new Float32Array(16);
  m[0] = 1;
  m[5] = 1;
  m[10] = 1001 / 999;
  m[11] = 1;
  m[14] = -2000 / 999;
  const planes = new Float64Array(24);
  extractFrustumPlanes(m, planes);
  return {
    renderTick: 1,
    renderFrac: 0,
    originX: 0,
    originZ: 0,
    eyeX: 0,
    eyeY: 0,
    eyeZ: 0,
    planes,
    pixelsPerMetre: 400,
    viewportWidth: 800,
    viewportHeight: 800,
    selectedNetId: 0,
    ground: FLAT_GROUND,
  };
}

/** A world of the deep schema, and a placer taking engine axes: ground x, ground y (the renderer's z), altitude. */
function setup() {
  const world = new WorldStore(DEEP_SCHEMA, { maxNetId: 4096 });
  const store = world.archetypeStore(0);
  const packer = new LayerPacker(0, styleFor('Player'));
  packer.bind(store);
  world.beginFrame(1);
  const place = (netId: number, p: [number, number, number], v: [number, number, number]): number => {
    const slot = world.enter(0, netId);
    store.resetMotion(slot, p, v, 1, 0);
    return slot;
  };
  return { world, store, packer, place };
}

describe('Placement', () => {
  it('reads velocity at dims, not at 2 — the whole bug', () => {
    // A record laid out p[3] then v[3]: altitude 40, and a GROUND velocity of (3, 4) with a climb of 99.
    const motion = Float64Array.from([10, 20, 40, 3, 4, 99]);
    const at = new Placement().read(3, motion, 0);

    expect([at.x, at.z, at.y]).toEqual([10, 20, 40]);
    expect([at.vx, at.vz, at.vy]).toEqual([3, 4, 99]);

    // The old code read motion[2] and motion[3] as the ground velocity: that is (40, 3) — the altitude and one
    // velocity component — which is what made a climbing ship point in a direction it was not going.
    expect([at.vx, at.vz]).not.toEqual([motion[2], motion[3]]);
  });

  it('reports 0 altitude and the same ground values for a two-axis record', () => {
    const at = new Placement().read(2, Float64Array.from([10, 20, 3, 4]), 0);

    expect([at.x, at.z, at.y]).toEqual([10, 20, 0]);
    expect([at.vx, at.vz, at.vy]).toEqual([3, 4, 0]);
  });

  it('reads at an offset, so a caller may batch records', () => {
    const at = new Placement().read(2, Float64Array.from([0, 0, 0, 0, 7, 8, 1, 2]), 4);

    expect([at.x, at.z, at.vx, at.vz]).toEqual([7, 8, 1, 2]);
  });

  it('measures speed over the ground and heading from the ground velocity', () => {
    // Climbing at 100 units a tick while crossing the ground at (0, 1): speed and heading must ignore the climb.
    const at = new Placement().read(3, Float64Array.from([0, 0, 0, 0, 1, 100]), 0);

    expect(at.groundSpeedMps(TICK_PERIOD_MS)).toBeCloseTo(10, 9);
    expect(at.headingDeg()).toBeCloseTo(0, 9);
  });
});

describe('LayerPacker with a three-axis store', () => {
  it('packs (x, y, z) with the altitude in y, and the yaw in its own buffer', () => {
    const { packer, place } = setup();
    place(11, [5, 100, 40], [0, 0, 0]); // 8.8 px at 100 m: a mesh

    packer.pack(view());

    expect(packer.nearCount).toBe(1);
    expect(Array.from(packer.nearData.subarray(0, 3))).toEqual([5, 40, 100]);
    expect(packer.nearYaw.length).toBeGreaterThanOrEqual(1);
  });

  it('turns a mesh by its GROUND heading, ignoring the climb', () => {
    const { packer, place } = setup();
    // Ground velocity (1, 0) is +x, which is a quarter turn; the climb of 50 must not enter it.
    place(12, [0, 100, 0], [1, 0, 50]);

    packer.pack(view());

    expect(packer.nearCount).toBe(1);
    expect(packer.nearYaw[0]).toBeCloseTo(Math.PI / 2, 5);
  });

  it('culls by where the entity actually is, so a thing far overhead is not drawn at your feet', () => {
    const { packer, place } = setup();
    // 100 m down +z, but 300 m up. The frustum is 90° from the origin, so its half-height at 100 m is 100 m: this is
    // well outside it. Culling that reads only the ground point keeps it, and it then draws in the middle of the view.
    place(14, [0, 100, 300], [0, 0, 0]);
    packer.pack(view());
    expect(packer.nearCount + packer.farCount).toBe(0);

    // The same entity at an altitude inside the frustum is kept, so the case above is culling and not a dead fixture.
    place(15, [0, 100, 40], [0, 0, 0]);
    packer.pack(view());
    expect(packer.nearCount + packer.farCount).toBe(1);
  });

  it('keeps altitude out of the render origin, which only slides along the ground', () => {
    const { packer, place } = setup();
    place(13, [1000, 1000, 40], [0, 0, 0]);
    // The origin has slid 1000 m in x and 900 m in z. The camera stays at the origin looking down +z, so the entity
    // lands at render (0, 40, 100) — inside a 90° frustum, whose half-height at 100 m is 100 m.
    packer.pack({ ...view(), originX: 1000, originZ: 900 });

    expect(packer.nearCount).toBe(1);
    // x and z are relative to the origin; y is NOT — an altitude offset would put every flying thing underground.
    expect(Array.from(packer.nearData.subarray(0, 3))).toEqual([0, 40, 100]);
  });
});
