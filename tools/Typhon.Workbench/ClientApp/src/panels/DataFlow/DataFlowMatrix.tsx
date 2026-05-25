import { useMemo } from 'react';
import type { SystemArchetypeTouchSummary } from '@/api/generated/model/systemArchetypeTouchSummary';
import type { TopologyDto } from '@/api/generated/model/topologyDto';
import { useSelectionStore } from '@/stores/useSelectionStore';
import AccessMatrixGrid from './AccessMatrixGrid';
import { buildAccessMatrix } from './matrixBuilding';
import { clusterReorderColumns, clusterReorderRows, orderColumnsByPhaseDependency } from './matrixOrdering';
import { trackToDataTrackSelection } from './trackBuilding';
import { type GranularityLevel, useDataFlowViewStore } from './useDataFlowViewStore';

/**
 * Matrix mode of the Data Flow view — the absorbed Access Matrix (the 2→1 consolidation, [data-flow.md §1]). A
 * system×data-track heatmap of access kinds over the *same* `systemArchetypeTouches` slice the Timeline mode uses:
 * the parent (`DataFlowPanel`) passes the already-fetched topology, the shared granularity, and the visible-range
 * touch slice, so the two modes can never diverge. Selection is the bus (`system`/`dataTrack`/`phase`), so toggling
 * back to Timeline keeps the highlight. Row/column ordering is the only matrix-specific state ({@link useDataFlowViewStore}).
 */
export default function DataFlowMatrix({
  topology,
  granularityLevel,
  touchSlice,
}: {
  topology: TopologyDto;
  granularityLevel: GranularityLevel;
  touchSlice: SystemArchetypeTouchSummary[];
}) {
  const rowSort = useDataFlowViewStore((s) => s.rowSort);
  const colSort = useDataFlowViewStore((s) => s.colSort);

  const selectedSystem = useSelectionStore((s) => s.system);
  const setSelectedSystem = useSelectionStore((s) => s.setSystem);
  const dataTrack = useSelectionStore((s) => s.dataTrack);
  const setDataTrack = useSelectionStore((s) => s.setDataTrack);
  const selectedPhase = useSelectionStore((s) => s.phase);
  const setPhase = useSelectionStore((s) => s.setPhase);
  const hoveredKey = useSelectionStore((s) => s.hoveredSystemTickKey);

  const baseMatrix = useMemo(
    () => buildAccessMatrix(topology, granularityLevel, touchSlice),
    [topology, granularityLevel, touchSlice],
  );

  // Column ordering — phase-then-dependency (default, matches the DAG swim-lanes) or cosine cluster.
  const orderedColumns = useMemo(() => {
    if (colSort === 'cluster') {
      return clusterReorderColumns(baseMatrix);
    }
    const phases = topology.phases ?? [];
    const indexToName = new Map<number, string>();
    for (const s of topology.systems ?? []) {
      if (!s.name) continue;
      const idx = typeof s.index === 'string' ? Number(s.index) : s.index;
      if (!Number.isFinite(idx)) continue;
      indexToName.set(idx, s.name);
    }
    const predecessorsByName = new Map<string, string[]>();
    for (const s of topology.systems ?? []) {
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
    return orderColumnsByPhaseDependency(baseMatrix.columns, phases, predecessorsByName);
  }, [baseMatrix, colSort, topology]);

  // Row ordering — topology declaration order (default) or cosine cluster.
  const orderedRows = useMemo(() => {
    if (rowSort === 'cluster') {
      return clusterReorderRows(baseMatrix);
    }
    return baseMatrix.rows;
  }, [baseMatrix, rowSort]);

  const matrix = useMemo(
    () => ({ rows: orderedRows, columns: orderedColumns, cells: baseMatrix.cells }),
    [orderedRows, orderedColumns, baseMatrix.cells],
  );

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
    setPhase(selectedPhase === phaseName ? null : phaseName);
  }

  return (
    <div className="min-h-0 flex-1" data-testid="data-flow-matrix">
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
  );
}
