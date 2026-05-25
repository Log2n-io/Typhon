import { create } from 'zustand';
import { persist } from 'zustand/middleware';
import { safeStorage } from './safeStorage';
import type { SchemaExplorerMode } from '@/panels/SchemaExplorer/schemaExplorerModel';

/**
 * Schema Explorer per-file preference (PC-1 / AC2.16) — the last-used mode (Archetypes vs Types) is recorded
 * and restored **per database file**, so reopening a file lands in the mode you left it in. Persisted via the
 * same test-safe localStorage wrapper as the other prefs stores (theme/density/data-browser).
 */
interface SchemaExplorerPrefsState {
  modeByFile: Record<string, SchemaExplorerMode>;
  setMode: (filePath: string, mode: SchemaExplorerMode) => void;
}


export const useSchemaExplorerPrefsStore = create<SchemaExplorerPrefsState>()(
  persist(
    (set) => ({
      modeByFile: {},
      setMode: (filePath, mode) => set((s) => ({ modeByFile: { ...s.modeByFile, [filePath]: mode } })),
    }),
    { name: 'typhon-schema-explorer-prefs', storage: safeStorage },
  ),
);
