import { useEffect, useState } from 'react';
import {
  acquireSessionCache,
  releaseSessionCache,
  subscribeSessionCache,
  getSessionCacheSnapshot,
  _entryByIdOrNull,
  type ProfilerCacheSnapshot,
  type ProfilerGaugeData,
  type SlotThreadInfo,
} from '@/hooks/profiler/profilerCacheRegistry';

/**
 * Thin wrapper over the per-session `profilerCacheRegistry`. The cache lifecycle (chunk loads,
 * assembly, store subscriptions) lives in the registry — this hook just acquires the entry on
 * mount, subscribes for change notifications, and releases on unmount.
 *
 * Multiple callers within the same session **share a single cache entry** (refcounted): the entry
 * is created on the first acquire, all subsequent callers reuse it, and the entry is destroyed
 * (in-flight aborted, store subscriptions detached) when the last consumer unmounts. This is the
 * fix for the Stage-4 perf regression where three independent hook instances each ran their own
 * decode + assembly pipeline on every chunk arrival (#377 follow-up, 2026-05-26) — turning a
 * ~120 ms main-thread Task per chunk into one that fits comfortably inside the 16-ms frame budget.
 *
 * Public contract preserved: the return shape is identical to the pre-registry hook, so the three
 * downstream call sites (`ProfilerPanel`, `useLiveGaugeData`, `useAnomalyDetection`) need no
 * changes — they automatically benefit from the dedup.
 *
 * #289 — post-unification, both Trace (replay) and Attach (live) sessions ride the same path; the
 * `isLive` flag drives the live-tail prefetch inside the registry. Re-acquiring with `isLive=true`
 * upgrades an entry created in replay mode.
 *
 * **First render returns the empty snapshot.** The effect that acquires the entry runs *after* the
 * initial render — that's React's contract. The bootstrap render shows zero data; the subsequent
 * render (triggered by the post-acquire `setTick`) carries the real snapshot. Downstream consumers
 * already handle empty data gracefully (cold-state panels, empty arrays in canvas draws).
 */
export type { ProfilerGaugeData, SlotThreadInfo };

export function useProfilerCache(sessionId: string | null, isLive: boolean): ProfilerCacheSnapshot {
  // The `tick` counter increments whenever the registry notifies us of new data (or on initial acquire).
  // We read the snapshot directly from the registry below — no React state holds the snapshot; the
  // registry IS the source of truth, the tick just forces the re-render.
  const [tick, setTick] = useState(0);

  useEffect(() => {
    if (sessionId === null) return undefined;
    acquireSessionCache(sessionId, isLive);
    // Pull the post-acquire snapshot into view (the first render saw the empty snapshot — this
    // bumps the tick so we re-read).
    setTick((t) => t + 1);
    const entry = _entryByIdOrNull(sessionId);
    const unsub = entry !== null ? subscribeSessionCache(entry, () => setTick((t) => t + 1)) : () => {};
    return () => {
      unsub();
      releaseSessionCache(sessionId);
    };
    // isLive intentionally omitted — acquire is idempotent on sessionId, and a mid-life replay→live
    // flip is handled by the registry's upgrade path.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sessionId]);

  void tick; // tick only drives re-renders; the snapshot read below is the real value.
  if (sessionId === null) return EMPTY_SNAPSHOT_REF;
  const entry = _entryByIdOrNull(sessionId);
  if (entry === null) return EMPTY_SNAPSHOT_REF;
  return getSessionCacheSnapshot(entry);
}

const EMPTY_SNAPSHOT_REF: ProfilerCacheSnapshot = Object.freeze({
  ticks: [],
  traceMetadata: null,
  gaugeData: {
    gaugeSeries: new Map(),
    gaugeCapacities: new Map(),
    memoryAllocEvents: [],
    gcEvents: [],
    gcSuspensions: [],
    threadNames: new Map(),
    offCpuBySlot: new Map(),
  },
  threadInfos: new Map(),
  pendingRangesUs: [],
}) as ProfilerCacheSnapshot;
