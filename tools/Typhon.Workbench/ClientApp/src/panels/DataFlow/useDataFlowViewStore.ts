import { create } from 'zustand';
import { persist } from 'zustand/middleware';
import { safeStorage } from '@/stores/safeStorage';

/**
 * Panel-local view state for the Data Flow Timeline. The shared cross-panel state (tick range, selected
 * system) lives in {@link useSelectionStore}; this store carries only what's specific to this panel:
 * granularity altitude, X-axis layout mode, phase collapse choices, and the hover-isolate escape hatch.
 *
 * Persisted to localStorage so users keep their preferred altitude across sessions, mirroring
 * `useDagViewStore`. Storage key: `typhon-dataflow-view`.
 */
export type GranularityLevel = 'L0' | 'L1' | 'L2' | 'L3' | 'L4';

/**
 * X-axis layout modes per design §6.1.
 *
 * - <b>uniform</b> — phase columns sized proportional to wall-clock contribution (default; honest representation).
 * - <b>equal</b>   — each phase gets <code>1/N</code> of screen width (better for "is each phase efficient?").
 * - <b>log</b>     — log-time compression of the dominant phase so smaller phases stay readable.
 */
export type XAxisMode = 'uniform' | 'equal' | 'log';

/**
 * Aggregation modes (X axis), orthogonal to {@link GranularityLevel}. Per spec §7.
 *
 * - <b>replay</b>   — single-tick replay on the dominant tick of the selection (default per D8).
 *                     Bars are actual `[startUs, endUs]` of that one tick mapped through the phase axis.
 * - <b>envelope</b> — p5–p95 envelope over the selected ticks. Per (system, track), the bar is the rectangle
 *                     covering the 90th-percentile band of `(startUs, endUs)` across the range. Answers
 *                     "is this stable or jittery across ticks?".
 * - <b>density</b>  — heat strip per (track, phase) — touch density across the range, no per-system bars.
 *                     Answers "where in the data is the workload concentrated over time?".
 */
export type AggregationMode = 'replay' | 'envelope' | 'density';

/**
 * View mode — the two renderings of the same `systemArchetypeTouches` dataset, merged from the former Data Flow +
 * Access Matrix panels ([data-flow.md §1](../../../../claude/design/Apps/Workbench/views/data-flow.md)).
 *
 * - <b>timeline</b> — Marey bars over phase-segmented time (temporal data contention). Default.
 * - <b>matrix</b>   — the absorbed Access Matrix: a system×data-track heatmap of access kinds (the access structure).
 *
 * Mode is a PC-1 preference (persisted). Selection lives on the bus, so toggling modes preserves it.
 */
export type DataFlowMode = 'timeline' | 'matrix';

/**
 * Matrix-mode column ordering (formerly the Access Matrix's `ColumnSort`).
 * - <b>phase-then-dependency</b> — group by phase (matching the System DAG lanes), then topological order. Default.
 * - <b>cluster</b> — cosine-similarity reorder grouping systems with similar data needs.
 */
export type ColumnSort = 'phase-then-dependency' | 'cluster';

/**
 * Matrix-mode row ordering (formerly the Access Matrix's `RowSort`).
 * - <b>topology</b> — declaration order (matches the Timeline's track order). Default.
 * - <b>cluster</b> — cosine-similarity reorder over rows' access vectors.
 */
export type RowSort = 'topology' | 'cluster';

export interface DataFlowViewState {
  /** Timeline ⇄ Matrix mode (PC-1). The two renderings of one dataset; selection (on the bus) survives the toggle. */
  mode: DataFlowMode;
  /** Y-axis altitude. Default L2 (Component-family) per design D9 — right altitude for "what's happening to my data". */
  granularityLevel: GranularityLevel;
  /** Phase column scaling along the X axis. */
  xMode: XAxisMode;
  /** Aggregation mode along the X axis. Default `replay` per design D8. */
  aggMode: AggregationMode;
  /**
   * Phase names the user has explicitly collapsed (forced thin even when wall-clock-significant). Mutually
   * exclusive with {@link manuallyExpandedPhases} — clicking a phase toggles between the two states.
   */
  collapsedPhases: string[];
  /**
   * Phase names the user has explicitly expanded (kept wide even when D10 auto-collapse would normally
   * thin them). Required because auto-collapse fires on every render — without this set, the user couldn't
   * keep a small phase open across re-renders.
   */
  manuallyExpandedPhases: string[];
  /** When true, tracks with zero bars in the current tick are hidden. Per design §7 filter chips. */
  hideUntouched: boolean;
  /** When true, systems whose summary reports skipReason !== None render dimmed. Per design §7 filter chips. */
  dimSkipped: boolean;
  /**
   * When true, hovering a bar dims every other bar that doesn't share its (system, tick) key. The v1
   * unification mechanism per design D3 — the bridge that makes per-track bars feel unified without
   * committing to a multi-row custom renderer. Default ON; can be turned off via the H key for users
   * who find it noisy.
   */
  hoverIsolateEnabled: boolean;
  /** Matrix-mode row ordering. Only consulted in `matrix` mode. */
  rowSort: RowSort;
  /** Matrix-mode column ordering. Only consulted in `matrix` mode. */
  colSort: ColumnSort;
  setMode: (mode: DataFlowMode) => void;
  setGranularityLevel: (level: GranularityLevel) => void;
  setXMode: (mode: XAxisMode) => void;
  setAggMode: (mode: AggregationMode) => void;
  setRowSort: (sort: RowSort) => void;
  setColSort: (sort: ColumnSort) => void;
  /** Toggle phase between collapsed / expanded / default (auto). Click semantics: 1st click → collapse, 2nd → expand, 3rd → default. */
  cyclePhaseCollapse: (phaseName: string) => void;
  setHideUntouched: (hide: boolean) => void;
  setDimSkipped: (dim: boolean) => void;
  setHoverIsolateEnabled: (enabled: boolean) => void;
}


export const useDataFlowViewStore = create<DataFlowViewState>()(
  persist(
    (set, get) => ({
      mode: 'timeline',
      granularityLevel: 'L2',
      xMode: 'uniform',
      aggMode: 'replay',
      collapsedPhases: [],
      manuallyExpandedPhases: [],
      hideUntouched: false,
      dimSkipped: false,
      hoverIsolateEnabled: true,
      rowSort: 'topology',
      colSort: 'phase-then-dependency',
      setMode: (mode) => set({ mode }),
      setGranularityLevel: (granularityLevel) => set({ granularityLevel }),
      setXMode: (xMode) => set({ xMode }),
      setAggMode: (aggMode) => set({ aggMode }),
      setRowSort: (rowSort) => set({ rowSort }),
      setColSort: (colSort) => set({ colSort }),
      cyclePhaseCollapse: (phaseName) => {
        const { collapsedPhases, manuallyExpandedPhases } = get();
        const isCollapsed = collapsedPhases.includes(phaseName);
        const isExpanded = manuallyExpandedPhases.includes(phaseName);
        // Cycle: default (auto) → collapsed → expanded → default. The user clicks once to collapse a wide phase
        // (override auto-expand), again to force-expand a thin one, third time to return to D10 auto-collapse.
        if (!isCollapsed && !isExpanded) {
          set({ collapsedPhases: [...collapsedPhases, phaseName] });
        } else if (isCollapsed) {
          set({
            collapsedPhases: collapsedPhases.filter((p) => p !== phaseName),
            manuallyExpandedPhases: [...manuallyExpandedPhases, phaseName],
          });
        } else {
          set({ manuallyExpandedPhases: manuallyExpandedPhases.filter((p) => p !== phaseName) });
        }
      },
      setHideUntouched: (hideUntouched) => set({ hideUntouched }),
      setDimSkipped: (dimSkipped) => set({ dimSkipped }),
      setHoverIsolateEnabled: (hoverIsolateEnabled) => set({ hoverIsolateEnabled }),
    }),
    { name: 'typhon-dataflow-view', storage: safeStorage },
  ),
);
