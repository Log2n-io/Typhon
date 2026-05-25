import { create } from 'zustand';
import { persist } from 'zustand/middleware';
import { safeStorage } from './safeStorage';

/**
 * Display density — one global, token-driven setting (DS-1). Drives row heights, spacing, **and the `--fs-*`
 * font-size ramp** (globals.css), so a switch restyles list density + every label app-wide. Three modes:
 * `compact` (dense, good-eyes core users — today's pixel sizes), `normal` (the default, +1px), `comfortable` (+2px).
 */
export type Density = 'compact' | 'normal' | 'comfortable';

/** Row height (px) per density. Read by virtualized lists / trees so they re-measure on a density change. */
export const DENSITY_ROW_HEIGHT: Record<Density, number> = {
  compact: 22,
  normal: 25,
  comfortable: 28,
};

/** Cycle order for the palette/keyboard one-shot toggle. */
const CYCLE: Record<Density, Density> = {
  compact: 'normal',
  normal: 'comfortable',
  comfortable: 'compact',
};

interface DensityState {
  mode: Density;
  setMode: (mode: Density) => void;
  /** Advance compact → normal → comfortable → compact (palette command / keyboard). */
  cycle: () => void;
}


export const useDensityStore = create<DensityState>()(
  persist(
    (set, get) => ({
      mode: 'normal',
      setMode: (mode) => {
        if (get().mode === mode) return;
        set({ mode });
      },
      cycle: () => set((s) => ({ mode: CYCLE[s.mode] })),
    }),
    { name: 'typhon-density-v1', storage: safeStorage },
  ),
);
