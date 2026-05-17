import { create } from 'zustand';
import { createJSONStorage, persist } from 'zustand/middleware';

/**
 * Cross-panel view options shared by the System DAG and Critical Path panels — and edited from the
 * Options panel's "DAG" section.
 *
 * Currently a single setting: {@link ViewOptionsState.showEngineSystems}. It lives in its own
 * store (rather than a panel-local one like `useDagViewStore`) because both panels honour it: the
 * System DAG hides engine-tagged tracks/DAGs, the Critical Path drops them from its track selector
 * and "All" scope. A pure client UI preference — persisted to localStorage, not a server-backed
 * `WorkbenchOptions` field.
 */
export interface ViewOptionsState {
  /**
   * Reveal engine-internal tracks (Engine-Pre, Engine-Post / Fence — `engine`-tagged) and their
   * systems. Default OFF — the engine's own DAGs are infrastructure noise for app-level work.
   * Keyed off the track's `engine` tag, never its name (#354).
   */
  showEngineSystems: boolean;
  setShowEngineSystems: (show: boolean) => void;
}

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

export const useViewOptionsStore = create<ViewOptionsState>()(
  persist(
    (set) => ({
      showEngineSystems: false,
      setShowEngineSystems: (showEngineSystems) => set({ showEngineSystems }),
    }),
    { name: 'typhon-view-options', storage: safeStorage },
  ),
);
