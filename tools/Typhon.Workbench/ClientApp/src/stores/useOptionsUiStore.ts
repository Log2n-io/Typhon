import { create } from 'zustand';

/**
 * Client-only UI state for the Options panel — the category it should snap to when opened
 * programmatically. Lets a deep-link (e.g. the schema migrate/incompatible banners' "Manage schema
 * directories…" action) request a specific category without prop-drilling into a panel dockview
 * spawns. Same indirection as `registerOpenConnect` for the Connect dialog's initial tab.
 */
export type OptionsCategory = 'editor' | 'profiler' | 'schema' | 'dag';

interface OptionsUiState {
  /** The category the Options panel should switch to on its next render, or null when none is pending. */
  requestedCategory: OptionsCategory | null;
  /** Request the Options panel to open on a given category. */
  requestCategory: (category: OptionsCategory) => void;
  /** Clear the pending request (called by the panel once it has honored it). */
  clearRequested: () => void;
}

export const useOptionsUiStore = create<OptionsUiState>()((set) => ({
  requestedCategory: null,
  requestCategory: (category) => set({ requestedCategory: category }),
  clearRequested: () => set({ requestedCategory: null }),
}));
