import { describe, expect, it } from 'vitest';
import { gridCols, gridSubRect } from '../dbMapGrid';

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
