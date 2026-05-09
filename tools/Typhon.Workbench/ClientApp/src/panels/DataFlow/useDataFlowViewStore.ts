import { create } from 'zustand';
import { createJSONStorage, persist } from 'zustand/middleware';

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

export interface DataFlowViewState {
  /** Y-axis altitude. Default L2 (Component-family) per design D9 — right altitude for "what's happening to my data". */
  granularityLevel: GranularityLevel;
  /** Phase column scaling along the X axis. */
  xMode: XAxisMode;
  /** Phase names that the user has manually collapsed (to a thin summary strip). */
  collapsedPhases: string[];
  /**
   * When true, hovering a bar dims every other bar that doesn't share its (system, tick) key. The v1
   * unification mechanism per design D3 — the bridge that makes per-track bars feel unified without
   * committing to a multi-row custom renderer. Default ON; can be turned off via the H key for users
   * who find it noisy.
   */
  hoverIsolateEnabled: boolean;
  setGranularityLevel: (level: GranularityLevel) => void;
  setXMode: (mode: XAxisMode) => void;
  togglePhaseCollapsed: (phaseName: string) => void;
  setHoverIsolateEnabled: (enabled: boolean) => void;
}

// SSR/test-safe localStorage wrapper — same shape as `useThemeStore` / `useDagViewStore`.
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

export const useDataFlowViewStore = create<DataFlowViewState>()(
  persist(
    (set, get) => ({
      granularityLevel: 'L2',
      xMode: 'uniform',
      collapsedPhases: [],
      hoverIsolateEnabled: true,
      setGranularityLevel: (granularityLevel) => set({ granularityLevel }),
      setXMode: (xMode) => set({ xMode }),
      togglePhaseCollapsed: (phaseName) => {
        const current = get().collapsedPhases;
        const idx = current.indexOf(phaseName);
        if (idx === -1) {
          set({ collapsedPhases: [...current, phaseName] });
        } else {
          set({ collapsedPhases: current.filter((p) => p !== phaseName) });
        }
      },
      setHoverIsolateEnabled: (hoverIsolateEnabled) => set({ hoverIsolateEnabled }),
    }),
    { name: 'typhon-dataflow-view', storage: safeStorage },
  ),
);
