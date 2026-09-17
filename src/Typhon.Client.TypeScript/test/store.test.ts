import { describe, expect, it } from 'vitest';
import {
  archetypeOf,
  evaluateSlot,
  MOTION_STRIDE,
  NOT_FOUND,
  slotOf,
  validateSchema,
  WorldStore,
  type WorldSchema,
} from '../src/index.js';

const schema: WorldSchema = {
  archetypes: [
    {
      index: 0,
      name: 'Rock',
      position: 'static',
      groups: ['enter'],
      fields: [{ name: 'kind', kind: 'u8', group: 0 }],
    },
    {
      index: 1,
      name: 'Critter',
      position: 'motion',
      groups: ['enter', 'motion', 'state'],
      fields: [
        { name: 'template', kind: 'u8', group: 0 },
        { name: 'hp', kind: 'f32', group: 2 },
      ],
    },
  ],
};

const numeric = (a: number, b: number) => a - b;

describe('WorldStore', () => {
  it('maps netIds to archetype and slot, and forgets them on leave', () => {
    const world = new WorldStore(schema);
    world.beginFrame(1);
    const a = world.enter(1, 10);
    const b = world.enter(0, 11);
    world.endFrame();

    expect(archetypeOf(world.locate(10))).toBe(1);
    expect(slotOf(world.locate(10))).toBe(a);
    expect(archetypeOf(world.locate(11))).toBe(0);
    expect(slotOf(world.locate(11))).toBe(b);
    expect(world.entityCount).toBe(2);

    world.beginFrame(2);
    expect(world.leave(10)).toBe(true);
    expect(world.locate(10)).toBe(NOT_FOUND);
    expect(world.archetypes[1]!.leftCount).toBe(1);
    expect(world.archetypes[1]!.left[0]).toBe(10);
    expect(world.entityCount).toBe(1);
  });

  it('never reuses a slot inside the frame that freed it', () => {
    const world = new WorldStore(schema);
    world.beginFrame(1);
    const first = world.enter(1, 1);
    world.endFrame();

    world.beginFrame(2);
    world.leave(1);
    const second = world.enter(1, 2);
    expect(second).not.toBe(first);
    world.endFrame();

    world.beginFrame(3);
    world.leave(2);
    const third = world.enter(1, 3);
    expect(third).toBe(first);
  });

  it('resolves a netId reused within one frame: the new entity survives the old one’s leave', () => {
    // Wire order: enters first, leaves last. The server released netId 5 and gave it to another entity in the same tick.
    const world = new WorldStore(schema);
    world.beginFrame(1);
    world.enter(1, 5);
    world.endFrame();

    world.beginFrame(2);
    const slot = world.enter(0, 5);
    expect(world.leave(5)).toBe(true);
    world.endFrame();

    expect(world.locate(5)).not.toBe(NOT_FOUND);
    expect(archetypeOf(world.locate(5))).toBe(0);
    expect(slotOf(world.locate(5))).toBe(slot);
    expect(world.entityCount).toBe(1);
    expect(world.archetypes[1]!.leftCount).toBe(1);
    expect(world.anomalies).toBe(0);

    // The next frame's leave is a real one.
    world.beginFrame(3);
    expect(world.leave(5)).toBe(true);
    expect(world.entityCount).toBe(0);
  });

  it('keeps live slots dense and consistent through swap-remove', () => {
    const world = new WorldStore(schema);
    const store = world.archetypes[1]!;
    world.beginFrame(1);
    for (let id = 1; id <= 5; id++) {
      world.enter(1, id);
    }

    world.beginFrame(2);
    world.leave(2);
    world.leave(4);
    const liveNetIds = Array.from(store.live.subarray(0, store.liveCount), (slot) => store.netIds[slot]!).sort(numeric);
    expect(liveNetIds).toEqual([1, 3, 5]);
    for (let i = 0; i < store.liveCount; i++) {
      expect(store.isLive(store.live[i]!)).toBe(true);
    }
  });

  it('grows past its capacity without losing data, including slots pending release and this frame’s lists', () => {
    const world = new WorldStore(schema);
    const store = world.archetypes[1]!;
    world.beginFrame(1);
    for (let id = 1; id <= 100; id++) {
      world.enter(1, id);
    }

    world.beginFrame(2);
    for (let id = 1; id <= 50; id++) {
      world.leave(id);
    }

    const version = store.version;
    for (let id = 101; id <= 1100; id++) {
      const slot = world.enter(1, id);
      store.field('hp')[slot] = id / 2000;
      store.resetMotion(slot, id, -id, 0, 0, 2, 0);
    }

    expect(store.version).toBeGreaterThan(version);
    expect(store.enteredCount).toBe(1000);
    expect(store.leftCount).toBe(50);
    const out = new Float64Array(MOTION_STRIDE);
    for (let id = 51; id <= 1100; id++) {
      const slot = slotOf(world.locate(id));
      expect(store.netIds[slot]).toBe(id);
      if (id > 100) {
        expect(store.field('hp')[slot]).toBeCloseTo(id / 2000, 6);
        evaluateSlot(store, slot, 2, 0, out, 0);
        expect(out[0]).toBe(id);
      }
    }

    // Slots released before the growth are still held back for this frame, then reusable.
    world.beginFrame(3);
    const capacity = store.capacity;
    for (let id = 2000; id < 2050; id++) {
      world.enter(1, id);
    }

    expect(store.capacity).toBe(capacity);
  });

  it('records enters, updates with merged masks, and leaves per frame, then clears them', () => {
    const world = new WorldStore(schema);
    const store = world.archetypes[1]!;
    world.beginFrame(1);
    const slot = world.enter(1, 7);
    expect(store.enteredCount).toBe(1);
    world.endFrame();

    world.beginFrame(2);
    expect(store.enteredCount).toBe(0);
    store.markUpdated(slot, 0b100);
    store.pushSegment(slot, 1, 2, 0.1, 0.2, 2, 0);
    expect(store.updatedCount).toBe(1);
    expect(store.updateMask[slot]).toBe(0b110);
    world.endFrame();

    world.beginFrame(3);
    expect(store.updatedCount).toBe(0);
    expect(store.updateMask[slot]).toBe(0);
  });

  it('clears the mask of a slot updated then released in the same frame, and ignores updates to dead slots', () => {
    const world = new WorldStore(schema);
    const store = world.archetypes[1]!;
    world.beginFrame(1);
    const slot = world.enter(1, 7);
    world.beginFrame(2);
    store.markUpdated(slot, 0b100);
    world.leave(7);
    expect(store.updateMask[slot]).toBe(0);
    store.markUpdated(slot, 0b100);
    store.pushSegment(slot, 0, 0, 0, 0, 2, 0);
    expect(store.updateMask[slot]).toBe(0);
    expect(() => {
      store.release(slot);
    }).toThrow();
    expect(() => {
      store.release(store.capacity + 5);
    }).toThrow();
  });

  it('zeroes the fields of a reused slot', () => {
    const world = new WorldStore(schema);
    const store = world.archetypes[1]!;
    world.beginFrame(1);
    const slot = world.enter(1, 1);
    store.field('template')[slot] = 9;
    world.beginFrame(2);
    world.leave(1);
    world.beginFrame(3);
    const reused = world.enter(1, 2);
    expect(reused).toBe(slot);
    expect(store.field('template')[reused]).toBe(0);
  });

  it('counts a leave of an unknown netId and a tick that does not advance as anomalies', () => {
    const world = new WorldStore(schema);
    world.beginFrame(1);
    expect(world.leave(99)).toBe(false);
    expect(world.anomalies).toBe(1);
    world.beginFrame(1);
    expect(world.anomalies).toBe(2);
  });

  it('drops everything on reset, reports it as leaves, and refills without growing', () => {
    const world = new WorldStore(schema);
    const store = world.archetypes[1]!;
    world.beginFrame(1);
    for (let id = 1; id <= store.capacity; id++) {
      world.enter(1, id);
    }

    const capacity = store.capacity;
    world.beginFrame(2);
    world.reset();
    expect(world.resetThisFrame).toBe(true);
    expect(world.entityCount).toBe(0);
    expect(store.leftCount).toBe(capacity);
    expect(world.locate(1)).toBe(NOT_FOUND);
    for (let id = 1000; id < 1000 + capacity; id++) {
      world.enter(1, id);
    }

    expect(store.capacity).toBe(capacity);
    world.beginFrame(3);
    expect(world.resetThisFrame).toBe(false);
  });

  it('rejects netId 0, fractional ids and ids beyond the configured bound', () => {
    const world = new WorldStore(schema, { maxNetId: 1000 });
    world.beginFrame(1);
    expect(() => world.enter(1, 0)).toThrow();
    expect(() => world.enter(1, 1.5)).toThrow();
    expect(() => world.enter(1, 1001)).toThrow();
    expect(world.enter(1, 1000)).toBe(0);
  });
});

describe('validateSchema', () => {
  it('rejects a motion archetype without a motion group', () => {
    expect(() => {
      validateSchema({ archetypes: [{ index: 0, name: 'X', position: 'motion', groups: ['state'], fields: [] }] });
    }).toThrow(/motion/);
  });

  it('rejects a misplaced index and an unknown group', () => {
    expect(() => {
      validateSchema({ archetypes: [{ index: 1, name: 'X', position: 'none', groups: [], fields: [] }] });
    }).toThrow(/index/);
    expect(() => {
      validateSchema({
        archetypes: [
          { index: 0, name: 'X', position: 'none', groups: ['a'], fields: [{ name: 'f', kind: 'u8', group: 3 }] },
        ],
      });
    }).toThrow(/group/);
  });
});
