import { useEffect } from 'react';
import { installSelectionBridges } from '@/stores/selectionBridges';
import {
  applySelectionToStore,
  installSelectionUrlSync,
  parseSelectionFromSearch,
} from '@/stores/selectionUrlSync';

/**
 * One-shot bootstrap for the cross-panel selection store. Runs at app mount in three steps:
 *
 *   1. Install per-panel ↔ unified-store bridges. They snapshot the current state, then forward
 *      future changes both ways.
 *   2. Apply URL → unified store. The bridges installed in (1) propagate URL-loaded values out to
 *      the legacy per-panel stores so cold-load deep-linking reaches every consumer that still
 *      reads from the legacy stores.
 *   3. Install the unified-store → URL mirror. From here on, any stable-slot change writes back
 *      to `window.location.search` via `history.replaceState`.
 *
 * Call this from a single top-level component (Shell). Calling it elsewhere doubles the
 * subscriptions and produces redundant URL writes.
 */
export function useSelectionBootstrap(): void {
  useEffect(() => {
    const stopBridges = installSelectionBridges();
    applySelectionToStore(parseSelectionFromSearch(window.location.search));
    const stopUrlSync = installSelectionUrlSync();
    return () => {
      stopUrlSync();
      stopBridges();
    };
  }, []);
}
