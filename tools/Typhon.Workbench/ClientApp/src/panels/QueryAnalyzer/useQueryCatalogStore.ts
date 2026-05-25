import { create } from 'zustand';

/**
 * UI-state slice for the Query Catalog panel. Holds search/filter inputs + the currently expanded
 * row identity. Server data is in TanStack Query (see <c>useQueryDefinitions</c>); this store only
 * tracks user-facing UI state.
 *
 * Issue #338 (P5 of #342).
 */
interface QueryCatalogState {
  /** Free-text filter applied to filters/owners/archetype columns. */
  search: string;
  /** Optional system filter — null means "all systems". */
  systemFilter: number | null;
  /** Optional archetype filter — null means "all archetypes". */
  archetypeFilter: number | null;
  /**
   * Identity of the row currently expanded into its detail view. Encoded as a `kind:localId` string
   * since composite keys aren't friendly in primitive store state. Null when no row is expanded.
   */
  expandedRowId: string | null;

  setSearch: (value: string) => void;
  setSystemFilter: (value: number | null) => void;
  setArchetypeFilter: (value: number | null) => void;
  toggleExpanded: (rowId: string) => void;
  /**
   * Unconditional set — collapses any prior expansion. Pairs with cross-panel hand-offs (e.g. the
   * System DAG "Queries" badge clicking through to a specific row) where the caller knows the
   * exact row they want expanded regardless of the current state.
   */
  setExpanded: (rowId: string | null) => void;
  reset: () => void;
}

const initial = {
  search: '',
  systemFilter: null,
  archetypeFilter: null,
  expandedRowId: null,
} as const;

export const useQueryCatalogStore = create<QueryCatalogState>()((set, get) => ({
  ...initial,
  setSearch: (search) => set({ search }),
  setSystemFilter: (systemFilter) => set({ systemFilter }),
  setArchetypeFilter: (archetypeFilter) => set({ archetypeFilter }),
  toggleExpanded: (rowId) =>
    set({ expandedRowId: get().expandedRowId === rowId ? null : rowId }),
  setExpanded: (rowId) => set({ expandedRowId: rowId }),
  reset: () => set({ ...initial }),
}));

/** Compose a stable identity key for a `(kind, localId)` pair — used by `toggleExpanded` and React keys. */
export function rowIdOf(kind: number, localId: number): string {
  return `${kind}:${localId}`;
}
