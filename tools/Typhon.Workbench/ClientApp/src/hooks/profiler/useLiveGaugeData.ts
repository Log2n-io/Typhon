import { useMemo } from 'react';
import { aggregateGaugeData, type GaugeSeries, type GcEvent, type GcSuspensionEvent, type MemoryAllocEventData, type OffCpuStore, type TickData } from '@/libs/profiler/model/traceModel';
import type { GaugeId } from '@/libs/profiler/model/types';
import { useProfilerCache } from '@/hooks/profiler/useProfilerCache';

/**
 * Live-attach gauge feed for the Engine Health panel (#377 Stage 4 Phase 2). Rides the existing
 * `useProfilerCache` (which already keeps the manifest's tail resident in live mode — see #289)
 * and re-aggregates the last `windowMs` of ticks into a fresh `gaugeData` bundle. The window is
 * **independent of the timeline's `viewRange`** by design — Engine Health answers "what is the
 * engine doing right now?", not "what was it doing where I'm looking?".
 *
 * Why re-aggregate instead of reusing `useProfilerCache.gaugeData`: the cache's gaugeData spans the
 * full resident range (which can grow to many minutes once a live session has been running). For
 * the 60-s health snapshot we want a tight Y-scale + GC-event window, so a second pass over the
 * filtered subset is the cleanest seam. `aggregateGaugeData` is pure and O(N) — at ~1 Hz × 60 s
 * = 60 snapshots, the cost is negligible.
 *
 * Returns an empty bundle when the session isn't attached / metadata hasn't arrived yet — the
 * canvas renderer treats empty `gaugeSeries` as "no data" and draws axes only.
 */
export interface LiveGaugeData {
  /** Ticks whose `endUs` falls inside the window, sorted by tickNumber (cache assembly order). */
  windowedTicks: readonly TickData[];
  /** Aggregated gauge series + capacities + GC events for the windowed subset. */
  gaugeData: {
    gaugeSeries: Map<GaugeId, GaugeSeries>;
    gaugeCapacities: Map<GaugeId, number>;
    memoryAllocEvents: readonly MemoryAllocEventData[];
    gcEvents: readonly GcEvent[];
    gcSuspensions: readonly GcSuspensionEvent[];
    /** Empty — Engine Health doesn't render slot lanes, but the renderers' GaugeData shape carries it. */
    offCpuBySlot: Map<number, OffCpuStore>;
  };
  /** Window left edge in µs. `windowEndUs - windowStartUs` always equals `windowMs * 1000` when there's data. */
  windowStartUs: number;
  /** Window right edge in µs — pinned to the latest tick's `endUs` so the panel "lives" at the head. */
  windowEndUs: number;
  /** True iff the windowed subset is non-empty. UI uses this to gate "no data yet" placeholders. */
  hasData: boolean;
}

const EMPTY: LiveGaugeData = {
  windowedTicks: [],
  gaugeData: {
    gaugeSeries: new Map(),
    gaugeCapacities: new Map(),
    memoryAllocEvents: [],
    gcEvents: [],
    gcSuspensions: [],
    offCpuBySlot: new Map(),
  },
  windowStartUs: 0,
  windowEndUs: 0,
  hasData: false,
};

export function useLiveGaugeData(sessionId: string | null, windowMs: number = 60_000): LiveGaugeData {
  // Live mode (#289) — the chunk cache keeps the manifest's tail resident so the recent window is always loaded.
  const { ticks } = useProfilerCache(sessionId, true);

  return useMemo(() => {
    if (!sessionId || ticks.length === 0) return EMPTY;

    // Pin the window's right edge to the latest tick's `endUs` so the panel always tracks the head
    // of the stream, regardless of wall-clock vs. trace-clock skew. (Wall-clock would drift when the
    // engine pauses or the laptop sleeps.)
    const lastTick = ticks[ticks.length - 1];
    const windowEndUs = lastTick.endUs;
    const windowStartUs = windowEndUs - windowMs * 1000;

    // Binary search would be O(log N) but ticks are sorted and N is ~60 — linear from the tail is cache-friendlier.
    let firstIn = ticks.length;
    for (let i = ticks.length - 1; i >= 0; i--) {
      if (ticks[i].endUs < windowStartUs) break;
      firstIn = i;
    }
    const windowed = ticks.slice(firstIn);
    if (windowed.length === 0) return EMPTY;

    const agg = aggregateGaugeData(windowed as TickData[]);
    return {
      windowedTicks: windowed,
      gaugeData: {
        gaugeSeries: agg.gaugeSeries,
        gaugeCapacities: agg.gaugeCapacities,
        memoryAllocEvents: agg.memoryAllocEvents,
        gcEvents: agg.gcEvents,
        gcSuspensions: agg.gcSuspensions,
        offCpuBySlot: agg.offCpuBySlot,
      },
      windowStartUs,
      windowEndUs,
      hasData: true,
    };
    // `ticks` is the assembled array — reference flips whenever `entriesVersion` bumps in the cache.
    // That is the correct dependency: any chunk arrival re-runs aggregation.
  }, [sessionId, ticks, windowMs]);
}
