import { useEffect, useMemo } from 'react';
import { causalKeyOf, computeRequests, rootsInView, spansForCausal, type NamedSystem } from '@/libs/profiler/correlation/requests';
import type { ProfilerSelection, TickData } from '@/libs/profiler/model/traceModel';
import type { TimeRange } from '@/libs/profiler/model/uiTypes';
import { useProfilerRequestsStore } from '@/stores/useProfilerRequestsStore';
import { useSelectionStore } from '@/stores/useSelectionStore';

const EVERYTHING: TimeRange = { startUs: -Infinity, endUs: Infinity };

/**
 * Single producer of the request rows (#1517), the same shape as `useProfilerStatsWriter`: called once from `ProfilerPanel` with the cache's ticks
 * and the committed viewport. The roots are computed once per tick set — a pan only re-filters them — and published rAF-coalesced so a burst of
 * live chunk arrivals publishes once per frame. Also publishes the spans sharing the selected span's cause, for the Details pane's links.
 */
export function useProfilerRequestsWriter(ticks: TickData[], systems: readonly NamedSystem[], viewRange: TimeRange): void {
  const setRows = useProfilerRequestsStore((s) => s.setRows);
  const setRelated = useProfilerRequestsStore((s) => s.setRelated);
  const allRows = useMemo(() => computeRequests(ticks, EVERYTHING, systems), [ticks, systems]);

  // The selected span's or root's causal key, as a string so an unrelated selection change does not re-run the scan below.
  const selectedKey = useSelectionStore((s) => {
    const ref = s.leaf?.type === 'span' ? (s.leaf.ref as ProfilerSelection) : null;
    if (ref === null) return null;
    if (ref.kind === 'request') return ref.row.kind === 'dropped' ? null : ref.row.key;
    return ref.kind === 'span' ? causalKeyOf(ref.span) : null;
  });
  const related = useMemo(() => (selectedKey === null ? null : { key: selectedKey, spans: spansForCausal(ticks, selectedKey) }),
    [ticks, selectedKey]);

  useEffect(() => {
    const rafId = requestAnimationFrame(() => {
      setRows(allRows, rootsInView(allRows, viewRange));
    });
    return () => cancelAnimationFrame(rafId);
  }, [allRows, viewRange, setRows]);

  useEffect(() => setRelated(related), [related, setRelated]);

  // The pin follows the selection: selecting something the pinned root did not cause, or clearing the selection, unpins it. Otherwise the pin
  // outlives what showed it and dims the lanes with nothing on screen to say why. Moving between the root and its own spans keeps it.
  useEffect(() => {
    const requests = useProfilerRequestsStore.getState();
    if (requests.selectedKey !== null && requests.selectedKey !== selectedKey) requests.select(null);
  }, [selectedKey]);
}
