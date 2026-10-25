import { describe, expect, it } from 'vitest';
import { prepareShape, type PlanBox, type PlacedShape } from '../src/app/structure-collision';
import { rayOnCylinder, rayOnShapes, rayOnSlabs, type ShapeHit } from '../src/app/world-pick';
import type { PickRay } from '../src/render/view-math';

/** A level ray from (ox, oy, oz) along +x. */
const along = (ox: number, oy: number, oz: number): PickRay => ({ ox, oy, oz, dx: 1, dy: 0, dz: 0 });

const box = (
  minX: number,
  maxX: number,
  minZ: number,
  maxZ: number,
  minY: number,
  maxY: number,
  radius = 0,
): PlanBox => ({
  minX,
  maxX,
  minZ,
  maxZ,
  minY,
  maxY,
  planeY: maxY,
  slopeX: 0,
  slopeZ: 0,
  radius,
});

describe('rayOnSlabs', () => {
  it('enters a box at its near face, from inside at 0, and misses one beside or behind the ray', () => {
    expect(rayOnSlabs(along(0, 1, 0), 10, 0, -1, 12, 2, 1)).toBeCloseTo(10, 9);
    expect(rayOnSlabs(along(11, 1, 0), 10, 0, -1, 12, 2, 1)).toBe(0);
    expect(rayOnSlabs(along(0, 1, 5), 10, 0, -1, 12, 2, 1)).toBe(Infinity);
    expect(rayOnSlabs(along(20, 1, 0), 10, 0, -1, 12, 2, 1)).toBe(Infinity);
  });
});

describe('rayOnCylinder', () => {
  it('enters a round collider at its curve, and passes over one it is above', () => {
    expect(rayOnCylinder(along(0, 1, 0), 10, 0, 2, 0, 3)).toBeCloseTo(8, 9);
    expect(rayOnCylinder(along(0, 4, 0), 10, 0, 2, 0, 3)).toBe(Infinity);
    // Through the bounding square's corner but outside the circle: the plaza's corner is open desert.
    expect(rayOnCylinder(along(0, 1, 1.9), 10, 0, 2, 0, 3)).toBeCloseTo(10 - Math.sqrt(4 - 1.9 * 1.9), 9);
    expect(rayOnCylinder(along(0, 1, 2.1), 10, 0, 2, 0, 3)).toBe(Infinity);
  });
});

describe('rayOnShapes', () => {
  // A port, simplified: a wall 20 m out east, and an open plaza between the eye and it.
  const port: PlacedShape = { shape: prepareShape([box(19, 21, -10, 10, 0, 4)]), x: 0, y: 100, z: 0, quarters: 0 };
  const hit = (): ShapeHit => ({ t: 0, index: -1 });

  it('meets the wall across the open plaza, at its altitude, and reports which structure', () => {
    const out = hit();
    expect(rayOnShapes([port], 1, along(0, 101, 0), 1000, out)).toBe(true);
    expect(out.t).toBeCloseTo(19, 9);
    expect(out.index).toBe(0);
  });

  it('meets nothing above the wall, or past the ground the caller says the ray stops at', () => {
    expect(rayOnShapes([port], 1, along(0, 105, 0), 1000, hit())).toBe(false);
    expect(rayOnShapes([port], 1, along(0, 101, 0), 15, hit())).toBe(false);
  });

  it('turns the boxes by the structure quarter, as collision does', () => {
    // A quarter turn takes (x, z) to (z, -x): the wall east of the origin stands south of it.
    const turned: PlacedShape = { ...port, quarters: 1 };
    const south: PickRay = { ox: 0, oy: 101, oz: 0, dx: 0, dy: 0, dz: -1 };
    const out = hit();
    expect(rayOnShapes([turned], 1, south, 1000, out)).toBe(true);
    expect(out.t).toBeCloseTo(19, 9);
  });
});
