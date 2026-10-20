import { useBackupsStore } from '@/stores/useBackupsStore';
import { useIntegrityStore } from '@/stores/useIntegrityStore';
import { useSessionStore } from '@/stores/useSessionStore';
import { ensureDockPanel } from './openSchemaBrowser';

/**
 * Opens the Backups view wherever it can live right now (#1417), as `openIntegrity` does: a dock panel with a session, the main area without one. The
 * no-session shell shows at most one full-bleed view, so opening this one closes the Integrity view's.
 *
 * @param bundle Database bundle to target. Omit to keep whatever the view was last looking at.
 */
export function openBackups(bundle?: string): void {
  const store = useBackupsStore.getState();
  if (bundle) {
    store.setBundle(bundle);
  }

  if (useSessionStore.getState().kind === 'none') {
    useIntegrityStore.getState().closeStandalone();
    store.openStandalone(bundle);
    return;
  }

  ensureDockPanel('backups', 'Backups', 'Backups');
}

/** Opens the view on the session's own database (falls back to a bare open for an attach session, whose path is an endpoint). */
export function openBackupsForSession(): void {
  const s = useSessionStore.getState();
  openBackups(s.kind === 'open' ? (s.filePath ?? undefined) : undefined);
}
