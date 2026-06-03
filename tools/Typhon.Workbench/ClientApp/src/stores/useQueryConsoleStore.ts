import { create } from 'zustand';
import { persist } from 'zustand/middleware';
import type { ParseErrorDto } from '@/api/generated/model/parseErrorDto';
import type { PredicateNodeDto } from '@/api/generated/model/predicateNodeDto';
import type { QueryPlanDto } from '@/api/generated/model/queryPlanDto';
import type { QueryResultDto } from '@/api/generated/model/queryResultDto';
import type { QuerySpecDto } from '@/api/generated/model/querySpecDto';
import { safeStorage } from './safeStorage';

/**
 * Query Console panel state (#386 Phase 1). Holds the chip/DSL mode toggle, the single-source-of-truth
 * {@link QuerySpecDto}, the DSL draft (kept in sync with spec via the /parse endpoint), the last result,
 * and the saved-query / history rails. Server-side work (plan / execute / parse) lives in TanStack Query —
 * this store carries only view state. Persisted state: savedQueries (localStorage via safeStorage); history
 * is in-memory (the IndexedDB upgrade with row snapshots lands in Phase 2).
 */
export type QueryConsoleMode = 'chips' | 'dsl' | 'explain';
export type RunState = 'idle' | 'running' | 'ok' | 'error';
export type CostState = 'idle' | 'loading' | 'ok' | 'error';

export interface SavedQuery {
  /** Stable key composed of `filePath::name` — used both as the index key and as the dedupe identity. */
  readonly id: string;
  readonly name: string;
  /** File path of the .typhon source the query was authored against (scopes the save). */
  readonly filePath: string;
  readonly dsl: string;
  /** Unix ms when saved. */
  readonly createdAt: number;
}

export interface HistoryEntry {
  readonly dsl: string;
  /** Unix ms when the query was run (NOT when the entry was appended). */
  readonly ranAt: number;
  readonly rowCount: number;
  readonly elapsedNs: number;
  /** Engine error code (e.g. "invalid_field"), or null when the run succeeded. */
  readonly errorCode: string | null;
}

export const HISTORY_RING_SIZE = 50;

/** The empty starter spec — used by `reset()` and when no DSL has been authored yet. */
export const EMPTY_SPEC: QuerySpecDto = {
  archetype: '',
  polymorphic: true,
  with: [],
  without: [],
  exclude: [],
  enabled: [],
  disabled: [],
  where: null as unknown as PredicateNodeDto,    // null in DTO; the API DTO type uses optional/null
  select: [],
  spatial: [],
  navigate: [],
  orderBy: null as unknown as QuerySpecDto['orderBy'],
  skip: 0,
  take: 1000,
  revision: { kind: 'head', value: 0, timeIso: '' },
};

interface QueryConsoleState {
  // --- editor ---
  mode: QueryConsoleMode;
  spec: QuerySpecDto;
  /** DSL text the user edited in Monaco. Pushed to /parse, which round-trips back to `spec` on success. */
  dslDraft: string;
  parseErrors: ParseErrorDto[];

  // --- run loop ---
  costPreview: QueryPlanDto | null;
  costState: CostState;
  lastResult: QueryResultDto | null;
  runState: RunState;
  /** Stable error code from the last run, or null on success. */
  runErrorCode: string | null;
  runErrorMessage: string | null;

  // --- saved queries + history ---
  savedQueries: SavedQuery[];
  history: HistoryEntry[];

  // --- actions ---
  setMode: (mode: QueryConsoleMode) => void;
  setSpec: (spec: QuerySpecDto) => void;
  setDslDraft: (dsl: string) => void;
  setParseErrors: (errors: ParseErrorDto[]) => void;
  setCostPreview: (plan: QueryPlanDto | null, state: CostState) => void;
  setRunResult: (result: QueryResultDto | null, state: RunState, errorCode?: string | null, errorMessage?: string | null) => void;
  appendHistory: (entry: HistoryEntry) => void;
  saveQuery: (filePath: string, name: string, dsl: string) => void;
  deleteSavedQuery: (id: string) => void;
  loadSavedQuery: (id: string) => void;
  reset: () => void;
}

export const useQueryConsoleStore = create<QueryConsoleState>()(
  persist(
    (set, get) => ({
      mode: 'chips',
      spec: EMPTY_SPEC,
      dslDraft: '',
      parseErrors: [],
      costPreview: null,
      costState: 'idle',
      lastResult: null,
      runState: 'idle',
      runErrorCode: null,
      runErrorMessage: null,
      savedQueries: [],
      history: [],

      setMode: (mode) => set({ mode }),
      setSpec: (spec) => set({ spec }),
      setDslDraft: (dslDraft) => set({ dslDraft }),
      setParseErrors: (parseErrors) => set({ parseErrors }),
      setCostPreview: (plan, state) => set({ costPreview: plan, costState: state }),
      setRunResult: (result, state, errorCode = null, errorMessage = null) =>
        set({ lastResult: result, runState: state, runErrorCode: errorCode, runErrorMessage: errorMessage }),

      // Bounded ring — oldest entry drops when capacity is reached.
      appendHistory: (entry) =>
        set((s) => {
          const next = [entry, ...s.history];
          if (next.length > HISTORY_RING_SIZE) next.length = HISTORY_RING_SIZE;
          return { history: next };
        }),

      // Idempotent on (filePath, name) — overwrites the existing entry. Caller is responsible for confirm-on-overwrite UX.
      saveQuery: (filePath, name, dsl) =>
        set((s) => {
          const id = `${filePath}::${name}`;
          const filtered = s.savedQueries.filter((q) => q.id !== id);
          return {
            savedQueries: [...filtered, { id, name, filePath, dsl, createdAt: Date.now() }],
          };
        }),

      deleteSavedQuery: (id) =>
        set((s) => ({ savedQueries: s.savedQueries.filter((q) => q.id !== id) })),

      // Replaces the DSL draft + switches to DSL mode so the user can edit (chip-mode rebuild happens via the next /parse call).
      loadSavedQuery: (id) => {
        const q = get().savedQueries.find((it) => it.id === id);
        if (!q) return;
        set({ dslDraft: q.dsl, mode: 'dsl' });
      },

      reset: () =>
        set({
          mode: 'chips',
          spec: EMPTY_SPEC,
          dslDraft: '',
          parseErrors: [],
          costPreview: null,
          costState: 'idle',
          lastResult: null,
          runState: 'idle',
          runErrorCode: null,
          runErrorMessage: null,
        }),
    }),
    {
      name: 'query-console',
      storage: safeStorage,
      // Persist only the rails that belong on disk — runtime state (mode, current spec, current result) is session-scoped.
      partialize: (s) => ({ savedQueries: s.savedQueries }),
    },
  ),
);
