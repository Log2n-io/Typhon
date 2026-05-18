import { create } from 'zustand';

export type LogLevel = 'info' | 'warn' | 'error';

/**
 * Log entry source. Single enum across the Workbench so the Logs panel can facet by origin when
 * multiple streams are live simultaneously (client-side events, server-side engine logs, attached
 * app logs). Today only `workbench-ui` is populated; `workbench-server` and `attached-app` come in
 * later phases (see `claude/design/typhon-workbench/modules/05-logs.md`).
 */
export type LogSource = 'workbench-ui' | 'workbench-server' | 'attached-app';

export interface LogEntry {
  id: number;
  timestamp: number; // ms since epoch
  level: LogLevel;
  source: LogSource;
  message: string;
  /** Optional structured detail — rendered in an expandable row. Must be JSON-serializable. */
  details?: unknown;
}

const MAX_ENTRIES = 500;

/** Severity rank — higher is more critical. Drives the unseen-activity dot on the Logs tab. */
const LEVEL_SEVERITY: Record<LogLevel, number> = { info: 0, warn: 1, error: 2 };

interface LogState {
  entries: LogEntry[];
  nextId: number;
  /**
   * The id at/below which every entry is considered already seen by the user. Entries with a
   * higher id arrived while the Logs panel was hidden and feed the tab's unseen-activity dot.
   */
  lastSeenLogId: number;
  /**
   * Whether the Logs panel is currently visible to the user. Optimistically `true` — the Logs tab
   * component corrects it on mount. A `false` default would mis-count a session-open log appended
   * before the tab's first effect runs, producing a spurious first-paint dot.
   */
  logsVisible: boolean;
  append: (entry: Omit<LogEntry, 'id' | 'timestamp'> & { timestamp?: number }) => void;
  clear: () => void;
  setLogsVisible: (visible: boolean) => void;
}

export const useLogStore = create<LogState>()((set) => ({
  entries: [],
  nextId: 1,
  lastSeenLogId: 0,
  logsVisible: true,
  append: (input) =>
    set((s) => {
      const entry: LogEntry = {
        id: s.nextId,
        timestamp: input.timestamp ?? Date.now(),
        level: input.level,
        source: input.source,
        message: input.message,
        details: input.details,
      };
      // Bounded ring — drop oldest when we hit the cap. Simple slice keeps implementation tiny at the
      // cost of O(n) per append, but n=500 and logs typically accrue slowly on the client side.
      const next = s.entries.length >= MAX_ENTRIES ? s.entries.slice(1) : s.entries;
      // A log appended while the panel is open is seen immediately — keep the watermark current so
      // it never accumulates into the unseen-activity dot.
      return {
        entries: [...next, entry],
        nextId: s.nextId + 1,
        lastSeenLogId: s.logsVisible ? entry.id : s.lastSeenLogId,
      };
    }),
  clear: () => set({ entries: [], nextId: 1, lastSeenLogId: 0 }),
  setLogsVisible: (visible) =>
    set((s) => ({
      logsVisible: visible,
      // Becoming visible clears the dot: everything currently buffered counts as seen.
      lastSeenLogId: visible && s.entries.length > 0 ? s.entries[s.entries.length - 1].id : s.lastSeenLogId,
    })),
}));

/**
 * Most critical level among entries that arrived since the Logs panel was last visible, or `null`
 * when there is no unseen activity. Returns a primitive so it is safe as a zustand selector under
 * the default equality check. O(unseen) — tail-iterates and stops at the first already-seen entry
 * (ids are monotonically increasing and the ring only evicts from the front).
 */
export function selectUnseenLevel(s: LogState): LogLevel | null {
  let worst: LogLevel | null = null;
  for (let i = s.entries.length - 1; i >= 0; i--) {
    const e = s.entries[i];
    if (e.id <= s.lastSeenLogId) {
      break;
    }
    if (worst === null || LEVEL_SEVERITY[e.level] > LEVEL_SEVERITY[worst]) {
      worst = e.level;
    }
    if (worst === 'error') {
      break;
    }
  }
  return worst;
}

/**
 * Number of log entries that arrived since the Logs panel was last visible (0 when none). Exact
 * regardless of ring eviction: entry ids are contiguous and `nextId` counts every append ever, so
 * the count is derived arithmetically rather than by scanning the (possibly truncated) buffer.
 */
export function selectUnseenCount(s: LogState): number {
  return Math.max(0, s.nextId - 1 - s.lastSeenLogId);
}

/**
 * Convenience helpers for the common client-side logging patterns — one-liner at the call site
 * rather than having to spell out the full object literal each time.
 */
export const logInfo = (message: string, details?: unknown) =>
  useLogStore.getState().append({ level: 'info', source: 'workbench-ui', message, details });

export const logWarn = (message: string, details?: unknown) =>
  useLogStore.getState().append({ level: 'warn', source: 'workbench-ui', message, details });

export const logError = (message: string, details?: unknown) =>
  useLogStore.getState().append({ level: 'error', source: 'workbench-ui', message, details });
