import { create } from 'zustand';
import type { DbMapEncoding, DbMapLens } from '@/libs/dbmap/types';

// Singleton state for the Database File Map panel (Module 15, §8). A single-panel module, so the store is a
// plain Zustand singleton — no instance scoping. The camera lives in a panel-local ref (gesture-transient,
// rAF-driven) rather than here; this store holds the discrete UI state the chrome reacts to. A3 adds the
// analytical lens, the side-rail collapse / active-tab state.

/** The side-rail tabs (A3, §6.4). */
export type DbMapTab = 'legend' | 'regions' | 'bookmarks';

interface DbMapStoreState {
  /** The active base encoding. */
  encoding: DbMapEncoding;
  /** Whether the segment-boundary overlay is shown. */
  segmentOverlay: boolean;
  /** The active analytical lens (§4.3). */
  lens: DbMapLens;
  /** The segment the fragmentation lens focuses, or null. */
  lensSegmentId: number | null;
  /** Whether the side rail is collapsed to its thin strip. */
  railCollapsed: boolean;
  /** The selected side-rail tab. */
  activeTab: DbMapTab;
  setEncoding: (encoding: DbMapEncoding) => void;
  toggleSegmentOverlay: () => void;
  setLens: (lens: DbMapLens) => void;
  /** Activates the fragmentation lens focused on a segment (the canonical AC1 entry point). */
  focusSegment: (segmentId: number) => void;
  toggleRail: () => void;
  setActiveTab: (tab: DbMapTab) => void;
}

export const useDbMapStore = create<DbMapStoreState>()((set) => ({
  encoding: 'pageType',
  segmentOverlay: false,
  lens: 'none',
  lensSegmentId: null,
  railCollapsed: false,
  activeTab: 'legend',
  setEncoding: (encoding) => set({ encoding }),
  toggleSegmentOverlay: () => set((s) => ({ segmentOverlay: !s.segmentOverlay })),
  // Switching away from the fragmentation lens drops its focused segment so a later re-entry starts clean.
  setLens: (lens) => set((s) => ({ lens, lensSegmentId: lens === 'fragmentation' ? s.lensSegmentId : null })),
  focusSegment: (segmentId) => set({ lens: 'fragmentation', lensSegmentId: segmentId, activeTab: 'legend' }),
  toggleRail: () => set((s) => ({ railCollapsed: !s.railCollapsed })),
  setActiveTab: (tab) => set({ activeTab: tab }),
}));
