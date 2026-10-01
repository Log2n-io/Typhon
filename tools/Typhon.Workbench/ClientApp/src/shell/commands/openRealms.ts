import { useSessionStore } from '@/stores/useSessionStore';
import { ensureDockPanel } from './openSchemaBrowser';

/**
 * Realms navigator open/toggle commands (#1083 rung 3).
 *
 * **Gated on the `realms` capability, not on the session kind.** A single-realm database is `kind === 'open'` and has
 * no realms, so a kind test would offer the navigator on every database and answer it with an empty list; and an attach
 * session is not `open` at all yet will have realms when rung 6 lands. The capability is the server's own statement
 * that there is something here, and it is the only test that is right in both directions.
 */
export function canOpenRealms(): boolean {
  return useSessionStore.getState().capabilities.includes('realms');
}

export function openRealms(): void {
  if (!canOpenRealms()) return;
  ensureDockPanel('realms', 'Realms', 'Realms');
}

/** Toggle variant for the View menu and the palette — the same shape the other navigators use. */
export function toggleViewRealms(): void {
  openRealms();
}
