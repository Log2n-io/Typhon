import { create } from 'zustand';
import { createJSONStorage, persist } from 'zustand/middleware';

/**
 * Persisted manual node positions for the System DAG. When the user Ctrl+drags a tile to
 * un-cover an edge or rearrange a phase, the new (x, y) goes here keyed by
 * `${layout}|${systemName}` — the same system has different "good" positions in
 * `horizontal-lanes` vs `vertical-lanes` vs `compact` vs `circular`, so the override is
 * per-layout. Layout switches don't bleed positions across each other.
 *
 * The render path applies the override AFTER dagre layout, not before — so dropping an
 * override (or clearing the whole layout) immediately snaps the system back to the
 * auto-computed position. There's no "merged" position concept; either the override exists
 * for `(layout, systemName)` or it doesn't.
 */

export interface NodePosition {
  x: number;
  y: number;
}

interface NodePositionsState {
  /** Keyed by `${layout}|${systemName}`. */
  overrides: Record<string, NodePosition>;
  /** Set or replace the manual position for a (layout, system) pair. */
  setOverride: (layout: string, systemName: string, pos: NodePosition) => void;
  /** Drop every override for the given layout. Other layouts' overrides untouched. */
  clearLayout: (layout: string) => void;
  /** Nuke all manual positions across all layouts. */
  clearAll: () => void;
  /** Return the override count for a layout — drives the "Reset positions" button visibility. */
  countForLayout: (layout: string) => number;
}

// SSR/test-safe localStorage wrapper — mirrors useThemeStore / useDagViewStore conventions.
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

function keyOf(layout: string, systemName: string): string {
  // encodeURIComponent on both halves so a system name that happens to contain `|` (or any other delimiter)
  // can't collide with a different (layout, system) pair. Cheap; only runs on overrides set / clear paths.
  return `${encodeURIComponent(layout)}|${encodeURIComponent(systemName)}`;
}

export const useNodePositionsStore = create<NodePositionsState>()(
  persist(
    (set, get) => ({
      overrides: {},
      setOverride: (layout, systemName, pos) =>
        set((state) => ({
          overrides: { ...state.overrides, [keyOf(layout, systemName)]: pos },
        })),
      clearLayout: (layout) =>
        set((state) => {
          // Match the encoded prefix produced by keyOf() so a layout name with reserved chars still groups correctly.
          const prefix = `${encodeURIComponent(layout)}|`;
          const next: Record<string, NodePosition> = {};
          for (const [k, v] of Object.entries(state.overrides)) {
            if (!k.startsWith(prefix)) next[k] = v;
          }
          return { overrides: next };
        }),
      clearAll: () => set({ overrides: {} }),
      countForLayout: (layout) => {
        const prefix = `${layout}|`;
        let n = 0;
        for (const k of Object.keys(get().overrides)) {
          if (k.startsWith(prefix)) n++;
        }
        return n;
      },
    }),
    { name: 'typhon-dag-node-positions', storage: safeStorage },
  ),
);

/**
 * Resolve the override for a single (layout, system) pair. Pure helper so the canvas can
 * apply overrides without subscribing to the whole `overrides` map (subscribing to the map
 * causes re-renders on every drag tick instead of only when the dragged system changes).
 */
export function getOverride(
  overrides: Record<string, NodePosition>,
  layout: string,
  systemName: string,
): NodePosition | undefined {
  return overrides[keyOf(layout, systemName)];
}
