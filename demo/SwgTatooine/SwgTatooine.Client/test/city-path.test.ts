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

describe('round colliders and struts', () => {
  it('walls off a round pad as its disc, not its square, and a tall leaning strut as a wall', () => {
    const pad = box({ minX: -12, maxX: 12, minZ: -12, maxZ: 12, minY: 0.3, maxY: 1.26, radius: 12 });
    const strut = box({ minX: 19.5, maxX: 21.3, minZ: -0.5, maxZ: 0.5, minY: -0.08, maxY: 2.3, slopeX: 0.41 });
    const grid = wallGrid([placed([pad, strut])], 1, -30, -30, 30, 30, 0.3);
    expect(lineClear(grid, 10.5, 10.5, 11.5, 9.5)).toBe(true);
    expect(lineClear(grid, 0, -20, 0, 20)).toBe(false);
    expect(lineClear(grid, 20.4, -4, 20.4, 4)).toBe(false);
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

  it('steps out of the wall first from a body pressed against it, and every leg after is clear', () => {
    const house = placed([box({ minX: -3, maxX: 3, minZ: -3, maxZ: 3, maxY: 2.7 })]);
    const grid = wallGrid([house], 1, -60, -40, 40, 40, 0.3);
    const path = findPath(grid, 3.3, -2.7, -40, -10);
    expect(path).not.toBeNull();
    expect(Math.hypot(path![0][0] - 3.3, path![0][1] + 2.7)).toBeLessThan(1.5);
    for (let i = 1; i < path!.length; i++) {
      expect(lineClear(grid, path![i - 1][0], path![i - 1][1], path![i][0], path![i][1])).toBe(true);
    }
  });

  it('ends on the open cell beside a goal that stands inside a wall, not inside the wall', () => {
    const grid = wallGrid([WALL], 1, -40, -10, 40, 30, 0.3);
    const path = findPath(grid, 0, 0, 0, 10.5);
    expect(path).not.toBeNull();
    const [x, z] = path![path!.length - 1];
    expect(grid.blocked[Math.floor(z - grid.minZ) * grid.cols + Math.floor(x - grid.minX)]).toBe(0);
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
