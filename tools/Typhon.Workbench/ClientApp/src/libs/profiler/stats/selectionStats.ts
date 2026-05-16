import type { SpanData, TickData } from '@/libs/profiler/model/traceModel';
import type { TickSummary } from '@/libs/profiler/model/types';
import type { TimeRange } from '@/libs/profiler/model/uiTypes';

/**
 * Aggregated stats for a viewport range — feeds the right-pane "Selection stats" detail view.
 *
 * **Coverage model.** The aggregation walks only ticks that are actually resident in the chunk
 * cache (see <see cref="computeSelectionStats"/>'s `ticks` parameter). The cache loader keeps the
 * visible range resident, so for normal pan/zoom the resident set covers the user's viewport. When
 * the user zooms way out past the cache budget we expose the partial coverage via
 * {@link ticksLoaded} / {@link ticksTotal} so the consumer can render a "X of Y ticks loaded"
 * caveat instead of pretending the partial sums are the full picture.
 *
 * **Performance.** O(events in resident ticks within range). The cache budget caps that; on a
 * whole-trace zoom-out where the cache holds 5% of the trace, we aggregate that 5%. No span/event
 * sort, no nested loops — single linear pass per stat dimension.
 */
export interface SelectionStats {
  /** Range bounds (µs) — copied from the viewRange the caller passed in. */
  rangeStartUs: number;
  rangeEndUs: number;
  rangeDurationUs: number;

  /** Tick coverage: how many ticks intersect the range vs. how many of those are resident in the cache. */
  ticksTotal: number;
  ticksLoaded: number;

  /** Sum of `eventCount` across loaded ticks within range. */
  eventsLoaded: number;

  /** Tick-duration stats (min / avg / p95 / max in µs) computed across the loaded ticks. Null when zero loaded. */
  tickDurationStats: { minUs: number; avgUs: number; p95Us: number; maxUs: number } | null;

  /**
   * All span groups (by name) within range — full per-group stats. Caller selects + slices for
   * its surface: the right-pane's "Top spans" card uses descending totalUs, top-10; the sortable
   * "Top N" table sorts by whichever column the user clicked, top-20. Computed once here so both
   * surfaces share the underlying aggregation and the worst-instance click-jump target.
   */
  spanGroups: SpanGroupStats[];

  /**
   * Top system chunks (by systemIndex) within range, sorted descending by total wall-clock time.
   * Trimmed to {@link TOP_N}. Each entry exposes both wall-clock and cpu time:
   *   - `totalWallUs`: Σ (per-tick wall-clock window of the system) across ticks. Critical-path-style
   *     latency cost. Single-threaded and 16-worker-parallel systems contribute their wall-clock
   *     span equally — running 16 chunks in 690 µs adds 690 µs, not 11 ms.
   *   - `totalCpuUs`: Σ chunk durations clipped to range. The actual worker-µs consumed; for the
   *     same parallel system this would be ~11 ms across the 16 chunks.
   * Their ratio `totalCpuUs / totalWallUs` is the system's effective parallel width — useful when
   * compared against the worker pool size (e.g. 8 / 16 → "you're using ~half the pool when this runs").
   */
  topSystemsByTotal: Array<{ systemIndex: number; systemName: string; count: number; totalWallUs: number; totalCpuUs: number }>;

  /** GC pause time within range (µs). Sourced from per-tick `gcSuspensions[]`. */
  gcPauseTotalUs: number;
  gcSuspensionCount: number;
}

/** Cap on how many top-N entries we return per category. Keeps the right-pane vertical extent bounded. */
export const TOP_N = 10;

/**
 * Aggregate metrics for one span name within the selection range. Each instance contributes its
 * RANGE-CLIPPED duration (a long span that straddles the range boundary contributes only the
 * portion inside the range), so totals stay honest as the user pans/zooms. {@link worstSpan} is
 * the un-clipped span instance with the largest in-range duration — used by the "click row to
 * jump" handler to tween the viewport to that span's full extent.
 */
export interface SpanGroupStats {
  name: string;
  count: number;
  minUs: number;
  avgUs: number;
  maxUs: number;
  p95Us: number;
  totalUs: number;
  /** The instance with the largest in-range duration. Used for click-to-jump. */
  worstSpan: SpanData;
}

/**
 * Compute aggregated stats for the resident ticks inside <paramref name="viewRange"/>. Pass:
 *  - <paramref name="ticks"/>: the cache-resident `TickData[]` (from `useProfilerCache.ticks`).
 *  - <paramref name="tickSummaries"/>: the full per-tick summary array from metadata; used for the
 *    "ticksTotal" denominator only — it's a cheap header-level fetch even when the chunk cache
 *    isn't resident, so coverage stays honest.
 *  - <paramref name="viewRange"/>: the viewport's [startUs, endUs).
 *
 * Returns `null` if the range is degenerate (`endUs <= startUs`) — caller should render the empty
 * state instead of a stats card with all zeros.
 */
export function computeSelectionStats(
  ticks: TickData[],
  tickSummaries: TickSummary[] | undefined | null,
  viewRange: TimeRange,
): SelectionStats | null {
  const rangeStartUs = viewRange.startUs;
  const rangeEndUs = viewRange.endUs;
  if (!(rangeEndUs > rangeStartUs)) return null;

  // ── Tick coverage from summaries (cheap; doesn't require resident chunks) ─────────────────────
  // tick i overlaps [start, end) iff tick.endUs > start && tick.startUs < end (strict half-open).
  let ticksTotal = 0;
  if (tickSummaries) {
    for (const t of tickSummaries) {
      const ts = Number(t.startUs);
      const dur = Number(t.durationUs);
      const te = ts + dur;
      if (te > rangeStartUs && ts < rangeEndUs) ticksTotal++;
    }
  }

  // ── Loaded-only aggregation ──────────────────────────────────────────────────────────────────
  let ticksLoaded = 0;
  let eventsLoaded = 0;
  const tickDurations: number[] = [];

  // Groupings — Maps for O(1) accumulation.
  // Per-name span aggregator carries: durations[] (for min/avg/max/p95), running total, count,
  // and the worst-instance reference for click-to-jump. Storing `durations` raw keeps the math
  // simple at the cost of O(events) memory in range — acceptable since the cache budget caps
  // the resident span count.
  const spanAgg = new Map<string, {
    count: number;
    totalUs: number;
    minUs: number;
    maxUs: number;
    durations: number[];
    worstSpan: SpanData;
    worstUs: number;
  }>();
  const systemAgg = new Map<number, { systemName: string; count: number; totalWallUs: number; totalCpuUs: number }>();
  // Per-tick accumulator for chunk wall-clock windows. Reused each tick (clear + fill).
  // Tracks min(clipped start) and max(clipped end) per system so parallel chunks that execute
  // concurrently on different threads contribute the wall-clock span of the system's execution
  // window, not the inflated sum of every thread's duration.
  // Per-tick scratch: wall-clock window (min/max), chunk count, and Σ chunk durations (cpu time).
  // CPU time is summed at chunk grain — reflects the actual worker-µs consumed even when chunks
  // ran in parallel. Pair with the wall-clock window for the parallel-width derivation downstream.
  const tickSystemWc = new Map<number, { minStart: number; maxEnd: number; count: number; cpuUs: number; systemName: string }>();
  let gcPauseTotalUs = 0;
  let gcSuspensionCount = 0;

  for (const tick of ticks) {
    if (!(tick.endUs > rangeStartUs && tick.startUs < rangeEndUs)) continue;
    ticksLoaded++;
    eventsLoaded += tick.rawEvents.length;
    tickDurations.push(tick.durationUs);

    // Spans grouped by name. Range-clip each span: a span that straddles the range edge contributes
    // only its in-range portion. Keeps the totals honest at the boundaries when the user has dragged
    // a tight selection that cuts a long span. Track the worst (largest-clipped-duration) instance
    // per name so the right-pane Top-N table can click-to-jump straight to the offender.
    for (const span of tick.spans) {
      if (!(span.endUs > rangeStartUs && span.startUs < rangeEndUs)) continue;
      const clipStart = Math.max(span.startUs, rangeStartUs);
      const clipEnd = Math.min(span.endUs, rangeEndUs);
      const dur = clipEnd - clipStart;
      if (dur <= 0) continue;
      let e = spanAgg.get(span.name);
      if (e === undefined) {
        e = { count: 0, totalUs: 0, minUs: Infinity, maxUs: 0, durations: [], worstSpan: span, worstUs: 0 };
        spanAgg.set(span.name, e);
      }
      e.count++;
      e.totalUs += dur;
      if (dur < e.minUs) e.minUs = dur;
      if (dur > e.maxUs) e.maxUs = dur;
      e.durations.push(dur);
      if (dur > e.worstUs) {
        e.worstUs = dur;
        e.worstSpan = span;
      }
    }

    // Chunks grouped by systemIndex. For each system within this tick we track the wall-clock
    // execution window (min clipped start → max clipped end) rather than the sum of individual
    // chunk durations. Summing would inflate parallel systems by N× because their chunks run
    // concurrently on separate threads; the wall-clock span correctly reflects the critical-path
    // cost to the tick regardless of whether the system is serial or parallel.
    tickSystemWc.clear();
    for (const chunk of tick.chunks) {
      if (!(chunk.endUs > rangeStartUs && chunk.startUs < rangeEndUs)) continue;
      const clipStart = Math.max(chunk.startUs, rangeStartUs);
      const clipEnd = Math.min(chunk.endUs, rangeEndUs);
      if (clipEnd <= clipStart) continue;
      const chunkCpu = clipEnd - clipStart;
      const wc = tickSystemWc.get(chunk.systemIndex);
      if (wc === undefined) {
        tickSystemWc.set(chunk.systemIndex, { minStart: clipStart, maxEnd: clipEnd, count: 1, cpuUs: chunkCpu, systemName: chunk.systemName });
      } else {
        wc.count++;
        wc.cpuUs += chunkCpu;
        if (clipStart < wc.minStart) wc.minStart = clipStart;
        if (clipEnd > wc.maxEnd) wc.maxEnd = clipEnd;
      }
    }
    for (const [sysIdx, wc] of tickSystemWc) {
      const wallClockDur = wc.maxEnd - wc.minStart;
      const e = systemAgg.get(sysIdx);
      if (e === undefined) systemAgg.set(sysIdx, { systemName: wc.systemName, count: wc.count, totalWallUs: wallClockDur, totalCpuUs: wc.cpuUs });
      else { e.count += wc.count; e.totalWallUs += wallClockDur; e.totalCpuUs += wc.cpuUs; }
    }

    // GC pause sum. Same clip — a 5 ms pause that straddles the range start gets credited only
    // for the portion inside the range.
    for (const sus of tick.gcSuspensions) {
      const susEnd = sus.startUs + sus.durationUs;
      if (!(susEnd > rangeStartUs && sus.startUs < rangeEndUs)) continue;
      const clipStart = Math.max(sus.startUs, rangeStartUs);
      const clipEnd = Math.min(susEnd, rangeEndUs);
      const dur = clipEnd - clipStart;
      if (dur > 0) {
        gcPauseTotalUs += dur;
        gcSuspensionCount++;
      }
    }
  }

  // ── Tick duration stats (computed once over the collected array) ─────────────────────────────
  let tickDurationStats: SelectionStats['tickDurationStats'] = null;
  if (tickDurations.length > 0) {
    let min = Infinity;
    let max = -Infinity;
    let sum = 0;
    for (const d of tickDurations) {
      if (d < min) min = d;
      if (d > max) max = d;
      sum += d;
    }
    // p95 needs sorted copy. Cap the sort cost at O(N log N) where N = ticksLoaded; for a typical
    // viewport that's at most a few thousand. Past that, the cache budget gates resident size.
    const sorted = tickDurations.slice().sort((a, b) => a - b);
    const p95Idx = Math.min(sorted.length - 1, Math.floor(sorted.length * 0.95));
    tickDurationStats = {
      minUs: min,
      avgUs: sum / tickDurations.length,
      p95Us: sorted[p95Idx],
      maxUs: max,
    };
  }

  // ── Per-group finalisation: derive avg + p95 from the collected durations ─────────────────────
  // Sort each group's durations once; keep the result so the caller can re-sort by any column
  // without recomputing percentiles. The cache budget caps total spans, so the cumulative
  // sort cost is bounded by the budget, not by the trace size.
  const spanGroups: SpanGroupStats[] = [];
  for (const [name, v] of spanAgg) {
    const sorted = v.durations.slice().sort((a, b) => a - b);
    const p95Idx = Math.min(sorted.length - 1, Math.floor(sorted.length * 0.95));
    spanGroups.push({
      name,
      count: v.count,
      minUs: v.minUs,
      avgUs: v.totalUs / v.count,
      maxUs: v.maxUs,
      p95Us: sorted[p95Idx],
      totalUs: v.totalUs,
      worstSpan: v.worstSpan,
    });
  }

  const topSystemsByTotal = Array.from(systemAgg.entries())
    .map(([systemIndex, v]) => ({ systemIndex, systemName: v.systemName, count: v.count, totalWallUs: v.totalWallUs, totalCpuUs: v.totalCpuUs }))
    .sort((a, b) => b.totalWallUs - a.totalWallUs)
    .slice(0, TOP_N);

  return {
    rangeStartUs,
    rangeEndUs,
    rangeDurationUs: rangeEndUs - rangeStartUs,
    ticksTotal,
    ticksLoaded,
    eventsLoaded,
    tickDurationStats,
    spanGroups,
    topSystemsByTotal,
    gcPauseTotalUs,
    gcSuspensionCount,
  };
}
