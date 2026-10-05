import { describe, expect, it } from 'vitest';

import { HEADING_EPSILON, intentChanged, movementIntent, stopAfter, type MoveIntent } from '../src/app/player-intent';
import { SpeedClass } from '../src/data/source';

/** Degrees, for cases a reader can check against a compass rather than against a decimal. */
const deg = (radians: number): number => (radians * 180) / Math.PI;

const held = (...codes: string[]): ReadonlySet<string> => new Set(codes);

describe('movementIntent', () => {
  it('says nothing when nothing is held', () => {
    expect(movementIntent(held(), 0)).toBeNull();
  });

  it('sends forward along the camera facing', () => {
    // Facing +X: W is +X, which is heading 0 in the frame the server integrates in.
    expect(movementIntent(held('KeyW'), 0)?.heading).toBeCloseTo(0, 6);

    // Facing +Z (90°): W is +Z.
    expect(deg(movementIntent(held('KeyW'), Math.PI / 2)!.heading)).toBeCloseTo(90, 4);

    // And a facing that is not an axis, so the case is not passing on symmetry alone.
    expect(deg(movementIntent(held('KeyW'), 0.4)!.heading)).toBeCloseTo(deg(0.4), 4);
  });

  it('sends backward as the opposite heading', () => {
    const back = movementIntent(held('KeyS'), 0)!;
    expect(Math.abs(deg(back.heading))).toBeCloseTo(180, 4);
  });

  it('turns strafe into a heading a quarter turn from the facing', () => {
    expect(deg(movementIntent(held('KeyD'), 0)!.heading)).toBeCloseTo(90, 4);
    expect(deg(movementIntent(held('KeyA'), 0)!.heading)).toBeCloseTo(-90, 4);
  });

  /**
   * The oldest bug in the genre: summing two axis speeds makes a diagonal 1.41x faster than a straight line. Taking the
   * HEADING of the summed vector cannot have that bug, because the server turns a heading plus a speed class into a
   * destination and the class is the only thing that sets the pace. This pins the 45 degrees that proves the vector was
   * normalised by `atan2` rather than added.
   */
  it('gives a diagonal 45 degrees and the same speed class as a straight line', () => {
    const diagonal = movementIntent(held('KeyW', 'KeyD'), 0)!;
    const straight = movementIntent(held('KeyW'), 0)!;

    expect(deg(diagonal.heading)).toBeCloseTo(45, 4);
    expect(diagonal.speedClass).toBe(straight.speedClass);
  });

  it('arrow keys mean what the letters mean', () => {
    expect(movementIntent(held('ArrowUp'), 0.7)).toEqual(movementIntent(held('KeyW'), 0.7));
    expect(movementIntent(held('ArrowLeft'), 0.7)).toEqual(movementIntent(held('KeyA'), 0.7));
  });

  it('runs by default and walks with shift', () => {
    expect(movementIntent(held('KeyW'), 0)?.speedClass).toBe(SpeedClass.run);
    expect(movementIntent(held('KeyW', 'ShiftLeft'), 0)?.speedClass).toBe(SpeedClass.walk);
    expect(movementIntent(held('KeyW', 'ShiftRight'), 0)?.speedClass).toBe(SpeedClass.walk);
  });

  /**
   * Opposites cancelling is a STOP, and no keys at all is silence. Conflating them either leaves an avatar running after
   * the person asked it to stand still, or sends a stop sixty times a second to a session that is not playing.
   */
  it('tells cancelling keys apart from no keys', () => {
    expect(movementIntent(held('KeyW', 'KeyS'), 0)?.speedClass).toBe(SpeedClass.stop);
    expect(movementIntent(held('KeyA', 'KeyD'), 0)?.speedClass).toBe(SpeedClass.stop);
    expect(movementIntent(held(), 0)).toBeNull();
  });

  it('ignores keys that mean nothing here', () => {
    expect(movementIntent(held('KeyQ', 'Space', 'F3'), 0)).toBeNull();
  });
});

describe('intentChanged', () => {
  const run: MoveIntent = { heading: 1, speedClass: SpeedClass.run };

  /**
   * `intentChanged` is asked only about an intent that EXISTS — the release is `stopAfter`'s job, and conflating the two is what
   * made letting go of a key send nothing at all. This case says what the suppression means, not that silence is correct.
   */
  it('has nothing to say about a frame with no intent', () => {
    expect(intentChanged(null, run)).toBe(false);
    expect(intentChanged(null, null)).toBe(false);
  });

  it('sends the first intent', () => {
    expect(intentChanged(run, null)).toBe(true);
  });

  it('suppresses an unchanged intent', () => {
    expect(intentChanged({ ...run }, run)).toBe(false);
  });

  it('sends a new speed class, including the stop that ends a run', () => {
    expect(intentChanged({ heading: 1, speedClass: SpeedClass.walk }, run)).toBe(true);
    expect(intentChanged({ heading: 1, speedClass: SpeedClass.stop }, run)).toBe(true);
  });

  it('suppresses a tremble and sends a turn', () => {
    expect(intentChanged({ heading: 1 + HEADING_EPSILON / 2, speedClass: SpeedClass.run }, run)).toBe(false);
    expect(intentChanged({ heading: 1 + HEADING_EPSILON * 2, speedClass: SpeedClass.run }, run)).toBe(true);
  });
});

describe('stopAfter', () => {
  /**
   * The behaviour A-1 actually needs, and the one nothing asserted: releasing the keys must stop the avatar. The server steers
   * towards a destination a second of travel ahead, so a client that just stops talking watches its character walk on about five
   * metres — which reads as input lag and no refresh rate fixes it.
   */
  it('stops at the heading it was last running on', () => {
    const stop = stopAfter({ heading: 1.25, speedClass: SpeedClass.run });
    expect(stop).not.toBeNull();
    expect(stop!.speedClass).toBe(SpeedClass.stop);
    expect(stop!.heading).toBeCloseTo(1.25, 6);
  });

  it('stops a walk as well as a run', () => {
    expect(stopAfter({ heading: 0, speedClass: SpeedClass.walk })?.speedClass).toBe(SpeedClass.stop);
  });

  it('owes nothing when nothing was moving', () => {
    expect(stopAfter(null)).toBeNull();
    expect(stopAfter({ heading: 2, speedClass: SpeedClass.stop })).toBeNull();
  });
});

describe('heading wrap', () => {
  /**
   * Heading wraps at ±π, so a camera crossing it moves from 3.14 to -3.14 — a raw difference of 2π that a straight subtraction
   * read as the sharpest possible turn, re-sending an identical intent every time the viewer faced that way.
   */
  it('reads a crossing of the wrap as no change', () => {
    const near = Math.PI - HEADING_EPSILON / 4;
    const past = -Math.PI + HEADING_EPSILON / 4;
    expect(
      intentChanged({ heading: past, speedClass: SpeedClass.run }, { heading: near, speedClass: SpeedClass.run }),
    ).toBe(false);
  });

  it('still sees a real turn across the wrap', () => {
    const near = Math.PI - HEADING_EPSILON / 4;
    const past = -Math.PI + HEADING_EPSILON * 4;
    expect(
      intentChanged({ heading: past, speedClass: SpeedClass.run }, { heading: near, speedClass: SpeedClass.run }),
    ).toBe(true);
  });
});
