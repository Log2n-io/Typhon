import { create } from 'zustand';

/**
 * Unified state for the Query Analyzer (#376 Stage-3 Phase 4). One store replaces the three former
 * panel stores (`useQueryCatalogStore` selection bits + `useQueryPlanStore` + `useExecutionInspectorStore`)
 * as the master/detail surface consolidates Catalog + Plan + Executions into a single view
 * (design `query-analyzer.md` §7). The old stores stay until the old panels are deleted in 4D.
 *
 * Session-keyed like {@link useCallTreeScopeStore}: {@link ownerSessionId} stamps which session a
 * selection belongs to, so a query selected in one trace is ignored once the session changes (the
 * panel compares {@link ownerSessionId} to the live session id and treats a mismatch as "no selection").
 */

/** Identity of a query definition — `(Kind, LocalId)`. Kind 0 = View, 1 = EcsQuery. */
export interface QueryRef {
  kind: number;
  localId: number;
}

/** Identity of one execution row within the focused query (the Executions-list selection key). */
export interface ExecutionRef {
  tickIndex: number;
  systemId: number;
}

export type QueryDetailTab = 'plan' | 'executions';
export type QueryPlanMode = 'structural' | 'execution';

/** Reference-stable value equality for a {@link QueryRef} (the bus carries fresh ref objects). */
export function queryRefEqual(a: QueryRef | null, b: QueryRef | null): boolean {
  if (a === b) return true;
  if (a === null || b === null) return false;
  return a.kind === b.kind && a.localId === b.localId;
}

interface QueryAnalyzerState {
  /** The session the current selection belongs to — selection is stale once the session changes. */
  ownerSessionId: string | null;
  /** The focused query definition (master selection), or null for "nothing selected". */
  selectedQuery: QueryRef | null;
  /** Which detail tab is showing. */
  activeTab: QueryDetailTab;
  /** Plan-graph display mode: static shape vs. an execution's actual per-phase overlay. */
  planMode: QueryPlanMode;
  /** The execution whose stats overlay the plan / whose phases the Executions table shows. */
  selectedExecution: ExecutionRef | null;
  /** When true (default), the Executions list follows the global time window; false = whole trace. */
  execScopeLinked: boolean;

  /**
   * Focus a query. Resets the execution selection + plan mode to the structural default (a new query
   * has no execution context yet) — mirrors the old `useQueryPlanStore.setFocus` quirk. Idempotent
   * for the same session + identity so the bus-sync re-write is a silent no-op.
   */
  setSelectedQuery: (sessionId: string, q: QueryRef | null) => void;
  setActiveTab: (tab: QueryDetailTab) => void;
  setPlanMode: (mode: QueryPlanMode) => void;
  /**
   * Select an execution (drives the Executions phase table + enables the Plan's execution mode). Unlike
   * the old `useQueryPlanStore.setSelectedExecution`, this does NOT silently flip plan mode — in a unified
   * view, picking a row in the Executions tab must not change the Plan tab behind the user's back. The
   * Plan-mode flip is reserved for the explicit {@link showExecutionInPlan} verb.
   */
  setSelectedExecution: (exec: ExecutionRef | null) => void;
  /** "Show in plan": select the execution, switch the plan to execution mode, and reveal the Plan tab. */
  showExecutionInPlan: (exec: ExecutionRef) => void;
  setExecScopeLinked: (linked: boolean) => void;
  reset: () => void;
}

const INITIAL = {
  ownerSessionId: null,
  selectedQuery: null,
  activeTab: 'plan' as QueryDetailTab,
  planMode: 'structural' as QueryPlanMode,
  selectedExecution: null,
  execScopeLinked: true,
};

export const useQueryAnalyzerStore = create<QueryAnalyzerState>()((set, get) => ({
  ...INITIAL,
  setSelectedQuery: (sessionId, q) => {
    const s = get();
    if (s.ownerSessionId === sessionId && queryRefEqual(s.selectedQuery, q)) return;
    set({ ownerSessionId: sessionId, selectedQuery: q, selectedExecution: null, planMode: 'structural' });
  },
  setActiveTab: (tab) => set({ activeTab: tab }),
  setPlanMode: (mode) => set({ planMode: mode }),
  setSelectedExecution: (exec) => set({ selectedExecution: exec }),
  showExecutionInPlan: (exec) => set({ selectedExecution: exec, planMode: 'execution', activeTab: 'plan' }),
  setExecScopeLinked: (linked) => set({ execScopeLinked: linked }),
  reset: () => set({ ...INITIAL }),
}));

/**
 * The session-valid focused query: the stored selection only when it belongs to the live session,
 * else null. Consumers pass the current session id; a stale selection (from a closed trace) reads
 * as "nothing selected" without needing an explicit reset on session change.
 */
export function selectValidQuery(s: QueryAnalyzerState, sessionId: string | null): QueryRef | null {
  if (sessionId == null || s.ownerSessionId !== sessionId) return null;
  return s.selectedQuery;
}
