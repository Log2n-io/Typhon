import { describe, expect, it } from 'vitest';
import {
  groundOnShapes,
  prepareShape,
  pushOut,
  topAt,
  turn,
  type PlacedShape,
  type PlanBox,
} from '../src/app/structure-collision';

/**
 * S3 — the client predicts the same collision the server applies.
 *
 * <b>These cases are the client half of `AssetCollisionChecks.cs`, deliberately asserting the same numbers.</b> The
 * value of client-side collision is entirely in the two sides agreeing: if they disagree, prediction is corrected by
 * the server anyway and the work bought nothing, and the disagreement shows as a body jittering near a wall rather
 * than as anything that fails.
 *
 * The geometry here is what the server publishes, in the asset's own frame — the client derives none of it.
 */

/** Two wall boxes with a 1.2 m doorway between them, as the published houses are built. */
const DOORWAY: PlanBox[] = [
  {
    minX: -4.6,
    minZ: -0.2,
    maxX: -0.6,
    maxZ: 0.2,
    minY: 0,
    maxY: 2.5,
    planeY: Number.POSITIVE_INFINITY,
    radius: 0,
    slopeX: 0,
    slopeZ: 0,
  },
  {
    minX: 0.6,
    minZ: -0.2,
    maxX: 4.6,
    maxZ: 0.2,
    minY: 0,
    maxY: 2.5,
    planeY: Number.POSITIVE_INFINITY,
    radius: 0,
    slopeX: 0,
    slopeZ: 0,
  },
];

/** The server's own body half-extent, which it publishes; written here because a test is not a client. */
const HALF = 0.3;

/** No world edge: the clamp is exercised by its own case. */
const NO_EDGE = 0;

function at(x: number, z: number): { x: number; z: number } {
  return { x, z };
}

function placed(quarters = 0, x = 0, z = 0, boxes: PlanBox[] = DOORWAY): PlacedShape[] {
  return [{ shape: prepareShape(boxes), x, z, quarters, y: 0 }];
}

describe('structure collision', () => {
  it('lets a body through a doorway and stops it at a wall', () => {
    const inDoor = at(0, 0);
    expect(pushOut(placed(), 1, HALF, NO_EDGE, inDoor)).toBe(false);
    expect(inDoor).toEqual({ x: 0, z: 0 });

    const inWall = at(-2, 0);
    expect(pushOut(placed(), 1, HALF, NO_EDGE, inWall)).toBe(true);
    expect(Math.abs(inWall.z)).toBeGreaterThanOrEqual(0.2 + HALF - 1e-6);
  });

  it('pushes a clipped jamb sideways into the opening, not through the wall', () => {
    const clipped = at(-0.7, 0);
    pushOut(placed(), 1, HALF, NO_EDGE, clipped);
    expect(clipped.x).toBeGreaterThan(-0.6);
    expect(clipped.z).toBe(0);
  });

  it('turns the doorway with the building, the way the renderer turns it', () => {
    expect(pushOut(placed(1), 1, HALF, NO_EDGE, at(0, 0))).toBe(false);
    expect(pushOut(placed(1), 1, HALF, NO_EDGE, at(0, 2))).toBe(true);
    expect(turn(1, DOORWAY[0])).toEqual({
      minX: -0.2,
      minZ: 0.6,
      maxX: 0.2,
      maxZ: 4.6,
      minY: 0,
      maxY: 2.5,
      planeY: Number.POSITIVE_INFINITY,
      radius: 0,
      slopeX: 0,
      slopeZ: 0,
    });
  });

  it('brings a box back to itself after four quarters, and moves it after one', () => {
    let box = DOORWAY[0];
    for (let i = 0; i < 4; i++) {
      box = turn(1, box);
    }

    expect(box).toEqual(DOORWAY[0]);
    expect(turn(1, DOORWAY[0])).not.toEqual(DOORWAY[0]);
  });

  it('places the shapes at the structure, not at the origin', () => {
    expect(pushOut(placed(0, 100, 100), 1, HALF, NO_EDGE, at(0, 0))).toBe(false);
    expect(pushOut(placed(0, 100, 100), 1, HALF, NO_EDGE, at(98, 100))).toBe(true);
  });

  it('rejects on the asset bound before testing a primitive', () => {
    // The bound is the union of the turned boxes: a body outside it cannot touch any of them, which is the client's
    // half of the server's two-level test.
    const shape = prepareShape(DOORWAY);
    // The bound is a PLAN reject only: it spans every height, because a box it rejects is rejected whatever the feet
    // are doing. The vertical test belongs to the individual box, where the floor-or-wall question is answered.
    expect(shape.bounds[0]).toEqual({
      minX: -4.6,
      minZ: -0.2,
      maxX: 4.6,
      maxZ: 0.2,
      minY: -Infinity,
      maxY: Infinity,
      planeY: Number.POSITIVE_INFINITY,
      radius: 0,
      slopeX: 0,
      slopeZ: 0,
    });
    expect(shape.bounds[1]).toEqual({
      minX: -0.2,
      minZ: -4.6,
      maxX: 0.2,
      maxZ: 4.6,
      minY: -Infinity,
      maxY: Infinity,
      planeY: Number.POSITIVE_INFINITY,
      radius: 0,
      slopeX: 0,
      slopeZ: 0,
    });
  });

  it('honours only the live entries of a pooled array', () => {
    // The caller reuses the array and passes a count; a stale entry past the count must not collide.
    const pool: PlacedShape[] = [
      { shape: prepareShape(DOORWAY), x: 1000, z: 1000, quarters: 0, y: 0 },
      { shape: prepareShape(DOORWAY), x: 0, z: 0, quarters: 0, y: 0 },
    ];

    expect(pushOut(pool, 1, HALF, NO_EDGE, at(-2, 0)), 'the live entry is 1 000 m away').toBe(false);
    expect(pushOut(pool, 2, HALF, NO_EDGE, at(-2, 0)), 'both live: the wall at the origin is hit').toBe(true);
  });

  it('clamps to the world edge, where the server clamps', () => {
    // A wall at the very edge: the push would put the body outside, and the server would not have.
    const edge = 100;
    const wall: PlanBox[] = [
      {
        minX: -1,
        minZ: -1,
        maxX: 1,
        maxZ: 1,
        minY: 0,
        maxY: 2.5,
        planeY: Number.POSITIVE_INFINITY,
        radius: 0,
        slopeX: 0,
        slopeZ: 0,
      },
    ];
    const body = at(99.5, 0.5);
    pushOut(placed(0, 99.5, 0, wall), 1, HALF, edge, body);
    expect(body.x).toBeLessThanOrEqual(edge - HALF + 1e-6);
    expect(body.x).toBeGreaterThanOrEqual(-edge + HALF - 1e-6);
  });

  it('leaves a non-finite position alone rather than poisoning it', () => {
    // NaN passes every "outside" test, so an unguarded push would place the body inside every box at once and return
    // a position the predictor could never recover from.
    const nan = at(Number.NaN, 0);
    expect(pushOut(placed(), 1, HALF, NO_EDGE, nan)).toBe(false);
    expect(Number.isNaN(nan.x)).toBe(true);
    expect(nan.z).toBe(0);
  });

  it('resolves a corner between two walls rather than oscillating', () => {
    const corner: PlacedShape[] = [
      {
        shape: prepareShape([
          {
            minX: -4,
            minZ: -0.2,
            maxX: 0.2,
            maxZ: 0.2,
            minY: 0,
            maxY: 2.5,
            planeY: Number.POSITIVE_INFINITY,
            radius: 0,
            slopeX: 0,
            slopeZ: 0,
          },
        ]),
        x: 0,
        z: 0,
        quarters: 0,
        y: 0,
      },
      {
        shape: prepareShape([
          {
            minX: -0.2,
            minZ: -0.2,
            maxX: 0.2,
            maxZ: 4,
            minY: 0,
            maxY: 2.5,
            planeY: Number.POSITIVE_INFINITY,
            radius: 0,
            slopeX: 0,
            slopeZ: 0,
          },
        ]),
        x: 0,
        z: 0,
        quarters: 0,
        y: 0,
      },
    ];

    const body = at(-0.1, 0.1);
    expect(pushOut(corner, 2, HALF, NO_EDGE, body)).toBe(true);

    const insideFirst = body.x > -4 - HALF && body.x < 0.2 + HALF && body.z > -0.2 - HALF && body.z < 0.2 + HALF;
    const insideSecond = body.x > -0.2 - HALF && body.x < 0.2 + HALF && body.z > -0.2 - HALF && body.z < 4 + HALF;
    expect(insideFirst || insideSecond, `ended at ${JSON.stringify(body)}`).toBe(false);
  });
});

/**
 * The client's half of the character controller: a top face shallow enough to stand on carries the feet up with it.
 *
 * <b>The same four cases the server's `RampChecks` makes, against the client's own code.</b> The two have to agree step
 * for step or every step is corrected — a client that walks a ramp the server treats as a wall predicts a body a metre
 * above where the authority has it, and the reconciler spends the session pulling it back down.
 */
describe('walking a published ramp', () => {
  /**
   * The body's half-extent, as the server publishes it in `playerBodyHalfM`.
   *
   * <b>It must be the server's figure, because the whole point of this file is that the two agree step for step.</b> It
   * said 1 m, and said the server published that, long after the server moved to 0.3 — so the client's half of the
   * contract was being checked against a body three times too wide, which is exactly the disagreement a reconciler
   * would have spent the session correcting.
   */
  const HALF = 0.3;
  /** The desert the ramp rises out of. */
  const TERRAIN = -1;

  /**
   * A ramp 8 m long, its top measured from the box's CENTRE: `maxY` is the height there, so a gradient of 0.25 over a
   * box centred at x = -4 puts the top at -1 m at its west lip and +1 m at its head.
   */
  const ramp = (grade: number): PlanBox[] => [
    {
      minX: -8,
      minZ: -2,
      maxX: 0,
      maxZ: 2,
      minY: -3,
      maxY: 0,
      planeY: Number.POSITIVE_INFINITY,
      radius: 0,
      slopeX: grade,
      slopeZ: 0,
    },
  ];

  const placed = (boxes: PlanBox[]): PlacedShape[] => [{ shape: prepareShape(boxes), x: 0, z: 0, quarters: 0, y: 0 }];

  /** Walks east across the ramp a quarter-metre at a time, feeding each answer back as the feet, the way a frame does. */
  function climb(boxes: PlanBox[], step = 0.25): number {
    const shapes = placed(boxes);
    let feet = TERRAIN;
    for (let x = -8; x <= 0.001; x += step) {
      feet = groundOnShapes(shapes, shapes.length, HALF, { x, z: 0 }, TERRAIN, feet);
    }

    return feet;
  }

  it('rises with the ramp and arrives at its head', () => {
    expect(climb(ramp(0.25))).toBeCloseTo(1, 3);
  });

  it('gains no more than a step at a time, so the rise is a walk and not a teleport', () => {
    const shapes = placed(ramp(0.25));
    let feet = TERRAIN;
    for (let x = -8; x <= 0.001; x += 0.25) {
      const next = groundOnShapes(shapes, shapes.length, HALF, { x, z: 0 }, TERRAIN, feet);
      expect(next).toBeGreaterThanOrEqual(feet - 1e-4);
      // STEP_M, which this module holds beside the body's height.
      expect(next - feet).toBeLessThanOrEqual(0.4 + 1e-4);
      feet = next;
    }
  });

  it('does not step onto the flat ledge the same box becomes without its gradient', () => {
    // What the loader produced before the top plane was kept: a 1 m slab, two metres above the feet.
    expect(
      climb([
        {
          minX: -8,
          minZ: -2,
          maxX: 0,
          maxZ: 2,
          minY: -3,
          maxY: 1,
          planeY: Number.POSITIVE_INFINITY,
          radius: 0,
          slopeX: 0,
          slopeZ: 0,
        },
      ]),
    ).toBe(TERRAIN);
  });

  it('does not walk up a face too steep to stand on', () => {
    // A gradient of 2 is 63 degrees, past the 46 the testbench allows. Approached finely, so the refusal is the grade
    // and not the step.
    expect(climb(ramp(2), 0.05)).toBe(TERRAIN);
  });

  it('keeps every height through a quarter turn, because a gradient is a direction in the plan', () => {
    const box = ramp(0.25)[0];
    const probes: [number, number][] = [
      [-8, -2],
      [-8, 2],
      [-4, 0],
      [0, -2],
      [0, 2],
      [-6.5, 1.25],
    ];

    for (let quarters = 0; quarters < 4; quarters++) {
      let turned = box;
      for (let i = 0; i < quarters; i++) {
        turned = turn(1, turned);
      }

      for (const [px, pz] of probes) {
        // The probe turned the way a POSITION turns, which is what the ground sampler applies before reading the plane.
        let qx = px;
        let qz = pz;
        for (let i = 0; i < quarters; i++) {
          [qx, qz] = [qz, -qx];
        }

        expect(topAt(turned, qx, qz)).toBeCloseTo(topAt(box, px, pz), 4);
      }
    }
  });
});
