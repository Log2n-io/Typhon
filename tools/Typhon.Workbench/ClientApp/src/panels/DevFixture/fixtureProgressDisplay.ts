// Pure formatting helpers for the DevFixture progress strip — extracted from the panel so the math is unit-
// testable without React. Drives the "slow vs stuck" diagnostic: instead of a `Math.round(percent)` that collapses
// 0.4% to "0%" and makes the user think nothing is happening, we show absolute counts (with thousand separators),
// a 2-decimal percent, elapsed wall-clock, and an instantaneous rate (entities/sec). When the rate has been zero
// for ≥ STALL_THRESHOLD_MS the panel flags the job as stalled — distinguishes a saturated-cache slow path from a
// genuinely wedged job.

/** Below this rate-of-change window we report rate as "—" (avoids dividing by tiny elapsed deltas at startup). */
const RATE_WARMUP_MS = 500;

/** Past this gap without any `completed` change we render the strip's stall hint. Tuned generous so that a normal
 *  slow batch (e.g. fsync-bound on a large WAL flush) doesn't cry wolf. */
export const STALL_THRESHOLD_MS = 10_000;

/**
 * Format an integer count with locale thousand separators. `12345` → `"12,345"`. Cheap and stable — `toLocaleString`
 * with no options uses the current locale's grouping; we don't pass a locale so the user's browser preference wins.
 */
export function formatCount(value: number): string {
  return Math.max(0, Math.floor(value)).toLocaleString();
}

/**
 * Format a percent with two decimals while keeping the human cases readable: exactly 0 stays `"0%"`, exactly 100
 * stays `"100%"`, the intermediate range gets the decimals so sub-percent progress is visible (`"0.39%"` instead of
 * `"0%"` — the whole reason this module exists). `null` / NaN / divide-by-zero → `null` so the caller can hide the
 * label entirely.
 */
export function formatPercent(completed: number, total: number): string | null {
  if (!Number.isFinite(completed) || !Number.isFinite(total) || total <= 0) {
    return null;
  }
  const ratio = completed / total;
  if (ratio <= 0) return '0%';
  if (ratio >= 1) return '100%';
  return `${(ratio * 100).toFixed(2)}%`;
}

/**
 * Format an elapsed wall-clock duration in ms as a compact string: `"4s"`, `"1m 23s"`, `"12m 04s"`, `"1h 02m"`.
 * Sub-second values round up to `"0s"`. Keeps the strip narrow — useful when the panel is docked in a side group.
 */
export function formatElapsed(ms: number): string {
  if (!Number.isFinite(ms) || ms < 0) ms = 0;
  const totalSec = Math.floor(ms / 1000);
  if (totalSec < 60) return `${totalSec}s`;
  const min = Math.floor(totalSec / 60);
  const sec = totalSec % 60;
  if (min < 60) return `${min}m ${String(sec).padStart(2, '0')}s`;
  const hr = Math.floor(min / 60);
  const remMin = min % 60;
  return `${hr}h ${String(remMin).padStart(2, '0')}m`;
}

/**
 * Format an entities-per-second rate compactly: `"12k/s"`, `"1.4M/s"`, `"950/s"`. Returns `null` for non-positive
 * or non-finite rates so the caller can hide the field entirely (we don't show "0/s" because the stall-detector
 * already covers that case with a more specific message).
 */
export function formatRate(perSec: number): string | null {
  if (!Number.isFinite(perSec) || perSec <= 0) return null;
  if (perSec >= 1_000_000) return `${(perSec / 1_000_000).toFixed(1)}M/s`;
  if (perSec >= 1000) return `${(perSec / 1000).toFixed(perSec >= 10_000 ? 0 : 1)}k/s`;
  return `${Math.round(perSec)}/s`;
}

/**
 * Compute the average rate over the elapsed window. Returns `null` while the window is shorter than
 * {@link RATE_WARMUP_MS} (a 100 ms window with 5 events gives nonsense rates — wait for stabilisation).
 */
export function computeRate(completed: number, elapsedMs: number): number | null {
  if (!Number.isFinite(completed) || completed <= 0) return null;
  if (!Number.isFinite(elapsedMs) || elapsedMs < RATE_WARMUP_MS) return null;
  return (completed * 1000) / elapsedMs;
}

/**
 * Render the full one-line progress strip from a single state object. Used by the panel; also re-usable by any
 * future tooling that polls the same job DTO and wants a uniform display. `null` fields are simply omitted so the
 * strip degrades gracefully — first poll won't have a rate yet, that's fine.
 */
export interface ProgressDisplay {
  /** "12,345 / 3,200,000" — the absolute progress on this phase. Always present. */
  countLabel: string;
  /** "0.39%" — sub-percent precision; null when total is 0 (indeterminate phase). */
  percentLabel: string | null;
  /** "1m 23s" — elapsed wall-clock since generation started. */
  elapsedLabel: string;
  /** "12k/s" — instantaneous rate, null during warmup or after a stall. */
  rateLabel: string | null;
  /** True when the last update to `completed` was ≥ {@link STALL_THRESHOLD_MS} ago AND the job is still active. */
  isStalled: boolean;
  /** Width % for the progress bar — capped to 100, never < 0. Indeterminate phases get a fixed 20 for visual feedback. */
  barWidthPct: number;
}

export function deriveProgressDisplay(input: {
  completed: number;
  total: number;
  genStartedAtMs: number;
  lastCompletedChangeAtMs: number;
  nowMs: number;
}): ProgressDisplay {
  const { completed, total, genStartedAtMs, lastCompletedChangeAtMs, nowMs } = input;
  const elapsedMs = Math.max(0, nowMs - genStartedAtMs);
  const sinceChangeMs = Math.max(0, nowMs - lastCompletedChangeAtMs);

  const indeterminate = !Number.isFinite(total) || total <= 0;
  const countLabel = indeterminate
    ? `${formatCount(completed)}`
    : `${formatCount(completed)} / ${formatCount(total)}`;
  const percentLabel = formatPercent(completed, total);
  const rate = computeRate(completed, elapsedMs);
  const rateLabel = rate !== null ? formatRate(rate) : null;
  const isStalled = completed > 0 && sinceChangeMs >= STALL_THRESHOLD_MS;
  const barWidthPct = indeterminate
    ? 20
    : Math.min(100, Math.max(0, total > 0 ? (completed / total) * 100 : 0));

  return {
    countLabel,
    percentLabel,
    elapsedLabel: formatElapsed(elapsedMs),
    rateLabel,
    isStalled,
    barWidthPct,
  };
}
