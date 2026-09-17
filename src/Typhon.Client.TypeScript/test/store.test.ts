import { describe, expect, it } from 'vitest';
import {
  archetypeOf,
  evaluateSlot,
  MAX_MOTION_STRIDE,
  MOTION_CHANGE_BIT,
  NOT_FOUND,
  slotOf,
  validateSchema,
  WorldStore,
  type ArchetypeSchema,
  type PositionSchema,
  type WorldSchema,
} from '../src/index.js';

const schema: WorldSchema = {
  tickPeriodUs: 100_000,
  archetypes: [
    {
      index: 0,
      name: 'Rock',
      position: { kind: 'static', dims: 2 },
      groups: [],
      fields: [{ name: 'kind', kind: 'u8' }],
    },
    {
      index: 1,
      name: 'Critter',
      position: { kind: 'motion', dims: 2 },
      groups: ['state'],
      fields: [
        { name: 'template', kind: 'u8' },
        { name: 'hp', kind: 'f32', group: 0 },
      ],
    },
    {
      index: 2,
      name: 'Probe',
      position: { kind: 'motion', dims: 3 },
      groups: ['look', 'tags'],
      fields: [
        { name: 'rotation', kind: 'f64', components: 4, group: 0 },
        { name: 'label', kind: 'text', group: 1 },
        { name: 'serial', kind: 'bytes' },
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

  it('applies § 10 netId reuse: an enter for a held netId replaces it as an anomaly; a leave removes the holder', () => {
    const world = new WorldStore(schema);
    world.beginFrame(1);
    world.enter(1, 5);
    world.endFrame();

    // A reuse the server spreads over two frames: the leave, then the enter. No anomaly.
    world.beginFrame(2);
    expect(world.leave(5)).toBe(true);
    world.endFrame();
    world.beginFrame(3);
    world.enter(0, 5);
    world.endFrame();
    expect(world.anomalies).toBe(0);

    // An enter for a netId still held replaces the holder, which is listed as left.
    world.beginFrame(4);
    const slot = world.enter(1, 5);
    expect(world.anomalies).toBe(1);
    expect(archetypeOf(world.locate(5))).toBe(1);
    expect(slotOf(world.locate(5))).toBe(slot);
    expect(world.archetypes[0]!.leftCount).toBe(1);
    expect(world.entityCount).toBe(1);

    // A leave naming another archetype changes nothing; one naming the holder's, or none, removes it.
    expect(world.leave(5, 0)).toBe(false);
    expect(world.anomalies).toBe(2);
    expect(world.leave(5, 1)).toBe(true);
    expect(world.entityCount).toBe(0);
    expect(world.leave(5)).toBe(false);
    expect(world.anomalies).toBe(3);
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
      store.resetMotion(slot, [id, -id], [0, 0], 2, 0);
    }

    expect(store.version).toBeGreaterThan(version);
    expect(store.enteredCount).toBe(1000);
    expect(store.leftCount).toBe(50);
    const out = new Float64Array(MAX_MOTION_STRIDE);
    for (let id = 51; id <= 1100; id++) {
      const slot = slotOf(world.locate(id));
      expect(store.netIds[slot]).toBe(id);
      if (id > 100) {
        expect(store.field('hp')[slot]).toBeCloseTo(id / 2000, 6);
        evaluateSlot(store, slot, 2, 0, out, 0);
        expect([out[0], out[1]]).toEqual([id, -id]);
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

  it('records enters, merged update masks (motion outside the group bits) and leaves per frame, then clears them', () => {
    const world = new WorldStore(schema);
    const store = world.archetypes[1]!;
    world.beginFrame(1);
    const slot = world.enter(1, 7);
    expect(store.enteredCount).toBe(1);
    world.endFrame();

    world.beginFrame(2);
    expect(store.enteredCount).toBe(0);
    store.markUpdated(slot, 0b1);
    store.pushSegment(slot, [1, 2], [0.1, 0.2], 2, 0);
    expect(store.updatedCount).toBe(1);
    expect(store.updateMask[slot]).toBe(0b1 | MOTION_CHANGE_BIT);
    expect(MOTION_CHANGE_BIT).toBe(0x100);
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
    store.markUpdated(slot, 0b1);
    world.leave(7);
    expect(store.updateMask[slot]).toBe(0);
    store.markUpdated(slot, 0b1);
    store.pushSegment(slot, [0, 0], [0, 0], 2, 0);
    expect(store.updateMask[slot]).toBe(0);
    expect(() => {
      store.release(slot);
    }).toThrow();
    expect(() => {
      store.release(store.capacity + 5);
    }).toThrow();
  });

  it('zeroes the fields of a reused slot, numeric, multi-component, text and bytes alike', () => {
    const world = new WorldStore(schema);
    const probe = world.archetypes[2]!;
    world.beginFrame(1);
    const slot = world.enter(2, 1);
    probe.field('rotation').set([0.1, 0.2, 0.3, 0.9], slot * 4);
    probe.textAt(probe.fieldIndex('label'))[slot] = 'alpha';
    probe.bytesAt(probe.fieldIndex('serial'))[slot] = Uint8Array.of(1, 2);
    probe.resetMotion(slot, [1, 2, 3], [4, 5, 6], 1, 3);
    probe.pushSegment(slot, [7, 8, 9], [1, 1, 1], 2, 4);
    world.beginFrame(2);
    world.leave(1);
    world.beginFrame(3);
    const reused = world.enter(2, 2);
    expect(reused).toBe(slot);
    expect(Array.from(probe.field('rotation').subarray(slot * 4, slot * 4 + 4))).toEqual([0, 0, 0, 0]);
    expect(probe.textAt(1)[reused]).toBe('');
    expect(probe.bytesAt(2)[reused]!.length).toBe(0);
    // Motion: head, count and entry 0 are zeroed; the entries past the count are never read.
    const motion = new Float64Array(MAX_MOTION_STRIDE);
    evaluateSlot(probe, reused, 10, 0.5, motion, 0);
    expect(Array.from(motion)).toEqual([0, 0, 0, 0, 0, 0]);
    expect(probe.headEntry(reused)).toBe(0);
    expect(probe.fieldComponents).toEqual([4, 0, 0]);
    expect(() => probe.field('label')).toThrow(/numeric/);
  });

  it('keeps multi-component and text values through growth', () => {
    const world = new WorldStore(schema);
    const probe = world.archetypes[2]!;
    world.beginFrame(1);
    for (let id = 1; id <= 600; id++) {
      const slot = world.enter(2, id);
      probe.field('rotation')[slot * 4 + 3] = id;
      probe.textAt(1)[slot] = `p${id}`;
    }

    for (let id = 1; id <= 600; id++) {
      const slot = slotOf(world.locate(id));
      expect(probe.field('rotation')[slot * 4 + 3]).toBe(id);
      expect(probe.textAt(1)[slot]).toBe(`p${id}`);
    }
  });

  it('counts a leave of an unknown netId and a tick that does not advance as anomalies', () => {
    const world = new WorldStore(schema);
    world.beginFrame(1);
    expect(world.leave(99)).toBe(false);
    expect(world.anomalies).toBe(1);
    world.beginFrame(1);
    expect(world.anomalies).toBe(2);

    // A RESET frame may go back: a restarted server begins again at a low tick.
    world.beginFrame(10);
    world.beginFrame(3, true);
    expect(world.anomalies).toBe(2);
    world.beginFrame(3);
    expect(world.anomalies).toBe(3);
  });

  it('drops everything on reset without listing it as left, and refills without growing', () => {
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
    expect(store.leftCount).toBe(0);
    expect(world.locate(1)).toBe(NOT_FOUND);
    for (let id = 1000; id < 1000 + capacity; id++) {
      world.enter(1, id);
    }

    expect(store.capacity).toBe(capacity);
    expect(store.enteredCount).toBe(capacity);
    world.beginFrame(3);
    expect(world.resetThisFrame).toBe(false);
  });

  it('refuses a maxNetId outside [1, 2^32 − 1] and a render delay that is not positive, and locates no fraction', () => {
    for (const maxNetId of [0, 1.5, 2 ** 32, -1]) {
      expect(() => new WorldStore(schema, { maxNetId })).toThrow(/maxNetId/);
    }

    for (const maxRenderDelayMs of [0, -5, Infinity, Number.NaN]) {
      expect(() => new WorldStore(schema, { maxRenderDelayMs })).toThrow(/maxRenderDelayMs/);
    }

    const world = new WorldStore(schema);
    world.beginFrame(1);
    world.enter(1, 3);
    expect(world.locate(3)).not.toBe(NOT_FOUND);
    expect(world.locate(3.5)).toBe(NOT_FOUND);
    expect(world.locate(-3)).toBe(NOT_FOUND);
    expect(world.locate(Number.NaN)).toBe(NOT_FOUND);
  });

  it('starts small and doubles', () => {
    const world = new WorldStore(schema);
    const store = world.archetypes[2]!;
    expect(store.capacity).toBe(16);
    world.beginFrame(1);
    for (let id = 1; id <= 17; id++) {
      world.enter(2, id);
    }

    expect(store.capacity).toBe(32);
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
  const world = (archetype: ArchetypeSchema, tickPeriodUs = 100_000): WorldSchema => ({
    tickPeriodUs,
    archetypes: [archetype],
  });

  it('rejects more than eight groups, and a position of other than 2 or 3 dimensions', () => {
    expect(() => {
      validateSchema(world({ index: 0, name: 'X', groups: ['a', 'b', 'c', 'd', 'e', 'f', 'g', 'h', 'i'], fields: [] }));
    }).toThrow(/8/);
    expect(() => {
      validateSchema(
        world({ index: 0, name: 'X', position: { kind: 'motion', dims: 4 as 3 }, groups: [], fields: [] }),
      );
    }).toThrow(/dimensions/);
  });

  it('rejects an unknown position kind or model, and a model on a static position', () => {
    const at = (position: object) =>
      world({ index: 0, name: 'X', position: position as PositionSchema, groups: [], fields: [] });
    expect(() => {
      validateSchema(at({ kind: 'none', dims: 2 }));
    }).toThrow(/kind/);
    expect(() => {
      validateSchema(at({ kind: 'motion', model: 'spline', dims: 2 }));
    }).toThrow(/model/);
    expect(() => {
      validateSchema(at({ kind: 'static', model: 'linear', dims: 2 }));
    }).toThrow(/model/);
    validateSchema(at({ kind: 'motion', model: 'none', dims: 3 }));
  });

  it('rejects a field kind the store does not know', () => {
    expect(() => {
      validateSchema(world({ index: 0, name: 'X', groups: [], fields: [{ name: 'f', kind: 'u64' as 'u32' }] }));
    }).toThrow(/kind 'u64'/);
  });

  it('rejects a tick period that is not a positive integer of microseconds', () => {
    const empty: ArchetypeSchema = { index: 0, name: 'X', groups: [], fields: [] };
    for (const period of [0, -1, 0.5, Number.NaN]) {
      expect(() => {
        validateSchema(world(empty, period));
      }).toThrow(/tick period/);
    }
  });

  it('rejects a misplaced index, an unknown group and a component count outside 1..4', () => {
    expect(() => {
      validateSchema(world({ index: 1, name: 'X', groups: [], fields: [] }));
    }).toThrow(/index/);
    expect(() => {
      validateSchema(world({ index: 0, name: 'X', groups: ['a'], fields: [{ name: 'f', kind: 'u8', group: 3 }] }));
    }).toThrow(/group/);
    expect(() => {
      validateSchema(world({ index: 0, name: 'X', groups: [], fields: [{ name: 'f', kind: 'f64', components: 5 }] }));
    }).toThrow(/components/);
  });
});
