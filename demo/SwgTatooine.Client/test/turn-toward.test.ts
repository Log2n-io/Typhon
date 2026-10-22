import { describe, expect, it } from 'vitest';
import { shortestAngle, turnToward } from '../src/math/vec';

/**
 * The facing clamp that keeps a reconciliation from spinning the character round.
 *
 * The case that matters is the measured one: a body walking at 5 m/s is corrected after a key release, the replicated
 * velocity reverses for about 17 ms, and an unbounded facing follows it through 180 degrees — then freezes there,
 * because once the velocity is exactly zero the heading keeps its last value.
 */

const RATE = Math.PI * 3;

describe('turnToward', () => {
  it('snaps when the turn is within the step', () => {
    expect(turnToward(0, 0.1, 1)).toBeCloseTo(0.1, 12);
    expect(turnToward(1, 1, 1)).toBeCloseTo(1, 12);
  });

  it('turns the SHORT way round across the wrap', () => {
    // From just under +pi to just over -pi is a small turn, not a trip the long way through zero.
    const from = Math.PI - 0.05;
    const to = -Math.PI + 0.05;
    expect(shortestAngle(to - from)).toBeCloseTo(0.1, 12);
    expect(turnToward(from, to, 1)).toBeCloseTo(to, 12);
    // And a bounded step from there moves towards it, never away.
    expect(shortestAngle(turnToward(from, to, 0.02) - from)).toBeCloseTo(0.02, 12);
  });

  it('bounds a reversal to the rate, so one frame of correction is a lean', () => {
    // 17 ms of backwards velocity, the duration measured in the browser.
    const after = turnToward(0, Math.PI, RATE * 0.017);
    expect(Math.abs(after) * (180 / Math.PI)).toBeLessThan(10);
  });

  it('completes a real about-face in about a third of a second', () => {
    let facing = 0;
    let frames = 0;
    // 60 fps, turning to face behind.
    while (Math.abs(shortestAngle(Math.PI - facing)) > 1e-6 && frames < 1000) {
      facing = turnToward(facing, Math.PI, RATE / 60);
      frames++;
    }

    expect(frames / 60).toBeGreaterThan(0.2);
    expect(frames / 60).toBeLessThan(0.45);
  });

  it('unwinds: a facing dragged off by a correction returns once the body faces forward again', () => {
    const dt = 1 / 60;
    let facing = 0;
    facing = turnToward(facing, Math.PI, RATE * 0.017);
    for (let i = 0; i < 6; i++) {
      facing = turnToward(facing, 0, RATE * dt);
    }

    expect(facing).toBeCloseTo(0, 9);
  });

  it('snaps on a non-positive step, which is what a paused frame asks for', () => {
    expect(turnToward(0, 2, 0)).toBe(2);
    expect(turnToward(0, 2, -1)).toBe(2);
  });
});
