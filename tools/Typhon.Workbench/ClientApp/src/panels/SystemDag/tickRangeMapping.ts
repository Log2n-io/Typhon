import type { TickSummaryDto } from '@/api/generated/model/tickSummaryDto';
import type { TimeSelection } from '@/stores/useSelectionStore';
import type { TickRange } from './useDagViewStore';

/**
 * Converts a µs `[start, end)` time selection (the units used by the profiler's TimeArea and
 * `useSelectionStore.time`) into the inclusive `[from, to]` tick-number range that the
 * `/aggregate` endpoint and the DAG panel's downstream hooks consume.
 *
 * `tickSummaries` is assumed to be tick-ordered with monotonically non-decreasing `startUs` —
 * this matches the wire contract (cache writes ticks in order). Binary search is O(log N) so the
 * conversion is cheap to call on every TimeArea scrub.
 *
 * Semantics:
 * - `[time.start, time.end)` is the µs window. A tick is **in** the window iff `startUs` falls
 *   in that half-open interval. (End-exclusive matches the {@link TimeSelection} contract.)
 * - Returns `null` when no ticks fall in the window — the caller should skip aggregation rather
 *   than fire empty queries.
 *
 * Edge cases tested in the companion spec.
 */
export function timeToTickRange(
  time: TimeSelection | null,
  tickSummaries: readonly TickSummaryDto[] | null | undefined,
): TickRange | null {
  if (!time || !tickSummaries || tickSummaries.length === 0) return null;

  // First tick with startUs >= time.start (inclusive lower bound).
  const firstIdx = firstIndexWithStartUsGte(tickSummaries, time.start);
  if (firstIdx >= tickSummaries.length) return null; // window starts after every tick.

  // Last tick with startUs < time.end (end is exclusive — first index past it, minus 1).
  const lastIdx = firstIndexWithStartUsGte(tickSummaries, time.end) - 1;
  if (lastIdx < firstIdx) return null; // window contains no tick.

  const fromTick = numericValue(tickSummaries[firstIdx].tickNumber);
  const toTick = numericValue(tickSummaries[lastIdx].tickNumber);
  if (fromTick == null || toTick == null) return null;
  return { from: fromTick, to: toTick };
}

/**
 * Computes the µs `[start, end)` window that covers exactly the last `n` ticks. Used by the
 * "Snapshot last N ticks" toolbar action: it writes the result to {@link useSelectionStore.time}
 * which the bridge fans out to both the profiler's TimeArea and back through {@link timeToTickRange}
 * for the DAG aggregations.
 *
 * If fewer than `n` ticks exist, returns the window covering all available ticks (degrades
 * gracefully on early-session captures). Returns `null` if no ticks are loaded.
 */
export function lastNTicksToTime(
  n: number,
  tickSummaries: readonly TickSummaryDto[] | null | undefined,
): TimeSelection | null {
  if (!tickSummaries || tickSummaries.length === 0 || n <= 0) return null;

  const total = tickSummaries.length;
  const startIdx = Math.max(0, total - n);
  const lastIdx = total - 1;

  const start = numericValue(tickSummaries[startIdx].startUs);
  const lastStart = numericValue(tickSummaries[lastIdx].startUs);
  const lastDuration = numericValue(tickSummaries[lastIdx].durationUs) ?? 0;
  if (start == null || lastStart == null) return null;

  // End is the moment AFTER the last tick finishes — matches the end-exclusive convention so the
  // last tick is included by `timeToTickRange`. `+ 1` provides a tiny epsilon in the (unusual)
  // case `durationUs == 0`, ensuring the strict `<` comparator includes the final tick.
  const end = lastStart + Math.max(lastDuration, 1);
  return { start, end };
}

// ── internals ────────────────────────────────────────────────────────────

/**
 * Smallest index `i` with `startUs(i) >= value`. Returns `length` if no tick satisfies. Used for
 * both ends of the window: directly for the inclusive lower bound, and `result - 1` gives the
 * largest index with `startUs < value` for the exclusive upper bound.
 */
function firstIndexWithStartUsGte(rows: readonly TickSummaryDto[], value: number): number {
  let lo = 0;
  let hi = rows.length;
  while (lo < hi) {
    const mid = (lo + hi) >>> 1;
    const start = numericValue(rows[mid].startUs);
    if (start == null || start < value) {
      lo = mid + 1;
    } else {
      hi = mid;
    }
  }
  return lo;
}

function numericValue(v: unknown): number | null {
  if (v == null) return null;
  const n = typeof v === 'number' ? v : Number(v as string);
  return Number.isFinite(n) ? n : null;
}
