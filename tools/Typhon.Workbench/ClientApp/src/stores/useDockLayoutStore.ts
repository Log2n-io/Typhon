import { create } from 'zustand';
import { createJSONStorage, persist } from 'zustand/middleware';
import type { SessionKind } from '@/stores/useSessionStore';

interface DockLayoutState {
  layouts: Record<string, unknown>;
  save: (key: string, layout: unknown) => void;
  get: (key: string) => unknown | null;
  saveTemplate: (kind: SessionKind, layout: unknown) => void;
  getTemplate: (kind: SessionKind) => unknown | null;
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
      saveTemplate: (kind, layout) =>
        set((s) => ({ layouts: { ...s.layouts, [`__template__:${kind}`]: layout } })),
      getTemplate: (kind) => get().layouts[`__template__:${kind}`] ?? null,
      clear: () => set({ layouts: {} }),
    }),
    { name: 'typhon-dock-layouts-v4', storage: safeStorage },
  ),
);
