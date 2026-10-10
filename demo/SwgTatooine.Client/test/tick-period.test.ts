import { describe, expect, it } from 'vitest';

import { segmentHistoryFor } from '@typhondb/client';

import { FASTEST_TICK_PERIOD_MS, SWG_SCHEMA, TICK_PERIOD_MS } from '../src/data/swg-schema';

/**
 * The client must not assume the server's tick rate.
 *
 * `--hz` is a server flag, and everything the client does with time is derived from the period: the clock maps ticks to
 * instants with it, and the world store sizes its segment ring with it. Both were taken from a constant that said 10 Hz,
 * so a server at 25 Hz produced a timeline running 2.5x slow and a ring too short to hold the history render time needed —
 * which looks like dropped messages and is not.
 */

/** The SDK's own rule, restated so a change to it fails here rather than in a viewport. */
const needed = (periodMs: number, maxRenderDelayMs: number): number =>
  Math.min(255, Math.max(4, Math.ceil((maxRenderDelayMs * 1000) / (periodMs * 1000)) + 2));

describe('the segment ring', () => {
  /** 300 ms is the clock's delay ceiling, which is the most history that can ever be asked for. */
  const MAX_RENDER_DELAY_MS = 300;

  it('is sized from the fastest supported tick, not the default one', () => {
    expect(SWG_SCHEMA.tickPeriodUs).toBe(FASTEST_TICK_PERIOD_MS * 1000);
    expect(FASTEST_TICK_PERIOD_MS).toBeLessThan(TICK_PERIOD_MS);
  });

  it.each([10, 15, 20, 25, 30, 50])('holds enough history for a server at %i Hz', (hz) => {
    const have = segmentHistoryFor(SWG_SCHEMA.tickPeriodUs, MAX_RENDER_DELAY_MS);
    expect(have).toBeGreaterThanOrEqual(needed(1000 / hz, MAX_RENDER_DELAY_MS));
  });

  /**
   * The failure this guards: sized from the 10 Hz default, a 25 Hz server needs ten segments and gets five, so the ring
   * overwrites the segment render time is about to read.
   */
  it('would NOT have held enough when sized from the default period', () => {
    const sizedFromDefault = segmentHistoryFor(TICK_PERIOD_MS * 1000, MAX_RENDER_DELAY_MS);
    expect(sizedFromDefault).toBeLessThan(needed(1000 / 25, MAX_RENDER_DELAY_MS));
  });
});
