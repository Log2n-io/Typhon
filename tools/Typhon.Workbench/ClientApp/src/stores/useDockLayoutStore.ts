import { create } from 'zustand';
import { createJSONStorage, persist } from 'zustand/middleware';

interface DockLayoutState {
  layouts: Record<string, unknown>;
  save: (key: string, layout: unknown) => void;
  get: (key: string) => unknown | null;
  clear: () => void;
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

export const useDockLayoutStore = create<DockLayoutState>()(
  persist(
    (set, get) => ({
      layouts: {},
      save: (key, layout) =>
        set((s) => ({ layouts: { ...s.layouts, [key]: layout } })),
      get: (key) => get().layouts[key] ?? null,
      clear: () => set({ layouts: {} }),
    }),
    { name: 'typhon-dock-layouts-v3', storage: safeStorage },
  ),
);
