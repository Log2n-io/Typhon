/**
 * Generic interval lane-packing for the Critical-Path timeline.
 *
 * Assigns each item to the lowest lane whose previous interval ended at or before this item's
 * start; items that overlap in `[start, end)` land on separate lanes. Sorting by start and always
 * taking the first free lane yields the minimum lane count (= peak overlap depth) — the classic
 * interval-partitioning greedy.
 *
 * Used for both the non-CP track layout and the multi-lane phase band (`09-system-dag.md §5.1 /
 * §5.4`). `O(n · lanes)` — fine for the scales involved (tens to low hundreds of items).
 *
 * Exact-touch is shared: an item starting exactly when another ends (`end === nextStart`) reuses
 * the lane — the half-open `[start, end)` intervals don't overlap.
 */
export interface PackedItem<T> {
  item: T;
  /** Zero-based lane index. */
  lane: number;
}

export interface PackResult<T> {
  packed: PackedItem<T>[];
  /** Number of lanes used — `0` for an empty input. */
  laneCount: number;
}

export function packIntervals<T>(
  items: readonly T[],
  getStart: (item: T) => number,
  getEnd: (item: T) => number,
): PackResult<T> {
  const sorted = [...items].sort((a, b) => (getStart(a) - getStart(b)) || (getEnd(a) - getEnd(b)));
  /** `laneEnds[i]` = end time of the last interval placed in lane `i`. */
  const laneEnds: number[] = [];
  const packed: PackedItem<T>[] = [];

  for (const item of sorted) {
    const start = getStart(item);
    const end = getEnd(item);
    let lane = -1;
    for (let i = 0; i < laneEnds.length; i++) {
      if (laneEnds[i] <= start) {
        lane = i;
        break;
      }
    }
    if (lane === -1) {
      lane = laneEnds.length;
      laneEnds.push(end);
    } else {
      laneEnds[lane] = end;
    }
    packed.push({ item, lane });
  }

  return { packed, laneCount: laneEnds.length };
}
