import { create } from 'zustand';
import { persist } from 'zustand/middleware';
import { safeStorage } from './safeStorage';

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
