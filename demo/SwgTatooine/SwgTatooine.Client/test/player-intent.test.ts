import { describe, expect, it } from 'vitest';

import {
  gaitChanged,
  HEADING_EPSILON,
  intentChanged,
  movementIntent,
  stopAfter,
  type MoveIntent,
} from '../src/app/player-intent';
import { SpeedClass } from '../src/data/source';

/** Degrees, for cases a reader can check against a compass rather than against a decimal. */
const deg = (radians: number): number => (radians * 180) / Math.PI;

const held = (...codes: string[]): ReadonlySet<string> => new Set(codes);

describe('movementIntent', () => {
  it('says nothing when nothing is held', () => {
    expect(movementIntent(held(), 0)).toBeNull();
  });

  /**
   * <b>The basis is the EYE CAMERA's, and these cases are derived from it rather than asserted independently.</b>
   * `EyeCamera.updateEye` pulls the eye backward along `(-sin yaw, -cos yaw)`, so it LOOKS along `(sin yaw, cos yaw)` —
   * Babylon's left-handed reading of `rotation.y` — and screen-right is `(cos yaw, -sin yaw)`. The first version of both
   * this file and the code under test used `(cos yaw, sin yaw)` for forward, which is the same vector a quarter turn
   * away, so W walked the avatar to screen-right and the cases agreed with it. Stating the camera's convention here is
   * what stops that happening again.
   */
  /** Where the camera is looking at this yaw, as a heading the server would integrate. */
  const facing = (yaw: number): number => Math.atan2(Math.cos(yaw), Math.sin(yaw));

  it('sends forward along the camera facing', () => {
    // Yaw 0: the camera looks down +Z, so W is +Z, which is heading 90° in the frame the server integrates in.
    expect(deg(movementIntent(held('KeyW'), 0)!.heading)).toBeCloseTo(90, 4);

    // A quarter turn of the camera turns the walk by the same quarter turn.
    expect(deg(movementIntent(held('KeyW'), Math.PI / 2)!.heading)).toBeCloseTo(0, 4);

    // And a facing that is not an axis, so the case is not passing on symmetry alone.
    expect(deg(movementIntent(held('KeyW'), 0.4)!.heading)).toBeCloseTo(deg(facing(0.4)), 4);
  });

  it('sends backward as the opposite heading', () => {
    const back = movementIntent(held('KeyS'), 0)!;
    expect(deg(back.heading)).toBeCloseTo(-90, 4);
  });

  it('turns strafe into a heading a quarter turn from the facing', () => {
    // Yaw 0 looks down +Z, so screen-right is +X (heading 0) and left is -X.
    expect(deg(movementIntent(held('KeyD'), 0)!.heading)).toBeCloseTo(0, 4);
    expect(Math.abs(deg(movementIntent(held('KeyA'), 0)!.heading))).toBeCloseTo(180, 4);
  });

  /**
   * The property that matters more than any single axis: the walk is always the camera's facing, turned by whatever the
   * key means relative to the screen. Checked at several yaws so a basis that is merely rotated cannot satisfy it.
   */
  it.each([0, 0.4, 1.2, Math.PI / 2, 2.9, -1.1])('walks exactly where the camera looks at yaw %s', (yaw) => {
    const w = movementIntent(held('KeyW'), yaw)!.heading;
    expect(Math.abs(Math.atan2(Math.sin(w - facing(yaw)), Math.cos(w - facing(yaw))))).toBeLessThan(1e-6);
  });

  /**
   * The oldest bug in the genre: summing two axis speeds makes a diagonal 1.41x faster than a straight line. Taking the
   * HEADING of the summed vector cannot have that bug, because the server turns a heading plus a speed class into a
   * destination and the class is the only thing that sets the pace. This pins the 45 degrees that proves the vector was
   * normalised by `atan2` rather than added.
   */
  it('gives a diagonal 45 degrees from each of its parts, and the same speed class', () => {
    const diagonal = movementIntent(held('KeyW', 'KeyD'), 0)!;
    const straight = movementIntent(held('KeyW'), 0)!;
    const strafe = movementIntent(held('KeyD'), 0)!;

    const between = (a: number, b: number): number => Math.abs(deg(Math.atan2(Math.sin(a - b), Math.cos(a - b))));
    expect(between(diagonal.heading, straight.heading)).toBeCloseTo(45, 4);
    expect(between(diagonal.heading, strafe.heading)).toBeCloseTo(45, 4);
    expect(diagonal.speedClass).toBe(straight.speedClass);
  });

  it('arrow keys mean what the letters mean', () => {
    expect(movementIntent(held('ArrowUp'), 0.7)).toEqual(movementIntent(held('KeyW'), 0.7));
    expect(movementIntent(held('ArrowLeft'), 0.7)).toEqual(movementIntent(held('KeyA'), 0.7));
  });

  it('walks by default and travels at the latched gait with shift', () => {
    // Inverted on 2026-10-21 (Loïc): every rung of the ladder is a deliberate thing, so none of them is what a body
    // should be doing by accident while crossing a room.
    expect(movementIntent(held('KeyW'), 0)?.speedClass).toBe(SpeedClass.walk);
    expect(movementIntent(held('KeyW', 'ShiftLeft'), 0)?.speedClass).toBe(SpeedClass.run);
    expect(movementIntent(held('KeyW', 'ShiftRight'), 0)?.speedClass).toBe(SpeedClass.run);
    // Shift gives whatever Shift+1..4 last latched, not a fixed gait.
    expect(movementIntent(held('KeyW', 'ShiftLeft'), 0, SpeedClass.speeder)?.speedClass).toBe(SpeedClass.speeder);
    expect(movementIntent(held('KeyW'), 0, SpeedClass.speeder)?.speedClass).toBe(SpeedClass.walk);
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

/**
 * A14b's rule: which transitions have an INSTANT, and therefore carry a sub-tick phase.
 *
 * The phase is what makes the client's integration and the server's cover the same duration — the server splits the tick a
 * command lands in between the old velocity and the new one — and it only means something for a change caused by one key
 * going down or up. A heading change is a continuous thing the server is told about at most once a tick, so it carries no
 * phase and both sides integrate whole steps.
 */
describe('gaitChanged', () => {
  const run = (heading = 0): MoveIntent => ({ heading, speedClass: SpeedClass.run });

  it('is a start, because no opinion reads as a stop', () => {
    expect(gaitChanged(run(), null)).toBe(true);
  });

  it('is a stop, both as an explicit one and as the keys coming up', () => {
    expect(gaitChanged({ heading: 0, speedClass: SpeedClass.stop }, run())).toBe(true);
    expect(gaitChanged(null, run())).toBe(true);
  });

  it('is a change of speed class — Shift taken or released under a held key', () => {
    expect(gaitChanged({ heading: 0, speedClass: SpeedClass.walk }, run())).toBe(true);
  });

  it('is NOT a change of heading, however large: a turn has no single instant to name', () => {
    expect(gaitChanged(run(Math.PI), run(0))).toBe(false);
  });

  it('is not the same question as intentChanged, which decides whether to SEND', () => {
    // A turn is worth sending and carries no phase. Stated together because conflating the two is the mistake available
    // here: it would stamp every frame of a mouse turn with the instant of whatever key was last touched.
    const turned = run(1);
    expect(intentChanged(turned, run(0))).toBe(true);
    expect(gaitChanged(turned, run(0))).toBe(false);
  });

  it('says nothing changed when nothing is held and nothing was', () => {
    expect(gaitChanged(null, null)).toBe(false);
  });
});
