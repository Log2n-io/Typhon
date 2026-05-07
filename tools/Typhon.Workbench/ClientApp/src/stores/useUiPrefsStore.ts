import { create } from 'zustand';
import { createJSONStorage, persist } from 'zustand/middleware';

/**
 * App-wide UI preferences shared across panels.
 *
 * `legendsVisible` started life inside `useProfilerViewStore` (the profiler's `l` keybind toggled
 * it for the gauge / span legends), but every panel that overlays user-help affordances —
 * Critical Path, future System DAG legends, etc. — needs the same toggle. Promoting it here lets a
 * single command (`Toggle Legends` in the palette + the `l` keybind) drive every panel's "show
 * inline help" state, instead of each panel rolling its own.
 */
interface UiPrefsState {
  /** Inline legends + per-panel "?" help glyph visibility. App-wide. */
  legendsVisible: boolean;
  toggleLegends: () => void;
  setLegendsVisible: (visible: boolean) => void;
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

export const useUiPrefsStore = create<UiPrefsState>()(
  persist(
    (set) => ({
      legendsVisible: true,
      toggleLegends: () => set((s) => ({ legendsVisible: !s.legendsVisible })),
      setLegendsVisible: (legendsVisible) => set({ legendsVisible }),
    }),
    { name: 'workbench-ui-prefs', storage: safeStorage },
  ),
);
