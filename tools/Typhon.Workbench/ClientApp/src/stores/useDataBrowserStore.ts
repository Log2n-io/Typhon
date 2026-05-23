import { create } from 'zustand';
import type { PreviewField } from '@/hooks/dataBrowser/previewFields';
import { useSelectionStore } from './useSelectionStore';

/**
 * Data Browser panel-local selection state (Module 06, v1). Holds the chosen archetype and the selected entity;
 * server data (entity pages, entity detail) lives in TanStack Query, not here. `touchedAt` mirrors the schema
 * inspector's recency pattern so a shared Detail surface can pick the most-recent selection.
 */
export const DEFAULT_PAGE_SIZE = 25;
export const PAGE_SIZE_OPTIONS = [10, 25, 50, 100] as const;

interface DataBrowserState {
  /** Archetype id (numeric, as string) currently being browsed, or null. */
  archetypeId: string | null;
  /** Raw entity id (decimal string) of the selected entity, or null. */
  selectedEntityId: string | null;
  /** Date.now() at which the selection last changed. */
  touchedAt: number;
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
  selectedEntityId: null,
  touchedAt: 0,
  pageSize: DEFAULT_PAGE_SIZE,
  autoPageSize: false,
  pageIndex: 0,
  previewFields: null,
  // Switching archetype clears the entity selection, returns to page 1, and drops custom columns (they belong to the old schema).
  setArchetype: (id) => {
    set({ archetypeId: id, selectedEntityId: null, pageIndex: 0, previewFields: null, touchedAt: Date.now() });
    // Strangler mirror → unified bus leaf (Stage 1, #373).
    if (id != null) {
      useSelectionStore.getState().select('archetype', id);
    }
  },
  selectEntity: (entityId) => {
    set({ selectedEntityId: entityId, touchedAt: Date.now() });
    // The entity ref carries its archetype so the Inspector context-stack can show Archetype ⊃ Entity.
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
      selectedEntityId: null,
      touchedAt: 0,
      pageSize: DEFAULT_PAGE_SIZE,
      autoPageSize: false,
      pageIndex: 0,
      previewFields: null,
    }),
}));
