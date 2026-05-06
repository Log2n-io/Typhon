import { create } from 'zustand';
import { createJSONStorage, persist } from 'zustand/middleware';

/**
 * View state for the dedicated Critical-Path panel. The panel reads its **tick** from
 * `useSelectionStore` (cross-panel binding — same as the System DAG aggregation range), so the
 * tick / range slots do NOT live here. This store owns purely visual concerns:
 *
 * - **orientation** — bars flow left→right (`horizontal`) or top→bottom (`vertical`).
 * - **scale** — `linear` or `log`. Log helps when one phase dwarfs the others.
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
export type CpScale = 'linear' | 'log';

export interface CriticalPathViewState {
  orientation: Orientation;
  scale: CpScale;
  pxPerUs: number;
  /**
   * When `false` (default), the panel auto-fits the timeline whenever the displayed tick changes
   * — fresh tick → fresh wall-clock total → previous `pxPerUs` is almost always the wrong scale.
   * When `true`, the user's manual zoom is preserved across tick changes, which matters when
   * scrubbing the profiler to compare a specific phase / system across many ticks at the same
   * scale.
   */
  lockZoom: boolean;
  setOrientation: (orientation: Orientation) => void;
  setScale: (scale: CpScale) => void;
  setPxPerUs: (pxPerUs: number) => void;
  setLockZoom: (lock: boolean) => void;
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
      scale: 'linear',
      pxPerUs: DEFAULT_PX_PER_US,
      lockZoom: false,
      setOrientation: (orientation) => set({ orientation }),
      setScale: (scale) => set({ scale }),
      setPxPerUs: (pxPerUs) => set({ pxPerUs: Math.max(1e-6, pxPerUs) }),
      setLockZoom: (lockZoom) => set({ lockZoom }),
      zoomBy: (factor) => set((state) => ({ pxPerUs: Math.max(1e-6, state.pxPerUs * factor) })),
    }),
    { name: 'typhon-cp-view', storage: safeStorage },
  ),
);
