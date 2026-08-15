import { describe, expect, it } from 'vitest';
import {
  BAR_LEFT_PAD,
  BAR_WIDTH,
  GAP_BAND_WIDTH,
  barsThatFit,
  buildTickRows,
  computeBarOffsets,
  hitTestTick,
  isRecordedTick,
  missingTicksBefore,
  recordedTicksOnly,
  type TickSummaryLike,
} from '../tickOverview';

/**
 * #805 — the overview must draw only the ticks a cherry-pick capture actually recorded.
 *
 * A skipped tick still produces a summary: the capture filter forwards a per-tick skeleton (TickStart/TickEnd, the
 * metronome wait, the overload counters, the gauge snapshot) so tick numbering stays absolute across the windows the
 * operator never armed. Those summaries carry no system activity, so a bar drawn for one is a bar you can click to
 * see nothing.
 */
function tick(over: Partial<TickSummaryLike> & { tickNumber: number }): TickSummaryLike {
  return {
    startUs: 0,
    durationUs: 1000,
    eventCount: 5,
    activeSystemsBitmask: '0',
    maxSystemDurationUs: 0,
    ...over,
  } as TickSummaryLike;
}

describe('isRecordedTick', () => {
  it('rejects a skeleton tick — no systems ran', () => {
    expect(isRecordedTick(tick({ tickNumber: 512 }))).toBe(false);
  });

  it('accepts a tick with an active-systems bitmask', () => {
    expect(isRecordedTick(tick({ tickNumber: 512, activeSystemsBitmask: '6' }))).toBe(true);
  });

  it('accepts a tick whose systems ran but sit past bit 63 — bitmask 0, duration > 0', () => {
    // The builder only ORs system indices below 64, so a wider DAG reports 0 for a tick that genuinely ran.
    expect(isRecordedTick(tick({ tickNumber: 512, activeSystemsBitmask: '0', maxSystemDurationUs: 42 }))).toBe(true);
  });

  it('does not special-case tick 0 — whether the pre-tick window is worth showing depends on its neighbour', () => {
    expect(isRecordedTick(tick({ tickNumber: 0, eventCount: 200_000 }))).toBe(false);
  });

  it('keeps a summary that carries neither field — absence of evidence must not blank the strip', () => {
    const minimal = { tickNumber: 7, startUs: 0, durationUs: 10, eventCount: 3 } as TickSummaryLike;
    expect(isRecordedTick(minimal)).toBe(true);
  });

  it('treats a null bitmask as no information rather than as zero', () => {
    const s = { tickNumber: 7, startUs: 0, durationUs: 10, eventCount: 3, activeSystemsBitmask: null } as TickSummaryLike;
    expect(isRecordedTick(s)).toBe(true);
  });

  it('does not lose a huge u64 bitmask to float precision', () => {
    expect(isRecordedTick(tick({ tickNumber: 9, activeSystemsBitmask: '18446744073709551615' }))).toBe(true);
  });
});

describe('recordedTicksOnly', () => {
  it('keeps two armed windows and drops the skeleton between them', () => {
    const summaries: TickSummaryLike[] = [
      tick({ tickNumber: 100, activeSystemsBitmask: '3' }),
      tick({ tickNumber: 101, activeSystemsBitmask: '3' }),
      tick({ tickNumber: 102 }),
      tick({ tickNumber: 103 }),
      tick({ tickNumber: 104 }),
      tick({ tickNumber: 105, activeSystemsBitmask: '3' }),
    ];

    const kept = recordedTicksOnly(summaries);
    expect(kept?.map((s) => Number(s.tickNumber))).toEqual([100, 101, 105]);
  });

  it('keeps the pre-tick window when the run starts at tick 1 — it abuts what is shown', () => {
    const summaries: TickSummaryLike[] = [
      tick({ tickNumber: 0, eventCount: 200_000 }),
      tick({ tickNumber: 1, activeSystemsBitmask: '3' }),
      tick({ tickNumber: 2, activeSystemsBitmask: '3' }),
    ];

    expect(recordedTicksOnly(summaries)?.map((s) => Number(s.tickNumber))).toEqual([0, 1, 2]);
  });

  /**
   * The reported case: a cherry-picked window recorded from tick 133. The tick-0 stub is not the setup window for
   * anything on screen, and keeping it drags a 132-tick separator onto the strip beside it.
   */
  it('drops the pre-tick window when the recording starts far later', () => {
    const summaries: TickSummaryLike[] = [
      tick({ tickNumber: 0, eventCount: 12 }),
      tick({ tickNumber: 133, activeSystemsBitmask: '3' }),
      tick({ tickNumber: 134, activeSystemsBitmask: '3' }),
    ];

    const kept = recordedTicksOnly(summaries);
    expect(kept?.map((s) => Number(s.tickNumber))).toEqual([133, 134]);
    // And with tick 0 gone there is no jump at the head of the strip, so no separator is drawn before the first bar.
    expect(missingTicksBefore(buildTickRows(kept), 1)).toBe(0);
  });

  it('keeps a tick 0 that genuinely ran systems, wherever it sits', () => {
    const summaries: TickSummaryLike[] = [
      tick({ tickNumber: 0, activeSystemsBitmask: '3' }),
      tick({ tickNumber: 133, activeSystemsBitmask: '3' }),
    ];

    expect(recordedTicksOnly(summaries)?.map((s) => Number(s.tickNumber))).toEqual([0, 133]);
  });

  it('returns the SAME array when nothing is filtered, so useMemo consumers keep referential identity', () => {
    const summaries: TickSummaryLike[] = [
      tick({ tickNumber: 1, activeSystemsBitmask: '1' }),
      tick({ tickNumber: 2, activeSystemsBitmask: '1' }),
    ];
    expect(recordedTicksOnly(summaries)).toBe(summaries);
  });

  it('passes null and empty through unchanged', () => {
    expect(recordedTicksOnly(null)).toBeNull();
    expect(recordedTicksOnly(undefined)).toBeUndefined();
    expect(recordedTicksOnly([])).toEqual([]);
  });
});

describe('missingTicksBefore — the discontinuity marker', () => {
  const rows = buildTickRows([
    tick({ tickNumber: 1, startUs: 0, activeSystemsBitmask: '1' }),
    tick({ tickNumber: 2, startUs: 1000, activeSystemsBitmask: '1' }),
    tick({ tickNumber: 4, startUs: 2000, activeSystemsBitmask: '1' }),
    tick({ tickNumber: 8, startUs: 3000, activeSystemsBitmask: '1' }),
  ]);

  it('reports no gap for consecutive ticks', () => {
    expect(missingTicksBefore(rows, 1)).toBe(0);
  });

  it('counts a single skipped tick', () => {
    expect(missingTicksBefore(rows, 2)).toBe(1); // 2 → 4 skips tick 3
  });

  it('counts a multi-tick gap', () => {
    expect(missingTicksBefore(rows, 3)).toBe(3); // 4 → 8 skips 5, 6, 7
  });

  it('reports nothing before the first bar — there is no earlier tick to jump from', () => {
    expect(missingTicksBefore(rows, 0)).toBe(0);
  });

  it('is bounds-safe past the end', () => {
    expect(missingTicksBefore(rows, 99)).toBe(0);
  });

  it('never invents a gap from a non-ascending list', () => {
    const descending = buildTickRows([
      tick({ tickNumber: 9, startUs: 0, activeSystemsBitmask: '1' }),
      tick({ tickNumber: 4, startUs: 1000, activeSystemsBitmask: '1' }),
    ]);
    expect(missingTicksBefore(descending, 1)).toBe(0);
  });
});

describe('computeBarOffsets / hitTestTick — the band takes real space', () => {
  // Ticks 1,2,4,8 → bands before the 3rd bar (2→4) and the 4th (4→8).
  const rows = buildTickRows([
    tick({ tickNumber: 1, startUs: 0, activeSystemsBitmask: '1' }),
    tick({ tickNumber: 2, startUs: 1000, activeSystemsBitmask: '1' }),
    tick({ tickNumber: 4, startUs: 2000, activeSystemsBitmask: '1' }),
    tick({ tickNumber: 8, startUs: 3000, activeSystemsBitmask: '1' }),
  ]);

  it('inserts a band width before each discontinuity, shifting the following bars', () => {
    expect(computeBarOffsets(rows, 0, 4)).toEqual([
      0,
      BAR_WIDTH,
      BAR_WIDTH * 2 + GAP_BAND_WIDTH,
      BAR_WIDTH * 3 + GAP_BAND_WIDTH * 2,
    ]);
  });

  it('reserves no band before the first visible bar — there is no visible neighbour to separate', () => {
    // Window starts ON the discontinuity at index 2; it must not be pushed right by a band with nothing to its left.
    expect(computeBarOffsets(rows, 2, 4)).toEqual([0, BAR_WIDTH + GAP_BAND_WIDTH]);
  });

  it('hit-tests bars through the shifted layout', () => {
    const offsets = computeBarOffsets(rows, 0, 4);
    const at = (x: number) => hitTestTick(BAR_LEFT_PAD + x, 500, { startIdx: 0, endIdx: 4 }, offsets);

    expect(at(0)).toBe(0);
    expect(at(BAR_WIDTH + 1)).toBe(1);
    // Without the offset table this x divides to bar 2 and would select the wrong tick — silently, because the
    // wrong tick is a perfectly plausible one.
    expect(at(BAR_WIDTH * 2 + GAP_BAND_WIDTH + 1)).toBe(2);
    expect(at(BAR_WIDTH * 3 + GAP_BAND_WIDTH * 2 + 1)).toBe(3);
  });

  it('returns -1 inside a band — there is no tick there', () => {
    const offsets = computeBarOffsets(rows, 0, 4);
    const insideBand = BAR_WIDTH * 2 + 2; // between bar 1's end and bar 2's start
    expect(hitTestTick(BAR_LEFT_PAD + insideBand, 500, { startIdx: 0, endIdx: 4 }, offsets)).toBe(-1);
  });

  it('returns -1 past the last bar', () => {
    const offsets = computeBarOffsets(rows, 0, 4);
    const past = BAR_WIDTH * 4 + GAP_BAND_WIDTH * 2 + 1;
    expect(hitTestTick(BAR_LEFT_PAD + past, 500, { startIdx: 0, endIdx: 4 }, offsets)).toBe(-1);
  });
});

describe('barsThatFit', () => {
  const rows = buildTickRows([
    tick({ tickNumber: 1, startUs: 0, activeSystemsBitmask: '1' }),
    tick({ tickNumber: 2, startUs: 1000, activeSystemsBitmask: '1' }),
    tick({ tickNumber: 9, startUs: 2000, activeSystemsBitmask: '1' }),
  ]);

  it('counts the bands against the budget, so bars are not clipped off the right edge', () => {
    // Two bars fit in 2×BAR_WIDTH. The third needs a band as well, so it needs BAR_WIDTH + GAP_BAND_WIDTH more.
    expect(barsThatFit(rows, 0, BAR_WIDTH * 2)).toBe(2);
    expect(barsThatFit(rows, 0, BAR_WIDTH * 3)).toBe(2);
    expect(barsThatFit(rows, 0, BAR_WIDTH * 3 + GAP_BAND_WIDTH)).toBe(3);
  });

  it('never reports more bars than exist, and none for no space', () => {
    expect(barsThatFit(rows, 0, 10_000)).toBe(3);
    expect(barsThatFit(rows, 0, 0)).toBe(0);
  });
});

describe('buildTickRows over a filtered list', () => {
  /**
   * `buildTickRows` clamps each bar's end to the NEXT summary's start to absorb wire float drift. Across a gap between
   * two armed windows that next start is far away, so the clamp must not apply — a bar has to keep its own duration
   * rather than stretch across the ticks that were skipped.
   */
  it('does not stretch a bar across the gap left by skipped ticks', () => {
    const summaries: TickSummaryLike[] = [
      tick({ tickNumber: 100, startUs: 0, durationUs: 100, activeSystemsBitmask: '3' }),
      tick({ tickNumber: 900, startUs: 1_000_000, durationUs: 100, activeSystemsBitmask: '3' }),
    ];

    const rows = buildTickRows(recordedTicksOnly(summaries));
    expect(rows).toHaveLength(2);
    expect(rows[0].endUs).toBe(100);
    expect(rows[1].startUs).toBe(1_000_000);
  });
});
