import type { SystemArchetypeTouchSummary } from '@/api/generated/model/systemArchetypeTouchSummary';

/**
 * Inclusive tick range. Reused across the panel so consumers don't need to import the System DAG's TickRange.
 */
export interface TickRange {
  readonly from: number;
  readonly to: number;
}

/**
 * Result of {@link findTickRangeSlice} — half-open `[startIdx, endIdx)` indices into the input array.
 * Both indices satisfy `0 <= startIdx <= endIdx <= input.length`. When no rows match, returns
 * `{ startIdx: 0, endIdx: 0 }` so consumers can `array.slice(startIdx, endIdx)` without bound checks.
 */
export interface SliceIndices {
  readonly startIdx: number;
  readonly endIdx: number;
}

/**
 * Binary-search the cache section's per-(tick, system, archetype) rollup rows for the requested tick window.
 * The producer (`IncrementalCacheBuilder.FoldV12Event` + finalizeTick) emits rows sorted by
 * <code>(tickNumber, systemIndex, archetypeId)</code> — this function relies on tick-sort being primary, so
 * a single sub-array slice covers every matching row. O(log N) regardless of trace length.
 *
 * Returns `{ 0, 0 }` when:
 * - the input is empty
 * - the range is null (caller may interpret null as "no time selection yet")
 * - no rows fall inside the inclusive range
 *
 * @param rows  Sorted-by-tick row array from `metadata.systemArchetypeTouches`. Order is enforced server-side.
 * @param range Inclusive `[from, to]` tick bounds. Pass null to opt out of filtering (returns full extent).
 */
export function findTickRangeSlice(
  rows: readonly SystemArchetypeTouchSummary[] | null | undefined,
  range: TickRange | null,
): SliceIndices {
  if (!rows || rows.length === 0) return { startIdx: 0, endIdx: 0 };
  if (range === null) return { startIdx: 0, endIdx: rows.length };
  if (range.from > range.to) return { startIdx: 0, endIdx: 0 };

  const startIdx = lowerBound(rows, range.from);
  const endIdx = upperBound(rows, range.to);
  if (startIdx >= endIdx) return { startIdx: 0, endIdx: 0 };
  return { startIdx, endIdx };
}

/**
 * Returns the smallest index `i` such that `rows[i].tickNumber >= target`, or `rows.length` if no row qualifies.
 * Standard lower-bound binary search.
 */
function lowerBound(rows: readonly SystemArchetypeTouchSummary[], target: number): number {
  let lo = 0;
  let hi = rows.length;
  while (lo < hi) {
    const mid = (lo + hi) >>> 1;
    if (tickOf(rows[mid]) < target) {
      lo = mid + 1;
    } else {
      hi = mid;
    }
  }
  return lo;
}

/**
 * Returns the smallest index `i` such that `rows[i].tickNumber > target`, or `rows.length` if every row qualifies.
 * Standard upper-bound binary search. Combined with {@link lowerBound} on the same target this would yield a zero-
 * width slice; the call site uses `upperBound(target=to)` for inclusive-end semantics.
 */
function upperBound(rows: readonly SystemArchetypeTouchSummary[], target: number): number {
  let lo = 0;
  let hi = rows.length;
  while (lo < hi) {
    const mid = (lo + hi) >>> 1;
    if (tickOf(rows[mid]) <= target) {
      lo = mid + 1;
    } else {
      hi = mid;
    }
  }
  return lo;
}

/**
 * Field accessor with a defensive fallback. The Orval-generated DTO is a `{ [key: string]: unknown }` because
 * the C# struct ships through the OpenAPI doc with no per-field detail (StructLayout structs are opaque to the
 * generator). Cast + coerce, returning -Infinity on a missing field so the search degrades gracefully (rows that
 * don't carry tickNumber sort before everything else, which means they're excluded from any positive range).
 */
function tickOf(row: SystemArchetypeTouchSummary): number {
  const raw = (row as { tickNumber?: unknown }).tickNumber;
  if (typeof raw === 'number' && Number.isFinite(raw)) return raw;
  return Number.NEGATIVE_INFINITY;
}
