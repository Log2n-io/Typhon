import { create } from 'zustand';

/**
 * One row of the compile-time source-location table emitted by `SourceLocationGenerator`.
 * Mirrors `Typhon.Profiler.SourceLocationManifestEntry` on the server. See
 * `claude/design/observability/10-profiler-source-attribution.md`.
 */
export interface SourceLocation {
  /** Site id carried in span records when bit 1 of SpanFlags is set. 0 = unknown source. */
  id: number;
  /** Index into the parallel `files` array. */
  fileId: number;
  /** 1-based line number within the file. */
  line: number;
  /** Compile-time hint of the kind. May be 0 — runtime kind byte is the source of truth. */
  kind: number;
  /** Containing-method short name for display. */
  method: string;
}

/**
 * High bit reserved on system source-location ids (#302 system attribution). Compile-time call-site
 * ids start at 1 and grow sequentially — they will not reach 0x8000 until ~32K distinct sites exist
 * (we have hundreds today). The synthesized id for a system's chunk-span source is
 * <c>SOURCE_LOCATION_SYSTEM_ID_MASK | systemIndex</c>. Mirrors <c>RuntimeSourceLocationManifest</c>
 * on the server.
 */
export const SOURCE_LOCATION_SYSTEM_ID_MASK = 0x8000;

interface SourceLocationState {
  /** Map siteId → resolved location. Lookup miss = unknown source. */
  locations: Map<number, SourceLocation>;
  /** Map fileId → repo-relative path. */
  files: Map<number, string>;
  /**
   * Replace the table with a new manifest payload. Called once per session: at session-init
   * for live-attach (FileTable + SourceLocationManifest frames), or at trace-load for file-mode.
   */
  setManifest: (entries: SourceLocation[], files: Array<{ fileId: number; path: string }>) => void;
  /** Convenience: resolve a siteId to file/line/method, or null if unknown. */
  resolve: (siteId: number | undefined | null) => ResolvedSourceLocation | null;
  /** #302 system attribution: resolve a system's source via the synthesized id formula. */
  resolveSystem: (systemIndex: number | undefined | null) => ResolvedSourceLocation | null;
  clear: () => void;
}

/** Materialized lookup result with the file path joined in. */
export interface ResolvedSourceLocation {
  file: string;
  line: number;
  method: string;
  kind: number;
}

export const useSourceLocationStore = create<SourceLocationState>()((set, get) => ({
  locations: new Map(),
  files: new Map(),
  setManifest: (entries, files) => {
    const locMap = new Map<number, SourceLocation>();
    for (const e of entries) locMap.set(e.id, e);
    const fileMap = new Map<number, string>();
    for (const f of files) fileMap.set(f.fileId, f.path);
    set({ locations: locMap, files: fileMap });
  },
  resolve: (siteId) => {
    if (!siteId) return null;
    const { locations, files } = get();
    const loc = locations.get(siteId);
    if (!loc) return null;
    const file = files.get(loc.fileId);
    if (!file) return null;
    return { file, line: loc.line, method: loc.method, kind: loc.kind };
  },
  resolveSystem: (systemIndex) => {
    if (systemIndex === null || systemIndex === undefined || systemIndex < 0) return null;
    return get().resolve(SOURCE_LOCATION_SYSTEM_ID_MASK | (systemIndex & 0x7fff));
  },
  clear: () => set({ locations: new Map(), files: new Map() }),
}));
