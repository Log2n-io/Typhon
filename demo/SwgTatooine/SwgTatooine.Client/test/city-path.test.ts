import { describe, expect, it } from 'vitest';
import { findPath, lineClear, wallGrid } from '../src/app/city-path';
import { prepareShape, type PlacedShape, type PlanBox } from '../src/app/structure-collision';

function box(over: Partial<PlanBox>): PlanBox {
  return {
    minX: -1,
    minZ: -1,
    maxX: 1,
    maxZ: 1,
    minY: 0,
    maxY: 2,
    planeY: 2,
    slopeX: 0,
    slopeZ: 0,
    radius: 0,
    ...over,
  };
}

function placed(boxes: PlanBox[], x = 0, z = 0): PlacedShape {
  return { shape: prepareShape(boxes), x, z, quarters: 0, y: 0 };
}

/** A wall across the walk, from x -20 to 20 at z 10..11, two metres high. */
const WALL = placed([box({ minX: -20, maxX: 20, minZ: 10, maxZ: 11 })]);

describe('wallGrid', () => {
  it('blocks a wall grown by the body, and leaves steps, ramps and overhead beams walkable', () => {
    const step = box({ minX: 30, maxX: 32, minZ: 0, maxZ: 2, maxY: 0.3 });
    const ramp = box({ minX: 40, maxX: 42, minZ: 0, maxZ: 2, maxY: 1.2, slopeZ: 0.4 });
    const beam = box({ minX: 50, maxX: 52, minZ: 0, maxZ: 2, minY: 2.2, maxY: 2.5 });
    const grid = wallGrid([WALL, placed([step, ramp, beam])], 2, -40, -10, 60, 30, 0.3);
    expect(lineClear(grid, 0, 0, 0, 20)).toBe(false);
    expect(lineClear(grid, 31, -5, 31, 5)).toBe(true);
    expect(lineClear(grid, 41, -5, 41, 5)).toBe(true);
    expect(lineClear(grid, 51, -5, 51, 5)).toBe(true);
  });
});

describe('findPath', () => {
  it('goes round a wall, never through it, and ends on the goal', () => {
    const grid = wallGrid([WALL], 1, -40, -10, 40, 30, 0.3);
    const path = findPath(grid, 0, 0, 0, 20);
    expect(path).not.toBeNull();
    expect(path![path!.length - 1]).toEqual([0, 20]);
    // Every leg in sight of the next: the walk the body takes is the path drawn.
    let [ax, az] = [0, 0];
    for (const [bx, bz] of path!) {
      expect(lineClear(grid, ax, az, bx, bz)).toBe(true);
      [ax, az] = [bx, bz];
    }

    // And round the end of the wall: a waypoint past its east or west end.
    expect(path!.some(([x]) => Math.abs(x) > 20)).toBe(true);
  });

  it('walks straight where nothing is in the way: one leg', () => {
    const grid = wallGrid([WALL], 1, -40, -10, 40, 30, 0.3);
    expect(findPath(grid, -30, 0, 30, 5)).toEqual([[30, 5]]);
  });

  it('is null when the goal is walled in', () => {
    const pen = placed([
      box({ minX: -6, maxX: -5, minZ: -6, maxZ: 6 }),
      box({ minX: 5, maxX: 6, minZ: -6, maxZ: 6 }),
      box({ minX: -6, maxX: 6, minZ: -6, maxZ: -5 }),
      box({ minX: -6, maxX: 6, minZ: 5, maxZ: 6 }),
    ]);
    const grid = wallGrid([pen], 1, -20, -20, 20, 20, 0.3);
    expect(findPath(grid, -15, -15, 0, 0)).toBeNull();
  });
});
