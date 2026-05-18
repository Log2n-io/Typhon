import { create } from 'zustand';
import type { DbMapEncoding } from '@/libs/dbmap/types';

// Singleton state for the Database File Map panel (Module 15, §8). A1 is a single-panel module, so the store is
// a plain Zustand singleton — no instance scoping. The camera lives in a panel-local ref (gesture-transient,
// rAF-driven) rather than here; this store holds only the discrete UI state the chrome reacts to.
interface DbMapStoreState {
  /** The active base encoding. */
  encoding: DbMapEncoding;
  /** Whether the segment-boundary overlay is shown. */
  segmentOverlay: boolean;
  setEncoding: (encoding: DbMapEncoding) => void;
  toggleSegmentOverlay: () => void;
}

export const useDbMapStore = create<DbMapStoreState>()((set) => ({
  encoding: 'pageType',
  segmentOverlay: false,
  setEncoding: (encoding) => set({ encoding }),
  toggleSegmentOverlay: () => set((s) => ({ segmentOverlay: !s.segmentOverlay })),
}));
