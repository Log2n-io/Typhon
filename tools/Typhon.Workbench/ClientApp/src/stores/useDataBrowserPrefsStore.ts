import { create } from 'zustand';
import { persist } from 'zustand/middleware';
import { safeStorage } from './safeStorage';
import type { PreviewField } from '@/hooks/dataBrowser/previewFields';

/**
 * Data Browser per-view preferences (PC-1 / AC2.16) — recorded + restored, scoped **per file × archetype**.
 * The page size and the chosen preview columns belong to a given archetype within a given database, so they
 * survive closing/reopening the panel (and the app) and are keyed accordingly. Persisted via the same
 * test-safe localStorage wrapper as the theme/density stores.
 */
export interface DataBrowserPrefs {
  pageSize?: number;
  autoPageSize?: boolean;
  previewFields?: PreviewField[] | null;
}

interface DataBrowserPrefsState {
  byKey: Record<string, DataBrowserPrefs>;
  save: (key: string, patch: DataBrowserPrefs) => void;
}


export const useDataBrowserPrefsStore = create<DataBrowserPrefsState>()(
  persist(
    (set) => ({
      byKey: {},
      save: (key, patch) =>
        set((s) => ({ byKey: { ...s.byKey, [key]: { ...s.byKey[key], ...patch } } })),
    }),
    { name: 'typhon-data-browser-prefs', storage: safeStorage },
  ),
);

/** Build the per-file × per-archetype preference key (PC-1 scope). Null when either part is unknown. */
export function dataBrowserPrefsKey(filePath: string | null, archetypeId: string | null): string | null {
  return filePath && archetypeId ? `${filePath}::${archetypeId}` : null;
}
