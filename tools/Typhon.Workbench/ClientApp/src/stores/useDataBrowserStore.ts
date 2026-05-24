import { create } from 'zustand';
import type { PreviewField } from '@/hooks/dataBrowser/previewFields';
import { useSelectionStore } from './useSelectionStore';

/**
 * Data Browser panel **view-state** (Module 06). Holds the chosen archetype + paging/column prefs; the
 * *selected entity* now lives on the unified selection bus (GAP-05, Stage 2) — `selectEntity`/`setArchetype`
 * write through to it, and the panel reads the highlight back from {@link useSelectionStore}'s `entity` leaf.
 * Server data (entity pages, entity detail) lives in TanStack Query, not here.
 */
export const DEFAULT_PAGE_SIZE = 25;
export const PAGE_SIZE_OPTIONS = [10, 25, 50, 100] as const;

interface DataBrowserState {
  /** Archetype id (numeric, as string) currently being browsed, or null. */
  archetypeId: string | null;
  /** Rows per page when not in auto mode. */
  pageSize: number;
  /** When true, the effective page size is computed from the visible list height (see EntityListPanel). */
  autoPageSize: boolean;
  /** Zero-based current page. */
  pageIndex: number;
  /** Chosen preview columns, or null to use the schema-derived default for the current archetype. */
  previewFields: PreviewField[] | null;

  setArchetype: (id: string | null) => void;
  selectEntity: (entityId: string | null) => void;
  setPageSize: (size: number) => void;
  setAutoPageSize: (on: boolean) => void;
  setPageIndex: (index: number) => void;
  setPreviewFields: (fields: PreviewField[] | null) => void;
  reset: () => void;
}

export const useDataBrowserStore = create<DataBrowserState>()((set, get) => ({
  archetypeId: null,
  pageSize: DEFAULT_PAGE_SIZE,
  autoPageSize: false,
  pageIndex: 0,
  previewFields: null,
  // Switching archetype returns to page 1 and drops custom columns (they belong to the old schema). The
  // strangler mirror sets the bus leaf to the new archetype, which also supersedes any prior entity leaf.
  setArchetype: (id) => {
    set({ archetypeId: id, pageIndex: 0, previewFields: null });
    if (id != null) {
      useSelectionStore.getState().select('archetype', id);
    }
  },
  // GAP-05: the selected entity is bus state, not silo state. The ref carries its archetype so the
  // Inspector context-stack can show Archetype ⊃ Entity and the row highlight can be archetype-scoped.
  selectEntity: (entityId) => {
    if (entityId != null) {
      useSelectionStore.getState().select('entity', { archetypeId: get().archetypeId, entityId });
    }
  },
  // Picking an explicit size leaves auto mode; resets to the first page so the offset never lands past the end.
  setPageSize: (size) => set({ pageSize: size, autoPageSize: false, pageIndex: 0 }),
  setAutoPageSize: (on) => set({ autoPageSize: on, pageIndex: 0 }),
  setPageIndex: (index) => set({ pageIndex: Math.max(0, index) }),
  setPreviewFields: (fields) => set({ previewFields: fields }),
  reset: () =>
    set({
      archetypeId: null,
      pageSize: DEFAULT_PAGE_SIZE,
      autoPageSize: false,
      pageIndex: 0,
      previewFields: null,
    }),
}));
