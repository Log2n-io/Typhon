import { describe, expect, it } from 'vitest';
import {
  STALL_THRESHOLD_MS,
  computeRate,
  deriveProgressDisplay,
  formatCount,
  formatElapsed,
  formatPercent,
  formatRate,
} from '../fixtureProgressDisplay';

/**
 * Tests the pure progress-formatting helpers — the maths that gives the DevFixture progress strip its "slow vs
 * stuck" diagnostic resolution. Each helper has the responsibility of one display field; the integration is
 * covered by `deriveProgressDisplay`. The whole reason this module exists is that `Math.round(0.4)` was
 * collapsing sub-percent progress to "0%" on large generations, hiding the slow-but-progressing case behind a
 * static-looking bar.
 */

describe('formatCount', () => {
  it('formats integers with locale thousand separators', () => {
    expect(formatCount(12345)).toMatch(/^12[,.]345$|^12345$/); // locale-dependent grouping char
    expect(formatCount(0)).toBe('0');
    expect(formatCount(1)).toBe('1');
  });

  it('clamps negative to 0 and floors fractional', () => {
    expect(formatCount(-5)).toBe('0');
    expect(formatCount(3.9)).toBe('3');
  });
});

describe('formatPercent', () => {
  it('returns 2-decimal precision so sub-percent progress is visible', () => {
    // The bug we're fixing: 12,345 / 3,200,000 ≈ 0.39%. The old `Math.round` rendered this as "0%".
    expect(formatPercent(12_345, 3_200_000)).toBe('0.39%');
  });

  it('preserves clean boundaries', () => {
    expect(formatPercent(0, 100)).toBe('0%');
    expect(formatPercent(100, 100)).toBe('100%');
    expect(formatPercent(200, 100)).toBe('100%'); // overshoot clamps to 100
  });

  it('returns null for indeterminate / invalid inputs (caller hides the label)', () => {
    expect(formatPercent(0, 0)).toBeNull();
    expect(formatPercent(10, -1)).toBeNull();
    expect(formatPercent(NaN, 100)).toBeNull();
    expect(formatPercent(10, NaN)).toBeNull();
  });

  it('keeps mid-range values readable', () => {
    expect(formatPercent(50, 100)).toBe('50.00%');
    expect(formatPercent(33, 100)).toBe('33.00%');
    expect(formatPercent(3_200_000, 8_000_000)).toBe('40.00%');
  });
});

describe('formatElapsed', () => {
  it('uses seconds-only under one minute', () => {
    expect(formatElapsed(0)).toBe('0s');
    expect(formatElapsed(500)).toBe('0s');
    expect(formatElapsed(4_000)).toBe('4s');
    expect(formatElapsed(59_999)).toBe('59s');
  });

  it('switches to "Xm 0Ys" after a minute (zero-padded seconds)', () => {
    expect(formatElapsed(60_000)).toBe('1m 00s');
    expect(formatElapsed(83_000)).toBe('1m 23s');
    expect(formatElapsed(724_000)).toBe('12m 04s');
  });

  it('switches to "Xh 0Ym" past an hour', () => {
    expect(formatElapsed(3_600_000)).toBe('1h 00m');
    expect(formatElapsed(3_720_000)).toBe('1h 02m');
  });

  it('treats invalid / negative as 0', () => {
    expect(formatElapsed(-100)).toBe('0s');
    expect(formatElapsed(NaN)).toBe('0s');
  });
});

describe('formatRate', () => {
  it('uses /s under 1000', () => {
    expect(formatRate(950)).toBe('950/s');
    expect(formatRate(1)).toBe('1/s');
  });

  it('uses k/s in the 1k-1M range, with smart precision', () => {
    expect(formatRate(1234)).toBe('1.2k/s');
    expect(formatRate(9999)).toBe('10.0k/s');
    expect(formatRate(12_345)).toBe('12k/s'); // ≥10k drops the decimal to keep the strip narrow
    expect(formatRate(150_000)).toBe('150k/s');
  });

  it('uses M/s past a million', () => {
    expect(formatRate(1_500_000)).toBe('1.5M/s');
    expect(formatRate(12_000_000)).toBe('12.0M/s');
  });

  it('returns null for non-positive / non-finite (caller hides the field)', () => {
    expect(formatRate(0)).toBeNull();
    expect(formatRate(-5)).toBeNull();
    expect(formatRate(NaN)).toBeNull();
    expect(formatRate(Infinity)).toBeNull();
  });
});

describe('computeRate', () => {
  it('waits for a warmup window before reporting (avoids divide-by-tiny-elapsed)', () => {
    expect(computeRate(5, 100)).toBeNull(); // 100 ms < 500 ms warmup
    expect(computeRate(5, 400)).toBeNull();
  });

  it('computes entities/sec after warmup', () => {
    // 1000 entities in 1 second → 1000/s
    expect(computeRate(1000, 1000)).toBe(1000);
    // 12,000 entities in 1 second → 12,000/s
    expect(computeRate(12_000, 1000)).toBe(12_000);
    // 3.2 M destroys in 30 s → ~107k/s
    expect(computeRate(3_200_000, 30_000)).toBeCloseTo(106_666.67, 0);
  });

  it('returns null for zero/negative completed', () => {
    expect(computeRate(0, 1000)).toBeNull();
    expect(computeRate(-1, 1000)).toBeNull();
  });
});

describe('deriveProgressDisplay — integration', () => {
  it('produces all four fields on the canonical slow-but-progressing case', () => {
    // Simulates the user's screenshot: 12,345 destroys completed out of 3,200,000, 30 seconds elapsed,
    // last poll updated completed 1 second ago (so NOT stalled — the bar IS moving).
    const d = deriveProgressDisplay({
      completed: 12_345,
      total: 3_200_000,
      genStartedAtMs: 1_000_000,
      lastCompletedChangeAtMs: 1_029_000,
      nowMs: 1_030_000,
    });
    expect(d.countLabel).toMatch(/12.?345.*3.?200.?000/); // locale-flex
    expect(d.percentLabel).toBe('0.39%'); // sub-percent visible!
    expect(d.elapsedLabel).toBe('30s');
    expect(d.rateLabel).not.toBeNull(); // 12345/30s ≈ 411/s
    expect(d.isStalled).toBe(false);
    expect(d.barWidthPct).toBeGreaterThan(0);
    expect(d.barWidthPct).toBeLessThan(1);
  });

  it('flags stalled when no forward movement in ≥ STALL_THRESHOLD_MS', () => {
    const d = deriveProgressDisplay({
      completed: 5_000,
      total: 1_000_000,
      genStartedAtMs: 1_000_000,
      lastCompletedChangeAtMs: 1_010_000, // last change was 10 s before nowMs
      nowMs: 1_010_000 + STALL_THRESHOLD_MS + 100,
    });
    expect(d.isStalled).toBe(true);
  });

  it('does NOT flag stalled before any progress (completed=0 — job is still starting)', () => {
    // If we flagged here, every fresh job would render "stalled" on its first frame. Wait for at least one
    // forward step before deciding.
    const d = deriveProgressDisplay({
      completed: 0,
      total: 1_000_000,
      genStartedAtMs: 1_000_000,
      lastCompletedChangeAtMs: 1_000_000,
      nowMs: 1_000_000 + STALL_THRESHOLD_MS + 100,
    });
    expect(d.isStalled).toBe(false);
  });

  it('handles indeterminate phases (total=0) with a fixed bar width + null percent', () => {
    const d = deriveProgressDisplay({
      completed: 0,
      total: 0,
      genStartedAtMs: 1_000_000,
      lastCompletedChangeAtMs: 1_000_000,
      nowMs: 1_005_000,
    });
    expect(d.percentLabel).toBeNull();
    expect(d.barWidthPct).toBe(20); // visual feedback for indeterminate work
    expect(d.countLabel).toBe('0'); // single-count form, no denominator
  });

  it('clamps bar width to [0, 100]', () => {
    const d = deriveProgressDisplay({
      completed: 1_500,
      total: 1_000, // overshoot by 50%
      genStartedAtMs: 1_000_000,
      lastCompletedChangeAtMs: 1_005_000,
      nowMs: 1_005_000,
    });
    expect(d.barWidthPct).toBe(100);
  });
});
