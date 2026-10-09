import { useMemo } from 'react';
import { aggregateGaugeData, type GaugeSeries, type GcEvent, type GcSuspensionEvent, type MemoryAllocEventData, type OffCpuStore, type TickData } from '@/libs/profiler/model/traceModel';
import type { GaugeId } from '@/libs/profiler/model/types';
import { useProfilerCache } from '@/hooks/profiler/useProfilerCache';
import { selectEffectiveScope, useProfilerViewStore } from '@/stores/useProfilerViewStore';
import { useLiveStreamSession } from '@/stores/useSessionStore';

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

/**
 * The window a panel reads, and — since #1083 — which end of the stream it is anchored to.
 *
 * **Following the head is right only while the session is live AND the global scope is linked.** Linked means follow;
 * a live stream's head is what there is to follow. Two cases were being answered as if they were that one:
 *
 * - a **replayed capture** has no head to chase, and its view range IS the scrub control, so pinning it to the last
 *   recorded tick made every panel show the end of the file and nothing else;
 * - an **unlinked** scope is the app's own freeze affordance (GAP-11), so a panel that keeps tracking the head after
 *   the user pinned a moment is ignoring the one control provided for holding it still.
 *
 * Live and linked keeps the previous behaviour exactly — the last `windowMs` measured back from the newest tick —
 * because the rates computed over this window (per-second migrations, drifters, window growth) are only meaningful
 * over a bounded span, and widening it silently would change every number a panel reports.
 */
function resolveWindow(
  ticks: readonly TickData[],
  windowMs: number,
  followHead: boolean,
  scope: { startUs: number; endUs: number },
): { startUs: number; endUs: number } {
  // Pin the window's right edge to the latest tick's `endUs` so the panel tracks the head of the stream, regardless of
  // wall-clock vs. trace-clock skew. (Wall-clock would drift when the engine pauses or the laptop sleeps.)
  const headEndUs = ticks[ticks.length - 1].endUs;
  if (followHead || !(scope.endUs > scope.startUs)) {
    return { startUs: headEndUs - windowMs * 1000, endUs: headEndUs };
  }
  return { startUs: scope.startUs, endUs: scope.endUs };
}

export function useLiveGaugeData(sessionId: string | null, windowMs: number = 60_000): LiveGaugeData {
  // Live mode (#289) — the chunk cache keeps the manifest's tail resident so the recent window is always loaded.
  const { ticks } = useProfilerCache(sessionId, true);
  const isLive = useLiveStreamSession();
  const scopeLinked = useProfilerViewStore((s) => s.scopeLinked);
  const scope = useProfilerViewStore(selectEffectiveScope);
  const followHead = isLive && scopeLinked;

  return useMemo(() => {
    if (!sessionId || ticks.length === 0) return EMPTY;

    const { startUs: windowStartUs, endUs: windowEndUs } = resolveWindow(ticks, windowMs, followHead, scope);

    // A tick is in the window when it OVERLAPS it — a range is not a containment test, and a scrubbed range narrower
    // than one tick must still select that tick rather than nothing.
    const windowed = ticks.filter((t) => t.endUs >= windowStartUs && t.startUs <= windowEndUs);
    // Never answer "no data" because a scope fell between two ticks: that reads as an idle engine, which is a claim
    // several of these panels make for real.
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
  }, [sessionId, ticks, windowMs, followHead, scope]);
}
