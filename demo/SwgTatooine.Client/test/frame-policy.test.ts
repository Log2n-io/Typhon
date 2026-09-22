import { describe, expect, it } from 'vitest';
import { meanAndP95, regionDue } from '../src/app/frame-policy';

describe('meanAndP95', () => {
  const run = (values: number[]): number[] => {
    const out = new Float64Array(2);
    meanAndP95(Float64Array.from(values), values.length, new Float64Array(values.length), out);
    return Array.from(out);
  };

  it('uses the nearest rank: with 20 samples the 19th, not the maximum', () => {
    const values = Array.from({ length: 20 }, (_, i) => 20 - i);
    expect(run(values)).toEqual([10.5, 19]);
  });

  it('is the only sample for one, and zero for none', () => {
    expect(run([3])).toEqual([3, 3]);
    const out = new Float64Array([7, 7]);
    meanAndP95(new Float64Array(4), 0, new Float64Array(4), out);
    expect(Array.from(out)).toEqual([0, 0]);
  });
});

describe('regionDue', () => {
  const due = (x: number, radius: number, now: number, sent = { x: 0, radius: 1000, ms: 0 }): boolean =>
    regionDue(x, 0, radius, sent.x, 0, sent.radius, now, sent.ms, 200);

  it('sends the first region at once', () => {
    expect(regionDue(5, 5, 1000, Number.NaN, Number.NaN, Number.NaN, 0, Number.NEGATIVE_INFINITY, 200)).toBe(true);
  });

  it('waits for a move of 5 % of the radius, or a new radius', () => {
    expect(due(49, 1000, 1000)).toBe(false);
    expect(due(51, 1000, 1000)).toBe(true);
    expect(due(0, 1200, 1000)).toBe(true);
  });

  it('sends at most once per interval', () => {
    expect(due(500, 1000, 199)).toBe(false);
    expect(due(500, 1000, 200)).toBe(true);
  });
});
