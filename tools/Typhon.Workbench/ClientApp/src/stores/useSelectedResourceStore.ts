import { create } from 'zustand';
import type { ResourceNodeDto } from '@/api/generated/model/resourceNodeDto';
import { useSelectionStore } from './useSelectionStore';

export interface SelectedResource {
  resourceId: string;
  kind: string;
  name: string;
  path: string[];
  raw: ResourceNodeDto;
}

interface SelectedResourceState {
  selected: SelectedResource | null;
  /** <c>Date.now()</c> at which <c>selected</c> was last updated. Consumed by DetailPanel to pick
   *  whichever selection — resource vs. schema field — is most recent. */
  touchedAt: number;
  setSelected: (s: SelectedResource | null) => void;
  clear: () => void;
}

/**
 * Inlined cross-store write: mirror the resource-id projection into `useSelectionStore.resource`.
 * Pre-#345 this lived as a Zustand subscription in `selectionBridges.ts`; folding it into the
 * setter is one-way (rich payload → id) and idempotent (the unified store's `setResource` is
 * value-equal-aware so a no-op write doesn't trigger URL sync). One less subscription on the hot
 * cross-store layer.
 */
function mirrorToUnifiedSelection(s: SelectedResource | null): void {
  useSelectionStore.getState().setResource(s?.resourceId ?? null);
  // Stage 1 (#373): a tree selection is also the Inspector leaf — the rich payload rides as the ref
  // so the Resource card renders without a refetch.
  if (s !== null) {
    useSelectionStore.getState().select('resource', s);
  }
}

export const useSelectedResourceStore = create<SelectedResourceState>()((set) => ({
  selected: null,
  touchedAt: 0,
  setSelected: (s) => {
    set({ selected: s, touchedAt: Date.now() });
    mirrorToUnifiedSelection(s);
  },
  clear: () => {
    set({ selected: null, touchedAt: Date.now() });
    mirrorToUnifiedSelection(null);
  },
}));
