import { useEffect } from 'react';
import { computeSelectionStats } from '@/libs/profiler/stats/selectionStats';
import type { TickData } from '@/libs/profiler/model/traceModel';
import type { TickSummary } from '@/libs/profiler/model/types';
import type { TimeRange } from '@/libs/profiler/model/uiTypes';
import { useProfilerStatsStore } from '@/stores/useProfilerStatsStore';

const DEBOUNCE_MS = 150;

/**
 * Single-producer hook that runs `computeSelectionStats` 150 ms after the inputs settle and writes
 * the result to {@link useProfilerStatsStore}. Both the right-pane RangeStatsDetail and the
 * TopSpansPanel subscribe to that store, so the aggregation runs once per click instead of twice.
 *
 * Must be called from exactly one place — currently `ProfilerPanel` — alongside the
 * `useProfilerCache` instance whose `ticks` it consumes. Calling it from multiple components
 * would re-introduce the duplicate-compute that this hook exists to eliminate.
 *
 * Debounce rationale: a wheel-zoom event burst flips viewRange dozens of times per second. Coalesce
 * to one compute after the user stops; the canvas redraws on every viewRange tick so the flame
 * graph stays responsive while the heavier aggregation waits for idle.
 */
export function useProfilerStatsWriter(
  ticks: TickData[],
  tickSummaries: TickSummary[] | null,
  viewRange: TimeRange,
): void {
  const setStats = useProfilerStatsStore((s) => s.setStats);

  useEffect(() => {
    const id = window.setTimeout(() => {
      setStats(computeSelectionStats(ticks, tickSummaries, viewRange));
    }, DEBOUNCE_MS);
    return () => window.clearTimeout(id);
  }, [ticks, tickSummaries, viewRange, setStats]);
}
