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
    expect(timeToTickRange({ start: 0, end: 5000 }, [])).toBeNull();
    expect(timeToTickRange({ start: 0, end: 5000 }, null)).toBeNull();
  });

  it('full window covers all ticks', () => {
    expect(timeToTickRange({ start: 0, end: 10000 }, FIVE_TICKS)).toEqual({ from: 1, to: 5 });
  });

  it('window matching exactly one tick startUs (inclusive start)', () => {
    // start at 2000 → tick 3 included; end at 3000 (exclusive) → tick 4 excluded.
    expect(timeToTickRange({ start: 2000, end: 3000 }, FIVE_TICKS)).toEqual({ from: 3, to: 3 });
  });

  it('end-exclusive boundary excludes the tick at end', () => {
    // tick 4 has startUs=3000. end=3000 means "up to but not including 3000".
    expect(timeToTickRange({ start: 0, end: 3000 }, FIVE_TICKS)).toEqual({ from: 1, to: 3 });
  });

  it('window before any tick returns null', () => {
    expect(timeToTickRange({ start: -1000, end: 0 }, FIVE_TICKS)).toBeNull();
  });

  it('window after all ticks returns null', () => {
    // ticks end startUs at 4000; window starts at 5000 → no tick has startUs >= 5000.
    expect(timeToTickRange({ start: 5000, end: 10000 }, FIVE_TICKS)).toBeNull();
  });

  it('partial overlap — start mid-tick still picks the next tick', () => {
    // start at 1500 (between tick 2 startUs=1000 and tick 3 startUs=2000) → tick 2 has startUs<1500
    // so it's excluded; tick 3 has startUs=2000 >= 1500 so it's the first match.
    expect(timeToTickRange({ start: 1500, end: 4000 }, FIVE_TICKS)).toEqual({ from: 3, to: 4 });
  });

  it('zero-width window finds nothing', () => {
    expect(timeToTickRange({ start: 1000, end: 1000 }, FIVE_TICKS)).toBeNull();
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
    expect(lastNTicksToTime(3, FIVE_TICKS)).toEqual({ start: 2000, end: 4800 });
  });

  it('roundtrips through timeToTickRange to the same tick range', () => {
    // Snapshot last 3 ticks → time selection → back to tick range = ticks 3..5.
    const time = lastNTicksToTime(3, FIVE_TICKS);
    expect(time).not.toBeNull();
    expect(timeToTickRange(time, FIVE_TICKS)).toEqual({ from: 3, to: 5 });
  });

  it('clamps when fewer than N ticks exist', () => {
    expect(lastNTicksToTime(100, FIVE_TICKS)).toEqual({ start: 0, end: 4800 });
  });

  it('zero-duration last tick still includes it via +1 µs epsilon', () => {
    // If the last tick has durationUs == 0 (rare), end = startUs + 1 ensures the strict-less
    // comparator in timeToTickRange still includes it on roundtrip.
    const ticks = [tick(1, 0, 800), tick(2, 1000, 0)];
    const time = lastNTicksToTime(1, ticks);
    expect(time).toEqual({ start: 1000, end: 1001 });
    expect(timeToTickRange(time, ticks)).toEqual({ from: 2, to: 2 });
  });

  it('roundtrips on a single-tick capture', () => {
    const time = lastNTicksToTime(1, FIVE_TICKS);
    expect(timeToTickRange(time, FIVE_TICKS)).toEqual({ from: 5, to: 5 });
  });
});
