import { create } from 'zustand';

// The Database File Map's per-component enabled-state overlay (Module 15, A6 / §10.1). When a component is
// selected, the L4 cluster entity sub-grid recolours each occupied slot by whether that component is enabled
// for its entity (from the per-slot `enabledMask` already in the chunk decode — no server round-trip). The
// overlay is scoped to one cluster segment; slots of other segments render with their normal occupancy colour.

interface DbMapOverlayState {
  /** Cluster segment the overlay applies to, or null when no overlay is active (plain occupancy colouring). */
  segmentId: number | null;
  /** Component slot index = bit position in each slot's `enabledMask`; null = occupancy (no component overlay). */
  componentSlot: number | null;
  /** Display name of the selected component (for the picker's active label). */
  componentName: string;
  setOverlay: (segmentId: number, componentSlot: number, componentName: string) => void;
  /** Clears the overlay back to plain occupancy colouring. */
  clear: () => void;
}

export const useDbMapOverlayStore = create<DbMapOverlayState>()((set) => ({
  segmentId: null,
  componentSlot: null,
  componentName: '',
  setOverlay: (segmentId, componentSlot, componentName) => set({ segmentId, componentSlot, componentName }),
  clear: () => set({ segmentId: null, componentSlot: null, componentName: '' }),
}));
