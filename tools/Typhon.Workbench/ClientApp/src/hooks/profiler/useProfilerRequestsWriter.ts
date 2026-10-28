import { useEffect, useMemo } from 'react';
import { computeRequests, type NamedSystem } from '@/libs/profiler/correlation/requests';
import type { TickData } from '@/libs/profiler/model/traceModel';
import type { TimeRange } from '@/libs/profiler/model/uiTypes';
import { useProfilerRequestsStore } from '@/stores/useProfilerRequestsStore';

const EVERYTHING: TimeRange = { startUs: -Infinity, endUs: Infinity };

/**
 * Single producer of the request rows (#1517), the same shape as `useProfilerStatsWriter`: called once from `ProfilerPanel` with the cache's ticks
 * and the committed viewport. The roots are computed once per tick set — a pan only re-filters them — and published rAF-coalesced so a burst of
 * live chunk arrivals publishes once per frame.
 */
export function useProfilerRequestsWriter(ticks: TickData[], systems: readonly NamedSystem[], viewRange: TimeRange): void {
  const setRows = useProfilerRequestsStore((s) => s.setRows);
  const allRows = useMemo(() => computeRequests(ticks, EVERYTHING, systems), [ticks, systems]);

  useEffect(() => {
    const rafId = requestAnimationFrame(() => {
      const inView = viewRange.endUs > viewRange.startUs
        ? allRows.filter((r) => r.startUs >= viewRange.startUs && r.startUs < viewRange.endUs)
        : [];
      setRows(allRows, inView);
    });
    return () => cancelAnimationFrame(rafId);
  }, [allRows, viewRange, setRows]);
}
