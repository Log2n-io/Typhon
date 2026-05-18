import { create } from 'zustand';

// The current Database File Map selection — a 4th source for the shared Detail panel (Module 15, §6.5).
// Mirrors the touched-at recency pattern of useSelectedResourceStore / useSchemaInspectorStore so DetailPanel
// arbitrates the most-recent interaction.

/** Coarse, A1-level detail of a selected file page. */
export interface DbMapPageInfo {
  databaseName: string;
  pageIndex: number;
  typeLabel: string;
  /** Dense owning-segment id, or null when the page is owned by no segment. */
  segmentId: number | null;
  segmentKind: string | null;
  byteOffset: number;
}

interface DbMapSelectionState {
  selected: DbMapPageInfo | null;
  touchedAt: number;
  select: (info: DbMapPageInfo) => void;
  clear: () => void;
}

export const useDbMapSelectionStore = create<DbMapSelectionState>()((set) => ({
  selected: null,
  touchedAt: 0,
  select: (info) => set({ selected: info, touchedAt: Date.now() }),
  clear: () => set({ selected: null, touchedAt: 0 }),
}));
