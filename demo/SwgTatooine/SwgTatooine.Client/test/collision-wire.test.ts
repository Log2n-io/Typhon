import { describe, expect, it } from 'vitest';

import { readCollision } from '../src/data/server-config';
import { groundOnShapes, prepareShape, pushOut, topAt, type PlacedShape, type PlanBox } from '../src/app/structure-collision';

/**
 * The ten numbers the server publishes for a collider, read at the indices it publishes them.
 *
 * <b>This file exists because a number was added in the MIDDLE of the wire format and nothing failed.</b> The server
 * grew a seventh number — the top face's plane, which is not the bounding box's top — and this parser was still
 * written for the shorter box: it took the plane for `slopeX` and `slopeX` for `slopeZ`. Every typecheck, lint and one
 * of the 736 tests passed. What happened instead is that a house's entry ramp predicted as a FLAT slab at its bounding
 * box's top, 0.79 m above the desert, which is a wall three steps tall — so the client refused to walk a body up a
 * ramp the server was carrying it up perfectly well, and the body stuck at the foot of one. It read as "collision is
 * broken".
 *
 * The numbers below are `tat_house_large`'s published `EntryRamp` as the server writes it, and the server asserts the
 * same figures in `RoundColliderChecks.TheWireCarriesTenNumbersInThisOrder`.
 */

/** `[minX, minZ, maxX, maxZ, minY, maxY, planeY, slopeX, slopeZ, radius]` — the order, which is the whole point. */
const ENTRY_RAMP = [-3.2, 6.8915, 0.2, 11.2471, -1.2789, 0.6998, -0.1275, 0, -0.4005, 0];

/** The shuttleport's plaza: a 21.6 m cylinder, published as the square around it plus its radius. */
const PLAZA = [-21.6, -21.6, 21.6, 21.6, -1, 0.3, 0.3, 0, 0, 21.6];

const HALF = 0.3;
const NO_EDGE = 0;

function parsed(...boxes: readonly (readonly number[])[]): readonly PlanBox[] {
  return readCollision([boxes], 1)[0];
}

function placed(boxes: readonly PlanBox[]): PlacedShape[] {
  return [{ shape: prepareShape(boxes), x: 0, z: 0, quarters: 0, y: 0 }];
}

describe('the published collider', () => {
  it('puts each number where the server put it', () => {
    const [ramp] = parsed(ENTRY_RAMP);
    expect(ramp.minY).toBeCloseTo(-1.2789, 4);
    expect(ramp.maxY).toBeCloseTo(0.6998, 4);
    expect(ramp.planeY).toBeCloseTo(-0.1275, 4);
    expect(ramp.slopeX).toBe(0);
    expect(ramp.slopeZ).toBeCloseTo(-0.4005, 4);
    expect(ramp.radius).toBe(0);
  });

  it('reads the ramp as tilted along Z, not along X', () => {
    // The off-by-one put the plane (-0.1275) in slopeX and the real slopeX (0) in slopeZ: a gentle slope on the wrong
    // axis, which over a ramp walked along Z is no slope at all.
    const [ramp] = parsed(ENTRY_RAMP);
    expect(Math.abs(ramp.slopeZ)).toBeGreaterThan(0.3);
    expect(ramp.slopeX).toBe(0);
  });

  it('climbs, which a flat slab does not', () => {
    const [ramp] = parsed(ENTRY_RAMP);
    const low = topAt(ramp, -1.5, 11.0);
    const high = topAt(ramp, -1.5, 7.0);
    expect(low).toBeLessThan(-0.7);
    expect(high).toBeGreaterThan(0.6);
    expect(high - low).toBeGreaterThan(1.3);
  });

  it('carries a body up, a step at a time, from the desert to the plinth', () => {
    // The live loop: the previous answer becomes the next step's feet, which is how the server does it. Before the fix
    // this never left the terrain, because every sample came back at the slab's flat top and was a wall.
    const shapes = placed(parsed(ENTRY_RAMP));
    const terrain = -0.09;
    let feet = terrain;
    for (let z = 11.5; z >= 6.9; z -= 0.5) {
      const next = groundOnShapes(shapes, 1, HALF, { x: -1.5, z }, terrain, feet);
      expect(next - feet).toBeLessThanOrEqual(0.4 + 1e-4);
      expect(next).toBeGreaterThanOrEqual(feet - 1e-4);
      feet = next;
    }

    expect(feet).toBeGreaterThan(0.6);
  });

  it('keeps a short box from an older server blocking rather than turning it into a floor', () => {
    const [wall] = parsed([-1, -1, 1, 1]);
    expect(wall.maxY).toBe(Number.POSITIVE_INFINITY);
    expect(wall.slopeX).toBe(0);
    expect(wall.radius).toBe(0);
  });
});

describe('a round collider', () => {
  it('keeps its radius off the wire', () => {
    const [plaza] = parsed(PLAZA);
    expect(plaza.radius).toBeCloseTo(21.6, 4);
    expect(plaza.maxX - plaza.minX).toBeCloseTo(43.2, 4);
  });

  it('leaves the corner of its bounding square open, where nothing is drawn', () => {
    // 22 m out on the diagonal is 15.56 on each axis: inside the 21.6 square, outside the 21.6 circle. Taken as a
    // square this ejected a body standing in open desert, up to 8.9 m from the plaza's real edge.
    const shapes = placed(parsed(PLAZA));
    const d = 22 / Math.SQRT2;
    const body = { x: d, z: d };
    expect(Math.max(Math.abs(body.x), Math.abs(body.z))).toBeLessThan(21.6);
    expect(pushOut(shapes, 1, HALF, NO_EDGE, body, -0.4)).toBe(false);
    expect(Math.hypot(body.x, body.z)).toBeCloseTo(22, 4);
  });

  it('stops a body at its real edge, radially', () => {
    const shapes = placed(parsed(PLAZA));
    const body = { x: 21.0, z: 0 };
    expect(pushOut(shapes, 1, HALF, NO_EDGE, body, -0.4)).toBe(true);
    expect(body.x).toBeCloseTo(21.6 + HALF, 3);
    expect(body.z).toBeCloseTo(0, 6);
  });

  it('is a floor inside its circle and not outside it', () => {
    const shapes = placed(parsed(PLAZA));
    const d = 22 / Math.SQRT2;
    expect(groundOnShapes(shapes, 1, HALF, { x: 0, z: 0 }, -0.4, 0.3)).toBeCloseTo(0.3, 4);
    expect(groundOnShapes(shapes, 1, HALF, { x: d, z: d }, -0.4, 0.3)).toBeCloseTo(-0.4, 4);
  });
});
