import { describe, expect, it } from 'vitest';
import { chunkAreaRect, gridCols, gridSubRect } from '../dbMapGrid';

describe('gridCols', () => {
  it('lays cells into a near-square grid', () => {
    expect(gridCols(1)).toBe(1);
    expect(gridCols(4)).toBe(2);
    expect(gridCols(5)).toBe(3);
    expect(gridCols(16)).toBe(4);
    expect(gridCols(17)).toBe(5);
  });

  it('never reports zero columns for an empty grid', () => {
    expect(gridCols(0)).toBe(1);
  });
});

describe('gridSubRect', () => {
  const unit = { x: 0, y: 0, w: 1, h: 1 };

  it('places cell 0 at the top-left', () => {
    expect(gridSubRect(unit, 2, 2, 0)).toEqual({ x: 0, y: 0, w: 0.5, h: 0.5 });
  });

  it('places the last cell of a 2x2 grid at the bottom-right', () => {
    expect(gridSubRect(unit, 2, 2, 3)).toEqual({ x: 0.5, y: 0.5, w: 0.5, h: 0.5 });
  });

  it('honours the parent rect offset and size', () => {
    const parent = { x: 10, y: 20, w: 4, h: 8 };
    expect(gridSubRect(parent, 2, 2, 1)).toEqual({ x: 12, y: 20, w: 2, h: 4 });
  });
});

describe('chunkAreaRect — reserved overhead band (A6 memory-faithful layout)', () => {
  const unit = { x: 0, y: 0, w: 1, h: 1 };
  const PAGE = 8192;

  it('returns the whole cell when there is no overhead', () => {
    expect(chunkAreaRect(unit, 0, PAGE)).toEqual(unit);
  });

  it('reserves the overhead band as a top fraction of the cell', () => {
    const r = chunkAreaRect(unit, 996, PAGE); // a cluster non-root page's header + alignment
    expect(r.y).toBeCloseTo(996 / PAGE, 6);
    expect(r.h).toBeCloseTo(1 - 996 / PAGE, 6);
    expect(r.x).toBe(0);
    expect(r.w).toBe(1);
  });

  it('scales the reserve to the parent rect height', () => {
    const parent = { x: 5, y: 10, w: 2, h: 4 };
    const r = chunkAreaRect(parent, 2048, PAGE); // 2048/8192 = 0.25 of the page
    expect(r.y).toBeCloseTo(10 + 4 * 0.25, 6);
    expect(r.h).toBeCloseTo(4 * 0.75, 6);
  });
});
