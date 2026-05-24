import { useSessionStore } from './useSessionStore';
import { useSelectionStore } from './useSelectionStore';
import { useNavHistoryStore } from './useNavHistoryStore';
import { useSelectedResourceStore } from './useSelectedResourceStore';
import { useSchemaInspectorStore } from './useSchemaInspectorStore';
import { useDataBrowserStore } from './useDataBrowserStore';
import { useProfilerSelectionStore } from './useProfilerSelectionStore';

/**
 * Clear every session-scoped selection store so switching sessions (or closing one) leaves no stale
 * selection, breadcrumb, nav history, or Inspector leaf from the previous session (GAP-12a switch-
 * without-close; no cross-session bleed, AC1.10). TanStack Query data is already keyed by `sessionId`,
 * so server caches invalidate on their own — only the client selection state needs an explicit wipe.
 */
export function resetSessionScopedState(): void {
  useSelectedResourceStore.getState().clear();
  useSchemaInspectorStore.getState().reset();
  useDataBrowserStore.getState().reset();
  useProfilerSelectionStore.getState().clear();
  useSelectionStore.getState().clear();
  useNavHistoryStore.getState().clear();
}

/**
 * Install the session → reset bridge: whenever the active `sessionId` changes (a new session is set,
 * or the session is closed), wipe the session-scoped selection state. Returns the unsubscribe handle.
 * Mounted once at the shell level (alongside the selection bootstrap).
 */
export function installSessionResetSync(): () => void {
  let lastSessionId = useSessionStore.getState().sessionId;
  return useSessionStore.subscribe((state) => {
    if (state.sessionId === lastSessionId) return;
    lastSessionId = state.sessionId;
    resetSessionScopedState();
  });
}
