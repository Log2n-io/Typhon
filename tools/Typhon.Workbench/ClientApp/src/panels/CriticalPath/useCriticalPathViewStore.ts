import { create } from 'zustand';
import { createJSONStorage, persist } from 'zustand/middleware';

/**
 * View state for the dedicated Critical-Path panel. The panel reads its **tick** from
 * `useSelectionStore` (cross-panel binding — same as the System DAG aggregation range), so the
 * tick / range slots do NOT live here. This store owns purely visual concerns:
 *
 * - **orientation** — bars flow left→right (`horizontal`) or top→bottom (`vertical`).
 * - **pxPerUs** — zoom factor: pixels per microsecond on the major (time) axis. Truly unbounded.
 *   Wheel-zoom multiplies this; "Fit" recomputes it from the current viewport size.
 *
 * Persisted across sessions because user preference is sticky (same pattern as `useThemeStore`
 * and `useDagViewStore`).
 */
/**
 * Orientation mode. `auto` (default) picks horizontal or vertical at runtime based on the
 * viewport's width-to-height ratio — wider docks land on horizontal, taller docks land on
 * vertical. `horizontal` / `vertical` lock the choice regardless of dock shape.
 */
export type Orientation = 'auto' | 'horizontal' | 'vertical';

export interface CriticalPathViewState {
  orientation: Orientation;
  pxPerUs: number;
  /**
   * When `false` (default), the panel auto-fits the timeline whenever the displayed tick changes
   * — fresh tick → fresh wall-clock total → previous `pxPerUs` is almost always the wrong scale.
   * When `true`, the user's manual zoom is preserved across tick changes, which matters when
   * scrubbing the profiler to compare a specific phase / system across many ticks at the same
   * scale.
   */
  lockZoom: boolean;
  /**
   * When `true`, the tape includes every system that ran in each phase — not just the ones on the
   * critical path. Non-CP bars render dimmed alongside CP bars so the user can see the rest of
   * the work without losing the CP focus. Off by default per `09-system-dag.md §5.6` (the CP
   * focus is the primary diagnostic, full Gantt is the "what else ran" pivot).
   */
  fullGantt: boolean;
  /**
   * Aggregate vs single-tick. When `true`, the tape shows means across the selected tick range
   * instead of one representative tick. Per `09-system-dag.md §5.6` — useful for comparing the
   * "shape of a tick" across a window without scrubbing tick-by-tick.
   */
  aggregateMode: boolean;
  /**
   * Show the leading metronome-wait stripe in the tape. Off by default per
   * `09-system-dag.md §5.4` — the metronome wait is "what the engine wasn't doing", noise to most
   * investigations. Flip on to investigate engine throttling / sleep behaviour, paired with the
   * intent-class chip surfaced on the stripe.
   */
  showMetronome: boolean;
  /**
   * Critical-path track scope (#354). `'all'` walks every in-scope track and concatenates their
   * per-track chains in track order; a track name scopes the view to that track alone. Persisted;
   * an unknown name (a scope persisted from a different trace) degrades to `'all'` in the
   * algorithm. See `09-system-dag.md §5`.
   */
  trackScope: string;
  setOrientation: (orientation: Orientation) => void;
  setPxPerUs: (pxPerUs: number) => void;
  setLockZoom: (lock: boolean) => void;
  setFullGantt: (full: boolean) => void;
  setAggregateMode: (agg: boolean) => void;
  setShowMetronome: (show: boolean) => void;
  setTrackScope: (scope: string) => void;
  /**
   * Multiply zoom by `factor` — used by the wheel handler. Caller is responsible for any scroll
   * compensation needed to keep the cursor anchored.
   */
  zoomBy: (factor: number) => void;
}

// Default: 0.05 px/µs = 50 px/ms. A typical 16 ms tick = 800 px on the major axis — fits a normal
// viewport with room to scroll. User wheel-zoom adjusts from there.
const DEFAULT_PX_PER_US = 0.05;

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

export const useCriticalPathViewStore = create<CriticalPathViewState>()(
  persist(
    (set) => ({
      orientation: 'auto',
      pxPerUs: DEFAULT_PX_PER_US,
      lockZoom: false,
      fullGantt: false,
      aggregateMode: false,
      showMetronome: false,
      trackScope: 'all',
      setOrientation: (orientation) => set({ orientation }),
      setPxPerUs: (pxPerUs) => set({ pxPerUs: Math.max(1e-6, pxPerUs) }),
      setLockZoom: (lockZoom) => set({ lockZoom }),
      setFullGantt: (fullGantt) => set({ fullGantt }),
      setAggregateMode: (aggregateMode) => set({ aggregateMode }),
      setShowMetronome: (showMetronome) => set({ showMetronome }),
      setTrackScope: (trackScope) => set({ trackScope }),
      zoomBy: (factor) => set((state) => ({ pxPerUs: Math.max(1e-6, state.pxPerUs * factor) })),
    }),
    { name: 'typhon-cp-view', storage: safeStorage },
  ),
);
