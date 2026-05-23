import { create } from 'zustand';
import { useSelectionStore } from './useSelectionStore';

// The current Database File Map selection — a source for the shared Detail panel (Module 15, §6.5). Mirrors the
// touched-at recency pattern of useSelectedResourceStore / useSchemaInspectorStore so DetailPanel arbitrates the
// most-recent interaction. A2 widens the A1 page-only selection to page / chunk / content-cell.

/** A selected file page (L1). */
export interface DbMapPageSelection {
  kind: 'page';
  pageIndex: number;
}

/** A selected chunk within a page (L3). */
export interface DbMapChunkSelection {
  kind: 'chunk';
  pageIndex: number;
  segmentId: number;
  chunkId: number;
}

/** A selected content cell within a chunk (L4). */
export interface DbMapCellSelection {
  kind: 'cell';
  pageIndex: number;
  segmentId: number;
  chunkId: number;
  /** Byte offset of the cell within the chunk — identifies it in the decoded cell list. */
  cellOffset: number;
}

/** A selected logical segment — drives the A6 harvest summary card (Module 15, §10.1). */
export interface DbMapSegmentSelection {
  kind: 'segment';
  segmentId: number;
}

export type DbMapSelection = DbMapPageSelection | DbMapChunkSelection | DbMapCellSelection | DbMapSegmentSelection;

interface DbMapSelectionState {
  databaseName: string;
  selected: DbMapSelection | null;
  touchedAt: number;
  select: (databaseName: string, selection: DbMapSelection) => void;
  clear: () => void;
}

export const useDbMapSelectionStore = create<DbMapSelectionState>()((set) => ({
  databaseName: '',
  selected: null,
  touchedAt: 0,
  select: (databaseName, selection) => {
    set({ databaseName, selected: selection, touchedAt: Date.now() });
    // Strangler mirror → unified bus leaf (Stage 1, #373). The selection kind is itself the object
    // type (page/chunk/cell/segment); the ref carries the storage address for the containment chain.
    useSelectionStore.getState().select(selection.kind, selection);
  },
  clear: () => set({ selected: null, touchedAt: 0 }),
}));
