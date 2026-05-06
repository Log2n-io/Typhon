import { create } from 'zustand';
import { createJSONStorage, persist } from 'zustand/middleware';

/**
 * Panel-local view state for the System DAG. After cross-panel binding (per `09-system-dag.md §7.1`)
 * the **tick range no longer lives here** — it's derived from {@link useSelectionStore.time}, which
 * is the single source of truth shared with the profiler's TimeArea. This store now holds:
 *
 * - **stat mode** (mean / p50 / p95 / p99 / max) — drives the per-node primary stat per §6.1.
 * - **layout mode** — chooses how nodes occupy the canvas. Persisted across sessions because user
 *   layout preference is sticky (per the `useThemeStore` precedent).
 *
 * The {@link TickRange} type is still exported here because every downstream consumer
 * (`useSystemStats`, `useQueueBackpressure`, `criticalPath`) takes a tick-numbered range — the panel
 * converts the µs `useSelectionStore.time` to ticks at the boundary via {@link tickRangeMapping}.
 */
export type StatMode = 'mean' | 'p50' | 'p95' | 'p99' | 'max';

/**
 * Available DAG layouts. Phase-aware ones (`horizontal-lanes`, `vertical-lanes`) preserve the
 * design's swim-lane skeleton (§4.1 — phases ARE the structural mental model). Phase-agnostic ones
 * (`compact`, `circular`) drop the lanes for cases where the user wants a different visual angle on
 * the same topology. `compact` additionally surfaces cross-phase edges, which the swim-lane
 * layouts hide as O(systems²) noise.
 */
export type LayoutMode = 'horizontal-lanes' | 'vertical-lanes' | 'compact' | 'circular';

export interface TickRange {
  /** Inclusive first tick. */
  from: number;
  /** Inclusive last tick. */
  to: number;
}

export interface DagViewState {
  /** Primary stat shown on each node tile and used for heat colouring. */
  statMode: StatMode;
  /** Node placement strategy — see {@link LayoutMode}. */
  layout: LayoutMode;
  setStatMode: (mode: StatMode) => void;
  setLayout: (layout: LayoutMode) => void;
}

// SSR/test-safe localStorage wrapper — same shape as `useThemeStore`.
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

export const useDagViewStore = create<DagViewState>()(
  persist(
    (set) => ({
      statMode: 'mean',
      layout: 'horizontal-lanes',
      setStatMode: (statMode) => set({ statMode }),
      setLayout: (layout) => set({ layout }),
    }),
    { name: 'typhon-dag-view', storage: safeStorage },
  ),
);
