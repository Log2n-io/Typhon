import { create } from 'zustand';
import { persist } from 'zustand/middleware';
import { safeStorage } from './safeStorage';
import type { CallTreeViewMode } from '@/hooks/profiler/useCallTree';

/**
 * Fold direction for the Call Tree panel (§8.7). top-down = callees · bottom-up = callers · sandwich = both
 * around the drilled frame. Wider than the server-side `CallTreeDirection` (in `@/hooks/profiler/useCallTree`),
 * which is 'top-down' | 'bottom-up' only — sandwich is a UI composition of the two and never reaches the wire.
 */
export type CallTreeFoldDirection = 'top-down' | 'bottom-up' | 'sandwich';

/**
 * Persisted UX preferences for the Call Tree panel (PC-1 / AC3.16). The Call Tree's *scope* is intentionally
 * session-scoped (see {@link useCallTreeScopeStore} — it carries a trace-specific span/system/window selection
 * via `ownerSessionId`), but its lenses — `viewMode` (the off-CPU split: `wall-clock` includes blocked time,
 * `on-cpu` separates it) and `direction` (top-down / bottom-up / sandwich) — are user-of-the-tool preferences
 * that should survive a Workbench reload and a session change.
 *
 * Kept separate from {@link useProfilerViewStore} (timeline-centric prefs) and {@link useCallTreeScopeStore}
 * (the session-scoped scope) so each store owns one concern.
 */
interface CallTreePrefsState {
  viewMode: CallTreeViewMode;
  direction: CallTreeFoldDirection;
  /** Group frames by category (lock / IO / GC / user code / …) — an organisational lens, not data. */
  groupByCategory: boolean;
  setViewMode: (m: CallTreeViewMode) => void;
  setDirection: (d: CallTreeFoldDirection) => void;
  setGroupByCategory: (v: boolean) => void;
}

export const useCallTreePrefsStore = create<CallTreePrefsState>()(
  persist(
    (set) => ({
      viewMode: 'wall-clock',
      direction: 'top-down',
      groupByCategory: false,
      setViewMode: (viewMode) => set({ viewMode }),
      setDirection: (direction) => set({ direction }),
      setGroupByCategory: (groupByCategory) => set({ groupByCategory }),
    }),
    {
      name: 'workbench-call-tree-prefs',
      storage: safeStorage,
      version: 1,
    },
  ),
);
