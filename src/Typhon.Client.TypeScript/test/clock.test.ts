import { describe, expect, it } from 'vitest';
import { Clock } from '../src/index.js';

const PERIOD = 100;

/** Server time of a tick in the tests' constant-period world: tick 0 is sent at local time 5 000. */
const sentAt = (tick: number) => 5000 + tick * PERIOD;

describe('Clock', () => {
  it('does nothing before the first frame', () => {
    const clock = new Clock({ tickPeriodMs: PERIOD });
    clock.update(1000);
    expect(clock.renderTick).toBe(0);
    expect(clock.latestTick).toBe(-1);
  });

  it('settles exactly one render delay behind the least-delayed frame at 60 fps', () => {
    const clock = new Clock({ tickPeriodMs: PERIOD, initialDelayMs: 200 });
    let next = 0;
    let worstLead = 0;
    for (let now = 5000; now < 11_000; now += 1000 / 60) {
      while (sentAt(next) + 30 <= now) {
        clock.onFrame(next, sentAt(next) + 30);
        next++;
      }

      clock.update(now);
      // The delay drops from its initial 200 ms to 150 ms once jitter is measurable; ±5 % closes that in about 2.5 s.
      if (now > 8500) {
        // Constant 30 ms delay, no jitter: delay settles at max(150, period + 0) = 150; target = now − 5 030 − 150.
        const targetTicks = (now - 5030 - clock.renderDelayMs) / PERIOD;
        worstLead = Math.max(worstLead, Math.abs(clock.renderTime - targetTicks));
      }
    }

    expect(clock.renderDelayMs).toBe(150);
    expect(worstLead * PERIOD).toBeLessThan(1);
  });

  it('never goes backwards and stays smooth under jitter', () => {
    const clock = new Clock({ tickPeriodMs: PERIOD });
    let next = 0;
    let previous = -Infinity;
    let seed = 7;
    const random = () => {
      seed = (seed * 1103515245 + 12345) % 2147483648;
      return seed / 2147483648;
    };
    const arrivals: number[] = [];
    let last = 0;
    for (let t = 0; t < 400; t++) {
      last = Math.max(last, sentAt(t) + 20 + random() * 60);
      arrivals.push(last);
    }

    for (let now = 5000; now < 5000 + 38_000; now += 1000 / 60) {
      while (next < arrivals.length && arrivals[next]! <= now) {
        clock.onFrame(next, arrivals[next]!);
        next++;
      }

      clock.update(now);
      if (clock.latestTick >= 0) {
        expect(clock.renderTime).toBeGreaterThanOrEqual(previous);
        previous = clock.renderTime;
      }
    }
  });

  it('takes the nearest-rank p95: a single outlier among twenty samples does not set the delay', () => {
    const clock = new Clock({ tickPeriodMs: PERIOD, minDelayMs: 100, maxDelayMs: 300 });
    for (let t = 0; t < 20; t++) {
      clock.onFrame(t, sentAt(t) + (t === 10 ? 160 : 10));
    }

    expect(clock.renderDelayMs).toBe(PERIOD);
  });

  it('clamps the delay into its bounds', () => {
    const noisy = new Clock({ tickPeriodMs: PERIOD, minDelayMs: 150, maxDelayMs: 300 });
    for (let t = 0; t < 20; t++) {
      noisy.onFrame(t, sentAt(t) + (t % 2 === 0 ? 900 : 0));
    }

    expect(noisy.renderDelayMs).toBe(300);
  });

  it('holds instead of jumping back when the first frame after a stall looks late', () => {
    const clock = new Clock({ tickPeriodMs: PERIOD, initialDelayMs: 200 });
    let now: number;
    for (let t = 0; t < 30; t++) {
      now = sentAt(t) + 10;
      clock.onFrame(t, now);
      clock.update(now);
    }

    const before = clock.renderTime;
    // A 3 s stall: frames 30..59 all arrive at once, 20 ms apart, the first one alone in the window.
    now = sentAt(59) + 10;
    clock.onFrame(30, now);
    clock.update(now);
    expect(clock.renderTime).toBeGreaterThanOrEqual(before);
    for (let t = 31; t < 60; t++) {
      now += 20;
      clock.onFrame(t, now);
      clock.update(now);
      expect(clock.renderTime).toBeGreaterThanOrEqual(before);
    }
  });

  it('holds when frames stop arriving, one second past the newest frame', () => {
    const clock = new Clock({ tickPeriodMs: PERIOD, initialDelayMs: 200, maxExtrapolationMs: 1000 });
    let now = 5000;
    for (let t = 0; t <= 4; t++) {
      now = sentAt(t);
      clock.onFrame(t, now);
    }

    clock.update(now);
    for (let k = 1; k <= 200; k++) {
      clock.update(now + k * 16);
    }

    expect(clock.renderTick).toBe(14);
    expect(clock.renderFrac).toBe(0);
  });

  it('does not stutter on keepalive-only traffic at 60 fps', () => {
    const clock = new Clock({ tickPeriodMs: PERIOD });
    let next = 0;
    let frozenFrames = 0;
    let previous = -1;
    for (let now = 5000; now < 15_000; now += 1000 / 60) {
      // Only every fifth tick carries a frame (a header-only keepalive every 500 ms).
      while (sentAt(next) <= now) {
        clock.onFrame(next, sentAt(next));
        next += 5;
      }

      clock.update(now);
      if (now > 7000 && clock.renderTime === previous) {
        frozenFrames++;
      }

      previous = clock.renderTime;
    }

    expect(frozenFrames).toBe(0);
  });

  it('keeps the old period before a change and the new one after it, with no drift', () => {
    const clock = new Clock({ tickPeriodMs: PERIOD, initialDelayMs: 200, minDelayMs: 200, maxDelayMs: 200 });
    // Ticks 0..49 at 100 ms, then ticks 50.. at 200 ms (time dilation). Server time of tick 50 is 5 000 ms.
    const serverAt = (tick: number) => (tick <= 50 ? tick * 100 : 5000 + (tick - 50) * 200);
    let next = 0;
    let changed = false;
    let worst = 0;
    for (let now = 0; now < 20_000; now += 1000 / 60) {
      while (serverAt(next) <= now) {
        if (next === 50 && !changed) {
          clock.onPeriodChange(50, 200);
          changed = true;
        }

        clock.onFrame(next, serverAt(next));
        next++;
      }

      clock.update(now);
      if (now > 1000) {
        const renderServerMs = now - 200;
        const expected = renderServerMs <= 5000 ? renderServerMs / 100 : 50 + (renderServerMs - 5000) / 200;
        worst = Math.max(worst, Math.abs(clock.renderTime - expected));
      }
    }

    expect(clock.tickPeriodMs).toBe(200);
    expect(worst).toBeLessThan(0.02);
  });

  it('forgets everything on reset', () => {
    const clock = new Clock({ tickPeriodMs: PERIOD, initialDelayMs: 200 });
    for (let t = 0; t < 20; t++) {
      clock.onFrame(t, sentAt(t) + (t % 2 === 0 ? 150 : 0));
    }

    clock.onPeriodChange(20, 50);
    clock.reset();
    expect(clock.latestTick).toBe(-1);
    expect(clock.renderDelayMs).toBe(200);
    expect(clock.tickPeriodMs).toBe(PERIOD);
    clock.onFrame(100, 1000);
    clock.update(1000);
    expect(clock.renderTime).toBeCloseTo(98, 9);
  });
});
