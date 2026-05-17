import { describe, expect, it } from 'vitest';
import { packIntervals } from '../intervalPacking';

interface Span { id: string; start: number; end: number }

function pack(spans: Span[]) {
  return packIntervals(spans, (s) => s.start, (s) => s.end);
}

/** lane index for a given id. */
function laneOf(result: ReturnType<typeof pack>, id: string): number {
  return result.packed.find((p) => p.item.id === id)!.lane;
}

describe('packIntervals', () => {
  it('empty input → 0 lanes', () => {
    const r = pack([]);
    expect(r.laneCount).toBe(0);
    expect(r.packed).toHaveLength(0);
  });

  it('non-overlapping intervals all share lane 0', () => {
    const r = pack([
      { id: 'a', start: 0, end: 10 },
      { id: 'b', start: 20, end: 30 },
      { id: 'c', start: 40, end: 50 },
    ]);
    expect(r.laneCount).toBe(1);
    expect(r.packed.every((p) => p.lane === 0)).toBe(true);
  });

  it('N fully-overlapping intervals need N lanes', () => {
    const r = pack([
      { id: 'a', start: 0, end: 100 },
      { id: 'b', start: 10, end: 100 },
      { id: 'c', start: 20, end: 100 },
    ]);
    expect(r.laneCount).toBe(3);
    expect(new Set(r.packed.map((p) => p.lane)).size).toBe(3);
  });

  it('exact-touch (end === next start) shares a lane — half-open intervals do not overlap', () => {
    const r = pack([
      { id: 'a', start: 0, end: 10 },
      { id: 'b', start: 10, end: 20 },
    ]);
    expect(r.laneCount).toBe(1);
    expect(laneOf(r, 'a')).toBe(0);
    expect(laneOf(r, 'b')).toBe(0);
  });

  it('staircase — partial overlaps pack into the minimum lane count', () => {
    // a[0,15] b[10,25] c[20,35]: peak overlap is 2 (a∩b, then b∩c) — a and c never overlap, so
    // c reuses a's freed lane 0.
    const r = pack([
      { id: 'a', start: 0, end: 15 },
      { id: 'b', start: 10, end: 25 },
      { id: 'c', start: 20, end: 35 },
    ]);
    expect(r.laneCount).toBe(2);
    expect(laneOf(r, 'a')).toBe(0);
    expect(laneOf(r, 'b')).toBe(1);
    expect(laneOf(r, 'c')).toBe(0);
  });

  it('a freed lane is reused by a later non-overlapping interval', () => {
    // a[0,10] on lane 0; b[5,15] forced to lane 1; c[20,30] reuses lane 0 (a ended at 10).
    const r = pack([
      { id: 'a', start: 0, end: 10 },
      { id: 'b', start: 5, end: 15 },
      { id: 'c', start: 20, end: 30 },
    ]);
    expect(r.laneCount).toBe(2);
    expect(laneOf(r, 'c')).toBe(0);
  });

  it('is deterministic for ties on start time', () => {
    const spans: Span[] = [
      { id: 'a', start: 0, end: 5 },
      { id: 'b', start: 0, end: 10 },
    ];
    const r1 = pack(spans);
    const r2 = pack([...spans].reverse());
    // Sorted by (start, end): a before b → a lane 0, b lane 1, regardless of input order.
    expect(laneOf(r1, 'a')).toBe(laneOf(r2, 'a'));
    expect(laneOf(r1, 'b')).toBe(laneOf(r2, 'b'));
  });
});
