import { WorldStore } from '@typhondb/client';
import { describe, expect, it } from 'vitest';
import { Archetype, SWG_SCHEMA } from '../src/data/swg-schema';
import { LayerPacker, type FrameView } from '../src/render/layer-packer';
import { LAYER_STYLES } from '../src/render/styles';
import { extractFrustumPlanes, packedStyle, SELECTED_BIT } from '../src/render/view-math';

/** Eye at the render origin looking down +Z, 90° vertical field of view, 800 × 800 CSS px: 400 px per metre at 1 m. */
function view(selectedNetId = 0): FrameView {
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
    selectedNetId,
  };
}

function setup() {
  const world = new WorldStore(SWG_SCHEMA, { maxNetId: 4096 });
  const store = world.archetypeStore(Archetype.Player);
  const packer = new LayerPacker(Archetype.Player, LAYER_STYLES[Archetype.Player]);
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
    expect(Array.from(packer.nearData.subarray(0, 2))).toEqual([0, 100]);
    expect(packedStyle(packer.nearData[3])).toBe(0);
  });

  it("keeps an entity just outside the frustum by less than its shape's bounds", () => {
    const { packer, place } = setup();
    place(21, 100 + 1 * Math.SQRT2, 100); // 1 m outside the side plane: within the 1.9 m bound
    place(22, 104, 100); // 2.8 m outside: beyond the bound and the sprite
    packer.pack(view());
    expect(packer.nearCount + packer.farCount).toBe(1);
    expect(packer.nearCount === 1 ? packer.nearNetIds[0] : packer.farNetIds[0]).toBe(21);
  });

  it('marks the selected entity', () => {
    const { packer, place } = setup();
    place(31, 0, 100);
    packer.pack(view(31));
    expect(packer.nearData[3] >= SELECTED_BIT).toBe(true);
    packer.pack(view(0));
    expect(packer.nearData[3] >= SELECTED_BIT).toBe(false);
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
