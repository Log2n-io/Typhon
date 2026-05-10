import { create } from 'zustand';
import { createJSONStorage, persist } from 'zustand/middleware';
import type { GranularityLevel } from '@/panels/DataFlow/useDataFlowViewStore';

/**
 * Panel-local view state for the Access Matrix. Cross-panel state (selected system, time range) lives in
 * {@link useSelectionStore}; this store carries only what's specific to this panel.
 *
 * Persisted to localStorage so users keep their preferred altitude + sort across sessions, mirroring
 * `useDagViewStore` / `useDataFlowViewStore`. Storage key: `typhon-access-matrix-view`.
 *
 * The {@link GranularityLevel} type is shared with the Data Flow Timeline — same Y-axis altitude semantics, same
 * 5 stops, so users carry their mental model across both panels.
 */

/**
 * Column ordering strategies.
 *
 * - <b>phase-then-dependency</b> — group columns by phase (matching System DAG lane colours), then within each
 *   phase sort by topological dependency order (root systems first, leaves last). Default and most readable.
 * - <b>cluster</b> — reorder via cosine similarity over (system × component access vectors). Groups systems
 *   with similar data needs adjacent to each other. Useful for spotting "these systems all hammer the same
 *   data" patterns; degrades gracefully when access vectors are sparse.
 */
export type ColumnSort = 'phase-then-dependency' | 'cluster';

/**
 * Row ordering strategies.
 *
 * - <b>topology</b> — declaration order (matches the Data Flow Timeline's row order via `trackBuilding`). Default.
 * - <b>cluster</b> — reorder by cosine similarity over rows' access vectors (which systems touched the row).
 *   Mirrors the column cluster reorder.
 */
export type RowSort = 'topology' | 'cluster';

export interface AccessMatrixViewState {
  /** Y-axis altitude — mirrors Data Flow Timeline's level. Default L2 (Component-family) per design D9. */
  granularityLevel: GranularityLevel;
  /** Row ordering — see {@link RowSort}. */
  rowSort: RowSort;
  /** Column ordering — see {@link ColumnSort}. */
  colSort: ColumnSort;
  /** Phase names that the user has manually collapsed to a single aggregate column (per design §6.2). */
  collapsedPhases: string[];
  setGranularityLevel: (level: GranularityLevel) => void;
  setRowSort: (sort: RowSort) => void;
  setColSort: (sort: ColumnSort) => void;
  togglePhaseCollapsed: (phaseName: string) => void;
}

// SSR/test-safe localStorage wrapper — same shape as the System DAG / Data Flow stores.
const safeStorage = createJSONStorage(() => ({
  getItem: (name: string) => {
    try { return localStorage.getItem(name); } catch { return null; }
  },
  setItem: (name: string, value: string) => {
    try { localStorage.setItem(name, value); } catch { /* noop */ }
  },
  removeItem: (name: string) => {
    try { localStorage.removeItem(name); } catch { /* noop */ }
  },
}));

export const useAccessMatrixViewStore = create<AccessMatrixViewState>()(
  persist(
    (set, get) => ({
      granularityLevel: 'L2',
      rowSort: 'topology',
      colSort: 'phase-then-dependency',
      collapsedPhases: [],
      setGranularityLevel: (granularityLevel) => set({ granularityLevel }),
      setRowSort: (rowSort) => set({ rowSort }),
      setColSort: (colSort) => set({ colSort }),
      togglePhaseCollapsed: (phaseName) => {
        const current = get().collapsedPhases;
        const idx = current.indexOf(phaseName);
        if (idx === -1) {
          set({ collapsedPhases: [...current, phaseName] });
        } else {
          set({ collapsedPhases: current.filter((p) => p !== phaseName) });
        }
      },
    }),
    { name: 'typhon-access-matrix-view', storage: safeStorage },
  ),
);
