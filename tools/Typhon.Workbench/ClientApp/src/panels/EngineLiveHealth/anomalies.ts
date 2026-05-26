import type { TickData } from '@/libs/profiler/model/traceModel';

/**
 * Anomaly model + detection heuristics for Engine Live Health (#377 Stage 4 Phase 3, GAP-21 jump).
 *
 * **Scope** (per the P3 plan): client-side heuristics only, against data already in the chunk cache.
 * Two kinds: `tick-duration` outliers and `gc-pause` events above a threshold. Overload + queue
 * spikes need engine-API additions (`overloadLevel` byte not yet materialized into `TickData`;
 * queue-depth gauge not yet exposed) — they land later. See stage-4-observe.md §7 risks.
 *
 * **Why pure** (no React, no store imports): the detector is the heart of the value, and keeping it
 * pure means a single-pass test covers the contract — synthetic ticks in, expected anomalies out —
 * without needing a renderer, a store, or async. The wrapping `useAnomalyDetection` hook handles the
 * cache → store wiring.
 */

/** Discriminated union over the two kinds. */
export type Anomaly =
  | {
      kind: 'tick-duration';
      tickNumber: number;
      /** Tick start in absolute trace µs — feeds the time-jump scope window. */
      startUs: number;
      /** Tick end in absolute trace µs. */
      endUs: number;
      /** Tick duration in µs (= `endUs - startUs` but cached for ergonomics). */
      durationUs: number;
      /** Ratio of `durationUs` to the baseline p95. ≥ k when the anomaly is reported. */
      magnitude: number;
      /** Pre-rendered short description for the log row (no inline computation in the renderer). */
      details: string;
    }
  | {
      kind: 'gc-pause';
      tickNumber: number;
      startUs: number;
      endUs: number;
      /** Total GC pause µs *within this tick* — sum of `gcSuspensions[*].durationUs`. */
      totalPauseUs: number;
      /** Ratio of `totalPauseUs` to the threshold (≥ 1 when reported). */
      magnitude: number;
      /** How many distinct GC suspension events landed in this tick (often 1, can be more). */
      eventCount: number;
      details: string;
    };

export interface DetectThresholds {
  /**
   * Tick-duration anomaly fires when `durationUs > baselineP95 * tickDurationMultiplier`. Higher k
   * → fewer false positives, more missed real spikes. Default 3× picked from the design's stated
   * heuristic ("overload > 2× baseline" — we err on the side of fewer alarms for tick duration,
   * since the tick variance on a healthy engine can already be 2× routinely).
   */
  tickDurationMultiplier: number;
  /**
   * Minimum number of ticks required in the window before any tick-duration anomaly is reported.
   * Below this, the baseline p95 is too noisy to be meaningful (e.g., 2 ticks → p95 = the larger of
   * the two, which is the same as the candidate). Default 10.
   */
  minSampleSize: number;
  /**
   * Minimum total GC pause µs *within a single tick* to flag a `gc-pause` anomaly. Default 16 ms —
   * matches the stage-4 design's "GC > 16 ms" example. Below this, GC is normal background activity.
   */
  gcPauseThresholdUs: number;
}

export const DEFAULT_THRESHOLDS: DetectThresholds = {
  tickDurationMultiplier: 3,
  minSampleSize: 10,
  gcPauseThresholdUs: 16_000,
};

/**
 * Walk `ticks` once and emit any anomaly fired by the heuristics. O(n) — the baseline p95 is the
 * dominant cost (one sort of an O(n) array; we cap it at ~3600 ticks/hour at 1 Hz, fine).
 *
 * Anomalies are returned in `tickNumber` ascending order (the same order they were observed). The
 * UI sorts descending by tickNumber for display ("most recent first") — kept here as observation
 * order so other consumers (e.g., a tests-by-tick-number assertion) read naturally.
 */
export function detectAnomalies(ticks: readonly TickData[], thresholds: DetectThresholds = DEFAULT_THRESHOLDS): Anomaly[] {
  const out: Anomaly[] = [];
  if (ticks.length === 0) return out;

  // Baseline p95 of tick duration — gates the tick-duration heuristic.
  const baseline = ticks.length >= thresholds.minSampleSize ? computeP95(ticks.map((t) => t.durationUs)) : null;

  for (const tick of ticks) {
    // Heuristic 1 — tick duration outlier (only when the baseline is statistically meaningful).
    if (baseline !== null && baseline > 0) {
      const ratio = tick.durationUs / baseline;
      if (ratio >= thresholds.tickDurationMultiplier) {
        out.push({
          kind: 'tick-duration',
          tickNumber: tick.tickNumber,
          startUs: tick.startUs,
          endUs: tick.endUs,
          durationUs: tick.durationUs,
          magnitude: ratio,
          details: `${formatMicros(tick.durationUs)} (${ratio.toFixed(1)}× p95 baseline)`,
        });
      }
    }

    // Heuristic 2 — GC pauses inside this tick exceeding the threshold.
    if (tick.gcSuspensions.length > 0) {
      let totalPauseUs = 0;
      for (const s of tick.gcSuspensions) totalPauseUs += s.durationUs;
      if (totalPauseUs >= thresholds.gcPauseThresholdUs) {
        out.push({
          kind: 'gc-pause',
          tickNumber: tick.tickNumber,
          startUs: tick.startUs,
          endUs: tick.endUs,
          totalPauseUs,
          magnitude: totalPauseUs / thresholds.gcPauseThresholdUs,
          eventCount: tick.gcSuspensions.length,
          details: `${formatMicros(totalPauseUs)} across ${tick.gcSuspensions.length} GC event${tick.gcSuspensions.length === 1 ? '' : 's'}`,
        });
      }
    }
  }
  return out;
}

/**
 * Suggest a viewRange to scope the timeline to when a user clicks "Jump" on an anomaly. The window
 * is centred on the anomaly tick, with width = 2 × tick duration but clamped to a sensible floor
 * (1 ms — single ticks are too narrow to see at default zoom) and ceiling (1 s — anomalies are
 * "look near this tick", not "give me the whole second").
 */
export function suggestJumpRange(a: Anomaly): { startUs: number; endUs: number } {
  const tickWidth = Math.max(1, a.endUs - a.startUs);
  const center = (a.startUs + a.endUs) / 2;
  const scopeWidth = Math.min(1_000_000, Math.max(1_000, tickWidth * 4));
  return {
    startUs: center - scopeWidth / 2,
    endUs: center + scopeWidth / 2,
  };
}

/**
 * p95 of a numeric series. Copies + sorts (O(n log n)); the caller owns the input array. We don't
 * use linear-time quickselect because the sort is also reused by the renderer for sparkline ranges
 * and the constant factor is well below a 1 Hz update budget.
 */
function computeP95(values: number[]): number {
  if (values.length === 0) return 0;
  const sorted = [...values].sort((a, b) => a - b);
  const idx = Math.floor(sorted.length * 0.95);
  // Clamp to range — `Math.floor(1 * 0.95) = 0` so a single-element array picks index 0; >=2 picks
  // a valid index up to length-1.
  return sorted[Math.min(idx, sorted.length - 1)];
}

function formatMicros(us: number): string {
  if (us >= 1_000_000) return `${(us / 1_000_000).toFixed(2)} s`;
  if (us >= 1_000) return `${(us / 1_000).toFixed(2)} ms`;
  return `${us.toFixed(0)} µs`;
}
