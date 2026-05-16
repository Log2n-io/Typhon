import { create } from 'zustand';
import { persist, createJSONStorage } from 'zustand/middleware';
import type { SavedViewport } from '@/libs/profiler/initialViewport';

const MAX_ENTRIES = 20;

export type RecentFileState = 'Ready' | 'MigrationRequired' | 'Incompatible';
export type RecentFileKind = 'db' | 'trace';

export interface RecentFile {
  filePath: string;
  schemaDllPaths: string[];
  lastOpenedAt: string;
  lastState: RecentFileState;
  pinnedResourceIds?: string[];
  /**
   * Session kind this file was opened as. Optional for backwards-compatibility — legacy entries without
   * this field are treated as <c>'db'</c> by {@link getRecentFileKind}.
   */
  kind?: RecentFileKind;
  /**
   * Last committed profiler viewport for this file, fingerprint-tagged. Restored on reopen via
   * `resolveInitialViewport`; absent for files never inspected in the profiler. Survives restarts
   * (the store is localStorage-persisted).
   */
  lastViewport?: SavedViewport;
}

/** Returns the entry's {@link RecentFileKind}; legacy entries (pre-Phase 1b) default to <c>'db'</c>. */
export function getRecentFileKind(entry: RecentFile): RecentFileKind {
  return entry.kind ?? 'db';
}

/** Parent directory of a file path. Splits on the last <c>\</c> or <c>/</c>; a bare drive root keeps its separator. */
export function dirOf(filePath: string): string {
  const idx = Math.max(filePath.lastIndexOf('\\'), filePath.lastIndexOf('/'));
  if (idx <= 0) {
    return filePath;
  }
  const dir = filePath.slice(0, idx);
  // `C:` → `C:\` (or `C:/`): a bare drive letter is not a listable directory on its own.
  return /^[a-zA-Z]:$/.test(dir) ? dir + filePath[idx] : dir;
}

/** A directory a recent file was loaded from. */
export interface RecentLocation {
  dir: string;
  kind: RecentFileKind;
  lastOpenedAt: string;
}

/** Distinct parent directories of {@link RecentFile} entries, most-recent-first. Optionally filtered to one {@link RecentFileKind}. */
export function getRecentLocations(entries: RecentFile[], kind?: RecentFileKind): RecentLocation[] {
  const seen = new Set<string>();
  const out: RecentLocation[] = [];
  for (const e of entries) {
    const k = getRecentFileKind(e);
    if (kind && k !== kind) {
      continue;
    }
    const dir = dirOf(e.filePath);
    const key = normalizePath(dir);
    if (seen.has(key)) {
      continue;
    }
    seen.add(key);
    out.push({ dir, kind: k, lastOpenedAt: e.lastOpenedAt });
  }
  return out;
}

interface RecentFilesStore {
  entries: RecentFile[];
  record: (entry: RecentFile) => void;
  remove: (filePath: string) => void;
  /** Drops every entry whose parent directory matches <paramref name="dir"/> — used to prune a directory that no longer exists. */
  removeUnderDirectory: (dir: string) => void;
  clear: () => void;
  pinResource: (filePath: string, resourceId: string) => void;
  unpinResource: (filePath: string, resourceId: string) => void;
  getPins: (filePath: string) => string[];
  /** Records the last profiler viewport for a file. No-op when the file is not a recent entry. */
  setLastViewport: (filePath: string, viewport: SavedViewport) => void;
  /** The last profiler viewport saved for a file, or <c>null</c> if none was recorded. */
  getLastViewport: (filePath: string) => SavedViewport | null;
}

const safeStorage = () => {
  try {
    return createJSONStorage(() => localStorage);
  } catch {
    return undefined;
  }
};

function normalizePath(p: string) {
  return p.toLowerCase();
}

export const useRecentFilesStore = create<RecentFilesStore>()(
  persist(
    (set, get) => ({
      entries: [],
      record: (entry) =>
        set((s) => {
          const key = normalizePath(entry.filePath);
          const existing = s.entries.find((e) => normalizePath(e.filePath) === key);
          const merged: RecentFile = {
            ...entry,
            pinnedResourceIds: entry.pinnedResourceIds ?? existing?.pinnedResourceIds ?? [],
            // Re-recording on open carries no viewport — preserve the prior one so reopening a file
            // doesn't wipe its remembered viewport before the restore effect can read it.
            lastViewport: entry.lastViewport ?? existing?.lastViewport,
          };
          const deduped = s.entries.filter((e) => normalizePath(e.filePath) !== key);
          return { entries: [merged, ...deduped].slice(0, MAX_ENTRIES) };
        }),
      remove: (filePath) =>
        set((s) => ({
          entries: s.entries.filter((e) => normalizePath(e.filePath) !== normalizePath(filePath)),
        })),
      removeUnderDirectory: (dir) =>
        set((s) => {
          const key = normalizePath(dir);
          return { entries: s.entries.filter((e) => normalizePath(dirOf(e.filePath)) !== key) };
        }),
      clear: () => set({ entries: [] }),
      pinResource: (filePath, resourceId) =>
        set((s) => {
          const key = normalizePath(filePath);
          return {
            entries: s.entries.map((e) => {
              if (normalizePath(e.filePath) !== key) return e;
              const current = e.pinnedResourceIds ?? [];
              if (current.includes(resourceId)) return e;
              return { ...e, pinnedResourceIds: [...current, resourceId] };
            }),
          };
        }),
      unpinResource: (filePath, resourceId) =>
        set((s) => {
          const key = normalizePath(filePath);
          return {
            entries: s.entries.map((e) => {
              if (normalizePath(e.filePath) !== key) return e;
              const current = e.pinnedResourceIds ?? [];
              return { ...e, pinnedResourceIds: current.filter((id) => id !== resourceId) };
            }),
          };
        }),
      getPins: (filePath) => {
        const key = normalizePath(filePath);
        return get().entries.find((e) => normalizePath(e.filePath) === key)?.pinnedResourceIds ?? [];
      },
      setLastViewport: (filePath, viewport) =>
        set((s) => {
          const key = normalizePath(filePath);
          let changed = false;
          const entries = s.entries.map((e) => {
            if (normalizePath(e.filePath) !== key) return e;
            changed = true;
            return { ...e, lastViewport: viewport };
          });
          return changed ? { entries } : s;
        }),
      getLastViewport: (filePath) => {
        const key = normalizePath(filePath);
        return get().entries.find((e) => normalizePath(e.filePath) === key)?.lastViewport ?? null;
      },
    }),
    {
      name: 'workbench-recent-files',
      storage: safeStorage(),
    },
  ),
);
