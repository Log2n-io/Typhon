import { useEffect } from 'react';
import { computeSelectionStats } from '@/libs/profiler/stats/selectionStats';
import type { TickData } from '@/libs/profiler/model/traceModel';
import type { TickSummary } from '@/libs/profiler/model/types';
import type { TimeRange } from '@/libs/profiler/model/uiTypes';
import { useProfilerStatsStore } from '@/stores/useProfilerStatsStore';

/**
 * Single-producer hook that runs `computeSelectionStats` and writes the result to
 * {@link useProfilerStatsStore}. Both the right-pane RangeStatsDetail and the TopSpansPanel
 * subscribe to that store, so the aggregation runs once per click instead of twice.
 *
 * Must be called from exactly one place — currently `ProfilerPanel` — alongside the
 * `useProfilerCache` instance whose `ticks` it consumes. Calling it from multiple components
 * would re-introduce the duplicate-compute that this hook exists to eliminate.
 *
 * **Debouncing is upstream now (#345).** Caller passes `useProfilerViewStore.viewRange`, which is
 * the *committed* slot — already debounced by `setTransientViewRange`. This hook just reacts to
 * settled changes synchronously. The previous internal 150 ms `setTimeout` was redundant once
 * pan/zoom started writing the transient slot instead of viewRange directly.
 *
 * **rAF coalescing (#377 perf follow-up, 2026-05-26).** During live capture the `ticks` reference
 * flips on every `chunkAdded` SSE event — up to dozens per second. Running `computeSelectionStats`
 * synchronously on each flip was burning ~5% of the main thread for stats that the user only sees
 * once per paint. We now defer the compute into a `requestAnimationFrame`: when deps change, the
 * effect's cleanup cancels the pending rAF and the new body schedules a fresh one. If N deps flips
 * happen inside a single 16 ms frame they collapse into one compute against the latest values —
 * the closure captures the deps from the most-recent render, the older closures get GC'd alongside
 * their cancelled rAFs. Net result: at most one `setStats` per frame, no missed updates because the
 * trailing schedule always wins.
 */
export function useProfilerStatsWriter(
  ticks: TickData[],
  tickSummaries: TickSummary[] | null,
  viewRange: TimeRange,
): void {
  const setStats = useProfilerStatsStore((s) => s.setStats);

  useEffect(() => {
    const rafId = requestAnimationFrame(() => {
      setStats(computeSelectionStats(ticks, tickSummaries, viewRange));
    });
    return () => cancelAnimationFrame(rafId);
  }, [ticks, tickSummaries, viewRange, setStats]);
}
