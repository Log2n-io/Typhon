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
 */
export function useProfilerStatsWriter(
  ticks: TickData[],
  tickSummaries: TickSummary[] | null,
  viewRange: TimeRange,
): void {
  const setStats = useProfilerStatsStore((s) => s.setStats);

  useEffect(() => {
    setStats(computeSelectionStats(ticks, tickSummaries, viewRange));
  }, [ticks, tickSummaries, viewRange, setStats]);
}
