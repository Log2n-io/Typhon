import { create } from 'zustand';
import { createJSONStorage, persist } from 'zustand/middleware';

/** Display density — one global, token-driven setting (DS-1). Affects list/tree row heights + spacing. */
export type Density = 'compact' | 'comfortable';

/** Row height (px) per density. Read by virtualized lists / trees so they re-measure on a density change. */
export const DENSITY_ROW_HEIGHT: Record<Density, number> = {
  compact: 22,
  comfortable: 28,
};

interface DensityState {
  mode: Density;
  setMode: (mode: Density) => void;
  toggle: () => void;
}

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

export const useDensityStore = create<DensityState>()(
  persist(
    (set, get) => ({
      mode: 'compact',
      setMode: (mode) => {
        if (get().mode === mode) return;
        set({ mode });
      },
      toggle: () => set((s) => ({ mode: s.mode === 'compact' ? 'comfortable' : 'compact' })),
    }),
    { name: 'typhon-density-v1', storage: safeStorage },
  ),
);
