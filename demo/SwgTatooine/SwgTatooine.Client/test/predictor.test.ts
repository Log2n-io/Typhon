import { describe, expect, it } from 'vitest';

import { SpeedClass } from '../src/data/source';
import { Predictor, speedForClass } from '../src/app/predictor';

/**
 * A14's predictor. The properties under test are the ones that make prediction worth having and the ones that make it safe:
 * it moves on the frame the key arrives, it does NOT fight the render delay, and it gives way to the server whenever the
 * server did something the client could not know about.
 */

const WALK = 2.5;
const RUN = 5;

/** The whole granted ladder, as a session's owner block carries it. The figures are the server's; these are its. */
const GRANTED = { walkMps: WALK, runMps: RUN, burstMps: 9.11, speederMps: 12, speederFastMps: 24, swoopMps: 20 };

/** Before the owner block arrives, every one of them is NaN. */
const UNKNOWN = { walkMps: NaN, runMps: NaN, burstMps: NaN, speederMps: NaN, speederFastMps: NaN, swoopMps: NaN };

/** A frame at 60 Hz. */
const FRAME_MS = 16;
const FRAME_S = FRAME_MS / 1000;

/**
 * Drives the predictor for `frames` frames with a fixed intent, feeding an authority that lags by `delayMs` — the shape a
 * real session has. The authority is generated from the SAME rule the server uses, which is what makes a non-zero steady
 * state a bug rather than a modelling difference.
 */
function run(options: {
  frames: number;
  heading: number;
  speedMps: number;
  delayMs: number;
  /** Where the server actually is at time t, overriding the matching model — a wall, a clamp, a teleport. */
  authorityAt?: (tMs: number) => { x: number; z: number };
}): Predictor {
  const { frames, heading, speedMps, delayMs } = options;
  const predictor = new Predictor();
  let now = 1000;
  predictor.start(now, 0, 0);

  const model = (tMs: number) => ({
    x: Math.cos(heading) * speedMps * (tMs / 1000),
    z: Math.sin(heading) * speedMps * (tMs / 1000),
  });
  const authority = options.authorityAt ?? model;

  for (let i = 0; i < frames; i++) {
    now += FRAME_MS;
    predictor.setIntent(heading, speedMps);
    // The authority as of render time: now − delay, measured from the start of the motion.
    const at = authority(Math.max(0, now - delayMs - 1000));
    predictor.step(now, FRAME_S, at.x, at.z, delayMs);
  }

  return predictor;
}

/**
 * The client's half of A14b: a frame is split at the instant the key moved, so the integral runs from the key and not from
 * the frame that noticed it.
 *
 * The server does the same to the tick its command lands in, and the two together are what make "the same duration both
 * sides" true rather than true on average. Driven here over a 60 fps frame train with the authority fed from the
 * predictor's own path, so the corrector never acts and what is left is the integration.
 */
describe('the frame an intent changes in', () => {
  /** Runs a gesture from `downMs` to `upMs` on a fixed 60 fps train and returns how far the pure path travelled. */
  const gesture = (downMs: number, upMs: number): number => {
    const predictor = new Predictor();
    let now = 1000;
    predictor.start(now, 0, 0);

    for (let i = 0; i < 120; i++) {
      now += FRAME_MS;
      const down = now - FRAME_MS < downMs && downMs <= now;
      const up = now - FRAME_MS < upMs && upMs <= now;
      if (now >= upMs) {
        predictor.clearIntent(up ? upMs : NaN);
      } else if (now >= downMs) {
        predictor.setIntent(0, RUN, down ? downMs : NaN);
      } else {
        predictor.clearIntent();
      }

      // A delay past the ring's own span, so `sampleAt` refuses every frame and NO correction is applied: what is left is
      // the integration, which is what this case is about. Feeding an authority instead would let the corrector converge
      // on it and pass the case for a predictor that integrated wrongly.
      predictor.step(now, FRAME_S, 0, 0, 1e6);
    }

    return predictor.x;
  };

  it.each([
    [1004, 1504],
    [1007.3, 1511.9],
    [1015.9, 1523.1],
    [1000.4, 1600.6],
  ])('travels exactly the held duration for a key down at %s and up at %s', (downMs, upMs) => {
    // Sub-millimetre: the artefact the phase exists to remove is up to 500 mm at 10 Hz, and a frame of travel — what this
    // half of it removes — is 83 mm at 60 fps.
    expect(gesture(downMs, upMs)).toBeCloseTo((RUN * (upMs - downMs)) / 1000, 9);
  });

  it('is a plain whole-frame integration when no instant is given, which is every frame of a held key', () => {
    const predictor = new Predictor();
    let now = 1000;
    predictor.start(now, 0, 0);
    for (let i = 0; i < 60; i++) {
      now += FRAME_MS;
      predictor.setIntent(0, RUN);
      predictor.step(now, FRAME_S, 0, 0, 1e6);
    }

    expect(predictor.x).toBeCloseTo(RUN * 60 * FRAME_S, 9);
  });

  it('pays at most one extra frame for an instant that predates the frame, and never a multiple of it', () => {
    // A stamp before the frame began is the catch-up: the handler that declared it ran while the previous frame was still
    // being drawn, so the client owes the travel between the two. Bounded at one frame, because a long frame, a
    // backgrounded tab or a caller passing the wrong clock would otherwise scale one frame's integration arbitrarily.
    const predictor = new Predictor();
    predictor.start(1000, 0, 0);
    predictor.setIntent(0, RUN, -1e9);
    predictor.step(1016, FRAME_S, 0, 0, 1e6);

    expect(predictor.x).toBeCloseTo(2 * RUN * FRAME_S, 12);
  });

  /**
   * A change declared for an instant that has not arrived yet.
   *
   * The client tells the server the first tick boundary its command can actually reach, and then has to wait for it: a
   * predictor that started early would be ahead of the authority by exactly the margin, every time, which is the error
   * the declaration exists to avoid.
   */
  it('waits for an instant in the future, then integrates from it', () => {
    const predictor = new Predictor();
    let now = 1000;
    predictor.start(now, 0, 0);

    // Declared 30 ms out: two frames away at 60 fps.
    const dueAt = now + 30;
    for (let i = 0; i < 10; i++) {
      now += FRAME_MS;
      predictor.setIntent(0, RUN, i === 0 ? dueAt : NaN);
      predictor.step(now, FRAME_S, 0, 0, 1e6);
      if (now <= dueAt) {
        expect(predictor.x).toBe(0);
      }
    }

    expect(predictor.x).toBeCloseTo((RUN * (now - dueAt)) / 1000, 12);
  });
});

describe('speedForClass', () => {
  it('maps each class onto the stat the server replicated', () => {
    expect(speedForClass(SpeedClass.stop, GRANTED)).toBe(0);
    expect(speedForClass(SpeedClass.walk, GRANTED)).toBe(WALK);
    expect(speedForClass(SpeedClass.run, GRANTED)).toBe(RUN);
    expect(speedForClass(SpeedClass.burst, GRANTED)).toBe(GRANTED.burstMps);
    expect(speedForClass(SpeedClass.speeder, GRANTED)).toBe(GRANTED.speederMps);
    expect(speedForClass(SpeedClass.speederFast, GRANTED)).toBe(GRANTED.speederFastMps);
    expect(speedForClass(SpeedClass.swoop, GRANTED)).toBe(GRANTED.swoopMps);
  });

  /**
   * Every gait reads its OWN stat, which is the mistake a switch like this invites: a case falling through to `runMps`
   * predicts a run while the server moves a speeder, and the error grows at 7 m/s with nothing saying so.
   */
  it('reads a different stat for every gait', () => {
    const speeds = [SpeedClass.walk, SpeedClass.run, SpeedClass.burst, SpeedClass.speeder, SpeedClass.speederFast].map(
      (gait) => speedForClass(gait, GRANTED),
    );

    expect(new Set(speeds).size).toBe(speeds.length);
  });

  /**
   * The stats arrive in the owner block, so before the first `SELF` they are `NaN`. Propagating it is deliberate: a client
   * that substituted a guess would drift without bound, and one that substituted 0 would look frozen.
   */
  it('propagates NaN when the stat has not arrived', () => {
    expect(speedForClass(SpeedClass.run, UNKNOWN)).toBeNaN();
    expect(speedForClass(SpeedClass.speederFast, UNKNOWN)).toBeNaN();
  });

  /**
   * A class this build does not know about predicts NOTHING, rather than falling back to a run.
   *
   * The server refuses a class it does not grant, so the avatar does not move at all — and a client that predicted a run
   * for it would walk the body forward while the server held it still, then be snapped back, for as long as the key was
   * held.
   */
  it('refuses to predict a class it does not know', () => {
    expect(speedForClass(200, GRANTED)).toBeNaN();
  });
});

describe('Predictor', () => {
  it('moves on the first frame, not after a round trip', () => {
    const predictor = new Predictor();
    predictor.start(1000, 0, 0);
    predictor.setIntent(0, RUN);
    predictor.step(1000 + FRAME_MS, FRAME_S, 0, 0, 150);

    // cos(0) = 1, so all of it on x. One frame of running is 8 cm, and the point is that it is not zero.
    expect(predictor.x).toBeCloseTo(RUN * FRAME_S, 5);
    expect(predictor.z).toBeCloseTo(0, 5);
  });

  /**
   * <b>The property the whole design turns on.</b> The authority trails real time by the render delay, so a predictor that
   * corrected toward it would be pulled back at `lag / τ` — about 1.5 m/s against a 5 m/s run, a third of the speed — and the
   * avatar would move visibly slower than it should while looking like a smoothing bug. Reconciling against what was
   * predicted FOR the authority's moment leaves the steady state at zero.
   */
  it('runs at its full speed despite a lagging authority', () => {
    const frames = 90;
    const predictor = run({ frames, heading: 0, speedMps: RUN, delayMs: 150 });

    const elapsedS = (frames * FRAME_MS) / 1000;
    expect(predictor.x).toBeCloseTo(RUN * elapsedS, 1);
    expect(predictor.snaps).toBe(0);
  });

  it('leads the authority by about the delay, which is what removes the latency', () => {
    const delayMs = 150;
    const predictor = run({ frames: 90, heading: 0, speedMps: RUN, delayMs });

    const authorityNow = RUN * ((90 * FRAME_MS - delayMs) / 1000);
    expect(predictor.x - authorityNow).toBeCloseTo((RUN * delayMs) / 1000, 1);
  });

  it('integrates in the server frame: cos on x, sin on z', () => {
    const heading = Math.PI / 2;
    const predictor = run({ frames: 30, heading, speedMps: WALK, delayMs: 100 });

    expect(predictor.x).toBeCloseTo(0, 1);
    expect(predictor.z).toBeGreaterThan(0.5);
  });

  /**
   * A wall, over the WHOLE delay range the clock can hand out (its floor is 20 ms here and its ceiling 300 ms).
   *
   * <b>This is the case that catches a dead-time windup, and the single-delay version of it did not.</b> The first version of
   * the predictor corrected the position without correcting its history, so the same error was re-measured for a whole render
   * delay: at D = 200 ms it limit-cycled +/- 0.21 m and at D = 300 ms it diverged until it snapped, then repeated. The old
   * test asserted a band at D = 150 only and passed anyway, because it sampled a value that was oscillating THROUGH the band.
   * Monotonic settling is the property; a final position is not.
   */
  it.each([20, 41, 150, 200, 250, 300])('parks monotonically past a wall at a render delay of %i ms', (delayMs) => {
    const wall = 1;
    const predictor = new Predictor();
    let now = 1000;
    predictor.start(now, 0, 0);

    const path: number[] = [];
    let staleAfterWarmUp = 0;
    for (let i = 0; i < 240; i++) {
      now += FRAME_MS;
      predictor.setIntent(0, RUN);
      // The server walks to the wall and stops; the authority is that, as of render time.
      const authAt = Math.max(0, now - delayMs - 1000);
      predictor.step(now, FRAME_S, Math.min(wall, RUN * (authAt / 1000)), 0, delayMs);
      path.push(predictor.x);
      // The ring legitimately cannot reach back during its first D of frames. What must not happen is a stale read AFTER it
      // has filled — that is the ring being too small for this refresh rate, and it stops reconciliation silently.
      if (i === 120) {
        staleAfterWarmUp = predictor.staleReads;
      }
    }

    // Settled: the last half-second varies by millimetres, not by the tens of centimetres a limit cycle shows.
    const tail = path.slice(-30);
    const spread = Math.max(...tail) - Math.min(...tail);
    expect(spread, `oscillating at D=${delayMs}: spread ${spread.toFixed(3)} m over the last 30 frames`).toBeLessThan(
      0.02,
    );

    // And it settled where the physics says it must: the client cannot learn about the wall sooner than the render delay.
    const steady = wall + RUN * (delayMs / 1000 + 0.12);
    expect(path[path.length - 1]).toBeGreaterThan(steady - 0.35);
    expect(path[path.length - 1]).toBeLessThan(steady + 0.35);

    // A held key against a building is not a structural surprise, at any delay the clock can produce.
    expect(predictor.snaps, `snapped at D=${delayMs}`).toBe(0);
    expect(predictor.staleReads, `the ring stopped reaching back after warm-up at D=${delayMs}`).toBe(staleAfterWarmUp);
  });

  /**
   * A standing player with a small offset must converge to zero, not swing. The windup made this limit-cycle at every delay
   * in the clock's range, which is the most visible form of the bug: an avatar nobody is driving, drifting back and forth.
   */
  it.each([20, 150, 300])('settles a standing offset to zero at a render delay of %i ms', (delayMs) => {
    const predictor = new Predictor();
    let now = 1000;
    predictor.start(now, 0, 0);
    const auth = 0.019;

    const path: number[] = [];
    for (let i = 0; i < 180; i++) {
      now += FRAME_MS;
      predictor.clearIntent();
      predictor.step(now, FRAME_S, auth, 0, delayMs);
      path.push(predictor.x);
    }

    expect(path[path.length - 1]).toBeCloseTo(auth, 3);
    const tail = path.slice(-60);
    expect(Math.max(...tail) - Math.min(...tail), `oscillating at D=${delayMs}`).toBeLessThan(0.001);
    expect(predictor.snaps).toBe(0);
  });

  /**
   * A 240 Hz display with the clock at its ceiling: the ring must still reach the authority's moment. At 48 samples it spanned
   * 196 ms against a 300 ms delay, so reconciliation stopped completely and an offset was held for ever with nothing counted.
   */
  it('reconciles at 240 Hz with the delay at its ceiling', () => {
    const fastFrameMs = 1000 / 240;
    const predictor = new Predictor();
    let now = 1000;
    predictor.start(now, 0, 0);
    const auth = 0.5;

    let staleAfterWarmUp = 0;
    for (let i = 0; i < 400; i++) {
      now += fastFrameMs;
      predictor.clearIntent();
      predictor.step(now, fastFrameMs / 1000, auth, 0, 300);
      if (i === 200) {
        staleAfterWarmUp = predictor.staleReads;
      }
    }

    // 300 ms at 240 Hz is 72 frames of warm-up, and then it must reconcile on every frame. At HISTORY = 48 the ring spanned
    // 196 ms, so it NEVER reached back: every frame was stale and the offset below was held for ever.
    expect(staleAfterWarmUp, 'the ring never filled at this refresh rate').toBeLessThan(100);
    expect(predictor.staleReads, 'the ring cannot reach back far enough at this refresh rate').toBe(staleAfterWarmUp);
    expect(predictor.x).toBeCloseTo(auth, 2);
  });

  /** A teleport — a portal, a shuttle. Past the limit the client is wrong about something structural, so it jumps and says so. */
  it('snaps and counts when the server moves the player a long way', () => {
    const predictor = run({
      frames: 60,
      heading: 0,
      speedMps: 0,
      delayMs: 100,
      authorityAt: () => ({ x: 400, z: -250 }),
    });

    expect(predictor.snaps).toBeGreaterThan(0);
    expect(predictor.x).toBeCloseTo(400, 3);
    expect(predictor.z).toBeCloseTo(-250, 3);
  });

  it('stands still when the keys come up, and lets the authority catch up', () => {
    const predictor = new Predictor();
    let now = 1000;
    predictor.start(now, 0, 0);
    for (let i = 0; i < 30; i++) {
      now += FRAME_MS;
      predictor.setIntent(0, RUN);
      predictor.step(now, FRAME_S, 0, 0, 0);
    }

    const stoppedAt = predictor.x;
    predictor.clearIntent();
    for (let i = 0; i < 10; i++) {
      now += FRAME_MS;
      predictor.step(now, FRAME_S, stoppedAt, 0, 0);
    }

    expect(predictor.x).toBeCloseTo(stoppedAt, 2);
  });

  /** Predicting at a guessed speed is worse than not predicting, so an absent stat refuses and counts rather than assuming. */
  it('refuses an intent whose speed is not known, and counts it', () => {
    const predictor = new Predictor();
    predictor.start(1000, 7, 7);

    expect(predictor.setIntent(0, NaN)).toBe(false);
    predictor.step(1000 + FRAME_MS, FRAME_S, 7, 7, 0);

    expect(predictor.fallbacks).toBe(1);
    expect(predictor.x).toBeCloseTo(7, 5);
    expect(predictor.z).toBeCloseTo(7, 5);
  });

  it('does nothing at all when it is not active', () => {
    const predictor = new Predictor();
    predictor.setIntent(0, RUN);
    predictor.step(1000, FRAME_S, 99, 99, 0);

    expect(predictor.active).toBe(false);
    expect(predictor.x).toBe(0);
    expect(predictor.z).toBe(0);
  });

  it('stops cleanly, so a god camera or a spectator predicts nothing', () => {
    const predictor = new Predictor();
    predictor.start(1000, 1, 2);
    predictor.setIntent(0, RUN);
    predictor.stop();
    predictor.step(1000 + FRAME_MS, FRAME_S, 50, 50, 0);

    expect(predictor.active).toBe(false);
    expect(predictor.x).toBe(1);
    expect(predictor.z).toBe(2);
  });

  /**
   * Re-anchoring on a new avatar must not integrate from the old one's position — a realm change gives the session a new
   * netId at a completely different place.
   */
  it('re-anchors on start', () => {
    const predictor = new Predictor();
    predictor.start(1000, 10, 10);
    predictor.setIntent(0, RUN);
    predictor.step(1016, FRAME_S, 10, 10, 0);
    predictor.start(1032, -300, 400);

    expect(predictor.x).toBe(-300);
    expect(predictor.z).toBe(400);
    expect(predictor.snaps).toBe(0);
  });
});
