import { create } from 'zustand';
import { createJSONStorage, persist } from 'zustand/middleware';
import type { DbMapEncoding, DbMapLens, DbMapPageOrder } from '@/libs/dbmap/types';
import type { DbMapBookmark } from '@/libs/dbmap/dbMapBookmarks';
import type { DbMapFilter } from '@/libs/dbmap/dbMapFilter';

// Singleton state for the Database File Map panel (Module 15, §8). A single-panel module, so the store is a
// plain Zustand singleton — no instance scoping. The camera lives in a panel-local ref (gesture-transient,
// rAF-driven) rather than here; this store holds the discrete UI state the chrome reacts to. A3 added the
// analytical lens + side-rail state; A4 adds per-database bookmarks (the only persisted slice — §13 A4 AC3).

/** The side-rail tabs (A3, §6.4). */
export type DbMapTab = 'legend' | 'regions' | 'bookmarks';

interface DbMapStoreState {
  /** The active base encoding. */
  encoding: DbMapEncoding;
  /** How file pages are laid out on the 2D grid — Hilbert curve (locality) or row-major sequential. */
  pageOrder: DbMapPageOrder;
  /** Whether the segment-boundary overlay is shown. */
  segmentOverlay: boolean;
  /**
   * Whether the cache-residency overlay is shown — a small persistent corner mark per page (Module 15 L1
   * enhancement #3). Independent of the residency *encoding* — this lets the user keep e.g. fillDensity in
   * the body while still seeing which pages are resident in cache.
   */
  residencyOverlay: boolean;
  /** Whether on-canvas region captions are drawn (Module 15 L1 enhancement #7). */
  regionCaptions: boolean;
  /** The active analytical lens (§4.3). */
  lens: DbMapLens;
  /** The segment the fragmentation lens focuses, or null. */
  lensSegmentId: number | null;
  /** Whether the side rail is collapsed to its thin strip. */
  railCollapsed: boolean;
  /** The selected side-rail tab. */
  activeTab: DbMapTab;
  /**
   * A component type name a cross-link (Resource Explorer / Schema Inspector) asked the map to focus on open
   * (§13 A4 AC1) — cross-links identify components by type name. The panel consumes it once and clears it.
   */
  pendingFocusType: string | null;
  /** The active filter-to-dim predicate, or null when no filter is applied (§4.6). */
  filter: DbMapFilter | null;
  /** Saved viewports, keyed by database name — the only persisted slice (§13 A4 AC3). */
  bookmarks: Record<string, DbMapBookmark[]>;
  setEncoding: (encoding: DbMapEncoding) => void;
  setPageOrder: (pageOrder: DbMapPageOrder) => void;
  toggleSegmentOverlay: () => void;
  toggleResidencyOverlay: () => void;
  toggleRegionCaptions: () => void;
  setLens: (lens: DbMapLens) => void;
  /** Activates the fragmentation lens focused on a segment (the canonical AC1 entry point). */
  focusSegment: (segmentId: number) => void;
  toggleRail: () => void;
  setActiveTab: (tab: DbMapTab) => void;
  /** Requests the panel focus a component type's segment on its next render — the cross-link entry point. */
  requestFocusComponent: (typeName: string) => void;
  /** Clears a consumed cross-link focus request. */
  clearPendingFocus: () => void;
  /** Sets the filter-to-dim predicate; null clears it. */
  setFilter: (filter: DbMapFilter | null) => void;
  /** Adds a bookmark for a database. */
  addBookmark: (databaseName: string, bookmark: DbMapBookmark) => void;
  /** Removes a bookmark by id. */
  removeBookmark: (databaseName: string, id: string) => void;
  /** Renames a bookmark. */
  renameBookmark: (databaseName: string, id: string, label: string) => void;
}

const safeStorage = () => {
  try {
    return typeof localStorage !== 'undefined' ? createJSONStorage(() => localStorage) : undefined;
  } catch {
    return undefined;
  }
};

export const useDbMapStore = create<DbMapStoreState>()(
  persist(
    (set) => ({
      encoding: 'pageType',
      pageOrder: 'hilbert',
      segmentOverlay: false,
      residencyOverlay: true,
      regionCaptions: false,
      lens: 'none',
      lensSegmentId: null,
      railCollapsed: false,
      activeTab: 'legend',
      pendingFocusType: null,
      filter: null,
      bookmarks: {},
      setEncoding: (encoding) => set({ encoding }),
      setPageOrder: (pageOrder) => set({ pageOrder }),
      toggleSegmentOverlay: () => set((s) => ({ segmentOverlay: !s.segmentOverlay })),
      toggleResidencyOverlay: () => set((s) => ({ residencyOverlay: !s.residencyOverlay })),
      toggleRegionCaptions: () => set((s) => ({ regionCaptions: !s.regionCaptions })),
      // Switching away from the fragmentation lens drops its focused segment so a later re-entry starts clean.
      setLens: (lens) => set((s) => ({ lens, lensSegmentId: lens === 'fragmentation' ? s.lensSegmentId : null })),
      focusSegment: (segmentId) => set({ lens: 'fragmentation', lensSegmentId: segmentId, activeTab: 'legend' }),
      toggleRail: () => set((s) => ({ railCollapsed: !s.railCollapsed })),
      setActiveTab: (tab) => set({ activeTab: tab }),
      requestFocusComponent: (typeName) => set({ pendingFocusType: typeName }),
      clearPendingFocus: () => set({ pendingFocusType: null }),
      setFilter: (filter) => set({ filter }),
      addBookmark: (databaseName, bookmark) =>
        set((s) => ({
          bookmarks: { ...s.bookmarks, [databaseName]: [bookmark, ...(s.bookmarks[databaseName] ?? [])] },
        })),
      removeBookmark: (databaseName, id) =>
        set((s) => ({
          bookmarks: {
            ...s.bookmarks,
            [databaseName]: (s.bookmarks[databaseName] ?? []).filter((b) => b.id !== id),
          },
        })),
      renameBookmark: (databaseName, id, label) =>
        set((s) => ({
          bookmarks: {
            ...s.bookmarks,
            [databaseName]: (s.bookmarks[databaseName] ?? []).map((b) => (b.id === id ? { ...b, label } : b)),
          },
        })),
    }),
    {
      name: 'workbench-dbmap',
      storage: safeStorage(),
      // Persist the view configuration so the panel reopens the way the user left it — encoding, lens, overlay
      // toggles, filter, and rail layout — alongside the per-database bookmarks. `lensSegmentId` and
      // `pendingFocusType` are deliberately NOT persisted: they reference a specific segment / cross-link that
      // may not exist next session, so they reset (the panel guards a restored fragmentation lens with no
      // segment so it doesn't dim the whole map on open).
      partialize: (s) => ({
        bookmarks: s.bookmarks,
        encoding: s.encoding,
        pageOrder: s.pageOrder,
        lens: s.lens,
        segmentOverlay: s.segmentOverlay,
        residencyOverlay: s.residencyOverlay,
        regionCaptions: s.regionCaptions,
        filter: s.filter,
        railCollapsed: s.railCollapsed,
        activeTab: s.activeTab,
      }),
    },
  ),
);
