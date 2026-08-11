import { useIntegrityStore } from '@/stores/useIntegrityStore';
import { useSessionStore } from '@/stores/useSessionStore';
import { ensureDockPanel } from './openSchemaBrowser';

/**
 * Opens the Integrity view, wherever it can live right now.
 *
 * The two-home dance is invisible to every caller: `DockHost` only mounts once a session exists, so with a
 * session the view is a dock panel and without one it takes the main area. Callers — the Welcome card, the
 * palette, the View menu, the Storage Health strip, the launch-URL deep link — say "open integrity" and
 * this decides where that means. If it were the callers' problem, the no-session path (the one that matters
 * most) would be the one each of them forgot.
 *
 * @param path Bundle to target. Omit to keep whatever the view was last looking at.
 */
export function openIntegrity(path?: string): void {
  const store = useIntegrityStore.getState();

  if (path) {
    store.setPath(path);
  }

  if (useSessionStore.getState().kind === 'none') {
    store.openStandalone(path);
    return;
  }

  ensureDockPanel('integrity', 'Integrity', 'Database Integrity');
}

/**
 * Opens the view targeting the session's own database — the Storage Health strip's "view report" action.
 * Falls back to a bare open when the session has no file path (attach / trace sessions).
 */
export function openIntegrityForSession(): void {
  openIntegrity(useSessionStore.getState().filePath ?? undefined);
}
