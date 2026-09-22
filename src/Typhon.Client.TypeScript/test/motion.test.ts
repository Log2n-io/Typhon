import { describe, expect, it } from 'vitest';
import {
  epochAt,
  evaluateLive,
  evaluateSlot,
  headingOf,
  MAX_MOTION_STRIDE,
  motionRecordBytes,
  motionSegmentsOffset,
  segmentHistoryFor,
  WorldStore,
  type WorldSchema,
} from '../src/index.js';

const schema: WorldSchema = {
  tickPeriodUs: 100_000,
  archetypes: [
    { index: 0, name: 'Mover', position: { kind: 'motion', dims: 2 }, groups: [], fields: [] },
    { index: 1, name: 'Ghost', groups: [], fields: [] },
    { index: 2, name: 'Flyer', position: { kind: 'motion', model: 'linear', dims: 3 }, groups: [], fields: [] },
    { index: 3, name: 'Buoy', position: { kind: 'motion', model: 'none', dims: 2 }, groups: [], fields: [] },
    { index: 4, name: 'Tower', position: { kind: 'static', dims: 3 }, groups: [], fields: [] },
  ],
};

function makeMover(t0: number): { world: WorldStore; slot: number } {
  const world = new WorldStore(schema);
  world.beginFrame(t0);
  const slot = world.enter(0, 1);
  // 10 m/tick along +X from (100, 50).
  world.archetypes[0]!.resetMotion(slot, [100, 50], [10, 0], t0, 0);
  return { world, slot };
}

function at(world: WorldStore, archetype: number, slot: number, tick: number, frac: number): number[] {
  const out = new Float64Array(MAX_MOTION_STRIDE);
  const store = world.archetypes[archetype]!;
  evaluateSlot(store, slot, tick, frac, out, 0);
  return Array.from(out.subarray(0, store.motionStride));
}

function xAt(world: WorldStore, slot: number, tick: number, frac: number): number {
  return at(world, 0, slot, tick, frac)[0]!;
}

describe('motion evaluation', () => {
  it('sizes the ring from the tick period: the 300 ms render delay plus two segments, 4 to 255', () => {
    expect(segmentHistoryFor(1_000_000)).toBe(4);
    expect(segmentHistoryFor(100_000)).toBe(5);
    expect(segmentHistoryFor(50_000)).toBe(8);
    expect(segmentHistoryFor(16_667)).toBe(20);
    expect(segmentHistoryFor(1_000)).toBe(255);
  });

  it('lays the record out per dimension count and ring size, 8-aligned', () => {
    // H t0s (4 B), head, count, H epochs, padding, then H × p[dims] v[dims] doubles.
    expect([motionSegmentsOffset(4), motionRecordBytes(2, 4), motionRecordBytes(3, 4)]).toEqual([24, 152, 216]);
    expect([motionSegmentsOffset(5), motionRecordBytes(2, 5), motionRecordBytes(3, 5)]).toEqual([32, 192, 272]);
    expect(motionSegmentsOffset(255)).toBe(1280);
    const world = new WorldStore(schema);
    expect(world.archetypes.map((a) => [a.dims, a.motionStride, a.segmentHistory, a.motionRecordBytes])).toEqual([
      [2, 4, 5, 192],
      [0, 0, 5, 0],
      [3, 6, 5, 272],
      [2, 4, 5, 192],
      [3, 6, 5, 272],
    ]);
  });

  it('extrapolates p0 + v·(τ − t0) with an integer tick and a fraction', () => {
    const { world, slot } = makeMover(1000);
    expect(xAt(world, slot, 1002, 0.5)).toBeCloseTo(125, 9);
  });

  it('keeps using an older segment until render time reaches the next t0, then switches without blending', () => {
    const { world, slot } = makeMover(1000);
    world.archetypes[0]!.pushSegment(slot, [-500, -500], [0, 0], 1004, 1);
    expect(xAt(world, slot, 1003, 0.99)).toBeCloseTo(139.9, 9);
    expect(epochAt(world.archetypes[0]!, slot, 1003)).toBe(0);
    expect(xAt(world, slot, 1004, 0)).toBe(-500);
    expect(epochAt(world.archetypes[0]!, slot, 1004)).toBe(1);
  });

  it('finds the right segment when several arrive before render time reaches them', () => {
    // Render time trails the newest frame by up to three ticks: segments at 1001, 1002 and 1003 all land first.
    const { world, slot } = makeMover(1000);
    const store = world.archetypes[0]!;
    store.pushSegment(slot, [110, 50], [-1, 0], 1001, 0);
    store.pushSegment(slot, [109, 50], [0, 2], 1002, 0);
    store.pushSegment(slot, [109, 52], [0, 0], 1003, 0);
    expect(xAt(world, slot, 1000, 0.5)).toBe(105);
    expect(xAt(world, slot, 1001, 0.5)).toBe(109.5);
    expect(xAt(world, slot, 1003, 0.5)).toBe(109);
  });

  it('keeps the ring’s newest segments and extrapolates the oldest backwards before all of them', () => {
    const { world, slot } = makeMover(1000);
    const store = world.archetypes[0]!;
    const n = store.segmentHistory + 1;
    for (let k = 1; k <= n; k++) {
      store.pushSegment(slot, [100 + k, 0], [1, 0], 1000 + 10 * k, 0);
    }

    // Segments 2..n remain (t0 1020..); 1015 is before all of them: the oldest, at t0 1020, backwards.
    expect(xAt(world, slot, 1015, 0)).toBe(102 - 5);
    expect(xAt(world, slot, 1000 + 10 * n + 2, 0)).toBe(100 + n + 2);
  });

  it('interpolates `none` samples across the ring wrap, from entry H − 1 to entry 0', () => {
    const world = new WorldStore(schema);
    world.beginFrame(100);
    const slot = world.enter(3, 1);
    const store = world.archetypes[3]!;
    const h = store.segmentHistory;
    store.resetMotion(slot, [0, 0], null, 100, 0);
    for (let k = 1; k <= h; k++) {
      store.pushSegment(slot, [10 * k, 0], null, 100 + 2 * k, 0);
    }

    // The newest sample wrapped into entry 0; the one before it is in entry H − 1.
    expect(store.headEntry(slot)).toBe(0);
    expect(at(world, 3, slot, 100 + 2 * h - 1, 0)).toEqual([10 * h - 5, 0, 5, 0]);
    expect(at(world, 3, slot, 100 + 2 * h - 2, 0.5)).toEqual([10 * h - 7.5, 0, 5, 0]);
  });

  it('degrades to the oldest segment held when render time lags beyond the ring, without throwing', () => {
    const world = new WorldStore(schema);
    world.beginFrame(1000);
    const mover = world.enter(0, 1);
    const buoy = world.enter(3, 2);
    const moving = world.archetypes[0]!;
    const sampled = world.archetypes[3]!;
    moving.resetMotion(mover, [0, 0], [1, 0], 1000, 0);
    sampled.resetMotion(buoy, [0, 0], null, 1000, 0);
    const pushes = 3 * moving.segmentHistory;
    for (let k = 1; k <= pushes; k++) {
      moving.pushSegment(mover, [k, 0], [1, 0], 1000 + k, 0);
      sampled.pushSegment(buoy, [k, 0], null, 1000 + k, 0);
    }

    // The oldest held starts at tick 1000 + pushes − H + 1; render time 100 ticks before the first segment ever sent.
    const oldest = pushes - moving.segmentHistory + 1;
    expect(at(world, 0, mover, 900, 0)).toEqual([oldest - (1000 + oldest - 900), 0, 1, 0]);
    expect(at(world, 3, buoy, 900, 0)).toEqual([oldest, 0, 0, 0]);
    const all = new Float64Array(sampled.liveCount * sampled.motionStride);
    expect(() => {
      evaluateLive(sampled, 900, 0, all);
      evaluateLive(moving, 900, 0, new Float64Array(moving.liveCount * moving.motionStride));
    }).not.toThrow();
    expect(Array.from(all.subarray(0, 2))).toEqual([oldest, 0]);
  });

  it('stays exact at ticks a float32 could not represent', () => {
    const t0 = 4_000_000_001;
    expect(Math.fround(t0)).not.toBe(t0);
    const { world, slot } = makeMover(t0);
    expect(xAt(world, slot, t0 + 3, 0.125)).toBe(100 + 10 * 3.125);
  });

  it('evaluates in three dimensions: p[3] then v[3]', () => {
    const world = new WorldStore(schema);
    world.beginFrame(10);
    const slot = world.enter(2, 1);
    const store = world.archetypes[2]!;
    store.resetMotion(slot, [1, 2, 3], [0.5, -1, 0.25], 10, 0);
    expect(at(world, 2, slot, 12, 0)).toEqual([2, 0, 3.5, 0.5, -1, 0.25]);
    store.pushSegment(slot, [-10, 20, 30], [0, 0, 1], 12, 1);
    expect(at(world, 2, slot, 11, 0.5)).toEqual([1.75, 0.5, 3.375, 0.5, -1, 0.25]);
    expect(at(world, 2, slot, 13, 0.5)).toEqual([-10, 20, 31.5, 0, 0, 1]);
  });

  it('holds a static position with zero velocity', () => {
    const world = new WorldStore(schema);
    world.beginFrame(1);
    const slot = world.enter(4, 1);
    world.archetypes[4]!.resetMotion(slot, [5, 6, 7], null, 0, 0);
    expect(at(world, 4, slot, 1000, 0.5)).toEqual([5, 6, 7, 0, 0, 0]);
  });

  it('interpolates the none model between samples of one epoch, and holds past the newest or across an epoch', () => {
    const world = new WorldStore(schema);
    world.beginFrame(100);
    const slot = world.enter(3, 1);
    const store = world.archetypes[3]!;
    store.resetMotion(slot, [0, 0], null, 100, 0);
    expect(at(world, 3, slot, 101, 0)).toEqual([0, 0, 0, 0]);

    store.pushSegment(slot, [10, -20], null, 104, 0);
    expect(at(world, 3, slot, 102, 0)).toEqual([5, -10, 2.5, -5]);
    expect(at(world, 3, slot, 104, 0.5)).toEqual([10, -20, 0, 0]);

    store.pushSegment(slot, [500, 500], null, 106, 1);
    expect(at(world, 3, slot, 105, 0)).toEqual([10, -20, 0, 0]);
    expect(at(world, 3, slot, 106, 0)).toEqual([500, 500, 0, 0]);
  });

  it('evaluates every live entity in live order, identically to per-slot evaluation, for every model', () => {
    const world = new WorldStore(schema);
    world.beginFrame(10);
    for (const archetype of [0, 2, 3]) {
      const store = world.archetypes[archetype]!;
      const dims = store.dims;
      for (let id = 1; id <= 6; id++) {
        const netId = archetype * 100 + id;
        const slot = world.enter(archetype, netId);
        store.resetMotion(
          slot,
          Array.from({ length: dims }, (_, a) => id + a),
          store.linear ? Array(dims).fill(0.5) : null,
          10,
          0,
        );
        if (id % 2 === 0) {
          store.pushSegment(
            slot,
            Array(dims).fill(-id),
            store.linear ? Array(dims).fill(-1) : null,
            12,
            id % 4 === 0 ? 1 : 0,
          );
        }
      }
    }

    world.beginFrame(11);
    world.leave(2);
    world.leave(302);
    for (const archetype of [0, 2, 3]) {
      const store = world.archetypes[archetype]!;
      const stride = store.motionStride;
      const all = new Float64Array(store.liveCount * stride);
      const one = new Float64Array(MAX_MOTION_STRIDE);
      for (const tick of [11, 12, 13]) {
        evaluateLive(store, tick, 0.25, all);
        for (let i = 0; i < store.liveCount; i++) {
          evaluateSlot(store, store.live[i]!, tick, 0.25, one, 0);
          expect(Array.from(all.subarray(i * stride, (i + 1) * stride))).toEqual(Array.from(one.subarray(0, stride)));
        }
      }
    }
  });

  it('refuses to evaluate an archetype without a position', () => {
    const world = new WorldStore(schema);
    world.beginFrame(1);
    world.enter(1, 1);
    expect(() => {
      evaluateLive(world.archetypes[1]!, 1, 0, new Float64Array(4));
    }).toThrow(/position/);
  });

  it('derives a heading from two velocity components and keeps the fallback when stationary', () => {
    expect(headingOf(0, 1, 7)).toBe(0);
    expect(headingOf(1, 0, 7)).toBeCloseTo(Math.PI / 2, 12);
    expect(headingOf(0, 0, 7)).toBe(7);
  });
});
