import { FLAT_GROUND } from '../src/terrain/ground-sampler';
import { WorldStore } from '@typhondb/client';
import { describe, expect, it } from 'vitest';
import { Archetype, SWG_SCHEMA } from '../src/data/swg-schema';
import { LayerPacker, type FrameView } from '../src/render/layer-packer';
import { styleFor } from '../src/render/styles';
import { extractFrustumPlanes, packedStyle } from '../src/render/view-math';

/** Eye at the render origin looking down +Z, 90° vertical field of view, 800 × 800 CSS px: 400 px per metre at 1 m. */
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
    ground: FLAT_GROUND,
    predicted: null,
    hidden: new Set<number>(),
    drawnElsewhere: new Set<number>(),
    crowd: null,
    crowdDraws: false,
    crowdCount: 0,
    perSecond: 1,
  };
}

function setup() {
  const world = new WorldStore(SWG_SCHEMA, { maxNetId: 4096 });
  const store = world.archetypeStore(Archetype.Player);
  const packer = new LayerPacker(Archetype.Player, styleFor('Player'));
  packer.bind(store);
  let tick = 1;
  world.beginFrame(tick);
  const place = (netId: number, x: number, z: number): number => {
    const slot = world.enter(Archetype.Player, netId);
    store.resetMotion(slot, [x, z], [0, 0], 1, 0);
    return slot;
  };
  const move = (slot: number, x: number, z: number): void => {
    store.resetMotion(slot, [x, z], [0, 0], 1, 0);
  };
  const nextFrame = (): void => {
    world.beginFrame(++tick);
  };
  return { world, store, packer, place, move, nextFrame };
}

// A player is 2.2 m tall (its largest extent): at 400 px per metre it spans 880 / z pixels at distance z.
describe('LayerPacker', () => {
  it('bands by projected size, culls what is behind or beside the view, and packs the netId with each instance', () => {
    const { packer, place } = setup();
    place(11, 0, 100); // 8.8 px: mesh
    place(12, 0, 200); // 4.4 px: sprite
    place(13, 0, -50); // behind
    place(14, 150, 100); // 35 m outside the side plane
    packer.pack(view());

    expect(packer.nearCount).toBe(1);
    expect(packer.farCount).toBe(1);
    expect(packer.nearNetIds[0]).toBe(11);
    expect(packer.farNetIds[0]).toBe(12);
    // (x, y, z, packed): the altitude slot is 0 for a two-axis archetype, which every SWG one is (CLI3D-04).
    expect(Array.from(packer.nearData.subarray(0, 3))).toEqual([0, 0, 100]);
    expect(packedStyle(packer.nearData[3])).toBe(0);
  });

  it('stands every instance on the ground, in both bands, at the ground under ITS OWN position', () => {
    // The server is 2D, so an entity's own altitude is 0 and the terrain is the whole of its height. A sampler that
    // returned one height for the frame — the camera's, say — would pass a test with one entity on flat ground; this one
    // puts two at different x on a ramp, in different bands, so only a per-entity sample gets both right.
    const { packer, place } = setup();
    place(31, 0, 100); // mesh band
    place(32, 40, 200); // sprite band
    packer.pack({ ...view(), ground: { heightAt: (x, z) => x * 0.25 + z * 0.01 } });

    expect(packer.nearCount).toBe(1);
    expect(packer.farCount).toBe(1);
    expect(packer.nearData[1]).toBeCloseTo(1, 6);
    expect(packer.farData[1]).toBeCloseTo(12, 6);
  });

  it("keeps an entity just outside the frustum by less than its shape's bounds", () => {
    const { packer, place } = setup();
    place(21, 100 + 1 * Math.SQRT2, 100); // 1 m outside the side plane: within the 1.9 m bound
    place(22, 104, 100); // 2.8 m outside: beyond the bound and the sprite
    packer.pack(view());
    expect(packer.nearCount + packer.farCount).toBe(1);
    expect(packer.nearCount === 1 ? packer.nearNetIds[0] : packer.farNetIds[0]).toBe(21);
  });

  it('skips the one body something else is drawing, and draws it again when nothing is', () => {
    // The rigged avatar stands in for the ridden body. Both drawn would be a capsule inside a character, and the
    // capsule — a sprite at any distance — would be the visible one.
    const { packer, place } = setup();
    place(51, 0, 100);
    place(52, 10, 100);

    packer.pack({ ...view(), hidden: new Set([51]) });
    expect(packer.nearCount).toBe(1);
    expect(packer.nearNetIds[0]).toBe(52);

    packer.pack(view());
    expect(packer.nearCount).toBe(2);
  });

  it('holds a body its asset draws: drawn by nobody here, still there for the pick', () => {
    // A camp standing as its asset. Hidden, it was out of the arrays the pick reads, and its lair could not be targeted.
    const { packer, place } = setup();
    place(61, 0, 100);
    place(62, 10, 100);

    packer.pack({ ...view(), drawnElsewhere: new Set([61]) });
    expect(packer.nearCount).toBe(1);
    expect(packer.nearNetIds[0]).toBe(62);
    expect(packer.heldCount).toBe(1);
    expect(packer.heldNetIds[0]).toBe(61);

    packer.pack({ ...view(), hidden: new Set([61]) });
    expect(packer.heldCount, 'a hidden body is not picked: the rigged avatar, a dormant lair').toBe(0);
  });

  it('keeps a band with hysteresis, and starts over when a slot is reused by another entity', () => {
    const { world, packer, place, move, nextFrame } = setup();
    const slot = place(41, 0, 100);
    packer.pack(view());
    expect(packer.nearCount).toBe(1);

    // 5.5 px: below the 7 px to enter, above the 5 px to leave — a mesh stays a mesh.
    move(slot, 0, 160);
    packer.pack(view());
    expect(packer.nearCount).toBe(1);

    nextFrame();
    world.leave(41);
    nextFrame();
    const reused = place(42, 0, 160);
    expect(reused).toBe(slot);
    packer.pack(view());
    // A new entity starts from the midpoint, 6 px: at 5.5 px it is a sprite, whatever the slot held before.
    expect(packer.nearCount).toBe(0);
    expect(packer.farNetIds[0]).toBe(42);
  });

  /**
   * A14. The mesh of every entity is placed from the packer's loop, so a prediction that reached only the camera would slide
   * the view off a character standing where the server last confirmed it — which looks worse than the latency it removes.
   */
  it('draws the predicted body at its predicted position, and nothing else', () => {
    const { packer, place } = setup();
    place(77, 0, 100);
    place(78, 1, 100);
    packer.pack({ ...view(), predicted: { netId: 77, x: 0, z: 60 } });

    expect(packer.nearCount).toBe(2);
    const own = packer.nearNetIds[0] === 77 ? 0 : 1;
    const other = 1 - own;
    expect(Array.from(packer.nearData.subarray(own * 4, own * 4 + 3))).toEqual([0, 0, 60]);
    expect(Array.from(packer.nearData.subarray(other * 4, other * 4 + 3))).toEqual([1, 0, 100]);
  });

  /** A god camera and a spectator predict nothing, so a null override must leave every entity exactly where the store says. */
  it('leaves every entity authoritative when there is no prediction', () => {
    const { packer, place } = setup();
    place(77, 0, 100);
    packer.pack(view());

    expect(packer.nearCount).toBe(1);
    expect(Array.from(packer.nearData.subarray(0, 3))).toEqual([0, 0, 100]);
  });

  /** The predicted body stands on the ground under ITS OWN position, or its feet sink on a dune by the lead distance. */
  it('samples the ground under the predicted position', () => {
    const { packer, place } = setup();
    place(77, 0, 100);
    packer.pack({
      ...view(),
      predicted: { netId: 77, x: 0, z: 60 },
      ground: { heightAt: (_x, z) => z * 0.1 },
    });

    expect(packer.nearCount).toBe(1);
    expect(packer.nearData[1]).toBeCloseTo(6, 6);
  });

  it('replaces its arrays when the view outgrows them, and says so', () => {
    const { packer, place } = setup();
    const version = packer.arraysVersion;
    for (let i = 0; i < 1500; i++) {
      place(100 + i, 0, 100 + i * 0.01);
    }

    packer.pack(view());
    expect(packer.capacity).toBeGreaterThanOrEqual(1500);
    expect(packer.arraysVersion).toBeGreaterThan(version);
    expect(packer.nearCount + packer.farCount).toBe(1500);
  });
});

/**
 * A creature standing to fight faces whom it fights — its replicated `target`, a netId of this view — rather than the way
 * its last step left it (Loïc, 2026-10-24: "creatures attacking us are not always facing us").
 */
describe('a creature standing in a fight', () => {
  function creature(target: number) {
    const world = new WorldStore(SWG_SCHEMA, { maxNetId: 4096 });
    const store = world.archetypeStore(Archetype.Creature);
    const packer = new LayerPacker(Archetype.Creature, styleFor('Creature'));
    packer.bind(store);
    world.beginFrame(1);
    const slot = world.enter(Archetype.Creature, 21);
    // Walked here heading +Z, and stopped.
    store.resetMotion(slot, [0, 10], [0, 0.5], 1, 0);
    store.fieldAt(store.fieldIndex('target'))[slot] = target;
    return { world, store, packer, slot };
  }

  const quarryAt = (x: number, z: number) => (netId: number, out: { x: number; z: number }) => {
    if (netId !== 99) {
      return false;
    }

    out.x = x;
    out.z = z;
    return true;
  };

  it('turns to face its quarry once it stands', () => {
    const { world, store, packer, slot } = creature(99);
    packer.pack({ ...view(), locate: quarryAt(10, 10) });
    world.beginFrame(2);
    store.resetMotion(slot, [0, 10], [0, 0], 2, 0);
    packer.pack({ ...view(), renderTick: 2, locate: quarryAt(10, 10) });
    // Babylon's yaw, atan2(dx, dz): the quarry is due +X of it.
    expect(packer.nearYaw[0]).toBeCloseTo(Math.PI / 2, 5);
  });

  it('keeps its heading with no quarry, or one this view does not hold', () => {
    for (const target of [0, 77]) {
      const { world, store, packer, slot } = creature(target);
      packer.pack({ ...view(), locate: quarryAt(10, 10) });
      const walked = packer.nearYaw[0];
      world.beginFrame(2);
      store.resetMotion(slot, [0, 10], [0, 0], 2, 0);
      packer.pack({ ...view(), renderTick: 2, locate: quarryAt(10, 10) });
      expect(packer.nearYaw[0]).toBeCloseTo(walked, 5);
    }
  });
});
