import { create } from 'zustand';
import { persist } from 'zustand/middleware';
import { safeStorage } from './safeStorage';

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


export const useViewOptionsStore = create<ViewOptionsState>()(
  persist(
    (set) => ({
      showEngineSystems: false,
      setShowEngineSystems: (showEngineSystems) => set({ showEngineSystems }),
    }),
    { name: 'typhon-view-options', storage: safeStorage },
  ),
);
