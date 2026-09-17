import { describe, expect, it } from 'vitest';
import {
  epochAt,
  evaluateLive,
  evaluateSlot,
  headingOf,
  MOTION_STRIDE,
  WorldStore,
  type WorldSchema,
} from '../src/index.js';

const schema: WorldSchema = {
  archetypes: [
    { index: 0, name: 'Mover', position: 'motion', groups: ['enter', 'motion'], fields: [] },
    { index: 1, name: 'Ghost', position: 'none', groups: ['enter'], fields: [] },
  ],
};

function makeMover(t0: number): { world: WorldStore; slot: number } {
  const world = new WorldStore(schema);
  world.beginFrame(t0);
  const slot = world.enter(0, 1);
  // 10 m/tick along +X from (100, 50).
  world.archetypes[0]!.resetMotion(slot, 100, 50, 10, 0, t0, 0);
  return { world, slot };
}

function xAt(world: WorldStore, slot: number, tick: number, frac: number): number {
  const out = new Float64Array(MOTION_STRIDE);
  evaluateSlot(world.archetypes[0]!, slot, tick, frac, out, 0);
  return out[0]!;
}

describe('motion evaluation', () => {
  it('extrapolates p0 + v·(τ − t0) with an integer tick and a fraction', () => {
    const { world, slot } = makeMover(1000);
    expect(xAt(world, slot, 1002, 0.5)).toBeCloseTo(125, 9);
  });

  it('keeps using an older segment until render time reaches the next t0, then switches without blending', () => {
    const { world, slot } = makeMover(1000);
    world.archetypes[0]!.pushSegment(slot, -500, -500, 0, 0, 1004, 1);
    expect(xAt(world, slot, 1003, 0.99)).toBeCloseTo(139.9, 9);
    expect(epochAt(world.archetypes[0]!, slot, 1003)).toBe(0);
    expect(xAt(world, slot, 1004, 0)).toBe(-500);
    expect(epochAt(world.archetypes[0]!, slot, 1004)).toBe(1);
  });

  it('finds the right segment when several arrive before render time reaches them', () => {
    // Render time trails the newest frame by up to three ticks: segments at 1001, 1002 and 1003 all land first.
    const { world, slot } = makeMover(1000);
    const store = world.archetypes[0]!;
    store.pushSegment(slot, 110, 50, -1, 0, 1001, 0);
    store.pushSegment(slot, 109, 50, 0, 2, 1002, 0);
    store.pushSegment(slot, 109, 52, 0, 0, 1003, 0);
    expect(xAt(world, slot, 1000, 0.5)).toBe(105);
    expect(xAt(world, slot, 1001, 0.5)).toBe(109.5);
    expect(xAt(world, slot, 1003, 0.5)).toBe(109);
  });

  it('keeps the four newest segments and extrapolates the oldest backwards before all of them', () => {
    const { world, slot } = makeMover(1000);
    const store = world.archetypes[0]!;
    for (let k = 1; k <= 5; k++) {
      store.pushSegment(slot, 100 + k, 0, 1, 0, 1000 + 10 * k, 0);
    }

    // Segments 2..5 remain (t0 1020..1050); 1015 is before all of them: the oldest, at t0 1020, backwards.
    expect(xAt(world, slot, 1015, 0)).toBe(102 - 5);
    expect(xAt(world, slot, 1052, 0)).toBe(105 + 2);
  });

  it('stays exact at ticks a float32 could not represent', () => {
    const t0 = 4_000_000_001;
    expect(Math.fround(t0)).not.toBe(t0);
    const { world, slot } = makeMover(t0);
    expect(xAt(world, slot, t0 + 3, 0.125)).toBe(100 + 10 * 3.125);
  });

  it('evaluates every live entity in live order, identically to per-slot evaluation', () => {
    const world = new WorldStore(schema);
    const store = world.archetypes[0]!;
    world.beginFrame(10);
    for (let id = 1; id <= 6; id++) {
      const slot = world.enter(0, id);
      store.resetMotion(slot, id, 0, 1, 0.5, 10, 0);
      if (id % 2 === 0) {
        store.pushSegment(slot, -id, 3, -1, 0, 12, 1);
      }
    }

    world.beginFrame(11);
    world.leave(2);
    const all = new Float64Array(store.liveCount * MOTION_STRIDE);
    const one = new Float64Array(MOTION_STRIDE);
    for (const tick of [11, 12, 13]) {
      evaluateLive(store, tick, 0.25, all);
      for (let i = 0; i < store.liveCount; i++) {
        evaluateSlot(store, store.live[i]!, tick, 0.25, one, 0);
        expect(Array.from(all.subarray(i * MOTION_STRIDE, (i + 1) * MOTION_STRIDE))).toEqual(Array.from(one));
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

  it('derives a heading from velocity and keeps the fallback when stationary', () => {
    expect(headingOf(0, 1, 7)).toBe(0);
    expect(headingOf(1, 0, 7)).toBeCloseTo(Math.PI / 2, 12);
    expect(headingOf(0, 0, 7)).toBe(7);
  });
});
