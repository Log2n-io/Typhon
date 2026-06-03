import { useQueryConsoleStore } from '@/stores/useQueryConsoleStore';
import { useSessionStore } from '@/stores/useSessionStore';

/**
 * Hook used by the toolbar's Save button — opens a name prompt and calls into the store. Split out of
 * {@link ./RailTabs.tsx} so the rail file only exports components (react-refresh fast-refresh constraint).
 */
export function useSaveCurrent(): () => void {
  const dslDraft = useQueryConsoleStore((s) => s.dslDraft);
  const saveQuery = useQueryConsoleStore((s) => s.saveQuery);
  return () => {
    const name = window.prompt('Save query as:', '');
    if (!name || !name.trim()) return;
    const filePath = useSessionStore.getState().sessionId ?? '_';     // local-scope; sessionId stands in for filePath
    saveQuery(filePath, name.trim(), dslDraft);
  };
}
