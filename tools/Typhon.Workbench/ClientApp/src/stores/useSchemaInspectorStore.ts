import { create } from 'zustand';
import { useSelectionStore } from './useSelectionStore';

export type SchemaOverlayKey = 'defaults' | 'cacheLineBoundaries';
export type SchemaViewMode = 'layout' | 'archetypes' | 'relationships' | 'defaults';

export interface SchemaOverlays {
  /** "Expand non-default" diff overlay. v1.5 — currently inert. */
  defaults: boolean;
  /** Draw the 64-byte cache-line separator rules. On by default. */
  cacheLineBoundaries: boolean;
}

interface SchemaInspectorState {
  /** DBComponentDefinition.Name of the currently-focused component, or null. */
  selectedComponentType: string | null;
  /** Field name within the focused component, or null (nothing selected). */
  selectedField: string | null;
  /** <c>Date.now()</c> at which the field was last selected (or cleared). Used by DetailPanel
   *  to pick whichever selection — schema field vs. resource tree node — is most recent. */
  fieldTouchedAt: number;
  overlays: SchemaOverlays;
  viewMode: SchemaViewMode;

  selectComponent: (typeName: string | null) => void;
  selectField: (fieldName: string | null) => void;
  toggleOverlay: (key: SchemaOverlayKey) => void;
  setViewMode: (mode: SchemaViewMode) => void;
  reset: () => void;
}

const INITIAL_OVERLAYS: SchemaOverlays = {
  defaults: false,
  cacheLineBoundaries: true,
};

export const useSchemaInspectorStore = create<SchemaInspectorState>()((set) => ({
  selectedComponentType: null,
  selectedField: null,
  fieldTouchedAt: 0,
  overlays: INITIAL_OVERLAYS,
  viewMode: 'layout',
  selectComponent: (typeName) => {
    set({ selectedComponentType: typeName, selectedField: null, fieldTouchedAt: Date.now() });
    // Inlined cross-store mirror — schema inspector ↔ unified selection. The unified store's
    // `setComponent` is value-equal-aware, so when the loop trips via `useSelectionStore.subscribe`
    // → `useSchemaInspectorStore.selectComponent(sameName)` → here → `setComponent(sameName)`,
    // it short-circuits without an infinite loop. Pre-#345 this lived as a Zustand subscription
    // pair in `selectionBridges.ts`.
    useSelectionStore.getState().setComponent(typeName);
  },
  selectField: (fieldName) => set({ selectedField: fieldName, fieldTouchedAt: Date.now() }),
  toggleOverlay: (key) =>
    set((s) => ({ overlays: { ...s.overlays, [key]: !s.overlays[key] } })),
  setViewMode: (mode) => set({ viewMode: mode }),
  reset: () => {
    set({
      selectedComponentType: null,
      selectedField: null,
      fieldTouchedAt: 0,
      overlays: INITIAL_OVERLAYS,
      viewMode: 'layout',
    });
    useSelectionStore.getState().setComponent(null);
  },
}));
