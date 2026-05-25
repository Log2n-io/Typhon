import { createJSONStorage } from 'zustand/middleware';

/**
 * Shared persist storage for every Zustand `persist` store. A `localStorage` wrapper whose every access
 * is try/caught so it falls back silently in non-browser environments (Vitest, SSR) instead of throwing.
 *
 * Previously each persisted store inlined a byte-identical copy of this block (~14 of them); this is the
 * one designated wrapper the `stores/` convention already points at (see `stores/CLAUDE.md`).
 */
export const safeStorage = createJSONStorage(() => ({
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
