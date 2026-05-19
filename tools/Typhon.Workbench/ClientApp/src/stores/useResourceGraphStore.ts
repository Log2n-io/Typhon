import { create } from 'zustand';

interface ResourceGraphState {
  filter: string;
  selectedId: string | null;
  /**
   * A cross-link reveal request — the resource node *name* to scroll into view and select (§7.3). A reveal
   * never filters the tree (that would hide every other node); the panel resolves the name to a node, opens
   * its ancestors, scrolls to it and selects it, then clears the request.
   */
  revealRequest: string | null;
  setFilter: (filter: string) => void;
  setSelected: (id: string | null) => void;
  requestReveal: (resourceName: string) => void;
  clearRevealRequest: () => void;
}

export const useResourceGraphStore = create<ResourceGraphState>()((set) => ({
  filter: '',
  selectedId: null,
  revealRequest: null,
  setFilter: (filter) => set({ filter }),
  setSelected: (id) => set({ selectedId: id }),
  requestReveal: (resourceName) => set({ revealRequest: resourceName }),
  clearRevealRequest: () => set({ revealRequest: null }),
}));
