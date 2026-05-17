import { create } from 'zustand';

/** Which axis a Call Tree scope is pinned to (#351 Phase 5, §8.2). */
export type CallTreeScopeKind = 'session' | 'range' | 'system' | 'phase' | 'span-kind';

/**
 * The active scope for the Call Tree panel — the time-window axis only (`frameRoot` / `viewMode` stay panel-local).
 * Written by the toolbar selectors and the Detail panel's "Scope Call Tree to this" action; read by the panel to build
 * its {@link CallTreeRequest}. `label` is the human-readable chip text.
 */
export interface CallTreeScope {
  kind: CallTreeScopeKind;
  startUs: number | null;
  endUs: number | null;
  systemIndex: number | null;
  phase: string | null;
  spanKind: number | null;
  label: string;
}

/** The default "no scope" — folds the whole session. */
export const WHOLE_SESSION_SCOPE: CallTreeScope = {
  kind: 'session',
  startUs: null,
  endUs: null,
  systemIndex: null,
  phase: null,
  spanKind: null,
  label: 'Whole session',
};

/** Builds a system scope. `label` is the system name (the chip shows "System: <name>"). */
export function systemScope(systemIndex: number, label: string): CallTreeScope {
  return { ...WHOLE_SESSION_SCOPE, kind: 'system', systemIndex, label: `System: ${label}` };
}

/** Builds a phase scope. */
export function phaseScope(phase: string): CallTreeScope {
  return { ...WHOLE_SESSION_SCOPE, kind: 'phase', phase, label: `Phase: ${phase}` };
}

/** Builds a span-kind scope. `label` is the span-kind name. */
export function spanKindScope(spanKind: number, label: string): CallTreeScope {
  return { ...WHOLE_SESSION_SCOPE, kind: 'span-kind', spanKind, label: `Span kind: ${label}` };
}

/** Builds a manual time-range scope (also used for a clicked single span instance). */
export function rangeScope(startUs: number | null, endUs: number | null): CallTreeScope {
  if (startUs == null && endUs == null) {
    return WHOLE_SESSION_SCOPE;
  }
  const lo = startUs != null ? `${(startUs / 1000).toFixed(2)}` : '0';
  const hi = endUs != null ? `${(endUs / 1000).toFixed(2)}` : '∞';
  return { ...WHOLE_SESSION_SCOPE, kind: 'range', startUs, endUs, label: `Range ${lo}–${hi} ms` };
}

interface CallTreeScopeState {
  /** The session the current scope belongs to — a scope is stale once the session changes. */
  ownerSessionId: string | null;
  scope: CallTreeScope;
  /** Pin the Call Tree to a scope for a given session. */
  setScope: (sessionId: string, scope: CallTreeScope) => void;
  /** Drop any scope back to whole-session. */
  reset: () => void;
}

/**
 * Cross-panel command channel for the Call Tree's scope (#351 Phase 5). The store is a module singleton; {@link ownerSessionId}
 * stamps which session the scope was set for, so a scope set in one session is ignored after the session changes (the panel
 * compares {@link ownerSessionId} to the current session and falls back to {@link WHOLE_SESSION_SCOPE} on a mismatch).
 */
export const useCallTreeScopeStore = create<CallTreeScopeState>()((set) => ({
  ownerSessionId: null,
  scope: WHOLE_SESSION_SCOPE,
  setScope: (sessionId, scope) => set({ ownerSessionId: sessionId, scope }),
  reset: () => set({ ownerSessionId: null, scope: WHOLE_SESSION_SCOPE }),
}));
