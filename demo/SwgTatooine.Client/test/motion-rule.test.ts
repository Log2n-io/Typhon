import { evaluateSlot, MOTION_STRIDE, WorldStore, type WorldSchema } from '@typhondb/client';
import { describe, expect, it } from 'vitest';
import { MotionTracker, quantizePosition } from '../src/data/mock/motion-rule';

/** Drives one entity along `path(tick)` and returns the ticks at which a segment was emitted. */
function run(path: (tick: number) => [number, number], ticks: number): { emitted: number[]; tracker: MotionTracker } {
  const tracker = new MotionTracker(1);
  const [x0, z0] = path(0);
  tracker.begin(0, x0, z0, 0);
  const emitted: number[] = [];
  for (let t = 1; t <= ticks; t++) {
    const [x, z] = path(t);
    if (tracker.step(0, x, z, t)) {
      emitted.push(t);
    }
  }

  return { emitted, tracker };
}

describe('mock motion rule (engine § 4)', () => {
  it('fits a straight run once, then only heartbeats every MaxAge ticks', () => {
    const { emitted } = run((t) => [100 + 0.34 * t, -200], 120);
    expect(emitted[0]).toBe(1);
    expect(emitted).toContain(51);
    expect(emitted).toContain(101);
    expect(emitted.length).toBeLessThanOrEqual(5);
  });

  it('emits within two ticks of a change of direction', () => {
    const turnAt = 30;
    const { emitted } = run((t) => (t <= turnAt ? [0.3 * t, 0] : [0.3 * turnAt, 0.3 * (t - turnAt)]), 60);
    const afterTurn = emitted.find((t) => t > turnAt);
    expect(afterTurn).toBeDefined();
    expect((afterTurn ?? 0) - turnAt).toBeLessThanOrEqual(2);
  });

  it('flags a jump beyond the teleport speed with a new epoch and zero velocity', () => {
    const { emitted, tracker } = run((t) => (t < 10 ? [0.2 * t, 0] : [1500, 1500]), 10);
    expect(emitted).toContain(10);
    expect(tracker.epoch[0]).toBe(1);
    expect(tracker.vx[0]).toBe(0);
    expect(tracker.p0x[0]).toBe(quantizePosition(1500));
  });

  it('stops with a zero-velocity segment', () => {
    const { tracker, emitted } = run((t) => [Math.min(t, 20) * 0.4, 0], 30);
    expect(emitted).toContain(21);
    expect(tracker.vx[0]).toBe(0);
  });

  it('keeps what the client renders — three ticks behind, through the SDK store — within tolerance on a zig-zag', () => {
    // The client receives each segment when emitted but renders a render delay behind, so it evaluates segments older
    // than the newest one. A path that turns every few ticks emits segments close together: the store's ring must
    // still hold the one render time needs.
    const schema: WorldSchema = {
      archetypes: [{ index: 0, name: 'M', position: 'motion', groups: ['enter', 'motion'], fields: [] }],
    };
    const world = new WorldStore(schema);
    const store = world.archetypes[0];
    const tracker = new MotionTracker(1);
    const truth: [number, number][] = [];
    let x = 0;
    let z = 0;
    let heading = 0;
    tracker.begin(0, x, z, 0);
    truth.push([quantizePosition(x), quantizePosition(z)]);
    world.beginFrame(0);
    const slot = world.enter(0, 1);
    store.resetMotion(slot, tracker.p0x[0], tracker.p0z[0], 0, 0, 0, 0);

    const out = new Float64Array(MOTION_STRIDE);
    const delay = 3;
    let worst = 0;
    for (let t = 1; t <= 600; t++) {
      if (t % 3 === 0) {
        heading += 1.1;
      }

      x += Math.cos(heading) * 0.42;
      z += Math.sin(heading) * 0.42;
      truth.push([quantizePosition(x), quantizePosition(z)]);
      world.beginFrame(t);
      if (tracker.step(0, x, z, t)) {
        store.pushSegment(
          slot,
          tracker.p0x[0],
          tracker.p0z[0],
          tracker.vx[0],
          tracker.vz[0],
          tracker.t0[0],
          tracker.epoch[0],
        );
      }

      if (t > delay) {
        evaluateSlot(store, slot, t - delay, 0, out, 0);
        const [tx, tz] = truth[t - delay];
        worst = Math.max(worst, Math.hypot(out[0] - tx, out[1] - tz));
      }
    }

    expect(worst).toBeLessThanOrEqual(0.05);
  });
});
