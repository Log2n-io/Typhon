import { describe, expect, it } from 'vitest';
import type { TickSummaryDto } from '@/api/generated/model/tickSummaryDto';
import { lastNTicksToTime, timeToTickRange } from '../tickRangeMapping';

/**
 * Boundary-case fixtures for the µs↔tick-range converter. These guarantees matter for the
 * cross-panel binding: a stale or off-by-one selection in the profiler's TimeArea must not
 * smear an extra tick into a DAG aggregation, and an empty roundtrip must not fire spurious
 * /aggregate queries.
 */

function tick(tickNumber: number, startUs: number, durationUs: number): TickSummaryDto {
  return {
    tickNumber,
    startUs,
    durationUs,
    eventCount: 0,
    maxSystemDurationUs: 0,
    activeSystemsBitmask: '0',
    overloadLevel: 0,
    tickMultiplier: 0,
    metronomeWaitUs: 0,
    metronomeIntentClass: 0,
    consecutiveOverrun: 0,
    consecutiveUnderrun: 0,
  } as unknown as TickSummaryDto;
}

// 5 ticks at 1000 µs apart, each 800 µs long.  startUs: 0, 1000, 2000, 3000, 4000.
const FIVE_TICKS: TickSummaryDto[] = [
  tick(1, 0, 800),
  tick(2, 1000, 800),
  tick(3, 2000, 800),
  tick(4, 3000, 800),
  tick(5, 4000, 800),
];

describe('timeToTickRange', () => {
  it('returns null on null time', () => {
    expect(timeToTickRange(null, FIVE_TICKS)).toBeNull();
  });

  it('returns null on empty tick array', () => {
    expect(timeToTickRange({ startUs: 0, endUs: 5000 }, [])).toBeNull();
    expect(timeToTickRange({ startUs: 0, endUs: 5000 }, null)).toBeNull();
  });

  it('full window covers all ticks', () => {
    expect(timeToTickRange({ startUs: 0, endUs: 10000 }, FIVE_TICKS)).toEqual({ from: 1, to: 5 });
  });

  it('window matching exactly one tick startUs (inclusive start)', () => {
    // start at 2000 → tick 3 included; end at 3000 (exclusive) → tick 4 excluded.
    expect(timeToTickRange({ startUs: 2000, endUs: 3000 }, FIVE_TICKS)).toEqual({ from: 3, to: 3 });
  });

  it('end-exclusive boundary excludes the tick at end', () => {
    // tick 4 has startUs=3000. end=3000 means "up to but not including 3000".
    expect(timeToTickRange({ startUs: 0, endUs: 3000 }, FIVE_TICKS)).toEqual({ from: 1, to: 3 });
  });

  it('window before any tick returns null', () => {
    expect(timeToTickRange({ startUs: -1000, endUs: 0 }, FIVE_TICKS)).toBeNull();
  });

  it('window after all ticks returns null', () => {
    // ticks end startUs at 4000; window starts at 5000 → no tick has startUs >= 5000.
    expect(timeToTickRange({ startUs: 5000, endUs: 10000 }, FIVE_TICKS)).toBeNull();
  });

  it('partial overlap — start mid-tick includes the enclosing tick', () => {
    // start at 1500 falls inside tick 2's [1000, 1800) interval. Overlap semantics include tick 2
    // because its right edge crosses the window's left edge. tick 4 (startUs=3000) is included by
    // the window's right edge < 4000.
    expect(timeToTickRange({ startUs: 1500, endUs: 4000 }, FIVE_TICKS)).toEqual({ from: 2, to: 4 });
  });

  it('zoom strictly inside a tick snaps to that tick', () => {
    // Window [2300, 2500) is fully inside tick 3's [2000, 2800). Overlap → just tick 3.
    expect(timeToTickRange({ startUs: 2300, endUs: 2500 }, FIVE_TICKS)).toEqual({ from: 3, to: 3 });
  });

  it('zoom into the gap between two ticks falls back to the previous overlapping tick', () => {
    // Tick 1 ends at 800; tick 2 starts at 1000. Window [810, 900) lies in the gap. No tick has
    // (endUs > 810 AND startUs < 900) AT THE SAME TIME — tick 2 ends at 1800 > 810 ✓ but its
    // startUs=1000 is not < 900. So no overlap → null.
    expect(timeToTickRange({ startUs: 810, endUs: 900 }, FIVE_TICKS)).toBeNull();
  });

  it('zero-width window at a tick boundary finds nothing', () => {
    // Boundary 1000 between tick 1 (ends at 800) and tick 2 (starts at 1000). Both half-open
    // intervals exclude their respective edges, so no overlap.
    expect(timeToTickRange({ startUs: 1000, endUs: 1000 }, FIVE_TICKS)).toBeNull();
  });

  it('zero-width window strictly inside a tick maps to that tick', () => {
    // A point at 1500 lies inside tick 2's [1000, 1800). endUs=1800 > 1500 ✓ and startUs=1000 < 1500 ✓.
    expect(timeToTickRange({ startUs: 1500, endUs: 1500 }, FIVE_TICKS)).toEqual({ from: 2, to: 2 });
  });
});

describe('lastNTicksToTime', () => {
  it('returns null on empty / non-positive', () => {
    expect(lastNTicksToTime(0, FIVE_TICKS)).toBeNull();
    expect(lastNTicksToTime(-5, FIVE_TICKS)).toBeNull();
    expect(lastNTicksToTime(5, [])).toBeNull();
    expect(lastNTicksToTime(5, null)).toBeNull();
  });

  it('window covers exactly the last N ticks', () => {
    // last 3 ticks: tick 3 (startUs=2000), tick 4, tick 5 (startUs=4000, durationUs=800).
    // start=2000, end=4000+800=4800.
    expect(lastNTicksToTime(3, FIVE_TICKS)).toEqual({ startUs: 2000, endUs: 4800 });
  });

  it('roundtrips through timeToTickRange to the same tick range', () => {
    // Snapshot last 3 ticks → time selection → back to tick range = ticks 3..5.
    const time = lastNTicksToTime(3, FIVE_TICKS);
    expect(time).not.toBeNull();
    expect(timeToTickRange(time, FIVE_TICKS)).toEqual({ from: 3, to: 5 });
  });

  it('clamps when fewer than N ticks exist', () => {
    expect(lastNTicksToTime(100, FIVE_TICKS)).toEqual({ startUs: 0, endUs: 4800 });
  });

  it('zero-duration last tick still includes it via +1 µs epsilon', () => {
    // If the last tick has durationUs == 0 (rare), end = startUs + 1 ensures the strict-less
    // comparator in timeToTickRange still includes it on roundtrip.
    const ticks = [tick(1, 0, 800), tick(2, 1000, 0)];
    const time = lastNTicksToTime(1, ticks);
    expect(time).toEqual({ startUs: 1000, endUs: 1001 });
    expect(timeToTickRange(time, ticks)).toEqual({ from: 2, to: 2 });
  });

  it('roundtrips on a single-tick capture', () => {
    const time = lastNTicksToTime(1, FIVE_TICKS);
    expect(timeToTickRange(time, FIVE_TICKS)).toEqual({ from: 5, to: 5 });
  });

  it('single-tick capture survives a mid-tick zoom — the user-facing bug guard', () => {
    // Regression for "zoom inside a tick → DF view shows nothing": after taking the last-N-ticks
    // snapshot, a user zooming the time selection to a window strictly inside that tick used to
    // produce `null` from timeToTickRange (no tick's startUs in the window) and the panel rendered
    // a blank canvas with a garbled X axis. With overlap semantics, any sub-window of the captured
    // tick still resolves to that one tick.
    const tickOnly = [tick(7, 10_000, 800)];
    const captured = lastNTicksToTime(1, tickOnly);
    expect(captured).toEqual({ startUs: 10_000, endUs: 10_800 });
    // Mid-tick zoom — window is fully inside [10_000, 10_800).
    expect(timeToTickRange({ startUs: 10_200, endUs: 10_400 }, tickOnly)).toEqual({ from: 7, to: 7 });
    // Zoom touching only the right half.
    expect(timeToTickRange({ startUs: 10_500, endUs: 10_799 }, tickOnly)).toEqual({ from: 7, to: 7 });
    // Zero-width point inside the tick.
    expect(timeToTickRange({ startUs: 10_500, endUs: 10_500 }, tickOnly)).toEqual({ from: 7, to: 7 });
  });
});
