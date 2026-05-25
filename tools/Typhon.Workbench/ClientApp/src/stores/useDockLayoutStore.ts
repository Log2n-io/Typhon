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
    // v8: Stage 3 Phase 1 made the Profiler timeline the trace/attach default center (a no-position addPanel
    // had docked it into the left edge once the view was un-gated). Bumping the key discards v7 trace/attach
    // layouts that captured the buggy left placement, so the centered timeline loads on first open rather than
    // only after Reset Layout. (v7 = Stage 2 Schema Explorer center; v6 = Stage 1 navigator; v5 = Stage 0.)
    { name: 'typhon-dock-layouts-v8', storage: safeStorage },
  ),
);
