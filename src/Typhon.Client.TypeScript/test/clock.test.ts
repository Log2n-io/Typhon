import { describe, expect, it } from 'vitest';
import { Clock } from '../src/index.js';

import { readFileSync } from 'node:fs';
import { monotonicNow } from '../src/clock/now.js';

const read = (path: string): string => readFileSync(new URL(path, import.meta.url), 'utf8');
const connectionSource = read('../src/net/connection.ts');
const pingSource = read('../src/net/ping.ts');
const queueSource = read('../src/commands/queue.ts');
const regionSource = read('../src/interest/region.ts');
const reconnectSource = read('../src/net/reconnect.ts');

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

/**
 * The grid a sub-tick intent is quantised against.
 *
 * The server advances an entity by one whole period of whatever intent it held at the boundary, so a client that changes
 * velocity on a key event integrates for a different DURATION than the server does — by `startPhase - stopPhase`, up to a
 * full tick either way. Sending the phase lets the server split the boundary tick and close it exactly, which is what these
 * assert is possible from what the clock knows.
 */
describe('the tick grid', () => {
  const DELAY = 30;

  /** A clock fed `n` frames at a constant delay, so the grid is anchored and the offset is settled. */
  const anchored = (n = 12): Clock => {
    const clock = new Clock({ tickPeriodMs: PERIOD });
    for (let tick = 0; tick < n; tick++) {
      clock.onFrame(tick, sentAt(tick) + DELAY);
    }

    return clock;
  };

  it('is NaN before a frame has anchored it, rather than a confident zero', () => {
    // A zero would read as "the intent changed exactly on the boundary", which is a claim, and the caller would send it.
    expect(new Clock({ tickPeriodMs: PERIOD }).subTickPhase(5000)).toBeNaN();
  });

  it.each([0, 1, 17, 50, 99, 99.9])('reads an instant %i ms into a tick as that fraction', (into) => {
    const clock = anchored();
    expect(clock.subTickPhase(sentAt(7) + into, DELAY)).toBeCloseTo(into / PERIOD, 12);
  });

  it('is exactly 0 on a boundary, and wraps rather than reaching 1', () => {
    const clock = anchored();
    expect(clock.subTickPhase(sentAt(4), DELAY)).toBe(0);
    expect(clock.subTickPhase(sentAt(4) + PERIOD, DELAY)).toBe(0);
  });

  it('is short by the downlink when the estimate is left at 0 — the documented bias', () => {
    // Not a defect to fix here: `offsetMs` is the arrival of a tick's frame, so the grid this reads sits one downlink
    // behind the server's. Stated as a test because the size of it decides whether a caller needs the ping.
    const clock = anchored();
    expect(clock.subTickPhase(sentAt(7) + 50, 0)).toBeCloseTo((50 - DELAY) / PERIOD, 12);
  });

  it('follows a period change, so a server that slows down is still read on the right grid', () => {
    const clock = anchored();
    clock.onPeriodChange(12, 40);
    for (let tick = 12; tick < 20; tick++) {
      clock.onFrame(tick, sentAt(12) + (tick - 12) * 40 + DELAY);
    }

    // Tick 14 begins 80 ms after tick 12; 10 ms into it is a quarter of a 40 ms period, not a tenth of a 100 ms one.
    expect(clock.subTickPhase(sentAt(12) + 80 + 10, DELAY)).toBeCloseTo(0.25, 12);
  });

  /**
   * The claim the whole mechanic rests on, driven over the clock rather than over arithmetic: for a gesture held from an
   * arbitrary phase to an arbitrary phase, paying the old velocity for `phase` of the boundary tick and the new one for
   * the rest makes the server's distance equal `v x heldSeconds` — the client's own integration — at every phase.
   */
  it('lets a tick-stepping server reproduce the exact duration a key was held', () => {
    const clock = anchored(400);
    const v = 5;
    let seed = 20261014;
    const random = () => {
      seed = (seed * 1103515245 + 12345) % 2147483648;
      return seed / 2147483648;
    };

    let worst = 0;
    for (let trial = 0; trial < 500; trial++) {
      const downAt = sentAt(20) + random() * 2000;
      const upAt = downAt + 50 + random() * 1500;
      const phaseDown = clock.subTickPhase(downAt, DELAY);
      const phaseUp = clock.subTickPhase(upAt, DELAY);

      // The server: one whole step per tick drained, with the boundary tick of each transition split by the phase the
      // client sent. `tickOf` is the tick whose Input phase first sees the command.
      const tickOf = (atMs: number) => Math.ceil((atMs - 5000) / PERIOD);
      const first = tickOf(downAt);
      const last = tickOf(upAt);
      const step = (v * PERIOD) / 1000;
      const moved = step * (1 - phaseDown) + step * (last - first - 1) + step * phaseUp;

      worst = Math.max(worst, Math.abs(moved - (v * (upAt - downAt)) / 1000));
    }

    // Float noise only: a millimetre is 0.2 ms of travel, and the artefact this removes is up to 500 mm.
    expect(worst).toBeLessThan(1e-9);
  });
});

describe('the SDK default clock', () => {
  it('is monotonic where the host has one, and shares its origin with the renderer', () => {
    // The defect this guards: five parts of the SDK defaulted to `Date.now()` while the browser client feeds
    // `Clock.update` `performance.now()`. The two differ by the Unix epoch, ~1.79e12 ms, so the offset estimator put
    // render time about 1.8e10 ticks in the past and every moving entity was culled. The view still drew static
    // archetypes, so it looked like a replication bug rather than a clock one.
    const a = monotonicNow();
    const b = performance.now();
    expect(Math.abs(a - b)).toBeLessThan(1000);

    // Never goes backwards within a session, which `Date.now()` does on an NTP correction.
    let previous = monotonicNow();
    for (let i = 0; i < 1000; i++) {
      const now = monotonicNow();
      expect(now).toBeGreaterThanOrEqual(previous);
      previous = now;
    }
  });

  it('is what a connection, a ping scheduler, a command queue, a region sender and a reconnect all take by default', () => {
    // Asserted as one fact because the failure mode is a mix: any one of them left on another timeline reintroduces it.
    //
    // It reads the source text, which is weaker than driving each of the five — a behavioural version would have to reach
    // a private field or invent an observable for each. What it must not be is satisfiable by a class that takes the
    // default and then overwrites it, so the second assertion is that `this.now` is assigned EXACTLY once per file.
    for (const [name, source] of [
      ['connection', connectionSource],
      ['ping', pingSource],
      ['queue', queueSource],
      ['region', regionSource],
      ['reconnect', reconnectSource],
    ] as const) {
      expect(source, name).toContain('options.now ?? monotonicNow');
      expect(source.match(/this\.now\s*=/g) ?? [], name).toHaveLength(1);
    }
  });
});
