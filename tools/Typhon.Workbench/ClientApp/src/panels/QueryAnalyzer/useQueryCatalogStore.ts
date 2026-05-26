import { create } from 'zustand';
import { persist } from 'zustand/middleware';
import { safeStorage } from '@/stores/safeStorage';

/**
 * UI-state slice for the Query Analyzer catalog (master). Holds the user-facing UI state — filters,
 * sort, and the currently-expanded row identity. Server data lives in TanStack Query
 * ({@link useQueryDefinitions}); this store only tracks user input.
 *
 * Persistence (AC3.16, PC-1): `sort`, `search`, `systemFilter`, `archetypeFilter` survive a Workbench
 * reload via {@link safeStorage}. `expandedRowId` is intentionally NOT persisted — it's volatile UI state
 * that should not bleed across reloads. On session change, {@link clearFilters} is called from
 * `resetSessionScopedState` to wipe trace-specific numeric filters (system / archetype ids only mean
 * something in their originating trace); `sort` survives session change as a preference.
 *
 * Originally issue #338 (P5 of #342); persistence added in #376 Phase 5 / AC3.16.
 */

/** Sortable numeric columns of the catalog master table. */
export type SortKey = 'id' | 'count' | 'total' | 'selectivity';

/** Sort selection: which column, ascending or descending. */
export interface SortState {
  key: SortKey;
  dir: 'asc' | 'desc';
}

/** Default sort — total wall-time descending, the canonical cost ranking. */
export const DEFAULT_SORT: SortState = { key: 'total', dir: 'desc' };

interface QueryCatalogState {
  /** Free-text filter applied across filters / owners / archetype columns. */
  search: string;
  /** Optional system filter — null means "all systems". Trace-specific (cleared on session change). */
  systemFilter: number | null;
  /** Optional archetype filter — null means "all archetypes". Trace-specific (cleared on session change). */
  archetypeFilter: number | null;
  /** Sort state for the master table. Persisted as a PC-1 preference (survives session change). */
  sort: SortState;
  /**
   * Identity of the row currently expanded into its detail view. Encoded as a `kind:localId` string
   * since composite keys aren't friendly in primitive store state. Null when no row is expanded.
   * NOT persisted — volatile UI state.
   */
  expandedRowId: string | null;

  setSearch: (value: string) => void;
  setSystemFilter: (value: number | null) => void;
  setArchetypeFilter: (value: number | null) => void;
  setSort: (value: SortState) => void;
  toggleExpanded: (rowId: string) => void;
  /**
   * Unconditional set — collapses any prior expansion. Pairs with cross-panel hand-offs (e.g. the
   * System DAG "Queries" badge clicking through to a specific row) where the caller knows the
   * exact row they want expanded regardless of the current state.
   */
  setExpanded: (rowId: string | null) => void;
  /**
   * Clear filters + expanded row, keeping {@link sort} intact. Called by `resetSessionScopedState`
   * on session change so trace-specific numeric filters (system / archetype ids) don't persist into a
   * new trace where they'd point at different things.
   */
  clearFilters: () => void;
  /** Full reset — clears every field including sort. Primarily for tests; production uses {@link clearFilters}. */
  reset: () => void;
}

const initial = {
  search: '',
  systemFilter: null as number | null,
  archetypeFilter: null as number | null,
  sort: DEFAULT_SORT,
  expandedRowId: null as string | null,
};

export const useQueryCatalogStore = create<QueryCatalogState>()(
  persist(
    (set, get) => ({
      ...initial,
      setSearch: (search) => set({ search }),
      setSystemFilter: (systemFilter) => set({ systemFilter }),
      setArchetypeFilter: (archetypeFilter) => set({ archetypeFilter }),
      setSort: (sort) => set({ sort }),
      toggleExpanded: (rowId) =>
        set({ expandedRowId: get().expandedRowId === rowId ? null : rowId }),
      setExpanded: (rowId) => set({ expandedRowId: rowId }),
      clearFilters: () => set({ search: '', systemFilter: null, archetypeFilter: null, expandedRowId: null }),
      reset: () => set({ ...initial }),
    }),
    {
      name: 'workbench-query-catalog',
      storage: safeStorage,
      version: 1,
      // Partialize: persist sort + filter inputs only. expandedRowId is volatile UI state and stays
      // session-scoped (the expanded row may not even exist after a reload + data refetch).
      partialize: (s) => ({
        search: s.search,
        systemFilter: s.systemFilter,
        archetypeFilter: s.archetypeFilter,
        sort: s.sort,
      }),
    },
  ),
);

/** Compose a stable identity key for a `(kind, localId)` pair — used by `toggleExpanded` and React keys. */
export function rowIdOf(kind: number, localId: number): string {
  return `${kind}:${localId}`;
}
