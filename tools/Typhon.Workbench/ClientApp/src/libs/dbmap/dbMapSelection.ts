import type { SelectionObjectType } from '@/stores/useSelectionStore';

// Database File Map selection shapes (Module 15, §6.5). A2 widens the A1 page-only selection to
// page / chunk / content-cell, plus a logical-segment summary selection.
//
// Stage 2 (#375): these moved out of the former `useDbMapSelectionStore` when the File Map was flipped
// onto the unified selection bus (`useSelectionStore`). They are pure types now — the bus carries one of
// these as its leaf `ref`, with `kind` doubling as the leaf `type` (page/chunk/cell/segment). No store,
// no mirror: the File Map writes the bus directly and the Inspector reads the bus leaf.

/** A selected file page (L1). */
export interface DbMapPageSelection {
  kind: 'page';
  pageIndex: number;
  /**
   * The owning segment, so the Inspector can show the page's `Segment ⊃ Page` ancestor section (IA §2.5),
   * matching chunk/cell. Omitted for a free / unowned page (`NO_SEGMENT`) → no bogus parent in the chain.
   */
  segmentId?: number;
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
  /**
   * The owning component's registered type name when the segment is a component table (empty/absent otherwise).
   * Carried so the Inspector's segment card can offer the same handoffs a component does (Open in Schema /
   * Data Browser, Reveal in Resource) — AC2.14. The `summary` DTO has no type name, so it rides the selection.
   */
  typeName?: string;
}

export type DbMapSelection = DbMapPageSelection | DbMapChunkSelection | DbMapCellSelection | DbMapSegmentSelection;

/** The bus leaf `type`s owned by the File Map (each equals its selection's `kind`). */
const DB_MAP_LEAF_TYPES: ReadonlySet<SelectionObjectType> = new Set(['page', 'chunk', 'cell', 'segment']);

/**
 * True when a bus leaf type belongs to the File Map — so clicking empty space in the map clears the leaf
 * only when *it* owns it, never wiping a leaf another panel set (e.g. a component selected in Schema).
 */
export function isDbMapLeafType(type: SelectionObjectType): boolean {
  return DB_MAP_LEAF_TYPES.has(type);
}
