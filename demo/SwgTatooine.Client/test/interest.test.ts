import { describe, expect, it } from 'vitest';
import { SpatialBins } from '../src/data/mock/bins';
import { InterestManager, NetIdAllocator, type InterestOptions } from '../src/data/mock/interest';
import type { EntitySet } from '../src/data/mock/world';

function line(count: number, spacing: number): EntitySet & { x: Float64Array; z: Float64Array } {
  return {
    archetype: 0,
    count,
    x: Float64Array.from({ length: count }, (_, i) => i * spacing),
    z: new Float64Array(count),
  };
}

function binsFor(sets: EntitySet[]): SpatialBins[] {
  return sets.map((s) => {
    const b = new SpatialBins(64);
    b.build(s);
    return b;
  });
}

const options = (overrides: Partial<InterestOptions>): InterestOptions => ({
  enterBudget: 500,
  nearBudget: 10_000,
  maxRadius: 4096,
  netIdQuarantineTicks: 50,
  ...overrides,
});

describe('mock interest', () => {
  it('enters within the radius and leaves only past the hysteresis band', () => {
    const set = line(1, 1);
    const interest = new InterestManager([set]);
    interest.setRegion(0, 0, 1000);
    interest.update(binsFor([set]), 1);
    expect(interest.archetypes[0].watchedCount).toBe(1);

    // 1020 m: outside the enter radius, inside the leave radius (1000 + max(32, 50) = 1050).
    set.x[0] = 1020;
    interest.update(binsFor([set]), 2);
    expect(interest.archetypes[0].watchedCount).toBe(1);

    set.x[0] = 1060;
    interest.update(binsFor([set]), 3);
    expect(interest.archetypes[0].watchedCount).toBe(0);
    expect(interest.archetypes[0].leaveCount).toBe(1);
  });

  it('keeps the netId of an entity that left this tick readable for this tick only', () => {
    const set = line(1, 1);
    const interest = new InterestManager([set]);
    interest.setRegion(0, 0, 100);
    interest.update(binsFor([set]), 1);
    const netId = interest.archetypes[0].netIds[0];
    set.x[0] = 5000;
    interest.update(binsFor([set]), 2);
    expect(interest.archetypes[0].netIdOf(0)).toBe(netId);
    interest.update(binsFor([set]), 3);
    expect(interest.archetypes[0].netIdOf(0)).toBe(0);
  });

  it('spends the enter budget on the nearest entities and reports the view complete once drained', () => {
    const set = line(1200, 1);
    const interest = new InterestManager([set], options({}));
    interest.setRegion(0, 0, 4000);
    const bins = binsFor([set]);
    interest.update(bins, 1);
    const a = interest.archetypes[0];
    expect(a.enterCount).toBe(500);
    expect(interest.viewComplete).toBe(false);
    for (let k = 0; k < a.enterCount; k++) {
      expect(a.enters[k]).toBeLessThan(500);
    }

    interest.update(bins, 2);
    interest.update(bins, 3);
    expect(a.watchedCount).toBe(1200);
    expect(interest.viewComplete).toBe(true);
  });

  it('shrinks the radius to the near budget from per-cell counts, then grows back when the crowd thins', () => {
    const set = line(3000, 1);
    const interest = new InterestManager([set], options({ enterBudget: 5000, nearBudget: 1000 }));
    interest.setRegion(0, 0, 3000);
    const bins = binsFor([set]);
    interest.update(bins, 1);
    expect(interest.effectiveRadius).toBeLessThan(1300);
    expect(interest.archetypes[0].watchedCount).toBeLessThanOrEqual(1100);

    // The crowd leaves: most entities move far away, so the budget no longer binds.
    for (let i = 200; i < set.count; i++) {
      set.x[i] = -8000;
    }

    const thinned = binsFor([set]);
    // Growth is one 5 % step per 20 quiet ticks: about 24 steps from the limited radius back to 3 000 m.
    for (let t = 2; t < 700; t++) {
      interest.update(thinned, t);
    }

    expect(interest.effectiveRadius).toBe(3000);
  });

  it('does not let a radius change undo a budget-limited radius', () => {
    const set = line(3000, 1);
    const interest = new InterestManager([set], options({ enterBudget: 5000, nearBudget: 1000 }));
    interest.setRegion(0, 0, 3000);
    const bins = binsFor([set]);
    interest.update(bins, 1);
    const limited = interest.effectiveRadius;
    interest.setRegion(0, 0, 3500);
    expect(interest.effectiveRadius).toBe(limited);
    interest.setRegion(0, 0, 500);
    expect(interest.effectiveRadius).toBe(500);
  });

  it('clamps the requested radius to the profile maximum', () => {
    const interest = new InterestManager([line(1, 1)], options({ maxRadius: 2000 }));
    interest.setRegion(0, 0, 9000);
    expect(interest.requestedRadius).toBe(2000);
  });
});

describe('NetIdAllocator', () => {
  it('hands out dense ids and reuses a released one only after the quarantine', () => {
    const ids = new NetIdAllocator(50);
    expect(ids.allocate(1)).toBe(1);
    expect(ids.allocate(1)).toBe(2);
    ids.release(1, 10);
    expect(ids.allocate(20)).toBe(3);
    expect(ids.allocate(60)).toBe(1);
  });

  it('keeps release order through quarantine growth', () => {
    const ids = new NetIdAllocator(10);
    for (let i = 1; i <= 3000; i++) {
      expect(ids.allocate(0)).toBe(i);
    }

    for (let i = 1; i <= 3000; i++) {
      ids.release(i, i);
    }

    const reused = new Set<number>();
    for (let i = 0; i < 3000; i++) {
      reused.add(ids.allocate(5000));
    }

    expect(reused.size).toBe(3000);
    expect(Math.max(...reused)).toBe(3000);
  });
});
