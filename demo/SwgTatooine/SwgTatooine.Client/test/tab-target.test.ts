import { describe, expect, it } from 'vitest';
import { nextTabTarget } from '../src/app/tab-target';

describe('Tab targeting', () => {
  const near = [
    { netId: 30, d2: 900, enemy: false },
    { netId: 10, d2: 100, enemy: false },
    { netId: 20, d2: 400, enemy: false },
  ];

  it('selects the nearest when nothing of them is selected', () => {
    expect(nextTabTarget(near, 0)).toBe(10);
    expect(nextTabTarget(near, 999)).toBe(10);
  });

  it('cycles outward by distance and wraps to the nearest', () => {
    expect(nextTabTarget(near, 10)).toBe(20);
    expect(nextTabTarget(near, 20)).toBe(30);
    expect(nextTabTarget(near, 30)).toBe(10);
  });

  it('cycles the enemies first, nearest out, then the other characters, then the nearest enemy again', () => {
    const mixed = [
      { netId: 1, d2: 4, enemy: false },
      { netId: 2, d2: 900, enemy: true },
      { netId: 3, d2: 100, enemy: true },
      { netId: 4, d2: 50, enemy: false },
    ];
    const order = [nextTabTarget(mixed, 0)];
    for (let i = 0; i < 4; i++) {
      order.push(nextTabTarget(mixed, order[order.length - 1]));
    }

    expect(order).toEqual([3, 2, 1, 4, 3]);
  });

  it('visits two characters at one distance in turn instead of skipping one', () => {
    const tied = [
      { netId: 7, d2: 25, enemy: false },
      { netId: 5, d2: 25, enemy: false },
    ];
    expect(nextTabTarget(tied, 0)).toBe(5);
    expect(nextTabTarget(tied, 5)).toBe(7);
    expect(nextTabTarget(tied, 7)).toBe(5);
  });

  it('selects nobody when nobody is near', () => {
    expect(nextTabTarget([], 10)).toBe(0);
  });
});
