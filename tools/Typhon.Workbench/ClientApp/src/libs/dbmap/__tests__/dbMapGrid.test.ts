import { describe, expect, it } from 'vitest';
import { chunkAreaRect, gridCellIndexAt, gridCols, gridSubRect, gridVoidCount } from '../dbMapGrid';

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

describe('gridVoidCount — surplus (void) slots of a near-square grid', () => {
  it('reports the trailing empty slots that must be drawn as invalid area', () => {
    expect(gridVoidCount(3)).toBe(1); // 3 cells tile 2×2 → 1 void (the reported case)
    expect(gridVoidCount(5)).toBe(1); // 5 → 3×2 = 6 → 1 void
    expect(gridVoidCount(7)).toBe(2); // 7 → 3×3 = 9 → 2 voids
  });

  it('is zero for perfectly-filled grids', () => {
    expect(gridVoidCount(1)).toBe(0);
    expect(gridVoidCount(2)).toBe(0); // 2 → 2×1
    expect(gridVoidCount(4)).toBe(0); // 4 → 2×2
    expect(gridVoidCount(9)).toBe(0); // 9 → 3×3
  });

  it('is zero for an empty grid', () => {
    expect(gridVoidCount(0)).toBe(0);
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

describe('gridCellIndexAt — inverse of gridSubRect', () => {
  const unit = { x: 0, y: 0, w: 1, h: 1 };

  // Regression (L4 content hit-test): the row was scaled/clamped by the COLUMN count, so any non-square grid
  // mis-mapped its lower rows out of range and the click fell through to the parent chunk. Round-trip every
  // cell's centre — a point inside gridSubRect(.., k) must invert back to k.
  for (const [cols, rows] of [[1, 1], [2, 1], [3, 2], [4, 3], [4, 4]] as const) {
    it(`round-trips every cell centre of a ${cols}×${rows} grid`, () => {
      for (let k = 0; k < cols * rows; k++) {
        const r = gridSubRect(unit, cols, rows, k); // unit parent → rect bounds == fractional bounds
        expect(gridCellIndexAt(r.x + r.w / 2, r.y + r.h / 2, cols, rows)).toBe(k);
      }
    });
  }

  it('keeps a click in the lower half of a 2×1 grid on a real cell (the reported chunk-instead-of-field bug)', () => {
    // 2 content cells tile 2×1; the old math read fy>0.5 as row 1 → index 2/3 → out of range → chunk selected.
    expect(gridCellIndexAt(0.25, 0.9, 2, 1)).toBe(0);
    expect(gridCellIndexAt(0.75, 0.9, 2, 1)).toBe(1);
  });

  it('clamps out-of-bounds fractions to the edge cells', () => {
    expect(gridCellIndexAt(-0.5, -0.5, 3, 2)).toBe(0); // top-left
    expect(gridCellIndexAt(1.5, 1.5, 3, 2)).toBe(5); // bottom-right = (rows-1)*cols + (cols-1) = 1*3 + 2
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
