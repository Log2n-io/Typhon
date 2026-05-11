import { useEffect, useMemo } from 'react';
import type { IDockviewPanelProps } from 'dockview-react';
import type { SystemArchetypeTouchSummary } from '@/api/generated/model/systemArchetypeTouchSummary';
import { useTopology } from '@/hooks/data/useTopology';
import { useProfilerMetadata } from '@/hooks/profiler/useProfilerMetadata';
import { REFRESH_DEBOUNCE_MS, useDebouncedValue, useTickGatedSnapshot } from '@/hooks/useTickGatedSnapshot';
import { useProfilerSessionStore } from '@/stores/useProfilerSessionStore';
import { useSelectionStore } from '@/stores/useSelectionStore';
import { useSessionStore } from '@/stores/useSessionStore';
import { findTickRangeSlice } from '@/panels/DataFlow/tickRangeFilter';
import { trackToDataTrackSelection } from '@/panels/DataFlow/trackBuilding';
import type { GranularityLevel } from '@/panels/DataFlow/useDataFlowViewStore';
import { timeToTickRange } from '@/panels/SystemDag/tickRangeMapping';
import AccessMatrixGrid from './AccessMatrixGrid';
import AccessMatrixToolbar from './AccessMatrixToolbar';
import { buildAccessMatrix } from './matrixBuilding';
import {
  clusterReorderColumns,
  clusterReorderRows,
  orderColumnsByPhaseDependency,
} from './matrixOrdering';
import { useAccessMatrixViewStore } from './useAccessMatrixViewStore';

/**
 * Access Matrix panel — the structural complement to the Data Flow Timeline. Where the Timeline answers "what
 * happens to the data over time?", this panel answers "which systems touch which data?" Same L0–L4 altitude
 * semantics; same access-kind palette; same click-to-select-system bridge.
 *
 * Composes:
 * - {@link AccessMatrixToolbar} — granularity / row sort / column sort
 * - {@link AccessMatrixGrid}    — virtualisation-free CSS Grid renderer (suitable for typical 20×10 matrices)
 *
 * Data flow:
 * 1. `useTopology` + `useProfilerMetadata` — already-cached, shared with the System DAG and Data Flow Timeline.
 * 2. `metadata.systemArchetypeTouches` filtered to the visible tick range via `findTickRangeSlice`.
 * 3. `buildAccessMatrix(topology, level, touchSlice)` — pure derivation of rows × columns × cells.
 * 4. `orderColumnsByPhaseDependency` (default) or `clusterReorderColumns` (cluster mode) — column ordering.
 * 5. `clusterReorderRows` (cluster mode) — row ordering.
 *
 * Selection: cell clicks write to {@link useSelectionStore.system} (already wired from Phase B → System DAG +
 * Data Flow Timeline both react). Phase D will add the reverse direction `dataTrack` slot.
 */
export default function AccessMatrixPanel(_props: IDockviewPanelProps) {
  const sessionId = useSessionStore((s) => s.sessionId);
  const { data: topology } = useTopology(sessionId);
  const { data: liveMetadata } = useProfilerMetadata(sessionId);
  // Tick-gated debounced snapshot — same pattern as DataFlowPanel. Heavy memos below (touchSlice,
  // baseMatrix, orderedColumns/Rows) only recompute when the latest tick advances OR the user changes
  // the time selection, debounced (see REFRESH_DEBOUNCE_MS) to coalesce live-stream bursts.
  const latestTickNumber = useProfilerSessionStore((s) => s.latestTickNumber);
  const liveTime = useSelectionStore((s) => s.time);
  const refreshKey = `${latestTickNumber}|${liveTime?.start ?? '-'}|${liveTime?.end ?? '-'}`;
  const debouncedRefreshKey = useDebouncedValue(refreshKey, REFRESH_DEBOUNCE_MS);
  const metadata = useTickGatedSnapshot(liveMetadata, debouncedRefreshKey);
  const time = useTickGatedSnapshot(liveTime, debouncedRefreshKey);

  const granularityLevel = useAccessMatrixViewStore((s) => s.granularityLevel);
  const rowSort = useAccessMatrixViewStore((s) => s.rowSort);
  const colSort = useAccessMatrixViewStore((s) => s.colSort);
  const setGranularityLevel = useAccessMatrixViewStore((s) => s.setGranularityLevel);

  const selectedSystem = useSelectionStore((s) => s.system);
  const setSelectedSystem = useSelectionStore((s) => s.setSystem);
  // Phase D (#327): cross-panel selection slots. The matrix listens to all three; only writes dataTrack and
  // phase (system selection happens through cell clicks).
  const dataTrack = useSelectionStore((s) => s.dataTrack);
  const setDataTrack = useSelectionStore((s) => s.setDataTrack);
  const selectedPhase = useSelectionStore((s) => s.phase);
  const setPhase = useSelectionStore((s) => s.setPhase);
  const hoveredKey = useSelectionStore((s) => s.hoveredSystemTickKey);
  const clearSelection = useSelectionStore((s) => s.clear);

  // Phase D (#327): granularity step + Esc clear shortcuts. Same gating logic as DataFlowPanel — input/textarea
  // exits early so the user can type without triggering panel actions.
  useEffect(() => {
    function onKey(e: KeyboardEvent) {
      const active = document.activeElement;
      if (active && (active.tagName === 'INPUT' || active.tagName === 'TEXTAREA')) return;
      if (e.key === '[' || e.key === ']') {
        const order: GranularityLevel[] = ['L0', 'L1', 'L2', 'L3', 'L4'];
        const idx = order.indexOf(granularityLevel);
        const next = e.key === ']' ? Math.min(order.length - 1, idx + 1) : Math.max(0, idx - 1);
        if (next !== idx) setGranularityLevel(order[next]);
        return;
      }
      if (e.key === 'Escape') {
        clearSelection();
        return;
      }
    }
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [granularityLevel, setGranularityLevel, clearSelection]);

  // Tick range for the touch-count fold. Null when no time selection — caller defaults to "all rows".
  const tickRange = useMemo(
    () => timeToTickRange(time, metadata?.tickSummaries),
    [time, metadata],
  );

  const touchSlice = useMemo(() => {
    const all = (metadata?.systemArchetypeTouches ?? []) as SystemArchetypeTouchSummary[];
    if (all.length === 0) return [];
    const slice = findTickRangeSlice(all, tickRange);
    return all.slice(slice.startIdx, slice.endIdx);
  }, [metadata, tickRange]);

  // Build base matrix.
  const baseMatrix = useMemo(
    () => buildAccessMatrix(topology ?? null, granularityLevel, touchSlice),
    [topology, granularityLevel, touchSlice],
  );

  // Column ordering — switches between the two strategies. Memoize on the full set of inputs the
  // ordering depends on so we don't reorder on every (unrelated) parent re-render.
  const orderedColumns = useMemo(() => {
    if (colSort === 'cluster') return clusterReorderColumns(baseMatrix);
    const phases = topology?.phases ?? [];
    const predecessorsByName = new Map<string, string[]>();
    if (topology?.systems) {
      const indexToName = new Map<number, string>();
      for (const s of topology.systems) {
        if (!s.name) continue;
        const idx = typeof s.index === 'string' ? Number(s.index) : s.index;
        if (!Number.isFinite(idx)) continue;
        indexToName.set(idx, s.name);
      }
      for (const s of topology.systems) {
        if (!s.name) continue;
        const preds: string[] = [];
        for (const p of s.predecessors ?? []) {
          const pIdx = typeof p === 'string' ? Number(p) : p;
          if (!Number.isFinite(pIdx)) continue;
          const pName = indexToName.get(pIdx);
          if (pName) preds.push(pName);
        }
        predecessorsByName.set(s.name, preds);
      }
    }
    return orderColumnsByPhaseDependency(baseMatrix.columns, phases, predecessorsByName);
  }, [baseMatrix, colSort, topology]);

  // Row ordering — topology default (no reorder) or cosine cluster.
  const orderedRows = useMemo(() => {
    if (rowSort === 'cluster') return clusterReorderRows(baseMatrix);
    return baseMatrix.rows;
  }, [baseMatrix, rowSort]);

  // Final matrix passed to the renderer — same cells, freshly-ordered axes.
  const matrix = useMemo(
    () => ({ rows: orderedRows, columns: orderedColumns, cells: baseMatrix.cells }),
    [orderedRows, orderedColumns, baseMatrix.cells],
  );

  if (!topology) {
    return (
      <div className="flex h-full w-full items-center justify-center bg-background text-sm text-muted-foreground">
        Loading topology…
      </div>
    );
  }

  function onSelectTrack(trackId: string) {
    // Toggle off when clicking the already-selected row.
    if (dataTrack && dataTrack.id === trackId) {
      setDataTrack(null);
      return;
    }
    const track = matrix.rows.find((r) => r.id === trackId);
    if (!track) return;
    const projection = trackToDataTrackSelection(track);
    if (!projection) return;
    setDataTrack(projection);
  }

  function onSelectPhase(phaseName: string) {
    // Toggle off when clicking the already-selected phase.
    setPhase(selectedPhase === phaseName ? null : phaseName);
  }

  return (
    <div className="flex h-full w-full flex-col overflow-hidden bg-background text-foreground">
      <AccessMatrixToolbar />
      <div className="min-h-0 flex-1">
        <AccessMatrixGrid
          matrix={matrix}
          phases={topology.phases ?? []}
          selectedSystem={selectedSystem}
          selectedTrackId={dataTrack?.id ?? null}
          selectedPhase={selectedPhase}
          hoveredSystem={hoveredKey?.systemName ?? null}
          onSelectSystem={setSelectedSystem}
          onSelectTrack={onSelectTrack}
          onSelectPhase={onSelectPhase}
        />
      </div>
    </div>
  );
}
