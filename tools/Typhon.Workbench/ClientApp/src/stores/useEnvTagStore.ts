import { create } from 'zustand';
import { persist } from 'zustand/middleware';
import { safeStorage } from './safeStorage';

/**
 * Environment tag for a file/engine (the DataGrip "this is PROD" safety signal — context-bar §4.1, GAP-23).
 * A per-file [PC-1] preference: the chosen environment tints the context bar (and, later, tab chrome) so a
 * destructive action against the wrong target is visually obvious. `none` = untagged.
 */
export type EnvTag = 'none' | 'dev' | 'staging' | 'prod';

interface EnvTagState {
  /** Tag per file path / engine endpoint. Absent key = `none`. */
  tags: Record<string, EnvTag>;
  get: (key: string | null) => EnvTag;
  set: (key: string, tag: EnvTag) => void;
}


export const useEnvTagStore = create<EnvTagState>()(
  persist(
    (set, get) => ({
      tags: {},
      get: (key) => (key ? (get().tags[key] ?? 'none') : 'none'),
      set: (key, tag) =>
        set((s) => {
          if (tag === 'none') {
            if (!(key in s.tags)) return s;
            const next = { ...s.tags };
            delete next[key];
            return { tags: next };
          }
          if (s.tags[key] === tag) return s;
          return { tags: { ...s.tags, [key]: tag } };
        }),
    }),
    { name: 'typhon-env-tags-v1', storage: safeStorage },
  ),
);

/** Tailwind tone classes per environment tag — used by the context bar (and future tab chrome). */
export const ENV_TAG_STYLE: Record<EnvTag, { label: string; chip: string; barTint: string }> = {
  none: { label: 'Tag', chip: 'bg-muted text-muted-foreground', barTint: '' },
  dev: { label: 'DEV', chip: 'bg-sky-500 text-white', barTint: 'border-l-2 border-l-sky-500' },
  staging: { label: 'STAGING', chip: 'bg-amber-500 text-slate-900', barTint: 'border-l-2 border-l-amber-500' },
  prod: { label: 'PROD', chip: 'bg-red-500 text-white', barTint: 'border-l-2 border-l-red-500' },
};
