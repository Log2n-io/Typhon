import { describe, expect, it } from 'vitest';
import type { SystemArchetypeTouchSummary } from '@/api/generated/model/systemArchetypeTouchSummary';
import { findTickRangeSlice } from '../tickRangeFilter';

function row(tick: number, sys = 0, arch = 0): SystemArchetypeTouchSummary {
  return {
    tickNumber: tick,
    systemIndex: sys,
    archetypeId: arch,
    entityCount: 0,
    chunkCount: 0,
  } as unknown as SystemArchetypeTouchSummary;
}

describe('findTickRangeSlice — empty / degenerate', () => {
  it('returns {0,0} for null/undefined input', () => {
    expect(findTickRangeSlice(null, { from: 1, to: 10 })).toEqual({ startIdx: 0, endIdx: 0 });
    expect(findTickRangeSlice(undefined, { from: 1, to: 10 })).toEqual({ startIdx: 0, endIdx: 0 });
  });

  it('returns {0,0} for empty array', () => {
    expect(findTickRangeSlice([], { from: 1, to: 10 })).toEqual({ startIdx: 0, endIdx: 0 });
  });

  it('returns full extent for null range (opt-out filter)', () => {
    const rows = [row(1), row(2), row(3)];
    expect(findTickRangeSlice(rows, null)).toEqual({ startIdx: 0, endIdx: 3 });
  });

  it('returns {0,0} for inverted range', () => {
    const rows = [row(1), row(2), row(3)];
    expect(findTickRangeSlice(rows, { from: 5, to: 2 })).toEqual({ startIdx: 0, endIdx: 0 });
  });
});

describe('findTickRangeSlice — boundaries', () => {
  it('includes rows whose tick == from', () => {
    const rows = [row(1), row(5), row(10)];
    const r = findTickRangeSlice(rows, { from: 5, to: 10 });
    expect(r).toEqual({ startIdx: 1, endIdx: 3 });
  });

  it('includes rows whose tick == to (inclusive end)', () => {
    const rows = [row(1), row(5), row(10), row(20)];
    const r = findTickRangeSlice(rows, { from: 5, to: 10 });
    expect(r).toEqual({ startIdx: 1, endIdx: 3 });
  });

  it('returns {0,0} when range is entirely before first row', () => {
    const rows = [row(10), row(11), row(12)];
    const r = findTickRangeSlice(rows, { from: 1, to: 5 });
    expect(r).toEqual({ startIdx: 0, endIdx: 0 });
  });

  it('returns {0,0} when range is entirely after last row', () => {
    const rows = [row(10), row(11), row(12)];
    const r = findTickRangeSlice(rows, { from: 100, to: 200 });
    expect(r).toEqual({ startIdx: 0, endIdx: 0 });
  });

  it('handles range that exactly covers single tick', () => {
    const rows = [row(1), row(2), row(2), row(2), row(3)];
    const r = findTickRangeSlice(rows, { from: 2, to: 2 });
    // All three rows with tick==2 must be in the slice.
    expect(r).toEqual({ startIdx: 1, endIdx: 4 });
  });
});

describe('findTickRangeSlice — multiple rows per tick', () => {
  it('returns the contiguous block when many rows share a tick number', () => {
    // Realistic shape: tick 5 has rows for (sys=0, arch=10), (sys=0, arch=11), (sys=1, arch=10).
    const rows = [
      row(4, 0, 10),
      row(5, 0, 10),
      row(5, 0, 11),
      row(5, 1, 10),
      row(6, 0, 10),
    ];
    const r = findTickRangeSlice(rows, { from: 5, to: 5 });
    expect(r).toEqual({ startIdx: 1, endIdx: 4 });
  });
});

describe('findTickRangeSlice — large array (perf path)', () => {
  it('binary search remains correct on 100k rows', () => {
    const rows: SystemArchetypeTouchSummary[] = [];
    for (let t = 0; t < 100_000; t++) rows.push(row(t));
    const r = findTickRangeSlice(rows, { from: 50_000, to: 50_010 });
    expect(r.startIdx).toBe(50_000);
    expect(r.endIdx).toBe(50_011);
  });
});

describe('findTickRangeSlice — defensive against missing tickNumber', () => {
  it('rows without a numeric tickNumber are excluded', () => {
    const goodRows = [row(5), row(6), row(7)];
    const corrupt: SystemArchetypeTouchSummary = { systemIndex: 0 } as unknown as SystemArchetypeTouchSummary;
    const rows = [corrupt, ...goodRows];
    const r = findTickRangeSlice(rows, { from: 5, to: 7 });
    // Corrupt row (tick = -Infinity) sorts first; range hunt skips past it.
    expect(r.startIdx).toBe(1);
    expect(r.endIdx).toBe(4);
  });
});
