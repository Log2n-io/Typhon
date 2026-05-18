import { describe, expect, it } from 'vitest';
import { hilbertD2XY, hilbertOrderFor, hilbertSide, hilbertXY2D } from '../hilbert';

describe('hilbert', () => {
  it('hilbertSide is 2^order', () => {
    expect(hilbertSide(0)).toBe(1);
    expect(hilbertSide(3)).toBe(8);
    expect(hilbertSide(9)).toBe(512);
  });

  it('hilbertOrderFor picks the smallest grid that holds the page count', () => {
    expect(hilbertOrderFor(1)).toBe(0);
    expect(hilbertOrderFor(4)).toBe(1);
    expect(hilbertOrderFor(5)).toBe(2);
    expect(hilbertOrderFor(16)).toBe(2);
    expect(hilbertOrderFor(17)).toBe(3);
    expect(hilbertOrderFor(130_000)).toBe(9); // 4^9 = 262144 ≥ 130000
  });

  it('d2xy and xy2d are exact inverses across the whole curve', () => {
    for (const order of [1, 3, 5]) {
      const side = hilbertSide(order);
      for (let d = 0; d < side * side; d++) {
        const { x, y } = hilbertD2XY(order, d);
        expect(x).toBeGreaterThanOrEqual(0);
        expect(y).toBeGreaterThanOrEqual(0);
        expect(x).toBeLessThan(side);
        expect(y).toBeLessThan(side);
        expect(hilbertXY2D(order, x, y)).toBe(d);
      }
    }
  });

  it('the curve visits every cell exactly once', () => {
    const order = 4;
    const side = hilbertSide(order);
    const seen = new Set<number>();
    for (let d = 0; d < side * side; d++) {
      const { x, y } = hilbertD2XY(order, d);
      seen.add(y * side + x);
    }
    expect(seen.size).toBe(side * side);
  });

  it('consecutive curve positions are 4-neighbours (locality)', () => {
    const order = 4;
    const side = hilbertSide(order);
    for (let d = 1; d < side * side; d++) {
      const a = hilbertD2XY(order, d - 1);
      const b = hilbertD2XY(order, d);
      expect(Math.abs(a.x - b.x) + Math.abs(a.y - b.y)).toBe(1);
    }
  });
});
