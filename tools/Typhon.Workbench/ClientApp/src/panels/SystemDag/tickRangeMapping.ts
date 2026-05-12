import type { TickSummaryDto } from '@/api/generated/model/tickSummaryDto';
import type { TimeRange } from '@/libs/profiler/model/uiTypes';
import type { TickRange } from './useDagViewStore';

/**
 * Converts a µs `[startUs, endUs)` time range (the units used by the profiler's TimeArea and
 * `useProfilerViewStore.viewRange`) into the inclusive `[from, to]` tick-number range that the
 * `/aggregate` endpoint and the DAG panel's downstream hooks consume.
 *
 * `tickSummaries` is assumed to be tick-ordered with monotonically non-decreasing `startUs` —
 * this matches the wire contract (cache writes ticks in order). Binary search is O(log N) so the
 * conversion is cheap to call on every settled viewport change.
 *
 * Semantics:
 * - A tick is **in** the window iff its `[startUs, startUs + durationUs)` interval overlaps
 *   `[range.startUs, range.endUs)`. Ticks don't overlap each other, so `endUs` is non-decreasing
 *   and the two bounds can each be located via a single O(log N) probe.
 * - Zoom-into-a-tick (a window strictly inside one tick's interval) snaps to that tick — the
 *   Data Flow / Access Matrix panels rely on this so the user can't end up with a "no ticks in
 *   window" hole and a blank canvas.
 * - Returns `null` when no tick overlaps — the caller should skip aggregation rather than fire
 *   empty queries.
 *
 * Edge cases tested in the companion spec.
 */
export function timeToTickRange(
  range: TimeRange | null,
  tickSummaries: readonly TickSummaryDto[] | null | undefined,
): TickRange | null {
  if (!range || !tickSummaries || tickSummaries.length === 0) return null;

  // First tick whose right edge exceeds the window's left edge — i.e., the earliest tick that
  // still overlaps the window. Mid-tick window starts still pick the enclosing tick because
  // `endUs > range.startUs` is true for it.
  const firstIdx = firstIndexWithEndUsGt(tickSummaries, range.startUs);
  if (firstIdx >= tickSummaries.length) return null; // window starts after every tick.

  // Last tick whose left edge is strictly inside the window (end is exclusive).
  const lastIdx = firstIndexWithStartUsGte(tickSummaries, range.endUs) - 1;
  if (lastIdx < firstIdx) return null; // window misses every tick (e.g., zero-width at a boundary).

  const fromTick = numericValue(tickSummaries[firstIdx].tickNumber);
  const toTick = numericValue(tickSummaries[lastIdx].tickNumber);
  if (fromTick == null || toTick == null) return null;
  return { from: fromTick, to: toTick };
}

/**
 * Computes the µs `[startUs, endUs)` window that covers exactly the last `n` ticks. Used by the
 * "Snapshot last N ticks" toolbar action: it writes the result to
 * `useProfilerViewStore.commitViewRange` which atomically updates both slots, so the profiler's
 * TimeArea and every downstream aggregation see the new range on the next frame.
 *
 * If fewer than `n` ticks exist, returns the window covering all available ticks (degrades
 * gracefully on early-session captures). Returns `null` if no ticks are loaded.
 */
export function lastNTicksToTime(
  n: number,
  tickSummaries: readonly TickSummaryDto[] | null | undefined,
): TimeRange | null {
  if (!tickSummaries || tickSummaries.length === 0 || n <= 0) return null;

  const total = tickSummaries.length;
  const startIdx = Math.max(0, total - n);
  const lastIdx = total - 1;

  const startUs = numericValue(tickSummaries[startIdx].startUs);
  const lastStart = numericValue(tickSummaries[lastIdx].startUs);
  const lastDuration = numericValue(tickSummaries[lastIdx].durationUs) ?? 0;
  if (startUs == null || lastStart == null) return null;

  // End is the moment AFTER the last tick finishes — matches the end-exclusive convention so the
  // last tick is included by `timeToTickRange`. `+ 1` provides a tiny epsilon in the (unusual)
  // case `durationUs == 0`, ensuring the strict `<` comparator includes the final tick.
  const endUs = lastStart + Math.max(lastDuration, 1);
  return { startUs, endUs };
}

// ── internals ────────────────────────────────────────────────────────────

/**
 * Smallest index `i` with `startUs(i) >= value`. Returns `length` if no tick satisfies. Used to
 * locate the exclusive upper bound of the visible window: `result - 1` gives the largest index
 * with `startUs < value`.
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

/**
 * Smallest index `i` with `startUs(i) + durationUs(i) > value`. Returns `length` if no tick satisfies.
 * Ticks don't overlap, so `endUs` is monotonically non-decreasing across the sorted array — binary
 * search is safe. Used to locate the earliest tick whose right edge crosses the window's left edge.
 */
function firstIndexWithEndUsGt(rows: readonly TickSummaryDto[], value: number): number {
  let lo = 0;
  let hi = rows.length;
  while (lo < hi) {
    const mid = (lo + hi) >>> 1;
    const start = numericValue(rows[mid].startUs);
    const dur = numericValue(rows[mid].durationUs) ?? 0;
    // Treat zero-duration ticks as 1 µs wide for overlap purposes — matches the +1 µs epsilon
    // convention in `lastNTicksToTime` so a `last-1-tick` snapshot roundtrips even when the final
    // tick has durationUs == 0.
    const endExclusive = start == null ? null : start + Math.max(dur, 1);
    if (endExclusive == null || endExclusive <= value) {
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
