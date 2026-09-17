import { describe, expect, it } from 'vitest';
import { AggregateGrid } from '../src/index.js';

const grid = () =>
  new AggregateGrid({ index: 0, originX: -8192, originZ: -8192, cellM: 256, dimsX: 64, dimsZ: 64, archetypes: [1, 2] });

const counts = (...values: number[]) => Uint32Array.from(values);

describe('AggregateGrid', () => {
  it('stores counts per cell per archetype and lists each changed cell once per frame', () => {
    const g = grid();
    g.beginFrame();
    g.setCell(5, counts(3, 4), 0);
    g.setCell(5, counts(6, 7), 0);
    g.setCell(9, counts(0, 1, 2), 1);
    expect(g.count(5, 0)).toBe(6);
    expect(g.count(5, 1)).toBe(7);
    expect(g.count(9, 1)).toBe(2);
    expect(Array.from(g.changed.subarray(0, g.changedCount))).toEqual([5, 9]);

    g.beginFrame();
    expect(g.changedCount).toBe(0);
    g.setCell(5, counts(1, 1), 0);
    expect(g.changedCount).toBe(1);
  });

  it('moves its version on every change, so a consumer that skipped frames still notices', () => {
    const g = grid();
    const v0 = g.version;
    g.beginFrame();
    g.setCell(1, counts(1, 1), 0);
    g.beginFrame();
    g.setCell(2, counts(1, 1), 0);
    expect(g.changedCount).toBe(1);
    expect(g.version).toBe(v0 + 2);
  });

  it('maps world points to cells and archetypes to count slots', () => {
    const g = grid();
    expect(g.cellAt(-8192, -8192)).toBe(0);
    expect(g.cellAt(-8192 + 256, -8192 + 256)).toBe(65);
    expect(g.cellAt(8192, 0)).toBe(-1);
    expect(g.cellAt(Number.NaN, 0)).toBe(-1);
    expect(g.slotOfArchetype(2)).toBe(1);
    expect(g.slotOfArchetype(0)).toBe(-1);
  });

  it('resets, clears the reset flag on the next frame, and accepts cells in the reset frame', () => {
    const g = grid();
    g.beginFrame();
    g.setCell(1, counts(10, 0), 0);
    g.setCell(2, counts(30, 5), 0);
    expect(g.maxCount(0)).toBe(30);

    g.beginFrame();
    g.reset();
    g.setCell(3, counts(2, 2), 0);
    expect(g.wasReset).toBe(true);
    expect(g.maxCount(0)).toBe(2);
    g.beginFrame();
    expect(g.wasReset).toBe(false);
  });

  it('rejects bad input instead of storing garbage', () => {
    const g = grid();
    g.beginFrame();
    expect(() => {
      g.setCell(64 * 64, counts(1, 1), 0);
    }).toThrow();
    expect(() => {
      g.setCell(0, counts(1), 0);
    }).toThrow();
    expect(
      () =>
        new AggregateGrid({ index: 1, originX: 0, originZ: 0, cellM: Number.NaN, dimsX: 4, dimsZ: 4, archetypes: [0] }),
    ).toThrow();
  });
});
