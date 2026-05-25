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
  /**
   * Hide systems that were 100%-skipped over the selected tick range. When ON, any node whose
   * <code>skipRate &gt;= 1</code> (no executions at all) drops out of the canvas, along with any
   * edges that referenced it. Useful for narrowing the DAG to "what actually ran" when zoomed
   * into a small tick window where high-tier systems with cell-amortise schedules don't fire.
   * Has no effect when no time range is selected (skip rates are unknown).
   */
  hideSkipped: boolean;
  /**
   * In the swim-lane layouts (horizontal-lanes / vertical-lanes), draw edges that span phases.
   * Default OFF because lane order already encodes phase ordering and fully-connected
   * cross-phase chains are visually noisy. Turn ON when investigating a specific cross-phase
   * data dependency (e.g. "why is Metabolism_T0 waiting on MoveAll?"). Compact and circular
   * layouts always show every edge; this toggle is a no-op there.
   */
  showCrossPhaseEdges: boolean;
  /**
   * Ephemeral "reveal this system" signal. A handoff (e.g. the Inspector's *Reveal in System DAG* verb) writes a
   * system name here; the canvas consumes it on the next render to centre + fit that node, then clears it. Distinct
   * from the bus `System` highlight so an ordinary cross-panel selection never yanks the viewport — only an explicit
   * reveal recentres. Session-scoped: never persisted (excluded from {@link partialize}).
   */
  pendingFocusSystem: string | null;
  setStatMode: (mode: StatMode) => void;
  setLayout: (layout: LayoutMode) => void;
  setHideSkipped: (hide: boolean) => void;
  setShowCrossPhaseEdges: (show: boolean) => void;
  /** Request the canvas to centre + fit the named system (the *Reveal in System DAG* handoff). */
  requestFocusSystem: (name: string) => void;
  /** Clear the pending reveal once the canvas has acted on it. */
  clearPendingFocusSystem: () => void;
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
      hideSkipped: false,
      showCrossPhaseEdges: false,
      pendingFocusSystem: null,
      setStatMode: (statMode) => set({ statMode }),
      setLayout: (layout) => set({ layout }),
      setHideSkipped: (hideSkipped) => set({ hideSkipped }),
      setShowCrossPhaseEdges: (showCrossPhaseEdges) => set({ showCrossPhaseEdges }),
      requestFocusSystem: (name) => set({ pendingFocusSystem: name }),
      clearPendingFocusSystem: () => set({ pendingFocusSystem: null }),
    }),
    {
      name: 'typhon-dag-view',
      storage: safeStorage,
      // Persist only the sticky view preferences. pendingFocusSystem is an ephemeral handoff signal — persisting it
      // would replay a stale reveal on the next open.
      partialize: (s) => ({
        statMode: s.statMode,
        layout: s.layout,
        hideSkipped: s.hideSkipped,
        showCrossPhaseEdges: s.showCrossPhaseEdges,
      }),
    },
  ),
);
